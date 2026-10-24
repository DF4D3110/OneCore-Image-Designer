// PoolMount.cpp — 把池内 space 挂到宿主盘符，返回 MainOS 分区路径。
//
// ImageApp 在建 UpdateOS.wim 之前会先 AssignMountPoints 把各 store 的卷挂出来，
// 然后往 <MainOS>\PROGRAMS\UpdateOS\UPDATEOS.wim 里捕一个空 WIM。这里做同一件事：
//   基座盘 → 打开池 → 逐 space 挂载 → 取 STORE_ID → AssignMountPoints
//   → GetPartitionPath(MainOS) 得到宿主路径。
// space 保持挂载（UpdateDLL 还要用），池与服务句柄在本函数返回前关闭。

#include "ocid/imaging/Pool.h"
#include "ocid/imaging/Common.h"

#include <windows.h>

#include <string>
#include <vector>

namespace ocid
{
namespace imaging
{
namespace
{
typedef long(__stdcall* CreateServiceFn)(void**, void(__stdcall*)(const wchar_t*), unsigned int, void*, int);
typedef long(__stdcall* CloseServiceFn)(void*);
typedef long(__stdcall* OpenVhdFn)(void*, const wchar_t*, int, unsigned char*, void**);
typedef long(__stdcall* GetDiskNameFn)(void*, wchar_t*, unsigned int);
typedef long(__stdcall* GetStoreIdFn)(void*, void*, unsigned char*);
typedef long(__stdcall* OpenPoolFn)(void*, const wchar_t*, const wchar_t*, int, void**);
typedef long(__stdcall* ClosePoolFn)(void*, void*, int);
typedef long(__stdcall* AttachSpaceFn)(void*, void*, const wchar_t*, int, void**);
typedef long(__stdcall* DetachSpaceFn)(void*, void*, const wchar_t*, int);
// GetPartitionPath_Native 的 STORE_ID 参数是“按值”传（托管声明 storeId 而非 ref/out），
// 20 字节结构会直接压栈；早前按指针传导致 ABI 错位，服务返回 0x80070490。
#pragma pack(push, 4)
struct StoreIdValue
{
    unsigned int storeType;
    unsigned char guid[16];
};
#pragma pack(pop)
typedef long(__stdcall* AssignMountPointsFn)(void*, void*, unsigned char*, unsigned int, int);
typedef long(__stdcall* GetPartitionPathFn)(void*, StoreIdValue, const wchar_t*, wchar_t*, unsigned int, int);
static StoreIdValue ToStoreIdValue(const std::vector<unsigned char>& id)
{
    StoreIdValue value{};
    value.storeType = static_cast<unsigned int>(id[0]) | (static_cast<unsigned int>(id[1]) << 8) |
                      (static_cast<unsigned int>(id[2]) << 16) | (static_cast<unsigned int>(id[3]) << 24);
    for (int i = 0; i < 16; ++i) value.guid[i] = id[4 + i];
    return value;
}

std::string WideToLocal(const wchar_t* text)
{
    if (text == nullptr) return std::string();
    const int need = WideCharToMultiByte(CP_UTF8, 0, text, -1, nullptr, 0, nullptr, nullptr);
    if (need <= 1) return std::string();
    std::string out(static_cast<std::size_t>(need - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text, -1, &out[0], need - 1, nullptr, nullptr);
    return out;
}

std::string Hex(long value)
{
    char buffer[32] = { 0 };
    sprintf_s(buffer, sizeof(buffer), "0x%08lX", static_cast<unsigned long>(value));
    return std::string(buffer);
}

void __stdcall MountLog(const wchar_t* line)
{
    if (line != nullptr) OutputDebugStringW(line);
}

bool EnsureDirectoryTree(const std::wstring& path, std::string& error)
{
    if (path.empty()) { error = "directory path is empty"; return false; }
    std::wstring current;
    for (std::size_t i = 0; i < path.size(); ++i)
    {
        current.push_back(path[i]);
        const bool last = i + 1 == path.size();
        if (!last && path[i] != L'\\' && path[i] != L'/') continue;
        if (current.size() <= 1 || (current.size() == 3 && current[1] == L':')) continue;
        if (!CreateDirectoryW(current.c_str(), nullptr))
        {
            const DWORD code = GetLastError();
            if (code != ERROR_ALREADY_EXISTS)
            {
                error = "CreateDirectoryW failed (win32=" + std::to_string(code) + ").";
                return false;
            }
        }
    }
    return true;
}
} // namespace

bool PoolMountForUpdate(const std::wstring& imagePath,
                        const std::wstring& poolName,
                        unsigned int version,
                        const std::wstring& directory,
                        const std::vector<std::wstring>& spaceNames,
                        const std::wstring& mainOsSpaceName,
                        std::vector<std::vector<unsigned char> >& storeIds,
                        std::wstring& mainOsPath,
                        std::vector<std::string>& report,
                        std::string& error)
{
    (void)poolName;
    (void)version;
    storeIds.clear();
    mainOsPath.clear();
    report.clear();
    error.clear();

    std::wstring dllPath;
    if (!PoolFindAdkDll(L"ImageStorageService.dll", "AssignMountPoints", dllPath, error)) return false;
    HMODULE module = LoadLibraryExW(dllPath.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (module == nullptr)
    {
        error = "LoadLibraryExW(ImageStorageService) failed (win32=" + std::to_string(GetLastError()) + ").";
        return false;
    }
    CreateServiceFn createService = reinterpret_cast<CreateServiceFn>(GetProcAddress(module, "CreateStorageService"));
    CloseServiceFn closeService = reinterpret_cast<CloseServiceFn>(GetProcAddress(module, "CloseImageStorageService"));
    OpenVhdFn openVhd = reinterpret_cast<OpenVhdFn>(GetProcAddress(module, "OpenVirtualHardDisk"));
    GetDiskNameFn getDiskName = reinterpret_cast<GetDiskNameFn>(GetProcAddress(module, "GetDiskName"));
    GetStoreIdFn getStoreId = reinterpret_cast<GetStoreIdFn>(GetProcAddress(module, "GetStoreId"));
    OpenPoolFn openPool = reinterpret_cast<OpenPoolFn>(GetProcAddress(module, "OpenStoragePool"));
    ClosePoolFn closePool = reinterpret_cast<ClosePoolFn>(GetProcAddress(module, "CloseStoragePool"));
    AttachSpaceFn attachSpace = reinterpret_cast<AttachSpaceFn>(GetProcAddress(module, "AttachStorageSpace"));
    DetachSpaceFn detachSpace = reinterpret_cast<DetachSpaceFn>(GetProcAddress(module, "DetachStorageSpace"));
    AssignMountPointsFn assignMountPoints =
        reinterpret_cast<AssignMountPointsFn>(GetProcAddress(module, "AssignMountPoints"));
    GetPartitionPathFn getPartitionPath =
        reinterpret_cast<GetPartitionPathFn>(GetProcAddress(module, "GetPartitionPath"));
    if (createService == nullptr || closeService == nullptr || openVhd == nullptr || getDiskName == nullptr ||
        getStoreId == nullptr || openPool == nullptr || closePool == nullptr || attachSpace == nullptr ||
        assignMountPoints == nullptr || getPartitionPath == nullptr)
    {
        FreeLibrary(module);
        error = "ImageStorageService exports missing (PoolMountForUpdate).";
        return false;
    }

    std::string dirError;
    if (!EnsureDirectoryTree(directory, dirError))
    {
        FreeLibrary(module);
        error = dirError;
        return false;
    }

    OutLn("[mount] CreateStorageService ...");
    void* service = nullptr;
    long rc = createService(&service, MountLog, 0, nullptr, 1);
    if (rc < 0 || service == nullptr)
    {
        FreeLibrary(module);
        error = "CreateStorageService failed (" + Hex(rc) + ").";
        return false;
    }

    const bool fromDevice = imagePath.size() > 4 && imagePath.compare(0, 4, L"\\\\.\\") == 0;
    unsigned char baseStore[20] = { 0 };
    wchar_t diskName[512] = { 0 };
    void* baseDisk = nullptr;
    if (fromDevice)
    {
        HANDLE disk = CreateFileW(imagePath.c_str(), GENERIC_READ | GENERIC_WRITE,
                                  FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr);
        if (disk == INVALID_HANDLE_VALUE)
        {
            closeService(service);
            FreeLibrary(module);
            error = "Open device failed (win32=" + std::to_string(GetLastError()) + ").";
            return false;
        }
        rc = getStoreId(service, disk, baseStore);
        CloseHandle(disk);
        if (rc < 0)
        {
            closeService(service);
            FreeLibrary(module);
            error = "GetStoreId(base) failed (" + Hex(rc) + ").";
            return false;
        }
        wcsncpy_s(diskName, 511, imagePath.c_str(), 510);
    }
    else
    {
        rc = openVhd(service, imagePath.c_str(), 0, baseStore, &baseDisk);
        if (rc < 0)
        {
            closeService(service);
            FreeLibrary(module);
            error = "OpenVirtualHardDisk failed (" + Hex(rc) + ").";
            return false;
        }
        rc = getDiskName(baseDisk, diskName, 511);
        if (rc < 0)
        {
            CloseHandle(baseDisk);
            closeService(service);
            FreeLibrary(module);
            error = "GetDiskName failed (" + Hex(rc) + ").";
            return false;
        }
    }

    OutLn("[mount] OpenStoragePool ...");
    void* pool = nullptr;
    rc = openPool(service, diskName, directory.c_str(), 1, &pool);
    if (rc < 0)
    {
        if (baseDisk != nullptr) CloseHandle(baseDisk);
        closeService(service);
        FreeLibrary(module);
        error = "OpenStoragePool failed (" + Hex(rc) + ").";
        return false;
    }

    OutLn("[mount] base store id captured");
    storeIds.push_back(std::vector<unsigned char>(baseStore, baseStore + 20));
    std::vector<void*> spaceHandles;
    for (const std::wstring& name : spaceNames)
    {
        void* spaceDisk = nullptr;
        OutLn("[mount] AttachStorageSpace " + WideToLocal(name.c_str()) + " ...");
        rc = attachSpace(service, pool, name.c_str(), 0, &spaceDisk);
        if (rc < 0)
        {
            error = "AttachStorageSpace(" + WideToLocal(name.c_str()) + ") failed (" + Hex(rc) + ").";
            closePool(service, pool, 0);
            if (baseDisk != nullptr) CloseHandle(baseDisk);
            closeService(service);
            FreeLibrary(module);
            return false;
        }
        unsigned char storeId[20] = { 0 };
        rc = getStoreId(service, spaceDisk, storeId);
        if (rc < 0)
        {
            error = "GetStoreId(" + WideToLocal(name.c_str()) + ") failed (" + Hex(rc) + ").";
            closePool(service, pool, 0);
            if (baseDisk != nullptr) CloseHandle(baseDisk);
            closeService(service);
            FreeLibrary(module);
            return false;
        }
        storeIds.push_back(std::vector<unsigned char>(storeId, storeId + 20));
        spaceHandles.push_back(spaceDisk);
        report.push_back("space " + WideToLocal(name.c_str()) + ": storeId captured");
    }

    // AssignMountPoints 的第二个参数在 MS 托管代码里是 MainOSStorage.SafeStoreHandle，
    // 即 MainOS 所在 space 的 store 句柄，不是基座物理盘句柄（传错会得到 0x80070490）。
    std::size_t mainOsIndex = spaceHandles.size();
    for (std::size_t i = 0; i < spaceNames.size(); ++i)
    {
        if (!mainOsSpaceName.empty() && spaceNames[i] == mainOsSpaceName)
        {
            mainOsIndex = i;
            break;
        }
    }
    if (mainOsIndex >= spaceHandles.size())
    {
        error = "no space in this pool holds a MainOS partition.";
        closePool(service, pool, 0);
        if (baseDisk != nullptr) CloseHandle(baseDisk);
        closeService(service);
        FreeLibrary(module);
        return false;
    }

    // 先试一次 AssignMountPoints：ImageApp 在造 UpdateOS.wim 之前就把盘符装进 MountManager，
    // UpdateDLL 之后按盘符解析 <MainOS>\PROGRAMS\UpdateOS\UpdateOS.wim。成功与否都继续。
    {
        std::vector<unsigned char> assignFlat;
        assignFlat.reserve(storeIds.size() * 20);
        for (std::size_t i = 1; i < storeIds.size(); ++i)
            assignFlat.insert(assignFlat.end(), storeIds[i].begin(), storeIds[i].end());
        OutLn("[mount] AssignMountPoints ...");
        const long assignRc = assignMountPoints(service, spaceHandles[mainOsIndex], &assignFlat[0],
                                               static_cast<unsigned int>(storeIds.size() - 1), 0);
        char line[128] = {};
        wsprintfA(line, "AssignMountPoints rc=%ld (0x%08lx)", assignRc, static_cast<unsigned long>(assignRc));
        report.push_back(line);
        OutLn(std::string("  ") + line);
    }

    // 先直接要 MainOS 卷路径（GetPartitionPath 带 waitForVolume=1，卷出现后会返回）。
    // AssignMountPoints 在“没有 ImageApp 那套 FFU store 状态”的进程里会返回 0x80070490，
    // 而造 UpdateOS.wim 只需要卷路径，不需要盘符，所以这里以 GetPartitionPath 为主。
    wchar_t pathBuffer[512] = { 0 };
    mainOsPath.clear();
    for (int attempt = 0; attempt < 12 && mainOsPath.empty(); ++attempt)
    {
        for (std::size_t i = 0; i < spaceHandles.size() && mainOsPath.empty(); ++i)
        {
            const std::vector<unsigned char>& id = storeIds[i + 1];
            pathBuffer[0] = L'\0';
            const long pathRc = getPartitionPath(service, ToStoreIdValue(id), L"MainOS", pathBuffer, 512, 1);
            if (pathRc >= 0 && pathBuffer[0] != L'\0')
            {
                mainOsPath.assign(pathBuffer);
                report.push_back("MainOS path: " + WideToLocal(pathBuffer));
            }
        }
        if (mainOsPath.empty()) Sleep(500);
    }
    if (mainOsPath.empty())
    {
        report.push_back("GetPartitionPath(MainOS) 12 次均未拿到路径，尝试 AssignMountPoints 兜底");
        std::vector<unsigned char> assignFlat;
        assignFlat.reserve(storeIds.size() * 20);
        for (std::size_t i = 1; i < storeIds.size(); ++i)
            assignFlat.insert(assignFlat.end(), storeIds[i].begin(), storeIds[i].end());
        OutLn("[mount] AssignMountPoints ...");
        rc = assignMountPoints(service, spaceHandles[mainOsIndex], &assignFlat[0],
                               static_cast<unsigned int>(storeIds.size() - 1), 0);
        if (rc < 0)
        {
            error = "GetPartitionPath(MainOS) failed and AssignMountPoints failed (" + Hex(rc) + ").";
            closePool(service, pool, 0);
            if (baseDisk != nullptr) CloseHandle(baseDisk);
            closeService(service);
            FreeLibrary(module);
            return false;
        }
        for (int attempt = 0; attempt < 12 && mainOsPath.empty(); ++attempt)
        {
            for (std::size_t i = 0; i < spaceHandles.size() && mainOsPath.empty(); ++i)
            {
                const std::vector<unsigned char>& id = storeIds[i + 1];
                pathBuffer[0] = L'\0';
                if (getPartitionPath(service, ToStoreIdValue(id), L"MainOS", pathBuffer, 512, 1) >= 0 && pathBuffer[0] != L'\0')
                    mainOsPath.assign(pathBuffer);
            }
            if (mainOsPath.empty()) Sleep(500);
        }
    }

    (void)detachSpace; // space 保持挂载，UpdateDLL 还要用
    closePool(service, pool, 0);
    closeService(service);
    FreeLibrary(module);

    if (mainOsPath.empty())
    {
        error = "GetPartitionPath(MainOS) failed (no MainOS partition found in pool).";
        return false;
    }
    return true;
}
// 挂载池内全部 space 并解析每个 space 内指定分区的卷路径（供 CLI 的 pool mount 使用）；
// 调用方负责随后 PoolDetachSpaces 或让进程退出时自动卸载。
namespace {
static std::string HrHexLocal(long rc)
{
    char buffer[16] = {0};
    wsprintfA(buffer, "%08X", static_cast<unsigned int>(rc));
    return std::string(buffer);
}
}
bool PoolMountSpaces(const std::wstring& imagePath, const std::wstring& poolName,
                     unsigned int version, const std::wstring& directory,
                     const std::vector<std::pair<std::wstring, std::wstring> >& spaces,
                     std::vector<std::vector<unsigned char> >& storeIds,
                     std::vector<std::pair<std::wstring, std::wstring> >& volumes,
                     std::vector<std::string>& report, std::string& error)
{
    (void)poolName;
    (void)version;
    storeIds.clear();
    volumes.clear();
    report.clear();
    error.clear();
    if (!EnsureDirectoryTree(directory, error)) return false;

    std::wstring dllPath;
    if (!PoolFindAdkDll(L"ImageStorageService.dll", "AssignMountPoints", dllPath, error)) return false;
    HMODULE module = LoadLibraryExW(dllPath.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (module == nullptr)
    {
        error = "LoadLibraryExW(ImageStorageService) failed (win32=" + std::to_string(GetLastError()) + ").";
        return false;
    }
    CreateServiceFn createService = reinterpret_cast<CreateServiceFn>(GetProcAddress(module, "CreateStorageService"));
    CloseServiceFn closeService = reinterpret_cast<CloseServiceFn>(GetProcAddress(module, "CloseImageStorageService"));
    OpenVhdFn openVhd = reinterpret_cast<OpenVhdFn>(GetProcAddress(module, "OpenVirtualHardDisk"));
    GetDiskNameFn getDiskName = reinterpret_cast<GetDiskNameFn>(GetProcAddress(module, "GetDiskName"));
    GetStoreIdFn getStoreId = reinterpret_cast<GetStoreIdFn>(GetProcAddress(module, "GetStoreId"));
    OpenPoolFn openPool = reinterpret_cast<OpenPoolFn>(GetProcAddress(module, "OpenStoragePool"));
    ClosePoolFn closePool = reinterpret_cast<ClosePoolFn>(GetProcAddress(module, "CloseStoragePool"));
    AttachSpaceFn attachSpace = reinterpret_cast<AttachSpaceFn>(GetProcAddress(module, "AttachStorageSpace"));
    DetachSpaceFn detachSpace = reinterpret_cast<DetachSpaceFn>(GetProcAddress(module, "DetachStorageSpace"));
    GetPartitionPathFn getPartitionPath = reinterpret_cast<GetPartitionPathFn>(GetProcAddress(module, "GetPartitionPath"));
    if (createService == nullptr || openVhd == nullptr || getDiskName == nullptr || getStoreId == nullptr ||
        openPool == nullptr || attachSpace == nullptr || getPartitionPath == nullptr)
    {
        FreeLibrary(module);
        error = "ImageStorageService 缺少 pool mount 所需导出。";
        return false;
    }
    void* service = nullptr;
    if (createService(&service, MountLog, 0, nullptr, 1) < 0 || service == nullptr)
    {
        FreeLibrary(module);
        error = "CreateStorageService failed.";
        return false;
    }
    unsigned char baseId[20] = { 0 };
    void* imageDisk = nullptr;
    wchar_t diskName[512] = { 0 };
    const bool byDevice = imagePath.size() > 4 && imagePath.compare(0, 4, L"\\\\.\\") == 0;
    if (byDevice)
    {
        HANDLE diskHandle = CreateFileW(imagePath.c_str(), GENERIC_READ | GENERIC_WRITE,
                                        FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr);
        if (diskHandle == INVALID_HANDLE_VALUE)
        {
            if (closeService != nullptr) closeService(service);
            FreeLibrary(module);
            error = "CreateFileW(device) failed (win32=" + std::to_string(GetLastError()) + ").";
            return false;
        }
        imageDisk = diskHandle;
        if (getStoreId(service, imageDisk, baseId) < 0)
        {
            CloseHandle(diskHandle);
            if (closeService != nullptr) closeService(service);
            FreeLibrary(module);
            error = "GetStoreId(base) failed.";
            return false;
        }
    }
    else if (openVhd(service, imagePath.c_str(), 0, baseId, &imageDisk) < 0 || imageDisk == nullptr)
    {
        if (closeService != nullptr) closeService(service);
        FreeLibrary(module);
        error = "OpenVirtualHardDisk failed.";
        return false;
    }
    getDiskName(imageDisk, diskName, 512);
    void* pool = nullptr;
    if (openPool(service, diskName, directory.c_str(), 1, &pool) < 0 || pool == nullptr)
    {
        if (closeService != nullptr) closeService(service);
        FreeLibrary(module);
        error = "OpenStoragePool failed.";
        return false;
    }
    bool ok = true;
    for (std::size_t i = 0; i < spaces.size(); ++i)
    {
        void* spaceDisk = nullptr;
        const long attachRc = attachSpace(service, pool, spaces[i].first.c_str(), 0, &spaceDisk);
        if (attachRc < 0 || spaceDisk == nullptr)
        {
            report.push_back("space " + WideToLocal(spaces[i].first.c_str()) + ": attach failed (hr=0x" + HrHexLocal(attachRc) + ")");
            ok = false;
            continue;
        }
        std::vector<unsigned char> id(20, 0);
        if (getStoreId(service, spaceDisk, id.data()) < 0)
        {
            report.push_back("space " + WideToLocal(spaces[i].first.c_str()) + ": GetStoreId failed");
            ok = false;
            if (detachSpace != nullptr) detachSpace(service, spaceDisk, spaces[i].first.c_str(), 0);
            continue;
        }
        wchar_t path[512] = { 0 };
        const long pathRc = getPartitionPath(service, ToStoreIdValue(id), spaces[i].second.c_str(), path, 512, 1);
        if (pathRc < 0)
        {
            report.push_back("space " + WideToLocal(spaces[i].first.c_str()) + ": GetPartitionPath(" +
                             WideToLocal(spaces[i].second.c_str()) + ") failed " + Hex(pathRc));
            ok = false;
        }
        else
        {
            storeIds.push_back(id);
            volumes.push_back(std::make_pair(spaces[i].first, std::wstring(path)));
            report.push_back("space " + WideToLocal(spaces[i].first.c_str()) + " -> " + WideToLocal(path));
        }
    }
    if (closePool != nullptr) closePool(service, pool, 0);
    if (closeService != nullptr) closeService(service);
    FreeLibrary(module);
    return ok;
}

} // namespace imaging
} // namespace ocid
