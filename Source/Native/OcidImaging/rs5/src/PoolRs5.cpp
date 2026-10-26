// OcidImaging 的存储池封装 —— RS5（17763 / legacy space API）版本。
//
// 为什么要单列一份：RS5 的 ImageStorageService.dll 与 FE（20348 分支）不是同一套 ABI。
//   * FE  ：68 个导出、cdecl 名字，池内 space 用 AttachStorageSpace / DetachStorageSpace /
//           OpenStoragePool / CloseStoragePool / GetStoragePool / GetStorageSpaces 管理。
//   * RS5 ：82 个导出、**全部 stdcall 装饰名**（_CreateStorageSpace@40 这种），没有上面这些
//           函数；space 走 CreateStorageSpace / GetStorageSpace / SetStorageSpaceName /
//           RemoveStorageSpace / RemoveStoragePool + GetPoolIdFromPoolName。
// 因此 GetProcAddress 必须按装饰名取，且整个调用序列不同（见 STAGES.md §5）。
//
// 当前状态：探测与导出枚举已按 RS5 ABI 实现（可验证工具集是否装对、ADK 是否混用）；
// 真正的建池/挂载/卸载序列尚未实现——这些函数返回明确的“未实现”错误，而不是假装成功，
// 以免上层拿到半成品镜像。
#include "ocid/imaging/Pool.h"

#include "ocid/imaging/Common.h"
#include "ocid/imaging/Stage.h"

#include <windows.h>

#include <cstring>
#include <string>
#include <vector>

namespace ocid {
namespace imaging {
namespace {

// RS5 的导出是 stdcall 装饰名，参数字节数写死在这里。PoolProbe 需要用它验证，
// 后续实现这些函数时也要用它去 GetProcAddress（DllImport 之所以能用裸名，是因为
// 托管侧带了完整的 P/Invoke 元数据；我们裸用 C++ 必须自己拼）。
struct Rs5ExportEntry
{
    const char* base;
    const char* decorated;
    bool required;   // 建池/挂载/落包主链必需
};

const Rs5ExportEntry kRs5Exports[] = {
    {"CreateImageStorageService", "_CreateImageStorageService@16", true},
    {"CloseImageStorageService",  "_CloseImageStorageService@4",  true},
    {"SetLoggingFunction",        "_SetLoggingFunction@12",       false},
    {"CreateStoragePool",         "_CreateStoragePool@16",        true},
    {"AddDriveToStoragePool",     "_AddDriveToStoragePool@24",    true},
    {"CreateStorageSpace",        "_CreateStorageSpace@40",       true},
    {"GetStorageSpace",           "_GetStorageSpace@32",          true},
    {"SetStorageSpaceName",       "_SetStorageSpaceName@24",      false},
    {"RemoveStorageSpace",        "_RemoveStorageSpace@20",       true},
    {"RemoveStoragePool",         "_RemoveStoragePool@20",        true},
    {"GetPoolIdFromPoolName",     "_GetPoolIdFromPoolName@12",    true},
    {"AssignMountPoints",         "_AssignMountPoints@36",        true},
    {"FormatPartition",           "_FormatPartition@36",          true},
    {"FormatPartitions",          "_FormatPartitions@36",         false},
    {"GetPartitionPath",          "_GetPartitionPath@40",         true},
    {"GetPartitionNames",         "_GetPartitionNames@24",        false},
    {"GetPartitionSizeInSectors", "_GetPartitionSizeInSectors@32", false},
    {"GetPartitionFileSystem",    "_GetPartitionFileSystem@36",   false},
    {"GetPartitionType",          "_GetPartitionType@32",         false},
    {"GetPartitionOffset",        "_GetPartitionOffset@32",       false},
    {"SetPartitionType",          "_SetPartitionType@44",         false},
    {"SetPartitionId",            "_SetPartitionId@28",           false},
    {"SetDiskId",                 "_SetDiskId@28",                false},
    {"GetDiskName",               "_GetDiskName@32",              true},
    {"GetDiskSize",               "_GetDiskSize@12",              true},
    {"GetDiskLayout",             "_GetDiskLayout@12",            false},
    {"SafeFreeDiskLayout",        "_SafeFreeDiskLayout@4",        false},
    {"AttachWOFToVolume",         "_AttachWOFToVolume@8",         true},
    {"AttachToMountedImage",      "_AttachToMountedImage@20",     true},
    {"OpenVirtualHardDisk",       "_OpenVirtualHardDisk@20",      true},
    {"CreateVirtualHardDisk",     "_CreateVirtualHardDisk@44",    true},
    {"DismountVirtualHardDisk",   "_DismountVirtualHardDisk@32",  true},
    {"UpdateDiskLayout",          "_UpdateDiskLayout@8",          true},
    {"WriteMountManagerRegistry", "_WriteMountManagerRegistry@24", true},
    {"WriteMountManagerRegistry2", "_WriteMountManagerRegistry2@36", false},
    {"WriteVolumeMountPoints",    "_WriteVolumeMountPoints@20",   false},
    {"WaitForPartition",          "_WaitForPartition@28",         true},
    {"WaitForPartitions",         "_WaitForPartitions@32",        false},
    {"FlushPartition",            "_FlushPartition@12",           false},
    {"ReauctionDisk",             "_ReauctionDisk@8",             false},
    {"IsStoreSpace",              "_IsStoreSpace@28",             false},
    {"GetStoreIdFromPartitionName", "_GetStoreIdFromPartitionName@16", false},
    {"GetPartitionPathNoContext", "_GetPartitionPathNoContext@16", false},
    {"GetPartitionStyle",         "_GetPartitionStyle@12",        false},
    {"GetPartitionAttributes",    "_GetPartitionAttributes@32",   false},
    {"SetPartitionAttributes",    "_SetPartitionAttributes@36",   false},
    {"RemovePartitionAttributes", "_RemovePartitionAttributes@20", false},
    {"NormalizeVolumeMountPoints", "_NormalizeVolumeMountPoints@40", false},
    {"LockAndDismountVolumeByHandle", "_LockAndDismountVolumeByHandle@12", false},
    {"GetDiskNumAndPartitionCount", "_GetDiskNumAndPartitionCount@40", false},
};

const std::size_t kRs5ExportCount = sizeof(kRs5Exports) / sizeof(kRs5Exports[0]);

std::string WideToUtf8Simple(const std::wstring& value)
{
    if (value.empty()) return std::string();
    const int need = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (need <= 0) return std::string();
    std::string out(static_cast<std::size_t>(need - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.c_str(), -1, &out[0], need, nullptr, nullptr);
    return out;
}

std::wstring Utf8ToWideSimple(const std::string& value)
{
    if (value.empty()) return std::wstring();
    const int need = MultiByteToWideChar(CP_UTF8, 0, value.c_str(), -1, nullptr, 0);
    if (need <= 0) return std::wstring();
    std::wstring out(static_cast<std::size_t>(need - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, value.c_str(), -1, &out[0], need);
    return out;
}

// RS5 的 ADK 布局是 <根>\bin\i386（没有 "Windows Kits\10" 这一层）。
void CollectRs5Candidates(const wchar_t* fileName, std::vector<std::wstring>& candidates)
{
    wchar_t buffer[32768] = {0};
    if (GetEnvironmentVariableW(L"OCID_IMAGESTORAGE_DLL", buffer, 32768) > 0)
    {
        candidates.push_back(buffer);
    }
    buffer[0] = 0;
    if (GetEnvironmentVariableW(L"OCID_ADK_ROOT", buffer, 32768) > 0)
    {
        std::wstring root(buffer);
        candidates.push_back(root + L"\\bin\\i386\\" + fileName);
        candidates.push_back(root + L"\\i386\\" + fileName);
        candidates.push_back(root + L"\\Windows Kits\\10\\Tools\\bin\\i386\\" + fileName);
    }
    buffer[0] = 0;
    if (GetModuleFileNameW(nullptr, buffer, 32768) > 0)
    {
        std::wstring exe(buffer);
        const std::size_t slash = exe.find_last_of(L"\\/");
        if (slash != std::wstring::npos)
        {
            const std::wstring dir = exe.substr(0, slash);
            candidates.push_back(dir + L"\\" + fileName);
            candidates.push_back(dir + L"\\..\\" + fileName);
        }
    }
}

// 只在明确"未实现"的地方用：给上层一句能直接对应到文档的错误。
bool NotImplement(const char* what, std::string& error)
{
    error = std::string("RS5 阶段的存储池操作尚未实现：") + what +
            "。已可用的命令：stage / ffu / vhd / disk / devicedb / plan / appx collect；"
            "存储池（pool/apply/image build）需要按 legacy space API 重写，见 OcidImaging/STAGES.md §5。";
    return false;
}

} // namespace

bool PoolProbe(std::string& dllPath,
               std::vector<std::string>& found,
               std::vector<std::string>& missing,
               std::string& error)
{
    std::vector<std::wstring> candidates;
    CollectRs5Candidates(L"ImageStorageService.dll", candidates);
    if (candidates.empty())
    {
        error = "找不到 ImageStorageService.dll 的候选路径（设置 OCID_ADK_ROOT 或 OCID_IMAGESTORAGE_DLL）";
        return false;
    }

    std::vector<std::string> tried;
    for (const std::wstring& candidate : candidates)
    {
        const DWORD attributes = GetFileAttributesW(candidate.c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
        {
            continue;
        }
        HMODULE module = LoadLibraryExW(candidate.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (module == nullptr)
        {
            tried.push_back(WideToUtf8Simple(candidate) + ": LoadLibrary 失败");
            continue;
        }
        found.clear();
        missing.clear();
        for (std::size_t i = 0; i < kRs5ExportCount; ++i)
        {
            const FARPROC address = GetProcAddress(module, kRs5Exports[i].decorated);
            const std::string label = std::string(kRs5Exports[i].base) + " (" + kRs5Exports[i].decorated + ")";
            if (address != nullptr)
            {
                found.push_back(label);
            }
            else if (kRs5Exports[i].required)
            {
                missing.push_back(label);
            }
        }
        if (!missing.empty())
        {
            tried.push_back(WideToUtf8Simple(candidate) + ": 缺 " + std::to_string(missing.size()) + " 个必需导出");
            FreeLibrary(module);
            continue;
        }
        dllPath = WideToUtf8Simple(candidate);
        FreeLibrary(module);
        return true;
    }

    error = "no usable ImageStorageService.dll among " + std::to_string(candidates.size()) +
            " candidate(s)";
    for (const std::string& line : tried)
    {
        error += "\n  " + line;
    }
    error += "\n提示：RS5 的 ADK 工具集在 <根>\\bin\\i386；导出是 stdcall 装饰名（_Xxx@N），"
             "FE 那份 ADK 不满足这些导出。";
    return false;
}

bool PoolExports(std::string& dllPath,
                 std::vector<std::string>& names,
                 std::string& error)
{
    names.clear();
    const std::wstring file = Utf8ToWideSimple(dllPath);
    HMODULE module = LoadLibraryExW(file.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (module == nullptr)
    {
        error = "LoadLibraryExW 失败：" + dllPath;
        return false;
    }
    for (std::size_t i = 0; i < kRs5ExportCount; ++i)
    {
        const FARPROC address = GetProcAddress(module, kRs5Exports[i].decorated);
        if (address != nullptr)
        {
            names.push_back(kRs5Exports[i].decorated);
        }
    }
    FreeLibrary(module);
    return true;
}

bool PoolFindAdkDll(const std::wstring& fileName,
                    const std::string& requiredExport,
                    std::wstring& outPath,
                    std::string& error)
{
    std::vector<std::wstring> candidates;
    CollectRs5Candidates(fileName.c_str(), candidates);
    for (const std::wstring& candidate : candidates)
    {
        const DWORD attributes = GetFileAttributesW(candidate.c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
        {
            continue;
        }
        HMODULE module = LoadLibraryExW(candidate.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (module == nullptr) continue;
        bool ok = requiredExport.empty();
        if (!ok)
        {
            ok = GetProcAddress(module, requiredExport.c_str()) != nullptr;
            if (!ok)
            {
                // RS5 里的 stdcall 导出是装饰名，裸名查不到时按 kRs5Exports 反查。
                for (std::size_t i = 0; i < kRs5ExportCount; ++i)
                {
                    if (requiredExport == kRs5Exports[i].base &&
                        GetProcAddress(module, kRs5Exports[i].decorated) != nullptr)
                    {
                        ok = true;
                        break;
                    }
                }
            }
        }
        if (ok)
        {
            outPath = candidate;
            FreeLibrary(module);
            return true;
        }
        FreeLibrary(module);
    }
    error = "RS5 阶段找不到 " + WideToUtf8Simple(fileName) +
            (requiredExport.empty() ? std::string() : "（要求导出 " + requiredExport + "）");
    return false;
}

bool PoolCreateSandbox(const PoolCreateOptions&, std::string&, std::string& error)
{
    return NotImplement("pool create（建池沙箱）", error);
}

bool PoolBuild(const std::wstring&,
               const std::wstring&,
               unsigned int,
               const std::wstring&,
               const std::vector<PoolSpaceSpec>&,
               std::vector<std::string>&,
               std::string& error)
{
    return NotImplement("pool build（建池 + 建 space）", error);
}

bool PoolPrepareSpaces(const std::wstring&,
                       const std::wstring&,
                       unsigned int,
                       const std::wstring&,
                       const std::vector<SpacePrepareSpec>&,
                       bool,
                       std::vector<std::string>&,
                       std::string& error)
{
    return NotImplement("pool prepare（space 内 GPT + 格式化）", error);
}

bool PoolAttachSpacesForRead(const std::wstring&,
                             const std::wstring&,
                             unsigned int,
                             const std::wstring&,
                             const std::vector<std::wstring>&,
                             std::vector<std::wstring>&,
                             std::vector<std::vector<unsigned char> >&,
                             std::vector<std::string>&,
                             std::string& error)
{
    return NotImplement("pool attach（space 只读挂载）", error);
}

bool PoolClose(const std::wstring&, const std::wstring&, bool, std::vector<std::string>&, std::string& error)
{
    return NotImplement("pool close", error);
}

bool PoolFinalize(const std::wstring&,
                  const std::wstring&,
                  unsigned int,
                  const std::wstring&,
                  const std::vector<SpacePrepareSpec>&,
                  const std::wstring&,
                  bool,
                  std::vector<std::string>&,
                  std::string& error)
{
    return NotImplement("pool finalize", error);
}

bool PoolAttachForUpdate(const std::wstring&,
                         const std::wstring&,
                         unsigned int,
                         const std::wstring&,
                         const std::vector<std::wstring>&,
                         std::vector<std::vector<unsigned char> >&,
                         std::vector<std::string>&,
                         std::string& error)
{
    return NotImplement("pool attach-for-update（落包前挂 space 取 storeId）", error);
}

bool PoolMountSpaces(const std::wstring&, const std::wstring&, unsigned int, const std::wstring&,
                     const std::vector<std::pair<std::wstring, std::wstring> >&,
                     std::vector<std::vector<unsigned char> >&,
                     std::vector<std::pair<std::wstring, std::wstring> >&,
                     std::vector<std::string>&, std::string& error, std::wstring*)
{
    return NotImplement("pool mount（space 挂到宿主卷）", error);
}

bool PoolMountForUpdate(const std::wstring&, const std::wstring&, unsigned int, const std::wstring&,
                        const std::vector<std::wstring>&, const std::wstring&,
                        std::vector<std::vector<unsigned char> >&, std::wstring&,
                        std::vector<std::string>&, std::string& error)
{
    return NotImplement("pool mount-for-update", error);
}

bool PoolDetachSpaces(const std::wstring&, const std::wstring&, unsigned int, const std::wstring&,
                      const std::vector<std::wstring>&, std::vector<std::string>&, std::string& error)
{
    return NotImplement("pool detach（卸载 space）", error);
}

bool PoolVerifySpaces(const std::wstring&, const std::wstring&, unsigned int, const std::wstring&,
                      const std::vector<std::wstring>&, std::vector<std::string>&, std::string& error)
{
    return NotImplement("pool check（space 内分区校验）", error);
}

bool PoolFormatPartitions(const std::wstring&,
                          const std::vector<PhysicalFormatSpec>&,
                          std::vector<std::string>&,
                          std::string& error)
{
    return NotImplement("物理分区格式化", error);
}

} // namespace imaging
} // namespace ocid
