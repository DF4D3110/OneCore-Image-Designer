// OcidImaging / Pool 实现：只做 DLL 探测（LoadLibrary + GetProcAddress）。
//
// 真建池需要 CreateStorageService（要日志回调 + STORE_ID 清单 + TI 权限），
// 那一步留到 pool create 里接；这里先把“ADK 带的这份 DLL 到底导出什么”摸清，
// 免得后面照着反编译签名盲猜。
#include "ocid/imaging/Pool.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <string>
#include <vector>

namespace ocid::imaging {
namespace {

const char* const kExpected[] = {
    // M1 造盘真正要用的子集（从 68 个导出里挑出来的）。
    "CreateStorageService",
    "CloseImageStorageService",
    "SetLoggingFunction",
    "CreateStoragePool",
    "GetStoragePool",
    "CloseStoragePool",
    "CreateStorageSpace",
    "AttachStorageSpace",
    "DetachStorageSpace",
    "DeleteStorageSpace",
    "FormatPartition",
    "AssignMountPoints",
    "AttachWOFToVolume",
    "DiskHasStoragePool",
    "GetPartitionPath",
    "GetPartitionNames",
    "GetDiskName",
    "GetDiskSize",
    "GetFreeBytesOnVolume",
    "CreateVirtualHardDisk",
    "AttachVirtualHardDisk",
    "DismountVirtualHardDisk",
};

bool FileExists(const std::string& path)
{
    const std::wstring wide(path.begin(), path.end());
    const DWORD attr = GetFileAttributesW(wide.c_str());
    return attr != INVALID_FILE_ATTRIBUTES && (attr & FILE_ATTRIBUTE_DIRECTORY) == 0;
}

} // namespace

bool PoolProbe(std::string& dllPath,
               std::vector<std::string>& found,
               std::vector<std::string>& missing,
               std::string& error)
{
    found.clear();
    missing.clear();
    dllPath.clear();

    // 候选顺序：环境变量 OCID_IMAGESTORAGE_DLL → exe 同级 → exe 同级 x86/ → 源码树旁。
    std::vector<std::string> candidates;
    const char* env = std::getenv("OCID_IMAGESTORAGE_DLL");
    if (env != nullptr && env[0] != 0) candidates.push_back(env);

    // OCID_ADK_ROOT (ADK root) or registry KitsRoot10 also supply the DLL.
    {
        const char* adkRoot = std::getenv("OCID_ADK_ROOT");
        if (adkRoot != nullptr && adkRoot[0] != 0)
        {
            const std::string base(adkRoot);
            candidates.push_back(base + "\\Windows Kits\\10\\Tools\\bin\\i386\\ImageStorageService.dll");
            candidates.push_back(base + "\\Tools\\bin\\i386\\ImageStorageService.dll");
        }
    }
    {
        wchar_t kits[MAX_PATH] = { 0 };
        DWORD size = static_cast<DWORD>(sizeof(kits));
        if (RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\WOW6432Node\\Microsoft\\Windows Kits\\Installed Roots",
                         L"KitsRoot10", RRF_RT_REG_SZ, nullptr, kits, &size) == ERROR_SUCCESS)
        {
            std::wstring root(kits);
            if (!root.empty() && root.back() != L'\\') root.push_back(L'\\');
            root += L"Tools\\bin\\i386\\ImageStorageService.dll";
            candidates.emplace_back(root.begin(), root.end());
        }
    }
    char module[MAX_PATH] = { 0 };
    if (GetModuleFileNameW(nullptr, reinterpret_cast<LPWSTR>(module), MAX_PATH) != 0)
    {
        const std::wstring exe(reinterpret_cast<const wchar_t*>(module));
        const std::size_t slash = exe.find_last_of(L"\\/");
        if (slash != std::wstring::npos)
        {
            const std::wstring dir = exe.substr(0, slash);
            const std::wstring names[] = { L"\\ImageStorageService.dll", L"\\imagestorageservice.dll" };
            for (const std::wstring& name : names)
            {
                const std::wstring full = dir + name;
                candidates.emplace_back(full.begin(), full.end());
            }
        }
    }

    {
        const wchar_t* roots[] = { L"ProgramFiles(x86)", L"ProgramFiles" };
        for (const wchar_t* rootName : roots)
        {
            wchar_t root[MAX_PATH] = { 0 };
            if (GetEnvironmentVariableW(rootName, root, MAX_PATH) == 0) continue;
            const std::wstring base(root);
            const wchar_t* rels[] = {
                L"\\Windows Kits\\10\\Tools\\bin\\i386\\ImageStorageService.dll",
                L"\\Windows Kits\\10\\Tools\\bin\\x86\\ImageStorageService.dll",
                L"\\Windows Kits\\10\\Tools\\bin\\desktop\\ImageStorageService.dll"
            };
            for (const wchar_t* rel : rels)
            {
                const std::wstring full = base + rel;
                candidates.emplace_back(full.begin(), full.end());
            }
        }
    }
    bool exists = false;
    for (const std::string& candidate : candidates)
    {
        if (FileExists(candidate)) { dllPath = candidate; exists = true; break; }
    }
    if (!exists)
    {
        error = "未找到 ImageStorageService.dll（可用 OCID_IMAGESTORAGE_DLL 指定路径；"
                "ADK 里它在 Windows Kits\\10\\Tools\\bin\\<arch>\\ 或 Imaging 目录）";
        return false;
    }

    const std::wstring wide(dllPath.begin(), dllPath.end());
    HMODULE moduleHandle = LoadLibraryW(wide.c_str());
    if (moduleHandle == nullptr)
    {
        error = "LoadLibraryW 失败（win32=" + std::to_string(GetLastError()) + "）：" + dllPath;
        return false;
    }
    for (const char* name : kExpected)
    {
        if (GetProcAddress(moduleHandle, name) != nullptr) found.emplace_back(name);
        else missing.emplace_back(name);
    }
    FreeLibrary(moduleHandle);
    return true;
}

bool PoolExports(std::string& dllPath, std::vector<std::string>& names, std::string& error)
{
    names.clear();
    if (!PoolProbe(dllPath, names, names, error)) return false;
    names.clear();
    const std::wstring wide(dllPath.begin(), dllPath.end());
    HMODULE moduleHandle = LoadLibraryW(wide.c_str());
    if (moduleHandle == nullptr)
    {
        error = "LoadLibraryW 失败（win32=" + std::to_string(GetLastError()) + "）：" + dllPath;
        return false;
    }
    const BYTE* base = reinterpret_cast<const BYTE*>(moduleHandle);
    const IMAGE_DOS_HEADER* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE)
    {
        error = "不是 PE 文件：" + dllPath;
        FreeLibrary(moduleHandle);
        return false;
    }
    const IMAGE_NT_HEADERS* nt = reinterpret_cast<const IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE)
    {
        error = "PE 签名不对：" + dllPath;
        FreeLibrary(moduleHandle);
        return false;
    }
    const IMAGE_DATA_DIRECTORY& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT];
    if (dir.VirtualAddress == 0 || dir.Size == 0)
    {
        error = "没有导出表：" + dllPath;
        FreeLibrary(moduleHandle);
        return false;
    }
    const IMAGE_EXPORT_DIRECTORY* exp = reinterpret_cast<const IMAGE_EXPORT_DIRECTORY*>(base + dir.VirtualAddress);
    const DWORD* nameRva = reinterpret_cast<const DWORD*>(base + exp->AddressOfNames);
    for (DWORD i = 0; i < exp->NumberOfNames; ++i)
    {
        const char* name = reinterpret_cast<const char*>(base + nameRva[i]);
        names.emplace_back(name);
    }
    FreeLibrary(moduleHandle);
    return true;
}

namespace {

std::string g_poolLog;

std::string WideToUtf8Local(const wchar_t* text)
{
    if (text == nullptr) return std::string();
    const int chars = WideCharToMultiByte(CP_UTF8, 0, text, -1, nullptr, 0, nullptr, nullptr);
    if (chars <= 1) return std::string();
    std::string out(static_cast<std::size_t>(chars - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text, -1, &out[0], chars, nullptr, nullptr);
    return out;
}

void __stdcall PoolLogCallback(const wchar_t* message)
{
    if (message == nullptr) return;
    g_poolLog += WideToUtf8Local(message);
    g_poolLog += "\n";
}

typedef long(__stdcall* CreateServiceFn)(void** service, void(__stdcall* log)(const wchar_t*), unsigned int count,
                                         const void* storeIds, int imaging);
typedef long(__stdcall* CloseServiceFn)(void* service);
typedef long(__stdcall* CreatePoolFn)(void* service, void* disk, const wchar_t* name, unsigned int version,
                                      const wchar_t* directory, void** pool);
typedef long(__stdcall* ClosePoolFn)(void* service, void* pool, int detachSpaces);
typedef long(__stdcall* CreateSpaceFn)(void* service, void* pool, const wchar_t* spaceName, unsigned long long size,
                                       unsigned long long extentSize, int thin, GUID* spaceId, GUID* reserveId,
                                       unsigned int usage);

} // namespace

namespace {
const char* eol8() { return "\n"; }
} // namespace

bool PoolBuild(const std::wstring& devicePath, const std::wstring& poolName, unsigned int version,
               const std::wstring& directory, const std::vector<PoolSpaceSpec>& spaces,
               std::vector<std::string>& created, std::string& error)
{
    created.clear();
    error.clear();
    g_poolLog.clear();
    if (devicePath.empty())
    {
        error = "未指定虚拟磁盘设备路径";
        return false;
    }
    std::string dllPath;
    std::vector<std::string> found;
    std::vector<std::string> missing;
    if (!PoolProbe(dllPath, found, missing, error)) return false;
    if (!missing.empty())
    {
        error = "ImageStorageService 缺少导出：" + missing[0];
        return false;
    }
    const std::wstring wideDll(dllPath.begin(), dllPath.end());
    HMODULE module = LoadLibraryW(wideDll.c_str());
    if (module == nullptr)
    {
        error = "LoadLibraryW 失败（win32=" + std::to_string(GetLastError()) + "）";
        return false;
    }
    CreateServiceFn createService = reinterpret_cast<CreateServiceFn>(GetProcAddress(module, "CreateStorageService"));
    CloseServiceFn closeService = reinterpret_cast<CloseServiceFn>(GetProcAddress(module, "CloseImageStorageService"));
    CreatePoolFn createPool = reinterpret_cast<CreatePoolFn>(GetProcAddress(module, "CreateStoragePool"));
    ClosePoolFn closePool = reinterpret_cast<ClosePoolFn>(GetProcAddress(module, "CloseStoragePool"));
    CreateSpaceFn createSpace = reinterpret_cast<CreateSpaceFn>(GetProcAddress(module, "CreateStorageSpace"));
    if (createService == nullptr || closeService == nullptr || createPool == nullptr || closePool == nullptr ||
        createSpace == nullptr)
    {
        FreeLibrary(module);
        error = "GetProcAddress 失败";
        return false;
    }
    const HANDLE disk = CreateFileW(devicePath.c_str(), GENERIC_READ | GENERIC_WRITE,
                                    FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr);
    if (disk == INVALID_HANDLE_VALUE)
    {
        const DWORD last = GetLastError();
        FreeLibrary(module);
        error = "打开虚拟磁盘失败（win32=" + std::to_string(last) + "）";
        return false;
    }
    void* service = nullptr;
    void* pool = nullptr;
    long rc = createService(&service, PoolLogCallback, 0, nullptr, 1);
    if (rc < 0 || service == nullptr)
    {
        error = "CreateStorageService 失败（hr=" + std::to_string(static_cast<long long>(rc)) + "）" + eol8() + g_poolLog;
        CloseHandle(disk);
        FreeLibrary(module);
        return false;
    }
    const std::wstring name = poolName.empty() ? std::wstring(L"OSPool") : poolName;
    rc = createPool(service, disk, name.c_str(), version, directory.c_str(), &pool);
    if (rc < 0 || pool == nullptr)
    {
        error = "CreateStoragePool 失败（hr=" + std::to_string(static_cast<long long>(rc)) + "）" + eol8() + g_poolLog;
        closeService(service);
        CloseHandle(disk);
        FreeLibrary(module);
        return false;
    }
    bool ok = true;
    std::string failures;
    for (const PoolSpaceSpec& spec : spaces)
    {
        if (spec.name.empty() || spec.sizeBytes == 0) continue;
        GUID spaceId = { 0, 0, 0, { 0, 0, 0, 0, 0, 0, 0, 0 } };
        GUID reserveId = { 0, 0, 0, { 0, 0, 0, 0, 0, 0, 0, 0 } };
        const long spaceRc = createSpace(service, pool, spec.name.c_str(), spec.sizeBytes, 1048576ull,
                                         spec.thin ? 1 : 0, &spaceId, &reserveId, spec.usage);
        if (spaceRc < 0)
        {
            ok = false;
            failures += "CreateStorageSpace(" + std::string(spec.name.begin(), spec.name.end()) + ") 失败（hr=" +
                        std::to_string(static_cast<long long>(spaceRc)) + "）" + eol8();
        }
        else
        {
            created.push_back(std::string(spec.name.begin(), spec.name.end()));
        }
    }
    const long rcClose = closePool(service, pool, 1);
    closeService(service);
    CloseHandle(disk);
    FreeLibrary(module);
    if (!ok)
    {
        error = failures + g_poolLog;
        return false;
    }
    if (rcClose < 0)
    {
        error = "CloseStoragePool 失败（hr=" + std::to_string(static_cast<long long>(rcClose)) + "）";
        return false;
    }
    return true;
}

bool PoolCreateSandbox(const PoolCreateOptions& options, std::string& detail, std::string& error)
{
    detail.clear();
    std::vector<PoolSpaceSpec> spaces;
    if (!options.spaceName.empty() && options.spaceSize != 0)
    {
        PoolSpaceSpec spec;
        spec.name = options.spaceName;
        spec.sizeBytes = options.spaceSize;
        spaces.push_back(spec);
    }
    std::vector<std::string> created;
    if (!PoolBuild(options.devicePath, options.poolName, options.version, options.directory, spaces, created, error))
    {
        detail = error;
        return false;
    }
    for (const std::string& name : created) detail += "space " + name + " 已创建" + eol8();
    return true;
}

} // namespace ocid::imaging
