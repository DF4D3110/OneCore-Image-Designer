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
// 本模块负责"文本 → 描述符"，并提供把它写进**已打开的键句柄**的动作与回读；
// hive 的挂载/卸载仍由调用方负责（见 Services/OcidSmiService.cs）。这样绝大多数逻辑
// 可以在**不需要管理员、不需要挂载 hive** 的情况下被测试。
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

    // ─────────────────────────────────────────────────────────────────────────
    // 落盘：把 SDDL 应用到键（RegSetKeySecurity）与回读
    // ─────────────────────────────────────────────────────────────────────────

    /// 这段 SDDL 需要设置哪些 `SECURITY_INFORMATION` 位。
    ///
    /// 由 `SddlInfo` 的**段存在性**决定，而不是"全都设一遍" —— 清单里只写了 `D:` 的段落，
    /// 就不该去动 owner/group；否则会把镜像里原有的 owner 覆盖成当前用户。
    unsigned SddlSecurityInformation(const SddlInfo& info);

    enum class SddlApplyResult
    {
        Applied,         // 已写入
        SkippedInherit,  // sddl == "(inherit)"：按语义跳过，**不是**失败
        Failed,          // 失败，err 里有原因
    };

    /// 把一个 SDDL 应用到已打开的键句柄（`HKEY`，以 `void*` 传以避免头文件暴露 windows.h）。
    ///
    /// 需要的特权（缺了会返回 `ERROR_ACCESS_DENIED`，由调用方先行启用）：
    ///   · 写 owner（`O:`）        → `SeRestorePrivilege`（或 `SeTakeOwnershipPrivilege`）
    ///   · 写 SACL（`S:`）         → `SeSecurityPrivilege`
    ///   · 只写 DACL（`D:`）       → 键的 owner 即可，通常不需要额外特权
    SddlApplyResult ApplySddlToKey(void* keyHandle, const std::wstring& sddl, std::wstring& err);

    /// 回读键的安全描述符并转成 SDDL 文本。
    ///
    /// 【为什么必须能回读】"写进去"不算完成，"读回来逐字符对上"才算 —— 与注册表值那边
    /// 同一条规矩（见 RegistryValue.h 的三重证据）。`informationBits` 为 0 时读
    /// OWNER|GROUP|DACL：读 SACL 需要 `SeSecurityPrivilege`，拿不到是常态，所以默认不读，
    /// 由调用方按需显式传入 SECURITY_INFORMATION 位。
    bool ReadKeySddl(void* keyHandle, unsigned informationBits,
                     std::wstring& out, std::wstring& err);

    // ─────────────────────────────────────────────────────────────────────────
    // 落盘：把 SDDL 应用到**文件/目录**（SetNamedSecurityInfo / GetNamedSecurityInfo）
    // ─────────────────────────────────────────────────────────────────────────
    //
    // 为什么和键那条分开：句柄模型不同（文件按**路径**打开，键按已有 HKEY），API 也不同
    // （`SetNamedSecurityInfoW` vs `RegSetKeySecurity`）。清单里 `<file>` / `<directory>`
    // 内嵌的 `<securityDescriptor name="…"/>` 走的就是这条路。
    //
    // `informationBits` 同样由 `SddlSecurityInformation` 决定，理由与键一致：只写 `D:`
    // 的清单不该顺手把文件的 owner 改成当前用户。
    //
    // 需要的特权（缺了会 `ERROR_ACCESS_DENIED`，由调用方先行启用）：
    //   · 写 owner（`O:`） → `SeRestorePrivilege` / `SeTakeOwnershipPrivilege`
    //   · 写 SACL（`S:`）  → `SeSecurityPrivilege`
    //   · 只写 DACL（`D:`）→ 文件/目录的 owner 即可（**注意**：目录上若是受保护 DACL，
    //     写入后不再从父目录继承 —— 这正是清单声明的意图，不是副作用）

    /// 把一个 SDDL 应用到文件或目录路径。
    SddlApplyResult ApplySddlToPath(const std::wstring& path, const std::wstring& sddl,
                                    std::wstring& err);

    /// 回读文件/目录的安全描述符并转成 SDDL 文本（与键那条同样的"必须能回读"规矩）。
    bool ReadPathSddl(const std::wstring& path, unsigned informationBits,
                      std::wstring& out, std::wstring& err);

    /// 路径是否存在（计划阶段判"这条要不要落"用；`ApplySddlToPath` 自己不检查）。
    bool PathExists(const std::wstring& path);

} // namespace ocid
