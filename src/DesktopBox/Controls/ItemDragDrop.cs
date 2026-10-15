using System.IO;
using System.Windows;
using DesktopBox.Models;

namespace DesktopBox.Controls;

public static class ItemDragDrop
{
    public static bool CanDragAsFile(BoxItem item) =>
        item.Type is not ItemType.Url and not ItemType.SystemIcon
        && !item.TargetPath.StartsWith("::", StringComparison.Ordinal)
        && (File.Exists(item.TargetPath) || Directory.Exists(item.TargetPath));

    /// <summary>创建盒内条目拖拽数据。桌面落点由 DesktopBox 自己处理，不能附带
    /// FileDrop，否则 Explorer 可能再次执行原生复制，造成源/目标相同提示。</summary>
    public static DataObject CreateDesktopDropData(BoxItem item)
        => CreateDesktopDropData(new List<BoxItem> { item });

    /// <summary>多选拖拽:一次把整组都带上(<paramref name="items"/> 第一个是鼠标按住的那个)。
    /// 位置数据里的 id 列表让落点知道"这一拖带的是哪几条"。</summary>
    public static DataObject CreateDesktopDropData(IReadOnlyList<BoxItem> items)
    {
        var primary = items[0];
        var data = new DataObject();
        data.SetData(DataFormats.Text, primary.TargetPath);
        data.SetData(DataFormats.UnicodeText, primary.TargetPath);
        data.SetData(DragSourceItemFormat, primary.Id);
        data.SetData(DragSourceItemsFormat, items.Select(i => i.Id).ToArray());
        return data;
    }

    /// <summary>创建带原生 FileDrop 的数据，供需要交给外部程序的场景使用。</summary>
    public static DataObject CreateFileDropData(BoxItem item)
    {
        var data = CreateDesktopDropData(item);
        data.SetData(DataFormats.FileDrop, new[] { item.TargetPath });
        return data;
    }

    public const string DragSourceItemFormat = "DesktopBox.ItemId";

    /// <summary>多选拖拽时的整组 id(第一个是鼠标按住的那个)。落点优先看它,没有才退回单个。</summary>
    public const string DragSourceItemsFormat = "DesktopBox.ItemIds";

    /// <summary>从拖拽数据里取出"这一拖带的所有条目 id"。</summary>
    public static IReadOnlyList<Guid> ReadDraggedIds(IDataObject? data)
    {
        if (data is null) return Array.Empty<Guid>();

        if (data.GetData(DragSourceItemsFormat) is Guid[] many && many.Length > 0)
            return many;
        if (data.GetData(DragSourceItemFormat) is Guid one)
            return new[] { one };
        return Array.Empty<Guid>();
    }

    /// <summary>拖"标签盒子里某个标签"时的数据格式:放进 BoxTab.Id。
    /// 用途:拖到桌面 = 该标签独立成一个新盒子(见 BoxControl 的标签拖拽 + DesktopDropOverlay)。</summary>
    public const string DragSourceTabFormat = "DesktopBox.TabId";
}
