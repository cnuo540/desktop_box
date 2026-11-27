using System.IO;
using System.Windows;          // Clipboard(复制粘贴语义测试)
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
        ICategorizerService? categorizer = null, IDesktopScannerService? scanner = null,
        string? iconPath = null)
    {
        var store = new Mock<IPersistenceService>();
        store.Setup(s => s.Load()).Returns(config);
        var localizer = new Mock<ILocalizerService>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns<string>(k => k);
        var icons = new Mock<IDesktopIconsService>();
        icons.SetupGet(d => d.AreIconsVisible).Returns(false);

        var iconExtractor = new Mock<IIconExtractorService>();
        if (iconPath is not null)
            iconExtractor.Setup(i => i.Extract(It.IsAny<string>())).Returns(iconPath);

        // 默认扫不到新文件:不配置的话 Moq 返回 null,Organize 里 LINQ 直接抛
        var organize = new Mock<IOrganizeService>();
        organize.Setup(o => o.ScanAndCategorize()).Returns(new List<(string Path, string Category)>());

        var categorizerMock = categorizer is null ? new Mock<ICategorizerService>() : null;
        categorizerMock?.Setup(c => c.Categorize(It.IsAny<string>())).Returns("cat.docs");

        return new MainViewModel(store.Object, new DropParserService(),
            iconExtractor.Object,
            organize.Object,
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

    [Fact]
    public void RenamedReference_ReExtractsItsIcon()
    {
        // 真实故障(v1.7.38 及以前):UpdateReferencePath 只把 IconCachePath 置空、
        // 从不重新提取 —— 注释写着"按新路径后台重取"但代码没调。
        // 表现:桌面上把 Photoshop.exe.lnk 改个名,盒子里的图标就永久消失,只能重启才回来。
        var oldPath = DesktopPath($"dbx-icon-old-{Guid.NewGuid():N}.txt");
        var newPath = DesktopPath($"dbx-icon-new-{Guid.NewGuid():N}.txt");
        const string fakeIcon = @"C:\fake\icon.png";

        var watcher = new FakeWatcher();
        var vm = NewVm(ConfigWithItem(oldPath), watcher, iconPath: fakeIcon);
        vm.LoadCommand.Execute(null);
        vm.DesktopIconsVisible = false;

        var item = vm.Boxes[0].Items.Single();
        item.IconCachePath = null;          // 改名时会被清空

        watcher.Raise(new DesktopItemChange(DesktopChangeKind.Renamed, newPath, oldPath));

        // 图标提取走 Task.Run,等它回填
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (item.IconCachePath is null && sw.ElapsedMilliseconds < 3000)
            System.Threading.Thread.Sleep(20);

        item.TargetPath.Should().Be(newPath);
        item.IconCachePath.Should().Be(fakeIcon, "改名清掉图标后必须重新提取,否则图标永久消失");
    }

    [Fact]
    public void IconExtraction_LogsFailuresInsteadOfSwallowingThem()
    {
        // 图标不显示时原来完全无从查起:Extract 是裸 catch 返回 null、LoadIconAsync 静默 return。
        var extractor = ReadRepo("src", "DesktopBox", "Services", "IconExtractorService.cs");

        extractor.Should().Contain("IconExtract.Fail");
        extractor.Should().Contain("IconExtract.NoIcon");
        extractor.Should().Contain("IconExtract.Lnk");
    }

    [Fact]
    public void IconCacheKey_CarryVersionAndSizeSoStaleEntriesCannotStick()
    {
        // 真实故障:Photoshop.exe 的图标在 10/16 存成了通用白页,而 key=路径+mtime
        // (exe 的 mtime 是 2013 年、永不变化)→ 永远命中那个坏文件,新逻辑再正确也轮不到它。
        // 修法:key 加版本号(改提取逻辑就 +1,全部重新提取)+ 文件大小(补"内容换了但 mtime 没变"的盲区)。
        var extractor = ReadRepo("src", "DesktopBox", "Services", "IconExtractorService.cs");

        extractor.Should().Contain("CacheVersion");
        extractor.Should().Contain("CacheKey(");
        // 三处缓存(路径图标 / 快捷方式资源 / 系统图标)都必须走带版本的 key
        extractor.Should().Contain("var key = CacheKey(targetPath.ToLowerInvariant()");
        extractor.Should().Contain("var key = CacheKey(\"lnk|\"");
        extractor.Should().Contain("var key = CacheKey(\"sys|\"");
        // 路径图标的 key 必须含文件大小
        extractor.Should().Contain("+ \"|\" + fileSize)");
        // 不许再有裸的 StableKey 直接当缓存 key(会绕过版本号)
        extractor.Should().NotContain("var key = StableKey(");
    }

    [Fact]
    public void DeletingASystemIcon_RemovesItsReferenceInsteadOfDoingNothing()
    {
        // 真实故障:系统图标标签里的 4 个图标按 Delete 完全没反应 ——
        // MoveToRecycleBin 把 "::" 开头的路径过滤掉后 requested 变空,直接 return 0,
        // 于是标签永远清不空 → 永远删不掉(右键"移除"可用,但 Delete 这条主路是死的)。
        var vm = NewVm(ConfigWithItem("::{645FF040-5081-101B-9F08-00AA002F954E}"), new FakeWatcher());
        vm.LoadCommand.Execute(null);

        vm.Boxes[0].Items.Should().ContainSingle();

        var removed = vm.MoveToRecycleBin(new[] { "::{645FF040-5081-101B-9F08-00AA002F954E}" });

        removed.Should().Be(1, "虚拟项也要算作'处理成功',否则调用方不清选中");
        vm.Boxes[0].Items.Should().BeEmpty("引用必须被撤掉,否则标签永远非空、永远删不掉");
    }

    [Fact]
    public void DeletingAnUrl_RemovesItsReferenceToo()
    {
        var vm = NewVm(ConfigWithItem("https://example.com/page"), new FakeWatcher());
        vm.LoadCommand.Execute(null);

        var removed = vm.MoveToRecycleBin(new[] { "https://example.com/page" });

        removed.Should().Be(1);
        vm.Boxes[0].Items.Should().BeEmpty();
    }

    [Fact]
    public void DeleteFeedbackKeys_ExistInBothLanguages()
    {
        var zh = ReadRepo("src", "DesktopBox", "Resources", "Strings.zh-CN.xaml");
        var en = ReadRepo("src", "DesktopBox", "Resources", "Strings.en-US.xaml");

        foreach (var key in new[] { "dialog.delete.virtualOnly" })
        {
            zh.Should().Contain($"x:Key=\"{key}\"", $"中文缺 {key}");
            en.Should().Contain($"x:Key=\"{key}\"", $"英文缺 {key}");
        }
        // 两处必须一致使用:移除虚拟项后要给反馈,否则又是"按了没反应"
        ReadRepo("src", "DesktopBox", "ViewModels", "MainViewModel.cs")
            .Should().Contain("_localizer[\"dialog.delete.virtualOnly\"]");
    }

    [Fact]
    public void Organize_MergesLegacySystemIconsTabIntoShortcuts()
    {
        // 真实反馈:删掉「系统图标」标签后,下次一键整理又把它建回来 —— 标签永远删不掉。
        // 现在改成:老标签内容并进「快捷方式」,老标签移除,不再单独建。
        var clsid = "::{645FF040-5081-101B-9F08-00AA002F954E}";
        var config = new AppConfig
        {
            Boxes =
            [
                new Box
                {
                    Key = "box.organize", Name = "桌面整理",
                    Tabs =
                    [
                        new BoxTab
                        {
                            Key = "tab.sysicons", Name = "系统图标",
                            Items = [new BoxItem { Type = ItemType.SystemIcon, TargetPath = clsid, DisplayName = "回收站" }]
                        },
                        new BoxTab { Key = "cat.docs", Name = "文档", Items = [] }
                    ]
                }
            ]
        };
        var vm = NewVm(config, new FakeWatcher());
        vm.LoadCommand.Execute(null);

        vm.OrganizeCommand.Execute(null);

        var box = vm.Boxes.Single(b => b.Key == "box.organize");
        box.Tabs.Should().NotContain(t => t.Key == "tab.sysicons", "老标签必须被移除,否则删了下次整理又回来");
        var shortcuts = box.Tabs.Single(t => t.Key == "cat.shortcut");
        shortcuts.Items.Should().ContainSingle(i => i.TargetPath == clsid, "内容要并进「快捷方式」");
        box.Tabs.Should().Contain(t => t.Key == "cat.docs", "其它标签不受影响");
    }

    [Fact]
    public void Organize_PutsSystemIconsIntoShortcuts_NotASeparateTab()
    {
        var config = new AppConfig
        {
            Boxes =
            [
                new Box
                {
                    Key = "box.organize", Name = "桌面整理",
                    Tabs = [new BoxTab { Key = "cat.docs", Name = "文档", Items = [] }]
                }
            ]
        };
        var vm = NewVm(config, new FakeWatcher());
        vm.LoadCommand.Execute(null);

        vm.OrganizeCommand.Execute(null);

        var box = vm.Boxes.Single(b => b.Key == "box.organize");
        box.Tabs.Should().NotContain(t => t.Key == "tab.sysicons", "不再单独建「系统图标」标签");
        var shortcuts = box.Tabs.Single(t => t.Key == "cat.shortcut");
        shortcuts.Items.Should().HaveCount(4, "4 个系统图标(此电脑/回收站/控制面板/网络)应放进快捷方式");
        shortcuts.Items.Should().OnlyContain(i => i.Type == ItemType.SystemIcon);
    }

    [Fact]
    public void Organize_NeverRecreatesTheSystemIconsTab()
    {
        var vm = ReadRepo("src", "DesktopBox", "ViewModels", "MainViewModel.cs");

        vm.Should().NotContain("EnsureSystemIconsTab(", "旧方法应已删除");
        vm.Should().Contain("EnsureSystemIconsInShortcutTab(");
        // 创建语句必须消失(按 Key 查找/迁移仍然允许)
        vm.Should().NotContain("new BoxTab { Key = \"tab.sysicons\"");
        vm.Should().Contain("CategorizerService.Shortcut");
    }

    [Fact]
    public void VirtualItems_AreDraggableInsideTheBox_ButNeverToTheDesktop()
    {
        // 真实反馈:4 个系统图标在盒子里完全拖不动 ——
        // _dragCandidate 拿 CanDragAsFile 当门槛,而它明确排除 SystemIcon/Url,
        // 于是 OnTileMouseMove 第一行就 return。盒内换位用的是 DragSourceItemFormat,
        // 根本不需要"能当文件拖"。
        var tile = ReadRepo("src", "DesktopBox", "Controls", "ItemTile.xaml.cs");

        tile.Should().Contain("_dragCandidate = true");
        tile.Should().NotContain("_dragCandidate = ItemDragDrop.CanDragAsFile");
        // 桌面覆盖层只在这一拖里真有文件时才建:纯虚拟项拖到桌面不该触发"出盒"
        tile.Should().Contain("dragged.Any(ItemDragDrop.CanDragAsFile)");
        // 混合拖拽时只有文件出盒,虚拟项留下
        tile.Should().Contain("var landable = dragged.Where(ItemDragDrop.CanDragAsFile)");
        // FileDrop 那道过滤本身不能放松(它挡的是"给外部程序的载荷")
        ReadRepo("src", "DesktopBox", "Controls", "ItemDragDrop.cs")
            .Should().Contain("item.Type is not ItemType.Url and not ItemType.SystemIcon");
    }

    [Fact]
    public void CopyThenPasteOntoDesktop_MovesTheItemsOutOfTheBox()
    {
        // 用户要求:盒子内 Ctrl+C → 粘贴到桌面 = 移动(条目出盒、文件落桌面),
        // 和"拖出盒子"同一套语义;而不是 explorer 的原生复制(那会弹"源文件名和目标文件名相同")。
        // 整段放在 STA 线程里跑:WPF Clipboard 要求 STA(生产里钩子回调就在 STA 的 UI 线程上)。
        var src = Path.Combine(Path.GetTempPath(), $"dbx-copy-{Guid.NewGuid():N}.txt");
        File.WriteAllText(src, "x");
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var landed = Path.Combine(desktop, Path.GetFileName(src));
        if (File.Exists(landed)) File.Delete(landed);   // 先清掉上次残留,避免 (2) 后缀干扰

        (int Copied, bool HasMarker, int Pasted, bool Landed, int LeftInBox, bool DesktopWritable) result = default;
        var thread = new Thread(() =>
        {
            var vm = NewVm(ConfigWithItem(src), new FakeWatcher());
            vm.LoadCommand.Execute(null);
            var item = vm.Boxes[0].Items.Single();
            item.IsSelected = true;

            // 沙箱环境里测试进程可能写不了真实桌面(ACL 拒绝,与本功能无关) → 探测一下
            var canWrite = false;
            try
            {
                var probe = Path.Combine(desktop, $"dbx-probe-{Guid.NewGuid():N}.tmp");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                canWrite = true;
            }
            catch { /* 写不了就只验剪贴板与出盒语义 */ }

            var copied = vm.CopySelectionToClipboard();
            var hasMarker = MainViewModel.ReadCopiedItemIds() is not null;
            var pasted = vm.PasteOntoDesktop();
            result = (copied, hasMarker, pasted, File.Exists(landed), vm.Boxes[0].Items.Count, canWrite);
            try { Clipboard.Clear(); } catch { }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(15000);

        try
        {
            result.Copied.Should().Be(1);
            result.HasMarker.Should().BeTrue("剪贴板里必须带「这是从盒子复制的」标记");
            result.Pasted.Should().Be(1);
            result.LeftInBox.Should().Be(0, "粘贴到桌面 = 移动,条目必须出盒");
            if (result.DesktopWritable)
                result.Landed.Should().BeTrue("文件要落到桌面上(本机沙箱拒绝写桌面时跳过此断言)");
        }
        finally
        {
            if (File.Exists(landed)) File.Delete(landed);
            File.Delete(src);
        }
    }

    [Fact]
    public void MoveOrCopyIntoFolder_MovesOrCopiesAndSkipsSelfAndAlreadyInside()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var folder = Path.Combine(root.FullName, "收纳夹");
            Directory.CreateDirectory(folder);
            var inside = Path.Combine(folder, "已在里面.txt");
            File.WriteAllText(inside, "x");
            var loose = Path.Combine(root.FullName, "散落.txt");
            File.WriteAllText(loose, "x");

            var vm = NewVm(new AppConfig(), new FakeWatcher());
            vm.LoadCommand.Execute(null);

            // 移动:散落文件进去
            vm.MoveOrCopyIntoFolder(new[] { loose }, folder, move: true).Should().Be(1);
            File.Exists(Path.Combine(folder, "散落.txt")).Should().BeTrue();
            File.Exists(loose).Should().BeFalse("move:原文件应已挪走");

            // 跳过自己 / 已经在里面的:一个都不该重复处理
            vm.MoveOrCopyIntoFolder(new[] { folder }, folder, move: true).Should().Be(0, "不能把自己拖进自己");
            vm.MoveOrCopyIntoFolder(new[] { inside }, folder, move: true).Should().Be(0, "已在里面的不再处理");

            // 复制:原件保留
            var another = Path.Combine(root.FullName, "另一个.txt");
            File.WriteAllText(another, "x");
            vm.MoveOrCopyIntoFolder(new[] { another }, folder, move: false).Should().Be(1);
            File.Exists(another).Should().BeTrue("copy:原件必须还在");
            File.Exists(Path.Combine(folder, "另一个.txt")).Should().BeTrue();
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void KeyboardAndDropWiring_MatchesTheAgreedSemantics()
    {
        // 语义表(用户确认):盒子→桌面=移动;桌面→盒子=移动;两者→文件夹=复制;
        // 拖到文件夹图标上=移进该文件夹;拖到两图标之间=仅排序。
        var hook = ReadRepo("src", "DesktopBox", "Native", "KeyboardHook.cs");
        hook.Should().Contain("SuppressCurrentKey", "Ctrl+V 要能吞掉原生粘贴,否则 explorer 会再复制一次");
        hook.Should().Contain("return (IntPtr)1;   // 吞掉");

        var main = ReadRepo("src", "DesktopBox", "Views", "MainWindow.xaml.cs");
        main.Should().Contain("VK_C");            // Ctrl+C
        main.Should().Contain("VK_V");            // Ctrl+V
        main.Should().Contain("CopySelectionToClipboard");
        main.Should().Contain("PasteOntoDesktop");
        main.Should().Contain("SuppressCurrentKey = true");

        var layer = ReadRepo("src", "DesktopBox", "Views", "DesktopLayerWindow.cs");
        layer.Should().Contain("MoveOrCopyIntoFolder(paths, targetPath, move: true)", "拖到文件夹图标上=移进去");
        // 落在磁贴上的 handler 要带上路径(否则分不清文件夹还是普通文件)
        layer.Should().Contain("host.Drop += (_, e) => OnTileDrop(path, e)");
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
