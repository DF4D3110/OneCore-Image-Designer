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
typedef long(__stdcall* AssignMountPointsFn)(void*, void*, unsigned char*, unsigned int, int);
typedef long(__stdcall* GetPartitionPathFn)(void*, const unsigned char*, const wchar_t*, wchar_t*, unsigned int, int);

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
        report.push_back("space " + WideToLocal(name.c_str()) + ": storeId captured");
    }

    std::vector<unsigned char> flat;
    flat.reserve(storeIds.size() * 20);
    for (const std::vector<unsigned char>& one : storeIds) flat.insert(flat.end(), one.begin(), one.end());

    void* volumeDisk = baseDisk;
    if (volumeDisk == nullptr)
    {
        volumeDisk = CreateFileW(diskName, GENERIC_READ | GENERIC_WRITE,
                                 FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr);
        if (volumeDisk == INVALID_HANDLE_VALUE) volumeDisk = nullptr;
    }
    if (volumeDisk == nullptr)
    {
        error = "open base disk handle for AssignMountPoints failed.";
        closePool(service, pool, 0);
        closeService(service);
        FreeLibrary(module);
        return false;
    }

    OutLn("[mount] AssignMountPoints ...");
    rc = assignMountPoints(service, volumeDisk, &flat[0], static_cast<unsigned int>(storeIds.size()), 0);
    if (rc < 0)
    {
        error = "AssignMountPoints failed (" + Hex(rc) + ").";
        if (baseDisk == nullptr) CloseHandle(volumeDisk);
        closePool(service, pool, 0);
        closeService(service);
        FreeLibrary(module);
        return false;
    }
    report.push_back("mount points assigned");

    // MainOS 是 space 里的分区名；逐个 store 试，找到能解析出路径的即可。
    wchar_t pathBuffer[512] = { 0 };
    for (std::size_t i = 1; i < storeIds.size() && mainOsPath.empty(); ++i)
    {
        const std::vector<unsigned char>& id = storeIds[i];
        OutLn("[mount] GetPartitionPath MainOS ...");
        rc = getPartitionPath(service, &id[0], L"MainOS", pathBuffer, 512, 1);
        if (rc >= 0 && pathBuffer[0] != L'\0')
        {
            mainOsPath.assign(pathBuffer);
            report.push_back("MainOS path: " + WideToLocal(pathBuffer));
        }
    }

    if (baseDisk == nullptr && volumeDisk != nullptr) CloseHandle(volumeDisk);
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
} // namespace imaging
} // namespace ocid
