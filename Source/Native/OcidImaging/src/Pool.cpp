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

} // namespace ocid::imaging
