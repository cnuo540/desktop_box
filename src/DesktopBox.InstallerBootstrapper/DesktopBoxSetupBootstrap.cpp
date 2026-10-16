#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string>
#include <vector>

namespace {
constexpr WORD CoreResourceId = 101;

bool ShowError(const wchar_t* message) {
    MessageBoxW(nullptr, message, L"DesktopBox Setup", MB_OK | MB_ICONERROR);
    return false;
}

bool ExtractCore(std::wstring& path) {
    wchar_t tempDir[MAX_PATH] = {};
    const DWORD length = GetTempPathW(static_cast<DWORD>(std::size(tempDir)), tempDir);
    if (length == 0 || length >= std::size(tempDir))
        return ShowError(L"无法获取临时目录。请检查 TEMP/TMP 环境变量。" );

    wchar_t tempFile[MAX_PATH] = {};
    if (GetTempFileNameW(tempDir, L"dbx", 0, tempFile) == 0)
        return ShowError(L"无法创建安装临时文件。请检查临时目录权限。" );

    DeleteFileW(tempFile);
    path = tempFile;
    path += L".exe";

    HRSRC resource = FindResourceW(nullptr, MAKEINTRESOURCEW(CoreResourceId), MAKEINTRESOURCEW(10)); // 10 = RT_RCDATA
    if (!resource)
        return ShowError(L"安装包缺少核心安装程序。" );

    HGLOBAL loaded = LoadResource(nullptr, resource);
    const DWORD size = SizeofResource(nullptr, resource);
    void* data = loaded ? LockResource(loaded) : nullptr;
    if (!data || size == 0)
        return ShowError(L"无法读取核心安装程序。" );

    HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW,
                              FILE_ATTRIBUTE_TEMPORARY, nullptr);
    if (file == INVALID_HANDLE_VALUE)
        return ShowError(L"无法写入安装临时文件。" );

    DWORD written = 0;
    const bool ok = WriteFile(file, data, size, &written, nullptr) && written == size;
    CloseHandle(file);
    if (!ok) {
        DeleteFileW(path.c_str());
        return ShowError(L"写入核心安装程序失败。" );
    }
    return true;
}

/// 组合核心安装包的命令行:把外层收到的参数(如 /VERYSILENT)原样转过去,
/// 这样静默安装这类用法不会因为多了一层启动器而失效。
std::wstring BuildCommandLine(const std::wstring& core) {
    std::wstring command = L"\"" + core + L"\"";

    const wchar_t* raw = GetCommandLineW();
    if (!raw) return command;

    const wchar_t* p = raw;
    while (*p == L' ' || *p == L'\t') ++p;
    if (*p == L'"') {                       // argv[0] 是带引号的路径
        ++p;
        while (*p && *p != L'"') ++p;
        if (*p == L'"') ++p;
    } else {                                // 或者一直读到第一个空白
        while (*p && *p != L' ' && *p != L'\t') ++p;
    }
    while (*p == L' ' || *p == L'\t') ++p;

    if (*p) {
        command += L' ';
        command += p;
    }
    return command;
}
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    std::wstring core;
    if (!ExtractCore(core)) return 1;

    std::wstring commandLine = BuildCommandLine(core);
    std::vector<wchar_t> command(commandLine.begin(), commandLine.end());
    command.push_back(L'\0');
    STARTUPINFOW startup = {};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process = {};
    const bool started = CreateProcessW(nullptr, command.data(), nullptr, nullptr, FALSE,
                                        0, nullptr, nullptr, &startup, &process);
    if (!started) {
        DeleteFileW(core.c_str());
        ShowError(L"无法启动核心安装程序。" );
        return 1;
    }

    CloseHandle(process.hThread);
    WaitForSingleObject(process.hProcess, INFINITE);
    DWORD exitCode = 1;
    GetExitCodeProcess(process.hProcess, &exitCode);
    CloseHandle(process.hProcess);
    DeleteFileW(core.c_str());
    return static_cast<int>(exitCode);
}
