#include "ocid/CbsStore.h"
#include "ocid/ConsoleUtf8.h"

#include <algorithm>
#include <cstring>
#include <sstream>

namespace ocid {
namespace {

bool ReadUtf16(const std::vector<std::uint8_t>& bytes,
               std::wstring& text,
               std::string& error) {
    if (bytes.size() % 2 != 0) {
        std::ostringstream os;
        os << "UTF-16 文本长度必须是偶数，实际 " << bytes.size() << " 字节";
        error = os.str();
        return false;
    }
    std::wstring out;
    out.reserve(bytes.size() / 2);
    for (std::size_t i = 0; i < bytes.size(); i += 2) {
        const unsigned lo = bytes[i];
        const unsigned hi = bytes[i + 1];
        out.push_back(static_cast<wchar_t>(lo | (hi << 8)));
    }
    text.swap(out);
    return true;
}

std::string Trim(const std::string& text) {
    std::size_t begin = 0;
    std::size_t end = text.size();
    while (begin < end && (text[begin] == ' ' || text[begin] == '\t')) {
        ++begin;
    }
    while (end > begin && (text[end - 1] == ' ' || text[end - 1] == '\t')) {
        --end;
    }
    return text.substr(begin, end - begin);
}

bool StartsWith(const std::string& text, const char* prefix) {
    const std::size_t n = std::strlen(prefix);
    return text.size() >= n && std::equal(prefix, prefix + n, text.begin());
}

}  // namespace

std::vector<std::uint8_t> CbsEncodeAsciiText(const std::string& text) {
    return std::vector<std::uint8_t>(text.begin(), text.end());
}

bool CbsDecodeAsciiText(const std::vector<std::uint8_t>& bytes, std::string& text) {
    std::string out;
    out.reserve(bytes.size());
    for (std::uint8_t b : bytes) {
        if (b == 0) {
            // 台账里的文本不带结尾 NUL；出现 NUL 说明这不是我们要的那种值。
            return false;
        }
        out.push_back(static_cast<char>(b));
    }
    text.swap(out);
    return true;
}

std::vector<std::uint8_t> CbsEncodeUtf16Text(const std::wstring& text) {
    std::vector<std::uint8_t> out;
    out.resize(text.size() * 2);
    for (std::size_t i = 0; i < text.size(); ++i) {
        const std::uint16_t unit = static_cast<std::uint16_t>(text[i]);
        out[i * 2] = static_cast<std::uint8_t>(unit & 0xFF);
        out[i * 2 + 1] = static_cast<std::uint8_t>(unit >> 8);
    }
    return out;
}

bool CbsDecodeUtf16Text(const std::vector<std::uint8_t>& bytes,
                        std::wstring& text,
                        std::string& error) {
    return ReadUtf16(bytes, text, error);
}

std::vector<std::uint8_t> CbsEncodeDword(std::uint32_t value) {
    return std::vector<std::uint8_t>{
        static_cast<std::uint8_t>(value & 0xFF),
        static_cast<std::uint8_t>((value >> 8) & 0xFF),
        static_cast<std::uint8_t>((value >> 16) & 0xFF),
        static_cast<std::uint8_t>((value >> 24) & 0xFF)};
}

bool CbsDecodeDword(const std::vector<std::uint8_t>& bytes,
                    std::uint32_t& value,
                    std::string& error) {
    if (bytes.size() != 4) {
        std::ostringstream os;
        os << "DWORD 值必须是 4 字节，实际 " << bytes.size() << " 字节";
        error = os.str();
        return false;
    }
    value = static_cast<std::uint32_t>(bytes[0]) |
            (static_cast<std::uint32_t>(bytes[1]) << 8) |
            (static_cast<std::uint32_t>(bytes[2]) << 16) |
            (static_cast<std::uint32_t>(bytes[3]) << 24);
    return true;
}

std::vector<std::uint8_t> CbsEncodeLengthPrefixedAscii(const std::string& text) {
    // 真机前缀是 8 字节：u32 长度 + u32 0（不是 4 字节长度）。
    std::vector<std::uint8_t> out = CbsEncodeDword(static_cast<std::uint32_t>(text.size()));
    out.push_back(0);
    out.push_back(0);
    out.push_back(0);
    out.push_back(0);
    out.insert(out.end(), text.begin(), text.end());
    return out;
}

bool CbsDecodeLengthPrefixedAscii(const std::vector<std::uint8_t>& bytes,
                                  std::string& text,
                                  std::string& error) {
    if (bytes.size() < 8) {
        std::ostringstream os;
        os << "载荷至少要有 8 字节长度前缀，实际 " << bytes.size() << " 字节";
        error = os.str();
        return false;
    }
    // 真机格式：8 字节小端长度前缀（前 4 字节是长度，后 4 字节恒 0）。
    std::uint32_t length = static_cast<std::uint32_t>(bytes[0]) |
                           (static_cast<std::uint32_t>(bytes[1]) << 8) |
                           (static_cast<std::uint32_t>(bytes[2]) << 16) |
                           (static_cast<std::uint32_t>(bytes[3]) << 24);
    const std::uint32_t high =
        static_cast<std::uint32_t>(bytes[4]) |
        (static_cast<std::uint32_t>(bytes[5]) << 8) |
        (static_cast<std::uint32_t>(bytes[6]) << 16) |
        (static_cast<std::uint32_t>(bytes[7]) << 24);
    if (high != 0) {
        std::ostringstream os;
        os << "载荷前缀高 4 字节应为 0，实际 " << high;
        error = os.str();
        return false;
    }
    if (8 + static_cast<std::size_t>(length) != bytes.size()) {
        std::ostringstream os;
        os << "载荷长度前缀不匹配：前缀 " << length << "，负载 "
           << (bytes.size() - 8) << " 字节";
        error = os.str();
        return false;
    }
    std::string out(bytes.begin() + 8, bytes.end());
    for (char c : out) {
        if (c == '\0') {
            error = "载荷文本里出现 NUL";
            return false;
        }
    }
    text.swap(out);
    return true;
}

bool CbsParseIdentity(const std::string& text,
                      CbsIdentity& identity,
                      std::string& error) {
    if (text.empty()) {
        error = "身份文本为空";
        return false;
    }
    CbsIdentity out;
    std::size_t start = 0;
    bool first = true;
    while (start <= text.size()) {
        std::size_t comma = text.find(',', start);
        const std::string piece =
            Trim(text.substr(start, comma == std::string::npos ? std::string::npos
                                                               : comma - start));
        if (piece.empty()) {
            error = "身份文本里有空片段";
            return false;
        }
        const std::size_t eq = piece.find('=');
        if (first) {
            if (eq != std::string::npos) {
                error = "身份文本的第一段必须是名字（不含 '='）";
                return false;
            }
            out.name = piece;
            first = false;
        } else {
            if (eq == std::string::npos) {
                error = "身份文本的属性段缺少 '='";
                return false;
            }
            out.attributes.emplace_back(Trim(piece.substr(0, eq)),
                                        Trim(piece.substr(eq + 1)));
        }
        if (comma == std::string::npos) {
            break;
        }
        start = comma + 1;
    }
    identity = std::move(out);
    return true;
}

std::string CbsFormatIdentity(const CbsIdentity& identity) {
    std::string out = identity.name;
    for (const auto& attribute : identity.attributes) {
        out += ", ";
        out += attribute.first;
        out += "=";
        out += attribute.second;
    }
    return out;
}

bool CbsDecodeComponentRecord(const std::wstring& name,
                              const std::vector<CbsValue>& values,
                              CbsComponentRecord& record,
                              std::string& error) {
    CbsComponentRecord out;
    out.name = name;
    bool hasSha = false;
    bool hasIdentity = false;
    for (const CbsValue& value : values) {
        if (value.name == "S256H") {
            if (value.data.size() != 32) {
                std::ostringstream os;
                os << "S256H 必须是 32 字节，实际 " << value.data.size() << " 字节";
                error = os.str();
                return false;
            }
            out.manifestSha256 = value.data;
            hasSha = true;
        } else if (value.name == "identity") {
            std::string text;
            if (!CbsDecodeAsciiText(value.data, text)) {
                error = "identity 不是合法的 ASCII 文本";
                return false;
            }
            out.identity = text;
            hasIdentity = true;
        } else if (value.name == "CF") {
            if (!CbsDecodeDword(value.data, out.configFlags, error)) {
                error = "CF: " + error;
                return false;
            }
            out.hasConfigFlags = true;
        } else if (StartsWith(value.name, "c!")) {
            if (!value.data.empty()) {
                std::ostringstream os;
                os << "c! 标记 " << value.name << " 的值必须是空，实际 "
                   << value.data.size() << " 字节";
                error = os.str();
                return false;
            }
            out.deploymentMarks.push_back(value.name.substr(2));
        } else if (StartsWith(value.name, "f!")) {
            std::wstring file;
            if (!ReadUtf16(value.data, file, error)) {
                error = "f! 值 " + value.name + ": " + error;
                return false;
            }
            out.files.push_back(file);
        } else {
            out.extraValues.push_back(value);
        }
    }
    if (!hasSha) {
        error = "组件记录缺少 S256H";
        return false;
    }
    if (!hasIdentity) {
        error = "组件记录缺少 identity";
        return false;
    }
    record = std::move(out);
    return true;
}

std::vector<CbsValue> CbsEncodeComponentRecord(const CbsComponentRecord& record) {
    std::vector<CbsValue> values;
    if (!record.manifestSha256.empty()) {
        values.push_back(CbsValue{"S256H", record.manifestSha256});
    }
    if (!record.identity.empty()) {
        values.push_back(CbsValue{"identity", CbsEncodeAsciiText(record.identity)});
    }
    for (const std::string& mark : record.deploymentMarks) {
        values.push_back(CbsValue{"c!" + mark, {}});
    }
    for (const std::wstring& file : record.files) {
        // 值名 `f!<文件名>`：真机上都是 ASCII 文件名（实测 19,006 个 f!）。
        // 非 ASCII 文件名在真机上没见到样本，这里按 UTF-8 编码，避免宽字符被截断成 '?'。
        values.push_back(CbsValue{"f!" + ToUtf8(file), CbsEncodeUtf16Text(file)});
    }
    if (record.hasConfigFlags) {
        values.push_back(CbsValue{"CF", CbsEncodeDword(record.configFlags)});
    }
    values.insert(values.end(), record.extraValues.begin(), record.extraValues.end());
    return values;
}

bool CbsDecodeDeploymentRecord(const std::wstring& name,
                               const std::vector<CbsValue>& values,
                               CbsDeploymentRecord& record,
                               std::string& error) {
    CbsDeploymentRecord out;
    out.name = name;
    bool hasAppId = false;
    for (const CbsValue& value : values) {
        if (value.name == "appid") {
            if (!CbsDecodeAsciiText(value.data, out.appId)) {
                error = "appid 不是合法的 ASCII 文本";
                return false;
            }
            hasAppId = true;
        } else if (value.name == "CatalogThumbprint") {
            if (!CbsDecodeAsciiText(value.data, out.catalogThumbprint)) {
                error = "CatalogThumbprint 不是合法的 ASCII 文本";
                return false;
            }
            out.hasCatalogThumbprint = true;
        } else if (StartsWith(value.name, "p!") || StartsWith(value.name, "s!") ||
                   StartsWith(value.name, "i!")) {
            std::string identity;
            if (!CbsDecodeLengthPrefixedAscii(value.data, identity, error)) {
                error = "载荷 " + value.name + ": " + error;
                return false;
            }
            out.payloads.emplace_back(value.name.substr(0, 2), identity);
        } else {
            out.extraValues.push_back(value);
        }
    }
    if (!hasAppId) {
        error = "部署记录缺少 appid";
        return false;
    }
    record = std::move(out);
    return true;
}

std::vector<CbsValue> CbsEncodeDeploymentRecord(const CbsDeploymentRecord& record) {
    std::vector<CbsValue> values;
    if (!record.appId.empty()) {
        values.push_back(CbsValue{"appid", CbsEncodeAsciiText(record.appId)});
    }
    if (record.hasCatalogThumbprint) {
        values.push_back(
            CbsValue{"CatalogThumbprint", CbsEncodeAsciiText(record.catalogThumbprint)});
    }
    for (const auto& payload : record.payloads) {
        values.push_back(CbsValue{payload.first, CbsEncodeLengthPrefixedAscii(payload.second)});
    }
    values.insert(values.end(), record.extraValues.begin(), record.extraValues.end());
    return values;
}

bool CbsDecodeStoreHeader(const std::vector<CbsValue>& rootValues,
                          CbsStoreHeader& header,
                          std::string& error) {
    CbsStoreHeader out;
    bool hasFormat = false;
    for (const CbsValue& value : rootValues) {
        if (value.name == "StoreFormatVersion") {
            if (!ReadUtf16(value.data, out.formatVersion, error)) {
                error = "StoreFormatVersion: " + error;
                return false;
            }
            hasFormat = true;
        } else if (value.name == "StoreArchitecture") {
            if (!CbsDecodeDword(value.data, out.architecture, error)) {
                error = "StoreArchitecture: " + error;
                return false;
            }
            out.hasArchitecture = true;
        } else if (value.name == "ExecutionState") {
            if (!CbsDecodeDword(value.data, out.executionState, error)) {
                error = "ExecutionState: " + error;
                return false;
            }
            out.hasExecutionState = true;
        } else if (value.name == "PendingXmlIdentifier") {
            if (!ReadUtf16(value.data, out.pendingXmlIdentifier, error)) {
                error = "PendingXmlIdentifier: " + error;
                return false;
            }
            out.hasPendingXmlIdentifier = true;
        }
    }
    if (!hasFormat) {
        error = "台账根缺少 StoreFormatVersion";
        return false;
    }
    header = std::move(out);
    return true;
}

std::vector<CbsValue> CbsEncodeStoreHeader(const CbsStoreHeader& header) {
    std::vector<CbsValue> values;
    if (!header.formatVersion.empty()) {
        values.push_back(
            CbsValue{"StoreFormatVersion", CbsEncodeUtf16Text(header.formatVersion)});
    }
    if (header.hasArchitecture) {
        values.push_back(CbsValue{"StoreArchitecture", CbsEncodeDword(header.architecture)});
    }
    if (header.hasExecutionState) {
        values.push_back(CbsValue{"ExecutionState", CbsEncodeDword(header.executionState)});
    }
    if (header.hasPendingXmlIdentifier) {
        values.push_back(CbsValue{"PendingXmlIdentifier",
                                  CbsEncodeUtf16Text(header.pendingXmlIdentifier)});
    }
    return values;
}

bool CbsIsComponentName(const std::wstring& name) {
    // 组件名 = WinSxS 目录名：<...>_<16 个小写 hex>
    if (name.size() < 18) {
        return false;
    }
    const std::size_t underscore = name.rfind(L'_');
    if (underscore == std::wstring::npos || underscore + 17 != name.size()) {
        return false;
    }
    for (std::size_t i = underscore + 1; i < name.size(); ++i) {
        const wchar_t c = name[i];
        const bool hex = (c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f');
        if (!hex) {
            return false;
        }
    }
    return true;
}

}  // namespace ocid
