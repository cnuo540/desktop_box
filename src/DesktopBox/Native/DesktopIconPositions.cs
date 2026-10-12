using System;
using System.Runtime.InteropServices;
using DesktopBox.Services;

namespace DesktopBox.Native;

/// <summary>用桌面视图接口(IFolderView)读写桌面图标位置。
///
/// 为什么不用 ListView 消息:桌面图标层是 explorer 进程里的 SysListView32,
/// LVM_GETITEMPOSITION 需要把指针传进对方进程(得 VirtualAllocEx/WriteProcessMemory),
/// 既危险又容易被安全软件拦;而 IFolderView 是 COM 接口,由 COM 负责封送,安全得多。
///
/// 取桌面视图的链路(任一环失败都返回 false,由调用方降级,绝不抛):
///   ShellWindows.FindWindowSW(SWC_DESKTOP) → IServiceProvider.QueryService(SID_STopLevelBrowser)
///   → IShellBrowser.QueryActiveShellView → IFolderView
/// </summary>
public sealed class DesktopIconPositions : IDesktopIconPositions
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private const int SWC_DESKTOP = 0x8;
    private const int SWFO_NEEDDISPATCH = 0x1;
    private const uint SVSI_POSITIONITEM = 0x00000080;
    private const uint SFGAO_IN = 0;

    private static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");

    private static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IID_IShellBrowser = new("000214E2-0000-0000-C000-000000000046");

    /// <summary>桌面视图缓存。调用失败时置空,下次重取(explorer 重启后视图会失效)。</summary>
    private IFolderView? _view;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    // ---- COM 接口声明(占位槽位保证 vtable 顺序正确;占位方法不会被调用) ----

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProviderCom
    {
        void QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
    }

    /// <summary>IShellWindows(dual):IDispatch 的 7 个槽位用占位方法占住,保证后续方法在 vtable 中的位置正确。
    /// 只用到 FindWindowSW(第 9 个接口方法)。不用 dynamic 调 COM 是故意的:DLR 那条路在本项目里实测
    /// 会拿到 E_ACCESSDENIED(拒绝访问),声明接口直接走 vtable 才稳。</summary>
    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellWindows
    {
        void Dispatch01(); void Dispatch02(); void Dispatch03(); void Dispatch04();
        void Dispatch05(); void Dispatch06(); void Dispatch07();

        void GetCount(out int count);
        void Item(IntPtr index, out IntPtr dispatch);
        void NewEnum(out IntPtr enumUnknown);
        void Register(IntPtr dispatch, int hwnd, int swClass, out int cookie);
        void RegisterPending(int threadId, IntPtr loc, IntPtr root, int swClass, out int hwnd, int options, out int cookie);
        void Revoke(int cookie);
        void OnNavigate(int cookie, IntPtr loc);
        void OnActivated(int cookie, bool active);
        /// <summary>第 9 个接口方法:按类别找窗口(SWC_DESKTOP=桌面),返回它的 IDispatch。</summary>
        IntPtr FindWindowSW(IntPtr pvarLoc, IntPtr pvarRoot, int swClass, out int hwnd, int swfwOptions);
        void OnCreated(int cookie);
        void ProcessAttachDetach(bool attach);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void Slot01(); void Slot02(); void Slot03(); void Slot04(); void Slot05(); void Slot06();
        void Slot07(); void Slot08(); void Slot09(); void Slot10(); void Slot11(); void Slot12();
        /// <summary>第 13 槽:IShellBrowser::QueryActiveShellView。</summary>
        void QueryActiveShellView(out IntPtr ppshv);
        void Slot14(); void Slot15();
    }

    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        void GetCurrentViewMode(out uint pViewMode);
        void SetCurrentViewMode(uint viewMode);
        void GetFolder(ref Guid riid, out IntPtr ppv);
        void Item(int iItemIndex, out IntPtr ppidl);
        void ItemCount(uint uFlags, out int pcItems);
        void Items(uint uFlags, ref Guid riid, out IntPtr ppv);
        void GetSelectionMarkedItem(out int piItem);
        void GetFocusedItem(out int piItem);
        void GetItemPosition(IntPtr pidl, out POINT ppt);
        void GetSpacing(out POINT ppt);
        void GetDefaultSpacing(out POINT ppt);
        void GetAutoArrange();
        void SelectItem(int iItem, uint dwFlags);
        void SelectAndPositionItems(uint cid,
            [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
            [MarshalAs(UnmanagedType.LPArray)] POINT[] apt,
            uint dwFlags);
    }

    /// <summary>拿到(并缓存)桌面视图。失败返回 null。</summary>
    private IFolderView? TryGetView()
    {
        if (_view is not null) return _view;

        object? window = null;
        IntPtr browserPtr = IntPtr.Zero;
        IntPtr viewPtr = IntPtr.Zero;
        try
        {
            // ShellWindows 的桌面窗口:走声明接口(不用 dynamic,见 IShellWindows 注释)
            var shellWindowsType = Type.GetTypeFromCLSID(CLSID_ShellWindows);
            if (shellWindowsType is null) return null;

            var shellWindows = (IShellWindows?)Activator.CreateInstance(shellWindowsType);
            if (shellWindows is null) return null;

            var dispatchPtr = shellWindows.FindWindowSW(IntPtr.Zero, IntPtr.Zero, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH);
            if (dispatchPtr == IntPtr.Zero) return null;

            try
            {
                window = Marshal.GetObjectForIUnknown(dispatchPtr);
                var provider = (IServiceProviderCom)window;
                var sid = SID_STopLevelBrowser;
                var iid = IID_IShellBrowser;
                provider.QueryService(ref sid, ref iid, out browserPtr);
                if (browserPtr == IntPtr.Zero) return null;

                var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(browserPtr);
                browser.QueryActiveShellView(out viewPtr);
                if (viewPtr == IntPtr.Zero) return null;

                var viewObject = Marshal.GetObjectForIUnknown(viewPtr);
                _view = viewObject as IFolderView;
                return _view;
            }
            finally
            {
                if (dispatchPtr != IntPtr.Zero) { try { Marshal.Release(dispatchPtr); } catch { } }
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("DesktopIconPositions.View", Services.LogService.Truncate(ex.Message, 160));
            _view = null;
            return null;
        }
        finally
        {
            if (viewPtr != IntPtr.Zero) { try { Marshal.Release(viewPtr); } catch { } }
            if (browserPtr != IntPtr.Zero) { try { Marshal.Release(browserPtr); } catch { } }
            GC.KeepAlive(window);
        }
    }

    public bool TryParse(string path, out IntPtr item)
    {
        item = IntPtr.Zero;
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var hr = SHParseDisplayName(path, IntPtr.Zero, out var pidl, SFGAO_IN, out _);
            if (hr != 0 || pidl == IntPtr.Zero) return false;
            item = pidl;
            return true;
        }
        catch { return false; }
    }

    public void Free(IntPtr item)
    {
        if (item == IntPtr.Zero) return;
        try { CoTaskMemFree(item); } catch { }
    }

    public bool TryGet(IntPtr item, out int x, out int y)
    {
        x = 0;
        y = 0;
        var view = TryGetView();
        if (view is null || item == IntPtr.Zero) return false;
        try
        {
            view.GetItemPosition(item, out var point);
            x = point.X;
            y = point.Y;
            return true;
        }
        catch
        {
            _view = null;   // 视图可能已失效(explorer 重启),下次重取
            return false;
        }
    }

    public bool TrySet(IntPtr item, int x, int y)
    {
        var view = TryGetView();
        if (view is null || item == IntPtr.Zero) return false;
        try
        {
            view.SelectAndPositionItems(1, new[] { item }, new[] { new POINT { X = x, Y = y } }, SVSI_POSITIONITEM);
            return true;
        }
        catch
        {
            _view = null;
            return false;
        }
    }
}
