// OCID Imaging / Cab —— Microsoft Cabinet (.cab) 读取
//
// 【为什么需要】离线装包/建库的每一步都要读包内容：.cab 里有 .mum（CBS 清单）、
// 载荷文件、注册表项、以及 DeviceDB/UpdateInput/PackageList 需要的元数据。
// 自己解析 CAB 才能做到"完全自带"，不依赖 ADK 的 cabapi/expand.exe。
//
// 【支持范围】
//   压缩：NONE、MSZIP（Deflate）。LZX/Quantum 暂不支持 —— 遇到时给出明确报错，
//         而不是解出错数据（老代际包集里可能遇到，届时再补）。
//   目录：单 cabinet（不支持 spanning 多卷 / CFDATA 跨卷）。
//
// 【API 约定】所有路径都是宽字符；条目名是 CAB 里的原始 ASCII 名。
// 错误信息一律中文、可读（带文件名与原因）。输出走调用方，不由本层打印。
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace ocid
{
namespace imaging
{

#if defined(OCID_CAB_BUILD)
#define OCID_CAB_API __declspec(dllexport)
#else
#define OCID_CAB_API __declspec(dllimport)
#endif

enum class CabCompression
{
    None,
    MSZip,
    LZX,
    Quantum,
    Unknown,
};

OCID_CAB_API const char* CabCompressionName(CabCompression compression);

struct CabEntry
{
    std::string name;        // CAB 内路径（原样，反斜杠分隔）
    std::uint32_t size = 0;  // 未压缩大小
    std::uint32_t offset = 0; // 在所属 folder 内的偏移
    std::uint16_t folder = 0; // folder 下标
    std::uint16_t date = 0;   // MS-DOS 日期
    std::uint16_t time = 0;   // MS-DOS 时间
    std::uint16_t attribs = 0;
};

struct CabInfo
{
    std::uint32_t bytes = 0;          // 整个 cabinet 的大小
    std::uint32_t folderCount = 0;    // CFFOLDER 数
    std::uint32_t blockCount = 0;     // CFDATA 块数
    std::uint64_t totalSize = 0;      // 所有条目未压缩大小之和
    CabCompression compression = CabCompression::Unknown;
    std::vector<CabEntry> files;
};

// 读目录（不展开内容）。
OCID_CAB_API bool CabList(const std::wstring& path, CabInfo& info, std::string& error);

// 解出一个条目到内存。
OCID_CAB_API bool CabExtractEntry(const std::wstring& path, const std::string& entryName,
                                  std::vector<std::uint8_t>& out, std::string& error);

// 解出一个条目并当文本返回（.mum/.xml 用）。
OCID_CAB_API bool CabExtractText(const std::wstring& path, const std::string& entryName, std::string& text,
                                 std::string& error);

// 全解到目录（保留 CAB 内相对路径；只创建文件，不建空目录）。
OCID_CAB_API bool CabExtractAll(const std::wstring& path, const std::wstring& targetDir,
                                std::vector<std::string>& written, std::string& error);

} // namespace imaging
} // namespace ocid
