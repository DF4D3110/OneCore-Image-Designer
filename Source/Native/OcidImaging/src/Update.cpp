// OCID Imaging —— 落包（UpdateDLL 编排）实现
//
// 见 Update.h 的调用序列说明。这里只做"编排"，不做任何包处理逻辑：
// 真正的暂存/提交由微软 UpdateDLL.dll 完成，我们负责把它的上下文、store id、
// 布局与盘符配置按时序喂进去，并把错误码翻译成人能看懂的信息。

#include "ocid/imaging/Update.h"

#include "ocid/imaging/Common.h"
#include "ocid/imaging/Pool.h"

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <cstdio>
#include <mutex>
#include <string>
#include <vector>

namespace ocid
{
namespace imaging
{
namespace
{

typedef void* (__cdecl* CreateContextFn)();
typedef int (__cdecl* ReleaseContextFn)(void* context);
typedef int (__cdecl* InitializeFn)(void* context, int storeCount, void* storeIds,
                                    const wchar_t* actionList, const wchar_t* packageRoot,
                                    const wchar_t* updateInput, const wchar_t* updateOutput,
                                    const wchar_t* stagingDirectory, const wchar_t* logFile,
                                    unsigned int flags);
typedef int (__cdecl* PrepareUpdateFn)(void* context, unsigned int flags);
typedef int (__cdecl* ExecuteUpdateFn)(void* context, unsigned int flags);
typedef void (__cdecl* SetPathFn)(void* context, const wchar_t* path);
typedef void (__cdecl* AddSpaceFn)(void* context, const void* storeId);
typedef void (__cdecl* SetLayoutsFn)(void* context, const wchar_t* const* layouts, int count);
typedef void (__cdecl* DeinitializeFn)(void* context);

std::string HrText(long hr)
{
    char buffer[32] = { 0 };
    std::snprintf(buffer, sizeof(buffer), "0x%08lX", static_cast<unsigned long>(hr));
    return std::string(buffer);
}

std::string Narrow(const std::wstring& text)
{
    return WideToUtf8(text);
}

    std::vector<std::string> g_privLog;

    bool EnablePrivilege(const wchar_t* name)
    {
        HANDLE token = nullptr;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, &token)) return false;
        LUID luid = {};
        bool ok = false;
        if (LookupPrivilegeValueW(nullptr, name, &luid))
        {
            TOKEN_PRIVILEGES tp = {};
            tp.PrivilegeCount = 1;
            tp.Privileges[0].Luid = luid;
            tp.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;
            ok = AdjustTokenPrivileges(token, FALSE, &tp, 0, nullptr, nullptr) != FALSE;
            {
                const DWORD last = GetLastError();
                char line[512] = {};
                wsprintfA(line, "priv %ls ok=%d err=%lu", name, ok ? 1 : 0, last);
                g_privLog.push_back(line);
            }
        }
        CloseHandle(token);
        return ok;
    }

    void EnableImagingPrivileges()
    {
        EnablePrivilege(SE_BACKUP_NAME);
        EnablePrivilege(SE_RESTORE_NAME);
        EnablePrivilege(SE_TAKE_OWNERSHIP_NAME);
        EnablePrivilege(SE_SECURITY_NAME);
        EnablePrivilege(SE_DEBUG_NAME);
    }

}  // namespace

const char* UpdateVersion()
{
    return "0.1.0";
}

namespace
{
typedef HANDLE(__stdcall* WimCreateFileFn)(const wchar_t*, unsigned long, unsigned long, unsigned long,
                                           unsigned long, unsigned long*);
typedef HANDLE(__stdcall* WimCaptureImageFn)(HANDLE, const wchar_t*, unsigned long);
typedef HANDLE(__stdcall* WimSetBootImageFn)(HANDLE, unsigned long);
typedef int(__stdcall* WimCloseHandleFn)(HANDLE);

std::string NarrowPath(const std::wstring& text)
{
    if (text.empty()) return std::string();
    const int need = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (need <= 1) return std::string();
    std::string out(static_cast<std::size_t>(need - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, &out[0], need - 1, nullptr, nullptr);
    return out;
}

bool EnsureDirTreeLocal(const std::wstring& path, std::string& error)
{
    if (path.empty()) { error = "empty directory path"; return false; }
    // 设备路径（\\?\Volume{..}\、\\?\C:\、\\server\share\）的前缀不能拿去 CreateDirectoryW，
    // 否则会得到 ERROR_INVALID_NAME(123)；先切出前缀，再逐段创建。
    std::size_t start = 0;
    if (path.size() >= 4 && path.compare(0, 4, L"\\\\?\\") == 0) start = 4;
    else if (path.size() >= 4 && path.compare(0, 4, L"\\??\\") == 0) start = 4;
    else if (path.size() >= 2 && path[0] == L'\\' && path[1] == L'\\') start = 2;
    const std::size_t slash = path.find_first_of(L"\\/", start);
    const std::size_t prefixEnd = (slash == std::wstring::npos) ? path.size() : slash + 1;
    if (prefixEnd >= path.size()) return true;

    std::wstring current = path.substr(0, prefixEnd);
    for (std::size_t i = prefixEnd; i < path.size(); ++i)
    {
        const wchar_t ch = path[i];
        if (ch == L'\\' || ch == L'/')
        {
            if (i + 1 == path.size()) break;
            if (!CreateDirectoryW(current.c_str(), nullptr))
            {
                const DWORD code = GetLastError();
                if (code != ERROR_ALREADY_EXISTS)
                {
                    error = "CreateDirectoryW failed (win32=" + std::to_string(code) + ")";
                    return false;
                }
            }
            current.push_back(ch);
            continue;
        }
        current.push_back(ch);
    }
    if (!current.empty() && current.back() != L'\\' && current.back() != L'/')
    {
        if (!CreateDirectoryW(current.c_str(), nullptr))
        {
            const DWORD code = GetLastError();
            if (code != ERROR_ALREADY_EXISTS)
            {
                error = "CreateDirectoryW failed (win32=" + std::to_string(code) + ")";
                return false;
            }
        }
    }
    return true;
}

// ImageApp 在 Initialize 之前先往 MainOS 的 PROGRAMS\UpdateOS\UpdateOS.wim 里捕一个空 WIM；
// 提交阶段 UpdateDLL 再用它替换成真正的 UpdateOS 内容。
bool CreateEmptyUpdateOsWim(const std::wstring& mainOsPath, const std::wstring& tempRoot, std::string& error)
{
    std::wstring base = mainOsPath;
    if (base.size() >= 4 && base.compare(0, 4, L"\\??\\") == 0) base = L"\\\\?\\" + base.substr(4);
    if (!base.empty() && base.back() != L'\\' && base.back() != L'/') base.push_back(L'\\');
    const std::wstring wimPath = base + L"PROGRAMS\\UpdateOS\\UpdateOS.wim";
    std::wstring dir = wimPath;
    const std::size_t cut = dir.find_last_of(L"\\/");
    if (cut != std::wstring::npos) dir.erase(cut);
    std::string dirError;
    if (!EnsureDirTreeLocal(dir, dirError)) { error = "create UpdateOS directory failed: " + dirError; return false; }

    std::wstring captureDir = tempRoot;
    if (!captureDir.empty() && captureDir.back() != L'\\') captureDir.push_back(L'\\');
    captureDir += L"UpdateOSWim";
    if (!EnsureDirTreeLocal(captureDir, dirError)) { error = "create empty WIM capture dir failed: " + dirError; return false; }

    std::wstring wimDll;
    std::string findError;
    if (!PoolFindAdkDll(L"wimgapi.dll", "WIMCreateFile", wimDll, findError))
    {
        error = "wimgapi.dll not found: " + findError;
        return false;
    }
    HMODULE module = LoadLibraryExW(wimDll.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (module == nullptr)
    {
        error = "LoadLibraryExW(wimgapi) failed (win32=" + std::to_string(GetLastError()) + ")";
        return false;
    }
    WimCreateFileFn createWim = reinterpret_cast<WimCreateFileFn>(GetProcAddress(module, "WIMCreateFile"));
    WimCaptureImageFn captureImage = reinterpret_cast<WimCaptureImageFn>(GetProcAddress(module, "WIMCaptureImage"));
    WimSetBootImageFn setBootImage = reinterpret_cast<WimSetBootImageFn>(GetProcAddress(module, "WIMSetBootImage"));
    WimCloseHandleFn closeWim = reinterpret_cast<WimCloseHandleFn>(GetProcAddress(module, "WIMCloseHandle"));
    if (createWim == nullptr || captureImage == nullptr || closeWim == nullptr)
    {
        FreeLibrary(module);
        error = "wimgapi.dll missing WIMCreateFile/WIMCaptureImage/WIMCloseHandle";
        return false;
    }
    unsigned long creationResult = 0;
    HANDLE handle = createWim(wimPath.c_str(), 0x40000000ul, 2ul, 2ul, 2ul, &creationResult);
    if (handle == INVALID_HANDLE_VALUE || handle == nullptr)
    {
        const DWORD code = GetLastError();
        FreeLibrary(module);
        error = "WIMCreateFile failed (win32=" + std::to_string(code) + "): " + NarrowPath(wimPath);
        return false;
    }
    // WIMCaptureImage / WIMSetBootImage 返回的是新的 WIM 句柄（不是 BOOL）。
    HANDLE captured = captureImage(handle, captureDir.c_str(), 0);
    if (captured == nullptr || captured == INVALID_HANDLE_VALUE)
    {
        const DWORD code = GetLastError();
        closeWim(handle);
        FreeLibrary(module);
        error = "WIMCaptureImage failed (win32=" + std::to_string(code) + ")";
        return false;
    }
    if (setBootImage != nullptr)
    {
        HANDLE booted = setBootImage(captured, 1);
        if (booted != nullptr && booted != INVALID_HANDLE_VALUE && booted != captured) closeWim(booted);
    }
    closeWim(captured);
    if (captured != handle) closeWim(handle);
    FreeLibrary(module);
    return true;
}
} // namespace

bool UpdateCreateEmptyUpdateOsWim(const std::wstring& mainOsPath, const std::wstring& tempRoot, std::string& error)
{
    error.clear();
    return CreateEmptyUpdateOsWim(mainOsPath, tempRoot, error);
}

namespace
{
bool CreateWimViaChildProcess(const std::wstring& mainOsPath, const std::wstring& tempRoot, std::string& error)
{
    wchar_t exePath[MAX_PATH * 4] = {0};
    const DWORD count = GetModuleFileNameW(nullptr, exePath, static_cast<DWORD>(sizeof(exePath) / sizeof(exePath[0])));
    if (count == 0 || count >= sizeof(exePath) / sizeof(exePath[0]))
    {
        error = "GetModuleFileNameW failed";
        return false;
    }
    std::wstring mainOsClean = mainOsPath;
    while (!mainOsClean.empty() && (mainOsClean.back() == L'\\' || mainOsClean.back() == L'/')) mainOsClean.pop_back();
    std::wstring tempClean = tempRoot;
    while (!tempClean.empty() && (tempClean.back() == L'\\' || tempClean.back() == L'/')) tempClean.pop_back();
    std::wstring commandLine = L"\"";
    commandLine += exePath;
    commandLine += L"\" wim create-empty \"";
    commandLine += mainOsClean;
    commandLine += L"\" \"";
    commandLine += tempClean;
    commandLine += L"\"";
    std::vector<wchar_t> buffer(commandLine.begin(), commandLine.end());
    buffer.push_back(0);
    STARTUPINFOW startup;
    ZeroMemory(&startup, sizeof(startup));
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process;
    ZeroMemory(&process, sizeof(process));
    if (!CreateProcessW(nullptr, buffer.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW,
                        nullptr, nullptr, &startup, &process))
    {
        error = "CreateProcessW(wim create-empty) failed (win32=" + std::to_string(GetLastError()) + ")";
        return false;
    }
    const DWORD waited = WaitForSingleObject(process.hProcess, 180000);
    DWORD code = 0;
    GetExitCodeProcess(process.hProcess, &code);
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    if (waited != WAIT_OBJECT_0)
    {
        error = "wim create-empty timeout";
        return false;
    }
    if (code != 0)
    {
        error = "wim create-empty failed (exit=" + std::to_string(code) + ")";
        return false;
    }
    return true;
}
} // namespace

namespace {
// UpdateDLL 的诊断消息只通过 IU_LogTo 注册的回调送出来（ImageApp 的 LogUtil.IULogTo 就是它），
// 不注册的话我们只能拿到一个 HRESULT。回调签名是 void(*)(const wchar_t*)，默认 __stdcall。
typedef void(__stdcall* IuLogSink)(const wchar_t* message);
typedef void(__cdecl* IuLogToFn)(IuLogSink, IuLogSink, IuLogSink, IuLogSink);
typedef void(__cdecl* IuProgressFn)(int phase);

std::mutex g_iuLogMutex;
HANDLE g_iuLogHandle = INVALID_HANDLE_VALUE;

void IuLogWrite(const char* level, const wchar_t* message)
{
    if (message == nullptr) return;
    // 不能用 CRT 的 fprintf：回调发生在 UpdateDLL 的线程里，%s/流状态会触发 invalid parameter handler。
    char text[4096];
    const int n = WideCharToMultiByte(CP_UTF8, 0, message, -1, text, sizeof(text) - 1, nullptr, nullptr);
    if (n <= 0) return;
    text[n - 1] = 0;

    char line[4200];
    const int len = wsprintfA(line, "[iu][%s] %s" "\r\n", level, text);
    if (len <= 0) return;

    std::lock_guard<std::mutex> guard(g_iuLogMutex);
    DWORD written = 0;
    HANDLE out = GetStdHandle(STD_ERROR_HANDLE);
    if (out != nullptr && out != INVALID_HANDLE_VALUE)
    {
        WriteFile(out, line, static_cast<DWORD>(len), &written, nullptr);
    }
    if (g_iuLogHandle != INVALID_HANDLE_VALUE)
    {
        WriteFile(g_iuLogHandle, line, static_cast<DWORD>(len), &written, nullptr);
    }
}

void __stdcall IuLogError(const wchar_t* message) { IuLogWrite("error", message); }
void __stdcall IuLogWarning(const wchar_t* message) { IuLogWrite("warning", message); }
void __stdcall IuLogInfo(const wchar_t* message) { IuLogWrite("info", message); }
void __stdcall IuLogDebug(const wchar_t* message) { IuLogWrite("debug", message); }
} // namespace

bool UpdateApply(const UpdateRequest& request, std::vector<std::string>& report, std::string& error)
{
    report.clear();
    error.clear();
    g_privLog.clear();
    EnableImagingPrivileges();
    for (std::size_t i = 0; i < g_privLog.size(); ++i) report.push_back(g_privLog[i]);

    if (request.imagePath.empty()) { error = "没有给虚拟磁盘（--vhd）"; return false; }
    if (request.actionListFile.empty()) { error = "没有给 ActionList（--action-list）"; return false; }
    if (request.packagesRoot.empty()) { error = "没有给包根目录（--packages）"; return false; }
    if (request.updateInputFile.empty()) { error = "没有给 UpdateInput（--update-input）"; return false; }
    if (request.updateOutputFile.empty()) { error = "没有给 UpdateOutput（--update-output）"; return false; }

    std::wstring updateDllPath;
    if (!PoolFindAdkDll(L"UpdateDLL.dll", "AddImagingSpacesOverride", updateDllPath, error))
    {
        error = "找不到 UpdateDLL.dll：" + error;
        return false;
    }
    HMODULE module = LoadLibraryExW(updateDllPath.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (module == nullptr)
    {
        error = "LoadLibraryExW(UpdateDLL) 失败（win32=" + std::to_string(GetLastError()) + "）";
        return false;
    }
    CreateContextFn createContext = reinterpret_cast<CreateContextFn>(GetProcAddress(module, "CreateUpdateContext"));
    ReleaseContextFn releaseContext = reinterpret_cast<ReleaseContextFn>(GetProcAddress(module, "ReleaseUpdateContext"));
    InitializeFn initialize = reinterpret_cast<InitializeFn>(GetProcAddress(module, "Initialize"));
    PrepareUpdateFn prepareUpdate = reinterpret_cast<PrepareUpdateFn>(GetProcAddress(module, "PrepareUpdate"));
    ExecuteUpdateFn executeUpdate = reinterpret_cast<ExecuteUpdateFn>(GetProcAddress(module, "ExecuteUpdate"));
    SetPathFn setHistoryPath = reinterpret_cast<SetPathFn>(GetProcAddress(module, "SetAdditionalUpdateHistoryPath"));
    SetPathFn setOverlapList = reinterpret_cast<SetPathFn>(GetProcAddress(module, "SetOverlapFileList"));
    SetPathFn setDriveLetterConfig = reinterpret_cast<SetPathFn>(GetProcAddress(module, "SetDriveLetterConfigPath"));
    AddSpaceFn addSpace = reinterpret_cast<AddSpaceFn>(GetProcAddress(module, "AddImagingSpacesOverride"));
    SetLayoutsFn setLayouts = reinterpret_cast<SetLayoutsFn>(GetProcAddress(module, "SetAllDeviceLayoutPaths"));
    DeinitializeFn deinitialize = reinterpret_cast<DeinitializeFn>(GetProcAddress(module, "Deinitialize"));
    if (createContext == nullptr || releaseContext == nullptr || initialize == nullptr ||
        prepareUpdate == nullptr || executeUpdate == nullptr || setHistoryPath == nullptr ||
        setDriveLetterConfig == nullptr || addSpace == nullptr || setLayouts == nullptr)
    {
        FreeLibrary(module);
        error = "UpdateDLL.dll missing required exports (CreateUpdateContext/Initialize/PrepareUpdate/ExecuteUpdate/...)";
        if (!updateDllPath.empty()) error += " (" + std::string(updateDllPath.begin(), updateDllPath.end()) + ")";
        return false;
    }

    // UpdateDLL 的诊断消息只走 IU_LogTo 注册的回调；不注册就只有一个裸 HRESULT。
    if (g_iuLogHandle == INVALID_HANDLE_VALUE && !request.logFile.empty())
    {
        g_iuLogHandle = CreateFileW(request.logFile.c_str(), FILE_APPEND_DATA,
                                   FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS,
                                   FILE_ATTRIBUTE_NORMAL, nullptr);
    }
    IuLogToFn logTo = reinterpret_cast<IuLogToFn>(GetProcAddress(module, "IU_LogTo"));
    if (logTo != nullptr)
    {
        logTo(IuLogError, IuLogWarning, IuLogInfo, IuLogDebug);
        report.push_back("IU_LogTo 已注册");
    }
    IuProgressFn iuProgress = reinterpret_cast<IuProgressFn>(GetProcAddress(module, "IU_InitializeDefaultProgressReporting"));

    // 1) 挂盘、开池、逐 space 取 store id（随后交给 UpdateDLL）。
    std::vector<std::vector<unsigned char> > storeIds;
    std::vector<std::string> attachReport;
    OutLn("[apply] mounting pool spaces ...");
    std::wstring mainOsPath;
    if (!PoolMountForUpdate(request.imagePath, request.poolName, request.poolVersion,
                            request.stagingDirectory, request.spaces, request.mainOsSpaceName, storeIds, mainOsPath,
                            attachReport, error))
    {
        FreeLibrary(module);
        return false;
    }
    for (const std::string& line : attachReport) report.push_back(line);
    if (storeIds.empty())
    {
        FreeLibrary(module);
        error = "没有取到任何 store id";
        return false;
    }

    // ImageApp copies the original OEMInput.xml into <MainOS>\Windows\ImageUpdate before staging
    // (Imaging.CreateImage) and the commit phase reads it back from the image.
    if (!request.oemInputFile.empty())
    {
        std::wstring imageUpdateDir = mainOsPath;
        if (!imageUpdateDir.empty() && imageUpdateDir.back() != L'\\') imageUpdateDir.push_back(L'\\');
        imageUpdateDir += L"Windows\\ImageUpdate";
        if (!EnsureDirTreeLocal(imageUpdateDir, error))
        {
            FreeLibrary(module);
            return false;
        }
        const std::wstring imageOemInput = imageUpdateDir + L"\\OEMInput.xml";
        OutLn("[apply] copying OEMInput.xml into the image ...");
        if (!CopyFileW(request.oemInputFile.c_str(), imageOemInput.c_str(), FALSE))
        {
            error = "把 OEMInput.xml 写入镜像失败（win32=" + std::to_string(GetLastError()) + "）";
            FreeLibrary(module);
            return false;
        }
        report.push_back("OEMInput.xml 已写入镜像 Windows\\ImageUpdate");
    }

    OutLn("[apply] creating empty UpdateOS.wim ...");
    // MainOS 上现场造空 UpdateOS.wim：ImageApp 在 Initialize 之前就这么做。
    if (!CreateWimViaChildProcess(mainOsPath, request.stagingDirectory, error))
    {
        FreeLibrary(module);
        return false;
    }
    report.push_back("MainOS 空 UpdateOS.wim 已建");

    // UpdateDLL 期望 STORE_ID[] 连续 20 字节一块，这里拼成一块缓冲。
    std::vector<unsigned char> flat;
    flat.reserve(storeIds.size() * 20);
    for (const std::vector<unsigned char>& id : storeIds)
    {
        if (id.size() != 20) continue;
        flat.insert(flat.end(), id.begin(), id.end());
    }
    // UpdateDLL decides the store style from an environment variable when the store does not
    // exist yet (iustore.cpp: "Store does not exist and no env var is present...").
    {
        const std::string style = request.storeStyle.empty() ? std::string("onecore") : request.storeStyle;
        if (style == "desktop") SetEnvironmentVariableW(L"DESKTOP_STORE", L"1");
        else if (style == "state-separated" || style == "stateseparated") SetEnvironmentVariableW(L"STATE_SEPARATED_STORE", L"1");
        else SetEnvironmentVariableW(L"ONECORE_STORE", L"1");
    }
    const int storeCount = static_cast<int>(flat.size() / 20);

    std::vector<std::wstring> layouts = request.deviceLayoutPaths;
    std::vector<const wchar_t*> layoutPointers;
    layoutPointers.reserve(layouts.size());
    for (const std::wstring& path : layouts) layoutPointers.push_back(path.c_str());

    // 2) 暂存阶段：Initialize(InitForInput=1) → 铺盘信息 → PrepareUpdate。
    {
        void* context = createContext();
        if (context == nullptr)
        {
            FreeLibrary(module);
            error = "CreateUpdateContext 失败";
            return false;
        }
        // 参照 ImageApp：混合运行时会设 MIXED_RUNTIME_PROJECTION，UpdateDLL 才会建投影镜像。
        SetEnvironmentVariableW(L"IN_IMAGING", L"1");
    SetEnvironmentVariableW(L"WINDOWS_WCP_INSKUASSEMBLY", L"1");
    if (!request.storeArch.empty()) SetEnvironmentVariableW(L"STORE_ARCH", request.storeArch.c_str());
    if (!request.storeStyle.empty())
        {
            SetEnvironmentVariableW(L"MIXED_RUNTIME_PROJECTION", L"1");
        }
        // 参照 ImageApp：Initialize 之前不设布局（布局在 PrepareUpdate 阶段设），
        // 提前设会让 UpdateDLL 在 store 还没就绪时去找布局里的分区，返回 0x80070490。
        // UpdateDLL 在 Initialize 阶段就会去找设备布局（日志：Did not find any device layouts），
        // 所以布局必须在 Initialize 之前就设进去；PrepareUpdate 阶段再设一次也无害。
        if (!layoutPointers.empty()) setLayouts(context, layoutPointers.data(), static_cast<int>(layoutPointers.size()));
        OutLn("[apply] Initialize(InitForInput) ...");
        int rc = initialize(context, storeCount, flat.data(), request.actionListFile.c_str(),
                            request.packagesRoot.c_str(), request.updateInputFile.c_str(),
                            request.updateOutputFile.c_str(), request.stagingDirectory.c_str(),
                            request.logFile.empty() ? nullptr : request.logFile.c_str(), 1);
        if (rc < 0)
        {
            releaseContext(context);
            FreeLibrary(module);
            error = "Initialize(InitForInput) 失败（hr=" + HrText(rc) + "）";
            return false;
        }
        if (!request.updateHistoryFile.empty()) setHistoryPath(context, request.updateHistoryFile.c_str());
        for (std::size_t si = 0; si < storeIds.size(); ++si)
        {
            char hex[160] = {};
            int at = 0;
            for (std::size_t sj = 0; sj < storeIds[si].size() && at < 140; ++sj)
            {
                at += wsprintfA(hex + at, "%02x", storeIds[si][sj]);
            }
            report.push_back(std::string("storeId[") + std::to_string(si) + "]=" + hex);
        }
        for (const std::vector<unsigned char>& id : storeIds)
        {
            if (id.size() == 20) addSpace(context, id.data());
        }
        if (!request.overlapFile.empty() && setOverlapList != nullptr)
        {
            setOverlapList(context, request.overlapFile.c_str());
        }
        if (!layoutPointers.empty() && setLayouts != nullptr)
        {
            setLayouts(context, layoutPointers.data(), static_cast<int>(layoutPointers.size()));
        }
        if (!request.driveLetterConfigPath.empty())
        {
            setDriveLetterConfig(context, request.driveLetterConfigPath.c_str());
        }
        if (iuProgress != nullptr) iuProgress(1);
        rc = prepareUpdate(context, 2);
        if (deinitialize != nullptr) deinitialize(context);
        releaseContext(context);
        if (rc < 0)
        {
            FreeLibrary(module);
            error = "PrepareUpdate（暂存）失败（hr=" + HrText(rc) + "）";
            return false;
        }
        report.push_back("暂存完成（PrepareUpdate），store=" + std::to_string(storeCount));
    }

    if (request.stageOnly)
    {
        FreeLibrary(module);
        return true;
    }

    // 3) 提交阶段：Initialize(InitForOutput=2) → 布局/盘符 → ExecuteUpdate。
    {
        void* context = createContext();
        if (context == nullptr)
        {
            FreeLibrary(module);
            error = "CreateUpdateContext（提交阶段）失败";
            return false;
        }
        int rc = initialize(context, storeCount, flat.data(), request.actionListFile.c_str(),
                            request.packagesRoot.c_str(), request.updateInputFile.c_str(),
                            request.updateOutputFile.c_str(), request.stagingDirectory.c_str(),
                            request.logFile.empty() ? nullptr : request.logFile.c_str(), 2);
        if (rc < 0)
        {
            releaseContext(context);
            FreeLibrary(module);
            error = "Initialize(InitForOutput) 失败（hr=" + HrText(rc) + "）";
            return false;
        }
        if (!request.updateHistoryFile.empty()) setHistoryPath(context, request.updateHistoryFile.c_str());
        for (const std::vector<unsigned char>& id : storeIds)
        {
            if (id.size() == 20) addSpace(context, id.data());
        }
        if (!layoutPointers.empty() && setLayouts != nullptr)
        {
            setLayouts(context, layoutPointers.data(), static_cast<int>(layoutPointers.size()));
        }
        if (!request.driveLetterConfigPath.empty())
        {
            setDriveLetterConfig(context, request.driveLetterConfigPath.c_str());
        }
        if (iuProgress != nullptr) iuProgress(2);
        rc = executeUpdate(context, 0);
        if (deinitialize != nullptr) deinitialize(context);
        releaseContext(context);
        if (rc < 0)
        {
            FreeLibrary(module);
            error = "ExecuteUpdate（提交）失败（hr=" + HrText(rc) + "）";
            return false;
        }
        report.push_back("提交完成（ExecuteUpdate）");
    }

    FreeLibrary(module);
    return true;
}

namespace
{
typedef int(__stdcall* CreateActionListFn)(int deviceType, void* sessionData, const wchar_t* compDbFolder,
                                           const wchar_t* deviceInfoPath, int cchDest, wchar_t* actionListPath);
}  // namespace

bool UpdateCreateActionList(const std::wstring& compDbFolder, const std::wstring& deviceDbPath,
                            unsigned int deviceType, std::wstring& actionListPath, std::string& error)
{
    actionListPath.clear();
    if (compDbFolder.empty() || deviceDbPath.empty())
    {
        error = "compDB 目录或 DeviceDB 路径为空";
        return false;
    }

    std::wstring dllPath;
    if (!PoolFindAdkDll(L"UpdateAgent.dll", "UA_CreateActionList", dllPath, error))
    {
        return false;
    }

    HMODULE module = LoadLibraryExW(dllPath.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (module == nullptr)
    {
        error = "LoadLibraryEx(UpdateAgent.dll) 失败（" + Narrow(dllPath) + "）";
        return false;
    }

    CreateActionListFn createActionList =
        reinterpret_cast<CreateActionListFn>(GetProcAddress(module, "UA_CreateActionList"));
    if (createActionList == nullptr)
    {
        FreeLibrary(module);
        error = "UpdateAgent.dll 缺少导出 UA_CreateActionList（" + Narrow(dllPath) + "）";
        return false;
    }

    wchar_t buffer[1024] = {0};
    const int rc = createActionList(static_cast<int>(deviceType), nullptr, compDbFolder.c_str(),
                                    deviceDbPath.c_str(), static_cast<int>(sizeof(buffer) / sizeof(buffer[0])),
                                    buffer);
    FreeLibrary(module);
    if (rc != 0)
    {
        error = "UA_CreateActionList 失败（rc=" + std::to_string(rc) + "）";
        return false;
    }
    actionListPath = buffer;
    if (actionListPath.empty())
    {
        error = "UA_CreateActionList 返回成功但未给出 ActionList 路径";
        return false;
    }
    return true;
}

}  // namespace imaging
}  // namespace ocid
