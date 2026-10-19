# MobilePackageGen - OneCore Image Designer Preview

基于 [WSK Tools](https://github.com/gus33000/WSK) v1.0.4 与上游 [MobileTooling/MobilePackageGen](https://github.com/MobileTooling/MobilePackageGen) 独立重构并深度改造的移动设备包工具链。用于从 FFU / VHDX / VHD / 裸文件系统镜像 / 拆散存储池磁盘阵列中提取 CBS、SPKG、Driver、Appx 包，并按 Feature Manifest（FM）重建 / 校验 / 摆放包结构，为 imageapp 构建 WCOS / OneCore / Andromeda 镜像（如 17705 Flash.ffu）提供输入。

GUI 产品名：**OneCore Image Designer Preview**。

## 项目结构

```
MobilePackageGen/
├── MobilePackageGen.sln                         解决方案文件
├── publish_cli_win-{x64,x86,arm64}.cmd          CLI 发布脚本
├── publish_win_{x64,arm64}.cmd                  GUI 发布脚本
├── MobilePackageGen.CLI/                        命令行版本 (net8.0)
├── MobilePackageGen.GUI/                        GUI 版本 (net8.0-windows, WinForms)
├── MobilePackageGen.Common/                     公共核心库（含 CatBuilder、NSudo 提权、TrustedInstallerHelper）
├── MobilePackageGen.Adapters/                   镜像读取适配器（FFU/VHDX/WIM/目录/裸FS/存储池）
│   ├── Adapters/FileSystemImage/                裸 NTFS/FAT 文件系统镜像适配器
│   ├── Adapters/StorageSpace/                   存储池磁盘阵列适配器
│   ├── GPT/                                     GPT 解析（含边界修复）
│   └── Adapters/DiskCommon.cs                   多磁盘阵列合并读取
├── CbsTools/                                    CBS 包工具库
├── CbsxCli/                                     CBS 控制台工具（OneCore Image Designer 的 CBS 组件）
├── SxSFounderCli/                               SxS 源组装控制台工具
├── WPMT.Core/                                   自 WPMT 迁移的压缩/哈希/目录库
├── Cabinet/                                     CAB 压缩库 (Cabinet.Net.csproj)
├── Archives.DiscUtils/                          DiscUtils 扩展（含 SevenZipExtractor）
├── Img2Ffu.Library/                             FFU 镜像读写库（本地版）
├── Img2Ffu.Library.Compression.Windows/         Windows 压缩实现
├── StorageSpace/                                OSPool/Storage Spaces 解析库（本地版）
├── LibSxS/                                      SxS Delta 清单解析库
├── DeflateBlockCompressor/                      Deflate 块压缩库
├── DotnetPackaging.Msix/                        MSIX/Appx 打包库
├── Microsoft.Deployment.Compression/            CAB 压缩基础库 (来自 WiX)
└── Microsoft.Deployment.Compression.Cab/        CAB 压缩实现

所有第三方代码都**平铺**在 `MobilePackageGen/` 下（没有 `src/` 与 `thirdparty/` 二级目录，
也没有 Git 子模块）：克隆后不需要初始化任何子模块即可直接编译。
```

## 编译方法

### 命令行版本

```bash
dotnet build MobilePackageGen.CLI/MobilePackageGen.csproj -c Release
```

### GUI 版本

```bash
dotnet build MobilePackageGen.GUI/MobilePackageGen.GUI.csproj -c Release
```

### 发布 (self-contained, 免安装运行库)

```bash
# amd64
dotnet publish MobilePackageGen.CLI/MobilePackageGen.csproj -c Release -r win-x64 --self-contained true -o output/amd64
dotnet publish MobilePackageGen.GUI/MobilePackageGen.GUI.csproj -c Release -r win-x64 --self-contained true -o output/amd64

# x86
dotnet publish MobilePackageGen.CLI/MobilePackageGen.csproj -c Release -r win-x86 --self-contained true -o output/x86
dotnet publish MobilePackageGen.GUI/MobilePackageGen.GUI.csproj -c Release -r win-x86 --self-contained true -o output/x86

# arm64
dotnet publish MobilePackageGen.CLI/MobilePackageGen.csproj -c Release -r win-arm64 --self-contained true -o output/arm64
dotnet publish MobilePackageGen.GUI/MobilePackageGen.GUI.csproj -c Release -r win-arm64 --self-contained true -o output/arm64
```

## 使用方法

### 命令行版本

```bash
# 从分区/目录提取 CBS 包（支持一次传入多个磁盘/分区）
MobilePackageGen.exe <Path to MainOS/Data/EFIESP...> <Output folder CBSs>
# 示例: MobilePackageGen.exe D: E: F: C:\OutputCabs

# 从 FFU / VHDX / VHD / 裸 NTFS/FAT 镜像 / 存储池磁盘阵列提取
MobilePackageGen.exe <Path to image file(s)> <Output folder CBSs>
```

> 注意：部分功能需要以 Trusted Installer (TI) 权限运行。GUI 版本已集成 NSudo 自动提权；CLI 版本请以管理员/最高权限运行。

### GUI 版本

直接运行 `MobilePackageGen.GUI.exe`（OneCore Image Designer Preview）：
- 支持多选镜像/磁盘文件（多磁盘阵列一次导入）
- 包列表展示（含 Appx 与 CAB，按 FM 结构摆放输出）
- 全程日志输出到目标目录，缺失包也会在日志中说明
- 自动以最高权限运行（NSudo 提权）

## 第三方组件与来源

| 组件 | 来源 | 许可证 | 本仓库位置 |
|---|---|---|---|
| **MobilePackageGen**（上游） | [MobileTooling/MobilePackageGen](https://github.com/MobileTooling/MobilePackageGen) | MIT | 本目录是它的 fork（[DF4D3110/MobilePackageGen](https://github.com/DF4D3110/MobilePackageGen)）随产品内置的副本 |
| **WSK Tools** v1.0.4 | [gus33000/WSK](https://github.com/gus33000/WSK) | MIT | 该 fork 的原始基线（目录布局与部分实现来自它） |
| **NSudo**（TrustedInstaller 提权） | 原始项目 [M2TeamArchived/NSudo](https://github.com/M2TeamArchived/NSudo)（M2-Team / Mouri_Naruto，已归档）；编译 `NSudoAPI.dll` 用的是 [UUP-Project/NSudo](https://github.com/UUP-Project/NSudo) 分支 | MIT | `MobilePackageGen.Common/NSudo`（C# 绑定 `M2.NSudo`）；`NSudoAPI.dll` 本身不随仓库分发，需自行编译 |
| **LTRData.DiscUtils**（Vhd / Vhdx / Wim / Ntfs / FileSystems / Streams / Registry，1.0.85 与 1.0.55） | [LTRData/DiscUtils](https://github.com/LTRData/DiscUtils) | MIT | NuGet 包 |
| **RawDiskLib** 0.2.1 | [LTRData/RawDiskLib](https://github.com/LTRData/RawDiskLib) | 见包内许可 | NuGet 包 |
| **System.Reactive** 6.0.1 | [dotnet/reactive](https://github.com/dotnet/reactive) | MIT | NuGet 包（`DeflateBlockCompressor`） |
| **ZLibDotNet** 0.1.1 | [NN---/ZLibDotNet](https://github.com/NN---/ZLibDotNet) | MIT | NuGet 包（`DeflateBlockCompressor`） |
| **System.IO.Hashing** 8.0.0 / 10.0.0 | [dotnet/runtime](https://github.com/dotnet/runtime) | MIT | NuGet 包 |
| **Zafiro.DivineBytes** 41.2.10 | [SuperJMN/Zafiro](https://github.com/SuperJMN/Zafiro) | MIT | NuGet 包（`DotnetPackaging.Msix`） |
| **System.Net.Http / System.Private.Uri / System.Text.RegularExpressions** | [dotnet/runtime](https://github.com/dotnet/runtime) | MIT | NuGet 包（`Adapters` 的兼容引用） |
| **DotnetPackaging.Msix** | [dotnet-packaging/MSIX](https://github.com/dotnet-packaging/MSIX) | MIT | `DotnetPackaging.Msix` |
| **DeflateBlockCompressor** | 随 [WSK Tools](https://github.com/gus33000/WSK) 分发 | 见包内许可 | `DeflateBlockCompressor` |
| **Img2Ffu** | [WOA-Project/Img2Ffu](https://github.com/WOA-Project/Img2Ffu) | MIT | `Img2Ffu.Library`（本地版，含压缩实现） |
| **StorageSpace** | [gus33000/StorageSpace](https://github.com/gus33000/StorageSpace) | MIT | `StorageSpace`（本地版） |
| **EDLProgram2VHDX** | [gus33000/EDLProgram2VHDX](https://github.com/gus33000/EDLProgram2VHDX) | 见项目内许可 | 上游 fork 里的子模块；本目录**不包含**，也未引用 |
| **LibSxS** | 随 [WSK Tools](https://github.com/gus33000/WSK) 分发 | 见源码内说明 | `LibSxS` |
| **Cabinet**（容器读取） | 随 UUPMediaCreator / WPMT 分发 | `Cabinet/LICENSE`（MIT） | `Cabinet` —— 只保留 cab 容器（头、folder、`CFDATA`、文件落位）的读取；MSZIP 的 DEFLATE 与 LZX 解码由项目自有的 `Source/Managed/OcidCabinet/` 提供，本目录不再随附 XNA `LzxDecoder`，也不再引用 SharpZipLib |
| **Microsoft.Deployment.Compression(.Cab)** | WiX 工具集 | MS-RL | `Microsoft.Deployment.Compression(.Cab)` |
| **SevenZipExtractor / 7-Zip** | [adoconnection/SevenZipExtractor](https://github.com/adoconnection/SevenZipExtractor) | LGPL-2.1 | `Archives.DiscUtils/SevenZipExtractor` |
| **WPMT.Core**（压缩 / 哈希 / 目录库） | 自 WPMT（Windows Package Management Tool）迁移 | 见源码内说明 | `WPMT.Core` |
| **msdelta.dll**（DCM 压缩 API） | Windows 系统组件 | 系统组件 | 运行期从系统加载，不分发 |

> **没有子模块**：上游 fork 曾在 `.gitmodules` 里声明 `thirdparty/StorageSpace`、`thirdparty/Img2Ffu`、`thirdparty/EDLProgram2VHDX`，但本仓库实际编译使用的是平铺在 `MobilePackageGen/` 下的本地代码（`StorageSpace`、`Img2Ffu.Library`）。该失效的 `.gitmodules` 与其子模块声明已删除，克隆后直接编译即可。
>
> **WPMT.Core 迁移注意**：`WPMT.Core` 的压缩逻辑以 WPMT 的 `Compression` 目录修正版为权威；`parital source code` 目录中部分文件存在已知问题（如共享静态字典被原地修改导致连续解压第二个 DCM manifest 损坏，需每次使用前深拷贝字典）。详见 `WPMT.Core` 内实现。

## 与上游 fork 的主要差异

- 代码结构平铺在 `MobilePackageGen/` 下（第三方库与第一方代码并列，不再用 `src/` + `thirdparty/` 二级目录，也移除了失效的子模块声明）
- 新增 `MobilePackageGen.GUI`（WinForms 图形界面，产品名 OneCore Image Designer Preview）与 `MobilePackageGen.CLI` 分离
- `LTRData.DiscUtils` 升级（修复 WOF/XPRESS 压缩文件读取导致的数组越界崩溃）
- **多磁盘阵列读取**：支持存储池被拆散成多个 VHD/分区镜像的场景，跨分区剥离分区链接前缀（superfloppy 回退）合并提取，防止单分区文件缺失
- **裸文件系统镜像支持**：新增 `FileSystemImage` 适配器，可直接读取无分区表的 NTFS/FAT 卷 dump
- **GPT 解析修复**：`GPT.cs` 边界检查修复（Bad GPT 崩溃）
- **WPMT 压缩/哈希/目录能力迁移**：DCM manifest 压缩/解压、cat 重建（CDF + 签名）、文件名/哈希算法
- **CAB 解压改为自有实现**：`Cabinet` 只保留容器读取，MSZIP（DEFLATE）与 LZX 解码换成 `Source/Managed/OcidCabinet/`；不再引用 SharpZipLib，也不再随附 XNA `LzxDecoder`（对 `expand.exe` 逐字节对拍通过）
- **CAB 输出选项**：是否使用 DCM 压缩（两选项），满足 imageapp 构建要求
- **包校验日志**：全程输出所有包（含缺失包）的日志，便于与 FM 核对
- **FM 结构摆放**：Appx/CAB 按 FM 定义输出到对应架构目录，文件名与 FM 对齐
- **NSudo 提权集成**：GUI 默认以最高权限（TrustedInstaller）运行
- 镜像加载支持目录（RealFileSystem）方式，绕过 WOF 解码路径

## 版本信息

- 来源: WSK Tools v1.0.4（fork 自 MobileTooling/MobilePackageGen）
- 导出日期: 2026-08-30（后续持续改造至 2026-10）
- 项目数: 解决方案含 16 个项目，另有 `CbsTools`、`CbsxCli`、`SxSFounderCli`、`Cabinet` 四个独立工程
- CLI 目标框架: net8.0
- GUI 目标框架: net8.0-windows (WinForms)
