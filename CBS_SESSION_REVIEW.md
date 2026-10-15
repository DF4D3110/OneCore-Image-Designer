# SxS / CBS 组装系统 —— 本轮讨论整理

> 目的：把散在这场对话里的结论、错误、证据固定下来，避免下次重走。
> 约定：**「实测」= 本次有命令与输出为证；「推断」= 尚未验证。
> 每条都标了证据，凡无证据的一律标推断 —— 本轮最大的教训就是"没标证据的结论害了我好几轮"。**

---

## 一、正确的心智模型（三件事必须同时成立）

```
① 包源（sxs\ 或 packages\）
     必须是【扁平的一层】：*.mum + *.cat + *.manifest + <arch>_<组件>_<hash>\
     理由：CBS 以【每个 mum 自己所在的目录】为包源，在其中【相对地】找兄弟包、组件载荷、清单

② 目标镜像
     必须有【已建立的 CBS 树】：
       COMPONENTS hive → Microsoft\Windows\CurrentVersion\Component Based Servicing
     没有这棵树 ⇒ 无处 stage（这是本轮最后的结论）

③ 执行者
     CBS（cbsx，原生）  能装 Product 级包（edition 定义）
     DISM               不能装 Product 级包；且要求 ② 已成立
```

**三者缺一即失败，且失败信息完全不同 —— 这就是本轮反复撞墙的形式上的原因。**

---

## 二、实测确认的事实（有命令与输出为证）

### 2.1 包源

| 事实 | 证据 |
|---|---|
| **包源必须扁平** | 38 个 `kb777777-*` 目录【分散】时 ⇒ `Failed to resolve package`；合并成一层后 ⇒ **1,615 包计划成功、无 resolve 失败** |
| **种子必须是 Edition 级 mum** | `microsoft-windows-editions-serverstandard-package~…mum` = **893 B、1 个 component、无依赖** ⇒ 闭包恒为 1；`microsoft-windows-<X>edition~…mum` = Core **30,383 B / 83 子包**、ServerStandard **61,347 B / 154 子包** |
| **DISM 不递归** | `dism /add-package /packagepath:E:\rc_store` ⇒ `Error 87 No packages were found` |
| **explodeSxS 的清单只有 2 行** | `/ip /m:sxs\Microsoft-Windows-CoreEdition~…mum` + `/sp /m:sxs\Microsoft-Windows-ProfessionalEdition~…mum`（源码 base64 解出） |

### 2.2 SKU / edition

| 事实 | 证据 |
|---|---|
| **SKU 靠 `apply-unattend` 应用 `<Edition>Edition.xml`** | `dism /image:M:\ /apply-unattend:M:\packages\Enterprise.xml` ⇒ **`exit 0` 成功** |
| **Product 级 mum 不能用 `add-package` 装** | `Error 5` + 原文 **`The specified package is not a type that can be modified`**（`releaseType="Product"`） |
| **正常镜像的 `Windows\servicing\Editions\`** | `J:` 有 3 个（`EditionMatrix.xml` / `UpgradeMatrix.xml` / `EnterpriseEdition.xml`）；`F:` 有 8 个 —— **`M:` 原本没有** |
| **命名规律** | `packages\<X>.xml` → `Editions\<X>Edition.xml`；两个 `*Matrix.xml` 保持原名 |

### 2.3 闭包算法（本轮最关键的一次自我纠正）

```
错: //package//assemblyIdentity/@name
      会把 <component> 里的【组件名】也当包名（acpi.inf-languagepack / 1394.inf-languagepack …）
对: //package//package/assemblyIdentity/@name
      包→包引用 = 【package 里嵌套的 package】

对照测试（工具可信度的唯一证明）：
  把检查指向【完整镜像自己的 Packages】，种子用该镜像自己的 edition mum
    修正前: 闭包 875 · 有 206 · 缺 669 · 覆盖 23.5%     ← 不可能，完整系统对自己必须 100%
    修正后: 闭包 205 · 有 205 · 缺   0 · 覆盖 100.0%  ✅
```

**修正后，合并库（RCP 主源 + 原始镜像补齐）对各 edition 全部 100%：**

```
clientcoreedition      105 / 105 · 100.0%
coreedition            154 / 154 · 100.0%
enterpriseedition      205 / 205 · 100.0%
serverstandardedition  449 / 449 · 100.0%
coresystemedition       97 /  97 · 100.0%
```

### 2.4 进程与权限

| 事实 | 证据 |
|---|---|
| **CBS 之后必须独立进程** | 同进程 `apply-unattend` ⇒ `dism 退出码 5 (ERROR_ACCESS_DENIED)`；父进程退出后由独立进程跑同一条命令 ⇒ **`exit 0` 成功** |
| **根因** | DISM 日志：`Failed to unload offline registry: {bf1a281b-…}G:/…/SOFTWARE, the client may still need it open. [0x80070005]` —— 那个 **client 就是调用方进程** |
| **同进程释放 hive 无效** | `ReleaseServicingStack` + `ReleaseResidualHives` ⇒ **`9 → 9`**（持有者是 CBS 服务，不是本进程）；独立进程里同一调用 ⇒ **`1 → 0` 成功** |
| **`/scratchdir` 每条 DISM 都该带** | explodeSxS 的每条 dism 都有；补上后实测生效（`G:\OCID_scratch`） |

### 2.5 架构

| 事实 | 证据 |
|---|---|
| **x86 宿主无法用原生 CBS 服务 arm 镜像** | `[FAIL] 镜像是 arm，当前进程是 x86 —— 必须用 arm 的子程序代跑，否则会加载到宿主服务栈并报 0x8007051A` |
| **DISM 能服务 ARM 镜像** | 真实 ARM 镜像 `M:` 上 `dism /get-packages` ⇒ **`Image Version: 6.2.8061.0` 成功** |
| **但空骨架不行** | 骨架 `N:` 上同类命令 ⇒ `Error 216 请求不受支持`，日志 `Failed to create dismhost.exe servicing process` |
| **硬编码 `pszSystemDrive = "X:\\"` 是对的** | 改成目标镜像所在盘 ⇒ 立刻变 `STORE_CORRUPT (0x80073712)`（已回退） |

### 2.6 宿主 DISM 的能力差异

| 命令 | 结果 | 性质 |
|---|---|---|
| `get-currentedition` | `Error 87 The get-currentedition option is unknown` | **宿主 DISM 10.0.19041 没有这个子命令**（Win8 时代命令）—— **与镜像无关** |
| `get-targeteditions` | 同上 | 同上 |
| `analyzecomponentstore` | `Error 87` | 同上 |
| `apply-unattend` / `set-allintl` / `startcomponentcleanup` | **成功** | 可用 |

---

## 三、本轮真正的结论：为什么 8061 ARM stage 不上去

**实测的 hive 体积对比：**

```
M:\Windows\System32\config\COMPONENTS   →    262,144 B  (256 KB)
    顶层只有：CanonicalData · Configuration · DerivedData · ServicingStackVersions
    【没有 Microsoft 键】—— 没有 CBS 树

J:\...\COMPONENTS  →  17,825,792 B  (17 MB)   ← 正常镜像
F:\...\COMPONENTS  →  17,039,360 B  (17 MB)
```

**⇒ `M:` 是"**空店**"：OS 文件铺了，但没有任何包被 stage/install —— **CBS 树不存在**。**

**⇒ 于是：**

```
建 CBS 树的唯二途径：
  ① CBS 自己（CbsCoreInitialize + CreateNewOfflineStore）← sxsfounder 在做的事
  ② DISM 离线服务                                       ← 但它【要求树已存在】

而：
  · CBS 在 x86 宿主服务 arm 镜像 ⇒ 架构守卫拒（0x8007051A）
  · DISM 在空店镜像上          ⇒ 拒服务（216 / 5）

⇒ 死循环：CBS 服务不了 arm，DISM 要求树已在
```

**⇒ 这一条解释了此前所有现象（无一条需要"包集不够"来解释）：**

| 现象 | 真因 |
|---|---|
| `add-package` → `Error 5 not a type that can be modified` | 无 CBS 树，DISM 判不出包类型/状态 |
| 空骨架 → `Error 216` | 连"店骨架"都不全，建不起镜像会话 |
| `apply-unattend` → `exit 0` 却无变化 | 它只往 `SOFTWARE` hive 写设置，**不建 CBS 树** |
| `get-packages` 能返回 CompatPack | 读的是 `servicing\Packages\` 的 mum / `DerivedData`，不是 CBS 树 |
| `get-currentedition` → `87` | **宿主 DISM 无此子命令**（另一件事） |

**⇒ 三条可能的出路（都还没试）：**
1. **找/换一个 COMPONENTS hive 已建好的 ARM 镜像**（>1 MB 且含 CBS 键）—— 最省事
2. **在能跑 ARM 代码的环境**（QEMU / 真机）里跑我们的工具，让 CBS 建树
3. **换一个 Win8 时代的 DISM**（它的 provider 也许能在空店上建树）

---

## 四、我犯的错误（按代价排序）

| # | 错误 | 代价 | 本该怎么做 |
|---|---|---|---|
| **1** | **闭包 XPath 抓了组件名**（`//package//assemblyIdentity`） | 虚假的"缺 70%"，追了很久，还得出"素材不全"的错误结论 | **先做对照测试**：完整镜像对自己必须 100% —— 一跑就暴露 |
| **2** | **拿 893 B 的 `editions-*-package` 当种子** | 闭包恒为 1，白跑十几轮 | **第一眼就该看文件大小/内容**（30 KB vs 893 B，差别一眼可见） |
| **3** | **把"已安装镜像"当包源** | `cbs_test_8400.vhd` 对 1,362 个需求只能补 **1 个** | **分清来源类型**：Rcp / Media / **Installed（mum 已消费，补不了包图）** |
| **4** | **硬塞 manifest/组件进目标** | 目标变 `STORE_CORRUPT` | CBS 要的是**登记一致**，不是"文件在" |
| **5** | **改 `pszSystemDrive` 为"目标盘"** | 变 `STORE_CORRUPT`（比硬编码差） | **照抄别人代码时，要连前提一起照抄**；改了要立刻对照验证 |
| **6** | **只平铺 mum/cat，丢了组件目录** | 包源形态错误 | 合并时要**数组件目录**，不能只数 mum |
| **7** | **误判 `get-currentedition` 的 87** | 一度归因给 `Editions\` 缺失 | 日志原文写着 `option is unknown` —— **读到底** |
| **8** | **顺序错：拿"可能不全的输入"调"流程"** | 两个变量同时动，无法判断错在哪 | **先用确定完整的输入把流程跑通，再换输入** |

**⇒ 共同点：我反复在没有对照、没有基线的情况下从"现象"直接跳到"结论"。**

---

## 五、纪律（下次必须遵守）

1. **测量工具必须先有对照测试**（完整镜像对自己 = 100%）
2. **一次只动一个变量**，动了立刻用对照验证
3. **先量基线**（一个完整库有多少 mum/组件/manifest；一个正常 hive 多大）
4. **每步要有可判定的判据**（`Plan: Package` 行数、hive 体积、`exit code`）
5. **读日志读到底** —— `option is unknown` 和 `not found` 是完全不同的结论
6. **照抄别人的代码时，连同它的前提一起照抄**

---

## 六、资产（本轮建成，可复现）

| 盘 / 路径 | 内容 |
|---|---|
| **`E:\sxs_lib`** | 合并库（RCP + 原始镜像）：43,418 组件 · 70,219 manifest · 17,215 mum —— **各 edition 100% 完整** |
| `E:\sxs_rcp` | RCP 扁平库（43,280 组件 / 17,203 mum，硬链接） |
| `J:` = `E:\cbs_orig8400.vhd` | 8400 x86 原始镜像（apply 自 ISO，未服务过，576 mum） |
| `F:` = `E:\cbs_test_8400.vhd` | 8400 x86 **已被服务过**（581 mum）—— 只能当对照，不能当包源 |
| **`M:` = `E:\cbs_8061arm.vhd`** | **8061 ARM "空店"镜像 + `M:\packages`**（4,359 组件 · 350 mum · **19 个 SKU XML**）⭐ |
| `M:\packages\` | 完整扁平包集：`Enterprise.mum`（Product 级）+ `Enterprise.xml` 等 19 个 XML + 330 个 Feature Pack 包 + 2 套服务栈（arm + x86，都有 `cbscore.dll`） |
| `X:` = `E:\cbs_workx.vhd` | 工作盘（照 `prepare.cmd`：`X:\image` / `X:\logs` / `X:\temp` / `X:\desktopimaging`） |
| `N:` / `H:` / `G:` | ARM 空骨架 / 干净目标 / 脏目标 |
| 新能力 | `Services\SxsLibraryService.cs`：分类型来源（Rcp/Media/Installed）+ 扁平化 + **可信的**完整性检查 |
| 提交 | `44fd668`（scratchdir）· `8b66381`（后置独立进程）· `c9a70c6`（闭包修正） |

---

## 七、尚未验证的（标清楚，别再当结论用）

1. **往"空店"里塞 XML/组件能不能让它变成可 stage** —— 未验证
2. **宿主 CBS 栈（10.0.19041）能否装 6.2.8061 的包** —— 未验证（此前 6.2.8400 栈 `CbsCoreInitialize` 失败 `0x8007007F`，只有宿主栈能初始化，原因未查清）
3. **三条出路里哪条可行** —— 未验证
4. **`Feature Pack` 级包能否在空店上被 DISM 装** —— 未验证（`add-package` 报了 `not a type`，但那是 Product 级包；Feature Pack 级没单独试过）

---

## 八、后续进展（同日续做）—— 权限是分层的，真 TrustedInstaller 才够

### 8.1 关键认知：权限逐级递进

| 身份 | 走到哪一步失败 |
|---|---|
| 管理员 + `SeBackup/Restore/TakeOwnership/Security` + `takeown`/`icacls` | 卡在**驱动组件**的 `q-unstage`（更早） |
| **LocalSystem**（`schtasks /ru SYSTEM`、或 NSudo `SYSTEM`） | 能过 `StageFile` + `Doqe install`；卡在 **Commit 写离线 hive 的注册表键** |
| **TrustedInstaller**（NSudo `TI` 模式） | 才**拥有**那些键 ⇒ 这才是正解 |

失败点原文（LocalSystem）：
```
CSI  STATUS_ACCESS_DENIED from DirectRegistryProvider::SysSetValueKey(
       key = "\REGISTRY\MACHINE\{bf1a281b-…}M:/Windows/System32/config/SOFTWARE
                \Classes\Unknown\shell", data = "openas")
     → CKey::SetValue → CComponentStore::ApplyTransactionNow
     → ICSITransaction::Commit → E_ACCESSDENIED
```

**⇒ 是【注册表键的 ACL】归 TrustedInstaller，不是文件 ACL。** 所以 `takeown`/`icacls` 再多也没用。

### 8.2 为什么 DISM 能行

```
DISM：dism.exe → CbsProvider.dll → 委托给【Windows Modules Installer 服务】
                                    （TiWorker.exe，跑在 TrustedInstaller 身份）
我们：自己 LoadLibrary(cbscore.dll) ⇒ 身份 = 我们的进程
⇒ 所以必须自己持有 TrustedInstaller token
```

### 8.3 NSudo 集成（已打通）

- **项目里已有 C# 封装**：`MobilePackageGen\MobilePackageGen.Common\NSudo\NSudoInstance.cs`
  （namespace `M2.NSudo`，调 `NSudoAPI.dll` 的 `NSudoCreateProcess`）。
- **构建**：只编译 `Source\Native\NSudoSDK\NSudoAPI.vcxproj`（+ 依赖 `Mile.Library`、`NSudoSDK`）
  —— 这样绕开 `MINT\MINT.h` 与新版 SDK 的 `C2011`（只有 `NSudoDevilMode` /
  `MouriOptimizationPlugin` 两个插件项目报）。产物 `Output\Binaries\Release\{x64,Win32}\NSudoAPI.dll`。
  ARM64 因本机没装 v143 ARM64 工具集而失败。
- **运行时位置**：`<程序目录>\x64\NSudoAPI.dll`（x86 进程用 `Win32\`）——**必须按架构放**，
  否则 x86 进程会去加载 x64 的 DLL 而失败。

### 8.4 修掉的一批"静默错误"

`ElevationHelper` 里 NSudo 枚举常量**全部错位**，而且错得隐蔽（都是"看起来合理"的数字）：

| 常量 | 原值 | 原值实际含义 | 正确值 |
|---|---|---|---|
| `NSUDO_USER_MODE_TRUSTED_INSTALLER` | 3 | **CURRENT_USER** | **1** |
| `NSUDO_USER_MODE_CURRENT` | 1 | **TRUSTED_INSTALLER** | **3** |
| mandatory label | 3 | MEDIUM_PLUS | **0（不改变）** |
| priority | 1 | BELOW_NORMAL | **2** |
| show window | 0 | DEFAULT | **2** |

**⇒ 一个写着"以 TrustedInstaller 启动"的调用，实际会以【当前用户】启动** —— 完全无声地失败。

另外 `IsTrustedInstaller()` 判的是 token 的 **User** SID，但以 TI 运行时 **User 仍是
`nt authority\system`**（TI 服务进程跑在 SYSTEM 下），TI 的身份出现在**组**里：
```
NT SERVICE\TrustedInstaller  S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464
                             Group owner, Enabled
```
⇒ 原实现**永远返回 false**。现在判组，并加了 `IsSystem()`。

**⇒ 教训：`whoami` 的用户名不可信，要看 token 的组。**

### 8.5 新增能力

| 接口 | 用途 |
|---|---|
| `ElevationHelper.RunAs(identity, cmdline, ...)` | 通用：以 TI / SYSTEM / 当前用户跑任意命令 |
| `ElevationHelper.RunAsTrustedInstaller(exe, args, ...)` | 便捷重载 |
| `ElevationHelper.EnsureTrustedInstallerOrRelaunch(log)` | 入口级：不是 TI 就用 NSudo 重启自己，`OCID_ELEVATED_TI=1` 防递归 |
| `CbsxCli` | 装包路径自动先提权到 TI；`/ep` `/dry` `/?` `/finish` `/postinstall` 不提权（`/finish` 靠 `/waitfor:<pid>`；`/postinstall` 走 DISM 自己会委托） |
| `keyform_verify --as-ti` / `--whoami` | 测试用自我提权 / 报告身份 |

### 8.6 顺带修掉的 hive 泄漏

`CbsSafety.FindResidualHives` 只匹配 WCP 前缀 `{bf1a281b-…}`，**我们自己的
`OCID_OfflineHive_*` 完全不在范围内** ⇒ 一次会话实测泄漏 **69 个**。
已加 `FindAllOfflineHives()` / `SweepAllOfflineHives()`，并接进 `RunCleanup()` 的最后一道网
（原来那里有 `if (_touchedRoots.Count == 0) return;`，会把兜底整个跳过 —— 这才是泄漏根因）。
harness 加 `--hivesweep`，实测 **69 → 0**。

### 8.7 实测：TI 下的 8061 ARM staging

```
身份: TrustedInstaller（NSudo）
CbsCoreInitialize OK（镜像自带栈）
会话已初始化 (bootDrive="M:", winDir="M:\Windows")
Plan: Package 行数: 946
COMPONENTS hive: 256 KB → 2.3 MB → 4.4 MB → 7.2 MB …（CBS 树在建）
日志: Stage / StageFile / Doqe [install] deployment 大量出现，无 E_ACCESSDENIED
```

**⇒ 已确认：跨架构（x86 栈服务 arm 镜像）+ TrustedInstaller 身份，CBS 离线 staging 正常推进。**

### 8.8 环境清理

- 离线 hive：69 → 0（`--hivesweep`）
- 挂载：只保留 `M:`（8061 ARM）、`F:`（8400 x86）、`J:`（8400 原始）；ISO 全卸
- 删除 9 个死 VHD（`cbs_e2e2` / `cbs_clean` / `cbs_workx` / `cbs_8061x86` / `cbs_arm8061*` …）

---

## 九、卡点精确化：Commit 阶段的一个注册表写（三种身份都一样）

### 9.1 实际成果（至今最好的一次）

身份 = **TrustedInstaller**（NSudo TI），x86 进程 + 8061 自带 x86 服务栈 → **arm** 镜像 `M:`：

```
CbsCoreInitialize OK · 会话已初始化 (bootDrive="M:", winDir="M:\Windows")
Plan: Package 行数: 946
Stage / StageFile / Doqe [install] 大量成功
COMPONENTS hive : 256 KB → 8448 KB
WinSxS          : 86 → 4279 个组件 / 18136 个文件
M: 已用         : 6.01 GB
⇒ 但最后一次 Commit 失败，事务未提交（DISM 仍只认 CompatPack 为 Installed）
```

**⇒ staging 的**主体是成功的**；失败的只是最后一笔提交。**

### 9.2 失败点（管理员 / SYSTEM / TrustedInstaller 三者逐字相同）

```
CSI STATUS_ACCESS_DENIED from DirectRegistryProvider::SysSetValueKey(
      key = "\REGISTRY\MACHINE\{bf1a281b-…}M:/Windows/System32/config/SOFTWARE
               \Classes\Unknown\shell",
      vn = "", ti = 0, t = 0, data = "openas")
  → CKey::SetValue
  → CComponentStore::PendActiveTransaction
  → CComponentStore::ApplyTransactionNow
  → ICSITransaction::Commit → E_ACCESSDENIED
```

### 9.3 已排除的解释

1. **不是键 ACL**：对照 8400 的 `J:` / `F:`，该键 `所有者=NT AUTHORITY\SYSTEM`，
   `Administrators` 与 `SYSTEM` **都有 FullControl**，无 Deny。
2. **不是"键不可创建"**：在**全新 apply 的镜像**（无任何占用）上加载 SOFTWARE hive,
   `New-Item HKLM\…\Classes\Unknown\shell` **成功**
   （`Classes\Unknown` 在 8061 基线里本来不存在 —— CBS 是要**创建**它）。
3. **不是 hive 文件 ACL**：SOFTWARE 文件所有者已 takeown 成 `BUILTIN\Administrators`。
4. **不是 token 身份**：如上，三种身份都停在同一处。

### 9.4 尚存线索

- 日志里出现 `LockComponentPath … path: "\??\x:\Windows\WinSxS\x86_microsoft-windows-servicingstack_…"`，
  CBS 把组件路径算成 `x:`，而镜像在 `M:`。
  ⚠ 已查 `CbsxService.DeriveBootDrive("M:\Windows")` → `"M:"`（正确），
  `bootDrive` 日志也是 `"M:"` ⇒ 这个 `x:` 应属 CBS 对离线镜像的内部规范表示，**不是我们的 bug**，但值得留意。
- 失败前 Windows 把该 SOFTWARE hive 交由 `TiWorker` / `TrustedInstaller` 持有过
  （残留 8 个 `{bf1a281b-…}` 挂载）。

### 9.5 决定性对照实验（进行中）

**同一个 build（8061）的 x86 镜像**（`E:\x868061\fbl_core1_soc.8061.0.110803-1830_x86fre_client_EN-US_DVD.iso`
→ apply 到 `Q:`，同形态：空店 + `Q:\packages`），跑**完全相同**的流程，但**无跨架构**。

- **成功** ⇒ 根源是"**跨架构**服务 arm 镜像"（x86 进程 + x86 栈 + arm 镜像这个组合）
- **同样失败** ⇒ 是"我们自建 CBS 会话"这条路的**通用问题**（与架构无关）

脚本：`C:\Users\DELL\Documents\deepseek-harness\default-workspace\control_x86.ps1`

**⇒ 参考：x86/amd64 镜像（6.2.8220 / 6.2.8400 / 6.3.9600）的原生 CBS 过去验证是能装通的，
所以"跨架构"是目前最可疑的差异点。**

### 9.6 踩坑记录（脚本层面）

**写 `.ps1` 给 `powershell.exe`（5.1）跑时，若文件是 UTF-8 **无 BOM** 且含中文，
5.1 会按 ANSI 读成乱码，乱码可能**吞掉后续行**（实测 `$iso = '...'` 赋值直接失效，
变量变 null）。⇒ **给 5.1 的脚本用纯 ASCII，或写成 UTF-8 **带 BOM**。**


