// OCID - 注册表值类型与值数据编码
//
// 这一层回答一个问题：清单里写着 `valueType="REG_QWORD" value="0001000000000000"`，
// **最终落到 hive 文件里的到底是哪几个字节、什么类型码**。
//
// 这是"服务栈语义"，不是"格式偏好"，所以只能照抄，不能凭直觉。这里的每一条规则
// 都有出处，分三类证据：
//   [表] 从 wcp.dll 的只读表里直接 dump 出来的权威枚举
//   [语料] 全部 70224 份真实组件清单里的取值分布
//   [活机] 拿清单声明去查**本机已装好的真实注册表**，逐条比对（最硬的证据）
//
// ── 合法 valueType 全集（14 个）[表] ────────────────────────────────────────
//   wcp.dll 的 `AttributeParsing::MapRegistryValueTypeFlags` @0x100BE8A6 走
//   `StringToValueLookup<TRegValueTypeMap,unsigned long>`，表体在 VA 0x1000F800，
//   值在 VA 0x1000F80C，**14 条**（独立的 `AttributeValidation::ValidateRegValueType`
//   @0x100BEC85 用 `v4 += 16; if (v4 == 224)` 交叉印证了 14 这个数目）。
//   逐条 dump 出来的映射（注意：这是 **wcp 自己的枚举**，不是 Win32 类型码）：
//
//     REG_SZ=1  REG_EXPAND_SZ=2  REG_MULTI_SZ=3  REG_BINARY=4  REG_DWORD=5
//     REG_DWORD_LITTLE_ENDIAN=6  REG_DWORD_BIG_ENDIAN=7  REG_LINK=8
//     REG_RESOURCE_LIST=9  REG_FULL_RESOURCE_DESCRIPTOR=10
//     REG_RESOURCE_REQUIREMENTS_LIST=11  REG_QWORD=12  REG_QWORD_LITTLE_ENDIAN=13
//     REG_NONE=0
//
//   ⚠ 别把这个枚举当成 Win32 码用：这里 `REG_BINARY` 是 4，而 Win32 是 3；
//     `REG_DWORD` 是 5，而 Win32 是 4。两者必须分开，见 Win32RegType()。
//
// ── 值文本 → 字节 的规则（全部实测）─────────────────────────────────────────
//
//   REG_DWORD（15641 条）[语料]：**100% 是长度 10 的 `0x%08X`**，无例外。
//
//   REG_QWORD（501 条）[语料]+[活机]：**100% 是长度 16 的 hex**。
//     但它是**数值**还是**内存镜像**？两种读法会给出完全不同的结果，
//     所以拿本机真实的 ETW 关键字值逐条对照（WDI\Scenarios\{…}\Instrumentation\{…};N
//     下的 `Keyword`，正是 ETW 位掩码）：
//
//       清单值              活注册表      字节镜像读法（LE）        数值读法
//       0800000000000000 →  0x8          0x0000000000000008  ✅    0x0800000000000000 ✗
//       0001000000000000 →  0x100        0x0000000000000100  ✅    0x0001000000000000 ✗
//       0000000200000000 →  0x2000000    0x0000000002000000  ✅    0x0000000200000000 ✗
//       → **23/23 命中"字节镜像"，0 反例** ⇒ 这 16 个 hex 字符就是那 8 个字节，
//         按**低地址到高地址**书写。**不做任何数值解释、不做大小端转换。**
//
//   REG_MULTI_SZ（516 条）[语料]+[活机]：XML 里写成 `&quot;a&quot;,&quot;b&quot;`
//     （`&quot;` 是 XML 转义，解析后就是普通双引号）。
//     **493 条全部带引号，0 条不带** ⇒ 引号不是数据，是**列表项定界符**。
//     活机对照：清单 `&quot;%SystemRoot%\system32\shell32.dll,-229&quot;`
//              活注册表 `%SystemRoot%\system32\shell32.dll,-229`（**引号没了**）✅
//     ⇒ 去掉最外层引号，按 `","` 切分成项，项之间用 L'\0' 连接，末尾再加 L'\0'。
//
//   REG_BINARY / REG_RESOURCE_LIST / REG_FULL_RESOURCE_DESCRIPTOR /
//   REG_RESOURCE_REQUIREMENTS_LIST [语料]：**裸 hex 串**，就是字节序列，原样转换。
//
//   REG_NONE（158 条）[语料]：**全部为空** ⇒ 零字节数据。
//
//   REG_SZ / REG_EXPAND_SZ / REG_LINK：字面文本（UTF-16LE + 结尾 NUL）。
//
// ── 还没解决的：`$(…)` 宏 ───────────────────────────────────────────────────
//   语料里有 **5277 条 REG_SZ**、**426 条 REG_EXPAND_SZ**、**12 条 REG_MULTI_SZ**
//   的值里含 `$(runtime.startMenu)`／`$(runtime.system32)`／`$(runtime.public)` 这类宏。
//   宏的展开表还没从服务栈里挖出来，所以这里**不猜**：原样保留，并把
//   `hasUnresolvedMacro` 标出来，由上层决定是警告还是拒绝。
//   （真相是：直接把 `$(runtime.…)` 写进 hive 一定是错的，但静默展开成一个
//     我自己编的路径更错——宁可显式暴露。）

#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace ocid {

    // ─────────────────────────────────────────────────────────────────────────
    // 注册表值类型
    // ─────────────────────────────────────────────────────────────────────────

    /// wcp.dll `TRegValueTypeMap` 里的 14 个合法类型（与 Win32 码无关）。
    enum class RegValueType
    {
        Unknown = -1,
        None = 0,                       // REG_NONE
        Sz = 1,                         // REG_SZ
        ExpandSz = 2,                   // REG_EXPAND_SZ
        MultiSz = 3,                    // REG_MULTI_SZ
        Binary = 4,                     // REG_BINARY
        Dword = 5,                      // REG_DWORD
        DwordLittleEndian = 6,          // REG_DWORD_LITTLE_ENDIAN
        DwordBigEndian = 7,             // REG_DWORD_BIG_ENDIAN
        Link = 8,                       // REG_LINK
        ResourceList = 9,               // REG_RESOURCE_LIST
        FullResourceDescriptor = 10,    // REG_FULL_RESOURCE_DESCRIPTOR
        ResourceRequirementsList = 11,  // REG_RESOURCE_REQUIREMENTS_LIST
        Qword = 12,                     // REG_QWORD
        QwordLittleEndian = 13,         // REG_QWORD_LITTLE_ENDIAN
    };

    /// 解析 `valueType` 文本。**大小写不敏感**（wcp 的表查找用 FsnpDowncaseChar）。
    /// 只认上面 14 个名字，别的一律失败——不要"猜个近似的"。
    bool ParseRegValueType(const std::wstring& text, RegValueType& out);

    /// 规范名（`L"REG_QWORD"` 等）；未知返回 `L""`。
    const wchar_t* RegValueTypeName(RegValueType t);

    /// wcp 枚举 → **真实 Win32 `REG_*` 类型码**（写 `RegSetValueExW` 时用的那个）。
    /// 注意大小端别名要折叠：DWORD_LITTLE_ENDIAN→REG_DWORD(4)、
    /// QWORD_LITTLE_ENDIAN→REG_QWORD(11)、BIG_ENDIAN→5。
    /// 未知返回 `0xFFFFFFFF`（调用方必须当错误处理，不能当 REG_NONE 写下去）。
    uint32_t Win32RegType(RegValueType t);

    // ─────────────────────────────────────────────────────────────────────────
    // 值数据编码
    // ─────────────────────────────────────────────────────────────────────────

    /// 编码结果：**精确**要写进 hive 的字节 + 真实类型码。
    struct RegValueBlob
    {
        bool                 ok = false;
        std::wstring         error;
        uint32_t             typeCode = 0;   // Win32 REG_* 码
        std::vector<uint8_t> bytes;          // 原样写进 hive
        /// 值里含未展开的 `$(…)` 宏 —— 上层应据此告警（见文件头说明）。
        bool                 hasUnresolvedMacro = false;
    };

    /// 按类型把清单里的值文本编码成字节。
    ///
    /// 失败（`ok=false`）而不是"尽力而为"的情形：
    ///   · 类型未知
    ///   · REG_DWORD 文本不是合法数值
    ///   · REG_QWORD 不是 16 位 hex
    ///   · REG_BINARY / REG_RESOURCE_* 不是合法（偶数长度）hex
    /// 失败时 `bytes` 为空、`error` 说明原因；**调用方不得用空 bytes 顶替写下去**。
    RegValueBlob EncodeRegValue(RegValueType type, const std::wstring& textual);

    /// 便捷重载：先按名解析类型，再编码（类型名非法时报错）。
    RegValueBlob EncodeRegValue(const std::wstring& typeName, const std::wstring& textual);

    /// 把字节转成大写 hex（导出 JSONL / 排障用）。
    std::wstring BytesToHex(const std::vector<uint8_t>& bytes);

    // ─────────────────────────────────────────────────────────────────────────
    // 键保护标志（`systemProtection` 属性）
    // ─────────────────────────────────────────────────────────────────────────
    //
    // [表] wcp.dll `AttributeParsing::MapSystemProtectionFlags` @0x100BE8D9 走
    //   `StringToValueLookup<TRegKeyProtectionFlagsMap,unsigned long>`，表体 VA
    //   0x1000F760，值 VA 0x1000F76C，**10 条**
    //   （`AttributeValidation::ValidateSystemProtection` @0x100BECEF 的
    //    `v4 += 16; if (v4 == 160)` 交叉印证）。dump 出来：
    //
    //     readOnlyIgnoreWrites=1  readOnlyFailWrites=2  OSOnlyIgnoreWrites=3
    //     OSOnlyFailWrites=4  transacted=5  applicationVirtualized=6
    //     userVirtualized=7  applicationAndUserVirtualized=8  inherit=9
    //     notProtected=10
    enum class RegKeyProtection
    {
        Unknown = -1,
        ReadOnlyIgnoreWrites = 1,
        ReadOnlyFailWrites = 2,
        OSOnlyIgnoreWrites = 3,
        OSOnlyFailWrites = 4,
        Transacted = 5,
        ApplicationVirtualized = 6,
        UserVirtualized = 7,
        ApplicationAndUserVirtualized = 8,
        Inherit = 9,
        NotProtected = 10,
    };

    bool ParseRegKeyProtection(const std::wstring& text, RegKeyProtection& out);
    const wchar_t* RegKeyProtectionName(RegKeyProtection p);

    // ─────────────────────────────────────────────────────────────────────────
    // operationHint（写值决策）
    // ─────────────────────────────────────────────────────────────────────────
    //
    // [表] wcp.dll `AttributeParsing::MapRegistryValueOperationHint` @0x100BE90B，
    //   表体 VA 0x1000F730，**3 条**（`v4 += 16; if (v4 == 48)` 印证）：
    //     replace=1  append=2  prepend=3
    //
    // [代码] cmiv2.dll `CMI::Utility::RegistryInstallerImpl::InternalInstall` @0x1010E1AD
    //   的写值决策（`get_OperationHint(&v48)` 的 **默认值是 1 = replace**）：
    //
    //     Value = RegistryKey::GetValue(hKey, valueName, ...);
    //     if (Value == 2 /*ERROR_FILE_NOT_FOUND：值不存在*/)
    //         RegSetValueExW(hKey, valueName, 0, dwType, lpData, cbData);  // 直接写，不看 owner
    //     else if (Value != 0) throw;
    //     // 值已存在：
    //     switch (hint) {
    //       case 1 /* replace */:
    //         get_Owner(&bstr);
    //         if (bstr != 0) skip;      // owner=true ⇒ **不覆盖**（"Not setting value %S!%S…"）
    //         else RegSetValueExW(...); // owner=false ⇒ 覆盖
    //       case 2 /* append  */: Merge(existing, new);
    //       case 3 /* prepend */: Merge(new, existing);
    //       default: 记 trace 并**跳过**（不抛）
    //     }
    //
    //   ⚠ **`owner` 的真义（和直觉相反）**：它只在"值已存在且 hint=replace"时起作用；
    //     `owner=true` ⇒ **不覆盖**（这个组件拥有它，别人不该动）；
    //     `owner=false` ⇒ **覆盖**。值不存在时 owner 完全不参与。
    //     我原先把它当成"卸载时才相关"是错的。
    enum class RegValueOperationHint
    {
        Unknown = -1,
        Replace = 1,
        Append = 2,
        Prepend = 3,
    };

    bool ParseRegValueOperationHint(const std::wstring& text, RegValueOperationHint& out);
    const wchar_t* RegValueOperationHintName(RegValueOperationHint h);

    /// append / prepend 用的合并（`CMI::Utility::RawRegistryValue::Merge` @0x10110423）。
    ///
    /// [代码] 反编译出来的规则（cmiv2.c L240563-L240690）：
    ///   · 两个值的**类型不同** ⇒ 返回失败（0），调用方跳过写入（`this[7] != a2[7]`）
    ///   · `typeCode == 4`（Win32 `REG_DWORD`）⇒ **按位 OR**，且 L240580/L240585 要求
    ///     **两边长度都正好 4**，否则抛 `CmiException(0x80070057)` ⇒ 这里返回 false
    ///   · `typeCode ∈ {1,2,6,7}`（`REG_SZ`/`REG_EXPAND_SZ`/`REG_LINK`/`REG_MULTI_SZ`）
    ///     ⇒ 字符串通路。判据来自 L240576 (`v8 <= 2`) 与 L240598 (`v8 > 5 && v8 <= 7`)，
    ///     且 L240601/L240606 要求两边长度都 ≥ 2。
    ///     ★ **只从 target 去掉一个宽 NUL，source 整体接上**：
    ///       `newLen = thisLen - 2 + a2Len`（L240634）
    ///       `memcpy(dst, this->data, thisLen - 2)`（L240648）
    ///       `memcpy(dst + thisLen - 2, a2->data, a2Len)`（L240656）
    ///     别写成"两边都去 NUL 再补一个"——在 `REG_SZ` 上碰巧等价，在 `REG_MULTI_SZ`
    ///     上会少一个空项。（我第一版就是这么写的，被测试逮住了。）
    ///   · 其它类型 ⇒ `newLen = thisLen + a2Len`，两边整体直接拼接（L240667/L240684/L240686）
    ///
    /// ⚠ `RawRegistryValue` 的 `+7` 字段就是**真实 Win32 类型码**——上面那组判据
    ///   1/2/4/6/7 只有按 Win32 码才自洽（wcp 枚举与 smiengine 枚举都对不上）。
    ///   ⇒ `Win32RegType()` 的输出正是这个函数要的输入。
    ///
    /// `hint=append` 时结果是 `existing ++ incoming`；`hint=prepend` 时是 `incoming ++ existing`
    /// （即 target 与 source 的角色互换，去掉的那个 NUL 始终来自**先写的那个**）。
    /// 返回 false 表示"类型不同 / 长度非法"，调用方应当跳过该值。
    bool MergeRegValueBytes(uint32_t typeCode,
                            const std::vector<uint8_t>& existing,
                            const std::vector<uint8_t>& incoming,
                            bool prepend,
                            std::vector<uint8_t>& out);

} // namespace ocid
