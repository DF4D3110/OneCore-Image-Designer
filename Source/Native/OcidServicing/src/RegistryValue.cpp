// OCID - 注册表值类型与值数据编码（实现）
//
// 规则出处见 RegistryValue.h 的文件头；这里只写实现，并保留每条规则的证据标注。
// 原则：**宁可显式失败，也不猜一个"差不多"的字节序列写进 hive**。

#include "ocid/RegistryValue.h"

#include <algorithm>
#include <cwctype>

namespace ocid {

    namespace {

        std::wstring ToLower(std::wstring s)
        {
            for (auto& c : s)
                c = static_cast<wchar_t>(std::towlower(c));
            return s;
        }

        bool IsHexDigit(wchar_t c)
        {
            return (c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f') || (c >= L'A' && c <= L'F');
        }

        int HexVal(wchar_t c)
        {
            if (c >= L'0' && c <= L'9') return c - L'0';
            if (c >= L'a' && c <= L'f') return c - L'a' + 10;
            if (c >= L'A' && c <= L'F') return c - L'A' + 10;
            return -1;
        }

        /// 严格 hex 解析：只接受 hex 字符、长度必须偶数。绝不"跳过非法字符"。
        bool ParseHexStrict(const std::wstring& s, std::vector<uint8_t>& out, std::wstring& err)
        {
            if (s.size() % 2 != 0)
            {
                err = L"hex 串长度为奇数（" + std::to_wstring(s.size()) + L"）";
                return false;
            }
            out.clear();
            out.reserve(s.size() / 2);
            for (size_t i = 0; i < s.size(); i += 2)
            {
                int hi = HexVal(s[i]);
                int lo = HexVal(s[i + 1]);
                if (hi < 0 || lo < 0)
                {
                    err = L"hex 串含非 hex 字符 @";
                    err += std::to_wstring(i);
                    return false;
                }
                out.push_back(static_cast<uint8_t>((hi << 4) | lo));
            }
            return true;
        }

        /// UTF-16LE + 结尾 NUL（`RegSetValueExW` 的常规形态：cbData 含终止符）。
        std::vector<uint8_t> EncodeUtf16Z(const std::wstring& s)
        {
            std::vector<uint8_t> out;
            out.reserve((s.size() + 1) * 2);
            for (wchar_t c : s)
            {
                out.push_back(static_cast<uint8_t>(c & 0xFF));
                out.push_back(static_cast<uint8_t>((c >> 8) & 0xFF));
            }
            out.push_back(0);
            out.push_back(0);
            return out;
        }

        /// 无终止符的 UTF-16LE。
        std::vector<uint8_t> EncodeUtf16Raw(const std::wstring& s)
        {
            std::vector<uint8_t> out;
            out.reserve(s.size() * 2);
            for (wchar_t c : s)
            {
                out.push_back(static_cast<uint8_t>(c & 0xFF));
                out.push_back(static_cast<uint8_t>((c >> 8) & 0xFF));
            }
            return out;
        }

        bool ContainsMacro(const std::wstring& s)
        {
            return s.find(L"$(") != std::wstring::npos;
        }

        /// `0x%08X` / `0x…` / 纯十进制 → uint32_t。
        bool ParseDwordText(const std::wstring& s, uint32_t& out)
        {
            std::wstring t = s;
            // 去掉首尾空白
            while (!t.empty() && (t.front() == L' ' || t.front() == L'\t')) t.erase(t.begin());
            while (!t.empty() && (t.back() == L' ' || t.back() == L'\t' || t.back() == L'\r' || t.back() == L'\n'))
                t.pop_back();
            if (t.empty()) return false;

            bool hex = false;
            size_t i = 0;
            if (t.size() > 2 && t[0] == L'0' && (t[1] == L'x' || t[1] == L'X'))
            {
                hex = true;
                i = 2;
            }

            uint64_t acc = 0;
            for (; i < t.size(); ++i)
            {
                int d;
                if (hex)
                {
                    d = HexVal(t[i]);
                    if (d < 0) return false;
                }
                else
                {
                    if (t[i] < L'0' || t[i] > L'9') return false;
                    d = t[i] - L'0';
                }
                acc = acc * (hex ? 16u : 10u) + static_cast<uint64_t>(d);
                if (acc > 0xFFFFFFFFull && !hex) return false;
            }
            out = static_cast<uint32_t>(acc & 0xFFFFFFFFull);
            return true;
        }

        /// `REG_MULTI_SZ` 的列表拆分。
        ///
        /// [语料+活机] 实测：值一定是 `"a","b"` 形态（引号是 XML 的 `&quot;`），
        /// 493/516 全部带引号、0 条不带；活注册表里那对引号是**不存在的**。
        /// ⇒ 去掉最外层引号后按 `","` 切分。若没有引号（语料中未出现，但宽容处理），
        ///    按单个项处理。
        std::vector<std::wstring> SplitMultiString(const std::wstring& v)
        {
            std::vector<std::wstring> items;
            if (v.empty())
                return items;

            std::wstring body = v;
            if (body.size() >= 2 && body.front() == L'"' && body.back() == L'"')
                body = body.substr(1, body.size() - 2);
            else
            {
                items.push_back(v);
                return items;
            }

            const std::wstring sep = L"\",\"";
            size_t pos = 0;
            for (;;)
            {
                size_t hit = body.find(sep, pos);
                if (hit == std::wstring::npos)
                {
                    items.push_back(body.substr(pos));
                    break;
                }
                items.push_back(body.substr(pos, hit - pos));
                pos = hit + sep.size();
            }
            return items;
        }

    } // namespace

    // ─────────────────────────────────────────────────────────────────────────
    // 类型
    // ─────────────────────────────────────────────────────────────────────────

    bool ParseRegValueType(const std::wstring& text, RegValueType& out)
    {
        // 名字与 [表] TRegValueTypeMap 逐条一致（大小写不敏感，见 wcp 用 FsnpDowncaseChar）。
        static const struct { const wchar_t* name; RegValueType t; } kMap[] = {
            { L"REG_NONE",                        RegValueType::None },
            { L"REG_SZ",                          RegValueType::Sz },
            { L"REG_EXPAND_SZ",                   RegValueType::ExpandSz },
            { L"REG_MULTI_SZ",                    RegValueType::MultiSz },
            { L"REG_BINARY",                      RegValueType::Binary },
            { L"REG_DWORD",                       RegValueType::Dword },
            { L"REG_DWORD_LITTLE_ENDIAN",         RegValueType::DwordLittleEndian },
            { L"REG_DWORD_BIG_ENDIAN",            RegValueType::DwordBigEndian },
            { L"REG_LINK",                        RegValueType::Link },
            { L"REG_RESOURCE_LIST",               RegValueType::ResourceList },
            { L"REG_FULL_RESOURCE_DESCRIPTOR",    RegValueType::FullResourceDescriptor },
            { L"REG_RESOURCE_REQUIREMENTS_LIST",  RegValueType::ResourceRequirementsList },
            { L"REG_QWORD",                       RegValueType::Qword },
            { L"REG_QWORD_LITTLE_ENDIAN",         RegValueType::QwordLittleEndian },
        };

        const std::wstring want = ToLower(text);
        for (const auto& e : kMap)
        {
            if (want == ToLower(e.name))
            {
                out = e.t;
                return true;
            }
        }
        out = RegValueType::Unknown;
        return false;
    }

    const wchar_t* RegValueTypeName(RegValueType t)
    {
        switch (t)
        {
        case RegValueType::None:                      return L"REG_NONE";
        case RegValueType::Sz:                        return L"REG_SZ";
        case RegValueType::ExpandSz:                  return L"REG_EXPAND_SZ";
        case RegValueType::MultiSz:                   return L"REG_MULTI_SZ";
        case RegValueType::Binary:                    return L"REG_BINARY";
        case RegValueType::Dword:                     return L"REG_DWORD";
        case RegValueType::DwordLittleEndian:         return L"REG_DWORD_LITTLE_ENDIAN";
        case RegValueType::DwordBigEndian:            return L"REG_DWORD_BIG_ENDIAN";
        case RegValueType::Link:                      return L"REG_LINK";
        case RegValueType::ResourceList:              return L"REG_RESOURCE_LIST";
        case RegValueType::FullResourceDescriptor:    return L"REG_FULL_RESOURCE_DESCRIPTOR";
        case RegValueType::ResourceRequirementsList:  return L"REG_RESOURCE_REQUIREMENTS_LIST";
        case RegValueType::Qword:                     return L"REG_QWORD";
        case RegValueType::QwordLittleEndian:         return L"REG_QWORD_LITTLE_ENDIAN";
        default:                                      return L"";
        }
    }

    uint32_t Win32RegType(RegValueType t)
    {
        // Win32 `REG_*` 码（winreg.h）。注意与 wcp 枚举**不是一回事**。
        switch (t)
        {
        case RegValueType::None:                      return 0;   // REG_NONE
        case RegValueType::Sz:                        return 1;   // REG_SZ
        case RegValueType::ExpandSz:                  return 2;   // REG_EXPAND_SZ
        case RegValueType::Binary:                    return 3;   // REG_BINARY
        case RegValueType::Dword:                     return 4;   // REG_DWORD
        case RegValueType::DwordLittleEndian:         return 4;   // 别名 → REG_DWORD
        case RegValueType::DwordBigEndian:            return 5;   // REG_DWORD_BIG_ENDIAN
        case RegValueType::Link:                      return 6;   // REG_LINK
        case RegValueType::MultiSz:                   return 7;   // REG_MULTI_SZ
        case RegValueType::ResourceList:              return 8;   // REG_RESOURCE_LIST
        case RegValueType::FullResourceDescriptor:    return 9;   // REG_FULL_RESOURCE_DESCRIPTOR
        case RegValueType::ResourceRequirementsList:  return 10;  // REG_RESOURCE_REQUIREMENTS_LIST
        case RegValueType::Qword:                     return 11;  // REG_QWORD
        case RegValueType::QwordLittleEndian:         return 11;  // 别名 → REG_QWORD
        default:                                      return 0xFFFFFFFFu;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 值数据编码
    // ─────────────────────────────────────────────────────────────────────────

    RegValueBlob EncodeRegValue(RegValueType type, const std::wstring& textual)
    {
        RegValueBlob r;
        r.hasUnresolvedMacro = ContainsMacro(textual);

        if (type == RegValueType::Unknown)
        {
            r.error = L"未知的 valueType";
            return r;
        }

        const uint32_t code = Win32RegType(type);
        if (code == 0xFFFFFFFFu)
        {
            r.error = L"类型没有对应的 Win32 码";
            return r;
        }
        r.typeCode = code;

        switch (type)
        {
        case RegValueType::None:
            // [语料] 158/158 都是空 ⇒ 零字节。
            r.bytes.clear();
            r.ok = true;
            return r;

        case RegValueType::Sz:
        case RegValueType::ExpandSz:
        case RegValueType::Link:
            // 字面文本（含 `$(…)` 宏时原样保留，由上层决定）。
            r.bytes = EncodeUtf16Z(textual);
            r.ok = true;
            return r;

        case RegValueType::MultiSz:
        {
            auto items = SplitMultiString(textual);
            std::vector<uint8_t> b;
            for (const auto& it : items)
            {
                auto one = EncodeUtf16Raw(it);
                b.insert(b.end(), one.begin(), one.end());
                b.push_back(0);   // 每项结尾 NUL（宽字符）
                b.push_back(0);
            }
            // 列表终止符。空列表时也要有终止符，否则不是合法的 MULTI_SZ。
            b.push_back(0);
            b.push_back(0);
            r.bytes = std::move(b);
            r.ok = true;
            return r;
        }

        case RegValueType::Binary:
        case RegValueType::ResourceList:
        case RegValueType::FullResourceDescriptor:
        case RegValueType::ResourceRequirementsList:
        {
            // [语料] 裸 hex 串 = 字节序列，原样（**不做大小端翻转**）。
            std::vector<uint8_t> b;
            std::wstring err;
            if (!ParseHexStrict(textual, b, err))
            {
                r.error = std::wstring(L"REG_BINARY/资源类型解析失败：") + err;
                return r;
            }
            r.bytes = std::move(b);
            r.ok = true;
            return r;
        }

        case RegValueType::Dword:
        case RegValueType::DwordLittleEndian:
        {
            // [语料] 15641/15641 是 `0x%08X`；也接受十进制。
            uint32_t v = 0;
            if (!ParseDwordText(textual, v))
            {
                r.error = L"REG_DWORD 值不是合法数值";
                return r;
            }
            r.bytes = { static_cast<uint8_t>(v & 0xFF),
                        static_cast<uint8_t>((v >> 8) & 0xFF),
                        static_cast<uint8_t>((v >> 16) & 0xFF),
                        static_cast<uint8_t>((v >> 24) & 0xFF) };
            r.ok = true;
            return r;
        }

        case RegValueType::DwordBigEndian:
        {
            uint32_t v = 0;
            if (!ParseDwordText(textual, v))
            {
                r.error = L"REG_DWORD_BIG_ENDIAN 值不是合法数值";
                return r;
            }
            r.bytes = { static_cast<uint8_t>((v >> 24) & 0xFF),
                        static_cast<uint8_t>((v >> 16) & 0xFF),
                        static_cast<uint8_t>((v >> 8) & 0xFF),
                        static_cast<uint8_t>(v & 0xFF) };
            r.ok = true;
            return r;
        }

        case RegValueType::Qword:
        case RegValueType::QwordLittleEndian:
        {
            // [语料+活机] 501/501 是 16 位 hex；活机 23/23 命中「**字节镜像**」读法：
            //   这 16 个字符就是 8 个字节，按低地址→高地址书写，**不做数值解释**。
            if (textual.size() != 16)
            {
                r.error = L"REG_QWORD 值必须是 16 位 hex（实测 501/501 如此），实际长度 "
                          + std::to_wstring(textual.size());
                return r;
            }
            std::vector<uint8_t> b;
            std::wstring err;
            if (!ParseHexStrict(textual, b, err))
            {
                r.error = std::wstring(L"REG_QWORD 值不是合法 hex：") + err;
                return r;
            }
            r.bytes = std::move(b);
            r.ok = true;
            return r;
        }

        default:
            r.error = L"未实现的 valueType";
            return r;
        }
    }

    RegValueBlob EncodeRegValue(const std::wstring& typeName, const std::wstring& textual)
    {
        RegValueType t = RegValueType::Unknown;
        if (!ParseRegValueType(typeName, t))
        {
            RegValueBlob r;
            r.error = std::wstring(L"非法的 valueType：") + typeName;
            return r;
        }
        return EncodeRegValue(t, textual);
    }

    std::wstring BytesToHex(const std::vector<uint8_t>& bytes)
    {
        static const wchar_t* kHex = L"0123456789ABCDEF";
        std::wstring s;
        s.reserve(bytes.size() * 2);
        for (uint8_t b : bytes)
        {
            s.push_back(kHex[(b >> 4) & 0xF]);
            s.push_back(kHex[b & 0xF]);
        }
        return s;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // systemProtection
    // ─────────────────────────────────────────────────────────────────────────

    bool ParseRegKeyProtection(const std::wstring& text, RegKeyProtection& out)
    {
        static const struct { const wchar_t* name; RegKeyProtection p; } kMap[] = {
            { L"readOnlyIgnoreWrites",          RegKeyProtection::ReadOnlyIgnoreWrites },
            { L"readOnlyFailWrites",            RegKeyProtection::ReadOnlyFailWrites },
            { L"OSOnlyIgnoreWrites",            RegKeyProtection::OSOnlyIgnoreWrites },
            { L"OSOnlyFailWrites",              RegKeyProtection::OSOnlyFailWrites },
            { L"transacted",                    RegKeyProtection::Transacted },
            { L"applicationVirtualized",        RegKeyProtection::ApplicationVirtualized },
            { L"userVirtualized",               RegKeyProtection::UserVirtualized },
            { L"applicationAndUserVirtualized", RegKeyProtection::ApplicationAndUserVirtualized },
            { L"inherit",                       RegKeyProtection::Inherit },
            { L"notProtected",                  RegKeyProtection::NotProtected },
        };
        const std::wstring want = ToLower(text);
        for (const auto& e : kMap)
        {
            if (want == ToLower(e.name))
            {
                out = e.p;
                return true;
            }
        }
        out = RegKeyProtection::Unknown;
        return false;
    }

    const wchar_t* RegKeyProtectionName(RegKeyProtection p)
    {
        switch (p)
        {
        case RegKeyProtection::ReadOnlyIgnoreWrites:          return L"readOnlyIgnoreWrites";
        case RegKeyProtection::ReadOnlyFailWrites:            return L"readOnlyFailWrites";
        case RegKeyProtection::OSOnlyIgnoreWrites:            return L"OSOnlyIgnoreWrites";
        case RegKeyProtection::OSOnlyFailWrites:              return L"OSOnlyFailWrites";
        case RegKeyProtection::Transacted:                    return L"transacted";
        case RegKeyProtection::ApplicationVirtualized:        return L"applicationVirtualized";
        case RegKeyProtection::UserVirtualized:               return L"userVirtualized";
        case RegKeyProtection::ApplicationAndUserVirtualized: return L"applicationAndUserVirtualized";
        case RegKeyProtection::Inherit:                       return L"inherit";
        case RegKeyProtection::NotProtected:                  return L"notProtected";
        default:                                              return L"";
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // operationHint
    // ─────────────────────────────────────────────────────────────────────────

    bool ParseRegValueOperationHint(const std::wstring& text, RegValueOperationHint& out)
    {
        static const struct { const wchar_t* name; RegValueOperationHint h; } kMap[] = {
            { L"replace", RegValueOperationHint::Replace },
            { L"append",  RegValueOperationHint::Append },
            { L"prepend", RegValueOperationHint::Prepend },
        };
        const std::wstring want = ToLower(text);
        for (const auto& e : kMap)
        {
            if (want == ToLower(e.name))
            {
                out = e.h;
                return true;
            }
        }
        out = RegValueOperationHint::Unknown;
        return false;
    }

    const wchar_t* RegValueOperationHintName(RegValueOperationHint h)
    {
        switch (h)
        {
        case RegValueOperationHint::Replace: return L"replace";
        case RegValueOperationHint::Append:  return L"append";
        case RegValueOperationHint::Prepend: return L"prepend";
        default:                             return L"";
        }
    }

    bool MergeRegValueBytes(uint32_t typeCode,
                            const std::vector<uint8_t>& existing,
                            const std::vector<uint8_t>& incoming,
                            bool prepend,
                            std::vector<uint8_t>& out)
    {
        // [代码] RawRegistryValue::Merge：类型不同 ⇒ 合并失败，调用方跳过。
        // 这里 typeCode 由调用方保证两边一致（它来自同一个声明的类型）。

        const std::vector<uint8_t>& a = prepend ? incoming : existing;   // 先写的（= Merge 里的 `this`）
        const std::vector<uint8_t>& b = prepend ? existing : incoming;   // 后接的（= Merge 里的 `a2`）

        if (typeCode == 4 /* REG_DWORD */)
        {
            // 逐字节按位 OR。
            // [代码] cmiv2 L240580/L240585：两边长度都必须**正好 4**，否则抛
            //   CmiException(-2147024809) = 0x80070057 E_INVALIDARG。
            //   这里选择返回 false 让调用方跳过（不把异常当控制流）。
            if (a.size() != 4 || b.size() != 4)
                return false;
            out.resize(4);
            for (size_t i = 0; i < 4; ++i)
                out[i] = static_cast<uint8_t>(a[i] | b[i]);
            return true;
        }

        // [代码] cmiv2 L240598：`if ( v8 > 5 && v8 <= 7 )` 与 `v8 <= 2` 走同一条字符串通路
        //   ⇒ 字符串类是 { 1=REG_SZ, 2=REG_EXPAND_SZ, 6=REG_LINK, 7=REG_MULTI_SZ }。
        //   （注意 v8 就是**真实 Win32 类型码**，不是 wcp 的枚举。）
        if (typeCode == 1 || typeCode == 2 || typeCode == 6 || typeCode == 7)
        {
            // [代码] L240648 / L240634 / L240656：
            //     newLen = v7 + thisLen - 2 + a2Len        （v7 通常 0）
            //     memcpy(dst,                 this->data, thisLen - 2);
            //     memcpy(dst + thisLen - 2 + v7, a2->data, a2Len);
            // ⇒ **只从 target 去掉【一个】宽 NUL，source 整体接上（保留它自己的结尾 NUL）**。
            //   我原先"两边都 strip 再补一个 NUL"在 REG_SZ 上结果相同，但在 MULTI_SZ 上会丢
            //   掉一个空项——所以必须照代码来。
            if (a.size() < 2)
                return false;   // L240606：thisLen < 2 时抛 E_INVALIDARG
            out.assign(a.begin(), a.end() - 2);
            out.insert(out.end(), b.begin(), b.end());
            return true;
        }

        // [代码] L240665-L240690：其它类型 ⇒ newLen = thisLen + a2Len，两边整体拼接。
        out = a;
        out.insert(out.end(), b.begin(), b.end());
        return true;
    }

} // namespace ocid
