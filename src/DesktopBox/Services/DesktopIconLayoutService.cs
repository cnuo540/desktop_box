using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace DesktopBox.Services;

/// <summary>桌面图标"按属性隐藏"的收尾实现(见 IDesktopIconLayoutService)。
///
/// 历史:曾经用"给已收纳项加 Hidden 属性 + 关掉资源管理器的『显示隐藏项目』"来藏图标。
/// 实测两个硬伤:①那个全局开关被 explorer 缓存在内存里,外部改注册表它不理会,
/// 用户从界面把开关打开后几百个图标会一起涌回桌面;②它把文件夹窗口也一起影响了
/// (用户要的是"目录里全看得见、桌面上不显示")。
/// 现在改成关掉 explorer 的桌面图标层 + 自绘桌面层,这里只负责把当年的痕迹擦干净。</summary>
public class DesktopIconLayoutService : IDesktopIconLayoutService
{
    /// <summary>旧版"挪到屏幕外"的位置偏移:超过它就认定是历史遗留的停靠图标,启动时拉回来。</summary>
    private const int LegacyParkGapY = 4000;

    private const string HideDesktopIconsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons";
    private const string ExplorerAdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    /// <summary>资源管理器"显示隐藏项目"的开关名。<c>1</c>=显示,<c>2</c>=不显示。</summary>
    private const string ShowHiddenValueName = "Hidden";

    private static readonly string[] SystemIconClsids = SystemIcons.Definitions
        .Select(d => d.Clsid.StartsWith("::", StringComparison.Ordinal) ? d.Clsid[2..] : d.Clsid)
        .ToArray();

    private readonly IDesktopIconPositions _positions;
    private readonly string _storePath;
    private readonly Dictionary<string, FileAttributes> _hiddenAttributes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _priorSystemIcons = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>旧版遗留的"挪到屏幕外"记录:迁移时按这些路径把图标拉回可视区。</summary>
    private readonly List<string> _legacyParked = new();

    /// <summary>"显示隐藏项目"的原始值(当年改动前记下的,迁移时还原)。</summary>
    private int? _priorShowHidden;

    public DesktopIconLayoutService(IDesktopIconPositions positions, string? storePath = null)
    {
        _positions = positions ?? throw new ArgumentNullException(nameof(positions));
        _storePath = storePath ?? Models.AppPaths.IconLayoutPath;
        Load();
    }

    public bool Restore(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var restored = RestoreAttributes(path);
        var repaired = RepairLegacyParkedIcon(path);
        if (restored || repaired) Save();
        return restored || repaired;
    }

    public int RestoreAll()
    {
        var paths = _hiddenAttributes.Keys
            .Concat(_legacyParked)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var restored = paths.Count(Restore);
        _legacyParked.Clear();
        LogService.Info("DesktopIconLayout.RestoreAll", $"restored={restored}/{paths.Count} left={_hiddenAttributes.Count}");
        return restored;
    }

    /// <summary>还原资源管理器"显示隐藏项目"。第一次改动前记住原值,<paramref name="show"/>=true 时还原。</summary>
    public void SetExplorerShowHidden(bool show)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(ExplorerAdvancedKey);
            if (key is null) return;

            if (show)
            {
                if (_priorShowHidden is int prior)
                {
                    key.SetValue(ShowHiddenValueName, prior, RegistryValueKind.DWord);
                    _priorShowHidden = null;
                    Save();
                    LogService.Info("DesktopIconLayout.ShowHidden", $"restored={prior}");
                }
                return;
            }

            if (_priorShowHidden is null)
                _priorShowHidden = key.GetValue(ShowHiddenValueName) is int current ? current : 1;
            // 2 = 不显示隐藏项目
            key.SetValue(ShowHiddenValueName, 2, RegistryValueKind.DWord);
            Save();
        }
        catch (Exception ex)
        {
            LogService.Warn("DesktopIconLayout.ShowHidden", LogService.Truncate(ex.Message, 160));
        }
    }

    public void SetSystemIconsVisible(bool visible)
    {
        // 这 4 个系统图标走注册表(HideDesktopIcons),与桌面图标层无关。
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
            return (int)Math.Ceiling(SystemParameters.VirtualScreenHeight) + LegacyParkGapY;
        }
        catch
        {
            return LegacyParkGapY;
        }
    }

    /// <summary>把旧版挪到屏幕外的图标拉回可视区。记录可能已经丢失,所以按坐标本身判断。</summary>
    private bool RepairLegacyParkedIcon(string path)
    {
        if (!_positions.TryParse(path, out var item)) return false;
        try
        {
            if (!_positions.TryGet(item, out _, out var y) || y < ParkY()) return false;
            if (!_positions.TrySet(item, 24, 24)) return false;
            LogService.Info("DesktopIconLayout.RepairLegacy", $"path={path}");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Warn("DesktopIconLayout.RepairLegacy", LogService.Truncate(ex.Message, 160));
            return false;
        }
        finally
        {
            _positions.Free(item);
        }
    }

    private bool RestoreAttributes(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return false;

            // 旧版本或外部脚本可能已经把条目标为 Hidden,但没有留下属性快照。
            // 条目明确从盒子拖出时必须可见:有快照则恢复原属性,没有快照则清除 Hidden。
            var current = File.GetAttributes(path);
            var restored = _hiddenAttributes.TryGetValue(path, out var original)
                ? original
                : current & ~FileAttributes.Hidden;
            File.SetAttributes(path, restored);
            _hiddenAttributes.Remove(path);
            return (current & FileAttributes.Hidden) != 0 || current != restored;
        }
        catch (Exception ex)
        {
            LogService.Warn("DesktopIconLayout.RestoreAttribute", LogService.Truncate(ex.Message, 160));
            return false;
        }
    }

    // ---- 落盘(icon-layout.json)----

    private sealed class Store
    {
        public Dictionary<string, int> HiddenAttributes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> PriorSystemIcons { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>"显示隐藏项目"的原始值(未改动过则为 null)。</summary>
        public int? PriorShowHidden { get; set; }
        /// <summary>旧版遗留的停靠记录(只有路径有用),读出来是为了启动自愈。</summary>
        public List<LegacyItem> Items { get; set; } = new();
    }

    private sealed class LegacyItem
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

            foreach (var pair in store.HiddenAttributes)
                _hiddenAttributes[pair.Key] = (FileAttributes)pair.Value;
            foreach (var pair in store.PriorSystemIcons)
                _priorSystemIcons[pair.Key] = pair.Value;
            foreach (var item in store.Items.Where(i => !string.IsNullOrWhiteSpace(i.Path)))
                _legacyParked.Add(item.Path);
            _priorShowHidden = store.PriorShowHidden;
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
                HiddenAttributes = _hiddenAttributes.ToDictionary(p => p.Key, p => (int)p.Value, StringComparer.OrdinalIgnoreCase),
                PriorSystemIcons = new Dictionary<string, int>(_priorSystemIcons, StringComparer.OrdinalIgnoreCase),
                PriorShowHidden = _priorShowHidden,
                Items = _legacyParked.Select(p => new LegacyItem { Path = p }).ToList()
            };
            File.WriteAllText(_storePath, JsonSerializer.Serialize(store));
        }
        catch (Exception ex)
        {
            LogService.Warn("DesktopIconLayout.Save", LogService.Truncate(ex.Message, 160));
        }
    }
}
