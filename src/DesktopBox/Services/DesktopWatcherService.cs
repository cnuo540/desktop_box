using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace DesktopBox.Services;

/// <summary>基于 FileSystemWatcher 的桌面项监听(见 IDesktopWatcherService)。
/// 只看文件名的增/删/改名,不看属性变化 —— 免得我们自己改属性又把自己触发一遍。
/// 桌面层照着这个事件重画:桌面上多了或少了什么,它都能立刻跟上。</summary>
public sealed class DesktopWatcherService : IDesktopWatcherService
{
    /// <summary>去抖窗口:一次拖进来好几个文件时合并成一批。</summary>
    private const int DebounceMs = 400;

    private readonly List<FileSystemWatcher> _watchers = new();
    /// <summary>待上报的变化,按路径去重:同一路径后到的事件覆盖先到的。
    /// 例如"新建后立刻改名"会合并成一条 Renamed(而不是先 Created 再 Renamed —— 那会先收进盒子又改路径)。</summary>
    private readonly Dictionary<string, DesktopItemChange> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly Timer _debounce;
    private bool _disposed;

    public event EventHandler<DesktopItemChange>? DesktopItemChanged;

    public DesktopWatcherService()
    {
        _debounce = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        foreach (var dir in DesktopFolders()) Watch(dir);
    }

    private static IEnumerable<string> DesktopFolders()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (!string.IsNullOrWhiteSpace(desktop)) yield return desktop;

        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        if (!string.IsNullOrWhiteSpace(common)
            && !string.Equals(common, desktop, StringComparison.OrdinalIgnoreCase))
            yield return common;
    }

    private void Watch(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;

            var watcher = new FileSystemWatcher(dir)
            {
                // 必须同时订阅 DirectoryName:只给 FileName 的话**新建文件夹不会触发任何事件**
                // (实测:在桌面上新建文件夹,桌面层毫无反应,直到别的原因触发一次重画才出现)。
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            watcher.Created += (_, e) => Enqueue(new DesktopItemChange(DesktopChangeKind.Created, e.FullPath));
            watcher.Deleted += (_, e) => Enqueue(new DesktopItemChange(DesktopChangeKind.Deleted, e.FullPath));
            watcher.Renamed += (_, e) => Enqueue(new DesktopItemChange(DesktopChangeKind.Renamed, e.FullPath, e.OldFullPath));
            _watchers.Add(watcher);
            LogService.Info("DesktopWatcher.Watch", $"dir={dir}");
        }
        catch (Exception ex)
        {
            LogService.Warn("DesktopWatcher.Watch", LogService.Truncate(ex.Message, 160));
        }
    }

    private void Enqueue(DesktopItemChange change)
    {
        if (_disposed || string.IsNullOrWhiteSpace(change.Path)) return;
        lock (_gate)
        {
            // 改名:旧路径的待办作废(它已经不存在了),否则会先报一条对旧路径的 Created/Deleted。
            if (change.Kind == DesktopChangeKind.Renamed && !string.IsNullOrEmpty(change.OldPath))
                _pending.Remove(change.OldPath);

            _pending[change.Path] = change;
            _debounce.Change(DebounceMs, Timeout.Infinite);
        }
    }

    private void Flush()
    {
        DesktopItemChange[] batch;
        lock (_gate)
        {
            if (_disposed || _pending.Count == 0) return;
            batch = new DesktopItemChange[_pending.Count];
            _pending.Values.CopyTo(batch, 0);
            _pending.Clear();
        }

        foreach (var change in batch)
        {
            try
            {
                DesktopItemChanged?.Invoke(this, change);
            }
            catch (Exception ex)
            {
                LogService.Warn("DesktopWatcher.Raise", LogService.Truncate(ex.Message, 160));
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _debounce.Dispose();
        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch { /* 退出中 */ }
        }
        _watchers.Clear();
    }
}
