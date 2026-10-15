using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopBox.Views;

/// <summary>轻量提示条:替代"整理完成"那类需要点确定才消失的模态框。
///
/// 屏幕右下角(任务栏上方)、不抢焦点、2 秒后自动消失,点一下也能提前关掉;
/// 整窗不透明度 50%(按用户要求)—— 提一句就走,不挡着桌面。
/// 同一时刻只留一条:新提示来了就把旧的换掉。</summary>
internal sealed class ToastWindow
{
    private const double ToastWidth = 380;
    private const int AutoCloseMs = 2000;
    /// <summary>默认不透明度(整理完成那条用 50%)。</summary>
    private const double DefaultOpacity = 0.5;
    /// <summary>底部留白:避开任务栏(含自动隐藏时弹出来的那条)。</summary>
    private const double BottomMargin = 72;

    private static ToastWindow? _current;

    private readonly Window _window;
    private readonly DispatcherTimer _timer;

    /// <param name="opacity">整窗不透明度(0~1)。不同提示可以不一样:整理完成 0.5、刷新图标 0.9。</param>
    public static void Show(string message, double opacity = DefaultOpacity)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        _current?.CloseNow();
        _current = new ToastWindow(message, Math.Clamp(opacity, 0.1, 1.0));
    }

    private ToastWindow(string message, double opacity)
    {
        var text = new TextBlock
        {
            Text = message.Trim(),
            Foreground = Brushes.White,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 12, 16, 12)
        };

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x20, 0x20, 0x20)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = text
        };

        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,          // 不抢焦点:弹出来不打断你正在做的事
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.Height,
            // App.xaml 合并了 Wpf.Ui 的控件字典,它给 Window 带了最小尺寸(460x320),不压掉小提示条会被撑大
            MinWidth = 0,
            MinHeight = 0,
            Width = ToastWidth,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Opacity = opacity,
            Content = card
        };
        _window.MouseLeftButtonDown += (_, _) => CloseNow();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AutoCloseMs) };
        _timer.Tick += (_, _) => CloseNow();

        try
        {
            _window.Show();
            PlaceBottomRight();
            _timer.Start();
            Services.LogService.Info("Toast.Show",
                $"opacity={opacity} autoClose={AutoCloseMs}ms rect=({_window.Left:F0},{_window.Top:F0},{_window.ActualWidth:F0}x{_window.ActualHeight:F0}) " +
                $"text={Services.LogService.Truncate(text.Text)}");
        }
        catch (Exception ex)
        {
            App.LogError(ex, "ToastWindow.Show");
        }
    }

    /// <summary>贴到工作区右下角。底部留白给够 —— 本机任务栏是自动隐藏的,WorkArea 报的是整屏,
    /// 只留 16px 的话提示条会落在任务栏弹出来的那条带子里被挡住(实测)。</summary>
    private void PlaceBottomRight()
    {
        try
        {
            var area = SystemParameters.WorkArea;
            var height = _window.ActualHeight > 0 ? _window.ActualHeight : 80;
            _window.Left = Math.Max(area.Left + 8, area.Right - ToastWidth - 16);
            _window.Top = Math.Max(area.Top + 8, area.Bottom - height - BottomMargin);
        }
        catch
        {
            // 定位失败就随它去,反正 2 秒后自己消失
        }
    }

    private void CloseNow()
    {
        try
        {
            _timer.Stop();
            _window.Close();
        }
        catch { }

        if (ReferenceEquals(_current, this)) _current = null;
    }
}
