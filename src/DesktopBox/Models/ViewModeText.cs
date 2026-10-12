namespace DesktopBox.Models;

/// <summary>视图模式(图标大小)相关的小逻辑:本地化资源键、以及"图标大小按钮"的循环顺序。
/// 与 UI 无关,便于单测。</summary>
public static class ViewModeText
{
    /// <summary>视图模式对应的 i18n 资源键(与 Strings.*.xaml / 右键菜单一致)。</summary>
    public static string ResourceKey(ViewMode mode) => mode switch
    {
        ViewMode.Medium => "view.medium",
        ViewMode.Small => "view.small",
        ViewMode.List => "view.list",
        ViewMode.Detail => "view.detail",
        ViewMode.Tile => "view.tile",
        _ => "view.large"
    };

    /// <summary>图标大小按钮的下一个模式:大 → 中 → 小 → 大循环。
    /// 列表/详细信息/平铺属于"布局"而非图标大小(它们只能从右键菜单选),点按钮一律回到大图标。</summary>
    public static ViewMode NextIconSize(ViewMode current) => current switch
    {
        ViewMode.Large => ViewMode.Medium,
        ViewMode.Medium => ViewMode.Small,
        _ => ViewMode.Large
    };

    /// <summary>是否属于"图标大小"三档(大/中/小),用于判断当前是否处于循环内。</summary>
    public static bool IsIconSize(ViewMode mode) =>
        mode is ViewMode.Large or ViewMode.Medium or ViewMode.Small;
}
