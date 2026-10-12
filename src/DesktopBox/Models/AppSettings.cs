namespace DesktopBox.Models;

public class AppSettings
{
    public bool AutoStart { get; set; }
    public bool FollowSystemTheme { get; set; } = true;
    public string Theme { get; set; } = "Dark";
    /// <summary>界面语言:"auto"(跟随系统)/ "zh-CN" / "en-US"。默认跟随系统。</summary>
    public string Language { get; set; } = "auto";
    /// <summary>全局视图模式(所有盒子/标签共享)。默认大图标。</summary>
    public ViewMode GlobalViewMode { get; set; } = ViewMode.Large;
    /// <summary>卷起的盒子在鼠标悬停时自动展开、移开自动收起。默认开启。</summary>
    public bool HoverExpandCollapsedBoxes { get; set; } = true;

    /// <summary>是否隐藏"已收纳"的桌面图标(按项隐藏:程序把这些图标挪到可视区外)。
    /// 未收纳的、桌面上新建的照常显示。默认关闭。</summary>
    public bool HideCollectedDesktopIcons { get; set; }

    /// <summary>是否启用"按项隐藏"的实现(挪图标位置)。
    /// <b>默认关闭</b>:该实现依赖桌面视图的跨进程 COM,目前只在我的受限环境里跑过、
    /// 在真机上因接口声明问题出现访问违例(进程崩溃)还没修好 —— 关掉时走系统整层开关(稳定)。
    /// 修好并真机验证后再默认开启。</summary>
    public bool UsePerItemIconHiding { get; set; }
}
