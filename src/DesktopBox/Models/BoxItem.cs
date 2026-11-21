using CommunityToolkit.Mvvm.ComponentModel;

namespace DesktopBox.Models;

/// <summary>盒内单个条目。IconCachePath 设为可观察,以便后台异步提取图标后实时刷新磁贴。</summary>
public partial class BoxItem : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BoxId { get; set; }
    public ItemType Type { get; set; }
    public string TargetPath { get; set; } = "";

    /// <summary>界面上显示的名字。必须是可观察的:改名(改真实文件名)之后要立刻刷新,
    /// 否则会出现"文件已经改了、盒子里还是旧名字"。</summary>
    [ObservableProperty] private string _displayName = "";

    [ObservableProperty] private string? _iconCachePath;

    /// <summary>磁贴有没有真实图标可画。
    ///
    /// 必须是可观察的绑定源:v1.7.36 之前 ItemTile 用命令式 <c>Img.Visibility = ...</c> 只在
    /// DataContext 变化时算一次,而 IconCachePath 是后台异步填的 —— 一键整理批量加条目时磁贴先建、
    /// 图标后到,Visibility 永远停在 Collapsed(现象:整理完一片字母占位,切一次标签才正常)。
    /// 改成绑定后由 IconCachePath 驱动,到达即显示。</summary>
    public bool HasIcon => !string.IsNullOrEmpty(IconCachePath);

    partial void OnIconCachePathChanged(string? value) => OnPropertyChanged(nameof(HasIcon));

    /// <summary>是否被选中(单选/Ctrl多选)。选中时磁贴显示半透明蓝色高亮(资源管理器风格)。</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>详细信息视图用:文件大小(文件夹为空,显示"—")。惰性填充,可观察。</summary>
    [ObservableProperty] private string? _sizeText;
    /// <summary>详细信息视图用:最后修改时间。惰性填充,可观察。</summary>
    [ObservableProperty] private string? _modifiedText;

    public int Order { get; set; }
}
