// OCID servicing — COMPONENTS 台账（CBS store）记录编解码。
//
// 这里只做“字节语义”，不碰注册表：调用方（C# 侧）把 hive 里的值按
// REG_BINARY 原样取出来，交给这些函数解码 / 编码。所有结论都对着真机
// COMPONENTS hive（8061 x86 真机 20,971,520 B）实测过：
//
//   根：StoreFormatVersion = UTF-16 文本 "0.0.0.6"（14 字节，不带结尾 NUL）
//       StoreArchitecture = 4 字节二进制（x86=0、ARM=5）
//       ExecutionState    = REG_DWORD
//       PendingXmlIdentifier = UTF-16 文本（32 个 hex 字符）
//
//   组件 DerivedData\Components\<名>：
//       S256H    = 32 字节清单 SHA-256
//       identity = ASCII 身份文本（"Name, Key=Value, ..."）
//       c!<部署> = 长度恒 0 的 REG_BINARY（只是一种“标记”）
//       f!<文件> = UTF-16LE 文件名，不带结尾 NUL
//       CF       = REG_DWORD 配置标志
//
//   部署 CanonicalData\Deployments\<名>：
//       appid             = ASCII 身份文本
//       CatalogThumbprint = REG_SZ（64 个 hex 字符）
//       p!/s!/i!<包身份>  = 8 字节小端长度前缀 + ASCII 包身份
//
// 本模块刻意不做“组件名后缀（16hex）”的计算：那属于 identity authority，
// 尚未从反编译里解出；读侧按目录名原样保留，写侧由调用方给名字。
#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <utility>
#include <vector>

namespace ocid {

// 台账里的一个值：名字 + REG_BINARY 原始字节。
struct CbsValue {
    std::string name;
    std::vector<std::uint8_t> data;
};

// ---- 基础编码 ----

// 文本一律是“REG_BINARY 里的裸字节”，不带结尾 NUL。
std::vector<std::uint8_t> CbsEncodeAsciiText(const std::string& text);
bool CbsDecodeAsciiText(const std::vector<std::uint8_t>& bytes, std::string& text);

std::vector<std::uint8_t> CbsEncodeUtf16Text(const std::wstring& text);
bool CbsDecodeUtf16Text(const std::vector<std::uint8_t>& bytes,
                        std::wstring& text,
                        std::string& error);

std::vector<std::uint8_t> CbsEncodeDword(std::uint32_t value);
bool CbsDecodeDword(const std::vector<std::uint8_t>& bytes,
                    std::uint32_t& value,
                    std::string& error);

// p!/s!/i! 的载荷：8 字节小端长度前缀 + ASCII 文本。
std::vector<std::uint8_t> CbsEncodeLengthPrefixedAscii(const std::string& text);
bool CbsDecodeLengthPrefixedAscii(const std::vector<std::uint8_t>& bytes,
                                  std::string& text,
                                  std::string& error);

// ---- 身份文本 ----

// "Accessibility, Culture=neutral, Version=4.0.8061.0, PublicKeyToken=..., ..."
struct CbsIdentity {
    std::string name;
    std::vector<std::pair<std::string, std::string>> attributes;
};

bool CbsParseIdentity(const std::string& text,
                      CbsIdentity& identity,
                      std::string& error);
std::string CbsFormatIdentity(const CbsIdentity& identity);

// ---- 组件记录 ----

// 真机上的值名不是能由数据重算出来的：`f!` 后面是**文件名的小写**，而值数据
// 是原始大小写的 UTF-16（例：值名 `f!accessibility.dll`、数据 `Accessibility.dll`；
// 3,000 个采样里 1,468 个大小写不同）。所以记录里同时保留“完整值名”和“解码后的
// 数据”，往返读写才无损；写新记录时用 CbsFileValueName() 按规则造名字。

struct CbsMark {
    std::string valueName;       // 完整值名："c!<部署名>"
    std::string deploymentName;  // 去掉 "c!" 前缀的部署名
};

struct CbsFile {
    std::string valueName;       // 完整值名："f!<文件名小写>"
    std::wstring fileName;       // 值数据（UTF-16LE，无结尾 NUL）
};

struct CbsComponentRecord {
    std::wstring name;                          // 组件名（= WinSxS 目录名）
    std::vector<std::uint8_t> manifestSha256;   // S256H
    std::string identity;                       // identity（ASCII）
    std::vector<CbsMark> deploymentMarks;       // c!<部署名>，值恒为空
    std::vector<CbsFile> files;                 // f!<文件名小写> -> 真实文件名
    bool hasConfigFlags = false;
    std::uint32_t configFlags = 0;              // CF
    std::vector<CbsValue> extraValues;          // 未解释的值，原样保留
};

// 身份文本哈希（wcp.dll 的 RtlHashEncodedLBlob）：4 路 65599 累加（ASCII A-Z 先
// 小写），再按 0x1E5FFFFFD27 / 0xFFFFFFDC00000051 / 0x1FFFFFFF7 / 1 合成 64 位。
// 真机校验：`Microsoft-Windows-Common-Drivers-Package-drivers~31bf3856ad364e35~x86~
// en-US~6.2.8061.0.INF_1394` => 0xc1a886620aabcbe2（正是 p! 值名后缀）。
std::uint64_t CbsHashEncodedText(const std::string& text);

// 按真机规则造值名（4,892 条 p!/s!/i! 全命中）：
//   `<前缀>CBS_<身份文本 ASCII 小写，截断到 64 字符>_<16 个 hex 的 CbsHashEncodedText>`
// 例：p!CBS_microsoft-windows-common-drivers-package-drivers~31bf3856ad3_c1a886620aabcbe2
std::string CbsPayloadValueName(const std::string& prefix, const std::string& identity);

// 按真机规则造值名：文件名（ASCII 小写）不超过 25 字符时是 `f!<小写文件名>`；更长时
// 是 `f!<前 25 个字符>_<16 个 hex 的哈希>`。哈希的输入始终是**原始大小写**的文件名，
// 例：`PrintAdvancedInstaller.dll` => f!printadvancedinstaller.dl_b7ff1ded0fdd6573。
std::string CbsFileValueName(const std::wstring& fileName);
// 按真机规则造值名：`c!` + 部署名。
std::string CbsMarkValueName(const std::string& deploymentName);

// 组件/部署键尾部 16 个 hex 的伪键（wcp 对身份的折叠哈希）。真机 13,325 条组件键
// 100% 命中。折叠顺序与 wcp 的 canonical 属性顺序一致：
//   name、culture（值为 neutral 时整条跳过）、Type、version、PublicKeyToken、
//   processorArchitecture、versionScope；只有身份里出现的属性参与折叠：
//     acc = acc * 0x1FFFFFFF7 + (H(值) + 0x1FFFFFFF7 * H(属性名))
// 属性名取 canonical 拼写（大小写不影响，哈希会先小写）。
std::uint64_t CbsPseudoKeySuffix(const CbsIdentity& identity);

// 组件的键名（DerivedData\Components 下）：
//   `<架构>_<名字>_<token>_<版本>_<culture>_<16 个 hex 的伪键>`。每段先小写、只保留
//   `[a-z0-9._-]`（真机把空格与括号删掉）；名字超过 40 字符时保留前 19 + `..` + 后 19，
//   culture 超过 8 字符时保留前 3 + `..` + 后 3（`sr-latn-cs` => `sr-..-cs`），
//   culture 为 neutral 或缺省时写 `none`。真机 13,325 条键 100% 可复原。
std::string CbsComponentKeyName(const CbsIdentity& identity);

// 组件家庭的键名（DerivedData\VersionedIndex\<build>\ComponentFamilies 下）：
//   `<架构>_<名字>_<token>_<culture>_<16 个 hex 的伪键>` —— 与组件键同构但**没有
//   version 段**，且尾部伪键是"删掉 version 属性后"的身份折叠。真机 13,159 条
//   家庭键（8061 x86）100% 可复原。
std::string CbsFamilyKeyName(const CbsIdentity& identity);

// 部署的键名（CanonicalData\Deployments 下）：
//   `<名字>_<token>_<版本>_<16 个 hex 的伪键>`，没有架构/culture 段；名字同样先小写、
//   只保留 `[a-z0-9._-]`，超过 24 字符时保留前 11 + `..` + 后 11（真机 2,283 条键
//   100% 可复原）。
std::string CbsDeploymentKeyName(const CbsIdentity& identity);

// ---- ManifestCache（DerivedData\VersionedIndex 的 MCP 指针）----
//
// 一个 ManifestCache 文件（`<WinSxS>\ManifestCache\<16hex>_blobs.bin`）是若干
// "独立 LZNT1 压缩块"的顺序拼接：每块 = 28 字节包装头（+0x14 解压后大小、
// +0x18 算法号 2 = LZNT1、+0x1C 起压缩流）+ 压缩数据；解压后是 `PcmH` 容器。
//
// 文件名里的 16 hex = H39（CbsHashEncodedText）作用于**构建栈版本文本**：
//   H39("6.2.8061.0 (fbl_core1_soc.110803-1830)") = 0x84ccf215ad542233
//   H39("6.2.7992.0 (fbl_fun_lad.110407-1318)")   = 0x5fae0986fe7f7995
// 两例都是 8061 ARM 镜像 WinSxS\ManifestCache 下真实存在的文件名。
std::string CbsManifestCacheFileName(const std::string& stackVersionText);

// `MCP_<8hex>`：每个版本节点的块指针值名。8 hex 来自该 build 桶的
// `ManifestCacheIdentifier`（REG_BINARY 4 字节，小端；ARM 8061 桶实测 0x2E152B8E
// ⇒ `MCP_2e152b8e`）。该 id 的来源不可由清单推导，按输入参数处理。
std::string CbsManifestCachePointerName(std::uint32_t identifier);

// `UtilizedSpace_MCP_<8hex>`：该桶已用字节数（8 字节小端，= 各块长度之和）。
std::string CbsManifestCacheUtilizedSpaceName(std::uint32_t identifier);

// 失败时返回 false 并给出可读原因；成功时 extraValues 收下所有不认识的值。
bool CbsDecodeComponentRecord(const std::wstring& name,
                              const std::vector<CbsValue>& values,
                              CbsComponentRecord& record,
                              std::string& error);
std::vector<CbsValue> CbsEncodeComponentRecord(const CbsComponentRecord& record);

// ---- 部署记录 ----

// `p!/s!/i!` 的值名同样带派生后缀（例
// `p!CBS_microsoft-windows-common-drivers-package-drivers~31bf3856ad3_c1a886620aabcbe2`），
// 后缀规则尚未解出 ⇒ 完整值名原样保留。
struct CbsPayload {
    std::string valueName;       // 完整值名："p!/s!/i!<派生名>"
    std::string prefix;          // "p!" / "s!" / "i!"
    std::string identity;        // 值数据：8 字节长度前缀 + ASCII 身份
};

struct CbsDeploymentRecord {
    std::wstring name;                                   // 部署名
    std::string appId;                                   // appid（ASCII）
    bool hasCatalogThumbprint = false;
    std::string catalogThumbprint;                        // CatalogThumbprint
    std::vector<CbsPayload> payloads;
    std::vector<CbsValue> extraValues;
};

bool CbsDecodeDeploymentRecord(const std::wstring& name,
                               const std::vector<CbsValue>& values,
                               CbsDeploymentRecord& record,
                               std::string& error);
std::vector<CbsValue> CbsEncodeDeploymentRecord(const CbsDeploymentRecord& record);

// ---- 台账头 ----

struct CbsStoreHeader {
    std::wstring formatVersion;      // "0.0.0.6"
    bool hasArchitecture = false;
    std::uint32_t architecture = 0;  // x86=0、ARM=5、amd64=9
    bool hasExecutionState = false;
    std::uint32_t executionState = 0;
    bool hasPendingXmlIdentifier = false;
    std::wstring pendingXmlIdentifier;   // 32 个 hex 字符
};

bool CbsDecodeStoreHeader(const std::vector<CbsValue>& rootValues,
                          CbsStoreHeader& header,
                          std::string& error);
std::vector<CbsValue> CbsEncodeStoreHeader(const CbsStoreHeader& header);

// 组件名后缀（16 个 hex）是否合法：只做形态检查，不重算。
bool CbsIsComponentName(const std::wstring& name);

}  // namespace ocid
