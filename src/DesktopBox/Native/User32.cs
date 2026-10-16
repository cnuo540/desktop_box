using System;
using System.Runtime.InteropServices;

namespace DesktopBox.Native;

public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

public static class User32
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_LAYERED = 0x00080000;
    public const uint LWA_ALPHA = 0x00000002;
    public const uint SMTO_NORMAL = 0x0000;
    public const uint SMTO_ABORTIFHUNG = 0x0002;
    public const uint WM_COMMAND = 0x0111;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ScreenToClient(IntPtr hWnd, ref Point point);

    public static (int X, int Y) ScreenPointToClient(IntPtr parent, double screenX, double screenY)
    {
        var point = new Point
        {
            X = (int)Math.Round(screenX),
            Y = (int)Math.Round(screenY)
        };
        if (parent != IntPtr.Zero)
            ScreenToClient(parent, ref point);
        return (point.X, point.Y);
    }

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public const int SW_RESTORE = 9;
    public const int SW_SHOWNOACTIVATE = 4;

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// 把"可以抢占前台"的权限授予某个进程。必须在<b>创建该进程之前</b>调用,并传它的 pid。
    ///
    /// 为什么需要:右键菜单 helper 是独立进程,它内部要 SetForegroundWindow 自己的菜单窗口,
    /// 否则 TrackPopupMenuEx 会<b>立刻返回 0</b>(菜单根本不显示)。Windows 默认禁止非前台进程
    /// 抢前台焦点,只有当前前台进程显式授权才允许 —— 而桌面层窗口是 WS_EX_NOACTIVATE 的,
    /// 它并不是前台进程,所以 helper 弹菜单必然失败。ASFW_ANY 表示"任何进程都放行"。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool AllowSetForegroundWindow(int dwProcessId);

    /// <summary>AllowSetForegroundWindow 的特殊值:放行所有进程。</summary>
    public const int ASFW_ANY = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    /// <summary>取窗口类名的托管封装。菜单窗口的类名是 <c>#32768</c>。</summary>
    public static string GetClassNameOf(IntPtr hWnd)
    {
        var sb = new System.Text.StringBuilder(256);
        return GetClassName(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    /// <summary>x64 正确的指针宽度版本（GetWindowLong 在 x64 下会截断指针型索引）。新代码用这个。</summary>
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    /// <summary>HWND_TOP:把窗口置于非 topmost 序列顶部(不置顶,不挡后续激活的窗口)。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetWindowText(IntPtr hWnd, string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetParent(IntPtr hWnd);

    /// <summary>取窗口所属进程/线程 id(用于判断命中点上的窗口是不是本进程的)。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>GW_CHILD / GW_HWNDNEXT 等 GetWindow 方向常量。</summary>
    public const uint GW_HWNDFIRST = 0;
    public const uint GW_HWNDLAST = 1;
    public const uint GW_HWNDNEXT = 2;
    public const uint GW_HWNDPREV = 3;
    public const uint GW_OWNER = 4;
    public const uint GW_CHILD = 5;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;
    /// <summary>异步排队，不等 explorer 重排完成。跨进程 SetWindowPos 必备，否则 explorer 忙则 UI 卡死。</summary>
    public const uint SWP_ASYNCWINDOWPOS = 0x4000;
    /// <summary>跨进程 SetWindowPos 标准标志：不抢焦点 + 异步。</summary>
    public const uint SWP_CROSSPROC = SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_ASYNCWINDOWPOS;
    public const int GWL_STYLE = -16;
    public const int WS_CHILD = 0x40000000;
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_VISIBLE = 0x10000000;
    public const int WS_CLIPSIBLINGS = 0x04000000;
    public const int WS_CLIPCHILDREN = 0x02000000;

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>取屏幕点处最上层可见窗口（用于遮挡检测：盒子中心点返回的若不是自己，就是被盖住了）。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT pt);

    public static IntPtr WindowFromPoint(int x, int y) => WindowFromPoint(new POINT { X = x, Y = y });

    // ===== 全局低级鼠标钩子(WH_MOUSE_LL):只用来"点在别处时取消选中",装完就用、用完立刻卸 =====

    public const int WH_MOUSE_LL = 14;
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_MBUTTONDOWN = 0x0207;
    public const int WM_XBUTTONDOWN = 0x020B;

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>装钩子。回调在该线程的消息循环上执行 —— 必须由 UI 线程安装。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    /// <summary>键盘钩子同名单入口(和鼠标钩子区别在回调委托类型)。</summary>
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookExW")]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    public const uint GA_ROOT = 2;

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    /// <summary>注册一个全局唯一的窗口消息号(用于 shell 变化通知等自定义消息)。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string lpString);

    public const uint WM_SPAWN_WORKERW = 0x052C;

    public static IntPtr GetProgman()
    {
        var h = FindWindow("Progman", null);
        if (h != IntPtr.Zero) return h;
        // 兜底:部分 Win11 环境 FindWindow("Progman") 返回0但窗口实际存在(实测 10.0.26200),
        // 用 EnumWindows 按类名查找绕过。否则 GetWorkerW 链路第一步就断,导致 reparent 失败。
        IntPtr found = IntPtr.Zero;
        EnumWindows((topHwnd, _) =>
        {
            var sb = new System.Text.StringBuilder(256);
            GetClassName(topHwnd, sb, 256);
            if (sb.ToString() == "Progman") { found = topHwnd; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static IntPtr GetWorkerW()
    {
        // 缓存：0x052C 会让 Progman 新建 WorkerW，高频调用=狂建壁纸层窗口=桌面闪。
        // 缓存有效（IsWindow）直接返回；失效才重新 spawn，且 spawn 消息只发一次。
        if (_cachedWorkerW != IntPtr.Zero && IsWindow(_cachedWorkerW))
            return _cachedWorkerW;
        var progman = GetProgman();
        if (progman == IntPtr.Zero) return IntPtr.Zero;
        SendMessageTimeout(progman, WM_SPAWN_WORKERW, IntPtr.Zero, IntPtr.Zero, SMTO_NORMAL, 1000, out _);
        // 0x052C 让 Progman 创建 WorkerW,但创建有延迟,需重试几次 EnumWindows 才能找到
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var workerW = FindWorkerWForDefView();
            if (workerW != IntPtr.Zero)
            {
                _cachedWorkerW = workerW;
                return workerW;
            }
            System.Threading.Thread.Sleep(60);
        }
        return IntPtr.Zero;
    }

    private static IntPtr _cachedWorkerW = IntPtr.Zero;

    /// <summary>explorer 重启后缓存失效，下次 GetWorkerW 会重新 spawn。TaskbarCreated 时调用。</summary>
    public static void InvalidateWorkerWCache() => _cachedWorkerW = IntPtr.Zero;

    private static IntPtr FindWorkerWForDefView()
    {
        IntPtr workerW = IntPtr.Zero;
        EnumWindows((topHwnd, _) =>
        {
            var shellDefView = FindWindowEx(topHwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (shellDefView != IntPtr.Zero)
            {
                workerW = FindWindowEx(IntPtr.Zero, topHwnd, "WorkerW", null);
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return workerW;
    }

    /// <summary>
    /// 桌面宿主解析（共享唯一入口）。
    /// DefView 优先：它是图标层本身，盒子作为其子窗口天然在壁纸层之上，保证可见。
    /// Progman 次之，WorkerW（带缓存，只 spawn 一次）最后兜底。
    /// 历史：v1.7.10 曾改 Progman 优先降闪烁，结果 Win11 上盒子被盖住不可见
    /// （v1.7.12 日志：窗 alive/visible 但人眼看不见），v1.7.13 改回 DefView 优先。
    /// </summary>
    public static IntPtr ResolveDesktopHost()
    {
        var def = FindShellDefView();
        if (def != IntPtr.Zero && IsWindow(def))
            return def;
        var progman = GetProgman();
        if (progman != IntPtr.Zero && IsWindow(progman))
            return progman;
        var worker = GetWorkerW();
        return IsWindow(worker) ? worker : IntPtr.Zero;
    }

    /// <summary>定位桌面图标视图窗口 SHELLDLL_DefView(用于控制桌面图标显隐)。</summary>
    public static IntPtr FindShellDefView()
    {
        var def = FindWindowEx(GetProgman(), IntPtr.Zero, "SHELLDLL_DefView", null);
        if (def != IntPtr.Zero) return def;

        IntPtr found = IntPtr.Zero;
        EnumWindows((topHwnd, _) =>
        {
            var d = FindWindowEx(topHwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (d != IntPtr.Zero) { found = d; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>定位桌面图标 ListView。实际图标可见性以它为准,注册表可能滞后或被外部工具改坏。</summary>
    public static IntPtr FindDesktopListView()
    {
        var def = FindShellDefView();
        if (def == IntPtr.Zero)
            return IntPtr.Zero;

        var view = FindWindowEx(def, IntPtr.Zero, "SysListView32", "FolderView");
        return view != IntPtr.Zero ? view : FindWindowEx(def, IntPtr.Zero, "SysListView32", null);
    }

}
