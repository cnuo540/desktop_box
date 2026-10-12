using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace DesktopBox.Services;

/// <summary>桌面图标"按项"显隐实现(见 IDesktopIconLayoutService)。
///
/// 隐藏 = 把该项的图标位置挪到桌面视图下方远处(桌面视图不滚动,挪出可视区就等于看不见);
/// 显示 = 挪回记录下来的原位置。原位置会落盘到 icon-layout.json —— 程序崩溃/重启后依然能还原,
/// 不会把用户的图标布局弄丢。</summary>
public class DesktopIconLayoutService : IDesktopIconLayoutService
{
    /// <summary>停靠位置:虚拟桌面下方这么远处(视图坐标原点=虚拟桌面左上角,所以必然不可见)。</summary>
    private const int ParkGapY = 4000;

    /// <summary>停靠区里每个图标横向间隔,避免叠在一起(也便于读回校验)。</summary>
    private const int ParkGapX = 80;

    private const string HideDesktopIconsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons";

    private static readonly string[] SystemIconClsids = SystemIcons.Definitions
        .Select(d => d.Clsid.StartsWith("::", StringComparison.Ordinal) ? d.Clsid[2..] : d.Clsid)
        .ToArray();

    private readonly IDesktopIconPositions _positions;
    private readonly string _storePath;
    private readonly Dictionary<string, (int X, int Y)> _parked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _priorSystemIcons = new(StringComparer.OrdinalIgnoreCase);

    public DesktopIconLayoutService(IDesktopIconPositions positions, string? storePath = null)
    {
        _positions = positions ?? throw new ArgumentNullException(nameof(positions));
        _storePath = storePath ?? Models.AppPaths.IconLayoutPath;
        Load();
    }

    public int ParkedCount => _parked.Count;

    public int Park(IEnumerable<string> paths)
    {
        if (paths is null) return 0;

        var parkY = ParkY();
        var moved = 0;
        foreach (var path in paths.Where(IsDesktopItem).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_parked.ContainsKey(path)) continue;          // 已经挪走了
            if (!_positions.TryParse(path, out var item)) continue;   // 不在桌面上/解析不了 → 不管它

            try
            {
                // 先读原位置:读不到就绝不挪 —— 宁可"没隐藏",也不能挪了回不来
                if (!_positions.TryGet(item, out var x, out var y)) continue;
                if (y >= parkY) continue;                      // 已经在停靠区

                if (!_positions.TrySet(item, moved * ParkGapX, parkY)) continue;
                _parked[path] = (x, y);
                moved++;
            }
            finally
            {
                _positions.Free(item);
            }
        }

        if (moved > 0) Save();
        LogService.Info("DesktopIconLayout.Park", $"moved={moved} parked={_parked.Count}");
        return moved;
    }

    public bool Restore(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !_parked.TryGetValue(path, out var origin)) return false;
        if (!_positions.TryParse(path, out var item)) return false;

        try
        {
            if (!_positions.TrySet(item, origin.X, origin.Y))
                return false;   // 保留记录,下次再试
            _parked.Remove(path);
            Save();
            return true;
        }
        finally
        {
            _positions.Free(item);
        }
    }

    public int RestoreAll()
    {
        var restored = 0;
        foreach (var path in _parked.Keys.ToList())
        {
            if (Restore(path)) restored++;
        }

        LogService.Info("DesktopIconLayout.RestoreAll", $"restored={restored} left={_parked.Count}");
        return restored;
    }

    public void SetSystemIconsVisible(bool visible)
    {
        // 这 4 个系统图标走注册表(HideDesktopIcons),与文件图标那套"挪位置"无关。
        // 隐藏前先记住原值,显示时还原成用户原来的设置。
        try
        {
            foreach (var scope in new[] { "NewStartPanel", "ClassicStartMenu" })
            {
                using var key = Registry.CurrentUser.CreateSubKey($@"{HideDesktopIconsKey}\{scope}");
                if (key is null) continue;

                foreach (var clsid in SystemIconClsids)
                {
                    if (visible)
                    {
                        if (_priorSystemIcons.TryGetValue(clsid, out var prior))
                            key.SetValue(clsid, prior, RegistryValueKind.DWord);
                        else
                            key.DeleteValue(clsid, throwOnMissingValue: false);
                    }
                    else
                    {
                        if (!_priorSystemIcons.ContainsKey(clsid) && key.GetValue(clsid) is int existing)
                            _priorSystemIcons[clsid] = existing;
                        key.SetValue(clsid, 1, RegistryValueKind.DWord);   // 1 = 隐藏
                    }
                }
            }

            if (visible) _priorSystemIcons.Clear();
            Save();
            LogService.Info("DesktopIconLayout.SystemIcons", $"visible={visible}");
        }
        catch (Exception ex)
        {
            LogService.Warn("DesktopIconLayout.SystemIcons", LogService.Truncate(ex.Message, 160));
        }
    }

    /// <summary>是否"直接位于桌面"的文件/文件夹(桌面文件夹可能被重定向,这里按系统给出的实际路径判断)。</summary>
    internal static bool IsDesktopItem(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var parent = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(parent)) return false;

            return SameFolder(parent, Environment.GetFolderPath(Environment.SpecialFolder.Desktop))
                || SameFolder(parent, Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
        }
        catch
        {
            return false;
        }
    }

    private static bool SameFolder(string a, string b) =>
        !string.IsNullOrEmpty(b)
        && string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
            StringComparison.OrdinalIgnoreCase);

    private static int ParkY()
    {
        try
        {
            return (int)Math.Ceiling(SystemParameters.VirtualScreenHeight) + ParkGapY;
        }
        catch
        {
            return 4000;
        }
    }

    // ---- 落盘(icon-layout.json)----

    private sealed class Store
    {
        public List<ParkedItem> Items { get; set; } = new();
        public Dictionary<string, int> PriorSystemIcons { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ParkedItem
    {
        public string Path { get; set; } = string.Empty;
        public int X { get; set; }
        public int Y { get; set; }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_storePath)) return;
            var store = JsonSerializer.Deserialize<Store>(File.ReadAllText(_storePath));
            if (store is null) return;

            foreach (var item in store.Items.Where(i => !string.IsNullOrWhiteSpace(i.Path)))
                _parked[item.Path] = (item.X, item.Y);
            foreach (var pair in store.PriorSystemIcons)
                _priorSystemIcons[pair.Key] = pair.Value;
        }
        catch (Exception ex)
        {
            LogService.Warn("DesktopIconLayout.Load", LogService.Truncate(ex.Message, 160));
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var store = new Store
            {
                Items = _parked.Select(p => new ParkedItem { Path = p.Key, X = p.Value.X, Y = p.Value.Y }).ToList(),
                PriorSystemIcons = new Dictionary<string, int>(_priorSystemIcons, StringComparer.OrdinalIgnoreCase)
            };
            File.WriteAllText(_storePath, JsonSerializer.Serialize(store));
        }
        catch (Exception ex)
        {
            LogService.Warn("DesktopIconLayout.Save", LogService.Truncate(ex.Message, 160));
        }
    }
}
