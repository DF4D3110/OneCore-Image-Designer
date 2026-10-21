# ocid_wssm —— Windows Servicing Stack Manager

`ocid_wssm.exe` 是本仓库原生化栈的**统一入口**：把原先散在 `ocid_manifest` 里的 30 多个原语，按"用户想做什么"重新编组成一条命令树，并在最上面加了一层**多步工作流**和**环境自检**。

它和 `ocid_manifest.exe` 的关系不是"新写一套"：

```
ocid_wssm.exe  ──┐
                 ├── 同一份实现：OcidServicing/tools/ocid_manifest.cpp
ocid_manifest.exe┘        导出为 int OcidManifestRun(int argc, char** argv)
```

- `ocid_manifest`：**原语**。一条命令只做一件事，参数直接映射到 C++ 接口，适合脚本调用。
- `ocid_wssm`：**工作台**。参数命名面向"这件事叫什么"，并负责把多步串起来、失败即停、每一步带中文解释。
- 两者的输出约定一致：**数据走 stdout，诊断走 stderr**，C# 侧（`Services/OcidServicingNative.cs`）可以照旧抓 stdout。
- `ocid_wssm` 的转发不是"再起一个进程"，而是直接调用 `OcidManifestRun`，因此永远不会出现"改了原语、忘了改工作台"的漂移。

---

## 1. 快速开始

```bat
ocid_wssm help                         :: 列出全部组与命令（默认中文）
ocid_wssm help workflow plan           :: 单条命令的完整说明：用途 / 用法 / 选项 / 示例
ocid_wssm version                      :: 版本 + 构建时间
ocid_wssm doctor                       :: 环境自检（管理员、wcp、DCM 字典、ADK、磁盘）
ocid_wssm run cbs-verify-store <hive>  :: 透传任意原语（参数原样）
```

> 当前只有 zh-CN 一种语言。`--lang zh-CN` 可写可不写；写别的值会明确报错而不是静默回退，
> 免得以后加 en-US 时，脚本以为自己拿到了英文输出。

---

## 2. 命令树

### 2.1 组（`ocid_wssm help <组>` 看组内命令）

| 组 | 别名 | 管什么 |
| --- | --- | --- |
| 环境 | `env` | 版本、自检、帮助 |
| 工作流 | `workflow` | 多步组合：全套计划 / 校验 / PCM 回归 |
| 清单 | `manifest` | 单份 .manifest 的解析、比较、落位 |
| 落位 | `place` | 计划落位（place-ops）与按计划摆文件（csi-place） |
| 驱动 | `driver` | 驱动包（DMI）台账与落位 |
| 台账 | `store` | COMPONENTS hive 读、校验、对拍；写入 |
| 生成 | `plan` | 生成 CBS 计划文本（components / deployments / versioned / root / drivers） |
| 装包 | `pkg` / `cbsx` / `install` | 离线装包：枚举 / 暂存 / 安装 / 卸载 / 批处理 / 收尾 / 后置（**跨模块调度 CbsxCli**） |
| 缓存 | `pcm` | ManifestCache（PCM/CDF）：列表、重编码、生成、对拍、微 DOM |
| DCM | `dcm` | DCM 清单解压与字典转储 |
| 工具 | `tool` | 哈希等零碎工具、`run` 透传 |

### 2.2 命令 → 原语映射

| 命令 | 原语 | 作用 |
| --- | --- | --- |
| `version` | — | 打印 `ocid_wssm` 版本与构建时间 |
| `doctor` | — | 环境自检（只读，不改任何东西） |
| `help [组] [命令]` | — | 帮助；不带参数 = 总览 |
| `workflow plan` | `cbs-plan-components` → `-deployments` → `-versioned` → `-root`（+`-drivers`） | 一次生成全套 CBS 台账计划 |
| `workflow verify` | `cbs-verify-files`（+`cbs-verify-store`） | 校验计划与 WinSxS/hive 是否吻合 |
| `workflow pcm` | `cbs-pcm-build`（+`cbs-pcm-diff`） | 批量生成 PCM，可选与真机逐字节对拍 |
| `workflow install` | **跨模块** `CbsxCli.exe` | 装包 + 收尾一次做完（见 §5） |
| `manifest info` | `info` | 清单概览 |
| `manifest ops` | `ops` | 把清单展开成注册表操作序列 |
| `manifest scan` | `scan` | 快速扫描（身份 + 摘要） |
| `manifest xsd` | `dump-xsd` | 转储内嵌 XSD |
| `manifest export` | `export-ops` | 导出操作到文件 |
| `manifest names` | `verify-names` | 校验清单里引用的名称 |
| `manifest expand` | `expand` | 宏展开 |
| `manifest macros` | `macros` | 列出已知宏 |
| `place plan` | `place-ops` | JSONL 落位计划 |
| `place csi` | `csi-place` | 按计划摆文件到目标目录 |
| `driver ops` | `dmi-ops` | 驱动包注册表操作 |
| `driver ledger` | `dmi-ledger` | 驱动台账（DriverDatabase） |
| `driver place` | `dmi-place` | 驱动文件落位 |
| `store scan` | `cbs-scan` | 读 COMPONENTS 台账 |
| `store keys` | `cbs-keys` | 列出 key |
| `store families` | `cbs-families` | 列组件族 |
| `store dump` | `cbs-dump` | 转储详情 |
| `store diff` | `cbs-diff` | 两个 hive 对拍 |
| `store verify` | `cbs-verify-store` | 台账自洽校验 |
| `store verify-files` | `cbs-verify-files` | 计划 ↔ WinSxS 文件校验 |
| `store write` | `cbs-write` | 把计划写进 hive（事务化） |
| `plan components` | `cbs-plan-components` | `DerivedData\Components` |
| `plan deployments` | `cbs-plan-deployments` | catalog 相关 |
| `plan versioned` | `cbs-plan-versioned` | `DerivedData\VersionedIndex` |
| `plan root` | `cbs-plan-root` | 台账根值 |
| `plan drivers` | `cbs-plan-drivers` | Drivers 台账 |
| `pkg list`（装包 列表） | `CbsxCli /ep` | 枚举镜像里已装的包 |
| `pkg stage`（装包 暂存） | `CbsxCli /sp` | 暂存包（Staged）：载荷落盘但不激活 |
| `pkg install`（装包 安装） | `CbsxCli /ip` | 安装包（Installed） |
| `pkg remove`（装包 卸载） | `CbsxCli /up` | 移除包（Absent） |
| `pkg batch`（装包 批处理） | `CbsxCli /b` | 按批处理文件混做多步 |
| `pkg finish`（装包 收尾） | `CbsxCli /finish` | generalize / reseal 到 OOBE + ImageState |
| `pkg postinstall`（装包 后置） | `CbsxCli /postinstall` | 应用 unattend / 清理 / edition 收尾 |
| `pcm tables` | `cbs-pcm-tables` | 列出 PCM 的表目录与槽 |
| `pcm reencode` | `cbs-pcm-reencode` | 把真机 PCM 重新编码（验证编码器） |
| `pcm build` | `cbs-pcm-build` | 清单 → PCM |
| `pcm diff` | `cbs-pcm-diff` | 两侧 PCM 目录逐字节对拍 |
| `pcm microdom` | `cbs-microdom` | 解出微 DOM（MdHd） |
| `dcm decode` | `cbs-dcm-decode` | DCM 压缩清单 → XML |
| `dcm dictionary` | `dcm-dump-dictionary` | 转储 DCM 字典 |
| `tool hash` | `cbs-hash` | 文件/缓冲区哈希 |
| `run <原语> ...` | 任意 | 原样透传，全局开关自动附加 |

---

## 3. 四个工作流

### 3.1 `workflow plan` —— 一次生成全套 CBS 计划

必填：

| 参数 | 含义 |
| --- | --- |
| `--manifests <目录>` | 清单目录（`Windows\WinSxS\Manifests`） |
| `--packages <目录>` | 含 `.mum` 的包目录；也可以直接给镜像根（自动补 `Windows\servicing\Packages`），不给则从 `--manifests` 反推 |
| `--staged <目录>` | WinSxS 实例目录（`Windows\WinSxS`） |
| `--arch <架构>` | `amd64` / `x86` / `arm64` |
| `--stack <版本>` | 服务栈版本（`6.3.9600.16384` 这样的四段式） |
| `--out <目录>` | 计划输出目录（不存在会自动建） |

可选：`--limit N`、`--installed`、`--markers`、`--claims`、`--flags`、`--sessions`、
`--buckets`、`--cache`、`--ct`、`--version-flags`、`--mcp`、`--skeleton-families`、
`--pending`、`--time`、`--ccpinterface-name`、`--stack-entry`、
`--minimal`、`--no-configuration`、`--noncanonical-data`、`--transform-settings`、
`--installers-root`、`--installers-shortcut`、`--drivers`。

产物：`components.txt`、`deployments.txt`、`versioned.txt`、`root.txt`（加 `--drivers` 还有 `drivers.txt`）。
每一步失败都会停下并打印"哪一步、退出码、怎么单独重跑"，不会带着半套计划继续。

注意：
- `--packages` 可以给镜像根；`9600` 这类 DCM 压缩清单要带全局开关
  `--dcm-dictionary <9600_dict.bin>`，否则部署/版本索引里大量清单都会匹配不上（日志里 `badManifest` 会很大）。
- 不给 `--installed/--markers/--claims/--flags/--sessions/--buckets/--cache/--ct/--version-flags/--mcp`
  时，部署记录与版本索引只会出**骨架**（只有 `K` 行、没有 `D`/`V` 记录）——这是原语的设计，
  真实值来自挂载镜像上的安装历史。先跑出骨架、再用 PowerShell 装配脚本补实值即可。

### 3.2 `workflow verify` —— 计划 ↔ 镜像

```bat
ocid_wssm workflow verify <计划目录> <WinSxS 目录> [--hive <hiveSpec>] [--limit N]
```

1. `cbs-verify-files`：计划里的每个文件在 WinSxS 里是否找得到、哈希是否一致；
2. `--hive` 给了就再跑 `cbs-verify-store`：hive 台账自身是否自洽。

### 3.3 `workflow pcm` —— 批量生成 + 可选对拍

```bat
ocid_wssm workflow pcm --manifests <清单目录> --out <输出目录> [--b0 N] [--limit N] [--diff <真机 PCM 目录>]
```

- 默认 `--b0 0`；`b0` 是文档根记录里那几个**真机未初始化栈字节**，不可推导，需要逐样本已知时用；
- `--diff` 给了就调 `cbs-pcm-diff` 做"真机 vs 我产"的逐槽对拍。

### 3.4 `workflow install` —— 装包 + 收尾

```bat
ocid_wssm workflow install <mum> [<mum> ...] --windows <WinDir> [--stage-only] [--skip-finish]
                         [--postinstall] [--dry] [--log <目录>] [--no-ti]
```

1. `安装`：一次调 `CbsxCli /ip`（多个 `.mum` 且路径不含空格时走批处理，否则逐个调）；
2. `收尾`：独立进程调 `CbsxCli /finish`（`--skip-finish` 跳过，`--dry` 自动只演练不写）；
3. `--postinstall` 追加后置阶段（`/postinstall`）；`--stage-only` 只暂存（`/sp`）。

`--log <目录>` 会按步骤落 `cbsx-install.log` / `cbsx-stage.log` / `cbsx-finish.log`。
任何一步失败立即停，并打印"哪一步、退出码、怎么单独重跑"。

---

## 4. 全局开关与约定

| 开关 | 作用 |
| --- | --- |
| `--dcm-dictionary <wcp.dll\|.bin>` | DCM 压缩清单的字典（16299+ 必填） |
| `--file-flags <csv>` | 清单 `f!` 状态位（`<文件名>|<f! 值>|<值>`） |
| `--os-build <构建号>` | 台账/记录的版本档位（如 `9600`、`16299`），默认从镜像 `ntoskrnl.exe` 探测 |
| `--lang zh-CN` | 输出语言（当前唯一支持；写别的值会报错） |
| `--cbsx <CbsxCli.exe>` | 装包桥（跨模块）的可执行文件；也可以用环境变量 `OCID_CBSXCLI` |

- 这些开关写在**哪个位置都行**，`ocid_wssm` 会把它们摘出来、附加到每一次原语调用上。
- 退出码：`0` 全部成功；`1` 某一步执行失败；`2` 用法/参数/前置条件不满足。
- `doctor` 只读：检查管理员、`wcp.dll`、DCM 字典、ADK 工具链、磁盘余量，并给出"缺什么、怎么补"。

---

## 5. 跨模块：装包（`pkg`）为什么是调度，不是重写

离线装包 / 收尾 / 后置这一整块**不在原生 C++ 栈里**，它在 C# 侧：
`Services\CbsxService.cs`（CBS COM 客户端）、`Services\FinishImageService.cs`（ImageState 收尾）、
`Services\PostInstallService.cs`（后置）、`Services\SkuAssemblyService.cs`（批处理解析），
由 `MobilePackageGen\CbsxCli` 打包成 `CbsxCli.exe`（自包含 win-x86）。

`ocid_wssm` 不重写这一套（重写 = 两份实现、两处 bug、两种行为），而是**进程外调度**：

1. **找桥**：`--cbsx` → 环境变量 `OCID_CBSXCLI` → 程序同级 `CbsxCli.exe` / `CBS\` / `x86\` / `tools\` →
   源码树上溯 8 层找 `MobilePackageGen\CbsxCli\bin\{Release,Debug}\net8.0\win-x86\CbsxCli.exe`
   与 `bin\{Release,Debug}\net8.0-windows\CBS\CbsxCli.exe`。找不到就一次把四种给法全列出来。
2. **参数改写**：`pkg install a.mum` → `CbsxCli /ip /m:a.mum /o:<WinDir>`；多个包走 `/b:<临时批处理>`，
   含空格的路径**逐个调**（批处理按空格分词，写不进去）。
3. **退出码**：`CbsxCli` 的写操作会经 NSudo 提权成 TrustedInstaller **子进程**，那条路径下父进程恒返回 0。
   所以 `ocid_wssm` 每次调用都带 `/exitcode:<临时文件>`，由真正干活的进程把退出码写进文件，我们以文件为准。
4. **输出**：子进程直接继承本进程的 stdout/stderr，CbsxCli 的中文输出原样透出；
   调度前会 `fflush(stdout)`，所以"调度提示 → 子进程输出 → 结论"的顺序是稳的。

> 原有两个控制台都保留：`ocid_manifest.exe`（原语）与 `CbsxCli.exe`（装包）。
> `ocid_wssm` 是第三个入口，改的是"怎么喊"，不是"谁来干"。

**前置条件（CbsxCli 自带的安全门禁）**：`--windows` 指向的镜像必须位于**挂载的 VHD/VHDX**
（物理磁盘 `BusType` 为 14 Virtual / 15 FileBackedVirtual 才放行）。在普通数据盘上跑 `pkg *` 会被拒绝，
这是 CbsxCli 原有行为，`ocid_wssm` 没有放宽它。

---

## 6. 扩展方式

**加一条命令（3 步）**

1. `tools/ocid_wssm.cpp` 的 `kDocs[]` 里加一条
   `{ "组 命令", "组", "摘要", "用法", "选项…", "示例", "legacy-原语", "同义写法" }`
   （`legacy` 写 `nullptr` 表示本程序自己实现；第 8 项可省略）；
2. 如果是新原语，先在 `tools/ocid_manifest.cpp` 里实现 `CmdXxx` 并在分派表登记；
3. 编译：`Source\Native\build-servicing.ps1`。

**加一步工作流**

在 `CmdWorkflowPlan` / `CmdWorkflowVerify` / `CmdWorkflowPcm` 里往 `steps` 追加一个 `Step{标题, 原语名, 参数}` 即可，
失败处理、耗时统计、中文提示都是现成的（`CmdWorkflowInstall` 是手写版，因为它要跨进程调 `CbsxCli`）。

---

## 7. 与 PowerShell 脚本的分工

| 脚本 | 负责 |
| --- | --- |
| `build-servicing.ps1` | 编译原生栈（DLL + 单测 + `ocid_manifest.exe` + `ocid_wssm.exe`） |
| `Extract-CbsInputs.ps1` | 从镜像抽输入 |
| `assemble-cbs-store.ps1` | 把多份计划合并写进 hive（PowerShell 负责拼装/事务） |
| `ocid-assemble.ps1` | 端到端：抽输入 → assemble → 覆盖表 → 再 assemble → 报 diff |
| `Build-OcidImage.ps1` / `Stage-OcidImage.ps1` | 建镜像 / 换入 COMPONENTS 并回归 |

`ocid_wssm` 负责的是**中间那一层**：生成计划、校验计划、对拍 PCM。
脚本负责"外围编排"和"要提权的宿主动作"。

---

## 8. 已知未处理

- 16299 的 PCM 字符串槽序（`t2`）规则未解，见 [UNRESOLVED-16299-pcm.md](UNRESOLVED-16299-pcm.md)。
  这不影响 `ocid_wssm` 的其它命令，`workflow pcm` 在 16299 上只会有 T1 命中。
- `pkg *` 与 `workflow install` 需要镜像位于挂载的 VHD/VHDX（CbsxCli 的 BusType 门禁），
  在普通物理盘上会被拒绝；这是 CbsxCli 的既有安全策略，本程序不放宽。
- 提权到 TrustedInstaller 需要 `NSudoAPI.dll` 在 `CbsxCli.exe` 旁边（或 `Win32\` / `x64\` 子目录）；
  开发目录里没有，`doctor` 会给 `[提示]`。缺它时装包会按当前身份跑（多半失败）。
