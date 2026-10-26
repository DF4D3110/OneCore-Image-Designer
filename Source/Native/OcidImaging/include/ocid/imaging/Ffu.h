// OCID Imaging —— FFU 层（Full Flash Update 容器）。
//
// 这一层只做“FFU 这个东西”本身，不掺造盘/落包：
//   pack     把一份已建好的磁盘（VHD/VHDX 或物理盘）整盘封成 FFU
//   info     读回 FFU 的头/manifest 摘要
//   manifest 把 FFU 里的 manifest 原文抽出来
//
// 【为什么单独一个 DLL】FFU 的坑全在“字节布局”上（ChunkSize 单位是 KB、SecurityPadding
// 对齐、store 元数据连续在前、描述符按 Phase1/2/3 分组），与造盘/落包互不相干。
// 单独成 ocid_ffu.wcos.dll 后，ImgDump 报 “image header is invalid” 时只需要怀疑这一个文件。
//
// 【结构依据】D:\ADK2022Wrokspace\_decomp 下 imagecommon / imagestorageservicemanaged / ImgDump
// 的反编译源码 + 官方 FFU 实测：
//   SecurityHeader { u32 ByteCount(32); char[12] "SignedImage "; u32 ChunkSize(KB); u32 HashAlgorithmID;
//                    u32 CatalogSize; u32 HashTableSize; } + CatalogData + HashTableData + SecurityPadding
//   ImageHeaderEx  { u32 ByteCount(24|28); char[12] "ImageFlash  "; u32 ManifestLength; u32 ChunkSize(KB);
//                    [u32 NumberOfDeviceTargetingIds] } + manifest + 对齐
//   payload = 各 store 元数据（连续、各自补到 BytesPerBlock）+ 各 store 数据块（连续）
//   官方整盘 FFU 只有一个 store（物理盘），池里 space 的内容在 OSPool 分区的块里。
#pragma once

#include <cstdint>
#include <string>
#include <utility>
#include <vector>

#ifdef OCID_FFU_BUILD
#define OCID_FFU_API __declspec(dllexport)
#else
#define OCID_FFU_API __declspec(dllimport)
#endif

namespace ocid
{
namespace imaging
{
    // manifest 里的一个 [Partition] / [StoragePoolPartition] 段。
    struct FfuPartitionSpec
    {
        std::string name;
        std::string primary;                     // 空则等于 name
        std::string type;                        // GPT 类型 GUID，{'...'} 形式
        std::string id;                          // 分区 GUID
        std::string fileSystem;                  // NTFS / FAT / FAT16 / FAT32
        unsigned long long totalSectors = 0;
        unsigned long long usedSectors = 0;
        unsigned long long clusterSize = 0;      // 0 = 不写该键
        unsigned long long byteAlignment = 0;    // 0 = 不写该键
        bool useAllSpace = false;
    };

    // manifest 里的一个 [Store] / [StoragePoolStore] 段。
    struct FfuStoreSpec
    {
        std::string storeId;
        std::string storeType;
        std::string devicePath;
        bool isMainOSStore = false;
        bool isSpace = false;
        bool onlyAllocateDefinedGptEntries = false;
        unsigned long long sectorSize = 512;
        unsigned long long minSectorCount = 0;   // 0 = 由 sizeBytes / sectorSize 推
        unsigned long long sizeBytes = 0;
        std::vector<FfuPartitionSpec> partitions;
    };

    struct FfuPackRequest
    {
        std::string image;                       // .vhd/.vhdx 文件，或已挂好的 \\.\PhysicalDriveN
        std::wstring outPath;
        std::string arch = "arm64";
        std::string osVersion = "10.0.20279.1002";
        std::string description = "ocid_imagegen pack";
        std::string poolName;                    // [StoragePool] Name
        unsigned int stateSeparationLevel = 0;
        unsigned int bytesPerBlock = 128u * 1024u;
        FfuStoreSpec physicalStore;              // [Store]；分区以真实 GPT 为准，这里只带布局提示
        std::vector<FfuStoreSpec> spaces;        // [StoragePoolStore]（池内 space，顺序即 manifest 顺序）
    };

    struct FfuImageInfo
    {
        std::uint64_t fileBytes = 0;
        std::uint32_t securityHeaderBytes = 0;
        std::uint32_t securityChunkKb = 0;
        std::uint32_t hashAlgorithmId = 0;
        std::uint32_t catalogBytes = 0;
        std::uint32_t hashTableBytes = 0;
        std::uint32_t imageHeaderBytes = 0;
        std::uint32_t manifestBytes = 0;
        std::uint32_t imageChunkKb = 0;
        std::uint64_t manifestOffset = 0;
        std::uint64_t payloadOffset = 0;
        std::vector<std::pair<std::string, std::size_t> > sections;   // 段名 → 键个数
    };

    // 整盘封 FFU。report 收进度行（调用方决定打印还是丢弃）。
    OCID_FFU_API bool FfuPack(const FfuPackRequest& request,
                              std::vector<std::string>& report,
                              std::string& error);

    // 读出 manifest 原文。
    OCID_FFU_API bool FfuReadManifest(const std::wstring& path, std::string& manifest, std::string& error);

    // 读头部摘要（顺带把各段键数统计出来）。
    OCID_FFU_API bool FfuReadInfo(const std::wstring& path, FfuImageInfo& info, std::string& error);

    // 校验 payload 的 SHA256 哈希表（未签名的镜像仍然有这张表）。report 里给逐块结论。
    OCID_FFU_API bool FfuVerify(const std::wstring& path, std::vector<std::string>& report, std::string& error);
}
}
