// OCID - CSI：组件文件落位（清单 → 落位计划）
//
// ── 这一层回答什么 ────────────────────────────────────────────────────────
//
// 清单里的 `<file>` / `<directories><directory>` 声明"这个组件的哪些文件要出现在
// 镜像的哪里"。把这份声明变成**可执行的落位计划**（宏已展开、SDDL 已解析、路径已拼全），
// 就是本文件；真正动磁盘的是 C# 侧（`Services/CsiFileService.cs`）。
//
// ── 实测出来的机制（不是猜的）────────────────────────────────────────────
//
// 目标是 8061 ARM 构建产物镜像（挂成 `P:`）：
//
// [落点] 72 份清单里共 **206 条** `<file destinationPath="…">`：
//     132× `$(runtime.system32)\`            32× `$(Runtime.System32)\Dism\`  ← 注意大写 R
//      22× `$(runtime.drivers)\`              4× `$(runtime.drivers)\etc\`
//       4× `$(runtime.windows)\system32\drivers\`   2× `$(runtime.systemRoot)\`
//       2× `$(runtime.windows)\Servicing\Version\6.2.8061.0\`
//       各 1× `…\Boot\` `…\drivers\` `…\migration\` `…\oobe\` `…\SMI\Schema\`
//              `$(runtime.wbem)\` `$(runtime.windows)\inf\` `$(runtime.windows)\servicing\`
//
// [验证] 这 206 条**全部**能用 17 项 `runtime.*` 宏表展开成**镜像里已存在**的路径，
//   残留宏 0 条；并且 **206/206** 都在自己的组件目录
//   `Windows\WinSxS\<清单文件名去掉 .manifest>\<文件名>` 里也有一份。
//
// [机制] 落点是**硬链接**，不是拷贝 —— `fsutil hardlink list` 实测：
//     \Windows\System32\basesrv.dll
//     \Windows\WinSxS\arm_microsoft-windows-basesrv_31bf3856ad364e35_6.2.8061.0_none_93d3f64c57474751\basesrv.dll
//   正好 2 条链接（`LinkType=HardLink`）。`cdfs.sys` / `Dism\DismCore.dll` / `kdcom.dll`
//   同样 2 条。⇒ **组件目录是正本，落点是链接**，这正是 WinSxS 的去重手段。
//
// [载荷] 源文件在包展开目录里：`packages\<组件目录名>\<sourceName>`
//   （实测 `P:\packages\arm_microsoft-windows-basesrv_…\basesrv.dll` 40960 字节，
//     与组件目录里那份同尺寸）。所以一条落位一共牵涉三个路径：
//       载荷 packages\<dir>\<src> → 正本 WinSxS\<dir>\<name> →（硬链接）落点 <dest>\<name>
//
// [清单在哪] 组件目录名**只能从清单文件名推**（去掉 `.manifest`）：
//   实测 `packages\` 下有 5664 份**平铺**的 `<组件目录>.manifest`（4359 份有同名载荷
//   目录），另有 118 份在 `Windows\WinSxS\Manifests\<组件目录>.manifest` —— 两处都是
//   "文件名 = 组件目录名"。按目录名推会让那 5664 份全算成 "packages"（载荷路径变成
//   `packages\packages\x.dll`），所以这条规则不是风格问题，是正确性问题。
//
// [目录] 49 条 `<directory>` 全部展开成镜像里已存在的目录；`attributes` 多为空（42 条），
//   其余是 `readOnly`(2) / `hidden notContentIndexed` / `notContentIndexed` /
//   `readOnly hidden` / `readOnly system` / `system notContentIndexed`；
//   `owner` 32 true / 17 false。
//
// ── 明确不做的事 ─────────────────────────────────────────────────────────
//
//   · **没有 `destinationPath` 的 `<file>`**（例如 `arm_ax88772.inf_…` 的
//     `<file name="ax88772.INF" sourceName="ax88772.INF" importPath="$(build.nttree)\driver_infs\">`）
//     属于驱动/存储类，落位规则与这里不同。本模块**只记数、放进 notes**，绝不替它们猜一个落点。
//   · `owner="true"` 的确切语义（"组件拥有该目录的 SD"）在离线环境**无法验证**，
//     所以只带出来给上层，不据此改 owner。
//
// ── 与其它模块的关系 ─────────────────────────────────────────────────────
//
//   · 宏展开   `ocid/MacroExpander.h`（`RuntimePaths` + `ExpandMacros`）
//   · SDDL     `ocid/SecurityDescriptor.h`（按名字解析 + 落盘 + 回读）
//   · 清单解析 `ocid/Manifest.h`（`FileSpec` / `DirectorySpec`）
//   · 控制台   `tools/ocid_manifest.cpp` 的 `place-ops`（吐 JSONL 给 C# 侧）

#pragma once

#include <cstddef>
#include <string>
#include <vector>

#include "ocid/Manifest.h"
#include "ocid/MacroExpander.h"

namespace ocid
{
    /// 一条 `<file>` 的落位计划。三个路径都为**绝对路径**（相对镜像根而言）。
    struct FilePlacementOp
    {
        std::wstring name;             ///< 目标文件名（`name=`，也是落点与正本的文件名）
        std::wstring payloadPath;      ///< `packages\<组件目录>\<sourceName>`（找不到就是空的）
        std::wstring storePath;        ///< `Windows\WinSxS\<组件目录>\<name>` —— **正本**
        std::wstring destinationPath;  ///< 落点完整路径（宏已展开、已按架构红移）
        std::wstring sddl;             ///< 该文件引用的 SDDL 原文（空 = 没引用）
        std::wstring sdName;           ///< 引用名（排障用）
        std::wstring unresolved;       ///< 未展开的宏（非空 ⇒ 这条**不能**执行）
        std::wstring writeableType;    ///< `writeableType=` 原值（空 = 没有该属性）
        bool         writeable = false;///< 非空 ⇒ 落点是**拷贝**，不是硬链接
        bool         redirected = false;///< 落点被架构红移改写过（System32 ⇒ SysWOW64）
    };

    /// 一条 `<directory>` 的落位计划。
    struct DirectoryPlacementOp
    {
        std::wstring path;             ///< 目录完整路径（宏已展开，无尾随反斜杠）
        std::wstring attributes;       ///< `readOnly` / `readOnly hidden` …（原样）
        bool         owner = false;    ///< `owner="true"`（语义未定，只带出来）
        std::wstring sddl;
        std::wstring sdName;
        std::wstring unresolved;
    };

    /// 一个组件的落位计划。
    struct PlacementPlan
    {
        std::wstring identity;      ///< 组件 identity（排障用）
        std::wstring componentDir;  ///< `arm_microsoft-windows-basesrv_…_93d3f64c57474751`
        std::wstring storeDir;      ///< `Windows\WinSxS\<componentDir>`
        std::vector<FilePlacementOp>      files;
        std::vector<DirectoryPlacementOp> directories;
        /// 只记数、不落位的东西（无 destinationPath 的文件、解析不了的引用…）。
        std::vector<std::wstring> notes;
        std::size_t filesWithoutDestination = 0;
        std::size_t unresolvedCount = 0;
        /// `<securityDescriptor name="…"/>` 在定义表里查不到的条数（ACL 会被静默丢掉，必须为 0）。
        std::size_t sddlUnresolved = 0;
        /// 载荷文件在包展开目录里**不存在**的条数 —— 这类只能落正本+链接，没有源可拷。
        std::size_t missingPayload = 0;
        /// 落点与正本**不是硬链接**（`writeableType="systemData"`）的条数。
        std::size_t writeableFiles = 0;
        /// 落点被架构红移（`System32` ⇒ `SysWOW64`）的条数。
        std::size_t redirectedFiles = 0;
        std::wstring componentArch;  ///< 从 componentDir 前缀推出的组件架构
        std::wstring imageArch;      ///< 用于判定红移的镜像架构（空 = 不红移）
        bool         archDetected = false; ///< imageArch 是不是自己探测出来的

        bool ok() const { return unresolvedCount == 0; }
        /// 真正可执行的 `<file>` 条数（有落点、宏全展开）。
        std::size_t executableFiles() const;
    };

    /// 从组件目录名推架构：`arm_microsoft-windows-…` ⇒ `arm`。
    /// 没有下划线（或下划线在开头）时返回空串 —— 不猜。
    std::wstring ComponentArchOf(const std::wstring& componentDir);

    /// 组件架构 ≠ 镜像架构的 x86 组件，落点要从 `System32\…` 红移到 `SysWOW64\…`。
    ///
    /// 实测（8061 ARM 镜像，内核 PE `machine=0x01C4`=ARMNT ⇒ 镜像架构 arm）：
    ///   · `arm_…` 组件声明 `$(Runtime.System32)\Dism\` ⇒ 落在 `Windows\System32\Dism\`
    ///   · `x86_…` 组件声明**同一句** `$(Runtime.System32)\Dism\` ⇒ 实际落在
    ///     `Windows\SysWOW64\Dism\`（`fsutil hardlink list` 指向该 x86 组件的正本）
    /// 已落位的 206 条里，27 条"落点与正本不是同一文件"，全部由这条规则（21 条）
    /// 与 `writeableType`（6 条）解释干净，没有第三种原因。
    ///
    /// **只实现了实测到的那一格**（arm 镜像上的 x86 组件）。arm64 镜像上的 arm/x86、
    /// amd64 镜像上的 x86 都理应同理，但**手上没有可测量的镜像**，所以不写成"通用规则"
    /// 以免把猜测当结论 —— `IsWow64Redirected` 里显式列出已确认的组合。
    bool IsWow64Redirected(const std::wstring& componentArch, const std::wstring& imageArch);

    /// 从镜像内核 PE 头读架构：`<windowsDir>\System32\ntoskrnl.exe` 的 Machine 字段。
    /// 返回 `arm` / `arm64` / `amd64` / `x86`；读不到返回空串（调用方决定怎么办）。
    std::wstring DetectImageArch(const std::wstring& windowsDir);

    /// 由一份已解析的清单构造落位计划。
    ///
    ///   componentDir  —— 组件目录名（store 目录名；**取自清单文件名去掉 `.manifest`**，
    ///   packageRoot   —— 包展开根（里面是 `<packageRoot>\<componentDir>\<文件>`）
    ///   windowsDir    —— 镜像里的 Windows 目录（如 `P:\Windows`），宏展开的基准
    ///   imageArch     —— 镜像架构；**传空串表示不红移**（不探测，保持"只做展开"的语义）
    PlacementPlan BuildPlacementPlan( const ComponentManifest& manifest,
                                      const std::wstring& componentDir,
                                      const std::wstring& packageRoot,
                                      const std::wstring& windowsDir,
                                      const std::wstring& imageArch = std::wstring() );

    /// 用镜像自己的声明（`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion` 下的
    /// `ProgramFilesDir` 等）覆盖宏展开基准 —— 透传，便于调用方统一。
    PlacementPlan BuildPlacementPlan( const ComponentManifest& manifest,
                                      const std::wstring& componentDir,
                                      const std::wstring& packageRoot,
                                      const RuntimePaths& paths,
                                      const std::wstring& imageArch = std::wstring() );

} // namespace ocid
