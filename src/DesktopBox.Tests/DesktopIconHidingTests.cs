using System.IO;
using DesktopBox.Models;
using DesktopBox.Services;
using DesktopBox.ViewModels;
using FluentAssertions;
using Moq;

namespace DesktopBox.Tests;

/// <summary>桌面图标"按项隐藏"(把已收纳的图标挪出可视区)的回归测试。
/// COM 层(Live 桌面视图)无法在单测里跑,这里用假实现覆盖全部决策逻辑:
/// 只动桌面项、读不到原位置就不动、可还原、可跨重启还原、失败时降级。</summary>
public class DesktopIconHidingTests
{
    private static string DesktopPath(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), name);

    private static string TempStore() =>
        Path.Combine(Path.GetTempPath(), $"dbx_icons_{Guid.NewGuid():N}.json");

    // ---- 位置服务 ----

    private sealed class FakePositions : IDesktopIconPositions
    {
        public readonly Dictionary<string, (int X, int Y)> Positions = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Unreadable = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<IntPtr, string> Handles = new();
        public readonly List<(string Path, int X, int Y)> Moves = new();
        private int _next = 1;

        public bool TryParse(string path, out IntPtr item)
        {
            item = new IntPtr(_next++);
            Handles[item] = path;
            return true;
        }

        public void Free(IntPtr item) { }

        public bool TryGet(IntPtr item, out int x, out int y)
        {
            x = 0;
            y = 0;
            var path = Handles[item];
            if (Unreadable.Contains(path)) return false;
            if (!Positions.TryGetValue(path, out var point)) return false;
            x = point.X;
            y = point.Y;
            return true;
        }

        public bool TrySet(IntPtr item, int x, int y)
        {
            var path = Handles[item];
            Positions[path] = (x, y);
            Moves.Add((path, x, y));
            return true;
        }
    }

    [Fact]
    public void Park_MovesOnlyDesktopItemsAndRemembersTheirPositions()
    {
        var fake = new FakePositions();
        var onDesktop = DesktopPath("a.txt");
        var elsewhere = @"D:\somewhere\b.txt";
        fake.Positions[onDesktop] = (100, 200);
        fake.Positions[elsewhere] = (10, 20);
        var service = new DesktopIconLayoutService(fake, TempStore());

        service.Park(new[] { onDesktop, elsewhere }).Should().Be(1);

        fake.Moves.Should().ContainSingle();
        fake.Moves[0].Path.Should().Be(onDesktop);
        fake.Moves[0].Y.Should().BeGreaterThan(1000, "要挪到可视区之外");
        service.ParkedCount.Should().Be(1);
        service.Park(new[] { onDesktop }).Should().Be(0, "已经挪过的跳过");
    }

    [Fact]
    public void Park_SkipsItemsWhosePositionCannotBeRead()
    {
        // 读不到原位置就绝不挪:宁可"没隐藏",也不能挪了回不来
        var fake = new FakePositions();
        var path = DesktopPath("c.txt");
        fake.Positions[path] = (5, 6);
        fake.Unreadable.Add(path);
        var service = new DesktopIconLayoutService(fake, TempStore());

        service.Park(new[] { path }).Should().Be(0);
        fake.Moves.Should().BeEmpty();
    }

    [Fact]
    public void Restore_PutsTheIconBack_AndTheRecordSurvivesRestart()
    {
        var store = TempStore();
        var fake = new FakePositions();
        var path = DesktopPath("d.txt");
        fake.Positions[path] = (300, 400);

        new DesktopIconLayoutService(fake, store).Park(new[] { path }).Should().Be(1);
        fake.Positions[path].Y.Should().BeGreaterThan(1000);

        // 模拟重启:新实例从 icon-layout.json 读回原位置
        var restarted = new DesktopIconLayoutService(fake, store);
        restarted.ParkedCount.Should().Be(1);
        restarted.RestoreAll().Should().Be(1);

        fake.Positions[path].Should().Be((300, 400));
        restarted.ParkedCount.Should().Be(0);
    }

    [Fact]
    public void IsDesktopItem_AcceptsDesktopAndPublicDesktopOnly()
    {
        DesktopIconLayoutService.IsDesktopItem(DesktopPath("x.txt")).Should().BeTrue();

        var publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        if (!string.IsNullOrEmpty(publicDesktop))
            DesktopIconLayoutService.IsDesktopItem(Path.Combine(publicDesktop, "y.lnk")).Should().BeTrue();

        DesktopIconLayoutService.IsDesktopItem(@"D:\somewhere\z.txt").Should().BeFalse();
        DesktopIconLayoutService.IsDesktopItem("").Should().BeFalse();
        DesktopIconLayoutService.IsDesktopItem("::{645FF040-5081-101B-9F08-00AA002F954E}").Should().BeFalse();
    }

    // ---- ViewModel 集成 ----

    private sealed class FakeLayout : IDesktopIconLayoutService
    {
        public readonly List<string> Parked = new();
        public int ParkResult = 1;
        public int RestoreAllCalls;
        public bool? SystemIconsVisible;

        public int ParkedCount => Parked.Count;
        public int Park(IEnumerable<string> paths) { Parked.AddRange(paths); return ParkResult; }
        public bool Restore(string path) => false;
        public int RestoreAll() { RestoreAllCalls++; return Parked.Count; }
        public void SetSystemIconsVisible(bool visible) => SystemIconsVisible = visible;
    }

    private static MainViewModel NewVm(AppConfig config, Mock<IDesktopIconsService> icons, IDesktopIconLayoutService layout, Mock<IPersistenceService> store)
    {
        var localizer = new Mock<ILocalizerService>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns<string>(k => k);
        return new MainViewModel(store.Object, new DropParserService(), new Mock<IIconExtractorService>().Object,
            new Mock<IOrganizeService>().Object, new Mock<ICategorizerService>().Object,
            icons.Object, localizer.Object, new Mock<IShellChangeNotifierService>().Object, layout);
    }

    private static Mock<IPersistenceService> StoreWith(AppConfig config)
    {
        var store = new Mock<IPersistenceService>();
        store.Setup(s => s.Load()).Returns(config);
        return store;
    }

    [Fact]
    public void HidingDesktopIcons_ParksOnlyCollectedDesktopItems()
    {
        var config = new AppConfig
        {
            Boxes =
            [
                new Box
                {
                    Name = "B",
                    Items =
                    [
                        new BoxItem { Type = ItemType.File, TargetPath = DesktopPath("collected.txt") },
                        new BoxItem { Type = ItemType.File, TargetPath = @"D:\elsewhere\not-on-desktop.txt" },
                        new BoxItem { Type = ItemType.Url, TargetPath = "https://example.com" },
                        new BoxItem { Type = ItemType.SystemIcon, TargetPath = "::{645FF040-5081-101B-9F08-00AA002F954E}" }
                    ]
                }
            ]
        };
        var icons = new Mock<IDesktopIconsService>();
        icons.SetupGet(d => d.AreIconsVisible).Returns(true);
        var layout = new FakeLayout();
        var vm = NewVm(config, icons, layout, StoreWith(config));
        vm.LoadCommand.Execute(null);   // 把配置里的盒子装进 VM(否则没有"已收纳项"可谈)

        vm.SetDesktopIconsVisible(false);

        layout.Parked.Should().ContainSingle();
        layout.Parked[0].Should().Be(DesktopPath("collected.txt"));
        layout.SystemIconsVisible.Should().BeFalse();
        vm.DesktopIconsVisible.Should().BeFalse();
        icons.Verify(d => d.SetVisible(It.IsAny<bool>()), Times.Never, "按项隐藏可用,不该动整层开关");
    }

    [Fact]
    public void WhenPerItemHidingMovesNothing_FallsBackToTheGlobalSwitch()
    {
        var config = new AppConfig
        {
            Boxes = [new Box { Name = "B", Items = [new BoxItem { Type = ItemType.File, TargetPath = DesktopPath("a.txt") }] }]
        };
        var icons = new Mock<IDesktopIconsService>();
        icons.SetupGet(d => d.AreIconsVisible).Returns(true);
        var layout = new FakeLayout { ParkResult = 0 };   // 环境不允许跨进程 COM 的情形
        var vm = NewVm(config, icons, layout, StoreWith(config));
        vm.LoadCommand.Execute(null);

        vm.SetDesktopIconsVisible(false);

        icons.Verify(d => d.SetVisible(false), Times.Once);
        vm.DesktopIconsVisible.Should().BeFalse();
    }

    [Fact]
    public void ShowingDesktopIcons_RestoresPositionsAndGlobalSwitch()
    {
        var config = new AppConfig();
        var icons = new Mock<IDesktopIconsService>();
        icons.SetupGet(d => d.AreIconsVisible).Returns(true);
        var layout = new FakeLayout();
        var vm = NewVm(config, icons, layout, StoreWith(config));

        vm.SetDesktopIconsVisible(false);
        vm.SetDesktopIconsVisible(true);

        layout.RestoreAllCalls.Should().BeGreaterThan(0);
        layout.SystemIconsVisible.Should().BeTrue();
        icons.Verify(d => d.SetVisible(true), Times.Once, "显示时也要把可能存在的整层隐藏撤掉");
        vm.DesktopIconsVisible.Should().BeTrue();
    }

    [Fact]
    public void AddingAnItemWhileHidden_ParksItRightAway()
    {
        var config = new AppConfig();
        var icons = new Mock<IDesktopIconsService>();
        icons.SetupGet(d => d.AreIconsVisible).Returns(true);
        var layout = new FakeLayout();
        var vm = NewVm(config, icons, layout, StoreWith(config));
        vm.SetDesktopIconsVisible(false);
        layout.Parked.Clear();

        var box = new BoxViewModel(new Box { Name = "B" });
        vm.Boxes.Add(box);
        var path = DesktopPath("newly-added.txt");
        vm.AddItemToBox(box, path);

        layout.Parked.Should().ContainSingle().Which.Should().Be(path);
    }
}
