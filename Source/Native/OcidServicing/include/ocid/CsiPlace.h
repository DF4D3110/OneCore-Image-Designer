// OCID - CSI 落位执行器（place-ops 的 JSONL → 真正动磁盘）
//
// ── 这一层回答什么 ────────────────────────────────────────────────────────
//
// `FilePlacement.h` 只把清单变成"落位计划"（`ocid_manifest place-ops` 吐 JSONL），
// 本文件把那份计划**执行**掉：载荷 → 正本（复制）→ 落点（硬链接）→ 安全描述符。
//
// ── 为什么是复制 + 硬链接（实测，不是猜）──────────────────────────────────
//
// 真机 8061 ARM（挂成 `P:`）上 `fsutil hardlink list` 的结果：
//
//     \Windows\System32\basesrv.dll
//     \Windows\WinSxS\arm_microsoft-windows-basesrv_…\basesrv.dll
//
// `LinkType=HardLink`、2 条链接 —— **组件目录是正本，落点是链接**。
// 而载荷 `packages\<组件目录>\basesrv.dll` 的 hardlink list 只有 1 条，说明 CBS
// 是把载荷**拷贝**进组件存储的（不是链过去）。链过去的话，`packages\` 一旦被重新
// 展开/清理，镜像正本跟着坏 —— 而 `packages` 是构建产物，随时可能被覆盖。
//
// ── 权限：为什么必须先启 SeRestorePrivilege ───────────────────────────────
//
// `Windows\WinSxS` 的所有者是 `NT SERVICE\TrustedInstaller`，DACL 里只有它拿
// Full Control，`SY/BA/BU/AC` 都只有 `0x1200a9`（读+执行+同步，**没有**
// `FILE_ADD_SUBDIRECTORY`）。所以即使是管理员，`CreateDirectory` 也直接 Access denied。
// 启用 **SeRestorePrivilege** 会绕过写侧 DACL 检查（读侧对应 SeBackupPrivilege），
// 这正是离线服务栈（Setup/CBS）靠的手段。
//
// ── 顺序：先落文件，最后上锁 ─────────────────────────────────────────────
//
// 新建的正本目录要套用 `WinSxS` 的安全策略（owner=TI、DACL 保护、BA 只剩读+执行）。
// 一旦先套上，后续 `CopyFile` 连管理员也写不进去 ⇒ 必须"先落文件、最后统一上锁"，
// 与真实 CBS "组件暂存完再定 ACL" 的顺序一致。
//
// ── 与其它模块的关系 ─────────────────────────────────────────────────────
//
//   · 计划生成 `FilePlacement.h`（`ocid_manifest place-ops`）
//   · SDDL     `SecurityDescriptor.h`（`ApplySddlToPath` / `ReadPathSddl`）
//   · 驱动文件 走 DMI（`DriverPackage.h`），本模块只处理带 `destinationPath` 的 `<file>`

#pragma once

#include <string>
#include <vector>

namespace ocid
{
    /// 一条落位操作（对应 `ocid_manifest place-ops` 输出的一行 JSON）。
    struct CsiPlaceOp
    {
        std::string component;        ///< 组件键（日志用）
        std::string identity;         ///< 清单里的身份文本（日志用）
        std::string kind;             ///< "file" / "directory"
        std::wstring path;            ///< kind=directory 时的目录完整路径
        std::wstring storeDir;        ///< 组件目录（kind=file）
        std::wstring storePath;       ///< 正本路径（kind=file）
        std::wstring destinationPath; ///< 落点路径（kind=file；空 = 驱动类，跳过）
        std::wstring payloadPath;     ///< 载荷路径（可能不存在）
        std::wstring sddl;            ///< 该文件/目录的安全描述符原文（空 = 不设）
        std::wstring unresolved;      ///< 未展开的宏（非空 ⇒ 跳过，绝不猜路径）
        bool redirected = false;      ///< 落点被架构红移改写过
    };

    /// 执行统计。**写进去不算完成**：`placed` 只统计"正本存在且落点是硬链接"的成功项。
    struct CsiPlaceResult
    {
        int planned = 0;
        int placed = 0;          ///< 落点已就绪（新建链接或原本就在）
        int copied = 0;          ///< 载荷 → 正本 的复制次数
        int linked = 0;          ///< 新建硬链接数
        int dirs = 0;            ///< 新建目录数
        int aclApplied = 0;      ///< 成功写入 SDDL 的次数
        int storeDirSecured = 0; ///< 新建正本目录最后套上了 WinSxS 策略
        int alreadyPresent = 0;  ///< 正本与落点都已经在（幂等重跑）
        int skipped = 0;         ///< 含未展开宏 / 无 destinationPath
        int missingPayload = 0;  ///< 载荷不存在（正本没法建）
        int failed = 0;
        std::vector<std::string> notes;

        std::string Summary() const;
    };

    /// 解析 `place-ops` 的一行 JSON。只支持**扁平对象 + 字符串/布尔**（这就是该命令的输出形态），
    /// 不做通用 JSON（仓库里 SMI 那条线也是同样的手写解析，不引依赖）。
    bool ParseCsiPlaceJsonLine(const std::string& line, CsiPlaceOp& op, std::string& error);

    /// 启用落位需要的特权（SeRestorePrivilege / SeBackupPrivilege）。
    /// 取不到不是致命错误（在可写目录里落位不需要它们），返回 false 并给原因。
    bool EnableCsiPlacePrivileges(std::string& error);

    /// 执行落位。`applyAcl=false` 时只落文件、不写 SDDL（排障用）。
    CsiPlaceResult PlaceCsiOps(const std::vector<CsiPlaceOp>& ops, bool applyAcl);
}
