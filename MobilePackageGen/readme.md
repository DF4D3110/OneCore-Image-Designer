# MobilePackageGen - OneCore Image Designer Preview

基于 [WSK Tools](https://github.com/gus33000/WSK) v1.0.4 与上游 [MobileTooling/MobilePackageGen](https://github.com/MobileTooling/MobilePackageGen) 独立重构并深度改造的移动设备包工具链。用于从 FFU / VHDX / VHD / 裸文件系统镜像 / 拆散存储池磁盘阵列中提取 CBS、SPKG、Driver、Appx 包，并按 Feature Manifest（FM）重建 / 校验 / 摆放包结构，为 imageapp 构建 WCOS / OneCore / Andromeda 镜像（如 17705 Flash.ffu）提供输入。

GUI 产品名：**OneCore Image Designer Preview**。

## 项目结构

```
MobilePackageGen/
├── MobilePackageGen.sln                         解决方案文件
├── publish_win_x64.cmd / publish_win_arm64.cmd  发布脚本
├── src/                                         第一方代码
│   ├── MobilePackageGen.CLI/                    命令行版本 (net8.0)
│   ├── MobilePackageGen.GUI/                    GUI 版本 (net8.0-windows, WinForms)
│   ├── MobilePackageGen.Common/                 公共核心库（含 CatBuilder、NSudo 提权、TrustedInstallerHelper）
│   ├── MobilePackageGen.Adapters/               镜像读取适配器（FFU/VHDX/WIM/目录/裸FS/存储池）
│   │   ├── Adapters/FileSystemImage/            （新增）裸 NTFS/FAT 文件系统镜像适配器
│   │   ├── Adapters/StorageSpace/               （新增）存储池磁盘阵列适配器
│   │   ├── GPT/                                  GPT 解析（含边界修复）
│   │   └── Adapters/DiskCommon.cs                多磁盘阵列合并读取
│   ├── WPMT.Core/                               （新增）自 WPMT 迁移的压缩/哈希/目录库
│   ├── Archives.DiscUtils/                      DiscUtils 扩展
│   ├── Img2Ffu.Library/                         FFU 镜像读写库（本地版）
│   ├── Img2Ffu.Library.Compression.Windows/     Windows 压缩实现
│   ├── StorageSpace/                            OSPool/Storage Spaces 解析库（本地版）
│   ├── LibSxS/                                  SxS Delta 清单解析库
│   ├── Microsoft.Deployment.Compression/        CAB 压缩基础库 (来自 WiX)
│   └── Microsoft.Deployment.Compression.Cab/    CAB 压缩实现
└── thirdparty/                                  第三方库（子模块声明保留，见 .gitmodules）
    ├── DotnetPackaging.Msix/                    MSIX/Appx 打包库
    ├── DeflateBlockCompressor/                  Deflate 块压缩库
    ├── StorageSpace/                            子模块（上游引用）
    ├── Img2Ffu/                                 子模块（上游引用）
    └── EDLProgram2VHDX/                         子模块（上游引用）
```

## 编译方法

### 命令行版本

```bash
dotnet build src/MobilePackageGen.CLI/MobilePackageGen.csproj -c Release
```

### GUI 版本

```bash
dotnet build src/MobilePackageGen.GUI/MobilePackageGen.GUI.csproj -c Release
```

### 发布 (self-contained, 免安装运行库)

```bash
# amd64
dotnet publish src/MobilePackageGen.CLI/MobilePackageGen.csproj -c Release -r win-x64 --self-contained true -o output/amd64
dotnet publish src/MobilePackageGen.GUI/MobilePackageGen.GUI.csproj -c Release -r win-x64 --self-contained true -o output/amd64

# x86
dotnet publish src/MobilePackageGen.CLI/MobilePackageGen.csproj -c Release -r win-x86 --self-contained true -o output/x86
dotnet publish src/MobilePackageGen.GUI/MobilePackageGen.GUI.csproj -c Release -r win-x86 --self-contained true -o output/x86

# arm64
dotnet publish src/MobilePackageGen.CLI/MobilePackageGen.csproj -c Release -r win-arm64 --self-contained true -o output/arm64
dotnet publish src/MobilePackageGen.GUI/MobilePackageGen.GUI.csproj -c Release -r win-arm64 --self-contained true -o output/arm64
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
| LTRData.DiscUtils | [LTRData/DiscUtils](https://github.com/LTRData/DiscUtils)（NuGet，升级版） | MIT | NuGet 包 |
| WPMT.Core（压缩/哈希/目录库） | 自 WPMT（Windows Package Management Tool）迁移，以 `D:\wpmtcli\Compression` 修正版为权威 | - | `src/WPMT.Core` |
| NSudo（TrustedInstaller 提权） | [UUP-Project/NSudo](https://github.com/UUP-Project/NSudo) | MIT | `src/MobilePackageGen.Common/NSudo` |
| msdelta.dll（DCM 压缩 API） | Windows 系统 DLL | 系统组件 | 运行期调用 |
| Img2Ffu | [WOA-Project/Img2Ffu](https://github.com/WOA-Project/Img2Ffu) | MIT | `src/Img2Ffu.Library`（本地版，含压缩实现） |
| StorageSpace | [gus33000/StorageSpace](https://github.com/gus33000/StorageSpace) | MIT | `src/StorageSpace`（本地版）；`thirdparty/StorageSpace` 保留子模块声明 |
| DotnetPackaging.Msix | [dotnet-packaging/MSIX](https://github.com/dotnet-packaging/MSIX) | MIT | `thirdparty/DotnetPackaging.Msix` |
| DeflateBlockCompressor | 随 WSK Tools 分发 | - | `thirdparty/DeflateBlockCompressor` |
| Microsoft.Deployment.Compression | WiX 工具集 | MS-RL | `src/Microsoft.Deployment.Compression(.Cab)` |
| LibSxS | 随 WSK Tools 分发 | - | `src/LibSxS` |
| SevenZipExtractor / 7-Zip | [adoconnection/SevenZipExtractor](https://github.com/adoconnection/SevenZipExtractor) | LGPL-2.1 | `src/Archives.DiscUtils/SevenZipExtractor` |
| RawDiskLib | NuGet 0.2.1 | - | NuGet 包 |
| EDLProgram2VHDX | [gus33000/EDLProgram2VHDX](https://github.com/gus33000/EDLProgram2VHDX) | - | `thirdparty/EDLProgram2VHDX`（子模块声明） |

> **子模块说明**：`thirdparty/StorageSpace`、`thirdparty/Img2Ffu`、`thirdparty/EDLProgram2VHDX` 在 `.gitmodules` 中保留了上游的子模块引用声明（fork 结构沿用上游）。本仓库**实际编译使用的是 `src/` 下的本地代码**（StorageSpace、Img2Ffu.Library），克隆后**不需要**初始化子模块即可直接编译。
>
> **WPMT.Core 迁移注意**：`src/WPMT.Core` 的压缩逻辑以 WPMT 的 `Compression` 目录修正版为权威；`parital source code` 目录中部分文件存在已知问题（如共享静态字典被原地修改导致连续解压第二个 DCM manifest 损坏，需每次使用前深拷贝字典）。详见 `src/WPMT.Core` 内实现。

## 与上游 fork 的主要差异

- 代码结构重组为 `src/` + `thirdparty/` 布局（保留上游组织方式）
- 新增 `MobilePackageGen.GUI`（WinForms 图形界面，产品名 OneCore Image Designer Preview）与 `MobilePackageGen.CLI` 分离
- `LTRData.DiscUtils` 升级（修复 WOF/XPRESS 压缩文件读取导致的数组越界崩溃）
- **多磁盘阵列读取**：支持存储池被拆散成多个 VHD/分区镜像的场景，跨分区剥离分区链接前缀（superfloppy 回退）合并提取，防止单分区文件缺失
- **裸文件系统镜像支持**：新增 `FileSystemImage` 适配器，可直接读取无分区表的 NTFS/FAT 卷 dump
- **GPT 解析修复**：`GPT.cs` 边界检查修复（Bad GPT 崩溃）
- **WPMT 压缩/哈希/目录能力迁移**：DCM manifest 压缩/解压、cat 重建（CDF + 签名）、文件名/哈希算法
- **CAB 输出选项**：是否使用 DCM 压缩（两选项），满足 imageapp 构建要求
- **包校验日志**：全程输出所有包（含缺失包）的日志，便于与 FM 核对
- **FM 结构摆放**：Appx/CAB 按 FM 定义输出到对应架构目录，文件名与 FM 对齐
- **NSudo 提权集成**：GUI 默认以最高权限（TrustedInstaller）运行
- 镜像加载支持目录（RealFileSystem）方式，绕过 WOF 解码路径

## 版本信息

- 来源: WSK Tools v1.0.4（fork 自 MobileTooling/MobilePackageGen）
- 导出日期: 2026-08-30（后续持续改造至 2026-10）
- 项目数: 15（src/ 13 + thirdparty/ 2）
- CLI 目标框架: net8.0
- GUI 目标框架: net8.0-windows (WinForms)
