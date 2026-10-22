// OcidImaging / Pool 池层（薄封装 ImageStorageService.dll 的建池/建 space）
//
// 为什么单独一层：把“存储空间”这套私有 IOCTL / 服务句柄的调用关在一个 DLL 里，
// 出问题时能立刻分辨是“池没建起来”还是“文件没写进去”。ImageStorageService.dll
// 由 ADK 的 Imaging 工具目录提供（imagestorageservice.dll，server2022 那次量到 403,456 字节）。
//
// 上层（imagegen pool ...）只认这里导出的纯数据接口，不直接碰 Win32 句柄。
#pragma once

#include <string>
#include <vector>

namespace ocid::imaging {

#if defined(OCID_POOL_BUILD)
#define OCID_POOL_API __declspec(dllexport)
#else
#define OCID_POOL_API __declspec(dllimport)
#endif

// 探测 ImageStorageService.dll：返回实际加载到的路径，并列出期望导出的函数里
// 哪些找到了（found）哪些缺失（missing）。只做 LoadLibrary + GetProcAddress，
// 不创建服务句柄、不碰磁盘。
OCID_POOL_API bool PoolProbe(std::string& dllPath,
                             std::vector<std::string>& found,
                             std::vector<std::string>& missing,
                             std::string& error);

// 列出 DLL 导出的全部函数名（按名称排序）。同样只做 LoadLibrary，不碰磁盘。
OCID_POOL_API bool PoolExports(std::string& dllPath,
                              std::vector<std::string>& names,
                              std::string& error);

// 沙箱建池：只在调用方给的虚拟磁盘设备上执行（调用方负责先过 VhdEnsureVirtualDisk）。
// 流程 = CreateStorageService → CreateStoragePool →（可选）CreateStorageSpace → CloseStoragePool。
// 不做格式化、不建分区表，目的是先验证服务句柄能开、池能落盘。
struct PoolCreateOptions
{
    std::wstring devicePath;      // \\.\PhysicalDriveN
    std::wstring poolName;        // 默认 OSPool
    unsigned int version;         // 24 = Windows 19H1，26 = Manganese
    std::wstring directory;       // 临时目录（MS 传 TempDirectory）
    std::wstring spaceName;       // 空 = 不建 space
    unsigned long long spaceSize; // 字节；0 = 不建 space
};

OCID_POOL_API bool PoolCreateSandbox(const PoolCreateOptions& options,
                                    std::string& detail,
                                    std::string& error);


// 布局驱动的建池：调用方先写好物理 GPT（只含非池分区），这里负责
// CreateStoragePool（服务会自己追加 OSPool 分区并写 SPACEDB）再逐 store 建 space。
// space 名取布局里的 <StoreType>（真机 SDBC 里的 space 名就是 MainOSDisk 这类）。
struct PoolSpaceSpec
{
    std::wstring name;
    unsigned long long sizeBytes = 0;
    unsigned int sectorSize = 512;
    bool thin = true;
    unsigned int usage = 2;   // 2 = Data
};

OCID_POOL_API bool PoolBuild(const std::wstring& devicePath,
                             const std::wstring& poolName,
                             unsigned int version,
                             const std::wstring& directory,
                             const std::vector<PoolSpaceSpec>& spaces,
                             std::vector<std::string>& created,
                             std::string& error);

// 池内“space 里的分区表 + 格式化”：真机镜像建完池后，每个 space 还要再分一次区
// （space 自己是一块磁盘），并按布局里的 FileSystem/ClusterSize 格式化。
// 参照证据：20279 QEMU-Stage 成品里 EFIESPDisk 的 EFIESP 分区在 2 MiB、MainOSDisk 的
// MainOS 分区在 8 MiB（布局的 DefaultPartitionByteAlignment=0x200000、该分区
// ByteAlignment=0x800000），分区类型 GUID 与布局 <Type> 完全一致。
struct SpacePartitionSpec
{
    std::wstring name;
    std::wstring typeGuid;       // 布局 <Type>，{'57434f53-...'} 形式
    std::wstring partitionGuid;  // 布局 <Id>，空则由写 GPT 的实现自动生成
    unsigned long long offsetBytes = 0;
    unsigned long long sizeBytes = 0;
    std::wstring fileSystem;     // NTFS / FAT / FAT16 / FAT32；空或 RAW = 不格式化
    unsigned int clusterSize = 0; // 0 = 由服务决定
};

struct SpacePrepareSpec
{
    std::wstring name;           // space 名（布局里的 <StoreType>）
    unsigned int sectorSize = 512;
    std::vector<SpacePartitionSpec> partitions;
};

// 打开已有池（OpenStoragePool）→ 逐个 space 挂上（AttachStorageSpace）→ 在 space 内写 GPT
// → 按需 FormatPartition → 卸下（DetachStorageSpace）→ 关池。
// 调用方负责先把虚拟磁盘挂上并过 VhdEnsureVirtualDisk。

// 关闭池：OpenVirtualHardDisk -> OpenStoragePool -> CloseStoragePool。detachSpaces=true 时
// 顺带把 space 虚拟盘卸下（用于清场；成品镜像不需要这一步）。
OCID_POOL_API bool PoolClose(const std::wstring& imagePath,
                             const std::wstring& directory,
                             bool detachSpaces,
                             std::vector<std::string>& report,
                             std::string& error);

OCID_POOL_API bool PoolPrepareSpaces(const std::wstring& devicePath,
                                     const std::wstring& poolName,
                                     unsigned int version,
                                     const std::wstring& directory,
                                     const std::vector<SpacePrepareSpec>& spaces,
                                     bool formatPartitions,
                                     std::vector<std::string>& report,
                                     std::string& error);

} // namespace ocid::imaging
