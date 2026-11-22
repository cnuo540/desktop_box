using System.IO;
using DesktopBox.Models;
using DesktopBox.Services;
using DesktopBox.ViewModels;
using FluentAssertions;
using Moq;

namespace DesktopBox.Tests;

/// <summary>v1.7.37 桌面变化分流的回归测试。
///
/// 三条主行为:新增 → 自动收纳;删除 → 撤掉盒子里的引用;改名 → 引用跟着改到新路径。
/// 外加自动收纳的排除规则(开关/图标可见/临时文件/刚拖过)与启动时的临时文件残留清理。</summary>
public class DesktopChangeHandlingTests
{
    // ---- 假实现 ----

    private sealed class FakeWatcher : IDesktopWatcherService
    {
        public event EventHandler<DesktopItemChange>? DesktopItemChanged;
        public void Raise(DesktopItemChange change) => DesktopItemChanged?.Invoke(this, change);
        public void Dispose() { }
    }

    private sealed class FakeScanner : IDesktopScannerService
    {
        public List<string> Items { get; } = new();
        public List<string> ScanDesktop() => Items.ToList();
    }

    private sealed class FakeLayout : IDesktopIconLayoutService
    {
        public int RestoreAll() => 0;
        public bool Restore(string path) => false;
        public void SetExplorerShowHidden(bool show) { }
        public void SetSystemIconsVisible(bool visible) { }
    }

    private static string DesktopPath(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), name);

    private static string TempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dbx-new-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "x");
        return path;
    }

    private static MainViewModel NewVm(AppConfig config, FakeWatcher watcher,
        ICategorizerService? categorizer = null, IDesktopScannerService? scanner = null)
    {
        var store = new Mock<IPersistenceService>();
        store.Setup(s => s.Load()).Returns(config);
        var localizer = new Mock<ILocalizerService>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns<string>(k => k);
        var icons = new Mock<IDesktopIconsService>();
        icons.SetupGet(d => d.AreIconsVisible).Returns(false);

        var categorizerMock = categorizer is null ? new Mock<ICategorizerService>() : null;
        categorizerMock?.Setup(c => c.Categorize(It.IsAny<string>())).Returns("cat.docs");

        return new MainViewModel(store.Object, new DropParserService(),
            new Mock<IIconExtractorService>().Object,
            new Mock<IOrganizeService>().Object,
            categorizer ?? categorizerMock!.Object,
            icons.Object, localizer.Object,
            new Mock<IShellChangeNotifierService>().Object,
            new FakeLayout(), watcher, scanner ?? new FakeScanner());
    }

    private static AppConfig EmptyConfig() => new();

    private static AppConfig ConfigWithItem(string path) => new()
    {
        Boxes =
        [
            new Box
            {
                Name = "B",
                Items = [new BoxItem { Type = ItemType.File, TargetPath = path, DisplayName = Path.GetFileName(path) }]
            }
        ]
    };

    /// <summary>建好 VM 并把"桌面图标隐藏"这个前置条件摆到位(自动收纳只在图标隐藏时生效)。</summary>
    private static MainViewModel LoadedVm(AppConfig config, FakeWatcher watcher,
        ICategorizerService? categorizer = null, IDesktopScannerService? scanner = null)
    {
        var vm = NewVm(config, watcher, categorizer, scanner);
        vm.LoadCommand.Execute(null);
        vm.DesktopIconsVisible = false;
        return vm;
    }

    [Fact]
    public void Created_CollectsNewFileIntoTheOrganizeBox()
    {
        var file = TempFile();
        try
        {
            var watcher = new FakeWatcher();
            var vm = LoadedVm(EmptyConfig(), watcher);

            watcher.Raise(new DesktopItemChange(DesktopChangeKind.Created, file));

            var box = vm.Boxes.Single(b => b.Key == "box.organize");
            box.Tabs.Should().ContainSingle();
            box.Tabs[0].Items.Should().ContainSingle(i => i.TargetPath == file);
            vm.DesktopIconsVisible.Should().BeFalse();
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Created_IsIgnored_WhenNativeIconsAreVisible()
    {
        // 图标可见 = 用户在用原生桌面,此时收纳会让"桌面和盒子各显示一次"
        var file = TempFile();
        try
        {
            var watcher = new FakeWatcher();
            var vm = LoadedVm(EmptyConfig(), watcher);
            vm.DesktopIconsVisible = true;

            watcher.Raise(new DesktopItemChange(DesktopChangeKind.Created, file));

            vm.Boxes.Should().BeEmpty();
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Created_IsIgnored_WhenTheSettingIsOff()
    {
        var file = TempFile();
        try
        {
            var config = EmptyConfig();
            config.Settings.AutoCollectNewDesktopFiles = false;
            var watcher = new FakeWatcher();
            var vm = LoadedVm(config, watcher);

            watcher.Raise(new DesktopItemChange(DesktopChangeKind.Created, file));

            vm.Boxes.Should().BeEmpty();
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Created_IsIgnored_ForTransientFiles()
    {
        // ~$ 锁文件即使正在存在也不收 —— 收了只会在 Excel 关闭后变成"目标当前不可用"
        var watcher = new FakeWatcher();
        var vm = LoadedVm(EmptyConfig(), watcher);
        var lockFile = DesktopPath($"~$dbx-{Guid.NewGuid():N}.xlsx");

        watcher.Raise(new DesktopItemChange(DesktopChangeKind.Created, lockFile));

        vm.Boxes.Should().BeEmpty();
    }

    [Fact]
    public void Created_IsIgnored_WhenTheUserJustDraggedSomething()
    {
        // 从资源管理器拖到桌面 = 有意放在桌面,不该被抽进盒子
        var file = TempFile();
        try
        {
            var watcher = new FakeWatcher();
            var vm = LoadedVm(EmptyConfig(), watcher);
            GlobalDragTracker.MarkDragEnd(100, 200);
            try
            {
                watcher.Raise(new DesktopItemChange(DesktopChangeKind.Created, file));

                vm.Boxes.Should().BeEmpty("刚结束过一次拖动,视为用户主动放置");
            }
            finally
            {
                GlobalDragTracker.Reset();
            }
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Deleted_RemovesTheReferenceFromTheBox()
    {
        var path = DesktopPath($"dbx-deleted-{Guid.NewGuid():N}.txt");
        var watcher = new FakeWatcher();
        var vm = LoadedVm(ConfigWithItem(path), watcher);

        watcher.Raise(new DesktopItemChange(DesktopChangeKind.Deleted, path));

        // 只撤引用 —— 文件本来就不在了,留着点开就是"目标当前不可用"
        vm.Boxes[0].Items.Should().BeEmpty();
    }

    [Fact]
    public void Deleted_LeavesReferencesToOtherPathsAlone()
    {
        var gone = DesktopPath($"dbx-gone-{Guid.NewGuid():N}.txt");
        var kept = DesktopPath($"dbx-kept-{Guid.NewGuid():N}.txt");
        var config = new AppConfig
        {
            Boxes =
            [
                new Box
                {
                    Name = "B",
                    Items =
                    [
                        new BoxItem { Type = ItemType.File, TargetPath = gone, DisplayName = "gone" },
                        new BoxItem { Type = ItemType.File, TargetPath = kept, DisplayName = "kept" }
                    ]
                }
            ]
        };
        var watcher = new FakeWatcher();
        var vm = LoadedVm(config, watcher);

        watcher.Raise(new DesktopItemChange(DesktopChangeKind.Deleted, gone));

        vm.Boxes[0].Items.Should().ContainSingle(i => i.TargetPath == kept);
    }

    [Fact]
    public void Renamed_KeepsTheReferencePointingAtTheNewPath()
    {
        // 桌面上改名后引用不跟着改,点开就是"目标当前不可用" —— 和删除是同一族问题
        var oldPath = DesktopPath($"dbx-old-{Guid.NewGuid():N}.txt");
        var newPath = DesktopPath($"dbx-new-{Guid.NewGuid():N}.txt");
        var watcher = new FakeWatcher();
        var vm = LoadedVm(ConfigWithItem(oldPath), watcher);

        watcher.Raise(new DesktopItemChange(DesktopChangeKind.Renamed, newPath, oldPath));

        var item = vm.Boxes[0].Items.Should().ContainSingle().Subject;
        item.TargetPath.Should().Be(newPath);
        item.DisplayName.Should().Be(Path.GetFileName(newPath), "显示名也要跟着换");
        item.IconCachePath.Should().BeNull("图标要按新路径重取");
    }

    [Fact]
    public void Startup_CleansUpTransientLeftovers_ButKeepsOrdinaryReferences()
    {
        // 旧版本把 ~$ 锁文件收进了盒子 → 启动清掉;
        // 普通文件的失效引用按设计保留(由手动「刷新失效图标」处理),不能被启动逻辑顺手删了。
        var transient = DesktopPath($"~$dbx-leftover-{Guid.NewGuid():N}.xlsx");
        var ordinary = DesktopPath($"dbx-ordinary-{Guid.NewGuid():N}.txt");
        var config = new AppConfig
        {
            Boxes =
            [
                new Box
                {
                    Name = "B",
                    Items =
                    [
                        new BoxItem { Type = ItemType.File, TargetPath = transient, DisplayName = "~$" },
                        new BoxItem { Type = ItemType.File, TargetPath = ordinary, DisplayName = "ordinary" }
                    ]
                }
            ]
        };
        var watcher = new FakeWatcher();

        var vm = NewVm(config, watcher);
        vm.LoadCommand.Execute(null);

        vm.Boxes[0].Items.Should().ContainSingle(i => i.TargetPath == ordinary,
            "只有临时文件的残留被清,普通引用必须留着");
    }

    // ---- 源码守卫(锁住这几处改动,防止被改回去)----

    [Fact]
    public void Overlay_PunchesHolesForForeignWindowsSoDragsReachThem()
    {
        var overlay = ReadRepo("src", "DesktopBox", "Controls", "DesktopDropOverlay.cs");

        // 不挖外部窗口的话,HWND_TOPMOST + 铺满全屏的覆盖层会把"拖给微信"的文件当成"拖到桌面"截走
        overlay.Should().Contain("ForeignWindowsAboveDesktop");
        overlay.Should().Contain("EnumWindows");
        overlay.Should().Contain("GetAncestor");
        overlay.Should().Contain("DwmApi.IsCloaked");
        // 桌面本身与自己进程的窗口不能挖
        overlay.Should().Contain("\"Progman\" or \"WorkerW\"");
        overlay.Should().Contain("pid == ourPid");
    }

    [Fact]
    public void MainViewModel_HandlesAllThreeDesktopChangeKinds()
    {
        var vm = ReadRepo("src", "DesktopBox", "ViewModels", "MainViewModel.cs");

        vm.Should().Contain("DesktopChangeKind.Created");
        vm.Should().Contain("TryAutoCollect(");
        vm.Should().Contain("DesktopChangeKind.Deleted");
        vm.Should().Contain("RemoveReferencesTo(");
        vm.Should().Contain("DesktopChangeKind.Renamed");
        vm.Should().Contain("UpdateReferencePath(");
        vm.Should().Contain("GlobalDragTracker.JustEnded()");
        // 自动收纳的开关与前置条件
        vm.Should().Contain("if (DesktopIconsVisible) return false;");
        vm.Should().Contain("AutoCollectNewDesktopFiles");
    }

    [Fact]
    public void Settings_ExposeTheAutoCollectSwitch()
    {
        var settings = ReadRepo("src", "DesktopBox", "Views", "SettingsWindow.xaml");
        var viewModel = ReadRepo("src", "DesktopBox", "ViewModels", "SettingsViewModel.cs");
        var model = ReadRepo("src", "DesktopBox", "Models", "AppSettings.cs");

        settings.Should().Contain("settings.autoCollect");
        settings.Should().Contain("IsChecked=\"{Binding AutoCollectNewDesktopFiles}\"");
        viewModel.Should().Contain("public bool AutoCollectNewDesktopFiles");
        model.Should().Contain("AutoCollectNewDesktopFiles { get; set; } = true");
    }

    [Fact]
    public void DoubleClickLaunch_ClearsTheSelection()
    {
        var tile = ReadRepo("src", "DesktopBox", "Controls", "ItemTile.xaml.cs");
        var layer = ReadRepo("src", "DesktopBox", "Views", "DesktopLayerWindow.cs");

        tile.Should().Contain("ClearSelectionAfterOpen()");
        // 打开失败时不清:用户还要看着它排查
        tile.Should().Contain("ClearSelectionAfterOpen();");
        layer.Should().Contain("if (Open(path)) ClearSelection();");
    }

    [Fact]
    public void DeleteFailure_ReportsTheReasonToTheUser_NotJustToTheLog()
    {
        var vm = ReadRepo("src", "DesktopBox", "ViewModels", "MainViewModel.cs");
        var shell = ReadRepo("src", "DesktopBox", "Native", "Shell32.cs");

        vm.Should().Contain("DescribeDeleteFailure(rc)");
        vm.Should().Contain("dialog.delete.alreadyGone");
        shell.Should().Contain("public static int MoveToRecycleBin");
        shell.Should().Contain("public static string DescribeDeleteFailure");
    }

    [Fact]
    public void Scanner_FiltersTransientAndHiddenItemsAtTheSingleEntry()
    {
        var scanner = ReadRepo("src", "DesktopBox", "Services", "DesktopScannerService.cs");

        // 桌面层与一键整理都走 ScanDesktop,过滤放这一处两边同时生效
        scanner.Should().Contain("DesktopItemPolicy.ShouldShow(entry)");
        scanner.Should().NotContain("Equals(\"desktop.ini\"");
    }

    [Fact]
    public void DesktopMarquee_SelfHealsInsteadOfGettingPermanentlyStuck()
    {
        // 真实故障:上一次拉框没收尾(抬起消息丢失)后 _marqueeSelecting 卡在 true,
        // 之后所有框选都被第一道守卫拦掉 —— 表现为"桌面再也框选不了,重启才好"。
        // 修法:新的一次左键按下本身就证明上一次已结束,重置而不是 return 放弃。
        var layer = ReadRepo("src", "DesktopBox", "Views", "DesktopLayerWindow.cs");

        layer.Should().Contain("自愈:上一次拉框未结束");
        layer.Should().NotContain("跳过:上一次拉框未结束", "卡住时不能只是跳过,必须自愈重置");
        layer.Should().Contain("ApplyRegion(full: false);   // 区域也收回来");
        // 诊断探针要留着:框选拉不起来时,该点的真实窗口类名是唯一线索
        layer.Should().Contain("WindowClassAt(screenX, screenY)");
    }

    private static string ReadRepo(params string[] parts)
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

        throw new FileNotFoundException(Path.Combine(parts));
    }
}
