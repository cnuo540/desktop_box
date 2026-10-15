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

    /// <summary>盒子背景的不透明度(0.3~1.0,默认 1 = 原来那样)。
    /// 只影响背景色:图标和文字始终清晰,调低就透出桌面/壁纸。</summary>
    public double BoxOpacity { get; set; } = 1.0;

    /// <summary>是否隐藏桌面上的原生图标(关掉 explorer 的桌面图标层)。
    /// 未收纳的项、桌面上新建的文件改由 DesktopLayerWindow 自绘 —— 目录窗口完全不受影响。
    /// 默认关闭。</summary>
    public bool HideCollectedDesktopIcons { get; set; }
}
