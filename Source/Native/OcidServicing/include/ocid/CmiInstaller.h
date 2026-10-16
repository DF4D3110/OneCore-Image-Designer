// OCID - CMI 注册表落地语义（Component Manager Installer）
//
// 这一层回答一个问题：**"清单声明了一条注册表值，现在 hive 里已经有（或没有）这个值，
// 到底该不该写、写什么"**。在它之前，我们只是"把清单里的值照抄进 hive"——
// 那是**错的**：真实服务栈在值已存在时会按 `owner` / `operationHint` 做决策。
//
// ── 归属（这一点之前搞错了）──────────────────────────────────────────────
//
// 清单里 **两套** 注册表声明分别归两个不同的引擎：
//
//   ① `<registryKeys>/<registryKey>/<registryValue>`  → **CMI**（cmiv2.dll 等）
//      父链 100% 一致：`assembly > registryKeys > registryKey > registryValue`（3165/3165 清单）
//      CMI 名字空间 `/$/registryKey/@*`：`create` `keyName` `mapDesiredAccessToMaxAllowedAccess`
//                    `migrate` `owner` `perUserVirtualization`
//      CMI 名字空间 `/$/registryValue/@*`：`create` `migrate` `name` `operationHint` `owner`
//                    `value` `valueType`
//      ⚠ `registryKeys` 还能嵌在 `/$/serviceData/` 下（安装为服务时）。
//
//   ② `<configuration>/<configurationSchema>/<xsd:element>`  → **SMI / WCM**（smiengine.dll）
//      这一套**没有** owner/operationHint 语义，语义就是"设置成这个值"。
//
//   ⇒ 本模块的决策语义**只对 ① 生效**（`mechanism == "registryKeys"`）。
//     ② 的操作走"直接写"。混用会把 WCM 的设置项错误地按 owner 规则跳过。
//
// ── 依据（反编译实证，不是推测）──────────────────────────────────────────
//
// 来源：`E:\ocid_decomp\sym8061x86_cmiv2.c`
//   `CMI::Utility::RegistryInstallerImpl::InternalInstall` @ 0x1010E1AD（size 0x724）
//   `CMI::Utility::RawRegistryValue::Merge`                @ 0x10110423（size 0x383）
//   `cmitrust.c`：`CMI::TrustInfo::AccessControlHelper::ApplyOperationHint` @ 0x1000E646
//   `wcp.c`：`wcp!AttributeParsing::MapRegistryValueOperationHint` @ 0x100BE90B
//
// `InternalInstall` 的写值决策（精确到分支）：
//
//   default OperationHint = 1 (replace)
//   Value = RegistryKey::GetValue(hKey, valueName, ...)
//
//   if (Value == 2 /*ERROR_FILE_NOT_FOUND，值不存在*/)
//       RegSetValueExW(...)                        // 直接写，**不看 owner**
//   else if (Value != 0) throw
//
//   // 值已存在：
//   switch (OperationHint) {
//     case 1: /* replace */
//       get_Owner(&bstr)
//       if ((WORD)bstr != 0)  → 【不写】 "Not setting value %S!%S as the component is an owner"
//       else                  → RegSetValueExW(...) 覆盖
//     case 2: /* append */  → Merge(existing, new)   // existing ++ new
//     case 3: /* prepend */ → Merge(new, existing)   // new ++ existing
//     default:              → 记 "Merge of value %S with existing value %S for key %S failed"，跳过
//   }
//   合并成功 → RegSetValueExW(merged)
//
// ⚠⚠ **`owner` 的真实语义**：值**已存在**且 hint=replace 时，
//     `owner=true` ⇒ **不覆盖**（"the component is an owner"）；`owner=false` ⇒ 覆盖。
//     这与"owner 决定卸载时是否删除"是**两件事**，之前只知后者、漏了前者。
//
// `OperationHint` 枚举（`W4OperationHint`）：`1=replace │ 2=append │ 3=prepend`。
// 其它值（含 0）抛 `E_INVALIDARG (0x80070057)`。清单里**缺省即 1**。
//
// `RawRegistryValue::Merge`：
//   类型不同          ⇒ 返回 false（合并失败，跳过写入）
//   REG_DWORD(4)      ⇒ 按位 OR                    （new | existing）
//   REG_SZ(1)/REG_EXPAND_SZ(2)/REG_MULTI_SZ(7) ⇒ 去掉尾 NUL 后字符串/数组拼接
//   其它（含 REG_QWORD） ⇒ 字节直接拼接
//   ⚠ REG_QWORD 落在"其它"里 ⇒ **字节拼接，不是按位 OR**（照代码，不照直觉）。
//
// ── 值文本形态（从 E:\sxs_lib 的 70224 份真实清单实测）───────────────────
//
//   REG_SZ / REG_EXPAND_SZ   直接是文本
//   REG_DWORD                十进制，或 `0x` 前缀
//   REG_QWORD                十六进制，**16 个字符**（如 `0000000000000100`）
//   REG_BINARY               十六进制串
//   REG_RESOURCE_LIST /
//   REG_RESOURCE_REQUIREMENTS_LIST   也是十六进制串
//   REG_NONE                 空
//   ★ REG_MULTI_SZ           **逗号分隔的带引号项**：`"a","b","c"`
//                            （XML 里是 `&quot;a&quot;,&quot;b&quot;`；单条目就是 `"a"`）
//   实测规模：REG_SZ 94218 │ REG_DWORD 15643 │ REG_EXPAND_SZ 9325 │ REG_BINARY 2376
//             REG_MULTI_SZ 516 │ REG_QWORD 501 │ REG_NONE 158 │ RESOURCE_* 10
//   实测 operationHint：replace 122722 │ append 24 │ prepend 1

#pragma once

#include "ocid/WcmSchema.h"

#include <cstdint>
#include <string>
#include <vector>

namespace ocid {

    // ─────────────────────────────────────────────────────────────────────────
    // 注册表类型
    // ─────────────────────────────────────────────────────────────────────────

    /// 我们把清单里的 `valueType` 归一化成的种类。
    enum class RegValueKind
    {
        Unknown = 0,
        Sz,           // REG_SZ
        ExpandSz,     // REG_EXPAND_SZ
        MultiSz,      // REG_MULTI_SZ
        Dword,        // REG_DWORD
        Qword,        // REG_QWORD
        Binary,       // REG_BINARY
        None,         // REG_NONE
        ResourceList, // REG_RESOURCE_LIST
        ResourceRequirementsList, // REG_RESOURCE_REQUIREMENTS_LIST
    };

    /// `valueType` 文本 → 种类（大小写不敏感）。
    RegValueKind ClassifyValueType(const std::wstring& valueType);

    /// 种类的规范文本名（写日志用）。
    const wchar_t* ValueKindName(RegValueKind kind);

    /// 该种类是否是"字符串类"（Merge 时按去尾 NUL 拼接）。
    bool IsStringKind(RegValueKind kind);

    // ─────────────────────────────────────────────────────────────────────────
    // 原始值（与 Win32 一致：类型 + 字节）
    // ─────────────────────────────────────────────────────────────────────────

    struct RawRegistryValue
    {
        bool                 exists = false;  // hive 里是否已有这个值
        std::wstring         valueType;       // REG_SZ / REG_DWORD / …
        std::vector<uint8_t> data;            // 原始字节（REG_DWORD 是 4 字节小端）

        /// 排障用：把字节还原成可读文本（二进制类给十六进制）。
        std::wstring ToText() const;
    };

    // ─────────────────────────────────────────────────────────────────────────
    // 值编码：清单文本 → 原始字节
    // ─────────────────────────────────────────────────────────────────────────

    /// 把清单里的 `value` 文本按 `valueType` 编码成注册表原始字节。
    ///
    /// ⚠ 字符串类**带结尾 NUL**（REG_SZ/EXPAND_SZ 一个，REG_MULTI_SZ 两个）——
    ///   真实服务栈调 `RegSetValueExW` 时是把 NUL 一起写进去的。
    bool EncodeValueData(RegValueKind kind, const std::wstring& text,
                         std::vector<uint8_t>& out, std::wstring* error);

    /// 字节 → 文本（往返验证 / 排障用）。字符串类去掉结尾 NUL。
    std::wstring DecodeValueData(RegValueKind kind, const std::vector<uint8_t>& data);

    /// 把一条操作编码成待写入的原始值。`mechanism` 不影响编码。
    bool EncodeOperation(const WcmRegistryOperation& op,
                         std::wstring& valueType, std::vector<uint8_t>& data,
                         std::wstring* error);

    // ─────────────────────────────────────────────────────────────────────────
    // 合并（RawRegistryValue::Merge 的忠实移植）
    // ─────────────────────────────────────────────────────────────────────────

    /// `Merge(target, source)`：结果写回 target。
    ///
    /// 语义（照反编译）：
    ///   - 类型不同 ⇒ false
    ///   - REG_DWORD ⇒ 按位 OR
    ///   - REG_SZ / REG_EXPAND_SZ / REG_MULTI_SZ ⇒ 去掉尾 NUL 后拼接
    ///   - 其它（含 REG_QWORD）⇒ 字节直接拼接
    bool MergeRawValues(const RawRegistryValue& target,
                        const RawRegistryValue& source,
                        RawRegistryValue& out,
                        std::wstring* error);

    // ─────────────────────────────────────────────────────────────────────────
    // operationHint
    // ─────────────────────────────────────────────────────────────────────────

    enum class OperationHint
    {
        Replace = 1,   // 缺省
        Append  = 2,
        Prepend = 3,
    };

    /// 文本 → 枚举。**空串或缺省按 1=replace**（代码里 `v48 = 1` 是初值）。
    /// 能解析成数字但不在 1/2/3 的 ⇒ 返回 false（对应 `E_INVALIDARG`）。
    bool ParseOperationHint(const std::wstring& text, OperationHint& out);

    const wchar_t* OperationHintName(OperationHint hint);

    /// 这一步在真实服务栈里是 `AttributeParsing::MapRegistryValueOperationHint`：
    /// 清单解析期就把非法值怼出来，而不是等落地时才炸。
    bool ValidateOperationHint(const std::wstring& text, std::wstring* error);

    // ─────────────────────────────────────────────────────────────────────────
    // 落地决策
    // ─────────────────────────────────────────────────────────────────────────

    enum class WriteAction
    {
        Write,             // 写（新增，或按 hint 覆盖/合并后写）
        SkipOwned,         // 值已存在 + hint=replace + owner=true ⇒ 不写
        SkipMergeFailed,   // 合并失败（类型不同等）⇒ 不写
        InvalidHint,       // operationHint 非法 ⇒ 对应 E_INVALIDARG，不写
    };

    struct WriteDecision
    {
        WriteAction          action = WriteAction::Write;
        std::wstring         valueType;   // 最终写入的类型
        std::vector<uint8_t> data;        // 最终写入的数据
        std::wstring         note;        // 排障说明（为什么这么决定）

        bool WillWrite() const { return action == WriteAction::Write; }
    };

    /// ★ 核心：对着"hive 里的现状"算出一条操作该不该写、写什么。
    ///
    /// ⚠ **只对 `mechanism == "registryKeys"`（CMI）套用 owner/operationHint 语义**；
    ///   其余（WCM 设置）语义就是"设置成这个值"，值存在就覆盖。
    ///
    /// `existing.exists == false` ⇒ 直接写（不看 owner）。
    /// `existing.exists == true`  ⇒ 按上述分支决策。
    ///
    /// 返回 false 表示**操作本身有问题**（编码失败／hint 非法），`error` 里有原因；
    /// 返回 true 时看 `out.action` 决定写不写。
    bool DecideRegistryWrite(const WcmRegistryOperation& op,
                             const RawRegistryValue& existing,
                             WriteDecision& out,
                             std::wstring* error);

    /// 决策的文本描述（日志用）。
    const wchar_t* WriteActionName(WriteAction action);

} // namespace ocid
