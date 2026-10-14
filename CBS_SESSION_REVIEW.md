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
