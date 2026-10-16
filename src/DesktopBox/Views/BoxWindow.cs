using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using DesktopBox.Controls;
using DesktopBox.ViewModels;

namespace DesktopBox.Views;

/// <summary>
/// 盒子窗口 —— <b>顶层 + 非置顶 + 逐像素 alpha</b>。
///
/// 这个组合是"真透明 + 不压住普通窗口"的唯一解,而它成立的前提是把两个特性拆开看
/// (实测确认):<b>逐像素 alpha 与 WS_EX_TOPMOST 是彼此独立的</b> ——
/// 非置顶的顶层窗口同样走 UpdateLayeredWindow 路径,所以能真透明;
/// 而它不在置顶带里,普通窗口照样能盖住它。
///
/// 为什么不能用子窗口(桌面宿主的子窗口):Windows 的子窗口与父窗口之间<b>不做 alpha 混合</b>,
/// 半透明底会被合成成不透明黑。且子窗口上设 UsesPerPixelOpacity=true 会被 WPF
/// <b>剥掉 WS_EX_LAYERED</b>(实测建出来 ex=0x00000080),退化成更糟的普通不透明窗口。
/// 其余机制也全部实测无效:DwmExtendFrameIntoClientArea(hr=0x80070057)、LWA_COLORKEY(err=87)。
///
/// 代价(已接受):盒子在普通窗口档里,不是子窗口那种"永远压在普通窗口之上"。
/// 谁在上面取决于 z 序 —— 点盒子它会抬起来,点别的窗口那个窗口会盖住它。
/// </summary>
public sealed class BoxWindow : IDisposable
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly HwndSource _source;
    private readonly IntPtr _handle;
    private readonly BoxControl _content;
    private bool _disposed;

    public BoxWindow(BoxViewModel box, MainViewModel mainVm, IntPtr parent)
    {
        MainVm = mainVm;
        Box = box;

        // 顶层窗口:模型坐标就是屏幕坐标,不需要再换算父窗口客户区。
        // 注意这里<b>不加 WS_EX_TOPMOST</b> —— 置顶会让盒子压住普通窗口(已被否决)。
        var parameters = new HwndSourceParameters(box.Header)
        {
            WindowStyle = WS_POPUP | WS_VISIBLE,
            ExtendedWindowStyle = WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE,
            PositionX = (int)Math.Round(box.X),
            PositionY = (int)Math.Round(box.Y),
            Width = Math.Max(1, (int)Math.Round(box.Width)),
            Height = Math.Max(1, (int)Math.Round(box.RenderHeight)),
            UsesPerPixelOpacity = true
        };

        _source = new HwndSource(parameters);
        _handle = _source.Handle;
        // 显式落在普通(非置顶)档:HwndSource 建出来的窗口默认就在这一档,这里再钉一次,
        // 免得将来有人顺手加了 WS_EX_TOPMOST 而没人发现盒子开始压住普通窗口。
        Native.User32.SetWindowPos(_handle, Native.User32.HWND_NOTOPMOST, 0, 0, 0, 0,
            Native.User32.SWP_NOMOVE | Native.User32.SWP_NOSIZE | Native.User32.SWP_NOACTIVATE);
        _source.Disposed += OnSourceDisposed;
        _source.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
        _content = new BoxControl
        {
            DataContext = box
        };
        _source.RootVisual = _content;
        MoveResize(Native.User32.HWND_TOP);
        box.PropertyChanged += OnBoxPropertyChanged;
    }

    public MainViewModel MainVm { get; }

    public BoxViewModel Box { get; }

    public IntPtr Handle => _disposed || _source.IsDisposed ? IntPtr.Zero : _handle;
    public bool IsDisposed => _disposed;
    public bool IsHandleAlive => !_disposed && !_source.IsDisposed && Native.User32.IsWindow(_handle);
    private DateTime _lastMoveResizeUtc;
    private Timer? _resizeFlushTimer;
    private IntPtr _pendingInsertAfter;
    private uint _pendingExtraFlags;
    private int _lastX = int.MinValue, _lastY = int.MinValue, _lastW, _lastH;
    private double _lastContentW, _lastContentH;

    /// <summary>诊断快照：HWND/可见性/实际矩形/父窗口/模型坐标，用于定位"盒子不可见"。</summary>
    public string Describe()
    {
        try
        {
            var alive = IsHandleAlive;
            var visible = alive && Native.User32.IsWindowVisible(_handle);
            var rect = Native.User32.GetWindowRect(_handle, out var r)
                ? $"{r.Left},{r.Top},{r.Right - r.Left}x{r.Bottom - r.Top}" : "n/a";
            var parent = alive ? Native.User32.GetParent(_handle) : IntPtr.Zero;
            return $"hwnd=0x{_handle:X} alive={alive} visible={visible} rect=[{rect}] " +
                   $"parent=0x{parent:X} model=({Box.X},{Box.Y},{Box.Width}x{Box.Height})";
        }
        catch { return "describe-failed"; }
    }

    /// <summary>
    /// 把盒子摆回模型记录的位置并确保可见。
    ///
    /// 盒子现在是<b>顶层</b>窗口,没有父窗口可挂 —— 形参 <paramref name="above"/> 是"盒子应排在谁上面"
    /// 的参照窗口(桌面层),保留它是为了维持 z 序:盒子要压在桌面磁贴之上、普通窗口之下。
    /// 模型坐标就是屏幕坐标,不再做父窗口客户区换算。
    /// </summary>
    public void EnsureVisibleOnDesktopHost(IntPtr above)
    {
        if (_disposed || _source.IsDisposed)
            return;
        if (!Native.User32.IsWindow(_handle))
            return;

        try
        {
            // 有参照窗口就排在它上面(盒子压在磁贴之上);没有就退到普通档顶部。
            var insertAfter = above != IntPtr.Zero && Native.User32.IsWindow(above)
                ? above
                : Native.User32.HWND_TOP;

            Native.User32.SetWindowPos(
                _handle,
                insertAfter,
                (int)Math.Round(Box.X),
                (int)Math.Round(Box.Y),
                Math.Max(1, (int)Math.Round(Box.Width)),
                Math.Max(1, (int)Math.Round(Box.RenderHeight)),
                Native.User32.SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            Services.LogService.Error(ex, "BoxWindow.EnsureVisible");
        }
    }

    public void CloseForRemoval() => Dispose();

    private void OnBoxPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed || _source.IsDisposed)
            return;

        if (e.PropertyName is nameof(BoxViewModel.X) or nameof(BoxViewModel.Y) or nameof(BoxViewModel.Width)
            or nameof(BoxViewModel.Height) or nameof(BoxViewModel.RenderHeight))
            MoveResize(Native.User32.HWND_TOP);
        else if (e.PropertyName == nameof(BoxViewModel.Header))
            Native.User32.SetWindowText(_handle, Box.Header);
        else if (e.PropertyName == nameof(BoxViewModel.IsHoverExpanded))
            RaiseToTop();
    }

    /// <summary>把窗口提到兄弟窗口最上层。仅在悬停展开这类状态切换时调一次:
    /// 逐帧 HWND_TOP 会逼桌面图标层重排(缩放闪烁的根因),所以不能放进 MoveResize 的逐帧路径。</summary>
    private void RaiseToTop()
    {
        if (_disposed || _source.IsDisposed)
            return;
        try
        {
            MoveResizeCore(Native.User32.HWND_TOP, 0, force: true);
        }
        catch (Exception ex)
        {
            Services.LogService.Error(ex, "BoxWindow.RaiseToTop");
        }
    }

    private void MoveResize(IntPtr insertAfter)
    {
        // 逐帧路径（拖动/缩放 PropertyChanged）：不重排 Z 序（NOZORDER），只动自己的矩形。
        MoveResizeCore(insertAfter, Native.User32.SWP_NOZORDER, force: false);
    }

    private void MoveResizeCore(IntPtr insertAfter, uint extraFlags, bool force)
    {
        if (_disposed || _source.IsDisposed)
            return;
        // 节流：输入可达上百 Hz，16ms 合并到 60fps；尾帧用计时器防抖补齐
        // （松手后精确落位；持续拖动时计时器不断顺延）。
        var now = DateTime.UtcNow;
        if (!force && (now - _lastMoveResizeUtc).TotalMilliseconds < 16)
        {
            _pendingInsertAfter = insertAfter;
            _pendingExtraFlags = extraFlags;
            try
            {
                _resizeFlushTimer ??= new Timer(_ =>
                {
                    try
                    {
                        _source.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (!_disposed && !_source.IsDisposed)
                                MoveResizeCore(_pendingInsertAfter, _pendingExtraFlags, force: true);
                        }));
                    }
                    catch { }
                }, null, Timeout.Infinite, Timeout.Infinite);
                _resizeFlushTimer.Change(16, Timeout.Infinite);
            }
            catch { }
            return;
        }
        _lastMoveResizeUtc = now;

        var scale = GetDpiScale();
        var contentW = Math.Max(1, Box.Width / scale.X);
        var contentH = Math.Max(1, Box.RenderHeight / scale.Y);
        if (contentW != _lastContentW || contentH != _lastContentH)
        {
            _lastContentW = contentW;
            _lastContentH = contentH;
            _content.Width = contentW;
            _content.Height = contentH;
        }
        // 顶层窗口:模型坐标就是屏幕坐标。
        var screenX = (int)Math.Round(Box.X);
        var screenY = (int)Math.Round(Box.Y);
        int w = Math.Max(1, (int)Math.Round(Box.Width));
        int h = Math.Max(1, (int)Math.Round(Box.RenderHeight));
        // 去重：四角缩放时 X/Y/W/H 四个属性各触发一次 MoveResize，圆整后矩形常常无变化；
        // 空刷一次 SetWindowPos 就逼重绘一次，闪烁就是这么来的。
        if (screenX == _lastX && screenY == _lastY && w == _lastW && h == _lastH)
            return;
        _lastX = screenX;
        _lastY = screenY;
        _lastW = w;
        _lastH = h;
        Native.User32.SetWindowPos(
            _handle,
            insertAfter,
            screenX,
            screenY,
            w,
            h,
            Native.User32.SWP_NOACTIVATE | extraFlags);

        ApplyRoundedRegion(w, h, scale);
    }

    /// <summary>Card 的 CornerRadius(DIP)。</summary>
    private const double CornerRadiusDip = 10;

    /// <summary>
    /// 把窗口本身裁成圆角。盒子是子窗口,圆角外的像素是透明的,而子窗口的透明像素会被合成成
    /// <b>黑色</b> —— 不裁的话四个角就是黑角。用圆角区域裁掉,窗口之外照旧归 explorer。
    /// </summary>
    private void ApplyRoundedRegion(int w, int h, (double X, double Y) scale)
    {
        try
        {
            if (w <= 0 || h <= 0) return;
            // 椭圆宽高 = 直径,所以传 2×半径;半径按 DPI 缩放,和 WPF 的圆角对齐。
            var r = (int)Math.Round(CornerRadiusDip * (scale.X <= 0 ? 1 : scale.X)) * 2;
            if (r < 2) return;

            var region = Native.Gdi32.CreateRoundRectRgn(0, 0, w + 1, h + 1, r, r);
            if (region == IntPtr.Zero) return;
            if (Native.User32.SetWindowRgn(_handle, region, true) == 0)
                Native.Gdi32.DeleteObject(region);   // 失败才由我们释放;成功则系统接管
        }
        catch (Exception ex)
        {
            Services.LogService.Error(ex, "BoxWindow.ApplyRoundedRegion");
        }
    }

    private (double X, double Y) GetDpiScale()
    {
        var transform = _source.CompositionTarget?.TransformToDevice ?? System.Windows.Media.Matrix.Identity;
        var x = transform.M11 == 0 ? 1 : transform.M11;
        var y = transform.M22 == 0 ? 1 : transform.M22;
        return (x, y);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        MarkDisposed();
        try { Interlocked.Exchange(ref _resizeFlushTimer, null)?.Dispose(); } catch { }
        try
        {
            // 顶层窗口:隐藏即可,没有跨进程子链表要摘。真挂了父窗口才摘,以防将来又改回子窗口。
            if (Native.User32.IsWindow(_handle))
            {
                Native.User32.ShowWindow(_handle, 0); // SW_HIDE
                if (Native.User32.GetParent(_handle) != IntPtr.Zero)
                    Native.User32.SetParent(_handle, IntPtr.Zero);
            }
        }
        catch { }
        _source.Disposed -= OnSourceDisposed;
        _source.RootVisual = null;
        _source.Dispose();
    }

    private void OnSourceDisposed(object? sender, EventArgs e) => MarkDisposed();

    private void MarkDisposed()
    {
        if (_disposed)
            return;

        _disposed = true;
        Box.PropertyChanged -= OnBoxPropertyChanged;
    }
}
