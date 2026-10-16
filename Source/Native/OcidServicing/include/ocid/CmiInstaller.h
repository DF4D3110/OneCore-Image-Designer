// OCID - CMI 注册表**落地决策**（Component Manager Installer）
//
// 这一层只回答一个问题：**"清单声明了一条注册表值，现在 hive 里已经有（或没有）这个值，
// 到底该不该写、写什么"**。
//
// 编码/合并/类型解析这些"原语"不在本文件 —— 它们在 `ocid/RegistryValue.h`
// （值文本 → hive 字节、`Merge`、operationHint 表、键保护标志）。
// 本文件只做**决策**，且决策要拿"hive 里的现状"当输入。
//
// ── 为什么需要这一层（在它之前我们是错的）─────────────────────────────────
//
// 原先的做法是"把清单里的值照抄进 hive"。这在"值已存在"时**是错的**：
// 真实服务栈（`cmiv2.dll` 的 `CMI::Utility::RegistryInstallerImpl::InternalInstall`
// @ 0x1010E1AD）会按 `owner` / `operationHint` 分叉，其中：
//
//   · `owner=true` + hint=replace + **值已存在** ⇒ **不覆盖**
//     （trace 原文 `"Not setting value %S!%S as the component is an owner"`）。
//     ⚠ 这与"owner 决定组件卸载时是否删除该值"是**两件事**——我原先只知后者，
//       漏了前者，于是会把 owner 组件已有的值无条件盖掉。
//   · hint=append / prepend ⇒ 与现有值**合并**（`RawRegistryValue::Merge`）。
//   · 值**不存在** ⇒ 直接写，owner 完全不参与。
//
// 分支细节与依据（反编译行号、类型判据）见 `ocid/RegistryValue.h` 的
// `RegValueOperationHint` 注释块，那里是唯一记录处，本文件不重复。
//
// ── 只对 CMI 生效（这一点之前搞错了）──────────────────────────────────────
//
// 清单里 **两套** 注册表声明归两个不同的引擎：
//
//   ① `<registryKeys>/<registryKey>/<registryValue>`  → **CMI**（cmiv2.dll 等）
//      父链 100% 一致：`assembly > registryKeys > registryKey > registryValue`（3165/3165 清单）
//      名字空间 `/$/registryKey/@*`：`create` `keyName` `mapDesiredAccessToMaxAllowedAccess`
//                    `migrate` `owner` `perUserVirtualization`
//      名字空间 `/$/registryValue/@*`：`create` `migrate` `name` `operationHint` `owner`
//                    `value` `valueType`
//      ⚠ `registryKeys` 还能嵌在 `/$/serviceData/` 下（安装为服务时）。
//      ⇒ **只有这一套**带 `owner` / `operationHint` 语义。
//
//   ② `<configuration>/<configurationSchema>/<xsd:element>`  → **SMI / WCM**（smiengine.dll）
//      这一套**没有** owner/operationHint：语义就是"设置成这个值"，值存在就覆盖。
//
//   ⇒ 本模块的 owner/operationHint 决策**只对 ①（`mechanism == "registryKeys"`）套用**。
//     把 ② 也按 owner 规则跳过，会静默丢掉 WCM 的设置项。
//
// 实测规模（70224 份真实组件清单）：operationHint `replace` 122722 │
// `append` 24 │ `prepend` 1。也就是说 append/prepend 极少但**真实存在**，
// 不能因为"几乎见不到"就当成不会发生。

#pragma once

#include "ocid/RegistryValue.h"
#include "ocid/WcmSchema.h"

#include <cstdint>
#include <string>
#include <vector>

namespace ocid {

    // ─────────────────────────────────────────────────────────────────────────
    // hive 里的现状
    // ─────────────────────────────────────────────────────────────────────────

    /// 目标值在 hive 里的当前状态。`exists == false` 时其余字段无效。
    struct ExistingValue
    {
        bool                 exists = false;
        uint32_t             typeCode = 0;   // 真实 Win32 REG_* 码
        std::vector<uint8_t> data;           // 原始字节（含结尾 NUL）

        /// 排障用：按类型还原成可读文本（二进制类给 hex）。
        std::wstring ToText() const;
    };

    // ─────────────────────────────────────────────────────────────────────────
    // 决策结果
    // ─────────────────────────────────────────────────────────────────────────

    enum class WriteAction
    {
        Write,            // 写：值是新增，或按 hint 覆盖/合并后写
        SkipOwned,        // 值已存在 + hint=replace + owner=true ⇒ 不写
        SkipMergeFailed,  // 合并失败（类型不同 / 长度非法）⇒ 不写
        InvalidHint,      // operationHint 非法 ⇒ 对应 E_INVALIDARG，不写
    };

    struct WriteDecision
    {
        WriteAction          action = WriteAction::Write;
        uint32_t             typeCode = 0;   // 最终写入的类型码
        std::vector<uint8_t> data;           // 最终写入的字节
        std::wstring         note;           // 为什么这么决定（排障）

        bool WillWrite() const { return action == WriteAction::Write; }
    };

    /// ★ 核心：对着"hive 里的现状"算出一条操作该不该写、写什么。
    ///
    /// 决策（照 `InternalInstall` 的分支）：
    ///   · `mechanism != "registryKeys"`（即 SMI/WCM 那套）⇒ 直接写（存在就覆盖），
    ///     不看 owner、不解析 operationHint；
    ///   · 值不存在 ⇒ 直接写（owner 不参与）；
    ///   · 值已存在 + hint=replace ⇒ `owner==true` 则 `SkipOwned`，否则覆盖写；
    ///   · 值已存在 + hint=append  ⇒ `Merge(existing, incoming)`；
    ///   · 值已存在 + hint=prepend ⇒ `Merge(incoming, existing)`；
    ///   · hint 无法解析成 1/2/3 ⇒ `InvalidHint`（**不抛**，返回 true + 该 action）。
    ///
    /// 返回 **false** 表示操作本身有问题（类型无法解析 / 值无法编码），`error` 里有原因；
    /// 返回 true 时看 `out.action` 决定写不写 —— 注意 `SkipOwned` 是**正常路径**，不是错误。
    ///
    /// `existing.typeCode` 与 `data` 在 `existing.exists == false` 时被忽略。
    bool DecideRegistryWrite(const WcmRegistryOperation& op,
                             const ExistingValue& existing,
                             WriteDecision& out,
                             std::wstring* error);

    /// 决策的文本名（日志 / JSONL 用）。
    const wchar_t* WriteActionName(WriteAction action);

} // namespace ocid
