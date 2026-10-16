//
// SecurityDescriptor.cpp —— SDDL 文本解析 + 与 Win32 描述符二进制互转
//
// 分成三层，刻意分开：
//   1) DescribeSddl / ResolveSddl / IsInheritSddl —— 纯文本，零 API，可无管理员测试
//   2) SddlToSelfRelative / SelfRelativeToSddl    —— 文本 ↔ 自相对二进制，碰 sddl.h
//   3) ApplySddlToKey / ReadKeySddl               —— 落到**已打开的键**上并读回，碰 winreg
//
// 这样"决策"（要不要设、设什么、有什么风险）既不与"编码"（SID/ACE 的二进制格式）耦合，
// 也不与"挂哪个 hive、开哪个键"耦合。
//
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <sddl.h>

#pragma comment(lib, "advapi32.lib")

#include "ocid/SecurityDescriptor.h"

#include <algorithm>

namespace ocid {

    namespace {

        std::wstring Trim(const std::wstring& s)
        {
            std::size_t b = 0, e = s.size();
            while (b < e && (s[b] == L' ' || s[b] == L'\t')) ++b;
            while (e > b && (s[e - 1] == L' ' || s[e - 1] == L'\t')) --e;
            return s.substr(b, e - b);
        }

        bool IEquals(const std::wstring& a, const wchar_t* b)
        {
            return _wcsicmp(a.c_str(), b) == 0;
        }

        bool IsSectionLetter(wchar_t c)
        {
            return c == L'O' || c == L'G' || c == L'D' || c == L'S';
        }

        /// 解析 ACL 段的**主体**（`:` 之后的全部，例如 `P(A;;FA;;;BA)(A;;GR;;;WD)`）。
        ///
        /// 语法：先是一串标志字母，然后是若干个 `(...)` ACE。
        /// 标志实测会遇到：P(protected)、AI(auto-inherited)、AR(auto-inherit-required)、
        /// NO_ACCESS_CONTROL（整段就是这个词，表示 NULL DACL）。
        bool ParseAclBody(const std::wstring& body, bool& prot, bool& ai, bool& noAccess,
                          int& aceCount, std::wstring& err)
        {
            std::size_t i = 0;

            // (1) `D:NO_ACCESS_CONTROL` —— 整个段就是这个词，没有 ACE。
            //     必须先判它：否则会被当成一串标志字母而"解析成功"，掩盖 NULL DACL。
            {
                std::wstring head = Trim(body);
                for (auto& ch : head) ch = static_cast<wchar_t>(towupper(ch));
                if (head == L"NO_ACCESS_CONTROL") {
                    noAccess = true;
                    return true;
                }
            }

            // (2) 标志字母
            for (; i < body.size() && body[i] != L'('; ++i) {
                wchar_t c = body[i];
                if (c == L' ' || c == L'\t') continue;
                if (c == L'P') { prot = true; continue; }
                if (c == L'A') {
                    // AI / AR / 或孤立的 A
                    if (i + 1 < body.size() && body[i + 1] == L'I') { ai = true; ++i; }
                    else if (i + 1 < body.size() && body[i + 1] == L'R') { ++i; }  // AR：保留继承
                    continue;
                }
                // 其他合法标志（如 SCL、RM、D 等）不参与本模块的判断，放行即可。
                continue;
            }

            // (3) ACE 列表
            while (i < body.size() && body[i] == L'(') {
                int depth = 1;
                std::size_t j = i + 1;
                while (j < body.size() && depth > 0) {
                    if (body[j] == L'(') ++depth;
                    else if (body[j] == L')') --depth;
                    ++j;
                }
                if (depth != 0) { err = L"SDDL 的 ACE 括号不匹配"; return false; }
                ++aceCount;
                i = j;
            }

            if (i < body.size()) {
                err = L"SDDL 的 ACL 段在 ACE 列表之后还有无法识别的内容";
                return false;
            }
            return true;
        }

    } // namespace

    // ─────────────────────────────────────────────────────────────────────────
    // 特殊值与定义表解析
    // ─────────────────────────────────────────────────────────────────────────

    bool IsInheritSddl(const std::wstring& sddl)
    {
        return IEquals(Trim(sddl), L"(inherit)");
    }

    bool ResolveSddl(const SddlDefinitions& defs, const std::wstring& name,
                     std::wstring& sddl, std::wstring& err)
    {
        sddl.clear();
        if (name.empty()) { err = L"安全描述符引用名为空"; return false; }

        auto it = defs.find(name);
        if (it != defs.end()) { sddl = it->second; return true; }

        // 退化为忽略大小写（清单里大小写是一致的，但不能因为大小写就丢 SD）
        for (const auto& kv : defs) {
            if (_wcsicmp(kv.first.c_str(), name.c_str()) == 0) { sddl = kv.second; return true; }
        }
        err = L"找不到安全描述符定义：" + name;
        return false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 文本级解读
    // ─────────────────────────────────────────────────────────────────────────

    bool DescribeSddl(const std::wstring& sddl, SddlInfo& info, std::wstring& err)
    {
        info = SddlInfo{};
        err.clear();

        if (sddl.empty()) { err = L"SDDL 为空"; return false; }
        if (IsInheritSddl(sddl)) {
            err = L"SDDL 是 (inherit)：这不是描述符，调用方应当跳过设置而不是解析它";
            return false;
        }

        const std::size_t n = sddl.size();
        std::size_t i = 0;
        while (i < n) {
            const wchar_t c = sddl[i];
            if (c == L' ' || c == L'\t' || c == L'\r' || c == L'\n') { ++i; continue; }

            if (!IsSectionLetter(c) || i + 1 >= n || sddl[i + 1] != L':') {
                err = L"SDDL 段格式错误：期望 O:/G:/D:/S: 之一";
                return false;
            }

            const std::size_t bodyStart = i + 2;

            // 段主体一直到下一个 `X:` 为止。SID 只由 S-1-… 的字母数字连字符组成，
            // 不会含 `:`，所以"下一个 X:"就是段的边界。
            std::size_t next = n;
            for (std::size_t j = bodyStart; j + 1 < n; ++j) {
                if (sddl[j + 1] == L':' && IsSectionLetter(sddl[j])) { next = j; break; }
            }
            const std::wstring body = sddl.substr(bodyStart, next - bodyStart);

            switch (c) {
            case L'O':
                info.hasOwner = true;
                info.ownerSid = Trim(body);
                break;
            case L'G':
                info.hasGroup = true;
                info.groupSid = Trim(body);
                break;
            case L'D':
                info.hasDacl = true;
                info.daclPresent = true;
                if (!ParseAclBody(body, info.daclProtected, info.daclAutoInherited,
                                  info.daclNoAccessControl, info.daclAceCount, err))
                    return false;
                break;
            case L'S':
                info.hasSacl = true;
                if (!ParseAclBody(body, info.saclProtected, info.saclAutoInherited,
                                  info.saclNoAccessControl, info.saclAceCount, err))
                    return false;
                break;
            default:
                err = L"未知的 SDDL 段前缀";
                return false;
            }
            i = next;
        }

        if (!info.hasOwner && !info.hasGroup && !info.hasDacl && !info.hasSacl) {
            err = L"SDDL 里一个段都没有";
            return false;
        }
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 与 Win32 的互转
    // ─────────────────────────────────────────────────────────────────────────

    bool SddlToSelfRelative(const std::wstring& sddl,
                            std::vector<unsigned char>& out, std::wstring& err)
    {
        out.clear();
        err.clear();

        if (sddl.empty()) { err = L"SDDL 为空"; return false; }
        if (IsInheritSddl(sddl)) {
            err = L"SDDL 是 (inherit)：调用方应跳过设置，而不是把它转成描述符";
            return false;
        }

        PSECURITY_DESCRIPTOR psd = nullptr;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl.c_str(), SDDL_REVISION_1, &psd, nullptr)) {
            err = L"ConvertStringSecurityDescriptorToSecurityDescriptor 失败，GetLastError="
                  + std::to_wstring(GetLastError());
            return false;
        }

        const DWORD len = GetSecurityDescriptorLength(psd);
        const auto* p = static_cast<const unsigned char*>(psd);
        out.assign(p, p + len);

        LocalFree(psd);
        return true;
    }

    bool SelfRelativeToSddl(const unsigned char* data, std::size_t len,
                            std::wstring& out, std::wstring& err)
    {
        out.clear();
        err.clear();

        if (data == nullptr || len == 0) { err = L"描述符字节为空"; return false; }
        if (len < sizeof(SECURITY_DESCRIPTOR)) { err = L"描述符长度不足一个 SECURITY_DESCRIPTOR"; return false; }

        auto* psd = const_cast<SECURITY_DESCRIPTOR*>(
            reinterpret_cast<const SECURITY_DESCRIPTOR*>(data));

        SECURITY_DESCRIPTOR_CONTROL ctrl = 0;
        DWORD rev = 0;
        if (!GetSecurityDescriptorControl(psd, &ctrl, &rev)) {
            err = L"GetSecurityDescriptorControl 失败，GetLastError=" + std::to_wstring(GetLastError());
            return false;
        }
        // 我们只处理自相对形式：绝对形式里面是指针，跨进程/跨字节块没有意义。
        if ((ctrl & SE_SELF_RELATIVE) == 0) {
            err = L"这不是自相对（self-relative）描述符";
            return false;
        }

        LPWSTR text = nullptr;
        const SECURITY_INFORMATION want =
            OWNER_SECURITY_INFORMATION | GROUP_SECURITY_INFORMATION |
            DACL_SECURITY_INFORMATION  | SACL_SECURITY_INFORMATION  |
            LABEL_SECURITY_INFORMATION;

        if (!ConvertSecurityDescriptorToStringSecurityDescriptorW(
                psd, SDDL_REVISION_1, want, &text, nullptr)) {
            err = L"ConvertSecurityDescriptorToStringSecurityDescriptor 失败，GetLastError="
                  + std::to_wstring(GetLastError());
            return false;
        }

        out = text;
        LocalFree(text);
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 落盘：应用到键 + 回读
    // ─────────────────────────────────────────────────────────────────────────

    unsigned SddlSecurityInformation(const SddlInfo& info)
    {
        unsigned bits = 0;
        if (info.hasOwner) bits |= OWNER_SECURITY_INFORMATION;
        if (info.hasGroup) bits |= GROUP_SECURITY_INFORMATION;
        if (info.hasDacl)  bits |= DACL_SECURITY_INFORMATION;
        if (info.hasSacl)  bits |= SACL_SECURITY_INFORMATION;
        return bits;
    }

    SddlApplyResult ApplySddlToKey(void* keyHandle, const std::wstring& sddl, std::wstring& err)
    {
        err.clear();

        if (keyHandle == nullptr) { err = L"键句柄为空"; return SddlApplyResult::Failed; }

        // `(inherit)` 的意思是"保持继承、别显式设置"，跳过是**正确行为**而不是失败。
        if (IsInheritSddl(sddl)) return SddlApplyResult::SkippedInherit;

        SddlInfo info;
        std::wstring perr;
        if (!DescribeSddl(sddl, info, perr)) {
            err = L"SDDL 解析失败：" + perr;
            return SddlApplyResult::Failed;
        }

        const unsigned bits = SddlSecurityInformation(info);
        if (bits == 0) {
            err = L"SDDL 里没有任何可设置的段（O:/G:/D:/S: 全缺）";
            return SddlApplyResult::Failed;
        }

        std::vector<unsigned char> sd;
        if (!SddlToSelfRelative(sddl, sd, perr)) {
            err = L"SDDL 转自相对描述符失败：" + perr;
            return SddlApplyResult::Failed;
        }
        if (sd.empty()) { err = L"自相对描述符为空"; return SddlApplyResult::Failed; }

        const DWORD rc = ::RegSetKeySecurity(
            static_cast<HKEY>(keyHandle), bits,
            reinterpret_cast<PSECURITY_DESCRIPTOR>(sd.data()));

        if (rc != ERROR_SUCCESS) {
            err = L"RegSetKeySecurity 失败，Win32 错误 " + std::to_wstring(rc);
            // ERROR_ACCESS_DENIED 有好几个**完全不同**的原因，必须分开说：混成一句"缺特权"
            // 会把人引到错的方向。实测踩过：写 DACL 被拒，真正的原因是**句柄上没有 WRITE_DAC**，
            // 而 `RegistryKey.CreateSubKey(..., writable: true)` 给的是 KEY_READ|KEY_WRITE，
            // KEY_WRITE 的值是 0x20006，**不包含** WRITE_DAC(0x40000)（含它的是 KEY_ALL_ACCESS）。
            // 只有特权是错的时候，"以 WRITE_DAC 重开键"才是白费力气。
            if (rc == ERROR_ACCESS_DENIED) {
                if (bits & (OWNER_SECURITY_INFORMATION | GROUP_SECURITY_INFORMATION)) {
                    err += L"：句柄未请求 WRITE_DAC，或进程缺 SeRestorePrivilege（写 owner/group 用它）";
                } else if (bits & SACL_SECURITY_INFORMATION) {
                    err += L"：句柄未请求 WRITE_DAC，或进程缺 SeSecurityPrivilege（写 SACL 用它）";
                } else {
                    err += L"：句柄上缺 WRITE_DAC —— 注意 KEY_WRITE 里没有 WRITE_DAC 这一位，"
                           L"打开键时必须显式请求 WRITE_DAC（或 KEY_ALL_ACCESS）";
                }
            }
            return SddlApplyResult::Failed;
        }
        return SddlApplyResult::Applied;
    }

    bool ReadKeySddl(void* keyHandle, unsigned informationBits,
                     std::wstring& out, std::wstring& err)
    {
        out.clear();
        err.clear();

        if (keyHandle == nullptr) { err = L"键句柄为空"; return false; }

        // 默认不读 SACL：读它需要 SeSecurityPrivilege，拿不到是常态；
        // 硬要读会让"验证"这一步在普通权限下整体失败，反而掩盖了 DACL 是否写对。
        SECURITY_INFORMATION want = informationBits != 0
            ? static_cast<SECURITY_INFORMATION>(informationBits)
            : (OWNER_SECURITY_INFORMATION | GROUP_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION);

        DWORD need = 0;
        DWORD rc = ::RegGetKeySecurity(static_cast<HKEY>(keyHandle), want, nullptr, &need);
        if (rc != ERROR_INSUFFICIENT_BUFFER || need == 0) {
            err = L"RegGetKeySecurity 探测长度失败，Win32 错误 " + std::to_wstring(rc);
            return false;
        }

        std::vector<unsigned char> buf(need);
        rc = ::RegGetKeySecurity(static_cast<HKEY>(keyHandle), want,
                                 reinterpret_cast<PSECURITY_DESCRIPTOR>(buf.data()), &need);
        if (rc != ERROR_SUCCESS) {
            err = L"RegGetKeySecurity 失败，Win32 错误 " + std::to_wstring(rc);
            return false;
        }

        return SelfRelativeToSddl(buf.data(), need, out, err);
    }

} // namespace ocid
