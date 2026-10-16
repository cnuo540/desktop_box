using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace DesktopBox.Controls;

public sealed class DesktopDropOverlay : IDisposable
{
    private const double BoxExclusionPadding = 8;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly HwndSource _source;
    private bool _disposed;

    private DesktopDropOverlay(HwndSource source, Rect bounds)
    {
        _source = source;
        var scale = GetDpiScale();
        var surface = new Border
        {
            Width = Math.Max(1, bounds.Width / scale.X),
            Height = Math.Max(1, bounds.Height / scale.Y),
            // 几乎全透明:这块面板只是"接住拖放"用的,不该看得见。
            // 但它必须真的透明 —— 见 TryCreate 里 UsesPerPixelOpacity 的说明。
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
            AllowDrop = true
        };
        surface.DragEnter += OnDragOverDesktop;
        surface.DragOver += OnDragOverDesktop;
        surface.Drop += OnDropOnDesktop;
        _source.RootVisual = surface;
    }

    public bool DroppedOnDesktop { get; private set; }

    /// <summary>拖放落在桌面时的光标屏幕坐标(物理像素);未落在桌面时为 null。</summary>
    public (double X, double Y)? DropScreenPosition { get; private set; }

    /// <summary>盒子"当前实际占据"的屏幕矩形。卷起的盒子只占标题栏那么高 —— 挖洞必须按这个来:
    /// 用模型里存的完整高度挖洞的话,卷起后腾出来的那片空白仍然被当成盒子的一部分,拖到那里就漏给
    /// explorer,它会当成"把文件复制到它自己所在的同一个文件夹",弹「源文件名和目标文件名相同」。</summary>
    public static List<Rect> OccupiedBoxRects(IEnumerable<ViewModels.BoxViewModel> boxes)
        => boxes.Where(b => b.Width > 0 && b.RenderHeight > 0)
                .Select(b => new Rect(b.X, b.Y, b.Width, b.RenderHeight))
                .ToList();

    public static DesktopDropOverlay? TryCreate(IEnumerable<Rect> excludedBounds)
    {
        // 与主窗口同策略（Native.User32.ResolveDesktopHost）：DefView 优先保证可见。
        // 覆盖层现在是顶层窗口、不再挂到宿主下,但这次探测仍然有用:宿主还没就绪(explorer 正在
        // 重启)时说明桌面本身不可用,此时建覆盖层只会添乱。
        var host = Native.User32.ResolveDesktopHost();
        if (host == IntPtr.Zero || !Native.User32.IsWindow(host))
            return null;

        var bounds = GetVirtualDesktopBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return null;

        // 必须是<b>顶层</b>分层窗口,不能挂成桌面宿主的子窗口。
        // 实测:子窗口 + UsesPerPixelOpacity=true 会被 WPF 剥掉 WS_EX_LAYERED
        // (建出来 ex=0x00000080,layered=False),于是这块近乎全透明的面板变成<b>不透明黑</b> ——
        // 拖动桌面文件时整个屏幕变黑,松手(覆盖层销毁)才恢复。
        // 顶层窗口才能真正逐像素透明;而它只在拖拽期间存在,置顶不会长期影响任何东西。
        var parameters = new HwndSourceParameters("DesktopBox.DropOverlay")
        {
            WindowStyle = WS_POPUP | WS_VISIBLE,
            ExtendedWindowStyle = WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE,
            PositionX = (int)Math.Round(bounds.Left),
            PositionY = (int)Math.Round(bounds.Top),
            Width = Math.Max(1, (int)Math.Ceiling(bounds.Width)),
            Height = Math.Max(1, (int)Math.Ceiling(bounds.Height)),
            UsesPerPixelOpacity = true
        };

        var source = new HwndSource(parameters);
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        // 在盒子位置挖洞:盒子是桌面宿主的子窗口,本来就绘制在顶层窗口之上,但托盘/弹窗等
        // 置顶窗口会盖住盒子 —— 挖洞保证拖到盒子上的落点仍归盒子,不会被这层吃掉。
        ApplyExclusionRegion(source.Handle, bounds, excludedBounds);
        Native.User32.SetWindowPos(
            source.Handle,
            Native.User32.HWND_TOPMOST,
            parameters.PositionX,
            parameters.PositionY,
            parameters.Width,
            parameters.Height,
            Native.User32.SWP_SHOWWINDOW | Native.User32.SWP_NOACTIVATE);

        // 诊断(临时):确认分层与置顶真的生效
        try
        {
            var ex = Native.User32.GetWindowLong(source.Handle, Native.User32.GWL_EXSTYLE);
            Services.LogService.Info("DropOverlay.Create",
                $"hwnd=0x{source.Handle:X} ex=0x{ex:X8} layered={(ex & 0x00080000) != 0} " +
                $"parent=0x{Native.User32.GetParent(source.Handle):X} excluded={excludedBounds.Count()} " +
                $"desktop={bounds.Width:0}x{bounds.Height:0}");
        }
        catch { }

        return new DesktopDropOverlay(source, bounds);
    }

    internal static IReadOnlyList<Int32Rect> BuildExclusionRects(
        Rect overlayBounds,
        IEnumerable<Rect> excludedBounds,
        double padding = BoxExclusionPadding)
    {
        var result = new List<Int32Rect>();
        foreach (var excluded in excludedBounds)
        {
            if (excluded.Width <= 0 || excluded.Height <= 0)
                continue;

            var rect = excluded;
            rect.Inflate(padding, padding);
            rect.Intersect(overlayBounds);
            if (rect.IsEmpty)
                continue;

            var left = (int)Math.Floor(rect.Left - overlayBounds.Left);
            var top = (int)Math.Floor(rect.Top - overlayBounds.Top);
            var right = (int)Math.Ceiling(rect.Right - overlayBounds.Left);
            var bottom = (int)Math.Ceiling(rect.Bottom - overlayBounds.Top);
            if (right > left && bottom > top)
                result.Add(new Int32Rect(left, top, right - left, bottom - top));
        }

        return result;
    }

    private static void ApplyExclusionRegion(IntPtr hwnd, Rect overlayBounds, IEnumerable<Rect> excludedBounds)
    {
        var width = Math.Max(1, (int)Math.Ceiling(overlayBounds.Width));
        var height = Math.Max(1, (int)Math.Ceiling(overlayBounds.Height));
        var region = Native.Gdi32.CreateRectRgn(0, 0, width, height);
        if (region == IntPtr.Zero)
            return;

        var transferredToWindow = false;
        try
        {
            foreach (var excluded in BuildExclusionRects(overlayBounds, excludedBounds))
            {
                var hole = Native.Gdi32.CreateRectRgn(
                    excluded.X,
                    excluded.Y,
                    excluded.X + excluded.Width,
                    excluded.Y + excluded.Height);
                if (hole == IntPtr.Zero)
                    continue;

                try
                {
                    Native.Gdi32.CombineRgn(region, region, hole, Native.Gdi32.RGN_DIFF);
                }
                finally
                {
                    Native.Gdi32.DeleteObject(hole);
                }
            }

            transferredToWindow = Native.User32.SetWindowRgn(hwnd, region, true) != 0;
        }
        finally
        {
            if (!transferredToWindow)
                Native.Gdi32.DeleteObject(region);
        }
    }

    internal static Rect GetVirtualDesktopBounds()
    {
        try
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            if (screens.Length > 0)
            {
                var left = screens.Min(s => s.Bounds.Left);
                var top = screens.Min(s => s.Bounds.Top);
                var right = screens.Max(s => s.Bounds.Right);
                var bottom = screens.Max(s => s.Bounds.Bottom);
                return new Rect(left, top, right - left, bottom - top);
            }
        }
        catch
        {
            // Fall back to WPF virtual-screen metrics below.
        }

        return new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
    }

    private (double X, double Y) GetDpiScale()
    {
        var transform = _source.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var x = transform.M11 == 0 ? 1 : transform.M11;
        var y = transform.M22 == 0 ? 1 : transform.M22;
        return (x, y);
    }

    private void OnDragOverDesktop(object sender, DragEventArgs e)
    {
        e.Effects = EffectsFor(e.Data);
        e.Handled = true;
    }

    private void OnDropOnDesktop(object sender, DragEventArgs e)
    {
        var effects = EffectsFor(e.Data);
        if (effects != DragDropEffects.None)
        {
            DroppedOnDesktop = true;
            // 落点用光标屏幕坐标(物理像素,与盒子模型坐标同一坐标系):拖标签出盒时用它决定新盒子位置。
            try
            {
                var cursor = System.Windows.Forms.Cursor.Position;
                DropScreenPosition = (cursor.X, cursor.Y);
            }
            catch
            {
                DropScreenPosition = null;
            }

            e.Effects = effects;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    /// <summary>本覆盖层接收的拖放数据:盒子里的条目(拖出到桌面=从盒子移除)与标签(拖出=独立成新盒子)。</summary>
    internal static bool IsSupportedDrag(IDataObject? data) => EffectsFor(data) != DragDropEffects.None;

    /// <summary>按拖拽内容给出落下效果。<b>必须与拖拽源允许的效果有交集</b>,否则系统直接显示"禁止"
    /// 光标并拒绝落下——拖标签出盒曾经就栽在这里:源只允许 Move,而这里回了 Copy,
    /// 结果鼠标一路显示禁止图标、松手什么也不发生。
    /// 条目拖出(ItemTile)的源只允许 Copy,标签拖出(BoxControl)的源只允许 Move。</summary>
    internal static DragDropEffects EffectsFor(IDataObject? data)
    {
        if (data is null) return DragDropEffects.None;
        if (data.GetDataPresent(ItemDragDrop.DragSourceItemFormat)) return DragDropEffects.Copy;
        if (data.GetDataPresent(ItemDragDrop.DragSourceTabFormat)) return DragDropEffects.Move;
        // 桌面层的小图标是带着 FileDrop 拖的:拖到桌面空白处 = 留在原地(别显示"禁止"光标)
        if (data.GetDataPresent(DataFormats.FileDrop)) return DragDropEffects.Copy | DragDropEffects.Move;
        return DragDropEffects.None;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            if (Native.User32.IsWindow(_source.Handle))
            {
                Native.User32.ShowWindow(_source.Handle, 0);
                Native.User32.SetParent(_source.Handle, IntPtr.Zero);
            }
        }
        catch { }
        _source.RootVisual = null;
        _source.Dispose();
    }
}
