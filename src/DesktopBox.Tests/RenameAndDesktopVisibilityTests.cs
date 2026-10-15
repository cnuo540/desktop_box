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
    public void DraggingAnItemOutToTheDesktop_GoesThroughHandleItemDroppedOnDesktop()
    {
        var tile = ReadRepositoryFile("src", "DesktopBox", "Controls", "ItemTile.xaml.cs");
        var start = tile.IndexOf("DroppedOnDesktop == true", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        var end = tile.IndexOf("OnTileMouseUp", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        var body = tile[start..end];

        // 出盒逻辑集中在 ViewModel:复制到桌面/恢复可见/通知 Shell 都在那里。
        // 磁贴代码里仍禁止直接 SetVisible —— 整层开关不能在拖放回调里乱动(用户反馈过刷满桌面),
        // ShowLayerAndHideCollected 负责"只让拖出项可见、其余继续按属性隐藏"。
        body.Should().Contain("HandleItemsDroppedOnDesktop");
        body.Should().NotContain("EnsureDesktopIconsVisible");
        body.Should().NotContain("SetVisible");
    }

    [Fact]
    public void HandleItemDroppedOnDesktop_OnlyTakesItOutOfTheBox_AndRerendersTheLayer()
    {
        var vm = ReadRepositoryFile("src", "DesktopBox", "ViewModels", "MainViewModel.cs");

        vm.Should().Contain("public void HandleItemDroppedOnDesktop(BoxItem item)");
        vm.Should().Contain("RemoveItemAnywhere(item)");
        vm.Should().Contain("RaiseLooseItemsChanged()");
        // 落下效果是 Copy:不在桌面的文件复制过去,否则桌面上没有它
        vm.Should().Contain("PlaceOnDesktopIfMissing(source)");

        var start = vm.IndexOf("public void HandleItemDroppedOnDesktop(BoxItem item)", StringComparison.Ordinal);
        var end = vm.IndexOf("private static string? PlaceOnDesktopIfMissing", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        var body = vm[start..end];

        // 出盒不再需要动图标层、也不必再通知 shell —— 桌面图标现在由我们自己的桌面层画
        body.Should().NotContain("SetVisible");
        body.Should().NotContain("SHChangeNotify");
        body.Should().NotContain("SetAttributes");
    }

    [Fact]
    public void HidingIcons_UsesTheNativeLayerSwitch_AndNeverTouchesFileAttributes()
    {
        var vm = ReadRepositoryFile("src", "DesktopBox", "ViewModels", "MainViewModel.cs");

        var start = vm.IndexOf("public void SetDesktopIconsVisible(bool visible)", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        var end = vm.IndexOf("public void RefreshDesktopIconsState", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        var body = vm[start..end];

        body.Should().Contain("_desktopIcons.SetVisible(visible)", "隐藏 = 关掉 explorer 的桌面图标层");
        body.Should().Contain("RestoreAttributeHidingOnce()", "旧版按属性隐藏留下的属性和设置要还回去");

        // 整个 ViewModel 里都不该再有"按属性隐藏"那套 —— 它会把目录窗口一起藏掉(用户明确不要)
        vm.Should().NotContain("HideByAttribute");
        vm.Should().NotContain("SetExplorerShowHidden(false)");
        vm.Should().NotContain("SetFileAttributes");
    }

    [Fact]
    public void DesktopLayer_ClipsItselfToTheIconCells_SoTheRestOfTheDesktopStaysExplorers()
    {
        // 桌面层的窗口区域必须是"图标矩形并集":图标之间的空白处照旧归 explorer,
        // 否则右键桌面菜单、从资源管理器往桌面拖文件都会失效。
        var layer = ReadRepositoryFile("src", "DesktopBox", "Views", "DesktopLayerWindow.cs");

        layer.Should().Contain("SetWindowRgn");
        layer.Should().Contain("RGN_OR");
        layer.Should().Contain("HWND_BOTTOM", "桌面层排在最底,盒子窗口压在它上面");
        layer.Should().Contain("Background = null", "空白处不该有我们的任何绘制");
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
