using System.IO;
using DesktopBox.Models;
using DesktopBox.Services;
using DesktopBox.ViewModels;
using FluentAssertions;
using Moq;

namespace DesktopBox.Tests;

/// <summary>桌面图标显隐的回归测试。
///
/// 现在的机制是「关掉 explorer 的桌面图标层 + 自绘桌面层」(对齐 Coodesker 的 _cls_desk_):
/// 隐藏时只动图标层,<b>绝不碰文件属性</b>;桌面上只画"没进盒子"的项。
/// 窗口/注册表那层没法在单测里跑,这里用假实现覆盖决策逻辑。</summary>
public class DesktopIconHidingTests
{
    private static string DesktopPath(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), name);

    private static string TempStore() =>
        Path.Combine(Path.GetTempPath(), $"dbx_icons_{Guid.NewGuid():N}.json");

    // ---- 位置服务(现在只用于把旧版遗留在屏幕外的图标拉回来)----

    private sealed class FakePositions : IDesktopIconPositions
    {
        public readonly Dictionary<string, (int X, int Y)> Positions = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<IntPtr, string> Handles = new();
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
            if (!Positions.TryGetValue(Handles[item], out var point)) return false;
            x = point.X;
            y = point.Y;
            return true;
        }

        public bool TrySet(IntPtr item, int x, int y)
        {
            Positions[Handles[item]] = (x, y);
            return true;
        }

        public int TrySetMany(IReadOnlyList<(IntPtr Item, int X, int Y)> items)
        {
            var n = 0;
            foreach (var (item, x, y) in items)
            {
                if (TrySet(item, x, y)) n++;
            }
            return n;
        }
    }

    [Fact]
    public void Restore_PullsBackLegacyItemsLeftOutsideTheViewport()
    {
        // 更早的版本把图标挪到屏幕外来"藏";记录可能已经丢了,所以按坐标本身判断并拉回可视区。
        var fake = new FakePositions();
        var path = DesktopPath("legacy-bandizip.lnk");
        fake.Positions[path] = (80, 100000);
        var service = new DesktopIconLayoutService(fake, TempStore());

        service.Restore(path).Should().BeTrue();
        fake.Positions[path].Y.Should().Be(24);
    }

    [Fact]
    public void Restore_ClearsLegacyHiddenAttribute_WhenNoSnapshotExists()
    {
        // 上一版"按属性隐藏"给桌面上项加了 Hidden;迁移时没留下快照也要清掉。
        var path = Path.Combine(Path.GetTempPath(), $"dbx-hidden-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(path, "test");
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
            var service = new DesktopIconLayoutService(new FakePositions(), TempStore());

            service.Restore(path).Should().BeTrue();

            (File.GetAttributes(path) & FileAttributes.Hidden).Should().Be(0);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
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

    // ---- ViewModel ----

    private sealed class FakeLayout : IDesktopIconLayoutService
    {
        public readonly List<string> Restored = new();
        public int RestoreAllCalls;
        public bool? ShowHidden;
        public bool? SystemIconsVisible;

        public bool Restore(string path) { Restored.Add(path); return true; }
        public int RestoreAll() { RestoreAllCalls++; Restored.Clear(); return 0; }
        public void SetExplorerShowHidden(bool show) => ShowHidden = show;
        public void SetSystemIconsVisible(bool visible) => SystemIconsVisible = visible;
    }

    private sealed class FakeScanner : IDesktopScannerService
    {
        public readonly List<string> Items = new();
        public List<string> ScanDesktop() => Items.ToList();
    }

    private static MainViewModel NewVm(Mock<IDesktopIconsService> icons, IDesktopIconLayoutService layout,
        Mock<IPersistenceService> store, IDesktopScannerService? scanner = null)
    {
        var localizer = new Mock<ILocalizerService>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns<string>(k => k);
        return new MainViewModel(store.Object, new DropParserService(), new Mock<IIconExtractorService>().Object,
            new Mock<IOrganizeService>().Object, new Mock<ICategorizerService>().Object,
            icons.Object, localizer.Object, new Mock<IShellChangeNotifierService>().Object, layout,
            null, scanner ?? new FakeScanner());
    }

    private static Mock<IPersistenceService> StoreWith(AppConfig config)
    {
        var store = new Mock<IPersistenceService>();
        store.Setup(s => s.Load()).Returns(config);
        return store;
    }

    private static AppConfig ConfigWithBoxItems(params string[] paths) => new()
    {
        Boxes =
        [
            new Box
            {
                Name = "B",
                Items = paths.Select(p => new BoxItem
                {
                    Type = ItemType.File,
                    TargetPath = p,
                    DisplayName = Path.GetFileName(p)
                }).ToList()
            }
        ]
    };

    private static Mock<IDesktopIconsService> VisibleIcons()
    {
        var icons = new Mock<IDesktopIconsService>();
        icons.SetupGet(d => d.AreIconsVisible).Returns(true);
        return icons;
    }

    [Fact]
    public void Hiding_TurnsOffTheNativeIconLayer_AndReturnsLegacyAttributes()
    {
        var config = ConfigWithBoxItems(DesktopPath("a.txt"));
        var icons = VisibleIcons();
        var layout = new FakeLayout();
        var vm = NewVm(icons, layout, StoreWith(config));

        vm.LoadCommand.Execute(null);
        vm.SetDesktopIconsVisible(false);

        icons.Verify(d => d.SetVisible(false), Times.Once);
        layout.RestoreAllCalls.Should().Be(1, "旧版\"按属性隐藏\"留下的属性要还回去");
        layout.ShowHidden.Should().BeTrue("并还原\"显示隐藏项目\"");
        layout.SystemIconsVisible.Should().BeFalse();
        vm.DesktopIconsVisible.Should().BeFalse();
    }

    [Fact]
    public void Hiding_DoesNotTouchFileAttributesAtAll()
    {
        // 新机制一个文件属性都不改 —— 目录窗口里的显示状态必须和原来一模一样
        var config = ConfigWithBoxItems(DesktopPath("a.txt"));
        var layout = new FakeLayout();
        var vm = NewVm(VisibleIcons(), layout, StoreWith(config));

        vm.LoadCommand.Execute(null);
        vm.SetDesktopIconsVisible(false);

        layout.Restored.Should().BeEmpty();
    }

    [Fact]
    public void Showing_TurnsTheNativeIconLayerBackOn()
    {
        var config = ConfigWithBoxItems(DesktopPath("a.txt"));
        var icons = VisibleIcons();
        var layout = new FakeLayout();
        var vm = NewVm(icons, layout, StoreWith(config));

        vm.LoadCommand.Execute(null);
        vm.SetDesktopIconsVisible(false);
        vm.SetDesktopIconsVisible(true);

        icons.Verify(d => d.SetVisible(true), Times.AtLeastOnce);
        layout.SystemIconsVisible.Should().BeTrue();
        vm.DesktopIconsVisible.Should().BeTrue();
    }

    [Fact]
    public void Startup_AppliesTheConfiguredPreference()
    {
        var config = ConfigWithBoxItems(DesktopPath("a.txt"));
        config.Settings.HideCollectedDesktopIcons = true;
        var icons = VisibleIcons();
        var vm = NewVm(icons, new FakeLayout(), StoreWith(config));

        vm.LoadCommand.Execute(null);
        vm.RefreshDesktopIconsState();

        icons.Verify(d => d.SetVisible(false), Times.Once);
        vm.DesktopIconsVisible.Should().BeFalse();
    }

    [Fact]
    public void Startup_ShowsIcons_WhenThePreferenceSaysSo()
    {
        var config = ConfigWithBoxItems(DesktopPath("a.txt"));
        config.Settings.HideCollectedDesktopIcons = false;
        var icons = new Mock<IDesktopIconsService>();
        icons.SetupGet(d => d.AreIconsVisible).Returns(false);      // 上次留下的隐藏状态
        var vm = NewVm(icons, new FakeLayout(), StoreWith(config));

        vm.LoadCommand.Execute(null);
        vm.RefreshDesktopIconsState();

        icons.Verify(d => d.SetVisible(true), Times.Once);
        vm.DesktopIconsVisible.Should().BeTrue();
    }

    [Fact]
    public void LooseDesktopItems_SkipsWhatIsAlreadyInBoxes()
    {
        var inBox = DesktopPath("collected.txt");
        var loose = DesktopPath("loose.txt");
        var scanner = new FakeScanner();
        scanner.Items.AddRange(new[] { inBox, loose });

        var vm = NewVm(VisibleIcons(), new FakeLayout(), StoreWith(ConfigWithBoxItems(inBox)), scanner);
        vm.LoadCommand.Execute(null);

        vm.LooseDesktopItems().Should().BeEquivalentTo(new[] { loose },
            "已收纳的项只在盒子里出现,桌面层不该再画一遍");
    }

    [Fact]
    public void DraggingAnItemOutToTheDesktop_MakesItLooseAgain()
    {
        var gone = Path.Combine(Path.GetTempPath(), $"dbx-out-{Guid.NewGuid():N}.lnk");
        var vm = NewVm(VisibleIcons(), new FakeLayout(), StoreWith(ConfigWithBoxItems(gone)));
        vm.LoadCommand.Execute(null);

        var fired = 0;
        vm.LooseItemsChanged += (_, _) => fired++;

        vm.HandleItemDroppedOnDesktop(vm.Boxes[0].Items[0]);

        vm.Boxes[0].Items.Should().BeEmpty();
        fired.Should().BeGreaterThan(0, "得让桌面层重画,它才会出现在桌面上");
    }

    [Fact]
    public void RenamingAnItem_RenamesTheRealFile_AndRepointsTheItem()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dbx-rename-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var old = Path.Combine(dir, "old.txt");
        File.WriteAllText(old, "x");
        try
        {
            var vm = NewVm(VisibleIcons(), new FakeLayout(), StoreWith(ConfigWithBoxItems(old)));
            vm.LoadCommand.Execute(null);
            var item = vm.Boxes[0].Items[0];

            vm.RenameItem(item, "new.txt").Should().BeTrue();

            File.Exists(Path.Combine(dir, "new.txt")).Should().BeTrue("改的是真实文件名");
            File.Exists(old).Should().BeFalse();
            item.TargetPath.Should().Be(Path.Combine(dir, "new.txt"));
            item.DisplayName.Should().Be("new.txt");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void RenamingAnItem_RefusesAnExistingName()
    {
        // 失败路径会弹提示框 → 必须在 STA 线程上跑
        RunOnSta(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"dbx-rename-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            var first = Path.Combine(dir, "a.txt");
            var second = Path.Combine(dir, "b.txt");
            File.WriteAllText(first, "a");
            File.WriteAllText(second, "b");
            try
            {
                var vm = NewVm(VisibleIcons(), new FakeLayout(), StoreWith(ConfigWithBoxItems(first)));
                vm.LoadCommand.Execute(null);

                vm.RenameItem(vm.Boxes[0].Items[0], "b.txt").Should().BeFalse("同名文件已存在,不该覆盖");
                File.Exists(first).Should().BeTrue();
                File.Exists(second).Should().BeTrue();
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        });
    }

    [Fact]
    public void RenamingAnItem_NotifiesTheDisplayedName()
    {
        // 界面文本绑的是 DisplayName:改名(改了真实文件名)之后它必须通知刷新,
        // 否则会出现"文件已经改了、盒子里还是旧名字"。
        var dir = Path.Combine(Path.GetTempPath(), $"dbx-rename-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var old = Path.Combine(dir, "old.txt");
        File.WriteAllText(old, "x");
        try
        {
            var vm = NewVm(VisibleIcons(), new FakeLayout(), StoreWith(ConfigWithBoxItems(old)));
            vm.LoadCommand.Execute(null);
            var item = vm.Boxes[0].Items[0];

            var notified = new List<string?>();
            item.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

            vm.RenameItem(item, "new.txt").Should().BeTrue();

            notified.Should().Contain(nameof(BoxItem.DisplayName), "界面上的名字要跟着刷新");
            item.DisplayName.Should().Be("new.txt");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void MovingAnItemWithinABox_ReordersIt()
    {
        var vm = NewVm(VisibleIcons(), new FakeLayout(),
            StoreWith(ConfigWithBoxItems("a", "b", "c")));
        vm.LoadCommand.Execute(null);
        var items = vm.Boxes[0].Items;

        // 落点在 c 上 → 把 a 插到 c 前面(即挪到索引 1)
        vm.MoveItemToIndex(items[0], 2).Should().BeTrue();

        items.Select(i => Path.GetFileName(i.TargetPath)).Should().Equal("b", "a", "c");
    }

    [Fact]
    public void MovingAnItemToItsOwnPlace_DoesNothing()
    {
        var vm = NewVm(VisibleIcons(), new FakeLayout(),
            StoreWith(ConfigWithBoxItems("a", "b", "c")));
        vm.LoadCommand.Execute(null);
        var items = vm.Boxes[0].Items;

        vm.MoveItemToIndex(items[1], 1).Should().BeFalse();
        vm.MoveItemToIndex(items[1], 2).Should().BeFalse();   // 插到自己后面 = 原地
        items.Select(i => Path.GetFileName(i.TargetPath)).Should().Equal("a", "b", "c");
    }

    private static void RunOnSta(Action action)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        exception?.Throw();
    }

    [Fact]
    public void RemovingAnItemFromABox_MakesItLooseAgain()
    {
        var vm = NewVm(VisibleIcons(), new FakeLayout(), StoreWith(ConfigWithBoxItems(DesktopPath("keep.txt"))));
        vm.LoadCommand.Execute(null);

        var fired = 0;
        vm.LooseItemsChanged += (_, _) => fired++;

        vm.RemoveItemAnywhere(vm.Boxes[0].Items[0]);

        fired.Should().BeGreaterThan(0);
    }
}
