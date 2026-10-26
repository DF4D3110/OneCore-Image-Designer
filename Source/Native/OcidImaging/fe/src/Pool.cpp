// OcidImaging / Pool 实现：只做 DLL 探测（LoadLibrary + GetProcAddress）。
//
// 真建池需要 CreateStorageService（要日志回调 + STORE_ID 清单 + TI 权限），
// 那一步留到 pool create 里接；这里先把“ADK 带的这份 DLL 到底导出什么”摸清，
// 免得后面照着反编译签名盲猜。
#include "ocid/imaging/Pool.h"
#include <cstring>
#include "ocid/imaging/Vhd.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <winioctl.h>

#include <string>
#include <algorithm>
#include <vector>

namespace ocid::imaging {
namespace {

// 阶段隔离：exe 启动时会把本阶段 ADK 根写进 OCID_ADK_ROOT。只要该变量存在，
// ADK DLL 候选就只允许来自这个根——绝不允许回落到注册表/Program Files 里的另一套 ADK
// （FE 与 RS5 的 imagestorageservice.dll 同名但导出完全不同，混用会让报错点漂移）。
void RestrictCandidatesToAdkRoot(std::vector<std::string>& candidates)
{
    const char* env = std::getenv("OCID_ADK_ROOT");
    if (env == nullptr || env[0] == 0) return;
    const std::string prefix(env);
    candidates.erase(std::remove_if(candidates.begin(), candidates.end(), [&prefix](const std::string& item) {
        return item.size() < prefix.size() || _stricmp(item.substr(0, prefix.size()).c_str(), prefix.c_str()) != 0;
    }), candidates.end());
}

void RestrictCandidatesToAdkRoot(std::vector<std::wstring>& candidates)
{
    const char* env = std::getenv("OCID_ADK_ROOT");
    if (env == nullptr || env[0] == 0) return;
    const int need = MultiByteToWideChar(CP_UTF8, 0, env, -1, nullptr, 0);
    if (need <= 0) return;
    std::wstring prefix(static_cast<std::size_t>(need - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, env, -1, &prefix[0], need);
    candidates.erase(std::remove_if(candidates.begin(), candidates.end(), [&prefix](const std::wstring& item) {
        return item.size() < prefix.size() || _wcsnicmp(item.c_str(), prefix.c_str(), prefix.size()) != 0;
    }), candidates.end());
}


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
            candidates.push_back(base + "\\bin\\i386\\ImageStorageService.dll");
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
    // Portable ADK installs may skip the registry; scan fixed drives one level deep.
    {
        const DWORD drives = GetLogicalDrives();
        for (int letter = 0; letter < 26; ++letter)
        {
            if ((drives & (1u << letter)) == 0) continue;
            wchar_t driveRoot[4] = { static_cast<wchar_t>(L'A' + letter), L':', L'\\', 0 };
            if (GetDriveTypeW(driveRoot) != DRIVE_FIXED) continue;
            std::wstring pattern = driveRoot;
            pattern += L"*";
            WIN32_FIND_DATAW data;
            HANDLE find = FindFirstFileW(pattern.c_str(), &data);
            if (find == INVALID_HANDLE_VALUE) continue;
            do
            {
                if ((data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0) continue;
                if (data.cFileName[0] == L'.' || data.cFileName[0] == L'$') continue;
                std::wstring base = driveRoot;
                base += data.cFileName;
                base += L"\\Windows Kits\\10\\Tools\\bin\\i386\\ImageStorageService.dll";
                if (FileExists(std::string(base.begin(), base.end()))) candidates.emplace_back(base.begin(), base.end());
            } while (FindNextFileW(find, &data));
            FindClose(find);
        }
    }
    std::string best;
    std::vector<std::string> bestFound;
    std::vector<std::string> bestMissing;
    std::size_t tried = 0;
    RestrictCandidatesToAdkRoot(candidates);
    for (const std::string& candidate : candidates)
    {
        if (!FileExists(candidate)) continue;
        ++tried;
        const std::wstring wide(candidate.begin(), candidate.end());
        HMODULE moduleHandle = LoadLibraryW(wide.c_str());
        if (moduleHandle == nullptr) continue;
        std::vector<std::string> hit;
        std::vector<std::string> miss;
        for (const char* name : kExpected)
        {
            if (GetProcAddress(moduleHandle, name) != nullptr) hit.emplace_back(name);
            else miss.emplace_back(name);
        }
        FreeLibrary(moduleHandle);
        if (miss.empty())
        {
            dllPath = candidate;
            found = hit;
            missing.clear();
            return true;
        }
        if (hit.size() > bestFound.size())
        {
            best = candidate;
            bestFound = hit;
            bestMissing = miss;
        }
    }
    if (best.empty())
    {
        error = "no usable ImageStorageService.dll among " + std::to_string(tried) +
                " candidate(s); set OCID_IMAGESTORAGE_DLL or OCID_ADK_ROOT";
        return false;
    }
    dllPath = best;
    found = bestFound;
    missing = bestMissing;
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

bool EqualsNoCaseLocal(const std::string& left, const std::string& right)
{
    if (left.size() != right.size()) return false;
    for (std::size_t i = 0; i < left.size(); ++i)
    {
        const unsigned char a = static_cast<unsigned char>(left[i]);
        const unsigned char b = static_cast<unsigned char>(right[i]);
        const unsigned char la = (a >= 'A' && a <= 'Z') ? static_cast<unsigned char>(a - 'A' + 'a') : a;
        const unsigned char lb = (b >= 'A' && b <= 'Z') ? static_cast<unsigned char>(b - 'A' + 'a') : b;
        if (la != lb) return false;
    }
    return true;
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


namespace {

// ImageStorageService uses the temp directory while staging spaces; if it does not
// exist AttachStorageSpace fails with a misleading 0x80070006 (ERROR_INVALID_HANDLE).
bool EnsurePoolTempDirectory(const std::wstring& dir, std::string& error)
{
    if (dir.empty()) return true;
    std::wstring current;
    for (std::size_t i = 0; i < dir.size(); ++i)
    {
        current.push_back(dir[i]);
        const bool boundary = (dir[i] == L'\\' || dir[i] == L'/');
        if (!boundary && i + 1 != dir.size()) continue;
        if (current.size() <= 3 && current.size() >= 2 && current[1] == L':') continue;
        if (::CreateDirectoryW(current.c_str(), nullptr) == 0)
        {
            const DWORD rc = ::GetLastError();
            if (rc != ERROR_ALREADY_EXISTS)
            {
                error = "cannot create temp directory (win32=" + std::to_string(rc) + ")";
                return false;
            }
        }
    }
    return true;
}

} // namespace

bool PoolBuild(const std::wstring& devicePath, const std::wstring& poolName, unsigned int version,
               const std::wstring& directory, const std::vector<PoolSpaceSpec>& spaces,
               std::vector<std::string>& created, std::string& error)
{
    created.clear();
    error.clear();
    g_poolLog.clear();
    {
        std::string dirError;
        if (!EnsurePoolTempDirectory(directory, dirError)) { error = dirError; return false; }
    }
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

namespace {

typedef long(__stdcall* OpenVhdFn)(void* service, const wchar_t* fileName, int readOnly,
                                   void* storeId, void** disk);
typedef long(__stdcall* GetDiskNameFn)(void* disk, wchar_t* name, unsigned int chars);
typedef long(__stdcall* OpenVhdFn)(void* service, const wchar_t* fileName, int readOnly,
                                   void* storeId, void** disk);
typedef long(__stdcall* GetDiskNameFn)(void* disk, wchar_t* name, unsigned int chars);
typedef long(__stdcall* OpenVhdFn)(void* service, const wchar_t* fileName, int readOnly,
                                   void* storeId, void** disk);
typedef long(__stdcall* GetDiskNameFn)(void* disk, wchar_t* name, unsigned int chars);
typedef long(__stdcall* OpenVhdFn)(void* service, const wchar_t* fileName, int readOnly,
                                   void* storeId, void** disk);
typedef long(__stdcall* GetDiskNameFn)(void* disk, wchar_t* name, unsigned int chars);
typedef long(__stdcall* OpenVhdFn)(void* service, const wchar_t* fileName, int readOnly,
                                   void* storeId, void** disk);
typedef long(__stdcall* GetDiskNameFn)(void* disk, wchar_t* name, unsigned int chars);
typedef long(__stdcall* OpenPoolFn)(void* service, const wchar_t* diskPath, const wchar_t* directory, int autoStage,
                                    void** pool);
typedef long(__stdcall* AttachSpaceFn)(void* service, void* pool, const wchar_t* spaceName, int temporary,
                                       void** disk);
typedef long(__stdcall* DetachSpaceFn)(void* service, void* pool, const wchar_t* spaceName, int discardChanges);
// STORE_ID（20 字节）在 x86 上按值压栈；这里用它给 space 的 GPT 磁盘 GUID 设成布局里的 <Id>。
struct StoreId20
{
    unsigned int type;
    unsigned char guid[16];
};
typedef long(__stdcall* SetDiskIdFn)(void* disk, StoreId20 storeId);

typedef long(__stdcall* FormatPartitionFn)(void* service, void* disk, const wchar_t* partitionName,
                                           const wchar_t* fileSystem, unsigned int clusterSize);

// 从 space 的存储句柄反查它暴露出来的设备名（\\.\PhysicalDriveN）。
// Storage Spaces 的分区表要写到 space 这块“磁盘”上，而写 GPT 的接口只认设备路径。
bool DevicePathFromHandle(HANDLE disk, std::wstring& path, std::string& error)
{
    STORAGE_DEVICE_NUMBER number = {};
    DWORD returned = 0;
    if (!DeviceIoControl(disk, IOCTL_STORAGE_GET_DEVICE_NUMBER, nullptr, 0, &number, sizeof(number), &returned,
                         nullptr))
    {
        error = "IOCTL_STORAGE_GET_DEVICE_NUMBER 失败（win32=" + std::to_string(GetLastError()) + "）";
        return false;
    }
    path = L"\\\\.\\PhysicalDrive" + std::to_wstring(number.DeviceNumber);
    return true;
}

// 设备路径（\\\\.\\PhysicalDriveN）与 VHD/VHDX 文件路径区分
bool IsDevicePathW(const std::wstring& path)
{
    return path.size() > 4 && path.compare(0, 4, L"\\\\.\\") == 0;
}

} // namespace


// 解析 "{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}" / "xxxxxxxx-xxxx-..." 形式的 GUID 文本，
// 按 GUID 的内存布局（Data1/2/3 小端）写进 16 字节。
static bool ParseGuidTextCpp(const std::wstring& text, unsigned char out[16])
{
    unsigned int d[32] = {0};
    int n = 0;
    for (std::size_t i = 0; i < text.size() && n < 32; ++i)
    {
        const wchar_t c = text[i];
        if (c >= L'0' && c <= L'9') d[n++] = static_cast<unsigned int>(c - L'0');
        else if (c >= L'a' && c <= L'f') d[n++] = static_cast<unsigned int>(c - L'a' + 10);
        else if (c >= L'A' && c <= L'F') d[n++] = static_cast<unsigned int>(c - L'A' + 10);
    }
    if (n != 32) return false;
    const unsigned int d1 = (d[0] << 28) | (d[1] << 24) | (d[2] << 20) | (d[3] << 16) |
                            (d[4] << 12) | (d[5] << 8) | (d[6] << 4) | d[7];
    const unsigned int d2 = (d[8] << 12) | (d[9] << 8) | (d[10] << 4) | d[11];
    const unsigned int d3 = (d[12] << 12) | (d[13] << 8) | (d[14] << 4) | d[15];
    out[0] = static_cast<unsigned char>(d1 & 0xFF);
    out[1] = static_cast<unsigned char>((d1 >> 8) & 0xFF);
    out[2] = static_cast<unsigned char>((d1 >> 16) & 0xFF);
    out[3] = static_cast<unsigned char>((d1 >> 24) & 0xFF);
    out[4] = static_cast<unsigned char>(d2 & 0xFF);
    out[5] = static_cast<unsigned char>((d2 >> 8) & 0xFF);
    out[6] = static_cast<unsigned char>(d3 & 0xFF);
    out[7] = static_cast<unsigned char>((d3 >> 8) & 0xFF);
    for (int i = 0; i < 8; ++i)
    {
        out[8 + i] = static_cast<unsigned char>(((d[16 + i * 2] << 4) | d[17 + i * 2]) & 0xFF);
    }
    return true;
}

bool PoolPrepareSpaces(const std::wstring& imagePath, const std::wstring& poolName, unsigned int version,
                       const std::wstring& directory, const std::vector<SpacePrepareSpec>& spaces,
                       bool formatPartitions, std::vector<std::string>& report, std::string& error)
{
    (void)version;
    report.clear();
    error.clear();
    g_poolLog.clear();
    {
        std::string dirError;
        if (!EnsurePoolTempDirectory(directory, dirError)) { error = dirError; return false; }
    }
    if (imagePath.empty())
    {
        error = "未指定虚拟磁盘（VHD/VHDX）路径";
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
        error = "LoadLibraryW 失败（win32=" + std::to_string(GetLastError()) + "）。";
        return false;
    }
    CreateServiceFn createService = reinterpret_cast<CreateServiceFn>(GetProcAddress(module, "CreateStorageService"));
    CloseServiceFn closeService = reinterpret_cast<CloseServiceFn>(GetProcAddress(module, "CloseImageStorageService"));
    OpenVhdFn openVhd = reinterpret_cast<OpenVhdFn>(GetProcAddress(module, "OpenVirtualHardDisk"));
    GetDiskNameFn getDiskName = reinterpret_cast<GetDiskNameFn>(GetProcAddress(module, "GetDiskName"));
    OpenPoolFn openPool = reinterpret_cast<OpenPoolFn>(GetProcAddress(module, "OpenStoragePool"));
    ClosePoolFn closePool = reinterpret_cast<ClosePoolFn>(GetProcAddress(module, "CloseStoragePool"));
    AttachSpaceFn attachSpace = reinterpret_cast<AttachSpaceFn>(GetProcAddress(module, "AttachStorageSpace"));
    DetachSpaceFn detachSpace = reinterpret_cast<DetachSpaceFn>(GetProcAddress(module, "DetachStorageSpace"));
    FormatPartitionFn formatPartition = reinterpret_cast<FormatPartitionFn>(GetProcAddress(module, "FormatPartition"));
    SetDiskIdFn setDiskId = reinterpret_cast<SetDiskIdFn>(GetProcAddress(module, "SetDiskId"));
    if (createService == nullptr || closeService == nullptr || openVhd == nullptr || getDiskName == nullptr ||
        openPool == nullptr || closePool == nullptr || attachSpace == nullptr || detachSpace == nullptr ||
        formatPartition == nullptr)
    {
        FreeLibrary(module);
        error = "GetProcAddress 失败（OpenVirtualHardDisk/GetDiskName/OpenStoragePool/FormatPartition）。";
        return false;
    }

    void* service = nullptr;
    long rc = createService(&service, PoolLogCallback, 0, nullptr, 1);
    if (rc < 0 || service == nullptr)
    {
        FreeLibrary(module);
        error = "CreateStorageService 失败（hr=" + std::to_string(static_cast<long long>(rc)) + "）。" + eol8() + g_poolLog;
        return false;
    }

    // 由服务自己打开虚拟磁盘再向它要磁盘名；直接把 \\.\PhysicalDriveN 传进去会被拒（0x8007007b = ERROR_INVALID_NAME）。
    unsigned char storeId[32] = {};
    void* baseDisk = nullptr;
    wchar_t diskName[512] = {};
    if (IsDevicePathW(imagePath))
    {
        baseDisk = CreateFileW(imagePath.c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                               nullptr, OPEN_EXISTING, 0, nullptr);
        if (baseDisk == INVALID_HANDLE_VALUE)
        {
            baseDisk = nullptr;
            closeService(service);
            FreeLibrary(module);
            error = "OpenDevice failed (win32=" + std::to_string(GetLastError()) + ")";
            return false;
        }
        wcsncpy_s(diskName, 512, imagePath.c_str(), 511);
    }
    else
    {
        rc = openVhd(service, imagePath.c_str(), 0, storeId, &baseDisk);
        if (rc < 0 || baseDisk == nullptr)
        {
            closeService(service);
            FreeLibrary(module);
            error = "OpenVirtualHardDisk 失败（hr=" + std::to_string(static_cast<long long>(rc)) + "）。" + eol8() + g_poolLog;
            return false;
        }
        rc = getDiskName(baseDisk, diskName, 512);
        if (rc < 0 || diskName[0] == 0)
        {
            CloseHandle(baseDisk);
            closeService(service);
            FreeLibrary(module);
            error = "GetDiskName 失败（hr=" + std::to_string(static_cast<long long>(rc)) + "）。" + eol8() + g_poolLog;
            return false;
        }
    }
    std::string baseDiskName;
    for (std::size_t k = 0; diskName[k] != 0; ++k) baseDiskName.push_back(static_cast<char>(diskName[k]));

    void* pool = nullptr;
    rc = openPool(service, diskName, directory.c_str(), 1, &pool);
    if (rc < 0 || pool == nullptr)
    {
        CloseHandle(baseDisk);
        closeService(service);
        FreeLibrary(module);
        error = "OpenStoragePool 失败（hr=" + std::to_string(static_cast<long long>(rc)) + "，disk=" + baseDiskName +
                "）。" + eol8() + g_poolLog;
        return false;
    }
    report.push_back("池已打开：" + baseDiskName);

    bool ok = true;
    std::string failures;
    for (const SpacePrepareSpec& spec : spaces)
    {
        void* spaceDisk = nullptr;
        const long attachRc = attachSpace(service, pool, spec.name.c_str(), 0, &spaceDisk);
        if (attachRc < 0 || spaceDisk == nullptr)
        {
            ok = false;
            failures += "space " + std::string(spec.name.begin(), spec.name.end()) + " 挂载失败（hr=" +
                        std::to_string(static_cast<long long>(attachRc)) + "）；" + eol8() + g_poolLog;
            continue;
        }
        wchar_t spaceName[512] = {};
        const long nameRc = getDiskName(spaceDisk, spaceName, 512);
        std::string device;
        for (std::size_t k = 0; nameRc >= 0 && spaceName[k] != 0; ++k) device.push_back(static_cast<char>(spaceName[k]));
        if (device.empty())
        {
            ok = false;
            failures += "space " + std::string(spec.name.begin(), spec.name.end()) + " 取磁盘名失败；";
            detachSpace(service, pool, spec.name.c_str(), 0);
            CloseHandle(spaceDisk);
            continue;
        }
        std::vector<VhdPartition> parts;
        for (const SpacePartitionSpec& p : spec.partitions)
        {
            VhdPartition vp;
            vp.name = std::string(p.name.begin(), p.name.end());
            vp.type = std::string(p.typeGuid.begin(), p.typeGuid.end());
            vp.id = std::string(p.partitionGuid.begin(), p.partitionGuid.end());
            vp.offsetBytes = p.offsetBytes;
            vp.sizeBytes = p.sizeBytes;
            vp.attributes = 0;
            parts.push_back(vp);
        }
        std::string gptError;
        if (!VhdWriteGpt(device, std::string(), parts, spec.sectorSize == 0 ? 512u : spec.sectorSize, gptError))
        {
            ok = false;
            failures += "space " + std::string(spec.name.begin(), spec.name.end()) + " 写 GPT 失败（" + gptError + "）；";
        }
        else
        {
            report.push_back("space " + std::string(spec.name.begin(), spec.name.end()) + "：" + device + " 已写 GPT（" +
                             std::to_string(parts.size()) + " 个分区）");
            // store id = GPT 磁盘 GUID。ImageApp 把 MainOS 的磁盘 GUID 固定成 MAINOS_STORE_GUID
            // （AE420040-13DD-41F2-AE7F-0DC35854C8D7），其余 store 用布局 <Id> 的 GUID；
            // UpdateDLL 就是按这个 GUID 找 store 的，缺失会直接 E_UNEXPECTED/NOT_FOUND。
            if (spec.diskId.empty() || setDiskId == nullptr)
            {
                report.push_back(std::string("  (diskId 未设置：") +
                                 (spec.diskId.empty() ? "布局无 Id" : "DLL 无 SetDiskId 导出") + "）");
            }
            if (!spec.diskId.empty() && setDiskId != nullptr)
            {
                unsigned char diskGuid[16] = {0};
                if (ParseGuidTextCpp(spec.diskId, diskGuid))
                {
                    StoreId20 sid{};
                    sid.type = 1; // ImageConstants.PartitionTypeGpt
                    std::memcpy(sid.guid, diskGuid, 16);
                    const long idRc = setDiskId(spaceDisk, sid);
                    if (idRc < 0)
                    {
                        ok = false;
                        failures += "space " + std::string(spec.name.begin(), spec.name.end()) +
                                    " SetDiskId 失败（hr=" + std::to_string(static_cast<long long>(idRc)) + "）；";
                    }
                    else
                    {
                        report.push_back("  diskId = " + std::string(spec.diskId.begin(), spec.diskId.end()));
                    }
                }
            }
            if (formatPartitions)
            {
                for (const SpacePartitionSpec& p : spec.partitions)
                {
                    if (p.fileSystem.empty()) continue;
                    const long fmtRc = formatPartition(service, spaceDisk, p.name.c_str(), p.fileSystem.c_str(),
                                                       p.clusterSize);
                    if (fmtRc < 0)
                    {
                        ok = false;
                        failures += "space " + std::string(spec.name.begin(), spec.name.end()) + " 的分区 " +
                                    std::string(p.name.begin(), p.name.end()) + " 格式化失败（hr=" +
                                    std::to_string(static_cast<long long>(fmtRc)) + "）；";
                        break;
                    }
                    report.push_back("  已格式化 " + std::string(p.name.begin(), p.name.end()) + "（" +
                                     std::string(p.fileSystem.begin(), p.fileSystem.end()) + "）");
                }
            }
        }
        detachSpace(service, pool, spec.name.c_str(), 0);
        CloseHandle(spaceDisk);
    }

    closePool(service, pool, 1);
    CloseHandle(baseDisk);
    closeService(service);
    FreeLibrary(module);
    if (!failures.empty()) error = failures;
    return ok;
}







bool PoolClose(const std::wstring& imagePath, const std::wstring& directory, bool detachSpaces,
               std::vector<std::string>& report, std::string& error)
{
    report.clear();
    error.clear();
    g_poolLog.clear();
    if (imagePath.empty())
    {
        error = "未指定虚拟磁盘（VHD/VHDX）路径";
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
        error = "LoadLibraryW 失败（win32=" + std::to_string(GetLastError()) + "）。";
        return false;
    }
    CreateServiceFn createService = reinterpret_cast<CreateServiceFn>(GetProcAddress(module, "CreateStorageService"));
    CloseServiceFn closeService = reinterpret_cast<CloseServiceFn>(GetProcAddress(module, "CloseImageStorageService"));
    OpenVhdFn openVhd = reinterpret_cast<OpenVhdFn>(GetProcAddress(module, "OpenVirtualHardDisk"));
    GetDiskNameFn getDiskName = reinterpret_cast<GetDiskNameFn>(GetProcAddress(module, "GetDiskName"));
    OpenPoolFn openPool = reinterpret_cast<OpenPoolFn>(GetProcAddress(module, "OpenStoragePool"));
    ClosePoolFn closePool = reinterpret_cast<ClosePoolFn>(GetProcAddress(module, "CloseStoragePool"));
    if (createService == nullptr || closeService == nullptr || openVhd == nullptr || getDiskName == nullptr ||
        openPool == nullptr || closePool == nullptr)
    {
        FreeLibrary(module);
        error = "GetProcAddress 失败（OpenVirtualHardDisk/GetDiskName/OpenStoragePool/CloseStoragePool）。";
        return false;
    }
    void* service = nullptr;
    long rc = createService(&service, PoolLogCallback, 0, nullptr, 1);
    if (rc < 0 || service == nullptr)
    {
        FreeLibrary(module);
        error = "CreateStorageService 失败（hr=" + std::to_string(static_cast<long long>(rc)) + "）。";
        return false;
    }
    unsigned char storeId[32] = {};
    void* baseDisk = nullptr;
    rc = openVhd(service, imagePath.c_str(), 0, storeId, &baseDisk);
    if (rc < 0 || baseDisk == nullptr)
    {
        closeService(service);
        FreeLibrary(module);
        error = "OpenVirtualHardDisk 失败（hr=" + std::to_string(static_cast<long long>(rc)) + "）。";
        return false;
    }
    wchar_t diskName[512] = {};
    rc = getDiskName(baseDisk, diskName, 512);
    if (rc < 0 || diskName[0] == 0)
    {
        CloseHandle(baseDisk);
        closeService(service);
        FreeLibrary(module);
        error = "GetDiskName 失败（hr=" + std::to_string(static_cast<long long>(rc)) + "）。";
        return false;
    }
    std::string baseDiskName;
    for (std::size_t k = 0; diskName[k] != 0; ++k) baseDiskName.push_back(static_cast<char>(diskName[k]));
    void* pool = nullptr;
    rc = openPool(service, diskName, directory.c_str(), 1, &pool);
    if (rc < 0 || pool == nullptr)
    {
        CloseHandle(baseDisk);
        closeService(service);
        FreeLibrary(module);
        error = "OpenStoragePool 失败（hr=" + std::to_string(static_cast<long long>(rc)) + "，disk=" + baseDiskName + "）。";
        return false;
    }
    report.push_back("池已打开：" + baseDiskName);
    const long closeRc = closePool(service, pool, detachSpaces ? 1 : 0);
    CloseHandle(baseDisk);
    closeService(service);
    FreeLibrary(module);
    if (closeRc < 0)
    {
        error = "CloseStoragePool 失败（hr=" + std::to_string(static_cast<long long>(closeRc)) + "）。";
        return false;
    }
    report.push_back(detachSpaces ? "池已关闭，space 已卸下" : "池已关闭（space 保留）");
    return true;
}

namespace {

typedef long(__stdcall* GetStoreIdFn)(void* service, void* disk, unsigned char* storeId);
typedef long(__stdcall* AttachWofFn)(void* service, void* disk, const wchar_t* partitionName);
typedef void(__cdecl* WriteMountManagerFn)(void* service, const unsigned char* storeId, int useWellKnownGuids,
                                           const unsigned char* storeIds, unsigned int storeIdsCount,
                                           const wchar_t* driveLetterConfigPath, int ramboot);

std::wstring LocateAdkDll(const wchar_t* fileName, const char* requiredExport)
{
    std::vector<std::wstring> candidates;
    if (const char* env = std::getenv("OCID_UPDATEDLL"))
    {
        const std::string text(env);
        candidates.emplace_back(text.begin(), text.end());
    }
    if (const char* adkRoot = std::getenv("OCID_ADK_ROOT"))
    {
        const std::string base(adkRoot);
        candidates.emplace_back(std::wstring(base.begin(), base.end()) + L"\\Windows Kits\\10\\Tools\\bin\\i386\\" + fileName);
        candidates.emplace_back(std::wstring(base.begin(), base.end()) + L"\\Tools\\bin\\i386\\" + fileName);
        candidates.emplace_back(std::wstring(base.begin(), base.end()) + L"\\bin\\i386\\" + fileName);
    }
    {
        wchar_t kits[MAX_PATH] = { 0 };
        DWORD size = static_cast<DWORD>(sizeof(kits));
        if (RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\WOW6432Node\\Microsoft\\Windows Kits\\Installed Roots",
                         L"KitsRoot10", RRF_RT_REG_SZ, nullptr, kits, &size) == ERROR_SUCCESS)
        {
            std::wstring kitRoot(kits);
            if (!kitRoot.empty() && kitRoot.back() != L'\\') kitRoot.push_back(L'\\');
            candidates.push_back(kitRoot + L"Tools\\bin\\i386\\" + fileName);
        }
    }
    {
        wchar_t module[MAX_PATH] = { 0 };
        if (GetModuleFileNameW(nullptr, module, MAX_PATH) != 0)
        {
            const std::wstring exe(module);
            const std::size_t slash = exe.find_last_of(L"\\/");
            if (slash != std::wstring::npos) candidates.push_back(exe.substr(0, slash + 1) + fileName);
        }
    }
    {
        const wchar_t* roots[] = { L"ProgramFiles(x86)", L"ProgramFiles" };
        for (const wchar_t* rootName : roots)
        {
            wchar_t envRoot[MAX_PATH] = { 0 };
            if (GetEnvironmentVariableW(rootName, envRoot, MAX_PATH) == 0) continue;
            candidates.push_back(std::wstring(envRoot) + L"\\Windows Kits\\10\\Tools\\bin\\i386\\" + fileName);
        }
    }
    {
        wchar_t drives[512] = { 0 };
        if (GetLogicalDriveStringsW(511, drives) != 0)
        {
            const wchar_t* cursor = drives;
            while (*cursor != 0)
            {
                const std::wstring driveRoot(cursor);
                cursor += driveRoot.size() + 1;
                const UINT type = GetDriveTypeW(driveRoot.c_str());
                if (type != DRIVE_FIXED && type != DRIVE_REMOVABLE) continue;
                const std::wstring pattern = driveRoot + L"*";
                WIN32_FIND_DATAW data{};
                HANDLE find = FindFirstFileW(pattern.c_str(), &data);
                if (find == INVALID_HANDLE_VALUE) continue;
                do
                {
                    if ((data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0) continue;
                    if (data.cFileName[0] == L'.') continue;
                    const std::wstring candidate = driveRoot + data.cFileName + L"\\Windows Kits\\10\\Tools\\bin\\i386\\" + fileName;
                    if (GetFileAttributesW(candidate.c_str()) != INVALID_FILE_ATTRIBUTES) candidates.push_back(candidate);
                } while (FindNextFileW(find, &data));
                FindClose(find);
            }
        }
    }
    std::wstring fallback;
    const std::wstring wideName(fileName);
    RestrictCandidatesToAdkRoot(candidates);
    for (const std::wstring& candidate : candidates)
    {
        if (GetFileAttributesW(candidate.c_str()) == INVALID_FILE_ATTRIBUTES) continue;
        if (fallback.empty()) fallback = candidate;
        // Prefer a copy that really exports what we need: an older kit on the same machine may
        // ship an UpdateDLL.dll without IU_WriteMountManagerRegistry.
        HMODULE probe = LoadLibraryExW(candidate.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (probe != nullptr)
        {
            const bool hasEntry = GetProcAddress(probe, requiredExport) != nullptr;
            if (std::getenv("OCID_DEBUG_ADK") != nullptr)
            {
                std::fprintf(stderr, "[adk] load=%p has=%d %s\n", (void*)probe, hasEntry ? 1 : 0,
                             std::string(candidate.begin(), candidate.end()).c_str());
            }
            FreeLibrary(probe);
            if (hasEntry) return candidate;
        }
        else if (std::getenv("OCID_DEBUG_ADK") != nullptr)
        {
            std::fprintf(stderr, "[adk] load failed err=%lu %s\n", GetLastError(),
                         std::string(candidate.begin(), candidate.end()).c_str());
        }
    }
    // Never hand back a copy that misses the required export: otherwise the caller
    // fails later with a puzzling "missing export" message from another library.
    if (requiredExport != nullptr && *requiredExport != 0) return std::wstring();
    return fallback;
}

} // namespace

bool PoolFinalize(const std::wstring& imagePath, const std::wstring& poolName, unsigned int version,
                  const std::wstring& directory, const std::vector<SpacePrepareSpec>& spaces,
                  const std::wstring& driveLetterConfigPath, bool attachWof,
                  std::vector<std::string>& report, std::string& error)
{
    (void)poolName;
    (void)version;
    report.clear();
    error.clear();
    g_poolLog.clear();
    {
        std::string dirError;
        if (!EnsurePoolTempDirectory(directory, dirError)) { error = dirError; return false; }
    }
    if (imagePath.empty())
    {
        error = "no virtual disk path";
        return false;
    }

    std::string dllPath;
    std::vector<std::string> found;
    std::vector<std::string> missing;
    if (!PoolProbe(dllPath, found, missing, error)) return false;
    const std::wstring wideDll(dllPath.begin(), dllPath.end());
    HMODULE imageStorage = LoadLibraryW(wideDll.c_str());
    if (imageStorage == nullptr)
    {
        error = "LoadLibraryW(ImageStorageService) failed (win32=" + std::to_string(GetLastError()) + ").";
        return false;
    }
    const std::wstring updatePath = LocateAdkDll(L"UpdateDLL.dll", "IU_WriteMountManagerRegistry");
    if (updatePath.empty())
    {
        FreeLibrary(imageStorage);
        error = "UpdateDLL.dll not found (set OCID_ADK_ROOT or OCID_UPDATEDLL).";
        return false;
    }
    HMODULE updateDll = LoadLibraryW(updatePath.c_str());
    if (updateDll == nullptr)
    {
        FreeLibrary(imageStorage);
        error = "LoadLibraryW(UpdateDLL) failed (win32=" + std::to_string(GetLastError()) + ").";
        return false;
    }

    CreateServiceFn createService = reinterpret_cast<CreateServiceFn>(GetProcAddress(imageStorage, "CreateStorageService"));
    CloseServiceFn closeService = reinterpret_cast<CloseServiceFn>(GetProcAddress(imageStorage, "CloseImageStorageService"));
    OpenVhdFn openVhd = reinterpret_cast<OpenVhdFn>(GetProcAddress(imageStorage, "OpenVirtualHardDisk"));
    GetDiskNameFn getDiskName = reinterpret_cast<GetDiskNameFn>(GetProcAddress(imageStorage, "GetDiskName"));
    OpenPoolFn openPool = reinterpret_cast<OpenPoolFn>(GetProcAddress(imageStorage, "OpenStoragePool"));
    ClosePoolFn closePool = reinterpret_cast<ClosePoolFn>(GetProcAddress(imageStorage, "CloseStoragePool"));
    AttachSpaceFn attachSpace = reinterpret_cast<AttachSpaceFn>(GetProcAddress(imageStorage, "AttachStorageSpace"));
    DetachSpaceFn detachSpace = reinterpret_cast<DetachSpaceFn>(GetProcAddress(imageStorage, "DetachStorageSpace"));
    GetStoreIdFn getStoreId = reinterpret_cast<GetStoreIdFn>(GetProcAddress(imageStorage, "GetStoreId"));
    AttachWofFn attachWofFn = reinterpret_cast<AttachWofFn>(GetProcAddress(imageStorage, "AttachWOFToVolume"));
    WriteMountManagerFn writeMountManager = reinterpret_cast<WriteMountManagerFn>(GetProcAddress(updateDll, "IU_WriteMountManagerRegistry"));
    if (createService == nullptr || closeService == nullptr || openVhd == nullptr || getDiskName == nullptr ||
        openPool == nullptr || closePool == nullptr || attachSpace == nullptr || detachSpace == nullptr ||
        getStoreId == nullptr || attachWofFn == nullptr || writeMountManager == nullptr)
    {
        FreeLibrary(updateDll);
        FreeLibrary(imageStorage);
        std::string detail = "required export missing:";
        if (getStoreId == nullptr) detail += " GetStoreId";
        if (attachWofFn == nullptr) detail += " AttachWOFToVolume";
        if (writeMountManager == nullptr) detail += " IU_WriteMountManagerRegistry";
        detail += " (UpdateDLL=" + std::string(updatePath.begin(), updatePath.end()) + ")";
        error = detail;
        return false;
    }

    void* service = nullptr;
    void* pool = nullptr;
    void* baseDisk = nullptr;
    bool ok = false;
    long rc = createService(&service, PoolLogCallback, 0, nullptr, 1);
    if (rc < 0 || service == nullptr)
    {
        FreeLibrary(updateDll);
        FreeLibrary(imageStorage);
        error = "CreateStorageService failed (hr=" + std::to_string(static_cast<long long>(rc)) + ")." + eol8() + g_poolLog;
        return false;
    }

    do
    {
        unsigned char baseStore[20] = { 0 };
        wchar_t diskName[512] = { 0 };
        if (IsDevicePathW(imagePath))
        {
            HANDLE rawDisk = CreateFileW(imagePath.c_str(), GENERIC_READ | GENERIC_WRITE,
                                         FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr);
            if (rawDisk == INVALID_HANDLE_VALUE) { error = "OpenDevice failed (win32=" + std::to_string(GetLastError()) + ")."; break; }
            rc = getStoreId(service, rawDisk, baseStore);
            CloseHandle(rawDisk);
            if (rc < 0) { error = "GetStoreId failed (hr=" + std::to_string(static_cast<long long>(rc)) + ")." + eol8() + g_poolLog; break; }
            wcsncpy_s(diskName, 512, imagePath.c_str(), 511);
        }
        else
        {
            rc = openVhd(service, imagePath.c_str(), 0, baseStore, &baseDisk);
            if (rc < 0) { error = "OpenVirtualHardDisk failed (hr=" + std::to_string(static_cast<long long>(rc)) + ")." + eol8() + g_poolLog; break; }
            rc = getDiskName(baseDisk, diskName, 511);
            if (rc < 0) { error = "GetDiskName failed (hr=" + std::to_string(static_cast<long long>(rc)) + ")." + eol8() + g_poolLog; break; }
        }

        rc = openPool(service, diskName, directory.c_str(), 1, &pool);
        if (rc < 0) { error = "OpenStoragePool failed (hr=" + std::to_string(static_cast<long long>(rc)) + ")." + eol8() + g_poolLog; break; }

        std::vector<unsigned char> storeIds;
        for (const SpacePrepareSpec& space : spaces)
        {
            void* spaceDisk = nullptr;
            const long attachRc = attachSpace(service, pool, space.name.c_str(), 0, &spaceDisk);
            if (attachRc < 0)
            {
                error = "AttachStorageSpace failed (hr=" + std::to_string(static_cast<long long>(attachRc)) + ")." + eol8() + g_poolLog;
                break;
            }
            unsigned char storeId[20] = { 0 };
            const long idRc = getStoreId(service, spaceDisk, storeId);
            if (idRc < 0)
            {
                detachSpace(service, pool, space.name.c_str(), 0);
                error = "GetStoreId failed (hr=" + std::to_string(static_cast<long long>(idRc)) + ").";
                break;
            }
            storeIds.insert(storeIds.end(), storeId, storeId + 20);
            unsigned int wofCount = 0;
            unsigned int wofFailed = 0;
            if (attachWof)
            {
                for (const SpacePartitionSpec& part : space.partitions)
                {
                    const long wofRc = attachWofFn(service, spaceDisk, part.name.c_str());
                    if (wofRc < 0) ++wofFailed; else ++wofCount;
                }
            }
            detachSpace(service, pool, space.name.c_str(), 0);
            report.push_back("space " + WideToUtf8Local(space.name.c_str()) + ": storeId captured, WOF attached=" + std::to_string(wofCount) +
                             (wofFailed != 0 ? (", WOF skipped=" + std::to_string(wofFailed)) : std::string()));
        }
        if (!error.empty()) break;

        if (!driveLetterConfigPath.empty() && !storeIds.empty())
        {
            // One call for the whole image: the first argument is the image's own store id,
            // the array carries every space's store id (matches ImageStorage.WriteMountManagerRegistry).
            const unsigned int count = static_cast<unsigned int>(storeIds.size() / 20);
            writeMountManager(service, baseStore, 1, storeIds.data(), count, driveLetterConfigPath.c_str(), 0);
            report.push_back("MountManager registry entries written: base + " + std::to_string(count) + " space store(s)");
        }
        else
        {
            report.push_back("MountManager registry skipped (no drive letter config)");
        }
        ok = true;
    } while (false);

    if (pool != nullptr) closePool(service, pool, 0);
    closeService(service);
    FreeLibrary(updateDll);
    FreeLibrary(imageStorage);
    return ok;
}

bool PoolFindAdkDll(const std::wstring& fileName, const std::string& requiredExport,
                    std::wstring& outPath, std::string& error)
{
    outPath.clear();
    error.clear();
    const std::string narrow(fileName.begin(), fileName.end());
    if (narrow.find("UpdateDLL") != std::string::npos)
    {
        const std::wstring found = LocateAdkDll(fileName.c_str(), requiredExport.c_str());
        if (found.empty())
        {
            error = "not found: " + narrow + " (need export " + requiredExport + ")";
            return false;
        }
        outPath = found;
    return true;
    }
    std::vector<std::wstring> candidates;
    if (const char* adkRoot = std::getenv("OCID_ADK_ROOT"))
    {
        const std::string base(adkRoot);
        candidates.push_back(std::wstring(base.begin(), base.end()) + L"\\Windows Kits\\10\\Tools\\bin\\i386\\" + fileName);
        candidates.push_back(std::wstring(base.begin(), base.end()) + L"\\Tools\\bin\\i386\\" + fileName);
        candidates.push_back(std::wstring(base.begin(), base.end()) + L"\\bin\\i386\\" + fileName);
    }
    wchar_t kits[MAX_PATH] = { 0 };
    DWORD size = static_cast<DWORD>(sizeof(kits));
    if (RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\WOW6432Node\\Microsoft\\Windows Kits\\Installed Roots",
                     L"KitsRoot10", RRF_RT_REG_SZ, nullptr, kits, &size) == ERROR_SUCCESS)
    {
        std::wstring kitRoot(kits);
        if (!kitRoot.empty() && kitRoot.back() != L'\\') kitRoot.push_back(L'\\');
        candidates.push_back(kitRoot + L"Tools\\bin\\i386\\" + fileName);
    }
    wchar_t module[MAX_PATH] = { 0 };
    if (GetModuleFileNameW(nullptr, module, MAX_PATH) != 0)
    {
        const std::wstring exe(module);
        const std::size_t slash = exe.find_last_of(L"\\/");
        if (slash != std::wstring::npos) candidates.push_back(exe.substr(0, slash + 1) + fileName);
    }
    const wchar_t* roots[] = { L"ProgramFiles(x86)", L"ProgramFiles" };
    for (const wchar_t* rootName : roots)
    {
        wchar_t envRoot[MAX_PATH] = { 0 };
        if (GetEnvironmentVariableW(rootName, envRoot, MAX_PATH) == 0) continue;
        candidates.push_back(std::wstring(envRoot) + L"\\Windows Kits\\10\\Tools\\bin\\i386\\" + fileName);
    }
    wchar_t drives[512] = { 0 };
    if (GetLogicalDriveStringsW(511, drives) != 0)
    {
        const wchar_t* cursor = drives;
        while (*cursor != 0)
        {
            const std::wstring driveRoot(cursor);
            cursor += driveRoot.size() + 1;
            if (GetDriveTypeW(driveRoot.c_str()) != DRIVE_FIXED) continue;
            const std::wstring pattern = driveRoot + L"*";
            WIN32_FIND_DATAW data{};
            HANDLE find = FindFirstFileW(pattern.c_str(), &data);
            if (find == INVALID_HANDLE_VALUE) continue;
            do
            {
                if ((data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0) continue;
                if (data.cFileName[0] == L'.') continue;
                candidates.push_back(driveRoot + data.cFileName + L"\\Windows Kits\\10\\Tools\\bin\\i386\\" + fileName);
            } while (FindNextFileW(find, &data));
            FindClose(find);
        }
    }
    std::wstring fallback;
    RestrictCandidatesToAdkRoot(candidates);
    for (const std::wstring& candidate : candidates)
    {
        if (GetFileAttributesW(candidate.c_str()) == INVALID_FILE_ATTRIBUTES) continue;
        if (fallback.empty()) fallback = candidate;
        HMODULE probe = LoadLibraryExW(candidate.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (probe == nullptr) continue;
        const bool hasEntry = GetProcAddress(probe, requiredExport.c_str()) != nullptr;
        FreeLibrary(probe);
        if (hasEntry) { outPath = candidate; return true; }
    }
    if (!fallback.empty())
    {
        // A copy that does not export what we need is useless: report it instead of
        // letting the caller fail later with a confusing "missing export" message.
        error = "no copy of " + narrow + " exports " + requiredExport +
                " (incomplete: " + std::string(fallback.begin(), fallback.end()) + ")";
        return false;
    }
    error = "not found: " + narrow + " (need export " + requiredExport + ")";
    return false;
}

bool PoolAttachForUpdate(const std::wstring& imagePath, const std::wstring& poolName, unsigned int version,
                         const std::wstring& directory, const std::vector<std::wstring>& spaceNames,
                         std::vector<std::vector<unsigned char> >& storeIds,
                         std::vector<std::string>& report, std::string& error)
{
    (void)poolName;
    (void)version;
    storeIds.clear();
    report.clear();
    error.clear();
    g_poolLog.clear();
    {
        std::string dirError;
        if (!EnsurePoolTempDirectory(directory, dirError)) { error = dirError; return false; }
    }
    std::string dllPath;
    std::vector<std::string> found;
    std::vector<std::string> missing;
    if (!PoolProbe(dllPath, found, missing, error)) return false;
    const std::wstring wideDll(dllPath.begin(), dllPath.end());
    HMODULE imageStorage = LoadLibraryW(wideDll.c_str());
    if (imageStorage == nullptr)
    {
        error = "LoadLibraryW(ImageStorageService) failed (win32=" + std::to_string(GetLastError()) + ").";
        return false;
    }
    CreateServiceFn createService = reinterpret_cast<CreateServiceFn>(GetProcAddress(imageStorage, "CreateStorageService"));
    OpenVhdFn openVhd = reinterpret_cast<OpenVhdFn>(GetProcAddress(imageStorage, "OpenVirtualHardDisk"));
    GetDiskNameFn getDiskName = reinterpret_cast<GetDiskNameFn>(GetProcAddress(imageStorage, "GetDiskName"));
    OpenPoolFn openPool = reinterpret_cast<OpenPoolFn>(GetProcAddress(imageStorage, "OpenStoragePool"));
    AttachSpaceFn attachSpace = reinterpret_cast<AttachSpaceFn>(GetProcAddress(imageStorage, "AttachStorageSpace"));
    GetStoreIdFn getStoreId = reinterpret_cast<GetStoreIdFn>(GetProcAddress(imageStorage, "GetStoreId"));
    DetachSpaceFn detachSpace = reinterpret_cast<DetachSpaceFn>(GetProcAddress(imageStorage, "DetachStorageSpace"));
    ClosePoolFn closePool = reinterpret_cast<ClosePoolFn>(GetProcAddress(imageStorage, "CloseStoragePool"));
    CloseServiceFn closeService = reinterpret_cast<CloseServiceFn>(GetProcAddress(imageStorage, "CloseImageStorageService"));
    if (createService == nullptr || openVhd == nullptr || getDiskName == nullptr || openPool == nullptr ||
        attachSpace == nullptr || getStoreId == nullptr)
    {
        FreeLibrary(imageStorage);
        error = "ImageStorageService exports missing (CreateStorageService/OpenVirtualHardDisk/GetDiskName/OpenStoragePool/AttachStorageSpace/GetStoreId).";
        return false;
    }

    void* service = nullptr;
    void* pool = nullptr;
    void* baseDisk = nullptr;
    long rc = createService(&service, PoolLogCallback, 0, nullptr, 1);
    if (rc < 0 || service == nullptr)
    {
        FreeLibrary(imageStorage);
        error = "CreateStorageService failed (hr=" + std::to_string(static_cast<long long>(rc)) + ")." + eol8() + g_poolLog;
        return false;
    }
    unsigned char baseStore[20] = { 0 };
    wchar_t diskName[512] = { 0 };
    const bool fromDevice = imagePath.size() > 4 && imagePath.compare(0, 4, L"\\\\.\\") == 0;
    if (fromDevice)
    {
        HANDLE disk = CreateFileW(imagePath.c_str(), GENERIC_READ | GENERIC_WRITE,
                                  FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr);
        if (disk == INVALID_HANDLE_VALUE)
        {
            error = "Open device failed (win32=" + std::to_string(GetLastError()) + ")." + eol8() + g_poolLog;
            return false;
        }
        rc = getStoreId(service, disk, baseStore);
        CloseHandle(disk);
        if (rc < 0)
        {
            error = "GetStoreId failed (hr=" + std::to_string(static_cast<long long>(rc)) + ")." + eol8() + g_poolLog;
            return false;
        }
        wcsncpy_s(diskName, 511, imagePath.c_str(), 510);
        report.push_back("base store id captured (device path)");
    }
    else
    {
        rc = openVhd(service, imagePath.c_str(), 0, baseStore, &baseDisk);
        if (rc < 0)
        {
            error = "OpenVirtualHardDisk failed (hr=" + std::to_string(static_cast<long long>(rc)) + ")." + eol8() + g_poolLog;
            return false;
        }
        rc = getDiskName(baseDisk, diskName, 511);
        if (rc < 0)
        {
            error = "GetDiskName failed (hr=" + std::to_string(static_cast<long long>(rc)) + ")." + eol8() + g_poolLog;
            return false;
        }
    }
    rc = openPool(service, diskName, directory.c_str(), 1, &pool);
    if (rc < 0)
    {
        error = "OpenStoragePool failed (hr=" + std::to_string(static_cast<long long>(rc)) + ")." + eol8() + g_poolLog;
        return false;
    }
    storeIds.push_back(std::vector<unsigned char>(baseStore, baseStore + 20));
    for (const std::wstring& spaceName : spaceNames)
    {
        void* spaceDisk = nullptr;
        const long attachRc = attachSpace(service, pool, spaceName.c_str(), 0, &spaceDisk);
        if (attachRc < 0)
        {
            error = "AttachStorageSpace(" + WideToUtf8Local(spaceName.c_str()) + ") failed (hr=" +
                    std::to_string(static_cast<long long>(attachRc)) + ")." + eol8() + g_poolLog;
            return false;
        }
        unsigned char storeId[20] = { 0 };
        const long idRc = getStoreId(service, spaceDisk, storeId);
        if (idRc < 0)
        {
            error = "GetStoreId(" + WideToUtf8Local(spaceName.c_str()) + ") failed (hr=" +
                    std::to_string(static_cast<long long>(idRc)) + ").";
            return false;
        }
        storeIds.push_back(std::vector<unsigned char>(storeId, storeId + 20));
        report.push_back("space " + WideToUtf8Local(spaceName.c_str()) + ": storeId captured");
    }
    // UpdateDLL 自己接管 space；我们必须先把挂载和句柄释放掉，
    // 否则 UpdateDLL 的 Initialize/PrepareUpdate 会挂死在池/space 的持久锁上（实测无进度）。
    if (detachSpace != nullptr && closePool != nullptr && closeService != nullptr)
    {
        for (std::size_t i = 1; i < storeIds.size(); ++i)
        {
            detachSpace(service, pool, spaceNames[i - 1].c_str(), 0);
        }
        closePool(service, pool, 0);
        closeService(service);
        FreeLibrary(imageStorage);
        report.push_back("spaces detached; pool/service closed before UpdateDLL");
    }
    return true;
}

bool PoolDetachSpaces(const std::wstring& imagePath, const std::wstring& poolName, unsigned int version,
                      const std::wstring& directory, const std::vector<std::wstring>& spaceNames,
                      std::vector<std::string>& report, std::string& error)
{
    (void)poolName;
    (void)version;
    report.clear();
    error.clear();
    g_poolLog.clear();
    {
        std::string dirError;
        if (!EnsurePoolTempDirectory(directory, dirError)) { error = dirError; return false; }
    }
    std::string dllPath;
    std::vector<std::string> found;
    std::vector<std::string> missing;
    if (!PoolProbe(dllPath, found, missing, error)) return false;
    const std::wstring wideDll(dllPath.begin(), dllPath.end());
    HMODULE module = LoadLibraryW(wideDll.c_str());
    if (module == nullptr)
    {
        error = "LoadLibraryW failed (win32=" + std::to_string(GetLastError()) + ").";
        return false;
    }
    CreateServiceFn createService = reinterpret_cast<CreateServiceFn>(GetProcAddress(module, "CreateStorageService"));
    CloseServiceFn closeService = reinterpret_cast<CloseServiceFn>(GetProcAddress(module, "CloseImageStorageService"));
    OpenVhdFn openVhd = reinterpret_cast<OpenVhdFn>(GetProcAddress(module, "OpenVirtualHardDisk"));
    GetDiskNameFn getDiskName = reinterpret_cast<GetDiskNameFn>(GetProcAddress(module, "GetDiskName"));
    OpenPoolFn openPool = reinterpret_cast<OpenPoolFn>(GetProcAddress(module, "OpenStoragePool"));
    ClosePoolFn closePool = reinterpret_cast<ClosePoolFn>(GetProcAddress(module, "CloseStoragePool"));
    DetachSpaceFn detachSpace = reinterpret_cast<DetachSpaceFn>(GetProcAddress(module, "DetachStorageSpace"));
    if (createService == nullptr || closeService == nullptr || openVhd == nullptr || getDiskName == nullptr ||
        openPool == nullptr || closePool == nullptr || detachSpace == nullptr)
    {
        FreeLibrary(module);
        error = "ImageStorageService exports missing (PoolDetachSpaces).";
        return false;
    }
    void* service = nullptr;
    long rc = createService(&service, PoolLogCallback, 0, nullptr, 1);
    if (rc < 0 || service == nullptr)
    {
        FreeLibrary(module);
        error = "CreateStorageService failed (hr=" + std::to_string(static_cast<long long>(rc)) + ").";
        return false;
    }
    wchar_t diskName[512] = { 0 };
    void* baseDisk = nullptr;
    if (IsDevicePathW(imagePath))
    {
        wcsncpy_s(diskName, 512, imagePath.c_str(), 511);
    }
    else
    {
        unsigned char storeId[20] = { 0 };
        rc = openVhd(service, imagePath.c_str(), 0, storeId, &baseDisk);
        if (rc < 0)
        {
            closeService(service);
            FreeLibrary(module);
            error = "OpenVirtualHardDisk failed (hr=" + std::to_string(static_cast<long long>(rc)) + ").";
            return false;
        }
        rc = getDiskName(baseDisk, diskName, 511);
        if (rc < 0)
        {
            CloseHandle(baseDisk);
            closeService(service);
            FreeLibrary(module);
            error = "GetDiskName failed (hr=" + std::to_string(static_cast<long long>(rc)) + ").";
            return false;
        }
    }
    void* pool = nullptr;
    rc = openPool(service, diskName, directory.c_str(), 1, &pool);
    if (rc < 0)
    {
        if (baseDisk != nullptr) CloseHandle(baseDisk);
        closeService(service);
        FreeLibrary(module);
        error = "OpenStoragePool failed (hr=" + std::to_string(static_cast<long long>(rc)) + ")." + eol8() + g_poolLog;
        return false;
    }
    std::vector<std::wstring> names = spaceNames;
    names.push_back(L"SERVICING_METADATADISK");
    names.push_back(L"SERVICING_FILESDISK");
    int detached = 0;
    for (const std::wstring& name : names)
    {
        const long detachRc = detachSpace(service, pool, name.c_str(), 0);
        if (detachRc >= 0)
        {
            ++detached;
            report.push_back("space detached: " + std::string(name.begin(), name.end()));
        }
        else if (static_cast<unsigned int>(detachRc) == 0x80070490u)
        {
            report.push_back("space not attached: " + std::string(name.begin(), name.end()));
        }
        else
        {
            char buffer[128] = { 0 };
            sprintf_s(buffer, sizeof(buffer), "space detach failed: %ls (hr=0x%08X)", name.c_str(),
                      static_cast<unsigned int>(detachRc));
            report.push_back(buffer);
            const long retryRc = detachSpace(service, pool, name.c_str(), 1);
            if (retryRc >= 0)
            {
                ++detached;
                report.push_back("space detached on retry: " + std::string(name.begin(), name.end()));
            }
        }
    }
    (void)closePool(service, pool, 0);
    closeService(service);
    if (baseDisk != nullptr) CloseHandle(baseDisk);
    report.push_back("spaces detached: " + std::to_string(detached));
    if (!IsDevicePathW(imagePath))
    {
        std::string detachError;
        if (VhdDetach(imagePath, detachError)) report.push_back("base vhdx detached");
        else report.push_back("base vhdx detach failed: " + detachError);
    }
    return true;
}

namespace {
    std::string ProbeVolumeFileSystem(const std::string& device, std::uint64_t offset)
    {
        unsigned char head[4096] = { 0 };
        std::string readError;
        if (!VhdReadBytes(device, offset, head, static_cast<std::size_t>(sizeof(head)), readError)) return "读失败";
        bool allZero = true;
        for (unsigned char ch : head) { if (ch != 0) { allZero = false; break; } }
        if (allZero) return "未格式化";
        if (head[3] == 'N' && head[4] == 'T' && head[5] == 'F' && head[6] == 'S') return "NTFS";
        if ((head[0] == 0xEB || head[0] == 0xE9) && std::memcmp(head + 54, "FAT16", 5) == 0) return "FAT16";
        if ((head[0] == 0xEB || head[0] == 0xE9) && std::memcmp(head + 54, "FAT32", 5) == 0) return "FAT32";
        if ((head[0] == 0xEB || head[0] == 0xE9) && std::memcmp(head + 82, "FAT32", 5) == 0) return "FAT32";
        if ((head[0] == 0xEB || head[0] == 0xE9) && std::memcmp(head + 54, "FAT", 3) == 0) return "FAT";
        return "未知";
    }
}

bool PoolVerifySpaces(const std::wstring& imagePath, const std::wstring& poolName, unsigned int version,
                      const std::wstring& directory, const std::vector<std::wstring>& spaceNames,
                      std::vector<std::string>& report, std::string& error)
{
    report.clear();
    error.clear();
    if (imagePath.empty()) { error = "未指定虚拟磁盘"; return false; }
    std::string dllPath, dirError;
    std::vector<std::string> found, missing;
    if (!EnsurePoolTempDirectory(directory, dirError)) { error = dirError; return false; }
    if (!PoolProbe(dllPath, found, missing, error)) return false;
    if (!missing.empty()) { error = "ImageStorageService 缺少导出：" + missing[0]; return false; }
    const std::wstring wideDll(dllPath.begin(), dllPath.end());
    HMODULE module = LoadLibraryW(wideDll.c_str());
    if (module == nullptr) { error = "LoadLibraryW 失败（win32=" + std::to_string(GetLastError()) + "）"; return false; }
    CreateServiceFn createService = reinterpret_cast<CreateServiceFn>(GetProcAddress(module, "CreateStorageService"));
    CloseServiceFn closeService = reinterpret_cast<CloseServiceFn>(GetProcAddress(module, "CloseImageStorageService"));
    OpenVhdFn openVhd = reinterpret_cast<OpenVhdFn>(GetProcAddress(module, "OpenVirtualHardDisk"));
    GetDiskNameFn getDiskName = reinterpret_cast<GetDiskNameFn>(GetProcAddress(module, "GetDiskName"));
    OpenPoolFn openPool = reinterpret_cast<OpenPoolFn>(GetProcAddress(module, "OpenStoragePool"));
    ClosePoolFn closePool = reinterpret_cast<ClosePoolFn>(GetProcAddress(module, "CloseStoragePool"));
    AttachSpaceFn attachSpace = reinterpret_cast<AttachSpaceFn>(GetProcAddress(module, "AttachStorageSpace"));
    DetachSpaceFn detachSpace = reinterpret_cast<DetachSpaceFn>(GetProcAddress(module, "DetachStorageSpace"));
    if (createService == nullptr || closeService == nullptr || openVhd == nullptr || getDiskName == nullptr ||
        openPool == nullptr || closePool == nullptr || attachSpace == nullptr || detachSpace == nullptr)
    {
        FreeLibrary(module);
        error = "ImageStorageService 缺少 space 相关导出";
        return false;
    }
    g_poolLog.clear();
    void* service = nullptr;
    long rc = createService(&service, PoolLogCallback, 0, nullptr, 1);
    if (rc < 0 || service == nullptr)
    {
        FreeLibrary(module);
        error = "CreateStorageService 失败（hr=" + std::to_string(static_cast<long long>(rc)) + "）。" + eol8() + g_poolLog;
        return false;
    }
    unsigned char storeId[32] = {};
    void* baseDisk = nullptr;
    std::wstring diskName;
    if (IsDevicePathW(imagePath))
    {
        baseDisk = CreateFileW(imagePath.c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                               nullptr, OPEN_EXISTING, 0, nullptr);
        if (baseDisk == INVALID_HANDLE_VALUE)
        {
            baseDisk = nullptr;
            closeService(service);
            FreeLibrary(module);
            error = "OpenDevice failed (win32=" + std::to_string(GetLastError()) + ")";
            return false;
        }
        diskName = imagePath;
    }
    else
    {
        rc = openVhd(service, imagePath.c_str(), 0, storeId, &baseDisk);
        if (rc < 0 || baseDisk == nullptr)
        {
            closeService(service);
            FreeLibrary(module);
            error = "OpenVirtualHardDisk 失败（hr=" + std::to_string(static_cast<long long>(rc)) + "）。" + eol8() + g_poolLog;
            return false;
        }
        wchar_t nameBuffer[512] = { 0 };
        if (getDiskName(baseDisk, nameBuffer, 512) >= 0) diskName.assign(nameBuffer);
    }
    void* pool = nullptr;
    rc = openPool(service, diskName.c_str(), directory.c_str(), 1, &pool);
    if (rc < 0 || pool == nullptr)
    {
        closeService(service);
        if (baseDisk != nullptr) CloseHandle(baseDisk);
        FreeLibrary(module);
        error = "OpenStoragePool 失败（hr=" + std::to_string(static_cast<long long>(rc)) + "）。" + eol8() + g_poolLog;
        return false;
    }
    (void)poolName;
    (void)version;
    bool ok = true;
    for (const std::wstring& name : spaceNames)
    {
        void* spaceDisk = nullptr;
        const long attachRc = attachSpace(service, pool, name.c_str(), 0, &spaceDisk);
        const std::string spaceName = WideToUtf8Local(name.c_str());
        if (attachRc < 0 || spaceDisk == nullptr)
        {
            ok = false;
            report.push_back("space " + spaceName + "：挂载失败（hr=" + std::to_string(static_cast<long long>(attachRc)) + "）");
            continue;
        }
        wchar_t spaceBuffer[512] = { 0 };
        const long nameRc = getDiskName(spaceDisk, spaceBuffer, 512);
        const std::string spaceDevice = nameRc >= 0 ? WideToUtf8Local(spaceBuffer) : std::string();
        std::vector<VhdPartition> parts;
        std::string gptError;
        std::string gptDiskId;
        std::uint32_t gptSector = 0;
        if (!VhdReadGpt(spaceDevice, gptDiskId, parts, gptSector, gptError))
        {
            ok = false;
            report.push_back("space " + spaceName + "：读 GPT 失败（" + gptError + "）");
        }
        else
        {
            report.push_back("space " + spaceName + "  device=" + spaceDevice + "  分区数=" + std::to_string(parts.size()));
            for (const VhdPartition& part : parts)
            {
                report.push_back("    " + part.name + "  type=" + part.type + "  offset=" + std::to_string(part.offsetBytes) +
                                 "  size=" + std::to_string(part.sizeBytes) + "  fs=" +
                                 ProbeVolumeFileSystem(spaceDevice, part.offsetBytes));
            }
        }
        (void)detachSpace(service, pool, name.c_str(), 0);
    }
    (void)closePool(service, pool, 0);
    closeService(service);
    if (baseDisk != nullptr) CloseHandle(baseDisk);
    FreeLibrary(module);
    return ok;
}

bool PoolFormatPartitions(const std::wstring& imagePath,
                          const std::vector<PhysicalFormatSpec>& partitions,
                          std::vector<std::string>& report,
                          std::string& error)
{
    report.clear();
    error.clear();
    if (imagePath.empty()) { error = "未指定虚拟磁盘"; return false; }
    if (partitions.empty()) { error = "没有要格式化的物理分区"; return false; }
    std::string dllPath;
    std::vector<std::string> found;
    std::vector<std::string> missing;
    if (!PoolProbe(dllPath, found, missing, error)) return false;
    if (!missing.empty()) { error = "ImageStorageService 缺少导出：" + missing[0]; return false; }
    const std::wstring wideDll(dllPath.begin(), dllPath.end());
    HMODULE module = LoadLibraryW(wideDll.c_str());
    if (module == nullptr) { error = "LoadLibraryW 失败（win32=" + std::to_string(GetLastError()) + "）"; return false; }
    CreateServiceFn createService = reinterpret_cast<CreateServiceFn>(GetProcAddress(module, "CreateStorageService"));
    CloseServiceFn closeService = reinterpret_cast<CloseServiceFn>(GetProcAddress(module, "CloseImageStorageService"));
    OpenVhdFn openVhd = reinterpret_cast<OpenVhdFn>(GetProcAddress(module, "OpenVirtualHardDisk"));
    FormatPartitionFn formatPartition = reinterpret_cast<FormatPartitionFn>(GetProcAddress(module, "FormatPartition"));
    if (createService == nullptr || closeService == nullptr || openVhd == nullptr || formatPartition == nullptr)
    {
        FreeLibrary(module);
        error = "GetProcAddress 失败（FormatPartition）。";
        return false;
    }
    g_poolLog.clear();
    void* service = nullptr;
    long rc = createService(&service, PoolLogCallback, 0, nullptr, 1);
    if (rc < 0 || service == nullptr)
    {
        FreeLibrary(module);
        error = "CreateStorageService 失败（hr=" + std::to_string(static_cast<long long>(rc)) + "）。" + eol8() + g_poolLog;
        return false;
    }
    unsigned char storeId[32] = {};
    void* baseDisk = nullptr;
    if (IsDevicePathW(imagePath))
    {
        baseDisk = CreateFileW(imagePath.c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                               nullptr, OPEN_EXISTING, 0, nullptr);
        if (baseDisk == INVALID_HANDLE_VALUE)
        {
            baseDisk = nullptr;
            closeService(service);
            FreeLibrary(module);
            error = "OpenDevice failed (win32=" + std::to_string(GetLastError()) + ")";
            return false;
        }
    }
    else
    {
        rc = openVhd(service, imagePath.c_str(), 0, storeId, &baseDisk);
        if (rc < 0 || baseDisk == nullptr)
        {
            closeService(service);
            FreeLibrary(module);
            error = "OpenVirtualHardDisk 失败（hr=" + std::to_string(static_cast<long long>(rc)) + "）。" + eol8() + g_poolLog;
            return false;
        }
    }
    bool ok = true;
    for (const PhysicalFormatSpec& spec : partitions)
    {
        g_poolLog.clear();
        rc = formatPartition(service, baseDisk, spec.name.c_str(), spec.fileSystem.c_str(), spec.clusterSize);
        const std::string name = WideToUtf8Local(spec.name.c_str());
        const std::string fs = WideToUtf8Local(spec.fileSystem.c_str());
        if (rc < 0)
        {
            ok = false;
            report.push_back("物理分区格式化失败：" + name + "（hr=" + std::to_string(static_cast<long long>(rc)) + "）");
            if (!g_poolLog.empty()) report.push_back(g_poolLog);
        }
        else
        {
            report.push_back("物理分区已格式化：" + name + " " + fs);
        }
    }
    closeService(service);
    if (baseDisk != nullptr) CloseHandle(baseDisk);
    FreeLibrary(module);
    return ok;
}
} // namespace ocid::imaging
