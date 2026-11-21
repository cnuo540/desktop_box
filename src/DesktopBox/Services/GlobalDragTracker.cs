using System;
using System.Threading;

namespace DesktopBox.Services;

/// <summary>全局"左键拖动刚刚结束"的打点。低级鼠标钩子在每次左键拖动结束时调用 <see cref="MarkDragEnd"/>。
///
/// 用途:自动收纳要区分两种"桌面上冒出新文件"——
/// ① 用户从资源管理器<b>拖到桌面</b>(有意放在桌面)→ 不收,留在桌面;
/// ② 程序<b>另存为/新建</b>到桌面 → 收进盒子。
/// 两者在文件系统事件上完全一样(都是 Created),只有"刚结束过一次拖动"这个旁证能区分。
///
/// 判定规则(简单可解释):拖动结束后的短窗口内,桌面上新出现的文件一律视为"用户主动放置"。
/// 误判方向是安全的 —— 漏收只是文件留在桌面上(看得见,一键整理随时能收),
/// 而误收会把用户刚放好的文件从桌面上抽走(更烦)。</summary>
public static class GlobalDragTracker
{
    /// <summary>拖动结束后多久内仍算"刚拖过"。拖放落盘紧跟在松手之后,1.5 秒足够覆盖慢盘。</summary>
    public const int JustEndedWindowMs = 1500;

    private static long _lastDragEndTicks;
    private static int _lastX;
    private static int _lastY;

    /// <summary>最近一次左键拖动结束的时刻(Environment.TickCount64);0 = 本次进程内还没拖过。</summary>
    public static long LastDragEndTicks => Interlocked.Read(ref _lastDragEndTicks);

    /// <summary>最近一次拖动结束时光标的屏幕坐标(物理像素)。</summary>
    public static (int X, int Y) LastDragEndPos =>
        (Volatile.Read(ref _lastX), Volatile.Read(ref _lastY));

    /// <summary>是否"刚刚结束过一次拖动"(默认 <see cref="JustEndedWindowMs"/> 毫秒内)。</summary>
    public static bool JustEnded(int withinMs = JustEndedWindowMs)
    {
        var ticks = Interlocked.Read(ref _lastDragEndTicks);
        return ticks != 0 && Environment.TickCount64 - ticks <= withinMs;
    }

    /// <summary>记录一次左键拖动结束(由低级鼠标钩子调用)。</summary>
    public static void MarkDragEnd(int x, int y)
    {
        Interlocked.Exchange(ref _lastDragEndTicks, Environment.TickCount64);
        Volatile.Write(ref _lastX, x);
        Volatile.Write(ref _lastY, y);
    }

    /// <summary>清掉打点(仅测试用:否则一次 MarkDragEnd 会污染之后 1.5 秒内的所有用例)。</summary>
    public static void Reset()
    {
        Interlocked.Exchange(ref _lastDragEndTicks, 0);
        Volatile.Write(ref _lastX, 0);
        Volatile.Write(ref _lastY, 0);
    }
}
