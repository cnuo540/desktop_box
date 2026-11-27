using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DesktopBox.Models;
using DesktopBox.Native;
using DesktopBox.Controls;
using DesktopBox.Services;
using DesktopBox.Views;

namespace DesktopBox.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IPersistenceService _store;
    private readonly IDropParserService _parser;
    private readonly IIconExtractorService _icon;
    private readonly IOrganizeService _organize;
    private readonly ICategorizerService _categorizer;
    private readonly IDesktopIconsService _desktopIcons;
    private readonly IDesktopIconLayoutService? _iconLayout;
    private readonly IDesktopWatcherService? _desktopWatcher;
    private readonly IDesktopScannerService? _scanner;
    /// <summary>旧版"按属性隐藏"的痕迹只擦一次。</summary>
    private bool _attributeHidingRestored;
    private readonly ILocalizerService _localizer;
    private readonly IShellChangeNotifierService _shellChange;
    /// <summary>刚从盒子拖到桌面的路径 → 拖出时刻。窗口期内不自动收纳(见 TryAutoCollect 说明④)。</summary>
    private readonly Dictionary<string, long> _recentEjected = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _ejectedGate = new();
    /// <summary>拖出排除窗口:比 GlobalDragTracker 的 1.5 秒宽,兜住"松手后才开始落盘的大文件复制"。</summary>
    private const long EjectExcludeMs = 60_000;
    private Timer? _debounce;
    private bool _disposed;
    private readonly object _systemIconRefreshGate = new();
    private bool _systemIconRefreshRunning;
    private List<BoxItem>? _pendingSystemIconRefreshItems;

    public event EventHandler<Exception>? PersistenceFailed;

    public ObservableCollection<BoxViewModel> Boxes { get; } = new();

    /// <summary>用于自动布局新盒子的可用画布宽度(由 MainWindow 设置)。</summary>
    public double ScreenWidth { get; set; } = 1600;

    /// <summary>全局视图模式:所有盒子/标签共享。变化时广播给所有盒子刷新。</summary>
    [ObservableProperty] private ViewMode _globalViewMode = ViewMode.Large;

    // 全局视图模式变化 → 通知所有盒子刷新其 IsLarge/.../IsTile
    partial void OnGlobalViewModeChanged(ViewMode value)
    {
        foreach (var b in Boxes) b.SyncViewMode(value);
        if (value == ViewMode.Detail) EnsureDetailFieldsForAll();
        ScheduleSave();
    }

    /// <summary>切换全局视图模式(由 BoxControl 右键菜单调用)。</summary>
    public void SetGlobalViewMode(ViewMode mode) => GlobalViewMode = mode;

    /// <summary>图标大小按钮:在大 → 中 → 小图标之间循环(列表/详细信息/平铺只能走右键菜单)。</summary>
    public void CycleIconSize() => GlobalViewMode = ViewModeText.NextIconSize(GlobalViewMode);

    /// <summary>卷起的盒子在鼠标悬停时自动展开、移开自动收起(设置项,持久化)。
    /// BoxControl 在鼠标进出时读取本值;关掉后卷起的盒子只能双击标题栏/右键菜单展开。</summary>
    [ObservableProperty] private bool _hoverExpandCollapsedBoxes = true;

    partial void OnHoverExpandCollapsedBoxesChanged(bool value) => ScheduleSave();

    /// <summary>桌面上新出现的文件自动收纳进「桌面整理」盒子(设置项,持久化)。
    /// 只在"桌面图标隐藏、由我们的桌面层管桌面"时生效 —— 图标可见时用户用的是原生桌面,
    /// 此时收纳会造成"桌面和盒子各显示一次"。开关只决定要不要做,不改变这个前置条件。</summary>
    [ObservableProperty] private bool _autoCollectNewDesktopFiles = true;

    partial void OnAutoCollectNewDesktopFilesChanged(bool value) => ScheduleSave();

    /// <summary>盒子背景的不透明度(设置项,持久化)。只作用于背景色 —— 图标和文字不跟着变淡。</summary>
    [ObservableProperty] private double _boxOpacity = 1.0;

    partial void OnBoxOpacityChanged(double value) => ScheduleSave();

    /// <summary>把新建/加载的盒子加入集合并同步当前全局视图模式。</summary>
    private void AddBoxSynced(BoxViewModel box)
    {
        box.SyncViewMode(GlobalViewMode);
        Boxes.Add(box);
    }

    /// <summary>切到详细信息时惰性填充所有盒子条目的大小/修改时间。</summary>
    private void EnsureDetailFieldsForAll()
    {
        foreach (var b in Boxes) EnsureDetailFields(b);
    }

    // 分类在标签盒子中的排列顺序
    private static readonly string[] CategoryOrderArr =
        { CategorizerService.Program, CategorizerService.Document, CategorizerService.Image,
          CategorizerService.Video, CategorizerService.Audio, CategorizerService.Shortcut,
          CategorizerService.Archive, CategorizerService.FolderCat, CategorizerService.Other };

    public MainViewModel(IPersistenceService store, IDropParserService parser,
                         IIconExtractorService icon, IOrganizeService organize,
                         ICategorizerService categorizer,
                         IDesktopIconsService desktopIcons, ILocalizerService localizer,
                         IShellChangeNotifierService shellChange,
                         IDesktopIconLayoutService? iconLayout = null,
                         IDesktopWatcherService? desktopWatcher = null,
                         IDesktopScannerService? scanner = null)
    {
        _store = store;
        _parser = parser;
        _icon = icon;
        _organize = organize;
        _categorizer = categorizer;
        _desktopIcons = desktopIcons;
        _iconLayout = iconLayout;
        _desktopWatcher = desktopWatcher;
        _scanner = scanner;
        _localizer = localizer;
        _shellChange = shellChange;
        // 语言切换后刷新所有程序生成盒子/标签的显示名(Header)
        _localizer.LanguageChanged += OnLanguageChanged;
        // 系统图标自动刷新:回收站清空/还原、此电脑盘符变化等 shell 通知 → 重新提取受影响图标
        _shellChange.SystemIconChanged += OnSystemIconChanged;
        // 桌面上新出现的文件要立刻可见(explorer 自己不会画外部新建的桌面文件);
        // 同时按类型分流:新增→尝试自动收纳、删除→撤引用、改名→引用跟着改。
        if (_desktopWatcher is not null) _desktopWatcher.DesktopItemChanged += OnDesktopItemChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshAllHeaders();

    private void OnSystemIconChanged(object? sender, EventArgs e) => RefreshSystemIcons();

    [RelayCommand]
    private void Load()
    {
        Boxes.Clear();
        var cfg = _store.Load();
        GlobalViewMode = cfg.Settings.GlobalViewMode;
        HoverExpandCollapsedBoxes = cfg.Settings.HoverExpandCollapsedBoxes;
        AutoCollectNewDesktopFiles = cfg.Settings.AutoCollectNewDesktopFiles;
        BoxOpacity = Math.Clamp(cfg.Settings.BoxOpacity, 0.1, 1.0);

        // 迁移系统图标 item 的旧 CLSID → SystemIcons.cs 当前定义。
        // 历史遗留:旧版用过的 CLSID(如网络曾是 F02C2A56,在 Win11 上已无效)仍存在老 boxes.json 里,
        // 导致图标提取和右键菜单对该项失败(E_INVALIDARG)。按 DisplayName 匹配当前定义并替换。
        var sysClsids = SystemIcons.Definitions
            .ToDictionary(d => d.Name, d => d.Clsid, StringComparer.OrdinalIgnoreCase);
        foreach (var b in cfg.Boxes)
        {
            foreach (var it in b.Items.Concat(b.Tabs.SelectMany(t => t.Items)))
            {
                if (it.Type == ItemType.SystemIcon
                    && sysClsids.TryGetValue(it.DisplayName ?? "", out var clsid)
                    && !string.Equals(it.TargetPath, clsid, StringComparison.OrdinalIgnoreCase))
                {
                    it.TargetPath = clsid;
                    it.IconCachePath = null;   // 清旧图标缓存路径,强制用新 CLSID 重新提取
                }
            }
        }

        // i18n 迁移:旧版程序生成的盒子/标签用中文 Name 当逻辑键。补上 Key,使其按当前语言显示,
        // 且逻辑匹配(找整理盒子/系统图标/分类标签)改用稳定的 Key。仅识别已知的旧中文名。
        foreach (var b in cfg.Boxes)
        {
            if (string.IsNullOrEmpty(b.Key))
            {
                if (b.Name == "桌面整理") b.Key = "box.organize";
                else if (b.Name == "系统图标") b.Key = "box.sysicons";
            }
            foreach (var t in b.Tabs)
            {
                if (string.IsNullOrEmpty(t.Key))
                {
                    if (t.Name == "系统图标") t.Key = "tab.sysicons";
                    else if (CategorizerService.LegacyZhToKey.TryGetValue(t.Name, out var catKey))
                        t.Key = catKey;
                }
            }
        }

        var all = new List<BoxItem>();
        foreach (var b in cfg.Boxes.OrderBy(b => b.Order))
        {
            var vm = new BoxViewModel(b);
            // 启动校正:旧坐标可能在屏外(换分辨率/拔屏幕),拉回可视区,避免"窗口消失找不到"
            var (x, y) = SystemParametersHelper.ClampIntoScreens(vm.X, vm.Y);
            vm.X = x; vm.Y = y;
            AddBoxSynced(vm);
            all.AddRange(vm.Items);
            foreach (var t in vm.Tabs) all.AddRange(t.Items);
        }
        // 图标缓存可能丢失(icons 目录被删/exe 被搬到别的路径):后台重提。
        // Extract 内部有 File.Exists 检查,命中则跳过、缺失则重建。
        if (all.Count > 0) ExtractIconsInBackground(all);

        // 存量残留清理:旧版本把 Office 的 ~$ 锁文件等临时文件也收进了盒子(v1.7.37 起扫描已过滤),
        // 这里把已经收进去的清掉。普通文件的失效引用仍按设计保留、由手动「刷新失效图标」处理。
        var pruned = CleanupStaleDesktopReferences();
        if (pruned > 0)
        {
            Services.LogService.Info("Desktop.Cleanup", $"启动清理临时文件引用 {pruned} 条");
            ScheduleSave();
        }
    }

    [RelayCommand]
    private void AddBox()
    {
        AddBoxSynced(new BoxViewModel(new Box
        {
            Name = $"盒子 {Boxes.Count + 1}",
            Order = Boxes.Count
        }));
        ScheduleSave();
    }

    [RelayCommand]
    private void RemoveBox(BoxViewModel? box)
    {
        if (box is null) return;
        Boxes.Remove(box);
        ScheduleSave();
    }

    public void AddItemToBox(BoxViewModel box, string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;

        var item = target.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? _parser.ParseUrl(target)
            : _parser.ParsePath(target);

        item.BoxId = box.Id;
        item.Order = box.DisplayItems.Count;   // 标签模式=当前标签,普通模式=Items
        box.DisplayItems.Add(item);
        ExtractIconAsync(item);   // 单个拖入:后台提取,不阻塞
        ScheduleSave();
        RaiseLooseItemsChanged();   // 它进盒了 → 桌面层不再画它
    }

    /// <summary>给盒子里的条目改名 —— 改的是真实的文件名/文件夹名,条目随后指向新路径。
    /// 目录窗口里立刻跟着变。返回是否改名成功。</summary>
    public bool RenameItem(BoxItem item, string? newName)
    {
        if (item is null) return false;
        if (item.Type is ItemType.SystemIcon or ItemType.Url) return false;
        if (item.TargetPath.StartsWith("::", StringComparison.Ordinal)) return false;

        var path = item.TargetPath;
        var name = (newName ?? string.Empty).Trim();
        var dest = TryRenamePath(path, name, out var error);
        if (dest is null)
        {
            ReportRenameFailure(name, error);
            return false;
        }

        item.TargetPath = dest;
        item.DisplayName = OrganizeDisplayName(dest);
        item.IconCachePath = null;      // 名字变了:图标按新路径重取
        ExtractIconAsync(item);
        ScheduleSave();
        Services.LogService.Info("Item.Rename", $"{Path.GetFileName(path)} -> {name}");
        return true;
    }

    /// <summary>给桌面上没进盒子的散落项改名(它没有 BoxItem,只有路径)。返回新路径。</summary>
    public string? RenameDesktopPath(string path, string? newName)
    {
        var name = (newName ?? string.Empty).Trim();
        var dest = TryRenamePath(path, name, out var error);
        if (dest is null)
        {
            ReportRenameFailure(name, error);
            return null;
        }

        Services.LogService.Info("Desktop.Rename", $"{Path.GetFileName(path)} -> {name}");
        RaiseLooseItemsChanged();       // 桌面层照着新名字重画
        return dest;
    }

    /// <summary>改真实文件名。成功返回新路径;失败返回 null 并用 <paramref name="error"/> 说明原因
    /// (invalid / exists / "" 表示没什么可做)。</summary>
    private static string? TryRenamePath(string path, string name, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || name.Length == 0) return null;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "invalid";
            return null;
        }

        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent)) return null;

        var dest = Path.Combine(parent, name);
        if (string.Equals(dest, path, StringComparison.OrdinalIgnoreCase)) return null;
        if (File.Exists(dest) || Directory.Exists(dest))
        {
            error = "exists";
            return null;
        }

        try
        {
            if (Directory.Exists(path)) Directory.Move(path, dest);
            else File.Move(path, dest);
            return dest;
        }
        catch (Exception ex)
        {
            App.LogError(ex, "MainViewModel.TryRenamePath");
            return null;
        }
    }

    private void ReportRenameFailure(string name, string error)
    {
        var message = error switch
        {
            "invalid" => _localizer["dialog.renameItem.invalid"],
            "exists" => string.Format(_localizer["dialog.renameItem.exists"], name),
            _ => null
        };
        if (message is null) return;

        Services.LogService.Warn("Item.Rename", message);
        // 没有 UI 上下文时(单测)只记日志:ShowDialog 会把调用线程一直挂住。
        if (Application.Current is not null) InputDialog.Inform(message);
    }

    /// <summary>从任意盒子(含任意标签)移除一个条目。它不再"已收纳"→ 桌面层会重新把它画出来。</summary>
    public void RemoveItemAnywhere(BoxItem item)
    {
        if (item is null) return;
        foreach (var box in Boxes)
        {
            if (box.Items.Remove(item)) { RaiseLooseItemsChanged(); ScheduleSave(); return; }
            foreach (var t in box.Tabs)
                if (t.Items.Remove(item)) { RaiseLooseItemsChanged(); ScheduleSave(); return; }
        }
    }

    /// <summary>清理目标已不存在的本地文件/文件夹引用(系统图标与网址保留)。返回移除数量。</summary>
    public int PruneMissingItems()
    {
        var missing = AllItems()
            .Where(IsMissingLocalTarget)
            .ToList();
        if (missing.Count == 0) return 0;

        foreach (var item in missing)
            RemoveItemAnywhere(item);
        return missing.Count;
    }

    /// <summary>判断条目是否为本地路径且目标当前不存在(已删/移走)。</summary>
    public static bool IsMissingLocalTarget(BoxItem item)
    {
        if (item.Type is ItemType.SystemIcon or ItemType.Url) return false;
        var path = item.TargetPath;
        if (string.IsNullOrWhiteSpace(path)) return true;
        if (path.StartsWith("::", StringComparison.Ordinal)) return false;
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            return !File.Exists(path) && !Directory.Exists(path);
        }
        catch
        {
            // 无法探测时保守保留,避免网络盘短暂离线被误删。
            return false;
        }
    }

    // ---- 选中态 + 批量操作(跨盒子):所有 BoxItem.IsSelected 汇总 ----

    /// <summary>把一批路径移到回收站(等同 explorer 里按 Delete),并把盒子里指向它们的条目一并撤掉。
    /// 返回真正处理掉的数量。**进回收站、可还原**,不做永久删除。
    ///
    /// v1.7.37 两处修正(现象都是"按 Delete 没反应"):
    /// ① 失败原因以前只写日志、界面无反馈(文件被 Excel 占着时就是静默失败)→ 现在用提示条说明;
    /// ② 目标全都不存在时以前直接 return 0、连日志都没有 → 现在按"处理成功"清掉引用并提示。</summary>
    public int MoveToRecycleBin(IEnumerable<string>? paths)
    {
        var all = (paths ?? Enumerable.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (all.Count == 0) return 0;

        // 真实文件才进回收站;系统图标(::CLSID)与网址是虚拟对象,没有文件可删 ——
        // 但用户按 Delete 的意图是"从盒子里拿掉",所以它们要走"撤引用"这条线。
        // 旧实现把 :: 直接过滤掉,requested 变空后 return 0 静默返回 → 按 Delete 完全没反应,
        // 于是"系统图标"标签永远清不空、永远删不掉。
        var requested = all
            .Where(p => !p.StartsWith("::", StringComparison.Ordinal))
            .Where(p => !p.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var virtualCount = all.Count - requested.Count;

        var existing = new List<string>();
        var missing = 0;
        if (requested.Count > 0)
        {
            existing = requested.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            missing = requested.Count - existing.Count;

            if (existing.Count > 0)
            {
                var rc = Shell32.MoveToRecycleBin(existing, IntPtr.Zero);
                if (rc != 0)
                {
                    Services.LogService.Warn("Item.Delete", $"移到回收站失败 rc={rc}: {existing.Count} 项");
                    ToastWindow.Show(string.Format(_localizer[Shell32.DescribeDeleteFailure(rc)], rc), opacity: 0.9);
                    return 0;   // 文件还在,引用也保留(混合选择整体回滚)
                }

                Services.LogService.Info("Item.Delete", $"已移到回收站 {existing.Count} 项");
            }
        }

        // 引用清理:真删掉的 + 本来就不存在的 + 虚拟项,都从盒子里撤掉
        foreach (var path in all)
        {
            var stale = AllItems()
                .Where(i => string.Equals(i.TargetPath, path, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var item in stale) RemoveItemAnywhere(item);
        }

        RaiseLooseItemsChanged();
        ScheduleSave();

        if (requested.Count == 0 && virtualCount > 0)
        {
            // 只动了虚拟项:必须说一声,否则又是"按了没反应"
            Services.LogService.Info("Item.Delete", $"移除虚拟引用 {virtualCount} 项");
            ToastWindow.Show(string.Format(_localizer["dialog.delete.virtualOnly"], virtualCount), opacity: 0.9);
        }
        else if (existing.Count == 0 && missing > 0)
        {
            // 一个都没真删掉(文件早没了)也要说一声,否则用户仍觉得"按了没反应"
            ToastWindow.Show(_localizer["dialog.delete.alreadyGone"], opacity: 0.9);
        }

        return all.Count;
    }

    /// <summary>按条目 Id 找条目(跨盒子/跨标签)。盒内拖拽重排时用它认出"拖的是哪一条"。</summary>
    public BoxItem? FindItemById(Guid id) => AllItems().FirstOrDefault(i => i.Id == id);

    /// <summary>盒内重排(整组):把这几条按原顺序挪到指定插入位置。返回是否真的动了。</summary>
    public bool MoveItemsToIndex(IReadOnlyList<BoxItem> items, int targetIndex)
    {
        if (items is null || items.Count == 0) return false;
        if (items.Count == 1) return MoveItemToIndex(items[0], targetIndex);

        var moved = false;
        var index = targetIndex;
        foreach (var item in items)
        {
            if (MoveItemToIndex(item, index))
            {
                moved = true;
                index++;                      // 下一条紧挨着它
            }
        }

        return moved;
    }

    /// <summary>把一组条目挪到另一个盒子(按落点顺序插进去)。返回真正挪动的条数。</summary>
    public int MoveItemsToBox(IReadOnlyList<BoxItem> items, BoxViewModel target, int targetIndex)
    {
        if (items is null || items.Count == 0 || target is null) return 0;

        var moved = 0;
        var index = targetIndex;
        foreach (var item in items)
        {
            if (item is null) continue;
            if (item.BoxId == target.Id) continue;      // 本来就在这个盒子里

            RemoveItemAnywhere(item);
            item.BoxId = target.Id;
            var list = target.DisplayItems;
            var at = Math.Clamp(index, 0, list.Count);
            list.Insert(at, item);
            index = at + 1;
            moved++;
        }

        if (moved > 0)
        {
            Services.LogService.Info("Box.MoveIn",
                $"{moved} 项 → {Services.LogService.Truncate(target.Header)}");
            ScheduleSave();
            RaiseLooseItemsChanged();
        }

        return moved;
    }

    /// <summary>盒内重排:把条目挪到指定插入位置(只在它所属盒子的当前标签里动)。
    /// <paramref name="targetIndex"/> 是"要插到第几个之前"的语义,和拖放落点一致。返回是否真的动了。</summary>
    public bool MoveItemToIndex(BoxItem item, int targetIndex)
    {
        if (item is null) return false;

        foreach (var box in Boxes)
        {
            var items = box.DisplayItems;
            var from = items.IndexOf(item);
            if (from < 0) continue;

            var to = targetIndex;
            if (to > from) to--;                        // 它自己被摘出去后,后面的索引前移
            to = Math.Clamp(to, 0, items.Count - 1);
            if (to == from) return false;

            items.Move(from, to);
            ScheduleSave();
            Services.LogService.Info("Box.Reorder",
                $"{from} -> {to} box={Services.LogService.Truncate(box.Header)}");
            return true;
        }

        return false;
    }

    /// <summary>遍历所有盒子(含标签)的条目。选中操作/批量操作用。</summary>
    public IEnumerable<BoxItem> AllItems()
    {
        foreach (var box in Boxes)
        {
            foreach (var it in box.Items) yield return it;
            foreach (var t in box.Tabs)
                foreach (var it in t.Items) yield return it;
        }
    }

    /// <summary>清除所有选中(点空白/单选新项时调用)。</summary>
    public void ClearSelection()
    {
        foreach (var it in AllItems())
            if (it.IsSelected) it.IsSelected = false;
    }

    /// <summary>处理单个磁贴点击的选中逻辑:普通点击=单选(清其它、选当前),Ctrl=切换(多选)。</summary>
    public void HandleTileClick(BoxItem clicked, bool ctrl)
    {
        if (ctrl)
        {
            clicked.IsSelected = !clicked.IsSelected;
            return;
        }
        // 普通点击:若已有多选,只保留当前;否则正常单选
        var selected = GetSelectedItems();
        if (selected.Count > 1 || (selected.Count == 1 && selected[0] != clicked))
        {
            ClearSelection();
            clicked.IsSelected = true;
        }
        else
        {
            // 当前是唯一选中或都没选:切换
            clicked.IsSelected = !clicked.IsSelected;
        }
    }

    /// <summary>获取所有选中的条目。</summary>
    public List<BoxItem> GetSelectedItems() => AllItems().Where(i => i.IsSelected).ToList();

    /// <summary>批量从盒子移除选中的条目(不删文件本体)。
    /// 按 TargetPath 全删:同一文件可能在盒子多个标签里有重复条目(历史整理遗留),
    /// 只移除选中那条会留下幽灵条目,导致整理时该文件不被当成新文件而无法回来。</summary>
    public void RemoveSelected()
    {
        var selected = GetSelectedItems();
        if (selected.Count == 0) return;
        var paths = selected.Select(i => i.TargetPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        RemoveItemsByPath(paths);
        ScheduleSave();
    }

    /// <summary>移除盒子中 TargetPath 命中 paths 的所有条目(跨盒子、跨标签)。</summary>
    private void RemoveItemsByPath(HashSet<string> paths)
    {
        foreach (var box in Boxes)
        {
            RemoveMatching(box.Items, paths);
            foreach (var t in box.Tabs) RemoveMatching(t.Items, paths);
        }
    }

    private static void RemoveMatching(System.Collections.Generic.IList<BoxItem> items, HashSet<string> paths)
    {
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (paths.Contains(items[i].TargetPath))
                items.RemoveAt(i);
        }
    }

    // ---- 一键整理(增量):只把桌面新文件分类并入「桌面整理」标签盒子 ----
    [RelayCommand]
    private void Organize()
    {
        // 找整理盒子:manifest 指向 → 失效则回退找现有「桌面整理」标签盒子 → 都没有才新建。
        // 回退查找可防止 manifest 丢失/损坏时重复建盒子。(一键整理与自动收纳共用)
        var box = GetOrCreateOrganizeBox(out bool firstTime);

        // 去重:整理盒子历史可能存在同一文件的重复条目(旧版整理逻辑/多次手动拖入),
        // 导致移除一条后该文件仍"存在"于盒子另一标签,整理时无法重新收录("移除后只回来部分")。
        // 整理前按 TargetPath 跨标签去重,每个文件只保留首次出现的条目。
        DedupOrganizeBox(box);

        // 重新归类:分类规则变化(如新增 .py 归文档)后,历史条目可能停在错误的标签。
        // 按当前 Categorizer 规则把每个文件移到正确标签,避免"改了分类旧文件还留在其他标签"。
        ReclassifyOrganizeBox(box);

        // 增量:扫描 → 排除已在整理盒子里的 → 分类并入对应标签
        var scanned = _organize.ScanAndCategorize();
        var existing = new HashSet<string>(
            box.Tabs.SelectMany(t => t.Items).Select(i => i.TargetPath),
            StringComparer.OrdinalIgnoreCase);
        var newEntries = scanned.Where(s => !existing.Contains(s.Path)).ToList();

        var allItems = new List<BoxItem>();
        BoxTab? firstChangedTab = null;   // 第一个收到新文件的标签:整理后切到它,让用户立即看到结果
        foreach (var g in newEntries.GroupBy(e => e.Category).OrderBy(g => CategoryOrder(g.Key)))
        {
            var tab = box.Tabs.FirstOrDefault(t => t.Key == g.Key) ?? NewTab(box, g.Key);
            firstChangedTab ??= tab;
            foreach (var it in g.OrderBy(e => System.IO.Path.GetFileName(e.Path)))
            {
                var item = _parser.ParsePath(it.Path);
                item.BoxId = box.Id;
                item.DisplayName = OrganizeDisplayName(it.Path);
                item.Order = tab.Items.Count;
                tab.Items.Add(item);
                allItems.Add(item);
            }
        }

        // 系统图标并进「快捷方式」标签(不再单独建标签:用户删掉后下次整理又会回来)
        bool sysAdded = EnsureSystemIconsInShortcutTab(box, allItems);

        // 切到第一个有新文件的标签;无新增时保持首个标签。避免新增文件在其它标签而用户以为"没回来"。
        box.SelectedTab = firstChangedTab ?? box.Tabs.FirstOrDefault();
        Save();
        Services.LogService.Info("Organize.Done",
            $"new={newEntries.Count} boxes={Boxes.Count} organizeBox={box.Id} tabs={box.Tabs.Count}");
        if (allItems.Count > 0) ExtractIconsInBackground(allItems);

        // 整理后把桌面图标藏起来(桌面层只画未收纳的项),并告知如何还原。
        bool hidIcons = false;
        if (DesktopIconsVisible)
        {
            SetDesktopIconsVisible(false);
            hidIcons = true;
        }
        else
        {
            RaiseLooseItemsChanged();   // 已处于隐藏状态:刚收录的这些项要从桌面层消失
        }

        string msg;
        if (newEntries.Count == 0)
        {
            msg = firstTime ? _localizer["dialog.organize.empty.first"]
                : (sysAdded ? _localizer["dialog.organize.empty.sysAdded"] : _localizer["dialog.organize.empty.done"]);
        }
        else
        {
            msg = string.Format(_localizer["dialog.organize.done"],
                newEntries.Count,
                sysAdded ? _localizer["dialog.organize.sysIconsTag"] : "");
        }
        if (hidIcons) msg += _localizer["dialog.organize.hidIcons"];
        // 整理结果是"成功类"提示 → 用 2 秒自动消失的提示条,不再要求点确定(用户要求)
        ToastWindow.Show(msg);
    }

    /// <summary>找到(或新建)「桌面整理」盒子,并把 manifest 指向它。一键整理与自动收纳共用。
    /// <paramref name="created"/> = 本次是新建的(一键整理要用它决定提示语)。</summary>
    private BoxViewModel GetOrCreateOrganizeBox(out bool created)
    {
        Guid? knownId = _organize.GetOrganizeBoxId();
        BoxViewModel? found = knownId.HasValue ? Boxes.FirstOrDefault(b => b.Id == knownId.Value) : null;
        found ??= Boxes.FirstOrDefault(b => b.Key == "box.organize");

        created = found is null;
        var box = found ?? new BoxViewModel(new Box
        {
            Key = "box.organize",
            Name = _localizer["box.organize"],
            Width = 300, Height = 380, X = 40, Y = 40
        });
        if (created) AddBoxSynced(box);

        _organize.RecordBoxIds(new[] { box.Id });   // 始终刷新,确保 manifest 指向当前整理盒子
        return box;
    }

    /// <summary>启动时清掉<b>临时文件</b>的引用(~$ 锁文件、*.tmp)。
    ///
    /// 只清这一类,不清"文件已消失"的普通引用 —— 后者是设计如此:引用保留、由用户用右键
    /// 「刷新失效图标」决定要不要清(见使用说明)。临时文件则不同:它们根本不该被收进来
    /// (v1.7.37 起 ScanDesktop 已过滤),留着的都是旧版本收进来的残留,清掉才对。
    /// 仍然只碰桌面路径,网络盘/其它目录的引用一律不动。</summary>
    private int CleanupStaleDesktopReferences()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        // 桌面目录本身都不可用(盘被拔/未就绪)时一个都不清:宁可留着,不能误删
        if (!Directory.Exists(desktop) && !Directory.Exists(common)) return 0;

        var stale = AllItems().Where(IsTransientDesktopReference).ToList();
        foreach (var item in stale) RemoveItemAnywhere(item);
        return stale.Count;
    }

    private static bool IsTransientDesktopReference(BoxItem item)
    {
        if (item.Type is ItemType.SystemIcon or ItemType.Url) return false;

        var path = item.TargetPath;
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith("::", StringComparison.Ordinal)) return false;
        if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
        if (!DesktopIconLayoutService.IsDesktopItem(path)) return false;   // 只管桌面路径

        return DesktopItemPolicy.IsTransientName(Path.GetFileName(path));
    }

    /// <summary>新建分类标签并加入盒子。catKey 是分类稳定 key(cat.xxx);Name 用当前语言翻译作可读备份。</summary>
    private BoxTab NewTab(BoxViewModel box, string catKey)    {
        var tab = new BoxTab { Key = catKey, Name = _localizer[catKey] };
        box.Tabs.Add(tab);
        return tab;
    }

    /// <summary>把系统图标(此电脑/回收站/控制面板/网络)放进「快捷方式」标签,
    /// <b>不再单独建「系统图标」标签</b>。
    ///
    /// 旧行为是整理时检测不到 tab.sysicons 就重建一个 —— 用户删掉标签后下次整理又回来,
    /// 标签永远删不掉(真实反馈)。现在:
    /// ① 老标签的内容并进 cat.shortcut,老标签一并移除(下次整理也不会再建);
    /// ② 4 个系统图标只在盒子里哪儿都没有时才补。
    /// 返回是否<b>新增</b>过系统图标(供整理完成提示用;单纯迁移不算"补上")。</summary>
    private bool EnsureSystemIconsInShortcutTab(BoxViewModel box, List<BoxItem> extracted)
    {
        var legacy = box.Tabs.FirstOrDefault(t => t.Key == "tab.sysicons");
        var tab = box.Tabs.FirstOrDefault(t => t.Key == CategorizerService.Shortcut);
        if (tab is null) tab = NewTab(box, CategorizerService.Shortcut);

        // 盒子里已有的路径(排除老标签本身 —— 否则迁移时会被自己挡住)
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in box.Tabs)
        {
            if (ReferenceEquals(t, legacy)) continue;
            foreach (var i in t.Items) present.Add(i.TargetPath);
        }
        foreach (var i in box.Items) present.Add(i.TargetPath);

        if (legacy is not null)
        {
            foreach (var item in legacy.Items.ToList())
            {
                if (!present.Add(item.TargetPath)) continue;   // 别的标签已有 → 丢弃重复
                item.BoxId = box.Id;
                item.Order = tab.Items.Count;
                tab.Items.Add(item);
                extracted.Add(item);
            }
            box.Tabs.Remove(legacy);
            Services.LogService.Info("Organize.SysIcons", $"老「系统图标」标签已并入「快捷方式」({tab.Items.Count} 项)");
        }

        var added = false;
        foreach (var def in SystemIcons.Definitions)
        {
            if (!present.Add(def.Clsid)) continue;
            var item = new BoxItem
            {
                Type = ItemType.SystemIcon,
                TargetPath = def.Clsid,
                DisplayName = def.Name,
                BoxId = box.Id,
                Order = tab.Items.Count
            };
            tab.Items.Add(item);
            extracted.Add(item);
            added = true;
        }
        return added;
    }

    /// <summary>整理时的显示名:.lnk 去扩展名,其余用完整文件名(与旧版一致)。</summary>
    private static string OrganizeDisplayName(string path)
    {
        var n = System.IO.Path.GetFileName(path);
        return n.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
            ? System.IO.Path.GetFileNameWithoutExtension(n) : n;
    }

    /// <summary>整理盒子按 TargetPath 跨标签去重:同一文件只保留首次出现的条目。
    /// 系统图标(以 :: 开头)按 TargetPath 也唯一化。修复历史重复条目导致"移除后整理只回来部分"。</summary>
    private static void DedupOrganizeBox(BoxViewModel box)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int removed = 0;
        foreach (var t in box.Tabs)
        {
            for (int i = t.Items.Count - 1; i >= 0; i--)
            {
                var path = t.Items[i].TargetPath;
                if (!seen.Add(path))
                {
                    t.Items.RemoveAt(i);
                    removed++;
                }
            }
        }
        if (removed > 0)
            App.LogError(new Exception($"DedupOrganizeBox removed {removed} duplicate item(s)"), "Organize.dedup");
    }

    /// <summary>按当前分类规则重新归类整理盒子的历史条目。
    /// 分类规则会演进(如新增 .py 归文档),旧条目按当时规则分到了某标签,改规则后不会自动迁移。
    /// 整理时检测"条目当前标签 != 当前分类结果",把不符的移到正确标签。系统图标不动(无扩展名分类)。</summary>
    private void ReclassifyOrganizeBox(BoxViewModel box)
    {
        var moves = new List<(BoxItem item, BoxTab from, string catKey)>();
        foreach (var t in box.Tabs)
        {
            foreach (var it in t.Items)
            {
                if (it.Type == ItemType.SystemIcon) continue;
                if (it.TargetPath.StartsWith("::", StringComparison.Ordinal)) continue;
                var catKey = _categorizer.Categorize(it.TargetPath);
                if (!string.Equals(catKey, t.Key, StringComparison.Ordinal))
                    moves.Add((it, t, catKey));
            }
        }
        foreach (var (item, from, catKey) in moves)
        {
            from.Items.Remove(item);
            var to = box.Tabs.FirstOrDefault(t => t.Key == catKey) ?? NewTab(box, catKey);
            item.Order = to.Items.Count;
            to.Items.Add(item);
        }
        if (moves.Count > 0)
            App.LogError(new Exception($"ReclassifyOrganizeBox moved {moves.Count} item(s)"), "Organize.reclassify");
    }

    private static int CategoryOrder(string c)
    {
        int i = Array.IndexOf(CategoryOrderArr, c);
        return i < 0 ? 99 : i;
    }

    // ---- 系统图标盒子(回收站/此电脑/控制面板)----
    [RelayCommand]
    private void AddSystemIconsBox()
    {
        if (Boxes.Any(b => b.Key == "box.sysicons"))
        {
            InputDialog.Inform(_localizer["dialog.sysIcons.exists"]);
            return;
        }

        var box = new BoxViewModel(new Box { Key = "box.sysicons", Name = _localizer["box.sysicons"], Width = 240, Height = 200 });
        var items = new List<BoxItem>();
        foreach (var def in SystemIcons.Definitions)
        {
            var item = new BoxItem
            {
                Type = ItemType.SystemIcon,
                TargetPath = def.Clsid,
                DisplayName = def.Name,
                BoxId = box.Id,
                Order = box.Items.Count
            };
            box.Items.Add(item);
            items.Add(item);
        }

        PlaceBoxAtNextSlot(box);
        AddBoxSynced(box);
        Save();
        ExtractIconsInBackground(items);
    }

    private void PlaceBoxAtNextSlot(BoxViewModel box)
    {
        int idx = Boxes.Count;
        int cols = Math.Max(1, (int)((ScreenWidth - 40) / 260));
        box.X = 40 + (idx % cols) * 260;
        box.Y = 40 + (idx / cols) * 340;
    }

    // ---- 标签盒子:手动合并 / 拆分 ----

    /// <summary>普通盒子转标签盒子:现有条目移入首个标签。</summary>
    [RelayCommand]
    private void ConvertToTabbed(BoxViewModel? box)
    {
        if (box is null || box.IsTabbed) return;
        var tab = new BoxTab { Name = string.IsNullOrWhiteSpace(box.Name) ? "标签 1" : box.Name };
        foreach (var it in box.Items.ToList())
        {
            it.BoxId = box.Id;
            tab.Items.Add(it);
        }
        box.Items.Clear();
        box.Tabs.Add(tab);
        box.SelectedTab = tab;
        ScheduleSave();
    }

    /// <summary>把 other 合并进 target 作为新标签(普通盒子或标签盒子均可)。</summary>
    public void MergeIn(BoxViewModel target, BoxViewModel other)
    {
        if (target.Id == other.Id) return;

        if (other.IsTabbed)
        {
            foreach (var t in other.Tabs.ToList())
            {
                var nt = new BoxTab { Name = t.Name };
                foreach (var it in t.Items.ToList()) { it.BoxId = target.Id; nt.Items.Add(it); }
                target.Tabs.Add(nt);
            }
        }
        else
        {
            var nt = new BoxTab { Name = string.IsNullOrWhiteSpace(other.Name) ? "标签" : other.Name };
            foreach (var it in other.Items.ToList()) { it.BoxId = target.Id; nt.Items.Add(it); }
            target.Tabs.Add(nt);
        }

        target.SelectedTab = target.Tabs.LastOrDefault();
        // 合并结果要看得见:目标盒子若处于卷起态,先展开(卷起时标签行是隐藏的)。
        target.IsCollapsed = false;
        target.IsMergeTarget = false;
        Boxes.Remove(other);
        ScheduleSave();
    }

    /// <summary>把一个标签从一个盒子拖到另一个盒子(拖标签到别的盒子上 = 并入对方成为新标签)。
    /// 源盒子只剩这一个标签时不允许移走(否则源盒子只剩空壳)。只搬引用,不动文件。</summary>
    public bool MoveTabToBox(BoxViewModel source, BoxViewModel target, BoxTab tab)
    {
        if (source.Id == target.Id || !source.Tabs.Contains(tab)) return false;
        if (source.Tabs.Count <= 1) return false;

        source.Tabs.Remove(tab);
        foreach (var item in tab.Items)
            item.BoxId = target.Id;
        target.Tabs.Add(tab);
        target.SelectedTab = tab;
        target.IsCollapsed = false;   // 卷起时看不到标签行,先展开让结果可见
        target.IsMergeTarget = false;
        ScheduleSave();
        return true;
    }

    /// <summary>把标签盒子里某个标签拖出来,变成落在拖放点上的独立盒子。
    /// 仅在源盒子还有其它标签时才拆(最后一个标签拆出去等于盒子消失,直接忽略)。
    /// 只搬引用,不动任何文件。返回是否真的拆了。</summary>
    public bool SplitTabToNewBox(BoxViewModel source, BoxTab tab, double screenX, double screenY)
    {
        if (source.Tabs.Count <= 1 || !source.Tabs.Contains(tab)) return false;

        var (x, y) = SystemParametersHelper.ClampIntoScreens(screenX, screenY);
        var box = new BoxViewModel(new Box
        {
            // 标签的 Header 已是当前语言的显示名;新盒子是用户盒子,不带 Key(不会被当成分类标签)
            Name = string.IsNullOrWhiteSpace(tab.Header) ? "新盒子" : tab.Header,
            X = x,
            Y = y,
            Width = source.Width,
            Height = source.Height
        });
        foreach (var item in tab.Items.ToList())
        {
            item.BoxId = box.Id;
            box.Items.Add(item);
        }

        source.Tabs.Remove(tab);   // BoxViewModel.OnTabsChanged 会自动重选标签
        AddBoxSynced(box);
        ScheduleSave();
        return true;
    }

    /// <summary>标签盒子拆分为独立盒子:每个标签变成一个盒子,位置略有错开。</summary>
    [RelayCommand]
    private void SplitBox(BoxViewModel? box)
    {
        if (box is null || !box.IsTabbed) return;
        if (!InputDialog.Confirm(string.Format(_localizer["dialog.split.confirm"], box.Header, box.Tabs.Count)))
            return;

        int index = Boxes.IndexOf(box);
        double baseX = box.X, baseY = box.Y;
        var created = new List<BoxViewModel>();
        int i = 0;
        foreach (var tab in box.Tabs.ToList())
        {
            var nb = new BoxViewModel(new Box
            {
                Name = tab.Name,
                X = baseX + (i % 4) * 30,
                Y = baseY + (i / 4) * 30,
                Width = box.Width,
                Height = box.Height
            });
            foreach (var it in tab.Items.ToList()) { it.BoxId = nb.Id; nb.Items.Add(it); }
            created.Add(nb);
            i++;
        }

        Boxes.Remove(box);
        for (int j = 0; j < created.Count; j++)
        {
            created[j].SyncViewMode(GlobalViewMode);
            Boxes.Insert(index + j, created[j]);
        }
        ScheduleSave();
    }

    // ---- 桌面图标显隐(纯视觉,不动文件)----
    /// <summary>当前桌面图标是否可见(可观察:按钮颜色据此变化)。</summary>
    [ObservableProperty] private bool _desktopIconsVisible = true;

    /// <summary>"未收纳的桌面项"集合变了(新拖进来/被收纳/被移除/桌面层要重画)。</summary>
    public event EventHandler? LooseItemsChanged;

    [RelayCommand]
    private void ToggleDesktopIcons() => SetDesktopIconsVisible(!DesktopIconsVisible);

    /// <summary>隐藏/显示桌面上的原生图标。
    /// 隐藏 = 直接关掉 explorer 的桌面图标层(整层开关):<b>目录窗口照旧、文件属性一概不碰</b>;
    /// 桌面上的"未收纳项"由 DesktopLayerWindow 自己画(对齐 Coodesker 的 _cls_desk_ 做法)。
    /// 显示 = 把原生图标层开回来。</summary>
    public void SetDesktopIconsVisible(bool visible)
    {
        try
        {
            if (!visible) RestoreAttributeHidingOnce();   // 擦掉旧版"按属性隐藏"留下的属性和设置

            _desktopIcons.SetVisible(visible);
            _iconLayout?.SetSystemIconsVisible(visible);  // 系统图标走注册表,和桌面图标层不是一套
            DesktopIconsVisible = visible;
            PersistIconHiding(!visible);
            RaiseLooseItemsChanged();
            Services.LogService.Info("DesktopIcons.Set", $"visible={visible} loose={LooseDesktopItems().Count}");
        }
        catch (Exception ex)
        {
            App.LogError(ex, "MainViewModel.SetDesktopIconsVisible");
        }
    }

    /// <summary>启动时按配置里的偏好应用"隐藏/显示桌面图标"。</summary>
    public void RefreshDesktopIconsState()
    {
        try
        {
            var preferred = _store.Load().Settings.HideCollectedDesktopIcons;
            SetDesktopIconsVisible(!preferred);
        }
        catch (Exception ex)
        {
            App.LogError(ex, "MainViewModel.RefreshDesktopIconsState");
        }
    }

    /// <summary>桌面上"没被收纳进盒子"的项(文件/文件夹)。桌面层就照这几个画图标。</summary>
    public IReadOnlyList<string> LooseDesktopItems()
    {
        if (_scanner is null) return Array.Empty<string>();
        try
        {
            return _scanner.ScanDesktop().Where(p => !IsCollected(p)).ToList();
        }
        catch (Exception ex)
        {
            App.LogError(ex, "MainViewModel.LooseDesktopItems");
            return Array.Empty<string>();
        }
    }

    private void RaiseLooseItemsChanged() => LooseItemsChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>旧版"按属性隐藏"把已收纳项标成了 Hidden、并把资源管理器的"显示隐藏项目"关掉了。
    /// 改成"整层隐藏 + 自绘桌面层"之后这些痕迹都要还回去:属性还原原样、设置还原成用户原来的值。
    /// 每次启动最多做一次(记录清空后就是空操作)。</summary>
    private void RestoreAttributeHidingOnce()
    {
        if (_attributeHidingRestored || _iconLayout is null) return;
        _attributeHidingRestored = true;
        try
        {
            var restored = _iconLayout.RestoreAll();
            _iconLayout.SetExplorerShowHidden(true);
            if (restored > 0)
                Services.LogService.Info("DesktopIcons.Migrate",
                    $"已还原 {restored} 个桌面项的属性,并恢复\"显示隐藏项目\"");
        }
        catch (Exception ex)
        {
            App.LogError(ex, "MainViewModel.RestoreAttributeHidingOnce");
        }
    }

    /// <summary>条目被拖到桌面上 = 出盒(对齐 Coodesker):从盒子移除 + 文件落到桌面,
    /// 然后让桌面层重画 —— 它一变成"未收纳",就会在桌面上被画出来。</summary>
    public void HandleItemDroppedOnDesktop(BoxItem item)
        => HandleItemsDroppedOnDesktop(new List<BoxItem> { item });

    /// <summary>整组拖到桌面:每条都出盒 + 落到桌面,最后只重画一次。</summary>
    public void HandleItemsDroppedOnDesktop(IReadOnlyList<BoxItem> items)
    {
        if (items is null || items.Count == 0) return;

        try
        {
            foreach (var item in items)
            {
                if (item is null) continue;
                var source = item.TargetPath;
                RemoveItemAnywhere(item);          // 先出盒:之后它就不在"已收纳"名单里了
                var landed = PlaceOnDesktopIfMissing(source);   // 落下效果是 Copy:不在桌面的文件复制过去
                // 记下"这是刚拖出盒的":落盘可能晚于拖动结束,靠 GlobalDragTracker 的 1.5 秒窗口兜不住
                MarkEjectedToDesktop(landed);
            }

            RaiseLooseItemsChanged();
            ScheduleSave();
        }
        catch (Exception ex)
        {
            App.LogError(ex, "MainViewModel.HandleItemsDroppedOnDesktop");
        }
    }

    /// <summary>拖到桌面的落地效果是"复制":条目指向的文件不在桌面时复制一份过去,
    /// 否则桌面上根本不会出现它。返回它在桌面上的路径(失败返回 null)。</summary>
    private static string? PlaceOnDesktopIfMissing(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        if (source.StartsWith("::", StringComparison.Ordinal)) return null;     // 系统图标没有对应文件
        if (source.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            if (!File.Exists(source) && !Directory.Exists(source)) return null;
            if (DesktopIconLayoutService.IsDesktopItem(source)) return source;  // 本来就在桌面,不用复制

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (string.IsNullOrWhiteSpace(desktop)) return null;

            var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var dest = UniqueSibling(Path.Combine(desktop, name));
            if (Directory.Exists(source)) CopyDirectory(source, dest);
            else File.Copy(source, dest, overwrite: false);
            Services.LogService.Info("Desktop.DragOut", $"copied {source} -> {dest}");
            return dest;
        }
        catch (Exception ex)
        {
            Services.LogService.Warn("Desktop.DragOut", Services.LogService.Truncate(ex.Message, 160));
            return null;
        }
    }

    /// <summary>外部把文件拖到了我们的桌面层上(桌面空白处的拖放仍归 explorer 管)。
    /// 按拖放效果放进桌面目录,然后让桌面层重画。</summary>
    public void HandleFilesDroppedOnDesktop(IEnumerable<string> sources, bool move)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (string.IsNullOrWhiteSpace(desktop)) return;

        foreach (var source in sources)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(source)) continue;
                if (DesktopIconLayoutService.IsDesktopItem(source)) continue;   // 本来就在桌面,不用搬
                if (!File.Exists(source) && !Directory.Exists(source)) continue;

                var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var dest = UniqueSibling(Path.Combine(desktop, name));
                if (Directory.Exists(source))
                {
                    if (move) Directory.Move(source, dest);
                    else CopyDirectory(source, dest);
                }
                else if (move)
                {
                    File.Move(source, dest);
                }
                else
                {
                    File.Copy(source, dest, overwrite: false);
                }
            }
            catch (Exception ex)
            {
                Services.LogService.Warn("Desktop.Drop", Services.LogService.Truncate(ex.Message, 160));
            }
        }

        RaiseLooseItemsChanged();
    }

    /// <summary>Ctrl+C:把选中项的文件放进剪贴板(同时带上"这是我们复制的条目"标记)。
    /// 系统图标/网址没有文件可复制,只带标记 —— 粘贴到桌面时按"移动"(出盒)处理。
    /// 返回放进去的条目数(0 = 没选中项,或剪贴板被别的程序占用)。</summary>
    public int CopySelectionToClipboard()
    {
        var items = GetSelectedItems();
        if (items.Count == 0) return 0;

        var files = items
            .Where(ItemDragDrop.CanDragAsFile)
            .Select(i => i.TargetPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        try
        {
            var data = new DataObject();
            if (files.Count > 0) data.SetData(DataFormats.FileDrop, files.ToArray());
            data.SetData(ItemDragDrop.CopiedItemsFormat, items.Select(i => i.Id).ToArray());
            Clipboard.SetDataObject(data, copy: true);
            Services.LogService.Info("Item.Copy", $"复制 {items.Count} 项(其中文件 {files.Count} 个)");
            return items.Count;
        }
        catch (Exception ex)
        {
            Services.LogService.Warn("Item.Copy", Services.LogService.Truncate(ex.Message, 160));
            return 0;
        }
    }

    /// <summary>剪贴板里"由我们复制出来的条目 Id"集合;不是我们复制的返回 null。</summary>
    public static List<Guid>? ReadCopiedItemIds()
    {
        try
        {
            if (!Clipboard.ContainsData(ItemDragDrop.CopiedItemsFormat)) return null;
            if (Clipboard.GetData(ItemDragDrop.CopiedItemsFormat) is Guid[] ids) return ids.ToList();
        }
        catch { /* 剪贴板被占用/格式异常:当作没复制过 */ }
        return null;
    }

    /// <summary>Ctrl+V 落在桌面上:按<b>移动</b>处理 —— 条目出盒、文件不在桌面就放过去,
    /// 与"把条目拖出盒子"完全同一套逻辑(见 <see cref="HandleItemsDroppedOnDesktop"/>)。返回处理条数。</summary>
    public int PasteOntoDesktop()
    {
        var ids = ReadCopiedItemIds();
        if (ids is null || ids.Count == 0) return 0;

        var items = ids.Select(FindItemById).OfType<BoxItem>().ToList();
        if (items.Count == 0) return 0;

        HandleItemsDroppedOnDesktop(items);
        Services.LogService.Info("Item.Paste", $"粘贴到桌面 {items.Count} 项(移动)");
        return items.Count;
    }

    /// <summary>把文件移动/复制进某个文件夹(拖到文件夹图标上 = 移动;粘贴进文件夹 = 复制)。
    /// 跳过自己、以及本来就在该文件夹里的。返回成功条数。
    ///
    /// **成功与失败都要给反馈**:拖上去时系统显示的是"+"号,松手若静默失败(最常见是文件正被
    /// Excel 等程序占用 → ERROR_SHARING_VIOLATION),用户就会以为"功能没实现"。</summary>
    public int MoveOrCopyIntoFolder(IEnumerable<string> sources, string folderPath, bool move)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return 0;

        var done = 0;
        var failed = 0;
        var inUse = false;
        var folder = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var folderName = Path.GetFileName(folder);

        foreach (var source in sources)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(source)) continue;
                if (!File.Exists(source) && !Directory.Exists(source)) continue;

                var srcFull = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(srcFull, folder, StringComparison.OrdinalIgnoreCase)) continue;   // 拖到自己身上
                if (srcFull.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;  // 已在里面

                var name = Path.GetFileName(srcFull);
                var dest = UniqueSibling(Path.Combine(folder, name));
                if (Directory.Exists(srcFull))
                {
                    if (move) Directory.Move(srcFull, dest);
                    else CopyDirectory(srcFull, dest);
                }
                else if (move)
                {
                    File.Move(srcFull, dest);
                }
                else
                {
                    File.Copy(srcFull, dest, overwrite: false);
                }

                done++;
                Services.LogService.Info("Item.IntoFolder", $"{(move ? "移动" : "复制")} {name} -> {folder}");
            }
            catch (Exception ex)
            {
                failed++;
                // 共享冲突 = 文件正被别的程序打开(最常见的"拖进去没反应")
                if (ex is IOException && (ex.HResult & 0xFFFF) is 32 or 33) inUse = true;
                Services.LogService.Warn("Item.IntoFolder",
                    $"{Path.GetFileName(source)}: {ex.GetType().Name} {Services.LogService.Truncate(ex.Message, 120)}");
            }
        }

        // 失败优先报(否则用户看到的仍然是"没反应");全成功时报一句"已移进/复制进 N 项"确认落到了哪儿
        if (failed > 0)
        {
            var key = inUse ? "dialog.move.inUse"
                : move ? "dialog.move.failed" : "dialog.copy.failed";
            var text = inUse ? _localizer[key]
                : string.Format(_localizer[key], Services.LogService.Truncate(failed.ToString() + " 项失败", 60));
            ToastWindow.Show(text, opacity: 0.9);
        }
        else if (done > 0)
        {
            ToastWindow.Show(string.Format(_localizer[move ? "dialog.move.done" : "dialog.copy.done"], folderName, done),
                opacity: 0.5);
        }

        if (done > 0) RaiseLooseItemsChanged();
        return done;
    }

    /// <summary>重名时在文件名后加 (2)/(3)…,不覆盖桌面上已有的同名文件。</summary>
    private static string UniqueSibling(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        return path;
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dest, Path.GetRelativePath(source, file));
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            File.Copy(file, target, overwrite: false);
        }
    }

    /// <summary>桌面目录发生变化(监听在后台线程),转回 UI 线程处理。</summary>
    private void OnDesktopItemChanged(object? sender, DesktopItemChange change)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            HandleDesktopItemChanged(change);
            return;
        }
        dispatcher.BeginInvoke(new Action(() => HandleDesktopItemChanged(change)));
    }

    /// <summary>桌面目录一变就按类型分流:
    /// <b>新增</b> → 尝试自动收纳(没收才画在桌面层);<b>删除</b> → 撤掉盒子里的引用(否则点开就是"目标当前不可用");
    /// <b>改名</b> → 引用跟着改到新路径(同理)。三种都要重画桌面层。</summary>
    private void HandleDesktopItemChanged(DesktopItemChange change)
    {
        if (_disposed || string.IsNullOrWhiteSpace(change.Path)) return;
        try
        {
            Services.LogService.Info("Desktop.Watch", $"{change.Kind} path={change.Path}");

            switch (change.Kind)
            {
                case DesktopChangeKind.Created:
                    // 收纳成功时自己会重画;没收成才单纯重画(把新文件画到桌面层上)
                    if (!TryAutoCollect(change.Path)) RaiseLooseItemsChanged();
                    break;

                case DesktopChangeKind.Deleted:
                    RemoveReferencesTo(change.Path);
                    RaiseLooseItemsChanged();
                    break;

                case DesktopChangeKind.Renamed:
                    if (!string.IsNullOrEmpty(change.OldPath))
                        UpdateReferencePath(change.OldPath, change.Path);
                    RaiseLooseItemsChanged();
                    break;
            }
        }
        catch (Exception ex)
        {
            App.LogError(ex, "MainViewModel.HandleDesktopItemChanged");
        }
    }

    /// <summary>把指向某路径的盒内引用全部撤掉(文件已删/已移出桌面)。只撤引用,绝不碰文件本身。</summary>
    private int RemoveReferencesTo(string path)
    {
        var stale = AllItems()
            .Where(i => string.Equals(i.TargetPath, path, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (stale.Count == 0) return 0;

        foreach (var item in stale) RemoveItemAnywhere(item);
        Services.LogService.Info("Desktop.Watch", $"撤掉失效引用 {stale.Count} 条 path={path}");
        return stale.Count;
    }

    /// <summary>桌面文件改名了:盒子里指向旧路径的引用跟着改,否则点开就是"目标当前不可用"。
    /// 图标缓存路径一并清掉,按新路径后台重取。</summary>
    private void UpdateReferencePath(string oldPath, string newPath)
    {
        var changed = 0;
        foreach (var item in AllItems()
                     .Where(i => string.Equals(i.TargetPath, oldPath, StringComparison.OrdinalIgnoreCase)))
        {
            item.TargetPath = newPath;
            item.DisplayName = OrganizeDisplayName(newPath);
            item.IconCachePath = null;
            // 清掉图标之后**必须重取** —— 之前这里只清不取(注释写着"按新路径后台重取"却没调用),
            // 结果是"改个名图标就永久消失,只能重启才回来"。
            ExtractIconAsync(item);
            changed++;
        }

        if (changed == 0) return;
        ScheduleSave();
        Services.LogService.Info("Desktop.Watch", $"引用改名 {changed} 条 {oldPath} -> {newPath}");
    }

    /// <summary>桌面上新出现的文件 → 自动分类收进「桌面整理」盒子。返回 true = 已收纳(调用方不必再重画)。
    ///
    /// 不收的四种情况(每条都对应一个真实反馈):
    /// ① 开关关着;② 桌面图标可见(用户在用原生桌面,收了会重复显示);
    /// ③ 临时/隐藏文件(~$ 锁文件、*.tmp —— 收了只会留下死引用);
    /// ④ <b>用户自己放在桌面的</b>:刚结束过一次拖动(从资源管理器拖过来),或刚从盒子里拖出去。</summary>
    private bool TryAutoCollect(string path)
    {
        if (!AutoCollectNewDesktopFiles) return false;
        if (DesktopIconsVisible) return false;
        if (IsExcludedFromAutoCollect(path)) return false;
        if (!DesktopItemPolicy.ShouldShow(path)) return false;
        if (IsCollected(path)) return false;
        if (!File.Exists(path) && !Directory.Exists(path)) return false;

        try
        {
            var box = GetOrCreateOrganizeBox(out _);
            var category = _categorizer.Categorize(path);
            var tab = box.Tabs.FirstOrDefault(t => t.Key == category) ?? NewTab(box, category);

            var item = _parser.ParsePath(path);
            item.BoxId = box.Id;
            item.DisplayName = OrganizeDisplayName(path);
            item.Order = tab.Items.Count;
            tab.Items.Add(item);

            box.SelectedTab = tab;          // 切到接收标签,让用户立刻看到它进来了
            ExtractIconAsync(item);
            ScheduleSave();                 // 防抖:一次拖进来多个文件时不逐个重复写配置
            RaiseLooseItemsChanged();       // 桌面层不再画它
            Services.LogService.Info("Desktop.AutoCollect", $"path={path} tab={category}");
            return true;
        }
        catch (Exception ex)
        {
            App.LogError(ex, "MainViewModel.TryAutoCollect");
            return false;
        }
    }

    /// <summary>这个新文件是不是"用户自己放在桌面的"。见 <see cref="TryAutoCollect"/> 的说明④。</summary>
    private bool IsExcludedFromAutoCollect(string path)
    {
        // 刚结束过一次拖动:不管是从资源管理器拖来的,还是我们自己把盒内条目拖出去的,都算用户有意放置
        if (GlobalDragTracker.JustEnded()) return true;

        lock (_ejectedGate)
        {
            PruneExpiredEjected();
            return _recentEjected.ContainsKey(path);
        }
    }

    /// <summary>记录"这个路径是刚从盒子里拖到桌面的"(拖出窗口期内不自动收纳)。
    /// 与 GlobalDragTracker 互补:拖动结束 1.5 秒后才落盘的大文件复制,靠这份名单兜底。</summary>
    private void MarkEjectedToDesktop(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (_ejectedGate)
        {
            PruneExpiredEjected();
            _recentEjected[path] = Environment.TickCount64;
        }
    }

    private void PruneExpiredEjected()
    {
        var now = Environment.TickCount64;
        foreach (var key in _recentEjected
                     .Where(kv => now - kv.Value > EjectExcludeMs)
                     .Select(kv => kv.Key)
                     .ToList())
            _recentEjected.Remove(key);
    }

    private bool IsCollected(string path) =>
        AllItems().Any(i => string.Equals(i.TargetPath, path, StringComparison.OrdinalIgnoreCase));

    private void PersistIconHiding(bool hidden)
    {
        try
        {
            var cfg = _store.Load();
            if (cfg.Settings.HideCollectedDesktopIcons == hidden) return;
            cfg.Settings.HideCollectedDesktopIcons = hidden;
            _store.Save(cfg);
        }
        catch (Exception ex)
        {
            Services.LogService.Warn("MainViewModel.PersistIconHiding", Services.LogService.Truncate(ex.Message, 160));
        }
    }

    /// <summary>后台批量提取图标,逐个回到 UI 线程更新(BoxItem.IconCachePath 可观察,磁贴自动刷新)。</summary>
    private void ExtractIconsInBackground(List<BoxItem> items)
    {
        Task.Run(() =>
        {
            foreach (var it in items)
            {
                try
                {
                    var icon = _icon.Extract(it.TargetPath);
                    var disp = Application.Current?.Dispatcher;
                    if (disp is null)
                    {
                        // 没有 UI 线程(测试宿主/无窗口场景):直接赋值。
                        // 原来这里一律 continue,图标永远回填不到条目上 —— 既是隐患也让这段逻辑无法被测到。
                        it.IconCachePath = icon;
                        continue;
                    }
                    if (disp.HasShutdownStarted) continue;
                    disp.BeginInvoke(new Action(() => it.IconCachePath = icon));
                }
                catch { /* 程序退出中或其他异常,忽略 */ }
            }
        });
    }

    private void ExtractIconAsync(BoxItem item) => ExtractIconsInBackground(new List<BoxItem> { item });

    /// <summary>收到 shell 变化通知(回收站清空/还原、系统图标列表更新等)后,
    /// 重新提取所有系统图标(回收站空/满、此电脑盘符等状态会变)。forceRefresh=true 强制删旧缓存。</summary>
    private void RefreshSystemIcons()
    {
        if (_disposed) return;
        var sysItems = CollectSystemIconItems();
        if (sysItems.Count == 0) return;

        lock (_systemIconRefreshGate)
        {
            if (_systemIconRefreshRunning)
            {
                _pendingSystemIconRefreshItems = sysItems;
                return;
            }

            _systemIconRefreshRunning = true;
        }

        Task.Run(() => RefreshSystemIconsLoop(sysItems));
    }

    private List<BoxItem> CollectSystemIconItems()
    {
        var sysItems = new List<BoxItem>();
        foreach (var b in Boxes)
        {
            var src = b.IsTabbed ? b.Tabs.SelectMany(t => t.Items) : b.Items;
            foreach (var it in src)
            {
                if (it.TargetPath.StartsWith("::", StringComparison.Ordinal))
                    sysItems.Add(it);
            }
        }
        return sysItems;
    }

    private void RefreshSystemIconsLoop(List<BoxItem> initialItems)
    {
        var currentItems = initialItems;
        while (true)
        {
            RefreshSystemIconSnapshot(currentItems);

            lock (_systemIconRefreshGate)
            {
                if (_pendingSystemIconRefreshItems is null)
                {
                    _systemIconRefreshRunning = false;
                    return;
                }

                currentItems = _pendingSystemIconRefreshItems;
                _pendingSystemIconRefreshItems = null;
            }
        }
    }

    private void RefreshSystemIconSnapshot(List<BoxItem> sysItems)
    {
        var targets = sysItems
            .GroupBy(i => i.TargetPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { TargetPath = g.Key, Items = g.ToList() })
            .ToList();

        foreach (var target in targets)
        {
            try
            {
                var icon = _icon.Extract(target.TargetPath, forceRefresh: true);
                var disp = Application.Current?.Dispatcher;
                if (disp is null || disp.HasShutdownStarted) continue;
                var affected = target.Items;
                disp.BeginInvoke(new Action(() =>
                {
                    foreach (var it in affected)
                        it.IconCachePath = icon;
                }));
            }
            catch (Exception ex) { App.LogError(ex, "RefreshSystemIcons.Extract"); }
        }
    }

    /// <summary>为详细信息视图惰性填充每个条目的大小/修改时间(后台线程,逐个回 UI 更新)。</summary>
    public void EnsureDetailFields(BoxViewModel box)
    {
        // 收集所有条目:标签模式要覆盖每个标签(不只当前选中标签)——否则切到其它标签时,
        // 那些标签的 ModifiedText 仍是 null,详细信息视图里就不显示修改时间(“有的标签有,有的没有”)。
        var source = box.IsTabbed ? box.Tabs.SelectMany(t => t.Items) : box.Items;
        var items = source.Where(i => i.ModifiedText is null).ToList();
        if (items.Count == 0) return;
        Task.Run(() =>
        {
            foreach (var it in items)
            {
                string? size = null, mod = null;
                try
                {
                    // 系统图标/网址等没有文件实体,显示占位
                    if (it.Type == ItemType.SystemIcon || it.TargetPath.StartsWith("::") ||
                        it.Type == ItemType.Url || it.TargetPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        size = "—"; mod = "—";
                    }
                    else if (Directory.Exists(it.TargetPath))
                    {
                        size = "—"; // 文件夹不显示大小
                        mod = new DirectoryInfo(it.TargetPath).LastWriteTime.ToString("yyyy-MM-dd HH:mm");
                    }
                    else if (File.Exists(it.TargetPath))
                    {
                        var fi = new FileInfo(it.TargetPath);
                        size = FormatSize(fi.Length);
                        mod = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
                    }
                }
                catch { }

                var disp = Application.Current?.Dispatcher;
                if (disp is null || disp.HasShutdownStarted) continue;
                var s = size; var m = mod;
                disp.BeginInvoke(new Action(() => { it.SizeText = s; it.ModifiedText = m; }));
            }
        });
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB"
    };

    // ---- 持久化 ----
    public void ScheduleSave()
    {
        if (_disposed) return;
        var timer = new Timer(_ =>
        {
            try
            {
                if (_disposed) return;
                var disp = Application.Current?.Dispatcher;
                if (disp is null)
                {
                    try { Save(); }
                    catch (Exception ex) { ReportPersistenceFailure(ex); }
                    return;
                }
                if (disp.HasShutdownStarted) return;
                disp.BeginInvoke(new Action(() =>
                {
                    try { Save(); }
                    catch (Exception ex) { ReportPersistenceFailure(ex); }
                }));
            }
            catch (Exception ex) { ReportPersistenceFailure(ex); }
        }, null, 300, Timeout.Infinite);
        Interlocked.Exchange(ref _debounce, timer)?.Dispose();
        if (_disposed && ReferenceEquals(
                Interlocked.CompareExchange(ref _debounce, null, timer), timer))
        {
            timer.Dispose();
        }
    }

    public void Save() => TrySave();

    public bool TrySave()
    {
        if (_disposed) return false;
        var disp = Application.Current?.Dispatcher;
        if (disp is not null && !disp.CheckAccess())
        {
            if (disp.HasShutdownStarted) return false;
            try
            {
                return disp.Invoke(TrySave);
            }
            catch (Exception ex) { ReportPersistenceFailure(ex); }
            return false;
        }

        try
        {
            var existing = _store.Load();
            var cfg = new AppConfig { Settings = existing.Settings };
            cfg.Settings.GlobalViewMode = GlobalViewMode;
            cfg.Settings.HoverExpandCollapsedBoxes = HoverExpandCollapsedBoxes;
        cfg.Settings.BoxOpacity = Math.Clamp(BoxOpacity, 0.1, 1.0);
            int order = 0;
            // 快照:避免后台线程枚举时集合被 UI 线程修改
            foreach (var b in Boxes.ToList())
            {
                var m = b.ToModel();
                m.Order = order++;
                cfg.Boxes.Add(m);
            }
            _store.Save(cfg);
            return true;
        }
        catch (Exception ex)
        {
            ReportPersistenceFailure(ex);
            return false;
        }
    }

    private void ReportPersistenceFailure(Exception exception)
    {
        try
        {
            Services.LogService.Error(exception, "MainViewModel.Save",
                $"boxes={Boxes.Count} path={Services.LogService.Truncate(_store?.ToString(), 120)}");
        }
        catch { }
        App.LogError(exception, "MainViewModel.Save");
        PersistenceFailed?.Invoke(this, exception);
    }

    /// <summary>语言切换后:刷新所有盒子与标签的 Header(显示名),让程序生成的项按新语言显示。</summary>
    private void RefreshAllHeaders()
    {
        foreach (var b in Boxes)
        {
            b.RefreshHeader();
            b.RefreshCollapseActionText();
            b.RefreshLockActionText();
            b.RefreshIconSizeActionText();
            foreach (var t in b.Tabs) t.RefreshHeader();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Exchange(ref _debounce, null)?.Dispose();
        _localizer.LanguageChanged -= OnLanguageChanged;
        _shellChange.SystemIconChanged -= OnSystemIconChanged;
        if (_desktopWatcher is not null) _desktopWatcher.DesktopItemChanged -= OnDesktopItemChanged;
        GC.SuppressFinalize(this);
    }
}
