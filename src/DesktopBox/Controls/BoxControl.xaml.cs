using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopBox.Models;
using DesktopBox.Services;
using DesktopBox.ViewModels;
using DesktopBox.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopBox.Controls;

public partial class BoxControl : UserControl
{
    private Point _dragOrigin;
    private Point _boxOrigin;            // 拖动起始时盒子位置:用"起点+累积位移"算意图位置,不受磁吸覆盖
    private bool _isDragging;
    private bool _isResizing;
    private readonly BoxEdgeSnapState _snapX = new();
    private readonly BoxEdgeSnapState _snapY = new();
    private MainViewModel? _mainVm;
    private ILocalizerService? _localizer;
    private BoxViewModel? _subscribedVm;
    private FirstLetterKeyboardNavigator? _keyboardNavigator;

    private MainViewModel MainVm =>
        _mainVm ??= App.Services.GetRequiredService<MainViewModel>();

    private ILocalizerService Localizer =>
        _localizer ??= App.Services.GetRequiredService<ILocalizerService>();

    private FirstLetterKeyboardNavigator KeyboardNavigator =>
        _keyboardNavigator ??= App.Services.GetRequiredService<FirstLetterKeyboardNavigator>();

    // ---- 框选(按住左键在条目区拉个框,松手选中框里的条目;explorer 的多选就是这套)----
    private Canvas? _marqueeLayer;
    private Border? _marqueeBox;
    private Point _marqueeOrigin;
    private bool _marqueeDragging;
    /// <summary>拉框开始前的选中快照:按住 Ctrl 拉框 = 加选。</summary>
    private readonly HashSet<Guid> _selectionBeforeMarquee = new();
    private static readonly Brush MarqueeFill = Frozen(Color.FromArgb(0x33, 0x99, 0xCC, 0xFF));
    private static readonly Brush MarqueeStroke = Frozen(Color.FromArgb(0x99, 0x99, 0xCC, 0xFF));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public BoxControl()
    {
        InitializeComponent();
        // WPF-UI hover 动画打 frozen 主题画刷会致命崩溃（v1.7.10）：Loaded 后把实例画刷解冻。
        Loaded += (_, _) =>
        {
            try { WpfUiAnimationGuard.UnfreezeSubtree(this); } catch { }
            UpdateCollapsedHeight();   // 标题栏实测高度(DPI/字号自适应)回填给 VM
            // 整盒子都能接住拖进来的文件:AllowDrop 不继承,必须把子树逐个打开,
            // 否则只有盒子自己的空白处能落,拖到图标或条目区会被拒。
            DropTargets.EnableOnSubtree(this);
            ApplyBoxOpacity();
            if (!_opacitySubscribed)
            {
                _opacitySubscribed = true;
                try { MainVm.PropertyChanged += OnMainVmPropertyChanged; } catch { }
            }
        };
        // 拉框期间鼠标被本控件捕获,移动/抬手都回到这里
        MouseMove += OnMarqueeMouseMove;
        MouseLeftButtonUp += OnMarqueeMouseUp;
        // 右键打开盒子菜单前,先把菜单锚点摆成"跟鼠标"
        PreviewMouseRightButtonUp += OnRightButtonUpForMenu;
        // 标题栏高度会随 DPI 缩放/系统字号/语言字体变化:持续跟随,保证卷起时刚好只剩标题栏。
        HeaderBar.SizeChanged += (_, _) => UpdateCollapsedHeight();
        Unloaded += (_, _) => StopHoverTimer();
        DataContextChanged += (_, _) =>
        {
            if (_subscribedVm is not null)
                _subscribedVm.ViewModeChanged -= OnViewModeChanged;

            _subscribedVm = Vm;
            if (_subscribedVm is not null)
                _subscribedVm.ViewModeChanged += OnViewModeChanged;
        };
    }

    private BoxViewModel? Vm => DataContext as BoxViewModel;

    private void OnBoxMouseEnter(object sender, MouseEventArgs e)
    {
        KeyboardNavigator.Activate(this);
        ScheduleHoverExpand(true);
    }

    private void OnBoxMouseLeave(object sender, MouseEventArgs e)
    {
        KeyboardNavigator.Deactivate(this);
        ScheduleHoverExpand(false);
    }

    // ---- 卷起 / 悬停自动展开 ----
    private DispatcherTimer? _hoverTimer;

    /// <summary>鼠标进入/离开后延时切换卷起态。
    /// 延时是为了不被"鼠标划过"或"在标题栏与条目之间移动时的瞬时出界"打断:
    /// 进入 180ms 后展开、离开 320ms 后收起(见 BoxCollapse)。</summary>
    private void ScheduleHoverExpand(bool expand)
    {
        if (Vm is null) return;
        var vm = Vm;
        if (expand)
        {
            if (!BoxCollapse.ShouldExpandOnHover(vm.IsCollapsed, vm.IsHoverExpanded, MainVm.HoverExpandCollapsedBoxes))
                return;
        }
        else if (!BoxCollapse.ShouldCollapseOnLeave(vm.IsHoverExpanded))
        {
            return;
        }

        StopHoverTimer();
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(expand ? BoxCollapse.HoverExpandDelayMs : BoxCollapse.HoverCollapseDelayMs)
        };
        timer.Tick += (_, _) =>
        {
            StopHoverTimer();
            if (!ReferenceEquals(vm, Vm)) return;   // DataContext 已换(窗口复用/回收)
            // 展开前复核鼠标仍在盒子内:延时期间已移开就放弃,否则会出现"鼠标走了它自己弹开"。
            if (expand && !IsMouseOver) return;
            vm.IsHoverExpanded = expand;
        };
        _hoverTimer = timer;
        timer.Start();
    }

    private void StopHoverTimer()
    {
        var timer = _hoverTimer;
        _hoverTimer = null;
        timer?.Stop();
    }

    /// <summary>卷起/展开切换(持久化)。手动切换时清掉悬停临时态,避免两种状态打架。</summary>
    private void ToggleCollapse()
    {
        if (Vm is null) return;
        StopHoverTimer();
        Vm.IsHoverExpanded = false;
        Vm.ToggleCollapsed();
        MainVm.ScheduleSave();
    }

    private void OnToggleCollapse(object sender, RoutedEventArgs e) => ToggleCollapse();

    /// <summary>锁定/解锁位置(持久化)。锁定后不响应拖动/缩放,避免误移动。</summary>
    private void ToggleLock()
    {
        if (Vm is null) return;
        StopHoverTimer();
        Vm.ToggleLocked();
        MainVm.ScheduleSave();
    }

    private void OnToggleLock(object sender, RoutedEventArgs e) => ToggleLock();

    /// <summary>右键菜单:显示/隐藏本盒子标题栏的时间(按盒子生效并持久化,默认关闭)。</summary>
    private void OnToggleShowClock(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        Vm.ToggleShowClock();
        MainVm.ScheduleSave();
    }

    /// <summary>图标大小按钮:大 → 中 → 小图标循环(全局,所有盒子一致)。</summary>
    private void OnCycleIconSize(object sender, RoutedEventArgs e) => MainVm.CycleIconSize();

    /// <summary>盒子右键 →「一键整理桌面」(与托盘/桌面空白处右键同一命令)。</summary>
    private void OnOrganizeDesktop(object sender, RoutedEventArgs e)
        => MainVm.OrganizeCommand.Execute(null);

    /// <summary>鼠标移到某个标签名上就切换过去(等价于点一下该标签)。</summary>
    private void OnTabItemMouseEnter(object sender, MouseEventArgs e)
    {
        if (Vm is null) return;
        if (sender is ListBoxItem { DataContext: BoxTab tab } && !ReferenceEquals(Vm.SelectedTab, tab))
            Vm.SelectedTab = tab;
    }

    /// <summary>把"标题栏实测高度 + 卡片边框(物理像素)"写回 VM,作为卷起后的窗口高度。
    /// 写死常量在 DPI 缩放/系统字号下会裁掉标题或露出一截内容,所以实测回填。</summary>
    private void UpdateCollapsedHeight()
    {
        if (Vm is null) return;
        var contentHeight = HeaderBar.ActualHeight;
        if (contentHeight <= 0) return;   // 尚未完成布局

        var border = Card.BorderThickness.Top + Card.BorderThickness.Bottom;
        var scale = GetDpiScale();
        // 尺寸换算:模型用的是物理像素,而 WPF 布局用 DIP(物理/缩放)。
        var measured = Math.Round((contentHeight + border) * scale.Y);
        if (measured <= 0 || Math.Abs(measured - Vm.CollapsedHeight) < 0.5) return;
        Vm.CollapsedHeight = measured;
    }

    public bool NavigateByFirstLetter(char key)
    {
        if (Vm is null || Vm.DisplayItems.Count == 0)
            return false;

        var normalized = char.ToUpperInvariant(key);
        var currentIndex = _lastNavigationKey == normalized ? _lastNavigationIndex : -1;
        var nextIndex = FirstLetterNavigator.FindNextIndex(Vm.DisplayItems, normalized, currentIndex);
        if (nextIndex < 0)
            return false;

        _lastNavigationKey = normalized;
        _lastNavigationIndex = nextIndex;

        var item = Vm.DisplayItems[nextIndex];
        MainVm.ClearSelection();
        item.IsSelected = true;
        ScrollItemIntoView(item);
        return true;
    }

    private char? _lastNavigationKey;
    private int _lastNavigationIndex = -1;

    private void ScrollItemIntoView(BoxItem item)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateLayout();
            var tile = FindVisualChildren<ItemTile>(ItemsViewport)
                .FirstOrDefault(t => ReferenceEquals(t.DataContext, item));
            tile?.BringIntoView();
        }), DispatcherPriority.Loaded);
    }

    /// <summary>视图模式变更:切到详细信息时惰性填充大小/修改时间。</summary>
    private void OnViewModeChanged(BoxViewModel vm)
    {
        if (vm.ViewMode == ViewMode.Detail)
            MainVm.EnsureDetailFields(vm);
    }

    // ---- 拖动盒子 ----
    private void OnHeaderDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null) return;
        // 双击标题栏 = 卷起/展开(悬停展开是临时态,双击是用户明确的持久切换)。
        if (e.ClickCount >= 2)
        {
            ToggleCollapse();
            e.Handled = true;
            return;
        }
        if (!Vm.CanMove) return;   // 位置已锁定:不拖动
        ClearMergeTarget();
        _isDragging = true;
        _dragOrigin = PointToScreen(e.GetPosition(this));
        _boxOrigin = new Point(Vm.X, Vm.Y);   // 记录起点:拖动期间意图位置=起点+累积位移
        ResetSnapState();
        Mouse.Capture((IInputElement)sender);
        e.Handled = true;
    }

    private void OnHeaderMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || Vm is null) return;
        var pos = PointToScreen(e.GetPosition(this));
        // 意图位置 = 起点位置 + 累积位移(不被磁吸覆盖,慢拖也能持续累积以脱离磁吸)
        double x = _boxOrigin.X + (pos.X - _dragOrigin.X);
        double y = _boxOrigin.Y + (pos.Y - _dragOrigin.Y);
        ApplyEdgeMagnet(Vm, ref x, ref y);
        // 越界约束:保证盒子有足够可见区域留在屏内,防止拖出屏幕后只剩一条小边
        // 被父窗口(DefView 只在屏幕内)裁掉 = "拖着拖着就不见了"。
        var (cx, cy) = SystemParametersHelper.ClampIntoScreens(
            x, y, Math.Min(Vm.Width, 320), Math.Min(Vm.Height, 120));
        Vm.X = cx;
        Vm.Y = cy;

        // 拖动中:指针若落在别的盒子上,高亮它——松手就把本盒子合并进去
        UpdateMergeTarget(pos.X, pos.Y);
    }

    // ---- 拖动盒子合并(把一个盒子拖进另一个盒子 = 合并成标签盒子) ----
    private BoxViewModel? _mergeTarget;

    /// <summary>找出指针下的其它盒子并高亮成"合并目标"。</summary>
    private void UpdateMergeTarget(double screenX, double screenY)
    {
        if (Vm is null) return;
        var target = BoxHitTest.FindAt(MainVm.Boxes, screenX, screenY, Vm.Id);
        if (ReferenceEquals(target, _mergeTarget)) return;

        if (_mergeTarget is not null) _mergeTarget.IsMergeTarget = false;
        _mergeTarget = target;
        if (_mergeTarget is not null) _mergeTarget.IsMergeTarget = true;
    }

    /// <summary>清掉合并目标高亮(松手/取消拖动/窗口回收时都要清,避免高亮留在桌面上)。</summary>
    private void ClearMergeTarget()
    {
        _mergeTarget = null;
        try { BoxHitTest.ClearMergeTargets(MainVm.Boxes); } catch { /* 退出期:容器已不可用 */ }
    }

    /// <summary>边缘磁吸(带迟滞):盒子靠近显示器工作区边缘时自动贴合。</summary>
    private void ApplyEdgeMagnet(BoxViewModel vm, ref double x, ref double y)
    {
        var screens = SystemParametersHelper.AllScreens;
        x = EdgeMagnet.ApplyHorizontal(_snapX, x, vm.Width, screens);
        y = EdgeMagnet.ApplyVertical(_snapY, y, vm.Height, screens);
    }

    private void ResetSnapState()
    {
        _snapX.Snapped = false;
        _snapX.Edge = null;
        _snapY.Snapped = false;
        _snapY.Edge = null;
    }

    private void OnHeaderUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        Mouse.Capture(null);

        // 松手时指针停在别的盒子上 → 把本盒子合并成对方的一个标签(对方随即变成标签盒子)
        var target = _mergeTarget;
        ClearMergeTarget();
        if (target is not null && Vm is not null)
        {
            MainVm.MergeIn(target, Vm);
            return;
        }

        MainVm.ScheduleSave();
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        ClearMergeTarget();
        MainVm.ScheduleSave();
    }

    // ---- 缩放 ----
    // 实现：屏幕坐标锚定法。缩放中唯一的自由度是当前鼠标的屏幕位置；对角/对边锚点
    // 在 DragStarted 时用屏幕坐标钉死，永不移动。尺寸=鼠标−锚点（按方向投影），每次
    // DragDelta 独立计算、零累积。
    // 历史：旧版用 DragDelta 的 e.HorizontalChange 逐次累加到 _resizeDx/_resizeDy。
    // WPF Thumb 的 delta 相对 Thumb 自身，而 Thumb 钉在窗口边缘上——拖动时窗口一变大，
    // Thumb 就追着鼠标跑，相对位移反向，形成正反馈振荡（窗口忽大忽小、日志里 dx 抖到
    // ±9000）。改成鼠标绝对坐标后回路消失，鼠标指哪边在哪。
    private void OnResizeStarted(object sender, DragStartedEventArgs e)
    {
        if (Vm is null || !Vm.CanMove) return;   // 位置已锁定:不改尺寸
        _isResizing = true;
        _resizeDir = (sender as FrameworkElement)?.Tag as string ?? "SE";
        _resizeOrigin = new Rect(Vm.X, Vm.Y, Vm.Width, Vm.Height);
        _resizeAnchor = ComputeAnchor(_resizeOrigin, _resizeDir);
        Services.LogService.Info("BoxResize.Start",
            $"dir={_resizeDir} origin=({_resizeOrigin.X:0},{_resizeOrigin.Y:0},{_resizeOrigin.Width:0}x{_resizeOrigin.Height:0}) anchor=({_resizeAnchor.X:0},{_resizeAnchor.Y:0})");
    }

    private string _resizeDir = "SE";
    private Rect _resizeOrigin;
    private Point _resizeAnchor;

    /// <summary>方向的对侧锚点（拖动中固定不动）：SE/W/S 用左上系，NW/E/N 用右下系。</summary>
    private static Point ComputeAnchor(Rect r, string dir) => dir switch
    {
        "SE" => new Point(r.Left, r.Top),
        "E" => new Point(r.Left, r.Top),
        "S" => new Point(r.Left, r.Top),
        "NW" => new Point(r.Right, r.Bottom),
        "W" => new Point(r.Right, r.Bottom),
        "N" => new Point(r.Right, r.Bottom),
        "NE" => new Point(r.Left, r.Bottom),
        "SW" => new Point(r.Right, r.Top),
        _ => new Point(r.Left, r.Top)
    };

    private void OnResize(object sender, DragDeltaEventArgs e)
    {
        if (!_isResizing || Vm is null) return;
        var dir = (sender as FrameworkElement)?.Tag as string ?? "SE";
        // 只信当前鼠标屏幕位置（PointToScreen 映射稳定，与 Thumb 无关），不信 delta。
        var mouse = PointToScreen(Mouse.GetPosition(this));
        var bounds = SystemParametersHelper.VirtualBounds;
        double x = _resizeOrigin.X, y = _resizeOrigin.Y;
        double w = _resizeOrigin.Width, h = _resizeOrigin.Height;

        switch (dir)
        {
            case "SE":
                w = ClampRange(mouse.X - _resizeAnchor.X, BoxResize.MinWidth, bounds.Right - x);
                h = ClampRange(mouse.Y - _resizeAnchor.Y, BoxResize.MinHeight, bounds.Bottom - y);
                break;
            case "S":
                h = ClampRange(mouse.Y - _resizeAnchor.Y, BoxResize.MinHeight, bounds.Bottom - y);
                break;
            case "E":
                w = ClampRange(mouse.X - _resizeAnchor.X, BoxResize.MinWidth, bounds.Right - x);
                break;
            case "SW":
                w = ClampRange(_resizeAnchor.X - mouse.X, BoxResize.MinWidth, _resizeAnchor.X - bounds.Left);
                h = ClampRange(mouse.Y - _resizeAnchor.Y, BoxResize.MinHeight, bounds.Bottom - y);
                x = _resizeAnchor.X - w;
                break;
            case "W":
                w = ClampRange(_resizeAnchor.X - mouse.X, BoxResize.MinWidth, _resizeAnchor.X - bounds.Left);
                x = _resizeAnchor.X - w;
                break;
            case "NW":
                w = ClampRange(_resizeAnchor.X - mouse.X, BoxResize.MinWidth, _resizeAnchor.X - bounds.Left);
                h = ClampRange(_resizeAnchor.Y - mouse.Y, BoxResize.MinHeight, _resizeAnchor.Y - bounds.Top);
                x = _resizeAnchor.X - w;
                y = _resizeAnchor.Y - h;
                break;
            case "N":
                h = ClampRange(_resizeAnchor.Y - mouse.Y, BoxResize.MinHeight, _resizeAnchor.Y - bounds.Top);
                y = _resizeAnchor.Y - h;
                break;
            case "NE":
                w = ClampRange(mouse.X - _resizeAnchor.X, BoxResize.MinWidth, bounds.Right - x);
                h = ClampRange(_resizeAnchor.Y - mouse.Y, BoxResize.MinHeight, _resizeAnchor.Y - bounds.Top);
                y = _resizeAnchor.Y - h;
                break;
        }

        Vm.X = x;
        Vm.Y = y;
        Vm.Width = w;
        Vm.Height = h;
    }

    /// <summary>把值夹在 [min,max] 内；max < min 时取 max（极小屏幕兜底）。</summary>
    private static double ClampRange(double value, double min, double max)
    {
        if (max < min) max = min;
        return Math.Clamp(value, min, max);
    }

    private void OnResizeCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!_isResizing || Vm is null) return;
        _isResizing = false;
        Services.LogService.Info("BoxResize.Done",
            $"dir={_resizeDir} final={Vm.Width:0}x{Vm.Height:0} at ({Vm.X:0},{Vm.Y:0})");
        MainVm.ScheduleSave();
    }

    private (double X, double Y) GetDpiScale()
    {
        var source = PresentationSource.FromVisual(this);
        var transform = source?.CompositionTarget?.TransformToDevice ?? System.Windows.Media.Matrix.Identity;
        var x = transform.M11 == 0 ? 1 : transform.M11;
        var y = transform.M22 == 0 ? 1 : transform.M22;
        return (x, y);
    }

    // ---- 拖放导入(归当前标签 / Items)----
    private void OnDragOver(object sender, DragEventArgs e)
    {
        // 盒内条目拖拽:落回本盒子 = 换个位置(重排);别的盒子仍不接受(免得误吞)
        if (e.Data.GetDataPresent(ItemDragDrop.DragSourceItemFormat))
        {
            e.Effects = CanReorderFrom(e.Data) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
            return;
        }

        // 从别的盒子拖来的标签:允许落下(落到本盒子任意位置 = 并入本盒子成为新标签)
        if (e.Data.GetDataPresent(typeof(BoxTab)))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (Vm is null || MainVm is null) return;
        if (e.Data.GetDataPresent(ItemDragDrop.DragSourceItemFormat))
        {
            e.Effects = TryReorderItem(e) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
            return;
        }

        // 从别的盒子拖来的标签 → 并入本盒子(落在条目区/标签行都算)
        if (e.Data.GetDataPresent(typeof(BoxTab)) && e.Data.GetData(typeof(BoxTab)) is BoxTab foreign)
        {
            TryAcceptForeignTab(foreign);
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                // Outlook/浏览器等虚拟拖放源 GetData(FileDrop) 可能不是 string[]，
                // 裸强转会在 UI 线程直接崩。先 is 校验 + 限批 500（防池饥饿/数千Timer）。
                if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
                {
                    Services.LogService.Warn("Drop.Ignored",
                        $"FileDrop 格式不符，formats=[{string.Join(",", e.Data.GetFormats())}]");
                    e.Handled = true;
                    return;
                }
                const int MaxBatch = 500;
                if (paths.Length > MaxBatch)
                {
                    Services.LogService.Warn("Drop.Truncated", $"批量 {paths.Length} > {MaxBatch}，只导入前 {MaxBatch}");
                    InputDialog.Inform($"一次最多导入 {MaxBatch} 个文件，本次只导入前 {MaxBatch} 个（共 {paths.Length} 个）。");
                    paths = paths.Take(MaxBatch).ToArray();
                }
                foreach (var p in paths)
                {
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    MainVm.AddItemToBox(Vm, p);
                }
            }
            else if (e.Data.GetDataPresent(DataFormats.Text))
            {
                // 大文本粘贴（10MB 日志）会在 UI 线程复制大字符串，先限长。
                var raw = e.Data.GetData(DataFormats.Text) as string;
                var txt = raw is not null && raw.Length > 2048 ? raw[..2048] : raw;
                if (!string.IsNullOrWhiteSpace(txt)) MainVm.AddItemToBox(Vm, txt.Trim());
            }
        }
        catch (Exception ex)
        {
            App.LogError(ex, "BoxControl.OnDrop");
        }
        e.Handled = true;
    }

    /// <summary>盒子右键菜单的两种形态:齿轮按钮打开的是完整菜单,其余(空白处/标题栏右键)是精简版。</summary>
    private enum BoxMenuMode
    {
        Compact,
        Full
    }

    /// <summary>XAML 里给"结构性操作"打的标记(合并/拆分/删除盒子/清空盒子)。</summary>
    private const string StructuralTag = "structural";

    /// <summary>按打开方式来显隐结构性操作。
    /// **必须在菜单打开之前就调用** —— 不能等 ContextMenuOpening:齿轮按钮是程序化打开,
    /// 那个事件不一定触发,结果就是"上次右键把这几项折叠过,齿轮里就再也看不到了"(实测踩到)。</summary>
    private void ApplyMenuMode(BoxMenuMode mode)
    {
        if (ContextMenu is null) return;

        var showStructural = mode == BoxMenuMode.Full;
        foreach (var item in ContextMenu.Items.OfType<MenuItem>())
            if (Equals(item.Tag, StructuralTag))
                item.Visibility = showStructural ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>右键按下时:把菜单锚点摆成"跟鼠标",并按精简版显隐。</summary>
    private void OnRightButtonUpForMenu(object sender, MouseButtonEventArgs e)
    {
        ApplyMenuMode(BoxMenuMode.Compact);
        if (ContextMenu is null) return;

        ContextMenu.Placement = PlacementMode.MousePoint;
        ContextMenu.HorizontalOffset = 0;
        ContextMenu.VerticalOffset = 0;
    }

    /// <summary>盒子背景的基准色(XAML 里 Card 的 #99000000)。滑条按比例缩它的 A 通道 ——
    /// 只改背景,图标和文字保持清晰。</summary>
    private static readonly Color BaseCardColor = Color.FromArgb(0x99, 0, 0, 0);

    private bool _opacitySubscribed;

    /// <summary>按全局设置刷新盒子背景不透明度。</summary>
    private void ApplyBoxOpacity()
    {
        try
        {
            var factor = Math.Clamp(MainVm.BoxOpacity, 0.1, 1.0);
            var alpha = (byte)Math.Round(BaseCardColor.A * factor);
            var brush = new SolidColorBrush(Color.FromArgb(alpha, BaseCardColor.R, BaseCardColor.G, BaseCardColor.B));
            brush.Freeze();
            Card.Background = brush;
        }
        catch
        {
            // 单测里没有 DI 容器 —— 背景保持原样即可
        }
    }

    private void OnMainVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.BoxOpacity)) ApplyBoxOpacity();
    }

    /// <summary>菜单要开了:按"从哪儿打开"决定要不要露出结构性操作,然后复位成默认的精简版。</summary>
    /// <summary>这次拖拽的是不是"本盒子当前标签里的条目"(是 → 允许落下并重排)。</summary>
    private bool CanReorderFrom(IDataObject data)
    {
        if (Vm is null || MainVm is null) return false;
        return ResolveDraggedItems(data).Count > 0;
    }

    /// <summary>把拖拽载荷里的 id 解析成真实条目(只保留还存在的)。多选拖拽会是一组。</summary>
    private List<BoxItem> ResolveDraggedItems(IDataObject? data)
    {
        var result = new List<BoxItem>();
        if (MainVm is null) return result;

        foreach (var id in ItemDragDrop.ReadDraggedIds(data))
        {
            var item = MainVm.FindItemById(id);
            if (item is not null) result.Add(item);
        }

        return result;
    }

    /// <summary>把拖动的条目挪到落点对应位置:同一盒子内 = 整组重排;别的盒子 = 整组挪进来。
    /// 返回是否真的动了。</summary>
    private bool TryReorderItem(DragEventArgs e)
    {
        if (Vm is null || MainVm is null) return false;

        var items = ResolveDraggedItems(e.Data);
        if (items.Count == 0) return false;

        var index = DropInsertIndex(e, Vm.DisplayItems);
        var fromThisBox = items.Where(i => Vm.DisplayItems.Contains(i)).ToList();
        var fromOtherBoxes = items.Where(i => !Vm.DisplayItems.Contains(i)).ToList();

        var moved = false;
        if (fromThisBox.Count > 0) moved |= MainVm.MoveItemsToIndex(fromThisBox, index);
        if (fromOtherBoxes.Count > 0) moved |= MainVm.MoveItemsToBox(fromOtherBoxes, Vm, index) > 0;

        Services.LogService.Info("Box.Reorder.Try",
            $"insert={index} sameBox={fromThisBox.Count} otherBox={fromOtherBoxes.Count} moved={moved}");
        return moved;
    }

    /// <summary>落点对应的插入位置:压在哪块磁贴上就插到它前面/后面;落在空白处排到最后。
    /// 大图标/平铺是横向排的,所以看落点在磁贴的左半还是右半。</summary>
    private int DropInsertIndex(DragEventArgs e, System.Collections.Generic.IList<BoxItem> items)
    {
        var point = e.GetPosition(this);
        foreach (var tile in FindVisualChildren<ItemTile>(this))
        {
            if (tile.Item is null) continue;

            var topLeft = tile.TranslatePoint(new Point(0, 0), this);
            var bounds = new Rect(topLeft.X, topLeft.Y, tile.ActualWidth, tile.ActualHeight);
            if (!bounds.Contains(point)) continue;

            var index = items.IndexOf(tile.Item);
            if (index < 0) continue;
            return point.X > bounds.Left + bounds.Width / 2 ? index + 1 : index;
        }

        return items.Count;
    }

    /// <summary>点击条目列表的空白区域(非磁贴)→ 清除所有选中(资源管理器行为)。
    /// Preview 隧道事件:点磁贴时也会触发,需排除命中 ItemTile 的情况,否则 Ctrl 多选会被先清空。</summary>
    private void OnItemsAreaMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 命中测试:若点中的是 ItemTile(或其子元素),不处理,交给磁贴自己的选中逻辑
        if (e.OriginalSource is DependencyObject d && FindAncestor<ItemTile>(d) is not null) return;

        // 空白处按下 = 开始拉框(没拖动就等价于"点空白取消选中")
        StartMarquee(e.GetPosition(ItemsViewport), Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
    }

    /// <summary>开始拉框。<paramref name="additive"/>=按住 Ctrl → 保留已有选中(加选)。</summary>
    private void StartMarquee(Point origin, bool additive)
    {
        if (_marqueeDragging || Vm is null || MainVm is null) return;

        _marqueeOrigin = origin;
        _marqueeDragging = true;
        _selectionBeforeMarquee.Clear();
        Services.LogService.Info("Box.Marquee.Start", $"additive={additive}");

        if (additive)
        {
            foreach (var item in Vm.DisplayItems)
                if (item.IsSelected) _selectionBeforeMarquee.Add(item.Id);
        }
        else
        {
            MainVm.ClearSelection();
        }

        CaptureMouse();
    }

    private void OnMarqueeMouseMove(object sender, MouseEventArgs e)
    {
        if (!_marqueeDragging) return;

        var rect = new Rect(_marqueeOrigin, e.GetPosition(ItemsViewport));
        DrawMarquee(rect);

        foreach (var tile in FindVisualChildren<ItemTile>(ItemsViewport))
        {
            if (tile.Item is null) continue;

            var topLeft = tile.TranslatePoint(new Point(0, 0), ItemsViewport);
            var bounds = new Rect(topLeft.X, topLeft.Y, tile.ActualWidth, tile.ActualHeight);
            var hit = bounds.IntersectsWith(rect);
            tile.Item.IsSelected = hit || _selectionBeforeMarquee.Contains(tile.Item.Id);
        }
    }

    private void OnMarqueeMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_marqueeDragging) return;
        _marqueeDragging = false;

        RemoveMarquee();
        if (IsMouseCaptured) ReleaseMouseCapture();
        MainVm?.ScheduleSave();
        Services.LogService.Info("Box.Marquee.End",
            $"选中 {Vm?.DisplayItems.Count(i => i.IsSelected) ?? 0} 项");
    }

    private void DrawMarquee(Rect rect)
    {
        if (_marqueeBox is null)
        {
            var layer = EnsureMarqueeLayer();
            if (layer is null) return;

            _marqueeBox = new Border
            {
                Background = MarqueeFill,
                BorderBrush = MarqueeStroke,
                BorderThickness = new Thickness(1),
                IsHitTestVisible = false
            };
            layer.Children.Add(_marqueeBox);
        }

        Canvas.SetLeft(_marqueeBox, rect.Left);
        Canvas.SetTop(_marqueeBox, rect.Top);
        _marqueeBox.Width = Math.Max(0, rect.Width);
        _marqueeBox.Height = Math.Max(0, rect.Height);
    }

    private Canvas? EnsureMarqueeLayer()
    {
        if (_marqueeLayer is not null) return _marqueeLayer;
        if (ItemsViewport is null) return null;

        _marqueeLayer = new Canvas { IsHitTestVisible = false };
        ItemsViewport.Children.Add(_marqueeLayer);
        return _marqueeLayer;
    }

    private void RemoveMarquee()
    {
        if (_marqueeBox is null) return;
        _marqueeLayer?.Children.Remove(_marqueeBox);
        _marqueeBox = null;
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t) return t;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
                yield return typed;
            foreach (var nested in FindVisualChildren<T>(child))
                yield return nested;
        }
    }

    private void OnRename(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var newName = InputDialog.Prompt(Localizer["dialog.renameBox.title"], Localizer["dialog.renameBox.prompt"], Vm.Name);
        if (string.IsNullOrWhiteSpace(newName)) return;
        Vm.Rename(newName.Trim());   // 走 Rename:改名 + 脱离程序生成身份(否则界面仍显示原来的翻译名)
        MainVm.ScheduleSave();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (Vm is null || Vm.TotalItemCount == 0) return;
        if (InputDialog.Confirm(Localizer["dialog.clearBox.confirm"]))
        {
            Vm.ClearAll();
            MainVm.ScheduleSave();
        }
    }

    /// <summary>批量删除当前所有选中的磁贴(跨盒子)。由右键选中项冒泡到此 ContextMenu 触发。</summary>
    private void OnRemoveSelected(object sender, RoutedEventArgs e)
    {
        var selected = MainVm.GetSelectedItems();
        if (selected.Count == 0) return;
        if (!InputDialog.Confirm(string.Format(Localizer["dialog.removeSelected.confirm"], selected.Count))) return;
        MainVm.RemoveSelected();
    }

    private void OnRefreshItems(object sender, RoutedEventArgs e)
    {
        var removed = MainVm.PruneMissingItems();
        // 成功类提示 → 2 秒自动消失的提示条(用户指定这条不透明度 90%)
        ToastWindow.Show(string.Format(Localizer["dialog.refreshItems.done"], removed), opacity: 0.9);
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var box = Vm;

        // 非空盒子:确认后先清空盒子里的引用再删盒子。清空只移除"盒子里的显示",
        // 文件本体从不删除(整理本就是只引用),所以不会因为删盒子丢文件。
        var message = box.TotalItemCount > 0
            ? string.Format(Localizer["dialog.deleteBox.clearThenDelete"], box.Header, box.TotalItemCount)
            : string.Format(Localizer["dialog.deleteBox.empty"], box.Header);
        if (!InputDialog.Confirm(message)) return;

        if (box.TotalItemCount > 0) box.ClearAll();
        MainVm.RemoveBoxCommand.Execute(box);
    }

    // ---- 标签操作 ----
    private void OnAddTab(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        Vm.AddTab("");   // 空=用当前语言的默认标签名(BoxViewModel.AddTab 内部本地化)
        MainVm.ScheduleSave();
    }

    private void OnRenameTab(object sender, RoutedEventArgs e)
    {
        if (Vm?.SelectedTab is not { } tab) return;
        var name = InputDialog.Prompt(Localizer["box.menu.renameTab"], Localizer["dialog.renameBox.prompt"], tab.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        tab.Rename(name.Trim());     // 走 Rename:改名 + 清掉分类 Key(否则分类标签改名后仍是原翻译名)
        MainVm.ScheduleSave();
    }

    private void OnDeleteTab(object sender, RoutedEventArgs e)
    {
        if (Vm is null || Vm.SelectedTab is null) return;
        var tab = Vm.SelectedTab;
        if (tab.Items.Count > 0)
        {
            InputDialog.Inform(string.Format(Localizer["dialog.deleteTab.nonEmpty"], tab.Header, tab.Items.Count));
            return;
        }
        if (Vm.Tabs.Count <= 1)
        {
            InputDialog.Inform(Localizer["dialog.deleteTab.last"]);
            return;
        }
        Vm.Tabs.Remove(tab);   // BoxViewModel.OnTabsChanged 会自动重选
        MainVm.ScheduleSave();
    }

    private void OnConvertToTabbed(object sender, RoutedEventArgs e)
        => MainVm.ConvertToTabbedCommand.Execute(Vm);

    private void OnMergeIn(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var candidates = MainVm.Boxes.Where(b => b.Id != Vm.Id).ToList();
        if (candidates.Count == 0)
        {
            InputDialog.Inform(Localizer["dialog.merge.none"]);
            return;
        }
        var names = candidates
            .Select(b => $"{b.Header}{(b.IsTabbed ? "  (标签盒)" : "")}  ·  {b.TotalItemCount} 项")
            .ToList();
        int idx = InputDialog.SelectIndex(Localizer["dialog.merge.title"], Localizer["dialog.merge.prompt"], names);
        if (idx < 0 || idx >= candidates.Count) return;
        MainVm.MergeIn(Vm, candidates[idx]);
    }

    private void OnSplit(object sender, RoutedEventArgs e)
        => MainVm.SplitBoxCommand.Execute(Vm);

    // ---- 设置按钮:复用盒子右键菜单,保证两处选项始终完全一致 ----
    // ---- 设置按钮:复用盒子右键菜单(菜单本身只有一份,靠"从哪儿打开"决定要不要显示结构性操作)----
    private void OnOpenContextMenu(object sender, RoutedEventArgs e)
    {
        if (ContextMenu is null) return;
        // 齿轮按钮 = 设置入口 → 完整菜单(含合并/拆分/删除盒子/清空盒子)
        ApplyMenuMode(BoxMenuMode.Full);
        ContextMenu.PlacementTarget = sender as UIElement ?? this;
        ContextMenu.Placement = PlacementMode.Bottom;
        ContextMenu.HorizontalOffset = 0;
        ContextMenu.IsOpen = true;
        e.Handled = true;
    }

    // ---- 视图模式切换(全局:所有盒子/标签共享)----
    private void OnViewLarge(object sender, RoutedEventArgs e) => SetView(ViewMode.Large);
    private void OnViewMedium(object sender, RoutedEventArgs e) => SetView(ViewMode.Medium);
    private void OnViewSmall(object sender, RoutedEventArgs e) => SetView(ViewMode.Small);
    private void OnViewList(object sender, RoutedEventArgs e) => SetView(ViewMode.List);
    private void OnViewDetail(object sender, RoutedEventArgs e) => SetView(ViewMode.Detail);
    private void OnViewTile(object sender, RoutedEventArgs e) => SetView(ViewMode.Tile);

    private void SetView(ViewMode mode) => MainVm.SetGlobalViewMode(mode);

    // ---- 标签拖拽排序(单击=切换,按住左键拖动=换位)----
    private Point _tabDragOrigin;
    private object? _tabDragData;

    private void OnTabListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _tabDragOrigin = e.GetPosition(null);
        _tabDragData = Ancestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext;
    }

    private void OnTabListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _tabDragData is null) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _tabDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _tabDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var data = _tabDragData;
        _tabDragData = null;
        if (data is not BoxTab tab || Vm is null) return;

        // 两种落点:
        //   盒内(<同盒子的标签行>) = 调整标签顺序,见 OnTabListDrop
        //   盒外(桌面)             = 该标签独立成一个新盒子,见下面的 SplitTabToNewBox
        // 桌面接收层与"拖条目出盒"共用同一套:整屏透明子窗口 + 在盒子位置挖洞,
        // 所以落在盒子上时事件仍由盒子自己处理,不会被这层吃掉。
        var payload = new DataObject();
        payload.SetData(typeof(BoxTab), tab);
        payload.SetData(ItemDragDrop.DragSourceTabFormat, tab.Id);

        var boxBounds = DesktopDropOverlay.OccupiedBoxRects(MainVm.Boxes);

        using var desktopDrop = DesktopDropOverlay.TryCreate(boxBounds);
        try
        {
            DragDrop.DoDragDrop(TabList, payload, DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            App.LogError(ex, "BoxControl.TabDragOut");
        }

        if (desktopDrop?.DroppedOnDesktop == true && desktopDrop.DropScreenPosition is { } drop
            && !MainVm.SplitTabToNewBox(Vm, tab, drop.X, drop.Y))
        {
            Services.LogService.Info("TabDragOut.Ignored", "只剩一个标签,不拆成独立盒子");
        }
    }

    private void OnTabListDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(BoxTab)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnTabListDrop(object sender, DragEventArgs e)
    {
        if (Vm is null || !e.Data.GetDataPresent(typeof(BoxTab))) return;
        var dragged = e.Data.GetData(typeof(BoxTab)) as BoxTab;
        if (dragged is null) return;

        // 从别的盒子拖过来的标签 → 并入本盒子成为新标签
        if (TryAcceptForeignTab(dragged))
        {
            e.Handled = true;
            return;
        }

        var target = Ancestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as BoxTab;
        if (target is null || ReferenceEquals(dragged, target)) return;
        int oldIdx = Vm.Tabs.IndexOf(dragged);
        int newIdx = Vm.Tabs.IndexOf(target);
        if (oldIdx >= 0 && newIdx >= 0) Vm.Tabs.Move(oldIdx, newIdx);
        MainVm.ScheduleSave();
    }

    /// <summary>把别处拖来的标签并入本盒子(拖标签到别的盒子上)。同盒子内的拖动不算。</summary>
    private bool TryAcceptForeignTab(BoxTab dragged)
    {
        if (Vm is null || Vm.Tabs.Contains(dragged)) return false;
        var source = MainVm.Boxes.FirstOrDefault(b => b.Tabs.Contains(dragged));
        return source is not null && MainVm.MoveTabToBox(source, Vm, dragged);
    }

    private void OnTabListPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var scrollViewer = FindVisualChildren<ScrollViewer>(TabList).FirstOrDefault();
        if (scrollViewer is null) return;
        if (e.Delta < 0)
            scrollViewer.LineRight();
        else
            scrollViewer.LineLeft();
        e.Handled = true;
    }

    private void OnTabListPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = Ancestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is BoxTab tab && Vm is not null)
        {
            Vm.SelectedTab = tab;
        }
    }

    /// <summary>沿可视树向上找指定类型的祖先。</summary>
    private static T? Ancestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null && d is not T)
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        return d as T;
    }
}
