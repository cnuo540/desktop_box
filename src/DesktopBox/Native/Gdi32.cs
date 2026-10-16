using System;
using System.Runtime.InteropServices;

namespace DesktopBox.Native;

public static class Gdi32
{
    public const int RGN_DIFF = 4;
    public const int RGN_OR = 2;

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    /// <summary>圆角矩形区域。用来把盒子窗口本身裁成圆角 —— 否则圆角外的透明像素会被合成成黑边。</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom,
        int ellipseWidth, int ellipseHeight);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern int CombineRgn(IntPtr dest, IntPtr source1, IntPtr source2, int mode);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool DeleteObject(IntPtr obj);
}
