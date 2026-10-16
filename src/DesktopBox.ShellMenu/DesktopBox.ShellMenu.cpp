// DesktopBox.ShellMenu.cpp
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shlobj.h>
#include <shellapi.h>
#include <objbase.h>
#include <shldisp.h>
#include <cstdio>
#include <string>

#define DBX_CMD_FIRST 1
#define DBX_CMD_LAST  0x6FFF
#define DBX_ID_REMOVE 0x7000
#define DBX_RESULT_FAILED 0x7001
#define DBX_RESULT_TARGET_DELETED 0x7002
// 菜单项 id 同时用作退出码:选它就返回它。都在 CMD_LAST 之上,不会和 shell 的命令撞。
#define DBX_ID_RENAME 0x7003

// 调用方追加的额外菜单项(第 4 个参数,逗号分隔;缺省=只加"从盒子移除",兼容旧调用)。
struct MenuOptions {
    bool removeFromBox = false;
    bool rename = false;
};

static MenuOptions ParseMenuOptions(const wchar_t* flags) {
    MenuOptions options;
    if (!flags) {                 // 没传:保持老行为(盒子里条目用)
        options.removeFromBox = true;
        return options;
    }
    const std::wstring text = flags;
    options.removeFromBox = text.find(L"remove") != std::wstring::npos;
    options.rename = text.find(L"rename") != std::wstring::npos;
    return options;
}

// Unicode build still sees CFSTR_PREFERREDDROPEFFECT as a narrow SDK macro here.
static const wchar_t* kPreferredDropEffectFormat = L"Preferred DropEffect";
static const wchar_t* kMenuWndClass = L"DesktopBox_ShellMenuHook_4F8A";
static thread_local IContextMenu3* g_cm3 = nullptr;
static thread_local IContextMenu2* g_cm2 = nullptr;

static bool EqualsVerb(const wchar_t* actual, const wchar_t* expected) {
    return actual && _wcsicmp(actual, expected) == 0;
}

static bool IsPropertiesCommand(const std::wstring& verb) {
    return EqualsVerb(verb.c_str(), L"properties");
}

static bool IsDeleteCommand(const std::wstring& verb) {
    return EqualsVerb(verb.c_str(), L"delete");
}

static bool IsEmptyRecycleBinCommand(const std::wstring& verb) {
    return EqualsVerb(verb.c_str(), L"empty");
}

/// 属性框是**非阻塞**弹出、且挂在调用方进程上的:
/// ① owner 必须传 nullptr —— 传我们那个临时钩子窗口的话,后面 DestroyWindow 会把它一起带走;
/// ② 本进程必须活着并继续泵消息,直到属性框关闭(实测:调用方一退属性框就没了,
///    用户看到的就是"点属性没反应")。
static bool ShowShellProperties(const wchar_t* path) {
    if (!path || !*path) return false;

    SHELLEXECUTEINFOW sei = {};
    sei.cbSize = sizeof(sei);
    sei.fMask = SEE_MASK_INVOKEIDLIST;
    sei.hwnd = nullptr;
    sei.lpVerb = L"properties";
    sei.lpFile = path;
    sei.nShow = SW_SHOWNORMAL;
    return ShellExecuteExW(&sei) != FALSE;
}

static void DbgLog(const char* step, long hr = 0);   // 实现在下面;这里先声明,供 PumpWhileSheetOpen 用

/// 本进程当前有没有**可见的**顶层对话框(#32770)——用来判断属性框还开着没。
/// 必须带可见性过滤:有些组件会在进程里常驻隐藏的 #32770,只看类名会秒判"已经出现/已经关闭",
/// 于是 helper 立刻退出,把还没显示出来的属性框一起带走(实测踩到)。
static bool HasOwnDialog() {
    struct Ctx { DWORD pid; bool found; };
    Ctx ctx = { GetCurrentProcessId(), false };
    EnumWindows([](HWND hwnd, LPARAM lp) -> BOOL {
        auto* ctx = reinterpret_cast<Ctx*>(lp);
        DWORD pid = 0;
        GetWindowThreadProcessId(hwnd, &pid);
        if (pid != ctx->pid) return TRUE;
        if (!IsWindowVisible(hwnd)) return TRUE;
        wchar_t cls[64] = {};
        if (GetClassNameW(hwnd, cls, _countof(cls)) > 0 && wcscmp(cls, L"#32770") == 0) {
            ctx->found = true;
            return FALSE;
        }
        return TRUE;
    }, reinterpret_cast<LPARAM>(&ctx));
    return ctx.found;
}

/// 先等属性框出现,再一直泵消息到它关闭(上限兜底,防止卡住不退)。
/// 等待给足:第三方属性页(压缩/安全软件/IDE 等扩展)第一次加载可能要几秒,等太短会以为"没弹"
/// 而立刻退出 —— 进程一退属性框就被带走,又回到"点了没反应"。
static void PumpWhileSheetOpen() {
    const DWORD start = GetTickCount();
    const DWORD kAppearWaitMs = 25000;               // 等它出现
    const DWORD kMaxWaitMs = 30 * 60 * 1000;         // 属性框最多陪用户 30 分钟
    bool seen = false;
    DWORD elapsed = 0;
    for (;;) {
        MSG msg;
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); }

        const bool open = HasOwnDialog();
        if (open) seen = true;
        if (seen && !open) break;                    // 属性框关了 → 收工
        elapsed = GetTickCount() - start;
        if (!seen && elapsed >= kAppearWaitMs) break;
        if (elapsed >= kMaxWaitMs) break;
        Sleep(30);
    }

    char buf[96];
    sprintf_s(buf, "PumpWhileSheetOpen seen=%d elapsedMs=%lu", (int)seen, (unsigned long)elapsed);
    DbgLog(buf);
}

static std::wstring GetCommandVerb(IContextMenu* cm, UINT idCmd) {
    wchar_t verb[128] = {};
    HRESULT hr = cm->GetCommandString(idCmd, GCS_VERBW, nullptr, reinterpret_cast<LPSTR>(verb), _countof(verb));
    if (FAILED(hr) || !verb[0])
        return L"";
    return verb;
}

static bool SetFileClipboard(const wchar_t* path, DWORD dropEffect) {
    if (!path || !*path) return false;

    const size_t pathChars = wcslen(path) + 1;
    const size_t bytes = sizeof(DROPFILES) + (pathChars + 1) * sizeof(wchar_t);
    HGLOBAL hDrop = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, bytes);
    if (!hDrop) return false;

    auto* drop = static_cast<DROPFILES*>(GlobalLock(hDrop));
    if (!drop) {
        GlobalFree(hDrop);
        return false;
    }
    drop->pFiles = sizeof(DROPFILES);
    drop->fWide = TRUE;
    auto* fileList = reinterpret_cast<wchar_t*>(reinterpret_cast<BYTE*>(drop) + sizeof(DROPFILES));
    memcpy(fileList, path, pathChars * sizeof(wchar_t));
    fileList[pathChars] = L'\0';
    GlobalUnlock(hDrop);

    HGLOBAL hEffect = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, sizeof(DWORD));
    if (!hEffect) {
        GlobalFree(hDrop);
        return false;
    }
    auto* effect = static_cast<DWORD*>(GlobalLock(hEffect));
    if (!effect) {
        GlobalFree(hEffect);
        GlobalFree(hDrop);
        return false;
    }
    *effect = dropEffect;
    GlobalUnlock(hEffect);

    if (!OpenClipboard(nullptr)) {
        GlobalFree(hEffect);
        GlobalFree(hDrop);
        return false;
    }

    bool ok = false;
    EmptyClipboard();
    const UINT preferredDropEffect = RegisterClipboardFormatW(kPreferredDropEffectFormat);
    if (SetClipboardData(CF_HDROP, hDrop)) {
        hDrop = nullptr;
        if (SetClipboardData(preferredDropEffect, hEffect)) {
            hEffect = nullptr;
            ok = true;
        }
    }
    CloseClipboard();

    if (hEffect) GlobalFree(hEffect);
    if (hDrop) GlobalFree(hDrop);
    return ok;
}

static void DbgLog(const char* step, long hr) {
#ifdef DESKTOPBOX_SHELLMENU_DIAG
    wchar_t exePath[MAX_PATH] = {};
    GetModuleFileNameW(nullptr, exePath, MAX_PATH);
    std::wstring dir = exePath;
    size_t pos = dir.find_last_of(L"\\/");
    if (pos != std::wstring::npos) dir = dir.substr(0, pos);
    CreateDirectoryW((dir + L"\\logs").c_str(), nullptr);
    FILE* f = nullptr;
    if (_wfopen_s(&f, (dir + L"\\logs\\shellmenu.log").c_str(), L"a") == 0 && f) {
        fprintf(f, "[%s] hr=0x%lX\n", step, (unsigned long)hr);
        fclose(f);
    }
#else
    (void)step;
    (void)hr;
#endif
}

static LRESULT CALLBACK MenuWndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    if (g_cm3) {
        LRESULT result = 0;
        if (SUCCEEDED(g_cm3->HandleMenuMsg2(msg, wParam, lParam, &result)))
            return result;
    } else if (g_cm2) {
        if (SUCCEEDED(g_cm2->HandleMenuMsg(msg, wParam, lParam)))
            return 0;
    }
    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

static HWND CreateHookWindow() {
    WNDCLASSW wc = {};
    wc.lpfnWndProc = MenuWndProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = kMenuWndClass;
    RegisterClassW(&wc);
    return CreateWindowExW(0, kMenuWndClass, L"", WS_POPUP, -32000, -32000, 1, 1,
                           nullptr, nullptr, wc.hInstance, nullptr);
}

static int ShowShellMenu(const wchar_t* path, int screenX, int screenY, const MenuOptions& options) {
    if (!path || !*path) { DbgLog("null/empty path"); return DBX_RESULT_FAILED; }
    // Record the helper argument for optional native diagnostics.
    char pathBuf[160] = {};
    WideCharToMultiByte(CP_UTF8, 0, path, -1, pathBuf, sizeof(pathBuf), nullptr, nullptr);
    { char buf[256]; sprintf_s(buf, "=== start === path=[%s] len=%zu x=%d y=%d", pathBuf, wcslen(path), screenX, screenY); DbgLog(buf); }

    HRESULT hrCo = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    { char buf[80]; sprintf_s(buf, "CoInitializeEx hr=0x%lX", (unsigned long)hrCo); DbgLog(buf); }
    const bool coInitialized = SUCCEEDED(hrCo);
    if (FAILED(hrCo)) return DBX_RESULT_FAILED;

    int result = DBX_RESULT_FAILED;
    PIDLIST_ABSOLUTE pidl = nullptr;
    IShellFolder* parent = nullptr;
    IContextMenu* cm = nullptr;
    PCUITEMID_CHILD childPidl = nullptr;
    HMENU hMenu = nullptr;
    HWND hwndMenu = nullptr;

    HRESULT hr = SHParseDisplayName(path, nullptr, &pidl, 0, nullptr);
    DbgLog("SHParseDisplayName", hr);
    if (FAILED(hr) || !pidl) goto cleanup;

    hr = SHBindToParent(pidl, IID_PPV_ARGS(&parent), &childPidl);
    DbgLog("SHBindToParent", hr);
    if (FAILED(hr) || !parent) goto cleanup;

    hr = parent->GetUIObjectOf(nullptr, 1, &childPidl, IID_IContextMenu, nullptr,
                               reinterpret_cast<void**>(&cm));
    DbgLog("GetUIObjectOf", hr);
    if (FAILED(hr) || !cm) goto cleanup;

    g_cm3 = nullptr; g_cm2 = nullptr;
    cm->QueryInterface(IID_IContextMenu3, reinterpret_cast<void**>(&g_cm3));
    if (!g_cm3) cm->QueryInterface(IID_IContextMenu2, reinterpret_cast<void**>(&g_cm2));
    DbgLog(g_cm3 ? "QI-cm3-OK" : (g_cm2 ? "QI-cm2-OK" : "QI-no-cm2/3"));

    hMenu = CreatePopupMenu();
    if (!hMenu) { DbgLog("CreatePopupMenu-FAIL"); goto cleanup; }

    hr = cm->QueryContextMenu(hMenu, 0, DBX_CMD_FIRST, DBX_CMD_LAST, CMF_NORMAL);
    DbgLog("QueryContextMenu", hr);
    if (FAILED(hr)) goto cleanup;

    if (options.removeFromBox || options.rename) {
        AppendMenuW(hMenu, MF_SEPARATOR, 0, nullptr);
        // "重命名"(重=91CD 命=547D 名=540D)
        if (options.rename)
            AppendMenuW(hMenu, MF_STRING, DBX_ID_RENAME, L"\x91CD\x547D\x540D");
        // "从盒子移除"(从=4ECE 盒=76D2 子=5B50 移=79FB 除=9664)
        if (options.removeFromBox)
            AppendMenuW(hMenu, MF_STRING, DBX_ID_REMOVE, L"\x4ECE\x76D2\x5B50\x79FB\x9664");
    }

    hwndMenu = CreateHookWindow();
    if (!hwndMenu) { DbgLog("CreateHookWindow-FAIL"); goto cleanup; }

    SetForegroundWindow(hwndMenu);
    UINT cmd = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_LEFTALIGN | TPM_RIGHTBUTTON,
                                screenX, screenY, hwndMenu, nullptr);
    { char buf[64]; sprintf_s(buf, "TrackPopupMenuEx cmd=%u", (unsigned)cmd); DbgLog(buf); }

    if (cmd == 0) {
        result = 0;
    } else if (cmd == DBX_ID_REMOVE) {
        result = DBX_ID_REMOVE;
    } else if (cmd == DBX_ID_RENAME) {
        result = DBX_ID_RENAME;      // 交给宿主去问新名字并改真实文件名
    } else if (cmd >= DBX_CMD_FIRST && cmd <= DBX_CMD_LAST) {
        const UINT idCmd = cmd - DBX_CMD_FIRST;
        const std::wstring verb = GetCommandVerb(cm, idCmd);
        if (IsPropertiesCommand(verb)) {
            // 由 helper 自己弹(宿主里调 shell 的 COM 会 AV 崩进程),但弹完**不能马上退**:
            // 属性框挂在本进程上,要泵消息陪到它关闭。见 ShowShellProperties 的说明。
            const bool shown = ShowShellProperties(path);
            DbgLog(shown ? "ShowShellProperties-OK" : "ShowShellProperties-FAIL");
            if (shown) PumpWhileSheetOpen();
            result = 0;
            goto cleanup;
        }
        if (IsEmptyRecycleBinCommand(verb)) {
            hr = SHEmptyRecycleBinW(nullptr, nullptr, 0);
            DbgLog("SHEmptyRecycleBin", hr);
            result = 0;
            goto cleanup;
        }

        CMINVOKECOMMANDINFOEX info = {};
        info.cbSize = sizeof(info);
        info.fMask = CMIC_MASK_UNICODE;
        info.hwnd = hwndMenu;
        info.lpVerb = MAKEINTRESOURCEA(idCmd);
        info.lpVerbW = MAKEINTRESOURCEW(idCmd);
        info.nShow = SW_SHOWNORMAL;
        hr = cm->InvokeCommand(reinterpret_cast<CMINVOKECOMMANDINFO*>(&info));
        DbgLog("InvokeCommand", hr);
        if (SUCCEEDED(hr)) {
            if (EqualsVerb(verb.c_str(), L"cut")) {
                DbgLog(SetFileClipboard(path, DROPEFFECT_MOVE) ? "SetFileClipboard-cut-OK" : "SetFileClipboard-cut-FAIL");
            } else if (EqualsVerb(verb.c_str(), L"copy")) {
                DbgLog(SetFileClipboard(path, DROPEFFECT_COPY) ? "SetFileClipboard-copy-OK" : "SetFileClipboard-copy-FAIL");
            }
            result = IsDeleteCommand(verb) ? DBX_RESULT_TARGET_DELETED : 0;
        }
    }

cleanup:
    if (hwndMenu) DestroyWindow(hwndMenu);
    if (hMenu) DestroyMenu(hMenu);
    if (g_cm3) { g_cm3->Release(); g_cm3 = nullptr; }
    if (g_cm2) { g_cm2->Release(); g_cm2 = nullptr; }
    if (cm) cm->Release();
    if (parent) parent->Release();
    if (pidl) ILFree(pidl);
    DbgLog("=== end ===");
    if (coInitialized) CoUninitialize();
    return result;
}

// ===== 预热 =====
//
// 实测(本机):shell 扩展<b>首次</b>加载时 QueryContextMenu 要 8 秒以上,热了只要 0.28~2.5 秒。
// 这段代价如果落在用户第一次右键上,菜单就要等好几秒。所以 App 在启动后用 --warm 在后台
// 把菜单"建一遍再销毁",把扩展提前加载进系统和本进程缓存,用户真正右键时就快了。
//
// 只建菜单、不显示、不 Invoke:不会弹任何界面,也不产生副作用。
static int WarmShellExtensions(const wchar_t* path) {
    if (!path || !*path) return DBX_RESULT_FAILED;

    HRESULT hrCo = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    const bool coInitialized = SUCCEEDED(hrCo);
    if (FAILED(hrCo)) return DBX_RESULT_FAILED;

    PIDLIST_ABSOLUTE pidl = nullptr;
    IShellFolder* parent = nullptr;
    IContextMenu* cm = nullptr;
    PCUITEMID_CHILD childPidl = nullptr;
    HMENU hMenu = nullptr;
    int result = DBX_RESULT_FAILED;

    HRESULT hr = SHParseDisplayName(path, nullptr, &pidl, 0, nullptr);
    if (FAILED(hr) || !pidl) goto warmCleanup;

    hr = SHBindToParent(pidl, IID_PPV_ARGS(&parent), &childPidl);
    if (FAILED(hr) || !parent) goto warmCleanup;

    hr = parent->GetUIObjectOf(nullptr, 1, &childPidl, IID_IContextMenu, nullptr,
                               reinterpret_cast<void**>(&cm));
    if (FAILED(hr) || !cm) goto warmCleanup;

    hMenu = CreatePopupMenu();
    if (hMenu) {
        // 这一步就是慢的那一步 —— 扩展在这里被加载。
        cm->QueryContextMenu(hMenu, 0, DBX_CMD_FIRST, DBX_CMD_LAST, CMF_NORMAL);
        DbgLog("warm-QueryContextMenu-done");
        result = 0;
    }

warmCleanup:
    if (hMenu) DestroyMenu(hMenu);
    if (cm) cm->Release();
    if (parent) parent->Release();
    if (pidl) ILFree(pidl);
    if (coInitialized) CoUninitialize();
    return result;
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv) return 1;

    // --warm <path> : 只预热,不弹菜单。参数个数与正常路径不同,必须先判。
    if (argc == 3 && wcscmp(argv[1], L"--warm") == 0) {
        const int warmResult = WarmShellExtensions(argv[2]);
        LocalFree(argv);
        return warmResult;
    }

    if (argc != 4 && argc != 5) {
        LocalFree(argv);
        return 1;
    }

    wchar_t* endX = nullptr;
    wchar_t* endY = nullptr;
    const long screenX = wcstol(argv[2], &endX, 10);
    const long screenY = wcstol(argv[3], &endY, 10);
    if (!endX || *endX || !endY || *endY) {
        LocalFree(argv);
        return 1;
    }

    const int result = ShowShellMenu(argv[1], static_cast<int>(screenX), static_cast<int>(screenY),
                                     ParseMenuOptions(argc >= 5 ? argv[4] : nullptr));
    LocalFree(argv);
    return result;
}
