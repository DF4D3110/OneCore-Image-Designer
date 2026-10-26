# OcidImaging 多阶段（FE / RS5）说明

本文件记录"同一套代码按版本分支产出两套模块"的约定、当前状态与 RS5 还缺什么。
任何"为什么这里要分两个 exe / 两个 DLL 后缀"的问题都以本文件为准。

## 1. 目录与命名

```
Source/Native/
  build-imaging2.ps1                 # 一次构建两个阶段 + 总控入口
  OcidImaging/
    include/ocid/imaging/Stage.h     # 阶段描述（纯数据）
    src/Stage.cpp                    # /DOCID_STAGE_FE 或 /DOCID_STAGE_RS5 编两次
    appx/OcidAppX.cs                 # FE 的 AppX helper（托管 Windows Kits\10\...\i386）
    appx/OcidAppX.Rs5.cs             # RS5 的 AppX helper（托管 ADK2022Wrokspace\RS5\bin\i386）
    tools/imagegen.cpp               # 主程序（两阶段同一份源码）
    tools/dispatch.cpp               # 总控入口 ocid_imagegen.all.exe

Output/Imaging3/
  fe/ocid_imagegen.exe               # 只链接 *.fe.wcos.dll
  fe/ocid_{stage,common,plan,cab,vhd,pool,update,ffu,appx}.fe.wcos.dll
  fe/appx/OcidAppX.exe + RS5 无关的 FE ADK i386 工具集
  rs5/ocid_imagegen.rs5.exe          # 只链接 *.rs5.wcos.dll
  rs5/ocid_{...}.rs5.wcos.dll
  rs5/appx/OcidAppX.exe + RS5 ADK i386 工具集
  ocid_imagegen.all.exe              # 总控：按 --stage 转发到上面两个 exe
```

命名规则（用户要求）：基于 20348 分支的产物全带 `.fe.wcos.dll`，17763（RS5）全带
`.rs5.wcos.dll`；出问题时看"哪个 DLL 名"就知道是哪个阶段。

## 2. ADK 绝不混用（硬约束）

`Stage.cpp` 里每个阶段写死自己的 ADK 根：

| 阶段 | ADK 根 | 工具集位置 |
| --- | --- | --- |
| fe  | `D:\ADK2022Wrokspace`          | `Windows Kits\10\Tools\bin\i386` |
| rs5 | `D:\ADK2022Wrokspace\RS5`      | `bin\i386` |

三层保护：

1. `StageApplyEnvironment()` 在每个 exe 启动时把本阶段根写入 `OCID_ADK_ROOT`；
   如果环境里已有**别的**根或 `OCID_STAGE` 指向别的阶段，直接拒绝运行（退出码 2）。
2. `Pool.cpp` 的候选列表在 `OCID_ADK_ROOT` 存在时只保留**该根之下**的路径，
   不再回落到注册表 `KitsRoot10`、`Program Files`、exe 同级目录——
   否则 RS5 阶段会静默加载 FE 的同名 DLL（实测过：probe 打出了 FE 路径）。
3. 每个阶段的 `appx\` 目录里放的是**本阶段** ADK 的完整 i386 工具集，
   `OcidAppX.exe` 也用本阶段程序集编译（两个 AppX API 不同，见 §4）。

## 3. 当前状态（2026-10-26）

| 命令 | fe | rs5 | 说明 |
| --- | --- | --- | --- |
| `stage`（阶段自检） | ✅ | ✅ | 打印阶段信息 + 拒绝跨阶段 |
| `schema` / `cab` / `layout` / `devicedb` / `plan` | ✅ | 未验证 | 与 ADK 无关或只读 XML |
| `vhd *` / `disk build` | ✅ | 未验证 | 我们自己的 VirtDisk + GPT 实现 |
| `ffu pack/info/manifest/verify` | ✅ | 未验证 | 我们自己的 FFU 容器（manifest 2.0 两阶段通用） |
| `appx collect` | ✅ | ✅ | RS5 实测：`E:\DriveD\17705\OUTPUT\package`（FM 11 个，看过 83 条 PackageFile，收 12 个包 / 10 framework，跳过架构 37、Optional 34） |
| `appx install` | ✅ | 待联调 | 需要 `pool mount` 才能跑（RS5 pool 未实现） |
| `pool probe` / `pool exports` | ✅ | ✅ | FE `导出命中 22/缺失 0`；RS5 `导出命中 49/缺失 0`（全部 stdcall 装饰名 `_Xxx@N`） |
| `pool build/prepare/mount/detach/finalize` | ✅ | ❌ 明确报未实现 | `src/PoolRs5.cpp` 里 `NotImplement()` 给出指向 §5 的错误信息，绝不假装成功 |
| `apply` / `image build` | ✅ | ❌ | 依赖上面的 RS5 pool |

## 4. FE 与 RS5 的 AppX API 差异（已在 helper 里分叉）

`appximaging.dll` 的托管入口不同：

| 项 | FE (`Windows Kits\10`) | RS5 (`ADK2022Wrokspace\RS5`) |
| --- | --- | --- |
| 架构枚举 | `Microsoft.Composition.ToolBox.CpuArch` | `Microsoft.WindowsPhone.ImageUpdate.PkgCommon.CpuId` |
| 所有者枚举 | `Composition.ToolBox.PhoneOwnerType` | `PkgCommon.OwnerType` |
| Stage | `StageAppXPackages(logger, list, stagingRoot, arch, langs)` | `StageAppXPackages(logger, list, msPackageRoot, stagingRoot, cpu, langs, resolutions)` |
| Commit | `CommitAppXPackages(logger, list, stagingRoot, preInstalled, arch, langs)` | `CommitAppXPackages(logger, list, msPackageRoot, stagingRoot, preInstalled, cpu, langs, resolutions)` |
| `AppXPkgFile` 字段 | 有 `PreferStub`/`Region` | 没有这两个字段 |

⇒ 两份源码：`appx/OcidAppX.cs`（FE）与 `appx/OcidAppX.Rs5.cs`（RS5），构建脚本按阶段选。

## 5. RS5 还缺什么：legacy space API 实现（`PoolRs5.cpp`）

RS5 的 `imagestorageservice.dll`（294,912 B，**82 个导出全部是 stdcall 装饰名**）与 FE
（403,456 B，68 个导出，cdecl 名字）不是同一套 ABI：

- FE 有：`AttachStorageSpace` / `DetachStorageSpace` / `OpenStoragePool` / `CloseStoragePool` /
  `GetStoragePool` / `GetStorageSpaces` / `GetPartitionFileSystem2` / `IsWCOSPartition` …
- RS5 **没有**上面这些；它走：
  `CreateImageStorageService@16`、`CreateStoragePool@16`、`CreateStorageSpace@40`、
  `GetStorageSpace@32`、`SetStorageSpaceName@24`、`RemoveStorageSpace@20`、`RemoveStoragePool@20`、
  `GetPoolIdFromPoolName@12`、`AddDriveToStoragePool@24`、`AssignMountPoints@36`、
  `FormatPartition@36`、`GetPartitionPath@40`、`GetPartitionNames@?`、`WriteMountManagerRegistry@24`、
  `WriteVolumeMountPoints@20`、`AttachWOFToVolume@8`、`AttachToMountedImage@20`、`ReauctionDisk@8`。

两点必须先处理：

1. **装饰名寻址**：`GetProcAddress(h, "CreateStorageSpace")` 在 RS5 上必然失败，
   要按 `_CreateStorageSpace@40` 形式取；建议加一层 `PoolGetProc(h, "CreateStorageSpace")`
   在 RS5 下自动拼 `@N`（N 来自参数字节数，逐个函数写死表）。
2. **调用顺序**（来自 `_decomp_rs5\imagestorageservicemanaged\Microsoft.WindowsPhone.Imaging\ImageStorageManager.cs`
   与 `ImageStorage.cs`）：`CreateImageStorageService` → 对物理盘 `CreateStoragePool` +
   `AddDriveToStoragePool`（或 `CreateStorageSpace` 直接给 `diskNumber/partitionNumber`）→
   `GetPoolIdFromPoolName` → 逐 space `CreateStorageSpace(...40 字节参数包...)` →
   `AssignMountPoints` → 写盘用 `GetPartitionPath`+文件 API → 收尾 `RemoveStorageSpace`/`RemoveStoragePool`
   → `CloseImageStorageService`，最后 `WriteMountManagerRegistry` 落盘注册表。

反编译源码位置（已导出，供实现时对照）：
`E:\ocid_out\_decomp_rs5\{imagestorageservicemanaged,appximaging,imagecommon,toolscommon,featureapi,pkgcommonmanaged,appxcommon}\`
（RS5 ADK 不带 PDB，托管程序集本身带完整 IL，反编译即为权威实现。）

## 6. 怎么加一个新阶段

1. `src/Stage.cpp` 的 `kStageTable` 加一行（name / ADK 根 / 默认 OSVersion / 架构 / poolApi）。
2. `build-imaging2.ps1` 的 `$stages` 加一行（name / exe 名 / 宏 `/DOCID_STAGE_<X>` / ADK i386 路径 / helper 源）。
3. 需要分叉的模块用 `#if defined(OCID_STAGE_<X>)` 或按阶段换源文件（如 pool）。
4. 构建：`pwsh -File build-imaging2.ps1 -OutName <名>`（两阶段）或 `-Stage <名>`。
