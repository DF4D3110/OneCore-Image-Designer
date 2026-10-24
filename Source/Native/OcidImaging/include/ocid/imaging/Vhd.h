// OCID Imaging —— 虚拟磁盘（VHDX）层。
//
// 这一层只做“把一个 .vhdx 建出来、挂上去、拿到它的物理盘路径、写分区表、卸下来”。
// 它对应 imageapp 里 ImageStorageService 的 CreateVirtualHardDisk / AttachVirtualHardDisk /
// PartitionVirtualHardDisk 这一小簇能力（那一簇本身走的是公开的 VirtDisk.dll）。
//
// 【为什么不用 imagestorageservice.dll】真 Storage Spaces 的池是 image core 私有的 SDB 栈
// （私有 IOCTL 0x7C0F4 + 盘上元数据），见 D:\ADK2022Wrokspace\IMAGING-VHD-STUDY.md；
// 这里先实现“单 VHDX + GPT 分区”的 B 方案，行为可逐项核对。
#pragma once

#include <cstdint>
#include <string>
#include <vector>

#ifdef OCID_VHD_BUILD
#define    OCID_VHD_API __declspec(dllexport)
#else
#define    OCID_VHD_API __declspec(dllimport)
#endif

namespace ocid
{
namespace imaging
{
    enum class VhdKind
    {
        Dynamic,
        Fixed,
    };

    // 磁盘文件格式：VHDX 支持 512/4096 扇区；VHD 只支持 512（微软工具 ImgDump 只认 VHD/FFU）。
    enum class VhdFormat
    {
        Vhdx,
        Vhd,
    };

    struct VhdInfo
    {
        std::string path;          // UTF-8 文件路径
        std::uint64_t fileBytes = 0;
        std::uint64_t virtualBytes = 0;
        std::uint32_t blockSize = 0;
        std::uint32_t physicalSectorSize = 0;
        bool attached = false;
        std::string devicePath;    // 已挂载时形如 \\.\PhysicalDriveN
        std::string diskId;        // GPT 磁盘 GUID（若已分区）
        std::uint32_t partitionCount = 0;
    };

    // 一个 GPT 分区（与设备布局里的 <Partition> 一一对应）。
    struct VhdPartition
    {
        std::string name;          // GPT 分区名（最长 36 个 UTF-16 字符）
        std::string type;          // {'57434f53-...'} 形式的 GUID 文本
        std::string id;            // 分区 GUID 文本；空则自动生成
        std::uint64_t offsetBytes = 0;
        std::uint64_t sizeBytes = 0;
        std::uint64_t attributes = 0;
        unsigned int mbrType = 0;   // MBR 分区类型字节（v1 布局的 0xNN）
        bool bootable = false;      // MBR 活动分区（0x80）
    };

    OCID_VHD_API std::string VhdVersion();

    // 建一个 VHDX。virtualBytes 必须是 MB 的整数倍（VirtDisk 的硬要求）。
    OCID_VHD_API bool VhdCreate(const std::wstring& path,
                                std::uint64_t virtualBytes,
                                VhdKind kind,
                                std::uint32_t blockSize,
                                std::uint32_t physicalSectorSize,
                                std::string& error,
                                VhdFormat format = VhdFormat::Vhdx);

    // 挂载（读写），成功后 devicePath 形如 \\.\PhysicalDriveN。
    // 进程退出时释放所有非永久挂载（wmain 收尾调用）。
    OCID_VHD_API void VhdReleaseAll();

    // permanent=false（默认）：挂载生命周期绑定本进程，进程退出（含被杀）时由系统自动卸载。
    // permanent=true：跨进程挂载（vhd attach/detach 两条独立命令）时才用。
    OCID_VHD_API bool VhdAttach(const std::wstring& path,
                                bool readOnly,
                                std::string& devicePath,
                                std::string& error,
                                bool permanent = false);

    OCID_VHD_API bool VhdDetach(const std::wstring& path, std::string& error);

    // 查询：不挂载也能拿到虚拟容量/块大小；已挂载时补 devicePath。
    OCID_VHD_API bool VhdQuery(const std::wstring& path, VhdInfo& info, std::string& error);

    // 写 GPT：清掉已有分区表，按 parts 顺序建（offset/size 用字节，内部换算成扇区）。
    OCID_VHD_API bool VhdEnsureVirtualDisk(const std::string& devicePath, std::string& error);

    OCID_VHD_API bool VhdWriteGpt(const std::string& devicePath,
                                  const std::string& diskId,
                                  const std::vector<VhdPartition>& parts,
                                  std::uint32_t sectorSize,
                                  std::string& error);

    // 读回磁盘上的 GPT（用于验收：分区数、每段的 GUID/偏移/大小）。
    OCID_VHD_API bool VhdSetPartitionType(const std::string& devicePath,
                                     const std::string& partitionName,
                                     const std::string& typeGuid,
                                     std::string& error);
    OCID_VHD_API bool VhdReadGpt(const std::string& devicePath,
                                 std::string& diskId,
                                 std::vector<VhdPartition>& parts,
                                 std::uint32_t& sectorSize,
                                 std::string& error);

    // 裸按字节读写设备：固件分区灌镜像 / 回读校验用（不做文件系统解释）。
    OCID_VHD_API bool VhdWriteBytes(const std::string& devicePath,
                                    std::uint64_t offset,
                                    const void* data,
                                    std::size_t size,
                                    std::string& error);

    // 写/读 MBR（v1 平铺布局：BIOS 引导，分区类型 0xNN）。parts 最多 4 项，偏移/大小必须是扇区整数倍。
    OCID_VHD_API bool VhdWriteMbr(const std::string& devicePath,
                                  const std::vector<VhdPartition>& parts,
                                  std::uint32_t sectorSize,
                                  std::string& error);

    OCID_VHD_API bool VhdReadMbr(const std::string& devicePath,
                                 std::vector<VhdPartition>& parts,
                                 std::uint32_t sectorSize,
                                 std::string& error);

    OCID_VHD_API bool VhdReadBytes(const std::string& devicePath,
                                   std::uint64_t offset,
                                   void* data,
                                   std::size_t size,
                                   std::string& error);
    // ---- 挂载状态与强制卸载 ----

    // 判断镜像是否处于挂载/被占用状态（按文件独占打开探测）。
    OCID_VHD_API bool VhdIsAttached(const std::wstring& imagePath, bool& attached, std::string& error);

    // 查询占用该镜像的进程（Restart Manager）。
    OCID_VHD_API bool VhdHolders(const std::wstring& imagePath, std::vector<unsigned long>& pids, std::string& error);

    // 强制卸载：多次重试 + 可选结束占用进程。
    OCID_VHD_API bool VhdForceDetach(const std::wstring& imagePath, bool killHolders,
                                     std::vector<std::string>& report, std::string& error);

}
}
