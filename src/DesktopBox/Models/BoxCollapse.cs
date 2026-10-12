namespace DesktopBox.Models;

/// <summary>盒子卷起(折叠)/悬停自动展开的纯逻辑。
/// 抽成静态类是为了让"卷起时该多高、鼠标悬停该不该展开"这类规则能被单元测试直接覆盖,
/// 不必启动 WPF 或真实窗口。</summary>
public static class BoxCollapse
{
    /// <summary>卷起后保留的标题栏高度(像素)的默认值。
    /// 实际值由 BoxControl 按标题栏实测高度回填(不同 DPI/系统字号下标题栏高度不同)。</summary>
    public const double DefaultHeaderHeight = 42;

    /// <summary>鼠标进入后多久自动展开(毫秒)。留少量延时,鼠标只是划过盒子时不会弹开。</summary>
    public const int HoverExpandDelayMs = 180;

    /// <summary>鼠标离开后多久自动收起(毫秒)。比展开延时更长:
    /// 从标题栏移到条目、或从条目移回标题栏时的短暂出界不应立刻收起。</summary>
    public const int HoverCollapseDelayMs = 320;

    /// <summary>是否按卷起态渲染(标题栏之外的标签行/条目区隐藏)。</summary>
    public static bool IsRolled(bool isCollapsed, bool isHoverExpanded)
        => isCollapsed && !isHoverExpanded;

    /// <summary>渲染高度:卷起且未悬停展开时=标题栏高度,否则=用户设定的高度。
    /// 盒子只向下生长(标题栏位置不变),所以 X/Y 不参与计算。</summary>
    public static double RenderHeight(bool isCollapsed, bool isHoverExpanded, double height, double collapsedHeight)
        => IsRolled(isCollapsed, isHoverExpanded) ? NormalizeCollapsedHeight(collapsedHeight) : height;

    /// <summary>卷起高度兜底:未实测到标题栏高度(<=0)时用默认值。</summary>
    public static double NormalizeCollapsedHeight(double collapsedHeight)
        => collapsedHeight > 0 ? collapsedHeight : DefaultHeaderHeight;

    /// <summary>鼠标进入后是否应触发自动展开:开关开启 + 盒子处于卷起态 + 当前还没有展开。
    /// 已手动展开(IsCollapsed=false)或已悬停展开时返回 false,避免重复触发。</summary>
    public static bool ShouldExpandOnHover(bool isCollapsed, bool isHoverExpanded, bool hoverExpandEnabled)
        => hoverExpandEnabled && isCollapsed && !isHoverExpanded;

    /// <summary>鼠标离开后是否应收起:只收"悬停展开"这个临时态,
    /// 用户手动设定的展开(IsCollapsed=false)不受影响。</summary>
    public static bool ShouldCollapseOnLeave(bool isHoverExpanded) => isHoverExpanded;
}
