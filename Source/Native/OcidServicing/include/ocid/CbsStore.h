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

// 按真机规则造值名：`f!` + 文件名的 ASCII 小写。
std::string CbsFileValueName(const std::wstring& fileName);
// 按真机规则造值名：`c!` + 部署名。
std::string CbsMarkValueName(const std::string& deploymentName);

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
