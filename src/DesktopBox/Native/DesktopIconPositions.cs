using System;
using System.Collections.Concurrent;
using System.Reflection;
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
    private static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");

    /// <summary>子 PIDL → 它所属的整块 PIDL(释放时必须释放整块,否则会破坏堆)。</summary>
    private readonly ConcurrentDictionary<IntPtr, IntPtr> _pidlOwner = new();

    /// <summary>桌面视图缓存。调用失败时置空,下次重取(explorer 重启后视图会失效)。</summary>
    private IFolderView? _view;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    /// <summary>取 PIDL 的最后一个 SHITEMID —— 即"子 PIDL",IFolderView 要的就是这个形态。</summary>
    [DllImport("shell32.dll")]
    private static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    // ---- COM 接口声明(占位槽位保证 vtable 顺序正确;占位方法不会被调用) ----

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProviderCom
    {
        void QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
    }

    /// <summary>IShellWindows(dual):IDispatch 只占 4 个槽位(IUnknown 的 3 个由 CLR 处理,不能声明),
    /// 且 IShellWindows 的方法顺序必须与 exdisp.idl 完全一致 ——
    /// get_Count, get__NewEnum, Item, <b>FindWindowSW(第 4 个)</b>, OnCreated, ProcessAttachDetach,
    /// Register, RegisterPending, Revoke, OnNavigate, OnActivated。
    ///
    /// 这两个错误(占位多写了 3 个 + FindWindowSW 排到第 9 位)正是 v1.7.29 真机崩溃的原因:
    /// 调用落到了错误的函数上 → 访问违例(不是异常,抓不住,进程直接消失)。
    /// 现在 FindWindowSW 一律走 IDispatch 调用(见 TryGetView),不再依赖这段 vtable。</summary>
    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellWindows
    {
        void Dispatch01(); void Dispatch02(); void Dispatch03(); void Dispatch04();

        void GetCount(out int count);
        void NewEnum(out IntPtr enumUnknown);
        void Item(IntPtr index, out IntPtr dispatch);
        IntPtr FindWindowSW(IntPtr pvarLoc, IntPtr pvarRoot, int swClass, out int hwnd, int swfwOptions);
        void OnCreated(int cookie);
        void ProcessAttachDetach(bool attach);
        void Register(IntPtr dispatch, int hwnd, int swClass, out int cookie);
        void RegisterPending(int threadId, IntPtr loc, IntPtr root, int swClass, out int hwnd, int options, out int cookie);
        void Revoke(int cookie);
        void OnNavigate(int cookie, IntPtr loc);
        void OnActivated(int cookie, bool active);
    }

    /// <summary>IShellFolder:只需要 ParseDisplayName(把路径解析成 PIDL)。</summary>
    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
                              out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        void Slot02(); void Slot03(); void Slot04(); void Slot05(); void Slot06();
        void Slot07(); void Slot08(); void Slot09(); void Slot10(); void Slot11();
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
            var shellWindowsType = Type.GetTypeFromCLSID(CLSID_ShellWindows);
            if (shellWindowsType is null) return null;

            var shellWindows = Activator.CreateInstance(shellWindowsType);
            if (shellWindows is null) return null;

            // FindWindowSW 走 IDispatch 调用:它有两个 VARIANT* 参数和一个 [out,retval] IDispatch**,
            // 手写 vtable 极易错(错一次就是进程崩溃),而 IDispatch 由系统负责参数转换。
            window = shellWindows.GetType().InvokeMember(
                "FindWindowSW", BindingFlags.InvokeMethod, null, shellWindows,
                new object?[] { null, null, SWC_DESKTOP, 0, SWFO_NEEDDISPATCH });
            if (window is null) return null;

            if (window is not IServiceProviderCom provider)
            {
                LogService.Warn("DesktopIconPositions.View", "桌面窗口不支持 IServiceProvider");
                return null;
            }

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
        if (string.IsNullOrWhiteSpace(path)) return false;

        var view = TryGetView();
        if (view is null) return false;

        IntPtr folderPtr = IntPtr.Zero;
        IntPtr fullPidl = IntPtr.Zero;
        try
        {
            // 用"视图自己的文件夹"解析路径,再取最后一个 SHITEMID 作为子 PIDL。
            // 直接用 SHParseDisplayName 的结果会得到 E_FAIL:那是整块绝对 PIDL,不是子 PIDL。
            var iid = IID_IShellFolder;
            view.GetFolder(ref iid, out folderPtr);
            if (folderPtr == IntPtr.Zero) return false;

            var folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr);
            var attributes = 0u;
            folder.ParseDisplayName(IntPtr.Zero, IntPtr.Zero, path, out _, out fullPidl, ref attributes);
            if (fullPidl == IntPtr.Zero) return false;

            var child = ILFindLastID(fullPidl);
            if (child == IntPtr.Zero) return false;

            _pidlOwner[child] = fullPidl;
            item = child;
            return true;
        }
        catch (Exception ex)
        {
            LogService.Warn("DesktopIconPositions.Parse", LogService.Truncate(ex.Message, 160));
            _view = null;
            if (fullPidl != IntPtr.Zero) { try { CoTaskMemFree(fullPidl); } catch { } }
            return false;
        }
        finally
        {
            if (folderPtr != IntPtr.Zero) { try { Marshal.Release(folderPtr); } catch { } }
        }
    }

    public void Free(IntPtr item)
    {
        if (item == IntPtr.Zero) return;
        // 释放整块 PIDL(子 PIDL 只是它中间的一个指针)
        var original = _pidlOwner.TryRemove(item, out var owner) ? owner : item;
        try { CoTaskMemFree(original); } catch { }
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
