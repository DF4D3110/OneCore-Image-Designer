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

    // 1) 挂盘、开池、逐 space 取 store id（随后交给 UpdateDLL）。
    std::vector<std::vector<unsigned char> > storeIds;
    std::vector<std::string> attachReport;
    if (!PoolAttachForUpdate(request.imagePath, request.poolName, request.poolVersion,
                             request.stagingDirectory, request.spaces, storeIds, attachReport, error))
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
        if (!request.storeStyle.empty())
        {
            SetEnvironmentVariableW(L"MIXED_RUNTIME_PROJECTION", L"1");
        }
        if (!layoutPointers.empty() && setLayouts != nullptr)
        {
            setLayouts(context, layoutPointers.data(), static_cast<int>(layoutPointers.size()));
        }
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

}  // namespace imaging
}  // namespace ocid
