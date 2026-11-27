using System;
using System.Runtime.InteropServices;

namespace DesktopBox.Native;

/// <summary>全局低级键盘钩子(WH_KEYBOARD_LL):用来补上"我们的窗口收不到键盘"这件事。
///
/// 桌面层的窗口是"桌面宿主的不可激活子窗口",盒子的窗口也是子窗口 —— 点它们都不会让进程拿到
/// 键盘焦点,所以 Delete(删除选中项)、Ctrl+C/V(复制粘贴)这类快捷键根本到不了我们手里,
/// 只能靠钩子看一眼按键。
///
/// **默认只放行**(`CallNextHookEx`),不改写别人的输入;只有调用方在 <see cref="KeyDown"/>
/// 里显式置 <see cref="SuppressCurrentKey"/>=true 才吞掉这一次按键 —— 唯一用途是 Ctrl+V:
/// 剪贴板里是"从盒子复制的条目"、又粘贴在桌面上时,必须由我们自己处理(移动),
/// 否则 explorer 会执行一次原生复制,弹出"源文件名和目标文件名相同"。
/// 回调跑在安装线程(UI 线程)的消息循环上。</summary>
internal sealed class LowLevelKeyboardHook : IDisposable
{
    public const int VK_DELETE = 0x2E;

    private readonly User32.LowLevelKeyboardProc _callback;   // 存字段防 GC
    private IntPtr _hook;
    private bool _disposed;

    /// <summary>按下某键(虚拟键码)。</summary>
    public event Action<int>? KeyDown;

    /// <summary>在 <see cref="KeyDown"/> 里置 true → 吞掉这一次按键(不再传给后面的程序)。</summary>
    public bool SuppressCurrentKey { get; set; }

    public LowLevelKeyboardHook()
    {
        _callback = Callback;
        _hook = User32.SetWindowsHookEx(User32.WH_KEYBOARD_LL, _callback, IntPtr.Zero, 0);
        if (_hook == IntPtr.Zero)
            Services.LogService.Warn("KeyboardHook.Install", "低级键盘钩子没装上:Delete 删除选中项、Ctrl+C/V 复制粘贴都会失效");
    }

    public bool IsInstalled => _hook != IntPtr.Zero;

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var message = wParam.ToInt32();
                if (message is User32.WM_KEYDOWN or User32.WM_SYSKEYDOWN)
                {
                    var info = Marshal.PtrToStructure<User32.KBDLLHOOKSTRUCT>(lParam);
                    SuppressCurrentKey = false;
                    KeyDown?.Invoke((int)info.vkCode);
                    if (SuppressCurrentKey)
                    {
                        SuppressCurrentKey = false;
                        return (IntPtr)1;   // 吞掉:后面的程序(explorer)不会看到这一下
                    }
                }
            }
        }
        catch
        {
            // 钩子里绝不能抛:会把整个键盘输入卡住
        }

        return User32.CallNextHookEx(_hook, nCode, wParam, lParam);
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
