using System.ComponentModel;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DesktopBox.Controls;
using DesktopBox.Models;
using DesktopBox.Services;
using DesktopBox.ViewModels;
using FluentAssertions;

namespace DesktopBox.Tests;

/// <summary>标题栏时钟(平时显示/悬停隐藏)与"操作按钮悬停才出现"的回归测试。</summary>
public class HeaderClockAndHoverTests
{
    [Fact]
    public void Clock_FormatsAs24HourMinutes()
    {
        HeaderClock.Format(new DateTime(2026, 1, 2, 9, 5, 0)).Should().Be("09:05");
        HeaderClock.Format(new DateTime(2026, 1, 2, 23, 59, 59)).Should().Be("23:59");
        HeaderClock.Format(new DateTime(2026, 1, 2, 0, 0, 0)).Should().Be("00:00");
    }

    [Fact]
    public void Clock_NotifiesOnlyWhenTheMinuteChanges()
    {
        var clock = HeaderClock.Instance;
        var notifications = 0;
        void Handler(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(HeaderClock.CurrentTime)) notifications++;
        }

        clock.PropertyChanged += Handler;
        try
        {
            clock.Refresh(new DateTime(2026, 1, 1, 9, 15, 5));
            var afterFirstRefresh = notifications;      // 已是该分钟则 0,否则 1

            clock.Refresh(new DateTime(2026, 1, 1, 9, 15, 59));   // 同一分钟
            notifications.Should().Be(afterFirstRefresh);
            clock.CurrentTime.Should().Be("09:15");

            clock.Refresh(new DateTime(2026, 1, 1, 9, 16, 0));    // 跨分钟
            notifications.Should().Be(afterFirstRefresh + 1);
            clock.CurrentTime.Should().Be("09:16");
        }
        finally
        {
            clock.PropertyChanged -= Handler;
        }
    }

    [Fact]
    public void RenderedHeader_ShowsARealClockText()
    {
        RunOnSta(() =>
        {
            var vm = new BoxViewModel(new Box { Name = "B", Width = 300, Height = 200 });
            var control = new BoxControl { DataContext = vm, Width = 300, Height = 200 };
            Arrange(control);

            var clock = (TextBlock)control.FindName("HeaderClock")!;
            clock.Text.Should().MatchRegex(@"^\d{2}:\d{2}$");
        });
    }

    [Fact]
    public void BoxHeader_LaysOutClockLeftNameCenterActionsRightWithoutAccentChip()
    {
        var xaml = ReadRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml");
        var clockAt = xaml.IndexOf("x:Name=\"HeaderClock\"", StringComparison.Ordinal);
        var nameAt = xaml.IndexOf("Text=\"{Binding Header}\"", StringComparison.Ordinal);
        var actionsAt = xaml.IndexOf("x:Name=\"HeaderActions\"", StringComparison.Ordinal);
        var lockAt = xaml.IndexOf("x:Name=\"LockBtn\"", StringComparison.Ordinal);
        var collapseAt = xaml.IndexOf("x:Name=\"CollapseBtn\"", StringComparison.Ordinal);
        var eyeAt = xaml.IndexOf("x:Name=\"ToggleIconsBtn\"", StringComparison.Ordinal);

        clockAt.Should().BeGreaterThanOrEqualTo(0);
        nameAt.Should().BeGreaterThan(clockAt);        // 时间在名称左边
        actionsAt.Should().BeGreaterThan(nameAt);      // 操作按钮在名称右边
        lockAt.Should().BeGreaterThan(actionsAt);
        collapseAt.Should().BeGreaterThan(lockAt);
        eyeAt.Should().BeGreaterThan(collapseAt);      // 眼睛按钮在最右

        // 原先名称左侧的蓝色小方块(色标)已按要求移除
        // (缩放手柄模板里还有透明 Rectangle,所以只断言"色标那个"的特征)
        xaml.Should().NotContain("Fill=\"#4FC3F7\"");
        xaml.Should().NotContain("RadiusX=\"2\" RadiusY=\"2\"");
    }

    [Fact]
    public void HeaderClockFont_IsLargerThanTheBoxName()
    {
        var xaml = ReadRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml");

        var clockSize = FontSizeOf(Block(xaml, "x:Name=\"HeaderClock\"", "</TextBlock>"));
        var nameSize = FontSizeOf(Block(xaml, "Text=\"{Binding Header}\"", "/>"));

        clockSize.Should().BeGreaterThan(nameSize, "时间字体要比盒子名称更大更醒目");
    }

    private static string Block(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        return source[start..end];
    }

    private static double FontSizeOf(string element)
    {
        var match = System.Text.RegularExpressions.Regex.Match(element, "FontSize=\"([0-9.]+)\"");
        match.Success.Should().BeTrue($"元素里应有 FontSize:{element}");
        return double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void BoxHeader_ShowsTheClockOnlyWhenEnabledAndFadesItOutOnHover()
    {
        var xaml = ReadRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml");
        var clockStart = xaml.IndexOf("x:Name=\"HeaderClock\"", StringComparison.Ordinal);
        var clockBlock = xaml[clockStart..xaml.IndexOf("</TextBlock>", clockStart, StringComparison.Ordinal)];
        var actionsBlock = xaml[xaml.IndexOf("x:Name=\"HeaderActions\"", StringComparison.Ordinal)..];

        // 时间:由本盒子的 ShowClock 开关决定显示与否(默认关闭,避免所有盒子都挂个时钟)
        clockBlock.Should().Contain("Visibility=\"{Binding ShowClock, Converter={StaticResource BoolVis}}\"");
        clockBlock.Should().Contain("{Binding Source={x:Static ctl:HeaderClock.Instance}, Path=CurrentTime}");
        // 悬停淡出走 Opacity:不与上面的 Visibility 绑定打架,且保留占位(名称悬停时不跳动)
        clockBlock.Should().Contain("<Setter Property=\"Opacity\" Value=\"0\"/>");
        clockBlock.Should().NotContain("Property=\"Visibility\"");

        // 操作按钮:默认 Hidden → 悬停 Visible
        actionsBlock.Should().Contain("<Setter Property=\"Visibility\" Value=\"Hidden\"/>");
        actionsBlock.Should().Contain("<Setter Property=\"Visibility\" Value=\"Visible\"/>");
    }

    [Fact]
    public void ClockVisibility_FollowsThePerBoxSwitch()
    {
        RunOnSta(() =>
        {
            var vm = new BoxViewModel(new Box { Name = "B", Width = 300, Height = 200 });
            var control = new BoxControl { DataContext = vm, Width = 300, Height = 200 };
            Arrange(control);

            var clock = (TextBlock)control.FindName("HeaderClock")!;
            clock.Visibility.Should().Be(Visibility.Collapsed, "时间默认关闭");

            vm.ToggleShowClock();
            clock.Visibility.Should().Be(Visibility.Visible);

            vm.ToggleShowClock();
            clock.Visibility.Should().Be(Visibility.Collapsed);
        });
    }

    [Fact]
    public void ShowClock_IsPersistedPerBoxAndDefaultsToOff()
    {
        var vm = new BoxViewModel(new Box { Name = "B" });
        vm.ShowClock.Should().BeFalse();
        vm.ToModel().ShowClock.Should().BeFalse();

        vm.ToggleShowClock();
        vm.ToModel().ShowClock.Should().BeTrue();
        new BoxViewModel(vm.ToModel()).ShowClock.Should().BeTrue();

        var path = Path.Combine(Path.GetTempPath(), $"dbx_clock_{Guid.NewGuid():N}.json");
        new JsonStoreService(path).Save(new AppConfig
        {
            Boxes = new() { new Box { Name = "带时钟", ShowClock = true } }
        });
        new JsonStoreService(path).Load().Boxes[0].ShowClock.Should().BeTrue();

        var legacy = Path.Combine(Path.GetTempPath(), $"dbx_clock_legacy_{Guid.NewGuid():N}.json");
        File.WriteAllText(legacy, @"{""boxes"":[{""name"":""旧盒子""}]}");
        new JsonStoreService(legacy).Load().Boxes[0].ShowClock.Should().BeFalse("旧配置默认不显示时间");
    }

    [Fact]
    public void BoxContextMenu_TogglesTheClockForThatBoxOnly()
    {
        var xaml = ReadRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml");
        xaml.Should().Contain("Header=\"{DynamicResource box.menu.showClock}\" Click=\"OnToggleShowClock\"");
        xaml.Should().Contain("IsCheckable=\"True\" IsChecked=\"{Binding ShowClock, Mode=OneWay}\"");

        var code = ReadRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml.cs");
        code.Should().Contain("private void OnToggleShowClock");
        code.Should().Contain("Vm.ToggleShowClock();");
        code.Should().Contain("MainVm.ScheduleSave();");

        foreach (var file in new[] { "Strings.zh-CN.xaml", "Strings.en-US.xaml" })
            ReadRepositoryFile("src", "DesktopBox", "Resources", file).Should().Contain("x:Key=\"box.menu.showClock\"");
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
