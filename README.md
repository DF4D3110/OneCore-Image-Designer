# OneCore-Image-Designer

## 核心功能

### 1. OEMInput 设计器（5 步向导：扫描→确认→配置）

1. **自动识别** — 选/粘贴 `MSPackageRoot` 后一键扫描：`FMFiles` 散装 FM XML + `MSPackages` 下 `*FM*.cab`（自动解开读包内 XML），按内部名去重（同名以散装 XML 为准，部分泄露样本 `AdditionalFMs` 风格）；候选 FM 默认全选可取消；DeviceLayout 按官方回退链（`DeviceLayoutPackages` → `SOCPackages` FIP）识别并自动选中，`$(mspackageroot)` 占位变量自动展开定位真实 CAB；跨 FM 重名 Feature 按冲突簇弹窗定夺（保留全部=官方先到先得合并 / 仅保留所选 FM）

2. **基本信息** — V1/V2 版本选择器；V1 用 `SOC` / `SupportedLanguages`，V2 用 `DeviceLayoutType` / 扁平 `Languages`；Test 时显示 `TestContent`；`SOC` / `SV` / `Device` 由步骤1自动预填

3. **语言** — 从 FM 提取可用语言，Default 唯一性约束

4. **Features** — Microsoft / OEM 双列 + 搜索过滤；互斥冲突用官方四约束（`None` / `OneOrMore` / `ZeroOrOne` / `OneAndOnlyOne`，支持嵌套子组）+ 前缀归一匹配（`MS_` / `OEM_`），WPF 单选对话框消解

5. **预览** — XML 预览 + 语义 / XSD 双层校验内联红绿汇总 + 保存

生成的 `AdditionalFMs` 写入工程用到的全部 FM 路径。支持「文件→打开工程(`.cwproj`) / 直接打开 `OEMInput.xml`」反向装载（自动探测版本）。

### 2. 设备磁盘布局预览

解析 DeviceLayout CAB，图形化分区条形图（扇区大小正确传播，4K 布局不再差 8 倍）。

### 2b. 设备布局创建（FM + CAB 造包）

首页磁贴 + 功能菜单入口。编辑分区表（内置标准 GPT 模板，GUID 取自官方样本；也可从现有 XML / CAB 导入作起点），一键产出：

- **DeviceLayout.xml** + 明文 manifest×2 + **update.mum** → **update.cat**（`makecat`；`cabarc` 缺失时托管引擎兜底）→ 可选 `signtool` 测试签名
- **FM.xml** — OEMInput 设计器可自动识别该 SOC，闭环

支持两个时代（不同 ADK 内部差异大，均取自真实样本）：

| 时代 | 关键特征 |
|------|----------|
| **早期 Phone**（WPAK 14~17xxx，QC8960/98 实证） | 点分包 ID、主组件名带 `0` 后缀、token `6288`、DeviceLayout 落在 `windows\ImageUpdate\`、mum=Update、FM=`DeviceLayoutPackages`+`SOCPackages`(FIP)+`Features`。keyform 哈希**逐字节复现官方**（探针验证） |
| **现代 FactoryOS**（10x/ModernPC 18xxx+，20279 实证） | 连字符布局包名(`-Package`)、组件名无 `0` 后缀、token `31bf`、DeviceLayout 落在组件目录根、`destinationPath` `$(runtime.windows)`+WRP SDDL、mum=Feature Pack；另产 **FIP 桩包**（纯 mum+cat、`<declareCapability>`）；FM 带 `ID=owner` / `SchemaVersion=1.3` 的三段 `*Packages`、无 `SOCPackages` / 无 `Features` |

> **注**：自造包的 keyform 哈希只需**内部一致**（manifest 文件名 ↔ mum cabpath ↔ 目录名）即被 `imageapp` 接受；20279 等跨时期包的微软内部哈希算法与这里的不同，探针对此按命名结构而非逐字节断言。

### 3. 镜像构建

选择 OS 包 + OEMInput + 架构，调用 `imageapp` 产出 FFU / VHDX。

### 4. 系统重构

从 FFU / VHDX 提取 CBS / SPKG / Driver / FM 包并重建（完全构建 / 选择构建）。

### 5. FFU 文件浏览器

镜像内文件系统浏览、多选 / 拖拽提取、十六进制查看、固件导出（C 数组 / Intel HEX）。

### 6. 实用工具

- **离线注册表编辑器** — 加载镜像 Hive 编辑
- **驱动提取与整理** — `FileRepository` INF 解析
- **UEFI 固件解析** — FFS 遍历
- **WinSxS Hash 计算器** — `X65599Variant`，实测非驱动组件 100% 命中
- **包依赖关系分析器**
- **组件存储完整性校验** — DCM 解压 + 哈希比对

## 致谢与开源参考

本项目在开发过程中参考并复用了以下开源项目的设计与组件，在此致谢：

| 项目 | 用途 | 链接 |
|------|------|------|
| **DevImgGen** | 部分设计思路与镜像生成流程参考 | [mediaexplorer74/DevImgGen](https://github.com/mediaexplorer74/DevImgGen) |
| **MobilePackageGen** | 部分包生成组件与封装逻辑复用 | [MobileTooling/MobilePackageGen](https://github.com/MobileTooling/MobilePackageGen) |
| **wmpt**（未发布） | 设计与实现参考 | [lzw29107](https://github.com/lzw29107) |
| **wcpex** | 设计与实现参考 | [smx-smx/wcpex](https://github.com/smx-smx/wcpex) |

> 上述项目的代码与设计均遵循其各自开源许可证。如有遗漏或不当引用，请提交 Issue 告知。

## 构建和运行

```bash
# 构建（仅 Release）
dotnet build "OneCore Image Designer.csproj" -c Release

# 运行（需要管理员权限：离线注册表编辑器要 RegLoadKey 特权）
bin\Release\net8.0-windows\OneCore Image Designer.exe
