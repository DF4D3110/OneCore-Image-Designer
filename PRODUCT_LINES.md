# 产品线边界（Desktop / WCOS-Phone）

本仓库同时承载两条**互不通用**的产品线。二者的词汇、机制与产物都不同，
**不要互相引用**。

---

## 一、两条线

| | **Desktop（CBS）** | **WCOS / Phone** |
|---|---|---|
| **目标系统** | 普通 Windows（Client / Server SKU） | WCOS · Windows Phone 类系统 |
| **服务机制** | CBS 离线服务（`ICbsSession`）· DISM | imageapp · Img2Ffu · FFU |
| **存储形态** | `WinSxS` + `servicing\Packages` | 分区映像 + FeatureManifest + OEMInput + DeviceLayout |
| **SKU 概念** | `Microsoft-Windows-Editions-*` 包 | FM 的 feature 组合 |
| **产物** | WIM / 目录树 | **FFU** |
| **典型工具** | `dism` · `cbsx` | `imageapp` · `Img2Ffu` · `MobilePackageGen` |
| **服务栈版本体系** | `6.x` 可原生服务；`10.0+` 走 DISM | 另一套（未涉及） |

**另外两条独立线**（同样不要混进来）：`Services\AddonKit*`（IoTCore）、
`ThirdParty\UUPMediaCreator`（UUP）。

---

## 二、文件归属

### Desktop（CBS）
```
Services\  CbsxService · CbsSafety · DismServicingService · ServicingRouter
           SystemAssemblyService · UpdatePackageService · RepairContentService
           SxSFounderService · ExplodeSxSNativeService · FounderStackInstaller
           ArchRelay · PrivilegeHelper · WinSxSHashCalculator · PureSxsService · CbsToRegService
Views\     Cbs\CbsBuildView · SxsView
ViewModels\Cbs\CbsBuildViewModel · SxsViewModel
CLI        MobilePackageGen\CbsxCli · MobilePackageGen\SxSFounderCli
```

### WCOS / Phone
```
MobilePackageGen\*                 （Img2Ffu · MobilePackageGen.Common · Adapters · GUI · CbsTools）
Services\  DeviceLayout* · OEMInputService · OemInput* · FMService
           FirmwareService · ImageFileReader · DriverPackageBuilder · ComponentExtractor
Models\    DeviceLayout\* · FM\* · OEMInputModels · MobilePackageGen\*
Views\     DeviceLayout\* · OEMInput\* · FFUBrowser\* · SystemReconstruct\*
ViewModels\ DeviceLayout\* · OEMInput\* · FFUBrowser\* · SystemReconstruct\*
```

### 刻意保持中立的组件
```
Services\CbsBuildService       按 update.mum 把包 CAB 展开成目录树
Services\WinSxSHashCalculator  SxS 组件哈希与文件夹命名算法
Services\ManifestDecompressor  DCM 清单解压（算法）
```
**理由**：
- `CbsBuildService` —— WCOS 的 CAB 包展开后与普通包**高度相似，很难区分**，无法干净归属到某一边，
  因此**两个产品线共用它**。它的 `LayoutXmlPath`（DeviceLayout 分区映射）能力为 WCOS 侧保留，
  但 **desktop 的页面不提供入口**。
- `WinSxSHashCalculator` / `ManifestDecompressor` —— 属**纯算法/格式知识**，与"系统怎么被服务"无关，
  两侧镜像都具备同样的形态（WCOS 镜像同样有 WinSxS 与 DCM 清单）。
  > 注：这两条是**边界检查脚本自己抓出来的** —— 它先报了
  > `WCOS 侧引用了 desktop 服务 [WinSxSHashCalculator] -> Services\DeviceLayoutBuilderService.cs`，
  > 复核后确认是**分类错误而非代码错误**，于是把它们移入共用清单。
  > 这正说明该脚本的价值：它既能抓真问题，也能暴露自己规则的过度严格。

**新增共用组件的判定标准**：纯算法/格式知识、与"系统怎么被服务"无关、两侧镜像形态相同。
不满足这三条的，必须先问再共用，并在此登记理由。

---

## 三、已纠正的具体混用

| 混用 | 纠正 |
|---|---|
| agent 曾把 desktop CBS 服务（`UpdatePackageService` / `RepairContentService` / `ServicingRouter` / `SystemAssemblyService` / `ExplodeSxSNativeService`）接进 **WCOS 的 `SystemReconstruct` 页** | 全部撤除（含三选一模型、`DispatchCommand`、配套 XAML） |
| **DeviceLayout** 曾出现在 desktop 的 **CBS 构建页**（布局选择器、从布局 CAB 解 XML、FM 自动填布局、`LayoutXmlPath` 传参、设备布局 GroupBox） | 从页面撤除；`CbsBuildService.LayoutXmlPath` 能力保留供 WCOS 侧 |
| **FM（FeatureManifest）** 曾是 desktop CBS 页的包来源之一（`LoadFromFmAsync`） | 从页面撤除；`FMService` 与 WCOS 侧调用（`PackageScanner` / `ProjectValidationService` / `OEMInputDesigner`）不动 |

---

## 四、如何防止再混

```powershell
# 在仓库根执行；发现跨线引用即非零退出，可接 CI 或提交前钩子
.\check-product-lines.ps1
```

脚本做**引用方向**检查：
1. WCOS 侧文件**不得**引用 desktop 独有服务（`CbsxService` / `ServicingRouter` / …）
2. desktop 页面**不得**引用 WCOS 独有词汇（`DeviceLayout` / `OEMInput` / `FeatureManifest` / `Img2Ffu` / …）

> 允许在**说明"为什么没有它"**的注释里出现这些词（脚本对该行的判定是含 `WCOS`/`不在此`/`已撤` 等字样则放行）。

---

## 五、我自己的作业纪律

- 后续所有 **CBS 工作只落在 Desktop 线**
- **不往 WCOS 页面加 desktop 能力**，也不反过来
- 需要跨线复用某个能力时，**先问**，并在此文档登记理由（像 `CbsBuildService` 那样）
