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

        /// DACL 里每条 ACE 的 **规范 SID 文本**（`S-1-5-…`，由
        /// `ConvertSidToStringSidW` 从二进制 ACE 里取），顺序与 ACE 一致。
        ///
        /// 【为什么需要】回读校验不能比 ACE 条数：Windows **应用** DACL 时会把
        /// "继承专用"（`IO`）ACE 拆成"对本对象生效"+"仅继承"两条 —— 实测把
        /// 8 条 ACE 的 `D:P(A;CI;GA;;;TI)…(A;CIOI;GRGX;;;BU)…` 落到目录上，
        /// 回读是 **11 条**（SID 集合不变、顺序变成规范序）。所以能比的稳定事实是
        /// **SID 集合**与"不少于"的条数。
        ///
        /// 别名会在规范化时被消掉（`BA` → `S-1-5-32-544`），这正是要的效果：
        /// 文本里写 `BA`、回读写 `S-1-5-32-544` 属于同一件事。
        std::vector<std::wstring> daclSids;
        /// `daclSids` 是否**可信**（二进制还原成功且 ACE 类型都认得）。
        /// 为 false 时调用方只能退回"条数不少于"的弱比对。
        bool daclSidsAvailable = false;

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

    // ─────────────────────────────────────────────────────────────────────────
    // 键的所有权（离线镜像里写 DriverDatabase 时唯一的进门钥匙）
    // ─────────────────────────────────────────────────────────────────────────
    //
    // 实测：8061 镜像的 `config\DRIVERS`（就是驱动数据库 hive）里，
    // `DriverDatabase` 键连**枚举**都拒管理员（Access denied）。这不是权限没配好，
    // 而是它的 owner/DACL 属于 SYSTEM/TrustedInstaller —— 和 WinSxS 是同一类问题。
    //
    // 这时唯一能进门的钥匙是 `SeTakeOwnershipPrivilege`：拿到所有权之后，WRITE_DAC
    // 自然到手，才能把原来的 DACL 读出来（`priorSddl`）以便**用完还原**。
    //
    // 语义：
    //   · 成功 ⇒ `priorSddl` = 夺权前的 SDDL（读不到时为空串，调用方须当成"无法还原"）；
    //     键的 owner 变成当前用户，DACL 换成"当前用户 KEY_ALL_ACCESS"的临时 ACL；
    //   · 失败 ⇒ 什么都没改（读完旧 SDDL 之后才动 DACL，失败点不会留下半改状态）。
    bool TakeKeyOwnership(void* rootKey, const std::wstring& subKey,
                          std::wstring& priorSddl, std::wstring& err);

    /// 返回当前进程用户 SID 的字符串形式（`S-1-5-21-…`）。拿不到返回 false。
    bool CurrentUserSidString(std::wstring& out, std::wstring& err);

    // ─────────────────────────────────────────────────────────────────────────
    // 整棵键树的所有权（DriverDatabase 是**树**，只拿下根键不够）
    // ─────────────────────────────────────────────────────────────────────────
    //
    // 实测（8061 真机 hive）：拿下 `DriverDatabase` 的 owner+DACL 之后，
    // 写 `DriverDatabase\DriverPackages\…` 仍然 Access denied —— 子键各有自己的
    // 受保护 DACL，**继承 ACE 的传播只发生在"创建"时，不会补写已存在的子键**
    // （把 ACE 设成可继承并重设父键 DACL 也不行，实测无效）。所以只能逐键夺权。
    //
    // 语义：
    //   · `priors` 收集 `(相对路径, 夺权前 SDDL)`，**深度优先、父在前**；
    //   · 调用方必须用 `RestoreKeyTreeSddl` 按**逆序**还原（先子后父）。

    /// 逐键夺权整棵树（含 `subKey` 自身），把每层夺权前的 SDDL 收进 `priors`。
    bool TakeKeyTreeOwnership(void* rootKey, const std::wstring& subKey,
                              std::vector<std::pair<std::wstring, std::wstring>>& priors,
                              std::wstring& err);

    /// 按逆序把 `priors` 里的 SDDL 逐键还原（owner + DACL + 继承标志）。
    bool RestoreKeyTreeSddl(void* rootKey,
                            const std::vector<std::pair<std::wstring, std::wstring>>& priors,
                            std::wstring& err);

} // namespace ocid
