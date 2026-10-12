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

namespace DesktopBox.Tests;

/// <summary>重命名盒子/标签、以及"条目拖到桌面后桌面图标要可见"的回归测试。</summary>
public class RenameAndDesktopVisibilityTests
{
    // ---- 重命名盒子 ----

    [Fact]
    public void RenamingABox_RefreshesTheDisplayedHeaderImmediately()
    {
        var vm = new BoxViewModel(new Box { Name = "旧名字" });
        var notified = new List<string?>();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        vm.Rename("新名字");

        vm.Name.Should().Be("新名字");
        vm.Header.Should().Be("新名字");
        // 界面绑定的是 Header:不通知它就"改了名界面还是旧名字"
        notified.Should().Contain(nameof(BoxViewModel.Header));
    }

    [Fact]
    public void RenamingAProgramGeneratedBox_DropsItsKeySoTheUsersNameWins()
    {
        // 程序生成的盒子(box.organize 等)显示名走语言翻译,不改名的话怎么改都还是"桌面整理"
        var vm = new BoxViewModel(new Box { Name = "桌面整理", Key = "box.organize" });

        vm.Rename("我的资料");

        vm.Key.Should().BeNull();                       // 脱离程序身份
        vm.Name.Should().Be("我的资料");
        vm.Header.Should().Be("我的资料");               // 不再走翻译
        vm.ToModel().Key.Should().BeNull();             // 持久化后依然
        vm.ToModel().Name.Should().Be("我的资料");
    }

    // ---- 重命名标签 ----

    [Fact]
    public void RenamingATab_RefreshesTheTabHeaderImmediately()
    {
        var tab = new BoxTab { Name = "旧标签" };
        var notified = new List<string?>();
        tab.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        tab.Rename("新标签");

        tab.Name.Should().Be("新标签");
        tab.Header.Should().Be("新标签");
        notified.Should().Contain(nameof(BoxTab.Header));
    }

    [Fact]
    public void RenamingACategoryTab_DropsTheCategoryKey()
    {
        var tab = new BoxTab { Name = "文档", Key = "cat.docs" };

        tab.Rename("工作文档");

        tab.Key.Should().BeNull();
        tab.Header.Should().Be("工作文档");
    }

    [Fact]
    public void RenameStillNotifiesHeaderWhenTheKeyWasTheOnlyThingThatChanged()
    {
        // 用户输入的名字恰好和 Name 一样,但 Key 被清掉了:也要刷新,否则界面还是旧翻译名
        var tab = new BoxTab { Name = "文档", Key = "cat.docs" };
        var notified = new List<string?>();
        tab.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        tab.Rename("文档");

        notified.Should().Contain(nameof(BoxTab.Header));
        tab.Header.Should().Be("文档");
    }

    // ---- 接线守卫 ----

    [Fact]
    public void RenameHandlers_GoThroughTheRenameMethods()
    {
        var code = ReadRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml.cs");

        code.Should().Contain("Vm.Rename(newName.Trim())");
        code.Should().Contain("tab.Rename(name.Trim())");
        // 不能再直接只赋 Name(那样界面不会刷新,程序生成的盒子/标签还会继续显示翻译名)
        code.Should().NotContain("Vm.Name = newName.Trim()");
        code.Should().NotContain("Vm.SelectedTab.Name = name.Trim()");
    }

    [Fact]
    public void DraggingAnItemOutToTheDesktop_ShowsDesktopIconsSoTheFileStaysVisible()
    {
        var tile = ReadRepositoryFile("src", "DesktopBox", "Controls", "ItemTile.xaml.cs");
        var start = tile.IndexOf("DroppedOnDesktop == true", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        var end = tile.IndexOf("OnTileMouseUp", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        var body = tile[start..end];

        body.Should().Contain("EnsureDesktopIconsVisible()");
        // 先把桌面图标显示出来,再把这个条目从盒子里移除
        body.IndexOf("EnsureDesktopIconsVisible()", StringComparison.Ordinal)
            .Should().BeLessThan(body.IndexOf("RemoveFromBox()", StringComparison.Ordinal));
    }

    // ---- 改名后界面(渲染出来的文字)必须真的变 ----

    [Fact]
    public void RenamingABox_UpdatesTheRenderedTitleText()
    {
        RunOnSta(() =>
        {
            var vm = new BoxViewModel(new Box { Name = "旧名字", Width = 320, Height = 200 });
            var control = new BoxControl { DataContext = vm, Width = 320, Height = 200 };
            Arrange(control);

            var title = (TextBlock)control.FindName("HeaderTitle")!;
            title.Text.Should().Be("旧名字");

            vm.Rename("新名字");

            title.Text.Should().Be("新名字", "改了名标题栏要立刻显示新名字(旧版这里还是旧名字)");
        });
    }

    [Fact]
    public void RenamingATab_UpdatesTheRenderedTabLabel()
    {
        RunOnSta(() =>
        {
            var box = new Box
            {
                Name = "盒子",
                Width = 320,
                Height = 200,
                Tabs = { new BoxTab { Name = "旧标签" }, new BoxTab { Name = "另一个" } }
            };
            var vm = new BoxViewModel(box);
            var control = new BoxControl { DataContext = vm, Width = 320, Height = 200 };
            Arrange(control);

            var tabList = (ListBox)control.FindName("TabList")!;
            Labels(tabList).Should().Contain("旧标签");

            vm.Tabs[0].Rename("新标签");

            Labels(tabList).Should().Contain("新标签");
            Labels(tabList).Should().NotContain("旧标签");
        });
    }

    private static List<string> Labels(DependencyObject root) =>
        FindVisualChildren<TextBlock>(root).Select(t => t.Text).ToList();

    private static void Arrange(FrameworkElement element)
    {
        var available = new Size(element.Width, element.Height);
        element.Measure(available);
        element.Arrange(new Rect(available));
        element.UpdateLayout();
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
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
}
