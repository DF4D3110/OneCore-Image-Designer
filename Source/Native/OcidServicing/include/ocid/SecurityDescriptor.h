#pragma once
//
// SecurityDescriptor.h —— 安全描述符（SDDL）模块
//
// 为什么需要它：清单里 `<registryKey>` / `<file>` / `<directory>` 会挂一个
//
//     <securityDescriptor name="WRP_REGKEY_DEFAULT_SDDL" />
//
// 这里只有**引用名**。SDDL 原文在别处定义：
//
//     <trustInfo><security><accessControl><securityDescriptorDefinitions>
//       <securityDescriptorDefinition name="WRP_REGKEY_DEFAULT_SDDL"
//                                     sddl="O:S-1-5-80-…D:P(A;;FA;;;S-1-5-80-…)"
//                                     operationHint="replace" />
//
// 规模（8400 x86 语料 70219 份清单实测）：
//   <securityDescriptor> 引用            107135 处（其中键上约 30712 个）
//   <securityDescriptorDefinition> 定义    47986 条
// 也就是说 ACL 不是边角料，漏掉它等于**所有键/目录/文件的安全属性都不对**。
//
// 本模块只负责"文本 → 描述符"，不碰注册表；落盘（RegSetKeySecurity）由调用方做。
// 这样绝大多数逻辑可以在**不需要管理员、不需要挂载 hive** 的情况下被测试。
//
#include <map>
#include <string>
#include <vector>

namespace ocid {

    // ─────────────────────────────────────────────────────────────────────────
    // 特殊值
    // ─────────────────────────────────────────────────────────────────────────

    /// `sddl="(inherit)"` —— **不是** SDDL，意思是"保持继承，别显式设置"。
    /// 真实出现在 OCID 自己生成的清单里（`DeviceLayoutBuilderService` 的 `SD0`）。
    /// 遇到它必须**跳过设置**，而不是把它当 SDDL 去解析（会失败或写出怪东西）。
    bool IsInheritSddl(const std::wstring& sddl);

    /// 定义表：`name → SDDL`（`ManifestDocument::securityDescriptorDefinitions` 就是它）。
    using SddlDefinitions = std::map<std::wstring, std::wstring>;

    /// 按引用名解析。名字是**大小写敏感**的（清单里大小写一致），
    /// 但为稳妥起见先精确匹配、再退化为忽略大小写匹配。
    bool ResolveSddl(const SddlDefinitions& defs, const std::wstring& name,
                     std::wstring& sddl, std::wstring& err);

    // ─────────────────────────────────────────────────────────────────────────
    // 文本级解读（不调用任何 Win32 API）
    // ─────────────────────────────────────────────────────────────────────────

    /// SDDL 的结构事实。用于决策与诊断 —— 例如"SACL 是不是空的"、
    /// "DACL 有没有被保护（P）"，这些不解析文本是看不出来的。
    struct SddlInfo
    {
        bool hasOwner = false;          // O:
        bool hasGroup = false;          // G:
        bool hasDacl  = false;          // D:
        bool hasSacl  = false;          // S:
        bool daclPresent   = false;     // D: 段存在（可能是空 DACL）
        bool daclProtected = false;     // D:P  —— 不继承父级
        bool daclAutoInherited = false; // D:AI —— 已自动继承
        /// `D:NO_ACCESS_CONTROL` —— 这是 **NULL DACL**（不是空 DACL！）：
        /// NULL DACL 等于"谁都全权"，是安全隐患而不是"没人有权"。
        /// 两者在 SDDL 里长得像但语义相反，必须分开记。
        bool daclNoAccessControl = false;
        bool saclProtected = false;
        bool saclAutoInherited = false;
        bool saclNoAccessControl = false;
        int  daclAceCount = 0;
        int  saclAceCount = 0;
        std::wstring ownerSid;          // O: 后面的 SID 文本（若有）
        std::wstring groupSid;          // G: 后面的 SID 文本（若有）

        int AceCount() const { return daclAceCount + saclAceCount; }
    };

    /// 纯文本解析。**不做** SID/ACE 的合法性校验（那是 Win32 的事），
    /// 只判断段结构、标志位与 ACE 计数，用来在落盘前做决策和告警。
    bool DescribeSddl(const std::wstring& sddl, SddlInfo& info, std::wstring& err);

    // ─────────────────────────────────────────────────────────────────────────
    // 与 Win32 的边界
    // ─────────────────────────────────────────────────────────────────────────

    /// SDDL 文本 → **自相对 SECURITY_DESCRIPTOR 字节**。
    /// 结果可以直接喂给 `RegSetKeySecurity` / `SetFileSecurity`。
    ///
    /// 说明：SID 与 ACE 的二进制编码是安全攸关的格式，自己重写没有收益，
    /// 这里调用 `advapi32!ConvertStringSecurityDescriptorToSecurityDescriptorW`
    /// —— 它与我们已经在用的 `RegSetValueEx` 属于同一类**基础 API**，
    /// 不是被替换掉的 CBS/wcp/CMI 服务栈语义。
    bool SddlToSelfRelative(const std::wstring& sddl,
                            std::vector<unsigned char>& out, std::wstring& err);

    /// 自相对 SD 字节 → SDDL 文本（回读校验、日志、测试用）。
    bool SelfRelativeToSddl(const unsigned char* data, std::size_t len,
                            std::wstring& out, std::wstring& err);

} // namespace ocid
