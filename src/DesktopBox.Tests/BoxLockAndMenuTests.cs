using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DesktopBox.Controls;
using DesktopBox.Models;
using DesktopBox.Services;
using DesktopBox.ViewModels;
using FluentAssertions;

namespace DesktopBox.Tests;

/// <summary>锁定位置、标签悬停切换、删除盒子流程、右键一键整理、对话框配色 的回归测试。</summary>
public class BoxLockAndMenuTests
{
    // ---- 锁定位置 ----

    [Fact]
    public void LockedBox_StopsDragAndResizeButKeepsCollapse()
    {
        var vm = new BoxViewModel(new Box { Name = "B", Width = 240, Height = 200 });
        vm.CanMove.Should().BeTrue();
        vm.ShowResizeHandles.Should().BeTrue();

        vm.ToggleLocked();

        vm.IsLocked.Should().BeTrue();
        vm.CanMove.Should().BeFalse();          // 不能拖动
        vm.ShowResizeHandles.Should().BeFalse(); // 也不能缩放
        vm.ToModel().IsLocked.Should().BeTrue(); // 持久化
    }

    [Fact]
    public void LockedBox_StillRollsAndHoverExpandsBecauseThatDoesNotMoveIt()
    {
        var vm = new BoxViewModel(new Box { Name = "B", Height = 200, IsLocked = true });

        vm.ToggleCollapsed();
        vm.IsRolled.Should().BeTrue();
        vm.RenderHeight.Should().Be(BoxCollapse.DefaultHeaderHeight);

        vm.IsHoverExpanded = true;
        vm.RenderHeight.Should().Be(200);
    }

    [Fact]
    public void ResizeHandles_HideWhenRolledOrLocked()
    {
        var vm = new BoxViewModel(new Box { Name = "B", Height = 200 });
        vm.ShowResizeHandles.Should().BeTrue();

        vm.IsLocked = true;
        vm.ShowResizeHandles.Should().BeFalse();

        vm.IsLocked = false;
        vm.IsCollapsed = true;
        vm.ShowResizeHandles.Should().BeFalse();

        vm.IsCollapsed = false;
        vm.ShowResizeHandles.Should().BeTrue();
    }

    [Fact]
    public void LockState_NotifiesDragResizeAffordances()
    {
        var vm = new BoxViewModel(new Box { Name = "B" });
        var notified = new List<string?>();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        vm.IsLocked = true;

        notified.Should().Contain(nameof(BoxViewModel.CanMove));
        notified.Should().Contain(nameof(BoxViewModel.ShowResizeHandles));
        notified.Should().Contain(nameof(BoxViewModel.LockActionText));
    }

    [Fact]
    public void JsonStore_RoundTripsLockedFlagAndDefaultsToUnlockedForLegacyJson()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dbx_lock_{Guid.NewGuid():N}.json");
        new JsonStoreService(path).Save(new AppConfig
        {
            Boxes = new() { new Box { Name = "锁住的", IsLocked = true } }
        });
        new JsonStoreService(path).Load().Boxes[0].IsLocked.Should().BeTrue();

        var legacy = Path.Combine(Path.GetTempPath(), $"dbx_lock_legacy_{Guid.NewGuid():N}.json");
        File.WriteAllText(legacy, @"{""boxes"":[{""name"":""旧盒子""}]}");
        new JsonStoreService(legacy).Load().Boxes[0].IsLocked.Should().BeFalse();
    }

    // ---- 标签行:居中 + 悬停即切换 ----

    [Fact]
    public void HoveringATabItem_SwitchesToItLikeAClick()
    {
        RunOnSta(() =>
        {
            var vm = TabbedVm(width: 400);
            var control = new BoxControl { DataContext = vm, Width = 400, Height = 200 };
            Arrange(control);

            var listBox = (ListBox)control.FindName("TabList")!;
            var second = (ListBoxItem?)listBox.ItemContainerGenerator.ContainerFromIndex(1);
            second.Should().NotBeNull();

            second!.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
            {
                RoutedEvent = UIElement.MouseEnterEvent
            });

            vm.SelectedTab.Should().BeSameAs(vm.Tabs[1]);
        });
    }

    [Fact]
    public void TabStrip_IsCenteredInTheBoxInsteadOfHuggingTheLeftEdge()
    {
        RunOnSta(() =>
        {
            const double width = 600;
            var vm = TabbedVm(width: width);
            var control = new BoxControl { DataContext = vm, Width = width, Height = 200 };
            Arrange(control);

            var listBox = (ListBox)control.FindName("TabList")!;
            listBox.ActualWidth.Should().BeGreaterThan(0);
            var stripCenter = listBox.TranslatePoint(new Point(listBox.ActualWidth / 2, 0), control).X;

            // 左对齐时标签组中心会在 ~70 附近;居中后应落在盒子中线附近(右侧给「+」留了 34px,故略偏左)
            stripCenter.Should().BeApproximately(width / 2 - 17, 45);
        });
    }

    // ---- 删除盒子:先清空引用再删盒子(文件永不删除) ----

    [Fact]
    public void DeleteBox_ClearsReferencesBeforeRemovingAndNeverDeletesFiles()
    {
        var body = MethodBody("BoxControl.xaml.cs", "private void OnDelete(", "// ---- 标签操作 ----");

        body.Should().Contain("dialog.deleteBox.clearThenDelete");
        body.Should().NotContain("dialog.deleteBox.nonEmpty");     // 不再"拒绝删除非空盒子"
        body.IndexOf("ClearAll()", StringComparison.Ordinal)
            .Should().BeLessThan(body.IndexOf("RemoveBoxCommand", StringComparison.Ordinal));
        body.Should().NotContain("File.Delete");
        body.Should().NotContain("Directory.Delete");
    }

    // ---- 右键一键整理桌面 ----

    [Fact]
    public void BoxContextMenu_OffersOneClickTidy()
    {
        BoxControlXaml.Should().Contain("Header=\"{DynamicResource menu.organize}\" Click=\"OnOrganizeDesktop\"");

        var code = ReadRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml.cs");
        code.Should().Contain("private void OnOrganizeDesktop");
        code.Should().Contain("MainVm.OrganizeCommand.Execute(null)");
    }

    // ---- 锁定按钮与菜单接线 ----

    [Fact]
    public void BoxControl_WiresLockButtonAndMenuToToggleLock()
    {
        BoxControlXaml.Should().Contain("x:Name=\"LockBtn\"");
        BoxControlXaml.Should().Contain("Click=\"OnToggleLock\"");
        BoxControlXaml.Should().Contain("box.menu.lock");
        BoxControlXaml.Should().Contain("box.menu.unlock");
        BoxControlXaml.Should().Contain("ToolTip=\"{Binding LockActionText}\"");
        // 缩放手柄跟着"锁定/卷起"状态走
        BoxControlXaml.Should().Contain("Visibility=\"{Binding ShowResizeHandles, Converter={StaticResource BoolVis}}\"");

        var code = ReadRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml.cs");
        code.Should().Contain("private void OnToggleLock");
        // 锁定时不进入拖动/缩放
        code.Should().Contain("if (!Vm.CanMove) return;   // 位置已锁定:不拖动");
        code.Should().Contain("if (Vm is null || !Vm.CanMove) return;   // 位置已锁定:不改尺寸");
    }

    [Fact]
    public void BoxHeader_CentersTheBoxName()
    {
        BoxControlXaml.Should().Contain("TextAlignment=\"Center\"");
    }

    // ---- 重命名/合并对话框:浅色主题下不能白底白字 ----

    [Fact]
    public void InputDialog_OverridesThemeBackgroundsWithItsOwnDarkTemplate()
    {
        var xaml = ReadRepositoryFile("src", "DesktopBox", "Views", "InputDialog.xaml");

        xaml.Should().Contain("x:Key=\"DarkTextBox\"");
        xaml.Should().Contain("x:Key=\"DarkComboBox\"");
        xaml.Should().Contain("Style=\"{StaticResource DarkTextBox}\"");
        xaml.Should().Contain("Style=\"{StaticResource DarkComboBox}\"");
        // 关键:自带模板 + TemplateBinding,否则 WPF-UI 的主题画刷在浅色主题下会渲染成白底,
        // 而对话框文字是白色 → 白底白字看不见。
        xaml.Should().Contain("Background=\"{TemplateBinding Background}\"");
        xaml.Should().Contain("x:Name=\"PART_ContentHost\"");
        xaml.Should().Contain("x:Name=\"PART_Popup\"");
        xaml.Should().Contain("CaretBrush");
        xaml.Should().Contain("SelectionBrush");
    }

    // ---- i18n ----

    [Fact]
    public void Strings_DefineLockAndDeleteTextsForEveryLanguage()
    {
        foreach (var file in new[] { "Strings.zh-CN.xaml", "Strings.en-US.xaml" })
        {
            var source = ReadRepositoryFile("src", "DesktopBox", "Resources", file);

            source.Should().Contain("x:Key=\"box.menu.lock\"");
            source.Should().Contain("x:Key=\"box.menu.unlock\"");
            source.Should().Contain("x:Key=\"tip.lockBox\"");
            source.Should().Contain("x:Key=\"tip.unlockBox\"");
            source.Should().Contain("x:Key=\"dialog.deleteBox.clearThenDelete\"");
            source.Should().NotContain("x:Key=\"dialog.deleteBox.nonEmpty\"");
        }
    }

    // ---- 辅助 ----

    private static string BoxControlXaml =>
        ReadRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml");

    private static string MethodBody(string fileName, string startMarker, string endMarker)
    {
        var source = ReadRepositoryFile("src", "DesktopBox", "Controls", fileName);
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        return source[start..end];
    }

    private static BoxViewModel TabbedVm(double width)
    {
        var box = new Box
        {
            Name = "B",
            Width = width,
            Height = 200,
            Tabs =
            {
                new BoxTab { Name = "标签一" },
                new BoxTab { Name = "标签二" }
            }
        };
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
