using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopBox.Views;

/// <summary>图标名字旁边的"就地改名"输入框(explorer 那种):贴在标签位置的一个小窗,
/// 回车确认、Esc 取消、点到别处也算确认(和 explorer 一致)。
///
/// 为什么用独立的顶层小窗,而不是直接在桌面层/磁贴里塞一个 TextBox:
/// 桌面层是"不可激活的子窗口"(WS_EX_NOACTIVATE),键盘输入根本到不了它;
/// 顶层小窗可以自己激活、自己拿键盘焦点,行为也就和 explorer 一样了。
///
/// 两个实测坑(改这里之前先读):
/// ① 尺寸不能靠 SizeToContent —— 它在"无边框 + 透明"的窗口上不生效;而且 App.xaml 合并了
///    Wpf.Ui 的控件字典,给 Window 带了 460x320 的最小尺寸,小输入框会被硬撑大。
///    所以尺寸自己算死(MinWidth/MinHeight=0 + 显式 Width/Height),Show 之后再用
///    SetWindowPos 按物理像素钉在标签位置(Left/Top 在无边框窗上会飘)。
/// ② "点到别处就确认"不能只靠 Deactivated / 失焦 —— 这个窗在桌面环境下不一定真拿到激活状态,
///    那两个事件就可能不来。用低级鼠标钩子直接看点击落在哪最可靠。</summary>
internal sealed class InlineRenameBox
{
    private readonly Window _window;
    private readonly TextBox _input;
    private readonly Action<string?> _onFinished;
    private readonly Native.LowLevelMouseHook _clickAwayHook;
    private bool _finished;
    private bool _activated;

    /// <param name="currentName">当前名字(带扩展名,和 explorer 显示的一致)</param>
    /// <param name="anchor">要盖住的标签矩形(设备像素,屏幕坐标)</param>
    /// <param name="fontSize">和图标名字一样大,换上去才不跳</param>
    /// <param name="onFinished">确认时给新名字,取消时给 null</param>
    public InlineRenameBox(string currentName, Rect anchor, double fontSize, Action<string?> onFinished)
    {
        _onFinished = onFinished;
        var scale = DpiScale();

        // 输入框就贴着名字那一行的大小(别撑成一大块);名字太长时按文本用量放宽,别把字截掉
        var labelWidth = Math.Max(64, anchor.Width / scale.X + 16);
        var width = Math.Min(Math.Max(labelWidth, MeasureTextWidth(currentName, fontSize) + 22), 480);
        var height = Math.Max(20, anchor.Height / scale.Y + 4);

        _input = new TextBox
        {
            Text = currentName,
            FontSize = fontSize,
            Width = width,
            Height = height,
            TextAlignment = TextAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(2, 0, 2, 0),
            Background = Brushes.White,          // 和 explorer 一样:白底黑字,一眼看出在改名
            Foreground = Brushes.Black,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x8C, 0xC8, 0xF0)),
            BorderThickness = new Thickness(1),
            CaretBrush = Brushes.Black
        };
        _input.KeyDown += OnKeyDown;
        _input.LostKeyboardFocus += (_, _) => Finish(_input.Text, "失焦");
        _input.Loaded += (_, _) =>
        {
            _input.Focus();
            _input.SelectAll();
            // 只选中主文件名,扩展名不选(explorer 的默认行为)
            var keepExtension = ExtensionStart(_input.Text);
            if (keepExtension > 0) _input.Select(0, keepExtension);
        };

        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.Manual,
            MinWidth = 0,
            MinHeight = 0,
            Width = width + 2,
            Height = height + 2,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Topmost = true,
            Content = _input
        };

        // 点到别处(左键点桌面、点别的窗口)也算确认
        _clickAwayHook = new Native.LowLevelMouseHook();
        _clickAwayHook.ButtonDown += (x, y) =>
        {
            if (_finished || IsInsideBox(x, y)) return;
            // 钩子回调要立刻返回,别在里面做文件操作
            _window.Dispatcher.BeginInvoke(new Action(() => Finish(_input.Text, "点到别处")));
        };

        // 兜底:真收到失焦/失去激活也确认(比如用键盘切走)
        _window.Activated += (_, _) => _activated = true;
        _window.Deactivated += (_, _) =>
        {
            if (_activated) Finish(_input.Text, "窗口失去激活");
        };

        try
        {
            _window.Show();
            _window.Activate();
            PlaceAt(anchor, Math.Round((width + 2) * scale.X), Math.Round((height + 2) * scale.Y));
            var handle = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
            var actualPx = handle == IntPtr.Zero || !Native.User32.GetWindowRect(handle, out var rect)
                ? "(?)"
                : $"{rect.Right - rect.Left}x{rect.Bottom - rect.Top}";
            Services.LogService.Info("InlineRename.Show",
                $"name={Services.LogService.Truncate(currentName)} anchor=({anchor.Left:F0},{anchor.Top:F0},{anchor.Width:F0}x{anchor.Height:F0}) " +
                $"calc={width:F0}x{height:F0} window={_window.Width:F0}x{_window.Height:F0} px={actualPx}");
        }
        catch (Exception ex)
        {
            App.LogError(ex, "InlineRenameBox.Show");
            Finish(null, "显示失败");
        }
    }

    /// <summary>元素在屏幕上的矩形(设备像素)。就地把输入框贴在标签位置时用它当锚点。</summary>
    public static Rect ScreenRectOf(FrameworkElement element)
    {
        try
        {
            var topLeft = element.PointToScreen(new Point(0, 0));
            var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
            var rect = new Rect(topLeft, bottomRight);
            return rect.Width > 1 && rect.Height > 1 ? rect : Rect.Empty;
        }
        catch
        {
            return Rect.Empty;
        }
    }

    /// <summary>把输入框摆到标签位置(物理像素,不走 WPF 的 DIP 换算,免得跨屏/缩放下飘或被样式撑大)。</summary>
    private void PlaceAt(Rect anchor, double pixelWidth, double pixelHeight)
    {
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
            if (handle == IntPtr.Zero) return;

            // HWND_TOPMOST = -1
            Native.User32.SetWindowPos(handle, new IntPtr(-1),
                (int)Math.Round(anchor.Left), (int)Math.Round(anchor.Top),
                Math.Max(1, (int)pixelWidth), Math.Max(1, (int)pixelHeight),
                Native.User32.SWP_SHOWWINDOW);
        }
        catch (Exception ex)
        {
            App.LogError(ex, "InlineRenameBox.PlaceAt");
        }
    }

    /// <summary>这个点是不是落在输入框自己身上(物理像素,屏幕坐标)。</summary>
    private bool IsInsideBox(int screenX, int screenY)
    {
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
            if (handle == IntPtr.Zero || !Native.User32.GetWindowRect(handle, out var rect)) return false;

            return screenX >= rect.Left && screenX < rect.Right && screenY >= rect.Top && screenY < rect.Bottom;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>按当前字体量一下文本宽度,决定输入框要多宽(名字长的时候不至于截断)。</summary>
    private static double MeasureTextWidth(string text, double fontSize)
    {
        try
        {
            var typeface = new Typeface("Segoe UI");
            var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, fontSize, Brushes.Black, 1.0);
            return formatted.Width;
        }
        catch
        {
            return 0;
        }
    }

    private static (double X, double Y) DpiScale()
    {
        try
        {
            var source = PresentationSource.FromVisual(Application.Current?.MainWindow);
            var matrix = source?.CompositionTarget?.TransformToDevice;
            if (matrix is { } m && m.M11 > 0 && m.M22 > 0) return (m.M11, m.M22);
        }
        catch { }
        return (1, 1);
    }

    /// <summary>主文件名的长度(即"扩展名从哪里开始");.lnk/.url 这类一律整个选中。</summary>
    private static int ExtensionStart(string name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        var extension = System.IO.Path.GetExtension(name);
        if (string.IsNullOrEmpty(extension)) return name.Length;
        // 像 "报告.2026.docx" 这种:只把最后一段当扩展名,和 explorer 一致
        return Math.Max(0, name.Length - extension.Length);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                Finish(_input.Text, "回车");
                break;
            case Key.Escape:
                e.Handled = true;
                Finish(null, "Esc");
                break;
        }
    }

    private void Finish(string? newName, string reason)
    {
        if (_finished) return;
        _finished = true;

        Services.LogService.Info("InlineRename.Finish",
            newName is null ? $"取消({reason})" : $"确认({reason}) name={Services.LogService.Truncate(newName)}");

        _clickAwayHook.Dispose();
        try { _window.Close(); } catch { }
        try { _onFinished(newName); } catch (Exception ex) { App.LogError(ex, "InlineRenameBox.Finish"); }
    }
}
