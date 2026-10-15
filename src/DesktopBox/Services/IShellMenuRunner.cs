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
}
