using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace DesktopBox.Services;

public sealed class ShellMenuProcessRunner : IShellMenuRunner
{
    public const int RemoveFromBoxExitCode = 0x7000;
    public const int TargetDeletedExitCode = 0x7002;
    public const int RenameExitCode = 0x7003;

    private readonly string _helperPath;
    private readonly TimeSpan _timeout;
    private readonly IReadOnlyList<string> _prefixArguments;
    private readonly object _processGate = new();
    private readonly HashSet<Process> _activeProcesses = new();
    private readonly ShellMenuJob _job = new();
    private bool _disposed;

    public ShellMenuProcessRunner()
        : this(Path.Combine(AppContext.BaseDirectory, "DesktopBox.ShellMenu.exe"))
    {
    }

    public ShellMenuProcessRunner(
        string helperPath,
        TimeSpan? timeout = null,
        IReadOnlyList<string>? prefixArguments = null)
    {
        _helperPath = helperPath;
        // 上限要够长:选"属性"时 helper 会一直泵消息陪属性框到用户关掉它为止,
        // 早点杀会把开着的属性框一起带走(菜单本身用得再久也就几十秒)。
        _timeout = timeout ?? TimeSpan.FromMinutes(30);
        _prefixArguments = prefixArguments ?? [];
    }

    public async Task<ShellMenuRunResult> ShowAsync(
        string path,
        int screenX,
        int screenY,
        ShellMenuCommands commands = ShellMenuCommands.RemoveFromBox,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _helperPath,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in _prefixArguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add(path);
        startInfo.ArgumentList.Add(screenX.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(screenY.ToString(CultureInfo.InvariantCulture));
        // 第 4 个参数只在"不止默认的从盒子移除"时才带:老 helper 只认 3 个参数,
        // 默认路径保持不带,升级不同步也不会把菜单打坏。
        if (commands != ShellMenuCommands.RemoveFromBox)
            startInfo.ArgumentList.Add(RenderCommands(commands));

        Process? process = null;
        try
        {
            bool isolated;
            lock (_processGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                // 关键:必须在 CreateProcess 之前授权,否则 helper 里的 SetForegroundWindow 会失败,
                // TrackPopupMenuEx 立刻返回 0 —— 表现就是"右键毫无反应"(菜单根本没弹)。
                // 桌面层窗口是 WS_EX_NOACTIVATE 的,本身不是前台进程,所以这里必须显式放行。
                try
                {
                    if (!Native.User32.AllowSetForegroundWindow(Native.User32.ASFW_ANY))
                    {
                        LogService.Warn("ShellMenu.AllowForeground",
                            $"AllowSetForegroundWindow 失败 err={Marshal.GetLastWin32Error()}(菜单可能弹不出来)");
                    }
                }
                catch (Exception ex)
                {
                    LogService.Warn("ShellMenu.AllowForeground", LogService.Truncate(ex.Message, 120));
                }

                try
                {
                    process = Process.Start(startInfo);
                }
                catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or DirectoryNotFoundException)
                {
                    // helper 缺失（没跑 build_dll.bat / publish 未拷贝）：回 StartFailed 走回退菜单，别弹错。
                    LogService.Warn("ShellMenu.MissingHelper",
                        $"helper 启动失败，回退到精简菜单 path={LogService.Truncate(_helperPath)} err={ex.Message}");
                    return new(ShellMenuRunStatus.StartFailed);
                }
                if (process is null)
                    return new(ShellMenuRunStatus.StartFailed);
                _activeProcesses.Add(process);
                isolated = _job.TryAssign(process);
            }

            if (!isolated)
            {
                // 宿主已在 Job 中（VS 调试/dotnet run/企业沙箱，单进程单 Job）时 Assign 恒失败。
                // 旧逻辑直接判菜单不可用（100% 右键失败）；现降级为无 Job 运行（helper 本身已是独立进程），只记日志。
                LogService.Warn("ShellMenu.NoJob",
                    $"helper 未进隔离 Job（宿主可能已在 Job 中），降级运行 path={LogService.Truncate(path)}");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await KillAndWaitAsync(process).ConfigureAwait(false);
                return new(ShellMenuRunStatus.TimedOut);
            }

            return process.ExitCode switch
            {
                0 => new(ShellMenuRunStatus.Completed, process.ExitCode),
                RemoveFromBoxExitCode => new(ShellMenuRunStatus.RemoveFromBox, process.ExitCode),
                RenameExitCode => new(ShellMenuRunStatus.Rename, process.ExitCode),
                TargetDeletedExitCode => new(ShellMenuRunStatus.TargetDeleted, process.ExitCode),
                _ => new(ShellMenuRunStatus.Crashed, process.ExitCode)
            };
        }
        catch (OperationCanceledException)
        {
            if (process is not null) await KillAndWaitAsync(process).ConfigureAwait(false);
            throw;
        }
        catch
        {
            if (process is null)
                return new(ShellMenuRunStatus.StartFailed);
            await KillAndWaitAsync(process).ConfigureAwait(false);
            return new(ShellMenuRunStatus.Crashed);
        }
        finally
        {
            if (process is not null)
            {
                lock (_processGate) _activeProcesses.Remove(process);
                process.Dispose();
            }
        }
    }

    /// <summary>把额外命令渲染成 helper 认的逗号串;"-" 表示什么都不加。</summary>
    private static string RenderCommands(ShellMenuCommands commands)
    {
        if (commands == ShellMenuCommands.None) return "-";

        var parts = new List<string>(2);
        if (commands.HasFlag(ShellMenuCommands.Rename)) parts.Add("rename");
        if (commands.HasFlag(ShellMenuCommands.RemoveFromBox)) parts.Add("remove");
        return parts.Count == 0 ? "-" : string.Join(",", parts);
    }

    private static async Task KillAndWaitAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch
        {
            // The helper may have exited between checks or already been killed by the job object.
        }
    }

    /// <summary>
    /// 终止当前正在运行的助手进程(若有)。
    ///
    /// 为什么需要:第三方 shell 扩展(云盘/杀软之类)偶尔会让 helper 卡在 QueryContextMenu
    /// 里 —— 实测表现为 cpu=0s、无任何菜单窗口、线程全部 Wait,而它不退出就意味着调用方
    /// 永远等不到结果。调用方据此改走兜底菜单,右键才不会"没反应"。
    /// </summary>
    public void KillActive()
    {
        Process[] active;
        lock (_processGate)
        {
            active = _activeProcesses.ToArray();
        }

        foreach (var process in active)
        {
            try
            {
                if (!process.HasExited)
                {
                    LogService.Warn("ShellMenu.KillStuck",
                        $"右键助手卡住,已终止 pid={process.Id}");
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // 可能刚好自己退出了
            }
        }
    }

    /// <summary>
    /// 后台预热:让助手把 shell 扩展提前加载好,这样用户第一次右键不用等好几秒。
    ///
    /// 实测:扩展首次加载时 QueryContextMenu 要 8 秒以上,热了只要 0.3~2.5 秒。这段代价放在
    /// 启动后台付掉,比让用户右键时干等好得多。预热只建菜单再销毁,不显示界面、不触发任何动作。
    /// 失败无所谓,下次右键照走正常路径。
    /// </summary>
    public void WarmUp(IEnumerable<string> paths)
    {
        // 预热进程不进 _activeProcesses:它不是"正在弹的菜单",不该被杀掉/被当成占用。
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = _helperPath,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("--warm");
                startInfo.ArgumentList.Add(path);
                Process.Start(startInfo)?.Dispose();
            }
            catch
            {
                // helper 缺失/被占用都无所谓 —— 预热是可选的
            }
        }
    }

    public void Dispose()
    {
        Process[] active;
        lock (_processGate)
        {
            if (_disposed) return;
            _disposed = true;
            active = _activeProcesses.ToArray();
        }

        foreach (var process in active)
        {
            try
            {
                // 退出时别在调用线程（常为 UI）同步等 5s，缩到 500ms，避免关机卡死观感。
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.WaitForExit(500);
            }
            catch { }
        }
        _job.Dispose();
        GC.SuppressFinalize(this);
    }
}
