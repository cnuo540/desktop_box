using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using DesktopBox.Services;
using DesktopBox.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopBox.Views;

/// <summary>桌面上"未收纳项"的自绘层(对齐 Coodesker 的 <c>_cls_desk_</c> 做法)。
///
/// explorer 自己的桌面图标层被整层关掉之后,桌面上的图标就由这一层来画:
/// 只画<b>没有进盒子</b>的项 —— 刚拖到桌面的文件、新建的文件夹都在这里出现;
/// 进了盒子的项只在盒子里出现。目录窗口完全不受影响(我们一个文件属性都不碰)。
///
/// 三个关键点:
/// ① 它是桌面宿主的<b>子窗口</b>(和盒子窗口同一套挂法),所以永远在壁纸之上、普通窗口之下,
///    并且用 HWND_BOTTOM 排在最底,盒子窗口永远压在它上面;
/// ② 窗口区域裁剪成<b>图标矩形并集</b>:图标之间的空白处鼠标事件照旧归 explorer,
///    右键桌面菜单、从资源管理器往桌面拖文件都不受影响;
/// ③ 图标是异步提取的(shell 提取可能慢),提取完再回填图像,不阻塞桌面刷新。</summary>
public sealed class DesktopLayerWindow : IDisposable
{
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int HWND_BOTTOM = 1;

    private const double IconSize = 48;
    /// <summary>标签字号/行高/两行高度:格高 75 里"48 图标 + 两行名字"刚好放得下,
    /// 和 explorer 桌面一样。只留一行的话像"新建文件夹 (5)"这种会被裁成"新建文件夹",几个看着一模一样。</summary>
    private const double LabelSize = 11;
    private const double LabelLineHeight = 13;
    private const double LabelMaxHeight = 26;
    private const double EdgePadding = 10;
    /// <summary>底部留给任务栏,别把图标摆到任务栏底下。</summary>
    private const double BottomReserve = 56;

    private readonly HwndSource _source;
    private readonly IntPtr _handle;
    private readonly Canvas _canvas;
    private readonly MainViewModel _vm;
    private readonly IIconExtractorService _icons;
    private readonly double _scaleX;
    private readonly double _scaleY;
    private readonly List<(int X, int Y, int W, int H)> _cells = new();
    /// <summary>用户拖过位置的那些项(层内坐标,DIP),落盘后重启依然保持。</summary>
    private readonly Dictionary<string, (double X, double Y)> _positions = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _positionsPath = Models.AppPaths.DesktopLayerPath;
    private readonly Dictionary<string, Border> _tilesByPath = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>每项的名字文本块:就地改名时要贴着它放输入框。</summary>
    private readonly Dictionary<string, TextBlock> _labels = new(StringComparer.OrdinalIgnoreCase);
    private double _cellW = 75;
    private double _cellH = 75;
    /// <summary>上一次排好的格子(带路径),拖放时用来判断"哪些格子已经被别人占了"。</summary>
    private List<Models.DesktopLayerLayout.Cell> _arranged = new();

    // 悬停/选中的方块:和 explorer 桌面一样是浅蓝半透明(悬停更淡,选中更实)
    private static readonly Brush HoverBrush = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    private static readonly Brush SelectedBrush = Frozen(Color.FromArgb(0x4D, 0xB4, 0xDC, 0xFF));
    private static readonly Brush SelectedBorderBrush = Frozen(Color.FromArgb(0x8C, 0x8C, 0xC8, 0xF0));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
    /// <summary>同一个进程里只允许一个 helper 菜单在弹。</summary>
    private readonly SemaphoreSlim _menuGate = new(1, 1);
    private Point _dragStart;
    private bool _dragCandidate;
    private string? _dragPath;
    private bool _disposed;

    /// <summary>按下时"暂时不动选中态"的那个磁贴:松手没拖走才塌成单选(见 SelectionClickPolicy)。</summary>
    private Border? _pendingCollapseTile;
    private string? _pendingCollapsePath;

    /// <summary>当前选中的磁贴(支持框选多选)。</summary>
    private readonly HashSet<Border> _selectedTiles = new();
    /// <summary>选中项的路径:重画(新增/删除文件)后按它把选中态搬到新磁贴上。</summary>
    private readonly HashSet<string> _selectedPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>常驻的鼠标钩子:空白处"点别处取消选中"和"拉框多选"都要靠它(见 LowLevelMouseHook)。</summary>
    private readonly Native.LowLevelMouseHook _mouseHook;

    // ---- 框选状态 ----
    private Border? _marquee;
    private Point _marqueeOrigin;
    private bool _marqueeSelecting;
    /// <summary>拉框期间把窗口区域铺满整屏了没。只有"真的拖动起来"才铺:按下的那一下必须留给桌面。</summary>
    private bool _regionExpanded;
    /// <summary>上一次记录的区域状态(用来只在变化时写日志 —— 钩子回调里不该反复写文件)。</summary>
    private int _lastLoggedRegion = int.MinValue;
    /// <summary>拉框开始前的选中快照:按住 Ctrl 拉框 = 加选。</summary>
    private readonly HashSet<string> _selectionBeforeMarquee = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Brush MarqueeFill = Frozen(Color.FromArgb(0x33, 0x99, 0xCC, 0xFF));
    private static readonly Brush MarqueeStroke = Frozen(Color.FromArgb(0x99, 0x99, 0xCC, 0xFF));

    public DesktopLayerWindow(IntPtr host, MainViewModel vm, IIconExtractorService icons)
    {
        _vm = vm;
        _icons = icons;

        var bounds = Controls.DesktopDropOverlay.GetVirtualDesktopBounds();
        var client = Native.User32.ScreenPointToClient(host, (int)bounds.Left, (int)bounds.Top);

        var parameters = new HwndSourceParameters("DesktopBox.DesktopLayer")
        {
            ParentWindow = host,
            WindowStyle = WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
            ExtendedWindowStyle = WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE,
            PositionX = client.X,
            PositionY = client.Y,
            Width = Math.Max(1, (int)Math.Ceiling(bounds.Width)),
            Height = Math.Max(1, (int)Math.Ceiling(bounds.Height)),
            UsesPerPixelOpacity = false
        };

        _source = new HwndSource(parameters);
        _handle = _source.Handle;
        _source.CompositionTarget.BackgroundColor = Colors.Transparent;

        var transform = _source.CompositionTarget.TransformToDevice;
        _scaleX = transform.M11 <= 0 ? 1 : transform.M11;
        _scaleY = transform.M22 <= 0 ? 1 : transform.M22;

        _canvas = new Canvas
        {
            Width = Math.Max(1, bounds.Width / _scaleX),
            Height = Math.Max(1, bounds.Height / _scaleY),
            Background = null            // 不铺底:空白处不该有我们的任何绘制
        };
        _source.RootVisual = _canvas;

        LoadPositions();

        Native.User32.SetWindowPos(_handle, new IntPtr(HWND_BOTTOM), parameters.PositionX, parameters.PositionY,
            parameters.Width, parameters.Height, Native.User32.SWP_CROSSPROC);

        Update();

        // 鼠标钩子常驻:空白处按下时要能"开始拉框"(那一刻还没有选中项,临时装就永远等不到)
        _mouseHook = new Native.LowLevelMouseHook();
        _mouseHook.LeftButtonDown += OnHookLeftButtonDown;
        _mouseHook.ButtonDown += OnHookButtonDown;
        _mouseHook.DragMove += OnHookDragMove;
        _mouseHook.DragEnd += OnHookDragEnd;
    }

    public IntPtr Handle => _disposed || _source.IsDisposed ? IntPtr.Zero : _handle;

    public bool IsHandleAlive => !_disposed && !_source.IsDisposed && Native.User32.IsWindow(_handle);

    /// <summary>重新扫描"未收纳项"并重排;物品集合没变时也安全(幂等)。</summary>
    public void Update()
    {
        if (!IsHandleAlive) return;
        try
        {
            Build(_vm.LooseDesktopItems());
            ApplyRegion();
        }
        catch (Exception ex)
        {
            App.LogError(ex, "DesktopLayer.Update");
        }
    }

    private void Build(IReadOnlyList<string> paths)
    {
        _canvas.Children.Clear();
        _cells.Clear();
        _tilesByPath.Clear();
        _labels.Clear();

        var keepSelected = _selectedPaths.ToList();   // 旧磁贴作废,下面按路径重新认领

        _cellW = Math.Max(72, SystemParameters.IconHorizontalSpacing);
        _cellH = Math.Max(72, SystemParameters.IconVerticalSpacing);

        var ordered = paths
            .OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var arranged = Models.DesktopLayerLayout.Arrange(ordered, _positions,
            _canvas.Width, _canvas.Height, _cellW, _cellH, EdgePadding, BottomReserve);
        _arranged = arranged;

        foreach (var cell in arranged)
        {
            var tile = CreateTile(cell.Path, _cellW, _cellH);
            Canvas.SetLeft(tile, cell.X);
            Canvas.SetTop(tile, cell.Y);
            _canvas.Children.Add(tile);
            _tilesByPath[cell.Path] = tile;
            _cells.Add(((int)Math.Floor(cell.X), (int)Math.Floor(cell.Y),
                        (int)Math.Ceiling(_cellW), (int)Math.Ceiling(_cellH)));
        }

        // 选中态跟到新磁贴上;那项已经不在了才真取消
        var restored = new List<(Border Host, string Path)>();
        foreach (var path in keepSelected)
        {
            if (_tilesByPath.TryGetValue(path, out var stillThere)) restored.Add((stillThere, path));
        }

        SetSelection(restored);

        Services.LogService.Info("DesktopLayer.Build", $"loose={arranged.Count} placed={_positions.Count}");
    }

    private Border CreateTile(string path, double cellW, double cellH)
    {
        var host = new Border
        {
            Width = cellW,
            Height = cellH,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            AllowDrop = true
        };

        var image = new Image
        {
            Width = IconSize,
            Height = IconSize,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var label = new TextBlock
        {
            Text = DisplayName(path),
            FontSize = LabelSize,
            LineHeight = LabelLineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            MaxHeight = LabelMaxHeight,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            // 名字太长也别无声裁掉:留个省略号,至少知道"后面还有内容"
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = cellW - 6,
            Margin = new Thickness(0),
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 4,
                ShadowDepth = 1,
                Direction = 315,
                Opacity = 0.9
            }
        };

        _labels[path] = label;
        var stack = new StackPanel { Margin = new Thickness(0) };
        stack.Children.Add(image);
        stack.Children.Add(label);
        host.Child = stack;

        host.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount >= 2)
            {
                Open(path);
                e.Handled = true;
                return;
            }

            // Ctrl 单击 = 加选/取消(和 explorer 桌面一样)
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                ClearPendingCollapse();
                ToggleSelection(host, path);
                return;
            }

            // 多选之后按住其中一条往外拖 = 整组一起拖:这一下先别塌成单选,
            // 否则拖到盒子/别处时只会过去一条。塌陷推迟到"松手没拖走"再做。
            if (Controls.SelectionClickPolicy.DeferCollapseOnPress(false, _selectedTiles.Contains(host), _selectedTiles.Count))
            {
                _pendingCollapseTile = host;
                _pendingCollapsePath = path;
                return;
            }

            ClearPendingCollapse();
            Select(host, path);
        };
        host.MouseEnter += (_, _) =>
        {
            if (!_selectedTiles.Contains(host)) host.Background = HoverBrush;
        };
        host.MouseLeave += (_, _) =>
        {
            if (!_selectedTiles.Contains(host)) host.Background = Brushes.Transparent;
        };
        host.MouseRightButtonUp += (_, e) =>
        {
            ShowShellMenu(path);
            e.Handled = true;
        };
        host.DragOver += OnTileDragOver;
        host.Drop += OnTileDrop;
        host.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragStart = e.GetPosition(null);
            _dragPath = path;
            _dragCandidate = true;
        };
        host.PreviewMouseMove += OnTileMouseMove;
        host.PreviewMouseLeftButtonUp += (_, _) =>
        {
            _dragCandidate = false;
            // 原地松手(没拖走)= 普通单击:这时候才把多选塌成这一条
            if (_pendingCollapseTile is null) return;
            var collapse = ReferenceEquals(_pendingCollapseTile, host);
            var collapsePath = _pendingCollapsePath;
            ClearPendingCollapse();
            if (collapse && collapsePath is not null) Select(host, collapsePath);
        };

        LoadIconAsync(path, image);
        return host;
    }

    /// <summary>单击选中(和 explorer 桌面一样:浅蓝底 + 描边);Ctrl 单击 = 加选/取消。</summary>
    private void Select(Border host, string path) => SetSelection(new[] { (host, path) });

    private void ToggleSelection(Border host, string path)
    {
        if (_selectedTiles.Contains(host))
        {
            host.Background = Brushes.Transparent;
            host.BorderBrush = Brushes.Transparent;
            _selectedTiles.Remove(host);
            _selectedPaths.Remove(path);
            Services.LogService.Info("DesktopLayer.Select", $"取消 {Services.LogService.Truncate(path)}");
            return;
        }

        _selectedTiles.Add(host);
        _selectedPaths.Add(path);
        host.Background = SelectedBrush;
        host.BorderBrush = SelectedBorderBrush;
        Services.LogService.Info("DesktopLayer.Select", $"加选 {Services.LogService.Truncate(path)}");
    }

    /// <summary>把选中态设成这批(空 = 全部取消)。</summary>
    private void SetSelection(IReadOnlyCollection<(Border Host, string Path)> tiles)
    {
        ClearSelectionVisuals();
        foreach (var (host, path) in tiles)
        {
            _selectedTiles.Add(host);
            _selectedPaths.Add(path);
            host.Background = SelectedBrush;
            host.BorderBrush = SelectedBorderBrush;
        }

        if (_selectedPaths.Count == 0) return;
        Services.LogService.Info("DesktopLayer.Select",
            $"选中 {_selectedPaths.Count} 项,首项={Services.LogService.Truncate(_selectedPaths.First())}");
    }

    private void ClearPendingCollapse()
    {
        _pendingCollapseTile = null;
        _pendingCollapsePath = null;
    }

    /// <summary>取消所有选中(不动钩子 —— 钩子是常驻的)。</summary>
    private void ClearSelection()
    {
        if (_selectedTiles.Count == 0 && _selectedPaths.Count == 0) return;
        ClearPendingCollapse();
        ClearSelectionVisuals();
    }

    private void ClearSelectionVisuals()
    {
        foreach (var tile in _selectedTiles)
        {
            tile.Background = Brushes.Transparent;
            tile.BorderBrush = Brushes.Transparent;
        }

        _selectedTiles.Clear();
        _selectedPaths.Clear();
    }

    // ---- 鼠标钩子:这就是我们"看得见桌面空白处"的唯一途径 ----

    /// <summary>左键按下:落在空白处 = 开始拉框多选(explorer 的框选)。</summary>
    private void OnHookLeftButtonDown(int screenX, int screenY)
    {
        if (_marqueeSelecting) return;
        if (TileAt(screenX, screenY) is not null) return;      // 点在图标上 → 交给磁贴自己的选中逻辑
        // 只在"按在桌面上"时才拉框:按在别的程序窗口上,那一下不属于我们
        if (!IsDesktopWindowAt(screenX, screenY)) return;

        var additive = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        _selectionBeforeMarquee.Clear();
        if (additive)
        {
            foreach (var path in _selectedPaths) _selectionBeforeMarquee.Add(path);
        }
        else
        {
            ClearSelection();
        }

        _marqueeOrigin = ToCanvasPoint(screenX, screenY);
        _marqueeSelecting = true;
        _mouseHook.TrackingDrag = true;
        // 这里**不能**把区域铺满整屏:按下的那一下消息必须留给桌面本身 ——
        // 铺满的话光标下的窗口当场变成我们,Wallpaper Engine 的交互按钮(以及桌面的单击/右键)
        // 就只剩一个"没有按下的抬起",点了毫无反应。等真的拖出框了再铺,见 OnHookDragMove。
    }

    /// <summary>任意键按下:点到选中项以外(别的图标、桌面空白、别的窗口)就取消选中。</summary>
    private void OnHookButtonDown(int screenX, int screenY)
    {
        if (_marqueeSelecting || _selectedTiles.Count == 0) return;
        // Ctrl 加选:这一下是往选中集合里"添",不能先把已有的清掉
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        if (TileAt(screenX, screenY) is { } hit && _selectedTiles.Contains(hit)) return;

        Services.LogService.Info("DesktopLayer.Deselect", $"点到别处 ({screenX},{screenY}) → 取消选中");
        ClearSelection();
    }

    private void OnHookDragMove(int screenX, int screenY)
    {
        if (!_marqueeSelecting) return;
        var current = ToCanvasPoint(screenX, screenY);

        // 真的拖出框了才把区域铺满整屏:框要能画到空白处。没超过拖动阈值就一直当作"单击",
        // 区域原样留着 —— 单击必须完整地落到桌面上。
        if (!_regionExpanded)
        {
            if (Math.Abs(current.X - _marqueeOrigin.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(current.Y - _marqueeOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            _regionExpanded = true;
            ApplyRegion(full: true);
        }

        DrawMarquee(_marqueeOrigin, current);
    }

    private void OnHookDragEnd(int screenX, int screenY)
    {
        if (!_marqueeSelecting) return;
        _marqueeSelecting = false;

        var rect = new Rect(_marqueeOrigin, ToCanvasPoint(screenX, screenY));
        RemoveMarquee();
        if (_regionExpanded)
        {
            _regionExpanded = false;
            ApplyRegion(full: false);     // 空白处还给 explorer
        }

        // 只是"点了一下"没拉出框 → 等同于点了空白处(选中已在按下时清掉)
        if (rect.Width < 4 && rect.Height < 4) return;

        var hits = new List<(Border Host, string Path)>();
        foreach (var (path, host) in _tilesByPath)
        {
            var bounds = new Rect(Canvas.GetLeft(host), Canvas.GetTop(host), host.ActualWidth, host.ActualHeight);
            if (bounds.IntersectsWith(rect)) hits.Add((host, path));
        }

        // Ctrl 拉框 = 加选:把拉框前已经选中的也带上
        foreach (var path in _selectionBeforeMarquee)
        {
            if (hits.Any(h => string.Equals(h.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            if (_tilesByPath.TryGetValue(path, out var host)) hits.Add((host, path));
        }

        SetSelection(hits);
    }

    /// <summary>Delete 键(全局钩子送进来):光标正压在我们画的图标上或桌面空白处、且本层有选中项时,
    /// 把选中项移到回收站。返回是否处理了。</summary>
    public bool TryDeleteSelectionAt(int screenX, int screenY)
    {
        if (_selectedPaths.Count == 0) return false;
        if (TileAt(screenX, screenY) is null && !IsDesktopWindowAt(screenX, screenY)) return false;

        var paths = _selectedPaths.ToList();
        var removed = _vm.MoveToRecycleBin(paths);
        if (removed > 0) ClearSelection();
        return removed > 0;
    }

    /// <summary>屏幕点上是不是"桌面"本身(而不是别的程序窗口/我们自己的东西)。</summary>
    private static bool IsDesktopWindowAt(int screenX, int screenY)
    {
        try
        {
            var hwnd = Native.User32.WindowFromPoint(screenX, screenY);
            if (hwnd == IntPtr.Zero) return false;

            var name = new System.Text.StringBuilder(64);
            Native.User32.GetClassName(hwnd, name, name.Capacity);
            var cls = name.ToString();

            // 用户桌面的图标层是 SHELLDLL_DefView;壁纸/宿主是 Progman / WorkerW
            return cls is "SHELLDLL_DefView" or "Progman" or "WorkerW"
                   || cls.Contains("SysListView32", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>屏幕物理坐标 → 层内 DIP 坐标(画框和命中判断都用它)。</summary>
    private Point ToCanvasPoint(int screenX, int screenY)
    {
        var bounds = Controls.DesktopDropOverlay.GetVirtualDesktopBounds();
        return new Point((screenX - bounds.Left) / _scaleX, (screenY - bounds.Top) / _scaleY);
    }

    /// <summary>屏幕点上是不是某块磁贴(是就返回它)。</summary>
    private Border? TileAt(int screenX, int screenY)
    {
        foreach (var host in _tilesByPath.Values)
        {
            if (_selectedTiles.Contains(host)) continue;
            var rect = ScreenRectOf(host);
            if (!rect.IsEmpty && rect.Contains(screenX, screenY)) return host;
        }

        foreach (var host in _selectedTiles)
        {
            var rect = ScreenRectOf(host);
            if (!rect.IsEmpty && rect.Contains(screenX, screenY)) return host;
        }

        return null;
    }

    private void DrawMarquee(Point from, Point to)
    {
        var rect = new Rect(from, to);
        if (_marquee is null)
        {
            _marquee = new Border
            {
                Background = MarqueeFill,
                BorderBrush = MarqueeStroke,
                BorderThickness = new Thickness(1),
                IsHitTestVisible = false
            };
            _canvas.Children.Add(_marquee);
        }

        Canvas.SetLeft(_marquee, rect.Left);
        Canvas.SetTop(_marquee, rect.Top);
        _marquee.Width = Math.Max(0, rect.Width);
        _marquee.Height = Math.Max(0, rect.Height);
    }

    private void RemoveMarquee()
    {
        if (_marquee is null) return;
        _canvas.Children.Remove(_marquee);
        _marquee = null;
    }

    private static Rect ScreenRectOf(FrameworkElement element)
    {
        try
        {
            var topLeft = element.PointToScreen(new Point(0, 0));
            var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
            return new Rect(topLeft, bottomRight);
        }
        catch
        {
            return Rect.Empty;
        }
    }

    private void OnTileMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragCandidate || _dragPath is null || e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragCandidate = false;
        ClearPendingCollapse();     // 已经变成拖拽了,松手时不该再塌陷(拖完保持整组选中)
        if (sender is FrameworkElement element) StartDrag(element, _dragPath);
    }

    private void LoadIconAsync(string path, Image target)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var iconPath = _icons.Extract(path);
                if (string.IsNullOrEmpty(iconPath) || !File.Exists(iconPath)) return;

                var dispatcher = _canvas.Dispatcher;
                if (dispatcher is null || dispatcher.HasShutdownStarted) return;
                dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_disposed) return;
                    try
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.UriSource = new Uri(iconPath!);
                        bitmap.EndInit();
                        bitmap.Freeze();
                        target.Source = bitmap;
                    }
                    catch { /* 单个图标提取失败不影响其余 */ }
                }));
            }
            catch { /* 提取失败就留空 */ }
        });
    }

    private static string DisplayName(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            return name[..^4];
        if (name.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
            return name[..^4];
        return name;
    }

    private static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Services.LogService.Warn("DesktopLayer.Open", Services.LogService.Truncate(ex.Message, 160));
        }
    }

    /// <summary>右键:走和盒子里同一套 helper 菜单(独立进程弹,崩了也不影响我们),额外追加"重命名"。
    /// helper 起不来时退回一个精简的 WPF 菜单,免得右键完全没反应。</summary>
    private async void ShowShellMenu(string path)
    {
        if (!await _menuGate.WaitAsync(0)) return;
        try
        {
            var cursor = System.Windows.Forms.Cursor.Position;
            var runner = App.Services.GetRequiredService<IShellMenuRunner>();
            var result = await runner.ShowAsync(path, cursor.X, cursor.Y, ShellMenuCommands.Rename);
            switch (result.Status)
            {
                case ShellMenuRunStatus.Rename:
                    PromptRename(path);
                    return;
                case ShellMenuRunStatus.StartFailed:
                case ShellMenuRunStatus.IsolationUnavailable:
                case ShellMenuRunStatus.Crashed:
                case ShellMenuRunStatus.TimedOut:
                    ShowFallbackMenu(path);
                    return;
                default:
                    Update();       // 菜单里可能改了名/删了/移动了
                    return;
            }
        }
        catch (Exception ex)
        {
            App.LogError(ex, "DesktopLayer.Menu");
        }
        finally
        {
            _menuGate.Release();
        }
    }

    private void PromptRename(string path)
    {
        var current = Path.GetFileName(path);
        var anchor = LabelScreenRect(path);
        if (anchor.IsEmpty)
        {
            Services.LogService.Warn("DesktopLayer.Rename", $"拿不到标签位置,取消改名 path={Services.LogService.Truncate(path)}");
            return;
        }

        _ = new InlineRenameBox(current, anchor, LabelSize, result =>
        {
            if (string.IsNullOrWhiteSpace(result))
            {
                Update();
                return;
            }

            var newPath = _vm.RenameDesktopPath(path, result);
            // 摆放位置是按路径记的:改名后路径变了,得把它搬过去,否则图标会掉回"第一个空格子"
            MoveSavedPosition(path, newPath);
            Update();
        });
    }

    /// <summary>改名后把记住的摆放位置搬到新路径上。</summary>
    private void MoveSavedPosition(string oldPath, string? newPath)
    {
        if (string.IsNullOrEmpty(newPath)) return;
        if (!_positions.Remove(oldPath, out var saved)) return;

        _positions[newPath] = saved;
        SavePositions();
    }

    /// <summary>改名输入框的锚点:优先贴着名字那一行;拿不到就退回整块磁贴。</summary>
    private Rect LabelScreenRect(string path)
    {
        if (_labels.TryGetValue(path, out var label))
        {
            var rect = InlineRenameBox.ScreenRectOf(label);
            if (!rect.IsEmpty) return rect;
        }

        if (_tilesByPath.TryGetValue(path, out var host))
        {
            var rect = InlineRenameBox.ScreenRectOf(host);
            if (!rect.IsEmpty) return rect;
        }

        return Rect.Empty;
    }

    private void ShowFallbackMenu(string path)
    {
        var localizer = App.Services.GetRequiredService<ILocalizerService>();
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };

        var open = new MenuItem { Header = localizer["menu.openItem"] };
        open.Click += (_, _) => Open(path);
        menu.Items.Add(open);

        var rename = new MenuItem { Header = localizer["menu.renameItem"] };
        rename.Click += (_, _) => PromptRename(path);
        menu.Items.Add(rename);

        menu.IsOpen = true;
    }

    /// <summary>把小图标拖出去:带上 FileDrop,于是拖进盒子就能被收纳(盒子按 FileDrop 收),
    /// 拖进资源管理器窗口也是正常的移动/复制。选中多项时整组一起拖。</summary>
    private void StartDrag(FrameworkElement source, string path)
    {
        var dragged = _selectedPaths.Contains(path) && _selectedPaths.Count > 1
            ? _selectedPaths.ToList()
            : new List<string> { path };

        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, dragged.ToArray());

        var boxBounds = Controls.DesktopDropOverlay.OccupiedBoxRects(_vm.Boxes);
        using var desktopDrop = Controls.DesktopDropOverlay.TryCreate(boxBounds);
        var droppedOnDesktop = false;
        (double X, double Y)? dropPoint = null;
        try
        {
            var effect = DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Move);
            // effect=None 就是"没有落点接住"(拖到盒子以外的地方会这样),便于对着日志排查
            Services.LogService.Info("DesktopLayer.DragOut",
                $"count={dragged.Count} effect={effect} path={Services.LogService.Truncate(path)}");
            // 拖到桌面空白处 = 换个位置摆着(落在盒子上则由盒子收走,覆盖层不会接)
            droppedOnDesktop = desktopDrop?.DroppedOnDesktop == true;
            dropPoint = desktopDrop?.DropScreenPosition;
        }
        catch (Exception ex)
        {
            App.LogError(ex, "DesktopLayer.DragOut");
        }
        finally
        {
            if (droppedOnDesktop && dropPoint is { } point) RememberPositions(path, dragged, point.X, point.Y);
            Update();       // 拖走/复制之后桌面上的东西可能变了
        }
    }

    /// <summary>把整组拖到桌面空白处:按相对偏移一起摆过去(鼠标按住的那条落在鼠标处),再各自吸附到网格。</summary>
    private void RememberPositions(string anchorPath, IReadOnlyList<string> paths, double screenX, double screenY)
    {
        try
        {
            var bounds = Controls.DesktopDropOverlay.GetVirtualDesktopBounds();
            var anchorX = (screenX - bounds.Left) / _scaleX - _cellW / 2;
            var anchorY = (screenY - bounds.Top) / _scaleY - _cellH / 2;

            var anchorOrigin = CurrentOrigin(anchorPath);
            var deltaX = anchorX - anchorOrigin.X;
            var deltaY = anchorY - anchorOrigin.Y;

            var maxX = Math.Max(EdgePadding, _canvas.Width - EdgePadding - _cellW);
            var maxY = Math.Max(EdgePadding, _canvas.Height - BottomReserve - _cellH);
            var taken = _arranged
                .Where(c => !paths.Any(p => string.Equals(p, c.Path, StringComparison.OrdinalIgnoreCase)))
                .Select(c => (c.X, c.Y))
                .ToList();

            foreach (var path in paths)
            {
                var origin = CurrentOrigin(path);
                var snapped = Models.DesktopLayerLayout.Snap(origin.X + deltaX, origin.Y + deltaY,
                    taken, _cellW, _cellH, EdgePadding, maxX, maxY);
                _positions[path] = snapped;
                taken.Add(snapped);
            }

            SavePositions();
            Services.LogService.Info("DesktopLayer.Move",
                $"count={paths.Count} 位移=({deltaX:F0},{deltaY:F0}) anchor={Services.LogService.Truncate(anchorPath)}");
        }
        catch (Exception ex)
        {
            App.LogError(ex, "DesktopLayer.RememberPositions");
        }
    }

    /// <summary>某项当前摆在哪:记过位置就按记录,否则用自动排好的格子。</summary>
    private (double X, double Y) CurrentOrigin(string path)
    {
        if (_positions.TryGetValue(path, out var saved)) return saved;

        foreach (var cell in _arranged)
            if (string.Equals(cell.Path, path, StringComparison.OrdinalIgnoreCase))
                return (cell.X, cell.Y);

        return (EdgePadding, EdgePadding);
    }

    /// <summary>把"拖到桌面空白处"当成重新摆放:位置吸附到网格后落盘(默认"对齐到网格"),
    /// 下次启动还在那儿。</summary>
    private void RememberPosition(string path, double screenX, double screenY)
    {
        try
        {
            var bounds = Controls.DesktopDropOverlay.GetVirtualDesktopBounds();
            var rawX = (screenX - bounds.Left) / _scaleX - _cellW / 2;
            var rawY = (screenY - bounds.Top) / _scaleY - _cellH / 2;

            var maxX = Math.Max(EdgePadding, _canvas.Width - EdgePadding - _cellW);
            var maxY = Math.Max(EdgePadding, _canvas.Height - BottomReserve - _cellH);
            var taken = _arranged
                .Where(c => !string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase))
                .Select(c => (c.X, c.Y))
                .ToList();

            var snapped = Models.DesktopLayerLayout.Snap(rawX, rawY, taken, _cellW, _cellH, EdgePadding, maxX, maxY);
            _positions[path] = snapped;
            SavePositions();
            Services.LogService.Info("DesktopLayer.Move",
                $"path={Services.LogService.Truncate(path)} raw=({rawX:F0},{rawY:F0}) -> ({snapped.X:F0},{snapped.Y:F0})");
        }
        catch (Exception ex)
        {
            App.LogError(ex, "DesktopLayer.RememberPosition");
        }
    }

    /// <summary>清掉记住的位置 → 回到自动排列(等价于 explorer 的"自动排列图标")。</summary>
    public void ResetPositions()
    {
        if (_positions.Count == 0) return;
        _positions.Clear();
        SavePositions();
        Update();
        Services.LogService.Info("DesktopLayer.Arrange", "已重新排列桌面图标");
    }

    private void LoadPositions()
    {
        try
        {
            if (!File.Exists(_positionsPath)) return;
            var saved = System.Text.Json.JsonSerializer
                .Deserialize<Dictionary<string, double[]>>(File.ReadAllText(_positionsPath));
            if (saved is null) return;

            foreach (var (path, value) in saved)
                if (value is { Length: 2 }) _positions[path] = (value[0], value[1]);
        }
        catch (Exception ex)
        {
            Services.LogService.Warn("DesktopLayer.Positions", Services.LogService.Truncate(ex.Message, 160));
        }
    }

    private void SavePositions()
    {
        try
        {
            var saved = _positions.ToDictionary(p => p.Key, p => new[] { p.Value.X, p.Value.Y });
            File.WriteAllText(_positionsPath, System.Text.Json.JsonSerializer.Serialize(saved));
        }
        catch (Exception ex)
        {
            Services.LogService.Warn("DesktopLayer.Positions", Services.LogService.Truncate(ex.Message, 160));
        }
    }

    private void OnTileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? e.AllowedEffects : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>落在图标上的拖放:我们自己就是这块区域的宿主,不处理的话系统会一路显示"禁止"光标。
    /// 按拖放效果把文件放进桌面目录,然后重画。</summary>
    private void OnTileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var move = (e.AllowedEffects & DragDropEffects.Move) != 0;
        _vm.HandleFilesDroppedOnDesktop(paths, move);
        e.Effects = move ? DragDropEffects.Move : DragDropEffects.Copy;
        e.Handled = true;
        Update();
    }

    /// <summary>把窗口区域裁成图标矩形并集:空白处不属于我们,鼠标事件照旧归 explorer。
    /// 拉框期间(<paramref name="full"/>=true)要临时铺满整个桌面,否则框画不到空白处。</summary>
    private void ApplyRegion(bool full = false)
    {
        if (full)
        {
            // 不给区域 = 整个窗口矩形都在(拉框用;松手后立刻收回)
            Native.User32.SetWindowRgn(_handle, IntPtr.Zero, true);
            LogRegion(-1);
            return;
        }

        if (_cells.Count == 0)
        {
            // 没有未收纳项 → 空区域,整个桌面都归 explorer(右键菜单、拖放都不受影响)
            var empty = Native.Gdi32.CreateRectRgn(0, 0, 0, 0);
            if (empty != IntPtr.Zero) Native.User32.SetWindowRgn(_handle, empty, true);
            LogRegion(0);
            return;
        }

        var region = IntPtr.Zero;
        foreach (var (x, y, w, h) in _cells)
        {
            var rect = Native.Gdi32.CreateRectRgn(
                (int)Math.Floor(x * _scaleX) - 2,
                (int)Math.Floor(y * _scaleY) - 2,
                (int)Math.Ceiling((x + w) * _scaleX) + 2,
                (int)Math.Ceiling((y + h) * _scaleY) + 2);
            if (rect == IntPtr.Zero) continue;

            if (region == IntPtr.Zero)
            {
                region = rect;
                continue;
            }

            Native.Gdi32.CombineRgn(region, region, rect, Native.Gdi32.RGN_OR);
            Native.Gdi32.DeleteObject(rect);
        }

        if (region == IntPtr.Zero) return;
        if (Native.User32.SetWindowRgn(_handle, region, true) == 0)
            Native.Gdi32.DeleteObject(region);
        LogRegion(_cells.Count);
    }

    /// <summary>区域状态只在变化时写日志:这段代码会在鼠标钩子回调里跑,不能反复做文件 I/O。</summary>
    private void LogRegion(int cells)
    {
        if (_lastLoggedRegion == cells) return;
        _lastLoggedRegion = cells;
        Services.LogService.Info("DesktopLayer.Region", $"cells={cells}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _mouseHook.Dispose();

        try
        {
            if (Native.User32.IsWindow(_handle))
            {
                Native.User32.ShowWindow(_handle, 0);
                Native.User32.SetParent(_handle, IntPtr.Zero);
            }
        }
        catch { }

        _source.RootVisual = null;
        _source.Dispose();
    }
}
