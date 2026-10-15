using System;
using System.Runtime.InteropServices;

namespace DesktopBox.Native;

/// <summary>全局低级键盘钩子(WH_KEYBOARD_LL):用来补上"我们的窗口收不到键盘"这件事。
///
/// 桌面层的窗口是"桌面宿主的不可激活子窗口",盒子的窗口也是子窗口 —— 点它们都不会让进程拿到
/// 键盘焦点,所以 Delete(删除选中项)这类快捷键根本到不了我们手里,只能靠钩子看一眼按键。
///
/// **只看不吞**:永远把按键放行(`CallNextHookEx`),绝不改写别人的输入;
/// 要不要响应由调用方判断:光标正压在我们的界面上、且那一处有选中项 —— 才动手。
/// 回调跑在安装线程(UI 线程)的消息循环上。</summary>
internal sealed class LowLevelKeyboardHook : IDisposable
{
    public const int VK_DELETE = 0x2E;

    private readonly User32.LowLevelKeyboardProc _callback;   // 存字段防 GC
    private IntPtr _hook;
    private bool _disposed;

    /// <summary>按下某键(虚拟键码)。</summary>
    public event Action<int>? KeyDown;

    public LowLevelKeyboardHook()
    {
        _callback = Callback;
        _hook = User32.SetWindowsHookEx(User32.WH_KEYBOARD_LL, _callback, IntPtr.Zero, 0);
        if (_hook == IntPtr.Zero)
            Services.LogService.Warn("KeyboardHook.Install", "低级键盘钩子没装上:Delete 删除选中项会失效");
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
                    KeyDown?.Invoke((int)info.vkCode);
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
