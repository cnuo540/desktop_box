using DesktopBox.Native;
using DesktopBox.Services;
using FluentAssertions;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Threading;

namespace DesktopBox.Tests;

public class ShellBehaviorTests
{
    [Fact]
    public void DesktopBoxProject_PublishesNativeShellMenuHelperWhenPresent()
    {
        var projectPath = FindRepositoryFile("src", "DesktopBox", "DesktopBox.csproj");
        var project = System.Xml.Linq.XDocument.Load(projectPath);
        var shellMenuItem = project
            .Descendants("Content")
            .SingleOrDefault(e => ((string?)e.Attribute("Include"))?.EndsWith("DesktopBox.ShellMenu.exe", StringComparison.OrdinalIgnoreCase) == true);

        shellMenuItem.Should().NotBeNull();
        shellMenuItem!.Attribute("Condition")?.Value.Should().Contain("Exists");
        GetItemMetadata(shellMenuItem, "CopyToOutputDirectory").Should().Be("PreserveNewest");
        GetItemMetadata(shellMenuItem, "CopyToPublishDirectory").Should().Be("PreserveNewest");
    }

    [Fact]
    public void AppManifest_DeclaresWindows8OrNewerCompatibilityForLayeredChildWindows()
    {
        var manifestPath = FindRepositoryFile("src", "DesktopBox", "app.manifest");
        var manifest = File.ReadAllText(manifestPath);

        manifest.Should().Contain("{4a2f28e3-53b9-4441-ba9c-d69d4a4a6e38}");
        manifest.Should().Contain("{1f676c76-80e1-4239-95bb-83d0f6d0da78}");
        manifest.Should().Contain("{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}");
    }

    [Fact]
    public void User32_ExposesLayeredWindowAlphaApis()
    {
        User32.GWL_EXSTYLE.Should().Be(-20);
        User32.WS_EX_LAYERED.Should().Be(0x00080000);
        User32.LWA_ALPHA.Should().Be(0x00000002);
        typeof(User32).GetMethod(nameof(User32.SetLayeredWindowAttributes)).Should().NotBeNull();
    }

    [Fact]
    public void BoxWindow_UsesHwndSourceWithLayeredStyleFromCreation()
    {
        var sourcePath = FindRepositoryFile("src", "DesktopBox", "Views", "BoxWindow.cs");
        var source = File.ReadAllText(sourcePath);

        source.Should().Contain("HwndSourceParameters");
        source.Should().Contain("WS_EX_LAYERED");
        source.Should().Contain("ExtendedWindowStyle");
        source.Should().Contain("CompositionTarget.BackgroundColor");
        source.Should().Contain("Colors.Transparent");
    }

    [Fact]
    public void BoxWindow_GuardsDisposedHwndSourceBeforeTouchingHandle()
    {
        var sourcePath = FindRepositoryFile("src", "DesktopBox", "Views", "BoxWindow.cs");
        var source = File.ReadAllText(sourcePath);

        source.Should().Contain("private readonly IntPtr _handle;");
        source.Should().Contain("_source.Disposed += OnSourceDisposed");
        source.Should().Contain("public IntPtr Handle => _disposed || _source.IsDisposed ? IntPtr.Zero : _handle;");
        source.Should().Contain("public bool IsHandleAlive => !_disposed && !_source.IsDisposed && Native.User32.IsWindow(_handle);");
        source.Should().Contain("if (_disposed || _source.IsDisposed)");
        source.Should().Contain("private void MarkDisposed()");
    }

    [Fact]
    public void BoxWindow_CanRepositionItselfWithoutTouchingZOrderEveryFrame()
    {
        var sourcePath = FindRepositoryFile("src", "DesktopBox", "Views", "BoxWindow.cs");
        var source = File.ReadAllText(sourcePath);

        // 盒子现在是顶层窗口(不再挂父窗口),但仍要能把"盒子排在桌面层之上"这件事做对 ——
        // 形参 above 就是这个参照窗口,没有它时退回 HWND_TOP。
        source.Should().Contain("EnsureVisibleOnDesktopHost");
        source.Should().Contain("insertAfter");
        source.Should().Contain("HWND_TOP");
        // 逐帧只调矩形不重排 Z 序（每帧重排会逼图标层重绘=缩放闪烁主因）。
        source.Should().Contain("SWP_NOZORDER");
    }

    [Fact]
    public void BoxWindow_StaysInTheNormalBand_NotTopmost()
    {
        var sourcePath = FindRepositoryFile("src", "DesktopBox", "Views", "BoxWindow.cs");
        var source = File.ReadAllText(sourcePath);

        // 关键约束:盒子必须留在<b>非置顶</b>档 —— 置顶会压住普通窗口(用户明确否决),
        // 而"非置顶 + 逐像素 alpha"同样能真透明(实测:非置顶顶层窗口也走 UpdateLayeredWindow)。
        source.Should().Contain("HWND_NOTOPMOST");
        source.Should().NotContain("HWND_TOPMOST");
        // 逐像素 alpha 必须开,否则半透明底会被合成成不透明黑
        source.Should().Contain("UsesPerPixelOpacity = true");
        source.Should().Contain("MoveResize(Native.User32.HWND_TOP)");
        source.Should().Contain("private void MoveResize(IntPtr insertAfter)");
    }

    [Fact]
    public void BoxWindow_ConvertsModelPixelsToWpfDipForContentSize()
    {
        var sourcePath = FindRepositoryFile("src", "DesktopBox", "Views", "BoxWindow.cs");
        var source = File.ReadAllText(sourcePath);

        source.Should().Contain("TransformToDevice");
        source.Should().Contain("Box.Width / scale.X");
        // 高度按 RenderHeight(卷起时=标题栏高度)换算,不再直接用 Box.Height
        source.Should().Contain("Box.RenderHeight / scale.Y");
    }

    [Fact]
    public void MainWindow_RepairsBoxWindowsWhenDesktopIconsVisibilityChanges()
    {
        var sourcePath = FindRepositoryFile("src", "DesktopBox", "Views", "MainWindow.xaml.cs");
        var source = File.ReadAllText(sourcePath);

        source.Should().Contain("nameof(MainViewModel.DesktopIconsVisible)");
        source.Should().Contain("RepairBoxWindowsAfterDesktopIconToggle");
        source.Should().Contain("RepairBoxWindowsOnDesktopLayer");
        source.Should().Contain("GetDesktopHost(refresh: true)");
    }

    [Fact]
    public void MainWindow_UsesShellDefViewAsPrimaryVisibleDesktopHost()
    {
        // 宿主解析已收敛到 Native.User32.ResolveDesktopHost（单入口，主窗口/覆盖层共用）。
        var sourcePath = FindRepositoryFile("src", "DesktopBox", "Native", "User32.cs");
        var source = File.ReadAllText(sourcePath);

        source.Should().Contain("ResolveDesktopHost");
        source.Should().Contain("FindShellDefView");
        source.Should().Contain("GetProgman");
        // 可见优先：DefView 是图标层本身，盒子挂它下面天然在壁纸之上。
        // v1.7.10 曾改 Progman 优先，Win11 上盒子被盖住不可见，v1.7.13 改回。
        // （比较 ResolveDesktopHost 方法体内的顺序，而非全文件首次出现位置。）
        var bodyStart = source.IndexOf("public static IntPtr ResolveDesktopHost", StringComparison.Ordinal);
        bodyStart.Should().BeGreaterThanOrEqualTo(0);
        var body = source[bodyStart..];
        body.IndexOf("FindShellDefView", StringComparison.Ordinal)
            .Should().BeLessThan(body.IndexOf("GetProgman", StringComparison.Ordinal));
    }

    [Fact]
    public void MainWindow_RebuildsBoxWindowsWhenDesktopHostOrHandleIsGone()
    {
        var sourcePath = FindRepositoryFile("src", "DesktopBox", "Views", "MainWindow.xaml.cs");
        var source = File.ReadAllText(sourcePath);

        source.Should().Contain("GetWorkerW");
        source.Should().Contain("IsHandleAlive");
        source.Should().Contain("RefreshDesktopLayer");
        source.Should().Contain("SyncBoxWindows");
        source.Should().Contain("TryGetValue");
    }

    [Fact]
    public void MainWindow_ReleasesTrayIconNativeHandle()
    {
        var sourcePath = FindRepositoryFile("src", "DesktopBox", "Views", "MainWindow.xaml.cs");
        var source = File.ReadAllText(sourcePath);

        source.Should().Contain("GetHicon");
        source.Should().Contain("DestroyIcon");
        source.Should().Contain("Clone");
    }

    [Fact]
    public void DesktopIconsService_ReadsActualDesktopListViewVisibilityBeforeRegistryFallback()
    {
        var servicePath = FindRepositoryFile("src", "DesktopBox", "Services", "DesktopIconsService.cs");
        var service = File.ReadAllText(servicePath);
        var user32Path = FindRepositoryFile("src", "DesktopBox", "Native", "User32.cs");
        var user32 = File.ReadAllText(user32Path);

        user32.Should().Contain("FindDesktopListView");
        user32.Should().Contain("SysListView32");
        service.Should().Contain("FindDesktopListView");
        service.Should().Contain("IsWindowVisible");
        service.IndexOf("FindDesktopListView", StringComparison.Ordinal)
            .Should().BeLessThan(service.IndexOf("OpenSubKey", StringComparison.Ordinal));
    }

    [Fact]
    public void BoxControl_ConstrainsWrapPanelsToScrollViewportWidth()
    {
        var xamlPath = FindRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml");
        var xaml = File.ReadAllText(xamlPath);

        xaml.Should().Contain("x:Name=\"ItemsScroll\"");
        xaml.Should().Contain("x:Name=\"ItemsViewport\"");
        xaml.Should().Contain("Path=ViewportWidth");
        // 面板按滚动视口宽度换行:靠"禁用横向滚动"(视口宽度即约束),而不是给面板设显式 Width。
        // 历史坑:面板 Width 曾绑 ItemsControl.ActualWidth——该 ItemsControl 一旦是 Collapsed
        // (切换图标大小后必然如此)ActualWidth 就是 0,面板宽度被钉死成 0,
        // 于是"点了图标大小按钮,下面的条目整片空白"(v1.7.22 线上问题)。
        xaml.Should().Contain("HorizontalScrollBarVisibility=\"Disabled\"");
        xaml.Should().NotContain("AncestorType=ItemsControl");
        // 防布局反馈振荡：垂直滚动条必须恒占位(Visible)。Auto 会在缩放过阈值时
        // 显隐翻转→ViewportWidth 变化→WrapPanel 重排→高度变化→再次翻转，64 磁贴跳行，
        // 透过 layered 窗拉伸成"窗口忽大忽小"。恒占位断环。
        xaml.Should().Contain("VerticalScrollBarVisibility=\"Visible\"");
    }

    [Fact]
    public void BoxControl_UsesHeaderMouseDragAndLargerResizeHitTargets()
    {
        var xamlPath = FindRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml");
        var xaml = File.ReadAllText(xamlPath);
        var codePath = FindRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml.cs");
        var code = File.ReadAllText(codePath);

        xaml.Should().Contain("MouseLeftButtonDown=\"OnHeaderDown\"");
        xaml.Should().Contain("MouseMove=\"OnHeaderMove\"");
        xaml.Should().Contain("MouseLeftButtonUp=\"OnHeaderUp\"");
        xaml.Should().Contain("LostMouseCapture=\"OnLostMouseCapture\"");
        xaml.Should().Contain("MouseEnter=\"OnBoxMouseEnter\"");
        xaml.Should().Contain("MouseLeave=\"OnBoxMouseLeave\"");
        xaml.Should().Contain("Height=\"10\" Margin=\"0,0,44,0\"");
        xaml.Should().Contain("Width=\"10\"");
        xaml.Should().Contain("Tag=\"S\"");
        xaml.Should().Contain("Height=\"18\" Margin=\"12,0,12,4\"");
        xaml.Should().Contain("Tag=\"E\"");
        xaml.Should().Contain("Width=\"18\" Margin=\"0,36,4,12\"");
        xaml.Should().Contain("Tag=\"NE\"");
        xaml.Should().Contain("Width=\"12\" Height=\"28\" Margin=\"0,4,0,0\"");
        xaml.Should().Contain("Tag=\"SE\"");
        xaml.Should().Contain("Width=\"28\" Height=\"28\" Margin=\"0,0,4,4\"");
        xaml.Should().Contain("DragStarted=\"OnResizeStarted\"");
        xaml.Should().Contain("DragCompleted=\"OnResizeCompleted\"");
        code.Should().Contain("OnHeaderMove");
        // 缩放用屏幕坐标锚定法（对角锚点+当前鼠标），零累积——旧版 DragDelta 累积量
        // 会被 Thumb 自身的移动反噬，形成正反馈振荡（窗口忽大忽小）。
        code.Should().Contain("_resizeAnchor");
        code.Should().Contain("ComputeAnchor");
        code.Should().Contain("Mouse.GetPosition(this)");
        code.Should().Contain("_isResizing = true");
        code.Should().Contain("if (!_isResizing || Vm is null) return");
        code.Should().Contain("OnResizeCompleted");
        code.Should().Contain("SystemParametersHelper.ClampIntoScreens");
        code.Should().Contain("NavigateByFirstLetter");
        code.Should().Contain("FirstLetterNavigator.FindNextIndex");
        code.Should().Contain("BringIntoView");
        code.Should().Contain("_subscribedVm.ViewModeChanged -= OnViewModeChanged");
        xaml.Should().NotContain("Width=\"{Binding Width}\"");
        xaml.Should().NotContain("Height=\"{Binding Height}\"");
    }

    [Fact]
    public void App_RegistersHoveredBoxFirstLetterKeyboardNavigator()
    {
        var appPath = FindRepositoryFile("src", "DesktopBox", "App.xaml.cs");
        var app = File.ReadAllText(appPath);
        var keyboardPath = FindRepositoryFile("src", "DesktopBox", "Controls", "FirstLetterKeyboardNavigator.cs");
        var keyboard = File.ReadAllText(keyboardPath);

        app.Should().Contain("AddSingleton<FirstLetterKeyboardNavigator>");
        keyboard.Should().Contain("SetWindowsHookEx");
        keyboard.Should().Contain("NavigationCharFromVirtualKey");
        keyboard.Should().Contain("TryHandleOnUiThread");
    }

    [Fact]
    public void ItemTile_UsesNativeFileDropAndNonBlockingDesktopOverlayForDragOut()
    {
        var tilePath = FindRepositoryFile("src", "DesktopBox", "Controls", "ItemTile.xaml.cs");
        var tile = File.ReadAllText(tilePath);
        var overlayPath = FindRepositoryFile("src", "DesktopBox", "Controls", "DesktopDropOverlay.cs");
        var overlay = File.ReadAllText(overlayPath);
        var dragPath = FindRepositoryFile("src", "DesktopBox", "Controls", "ItemDragDrop.cs");
        var drag = File.ReadAllText(dragPath);

        tile.Should().Contain("ItemDragDrop.CanDragAsFile");
        tile.Should().Contain("DragDrop.DoDragDrop");
        tile.Should().Contain("DesktopDropOverlay.TryCreate(boxBounds)");
        tile.Should().Contain("DroppedOnDesktop");
        tile.Should().NotContain("SetWindowPos(\r\n            boxHandle");
        tile.Should().NotContain("HwndSource.FromVisual");
        overlay.Should().Contain("AllowDrop = true");
        // 覆盖层是顶层分层窗口(挂成子窗口会丢掉 WS_EX_LAYERED,导致整屏变黑)
        overlay.Should().Contain("WS_POPUP");
        overlay.Should().Contain("HWND_TOPMOST");
        overlay.Should().Contain("ApplyExclusionRegion");
        overlay.Should().Contain("BuildExclusionRects");
        overlay.Should().Contain("SetWindowRgn");
        overlay.Should().Contain("RGN_DIFF");
        // 覆盖层与主窗口共用 Native.User32.ResolveDesktopHost（单入口，DefView 优先保证可见）。
        overlay.Should().Contain("ResolveDesktopHost");
        drag.Should().Contain("DataFormats.FileDrop");
    }

    [Fact]
    public void DesktopWindows_UseScreenCoordinatesBecauseTheyAreAllTopLevel()
    {
        var user32 = File.ReadAllText(FindRepositoryFile("src", "DesktopBox", "Native", "User32.cs"));
        var boxWindow = File.ReadAllText(FindRepositoryFile("src", "DesktopBox", "Views", "BoxWindow.cs"));
        var overlay = File.ReadAllText(FindRepositoryFile("src", "DesktopBox", "Controls", "DesktopDropOverlay.cs"));

        user32.Should().Contain("ScreenToClient");
        user32.Should().Contain("ScreenPointToClient");

        // 盒子与拖放覆盖层现在都是<b>顶层</b>窗口 —— 模型坐标就是屏幕坐标,不再做父窗口客户区换算。
        // 子窗口做不了半透明:与父窗口之间不做 alpha 混合,半透明底会被合成成不透明黑。
        boxWindow.Should().NotContain("ParentWindow = parent");
        boxWindow.Should().NotContain("ScreenPointToClient");
        boxWindow.Should().Contain("WS_POPUP");
        boxWindow.Should().NotContain("HWND_TOPMOST");

        overlay.Should().NotContain("ParentWindow = parent");
        overlay.Should().Contain("WS_POPUP");
        overlay.Should().Contain("HWND_TOPMOST");
    }

    [Fact]
    public void BoxBackground_IsTranslucent_BecauseTheBoxIsATopLevelAlphaWindow()
    {
        var xaml = File.ReadAllText(
            FindRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml"));
        var code = File.ReadAllText(
            FindRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml.cs"));

        // 半透明底:盒子窗口是"顶层 + 非置顶 + 逐像素 alpha",alpha 真的生效、真的透出壁纸。
        xaml.Should().Contain("x:Name=\"Card\"");
        xaml.Should().Contain("Background=\"#99000000\"");
        xaml.Should().Contain("Background=\"#66000000\"");
        xaml.Should().Contain("Background=\"#33000000\"");
        // 滑条缩的是 A 通道(越调越透)
        code.Should().Contain("BaseCardColor.A * factor");

        // 这两个条件缺一不可:少了逐像素 alpha 半透明会变成不透明黑;
        // 而子窗口上开逐像素 alpha 会被 WPF 剥掉 WS_EX_LAYERED,同样失效。
        var boxWindow = File.ReadAllText(
            FindRepositoryFile("src", "DesktopBox", "Views", "BoxWindow.cs"));
        boxWindow.Should().Contain("UsesPerPixelOpacity = true");
        boxWindow.Should().Contain("WS_POPUP");
        boxWindow.Should().NotContain("WS_CHILD");
        boxWindow.Should().NotContain("HWND_TOPMOST");
    }

    [Fact]
    public void LayeredWindowsWithTransparentContent_EnablePerPixelOpacity()
    {
        // 这是本项目已经犯过<b>两次</b>的错:窗口带 WS_EX_LAYERED、内容又是透明/半透明的,
        // 却没开 UsesPerPixelOpacity。此时那层 WS_EX_LAYERED 的 alpha 从未被初始化,透明像素会被
        // 合成成<b>不透明黑</b>:
        //   ① 桌面层(DesktopLayerWindow) —— 桌面图标底下出现整块黑矩形;
        //   ② 拖放覆盖层(DesktopDropOverlay) —— 拖动桌面文件时整个屏幕变黑,松手才恢复。

        var overlay = File.ReadAllText(
            FindRepositoryFile("src", "DesktopBox", "Controls", "DesktopDropOverlay.cs"));
        overlay.Should().Contain("UsesPerPixelOpacity = true");
        overlay.Should().NotContain("UsesPerPixelOpacity = false");

        var layer = File.ReadAllText(
            FindRepositoryFile("src", "DesktopBox", "Views", "DesktopLayerWindow.cs"));
        layer.Should().Contain("UsesPerPixelOpacity = true");
        layer.Should().NotContain("UsesPerPixelOpacity = false");

        // 盒子同样需要逐像素 alpha(它是半透明的顶层窗口,理由见 BoxWindow 的说明)。
        var box = File.ReadAllText(
            FindRepositoryFile("src", "DesktopBox", "Views", "BoxWindow.cs"));
        box.Should().Contain("UsesPerPixelOpacity = true");
        box.Should().NotContain("UsesPerPixelOpacity = false");
    }

    [Fact]
    public void TransparentOverlays_MustBeTopLevel_BecauseChildWindowsLoseLayeredFlag()
    {
        // 实测得到的硬约束:子窗口 + UsesPerPixelOpacity=true 会被 WPF <b>剥掉 WS_EX_LAYERED</b>
        // (建出来 ex=0x00000080,layered=False),于是"近乎全透明的黑色面板"变成不透明黑 ——
        // 拖动桌面文件时整屏变黑就是这么来的。透明覆盖层必须是顶层窗口。
        var overlay = File.ReadAllText(
            FindRepositoryFile("src", "DesktopBox", "Controls", "DesktopDropOverlay.cs"));

        overlay.Should().Contain("WS_POPUP");
        overlay.Should().NotContain("ParentWindow = parent");
        overlay.Should().NotContain("WS_CHILD");

        // 桌面层同理(它要透出壁纸),也必须是顶层
        var layer = File.ReadAllText(
            FindRepositoryFile("src", "DesktopBox", "Views", "DesktopLayerWindow.cs"));
        layer.Should().Contain("WS_POPUP");
        layer.Should().NotContain("WS_CHILD");
    }

    [Fact]
    public void BoxControl_RejectsDesktopBoxInternalDragOutDrops()
    {
        var codePath = FindRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml.cs");
        var code = File.ReadAllText(codePath);

        code.Should().Contain("ItemDragDrop.DragSourceItemFormat");
        code.Should().Contain("DragDropEffects.None");
        code.IndexOf("ItemDragDrop.DragSourceItemFormat", StringComparison.Ordinal)
            .Should().BeLessThan(code.IndexOf("DataFormats.FileDrop", StringComparison.Ordinal));
    }

    [Fact]
    public void BoxControl_SavesResizeOnlyWhenDragCompletes()
    {
        var codePath = FindRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml.cs");
        var code = File.ReadAllText(codePath);
        var resizeBody = code[code.IndexOf("private void OnResize(", StringComparison.Ordinal)..code.IndexOf("private void OnResizeCompleted", StringComparison.Ordinal)];

        resizeBody.Should().NotContain("ScheduleSave");
        code.Should().Contain("private void OnResizeCompleted");
        code.Should().Contain("MainVm.ScheduleSave()");
    }

    [Fact]
    public void MainWindow_DoesNotUseRejectedLayeredAlphaPatch()
    {
        var sourcePath = FindRepositoryFile("src", "DesktopBox", "Views", "MainWindow.xaml.cs");
        var source = File.ReadAllText(sourcePath);

        source.Should().NotContain("SetLayeredWindowAttributes");
    }

    [Fact]
    public void MainWindow_DoesNotUseSystemWideMinimizeAllToShowBoxes()
    {
        var sourcePath = FindRepositoryFile("src", "DesktopBox", "Views", "MainWindow.xaml.cs");
        var source = File.ReadAllText(sourcePath);

        source.Should().NotContain("MinimizeAll");
        source.Should().Contain("RefreshDesktopLayer");
    }

    [Fact]
    public void IconExtractorService_ResolvesShortcutIconLocationBeforeFallbackingToLnk()
    {
        var sourcePath = FindRepositoryFile("src", "DesktopBox", "Services", "IconExtractorService.cs");
        var source = File.ReadAllText(sourcePath);
        var resolverPath = FindRepositoryFile("src", "DesktopBox", "Native", "ShellLinkResolver.cs");
        var resolver = File.ReadAllText(resolverPath);

        source.Should().Contain("ExtractShortcutIcon");
        source.Should().Contain("ResolveIconLocation");
        source.Should().Contain("ExtractIconResource");
        source.Should().Contain("ResolveTarget(lnkPath)");
        source.Should().Contain("ExtractPathIcon(lnkPath");
        resolver.Should().Contain("IconLocation");
        resolver.Should().Contain("WScript.Shell");
    }

    [Fact]
    public void IconExtractorService_CanExtractIconFromShortcutIconLocation()
    {
        RunOnSta(() =>
        {
            var tempDir = Directory.CreateTempSubdirectory("dbx_").FullName;
            try
            {
                var targetPath = Path.Combine(tempDir, "target.txt");
                File.WriteAllText(targetPath, "x");

                var shortcutPath = Path.Combine(tempDir, "shortcut.lnk");
                var iconSource = Path.Combine(Environment.SystemDirectory, "shell32.dll");
                CreateShortcut(shortcutPath, targetPath, iconSource, 3);

                var resolved = ShellLinkResolver.ResolveIconLocation(shortcutPath);
                resolved.iconPath.Should().NotBeNull();
                resolved.iconPath!.Should().EndWith("shell32.dll");
                resolved.iconIndex.Should().Be(3);

                var extractor = new IconExtractorService();
                var png = extractor.Extract(shortcutPath);

                png.Should().NotBeNull();
                File.Exists(png!).Should().BeTrue();
                new FileInfo(png!).Length.Should().BeGreaterThan(0);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        });
    }

    [Fact]
    public void ShellChangeNotifierCanReRegisterAfterTaskbarRestart()
    {
        var sourcePath = FindRepositoryFile("src", "DesktopBox", "Services", "ShellChangeNotifierService.cs");
        var source = File.ReadAllText(sourcePath);
        var ifacePath = FindRepositoryFile("src", "DesktopBox", "Services", "IShellChangeNotifierService.cs");
        var iface = File.ReadAllText(ifacePath);

        source.Should().Contain("Register(IntPtr hwnd, bool force = false)");
        source.Should().Contain("SHChangeNotifyDeregister");
        source.Should().Contain("force");
        iface.Should().Contain("bool force = false");
    }

    [Fact]
    public void NativeShellMenu_SetsExplorerCompatibleClipboardForCutAndCopy()
    {
        var sourcePath = FindRepositoryFile("src", "DesktopBox.ShellMenu", "DesktopBox.ShellMenu.cpp");
        var source = File.ReadAllText(sourcePath);

        source.Should().Contain("GetCommandString");
        source.Should().Contain("GCS_VERBW");
        source.Should().Contain("SetFileClipboard");
        source.Should().Contain("CF_HDROP");
        source.Should().Contain("Preferred DropEffect");
        source.Should().Contain("DROPEFFECT_MOVE");
        source.Should().Contain("DROPEFFECT_COPY");
        source.Should().Contain("if (SetClipboardData(CF_HDROP, hDrop))");
        source.Should().Contain("hDrop = nullptr;");
        source.Should().Contain("if (SetClipboardData(preferredDropEffect, hEffect))");
        source.Should().Contain("hEffect = nullptr;");
        source.Should().NotContain("SetClipboardData(CF_HDROP, hDrop) && SetClipboardData");
    }

    [Fact]
    public void NativeShellMenu_UsesCharacterCapacityForUnicodeCommandVerb()
    {
        var sourcePath = FindRepositoryFile("src", "DesktopBox.ShellMenu", "DesktopBox.ShellMenu.cpp");
        var source = File.ReadAllText(sourcePath);

        source.Should().Contain("_countof(verb)");
        source.Should().NotContain("reinterpret_cast<LPSTR>(verb), sizeof(verb)");
    }

    [Fact]
    public void NativeShellMenu_BuildsAnExecutableProcessBoundary()
    {
        var sourcePath = FindRepositoryFile("src", "DesktopBox.ShellMenu", "DesktopBox.ShellMenu.cpp");
        var buildPath = FindRepositoryFile("src", "DesktopBox.ShellMenu", "build_dll.bat");
        var source = File.ReadAllText(sourcePath);
        var build = File.ReadAllText(buildPath);

        source.Should().Contain("int WINAPI wWinMain");
        source.Should().Contain("CommandLineToArgvW");
        source.Should().NotContain("__declspec(dllexport)");
        source.Should().NotContain("DllMain");
        build.Should().Contain("DesktopBox.ShellMenu.exe");
        build.Should().NotContain("/LD");
    }

    [Fact]
    public async Task ShellMenuProcessRunner_ReturnsRemoveCodeFromSuccessfulHelper()
    {
        var runner = new ShellMenuProcessRunner(
            Environment.GetEnvironmentVariable("ComSpec")!,
            TimeSpan.FromSeconds(2),
            ["/d", "/c", "exit /b 28672"]);

        var result = await runner.ShowAsync("ignored", 0x7000, 0);

        result.Status.Should().Be(ShellMenuRunStatus.RemoveFromBox);
    }

    [Fact]
    public async Task ShellMenuProcessRunner_ReturnsTargetDeletedCodeFromSuccessfulHelper()
    {
        var runner = new ShellMenuProcessRunner(
            Environment.GetEnvironmentVariable("ComSpec")!,
            TimeSpan.FromSeconds(2),
            ["/d", "/c", "exit /b 28674"]);

        var result = await runner.ShowAsync("ignored", 0, 0);

        result.Status.Should().Be(ShellMenuRunStatus.TargetDeleted);
    }

    [Fact]
    public async Task ShellMenuProcessRunner_ReturnsNullWhenHelperIsMissing()
    {
        var runner = new ShellMenuProcessRunner(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe"));

        var result = await runner.ShowAsync("ignored", 1, 2);

        result.Status.Should().Be(ShellMenuRunStatus.StartFailed);
    }

    [Fact]
    public async Task ShellMenuProcessRunner_ReturnsNullWhenHelperCrashes()
    {
        var runner = new ShellMenuProcessRunner(
            Environment.GetEnvironmentVariable("ComSpec")!,
            TimeSpan.FromSeconds(2),
            ["/d", "/c", "exit /b 9"]);

        var result = await runner.ShowAsync("ignored", 1, 2);

        result.Status.Should().Be(ShellMenuRunStatus.Crashed);
    }

    [Fact]
    public async Task ShellMenuProcessRunner_ReturnsNullOnTimeout()
    {
        var runner = new ShellMenuProcessRunner(
            Environment.GetEnvironmentVariable("ComSpec")!,
            TimeSpan.FromMilliseconds(100),
            ["/d", "/c", "ping 127.0.0.1 -n 6 >nul & rem"]);

        var result = await runner.ShowAsync("ignored", 1, 2);

        result.Status.Should().Be(ShellMenuRunStatus.TimedOut);
    }

    [Fact]
    public async Task ShellMenuProcessRunner_DisposeTerminatesActiveHelper()
    {
        var runner = new ShellMenuProcessRunner(
            Environment.GetEnvironmentVariable("ComSpec")!,
            TimeSpan.FromSeconds(10),
            ["/d", "/c", "ping 127.0.0.1 -n 6 >nul & rem"]);
        var run = runner.ShowAsync("ignored", 1, 2);
        await Task.Delay(100);

        runner.Dispose();
        var completed = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(3)));

        completed.Should().BeSameAs(run);
        (await run).Status.Should().Be(ShellMenuRunStatus.Crashed);
    }

    [Fact]
    public void ShellMenuJob_AllowsInvokedApplicationsToBreakAwayAndChecksAssignment()
    {
        var job = File.ReadAllText(FindRepositoryFile("src", "DesktopBox", "Services", "ShellMenuJob.cs"));
        var runner = File.ReadAllText(FindRepositoryFile("src", "DesktopBox", "Services", "ShellMenuProcessRunner.cs"));

        job.Should().Contain("JobObjectLimitSilentBreakawayOk");
        job.Should().Contain("JobObjectLimitKillOnJobClose | JobObjectLimitSilentBreakawayOk");
        runner.Should().Contain("isolated = _job.TryAssign(process);");
        runner.Should().Contain("if (!isolated)");
        // 宿主已在 Job 中（VS 调试/沙箱）时降级为无 Job 运行，只记日志，不再整单失败。
        runner.Should().Contain("降级运行");
    }

    [Fact]
    public void NativeShellMenu_EnablesPerMonitorV2BeforeCreatingWindows()
    {
        var sourcePath = FindRepositoryFile("src", "DesktopBox.ShellMenu", "DesktopBox.ShellMenu.cpp");
        var source = File.ReadAllText(sourcePath);

        source.Should().Contain("DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2");
        source.IndexOf("SetProcessDpiAwarenessContext", StringComparison.Ordinal)
            .Should().BeLessThan(source.IndexOf("ShowShellMenu(argv[1]", StringComparison.Ordinal));
    }

    [Fact]
    public void NativeShellMenu_ReportsDeleteAndUsesVisibleRecycleBinConfirmation()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "src", "DesktopBox.ShellMenu", "DesktopBox.ShellMenu.cpp"));

        source.Should().Contain("DBX_RESULT_TARGET_DELETED");
        source.Should().Contain("IsDeleteCommand");
        source.Should().Contain("IsEmptyRecycleBinCommand");
        source.Should().Contain("SHEmptyRecycleBinW(nullptr, nullptr, 0)");
        source.Should().NotContain("SHERB_NOCONFIRMATION");
    }

    [Fact]
    public void ItemTile_FallsBackWhenHelperCouldNotStartOrTargetMissing()
    {
        var sourcePath = FindRepositoryFile("src", "DesktopBox", "Controls", "ItemTile.xaml.cs");
        var source = File.ReadAllText(sourcePath);

        source.Should().Contain("ShellMenuRunStatus.StartFailed");
        source.Should().Contain("ShellMenuRunStatus.RemoveFromBox");
        source.Should().Contain("ShellMenuRunStatus.TargetDeleted");
        source.Should().Contain("RefreshAfterDeleteAsync");
        source.Should().Contain("ShellMenuRunStatus.Crashed or ShellMenuRunStatus.TimedOut");
        source.Should().Contain("ShellMenuRunStatus.IsolationUnavailable");
        source.Should().Contain("App.LogError(");
        source.Should().Contain("ItemTile.ShellMenuHelper");
        source.Should().Contain("IsLocalPathItem(item) && !TargetExists(item.TargetPath)");
        source.Should().Contain("ShowFallbackMenu");
    }

    [Fact]
    public void StartupConfigurationReadFailure_ShutsDownExplicitly()
    {
        var source = File.ReadAllText(FindRepositoryFile("src", "DesktopBox", "App.xaml.cs"));

        source.Should().Contain("catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)");
        source.Should().Contain("dialog.startupConfigFailed");
        source.Should().Contain("Shutdown();");
    }

    [Fact]
    public void NativeShellMenu_OpensAllPropertiesThroughShellExecute()
    {
        var sourcePath = FindRepositoryFile("src", "DesktopBox.ShellMenu", "DesktopBox.ShellMenu.cpp");
        var source = File.ReadAllText(sourcePath);

        source.Should().Contain("IsPropertiesCommand");
        source.Should().Contain("ShowShellProperties");
        source.Should().Contain("ShellExecuteExW");
        source.Should().Contain("SEE_MASK_INVOKEIDLIST");
        source.Should().Contain("L\"properties\"");
        source.Should().Contain("if (IsPropertiesCommand(verb))");
        source.Should().NotContain("IsShortcutPath(path) && IsPropertiesCommand");
    }

    [Fact]
    public void NativeShellMenu_KeepsPumpingUntilThePropertySheetCloses()
    {
        var source = File.ReadAllText(FindRepositoryFile("src", "DesktopBox.ShellMenu", "DesktopBox.ShellMenu.cpp"));

        // 属性框是非阻塞弹出、且挂在本进程上的:
        // ① owner 必须传 nullptr —— 传临时钩子窗口的话,随后的 DestroyWindow 会把它一起带走
        source.Should().Contain("sei.hwnd = nullptr;");
        // ② 弹完不能马上退出,要泵消息陪到它关闭(实测:调用方一退属性框就没了)
        source.Should().Contain("if (shown) PumpWhileSheetOpen();");
        source.Should().Contain("static void PumpWhileSheetOpen()");

        // helper 的存活上限要够长:默认 2 分钟会把用户开着的属性框杀掉
        var runner = File.ReadAllText(FindRepositoryFile("src", "DesktopBox", "Services", "ShellMenuProcessRunner.cs"));
        runner.Should().Contain("TimeSpan.FromMinutes(30)");
    }

    [Fact]
    public void NativeShellMenu_UninitializesComOnTheMenuThread()
    {
        var sourcePath = FindRepositoryFile("src", "DesktopBox.ShellMenu", "DesktopBox.ShellMenu.cpp");
        var source = File.ReadAllText(sourcePath);

        source.Should().Contain("CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED)");
        source.Should().Contain("const bool coInitialized = SUCCEEDED(hrCo)");
        source.Should().Contain("if (coInitialized) CoUninitialize();");
    }

    [Fact]
    public void NativeShellMenu_UsesThreadLocalContextMenuPointers()
    {
        var sourcePath = FindRepositoryFile("src", "DesktopBox.ShellMenu", "DesktopBox.ShellMenu.cpp");
        var source = File.ReadAllText(sourcePath);

        source.Should().Contain("static thread_local IContextMenu3* g_cm3");
        source.Should().Contain("static thread_local IContextMenu2* g_cm2");
    }

    [Fact]
    public void NativeShellMenu_DistinguishesInternalFailureFromUserCancel()
    {
        var sourcePath = FindRepositoryFile("src", "DesktopBox.ShellMenu", "DesktopBox.ShellMenu.cpp");
        var source = File.ReadAllText(sourcePath);

        source.Should().Contain("DBX_RESULT_FAILED");
        source.Should().Contain("int result = DBX_RESULT_FAILED;");
        source.Should().Contain("if (cmd == 0)");
        source.Should().Contain("result = 0;");
    }

    [Fact]
    public async Task StaTaskRunner_DoesNotBlockCallerWhileWorkRuns()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var task = StaTaskRunner.Run(() =>
        {
            started.Set();
            release.Wait();
            return Thread.CurrentThread.GetApartmentState();
        });

        started.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue();
        task.IsCompleted.Should().BeFalse();

        release.Set();
        var apartment = await task;
        apartment.Should().Be(ApartmentState.STA);
    }

    [Theory]
    [InlineData(Shell32.SHCNE_UPDATEIMAGE)]
    [InlineData(Shell32.SHCNE_ASSOCCHANGED)]
    [InlineData(Shell32.SHCNE_DELETE)]
    [InlineData(Shell32.SHCNE_UPDATEDIR)]
    [InlineData(Shell32.SHCNE_UPDATEITEM)]
    public void ShellNotificationsThatCanChangeRecycleBin_RefreshSystemIcons(uint evt)
    {
        ShellChangeNotifierService.ShouldRefreshSystemIcons(evt).Should().BeTrue();
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate repository file.", Path.Combine(parts));
    }

    private static string? GetItemMetadata(System.Xml.Linq.XElement item, string name) =>
        item.Attribute(name)?.Value ?? item.Element(name)?.Value;

    private static void CreateShortcut(string shortcutPath, string targetPath, string iconSource, int iconIndex)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        shellType.Should().NotBeNull("WScript.Shell should be available on Windows");

        dynamic shell = Activator.CreateInstance(shellType!)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetPath;
        shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath);
        shortcut.IconLocation = $"{iconSource},{iconIndex}";
        shortcut.Save();
    }

    private static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        exception?.Throw();
    }

}
