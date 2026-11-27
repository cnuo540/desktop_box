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
    /// 位置数据里的 id 列表让落点知道"这一拖带的是哪几条"。
    ///
    /// <paramref name="includeFileDrop"/>:要不要同时带上原生 FileDrop。
    /// 默认不带 —— 桌面落点由 DesktopBox 自己处理,带上的话万一覆盖层没建成,
    /// explorer 会自己执行一次原生复制,弹"源文件名和目标文件名相同"。
    /// 但<b>拖给外部程序(微信/资源管理器)必须有 FileDrop</b> 它才认,
    /// 所以调用方在覆盖层确实建成了之后才传 true(v1.7.37)。</summary>
    public static DataObject CreateDesktopDropData(IReadOnlyList<BoxItem> items, bool includeFileDrop = false)
    {
        var primary = items[0];
        var data = new DataObject();
        data.SetData(DataFormats.Text, primary.TargetPath);
        data.SetData(DataFormats.UnicodeText, primary.TargetPath);
        data.SetData(DragSourceItemFormat, primary.Id);
        data.SetData(DragSourceItemsFormat, items.Select(i => i.Id).ToArray());

        if (includeFileDrop)
        {
            var files = items.Select(i => i.TargetPath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Where(p => !p.StartsWith("::", StringComparison.Ordinal))
                .Where(p => !p.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                .Where(p => File.Exists(p) || Directory.Exists(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (files.Count > 0) data.SetData(DataFormats.FileDrop, files.ToArray());
        }

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

    /// <summary>Ctrl+C 时放进剪贴板的"这是我们复制出来的条目"标记(条目 Id 数组)。
    ///
    /// 粘贴时要靠它区分两种情况:
    /// ① 剪贴板里有这个格式 → 粘贴到桌面 = <b>移动</b>(出盒 + 落到桌面),由我们处理并吞掉 Ctrl+V;
    /// ② 没有(普通文件复制) → 完全交给 explorer,行为不变。
    /// 这样既能让"盒子→桌面"像拖拽一样是移动,又不会误伤系统里正常的复制粘贴。</summary>
    public const string CopiedItemsFormat = "DesktopBox.CopiedItemIds";

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
