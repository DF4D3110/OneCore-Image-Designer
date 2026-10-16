// OCID - WCM 配置模式（Windows Configuration Manager configuration schema）
//
// 这是把"清单声明"变成"可执行的注册表操作"的那一层。没有它，SMI 只能靠人工喂设置；
// 有了它，manifest 里的 <configurationSchema> 就能直接驱动我们的 OcidSmiService。
//
// ── 依据（全部来自镜像里的真实清单实测，不是推测）──────────────────────────
//
// shell32 清单（1.3 MB）里有 533 个 `<xsd:element>`，每个描述**一条设置**：
//
//   <xsd:element
//      name="HKEY_CLASSES_ROOT_Unknown_shell_defaultValue"     ← 设置项标识符
//      type="xsd:string"                                       ← XSD 类型
//      default="openas"                                        ← ★ 值数据
//      wcm:displayName="HKEY_CLASSES_ROOT\Unknown\shell\defaultValue"
//      wcm:handler="regkey('HKEY_CLASSES_ROOT\Unknown\shell')" ← ★ 目标【键】
//      wcm:legacyName=""                                       ← ★ 值名（空 = 默认值）
//      wcm:legacyType="REG_SZ"                                 ← ★ 注册表类型
//      wcm:scope="allUsers"                                    ← 作用域
//      wcm:subScope="machineIndependent" />
//
// ⇒ **一条注册表操作 = handler(键) + legacyName(值名) + legacyType(类型) + default(数据)**。
//   这四个字段是权威的；`name` 只是标识符。
//
// ⚠⚠ **`name` 不能从 `displayName` 推导！** 我试过规则（把 `\ { }` 换成 `_`、去掉空格），
//    533 条里有 **47 条不吻合**，而且是语义性的：
//      disp=HKEY_CLASSES_ROOT\.bmp\Content Type        → name=…_Content_Type      （空格→_）
//      disp=HKEY_CLASSES_ROOT\Paint.Picture\EditFlags  → name=…_ImageOptionFlags  （名字都不同）
//    ⇒ 要拿注册表操作，必须用 legacy* 那组字段，别去反推。
//
// ── 作用域（实测分布）──────────────────────────────────────────────────────
//   wcm:scope="allUsers"（502）：handler 根为 HKEY_CLASSES_ROOT（495）或 HKEY_LOCAL_MACHINE（7）
//   wcm:scope="perUser" （31）：handler 根为 HKEY_CURRENT_USER
//   ⇒ 离线服务时的落点：
//        HKCR → SOFTWARE hive 的 Classes 子树
//        HKLM → 对应的 hive（SOFTWARE/SYSTEM/…）
//        HKCU → 用户 hive（镜像里是 Users\default\ntuser.dat / config\DEFAULT）
//     这与 CBS 实际挂载的 hive 集合一致（它的日志里会挂 ntuser.dat）。
//
// ── legacyType / xsd type 的对应（实测取值）────────────────────────────────
//   REG_SZ 409 · REG_EXPAND_SZ 67 · REG_DWORD 44 · REG_BINARY 12
//   xsd:string 476 · xsd:unsignedInt 45 · xsd:hexBinary 12
//
// ── handler 语法 ───────────────────────────────────────────────────────────
//   实测 533 条**全部**是 `regkey('<根>\<子键>')`，没有别的形态。
//   但为了不把话说死，解析器认不出形态时**记 warning 并跳过**而不是失败。

#pragma once

#include "ocid/Manifest.h"

#include <cstdint>
#include <map>
#include <string>
#include <vector>

namespace ocid {

    /// 一条 WCM 设置（来自一个 `<xsd:element>`）。
    struct WcmSettingItem
    {
        // ── 原始字段 ────────────────────────────────────────────────────────
        std::wstring name;         // @name
        std::wstring xsdType;      // @type，如 xsd:string
        std::wstring defaultValue; // @default
        std::wstring displayName;  // @wcm:displayName
        std::wstring handler;      // @wcm:handler，如 regkey('HKLM\…')
        std::wstring legacyName;   // @wcm:legacyName —— 注册表值名；**空串 = 默认值**
        std::wstring legacyType;   // @wcm:legacyType —— REG_SZ / REG_DWORD / …
        std::wstring scope;        // @wcm:scope —— allUsers / perUser
        std::wstring subScope;     // @wcm:subScope
        std::wstring description;  // @wcm:description
        std::wstring migrate;      // @wcm:migrate

        /// handler 是否是可识别的注册表操作（`regkey('…')`）。
        bool IsRegistrySetting() const { return !RegistryKeyPath().empty(); }

        /// 从 `regkey('…')` 里取出的键全路径（含根，如 `HKEY_CLASSES_ROOT\Unknown\shell`）。
        /// 不是 regkey 形态时返回空串。
        std::wstring RegistryKeyPath() const;
    };

    /// 解析结果。
    struct WcmSchemaParseResult
    {
        bool ok = false;
        std::wstring error;
        std::vector<std::wstring> warnings;   // 认不出的 handler / 缺字段等
    };

    /// 一份清单的 `<configurationSchema>`。
    class WcmConfigurationSchema
    {
    public:
        /// 从 XSD 文本解析。`xsdText` 可以是 `<configurationSchema>` 整段，
        /// 也可以直接是 `<xsd:schema>` 段。
        WcmSchemaParseResult Parse(const std::wstring& xsdText);

        const std::vector<WcmSettingItem>& Items() const { return items_; }
        size_t Size() const { return items_.size(); }

        /// 按设置项名查（`name` 属性）。
        const WcmSettingItem* Find(const std::wstring& name) const;

        /// 统计：注册表设置条数（handler 为 regkey 形态的）。
        size_t RegistrySettingCount() const;

    private:
        std::vector<WcmSettingItem> items_;
        std::map<std::wstring, size_t> byName_;
    };

    // ─────────────────────────────────────────────────────────────────────────
    // 把 WCM 设置转换成"可执行的注册表操作"
    // ─────────────────────────────────────────────────────────────────────────

    /// 一条可直接执行的注册表操作（逻辑路径，尚未映射到具体 hive）。
    ///
    /// 注意这是**逻辑**路径（HKEY_CLASSES_ROOT\… / HKEY_LOCAL_MACHINE\…），
    /// 映射到"哪个 hive 文件 + hive 内相对路径"由 SMI 那一层负责
    /// （见 C# 侧的 OcidSmiService.MapLogicalToHive，两者必须一致）。
    struct WcmRegistryOperation
    {
        std::wstring root;       // HKEY_CLASSES_ROOT / HKEY_LOCAL_MACHINE / HKEY_CURRENT_USER
        std::wstring subKey;     // 根之下（不含根）
        std::wstring valueName;  // 空 = 默认值
        std::wstring valueType;  // REG_SZ / REG_DWORD / …
        std::wstring data;       // 值数据（字符串形式）
        std::wstring scope;      // allUsers / perUser
        std::wstring sourceItem; // 来自哪个设置项（排障用）
        /// 来源机制：`"registryKeys"`（①）或 `"wcm"`（②）。两套是不同集合，排障时必须能区分。
        std::wstring mechanism;
        /// `<registryKeys>` 那套才带：该组件是否**拥有**这个值（影响卸载时是否删除）。
        bool         owner = true;
        /// `operationHint`（实测几乎全是 `replace`）。
        std::wstring operationHint;
    };

    /// 把一份模式里的全部注册表设置转成操作列表。
    /// `settings_` 里非注册表形态的项会记进 `warnings`，不会导致失败。
    std::vector<WcmRegistryOperation> ToRegistryOperations(
        const WcmConfigurationSchema& schema,
        std::vector<std::wstring>* warnings = nullptr);

    // ─────────────────────────────────────────────────────────────────────────
    // 两套声明机制的统一收集
    // ─────────────────────────────────────────────────────────────────────────
    //
    // ⚠⚠ **一份清单里有【两套独立】的注册表声明，而且它们是不同的集合**，实测（shell32，1.3 MB）：
    //
    //   ① `<registryKeys>/<registryKey>/<registryValue>`     2115 key / **5282 value**
    //      属性：`keyName`、`owner` ／ `name`、`valueType`、`value`、`operationHint`、`owner`
    //      **里面没有** `Classes\Unknown\shell`
    //
    //   ② `<configuration>/<configurationSchema>/<xsd:element>`   **533 条**
    //      属性：`wcm:handler`、`wcm:legacyName`、`wcm:legacyType`、`default`、`wcm:scope`
    //      **里面有** `Classes\Unknown\shell = openas`（CBS 被 UCPD 拦的那一笔）
    //
    //   ⇒ 数目、属性、内容都不同 ⇒ **不是同一批设置的两种写法**。
    //   ⇒ 要完整替代 CBS，**两套都必须落**。只做 ② 会静默丢掉 ① 的几千条设置：
    //      清单解析成功、日志全绿、`Unknown\shell` 也对，**只有拿真实镜像逐条对比才会发现少了一大片**。
    //
    // 本函数把两套统一收集成同一种操作，用 `source`/`sourceItem` 区分来源，便于排障。
    struct RegistryOperationBatch
    {
        std::vector<WcmRegistryOperation> operations;
        size_t fromRegistryKeys = 0;   // ① 贡献的条数
        size_t fromWcmSchema = 0;      // ② 贡献的条数
    };

    /// ① 单个 `<registryKey>` 下的值 → 操作（含嵌套子键）。
    void RegistryKeyToOperations(const RegistryKeySpec& key,
                                 std::vector<WcmRegistryOperation>& out);

    /// 从整份清单收集**两套机制**的全部注册表操作。
    /// `hasSchema=false` 表示清单没有 configurationSchema（② 没有内容，属正常）。
    RegistryOperationBatch CollectManifestRegistryOperations(
        const ComponentManifest& manifest,
        std::vector<std::wstring>* warnings = nullptr);

    // ─────────────────────────────────────────────────────────────────────────
    // 值数据转换
    // ─────────────────────────────────────────────────────────────────────────

    /// XSD 类型 + legacyType → 我们内部统一的值表示。
    /// `xsd:hexBinary`（如 `30040000`）在注册表里是 REG_BINARY，
    /// 转换时按字节序原样保留（**不做大小端翻转**）——这是实测得到的语义：
    /// hexBinary 的文本就是字节序列，注册表里存的就是这些字节。
    struct ConvertedValue
    {
        bool         ok = false;
        std::wstring error;
        std::vector<uint8_t> bytes;   // BINARY 用
        uint32_t     dword = 0;       // DWORD 用
        std::wstring text;            // SZ / EXPAND_SZ 用
        bool         isBinary = false;
        bool         isDword  = false;
    };

    ConvertedValue ConvertSettingValue(const WcmSettingItem& item);

} // namespace ocid
