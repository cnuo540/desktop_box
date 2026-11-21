using System;
using System.Runtime.InteropServices;

namespace DesktopBox.Native;

public static class DwmApi
{
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;     // Win11 22000+
    public const int DWMSBT_MAINWINDOW = 2;              // Mica
    /// <summary>窗口是否被 DWM 遮蔽(UWP 应用挂起后矩形还留在屏幕上,但实际不可见)。</summary>
    public const int DWMWA_CLOAKED = 14;

    [DllImport("dwmapi.dll", PreserveSig = false)]
    public static extern void DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    /// <summary>这个窗口当前是不是被 cloak。读不到(系统不支持/窗口已销毁)一律当"没 cloak" ——
    /// 误挖一个洞的代价只是落点漏给 explorer,误判成 cloak 则会让拖放被我们的覆盖层截走。</summary>
    public static bool IsCloaked(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Margins { public int Left, Right, Top, Bottom; }
}
