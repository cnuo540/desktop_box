using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopBox.Controls;
using DesktopBox.Models;
using DesktopBox.Services;
using DesktopBox.ViewModels;
using FluentAssertions;
using Moq;

namespace DesktopBox.Tests;

/// <summary>图标大小切换按钮、标签拖出成独立盒子、盒子拖进盒子合并 的回归测试。</summary>
public class BoxDragMergeTests
{
    // ---- 图标大小循环 ----

    [Theory]
    [InlineData(ViewMode.Large, ViewMode.Medium)]
    [InlineData(ViewMode.Medium, ViewMode.Small)]
    [InlineData(ViewMode.Small, ViewMode.Large)]
    [InlineData(ViewMode.List, ViewMode.Large)]
    [InlineData(ViewMode.Detail, ViewMode.Large)]
    [InlineData(ViewMode.Tile, ViewMode.Large)]
    public void NextIconSize_CyclesLargeMediumSmallAndFallsBackToLarge(ViewMode current, ViewMode expected)
        => ViewModeText.NextIconSize(current).Should().Be(expected);

    [Fact]
    public void ViewModeResourceKeys_MatchTheMenuKeys()
    {
        ViewModeText.ResourceKey(ViewMode.Large).Should().Be("view.large");
        ViewModeText.ResourceKey(ViewMode.Medium).Should().Be("view.medium");
        ViewModeText.ResourceKey(ViewMode.Small).Should().Be("view.small");
        ViewModeText.ResourceKey(ViewMode.List).Should().Be("view.list");
        ViewModeText.ResourceKey(ViewMode.Detail).Should().Be("view.detail");
        ViewModeText.ResourceKey(ViewMode.Tile).Should().Be("view.tile");
        ViewModeText.IsIconSize(ViewMode.Medium).Should().BeTrue();
        ViewModeText.IsIconSize(ViewMode.Tile).Should().BeFalse();
    }

    [Fact]
    public void CycleIconSize_ChangesTheGlobalModeAndBroadcastsToEveryBox()
    {
        var vm = NewVm();
        var box = new BoxViewModel(new Box { Name = "B" });
        vm.Boxes.Add(box);

        vm.GlobalViewMode.Should().Be(ViewMode.Large);

        vm.CycleIconSize();
        vm.GlobalViewMode.Should().Be(ViewMode.Medium);
        box.ViewMode.Should().Be(ViewMode.Medium);   // 所有盒子立即跟随

        vm.CycleIconSize();
        vm.GlobalViewMode.Should().Be(ViewMode.Small);
        box.ViewMode.Should().Be(ViewMode.Small);

        vm.CycleIconSize();
        vm.GlobalViewMode.Should().Be(ViewMode.Large);

        vm.SetGlobalViewMode(ViewMode.List);          // 非图标大小档
        vm.CycleIconSize();
        vm.GlobalViewMode.Should().Be(ViewMode.Large);
    }

    // ---- 拖盒子进盒子:命中测试 ----

    [Fact]
    public void HitTest_FindsTheBoxUnderThePointerAndSkipsTheDraggedOne()
    {
        var a = BoxAt(0, 0, 100, 100);
        var b = BoxAt(200, 200, 100, 100);
        var boxes = new[] { a, b };

        BoxHitTest.FindAt(boxes, 50, 50)!.Id.Should().Be(a.Id);
        BoxHitTest.FindAt(boxes, 250, 250)!.Id.Should().Be(b.Id);
        BoxHitTest.FindAt(boxes, 150, 150).Should().BeNull();
        BoxHitTest.FindAt(boxes, 50, 50, a.Id).Should().BeNull();      // 排除被拖动的自己
    }

    [Fact]
    public void HitTest_PrefersTheSmallestOverlappingBox()
    {
        var big = BoxAt(0, 0, 400, 400);
        var small = BoxAt(100, 100, 100, 100);

        BoxHitTest.FindAt(new[] { big, small }, 150, 150)!.Id.Should().Be(small.Id);
    }

    [Fact]
    public void ClearMergeTargets_RemovesTheHighlightFromEveryBox()
    {
        var a = BoxAt(0, 0, 100, 100);
        var b = BoxAt(200, 0, 100, 100);
        a.IsMergeTarget = true;
        b.IsMergeTarget = true;

        BoxHitTest.ClearMergeTargets(new[] { a, b });

        a.IsMergeTarget.Should().BeFalse();
        b.IsMergeTarget.Should().BeFalse();
    }

    // ---- 拖盒子进盒子:合并 ----

    [Fact]
    public void MergeIn_ExpandsTheTargetAndAppendsAllTabsOfTheDroppedBox()
    {
        var vm = NewVm();
        var target = BoxWithTabs("目标", "甲");
        target.IsCollapsed = true;
        target.IsMergeTarget = true;
        var dragged = BoxWithTabs("被拖的", "乙", "丙");
        vm.Boxes.Add(target);
        vm.Boxes.Add(dragged);

        vm.MergeIn(target, dragged);

        target.Tabs.Should().HaveCount(3);              // 甲 + 乙 + 丙
        target.Tabs.Select(t => t.Name).Should().Contain(new[] { "乙", "丙" });
        target.IsCollapsed.Should().BeFalse();          // 卷起时看不到标签行 → 先展开
        target.IsMergeTarget.Should().BeFalse();        // 高亮必须清掉
        vm.Boxes.Should().ContainSingle();
    }

    // ---- 拖标签到别的盒子 ----

    [Fact]
    public void MoveTabToBox_JoinsTheTargetAndKeepsFileReferencesIntact()
    {
        var vm = NewVm();
        var source = BoxWithTabs("源", "甲", "乙");
        var target = BoxWithTabs("目标", "丙");
        target.IsCollapsed = true;
        vm.Boxes.Add(source);
        vm.Boxes.Add(target);

        var tab = source.Tabs[0];
        var item = new BoxItem { Type = ItemType.File, TargetPath = @"C:\x\a.txt", DisplayName = "a.txt" };
        tab.Items.Add(item);

        vm.MoveTabToBox(source, target, tab).Should().BeTrue();

        source.Tabs.Should().HaveCount(1);
        source.Tabs.Should().NotContain(tab);
        target.Tabs.Should().Contain(tab);
        target.SelectedTab.Should().BeSameAs(tab);
        target.IsCollapsed.Should().BeFalse();
        item.BoxId.Should().Be(target.Id);              // 引用归属跟着走
        item.TargetPath.Should().Be(@"C:\x\a.txt");     // 只搬引用,路径不变
    }

    [Fact]
    public void MoveTabToBox_RefusesTheLastTabAndSameBox()
    {
        var vm = NewVm();
        var source = BoxWithTabs("源", "甲");
        var target = BoxWithTabs("目标", "乙");
        vm.Boxes.Add(source);
        vm.Boxes.Add(target);

        vm.MoveTabToBox(source, target, source.Tabs[0]).Should().BeFalse();   // 最后一个标签留下
        vm.MoveTabToBox(target, target, target.Tabs[0]).Should().BeFalse();   // 同一个盒子不算
        source.Tabs.Should().HaveCount(1);
        target.Tabs.Should().HaveCount(1);
    }

    // ---- 拖标签出盒:独立成新盒子 ----

    [Fact]
    public void SplitTabToNewBox_PutsTheTabIntoAStandaloneBoxAtTheDropPoint()
    {
        var vm = NewVm();
        var source = BoxWithTabs("源盒子", "标签甲", "标签乙");
        source.Width = 300;
        source.Height = 260;
        vm.Boxes.Add(source);
        var tab = source.Tabs[0];
        var item = new BoxItem { Type = ItemType.File, TargetPath = @"C:\x\a.txt", DisplayName = "a.txt" };
        tab.Items.Add(item);

        vm.SplitTabToNewBox(source, tab, 400, 300).Should().BeTrue();

        source.Tabs.Should().HaveCount(1);
        source.Tabs.Should().NotContain(tab);

        vm.Boxes.Should().HaveCount(2);
        var created = vm.Boxes.Single(b => b.Id != source.Id);
        created.Name.Should().Be("标签甲");
        created.X.Should().Be(400);
        created.Y.Should().Be(300);
        created.Width.Should().Be(300);
        created.Height.Should().Be(260);
        created.Items.Should().ContainSingle();
        created.Items[0].TargetPath.Should().Be(@"C:\x\a.txt");
        item.BoxId.Should().Be(created.Id);
    }

    [Fact]
    public void SplitTabToNewBox_RefusesWhenOnlyOneTabIsLeft()
    {
        var vm = NewVm();
        var source = BoxWithTabs("源盒子", "唯一标签");
        vm.Boxes.Add(source);

        vm.SplitTabToNewBox(source, source.Tabs[0], 100, 100).Should().BeFalse();

        vm.Boxes.Should().ContainSingle();
        source.Tabs.Should().HaveCount(1);
    }

    [Fact]
    public void SplitTabToNewBox_ClampsTheDropPointIntoScreen()
    {
        var vm = NewVm();
        var source = BoxWithTabs("源盒子", "甲", "乙");
        vm.Boxes.Add(source);

        vm.SplitTabToNewBox(source, source.Tabs[0], 99999, 99999).Should().BeTrue();

        var created = vm.Boxes.Single(b => b.Id != source.Id);
        created.X.Should().BeLessThan(99999);
        created.Y.Should().BeLessThan(99999);
    }

    // ---- 拖放接收层 ----

    [Fact]
    public void DropOverlay_AcceptsItemAndTabPayloadsOnly()
    {
        RunOnSta(() =>
        {
            var item = new DataObject();
            item.SetData(ItemDragDrop.DragSourceItemFormat, Guid.NewGuid());
            DesktopDropOverlay.IsSupportedDrag(item).Should().BeTrue();

            var tab = new DataObject();
            tab.SetData(ItemDragDrop.DragSourceTabFormat, Guid.NewGuid());
            DesktopDropOverlay.IsSupportedDrag(tab).Should().BeTrue();

            var unrelated = new DataObject();
            unrelated.SetData(DataFormats.Text, "hello");
            DesktopDropOverlay.IsSupportedDrag(unrelated).Should().BeFalse();
            DesktopDropOverlay.IsSupportedDrag(null).Should().BeFalse();
        });
    }

    // ---- 合并目标高亮(视觉提示) ----

    [Fact]
    public void MergeTargetHighlight_TurnsTheCardBorderIntoTheAccentColor()
    {
        RunOnSta(() =>
        {
            var vm = new BoxViewModel(new Box { Name = "B", Width = 300, Height = 200 });
            var control = new BoxControl { DataContext = vm, Width = 300, Height = 200 };
            Arrange(control);

            var card = (Border)control.FindName("Card")!;
            card.BorderThickness.Left.Should().Be(1);

            vm.IsMergeTarget = true;

            card.BorderThickness.Left.Should().Be(2);
            ((SolidColorBrush)card.BorderBrush).Color.Should().Be(Color.FromRgb(0x4F, 0xC3, 0xF7));
        });
    }

    // ---- 接线守卫 ----

    [Fact]
    public void IconSizeButton_SitsRightOfTheLockButtonAndCyclesTheGlobalMode()
    {
        var xaml = ReadRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml");

        xaml.Should().Contain("x:Name=\"IconSizeBtn\"");
        xaml.Should().Contain("Click=\"OnCycleIconSize\"");
        xaml.Should().Contain("ToolTip=\"{Binding IconSizeActionText}\"");
        xaml.IndexOf("x:Name=\"IconSizeBtn\"", StringComparison.Ordinal)
            .Should().BeGreaterThan(xaml.IndexOf("x:Name=\"LockBtn\"", StringComparison.Ordinal));
        xaml.IndexOf("x:Name=\"IconSizeBtn\"", StringComparison.Ordinal)
            .Should().BeLessThan(xaml.IndexOf("x:Name=\"CollapseBtn\"", StringComparison.Ordinal));
        xaml.Should().Contain("{x:Static m:ViewMode.Medium}");   // 图标随当前档位变化
        xaml.Should().Contain("{x:Static m:ViewMode.Small}");

        var code = ReadRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml.cs");
        code.Should().Contain("private void OnCycleIconSize");
        code.Should().Contain("MainVm.CycleIconSize()");
    }

    [Fact]
    public void TabDragOut_UsesTheDesktopDropOverlayAndSplitsTheTabIntoANewBox()
    {
        var code = ReadRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml.cs");
        code.Should().Contain("ItemDragDrop.DragSourceTabFormat");
        code.Should().Contain("DesktopDropOverlay.TryCreate(boxBounds)");
        code.Should().Contain("SplitTabToNewBox(Vm, tab, drop.X, drop.Y)");

        var overlay = ReadRepositoryFile("src", "DesktopBox", "Controls", "DesktopDropOverlay.cs");
        overlay.Should().Contain("DragSourceTabFormat");
        overlay.Should().Contain("DropScreenPosition");
    }

    [Fact]
    public void DraggingABoxOntoAnotherBox_MergesItOnRelease()
    {
        var code = ReadRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml.cs");
        code.Should().Contain("UpdateMergeTarget(pos.X, pos.Y)");
        code.Should().Contain("BoxHitTest.FindAt(MainVm.Boxes, screenX, screenY, Vm.Id)");
        code.Should().Contain("MainVm.MergeIn(target, Vm)");
        code.Should().Contain("ClearMergeTarget()");

        var xaml = ReadRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml");
        xaml.Should().Contain("Binding=\"{Binding IsMergeTarget}\"");   // 合并目标高亮
    }

    [Fact]
    public void DroppingATabOnAnotherBox_JoinsThatBox()
    {
        var code = ReadRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml.cs");
        code.Should().Contain("TryAcceptForeignTab");
        code.Should().Contain("MainVm.MoveTabToBox(source, Vm, dragged)");
    }

    [Fact]
    public void Strings_DefineTheIconSizeTooltipForEveryLanguage()
    {
        foreach (var file in new[] { "Strings.zh-CN.xaml", "Strings.en-US.xaml" })
            ReadRepositoryFile("src", "DesktopBox", "Resources", file).Should().Contain("x:Key=\"tip.iconSize\"");
    }

    // ---- 辅助 ----

    private readonly Mock<IPersistenceService> _store = new();
    private readonly Mock<IIconExtractorService> _icon = new();
    private readonly Mock<IOrganizeService> _organize = new();
    private readonly Mock<IDesktopIconsService> _desktopIcons = new();
    private readonly Mock<ILocalizerService> _localizer = new();
    private readonly Mock<IShellChangeNotifierService> _shellChange = new();
    private readonly Mock<ICategorizerService> _categorizer = new();

    private MainViewModel NewVm()
    {
        _store.Reset();
        _icon.Reset();
        _organize.Reset();
        _desktopIcons.Reset();
        _localizer.Reset();
        _shellChange.Reset();
        _categorizer.Reset();
        _localizer.Setup(l => l[It.IsAny<string>()]).Returns<string>(k => k);
        _store.Setup(s => s.Load()).Returns(new AppConfig());
        _icon.Setup(i => i.Extract(It.IsAny<string>())).Returns((string?)null);
        return new MainViewModel(_store.Object, new DropParserService(), _icon.Object, _organize.Object,
            _categorizer.Object, _desktopIcons.Object, _localizer.Object, _shellChange.Object);
    }

    private static BoxViewModel BoxAt(double x, double y, double w, double h) =>
        new(new Box { Name = "B", X = x, Y = y, Width = w, Height = h });

    private static BoxViewModel BoxWithTabs(string name, params string[] tabs)
    {
        var box = new Box { Name = name, Width = 240, Height = 200 };
        foreach (var tab in tabs) box.Tabs.Add(new BoxTab { Name = tab });
        return new BoxViewModel(box);
    }

    private static void Arrange(FrameworkElement element)
    {
        var available = new Size(element.Width, element.Height);
        element.Measure(available);
        element.Arrange(new Rect(available));
        element.UpdateLayout();
    }

    private static string ReadRepositoryFile(params string[] parts) =>
        File.ReadAllText(FindRepositoryFile(parts));

    private static string FindRepositoryFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var segments = new List<string> { dir.FullName };
            segments.AddRange(parts);
            var candidate = Path.Combine(segments.ToArray());
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate repository file.", Path.Combine(parts));
    }

    private static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        exception?.Throw();
    }
}
