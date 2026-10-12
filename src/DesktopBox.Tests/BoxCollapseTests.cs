using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DesktopBox.Controls;
using DesktopBox.Models;
using DesktopBox.Services;
using DesktopBox.ViewModels;
using DesktopBox.Views;
using FluentAssertions;
using Moq;

namespace DesktopBox.Tests;

/// <summary>卷起(折叠)+ 悬停自动展开的回归测试。
/// 规则逻辑(BoxCollapse/BoxViewModel)与布局直接断言;宿主窗口/XAML 接线沿用 ShellBehaviorTests 的源文件守卫风格。</summary>
public class BoxCollapseTests
{
    private const double HeaderHeight = 42;
    private const double BodyHeight = 200;

    // ---- 规则逻辑:高度与触发条件 ----

    [Fact]
    public void RolledBox_RendersAtHeaderHeightOnly()
    {
        BoxCollapse.IsRolled(true, false).Should().BeTrue();
        BoxCollapse.RenderHeight(true, false, BodyHeight, HeaderHeight).Should().Be(HeaderHeight);
    }

    [Fact]
    public void HoverExpandedBox_RendersAtFullHeightWhileStillCollapsed()
    {
        BoxCollapse.IsRolled(true, true).Should().BeFalse();
        BoxCollapse.RenderHeight(true, true, BodyHeight, HeaderHeight).Should().Be(BodyHeight);
    }

    [Fact]
    public void ManuallyExpandedBox_IgnoresHoverState()
    {
        BoxCollapse.IsRolled(false, false).Should().BeFalse();
        BoxCollapse.RenderHeight(false, false, BodyHeight, HeaderHeight).Should().Be(BodyHeight);
        BoxCollapse.RenderHeight(false, true, BodyHeight, HeaderHeight).Should().Be(BodyHeight);
    }

    [Fact]
    public void CollapsedHeight_FallsBackToDefaultWhenHeaderWasNotMeasured()
    {
        BoxCollapse.NormalizeCollapsedHeight(0).Should().Be(BoxCollapse.DefaultHeaderHeight);
        BoxCollapse.NormalizeCollapsedHeight(-5).Should().Be(BoxCollapse.DefaultHeaderHeight);
        BoxCollapse.NormalizeCollapsedHeight(38).Should().Be(38);
        BoxCollapse.RenderHeight(true, false, BodyHeight, 0).Should().Be(BoxCollapse.DefaultHeaderHeight);
    }

    [Theory]
    [InlineData(true, false, true, true)]     // 卷起 + 未展开 + 开关开 → 悬停展开
    [InlineData(true, false, false, false)]   // 开关关 → 不展开(只能手动)
    [InlineData(false, false, true, false)]   // 本来就展开着 → 不重复触发
    [InlineData(true, true, true, false)]     // 已经悬停展开 → 不重复触发
    public void ShouldExpandOnHover_OnlyForRolledBoxWithSettingOn(
        bool collapsed, bool hoverExpanded, bool enabled, bool expected)
        => BoxCollapse.ShouldExpandOnHover(collapsed, hoverExpanded, enabled).Should().Be(expected);

    [Fact]
    public void ShouldCollapseOnLeave_OnlyCollapsesTheTemporaryHoverState()
    {
        BoxCollapse.ShouldCollapseOnLeave(true).Should().BeTrue();
        BoxCollapse.ShouldCollapseOnLeave(false).Should().BeFalse();
    }

    [Fact]
    public void HoverDelays_ArePositiveAndCollapseIsNotFasterThanExpand()
    {
        BoxCollapse.HoverExpandDelayMs.Should().BeGreaterThan(0);
        BoxCollapse.HoverCollapseDelayMs.Should().BeGreaterThanOrEqualTo(BoxCollapse.HoverExpandDelayMs);
    }

    // ---- ViewModel ----

    [Fact]
    public void RolledViewModel_HidesContentAndTabRow()
    {
        var vm = new BoxViewModel(new Box
        {
            Name = "B",
            Height = BodyHeight,
            IsCollapsed = true,
            Tabs = { new BoxTab { Name = "T" } }
        });

        vm.IsRolled.Should().BeTrue();
        vm.RenderHeight.Should().Be(BoxCollapse.DefaultHeaderHeight);
        vm.ShowContent.Should().BeFalse();
        vm.ShowTabs.Should().BeFalse();
    }

    [Fact]
    public void TabbedBox_ShowsTabRowAgainWhileHoverExpanded()
    {
        var vm = new BoxViewModel(new Box { Name = "B", Height = BodyHeight, IsCollapsed = true,
            Tabs = { new BoxTab { Name = "T" } } });

        vm.IsHoverExpanded = true;
        vm.IsRolled.Should().BeFalse();
        vm.ShowTabs.Should().BeTrue();
        vm.RenderHeight.Should().Be(BodyHeight);

        // 鼠标移开 → 收回卷起态(临时展开不持久化)
        vm.IsHoverExpanded = false;
        vm.IsRolled.Should().BeTrue();
        vm.ShowTabs.Should().BeFalse();
    }

    [Fact]
    public void CollapseStateChanges_NotifyRenderHeight()
    {
        var vm = new BoxViewModel(new Box { Name = "B", Height = BodyHeight });
        var notified = new List<string?>();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        vm.IsCollapsed = true;

        notified.Should().Contain(nameof(BoxViewModel.RenderHeight));
        notified.Should().Contain(nameof(BoxViewModel.ShowContent));
        vm.RenderHeight.Should().Be(BoxCollapse.DefaultHeaderHeight);
    }

    [Fact]
    public void ResizingBox_RefreshesRenderHeightSoWindowFollowsNewSize()
    {
        var vm = new BoxViewModel(new Box { Name = "B", Height = BodyHeight });
        var notified = new List<string?>();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        vm.Height = 320;

        notified.Should().Contain(nameof(BoxViewModel.RenderHeight));
        vm.RenderHeight.Should().Be(320);
    }

    [Fact]
    public void MeasuredHeaderHeight_BecomesTheRolledWindowHeight()
    {
        var vm = new BoxViewModel(new Box { Name = "B", Height = BodyHeight, IsCollapsed = true });

        vm.CollapsedHeight = 38;   // 由 BoxControl 实测标题栏后回填

        vm.RenderHeight.Should().Be(38);
    }

    [Fact]
    public void ToggleCollapsed_FlipsAndReturnsPersistedFlag()
    {
        var vm = new BoxViewModel(new Box { Name = "B" });

        vm.ToggleCollapsed();
        vm.IsCollapsed.Should().BeTrue();
        vm.ToModel().IsCollapsed.Should().BeTrue();

        vm.ToggleCollapsed();
        vm.IsCollapsed.Should().BeFalse();
        vm.ToModel().IsCollapsed.Should().BeFalse();
    }

    // ---- 持久化 ----

    [Fact]
    public void JsonStore_RoundTripsCollapsedBoxAndHoverExpandSetting()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dbx_collapse_{Guid.NewGuid():N}.json");
        var store = new JsonStoreService(path);

        store.Save(new AppConfig
        {
            Boxes = new() { new Box { Name = "卷起盒", IsCollapsed = true } },
            Settings = new AppSettings { HoverExpandCollapsedBoxes = false }
        });
        var loaded = new JsonStoreService(path).Load();

        loaded.Boxes[0].IsCollapsed.Should().BeTrue();
        loaded.Settings.HoverExpandCollapsedBoxes.Should().BeFalse();
    }

    [Fact]
    public void LegacyJsonWithoutCollapseFields_DefaultsToExpandedBoxAndHoverExpandOn()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dbx_legacy_collapse_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, @"{""boxes"":[{""name"":""旧盒子""}],""settings"":{}}");

        var cfg = new JsonStoreService(path).Load();

        cfg.Boxes[0].IsCollapsed.Should().BeFalse();                 // 旧配置:盒子照旧展开
        cfg.Settings.HoverExpandCollapsedBoxes.Should().BeTrue();    // 新功能默认开启
    }

    // ---- 布局:卷起后只剩标题栏 ----

    [Fact]
    public void RolledBoxControl_LaysOutHeaderOnlyAndShowsContentAgainWhenExpanded()
    {
        RunOnSta(() =>
        {
            var box = new BoxViewModel(new Box { Name = "B", Width = 240, Height = BodyHeight, IsCollapsed = true });
            // 不给控件设 Height:要验证的正是"卷起时它自己只想要标题栏那么高"。
            var control = new BoxControl { DataContext = box, Width = 240 };
            var available = new Size(240, BodyHeight);
            control.Measure(available);
            control.Arrange(new Rect(available));
            control.UpdateLayout();

            var header = (Border)control.FindName("HeaderBar")!;
            var items = (ScrollViewer)control.FindName("ItemsScroll")!;
            var tabRow = (Grid)control.FindName("TabRow")!;

            items.Visibility.Should().Be(Visibility.Collapsed);
            tabRow.Visibility.Should().Be(Visibility.Collapsed);
            // 卷起后卡片只"想要"标题栏那么高(宿主窗口据此把 HWND 缩到标题栏)
            control.DesiredSize.Height.Should().BeApproximately(header.ActualHeight + 2, 0.5);

            box.IsCollapsed = false;
            items.Visibility.Should().Be(Visibility.Visible);
        });
    }

    [Fact]
    public void HoverExpandedRolledBoxControl_ShowsItemsAgainWithoutChangingPersistedState()
    {
        RunOnSta(() =>
        {
            var box = new BoxViewModel(new Box { Name = "B", Width = 240, Height = BodyHeight, IsCollapsed = true });
            var control = new BoxControl { DataContext = box, Width = 240, Height = BodyHeight };
            var available = new Size(240, BodyHeight);
            control.Measure(available);
            control.Arrange(new Rect(available));
            control.UpdateLayout();
            var items = (ScrollViewer)control.FindName("ItemsScroll")!;
            items.Visibility.Should().Be(Visibility.Collapsed);

            box.IsHoverExpanded = true;   // 鼠标悬停触发的临时展开

            items.Visibility.Should().Be(Visibility.Visible);
            box.IsCollapsed.Should().BeTrue();   // 持久状态仍是卷起
        });
    }

    // ---- 源文件守卫:接线与宿主窗口 ----

    [Fact]
    public void BoxControl_DelaysHoverExpandAndGivesUpIfPointerAlreadyLeft()
    {
        var code = File.ReadAllText(FindRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml.cs"));

        code.Should().Contain("ScheduleHoverExpand");
        code.Should().Contain("DispatcherTimer");
        code.Should().Contain("BoxCollapse.ShouldExpandOnHover");
        code.Should().Contain("BoxCollapse.ShouldCollapseOnLeave");
        code.Should().Contain("BoxCollapse.HoverExpandDelayMs");
        code.Should().Contain("BoxCollapse.HoverCollapseDelayMs");
        // 延时期间鼠标已移开就放弃展开(否则会出现"鼠标走了它自己弹开")
        code.Should().Contain("if (expand && !IsMouseOver) return;");
        // 复数窗口/控件回收:DataContext 换了就不再回调旧 VM
        code.Should().Contain("ReferenceEquals(vm, Vm)");
    }

    [Fact]
    public void BoxControl_MeasuresRealHeaderHeightForTheRolledWindow()
    {
        var code = File.ReadAllText(FindRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml.cs"));
        var xaml = File.ReadAllText(FindRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml"));

        xaml.Should().Contain("x:Name=\"HeaderBar\"");
        code.Should().Contain("HeaderBar.ActualHeight");
        code.Should().Contain("HeaderBar.SizeChanged");
        code.Should().Contain("Card.BorderThickness");
        // 模型尺寸是物理像素、WPF 布局是 DIP:换算不能漏(否则高 DPI 下卷起会裁掉标题)
        code.Should().Contain("GetDpiScale()");
    }

    [Fact]
    public void BoxControl_WiresCollapseToDoubleClickHeaderButtonAndContextMenu()
    {
        var code = File.ReadAllText(FindRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml.cs"));
        var xaml = File.ReadAllText(FindRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml"));

        code.Should().Contain("e.ClickCount >= 2");
        code.Should().Contain("private void ToggleCollapse()");
        code.Should().Contain("OnToggleCollapse");
        xaml.Should().Contain("Click=\"OnToggleCollapse\"");
        xaml.Should().Contain("x:Name=\"CollapseBtn\"");
        xaml.Should().Contain("box.menu.rollUp");
        xaml.Should().Contain("box.menu.expandBox");
        // 卷起时隐藏标签行、条目区与缩放手柄
        xaml.Should().Contain("Visibility=\"{Binding ShowTabs, Converter={StaticResource BoolVis}}\"");
        xaml.Should().Contain("Visibility=\"{Binding ShowContent, Converter={StaticResource BoolVis}}\"");
    }

    [Fact]
    public void BoxWindow_SizesWindowAndContentByRenderHeight()
    {
        var source = File.ReadAllText(FindRepositoryFile("src", "DesktopBox", "Views", "BoxWindow.cs"));

        source.Should().Contain("Box.RenderHeight / scale.Y");
        source.Should().Contain("(int)Math.Round(box.RenderHeight)");
        source.Should().Contain("(int)Math.Round(Box.RenderHeight)");
        source.Should().Contain("nameof(BoxViewModel.RenderHeight)");
        // 悬停展开时提到最上层:只在状态切换时做一次(逐帧 HWND_TOP 会逼桌面图标层重排)
        source.Should().Contain("nameof(BoxViewModel.IsHoverExpanded)");
        source.Should().Contain("RaiseToTop");
    }

    [Fact]
    public void MainViewModel_PersistsAndExposesTheHoverExpandSetting()
    {
        var source = File.ReadAllText(FindRepositoryFile("src", "DesktopBox", "ViewModels", "MainViewModel.cs"));
        var settings = File.ReadAllText(FindRepositoryFile("src", "DesktopBox", "ViewModels", "SettingsViewModel.cs"));
        var window = File.ReadAllText(FindRepositoryFile("src", "DesktopBox", "Views", "SettingsWindow.xaml"));

        source.Should().Contain("HoverExpandCollapsedBoxes = cfg.Settings.HoverExpandCollapsedBoxes");
        source.Should().Contain("cfg.Settings.HoverExpandCollapsedBoxes = HoverExpandCollapsedBoxes");
        settings.Should().Contain("HoverExpandCollapsedBoxes");
        window.Should().Contain("settings.hoverExpand");
    }

    [Fact]
    public void Strings_DefineCollapseTextsForEveryLanguage()
    {
        foreach (var file in new[] { "Strings.zh-CN.xaml", "Strings.en-US.xaml" })
        {
            var source = File.ReadAllText(FindRepositoryFile("src", "DesktopBox", "Resources", file));

            source.Should().Contain("x:Key=\"box.menu.rollUp\"");
            source.Should().Contain("x:Key=\"box.menu.expandBox\"");
            source.Should().Contain("x:Key=\"tip.rollUp\"");
            source.Should().Contain("x:Key=\"tip.expandBox\"");
            source.Should().Contain("x:Key=\"settings.hoverExpand\"");
        }
    }

    // ---- 端到端:真实 HWND 的高度随卷起/悬停展开变化 ----

    [Fact]
    public void BoxWindow_ShrinksRealHwndToHeaderOnlyAndGrowsBackOnHover()
    {
        RunOnSta(() =>
        {
            // 隐藏的宿主窗口:只为提供一个真实父 HWND(不显示任何界面)
            var hostParams = new System.Windows.Interop.HwndSourceParameters("dbx-test-host")
            {
                WindowStyle = 0x00CF0000,   // WS_OVERLAPPEDWINDOW
                PositionX = -4000,
                PositionY = -4000,
                Width = 400,
                Height = 300
            };
            using var host = new System.Windows.Interop.HwndSource(hostParams);
            var box = new BoxViewModel(new Box
            {
                Name = "B",
                X = 0,
                Y = 0,
                Width = 240,
                Height = BodyHeight,
                IsCollapsed = true
            })
            { CollapsedHeight = HeaderHeight };

            using var window = new BoxWindow(box, NewMainViewModel(), host.Handle);

            HwndHeightOf(window).Should().Be((int)HeaderHeight, "卷起的盒子窗口只应保留标题栏高度");

            box.IsHoverExpanded = true;    // 鼠标悬停 → 自动展开
            Pump();
            HwndHeightOf(window).Should().Be((int)BodyHeight);

            box.IsHoverExpanded = false;   // 移开 → 收回卷起
            Pump();
            HwndHeightOf(window).Should().Be((int)HeaderHeight);
        });
    }

    private static int HwndHeightOf(BoxWindow window)
    {
        DesktopBox.Native.User32.GetWindowRect(window.Handle, out var rect).Should().BeTrue();
        return rect.Bottom - rect.Top;
    }

    /// <summary>BoxWindow 的逐帧改动走 16ms 节流 + Dispatcher 尾帧,断言前先泵一会儿消息。</summary>
    private static void Pump()
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(200);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(5);
        }
    }

    private static MainViewModel NewMainViewModel()
    {
        var store = new Mock<IPersistenceService>();
        store.Setup(s => s.Load()).Returns(new AppConfig());
        var localizer = new Mock<ILocalizerService>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns<string>(k => k);
        return new MainViewModel(store.Object, new DropParserService(),
            new Mock<IIconExtractorService>().Object, new Mock<IOrganizeService>().Object,
            new Mock<ICategorizerService>().Object, new Mock<IDesktopIconsService>().Object,
            localizer.Object, new Mock<IShellChangeNotifierService>().Object);
    }

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
