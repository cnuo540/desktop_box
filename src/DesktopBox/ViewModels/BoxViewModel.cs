using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using DesktopBox.Models;
using DesktopBox.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopBox.ViewModels;

public partial class BoxViewModel : ObservableObject
{
    public Guid Id { get; }

    [ObservableProperty] private string _name;
    /// <summary>程序生成盒子的稳定标识(box.organize / box.sysicons);用户手建为 null。逻辑匹配用 Key。</summary>
    public string? Key { get; }
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _width;
    [ObservableProperty] private double _height;
    [ObservableProperty] private string? _accentColor;

    /// <summary>是否卷起(只剩标题栏)。持久化到 boxes.json。</summary>
    [ObservableProperty] private bool _isCollapsed;

    /// <summary>鼠标悬停触发的临时展开。不持久化:鼠标移开后自动恢复卷起。</summary>
    [ObservableProperty] private bool _isHoverExpanded;

    /// <summary>卷起后保留的标题栏高度(像素),由 BoxControl 按标题栏实测高度回填。</summary>
    [ObservableProperty] private double _collapsedHeight = BoxCollapse.DefaultHeaderHeight;

    /// <summary>当前是否按卷起态渲染(卷起且未悬停展开)。</summary>
    public bool IsRolled => BoxCollapse.IsRolled(IsCollapsed, IsHoverExpanded);

    /// <summary>窗口/内容应使用的高度:卷起时=标题栏高度,否则=用户设定的高度。
    /// 盒子宿主窗口(BoxWindow)按这个值设置 HWND 高度,所以卷起/展开只改高度、不动 X/Y。</summary>
    public double RenderHeight => BoxCollapse.RenderHeight(IsCollapsed, IsHoverExpanded, Height, CollapsedHeight);

    /// <summary>是否显示标题栏之外的内容(标签行 + 条目区)。</summary>
    public bool ShowContent => !IsRolled;

    /// <summary>是否显示标签行:标签盒子且非卷起态。</summary>
    public bool ShowTabs => IsTabbed && ShowContent;

    /// <summary>卷起/展开按钮的提示文本:随当前语言与卷起状态变化(卷起时提示"展开盒子")。</summary>
    public string CollapseActionText
    {
        get
        {
            try
            {
                var loc = App.Services.GetRequiredService<ILocalizerService>();
                return loc[IsCollapsed ? "tip.expandBox" : "tip.rollUp"];
            }
            catch
            {
                // 无 DI 容器时(设计器/单元测试直接构造 VM):提示退化为英文兜底,不影响卷起功能本身。
                return IsCollapsed ? "Expand box" : "Roll up box";
            }
        }
    }

    /// <summary>语言切换后重算 CollapseActionText(触发 PropertyChanged)。</summary>
    public void RefreshCollapseActionText() => OnPropertyChanged(nameof(CollapseActionText));

    partial void OnIsCollapsedChanged(bool value) => RefreshCollapseState();

    partial void OnIsHoverExpandedChanged(bool value) => RefreshCollapseState();

    partial void OnCollapsedHeightChanged(double value) => RefreshCollapseState();

    // 高度变化会改变 RenderHeight(展开态下),通知出去让宿主窗口跟着改 HWND 高度。
    partial void OnHeightChanged(double value) => OnPropertyChanged(nameof(RenderHeight));

    /// <summary>重新计算并通知所有卷起态相关属性(宿主窗口监听 RenderHeight 来改 HWND 高度)。</summary>
    private void RefreshCollapseState()
    {
        OnPropertyChanged(nameof(IsRolled));
        OnPropertyChanged(nameof(RenderHeight));
        OnPropertyChanged(nameof(ShowContent));
        OnPropertyChanged(nameof(ShowTabs));
        OnPropertyChanged(nameof(CollapseActionText));
    }

    /// <summary>卷起/展开切换(用户操作,持久化由调用方 ScheduleSave)。</summary>
    public void ToggleCollapsed() => IsCollapsed = !IsCollapsed;

    public ObservableCollection<BoxItem> Items { get; } = new();
    public ObservableCollection<BoxTab> Tabs { get; } = new();

    [ObservableProperty] private BoxTab? _selectedTab;

    /// <summary>是否为标签模式(Tabs 非空)。</summary>
    public bool IsTabbed => Tabs.Count > 0;

    /// <summary>当前盒子在 UI 上应渲染的条目集合:普通模式=Items,标签模式=选中标签的 Items。</summary>
    public ObservableCollection<BoxItem> DisplayItems =>
        IsTabbed && SelectedTab is not null ? SelectedTab.Items : Items;

    /// <summary>显示名:有 Key 时取当前语言翻译,否则用 Name。语言切换时由 MainViewModel 触发刷新。</summary>
    public string Header
    {
        get
        {
            if (!string.IsNullOrEmpty(Key))
            {
                var loc = App.Services.GetRequiredService<ILocalizerService>();
                return loc[Key];
            }
            return Name;
        }
    }

    /// <summary>语言切换后重算 Header(触发 PropertyChanged)。</summary>
    public void RefreshHeader() => OnPropertyChanged(nameof(Header));

    // ---- 视图模式:统一用全局(MainViewModel.GlobalViewMode),所有盒子/标签一致 ----
    // ViewMode 字段保留为"当前视图"的只读镜像,IsLarge 等基于它;由 MainViewModel 广播刷新。
    public ViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (_viewMode == value) return;
            _viewMode = value;
            OnPropertyChanged(nameof(ViewMode));
            RefreshViewMode();
        }
    }
    private ViewMode _viewMode = ViewMode.Large;

    public bool IsLarge => ViewMode == ViewMode.Large;
    public bool IsMedium => ViewMode == ViewMode.Medium;
    public bool IsSmall => ViewMode == ViewMode.Small;
    public bool IsList => ViewMode == ViewMode.List;
    public bool IsDetail => ViewMode == ViewMode.Detail;
    public bool IsTile => ViewMode == ViewMode.Tile;

    /// <summary>同步全局模式到本盒子,并刷新所有视图相关属性(由 MainViewModel.GlobalViewMode 变化时调用)。</summary>
    public void SyncViewMode(ViewMode global)
    {
        _viewMode = global;
        OnPropertyChanged(nameof(ViewMode));
        RefreshViewMode();
    }

    /// <summary>重新计算并通知所有视图模式相关属性。</summary>
    public void RefreshViewMode()
    {
        OnPropertyChanged(nameof(IsLarge));
        OnPropertyChanged(nameof(IsMedium));
        OnPropertyChanged(nameof(IsSmall));
        OnPropertyChanged(nameof(IsList));
        OnPropertyChanged(nameof(IsDetail));
        OnPropertyChanged(nameof(IsTile));
        ViewModeChanged?.Invoke(this);
    }

    /// <summary>视图模式变更时触发(供 BoxControl 在切到详细信息时惰性填充大小/时间)。</summary>
    public event Action<BoxViewModel>? ViewModeChanged;

    /// <summary>条目总数(跨所有标签)。用于判断盒子是否允许删除。</summary>
    public int TotalItemCount =>
        IsTabbed ? Tabs.Sum(t => t.Items.Count) : Items.Count;

    public BoxViewModel(Box model)
    {
        Id = model.Id;
        _name = model.Name;
        Key = model.Key;
        _x = model.X; _y = model.Y;
        _width = model.Width; _height = model.Height;
        _accentColor = model.AccentColor;
        _viewMode = model.ViewMode;
        _isCollapsed = model.IsCollapsed;
        foreach (var i in model.Items) Items.Add(i);
        foreach (var t in model.Tabs) Tabs.Add(t);
        if (Tabs.Count > 0) _selectedTab = Tabs[0];

        // 增删标签后通知 IsTabbed / DisplayItems,并自动选首个标签
        Tabs.CollectionChanged += OnTabsChanged;
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsTabbed));
        OnPropertyChanged(nameof(DisplayItems));
        OnPropertyChanged(nameof(ShowTabs));
        if (SelectedTab is null || !Tabs.Contains(SelectedTab))
            SelectedTab = Tabs.FirstOrDefault();
    }

    partial void OnSelectedTabChanged(BoxTab? value)
        => OnPropertyChanged(nameof(DisplayItems));

    /// <summary>清空所有条目(普通模式清 Items,标签模式清每个标签)。</summary>
    public void ClearAll()
    {
        Items.Clear();
        foreach (var t in Tabs) t.Items.Clear();
    }

    /// <summary>新建一个空标签并选中它。name 为空时用当前语言的默认标签名。</summary>
    public BoxTab AddTab(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            name = $"{App.Services.GetRequiredService<ILocalizerService>()["tab.default"]} {Tabs.Count + 1}";
        var tab = new BoxTab { Name = name };
        Tabs.Add(tab);
        SelectedTab = tab;
        return tab;
    }

    public Box ToModel() => new()
    {
        Id = Id,
        Name = Name,
        Key = Key,
        X = X,
        Y = Y,
        Width = Width,
        Height = Height,
        AccentColor = AccentColor,
        ViewMode = ViewMode,
        IsCollapsed = IsCollapsed,
        Items = new(Items),
        Tabs = new(Tabs)
    };
}
