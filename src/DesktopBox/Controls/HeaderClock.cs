using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace DesktopBox.Controls;

/// <summary>盒子标题栏时钟:全局共用一次刷新,每分钟把当前时间(HH:mm)推给所有盒子标题栏。
/// 做成静态单例是为了避免"每个盒子各起一个定时器"——盒子多起来时定时器数量会线性增长。
/// 线程安全说明:只在 UI 线程创建/刷新(DispatcherTimer 的 Tick),属性读取是字符串原子引用,无需加锁。</summary>
public sealed class HeaderClock : INotifyPropertyChanged
{
    /// <summary>全局唯一实例(首次绑定标题栏时创建)。</summary>
    public static HeaderClock Instance { get; } = new();

    private readonly DispatcherTimer? _timer;

    private HeaderClock()
    {
        CurrentTime = Format(DateTime.Now);

        // 只在真正的 WPF 应用里起定时器:单元测试/设计器没有 Application,也就不会留下永不停止的定时器。
        if (Application.Current is null) return;

        try
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background, Application.Current.Dispatcher)
            {
                // 每秒检查一次、只在"分钟"变化时才通知:单纯用 60s 间隔会随启动时刻漂移,
                // 显示的分钟可能慢将近一分钟(例如 14:32:50 起,到 14:33:49 才跳到 14:33)。
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += (_, _) => Refresh();
            _timer.Start();
        }
        catch
        {
            // 起不来就只显示构造时的时间:时钟是装饰,不能影响盒子功能。
        }
    }

    /// <summary>当前显示的时间文本(HH:mm)。</summary>
    public string CurrentTime { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>按当前时间刷新。</summary>
    public void Refresh() => Refresh(DateTime.Now);

    /// <summary>按指定时间刷新(仅用于测试注入);分钟没变则不通知,避免无意义的重绘。</summary>
    internal void Refresh(DateTime now)
    {
        var text = Format(now);
        if (text == CurrentTime) return;

        CurrentTime = text;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentTime)));
    }

    /// <summary>时钟文本格式:24 小时制 HH:mm(中英文界面通用)。</summary>
    public static string Format(DateTime time) => time.ToString("HH:mm");
}
