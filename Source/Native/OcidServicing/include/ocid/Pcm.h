// COMPONENTS 台账之外的第二个二进制容器：WinSxS\ManifestCache\*_blobs.bin。
//
// 结构（8061 真机样本逐字节验证，见 E:\ocid_verify\pcm_spec.md）：
//   _blobs.bin = 若干"独立 LZNT1 压缩块"的顺序拼接，每块 = 28 字节包装头 + 压缩流；
//   块解压后是 `PcmH` 流：84 字节头 + 头部尾部表 + 8 字节/项 TOC + 载荷区。
//
// 本模块只负责**已解压的 `PcmH` 流**的容器层（重编码 + 头部哈希）。
// 压缩层（LZNT1）与"从清单 XML 生成各表载荷"不在本模块内。
#ifndef OCID_PCM_H
#define OCID_PCM_H

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace ocid {

// `RtlHashLBlob`（wcp 内部，不是 4 路 65599 的 RtlHashEncodedLBlob）：
// 单路 64 位 fold，h = h * 0x1FFFFFFF7 + word（小端 4 字节一组，尾部按单字节），
// 结果 mod 2^64。PCM 头部 +0x0C/+0x10 存的就是它的"低 32 | 高 32"。
std::uint64_t PcmFold64(const std::uint8_t* data, std::size_t size);

// 一张表的载荷（TOC 里的一个项）；type 占 TOC 项的高 8 位。
struct PcmTable {
    std::uint8_t type = 0;
    std::vector<std::uint8_t> payload;
};

// 解析 `PcmH` 流头：表数量（+0x14）与 TOC 偏移（+0x18）。
bool PcmReadHeader(const std::vector<std::uint8_t>& pcm, std::uint32_t& tableCount,
                   std::uint32_t& tocOffset, std::string& error);

// 按真机语义重编码 `PcmH`：载荷按"entry 哈希表桶迭代序"排放（不是 TOC 序），
// 每段载荷 4 字节对齐，最后写 TOC，并把头部 +0x0C..0x13 清零后用 PcmFold64
// 算整块哈希写回低/高 32 位。
//
// headerPrefix = 原流的前 tocOffset 个字节（含 magic/常量/GUID/19 个 well-known
// key 的 blob id），容器层不解释它的内容，只保证原样保留。
std::vector<std::uint8_t> PcmEncode(const std::vector<std::uint8_t>& headerPrefix,
                                    const std::vector<PcmTable>& tables, std::string& error);

}  // namespace ocid

#endif  // OCID_PCM_H
