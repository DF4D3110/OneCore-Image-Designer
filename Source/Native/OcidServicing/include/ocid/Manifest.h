// OCID - 组件清单模型（Component Manifest）
//
// 这是 OCID 自带服务栈的"输入模型"：CSI/CMI/SMI 做的每一件事，最终都来自某份清单里的声明。
//
// ⚠ 本文件里的元素名与属性名**全部照抄真实清单**，不要按直觉改名或简化。
//   依据是镜像里真实存在的清单（`P:\packages\*.manifest`）实测词表：
//
//   <assembly>
//     <assemblyIdentity .../>                      ← 见 ocid/Identity.h
//     <dependency><dependentAssembly><assemblyIdentity/></dependentAssembly></dependency>
//     <supportedComponent><supportedComponentIdentity/></supportedComponent>
//     <configurationSchema><xsd:schema>…</xsd:schema></configurationSchema>   ← WCM 模式（SMI 编译它）
//     <registryKeys>                               ← ★ 注册表操作（SMI 消费）
//       <registryKey keyName="HKEY_CLASSES_ROOT\*" owner="false">
//         <registryValue name="AlwaysShowExt" valueType="REG_SZ" value=""
//                        operationHint="replace" owner="true" />
//       </registryKey>
//     </registryKeys>
//     <directory>…</directory>  <file>…</file>     ← 文件落位
//     <securityDescriptor>…</securityDescriptor>   ← ACL（659 处，量很大）
//     <securityDescriptorDefinition>…</securityDescriptorDefinition>
//     <migXml>/<rules>/<merge>/<locationModify>/<conditions>/<processing>/<script>   ← 迁移规则
//     <taskScheduler><Task><Actions><ComHandler><ClassId>…                          ← 计划任务
//     <objectSet>…</objectSet>                     ← 分组
//     <signatureInfo><signatureDescriptor/><dsig:Transforms/><asmv2:hash/>…          ← 签名
//   </assembly>
//
// 实测规模（`arm_microsoft-windows-shell32_…manifest`，1293 KB）：
//   registryValue 5282 · registryKey 2115 · securityDescriptor 659 · xsd:element 533
//   directory 52 · file 3 · objectSet 18 · …
//
// 本文件先落"注册表"这一块（它是我们已经能执行的部分，SMI 已完成），
// 其余部分给出结构骨架与解析入口，按后续模块逐块填实。

#pragma once

#include "ocid/Identity.h"

#include <cstdint>
#include <map>
#include <string>
#include <vector>

namespace ocid {

    // ─────────────────────────────────────────────────────────────────────────
    // 注册表：`<registryKeys>/<registryKey>/<registryValue>`
    // ─────────────────────────────────────────────────────────────────────────

    /// 一个 `<registryValue>`。
    ///
    /// 真实属性（镜像里 5282 处实测）：
    ///   name           值名；**空串表示默认值**
    ///   valueType      REG_SZ / REG_EXPAND_SZ / REG_DWORD / REG_QWORD / REG_BINARY / REG_MULTI_SZ
    ///   value          值数据（字符串形式；DWORD 十进制或 0x 前缀；BINARY 十六进制）
    ///   owner          "true"/"false" —— ★ 该组件是否【拥有】这个值。
    ///                  这关系到卸载时"该不该删"与冲突判定，**不能省**。
    ///   operationHint  实测几乎全是 "replace"；语义是"以本声明覆盖既有值"。
    ///   罕见：id / LinkId / Ext
    struct RegistryValueSpec
    {
        std::wstring name;                       // "" = 默认值
        std::wstring valueType = L"REG_SZ";
        std::wstring value;
        bool         owner = true;
        std::wstring operationHint = L"replace";
        std::wstring id;                         // 罕见
        std::wstring linkId;                     // 罕见
        std::wstring ext;                        // 罕见

        /// 是否是默认值（`name` 为空）。
        bool IsDefault() const { return name.empty(); }
    };

    /// 一个 `<registryKey>`（可嵌套子键）。
    ///
    /// 实测属性：`keyName`（如 `HKEY_CLASSES_ROOT\*`）、`owner`。
    /// 清单里键可以嵌套（`<registryKey>` 里再有 `<registryKey>`），
    /// 但**真实数据里全部是扁平的**（每个 keyName 都写全路径），
    /// 所以子键列表主要用于兼容万一出现的嵌套写法。
    struct RegistryKeySpec
    {
        std::wstring keyName;                    // 全路径，含根（HKEY_*）
        bool         owner = false;
        std::vector<RegistryValueSpec> values;
        std::vector<RegistryKeySpec>   subKeys;  // 真实数据里为空
    };

    // ─────────────────────────────────────────────────────────────────────────
    // 文件落位：`<file>` / `<directories><directory>`
    // ─────────────────────────────────────────────────────────────────────────
    //
    // ⚠ 结构是**从真实清单读出来的**，不要按直觉排：
    //
    //   <file name="shell32.dll" destinationPath="$(runtime.system32)\"
    //         sourceName="shell32.dll" sourcePath=".\" importPath="$(build.nttree)\">
    //     <securityDescriptor name="WRP_FILE_DEFAULT_SDDL" />      ← 嵌在 file 里
    //     <signatureInfo><signatureDescriptor PETrust="true" pageHash="true"/></signatureInfo>
    //     <asmv2:hash>…</asmv2:hash>
    //   </file>
    //
    //   <directories>                                              ← ★ 有一层 <directories> 包裹
    //     <directory destinationPath="$(runtime.public)\" owner="true" attributes="readOnly" />
    //     <directory destinationPath="…\Desktop\" owner="true" attributes="readOnly hidden">
    //       <securityDescriptor name="PUBLIC_DESKTOP_SDDL" />      ← 嵌在 directory 里
    //     </directory>
    //   </directories>
    //
    // 另一个必须知道的机制：路径里的 **`$(…)` 是宏**（`$(runtime.system32)`、`$(build.nttree)`…），
    // 不是字面路径。展开规则属于另一个模块，这里先**原样保留**，由调用方决定何时展开。

    struct FileSpec
    {
        std::wstring name;            // 目标文件名
        std::wstring destinationPath; // 目标目录（可能含 $(…) 宏）
        std::wstring sourceName;      // 源文件名
        std::wstring sourcePath;      // 源目录
        std::wstring importPath;      // 构建期导入路径（镜像里无意义，仅保留）
        std::wstring securityDescriptorName;  // 内嵌 <securityDescriptor name="…"/>
    };

    struct DirectorySpec
    {
        std::wstring destinationPath; // 目录路径（可能含 $(…) 宏）
        bool         owner = false;   // owner="true"/"false"
        std::wstring attributes;      // "readOnly" / "readOnly hidden" …
        std::wstring securityDescriptorName;  // 内嵌 <securityDescriptor name="…"/>
    };

    // ─────────────────────────────────────────────────────────────────────────
    // 安全描述符：`<securityDescriptor name="…"/>`
    // ─────────────────────────────────────────────────────────────────────────
    //
    // ⚠ 它**嵌在 `<file>` / `<directory>` 里面**（实测 659 处），不是顶层元素。
    //   而且这里只给**名字**（如 `WRP_FILE_DEFAULT_SDDL`、`PUBLIC_DESKTOP_SDDL`），
    //   真正的 SDDL 文本由 `<securityDescriptorDefinition>` 提供（在别处定义后按名引用）。
    //   所以上层要维护一张 name → SDDL 的表；本结构只负责把名字带出来。
    struct SecurityDescriptorSpec
    {
        std::wstring name;      // 引用名（如 WRP_FILE_DEFAULT_SDDL）
        std::wstring rawText;   // 若该节点自带内容则保留
    };

    // ─────────────────────────────────────────────────────────────────────────
    // WCM 配置模式：`<configurationSchema><xsd:schema>…`
    // ─────────────────────────────────────────────────────────────────────────
    //
    // ★ 这是 SMI 的输入：manifest 里那 533 个 `<xsd:element wcm:handler="regkey('…')">`
    //   就在这个 XSD 里。<c>smiengine.dll</c> 的 `CXsdCompiler` 编译它，
    //   得到"设置项/属性"的元数据，`CSettingsItem`/`CSettingsAttribute` 再用它。
    //
    // ⇒ OCID 的 SMI 要么自己实现这个 XSD 编译，要么按同一份模式直接求值。
    //   本结构先把 XSD 原文收着，等实现编译那一步时再用。
    struct ConfigurationSchema
    {
        std::wstring xsdText;                 // 完整 XSD 原文
        std::vector<std::wstring> elementNames;  // 抽出的 xsd:element name 列表（便于检索/统计）
    };

    // ─────────────────────────────────────────────────────────────────────────
    // 依赖：`<dependency><dependentAssembly><assemblyIdentity>` / `<supportedComponent>`
    // ─────────────────────────────────────────────────────────────────────────

    struct DependencySpec
    {
        AssemblyIdentity identity;
        std::wstring     kind;    // "dependency" / "supportedComponent"
    };

    // ─────────────────────────────────────────────────────────────────────────
    // 整份清单
    // ─────────────────────────────────────────────────────────────────────────

    struct ComponentManifest
    {
        /// `<assemblyIdentity>` —— 这份清单描述的那个组件。
        AssemblyIdentity identity;

        /// `<registryKeys>` 下的全部键（含各自的 values）。
        std::vector<RegistryKeySpec> registryKeys;

        /// `<dependency>` / `<supportedComponent>` 声明的依赖。
        std::vector<DependencySpec> dependencies;

        /// `<deployment><infFile>` —— 驱动类组件的 INF。
        std::wstring infFile;

        std::vector<FileSpec>      files;
        std::vector<DirectorySpec> directories;

        /// `<securityDescriptorDefinition>` 提供的 name → SDDL 定义表。
        /// `<file>`/`<directory>` 里的 `<securityDescriptor name="…"/>` 按名引用它。
        std::map<std::wstring, std::wstring> securityDescriptorDefinitions;

        /// 顶层出现过的 `<securityDescriptor>`（少见；多数是嵌套的，已记在 FileSpec/DirectorySpec 里）。
        std::vector<SecurityDescriptorSpec> securityDescriptors;

        /// `<configurationSchema>` —— WCM 模式（SMI 的输入）。
        ConfigurationSchema configurationSchema;

        /// `<signatureInfo>` 原文（签名相关的 dsig/asmv2 片段），本阶段只保留不解析。
        std::wstring signatureInfoRaw;

        /// 清单文件本身的位置（排障用）。
        std::wstring sourcePath;

        /// 统计：这个清单声明了多少个注册表值（含嵌套）。
        size_t RegistryValueCount() const
        {
            size_t n = 0;
            for (const auto& k : registryKeys) {
                n += k.values.size();
                for (const auto& sk : k.subKeys) n += sk.values.size();
            }
            return n;
        }
    };

    // ─────────────────────────────────────────────────────────────────────────
    // 解析
    // ─────────────────────────────────────────────────────────────────────────

    /// 解析结果：成功/失败 + 诊断。
    struct ManifestParseResult
    {
        bool         ok = false;
        std::wstring error;          // ok=false 时的原因
        std::vector<std::wstring> warnings;  // 认不出但不致命的元素/属性
    };

    /// 从清单文本解析。
    ///
    /// 设计取舍：**不因为"看不懂的元素"而失败** —— 真实清单里元素种类极多
    /// （migXml/rules/taskScheduler/objectSet…），而我们目前只实现其中一部分。
    /// 遇到没实现的元素就记一条 warning 并跳过，这样：
    ///   · 注册表部分现在就能用；
    ///   · 后续补实现时，warning 列表正好是待办清单。
    ///
    /// 反过来，**结构性错误必须失败**（XML 不合法、没有 assemblyIdentity、registryValue 缺 name/valueType）。
    ManifestParseResult ParseComponentManifest(const std::wstring& xmlText,
                                               ComponentManifest& out);

} // namespace ocid
