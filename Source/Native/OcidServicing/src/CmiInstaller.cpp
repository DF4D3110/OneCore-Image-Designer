// OCID - CMI 注册表落地决策的实现。
//
// 决策逻辑的来源与依据见 include/ocid/CmiInstaller.h 与 include/ocid/RegistryValue.h。
// 本文件只做"对着 hive 现状算该不该写、写什么"，不碰编码原语。

#include "ocid/CmiInstaller.h"

#include <cstdio>

namespace ocid {

    namespace {

        bool EqualsNoCase(const std::wstring& a, const wchar_t* b)
        {
            size_t i = 0;
            for (; i < a.size() && b[i] != L'\0'; ++i)
            {
                wchar_t x = a[i], y = b[i];
                if (x >= L'A' && x <= L'Z') x = static_cast<wchar_t>(x - L'A' + L'a');
                if (y >= L'A' && y <= L'Z') y = static_cast<wchar_t>(y - L'A' + L'a');
                if (x != y) return false;
            }
            return i == a.size() && b[i] == L'\0';
        }

        /// UTF-16LE 字节 → 字符串（不剥 NUL；调用方按需处理）。
        std::wstring Utf16LeToString(const std::vector<uint8_t>& d)
        {
            std::wstring s;
            s.reserve(d.size() / 2);
            for (size_t i = 0; i + 1 < d.size(); i += 2)
                s.push_back(static_cast<wchar_t>(d[i] | (d[i + 1] << 8)));
            return s;
        }

        std::wstring ToHex(const std::vector<uint8_t>& bytes)
        {
            static const wchar_t* k = L"0123456789ABCDEF";
            std::wstring s;
            s.reserve(bytes.size() * 2);
            for (uint8_t b : bytes)
            {
                s.push_back(k[b >> 4]);
                s.push_back(k[b & 0x0F]);
            }
            return s;
        }

        std::wstring FormatDword(uint32_t v)
        {
            wchar_t buf[16];
            std::swprintf(buf, 16, L"0x%08X", v);
            return buf;
        }

        /// 去掉结尾的宽 NUL（字符串类）；MULTI_SZ 的**列表项**分隔符是单个 NUL。
        std::wstring StripWideNul(const std::wstring& s)
        {
            size_t n = s.size();
            while (n > 0 && s[n - 1] == L'\0') --n;
            return s.substr(0, n);
        }

    } // namespace

    const wchar_t* WriteActionName(WriteAction action)
    {
        switch (action)
        {
        case WriteAction::Write:           return L"write";
        case WriteAction::SkipOwned:       return L"skip-owned";
        case WriteAction::SkipMergeFailed: return L"skip-merge-failed";
        case WriteAction::InvalidHint:     return L"invalid-hint";
        }
        return L"?";
    }

    std::wstring ExistingValue::ToText() const
    {
        if (!exists) return L"(不存在)";

        switch (typeCode)
        {
        case 0:   // REG_NONE
            return data.empty() ? L"(空)" : L"(REG_NONE) " + ToHex(data);

        case 1:   // REG_SZ
        case 2:   // REG_EXPAND_SZ
        case 6:   // REG_LINK
            return L"\"" + StripWideNul(Utf16LeToString(data)) + L"\"";

        case 7:   // REG_MULTI_SZ：项之间是单个 NUL，结尾两个 NUL
        {
            const std::wstring raw = StripWideNul(Utf16LeToString(data));
            std::wstring out;
            std::wstring item;
            for (wchar_t c : raw)
            {
                if (c == L'\0')
                {
                    out += L"\"" + item + L"\",";
                    item.clear();
                }
                else item.push_back(c);
            }
            out += L"\"" + item + L"\"";
            return out;
        }

        case 4:   // REG_DWORD（小端）
        {
            if (data.size() != 4) return L"(REG_DWORD 长度异常 " + std::to_wstring(data.size()) + L")";
            const uint32_t v = static_cast<uint32_t>(data[0]) | (static_cast<uint32_t>(data[1]) << 8) |
                               (static_cast<uint32_t>(data[2]) << 16) | (static_cast<uint32_t>(data[3]) << 24);
            return FormatDword(v);
        }

        case 5:   // REG_DWORD_BIG_ENDIAN
        {
            if (data.size() != 4) return L"(REG_DWORD_BIG_ENDIAN 长度异常)";
            const uint32_t v = (static_cast<uint32_t>(data[0]) << 24) | (static_cast<uint32_t>(data[1]) << 16) |
                               (static_cast<uint32_t>(data[2]) << 8) | static_cast<uint32_t>(data[3]);
            return FormatDword(v);
        }

        case 11:  // REG_QWORD：**8 字节内存镜像**（照反编译，不按数值解释）
        {
            if (data.size() != 8) return L"(REG_QWORD 长度异常 " + std::to_wstring(data.size()) + L")";
            uint64_t v = 0;
            for (int i = 7; i >= 0; --i) v = (v << 8) | data[static_cast<size_t>(i)];
            // 注意别用固定小缓冲：swprintf 的宽度是**字符数**，16 个 hex + 前后缀会超。
            // 这里同时给出"数值"和"字节镜像"两种读法，因为两者常常不同（正是 REG_QWORD 的坑）。
            wchar_t vbuf[24];
            std::swprintf(vbuf, 24, L"0x%016llX", static_cast<unsigned long long>(v));
            return std::wstring(vbuf) + L" (镜像 " + ToHex(data) + L")";
        }

        default:  // REG_BINARY(3) / REG_RESOURCE_LIST(8) / REG_FULL_RESOURCE_DESCRIPTOR(9) / …
            return L"hex:" + ToHex(data);
        }
    }

    bool DecideRegistryWrite(const WcmRegistryOperation& op,
                             const ExistingValue& existing,
                             WriteDecision& out,
                             std::wstring* error)
    {
        out = WriteDecision{};

        // ① 先编码新值。两套机制都要做，失败即"操作本身有问题"，返回 false。
        const RegValueBlob blob = EncodeRegValue(op.valueType, op.data);
        if (!blob.ok)
        {
            if (error) *error = L"值无法编码（" + op.valueType + L"）：" + blob.error;
            return false;
        }
        out.typeCode = blob.typeCode;
        out.data = blob.bytes;

        // ② SMI/WCM 那套（`configurationSchema`）**没有** owner / operationHint 语义，
        //    语义就是"设置成这个值"。套用 CMI 的 owner 规则会把 WCM 的设置项静默丢掉。
        if (!EqualsNoCase(op.mechanism, L"registryKeys"))
        {
            out.action = WriteAction::Write;
            out.note = existing.exists
                ? L"SMI/WCM 机制：值已存在 ⇒ 覆盖（该机制不看 owner/operationHint）"
                : L"SMI/WCM 机制：新增值";
            return true;
        }

        // ③ 值不存在 ⇒ 直接写，**owner 不参与**（cmiv2：GetValue 返回 2=ERROR_FILE_NOT_FOUND 分支）。
        if (!existing.exists)
        {
            out.action = WriteAction::Write;
            out.note = L"CMI：值不存在 ⇒ 直接写（owner 不参与）";
            return true;
        }

        // ④ 值已存在 ⇒ 按 operationHint。
        //    缺省（空）按 1=replace（反编译里 `v48 = 1` 是初值）。
        RegValueOperationHint hint = RegValueOperationHint::Replace;
        if (!op.operationHint.empty() && !ParseRegValueOperationHint(op.operationHint, hint))
        {
            // 对应 `E_INVALIDARG (0x80070057)`。**不抛**：记录并跳过，让整批能继续。
            out.action = WriteAction::InvalidHint;
            out.note = L"CMI：operationHint 非法 \"" + op.operationHint +
                       L"\"（对应 E_INVALIDARG 0x80070057）⇒ 跳过";
            return true;
        }

        switch (hint)
        {
        case RegValueOperationHint::Replace:
            if (op.owner)
            {
                // ★ 最容易漏的一条：owner=true + 值已存在 + replace ⇒ **不覆盖**。
                out.action = WriteAction::SkipOwned;
                out.note = L"CMI：值已存在且 owner=true ⇒ 不覆盖"
                           L"（\"Not setting value … as the component is an owner\"）";
                return true;
            }
            out.action = WriteAction::Write;
            out.note = L"CMI：值已存在，owner=false ⇒ 覆盖";
            return true;

        case RegValueOperationHint::Append:
        case RegValueOperationHint::Prepend:
        {
            // 合并的前提是类型相同（cmiv2：`this[7] != a2[7]` ⇒ 合并失败、跳过写入）。
            if (existing.typeCode != blob.typeCode)
            {
                out.action = WriteAction::SkipMergeFailed;
                out.note = L"CMI：合并失败 —— 类型不同（现有 " + std::to_wstring(existing.typeCode) +
                           L" vs 声明 " + std::to_wstring(blob.typeCode) + L"）⇒ 跳过";
                return true;
            }

            const bool prepend = (hint == RegValueOperationHint::Prepend);
            std::vector<uint8_t> merged;
            if (!MergeRegValueBytes(blob.typeCode, existing.data, blob.bytes, prepend, merged))
            {
                out.action = WriteAction::SkipMergeFailed;
                out.note = L"CMI：合并失败 —— 长度/类型不满足 Merge 的前提（现有 " +
                           std::to_wstring(existing.data.size()) + L" 字节 vs 声明 " +
                           std::to_wstring(blob.bytes.size()) + L" 字节）⇒ 跳过";
                return true;
            }

            out.action = WriteAction::Write;
            out.data = std::move(merged);
            out.note = std::wstring(L"CMI：") + (prepend ? L"prepend" : L"append") +
                       L" 合并（去掉先写者尾部那一个宽 NUL 后拼接）";
            return true;
        }

        case RegValueOperationHint::Unknown:
        default:
            out.action = WriteAction::InvalidHint;
            out.note = L"CMI：operationHint 落在未知分支 ⇒ 跳过";
            return true;
        }
    }

} // namespace ocid
