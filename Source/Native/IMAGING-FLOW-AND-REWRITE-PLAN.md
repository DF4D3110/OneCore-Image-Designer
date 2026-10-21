# imageapp（20348 工具链）全流程解剖与自带重写方案

> 状态：**分析稿**，只做结论与路线，不含实现。基于两份真实运行的产物 + 日志（见 §1）。
> 目标口径：只负责 **20348 版 ADK 工具链**（`wcp.dll 10.0.20348.1`）这一代 imageapp，不追 1809/2025 其它版本。
> 撰写日期：2026-10-21。

---

## 0. 结论先行

1. **imageapp 本身几乎不干活，它是编排器**。真正的工作分散在五个子系统里：FeatureAPI（XML 与校验）、
   UpdateAgent（把 OEMInput+FM 变成计划）、UpdateDLL/CBS（离线装包 = 把组件装进空卷）、
   imagestorageservice（建盘/分区/格式化/收尾）、AppXCommon（AppX/MSIX 子流水线）。
2. 所以"重写 imageapp"实际是"重写这五个子系统里 imageapp 依赖的部分"。按依赖强度排序：
   **建盘与收尾（中等）< 计划（中等）< FM/DeviceLayout 解析（低）< CBS 装包（最难）< AppX 流水线（难且独立）**。
3. 六镜像/COMPONENTS 那条线（OCID 现有成果）解决的是"**替换已存在镜像里的 store**"；
   imageapp 这条线解决的是"**从一堆包 CAB 凭空造出一个能启动的卷**"——两者共用 CBS 知识，但**不是同一件事**：
   前者输入有现成 COMPONENTS，后者要从零建立（CSI 组件落位 + 注册表 + 服务/驱动/appx 各类安装器 + store 记账）。
4. 建议分三档推进（§6）：**L0 复刻编排（保留 UpdateDLL，1~2 周可见效果）→ L1 自带装包（自写 staging，最大工作量）
   → L2 全自带（CompDB + AppX + 去 ADK）**。不要一次性冲 L2。

---

## 1. 证据基础

| 素材 | 位置 | 说明 |
| --- | --- | --- |
| 完整产物 A（AMD64 + AppX） | `E:\wcos\10.0.20274.1000.fe_release_10x.201202-1415\成品\` | 带 `+StrictSettingPolicies /CPUType:AMD64`，含 AppX |
| 完整产物 B（ARM64 QEMU，无 AppX） | `E:\wcos\10.0.20279.1002.fe_release_10x.201214-1532\QEMU-Stage\` | `/CPUType:ARM64`，前缀拼写为 `Falsh.` |
| 编排日志 | `*.ImageApp.log` | 格式 `{yyyy/M/d H:mm:ss}  msg`，A 7,317 行 / B 3,603 行 |
| CBS 日志 | `*.ImageApp.cbs.log` | A 52,692 行 / 10.2 MB，B 10.6 MB，`.err` 为空 |
| 反编译 | `E:\ocid_decomp\*`、`D:\ADK2022Wrokspace\_decomp\imageapp` 等 | 见仓库 `imageapp-buildchain.md` |

两条命令行的实际形态（注意输出是 **`.vhdx`**，不是 FFU；`+StrictSettingPolicies` 用 `+` 前缀）：

```
ImageApp  "C:\10x\Output\Flash.vhdx" "C:\10x\OEMInput.xml" "C:\10x\MSPackages" +StrictSettingPolicies /CPUType:AMD64
"F:\Program Files\Windows Kits\10\Tools\bin\i386\imageapp.exe" D:\20279\Falsh.vhdx <OEMInput.xml> "<packages>" +StrictSettingPolicies /CPUType:ARM64
```

→ imageapp 是 **x86 进程**，即使造 ARM64 镜像也跑 x86 版（和 CbsxCli 必须 win-x86 同理：ARM 镜像里带的是 x86 服务栈）。

产物清单（A 为例，括号内为大小）：

```
ActionList.xml (167,454)          UpdateAgent 计划：Downloads 181 / Plan 55 / Actions 236
Flash.BSPDB.xml (650)              CompDB：Tags(UpdateType=Canonical)，空 Features/Packages
Flash.BSPDB_publish.xml (528)      CompDBPublishingInfo
Flash.Imaging.BSPDB.xml (650)      CompDB（ImageApp 用）
Flash.DeviceDB.xml (127,828)       CompDB：55 Features + 181 Packages + device targeting
Flash.Packageless.DeviceDB.xml (7,369)  无包 DeviceDB（保留 device/feature 骨架）
Flash.DeviceLayout.xml (6,901)     处理后的 DeviceLayout v2（含固定 store GUID、分区尺寸）
Flash.DriveLetterConfig.xml (475)  MainOS=M、Data=C
Flash.overlap.txt (8,444)          撞车文件列表
Flash.PackageList.xml (167,136)    当前镜像包清单（181）
Flash.UpdateHistory.xml (185,953)  更新历史（1 个 UpdateEvent）
Flash.UpdateInput.xml (35,074)     UpdateDLL 输入：233 个 PackageFile（181 包 + 52 FM 包）
Flash.UpdateOutput.xml (165,180)   逐包结果（181）
Flash.vhdx (3,399,483,392)         动态 VHDX（虚拟容量 ~42 GiB×6 + 128 MiB）
ProcessedFiles\                    本次实际喂进去的 OEMInput.xml 与 12 个 FM XML
```

---

## 2. 六段流水线（含实测耗时）

A 次（AMD64，含 AppX）总 43:19；B 次（ARM64，无 AppX）明显更快。

| # | 阶段 | 主要输入 | 落盘产物 | 实际执行者 | A 耗时 |
| --- | --- | --- | --- | --- | --- |
| 1 | 读输入与 FM | OEMInput.xml、AdditionalFMs + FM 包内 FM | `ProcessedFiles\*`、Temp 下解包的 FM | FeatureAPI | 13.5 s（ProcessFMs） |
| 2 | DeviceLayout | DeviceLayout 包 / FM 覆盖 + `OEMDevicePlatform.xml` | `Flash.DeviceLayout.xml` | Imaging（`DeviceLayoutValidation`） | 含在 1 内 |
| 3 | CompDB + FIP 校验 | 全部包 CAB（含 AppX） | `*.BSPDB.xml`、`*.DeviceDB.xml`、`Packageless.DeviceDB.xml` | Imaging + AppXCompDB | 5:00（其中 AppX 4:24） |
| 4 | 建盘 | DeviceLayout | `Flash.vhdx` + 存储池 OSPool + 7 个 store/分区 + 格式化 + WOF + Compact | imagestorageservice（native） | 6.3 s（CreateFullFlashImage） |
| 5 | 规划 | 包集合 + FM + CompDB | `ActionList.xml`、`UpdateInput.xml`、`PackageList.xml`、`DriveLetterConfig.xml` | UpdateAgent（`UA_CreateActionList`） | 1.5 s（LoadPackages） |
| 6 | 执行装包 | `ActionList`/`UpdateInput` + 已挂载卷 | 卷内容（组件/注册表/服务/驱动/AppX） | UpdateDLL → CBS（CSI/SXS/DPX） | 7:29（Staging）+ 15:59（Commit），其中 AppX Staging 19:47、AppX Commit 10:35 |
| 7 | 收尾 | — | 卷属性（USN/GPT ID/挂载点） | Imaging + imagestorageservice | 36 s（FinalizeDisks + Detach + Dismount） |
| 8 | 记账 | 执行结果 | `UpdateHistory.xml`、`UpdateOutput.xml` | Imaging + FeatureAPI 校验 | <1 s |

> 注：`AppxStaging` / `AppxCommit` 与 6 的阶段窗口重叠（AppX 在 Staging 中段与 Commit 段内执行），
> 它们占了 43 分钟里约 30 分钟，是**性能大头**。

---

## 3. 每段内部机制

### 3.1 输入与 FM（FeatureAPI）

- 先校验 `OEMInput.xml`（schema `.../2019/06/ImageUpdate`）：`Description/DeviceLayoutType/SV/Device/ReleaseType/BuildType/Languages/AdditionalFMs/Features/MachineInfoFile`。
- 按 `AdditionalFMs` 顺序读 FM XML；每个 FM 里被引用的 `<packages>`/`<Files>` 再从对应 **FM 包 CAB** 里解出
  `windows/imageupdate/featuremanifest/microsoft/*.xml` 到 `%TEMP%\<guid>\1\<guid>\...`，再校验一遍。
- FM 里会记 `DriveLetterConfig` 包路径（`Found drive letter config package path while parsing FM`）——
  即盘符配置本身也是包，不是外部文件。
- 记录 `PROCESSEDFILES\<FM>.XML`（大写名）用于诊断。

### 3.2 DeviceLayout

- 来源优先级：FM 覆盖 / OEMInput 指定 → 否则从 device platform 包里抽 `DeviceLayout.xml`（`Device platform id(s) was not found in OEM Input XML file. Attempting to fetch the id from device platform package.`）。
- 用 `DeviceLayoutValidation` 清单（`Microsoft.ARM_Qualcomm.Andromeda.DeviceLayoutValidationv2.xml` 等）按作用域
  `[CPU:SV:DeviceLayoutType:Device:ReleaseType:OSType:Version:Revision]`（例 `[ARM64:QEMU:QEMU_10X_SPACES_512:QEMUArm64:Test:WindowsMobile:10:0000]`）选规则并校验，`FileCount -> 14`。
- 产出 `Flash.DeviceLayout.xml`：**v2 结构**（`StoragePools/StoragePool/Stores/Store/Partitions/Partition`，另加 `Stores/Store` 的 Default store、`SectorSize 512`、`ChunkSize 128`、`DefaultPartitionByteAlignment 0x200000`、`StateSeparationLevel 0`）。

实测布局（A 次）：7 个 store，全部 `SizeInSectors=88080384`（42 GiB，thin）除 `VIRT_EFIESPDisk=262144`（128 MiB）；
每个 store 单分区 `UseAllSpace=true`，`FileSystem=NTFS`；MainOS 额外 `ByteAlignment=0x800000`、`ClusterSize=0x1000`、
`PrepareFveMetadata=true`；BSP 也 `PrepareFveMetadata=true`；Data 的分区类型是标准 Basic Data
`{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}`，VIRT_EFIESP 是 ESP 类型 `{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}`，
其余是 `{57434f53-…}` 系列（WCOS 自定义）。

### 3.3 CompDB（对重写最关键的一点）

- `Flash.BSPDB.xml`：`Tags/UpdateType=Canonical`，Features/Packages 均为空——BSP 侧的"发布信息"。
- `Flash.Imaging.BSPDB.xml`：同类，Imaging 用。
- `Flash.DeviceDB.xml`：**55 Features + 181 Packages**，带 device targeting（FIP/Feature 关系）——UpdateAgent 用它判断
  "这个包对这个设备是否适用"。
- `Flash.Packageless.DeviceDB.xml`：去掉包、只留 device/feature 骨架（给"包不在本地"的场景）。
- AppX 部分单独跑：`AppXCompDB: Started for AMD64`，逐 bundle 打印 `[AppXBundle]… Dependency: N, BundleChildren: M`，
  产出 AppX 的 CompDB 条目（B 次无 AppX 时 0.03 s 就结束）。

→ **重写如果自己算计划，就必须自己产出"设备可用性/特性映射"这份数据**，否则计划会与微软不同。
或者：跳过 CompDB，直接用自己的包选择器（OID 的 FMService + cbs-plan-*），代价是接受与官方计划不完全同构。

### 3.4 建盘

日志证据链（A）：

```
Imaging!InitializeMinFreeSectors: Skipping as it is not applicable to device layouts with storage pool(s)
CreateStoragePool: Creating Storage Pool (OSPool), Version (ScVersionWindows19H1)
CreateFullFlashImage: Created a new image in 6.3 seconds
Imaging: Attaching WOF to partition 'EFIESP' / 'MainOS' / 'BSP' / 'PreInstalled' / 'OSData' / 'Data' / 'VIRT_EFIESP'
Imaging: Marking partition 'MainOS' as Compact
```

含义：
1. 建动态 VHDX（3.4 GB 实占，虚拟 42 GiB×6+128 MiB）；
2. 建 **Storage Spaces 池 OSPool**（19H1 版本语义），每 store 一个 thin 虚拟磁盘，Manganese 及以上再建
   5 GB 保留空间（servicing metadata/files disk）；
3. 每分区格式化为 NTFS（MainOS/BSP/PreInstalled/OSData/Data/EFIESP/VIRT_EFIESP 共 7 个 + BS_EFIESP +
   SERVICING_METADATA + SERVICING_FILES = 10 个卷，`FlushVolumes` 逐个出现）；
4. 对每个分区挂 **WOF**（Windows Overlay Filter，实现文件压缩/去重），MainOS 标记 Compact。

### 3.5 规划（UpdateAgent）

`ActionList.xml` 三个节点，语义如下：

```xml
<ActionList xmlns="urn:schemas-microsoft-com:os-update-actionlist" SessionId="…" Revision="1"
            SessionData="MobileUpdate" BuildInfo="fe_release_10x.20274.1000.201202-1415" Operation="MobileUpdate">
  <Downloads>   <!-- 181：每个包一条，带 Payload 的 SourceName/SourcePath/DownloadSize/Reason(Base|Optional) -->
  <Plan>        <!-- 55：InstallFeature，带 Id/Group/FMID -->
  <Actions>     <!-- 236：55 InstallFeature + 181 InstallPackage -->
</ActionList>
```

- `InstallFeature` 的 `Actions` 形态：`<Token value="INSTALLED"/><Intent value="PREINSTALL"/><Intent value="SYSTEM"/>`，
  语言特性还带 `<DeclareSatellite type="LANGUAGE" value="en-US"/>`。
- `InstallPackage` 形态：`Keyform="<pkg>~<version>"`、`InstalledSize`、`StagedSize`、`SourcePath`、`FilePath`、
  `Partition="MainOS"`、`BinaryPartition="false"`，子节点 `<InstallPayload FilePath PayloadType="Canonical"/>`。
  **Partition 是这个包的落盘分区**——这是"包→分区"映射的唯一权威来源（EFIESP / MainOS / updateos）。
- `UpdateInput.xml` 只有 `Description/DateTime/PackageFiles`（233 条），给 UpdateDLL 当输入。
- `DriveLetterConfig.xml` 只有 MainOS=M、Data=C（CBS 需要知道哪个卷是系统卷、哪个是数据卷）。
- `PackageList.xml` 是"装完之后镜像里有什么"的清单（181），UpdateOutput 是逐包结果。

### 3.6 执行（UpdateDLL → CBS）

`ExecuteUpdate` 内部是 **两阶段 + 子阶段**（日志原文）：

```
Staging:  UpdateValidator → StagingEFIESP → StagingMainOS → Stagingupdateos → Commitupdateos → WimCommit → FinishUp
Commit:   CommitMainOS → CommitEFIESP
```

`cbs.log`（52,692 行）揭示 CBS 侧的动作类别：

- `WcpInitialize: wcp.dll version 10.0.20348.1` —— **用的是 20348 的 CBS 栈**（工具自带）。
- 包解到沙箱：`\??\ExtSandbox\<包>.cab-extracted\…`（先解到沙箱，再往里走）。
- `SXS | Staging component <name>, version …, arch amd64, nonSxS, pkt {l:8 b:…}` —— 逐组件落位（这是 **CSI/SXS** 的活）。
- `SXS | TURBOSTACK STAGE MOVING FILE \??\ExtSandbox\…` —— 文件搬运（turbostack 是并行文件落地通道）。
- `SXS | Encountered uncompressed manifest …` —— manifest 直接复制。
- `DPX | Started DPX phase: Apply Deltas Provided In File`（728 次）、`Resume and Download Job`（364 次）、
  `CUMULATIVE Dpx time` —— **DPX 增量/差分压缩**参与文件落地（这是性能关键的一环，二进制差分）。
- 各类安装器（SXS 分类）：`Services category (svchost group, lanman)`、`Task Scheduler`、`Counters Installer`、
  `Firewall Rule`、`Network Drivers`、`System Protection`、`Per-User Registry Installer`、`AppX Registration Installer`。
- 校验告警：`State Separation violation`（HKCU/.Default 键、ACL 传播），说明 20348 的 CBS 已经内建状态分离规则。

→ 这一段是 **CBS 的完整离线装包能力**：解包→组件落位→文件落地（含增量）→注册表→服务/驱动/任务/计数器/防火墙→AppX 注册→store 记账。
OCID 现有 `OcidServicing` 已覆盖其中"组件落位（CSI）、注册表、安全描述符、驱动 INF、DMI、PCM/CDF 写 store"，
**未覆盖**：turbostack/DPX 级的文件落地、WOF/Compact、以及"从零建 store"（现有实现是替换现成 COMPONENTS）。

### 3.7 AppX 子流水线（独立且昂贵）

```
AppxStaging start → AppXCommon: Stage started for AMD64（把 appxbundle 解到卷上 Temp\appx）
AppxCommit start  → 扫 staging 根：Found 33 apps, 21 frameworks, total 140 packages
                  → Copy license XML、Copy package …（140 条）
                  → Saving Space in \WindowsApps（Total 0 hard-links, 2938 compressed files, saved 708,759,318 bytes）
                  → 写注册表 AppxAllUserStore\Applications\… 与 \Staged\…（每包 1~4 条）
                  → Clean up apps staging folder → GC before finish → Commit finished
```

要点：AppX 包**不进 CBS 的组件通道**，而是"解包 → 复制到 WindowsApps → 硬链接/压缩优化 → 写 AppxAllUserStore 注册表"。
这是 imageapp 里最"自成一体"的一块，也是 L2 里最容易被单独延后的一块。

### 3.8 收尾（FinalizeDisks）

```
Enabling USN journal on partition MainOS / Data
Flushing all volumes（10 个卷）
Setting <分区> partition ID <GUID>（BS_EFIESP/EFIESP/MainOS/OSData/BSP/PreInstalled/VIRT_EFIESP）
Setting <store> disk ID <GUID>（MainOS/EFIESP/OSData/BSP/PreInstalled/VIRT_EFIESP/Default）
DetachStorageSpace: Detaching storage space <Store>（9 个，Discarded changes? False）
Finalized disks → DismountVirtualHardDisk → Image processing complete
```

**关键发现：这些 GUID 是确定值，两次运行完全一致**（同一 DeviceLayout）：

| 对象 | GUID |
| --- | --- |
| BS_EFIESP | `aa16c266-5f58-4cf8-829f-c323cbe22ae6` |
| EFIESP | `8183040a-8b44-4592-92f7-c6d9ee0560f7` |
| MainOS | `a76b8ce2-0187-4c13-8fca-8651c9b0620a` |
| OSData | `b0479ff4-43bb-4710-90cd-20653989dd7d` |
| BSP | `dff26bef-f2aa-413f-8e45-260df4a4a141` |
| PreInstalled | `c8adae64-47f4-4cfa-b67c-1e743ad5dfc9` |
| VIRT_EFIESP | `12aef83a-6cf2-4ea1-932f-b3a586a65308` |
| 磁盘 EFIESPDisk | `619b6838-1dca-4904-83ad-d5d0bea0aa4a` |
| 磁盘 MainOSDisk | `ae420040-13dd-41f2-ae7f-0dc35854c8d7` |
| 磁盘 OSDataDisk | `913bbeb3-2331-43d7-a53a-8d6e1eaf5f4e` |
| 磁盘 BSPDisk | `b59e0f00-c531-42ae-9223-7b1df438a46c` |
| 磁盘 PreInstalledDisk | `6ee30957-92a8-49c4-b02f-6eacecdc90d4` |
| 磁盘 VIRT_EFIESPDisk | `631cb517-f8bb-45f3-a70a-222ec9906f23` |
| Default store | `5a585bae-900b-41b5-b736-a4cecffc34b4` |

其中 store/Default 的 GUID 也出现在 `Flash.DeviceLayout.xml` 里（即**布局文件自带**），
分区 ID 则不在布局里 —— 是 imaging 侧的固定表（与 `DeviceLayoutType` 绑定）。

### 3.9 记账

`UpdateHistory.xml`（1 个 `UpdateEvent`，`DateTime=2026-07-07T06:08:13Z:: `）+ `UpdateOutput.xml`（181 包逐条
`Result/OverallResult`）+ `PackageList.xml`。三者都是"装完之后设备状态的快照"，下一次增量更新会读它们。

---

## 4. 重写必须对齐的契约（硬清单）

1. **DeviceLayout v2**：`StoragePools/Stores` 语义、`SizeInSectors`（sector=512）、`ByteAlignment`、`ClusterSize`、
   `PrepareFveMetadata`、`UseAllSpace`、`StateSeparationLevel`、`ChunkSize`。
2. **分区/磁盘 GUID 表**（§3.8）+ 分区类型 GUID（`{57434f53-…}` 系列、ESP、Basic Data）。
3. **`ActionList.xml`** 三个节点与 `Partition`/`Keyform`/`Token`/`Intent` 语义。
4. **`UpdateInput.xml`**（233 条 PackageFile）与 **`DriveLetterConfig.xml`**（MainOS/Data）。
5. **`PackageList/UpdateOutput/UpdateHistory`** 字段集（增量更新要读）。
6. **CompDB 四处 XML**（BSPDB / Imaging.BSPDB / DeviceDB / Packageless.DeviceDB）——至少 DeviceDB 的
   Feature↔Package↔FIP 关系要能算出来。
7. **CBS 落位语义**：沙箱解包、组件 manifest 复制、文件落地（含 DPX 差分）、硬链接/压缩、注册表事务、
   各类安装器（服务/驱动/任务/计数器/防火墙/AppX 注册）、状态分离告警。
8. **WOF/Compact**、USN journal、Storage Spaces 池与 5 GB 保留空间、挂载点归一化。
9. 忽略但要知道的：`Flash.overlap.txt`（撞车报告，只告警不阻断）、`Falsh.DiagnosticFiles.zip`。

---

## 5. OID 现状对照

| 能力 | OID 现有 | 差距 |
| --- | --- | --- |
| OEMInput / FM 解析与校验 | `Services\FMService`、`OEMInputService`、`OemInputSerializer`、`OemInputSchemaFamilyService`、`bundle` 资源里的 XSD | 基本够用（无 CompDB 视角的 FIP 校验） |
| DeviceLayout 生成/校验/模板 | `DeviceLayoutBuilderService`、`DeviceLayoutService`、`DeviceLayoutRuleChecker`、`DeviceLayoutTemplates`、`guids.csv`(12,624 行) | 缺"从包 CAB 里抽布局 + 校验清单按作用域匹配"这一层（部分在 Components 处理里） |
| 包选择 / 计划 | `cbs-plan-components/deployments/versioned/root/drivers`（ocid_manifest）+ `SkuPlanBuilder`、`SkuAssemblyService`、`DependencyAnalyzer` | 已有"计划"，但产出不是 `ActionList.xml`；缺 Feature↔Package 与 targeting |
| 装包（组件落位） | `OcidServicing`（CSI place、注册表、SD、驱动 INF、DMI、PCM/CDF store 写）+ `csi-place`/`cbs-write` | **缺"在空卷上从零建 store"**；缺 turbostack/DPX 级文件落地；缺 WOF/压缩优化 |
| 建盘/分区/格式化 | 只有 `Build-OcidImage.ps1` 里的 diskpart/dism/bcdboot（外壳脚本） | **无自带实现**（需 VHD API + 分区 + NTFS 格式化） |
| 收尾 | `FinishImageService`（State.ini + imagestate）、`OfflineRegistryService` | 缺 USN journal / GPT ID / 挂载管理器 / Storage Spaces detach |
| AppX | `MobilePackageGen\DotnetPackaging.Msix`（只能**打包** MSIX） | **缺 appxbundle 解析、AppX Staging/Commit、WindowsApps 硬链接/压缩、AppxAllUserStore 注册表** |
| FFU/WIM | `Img2Ffu.Library`（读写 FFU）、`libwim`-类能力（`DismOperations`/`wiminterop`） | 本次目标输出是 VHDX，FFU 是另一条线 |
| 编排 / 日志 / 记账 | `OcidSmiService`、`AddonKitCommandService`、`TaskLogSink` | 缺"一条命令从 OEMInput 到 VHDX"的编排 |

---

## 6. 重写方案（三档，建议按 L0→L1→L2 走）

### L0 — 复刻编排层（保留 UpdateDLL，1~2 周）

自带：OEMInput/FM 解析、DeviceLayout 处理、包选择、建盘、收尾、记账、日志。
**仍调用**：`UpdateDLL.dll!UA_CreateActionList / PrepareUpdate / ExecuteUpdate`（ADK 自带）。

- 产出：一条 `ocid_imagegen` 命令，输入 OEMInput + packages 目录，输出 `.vhdx`；
- 验收：与 imageapp 产物**功能等价**（分区表/GPT GUID/包清单/UpdateHistory 一致；卷内容用 `cbs-scan`/文件哈希抽样对比）。
- 价值：把"最难的一块"隔离出去，先把 80% 的流程变成自己的代码；也为 L1 提供对拍基线。

### L1 — 自带装包（最大工程量，4~8 周）

用 OcidServicing 取代 UpdateDLL：
1. `ActionList.xml` → 自己的 `InstallPlan`（分区、token、intent、payload 路径）；
2. 组件落位：解包（Cabinet.Net）→ 沙箱 → 组件 manifest/内容写 `WinSxS`；
3. 文件落地：硬链接/复制 + 压缩（先不追 DPX，用"参照 WIM 的压缩策略"或直接不压）；
4. 注册表：离线 hive 装（`OfflineRegistryService` + `OcidRegHook`），服务/驱动/任务/计数器/防火墙各自的安装器；
5. store 记账：从零建 COMPONENTS（`CbsStore` 现有写路径要支持"空 store 初始化"，而不是替换）；
6. 收尾：USN/WOF/Compact/GPT/挂载点。

- 验收：镜像能启动（QEMU）+ `dism /Get-Packages` 输出包集一致 + `cbs-scan` 通过；文件级抽样哈希一致（不追逐字节）。
- 风险：**这是最容易翻车的一段**，需要先在小镜像（QEMU BSP，无 AppX）上做到能启动。

### L2 — 全自带（CompDB + AppX + 去 ADK，6~10 周）

- CompDB：算 Feature↔Package↔FIP（`FMService` + `guids.csv` + targeting 规则）；
- AppX：appxbundle/msix 解析（`appxcommon` 反编译可参照）、WindowsApps 布局、硬链接/压缩、`AppxAllUserStore` 注册表；
- 彻底去掉 ADK 依赖（`imageapp.exe`/`UpdateDLL.dll`/`imagestorageservice.dll` 都不再需要）。

> 建议顺序：**L0 先跑通（可与现有 OCID 流程并存）→ 用 L0 的产物做 L1 的对拍基线 → 在 QEMU 小镜像上做 L1 → 最后 L2**。
> 不要一上来就写 CBS 装包，因为一旦 L0 能稳定产出 VHDX，L1 就可以逐包替换、随时回滚。

### 6.1 关键选型（我的建议）

| 决策点 | 建议 | 理由 |
| --- | --- | --- |
| 建盘 API | **VirtDisk（`CreateVirtualDisk`/`AttachVirtualDisk`）+ Win32 分区 API**，封装在 `OcidImaging`（C++）或 C# 服务里 | 托管库（DiscUtils）写 VHDX 时无法做 WOF/Compact/USN/GPT 归一化；这些必须走 OS |
| 是否复刻 Storage Spaces 池 | **复刻**（`CreateStoragePool` 语义） | 10X 镜像的布局就是"池 + thin 磁盘"，`InitializeMinFreeSectors` 等逻辑依赖它 |
| staging 通道 | 自写"解包 → 落盘"，先不做 DPX | DPX 是性能优化，不是语义；可用压缩/hardlink 兜底 |
| 计划来源 | 先用自己的 `cbs-plan-*`，不追 CompDB | 先能让镜像启动；CompDB 留 L2 |
| AppX | 独立模块、可空 | 无 AppX 的 QEMU 镜像能跑通就说明主链正确 |
| 语言/边界 | 编排 + 建盘在 C++（`Source/Native/OcidImaging`），其余复用 C# 服务 | 与 OCID 现有分层一致（native 做真活，C# 做编排） |

---

## 7. 里程碑与验收物

| 里程碑 | 交付 | 验收（对拍物） |
| --- | --- | --- |
| M0 | `ocid_imagegen --plan-only`：OEMInput+FM → 包清单 + DeviceLayout + DriveLetterConfig | 与 `Flash.PackageList.xml`（181）/`Flash.DeviceLayout.xml` 逐字段一致 |
| M1 | 建盘：VHDX + 池 + 7 分区 + 格式化 + GUID | `Get-Disk`/`Get-Partition` 反映的分区表与 GUID 与 §3.8 一致 |
| M2 | L0 编排：调 UpdateDLL 装包 + 收尾 | 镜像能启动；`cbs-scan` 通过；文件抽样一致 |
| M3 | L1 自带装包（QEMU、无 AppX） | 镜像能启动；`dism /Get-Packages` 一致 |
| M4 | L1 + AppX（AMD64 ModernPC） | AppX 包注册表/WindowsApps 与参照一致 |
| M5 | L2 去 ADK | 完全无 ADK 依赖下产出可启动 VHDX |

---

## 8. 风险与"不可复现"清单（先划清界限）

1. **DPX 增量**：文件落地用了二进制差分（728 次 DPX 阶段），完全复刻不现实；只能保证"文件内容一致"，不保证"IO 序列一致"。
2. **turbostack / 硬链接 / WOF 压缩**：会让卷内的物理布局（簇分配）与微软不同 → **逐字节对拍 VHDX 不现实**，
   验收口径应改成"逻辑文件集 + 注册表 + 包状态"一致，外加"能启动"。
3. **AppX 压缩优化**（`2938 compressed files, saved 708 MB`）：同样影响物理布局。
4. **GUID 归一化**：分区/磁盘 GUID 是固定表（可复现），但**卷 GUID/序列号**由系统生成（每次不同），不要追。
5. **时间戳**：`UpdateHistory` 里 `DateTime`、日志时间戳、`CompDB CreatedDate` 天然不同，对拍要排除。
6. **CBS 日志**：不追字节一致（信息量巨大且含时间/随机序）。

---

## 9. 待确认（动手前需要用户拍板）

1. **范围**：先做 QEMU/ModernPC（无 AppX、单一 BSP）还是直接冲完整 AMD64 + AppX？
2. **验收口径**：能启动即可？还是要求"包集/文件集/注册表与参照一致"？还是仍要 VHDX 逐字节（后者基本不可能，见 §8）？
3. **L0 是否可接受**（仍依赖 ADK 的 `UpdateDLL.dll`）作为过渡形态？
4. **入口形态**：并入 `ocid_wssm`（新组，如 `image build`）还是独立 `ocid_imagegen.exe`？
5. 是否需要保留"从 OEMInput 直接产出 FFU"的老路径（`Img2Ffu`）作为并行输出？

---

## 10. DeviceDB（`<Output>.DeviceDB.xml`）生成规则 —— 已解（2026-10-21）

> 依据：`D:\ADK2022Wrokspace\_decomp\compdbcommon\Microsoft.WindowsPhone.CompDB\{DeviceCompDB,PrepCBSFeature}.cs`
> 反编译 + 20279 QEMU 参照（`Falsh.DeviceDB.xml` 134,603 B / 52 Feature / 199 Package / 194 Enumerated）
> + 包内 `update.mum` 实测。

### 10.1 两处特性来源

**① FIP 包（Feature Identifier Package）携带特性信息。**
选中包里 `update.mum` **恰好含一个 `<declareCapability>`** 的包就是 FIP；该片段即特性定义：

```xml
<declareCapability>
  <capability group="Microsoft" fmid="MODERNPC_CORE_OPTIONAL">
    <capabilityIdentity name="Language.Basic.en-us" />
    <featurePackage>
      <package satelliteType="Base" targetPartition="mainos" binaryPartition="false">
        <assemblyIdentity name="Microsoft-WindowsCore-LanguageFeatures-Basic-en-us-Package"
                          processorArchitecture="arm64" publicKeyToken="31bf3856ad364e35"
                          version="10.0.20279.1002" />
      </package>
      <package satelliteType="Base" targetPartition="MainOS" ...>
        <assemblyIdentity name="Microsoft.ModernPC.Language.Basic.en-us.MODERNPC_CORE_OPTIONAL.FIP" ... />
      </package>
    </featurePackage>
  </capability>
</declareCapability>
```

⇒ `Group=capability/@group`、`FMID=capability/@fmid`、`FeatureID=capabilityIdentity/@name`、
`Packages=featurePackage/package/assemblyIdentity/@name`（文档序，**FIP 自身也在其中且排在最后**）；
`satelliteType="Language"` ⇒ 该特性追加 `<DeclareSatellite type="LANGUAGE" value="<lang>"/>` 且末条 Intent 用 `USER`；
依赖来自 FIP 里的 `<dependency><capabilityIdentity .../></dependency>`（渲染成 `<Dependencies><Feature FeatureID FMID Group/></Dependencies>`）。
普通（非 FIP）包的 `update.mum` 里 **没有** `declareCapability`（实测 BcdCoreCore / Sbcp 均为 0）。

**② 每个 FM 一条 `BASE` 特性。**
`FeatureID="BASE"`、`FMID=<FM 的 ID 属性>`（如 `MobileDeviceSpaceFM.xml` 的 `ID="FOSMOBSFM"`）、
`Group=<FM owner>`，`Packages` = 该 FM 的 `<BasePackages>` 里**被选中**的包（保持 FM 文档序）
**+ 该 FM 自己的包 cab 追加在最后**（`Microsoft.ModernPC.MobileDeviceSpaceFM~6284777~arm64~~`）。

### 10.2 顺序与固定字段

- 特性排序 = **`(FMID 升序，FMID 为空者排最后)`，同 FMID 内 `FeatureID` 升序**（`BASE` 因大写 B 自然最前）。
  实测 20279：`FOSMOBSFM, LANGUAGEPACKFM, MODERNPCEMULATOR, MODERNPCNPFM, MODERNPCPSFM, MODERNPC_CORE_OPTIONAL, WCINTFM, WCNPFM, WCOSCDGFM, WCPFM` → 再 `FMID=""` 的 `BASE, MS_KDNETUSB_ON, MS_STATESEPARATION_ON, RELEASE_TEST` → 最后 `BASE-DRIVERSET`（`Group="OEM"`）。
- 每条 `<Feature Type="MobileFeature" ...>` 固定带 `<Token value="INSTALLED"/>` + `<Intent value="PREINSTALL"/>` + `<Intent value="SYSTEM|USER"/>`。
- 根元素：`<CompDB xmlns="http://schemas.microsoft.com/embedded/2004/10/ImageUpdate" CreatedDate=… Revision="1" SchemaVersion="1.0" BuildInfo=… OSVersion=… Type="Device">`（`DeviceCompDB.WriteToFile` 会清空每包的 Payload；反射写序列化前把每个 Package 的 `Payload` 清掉）。
- 顶层 `<Packages>`：每条 `<Package ID="<pak>" KeyForm="<pak><Version>" Partition="<mainos|EFIESP|updateos|BSP>"/>`；
  `Partition` 取自该包 `update.mum` 的 `<package targetPartition="…">`（**大小写原样保留**：`mainos` 与 `EFIESP` 混用）；
  `Version` 取自 `<assemblyIdentity version="…">`。
- `<EnumeratedPackages>`：选中包全集（每条 `<Package ID=…><Token value="INSTALLED"/></Package>`；语言包额外 `<Token value="PROVISIONAL"/>`)。
- `<Languages>` = 卫星语言集合；`<Architectures/>`、`<FailedDiffs/>`、`<DeferredUpdates/>` 空。
- `<ValuePair>` 18 条：`Arch=x86`、`BuildBranch`、`BuildLayer.*`、`BuildLayer.TopLevel`、`CompactOS=1`、`FreeDataStorage=0`、`FreeSystemStorage=0`、`MobileStoreVersion=4`、`OSVersion`、`ProcessorArchitecture=ARM64`、`TotalRam=0`、`TotalStorage=0`、`WinREVersion`（空）—— 由 OEMInput/BuildInfo 推导，待逐条对拍。

### 10.3 待对拍 / 未定项

1. `BuildID`/`BSPBuildID`：源码从包内 `\Windows\ImageFM` 下的 FM 读；参照里 `BuildID` 未出现（`SchemaVersion="1.0"` 时可能省略）。
2. `CreatedDate` 的取法（是否 `DateTime.Now`）。
3. `ValuePair` 逐条来源（尤其 `BuildLayer.*`、`WinREVersion`）。
4. 顶层 `<Packages>` 199 与 `<EnumeratedPackages>` 194 的 5 条差集（需逐条列出：疑似 BSP 驱动 + InboxCompDB 包）。
5. 20274（amd64）参照需同步验证（55 Feature / 181 包）。
## §11 输入校验：XSD 资产与已知缺口（2026-10-21）

**资产位置（已入库）**：`Schemas\adk\` 28 个 XSD（提交 `5eb2c7a`）+ 仓库原有 `Schemas\*.xsd` 11 个。
来源是 ADK 托管程序集的**嵌入资源**，不是磁盘文件（ADK 目录里没有独立 .xsd）：
`featureapi.dll`（FeatureManifest/OEMInput V1+V2/UpdateOSInput/UpdateOSOutput/UpdateHistory/Appx.FMAddOnAppX/Drivers.FMAddOnDrivers…）、
`imagecommon.dll`（DeviceLayout*/OEMDevicePlatform/FMCollection/KML…）、`compdbcommon.dll`（DeviceCompDB/BuildCompDB/BSPCompDB/UpdateCompDB…）、
`imagestorageservicemanaged.dll`（BcdLayout）。
**提取方法**（零依赖、可复现，务必用 **32 位** PowerShell；64 位宿主 `LoadFile` 报 `0x800700C1`）：
`C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe` +
`[Reflection.Assembly]::LoadFile($dll)` → `GetManifestResourceNames()` → `GetManifestResourceStream($n)` → 写盘。

**发布链**：`build-imaging.ps1` 把 `Schemas\*.xsd` 复制到 `Output\Imaging\schemas\`、`Schemas\adk\*.xsd` 复制到 `Output\Imaging\schemas\adk\`；
`ocid_imagegen` 的 `FindSchema` 依次在 `<exe>\schemas`、`<exe>\..\schemas`、源码树 `Schemas` 及其 `adk` 子目录里找。

**校验引擎**：系统 MSXML6 + 纯 IDispatch 晚绑定（`#import <msxml6.dll>` 与 windows.h 的 `msxml.h` 撞类型重定义，故不用 #import）。
两个必须绕开的 MSXML 行为：
1. `schemaCache.add(ns, "<文件路径>")` 在本进程恒 `E_ACCESSDENIED`；必须先读文本 → `loadXML` → `add(ns, VT_DISPATCH)`。
2. **同一命名空间只能 add 一次**，而 ADK 的 `FeatureManifest.xsd` 引用的 `AppXType` 声明在同族另一文件里（多文件同命名空间 schema 集）。
   实测 `xsd:include` 包装文档 add 成功但**校验仍报"元素未声明"**（MSXML 不处理 include），因此改为**文本级拼接**：
   取每个 XSD 根标签的内部内容接到第一个文件的根标签下（三个文件根标签属性完全一致），再 `add` 一次。
   FM 校验因此传入 `FeatureManifest.xsd + Appx.FMAddOnAppX.xsd + Drivers.FMAddOnDrivers.xsd`。

**已知缺口（未处理）**：10x（build ≥ 20000）的 OEMInput 变体 XSD 不可得 —— 本机 ADK 只到 22621，
10x 的 `featureapi.dll` 也没随包集提供。基线 `OEMInput.V2.xsd` 会对真机 10x OEMInput 报
`0xC00CE011`（`<AppXOptionalPackages/>` 空容器不允许），属 schema 变体差异。
`ocid_imagegen plan` 在 build 未知或 ≥ 20000 时把这一条降级为**提示**并继续（FM 仍逐份严格校验），
其余校验错误照常失败。拿到 10x 的 `featureapi.dll` 后应抽其 OEMInput XSD 补进 `Schemas\adk\` 并去掉该降级。