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

    /// <summary>是否被选中(单选/Ctrl多选)。选中时磁贴显示半透明蓝色高亮(资源管理器风格)。</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>详细信息视图用:文件大小(文件夹为空,显示"—")。惰性填充,可观察。</summary>
    [ObservableProperty] private string? _sizeText;
    /// <summary>详细信息视图用:最后修改时间。惰性填充,可观察。</summary>
    [ObservableProperty] private string? _modifiedText;

    public int Order { get; set; }
}
