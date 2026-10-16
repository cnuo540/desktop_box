using System;

namespace DesktopBox.Services;

/// <summary>右键菜单里由我们追加的额外命令(shell 自己不会给这些)。</summary>
[Flags]
public enum ShellMenuCommands
{
    /// <summary>什么都不加,只用 shell 原生菜单。</summary>
    None = 0,

    /// <summary>追加"从盒子移除"。</summary>
    RemoveFromBox = 1,

    /// <summary>追加"重命名"(改的是真实文件名,由宿主弹输入框执行)。</summary>
    Rename = 2
}

public enum ShellMenuRunStatus
{
    Completed,
    RemoveFromBox,
    Rename,
    TargetDeleted,
    StartFailed,
    IsolationUnavailable,
    Crashed,
    TimedOut
}

public readonly record struct ShellMenuRunResult(ShellMenuRunStatus Status, int? ExitCode = null);

public interface IShellMenuRunner : IDisposable
{
    Task<ShellMenuRunResult> ShowAsync(
        string path,
        int screenX,
        int screenY,
        ShellMenuCommands commands = ShellMenuCommands.RemoveFromBox,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 终止当前正在运行的助手进程(若有),并让它尽快退出。
    /// 供调用方在"助手卡住不弹菜单"时改走兜底菜单 —— 见 <see cref="ShellMenuRunStatus.TimedOut"/>。
    /// </summary>
    void KillActive();

    /// <summary>
    /// 后台预热 shell 扩展(只建菜单再销毁,不显示界面)。
    /// 实测扩展首次加载要 8 秒以上,提前付掉这段代价,用户第一次右键就不必干等。
    /// </summary>
    void WarmUp(IEnumerable<string> paths);
}
