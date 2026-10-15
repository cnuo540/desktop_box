using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DesktopBox.Models;
using DesktopBox.Services;
using DesktopBox.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopBox.Controls;

public partial class ItemTile : UserControl
{
    private static readonly SemaphoreSlim ShellMenuGate = new(1, 1);
    private Point _dragStart;
    private bool _dragCandidate;
    /// <summary>这一下按下时"暂时不动选中态",等松手没拖走再塌成单选(见 SelectionClickPolicy)。</summary>
    private bool _collapseOnMouseUp;
    /// <summary>这次按下已经变成拖拽了 —— 松手时就不该再塌陷(拖完保持整组选中)。</summary>
    private bool _dragStarted;

    /// <summary>磁贴渲染尺寸/布局(由盒子 ViewMode 决定)。变化时重排内部元素。</summary>
    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(nameof(IconSize), typeof(TileSize), typeof(ItemTile),
            new PropertyMetadata(TileSize.Large, OnIconSizeChanged));

    public TileSize IconSize
    {
        get => (TileSize)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    private ILocalizerService? _localizer;
    private ILocalizerService Localizer =>
        _localizer ??= App.Services.GetRequiredService<ILocalizerService>();

    public ItemTile()
    {
        InitializeComponent();
        // WPF-UI hover 动画打 frozen 主题画刷会致命崩溃（v1.7.10）：Loaded 后把实例画刷解冻。
        // 盒子后续新增的磁贴走这里（BoxControl.Loaded 只覆盖首屏已建磁贴）。
        Loaded += (_, _) =>
        {
            try { WpfUiAnimationGuard.UnfreezeSubtree(this); } catch { }
            // WPF 的落点判定用"鼠标正下方那个元素",而 AllowDrop 不继承:整棵子树一起打开,
            // 否则把文件拖到图标正上方会被拒。
            DropTargets.EnableOnSubtree(this);
        };
        DataContextChanged += (_, _) =>
        {
            SetFallback();
            ApplyLayout();
        };
        // 单击选中(普通=单选,Ctrl=多选)。不拦截双击打开(OnDoubleClick 仍触发)。
        PreviewMouseLeftButtonDown += OnTileMouseDown;
        PreviewMouseMove += OnTileMouseMove;
        PreviewMouseLeftButtonUp += OnTileMouseUp;

        // 落在磁贴上的文件/图标也要能收进来 —— 否则拖到"已有图标"那一片时系统会显示禁止光标。
        AllowDrop = true;
        DragOver += OnTileFileDragOver;
        Drop += OnTileFileDrop;
    }

    /// <summary>磁贴上的拖入:只接"外部文件/桌面层图标"(FileDrop)。
    /// 盒内条目拖拽(内部格式)一律不拦 —— 让它冒泡到 BoxControl,由它决定是"落回本盒子 = 重排"还是"禁止"。</summary>
    private void OnTileFileDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(ItemDragDrop.DragSourceItemFormat)) return;

        var ok = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text);
        e.Effects = ok ? e.AllowedEffects : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnTileFileDrop(object sender, DragEventArgs e)
    {
        if (Item is null) return;
        // 盒内条目拖拽交给 BoxControl(盒内重排)
        if (e.Data.GetDataPresent(ItemDragDrop.DragSourceItemFormat)) return;

        var vm = App.Services.GetRequiredService<ViewModels.MainViewModel>();
        var box = vm.Boxes.FirstOrDefault(b => b.Id == Item.BoxId);
        if (box is null)
        {
            e.Handled = true;
            return;
        }

        Services.LogService.Info("Drop.Tile", $"落入磁贴 header={Services.LogService.Truncate(box.Header)}");

        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            foreach (var path in paths.Take(500))
            {
                if (!string.IsNullOrWhiteSpace(path)) vm.AddItemToBox(box, path);
            }
            e.Effects = DragDropEffects.Copy;
        }
        else if (e.Data.GetData(DataFormats.Text) is string text && !string.IsNullOrWhiteSpace(text))
        {
            vm.AddItemToBox(box, text.Trim());
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void OnTileMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Item is null) return;
        if (e.ClickCount >= 2)
        {
            Item.IsSelected = true;
            return;
        }
        _dragStart = e.GetPosition(null);
        _dragCandidate = ItemDragDrop.CanDragAsFile(Item);
        _collapseOnMouseUp = false;
        _dragStarted = false;

        var vm = App.Services.GetRequiredService<ViewModels.MainViewModel>();
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        // 多选之后按住其中一条往外拖要走整组:这一下先不塌成单选,否则拖拽载荷只剩它自己。
        _collapseOnMouseUp = SelectionClickPolicy.DeferCollapseOnPress(ctrl, Item.IsSelected, vm.GetSelectedItems().Count);
        if (_collapseOnMouseUp) return;

        vm.HandleTileClick(Item, ctrl);
    }

    private void OnTileMouseMove(object sender, MouseEventArgs e)
    {
        var item = Item;
        if (!_dragCandidate || item is null || e.LeftButton != MouseButtonState.Pressed)
            return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragCandidate = false;
        _dragStarted = true;

        var vm = App.Services.GetRequiredService<ViewModels.MainViewModel>();
        // 按住的那条在选中集合里 → 整组一起拖(explorer 也是这么干的);否则就拖这一条
        var selection = vm.GetSelectedItems();
        var dragged = selection.Contains(item) && selection.Count > 1
            ? selection
            : new List<BoxItem> { item };

        // DesktopDropOverlay 负责桌面落点并由 ViewModel 复制/恢复文件；不附带
        // FileDrop，避免 Explorer 同时执行一次原生复制。
        var data = ItemDragDrop.CreateDesktopDropData(dragged);
        var boxBounds = DesktopDropOverlay.OccupiedBoxRects(vm.Boxes);

        using var desktopDrop = DesktopDropOverlay.TryCreate(boxBounds);
        try
        {
            // 允许的效果必须和落点的有交集:拖到桌面 = Copy(落盘/复制),落回本盒子 = Move(盒内重排)。
            // 只给 Copy 的话盒内重排会被系统判成"禁止"(实测踩过)。
            Services.LogService.Info("ItemTile.DragStart",
                $"count={dragged.Count} path={Services.LogService.Truncate(item.TargetPath)} allowed=Copy|Move");
            DragDrop.DoDragDrop(this, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            App.LogError(ex, "ItemTile.DragOut");
        }
        finally
        {
            // DoDragDrop 的模态循环可能吞掉 mouseup:拖完就地把状态收干净,免得下一次点击误塌。
            _dragStarted = false;
            _collapseOnMouseUp = false;
        }

        if (desktopDrop?.DroppedOnDesktop == true && ReferenceEquals(Item, item))
        {
            // 拖到桌面 = 出盒(参考 Coodesker):从盒子移除 + 文件不在桌面则复制过去。
            // 按住的那条在选中集合里时,整组一起出盒。
            vm.HandleItemsDroppedOnDesktop(dragged);
        }
        e.Handled = true;
    }

    private void OnTileMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = false;
        if (_dragStarted)
        {
            _dragStarted = false;
            _collapseOnMouseUp = false;
            return;
        }

        if (!_collapseOnMouseUp) return;
        _collapseOnMouseUp = false;

        // 按住之后原地松手(没拖走)= 普通单击:这时候才把多选塌成这一条
        if (Item is null) return;
        var vm = App.Services.GetRequiredService<ViewModels.MainViewModel>();
        vm.HandleTileClick(Item, false);
    }

    private void SetFallback()
    {
        if (DataContext is not BoxItem item) return;
        FallbackText.Text = GetGlyph(item);
        Fallback.Background = Brushes.Transparent;
        bool hasIcon = !string.IsNullOrEmpty(item.IconCachePath);
        Img.Visibility = hasIcon ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string GetGlyph(BoxItem item)
    {
        var c = string.IsNullOrEmpty(item.DisplayName) ? '?' : item.DisplayName[0];
        if (!char.IsLetterOrDigit(c)) c = item.Type switch
        {
            ItemType.Folder => '文',
            ItemType.Url => '链',
            _ => '件'
        };
        return char.ToUpperInvariant(c).ToString();
    }

    /// <summary>根据 TileSize 调整行列定义、图标尺寸、详情可见性。</summary>
    private static void OnIconSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ItemTile)d).ApplyLayout();

    private static readonly GridLength Star = new(1, GridUnitType.Star);
    private static readonly GridLength Auto = GridLength.Auto;
    private static GridLength Fix(double v) => new(v, GridUnitType.Pixel);

    private const double DetailModifiedWidth = 108;
    private const double DetailSizeWidth = 68;

    private void ApplyLayout()
    {
        if (!IsInitialized) return;
        var size = IconSize;

        foreach (var child in new UIElement[] { IconBox, NameText, ModifiedText, SizeText })
        {
            Grid.SetColumnSpan(child, 1);
            Grid.SetRowSpan(child, 1);
        }
        IconBox.HorizontalAlignment = HorizontalAlignment.Center;
        IconBox.VerticalAlignment = VerticalAlignment.Top;
        ModifiedText.Visibility = Visibility.Collapsed;
        SizeText.Visibility = Visibility.Collapsed;

        switch (size)
        {
            case TileSize.Large:
                SetCols(LayoutGrid, Star);
                SetRows(LayoutGrid, Auto, Auto);
                Place(IconBox, 0, 0); Place(NameText, 0, 1);
                SetIcon(56, 50, 28);
                NameText.TextAlignment = TextAlignment.Center;
                NameText.FontSize = 12;
                NameText.VerticalAlignment = VerticalAlignment.Top;
                NameText.Margin = new Thickness(0, 3, 0, 0);
                break;
            case TileSize.Medium:
                SetCols(LayoutGrid, Star);
                SetRows(LayoutGrid, Auto, Auto);
                Place(IconBox, 0, 0); Place(NameText, 0, 1);
                SetIcon(44, 38, 22);
                NameText.TextAlignment = TextAlignment.Center;
                NameText.FontSize = 11;
                NameText.VerticalAlignment = VerticalAlignment.Top;
                NameText.Margin = new Thickness(0, 3, 0, 0);
                break;
            case TileSize.Small:
            case TileSize.List:
                SetCols(LayoutGrid, Auto, Star);
                SetRows(LayoutGrid, Auto);
                Place(IconBox, 0, 0); Place(NameText, 1, 0);
                SetIcon(26, 22, 15);
                IconBox.HorizontalAlignment = HorizontalAlignment.Left;
                IconBox.VerticalAlignment = VerticalAlignment.Center;
                NameText.TextAlignment = TextAlignment.Left;
                NameText.FontSize = 12;
                NameText.VerticalAlignment = VerticalAlignment.Center;
                NameText.Margin = new Thickness(6, 0, 4, 0);
                break;
            case TileSize.Detail:
                SetCols(LayoutGrid, Auto, Star, Fix(DetailModifiedWidth), Fix(DetailSizeWidth));
                SetRows(LayoutGrid, Auto);
                Place(IconBox, 0, 0); Place(NameText, 1, 0);
                Place(ModifiedText, 2, 0); Place(SizeText, 3, 0);
                SetIcon(26, 22, 15);
                IconBox.HorizontalAlignment = HorizontalAlignment.Left;
                IconBox.VerticalAlignment = VerticalAlignment.Center;
                NameText.TextAlignment = TextAlignment.Left;
                NameText.FontSize = 12;
                NameText.VerticalAlignment = VerticalAlignment.Center;
                NameText.Margin = new Thickness(6, 0, 8, 0);
                ModifiedText.Visibility = Visibility.Visible;
                SizeText.Visibility = Visibility.Visible;
                ModifiedText.VerticalAlignment = VerticalAlignment.Center;
                ModifiedText.Margin = new Thickness(0, 0, 8, 0);
                SizeText.VerticalAlignment = VerticalAlignment.Center;
                break;
            case TileSize.Tile:
                SetCols(LayoutGrid, Auto, Star);
                SetRows(LayoutGrid, Auto, Auto, Auto);
                Place(IconBox, 0, 0, rowSpan: 3);
                Place(NameText, 1, 0);
                Place(ModifiedText, 1, 1);
                Place(SizeText, 1, 2);
                SetIcon(44, 38, 22);
                IconBox.HorizontalAlignment = HorizontalAlignment.Left;
                IconBox.VerticalAlignment = VerticalAlignment.Center;
                NameText.TextAlignment = TextAlignment.Left;
                NameText.FontSize = 12;
                NameText.VerticalAlignment = VerticalAlignment.Top;
                NameText.Margin = new Thickness(8, 2, 8, 2);
                ModifiedText.Visibility = Visibility.Visible;
                SizeText.Visibility = Visibility.Visible;
                ModifiedText.VerticalAlignment = VerticalAlignment.Center;
                ModifiedText.Margin = new Thickness(8, 0, 8, 1);
                SizeText.VerticalAlignment = VerticalAlignment.Center;
                SizeText.Margin = new Thickness(8, 0, 8, 2);
                break;
        }
    }

    private static void SetCols(Grid g, params GridLength[] widths)
    {
        g.ColumnDefinitions.Clear();
        foreach (var w in widths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
    }

    private static void SetRows(Grid g, params GridLength[] heights)
    {
        g.RowDefinitions.Clear();
        foreach (var h in heights) g.RowDefinitions.Add(new RowDefinition { Height = h });
    }

    private static void Place(UIElement e, int col, int row, int colSpan = 1, int rowSpan = 1)
    {
        Grid.SetColumn(e, col);
        Grid.SetRow(e, row);
        Grid.SetColumnSpan(e, colSpan);
        Grid.SetRowSpan(e, rowSpan);
    }

    private void SetIcon(double box, double img, double fallbackFont)
    {
        IconBox.Width = box; IconBox.Height = box;
        Img.Width = img; Img.Height = img;
        FallbackText.FontSize = fallbackFont;
    }

    internal BoxItem? Item => DataContext as BoxItem;

    private void OnDoubleClick(object sender, MouseButtonEventArgs e) => OpenItem();

    private void OpenItem()
    {
        if (Item is null) return;
        if (IsLocalPathItem(Item) && !TargetExists(Item.TargetPath))
        {
            NotifyTargetUnavailable();
            return;
        }
        try
        {
            if (Item.Type == ItemType.SystemIcon || Item.TargetPath.StartsWith("::"))
                Process.Start(new ProcessStartInfo("explorer.exe", Item.TargetPath) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(Item.TargetPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.LogError(ex, "ItemTile.OpenItem");
            MessageBox.Show(string.Format(Localizer["dialog.openFail"], ex.Message),
                Localizer["app.errorTitle"], MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>右键:原生 Shell 菜单由独立 helper 进程提供,把第三方 Shell 扩展与主进程隔离。</summary>
    private async void OnPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var item = Item;
        if (item is null) return;

        // 多选批量场景:若当前处于多选(>1)且右键的是已选中项,不弹单文件 shell 菜单,
        // 让事件冒泡到 BoxControl 的 ContextMenu(含"从盒子移除选中项"等批量操作)。
        // 资源管理器也是这种行为:多选后右键弹的是批量菜单,而非单文件 verb 菜单。
        if (item.IsSelected)
        {
            var vm = App.Services.GetRequiredService<ViewModels.MainViewModel>();
            if (vm.GetSelectedItems().Count > 1) return;   // 放行 → 冒泡到 BoxControl 菜单
        }

        e.Handled = true;

        // 目标已不存在时 helper 常崩溃/失败,直接弹出精简菜单(含"从盒子移除")。
        if (IsLocalPathItem(item) && !TargetExists(item.TargetPath))
        {
            ShowFallbackMenu();
            return;
        }

        if (!await ShellMenuGate.WaitAsync(0))
            return;

        var pt = PointToScreen(new Point(0, ActualHeight));
        try
        {
            bool fallback = item.Type == ItemType.Url;
            if (!fallback)
            {
                try
                {
                    var runner = App.Services.GetRequiredService<IShellMenuRunner>();
                    var helperResult = await runner.ShowAsync(item.TargetPath, (int)pt.X, (int)pt.Y,
                        ShellMenuCommands.RemoveFromBox | ShellMenuCommands.Rename);
                    switch (helperResult.Status)
                    {
                        case ShellMenuRunStatus.StartFailed:
                        case ShellMenuRunStatus.IsolationUnavailable:
                            fallback = true;
                            break;
                        case ShellMenuRunStatus.RemoveFromBox:
                            RemoveFromBox();
                            return;
                        case ShellMenuRunStatus.Rename:
                            PromptRename(item);
                            return;
                        case ShellMenuRunStatus.TargetDeleted:
                            await RefreshAfterDeleteAsync(item);
                            return;
                        case ShellMenuRunStatus.Crashed or ShellMenuRunStatus.TimedOut:
                            App.LogError(
                                new InvalidOperationException(
                                    $"Shell menu helper {helperResult.Status}; exitCode={helperResult.ExitCode?.ToString() ?? "unknown"}; target={Services.LogService.Truncate(item.TargetPath)}"),
                                "ItemTile.ShellMenuHelper");
                            // 目标可能在菜单期间被删掉:优先给可操作的回退菜单,而不是只弹错误。
                            if (IsLocalPathItem(item) && !TargetExists(item.TargetPath))
                            {
                                fallback = true;
                                break;
                            }
                            InputDialog.Inform(Localizer["dialog.shellMenuFailed"]);
                            return;
                        case ShellMenuRunStatus.Completed:
                            break;
                    }
                }
                catch (Exception ex)
                {
                    App.LogError(ex, "ItemTile.ShowShellMenu");
                    fallback = true;
                }
            }
            if (fallback)
            {
                ShowFallbackMenu();
                return;
            }

            // 原生菜单可能执行了"删除/剪切/重命名"等命令,使目标路径失效。延一帧校验刷新。
            await Dispatcher.BeginInvoke(new Action(() =>
            {
                if (Item is not null && ReferenceEquals(Item, item) && IsLocalPathItem(item) && !TargetExists(item.TargetPath))
                    NotifyTargetUnavailable();
            }));
        }
        finally
        {
            ShellMenuGate.Release();
        }
    }

    /// <summary>右键菜单里选了"重命名":在名字上就地改(explorer 那种),不再弹对话框。
    /// 回车确认、Esc 取消、点到别处也算确认。</summary>
    private void PromptRename(BoxItem item)
    {
        var anchor = Views.InlineRenameBox.ScreenRectOf(NameText);
        if (anchor.IsEmpty) anchor = Views.InlineRenameBox.ScreenRectOf(this);
        if (anchor.IsEmpty) return;

        var current = System.IO.Path.GetFileName(item.TargetPath);
        _ = new Views.InlineRenameBox(current, anchor, NameText.FontSize > 0 ? NameText.FontSize : 12, name =>
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            App.Services.GetRequiredService<ViewModels.MainViewModel>().RenameItem(item, name);
        });
    }

    private void ShowFallbackMenu()
    {
        var menu = BuildContextMenu();
        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private static bool IsLocalPathItem(BoxItem item) =>
        item.Type != ItemType.SystemIcon && item.Type != ItemType.Url
        && !item.TargetPath.StartsWith("::", StringComparison.Ordinal);

    private static bool TargetExists(string path) =>
        File.Exists(path) || Directory.Exists(path);

    private async Task RefreshAfterDeleteAsync(BoxItem item)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (!ReferenceEquals(Item, item)) return;
            if (!TargetExists(item.TargetPath))
            {
                App.Services.GetRequiredService<ViewModels.MainViewModel>().RemoveItemAnywhere(item);
                return;
            }
            await Task.Delay(100);
        }
    }

    private void NotifyTargetUnavailable()
    {
        if (Item is null) return;
        InputDialog.Inform(string.Format(Localizer["dialog.targetGone"], Item.DisplayName));
    }

    private ContextMenu BuildContextMenu()
    {
        var item = Item!;
        var menu = new ContextMenu();

        if (item.Type != ItemType.SystemIcon && item.Type != ItemType.Url
            && !item.TargetPath.StartsWith("::"))
        {
            string[] verbs = Array.Empty<string>();
            try { verbs = new ProcessStartInfo(item.TargetPath) { UseShellExecute = true }.Verbs; }
            catch (Exception ex) { App.LogError(ex, "ItemTile.QueryVerbs"); }

            bool added = false;
            foreach (var v in verbs)
            {
                if (string.IsNullOrEmpty(v)) continue;
                var verb = v;
                var mi = new MenuItem { Header = VerbDisplayName(verb) };
                mi.Click += (_, _) =>
                {
                    try { Process.Start(new ProcessStartInfo(item.TargetPath) { Verb = verb, UseShellExecute = true }); }
                    catch (Exception ex) { App.LogError(ex, "ItemTile.VerbInvoke"); }
                };
                menu.Items.Add(mi);
                added = true;
            }
            if (added) menu.Items.Add(new Separator());

            var miLoc = new MenuItem { Header = Localizer["item.showInExplorer"] };
            miLoc.Click += (_, _) => OpenLocation();
            menu.Items.Add(miLoc);
        }
        else
        {
            var miOpen = new MenuItem { Header = Localizer["item.open"] };
            miOpen.Click += (_, _) => OpenItem();
            menu.Items.Add(miOpen);
        }

        var miCopy = new MenuItem { Header = Localizer["item.copyPath"] };
        miCopy.Click += (_, _) =>
        {
            try { Clipboard.SetText(item.TargetPath); } catch { }
        };
        menu.Items.Add(miCopy);

        menu.Items.Add(new Separator());

        var miRemove = new MenuItem { Header = Localizer["item.remove"] };
        miRemove.Click += (_, _) => RemoveFromBox();
        menu.Items.Add(miRemove);

        return menu;
    }

    /// <summary>Shell 动词 → 显示名:先查本地化 verb.&lt;name&gt;,找不到则首字母大写原样显示。</summary>
    private string VerbDisplayName(string verb)
    {
        var key = "verb." + verb.ToLowerInvariant();
        var localized = Localizer[key];
        if (!ReferenceEquals(localized, key) && localized != key) return localized;
        return char.ToUpperInvariant(verb[0]) + verb[1..].ToLowerInvariant();
    }

    private void OpenLocation()
    {
        if (Item is null) return;
        try
        {
            var original = Item.TargetPath;
            var target = original;
            if (original.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var resolved = Native.ShellLinkResolver.ResolveTarget(original);
                target = !string.IsNullOrEmpty(resolved) ? resolved : original;
            }
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { App.LogError(ex, "ItemTile.OpenLocation"); }
    }

    /// <summary>从盒子移除当前磁贴(不删文件本体)。</summary>
    private void RemoveFromBox()
    {
        if (Item is null) return;
        var vm = App.Services.GetRequiredService<ViewModels.MainViewModel>();
        vm.RemoveItemAnywhere(Item);
    }
}
