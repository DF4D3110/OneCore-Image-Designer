# CBS 离线服务技术交底（desktop 线）

本文件记录**实测得出**的关键事实。每一条都标了是怎么得出的，
因为其中数条与直觉相反，且**错误的理解会导致完全错误的排查方向**。

> 产品线说明见 `PRODUCT_LINES.md`。本文件只讲 **desktop（普通 Windows）** 的 CBS。
> WCOS / Phone 不适用本文件。

---

## 1. 架构决定一切

### 1.1 CBS 的 servicing stack 是**按架构**的

x64 进程**加载不了** x86 的 `cbscore.dll`。

### 1.2 失败方式极其误导

它**不会干脆报错**，而是**静默回退到宿主栈**。日志长这样：

```
Loaded Servicing Stack v6.2.19041.1704 with Core: C:\Windows\WinSxS\amd64_..._10.0.19041.1704_...\cbscore.dll
                                                          ^^^^^^ 宿主栈，不是镜像自带的！
Failed to get offline CSI store. [HRESULT = 0x8007051a - ERROR_REVISION_MISMATCH]
```

**⇒ 症状是"版本不符"，根因是"架构不符"。** 用宿主 19041 的栈去服务 8400 的镜像，当然对不上。

**修法**：用**与镜像同架构的进程**。换上 x86 进程后日志立刻变成：

```
Loaded Servicing Stack v6.2.8400.0 with Core: G:\Windows\WinSxS\x86_..._6.2.8400.0_...\cbscore.dll
                                                                  ^^^ 镜像自带的
会话已初始化 (bootDrive="G:", winDir="G:\Windows")
```

### 1.3 铺底（WCP）同样受架构影响

| 进程 | `CreateNewOfflineStore` |
|---|---|
| x64 → x86 镜像 | ❌ `0x8007051A` |
| **x86 → x86 镜像** | ✅ `Success`，`cbSize = 56`（x64 是 `112`） |

**⇒ `ArchRelay` 的存在是必需的，不是优化。** 子程序转接位于
`Services\ArchRelay.cs`：`DetectImageArchitecture` / `NeedsRelay` / `FindHelper` / `RunAsync`。

### 1.4 架构不符时的正解是**换通道**，不是硬阻断

架构不符 → 走 **DISM**（`dism /image` 有自己的跨架构离线调度）。
`ServicingRouter.Decide` 已把架构纳入判定，**架构最先判**，不符直接返回 `Channel.Dism`。

> 例外：**铺底无法交给 DISM**（骨架是 WCP 造的），那一步确实要求同架构进程。

### 1.5 空骨架的架构是 `Unknown`，且永不触发转接

还没有 `servicingstack` 目录、也没有系统 DLL 可读 PE 头 ⇒ 判定为 `Unknown` ⇒
`NeedsRelay` 返回 false。**所以对裸骨架做路由不会被挡住**（这是刻意的）。

---

## 2. `0x8007051A` 的真实含义（**我曾判错**）

**它是 `ERROR_REVISION_MISMATCH`，不是 `ERROR_NO_SUCH_PRIVILEGE`。**
（Win32 1306 是前者。我最初记成了后者，方向错了很久。）

CBS 的实际报错链：

```
Error STATUS_REVISION_MISMATCH originated in function Windows::COM::VerifyStoreVersionNumber
Failed to get offline CSI store. [HRESULT = 0x8007051a - ERROR_REVISION_MISMATCH]
Failed to initialize store parameters ... [HRESULT = 0x8007051a]
```

**⇒ 见到 `0x8007051A`，先查架构与栈版本是否匹配，而不是先去查特权。**
`PrivilegeHelper.Explain` 里已写入这条正确解释。

---

## 3. 特权：**必需，但单独不够**

CBS 离线服务需要（管理员本就持有，但**默认全部关闭**）：

```
SeBackupPrivilege · SeRestorePrivilege · SeTakeOwnershipPrivilege · SeSecurityPrivilege
```

`PrivilegeHelper.EnableServicingPrivileges` 显式启用并记录结果。
**已接入**：`CbsxService` · `SxSFounderService` · `FounderStackInstaller`。

---

## 4. 骨架 ACL：特权也救不了，必须改 ACL

**WCP 铺底出来的骨架带限制性 ACL**（目录归属系统/TrustedInstaller）。表现为：

```
UnauthorizedAccessException: Access to the path '...\WinSxS\x86_..._servicingstack_...' is denied.
```

**关键细节**：目录**能创建**，但**往里写文件被拒**；而且**光启用 `SeRestorePrivilege` 不够** ——
.NET 的文件复制**不会**因为进程持有还原特权就绕过目标目录的拒绝 ACE。

**修法**（`FounderStackInstaller.EnsureWritable`）：

```
takeown  /F <dir> /R /D Y
icacls   <dir> /grant *S-1-5-32-544:(OI)(CI)F /T /C /Q
```

> 用 **Administrators 的 SID**（`S-1-5-32-544`）而不是名称 —— 避免本地化名称差异。

---

## 5. 空骨架上让 CBS 起来所需的最小系统文件集

铺底 + 装服务栈后，CBS **仍然**起不来。日志精确指出原因（直接照做即可）：

```
Failed to open file: ...\Windows\system32\kernelbase.dll   [ERROR_FILE_NOT_FOUND]
Failed to load ntoskrnl.exe to detect bitness from offline image
Failed to populate information about offline store
```

**CBS 要读几个系统 DLL 来探测镜像位数、填充离线存储信息。**
补齐这一组后会话立刻成功（`CSI Store initialized` + 干净收尾）：

```
system32\  kernelbase.dll · ntdll.dll · kernel32.dll · advapi32.dll · msvcrt.dll
           rpcrt4.dll · bcrypt.dll · bcryptprimitives.dll · sechost.dll · ci.dll
           ntoskrnl.exe        ← 用于探测位数
```

实现在 `FounderStackInstaller.CoreFilesForCbs` / `InstallCoreFiles`。
**刻意不整份复制 `system32`** —— 实测这最小一组就够；缺哪个日志会明确指出，再补即可。

---

## 6. 服务栈版本体系：`6.x` 原生，`10.0+` 走 DISM

CBS 把**已加载栈自称的版本**与**镜像声明的栈版本**比较：

| 镜像声明 | 可加载栈自称 | 结果 |
|---|---|---|
| `6.2.8220.0` | `6.2.8220.0` | ✅ 同体系，通过 |
| `10.0.17046.1000` | `6.2.17046.1000` | ❌ `0x800F0823`（`6.2 < 10.0`） |

**⇒ 可加载的栈一律自称 `6.2.<build>`**（CBS 内部版本体系），与 `10.0` 体系镜像必然不符。

### 已排除的两个假设

| 假设 | 实验 | 结论 |
|---|---|---|
| 是架构问题？ | x86 进程 + 镜像自带 x86 栈（**版本号完全一致**，17046 vs 17046，arm64 镜像） | ❌ 仍被拒 ⇒ **不是架构** |
| `InitializeEx` + `externalDir` 能绕过？ | 分别指向宿主 amd64 栈 与 镜像自带 x86 栈 | ❌ **不能** —— 版本检查照旧；随后的 `E_ACCESSDENIED` 只是它的后果 |

### 已实测可用的世代

`6.2.8220` · `6.2.8400` · `6.3.9600` —— **三个镜像**跑通原生通道，会话干净收尾。

---

## 7. 服务栈**没有**独立的 mum（至少 6.2.8400 没有）

实测 `C:\rc_base\Windows\servicing\Packages`：
```
mum 总数 576，其中含 servicingstack 的 = 0
服务栈目录内也没有 manifest
```

**⇒ 服务栈是靠它自己的 WinSxS 目录被识别的**，不是靠 `servicing\Packages` 里的 mum。

所以 `FounderStackInstaller` 不再对"没找到 mum"报警，而是**如实说明这是该构建的形态**。
同时 `FindStackMum` 会**自动探测兄弟目录**（`servicing\Packages`、`Packages`、上两级）——
因为调用方天然只会传 `...\Windows\WinSxS`。

---

## 8. Repair Content 与 PA30

### 8.1 硬性解包顺序（顺序错会导致组件损坏）

```
① 文件名含 neutral-mergedcomponents 的包【先单独解包】
② 解完【删除】它的 PSF 与 CAB
③ 再解其余（组件包在前，语言包在后）
```

**机理**：delta 的源文件按 **SHA256** 在"已解出的输出目录"里查找。
`neutral-mergedcomponents` 提供**完整版本**；顺序颠倒时，后面的包会找到先前解出的
**异常截断文件**当源 ⇒ 产出损坏组件 ⇒ 安装失败。

**实现**：`RepairContentService` —— 每个包**单独调用一次**解包，并把**输出目录本身**作为源，
使每次调用都重新索引"当前已解出的一切"。**这正是顺序规则起作用的机制。**

### 8.2 PSF 格式（实测）

```
magic = "PSTREAM\0"，载荷【不是】内嵌 CAB
配对 CAB 装的是描述：update.mum / <name>.psf.cix.xml / update.cat
cix.xml 是索引：
  <File id name length time attr>
    <Hash alg="SHA256" value="目标文件哈希"/>
    <Delta><Source type="PA30" offset="PSF内偏移" length="压缩长度">
      <Hash alg="SHA256" value="源(base)哈希"/></Source></Delta>
  </File>
```

**还原** = 读 `PSF[offset..offset+length]` 得 PA30 载荷 → 对源文件应用 delta
（`msdelta!ApplyDeltaB`）→ 校验目标 SHA256。
**源文件按 SHA256 从调用方给的源目录里查找**（现代 PSFExtractor 的做法）。

**CAB 解出用 Windows 自带的 `expand.exe` 即可** —— **不需要 libmspack**（原 MSUpdateExtractor
内嵌了它，但没有必要照搬）。

### 8.3 `ApplyDeltaB` 互操作（踩过三个坑）

```c
BOOL ApplyDeltaB(DELTA_FLAGS Flags, DELTA_INPUT Source, DELTA_INPUT Delta, DELTA_OUTPUT* Target);
```

| 坑 | 后果 |
|---|---|
| `Source` / `Delta` 是**按值传结构体**，不是指针 | 原生侧把指针当结构体内容 → **CLR Fatal error `0x80131506`** |
| `DELTA_INPUT` **有第三个字段 `BOOL Editable`** | 同上 |
| **`DeltaFree` 要释放的是数据缓冲 `lpStart`，不是 `DELTA_OUTPUT` 结构体本身** | **堆损坏 `0xC0000374`** |

第 4 参数建议直接封送为 `out DeltaOutput`（对应 `DELTA_OUTPUT*`），别手工解引用。

### 8.4 实测成绩

```
顺序解包：203,556 文件 / 7.75 GB / 0 字节文件 = 0
PA30 还原：每个文件 SHA256 与包自带 <Hash> 相符 ⇒ 与原始文件【逐字节一致】
残留失败：约 0.5%，高度集中在 .mfl（218）与 .ini（161）—— 语言相关的 WMI/性能计数器本地化
```

---

## 9. 其它实测要点

| 事实 | 说明 |
|---|---|
| **CAB 解出用 `expand.exe`** | 免第三方解码器；`expand -F:* <cab> <dir>` 用目录形式（多文件 CAB 必须给目录） |
| **配对文件名哈希后缀不同** | `..._d52fdcae....cab` ↔ `..._9b08b7b3....psf` ⇒ 不能用 `ChangeExtension`，要用共享前缀回退 |
| **语言代码含 `-`** | 用 `-` 切分包名会把 `pt-br` 拆成 `pt`/`br` ⇒ 误判 6 个语言包。必须用 `-<lang>-` 子串匹配 |
| **`servicing\Packages` 的 mum 与 WinSxS 目录数不等** | 8400 镜像：576 mum，但只有一部分注册 ⇒ 枚举到的包数是"可操作集合" |
| **arm64 镜像自带 `x86_` 服务栈** | 包名含 `...CrossArch...` —— Microsoft 显式提供的跨架构机制 |
| **DISM 只列 `Installed` 状态的包** | 所以"我们的枚举 ⊇ DISM 列表"可作为交叉验证 |
| **Win8 RC 时代的 DISM 不支持部分开关** | `get-currentedition` / `set-allintl` / `analyzecomponentstore` 一律 `退出码 87` —— 属**目标能力不足**，不是我们的 bug |

---

## 10. `ApplyPlan` 的幂等性（曾导致"一个包挡住全部"）

原实现把"包已经是目标状态"当**错误**并**中止整个计划**。
后果：一个已 `Installed` 的 `Enterprise` 包，导致两个真正 `Absent→Installed` 的包**被一起放弃**。

**⇒ 现在是跳过并继续**（带计数），**只有适用性不足才中止**。


---

## 11. 收尾必须在【独立进程】里做（实测结论）

### 现象

CBS 会话 `FinalizeEx` 之后，目标镜像的 hive **仍挂在 HKLM 下**，且**本进程内怎么都卸不掉**：

| 场景 | `ReleaseResidualHives` 结果 |
|---|---|
| **同一个进程内**（刚跑完 CBS） | **9 → 9**，重试 6 次（累计等 42 秒）**全部失败** |
| **新进程**（哪怕同一块盘、同一批 hive） | **9 → 0**，`imagestate` 写入 **2 处**，收尾完整成功 |

对照：`ReleaseServicingStack()` 把本进程加载的 `cbscore.dll` / `wcp.dll` / `dpx.dll` /
`msdelta.dll` 都放掉了，**hive 依然被持有** ⇒ **持有者是 CBS 的【服务】，不是调用进程**，
而该服务要到调用方进程退出才释放。

### 因此

`FinishImageStep`（写 `State.ini` + imagestate）**不能与 CBS 步骤同进程执行**。
现在的实现保留同进程尝试（并带"释放 → 递增等待 → 重试"），**失败时明确提示应把收尾移出本进程**。

### 另一条实测结论：铺底会自愈

上一次运行留下的 hive 会让下一次 `CreateNewWindows` 直接失败
（`0x80070020 ERROR_SHARING_VIOLATION`）。`SxSFounderService` 现在**建 windows 之前先
`ReleaseResidualHives` 该盘**，并在仍有残留时告警。实测：**上一轮残留 9 个 hive 的情况下，铺底照样成功。**

### 还有一条：`reg.exe` 为何卸不掉

`reg unload` 报 `Access is denied`，而我们的 `RegUnLoadKey` 能成功 —— 差别在于
**`reg.exe` 那条路径不启用 `SeBackupPrivilege`/`SeRestorePrivilege`**。
所以"手工卸不掉"不代表"程序卸不掉"。


---

## 12. arm32：铺底能做，装包受平台限制（实测）

### 能做的部分（全部实测通过）

| 步骤 | 结果 |
|---|---|
| **仅凭 Repair Content 还原存储** | 155,954 文件 / 5.6 GB / **0 截断**，**不需要任何外部 base** |
| **x86 进程给 arm32 铺底** | ✅ `CreateNewWindows + CreateNewOfflineStore` 成功（disposition 0/0） |
| **装 arm32 服务栈** | ✅ 43 文件 / 12.7 MB，cbscore + wcp 齐，`6.2.8400.0` |
| **装核心文件（取自存储）** | ✅ 13/13（含 `ssshim.dll`、`cbsmsg.dll`） |
| **架构路由** | ✅ 正确判为"镜像 arm / 进程 x86"并改走 DISM 通道 |

**⇒ 跨架构铺底是可行的**：WCP 的 `ulProcessorArchitecture` 是参数，x86 进程能铺出 arm32 骨架。
（但 x64 进程铺 x86 会在 `CreateNewOfflineStore` 处失败 `0x8007051A` —— 那一条仍然拦。）
所以 `SxSFounderService` 的架构检查改为**只拦已实测失败的组合**，其余放行并告警。

### 装包：DISM 报 Error 50

```
Error: 50
DISM does not support servicing a Windows Vista RTM or earlier operating system.
If the operating system is supported check that SSShim.DLL is present.
```

- **x64 的 `Dism.exe`（0x8664）与 x86 的 `SysWOW64\Dism.exe`（0x014C）都是这个结果。**
- 镜像里 `ssshim.dll` 已就位 —— 而且**它在 arm32 内容里只有 x86 版本**（ARM 组件目录
  `arm_microsoft-windows-*` 里故意携带 x86 跨架构桥接文件），这不是缺陷。
- **arm32 内容确实带 1 个 x86 `cbscore.dll`**（和 arm64 一样有跨架构栈），但**没有 x64 版本**，
  所以 x64 宿主 DISM 用不上。

### 结论（**已更正**）

> ⚠ **上一版这里写的是"需要 ARM32 宿主、平台不可行"—— 那是错的。**
> 找到 `E:\ARM\8419.0..._CLIENT_COREARM_...wim`（ARM32 的 Windows RT 完整镜像）后重测，
> 结论完全反过来。

**在【真实完整的 arm32 镜像】上，DISM 从 x64 宿主完全可用：**

```
dism /image:H:\ /get-packages                    -> 成功，列出 Foundation-Package~...~arm~~6.2.8419.0
SysWOW64\Dism.exe /image:H:\ /get-packages       -> 同样成功
dism /image:H:\ /add-package /packagepath:<8400 arm 包>  -> 跑到 100%，"completed successfully"，exit=0
```

**⇒ arm32 装包是可行的**，通过 DISM（原生 CBS 仍不可能：x86 进程加载不了 ARM 的 `cbscore.dll`）。

**真正的障碍是"目标镜像要足够完整"。** 我们铺的裸骨架 DISM 报：
```
Error: 50   DISM does not support servicing a Windows Vista RTM or earlier operating system.
            check that SSShim.DLL is present.
```
而 `ssshim.dll` 其实已经就位 —— 这不是 shim 缺失，而是 **DISM/SSShim 判不出镜像的 OS 版本**，
于是按"太老"处理。

### 裸骨架缺的到底是什么（实测对比）

| 位置 | 真实 arm32 镜像 | 我们的裸骨架 |
|---|---|---|
| `COMPONENTS` 根值 | `StoreFormatVersion`（UTF-16 的 "0.0.0.6"）、`StoreArchitecture` = 5（ARM） | **无** |
| `COMPONENTS` 子键 | `CanonicalData` / `Configuration` / `DerivedData` / `Installers` / `ServicingStackVersions` / `ccpinterface` | 基本为空 |
| `SOFTWARE\Microsoft\Windows NT\CurrentVersion` | `CurrentVersion=6.2`、`CurrentBuild=8419`、`CurrentBuildNumber=8419`、`InstallationType=Client`、`EditionID=CoreARM`、`ProductName=Windows RT` | **无** |

**⇒ 要让"从零构建"的 arm32 骨架也能装包，必须在【装包之前】把这些版本信息写进 hive。**
注意时机：不能放在收尾（`FinishImageStep`）—— 装包在那之前就发生了。
正确插入点是**步骤 1.5（装服务栈 + 核心文件）之后、步骤 2（装包）之前**。


---

## 13. PA30 残留的准确度量与真因

### 数字（同一批内容、同一份代码，只改 base）

| base | 缺源 | 占比 |
|---|---|---|
| **不给 base** | 2,738 / 203,556 | **1.35%** |
| **Windows 8 Enterprise（Client）install.wim** | 1,523 / 203,556 | **0.75%** |

两次的产出**完全一致**（203,556 文件 / 7,939.6 MB / 0 截断），差别只在"能否还原"。
⇒ **base 对这批文件是承重件**：它把残留砍掉 44%。

### 剩下 1,523 个是什么

分属 1,145 个组件，按扩展名：
```
.manifest 346 · .inf_loc 222 · .dll 177 · .cab 170 · .png 48
.resx 45 · .gif 41 · .aspx 36 · .exe 32 · .asmx 31
```
按组件：**IIS / Web / .NET 角色**
```
msil_microsoft-windows-..rshellwebaccess-web   25
x86_netfx-aspnet_regbrowser_files              25
x86_tsportalwebpart                            21
x86_microsoft.dtc.powershell.scripts           16
x86_mscorlib                                   13
x86_netfx-aspnet_webadmin                      13
```

### 真因：**base 镜像不覆盖这些组件**，不是提取器的缺陷

抽查 20 个缺源组件，**base 里 0 个存在、20 个不存在**：
```
base 的 WinSxS 组件总数        : 7,753
base 里 netfx-aspnet 组件      : 3      （缺源清单里要 25 个）
base 里 webshellwebaccess      : 0
base 里 tsportalwebpart        : 0
```
这些是 Windows **Server 角色**（IIS、.NET、DTC、Web 服务）的家当，
而 base 是 Windows 8 **Enterprise（Client）** 的 install.wim —— **Client 镜像天然不含它们**。

⇒ **要把它压到 0.5% 以下，办法是换一个包含这些角色的 base**（Server 版镜像），
或者在 Repair Content 内部做跨包补齐。**不是调提取算法能解决的。**

### 度量时必须注意的坑

同一轮里我一度把 `ApplyDeltaB` 的返回值当 HRESULT 读，并据此放宽了 size 检查 ——
**结果整批 203,556 个文件被静默写成 0 字节**（0 字节文件从 0 变成 2,738→全量）。
教训见第 14 节。


---

## 14. arm64 走 DISM：已实测可用

素材：`E:\17046.1000.171118-1403.RS_PRERELEASE_CLIENTSINGLELANGUAGE_OEMRET_ARM64FRE_EN-US.iso`
（另有 `E:\winpe_10.0.9834.0_arm64.wim`）。应用后 102,754 个文件，
`Windows 10 Home Single Language`，世代 **10.0.17046**。

### 路由判定（正确）

```
镜像架构 : arm64（WinSxS 下 servicingstack 目录以 arm64 为主，4 个）
进程架构 : amd64
是否需要转接 : 是 —— 镜像是 arm64、进程是 amd64
```
⇒ 架构不符 ⇒ 走 DISM。**同时世代是 10.0，按版本策略也走 DISM** —— 两条判据一致。

### DISM 读

```
dism /image:L:\ /get-packages  -> 成功，含真实包：
  Microsoft-OneCore-ApplicationModel-Sync-Desktop-FOD-Package~...~arm64~~10.0.17046.1000   [Installed]
  Microsoft-Windows-CHPE-Binaries-arm64x86-Package~...~arm64.x86~~10.0.17046.1000        [Installed]
  Microsoft-Windows-EditionSpecific-Education-arm64arm-Package~...~arm64.arm~~10.0.17046.1000 [Staged]
dism /image:L:\ /get-currentedition -> 退出码 0
```

### DISM 写

```
dism /image:L:\ /add-package /packagepath:<...~arm64.arm~en-US~10.0.17046.1000.mum>
-> 跑到 100%，"The operation completed successfully"，exit=0
```

### 一个值得记的观察

arm64 镜像里存在 **`arm64.x86` 与 `arm64.arm` 这类交叉架构包**
（`CHPE-Binaries-arm64x86`、`EditionSpecific-Education-arm64arm`）。
**这正是 arm64 能携带 crossarch x86 服务栈的来源，也正是 arm32 所缺少的东西** ——
arm32 的内容里只有 ARM 与 x86 两种 shim，没有 x64，所以 x64 宿主的 DISM 用不上它。

**⇒ arm64：读、写、路由判定三者都已实测通过。**
