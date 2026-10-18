// OCID servicing — SHA-256（自带实现，不依赖 CNG/CryptoAPI）。
//
// 用途：COMPONENTS 台账里几处“数据就是哈希”的字段：
//   · 组件记录 `S256H`   = 组件清单文件的 SHA-256（32 字节二进制）
//   · 目录键 `CanonicalData\Catalogs\<64 hex>` = 目录文件（.cat）的 SHA-256
//   · 部署 `CatalogThumbprint` = 该 .cat 的 SHA-256 的十六进制文本
//   · 版本索引 `f256!` 的值 = "<base64(SHA-256)>!<文件版本>#"
// 都是“对整份文件字节做 SHA-256”，所以这里只提供最基础的两个入口，
// 由调用方决定怎么编码（二进制/hex/base64）。
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace ocid {

// 32 字节摘要。
std::vector<std::uint8_t> Sha256(const std::vector<std::uint8_t>& data);

// 同上的便捷重载（对内存块）。
std::vector<std::uint8_t> Sha256(const void* data, std::size_t size);

// 数据 SHA-256 摘要的小写十六进制（64 个字符）。
std::string Sha256Hex(const std::vector<std::uint8_t>& data);

// 标准 base64（带 '=' 填充）；台账里 f256 的基64 就是它。
std::string Base64Encode(const std::vector<std::uint8_t>& data);

}  // namespace ocid

