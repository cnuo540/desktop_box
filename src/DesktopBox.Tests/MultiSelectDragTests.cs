using System.IO;
using DesktopBox.Controls;
using DesktopBox.Models;
using FluentAssertions;

namespace DesktopBox.Tests;

/// <summary>多选拖拽:框选一堆之后按住其中一条往外拖,必须整组一起走。
/// 曾经的毛病是"鼠标一按下就把多选塌成单选",于是拖拽载荷里只剩按住的那条 ——
/// 表现就是"框选了半天只拖出来一个"。这里锁住"按下不塌、松手没拖走才塌"。</summary>
public class MultiSelectDragTests
{
    private static string ReadRepositoryFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var segments = new List<string> { dir.FullName };
            segments.AddRange(parts);
            var candidate = Path.Combine(segments.ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate repository file.", Path.Combine(parts));
    }

    // ---- 纯决策 ----

    [Fact]
    public void DeferCollapse_OnlyWhenPressingOneOfSeveralSelected()
    {
        // 多选后按住其中一条 → 先别动选中态(要整组拖)
        SelectionClickPolicy.DeferCollapseOnPress(ctrl: false, pressedIsSelected: true, selectedCount: 3).Should().BeTrue();

        // 只选中这一条 → 照常处理这次点击(即时切换)
        SelectionClickPolicy.DeferCollapseOnPress(ctrl: false, pressedIsSelected: true, selectedCount: 1).Should().BeFalse();

        // 按在没选中的条目上 → 照常单选
        SelectionClickPolicy.DeferCollapseOnPress(ctrl: false, pressedIsSelected: false, selectedCount: 3).Should().BeFalse();

        // Ctrl 是加选/取消,不能推迟
        SelectionClickPolicy.DeferCollapseOnPress(ctrl: true, pressedIsSelected: true, selectedCount: 3).Should().BeFalse();
    }

    // ---- 拖拽载荷 ----

    [Fact]
    public void DragPayload_CarriesEveryDraggedId()
    {
        var items = new List<BoxItem>
        {
            Item("D:\\桌面\\a.txt"),
            Item("D:\\桌面\\b.txt"),
            Item("D:\\桌面\\c.txt")
        };

        var data = ItemDragDrop.CreateDesktopDropData(items);

        ItemDragDrop.ReadDraggedIds(data).Should().Equal(items.Select(i => i.Id));
    }

    // ---- 实现守卫:两处"按下"都必须先问过策略,不能无条件塌陷 ----

    [Fact]
    public void BoxTile_PressDefersCollapseBeforeTouchingSelection()
    {
        var code = ReadRepositoryFile("src", "DesktopBox", "Controls", "ItemTile.xaml.cs");
        var start = code.IndexOf("private void OnTileMouseDown", StringComparison.Ordinal);
        var end = code.IndexOf("private void OnTileMouseMove", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "应该找得到按下处理");
        end.Should().BeGreaterThan(start);

        var handler = code[start..end];
        var policy = handler.IndexOf("SelectionClickPolicy.DeferCollapseOnPress", StringComparison.Ordinal);
        var click = handler.IndexOf("HandleTileClick", StringComparison.Ordinal);
        policy.Should().BeGreaterThanOrEqualTo(0, "按下时要先问策略");
        click.Should().BeGreaterThan(policy, "塌陷/单选必须在'要不要推迟'之后才动手");
    }

    [Fact]
    public void BoxTile_MouseUpCollapsesOnlyWhenNothingWasDragged()
    {
        var code = ReadRepositoryFile("src", "DesktopBox", "Controls", "ItemTile.xaml.cs");
        var start = code.IndexOf("private void OnTileMouseUp", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "应该找得到松手处理");
        var handler = code[start..];

        // 拖过就不再塌陷(拖完保持整组选中),没拖过才按普通单击处理
        handler.Should().Contain("_dragStarted");
        handler.Should().Contain("_collapseOnMouseUp");
        handler.Should().Contain("HandleTileClick");
    }

    [Fact]
    public void DesktopTile_PressDefersCollapseToo()
    {
        var code = ReadRepositoryFile("src", "DesktopBox", "Views", "DesktopLayerWindow.cs");

        code.Should().Contain("SelectionClickPolicy.DeferCollapseOnPress",
            "桌面层按住已选中的小图标时也不能把多选塌掉");
        code.Should().Contain("_pendingCollapseTile");
        code.Should().Contain("ToggleSelection", "Ctrl 单击加选要靠它(以前是死代码)");
    }

    private static BoxItem Item(string path) => new()
    {
        Type = ItemType.File,
        TargetPath = path,
        DisplayName = Path.GetFileName(path)
    };
}
