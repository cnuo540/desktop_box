using System;
using System.Runtime.InteropServices;

namespace DesktopBox.Native;

/// <summary>全局低级鼠标钩子(WH_MOUSE_LL):用来回答"鼠标在别处按下了吗 / 往哪儿拖"。
///
/// 桌面层的窗口区域被裁成"图标矩形并集":图标之间的空白处鼠标事件归 explorer ——
/// 所以"点空白处取消选中"和"在空白处拉框多选"这两件事我们自己收不到,只能靠这个钩子。
///
/// 钩子常驻(桌面层活着就装着):<b>不能只在有选中项时临时装</b> —— "开始框选"那一下按下发生时
/// 还没有任何选中项,临时装就永远等不到。回调只做几个比较,开销可忽略。
/// 回调跑在安装线程(UI 线程)的消息循环上,所以回调里可以直接改界面。</summary>
internal sealed class LowLevelMouseHook : IDisposable
{
    private readonly User32.LowLevelMouseProc _callback;   // 存成字段:委托被 GC 掉会让钩子回调空指针
    private IntPtr _hook;
    private bool _disposed;
    private bool _dragging;

    // ---- 常驻的"左键拖动"打点(与 TrackingDrag 无关) ----
    // TrackingDrag 只有我们自己(桌面层框选)会打开;而"用户从资源管理器拖文件到桌面"是我们要识别的
    // 另一类拖动,钩子必须无条件跟踪,否则自动收纳分不清"拖来的"和"程序新建的"。
    private const int DragThresholdPx = 5;   // 物理像素:小于它的移动算手抖,不算拖动
    private bool _leftDown;
    private bool _leftMoved;
    private int _leftDownX;
    private int _leftDownY;

    /// <summary>任意鼠标键按下(物理像素,屏幕坐标)。用于"点到别处 → 取消选中"。</summary>
    public event Action<int, int>? ButtonDown;

    /// <summary>左键按下(物理像素,屏幕坐标)。</summary>
    public event Action<int, int>? LeftButtonDown;

    /// <summary>左键按住期间的移动(只有 <see cref="TrackingDrag"/> 为真时才会来)。</summary>
    public event Action<int, int>? DragMove;

    /// <summary>左键抬起(只有 <see cref="TrackingDrag"/> 为真时才会来)。</summary>
    public event Action<int, int>? DragEnd;

    /// <summary>是否跟踪这次左键拖动(由使用方在 <see cref="LeftButtonDown"/> 里决定)。</summary>
    public bool TrackingDrag { get; set; }

    public LowLevelMouseHook()
    {
        _callback = Callback;
        _hook = User32.SetWindowsHookEx(User32.WH_MOUSE_LL, _callback, IntPtr.Zero, 0);
        if (_hook == IntPtr.Zero)
            Services.LogService.Warn("MouseHook.Install", "低级鼠标钩子没装上:点到别处取消选中、空白处框选都会失效");
    }

    public bool IsInstalled => _hook != IntPtr.Zero;

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
                Handle(wParam.ToInt32(), Marshal.PtrToStructure<User32.MSLLHOOKSTRUCT>(lParam));
        }
        catch
        {
            // 钩子回调里绝不能抛:会把整个鼠标输入卡住
        }

        return User32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void Handle(int message, User32.MSLLHOOKSTRUCT info)
    {
        // 无条件跟踪左键"按下 → 移动 → 抬起",用于给 GlobalDragTracker 打点
        // (判断桌面上新冒出的文件是不是用户拖过来的)。与下面的 TrackingDrag 逻辑互不干扰。
        TrackLeftButtonDrag(message, info);

        switch (message)
        {
            case User32.WM_LBUTTONDOWN:
                _dragging = false;
                TrackingDrag = false;
                LeftButtonDown?.Invoke(info.pt.X, info.pt.Y);
                _dragging = TrackingDrag;
                ButtonDown?.Invoke(info.pt.X, info.pt.Y);
                break;

            case User32.WM_RBUTTONDOWN:
            case User32.WM_MBUTTONDOWN:
            case User32.WM_XBUTTONDOWN:
                ButtonDown?.Invoke(info.pt.X, info.pt.Y);
                break;

            case User32.WM_MOUSEMOVE:
                if (_dragging) DragMove?.Invoke(info.pt.X, info.pt.Y);
                break;

            case User32.WM_LBUTTONUP:
                if (!_dragging) break;
                _dragging = false;
                TrackingDrag = false;
                DragEnd?.Invoke(info.pt.X, info.pt.Y);
                break;
        }
    }

    /// <summary>左键 按下→移动→抬起 的常驻跟踪:移动超过阈值才算"拖动",抬起时给
    /// <see cref="Services.GlobalDragTracker"/> 打点。纯记账,不派发任何事件。</summary>
    private void TrackLeftButtonDrag(int message, User32.MSLLHOOKSTRUCT info)
    {
        switch (message)
        {
            case User32.WM_LBUTTONDOWN:
                _leftDown = true;
                _leftMoved = false;
                _leftDownX = info.pt.X;
                _leftDownY = info.pt.Y;
                break;

            case User32.WM_MOUSEMOVE:
                if (!_leftDown || _leftMoved) break;
                if (Math.Abs(info.pt.X - _leftDownX) < DragThresholdPx
                    && Math.Abs(info.pt.Y - _leftDownY) < DragThresholdPx) break;
                _leftMoved = true;
                break;

            case User32.WM_LBUTTONUP:
                if (!_leftDown) break;
                var moved = _leftMoved;
                var x = info.pt.X;
                var y = info.pt.Y;
                _leftDown = false;
                _leftMoved = false;
                if (moved) Services.GlobalDragTracker.MarkDragEnd(x, y);
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hook != IntPtr.Zero)
        {
            try { User32.UnhookWindowsHookEx(_hook); } catch { }
            _hook = IntPtr.Zero;
        }

        GC.SuppressFinalize(this);
    }
}
