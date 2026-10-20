# OneCore Image Designer

> English version: [README.en-US.md](README.en-US.md)

![OneCore Image Designer](Assets/banner.png)

OneCore Image Designer（OCID）是一个独立的 Windows 工作台，用于准备与服务 Windows Core OS /
OneCore / IoT Core 镜像。它覆盖：基于工程的 OEMInput 编写、设备布局编写、镜像构建、从镜像反推系统包集、
FFU 浏览、微软 UUP 查询与下载、addonkit 前端，以及一整套离线注册表、驱动、固件与组件存储工具。

本项目**不是**微软产品，与微软没有从属关系。它不重分发自己所替换的 Windows 服务栈
（`CbsCore.dll`、`wcp.dll`、CMI/CSI/DMI、KTM），也不重分发任何微软包样例。只有少量工具二进制作为
**运行期输入**保留：用于解压 DCM 压缩清单的 `Tools/wcpex` 一对文件，以及内置 `addonkit/` 中随附的
工具——完整清单见[第三方代码与许可](#第三方代码与许可)。需要微软工具链的流程，其余一律使用机器上
已安装的 ADK、DISM 或 Windows 10 IoT Core Kits；本项目所需要的那部分服务栈，正在被重写为项目自有的
C++（见[自带服务栈](#自带服务栈进行中)）。

应用界面为中文。

## 运行要求

- Windows 10 / 11，x64。
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) —— 用于构建 WPF 主程序、
  `UupBridge` 辅助进程和离线测试工具。
- 视所用流程而定，另需：
  - 管理员权限：任何挂载、服务或写入镜像的操作都需要。
  - Windows ADK（Deployment Tools）：镜像构建与包生成流程需要；可在应用内选择使用哪个 ADK 版本。
  - Windows 10 IoT Core Kits 1809（10.0.17763.x）：内置 addonkit shell 需要，见上游 addonkit README。
  - 一个镜像来源：FFU、VHD/VHDX、WIM，或已挂载的目录。
  - Visual Studio 2022 与 C++ 工作负载：构建原生服务栈需要。
- 可选的「UUP 转 ISO」流程需要单独构建的 `UUPMediaConverter.exe`**以及**.NET 10 运行时；
  两者都不随本仓库分发（见[第三方代码与许可](#第三方代码与许可)）。

UUP 的查询与下载只会在用户主动发起时进行；启动时不查询、不下载任何东西。

## 构建与运行

```powershell
git clone https://github.com/DF4D3110/OneCore-Image-Designer.git
cd OneCore-Image-Designer
```

在仓库根目录构建并启动主程序。这一步同时会构建仓库本地的 `UupBridge`，并把它的运行期文件复制进主程序
输出目录。

```powershell
dotnet build ".\OneCore Image Designer.csproj" --configuration Release
bin\Release\net8.0-windows\OneCore Image Designer.exe
```

原生服务栈是独立的，需要 Visual Studio 的 C++ 构建环境。它刻意交叉构建两个架构：主程序是 x64，而它所
替换的服务栈二进制往往是 x86。

```powershell
.\Source\Native\build-servicing.ps1
```

该脚本输出到 `Source/Native/Output/x86/` 与 `Source/Native/Output/x64/`：`OcidServicing` 库、
`ocid_manifest.exe` 控制台工具，以及原生单元测试。这些都是构建产物，**不**入库：克隆后需先跑一次该
脚本，才能使用服务相关的页面。`Services/OcidServicingNative.cs` 会相对主程序输出目录定位该库，无需额外
拷贝。

组装独立的 CBS 控制台包（CLI 加其提权辅助）：

```powershell
.\Build-CbsBundle.ps1          # 加 -NoBuild 可复用已有的 CLI 构建
# -> bin\Release\net8.0-windows\CBS\
```

运行离线回归测试（不联网、不下载 UUP）：

```powershell
dotnet run --project ".\Tests\OneCore.Image.Designer.OfflineTests.csproj" --configuration Release
```

主程序构建还会把内置的 `addonkit/` 目录树复制到输出目录。这一复制是增量的：应用内下载器已经放到
`addonkit\adk\` 下的内容会原样保留。

## 应用概览

| 区域 | 视图 | 功能 |
| --- | --- | --- |
| 起始页 | `Views/StartPage.xaml` | 入口；每个流程一个磁贴，另有最近使用产物快捷入口。 |
| OEMInput | `Views/OEMInput/` | 发现 FM 与 DeviceLayout 输入，编辑 V1/V2 OEMInput 文档，用内置 schema 校验，并保存为 `.cwproj` 工程。 |
| 设备布局 | `Views/DeviceLayout/` | 查看与编写设备布局，生成配套 BSP 组件，并从目录中的 INF 构建 PkgGen 驱动包。 |
| 镜像构建 | `Views/ImageBuild/` | 驱动所选 ADK 版本的 `imageapp.exe` / `imggen.cmd`，自动发现与目标包版本号匹配的工具集。 |
| 系统重建 | `Views/SystemReconstruct/` | 从镜像重建系统包集，支持逐节点 XML 预览。 |
| CBS | `Views/Cbs/` | 对已挂载镜像做 CBS 服务。 |
| FFU 浏览 | `Views/FFUBrowser/` | 浏览 FFU 分区，带十六进制查看器。 |
| updateapp | `Views/UpdateAppView.xaml` | 派发各版本专属的 `updateapp.exe` 命令：按该版本的官方用法文本逐版本渲染，每个命令带参数模板、分类与风险等级。 |
| UUP | `Views/UupView.xaml` | 按 SKU/架构/通道/分支查询微软 UUP，挑选更新，保存与重载快照，下载，并可选地经外部转换器转成 ISO。 |
| SxS | `Views/SxsView.xaml` | 对选定镜像与输出目录做**不执行**的预检/预览：不跑脚本，不产生输出。 |
| addonkit | `Views/AddonKitView.xaml` | 微软 `iot-adk-addonkit` 前端：模块/ADK 状态、示例工作区发现、预检门禁，以及提权的一键构建命令并实时回传日志。 |
| 工具 | `Views/Tools/` | 离线注册表编辑器、驱动提取器、固件解析器、WinSxS 哈希计算器、依赖查看器、组件存储校验器，以及 ADK 工具下载器。 |

贯穿全局的行为：

- **长任务绝不阻塞 UI。** 任务输出经工作线程队列汇入统一日志窗口
  （`Views/TaskLogWindow.xaml`），按批定时排空，而不是每行一次 dispatcher 调用；日志同时落盘到临时
  文件，并提供复制/打开控件。
- **提权是显式的。** 需要提权的操作经内置的 NSudo 辅助启动（`Services/ElevationHelper.cs`），而不是
  要求整个应用以管理员身份运行。
- **破坏性命令需要确认**，并在界面上按风险等级标注。

## 代码组织

- `OneCore Image Designer.csproj` —— WPF 主程序（`net8.0-windows`，x64）：MVVM，`ViewModels/` +
  `Views/`，领域模型在 `Models/`，流程逻辑在 `Services/`。
- `Services/` —— 约 75 个单一职责服务（镜像构建、CBS 与 SxS 服务、驱动、固件、组件存储、离线注册表、
  UUP、addonkit、校验、日志）。绝大多数可见行为都在这里，视图层保持薄。
- `UupShared/` —— 离线 UUP 查询解析与负载契约，由主程序与桥接进程共用。
- `UupBridge/` —— 独立的 `net8.0` 适配可执行文件，包在内置 UUP 核心之上；主程序构建它并把结果复制进
  输出目录，而不是直接引用。
- `MobilePackageGen/` —— 包生成工具链（CLI、WinForms GUI、FFU/VHDX/WIM/裸文件系统/存储池适配器、
  CAB 与 DCM 压缩、cat/签名重建，以及 `CbsxCli`、`SxSFounderCli` 两个命令行工具）。它有自己的
  README：`MobilePackageGen/readme.md`。
- `Source/Native/` —— 项目自有的 C++ 服务栈，以及诊断用的注册表钩子。
- `ThirdParty/` —— `UupBridge` 使用的内置 UUP 源码闭包（`ThirdParty/UUPMediaCreator/`）。
- `Schemas/`、`Assets/` —— OEMInput XML schema，以及界面资源、工程模板与设备布局校验规则集。

### 按年代选择的 OEMInput 校验

OEMInput 文档会用与目标系统版本匹配的 schema 家族来校验：仓库内置 11 个 schema
（`Schemas/OEMInput.V1.*.xsd`、`V2.*.xsd`），可选的目标版本——在可能时从所选包根自动识别——决定用哪一个。
字段级的年代门禁会为并非每一代都存在的字段给出警告（AppX 可选包、MachineInfoFile、
TestContent/DevicePlatformIDs、V2 专有与 V2 已废弃字段）。`Assets/ValidationRules/` 下的设备布局规则集
同样按 ADK 版本家族存放，因此规则集与用户实际安装的 ADK 相符。

## 自带服务栈（进行中）

长期目标：**在不依赖操作系统自身服务组件的前提下**，从包集组装 Windows 镜像。上游这件事由
`CbsCore.dll`（CBS）、`wcp.dll`（WCP/SMI）、CMI 引擎、CSI/DMI 提供程序与 KTM 完成。这些组件与架构绑
定、与版本锁定，且部分受独立许可约束，因此 OCID 把所需行为重写为项目自有的 C++
（`Source/Native/OcidServicing/`），并通过一个小型 C ABI 暴露给应用。

| 模块 | 职责 |
| --- | --- |
| `Identity` | 装配标识：规范字符串 `name~version~arch~token~language`、`versionScope` 处理、非 SxS 包强制 `0.0.0.0` 版本，以及组件名推导（含 Windows 的长名缩写规则）。 |
| `Manifest` | 自带的组件清单拉取式解析器：标识、两套注册表声明机制（`registryKeys` 与 `configurationSchema`）、安全描述符定义、trustInfo 与 directories。 |
| `WcmSchema` | WCM（`smiengine`）schema 声明——`wcm:handler`、`wcm:legacyName`、`wcm:legacyType`、`default`——以及键级属性（`securityDescriptor`、`perUserVirtualization`、`mapDesiredAccessToMaxAllowedAccess`）。 |
| `RegistryValue` | 注册表值编码（声明文本到原始字节，按值类型）与类型正确的合并。 |
| `CmiInstaller` | CMI 写入决策：`owner`、`operationHint`（replace / append / prepend）、`InternalInstall`，以及分类型的合并规则。 |
| `MacroExpander` | `$(runtime.*)` 路径宏与 `$(runtime.host.*)` 注册表根宏的展开，含 WOW64 变体与未展开宏上报。 |
| `SecurityDescriptor` | SDDL 解析与描述、`(inherit)` 直通，以及 SDDL ↔ 自相对二进制互转。 |

### CAB 解压（托管侧，自有实现）

`Source/Managed/OcidCabinet/` 是本项目自写的 CAB 载荷解码器，`MobilePackageGen/Cabinet` 只保留
cab 容器（文件头、folder、`CFDATA`、文件落位）的读取，压缩部分改为引用它，因此那个 vendored 工程
不再依赖 SharpZipLib，也不再随附 XNA 的 libmspack 移植。两种编码都按 Windows 的实际语义实现：

| 编码 | 实现要点 |
| --- | --- |
| MSZIP（`TYPE_MSZIP`） | RFC 1951 原始 DEFLATE（无 zlib 头），支持 stored / 固定码 / 动态码。**每个 `CFDATA` 块沿用同一 folder 已解出的 32 KiB 历史**（makecab 的块边界并不重置字典），所以解码器实例按 folder 顺序复用。 |
| LZX（`TYPE_LZX`） | 窗口 15..21（`typeCompressOption` 直接作为窗口位），verbatim / aligned / uncompressed 三种块；码长表、R0/R1/R2、aligned 偏移都在块之间保持；`uncompressed` 块的奇数字节补齐与按 16 位对齐的重同步；intel E8 变换按 frame 位置累加。 |

验证口径是 `expand.exe`（Windows 自带的 cabinet API）：makecab 现造的 MSZIP 与 LZX（窗口 15/17/20/21）
夹具、8400 repair content 的 MSZIP 包、`C:\Windows\servicing` 下的 LZX 包（355 + 10 + 1 个文件）、
96 MB 的 `BucketConfidenceData.cab`（MSZIP），共 **429 个文件逐字节一致**；与替换前的实现
（SharpZipLib + XNA libmspack 移植）在 3,628 个文件上同样零差异。container CAB 按清单还原后与
`expand.exe` 的 696 个文件也逐个 SHA-256 一致。`Tests/` 里有对应的离线回归（用 makecab 现场造
多块夹具；真包对比通过 `OCID_CAB_TEST_DIR` 打开）。

WU 的 container CAB（条目是数字 id，真实文件名与 `PA30` delta 都记在 `_manifest_.cix.xml` 里）现在也
由读取器自己还原：`MobilePackageGen/Cabinet/ContainerCab.cs` 解析清单、按顺序把条目映射成真实文件，
`PA30` 的基取自同柜另一条目的内容（清单只记目标哈希、不记基是哪一条，所以按内容逐一试并用清单哈希
校验）。`PA30` 补丁由自研解码器（`Source/Managed/OcidCabinet/Delta/`，原生版
`Source/Native/OcidServicing/src/Delta.cpp`）应用；只有差分体是 PE（`fileType != 1`）时仍借
`msdelta.dll!ApplyDeltaB` 作过渡回退（RAW 与多格式补丁都已自研，
22,167 份 DCM 清单逐字节一致、计划哈希与 msdelta 结果相同）。selfupdate 容器包按清单还原出 696 个文件，与 `expand.exe`
的输出逐个 SHA-256 相同；`dummyrepairpackage_*.cab`（清单用 SHA-256、载荷为空）同样一致。

容器层仍有一类包超出读取器：sparse CAB（`iFolder` 取 `0xFFFD/0xFFFE/0xFFFF` 的分段文件约定）
与多卷包。桌面侧现在**完全不调用 `expand.exe`**——`Services/CabExtraction.cs` 遇到这类包直接抛
`NotSupportedException`，`CabHelper`、`UpdatePackageService`、`DeviceLayoutService`、
`CbsBuildService`、`CbsBuildViewModel` 都按失败报错，而不是偷偷退回系统组件。现有全部语料
（8400 repair content、16299 驱动包、container CAB、系统 servicing 包）都没有触发这条路径；
`expand.exe` 只在离线测试里作为独立真值 oracle 使用。

CAB 也可以脱离主程序单独使用：`Source/Managed/OcidCabinet.Tool/` 构建出 `ocid_cab.exe`，说明默认中文，
命令名保持英文以便脚本调用：

```powershell
dotnet build .\Source\Managed\OcidCabinet.Tool\OcidCabinet.Tool.csproj --configuration Release
# -> Source\Managed\OcidCabinet.Tool\bin\Release\net8.0\ocid_cab.exe

ocid_cab info a.cab            # 柜头、folder 的压缩/块布局、总大小
ocid_cab list a.cab --long     # 条目、未压缩大小、时间、属性、所在 folder
ocid_cab extract a.cab out     # 解包（MSZIP 与 LZX），--verbose 逐条目报进度
ocid_cab cat a.cab update.mum --output update.mum   # 抽单个条目
ocid_cab verify a.cab          # 解到临时目录并算每文件 SHA-256，确认全部可解码
```

`extract` 对 container CAB 会按 `_manifest_.cix.xml` 还原真实文件名并展开 `PA30` delta；sparse CAB
一律拒绝并说明原因。退出码：0 成功、1 用法错误、2 柜文件或解包失败。

### CBS 台账写侧（COMPONENTS）

`CbsStore` 负责 COMPONENTS 台账的编解码，`ocid_manifest cbs-write` 与 `assemble-cbs-store.ps1` 负责
从组件清单/包清单生成它；流程是"组件计划 → 部署与 catalog 计划 → 版本索引计划 → 根值 → 事务化落盘 →
`cbs-verify-files`/`cbs-verify-store` 自检 → 与真机 `cbs-diff` 对拍"。`ocid-assemble.ps1` 把整条链
（含安装历史输入导出与覆盖表自动生成）收成**一条命令**：

```powershell
pwsh -NoProfile -File .\ocid-assemble.ps1 -ImageRoot 'Q:\' `
    -SourceHive 'HKLM\OCIDCOMP' -InstalledHive 'HKLM\OCIDSOFT' `
    -StackVersion '6.2.8061.0 (fbl_core1_soc.110803-1830)' `
    -OutDir 'E:\ocid_verify\oneX86' -AutoOverrides -Transactional
```

| 项 | 状态 |
| --- | --- |
| 读侧全库 decode→re-encode（`cbs-scan`） | 0 不一致 |
| 组件/部署/家庭键、伪键折叠 | 13,325/13,325 与 2,283/2,283 命中 |
| `f!`、`p!/s!/i!`、`S256H`、`CatalogThumbprint`、`Catalogs`、`f256!`、`MCP_*`、`CF` | 已解并落地 |
| 验收（对微软原版逐键/逐值/逐字节） | 8061 x86 43,100 键/120,978 值；8061 ARM 613/1,408；8419 ARM 23,328/61,277；16299 amd64 73,535/215,978；9600 amd64 10,069/33,493 —— `cbs-diff` 均 0/0 |
| 版本差异 | 按构建号分档（`CbsProfile`）：`>=9600` 起 DCM 清单 + `f!` 状态位，`>=16212` 起 `Drivers` 台账，`>=16299` 起载荷名折叠与 identity 归一；`--os-build` 可显式指定 |
| 现代镜像（16299+） | `DCM` 紧凑清单（`PA30` 自研解码 + 独立字典文件）已解；`f!` 改 DWORD 状态位、`f256!` 取清单声明哈希、`S256H` 取解压后 XML、新增 `Drivers` 台账 —— 全流程同样 0/0 |
| 安装历史类值 | 作为输入：`Extract-CbsInputs.ps1` 一条命令导出，`make-cbs-overrides.ps1` 生成覆盖表 |
| `WinSxS\ManifestCache\*_blobs.bin`（PCM/CDF） | 容器层可逐字节重建（整文件一致率 62.1% → 99.6%，2,746/2,757），剩 11 份单样本形态（白名单 `Source/Native/OcidServicing/pcm-known-fails.txt`）；另接入 6.3.9600 第二套语料 2,784 对，T4 2,331（83.7%），差异已归类 |
| 可开机镜像（P1 串联最后一公里） | `Build-OcidImage.ps1` 一条命令：建盘 → `dism /Apply-Image` → `bcdboot` → 换台账 → `cbs-scan`/`cbs-verify-store` → `dism /Get-Packages` 换前后对比 → 分离 |
| 驱动包落位（DMI 台账） | `dmi-ops` → `dmi-place`（载荷进 `FileRepository`、INF 发布成 `Windows\INF\oemN.inf`、产出 `DriverDatabase` 计划）→ `cbs-write --append` 写进镜像的 SYSTEM hive；签名者信息作为输入（`--signer`/`--signer-score`） |

`Stage-OcidImage.ps1` 把三件事串成一条命令：CSI 落位（`place-ops` → `csi-place`）→ 驱动落位（DMI，
上面那行）→ 台账组装并换进镜像。

架构、算法与复现命令见 `Source/Native/OcidServicing/CBS-Writer.md` 与
`Source/Native/OcidServicing/DMI-Writer.md`。

同一份代码同时支撑控制台工具与主程序。托管侧经 `Services/OcidServicingNative.cs` 绑定，并在
`Services/OcidSmiService.cs` 中应用决策。应用刻意**不**回退到托管版的值编码实现：原生库不可用时跳过该
操作并如实上报——一个静默写错的字节映像比一次明确的失败更糟。

`Source/Native/OcidServicing/tools/ocid_manifest.cpp` 构建出 `ocid_manifest.exe`，这是查看与导出同一
批决策的控制台入口（`info`、`ops`、`scan`、`export-ops`、`verify-names`、`expand`、`macros`、
`dump-xsd`），用于全语料验证。全部命令的用法、选项与示例见 `ocid_manifest help`（单条命令用 `ocid_manifest help <命令>`），默认中文说明。

`Source/Native/OcidRegHook/` 是一个独立的 x86 DLL，它给 `wcp.dll` 的导入表打补丁，使上游服务栈发出的
注册表调用可以被观察——并可选地重定向到离线 hive——而不是直接落到宿主注册表。它只被遗留的、由
`wcp.dll` 驱动的 CBS 路径加载，不属于目标设计，且默认只记日志（`OCID_REGHOOK`、`OCID_REGHOOK_LOG`、
`OCID_REGHOOK_MAP`、`OCID_REGHOOK_REWRITE`）。它是过渡期的研究工具，不是自带服务栈的一部分。

## 仓库布局

```text
Assets/                   UI 资源、模板，以及分 ADK 版本的设备布局校验规则
Models/                   领域与工程模型
Services/                 流程服务（镜像、CBS、SxS、驱动、固件、离线注册表、UUP、校验）
ViewModels/               WPF 视图模型
Views/                    WPF 页面、步骤与对话框
Schemas/                  内置 OEMInput V1/V2 schema
MobilePackageGen/         包生成工具链、命令行工具与 GUI（自带 README）
UupBridge/                独立 .NET 8 适配进程，包在内置 UUP 核心之上
UupShared/                离线 UUP 查询解析与负载契约，主程序与桥接共用
Tests/                    离线回归测试
Source/Native/OcidServicing/  项目自有 C++ 服务栈：库、控制台工具与原生测试
Source/Native/OcidRegHook/    遗留 CBS 路径用的诊断注册表拦截 DLL
Source/Managed/OcidCabinet/   自有的 CAB 解压器（MSZIP 的 DEFLATE 与 LZX），供包工具与桌面侧共用
Source/Managed/OcidCabinet.Tool/  ocid_cab 控制台：查看/解包/抽取/自校验 CAB，可独立使用
Tools/wcpex/              内置 wcpex.exe + wcp.dll，用于解压 DCM 压缩的清单
ThirdParty/               UupBridge 使用的内置 UUP 源码闭包
addonkit/                 内置的微软 iot-adk-addonkit 17763-v7（PowerShell 工具链 + 示例工作区）
Build-CbsBundle.ps1       组装独立的 CBS 控制台包
```

生成的 `bin`、`obj`、`build`、`intermediate` 与 `Source/Native/Output` 目录、调试符号、生成的 Appx
文件都不入库；发布包放在 GitHub Releases。作为**运行期**输入保留的是 `Tools/wcpex/` 与 `addonkit/`
目录树，二者都是运行期被消费，而不是由构建产出。

应用内下载器消费的分 ADK 版本工具 zip（落在 `addonkit\adk\<build>\`）作为资源托管在本仓库的 GitHub
Release 上，标签为 `adktool`。

## 测试

`Tests/` 是离线回归工具（`Tests/Program.cs`，不联网、不需要镜像）。其中分组覆盖：UUP 查询解析与快照
往返、负载定位与路径穿越拒绝、桥接取消与错误传播、转换输入/输出/工具校验、覆盖保护、同盘剩余空间核算、
命令行参数安全构造、产物预检、SxS 不执行门禁、XML 能力探测、CAB 解压（makecab 现场造 MSZIP/LZX
夹具并逐字节比对），以及托管到原生的服务绑定。

原生模块各有断言套件，由 `build-servicing.ps1` 构建并运行：`identity`、`manifest`、`wcm`、
`registryvalue`、`cmiinstaller`、`macroexpander`、`securitydescriptor`。

## 第三方代码与许可

仓库根目录的 `LICENSE`（MIT）只适用于本项目自有的代码，不会重新许可内置的第三方软件；再分发时请保留
各组件自带的声明。下表是本仓库所引用、内置或随附的全部第三方代码与二进制清单；其余部分要么是项目自有，
要么取自用户自行提供的安装。

### 主程序直接依赖的库

| 组件 | 位置 | 许可证 |
| --- | --- | --- |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) 8.4.2 | `OneCore Image Designer.csproj` 的 NuGet 引用 | MIT |
| [RawDiskLib](https://github.com/LTRData/RawDiskLib) 0.2.1 | NuGet 引用 | 见包内许可 |
| [LTRData.DiscUtils](https://github.com/LTRData/DiscUtils) 1.0.55 | NuGet 引用 | MIT |
| [System.Management](https://github.com/dotnet/runtime) 8.0.0 | NuGet 引用 | MIT |

### 内置的源码树与二进制

| 组件 | 上游 | 本仓库位置 | 许可证 |
| --- | --- | --- | --- |
| iot-adk-addonkit 17763-v7 | [microsoft/iot-adk-addonkit](https://github.com/microsoft/iot-adk-addonkit) | `addonkit/` —— PowerShell 版 IoT Core 镜像工具链及其示例工作区 | MIT（`addonkit/LICENSE`）；微软自带的第三方声明清单见 `addonkit/TPN`。其中的示例证书与 `.pfx` 是微软随官方工具链公开发布的示例密钥，**不是**私密凭据。 |
| MobilePackageGen（fork） | [MobileTooling/MobilePackageGen](https://github.com/MobileTooling/MobilePackageGen) → [DF4D3110/MobilePackageGen](https://github.com/DF4D3110/MobilePackageGen) | `MobilePackageGen/` —— CLI、WinForms GUI、`CbsxCli`、`SxSFounderCli`、各类镜像适配器、CAB/DCM 压缩、cat/签名重建 | MIT。完整组件表见 `MobilePackageGen/readme.md`，许可正文见 `MobilePackageGen/license.md`。 |
| WSK Tools v1.0.4 | [gus33000/WSK](https://github.com/gus33000/WSK) | MobilePackageGen fork 的原始基线 | MIT |
| UUPMediaCreator | [OSTooling/UUPMediaCreator](https://github.com/OSTooling/UUPMediaCreator) | `ThirdParty/UUPMediaCreator/` —— 源码闭包；只含 `UupBridge` 需要的工程，保留上游许可与 README | MIT。`src/Cabinet/Xna/LzxDecoder.cs` 为 LGPL-2.1 或 MS-PL 双许可，保留其原始文件头。不重分发任何转换器或 `cdimage.exe` 二进制。 |
| NSudo（提权） | [M2TeamArchived/NSudo](https://github.com/M2TeamArchived/NSudo)（已归档；`NSudoAPI.dll` 用 [UUP-Project/NSudo](https://github.com/UUP-Project/NSudo) 分支构建） | C# 绑定位于 `MobilePackageGen/MobilePackageGen.Common/NSudo`；DLL 本身**不**随仓库分发 | MIT |
| wcpex | [smx-smx/wcpex](https://github.com/smx-smx/wcpex) | `Tools/wcpex/wcpex.exe` + `wcp.dll`，以内嵌资源方式打包、运行期释放，用于解压 DCM 压缩的清单 | 预编译二进制；上游未随附许可文件，因此这里按原样保留、不参与构建。 |
| DevImgGen | [mediaexplorer74/DevImgGen](https://github.com/mediaexplorer74/DevImgGen)（fork：[DF4D3110/DevImgGen](https://github.com/DF4D3110/DevImgGen)） | 镜像构建页入口的**设计参考**，记录在 `ViewModels/ImageBuild/ImageBuildViewModel.cs` 的文件头注释里 | MIT。未复制任何代码，也未分发其中任何内容。 |

内置的 `MobilePackageGen/` 子树的完整组件清单——LTRData.DiscUtils、RawDiskLib、
System.Reactive、ZLibDotNet、System.IO.Hashing、Zafiro.DivineBytes、SevenZipExtractor / 7-Zip
（LGPL-2.1）、Microsoft.Deployment.Compression（WiX，MS-RL）、DotnetPackaging.Msix、
DeflateBlockCompressor、LibSxS、Img2Ffu、StorageSpace、WPMT.Core，以及系统组件
`msdelta.dll`——在 `MobilePackageGen/readme.md` 中逐条列出。cab 的压缩解码器不在其中：
MSZIP/LZX 是项目自有的 `Source/Managed/OcidCabinet/`，`MobilePackageGen/Cabinet` 已不含
SharpZipLib 与 XNA `LzxDecoder`。

### 被引用或使用，但不随本仓库分发

- 从机器上取用的微软工具与组件：ADK、DISM、Windows 10 IoT Core Kits、微软包样例，以及
  `msdelta.dll`。
- 应用内下载器使用的分版本 ADK 工具 zip，作为资源托管在本仓库的 GitHub Release 上（标签
  `adktool`）。
- `CbsExploder` 与 `SxSFounder` 可执行文件——其独立的再分发条款尚未确定。
- UUP 转 ISO 转换器及其 .NET 10 运行时——需用户自行提供单独构建的版本。
- 曾在 `ThirdParty/SxS/` 下内置的 GPL-3.0 `pureSxS` / `explodeSxS` 脚本树已从仓库中**移除**；它们
  所记录的行为已在项目代码中重写（`Services/PureSxsService.cs`、
  `Services/ExplodeSxSNativeService.cs`，两者都在文件头注明了来源）。SxS 页面仍是只读预检，不执行
  任何东西。
- 包生成源码曾经携带的微软 `makeappx.exe` 夹具及其支持 DLL
  （`MobilePackageGen/DotnetPackaging.Msix/RemakeAppxTestContent/`）已**移除**；这些源码改用自有的托管
  实现，仅保留纯托管的测试内容。

## 状态

本项目仍在积极开发中。有三点值得直说：

1. **服务栈的重写已完成一半以上，且已完成的部分是自带的。** SMI（WCM 声明与值编码）、CMI 合并决策、
   DMI（驱动库导入）、CSI（文件落位）、CBS（COMPONENTS 台账的读与写）以及离线 ACL/TI 处理都已由
   `Source/Native/OcidServicing` 自己实现；CBS 写侧在四个真机镜像（8061 x86、8061 ARM、8419 ARM、
   16299 amd64、9600 amd64）上与微软原版的 COMPONENTS 做到逐键/逐值/逐字节一致（`cbs-diff` 0/0）。尚未自带的只有
   `WinSxS\ManifestCache` 的 PCM/CDF 编码器（容器层已逐字节可复现；`src/PcmBuild.cpp` 直接由清单构建，全语料 99.6% 整文件一致）。
   cab 的 MSZIP/LZX 解压也已自有（`Source/Managed/OcidCabinet`，见上），WU container CAB 的 `cix`
   清单还原也已接进读取器（`PA30` delta 由自研解码器应用，仅 PE 载荷回退 `msdelta.dll`；
   该格式的反编译结论已整理在 `Source/Native/OcidServicing/Delta-Format.md`，解码器本身尚未实现）；
   桌面侧全部解包都在进程内完成，不再调用 `expand.exe`；sparse CAB 与多卷包明确报不支持（现有语料未出现）。
2. **安装历史类信息必须作为输入。** 有些值（`f256` 多段的段序、identity 文化大小写、`c!` 归属、
   catalog 选择、`MCP_*`/(default) 位图、现代 `f!` 的非 1 状态位）来自镜像的安装历史，不能从包集推导；
   `Extract-CbsInputs.ps1` 从参考镜像导出它们，`make-cbs-overrides.ps1` 生成覆盖表，组装时显式传入。
   16299 及之后的镜像清单是 `DCM` 紧凑格式（已解，见 `Source/Native/OcidServicing/CBS-Writer.md`）。
3. **部分流程依赖用户自备的软件** —— ADK、IoT Core Kits、单独构建的 UUP 转换器，以及管理员权限。
   流程跑不起来时会明确说明，而不是静默降级。

## 关于

「帮助 → 关于」通过 `rundll32 shell32.dll,ShellAbout` 打开 Windows 自带的 Shell About 对话框；应用
自身不显示另行撰写的关于信息。
