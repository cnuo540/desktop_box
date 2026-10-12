using System.ComponentModel;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DesktopBox.Controls;
using DesktopBox.Models;
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
    public void BoxHeader_LaysOutClockLeftNameCenterAccentAndActionsRight()
    {
        var xaml = ReadRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml");
        var clockAt = xaml.IndexOf("x:Name=\"HeaderClock\"", StringComparison.Ordinal);
        var nameAt = xaml.IndexOf("Text=\"{Binding Header}\"", StringComparison.Ordinal);
        var accentAt = xaml.IndexOf("Fill=\"#4FC3F7\"", StringComparison.Ordinal);
        var actionsAt = xaml.IndexOf("x:Name=\"HeaderActions\"", StringComparison.Ordinal);
        var lockAt = xaml.IndexOf("x:Name=\"LockBtn\"", StringComparison.Ordinal);
        var collapseAt = xaml.IndexOf("x:Name=\"CollapseBtn\"", StringComparison.Ordinal);
        var eyeAt = xaml.IndexOf("x:Name=\"ToggleIconsBtn\"", StringComparison.Ordinal);

        clockAt.Should().BeGreaterThanOrEqualTo(0);
        nameAt.Should().BeGreaterThan(clockAt);        // 时间在名称左边
        accentAt.Should().BeGreaterThan(nameAt);       // 色标(原左侧蓝方块)已移到名称右边
        actionsAt.Should().BeGreaterThan(accentAt);    // 操作按钮在色标右侧
        lockAt.Should().BeGreaterThan(actionsAt);
        collapseAt.Should().BeGreaterThan(lockAt);
        eyeAt.Should().BeGreaterThan(collapseAt);      // 眼睛按钮在最右
    }

    [Fact]
    public void BoxHeader_HidesActionsAndShowsClockUntilTheMouseEntersTheBox()
    {
        var xaml = ReadRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml");
        var actionsBlock = xaml[xaml.IndexOf("x:Name=\"HeaderActions\"", StringComparison.Ordinal)..];

        // 时间:默认可见 → 鼠标进盒子(展开区域)变 Hidden(保留占位,悬停时布局不跳)
        xaml.Should().Contain("{Binding Source={x:Static ctl:HeaderClock.Instance}, Path=CurrentTime}");
        xaml.Should().Contain("<Setter Property=\"Visibility\" Value=\"Visible\"/>");
        xaml.Should().Contain("<Setter Property=\"Visibility\" Value=\"Hidden\"/>");
        // 操作按钮:默认 Hidden → 悬停 Visible
        actionsBlock.Should().Contain("<Setter Property=\"Visibility\" Value=\"Hidden\"/>");
        actionsBlock.Should().Contain("<Setter Property=\"Visibility\" Value=\"Visible\"/>");
        // 触发条件统一用"鼠标是否在盒子上"
        xaml.Should().Contain("Binding=\"{Binding IsMouseOver, RelativeSource={RelativeSource AncestorType=UserControl}}\"");
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
