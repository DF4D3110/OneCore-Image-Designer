// OCID WSSM —— Windows Servicing Stack Manager（离线服务栈统一入口）
//
// 设计意图
// ────────
// `ocid_manifest` 是一套"一件事一条命令"的原语（30 多条），脚本里要串好几步才能
// 完成一次装配。本程序是同一套能力的**统一工作台**：
//
//   * 把原语按"用户想干什么"重新分组（清单 / 落位 / 驱动 / 台账 / 生成 / 缓存 / DCM），
//     命令路径两层：`ocid_wssm <组> <命令> [参数...]`；
//   * 新增 workflow 组，把常用的多步流程一次跑完（生成全套计划、校验计划、批量构建
//     PCM 并回归），步骤间失败即停、逐条报时、末尾给下一步命令；
//   * 新增 doctor，先自检环境（管理员、目标镜像、服务栈、DCM 字典、ADK、磁盘）；
//   * **不复制实现**：所有原语仍然只有一份（`tools/ocid_manifest.cpp` 里的
//     `OcidManifestRun`），本程序只做参数翻译与流程编排。
//
// 约定
// ────
//   * 帮助文本默认 zh-CN（当前唯一内置语言）；命令名、参数名保持 ASCII，脚本里可直接用。
//   * 每条命令、每个参数都有解释：`ocid_wssm help <组> <命令>`。
//   * 数据走 stdout，诊断走 stderr（与 ocid_manifest 一致，便于 C# 侧消费）。
//   * 全局开关（--dcm-dictionary / --file-flags / --os-build）由本程序收集后原样
//     带进每一次转发调用。
//   * 兼容：`ocid_wssm <旧命令> ...` 直接把整条命令行透传给 ocid_manifest 的实现，
//     老脚本把 `ocid_manifest` 改名成 `ocid_wssm` 就能跑。
//
// 退出码：0 = 全部成功；1 = 某步命令失败；2 = 用法/参数错误或自检有失败项。

// <windows.h> first, with NOMINMAX already in effect: the ocid/*.h headers below
// include windows.h themselves, and without NOMINMAX their min/max macros would
// break std::max / std::min in this file.
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

#include "OcidManifestRun.h"
#include "ocid/CbsProfile.h"
#include "ocid/ConsoleUtf8.h"
#include "ocid/Dcm.h"

#include <algorithm>
#include <cctype>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <map>
#include <set>
#include <string>
#include <vector>


// (windows.h is pulled in at the top of this file, before the ocid/*.h headers.)

using namespace ocid;

#define OCID_WSSM_VERSION "1.0.0"

namespace {

    void SetupConsole() { SetupConsoleUtf8(); }

    void Out(const std::string& s) { fwrite(s.data(), 1, s.size(), stdout); }
    void OutLn(const std::string& s) { Out(s); Out("\n"); }
    void Err(const std::string& s) { fflush(stdout); fputs(s.c_str(), stderr); fputc('\n', stderr); }

    // ── 全局开关 ────────────────────────────────────────────────────────────
    // 这些开关由底层命令自己解析；本程序只在转发时保证它们出现在命令行里。
    std::vector<std::string> g_globals;

    // ── 命令文档树 ──────────────────────────────────────────────────────────
    //
    // path    ：两段命令路径（空格分隔），也是 `help <组> <命令>` 的键。
    // group   ：总览里的分组。
    // legacy  ：转发到 ocid_manifest 的哪个命令；nullptr = 本程序自己实现。
    struct Doc
    {
        const char* path;
        const char* group;
        const char* summary;
        const char* usage;
        const char* options;
        const char* example;
        const char* legacy;
    };

    const char* const kGroupOrder[] = {
        "环境", "工作流", "清单", "落位", "驱动", "台账", "生成", "缓存", "DCM", "工具",
    };

    const char* const kGroupSummary[] = {
        "自检、版本、帮助。",
        "多步串联：一次生成全套计划、校验计划、批量构建 PCM。",
        "组件清单的解析、统计、导出与宏展开。",
        "文件落位计划与执行（载荷 → WinSxS 正本 → 落点）。",
        "驱动包导入计划、DriverDatabase 台账、驱动落位。",
        "CBS 台账（COMPONENTS）的读取、校验、比对与写入。",
        "CBS 台账各部分记录的计划生成（组件 / 部署 / 版本索引 / 根 / 驱动）。",
        "ManifestCache（PCM/CDF）的解析、构建与回归统计。",
        "DCM 紧凑清单的解压与字典导出。",
        "零散工具与旧命令透传。",
    };

    const Doc kDocs[] = {
        // ── 环境 ────────────────────────────────────────────────────────────
        {"version", "环境",
         "打印本程序版本、构建信息与命令数。",
         "ocid_wssm version",
         "同 info；不给参数时的默认行为也是打印总览帮助。",
         "ocid_wssm version",
         nullptr},
        {"doctor", "环境",
         "环境自检：权限、目标镜像、服务栈、DCM 字典、ADK、磁盘空间。",
         "ocid_wssm doctor [--windows <WinDir>] [--image <镜像根>] [--dcm-dictionary <wcp.dll|.bin>] [--adk <ADK 根>] [--manifest-dir <清单目录>]",
         "--windows：离线镜像的 Windows 目录；给了就检查 ntoskrnl.exe 版本、清单数、\n"
         "  ManifestCache 块、服务栈 wcp.dll、磁盘空间。\n"
         "--image：镜像根（含 Windows 子目录）；给了就检查 COMPONENTS 台账文件。\n"
         "--dcm-dictionary：探测 DCM 字典是否真的能加载（16299+ 清单必需）。\n"
         "--adk：ADK 根；给了就找 tools\\bin\\i386\\imageapp.exe（桌面端构建链用的那个）。\n"
         "--manifest-dir：单独指定清单目录（不给就用 --windows\\WinSxS\\Manifests）。\n"
         "输出 [通过]/[提示]/[失败] 三类结论；退出码 0 = 全通过，1 = 有提示，2 = 有失败。",
         "ocid_wssm doctor --windows E:\\ocid_verify\\vmatrix\\mnt9600m\\Windows --dcm-dictionary E:\\ocid_verify\\pcm9600\\9600_dict.bin",
         nullptr},
        {"help", "环境",
         "帮助：总览、按组、按命令、或全部命令。",
         "ocid_wssm help [<组> [<命令>]] | ocid_wssm help --all",
         "不带参数：全部命令按组列出（一句话说明）。\n"
         "跟一个组名：列出该组命令。\n"
         "跟组名+命令名（或直接给命令路径的两个词）：显示用法、选项与示例。\n"
         "--all：把每条命令的完整帮助全部打印出来（较长，便于导出成文档）。",
         "ocid_wssm help store verify",
         nullptr},

        // ── 工作流 ──────────────────────────────────────────────────────────
        {"workflow plan", "工作流",
         "一次生成全套 CBS 台账计划（组件 → 部署 → 版本索引 → 根 [→ 驱动]）。",
         "ocid_wssm workflow plan --manifests <清单目录> [--packages <镜像根或 Packages 目录>] --staged <WinSxS> --arch <arch> --stack <版本文本> --out <输出目录> [--drivers] [--limit N] [--installed <csv>] [--markers <csv>] [--claims <csv>] [--flags <csv>] [--sessions <目录>] [--buckets <csv>] [--cache <csv>] [--ct <csv>] [--version-flags <csv>] [--mcp <csv>] [--skeleton-families <csv>] [--minimal] [--ccpinterface-name <名>] [--stack-entry <条目>] [--pending <32hex>] [--time <文本>]",
         "--manifests：清单目录（通常是 <image>\\Windows\\WinSxS\\Manifests）。\n"
         "--packages：包目录（<image>\\Windows\\servicing\\Packages），部署记录用。\n"
         "  可以只给镜像根（自动补 Windows\\servicing\\Packages），也可以不给——\n"
         "  会从 --manifests 反推；推不出来直接报错，不会拿空目录硬跑。\n"
         "--staged：镜像的 WinSxS，用来判断哪些文件真的在盘上（f! 值与 MCP 统计）。\n"
         "--arch：x86|arm|arm64|amd64|ia64（台账根值里的架构）。\n"
         "--stack：服务栈版本文本，如 \"6.2.8061.0 (fbl_core1_soc.110803-1830)\"。\n"
         "--out：计划输出目录；本命令会写 components.txt / deployments.txt /\n"
         "  versioned.txt / root.txt（--drivers 时还有 drivers.txt）。\n"
         "--drivers：额外生成现代镜像的 Drivers 台账计划。\n"
         "--limit：只处理前 N 份清单（调试用）。\n"
         "--installed/--markers/--claims/--flags/--sessions：转给部署计划（安装历史输入）。\n"
         "--buckets/--cache/--ct/--version-flags/--mcp/--skeleton-families：转给版本索引。\n"
         "--minimal/--ccpinterface-name/--stack-entry/--pending/--time：转给根值计划。\n"
         "每一步失败即停，最后打印每步耗时与下一步命令；全局开关会被带到每一步。",
         "ocid_wssm workflow plan --manifests E:\\v\\mnt9600m\\Windows\\WinSxS\\Manifests --packages E:\\v\\mnt9600m\\Windows\\servicing\\Packages --staged E:\\v\\mnt9600m\\Windows\\WinSxS --arch amd64 --stack \"6.3.9600.16384\" --out E:\\plans --drivers",
         nullptr},
        {"workflow verify", "工作流",
         "校验计划：组件计划里每个文件是否真的在镜像里；可选再自检台账。",
         "ocid_wssm workflow verify <计划目录> <WinSxS 目录> [--hive <hiveSpec>] [--limit N]",
         "<计划目录>：workflow plan 的 --out 目录（读其中的 components.txt）。\n"
         "第 1 步：验证 f! 值对应的文件确实在 <WinSxS> 里（提交前防漏落位）。\n"
         "第 2 步（--hive）：对已挂载的台账做自洽校验（根值、交叉引用、家庭/版本节点）。\n"
         "--hive：形如 HKLM\\OCIDCOMP 的已挂载路径；不给就只做文件校验。\n"
         "任一步失败即停，退出码为失败那一步的退出码。",
         "ocid_wssm workflow verify E:\\plans E:\\v\\mnt9600m\\Windows\\WinSxS --hive HKLM\\OCIDCOMP",
         nullptr},
        {"workflow pcm", "工作流",
         "批量构建 ManifestCache 块（每份清单一个 .pcm），可选接着跑对拍回归。",
         "ocid_wssm workflow pcm --manifests <清单目录> --out <输出目录> [--b0 N] [--limit N] [--diff <真机 PCM 目录>]",
         "--manifests：组件清单目录（16299+ 需全局 --dcm-dictionary）。\n"
         "--out：输出目录；文件名 = 清单文件名 + \".pcm\"。\n"
         "--b0：容器头 b0 的附加位（0..7），缺省 0；真机样本里不可复现的那几位由它输入。\n"
         "--limit：只构建前 N 份（调试用）。\n"
         "--diff：构建完接着做对拍统计；真机 PCM 目录里的文件名必须与本命令产出的\n"
         "  文件名**同名**（真机块名是哈希 tag，通常要先用 python 侧脚本按字符串池配对改名）。\n"
         "全局 --os-build 决定 16299+ 的记录形状（例如 104 B 文件记录），别忘了给。",
         "ocid_wssm workflow pcm --manifests E:\\ocid_dism\\16299\\mf --out E:\\built16299 --os-build 16299 --dcm-dictionary E:\\ocid_dism\\dcm\\dict.bin",
         nullptr},

        // ── 清单 ────────────────────────────────────────────────────────────
        {"manifest info", "清单",
         "打印一份组件清单的解析结果（身份、文件、注册表声明、依赖）。",
         "ocid_wssm manifest info <manifest>",
         "输入一份 .manifest（明文；16299+ 的 DCM 压缩清单要带全局 --dcm-dictionary）。\n"
         "输出身份文本、层级、<file> 清单、registryKeys/configurationSchema 数与依赖。",
         "ocid_wssm manifest info E:\\v\\mnt9600m\\Windows\\WinSxS\\Manifests\\amd64_1394.inf_*.manifest",
         "info"},
        {"manifest ops", "清单",
         "把清单展开成文件/注册表操作列表（人读或 JSON）。",
         "ocid_wssm manifest ops <manifest> [--json]",
         "--json：每行一个 JSON 对象，便于 C# 端消费；不给是 TSV 人读格式。",
         "ocid_wssm manifest ops a.manifest --json > ops.jsonl",
         "ops"},
        {"manifest scan", "清单",
         "批量扫一个清单目录，统计解析成功/失败与操作数。",
         "ocid_wssm manifest scan <目录> [maxFiles]",
         "不需要镜像，用来快速确认整目录清单都能解析。",
         "ocid_wssm manifest scan E:\\v\\mnt9600m\\Windows\\WinSxS\\Manifests 200",
         "scan"},
        {"manifest xsd", "清单",
         "导出清单里内嵌的配置 schema（XSD）到文件。",
         "ocid_wssm manifest xsd <manifest> <输出文件>",
         "排障用：configurationSchema 被当作原文塞在清单里。",
         "ocid_wssm manifest xsd a.manifest a.xsd",
         "dump-xsd"},
        {"manifest export", "清单",
         "目录里每份清单的操作按 JSONL 输出（给 C# 服务消费）。",
         "ocid_wssm manifest export <目录> [maxFiles] [--list <路径列表文件>]",
         "--list：用文件列表代替扫目录，每行一个 .manifest 绝对路径。",
         "ocid_wssm manifest export E:\\v\\mnt9600m\\Windows\\WinSxS\\Manifests > ops.jsonl",
         "export-ops"},
        {"manifest names", "清单",
         "用真实文件名反查组件名推导规则是否正确。",
         "ocid_wssm manifest names <目录> [maxFiles]",
         "对比清单文件名里的组件名与按身份推导出的名字，用来验证命名规则。",
         "ocid_wssm manifest names E:\\v\\mnt9600m\\Windows\\WinSxS 500",
         "verify-names"},
        {"manifest expand", "清单",
         "展开文本里的 $(runtime.*) 宏。",
         "ocid_wssm manifest expand <Windows 目录> <文本...> [--paths]",
         "--paths：同时打印每个宏对应的基准路径。",
         "ocid_wssm manifest expand E:\\v\\mnt9600m\\Windows \"$(runtime.system32)\\drivers\" --paths",
         "expand"},
        {"manifest macros", "清单",
         "列出已知的 $(runtime.*) 宏名。",
         "ocid_wssm manifest macros",
         "不需要参数。",
         "ocid_wssm manifest macros",
         "macros"},

        // ── 落位 ────────────────────────────────────────────────────────────
        {"place plan", "落位",
         "生成文件落位计划（JSONL）：载荷 → WinSxS 正本 → 落点。",
         "ocid_wssm place plan --windows <WinDir> (--list <路径列表文件> | <目录>) [maxFiles] [--packages <载荷根>] [--image-arch <arch>]",
         "--windows：离线镜像的 Windows 目录（算落点与宏用）。\n"
         "--packages：载荷根（通常是 <image>\\Windows\\WinSxS）。\n"
         "--image-arch：arm|arm64|amd64|x86|none；缺省按 ntoskrnl.exe 探测，none = 不做架构红移。\n"
         "输出每行：componentDir/identity/kind/path/storePath/destinationPath/payloadPath/sddl 等。",
         "ocid_wssm place plan --windows W:\\Windows --packages W:\\Windows\\WinSxS W:\\Windows\\WinSxS\\Manifests > place.jsonl",
         "place-ops"},
        {"place csi", "落位",
         "执行落位计划：载荷复制进正本、落点建硬链接、最后套 SDDL。",
         "ocid_wssm place csi <plan.jsonl> [--no-acl]",
         "顺序：先进正本（CopyFileW）、再硬链接落点（失败退化复制）、最后给文件/目录上锁。\n"
         "--no-acl：跳过 SDDL（只做文件落位，调试用）。\n"
         "未展开宏（$(...)）的行会跳过并计数，不瞎猜。",
         "ocid_wssm place csi place.jsonl",
         "csi-place"},

        // ── 驱动 ────────────────────────────────────────────────────────────
        {"driver ops", "驱动",
         "驱动包导入计划（JSONL）：每行一个驱动包（componentDir/StoreId/载荷清单）。",
         "ocid_wssm driver ops (--list <路径列表文件> | <清单目录>) [maxFiles] [--packages <载荷根>]",
         "只输出 Type=driverUpdate 的包；--packages 用来检查载荷是否齐（缺文件 ok=false）。",
         "ocid_wssm driver ops E:\\v\\mnt9600m\\Windows\\WinSxS\\Manifests --packages E:\\v\\mnt9600m\\Windows\\WinSxS > drv.jsonl",
         "dmi-ops"},
        {"driver ledger", "驱动",
         "从已存在的 FileRepository 目录生成 DriverDatabase 记录（JSONL）。",
         "ocid_wssm driver ledger <FileRepository 目录> [--list <目录列表文件>] [--arch <arch>] [--inbox] [maxFiles]",
         "镜像里已经有驱动仓库、只缺台账时用它补；会重算 StoreId 校验目录名。\n"
         "--inbox：收件箱驱动（INF 保留原名、不占 oemN）。",
         "ocid_wssm driver ledger E:\\v\\mnt9600m\\Windows\\System32\\DriverStore\\FileRepository --arch x86",
         "dmi-ledger"},
        {"driver place", "驱动",
         "把驱动包真正落进离线镜像：FileRepository + Windows\\INF\\oemN.inf + DriverDatabase 计划。",
         "ocid_wssm driver place <plan.jsonl> <镜像根> --packages <载荷根> [--plan-out <txt>] [--os 6.2] [--signer <名>] [--signer-score <dword>] [--inbox] [--dry-run]",
         "--packages：载荷根（必须给）。\n"
         "--plan-out：产出的 DriverDatabase 台账计划，喂 store write。\n"
         "--inbox：收件箱驱动，INF 保留原名、不占 oemN，0005 写 1。\n"
         "--signer/--signer-score：0007/0008 两个值（清单推不出，需 .cat 验签结果作输入）。\n"
         "--dry-run：只算不写。",
         "ocid_wssm driver place drv.jsonl W:\\ --packages E:\\v\\mnt9600m\\Windows\\WinSxS --plan-out driverdb.txt --inbox",
         "dmi-place"},

        // ── 台账（读与校验、写入）──────────────────────────────────────────
        {"store scan", "台账",
         "把 COMPONENTS 台账全量解码再编码，逐值比对（读侧自证）。",
         "ocid_wssm store scan <hiveSpec> [--limit N]",
         "hiveSpec：形如 HKLM\\OCIDCOMP 的已挂载路径（需要管理员挂载）。\n"
         "输出组件/部署/catalog 计数与 mismatch 数；result: OK 才算过。",
         "ocid_wssm store scan HKLM\\OCIDCOMP",
         "cbs-scan"},
        {"store keys", "台账",
         "用真机导出的 key,identity CSV 核对伪键后缀或完整键名。",
         "ocid_wssm store keys <csv 文件> [--names component|deployment]",
         "CSV 两列带引号：\"<键名>\",\"<身份文本>\"。\n"
         "--names：不比后缀，比完整键名。",
         "ocid_wssm store keys components_all.csv",
         "cbs-keys"},
        {"store families", "台账",
         "核对由组件身份算出的家庭键是否真的出现在真机 VersionedIndex 家庭集合里。",
         "ocid_wssm store families <组件 CSV> <家庭键文件>",
         "家庭键文件每行一个家庭键。",
         "ocid_wssm store families components_all.csv families.txt",
         "cbs-families"},
        {"store dump", "台账",
         "把整个台账导出成规范排序文本（对拍基准）。",
         "ocid_wssm store dump <hiveSpec> <输出文件>",
         "行格式：K<TAB>路径 / V<TAB>路径<TAB>值名<TAB>类型<TAB>hex。",
         "ocid_wssm store dump HKLM\\OCIDCOMP truth.txt",
         "cbs-dump"},
        {"store diff", "台账",
         "比较两份台账导出文本（逐键/逐值）。",
         "ocid_wssm store diff <a.txt> <b.txt> [--limit N]",
         "输出 only-in-a / only-in-b 的键数与值数，以及前 N 条差异；全 0 才算一致。",
         "ocid_wssm store diff ours.txt truth.txt --limit 20",
         "cbs-diff"},
        {"store verify", "台账",
         "台账自洽校验（不需要参考样本）。",
         "ocid_wssm store verify <hiveSpec>",
         "检查根值、组件/部署/catalog 交叉引用、家庭/版本节点、存储 ID 形态。",
         "ocid_wssm store verify HKLM\\OCIDCOMP",
         "cbs-verify-store"},
        {"store verify-files", "台账",
         "校验组件计划里每个 f! 值的文件是否真的在镜像里（提交前防漏落位）。",
         "ocid_wssm store verify-files <组件计划文本> <WinSxS 目录>",
         "配合 workflow plan 产出的 components.txt 使用。",
         "ocid_wssm store verify-files E:\\plans\\components.txt E:\\v\\mnt9600m\\Windows\\WinSxS",
         "cbs-verify-files"},
        {"store write", "台账",
         "把计划文本落成 hive 文件（或追加进已有 hive）。",
         "ocid_wssm store write <plan.txt> <hivePath> [--transactional] [--append]",
         "缺省：造空壳再写（RegSaveKey 种子键 + RegLoadKey）。\n"
         "--append：挂在已存在的 hive 上追加写（例如往镜像的 SYSTEM hive 写 DriverDatabase）。\n"
         "--transactional：整包写入放进 KTM 事务，失败回滚。\n"
         "需要管理员权限。",
         "ocid_wssm store write plans\\components.txt COMPONENTS.hiv --transactional",
         "cbs-write"},

        // ── 生成 ────────────────────────────────────────────────────────────
        {"plan components", "生成",
         "生成组件记录（DerivedData\\Components）：S256H、identity、f!、c!、CF 等。",
         "ocid_wssm plan components <清单目录> <输出文件> [--staged <WinSxS>] [--limit N]",
         "--staged：镜像的 WinSxS，用来决定哪些文件真的在盘上（f! 值）。\n"
         "全局开关：--dcm-dictionary（现代 DCM 清单）、--file-flags <csv>（现代 f! 状态位）、\n"
         "--os-build <构建号>（版本兼容分档兜底；不给则从 ntoskrnl.exe / --stack-version 探测）。",
         "ocid_wssm plan components E:\\v\\mnt9600m\\Windows\\WinSxS\\Manifests plan-c.txt --staged E:\\v\\mnt9600m\\Windows\\WinSxS --os-build 9600",
         "cbs-plan-components"},
        {"plan deployments", "生成",
         "生成部署记录与 catalog 标记（CanonicalData\\Deployments / Catalogs）。",
         "ocid_wssm plan deployments <Packages 目录> <清单目录> <输出文件> [--markers <csv>] [--installed <csv>] [--claims <csv>] [--flags <csv>] [--sessions <目录>]",
         "--installed：包身份|CurrentState（bit 0x40 = installed）。\n"
         "--claims：组件键|部署键（c! 归属；安装计划输入）。\n"
         "--flags：组件键|CF 十进制（组件标志位）。\n"
         "--markers：部署键|appid|CatalogThumbprint|marks（p!/s!/i! 标记）。\n"
         "--sessions：<win>\\Windows\\servicing\\Sessions（按 <Resolve> 决定 CatalogThumbprint）。",
         "ocid_wssm plan deployments E:\\v\\mnt9600m\\Windows\\servicing\\Packages E:\\v\\mnt9600m\\Windows\\WinSxS\\Manifests plan-d.txt --installed installed.csv",
         "cbs-plan-deployments"},
        {"plan versioned", "生成",
         "生成版本索引（DerivedData\\VersionedIndex）：家庭键、版本节点、f256!、CT、MCP。",
         "ocid_wssm plan versioned <清单目录> <Packages 目录> <WinSxS> <服务栈版本文本> <输出文件> [--buckets <csv>] [--cache <csv>] [--ct <csv>] [--version-flags <csv>] [--mcp <csv>] [--skeleton-families <csv>]",
         "--buckets：家庭键|桶文本（安装历史：组件留在更早服务栈的桶里）。\n"
         "--cache：桶文本|ManifestCacheIdentifier|UtilizedSpace。\n"
         "--mcp：桶文本|familyKey|versionKey|offset|length（ManifestCache 槽表）。\n"
         "--ct：家庭键|版本|组件键|CT（版本节点 CatalogThumbprint）。\n"
         "--version-flags：家庭键|版本|(default) 位图。\n"
         "--skeleton-families：只发家庭键/空版本节点/SomeUnparsedVersionsExist。",
         "ocid_wssm plan versioned E:\\v\\mnt9600m\\Windows\\WinSxS\\Manifests E:\\v\\mnt9600m\\Windows\\servicing\\Packages E:\\v\\mnt9600m\\Windows\\WinSxS \"6.3.9600.16384\" plan-v.txt",
         "cbs-plan-versioned"},
        {"plan root", "生成",
         "生成台账根值（格式版本、架构、执行态、形态开关）。",
         "ocid_wssm plan root <arch> <服务栈版本文本> <输出文件> [--pending <32hex>] [--time <文本>] [--minimal] [--stack-entry <名>|<文本>] [--ccpinterface-name <名>] [--no-configuration] [--noncanonical-data] [--transform-settings] [--installers-root] [--installers-shortcut]",
         "arch：x86|arm|arm64|amd64|ia64。\n"
         "--minimal：只发最小根值（现代镜像用）。\n"
         "--stack-entry：ServicingStackVersions 条目（可多次给）。\n"
         "--no-configuration / --noncanonical-data / --transform-settings / --installers-root /\n"
         "--installers-shortcut：按镜像代开关形态键。",
         "ocid_wssm plan root amd64 \"6.3.9600.16384\" plan-root.txt --ccpinterface-name CCPInterface",
         "cbs-plan-root"},
        {"plan drivers", "生成",
         "生成现代镜像的 Drivers 台账（Drivers\\<arch>\\<INF>\\<base64(SHA-256(INF))>）。",
         "ocid_wssm plan drivers <Packages 目录> <WinSxS 目录> <输出文件> [--installed <csv>]",
         "值名：<包身份>~<update 名>，数据 DWORD 0；架构取驱动自身声明，dual 目录逐候选试。\n"
         "只在现代镜像（16299+，清单是 DCM）装配时使用。",
         "ocid_wssm plan drivers E:\\v\\mnt16299\\Windows\\servicing\\Packages E:\\v\\mnt16299\\Windows\\WinSxS plan-drv.txt --installed installed.csv",
         "cbs-plan-drivers"},

        // ── 缓存 ────────────────────────────────────────────────────────────
        {"pcm tables", "缓存",
         "逐个 .pcm 解码表再重编码，按类型统计总数与逐字节一致数（表编码器自检）。",
         "ocid_wssm pcm tables <pcm 目录>",
         "目录里每个 .pcm 都会被解表并重编码，不打印表内容，只报逐字节一致率；\n"
         "想确认「表编码器没写坏」时用它，比逐份 diff 快得多。",
         "ocid_wssm pcm tables E:\\ocid_verify\\pcm9600\\blocks",
         "cbs-pcm-tables"},
        {"pcm reencode", "缓存",
         "拆 PcmH 再按容器语义重编码，报是否逐字节一致。",
         "ocid_wssm pcm reencode <真机 pcm> <输出 pcm>",
         "用来验证我们的容器编码/解码是自洽的（与构建器分开）。",
         "ocid_wssm pcm reencode real.pcm out.pcm",
         "cbs-pcm-reencode"},
        {"pcm build", "缓存",
         "从一份清单构建 PCM 块（PcmH）。",
         "ocid_wssm pcm build <manifest> <输出 pcm> [b0Extra]",
         "b0Extra：容器头 b0 的附加位（0..7）；真机样本里不可复现的那几位由它输入，缺省 0。\n"
         "16299+ 需要全局 --dcm-dictionary 与 --os-build（决定记录形状）。",
         "ocid_wssm pcm build a.manifest a.pcm 2 --os-build 9600",
         "cbs-pcm-build"},
        {"pcm diff", "缓存",
         "PCM 构建器对语料的回归统计：T1 槽数 / T2 type+len / T3 payload / T4 整文件。",
         "ocid_wssm pcm diff <真机目录> <我方产物目录> [--limit N]",
         "两份目录里按同名文件配对（builtMissing 报我方差缺）。\n"
         "通常配合 workflow pcm 使用。",
         "ocid_wssm pcm diff real_all built_all --limit 50",
         "cbs-pcm-diff"},
        {"pcm microdom", "缓存",
         "清单 XML → MdHd 微 DOM 段（与真机 ManifestCache 的 MdHd 表对拍）。",
         "ocid_wssm pcm microdom <manifest> <输出文件>",
         "拆开微 DOM 时用：确认节点顺序、属性、文本节点都对。",
         "ocid_wssm pcm microdom a.manifest a.mdhd",
         "cbs-microdom"},

        // ── DCM ─────────────────────────────────────────────────────────────
        {"dcm decode", "DCM",
         "解 DCM 紧凑清单（16299+）成明文 XML。",
         "ocid_wssm dcm decode <输入 manifest> <输出 xml> --dcm-dictionary <wcp.dll|.bin>",
         "字典来自镜像服务栈的 wcp.dll（资源 type 614 id 1），也可用 dcm dictionary 先导出成 .bin。\n"
         "给 .bin 时底层会自动识别（不必再带 wcp.dll）。",
         "ocid_wssm dcm decode a.manifest a.xml --dcm-dictionary E:\\ocid_dism\\dcm\\dict.bin",
         "cbs-dcm-decode"},
        {"dcm dictionary", "DCM",
         "把 wcp.dll 里的 DCM 字典导出成独立文件（.bin）。",
         "ocid_wssm dcm dictionary <wcp.dll> <输出 .bin>",
         "导出后全局 --dcm-dictionary 直接给这份 .bin：装配时不必再带 wcp.dll，\n"
         "也不再 LoadLibraryEx 去读微软二进制里的资源。",
         "ocid_wssm dcm dictionary E:\\v\\mnt9600m\\Windows\\WinSxS\\...\\wcp.dll dict.bin",
         "dcm-dump-dictionary"},

        // ── 工具 ────────────────────────────────────────────────────────────
        {"tool hash", "工具",
         "算 H39 文本哈希（伪键折叠 / 值名后缀用的那个）。",
         "ocid_wssm tool hash <文本...>",
         "多个参数逐行输出，方便和真机取值对拍。",
         "ocid_wssm tool hash \"x86_microsoft-windows-servicingstack_31bf3856ad364e35_6.2.8061.0_none\"",
         "cbs-hash"},
        {"run", "工具",
         "把命令行原样透传给 ocid_manifest 的既有命令（兼容老脚本）。",
         "ocid_wssm run <旧命令> [参数...]",
         "等价于直接 `ocid_wssm <旧命令> ...`；后者也可用（未知路径时按旧命令当第一段处理）。\n"
         "旧命令有 30 多条，帮助文本用 `ocid_manifest help` 看。",
         "ocid_wssm run cbs-pcm-tables sample.pcm",
         nullptr},
    };

    // ── 文档查找与打印 ──────────────────────────────────────────────────────
    std::vector<std::string> SplitWords(const std::string& text)
    {
        std::vector<std::string> out;
        size_t i = 0;
        while (i < text.size()) {
            while (i < text.size() && text[i] == ' ') ++i;
            const size_t begin = i;
            while (i < text.size() && text[i] != ' ') ++i;
            if (i > begin) out.push_back(text.substr(begin, i - begin));
        }
        return out;
    }

    std::string PadRight(const std::string& s, size_t width)
    {
        if (s.size() >= width) return s;
        return s + std::string(width - s.size(), ' ');
    }

    // 文档路径的两段词，用来做最长前缀匹配（"store verify" 比 "store" 更长）。
    const Doc* FindDoc(const std::vector<std::string>& words, size_t* matchedWords = nullptr)
    {
        const Doc* best = nullptr;
        size_t bestLen = 0;
        for (const Doc& doc : kDocs) {
            const std::vector<std::string> path = SplitWords(doc.path);
            if (path.size() > words.size()) continue;
            bool ok = true;
            for (size_t i = 0; i < path.size(); ++i)
                if (path[i] != words[i]) { ok = false; break; }
            if (ok && path.size() > bestLen) { best = &doc; bestLen = path.size(); }
        }
        if (matchedWords) *matchedWords = bestLen;
        return best;
    }

    const char* GroupSummary(const std::string& group)
    {
        size_t i = 0;
        for (const char* g : kGroupOrder) {
            if (group == g) return kGroupSummary[i];
            ++i;
        }
        return "";
    }

    // 组名/别名 → 规范组名（中文）。允许英文别名，方便脚本和英文习惯的调用：
    //   env / manifest / place / driver / store / plan / pcm / dcm / tool / workflow
    std::string ResolveGroup(const std::string& name)
    {
        struct Alias
        {
            const char* alias;
            const char* group;
        };
        static const Alias kAliases[] = {
            {"env", "环境"}, {"环境", "环境"},
            {"workflow", "工作流"}, {"工作流", "工作流"},
            {"manifest", "清单"}, {"清单", "清单"},
            {"place", "落位"}, {"落位", "落位"},
            {"driver", "驱动"}, {"驱动", "驱动"},
            {"store", "台账"}, {"台账", "台账"},
            {"plan", "生成"}, {"生成", "生成"},
            {"pcm", "缓存"}, {"缓存", "缓存"},
            {"dcm", "DCM"}, {"DCM", "DCM"}, {"dcm-format", "DCM"},
            {"tool", "工具"}, {"工具", "工具"},
        };
        for (const Alias& a : kAliases)
            if (name == a.alias) return a.group;
        return std::string();
    }

    void PrintVersion()
    {
        OutLn("OCID WSSM —— Windows Servicing Stack Manager");
        OutLn(std::string("版本：") + OCID_WSSM_VERSION);
        OutLn(std::string("构建：") + __DATE__ + " " + __TIME__);
#if defined(_M_X64) || defined(__x86_64__)
        OutLn("目标架构：x64");
#elif defined(_M_IX86) || defined(__i386__)
        OutLn("目标架构：x86");
#elif defined(_M_ARM64)
        OutLn("目标架构：arm64");
#else
        OutLn("目标架构：其它");
#endif
        size_t leaves = 0;
        for (const Doc& doc : kDocs) {
            if (std::string(doc.path) == "version") continue;
            ++leaves;
        }
        OutLn("命令数：" + std::to_string(leaves) + "（含 workflow 组合命令）");
        OutLn("统一入口：ocid_manifest 的全部原语 + 多步工作流 + 环境自检");
        OutLn("");
        OutLn("用法：ocid_wssm <组> <命令> [参数...]");
        OutLn("      ocid_wssm help [<组> [<命令>]]");
        OutLn("      ocid_wssm <旧命令> ...        兼容：直接透传给 ocid_manifest 的既有命令");
        OutLn("");
        OutLn("语言：zh-CN（默认也是当前唯一内置语言；命令名与参数保持英文）。");
    }

    void PrintUsage()
    {
        OutLn("OCID WSSM —— Windows Servicing Stack Manager（离线服务栈统一工作台）");
        OutLn(std::string("版本 ") + OCID_WSSM_VERSION + "，构建于 " + __DATE__);
        OutLn("");
        OutLn("用法：ocid_wssm <组> <命令> [参数...]");
        OutLn("      ocid_wssm help                       列出全部命令（按组）");
        OutLn("      ocid_wssm help <组>                  列出该组的命令");
        OutLn("      ocid_wssm help <组> <命令>           看单条命令的用法、选项与示例");
        OutLn("      ocid_wssm help --all                 打印全部命令的完整帮助");
        OutLn("      ocid_wssm <旧命令> [参数...]         兼容：透传给 ocid_manifest 的既有命令");
        OutLn("");
        OutLn("全局开关（可出现在任意位置，由底层命令消费）：");
        OutLn("  --dcm-dictionary <wcp.dll|.bin>   DCM 清单（16299+）解压字典");
        OutLn("  --file-flags <csv>                现代镜像的 f! 状态位（<组件键>|<f! 值名>|<值>）");
        OutLn("  --os-build <构建号>               台账/记录形状的版本分档兜底（如 9600 / 16299）");
        OutLn("  --lang zh-CN                      帮助语言（默认 zh-CN，当前唯一内置语言）");
        OutLn("");
        for (const char* group : kGroupOrder) {
            OutLn(std::string("【") + group + "】 " + GroupSummary(group));
            size_t width = 0;
            for (const Doc& doc : kDocs)
                if (group == doc.group) width = std::max(width, std::string(doc.path).size());
            width += 2;
            for (const Doc& doc : kDocs)
                if (group == doc.group)
                    OutLn("  " + PadRight(doc.path, width) + doc.summary);
            OutLn("");
        }
        OutLn("数据走 stdout，诊断走 stderr。退出码：0 = 成功；1 = 命令失败；2 = 用法错误或自检失败。");
    }

    void PrintDoc(const Doc& doc, bool verbose);  // 定义在后面，这里先声明（PrintDocsByPrefix 要用）

    void PrintGroup(const std::string& group)
    {
        OutLn(std::string("【") + group + "】 " + GroupSummary(group));
        OutLn("");
        size_t width = 0;
        for (const Doc& doc : kDocs)
            if (group == doc.group) width = std::max(width, std::string(doc.path).size());
        width += 2;
        for (const Doc& doc : kDocs) {
            if (group != doc.group) continue;
            OutLn("  " + PadRight(doc.path, width) + doc.summary);
        }
        OutLn("");
        OutLn("看单条命令的细节：ocid_wssm help " + group + " <命令>");
    }

    std::string JoinWords(const std::vector<std::string>& words)
    {
        std::string joined;
        for (size_t i = 0; i < words.size(); ++i) {
            if (i != 0) joined += ' ';
            joined += words[i];
        }
        return joined;
    }

    // 列出命令路径以 prefix 开头的所有命令（`ocid_wssm help 缓存 pcm` 用）。
    // 返回是否打印了至少一条。
    bool PrintDocsByPrefix(const std::vector<std::string>& prefix)
    {
        const std::string joined = JoinWords(prefix) + " ";
        bool first = true;
        for (const Doc& doc : kDocs) {
            const std::string path = doc.path;
            if (path.size() <= joined.size() || path.compare(0, joined.size(), joined) != 0) continue;
            if (!first) { OutLn(""); OutLn("──────────────────────────────────────────────"); OutLn(""); }
            first = false;
            PrintDoc(doc, false);
        }
        return !first;
    }

    void PrintDoc(const Doc& doc, bool withHeader = true)
    {
        if (withHeader) {
            OutLn(std::string("命令：") + doc.path + "（" + doc.group + "）");
            OutLn("");
        }
        OutLn("用途：");
        OutLn(std::string("  ") + doc.summary);
        OutLn("");
        OutLn("用法：");
        OutLn(std::string("  ") + doc.usage);
        OutLn("");
        OutLn("选项与说明：");
        std::string notes = doc.options ? doc.options : "";
        size_t begin = 0;
        while (begin <= notes.size()) {
            const size_t nl = notes.find('\n', begin);
            const std::string line = notes.substr(begin, nl == std::string::npos ? std::string::npos : nl - begin);
            OutLn(std::string("  ") + line);
            if (nl == std::string::npos) break;
            begin = nl + 1;
        }
        OutLn("");
        OutLn("示例：");
        OutLn(std::string("  ") + doc.example);
        OutLn("");
        if (doc.legacy)
            OutLn(std::string("实现：转发给 ocid_manifest 的 `") + doc.legacy + "`（原语只有一份实现）。");
        else
            OutLn("实现：本程序内置。");
        OutLn(std::string("同组其它命令：ocid_wssm help ") + doc.group);
    }

    void PrintAllDocs()
    {
        OutLn("OCID WSSM 全部命令帮助");
        OutLn("");
        bool first = true;
        for (const char* group : kGroupOrder) {
            for (const Doc& doc : kDocs) {
                if (group != doc.group) continue;
                if (!first) { OutLn(""); OutLn("──────────────────────────────────────────────"); OutLn(""); }
                first = false;
                PrintDoc(doc);
            }
        }
    }

    // ── 转发 ────────────────────────────────────────────────────────────────
    //
    // 把一条命令行交给 ocid_manifest 的实现。globals 追加在末尾：底层会各自扫描
    // 并摘掉这些开关，所以位置无关。
    int RunLegacy(const std::vector<std::string>& argv)
    {
        // OcidManifestRun keeps the usual main() convention: argv[0] is the
        // program name, argv[1] is the command. A synthetic argv[0] must be
        // prepended here, otherwise the primitive takes the first real argument
        // as its command and prints usage instead of running anything.
        std::vector<std::string> all;
        all.reserve(argv.size() + g_globals.size() + 1);
        all.push_back("ocid_wssm");
        for (const std::string& a : argv) all.push_back(a);
        for (const std::string& g : g_globals) all.push_back(g);
        std::vector<char*> ptrs;
        ptrs.reserve(all.size() + 1);
        for (std::string& s : all) ptrs.push_back(&s[0]);
        ptrs.push_back(nullptr);
        fflush(stdout);
        return OcidManifestRun(static_cast<int>(all.size()), ptrs.data());
    }

    // ── 小工具 ──────────────────────────────────────────────────────────────
    std::string HumanBytes(std::uint64_t bytes)
    {
        static const char* units[] = {"B", "KiB", "MiB", "GiB", "TiB"};
        double v = static_cast<double>(bytes);
        size_t u = 0;
        while (v >= 1024.0 && u + 1 < sizeof(units) / sizeof(units[0])) { v /= 1024.0; ++u; }
        char buf[64];
        if (u == 0) std::snprintf(buf, sizeof(buf), "%llu %s", static_cast<unsigned long long>(bytes), units[u]);
        else std::snprintf(buf, sizeof(buf), "%.1f %s", v, units[u]);
        return std::string(buf);
    }

    bool FileExistsSize(const std::filesystem::path& p, std::uint64_t* size = nullptr)
    {
        std::error_code ec;
        if (!std::filesystem::is_regular_file(p, ec)) return false;
        if (size) {
            const auto sz = std::filesystem::file_size(p, ec);
            if (ec) return false;
            *size = static_cast<std::uint64_t>(sz);
        }
        return true;
    }

    bool DirExists(const std::filesystem::path& p)
    {
        std::error_code ec;
        return std::filesystem::is_directory(p, ec);
    }

    std::uint64_t DirFreeBytes(const std::filesystem::path& p)
    {
#ifdef _WIN32
        std::error_code ec;
        const std::filesystem::path root = p.root_path();
        if (root.empty()) return 0;
        ULARGE_INTEGER freeBytes{};
        if (!GetDiskFreeSpaceExW(root.c_str(), &freeBytes, nullptr, nullptr)) return 0;
        return static_cast<std::uint64_t>(freeBytes.QuadPart);
#else
        (void)p;
        return 0;
#endif
    }

    // 统计目录里匹配扩展名的文件数（不递归；达到上限就停，避免在几万文件上白扫）。
    std::uint64_t CountFiles(const std::filesystem::path& dir, const std::string& ext, std::uint64_t limit)
    {
        std::uint64_t n = 0;
        std::error_code ec;
        std::filesystem::directory_iterator it(dir, ec);
        if (ec) return 0;
        for (; it != std::filesystem::directory_iterator(); it.increment(ec)) {
            if (ec) break;
            const std::string name = it->path().filename().string();
            if (name.size() >= ext.size() &&
                name.compare(name.size() - ext.size(), ext.size(), ext) == 0)
                ++n;
            if (limit && n >= limit) break;
        }
        return n;
    }

    // 把 "--packages" 归一成真正含 .mum 的目录（Windows\servicing\Packages）。
    // 允许三种写法：①已经是 Packages 目录；②给镜像根（自动补 Windows\servicing\Packages）；
    // ③什么都不给，只给 --manifests（从 <image>\Windows\WinSxS\Manifests 反推）。
    // 找不到就返回空串，调用方负责报错。
    std::string ResolvePackagesDir(const std::string& given, const std::string& manifests)
    {
        if (!given.empty()) {
            const std::filesystem::path dir(FromUtf8OrAnsi(given.c_str()));
            if (DirExists(dir)) {
                if (CountFiles(dir, ".mum", 1) > 0) return dir.string();
                const std::filesystem::path inner =
                    dir / L"Windows" / L"servicing" / L"Packages";
                if (DirExists(inner) && CountFiles(inner, ".mum", 1) > 0) return inner.string();
            }
        }
        if (!manifests.empty()) {
            const std::filesystem::path man(FromUtf8OrAnsi(manifests.c_str()));
            // <image>\Windows\WinSxS\Manifests → <image>\Windows\servicing\Packages
            const std::filesystem::path guess =
                man.parent_path().parent_path().parent_path() / L"servicing" / L"Packages";
            if (DirExists(guess) && CountFiles(guess, ".mum", 1) > 0) return guess.string();
        }
        return std::string();
    }

    bool IsElevated()
    {
#ifdef _WIN32
        HANDLE token = nullptr;
        bool elevated = false;
        if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) {
            TOKEN_ELEVATION info{};
            DWORD size = 0;
            if (GetTokenInformation(token, TokenElevation, &info, sizeof(info), &size))
                elevated = info.TokenIsElevated != 0;
            CloseHandle(token);
        }
        return elevated;
#else
        return false;
#endif
    }

    // 在 <WinSxS> 里找服务栈目录下的 wcp.dll（名字形如 x86_microsoft-windows-servicingstack_…）。
    std::filesystem::path FindServicingStackWcp(const std::filesystem::path& winSxs)
    {
        std::error_code ec;
        if (!DirExists(winSxs)) return std::filesystem::path();
        std::uint64_t scanned = 0;
        std::filesystem::directory_iterator it(winSxs, ec);
        if (ec) return std::filesystem::path();
        for (; it != std::filesystem::directory_iterator(); it.increment(ec)) {
            if (ec) break;
            if (++scanned > 60000) break;  // WinSxS 可能几万个子目录，扫到就停
            if (!it->is_directory(ec)) continue;
            const std::string name = it->path().filename().string();
            if (name.find("servicingstack") == std::string::npos) continue;
            const std::filesystem::path candidate = it->path() / "wcp.dll";
            std::uint64_t size = 0;
            if (FileExistsSize(candidate, &size) && size > 0) return candidate;
        }
        return std::filesystem::path();
    }

    // ── 带计数的自检结论 ────────────────────────────────────────────────────
    struct CheckCounts
    {
        int passed = 0;
        int warned = 0;
        int failed = 0;
    };

    void ReportPass(CheckCounts& c, const std::string& text)
    {
        ++c.passed;
        OutLn("[通过] " + text);
    }
    void ReportWarn(CheckCounts& c, const std::string& text)
    {
        ++c.warned;
        OutLn("[提示] " + text);
    }
    void ReportFail(CheckCounts& c, const std::string& text)
    {
        ++c.failed;
        OutLn("[失败] " + text);
    }

    // ── doctor ──────────────────────────────────────────────────────────────
    int CmdDoctor(const std::vector<std::string>& args)
    {
        std::filesystem::path windowsDir;
        std::filesystem::path imageRoot;
        std::filesystem::path dictionary;
        std::filesystem::path adkRoot;
        std::filesystem::path manifestDir;

        for (size_t i = 0; i < args.size(); ++i) {
            const std::string& a = args[i];
            auto take = [&](std::filesystem::path& target) {
                if (i + 1 < args.size()) target = std::filesystem::path(FromUtf8OrAnsi(args[++i].c_str()));
                else Err(std::string("参数缺少取值：") + a);
            };
            if (a == "--windows") take(windowsDir);
            else if (a == "--image") take(imageRoot);
            else if (a == "--dcm-dictionary") take(dictionary);
            else if (a == "--adk") take(adkRoot);
            else if (a == "--manifest-dir") take(manifestDir);
            else if (a == "--help" || a == "-h") { PrintDoc(*FindDoc(SplitWords("doctor"))); return 0; }
            else {
                Err(std::string("doctor 不认识这个参数：") + a);
                Err("用法：ocid_wssm doctor [--windows <WinDir>] [--image <镜像根>] [--dcm-dictionary <wcp.dll|.bin>] [--adk <ADK 根>] [--manifest-dir <清单目录>]");
                return 2;
            }
        }

        OutLn("OCID WSSM 环境自检");
        OutLn("");
        CheckCounts c;

        // 1. 控制台编码：SetupConsole 已经把输出代码页设成 UTF-8。
#ifdef _WIN32
        const unsigned int cp = GetConsoleOutputCP();
        if (cp == 65001) ReportPass(c, "控制台输出为 UTF-8（代码页 65001）");
        else ReportWarn(c, "控制台输出代码页是 " + std::to_string(cp) + "（不是 65001；中文可能显示异常，可 chcp 65001）");
#else
        ReportPass(c, "非 Windows 平台：跳过代码页检查");
#endif

        // 2. 权限：写台账 / 挂 hive / 落位都需要管理员。
        if (IsElevated()) ReportPass(c, "当前进程有管理员权限（可以挂载 hive、写 COMPONENTS）");
        else ReportWarn(c, "当前进程不是管理员：store write / store scan / driver place 会失败");

        // 3. 目标镜像
        if (!windowsDir.empty()) {
            if (!DirExists(windowsDir)) {
                ReportFail(c, "目标 Windows 目录不存在：" + windowsDir.string());
            } else {
                ReportPass(c, "目标 Windows 目录：" + windowsDir.string());
                const std::filesystem::path ntos = windowsDir / "System32" / "ntoskrnl.exe";
                std::uint64_t ntosSize = 0;
                if (FileExistsSize(ntos, &ntosSize)) {
                    std::int32_t build = 0;
                    std::string error;
                    if (CbsBuildFromImageFile(ntos.wstring(), build, error) && build > 0)
                        ReportPass(c, "ntoskrnl.exe 构建号 " + std::to_string(build) +
                                      "（" + CbsProfileSummary(CbsProfileForBuild(build)) + "）");
                    else
                        ReportWarn(c, "ntoskrnl.exe 存在但读不出构建号（" + error + "）；请加 --os-build 兜底");
                } else {
                    ReportWarn(c, "没找到 " + ntos.string() + "（不像是完整的 Windows 目录？）");
                }

                const std::filesystem::path sx = windowsDir / "WinSxS";
                if (DirExists(sx)) {
                    const std::uint64_t manifests = CountFiles(manifestDir.empty() ? sx / "Manifests" : manifestDir, ".manifest", 0);
                    ReportPass(c, "清单数：" + std::to_string(manifests) + " 份 .manifest（" +
                                  (manifestDir.empty() ? (sx / "Manifests").string() : manifestDir.string()) + "）");
                    const std::filesystem::path cache = sx / "ManifestCache";
                    std::uint64_t bins = 0;
                    std::string blobInfo;
                    std::error_code ec;
                    if (DirExists(cache)) {
                        std::filesystem::directory_iterator it(cache, ec);
                        for (; it != std::filesystem::directory_iterator(); it.increment(ec)) {
                            if (ec) break;
                            std::uint64_t size = 0;
                            if (FileExistsSize(it->path(), &size)) {
                                ++bins;
                                if (!blobInfo.empty()) blobInfo += "，";
                                blobInfo += it->path().filename().string() + " " + HumanBytes(size);
                            }
                        }
                    }
                    if (bins == 0) ReportWarn(c, "ManifestCache 里没有 blobs.bin（PCM 相关命令会没数据）");
                    else ReportPass(c, "ManifestCache：" + blobInfo);

                    const std::filesystem::path wcp = FindServicingStackWcp(sx);
                    if (wcp.empty()) ReportWarn(c, "WinSxS 里没找到服务栈 wcp.dll（DCM 字典 / f256 需要它）");
                    else {
                        std::uint64_t size = 0;
                        FileExistsSize(wcp, &size);
                        ReportPass(c, "服务栈 wcp.dll：" + wcp.string() + "（" + HumanBytes(size) + "）");
                    }
                } else {
                    ReportWarn(c, "没有 " + sx.string() + "（这不是挂载好的镜像）");
                }

                const std::uint64_t free = DirFreeBytes(windowsDir);
                if (free > 0) ReportPass(c, "所在磁盘可用空间：" + HumanBytes(free));
            }
        }

        // 4. COMPONENTS 台账
        if (!imageRoot.empty()) {
            const std::filesystem::path components = imageRoot / "Windows" / "System32" / "config" / "COMPONENTS";
            std::uint64_t size = 0;
            if (FileExistsSize(components, &size))
                ReportPass(c, "COMPONENTS 台账：" + components.string() + "（" + HumanBytes(size) + "）");
            else
                ReportFail(c, "找不到 " + components.string());
        }

        // 5. DCM 字典（16299+ 的清单压缩）
        if (!dictionary.empty()) {
            std::uint64_t size = 0;
            if (!FileExistsSize(dictionary, &size)) {
                ReportFail(c, "DCM 字典不存在：" + dictionary.string());
            } else if (size == 0) {
                ReportFail(c, "DCM 字典是空文件：" + dictionary.string());
            } else {
                std::vector<std::uint8_t> bytes;
                std::string error;
                bool ok = false;
                std::string ext = dictionary.extension().string();
                std::transform(ext.begin(), ext.end(), ext.begin(), [](unsigned char ch) { return (char)std::tolower(ch); });
                if (ext == ".dll") ok = LoadManifestDictionary(dictionary.wstring(), bytes, error);
                else ok = LoadManifestDictionaryFile(dictionary.wstring(), bytes, error);
                if (ok)
                    ReportPass(c, "DCM 字典可用（" + HumanBytes(size) + "，解出 " + HumanBytes(bytes.size()) + "）");
                else
                    ReportFail(c, "DCM 字典无法加载（" + error + "）：" + dictionary.string());
            }
        }

        // 6. ADK（桌面端构建链需要 tools\bin\i386\imageapp.exe）
        if (!adkRoot.empty()) {
            const std::filesystem::path candidates[] = {
                adkRoot / "Windows Kits" / "10" / "tools" / "bin" / "i386" / "imageapp.exe",
                adkRoot / "tools" / "bin" / "i386" / "imageapp.exe",
                adkRoot / "imageapp.exe",
            };
            std::filesystem::path found;
            std::uint64_t size = 0;
            for (const auto& candidate : candidates)
                if (FileExistsSize(candidate, &size)) { found = candidate; break; }
            if (found.empty()) ReportWarn(c, "ADK 根下没找到 tools\\bin\\i386\\imageapp.exe：" + adkRoot.string());
            else ReportPass(c, "ADK 构建工具：" + found.string() + "（" + HumanBytes(size) + "）");
        }

        OutLn("");
        OutLn("结论：" + std::to_string(c.passed) + " 项通过，" +
              std::to_string(c.warned) + " 项提示，" + std::to_string(c.failed) + " 项失败。");
        if (c.failed > 0) {
            Err("自检有失败项：先按上面的 [失败] 修环境，再跑装配。");
            return 2;
        }
        if (c.warned > 0) {
            OutLn("提示项不阻塞只读命令（解析/统计/对拍），但会阻塞需要管理员或缺失输入的步骤。");
            return 1;
        }
        return 0;
    }

    // ── workflow plan ───────────────────────────────────────────────────────
    //
    // 参数一律具名（--manifests 等），因为五步的原语各有一串位置参数，混在一起容易错。
    int CmdWorkflowPlan(const std::vector<std::string>& args)
    {
        std::map<std::string, std::string> v;
        std::set<std::string> flags;
        static const std::set<std::string> valueOpts = {
            "--manifests", "--packages", "--staged", "--arch", "--stack", "--out", "--limit",
            "--installed", "--markers", "--claims", "--flags", "--sessions",
            "--buckets", "--cache", "--ct", "--version-flags", "--mcp", "--skeleton-families",
            "--minimal", "--ccpinterface-name", "--stack-entry", "--pending", "--time",
        };
        static const std::set<std::string> flagOpts = {
            "--drivers", "--no-configuration", "--noncanonical-data", "--transform-settings",
            "--installers-root", "--installers-shortcut",
        };
        for (size_t i = 0; i < args.size(); ++i) {
            const std::string& a = args[i];
            if (a == "--help" || a == "-h") { PrintDoc(*FindDoc(SplitWords("workflow plan"))); return 0; }
            if (valueOpts.count(a)) {
                if (i + 1 >= args.size()) { Err("参数缺少取值：" + a); return 2; }
                v[a] = args[++i];
            } else if (flagOpts.count(a)) {
                flags.insert(a);
            } else {
                Err("workflow plan 不认识这个参数：" + a);
                Err("看用法：ocid_wssm help workflow plan");
                return 2;
            }
        }
        const char* required[] = {"--manifests", "--staged", "--arch", "--stack", "--out"};
        for (const char* name : required) {
            if (!v.count(name)) {
                Err(std::string("缺少必需参数：") + name);
                Err("看用法：ocid_wssm help workflow plan");
                return 2;
            }
        }
        const std::filesystem::path outDir(FromUtf8OrAnsi(v["--out"].c_str()));
        std::error_code ec;
        std::filesystem::create_directories(outDir, ec);
        if (!DirExists(outDir)) {
            Err("无法创建输出目录：" + outDir.string());
            return 2;
        }
        if (!DirExists(std::filesystem::path(FromUtf8OrAnsi(v["--manifests"].c_str())))) {
            Err("清单目录不存在：" + v["--manifests"]);
            return 2;
        }
        const std::string packagesDir = ResolvePackagesDir(v["--packages"], v["--manifests"]);
        if (packagesDir.empty()) {
            Err("找不到含 .mum 的包目录（Windows\\servicing\\Packages）。");
            Err("--packages 可给镜像根或 Packages 目录；当前：" +
                (v.count("--packages") ? v["--packages"] : std::string("（未给）")));
            Err("看用法：ocid_wssm help workflow plan");
            return 2;
        }

        struct Step
        {
            std::string title;
            std::string legacy;
            std::vector<std::string> args;
        };
        std::vector<Step> steps;

        Step components{"生成组件记录（DerivedData\\Components）", "cbs-plan-components",
                        {v["--manifests"], (outDir / "components.txt").string()}};
        components.args.push_back("--staged");
        components.args.push_back(v["--staged"]);
        if (v.count("--limit")) { components.args.push_back("--limit"); components.args.push_back(v["--limit"]); }
        steps.push_back(components);

        Step deployments{"生成部署记录与 catalog 标记", "cbs-plan-deployments",
                         {packagesDir, v["--manifests"], (outDir / "deployments.txt").string()}};
        for (const char* opt : {"--installed", "--markers", "--claims", "--flags", "--sessions"})
            if (v.count(opt)) { deployments.args.push_back(opt); deployments.args.push_back(v[opt]); }
        steps.push_back(deployments);

        Step versioned{"生成版本索引（DerivedData\\VersionedIndex）", "cbs-plan-versioned",
                       {v["--manifests"], packagesDir, v["--staged"], v["--stack"],
                        (outDir / "versioned.txt").string()}};
        for (const char* opt : {"--buckets", "--cache", "--ct", "--version-flags", "--mcp", "--skeleton-families"})
            if (v.count(opt)) { versioned.args.push_back(opt); versioned.args.push_back(v[opt]); }
        steps.push_back(versioned);

        Step root{"生成台账根值", "cbs-plan-root",
                  {v["--arch"], v["--stack"], (outDir / "root.txt").string()}};
        for (const char* opt : {"--pending", "--time", "--ccpinterface-name"})
            if (v.count(opt)) { root.args.push_back(opt); root.args.push_back(v[opt]); }
        if (v.count("--stack-entry")) { root.args.push_back("--stack-entry"); root.args.push_back(v["--stack-entry"]); }
        for (const char* opt : {"--minimal", "--no-configuration", "--noncanonical-data",
                                "--transform-settings", "--installers-root", "--installers-shortcut"})
            if (flags.count(opt)) root.args.push_back(opt);
        steps.push_back(root);

        if (flags.count("--drivers")) {
            Step drivers{"生成 Drivers 台账（现代镜像）", "cbs-plan-drivers",
                         {packagesDir, v["--staged"], (outDir / "drivers.txt").string()}};
            if (v.count("--installed")) { drivers.args.push_back("--installed"); drivers.args.push_back(v["--installed"]); }
            steps.push_back(drivers);
        }

        OutLn("OCID WSSM 工作流：生成全套 CBS 台账计划");
        OutLn("  清单目录：" + v["--manifests"]);
        OutLn("  包目录  ：" + packagesDir +
              (v.count("--packages") && v["--packages"] != packagesDir ? "（由 --packages 推出）" : ""));
        OutLn("  WinSxS  ：" + v["--staged"]);
        OutLn("  架构/栈 ：" + v["--arch"] + " / " + v["--stack"]);
        OutLn("  输出目录：" + outDir.string());
        OutLn("  步骤数  ：" + std::to_string(steps.size()));
        OutLn("");

        const auto started = std::chrono::steady_clock::now();
        int index = 0;
        for (const Step& step : steps) {
            ++index;
            OutLn("── [" + std::to_string(index) + "/" + std::to_string(steps.size()) + "] " +
                  step.title + "（" + step.legacy + "）");
            std::vector<std::string> argv;
            argv.push_back(step.legacy);
            for (const std::string& a : step.args) argv.push_back(a);
            const auto stepStarted = std::chrono::steady_clock::now();
            const int rc = RunLegacy(argv);
            const double seconds = std::chrono::duration<double>(std::chrono::steady_clock::now() - stepStarted).count();
            if (rc != 0) {
                Err("步骤失败：" + step.legacy + "（退出码 " + std::to_string(rc) + "）。已停止，后续步骤未执行。");
                Err("排查：先单独跑这一步（ocid_wssm run " + step.legacy + " ...），");
                Err("      原语帮助见 ocid_manifest help " + step.legacy + "。");
                return rc;
            }
            char buf[64];
            std::snprintf(buf, sizeof(buf), "   完成（%.1f 秒）", seconds);
            OutLn(buf);
            OutLn("");
        }

        const double total = std::chrono::duration<double>(std::chrono::steady_clock::now() - started).count();
        char summary[128];
        std::snprintf(summary, sizeof(summary), "全部完成：%zu 份计划写入 %s（用时 %.1f 秒）",
                      steps.size(), outDir.string().c_str(), total);
        OutLn(summary);
        OutLn("");
        OutLn("下一步（把计划写成 hive，需要管理员）：");
        OutLn("  ocid_wssm store write \"" + (outDir / "components.txt").string() + "\" <hivePath> --transactional");
        OutLn("  多份计划合并写入见 Source\\Native\\assemble-cbs-store.ps1（PowerShell 侧负责拼接与事务）。");
        return 0;
    }

    // ── workflow verify ─────────────────────────────────────────────────────
    int CmdWorkflowVerify(const std::vector<std::string>& args)
    {
        std::vector<std::string> positional;
        std::string hive;
        std::string limit;
        for (size_t i = 0; i < args.size(); ++i) {
            const std::string& a = args[i];
            if (a == "--help" || a == "-h") { PrintDoc(*FindDoc(SplitWords("workflow verify"))); return 0; }
            if (a == "--hive") {
                if (i + 1 >= args.size()) { Err("--hive 缺少取值"); return 2; }
                hive = args[++i];
            } else if (a == "--limit") {
                if (i + 1 >= args.size()) { Err("--limit 缺少取值"); return 2; }
                limit = args[++i];
            } else if (a.rfind("--", 0) == 0) {
                Err("workflow verify 不认识这个参数：" + a);
                return 2;
            } else {
                positional.push_back(a);
            }
        }
        if (positional.size() < 2) {
            Err("用法：ocid_wssm workflow verify <计划目录> <WinSxS 目录> [--hive <hiveSpec>] [--limit N]");
            return 2;
        }
        const std::filesystem::path planDir(FromUtf8OrAnsi(positional[0].c_str()));
        const std::string winSxs = positional[1];
        const std::filesystem::path components = planDir / "components.txt";
        if (!FileExistsSize(components)) {
            Err("计划目录里没有 components.txt：" + components.string());
            Err("先用 ocid_wssm workflow plan 生成，或把 <计划目录> 指对。");
            return 2;
        }

        OutLn("OCID WSSM 工作流：校验计划");
        OutLn("  计划目录：" + planDir.string());
        OutLn("  WinSxS  ：" + winSxs);
        if (!hive.empty()) OutLn("  台账    ：" + hive);
        OutLn("");

        OutLn("── [1/2] 文件落位校验（cbs-verify-files）");
        std::vector<std::string> step1{"cbs-verify-files", components.string(), winSxs};
        if (!limit.empty()) { step1.push_back("--limit"); step1.push_back(limit); }
        int rc = RunLegacy(step1);
        if (rc != 0) { Err("文件校验未通过（退出码 " + std::to_string(rc) + "）。"); return rc; }
        OutLn("   完成");
        OutLn("");

        if (!hive.empty()) {
            OutLn("── [2/2] 台账自洽校验（cbs-verify-store）");
            std::vector<std::string> step2{"cbs-verify-store", hive};
            rc = RunLegacy(step2);
            if (rc != 0) { Err("台账自检未通过（退出码 " + std::to_string(rc) + "）。"); return rc; }
            OutLn("   完成");
            OutLn("");
        }
        OutLn("校验通过。");
        return 0;
    }

    // ── workflow pcm ────────────────────────────────────────────────────────
    int CmdWorkflowPcm(const std::vector<std::string>& args)
    {
        std::string manifests;
        std::string out;
        std::string b0 = "0";
        std::string limit;
        std::string diffReal;
        for (size_t i = 0; i < args.size(); ++i) {
            const std::string& a = args[i];
            auto take = [&](std::string& target) {
                if (i + 1 < args.size()) target = args[++i];
                else Err("参数缺少取值：" + a);
            };
            if (a == "--help" || a == "-h") { PrintDoc(*FindDoc(SplitWords("workflow pcm"))); return 0; }
            else if (a == "--manifests") take(manifests);
            else if (a == "--out") take(out);
            else if (a == "--b0") take(b0);
            else if (a == "--limit") take(limit);
            else if (a == "--diff") take(diffReal);
            else {
                Err("workflow pcm 不认识这个参数：" + a);
                Err("用法：ocid_wssm workflow pcm --manifests <清单目录> --out <输出目录> [--b0 N] [--limit N] [--diff <真机 PCM 目录>]");
                return 2;
            }
        }
        if (manifests.empty() || out.empty()) {
            Err("用法：ocid_wssm workflow pcm --manifests <清单目录> --out <输出目录> [--b0 N] [--limit N] [--diff <真机 PCM 目录>]");
            return 2;
        }
        const std::filesystem::path manDir(FromUtf8OrAnsi(manifests.c_str()));
        const std::filesystem::path outDir(FromUtf8OrAnsi(out.c_str()));
        std::error_code ec;
        if (!DirExists(manDir)) { Err("清单目录不存在：" + manDir.string()); return 2; }
        std::filesystem::create_directories(outDir, ec);
        if (!DirExists(outDir)) { Err("无法创建输出目录：" + outDir.string()); return 2; }

        std::vector<std::filesystem::path> files;
        for (std::filesystem::directory_iterator it(manDir, ec), end; it != end; it.increment(ec)) {
            if (ec) break;
            const std::string name = it->path().filename().string();
            if (name.size() > 9 && name.compare(name.size() - 9, 9, ".manifest") == 0)
                files.push_back(it->path());
        }
        std::sort(files.begin(), files.end());
        if (!limit.empty()) {
            const size_t n = static_cast<size_t>(std::strtoull(limit.c_str(), nullptr, 10));
            if (n > 0 && files.size() > n) files.resize(n);
        }
        if (files.empty()) {
            Err("清单目录里没有 .manifest：" + manDir.string());
            return 2;
        }

        OutLn("OCID WSSM 工作流：批量构建 ManifestCache 块");
        OutLn("  清单目录：" + manDir.string() + "（" + std::to_string(files.size()) + " 份）");
        OutLn("  输出目录：" + outDir.string());
        OutLn("  b0 附加位：" + b0);
        OutLn("");

        int ok = 0;
        int failed = 0;
        std::vector<std::string> failedNames;
        const size_t total = files.size();
        for (size_t i = 0; i < total; ++i) {
            const std::filesystem::path& man = files[i];
            std::filesystem::path target = outDir / (man.filename().string() + ".pcm");
            std::vector<std::string> argv{"cbs-pcm-build", man.string(), target.string(), b0};
            const int rc = RunLegacy(argv);
            if (rc == 0) ++ok;
            else {
                ++failed;
                if (failedNames.size() < 20) failedNames.push_back(man.filename().string());
            }
            if ((i + 1) % 100 == 0 || i + 1 == total) {
                OutLn("  " + std::to_string(i + 1) + "/" + std::to_string(total) +
                      "  成功 " + std::to_string(ok) + "  失败 " + std::to_string(failed));
            }
        }

        OutLn("");
        OutLn("完成：成功 " + std::to_string(ok) + "，失败 " + std::to_string(failed) + "。");
        if (!failedNames.empty()) {
            OutLn("失败清单（最多列 20 条）：");
            for (const std::string& name : failedNames) OutLn("  " + name);
        }
        if (!diffReal.empty()) {
            OutLn("");
            OutLn("── 对拍统计（cbs-pcm-diff）");
            std::vector<std::string> argv{"cbs-pcm-diff", diffReal, outDir.string()};
            if (!limit.empty()) { argv.push_back("--limit"); argv.push_back(limit); }
            const int rc = RunLegacy(argv);
            if (rc != 0) Err("对拍统计有差异（退出码 " + std::to_string(rc) + "）。");
        }
        return failed == 0 ? 0 : 1;
    }

    // ── 旧命令名（兼容透传用）──────────────────────────────────────────────
    const char* const kLegacyCommands[] = {
        "info", "ops", "scan", "dump-xsd", "export-ops", "place-ops", "csi-place", "verify-names",
        "expand", "macros", "dmi-ops", "dmi-ledger", "dmi-place",
        "cbs-scan", "cbs-keys", "cbs-families", "cbs-dump", "cbs-diff",
        "cbs-verify-store", "cbs-verify-files",
        "cbs-plan-components", "cbs-plan-deployments", "cbs-plan-versioned", "cbs-plan-root",
        "cbs-plan-drivers", "cbs-write",
        "cbs-dcm-decode", "dcm-dump-dictionary",
        "cbs-pcm-tables", "cbs-pcm-reencode", "cbs-pcm-build", "cbs-pcm-diff", "cbs-microdom",
        "cbs-hash",
    };

    bool IsLegacyCommand(const std::string& name)
    {
        for (const char* command : kLegacyCommands)
            if (name == command) return true;
        return false;
    }

    // 从全局参数里摘出 --lang 与本程序要保存的全局开关；其余原样保留。
    //
    // 返回值：true = 参数可用；false = --lang 取值不支持（错误已打印）。
    bool StripOptions(std::vector<std::string>& args)
    {
        std::vector<std::string> kept;
        kept.reserve(args.size());
        for (size_t i = 0; i < args.size(); ++i) {
            const std::string& a = args[i];
            if (a == "--lang" || a.rfind("--lang=", 0) == 0) {
                std::string value;
                if (a == "--lang") {
                    if (i + 1 < args.size()) value = args[++i];
                } else {
                    value = a.substr(7);
                }
                if (value != "zh-CN" && value != "zh-cn" && value != "zh") {
                    Err("不支持的语言：" + value + "（当前只内置 zh-CN）");
                    return false;
                }
                continue;
            }
            if (a == "--dcm-dictionary" || a == "--file-flags" || a == "--os-build") {
                if (i + 1 < args.size()) {
                    g_globals.push_back(a);
                    g_globals.push_back(args[++i]);
                } else {
                    Err("全局开关缺少取值：" + a);
                    return false;
                }
                continue;
            }
            kept.push_back(a);
        }
        args.swap(kept);
        return true;
    }

}  // namespace

// The workbench exposes Chinese group names and Chinese option values on the
// command line, so it must read the command line as UTF-16 and convert it to
// UTF-8 itself. A plain main(char**) would hand us the ANSI code page bytes
// (GBK here) and every Chinese argument would silently stop matching.
static std::string Utf16ToUtf8(const wchar_t* text)
{
#ifdef _WIN32
    if (text == nullptr) return std::string();
    const int need = ::WideCharToMultiByte(CP_UTF8, 0, text, -1, nullptr, 0, nullptr, nullptr);
    if (need <= 1) return std::string();
    std::string out(static_cast<size_t>(need - 1), '\0');
    ::WideCharToMultiByte(CP_UTF8, 0, text, -1, &out[0], need, nullptr, nullptr);
    return out;
#else
    (void)text;
    return std::string();
#endif
}

int wmain(int argc, wchar_t** argv)
{
    SetupConsole();

    std::vector<std::string> args;
    args.reserve(argc > 1 ? static_cast<size_t>(argc - 1) : 0);
    for (int i = 1; i < argc; ++i) args.push_back(Utf16ToUtf8(argv[i]));

    if (!StripOptions(args)) return 2;

    if (args.empty()) {
        PrintUsage();
        return 0;
    }

    const std::string first = args[0];

    // help / 版本
    if (first == "help" || first == "--help" || first == "-h" || first == "-?" || first == "/?") {
        std::vector<std::string> rest(args.begin() + 1, args.end());
        if (rest.empty()) { PrintUsage(); return 0; }
        if (rest[0] == "--all" || rest[0] == "all") { PrintAllDocs(); return 0; }
        const std::string group = ResolveGroup(rest[0]);
        if (!group.empty()) {
            if (rest.size() >= 2) {
                // 三种写法都接受：
                //   help 缓存 pcm diff   （组名 + 完整命令）
                //   help 缓存 pcm        （组名 + 命令前缀，列出匹配的命令）
                //   help pcm diff        （不带组名）
                if (const Doc* doc = FindDoc(rest)) {
                    if (group == doc->group) { PrintDoc(*doc); return 0; }
                }
                std::vector<std::string> sub(rest.begin() + 1, rest.end());
                if (const Doc* doc = FindDoc(sub)) {
                    if (group == doc->group) { PrintDoc(*doc); return 0; }
                }
                if (PrintDocsByPrefix(sub)) return 0;
                Err("组「" + group + "」里没有命令「" + JoinWords(sub) + "」。");
                Err("看该组全部命令：ocid_wssm help " + rest[0]);
                return 2;
            }
            PrintGroup(group);
            return 0;
        }
        // 也接受直接给命令路径（"store verify"），不要求先是组名。
        size_t matched = 0;
        const Doc* doc = FindDoc(rest, &matched);
        if (doc != nullptr && matched == rest.size()) { PrintDoc(*doc); return 0; }
        if (doc != nullptr) { PrintDoc(*doc); return 0; }
        Err("没有这个命令组或命令：" + rest[0]);
        Err("运行 ocid_wssm help 看全部命令。");
        return 2;
    }

    if (first == "version" || first == "info" || first == "--version") {
        PrintVersion();
        return 0;
    }
    if (first == "doctor") {
        return CmdDoctor(std::vector<std::string>(args.begin() + 1, args.end()));
    }
    if (first == "run") {
        if (args.size() < 2) {
            Err("用法：ocid_wssm run <旧命令> [参数...]");
            Err("旧命令帮助：ocid_manifest help");
            return 2;
        }
        std::vector<std::string> fwd(args.begin() + 1, args.end());
        return RunLegacy(fwd);
    }
    if (first == "workflow") {
        if (args.size() < 2) {
            Err("用法：ocid_wssm workflow <plan|verify|pcm> [参数...]");
            Err("看帮助：ocid_wssm help workflow");
            return 2;
        }
        const std::string second = args[1];
        std::vector<std::string> rest(args.begin() + 2, args.end());
        if (second == "plan") return CmdWorkflowPlan(rest);
        if (second == "verify") return CmdWorkflowVerify(rest);
        if (second == "pcm") return CmdWorkflowPcm(rest);
        Err("workflow 没有这个子命令：" + second + "（可用：plan / verify / pcm）");
        return 2;
    }

    // 普通命令：按文档树做最长前缀匹配，剩余参数原样转发。
    size_t matched = 0;
    const Doc* doc = FindDoc(args, &matched);
    if (doc != nullptr && doc->legacy != nullptr) {
        std::vector<std::string> fwd;
        fwd.push_back(doc->legacy);
        for (size_t i = matched; i < args.size(); ++i) fwd.push_back(args[i]);
        return RunLegacy(fwd);
    }
    if (doc != nullptr && doc->legacy == nullptr) {
        // 文档树里属于本程序但从上面漏掉的（目前只有 help/version/doctor/workflow/run）；
        // 走到这里说明分派有缺口，直接打印该命令帮助而不是瞎跑。
        PrintDoc(*doc);
        return 2;
    }

    // 只给了组名（例如 `ocid_wssm store`）：列组内命令，而不是报"未知命令"。
    if (!ResolveGroup(first).empty()) {
        PrintGroup(ResolveGroup(first));
        return 0;
    }

    // 兼容：第一段就是旧命令名时整条透传（老脚本改名即可用）。
    if (IsLegacyCommand(first)) {
        return RunLegacy(args);
    }

    Err("未知命令：" + first);
    Err("");
    Err("提示：ocid_wssm help 看全部命令；ocid_wssm help <组> <命令> 看单条细节；");
    Err("      老脚本可直接用旧命令名（如 ocid_wssm cbs-pcm-build …）。");
    return 2;
}
