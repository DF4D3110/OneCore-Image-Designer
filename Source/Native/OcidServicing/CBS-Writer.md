# OCID CBS 写侧（COMPONENTS 台账组装）

这个目录里的原生代码负责把"一份 Windows 镜像的包集 + 组件清单 + 已落位载荷"重新
生成成微软 servicing 栈能读的 `COMPONENTS` 台账。验收标准是**与微软原版台账逐字节
一致**（键、值名、REGBINARY 原始字节全部对齐），而不是"系统能跑"。

实现全部自带：不调用系统 `wcp.dll`/`CbsCore.dll`/`dism`，也不依赖任何 GPL 组件。

## 1. 数据流

```
镜像 ─┬─ Windows\WinSxS\Manifests\*.manifest   （组件清单：identity / file / hash / dependency）
      ├─ Windows\servicing\Packages\*.mum      （包清单：update → component/driver 声明）
      ├─ Windows\servicing\Packages\*.cat      （包 catalog，SHA-256 即 Catalogs 键）
      └─ Windows\WinSxS\<组件目录>\...         （已落位文件，用来算 SHA-256 与 f!）

        ↓ cbs-plan-components    → DerivedData\Components（identity / S256H / f!）
        ↓ cbs-plan-deployments   → CanonicalData\{Deployments,Catalogs}
        ↓ cbs-plan-versioned     → DerivedData\VersionedIndex（家庭键/版本节点/f256/CT）
        ↓ cbs-plan-root          → 根值（StoreFormatVersion/StoreArchitecture/ServicingStackVersions）
        ↓ cbs-write              → 真 hive 文件（RegSaveKey 造壳 + RegLoadKey 写入）
        ↓ cbs-dump / cbs-diff    → 与参考台账对拍
```

`Source/Native/assemble-cbs-store.ps1` 把上面五步串起来，可选 `-DiffAgainst <真机转储>`
做对拍。

## 2. 已解出并可自证的算法

| 目标 | 规则 | 证据 |
| --- | --- | --- |
| 组件/部署键尾 16hex（伪键） | `acc=0`，按 `name`、`culture`（值为 `neutral` 整条跳过）、`typeName`、`Type`、`version`、`PublicKeyToken`、`publicKey`、`processorArchitecture`、`versionScope` 折叠：`acc = acc*0x1FFFFFFF7 + (H(值) + 0x1FFFFFFF7*H(属性名))` | 8061 x86 13,325/13,325、ARM 118/118 |
| `H`（H39） | 4 路 65599（每路 `(lane*65599+b)&0xFFFFFFFF`，ASCII 大写先小写），合成 `L3 + L2*K + L1*K² + L0*K³`，`K=0x1FFFFFFF7` | `wcp!RtlHashEncodedLBlob` 逐行读出 + 真机值名复现 |
| 家庭键 | 同上但**跳过 version**（VersionedIndex 的家庭键） | 13,159/13,159 |
| 键名段 | 小写、只留 `[a-z0-9._-]`（空格/括号被删）；超长省略为"前 half + `..` + 后 half"：组件名 40、部署名 24、culture 段 8 | 15,608 条全命中 |
| `f!` 值名 | 形态 = 小写 + 非法字符转 `_` + 连续 `_` 合并；形态含 `_` 或长度 >25 ⇒ `f!<前25>_<H(原始名)>`，否则 `f!<形态>` | 19,006 条零例外 |
| `f!` 值数据 | 仅当"值名后缀 ≠ 原始文件名"时写原始大小写 UTF-16LE；否则空（9,010 条空） | 同上 |
| `p!/s!/i!` 值名 | `<前缀>CBS_<包身份小写截断64>_<H(包身份)>`，数据 = 8 字节小端长度前缀 + ASCII（16299+：连续 `_` 先折叠成一个，总长仍 64）| 4,892/4,892 + 16299 全量 |
| `identity` | 清单 identity 的 `name, K=V, …`；`culture` 空/`*`⇒`neutral`、`en-*` 照抄清单大小写、其他小写；`versionScope` 规范成 `NonSxS` | 13,317/13,325（余 8 条见 §4） |
| `S256H` | 组件清单文件的 SHA-256 | 13,325/13,325 |
| `Catalogs\<key>` | 包 `.cat` 文件的 SHA-256；`CatalogThumbprint` 是同一 64 hex 的 REG_SZ 文本 | 792 键 + 1,703 thumb |
| `f256!` | `UTF-16("<base64(SHA-256(已落位文件))>!<组件版本>#")`，同一家庭跨版本拼接 | 18,783 条 |
| 根值 | `StoreFormatVersion="0.0.0.6"`、`StoreArchitecture`（x86=0/arm=5/ia64=6/amd64=9/arm64=12）、`ServicingStackVersions` | 真机实测 |
| ManifestCache 容器 | 28 字节块头 + LZNT1(format 2, chunk 4096)；解压出 `PcmH`：84 字节头 + 4 张 inn-id 表 + 8 字节/项 TOC + 载荷 | `src/Pcm.cpp`，容器层与表骨架 byte-exact |
| 微 DOM（MdHd） | `MdHd/MdSp/MdDl/MdDt/MdLc` 分节；`MdSp` 按串插入序 | `src/Microdom.cpp`，300/300 清单逐字节一致 |
| DCM 清单（16299+） | `DCM\x01` + msdelta 补丁；字典 = `wcp.dll` 资源 type 614 id 1，`ApplyDeltaB(DELTA_FLAG_NONE, dict, file[4:])` | `src/Dcm.cpp`，22,167 份全量 `bad=0` |
| `S256H`（现代） | **解压后**清单 XML 的 SHA-256（不是磁盘上的 DCM 字节） | 16299 22,167/22,167 |
| `f!` 值（现代） | 类型 4（REG_DWORD）文件状态位：1 / 0x21 / 0x41 / 0x61；非 1 由 `--file-flags` 输入 | 16299 34,285 条全类型 4 |
| `f256!`（现代） | 清单里 `<dsig:DigestValue>` 的 base64（落位文件被改过时与磁盘 SHA-256 不同） | 16299 全量 |
| `Drivers` 台账（16299+） | `Drivers\<arch>\<INF 名>\<base64(SHA-256(INF 字节))>`，值名 `<包身份>~<update 名>`、数据 0 | 686/686 |
| identity（现代） | `.inf.resources` 组件：整名小写后末段改回 `.Resources`；culture 逐字照抄（仅 `en-us`⇒`en-US`）；appid 的非 `en-US` culture 小写 | 16299 589/589 |

## 3. 必须当"安装历史"输入的值

下面这些值**在镜像内容里找不到依据**——它们记录的是"这台机器那次安装发生过什么"。
写侧把它们做成显式输入（CSV），而不是猜：

| 值 | 输入 | 为什么推不出 |
| --- | --- | --- |
| `c!<部署键>` | `--claims` | 组件由哪个部署的载荷安装，权威来源是 cab 里的 update.mum；8061 镜像已剥掉 cab |
| `CF` | `--flags` | 闭包校验位图（`corruptionrepair.cpp` 的 6 个位），只有走过安装/校验路径的组件才有值 |
| `MCP_<id>` | `--mcp` | 指向 ManifestCache 文件内的块偏移；块间有空隙 ⇒ 编码的是写入历史 |
| 版本节点 `(default)` | `--version-flags` | ManifestCache 写完清单后由工作栈置的位图 |
| 现代 `f!` 的非 1 状态 | `--file-flags` | REG_DWORD 状态位（0x21/0x41/0x61）编码的是文件落位/压缩历史 |
| `ManifestCacheIdentifier` / `UtilizedSpace_MCP_<id>` | `--cache` | 存储创建时生成，与 `H39` 无关（试过 30+ 候选全灭） |
| 旧桶归属（`SomeUnparsedVersionsExist`） | `--buckets` | 被更早服务栈暂存过的家庭挂在旧桶下 |
| `CT` 的不可推导部分 | `--ct` | 归属包不一定声明该组件（912 条） |
| `CatalogThumbprint` 的选择 | `--sessions` | 同一组件被多个包声明时，选哪个 `.cat` 由安装会话顺序决定 |
| `p!/s!/i!` 标记 | `--markers` | `i!` ≈ 包 installed 位，但 185 条例外来自功能启用状态 |
| "骨架家庭"（只有家庭键 + 空版本节点 + `SomeUnparsedVersionsExist`） | `--skeleton-families` | 内容被更早的服务栈写到已被裁剪的旧桶里（8419 有 112 个家庭这样，其中 55 个磁盘上还有载荷） |
| 根区形态（`ccpinterface`、`Installers\Shortcut`、`ExecutionState`…） | `--minimal` / `--ccpinterface` / `--installers-shortcut` / `--pending` | 不同镜像的首会话集合不同：8061 x86 有 ExecutionState+PendingXml+Ngen+ccpinterface，8061 ARM 只有栈表，8419 有 ccpinterface+Installers\Shortcut+LastScavenge 而没有任何会话值 |

输入 CSV 可用 `E:\ocid_verify\keyfold\mkinputs.py <真机转储> <前缀>` 从参考台账一键导出。

## 4. 已知不可推导项（逐字节复刻的边界）

1. **组件 identity 的 8 条大小写**（`Microsoft-Windows-ServicingStack-{Base,Admin,Ds,Net,Termsrv}-Deployment`、
   `PseudoTool-HashIDSpy*`）：真机存的是更早一份清单的写法，当前镜像里的清单是小写。
   它会连锁改掉这些组件的键后缀、`appid`，以及 5 条 servicing stack 部署记录。
2. **5 条 `Microsoft-Windows-ServicingStack-*-Deployment` 部署记录**：864 个 `.mum` 一条都没声明
   它们；真机的 payload 指向别处（`Microsoft-Windows-Foundation-Package~…~Foundation`）⇒
   这 5 条及其 `p!/s!/i!`（17 条值）只能作为安装计划输入。
3. **f256 多段拼接的顺序**：同一家庭里同一文件跨版本（例 `6.2.8061.0` 与 `4.0.8061.0`）会拼成一个值，
   段序 = 安装顺序。真机 61 条多段值里 44 升序 / 17 降序，与架构、文化、CF 都无相关性 ⇒ 只能抄。
4. **`CT`/`UnstagedFiles`/个别 `c!`、`CatalogThumbprint`**：来自会话历史（哪次 Resolve 真正落盘），
   镜像内无痕迹。
5. **ManifestCache 块外层的 28 字节头**（`+0x00/+0x08` 两个 u64、`+0x10` u32）：穷举
   fold64/H39/SHA1/256/512/MD5/CRC32/Adler32 × {压缩流、PcmH、清单 XML、XML 的 UTF-16、
   `ManifestCacheIdentifier`} 全灭。它只影响 `_blobs.bin` 缓存文件，不影响 COMPONENTS。
6. **PCM 里文档第 0 元素记录的 `b0` bit4..6**：真机来自未初始化栈字节（0..7 都出现），
   没有语义，只能照抄或任意填。
7. **type6 表的 `flags.bit0`**：清单里构造不出触发条件，按来源表照抄；重装时由安装计划带入。
8. **8419 追加的三类**（8061 没暴露）：
   - 组件 identity 里 22 条文化标签被小写（`en-GB`→`en-gb`），比 8061 多一个数量级，
     且同一镜像里另有大量 `en-US` 保持原样 ⇒ 不是文化规则，是清单来源不同；
   - 30 条 `p!/s!/i!` 的 payload 指向 `Microsoft-Windows-SKU-Foundation-Package-{base,admin,ds,net,termsrv}`，
     而我们的部署继承的是 `common-foundation-package`（哪一次交付装过 SKU 版属于安装历史）；
   - 23 条 `f256!comctl32.dll.mui` 的多段顺序是 **`6.0` 段在前、`6.2` 在后**（该镜像的 23 个语言全反），
     与"升序"假说相反 —— 再次证明段序是安装顺序而不是排序键。
9. **16299 追加的三类**：
   - **60 条 identity 是更早清单的写法**（`BgpNCProvider.Resources` 而清单是 `BgpNCProvider.resources`）；
     另 11 条能由 `.mum` 里 `<component><assemblyIdentity>` 的原文大小写解释（`dual_SDFLauncher.inf`）。
   - **14 条多余 `Drivers` 台账项**：Hyper-V/WVMS 虚拟驱动（`mpio.inf`、`msdm.inf` 一类），
     真机不收录，但其包 `CurrentState=0x70` 与收录的包相同 ⇒ 判据未知。
   - **`CatalogThumbprint` 的会话选择**：现代镜像的 `Sessions` 目录为空，多候选时只能按声明集合
     的最后出现顺序选；少数据会话历史。

## 5. 与真机对拍结果（2026-10，六个镜像都做到 0 差异）

把"安装历史"那一组输入喂满（`--markers/--installed/--claims/--flags/--buckets/--cache/
--ct/--version-flags/--mcp/--sessions`）之后，再对残余套一层**覆盖表**（`-Override`，
把只能从安装历史抄的值写死），组装结果与微软原版台账在逻辑层（键/值/类型/字节）完全一致：

| 镜像 | 真机规模 | 组装结果 | cbs-diff | 覆盖表 |
| --- | --- | --- | --- | --- |
| 8061 x86（`Q:`） | 43,100 键 / 120,978 值 | 43,100 键 / 120,978 值 | **only-in-a 0 / 0、only-in-b 0 / 0** | 54 行 |
| 8061 ARM（`P:`） | 613 键 / 1,408 值 | 613 键 / 1,408 值 | **only-in-a 0 / 0、only-in-b 0 / 0** | 73 行 |
| 8419 ARM（`E:\ocid_verify\mnt8419`） | 23,328 键 / 61,277 值 | 23,328 键 / 61,277 值 | **only-in-a 0 / 0、only-in-b 0 / 0** | 289 行 |
| 16299 amd64（挂载的 WIM） | 73,535 键 / 215,978 值 | 73,535 键 / 215,978 值 | **only-in-a 0 / 0、only-in-b 0 / 0** | 865 行 |
| 9600 amd64（Win8.1，挂载的 wim） | 10,069 键 / 33,493 值 | 10,069 键 / 33,493 值 | **only-in-a 0 / 0、only-in-b 0 / 0** | 71 行 |
| 17046 arm64（RS4 预览，挂载的 wim） | 62,881 键 / 153,580 值 | 62,881 键 / 153,580 值 | **only-in-a 0 / 0、only-in-b 0 / 0** | 245 行 |

六个镜像的 `cbs-verify-store` 也全部 `result=OK`（8419：`components=7350 deployments=911
catalogs=393 families=7310`）。

**没有覆盖表时**（纯清单 + 安装历史输入）的差额：x86 `only-in-a 1 键 / 42 值`、
`only-in-b 0 键 / 42 值`；ARM `only-in-a 0 键 / 20 值`、`only-in-b 0 键 / 53 值`；
8419 `only-in-a 2 键 / 89 值`、`only-in-b 0 键 / 253 值`；
16299 `only-in-a 30 键 / 779 值`、`only-in-b 2 键 / 420 值`；
9600 `only-in-a 16 键 / 55 值`、`only-in-b 0 键 / 28 值`；
17046 `only-in-a 14 键 / 164 值`、`only-in-b 1 键 / 182 值`。
按成因分（x86）：f256 段序 24/25、identity 大小写 8/8、appid 大小写 5/5、多余 CT 3、
`UnstagedFiles` 2、多余 `c!` 1、多余 `CatalogThumbprint` 1、多余 Catalogs 键 1 ——
**全部落在上面第 4 节的不可推导清单里**，没有一项属于算法缺口。
16299 的 779/420 里最大一块同样是第 4 节第 9 条（60 条 identity、14 条虚拟驱动、会话派生
`CatalogThumbprint`），其余是 `f256` 段序与个别 `c!`。
ARM 侧多出的 33 条 `p!/s!/i!` 是因为 ARM 镜像只有 4 个包、没有 Foundation 包，
清单里推出的部署组件拿不到 Windows Foundation 交付名字（x86 侧已由"清单独有部署继承
Foundation 交付 payload"复刻）。8419 的 253 条里最大一类是"骨架家庭"（已由
`--skeleton-families` 消掉 216 条），其余是第 4 节第 8 条那些安装历史。

覆盖表规模：x86 54 行、ARM 53 行、8419 290 行、16299 865 行、9600 71 行、17046 245 行（含 `set`/`remove`/`add-key`/`remove-key`）。
它只承担第 4 节那几类历史事实，不含任何"本可以推导"的值。

两个桶的规则（`--buckets` 把家庭指到更早的服务栈桶时）：真机在**两个桶**里都有这个家庭——
内容只落在真正暂存过它的桶（由 `--mcp` 的行反推），另一个桶只留骨架
（家庭键 + 空版本节点 + `SomeUnparsedVersionsExist = 0x00`）；当前桶的根键总是存在。

## 6. 复现

### 6.0 一条命令（推荐）

`ocid-assemble.ps1` 把"取输入 → 三次规划 → 合并 → 事务化落盘 → 提交前核对 → 自动生成覆盖表 → 再组装 → 最终 cbs-diff"串成一次调用。
只要目标镜像的 COMPONENTS 已挂载（`reg load`）且 SOFTWARE 可用，三个镜像都是**一条命令到 0/0**：

```powershell
cd D:\GitHub_Projects\OneCore-Image-Designer\Source\Native
pwsh -NoProfile -File .\build-servicing.ps1

# 8061 x86（Q:）：43,100 键 / 120,978 值，最终 0/0
pwsh -NoProfile -File .\ocid-assemble.ps1 `
    -ImageRoot 'Q:\' -SourceHive 'HKLM\OCIDCOMP' -InstalledHive 'HKLM\OCIDSOFT' `
    -StackVersion '6.2.8061.0 (fbl_core1_soc.110803-1830)' `
    -OutDir 'E:\ocid_verify\oneX86' -AutoOverrides -Transactional
#   pass 1: 43,098 键 / 120,978 值，差 42 值；覆盖表 54 行；pass 2: 0/0

# 8061 ARM（P:）：613 键 / 1,408 值，最终 0/0
pwsh -NoProfile -File .\ocid-assemble.ps1 `
    -ImageRoot 'P:\' -SourceHive 'HKLM\OCIDCOMPP' -InstalledHive 'HKLM\OCIDPSOFT' `
    -StackVersion '6.2.8061.0 (fbl_core1_soc.110803-1830)' `
    -OutDir 'E:\ocid_verify\oneARM' -AutoOverrides -Transactional
#   pass 1: 610 键 / 1,375 值，差 53 值；覆盖表 53 行；pass 2: 0/0

# 8419 ARM（挂载的 WIM + 两个 hive 文件）：23,328 键 / 61,277 值，最终 0/0
pwsh -NoProfile -File .\ocid-assemble.ps1 `
    -ImageRoot 'E:\ocid_verify\mnt8419' -SourceHive 'HKLM\OCID8419' `
    -SoftwareHivePath 'E:\ocid_verify\8419\SOFTWARE.hiv' `
    -StackVersion '6.2.8419.0 (winmain.120521-1835)' `
    -OutDir 'E:\ocid_verify\one8419' -AutoOverrides -Transactional
#   pass 1: 23,327 键 / 61,113 值，差 89+253 值；覆盖表 289 行；pass 2: 0/0

# 16299 amd64（挂载的 WIM，现代镜像：清单是 DCM 压缩）
pwsh -NoProfile -File .\ocid-assemble.ps1 `
    -ImageRoot 'E:\ocid_dism\mnt16299' -SourceHive 'HKLM\OCIDC16299' `
    -SoftwareHivePath 'E:\ocid_dism\16299cfg\SOFTWARE' `
    -StackVersion '10.0.16299.15 (WinBuild.160101.0800)' `
    -FileFlags 'E:\ocid_verify\one16299\inputs\file_flags.csv' `
    -OutDir 'E:\ocid_verify\one16299' -AutoOverrides -Transactional
#   pass 1: 73,559 键 / 216,337 值，差 767+408 值；覆盖表 865 行；pass 2: 0/0
#   现代镜像会自动启用：DCM 解码、f! DWORD、Drivers 台账、驱动部署 CatalogThumbprint
```

参数含义：`-SourceHive` 触发 `Extract-CbsInputs.ps1`（也可用 `-InputsDir` 复用已导出的输入，或逐个传 `-Markers/-Claims/...`）；`-ExtractDefaultBucket` 缺省取 `-StackVersion`（当前栈桶即默认桶）；`-AutoOverrides` 会用 pass 1 的 `hive-dump.txt` 与 `-DiffAgainst`（缺省 `inputs\truth.txt`）生成覆盖表再跑 pass 2；`-Transactional` 把注册表写入包进 KTM 事务。

### 6.0.1 打包成可开机镜像（P1 串联的最后一公里）

`Build-OcidImage.ps1` 把"台账 → 能开机的盘"做成一条命令：建 GPT 盘（ESP+MSR+NTFS）→ `dism /Apply-Image` → `bcdboot` → 备份并替换 `Windows\System32\config\COMPONENTS` → `reg load` 后 `cbs-scan`/`cbs-verify-store` → `dism /Get-Packages` 换前/换后逐条对比 → 分离磁盘。

```powershell
cd D:\GitHub_Projects\OneCore-Image-Designer\Source\Native

# 新建盘（8061 x86，ISO 里就是 staged 状态）
pwsh -NoProfile -File .\Build-OcidImage.ps1 `
    -Wim 'G:\sources\install.wim' -Index 1 `
    -VhdPath 'E:\ocid_verify\p5\ocid-8061-x86.vhdx' `
    -LedgerHive 'E:\ocid_verify\oneX86\final\COMPONENTS.hiv'

# 已有盘换台账（8061 ARM 那种 unstaged 盘）
pwsh -NoProfile -File .\Build-OcidImage.ps1 `
    -BaseVhd 'E:\ocid_verify\p5\arm-unstaged.vhd' `
    -VhdPath 'E:\ocid_verify\p5\arm-check.vhd' `
    -LedgerHive 'E:\ocid_verify\oneARM\final\COMPONENTS.hiv'
```

验证口径：`cbs-scan` 必须 `result: OK`；`cbs-verify-store` 必须 `result=OK`；`dism /Get-Packages` 换前/换后必须逐条相同（不同即台账的包状态与镜像不一致，会以警告退出 1）。`-BaseVhd` 是差分链时父盘要一起拷走，否则挂载失败。

注意：**同一磁盘签名的 VHD 不能同时挂着**（差分盘与它的源盘就是这种情况）。两个同签名磁盘同时 attach 时 DISM 会对镜像路径报
`Error: 87 / Offline image specified is the running system`，`dism /Get-Packages` 直接拒绝工作；脚本会把它当警告报出来。
做法是先 detach 源盘再跑（`diskpart`: `select vdisk file="…" detach vdisk`）。


### 6.0.2 CSI 落位执行（`csi-place`）

台账之外还有"文件落位"这条腿：`place-ops` 只把清单算成计划（JSONL），`csi-place` 负责真正动磁盘——
**载荷 → 正本（复制）→ 落点（硬链接）→ 最后统一上锁**。顺序不能颠倒，依据是实测：

* 真机 `fsutil hardlink list Windows\System32\basesrv.dll` 给出两条链接（落点 + `WinSxS\<组件键>\`），
  而 `packages\<组件键>\basesrv.dll` 只有一条 ⇒ CBS 把载荷**复制**进组件存储，落点才是链接；
* `WinSxS` 归 `TrustedInstaller`，管理员也只有读+执行，`CreateDirectory` 直接 Access denied ⇒
  必须启 **SeRestorePrivilege**（写侧绕过 DACL 检查）；
* 新建组件目录一旦先套上 `WinSxS` 的所有者/DACL，后续复制连管理员也写不进 ⇒ 先落文件、最后上锁。

```powershell
# 1) 把清单算成落位计划（--windows 要传 Windows 目录，不是盘根；--image-arch 可显式指定）
ocid_manifest place-ops --windows 'R:\Windows' --packages 'R:\Windows\WinSxS' --image-arch arm 'R:\Windows\WinSxS\Manifests' > place.jsonl

# 2) 执行（默认写 SDDL；排障可加 --no-acl 只看文件）
ocid_manifest csi-place place.jsonl
#   planned=1 placed=1 copied=1 linked=1 dirs=1 acl=1 storeDirSecured=1 already=0 skipped=0 missingPayload=0 failed=0
```

统计口径是"写进去不算完成"：`placed` 只统计**正本存在且落点确实是硬链接**的项（`csi-place` 用
`GetFileInformationByHandle` 比文件索引验证，`fsutil hardlink list` 也给出两条链接）。三种情况不会瞎猜：
含未展开宏（`unresolved` 非空）⇒ 跳过；载荷不存在 ⇒ `missingPayload` 记账、不建空文件；
正本与落点都在 ⇒ `already=1`、不重复复制（幂等）。

测试：`ocid_place_tests.exe` 118/118（新增 `TestCsiPlace`：真实 JSON 行解析、非 JSON 拒绝、
载荷复制+硬链接（文件索引相同）、幂等重跑、缺载荷、未展开宏）。冒烟里套过 SDDL 的目录会
**真的**变得只有 TI 能写 —— 清理时得 `takeown /f … /r` + `icacls … /grant …:(OI)(CI)F`，这本身
就是"最后上锁"生效的证据。
### 6.1 手工分步（首次用 6.0 已经不需要；保留用于逐步排障）

```powershell
cd D:\GitHub_Projects\OneCore-Image-Designer\Source\Native
pwsh -NoProfile -File .\build-servicing.ps1

# 0) 参考镜像的"安装历史"输入（替代以前散落在 E:\ocid_verify 的手工导出）
#    需要 COMPONENTS 已挂载（reg load HKLM\OCIDCOMP ...），SOFTWARE 可给文件或已挂载的键：
pwsh -NoProfile -File .\Extract-CbsInputs.ps1 `
    -Hive 'HKLM\OCIDCOMP' -OutDir 'E:\ocid_verify\inputs8061' `
    -DefaultBucket '6.2.8061.0 (fbl_core1_soc.110803-1830)' `
    -InstalledHive 'HKLM\OCIDSOFT'
#    产出 markers/claims/flags/buckets/cache/ct/version_flags/mcp/skeleton_families/
#    packages_state/root_info.json + truth.txt（同时是 cbs-diff 的基准）

# 1) 组装：输入全部来自上一步的 -OutDir（-RootInfo 自动展开镜像形态：
#    ExecutionState/PendingXmlIdentifier/历史栈条目/--minimal/--ccpinterface）
pwsh -NoProfile -File .\assemble-cbs-store.ps1 `
    -ImageRoot 'Q:\' `
    -StackVersion '6.2.8061.0 (fbl_core1_soc.110803-1830)' `
    -OutDir 'E:\ocid_verify\asmx' -HiveName 'OCIDX.hiv' -Transactional `
    -Markers 'E:\ocid_verify\inputs8061\markers.csv' `
    -Installed 'E:\ocid_verify\inputs8061\packages_state.txt' `
    -Claims 'E:\ocid_verify\inputs8061\claims.csv' `
    -Flags 'E:\ocid_verify\inputs8061\flags.csv' `
    -Buckets 'E:\ocid_verify\inputs8061\buckets.csv' `
    -Cache 'E:\ocid_verify\inputs8061\cache.csv' `
    -Ct 'E:\ocid_verify\inputs8061\ct.csv' `
    -VersionFlags 'E:\ocid_verify\inputs8061\version_flags.csv' `
    -Mcp 'E:\ocid_verify\inputs8061\mcp.csv' `
    -RootInfo 'E:\ocid_verify\inputs8061\root_info.json' `
    -Override 'E:\ocid_verify\overrides_x86.csv' `
    -DiffAgainst 'E:\ocid_verify\inputs8061\truth.txt'
#    → written keys=43097 values=120978、对 truth.txt 0/0
#    注意：**不要**传 -SkeletonFamilies。该开关只给"旧桶内容已被裁剪、只剩骨架"的镜像
#    （8419 ARM）；8061 x86/ARM 的旧桶有真实内容，传了会把内容家庭误判成骨架
#    （实测：x86 传了以后 201 键/562 值丢失、0/0 退化为差额）。

# 2) ARM（8061）：DefaultBucket 必须给"当前栈桶"（否则 118 个内容家庭会全被当成外移）
pwsh -NoProfile -File .\Extract-CbsInputs.ps1 `
    -Hive 'HKLM\OCIDCOMPP' -OutDir 'E:\ocid_verify\inputsARM' `
    -DefaultBucket '6.2.8061.0 (fbl_core1_soc.110803-1830)' `
    -InstalledHive 'HKLM\OCIDPSOFT'

pwsh -NoProfile -File .\assemble-cbs-store.ps1 `
    -ImageRoot 'P:\' `
    -StackVersion '6.2.8061.0 (fbl_core1_soc.110803-1830)' `
    -OutDir 'E:\ocid_verify\asmARM3' -HiveName 'ARMA.hiv' -Transactional `
    -Markers 'E:\ocid_verify\inputsARM\markers.csv' `
    -Installed 'E:\ocid_verify\inputsARM\packages_state.txt' `
    -Claims 'E:\ocid_verify\inputsARM\claims.csv' `
    -Flags 'E:\ocid_verify\inputsARM\flags.csv' `
    -Buckets 'E:\ocid_verify\inputsARM\buckets.csv' `
    -Cache 'E:\ocid_verify\inputsARM\cache.csv' `
    -VersionFlags 'E:\ocid_verify\inputsARM\version_flags.csv' `
    -Mcp 'E:\ocid_verify\inputsARM\mcp.csv' `
    -RootInfo 'E:\ocid_verify\inputsARM\root_info.json' `
    -Override 'E:\ocid_verify\overrides_arm2.csv' `
    -DiffAgainst 'E:\ocid_verify\inputsARM\truth.txt'
#    → written keys=610 values=1408、0/0
#    注意：ARM 这个镜像的 117 个家庭"旧桶有内容、当前桶有骨架"，绝不能传
#    -SkeletonFamilies —— 该开关只给 8419 那种旧桶已被裁剪、只剩骨架的镜像。

# 8419 ARM（单桶 6.2.8419.0，首会话/旧桶都已裁剪）：骨架家庭 + 根区开关 + 覆盖表
#   （同样可以全自动：先 Extract-CbsInputs，再 -RootInfo；-RootMinimal/-Ccpinterface/
#     -InstallersShortcut 由 root_info.json 自动带出）
pwsh -NoProfile -File .\assemble-cbs-store.ps1 `
    -ImageRoot 'E:\ocid_verify\mnt8419\' -Arch arm `
    -StackVersion '6.2.8419.0 (winmain.120521-1835)' `
    -OutDir 'E:\ocid_verify\asm8419' -HiveName 'OCID8419.hiv' -Transactional `
    -RootMinimal -Ccpinterface -InstallersShortcut `
    -Time '2012/6/12:1:8:0.193' `
    -StackEntries '6.2.7992.0 (fbl_fun_lad.110407-1318)|2012/5/22:9:36:2.871 6.2.7992.0 (fbl_fun_lad.110407-1318)' `
    -Markers 'E:\ocid_verify\8419\a8419_markers.csv' `
    -Installed 'E:\ocid_verify\8419\packages_state_8419.txt' `
    -Claims 'E:\ocid_verify\8419\a8419_claims.csv' -Flags 'E:\ocid_verify\8419\a8419_flags.csv' `
    -Cache 'E:\ocid_verify\8419\a8419_cache.csv' -Ct 'E:\ocid_verify\8419\a8419_ct.csv' `
    -VersionFlags 'E:\ocid_verify\8419\a8419_vflags.csv' -Mcp 'E:\ocid_verify\8419\a8419_mcp.csv' `
    -SkeletonFamilies 'E:\ocid_verify\8419\skeleton_families.csv' `
    -Override 'E:\ocid_verify\8419\ovr4.tsv' `
    -DiffAgainst 'E:\ocid_verify\8419\truth_8419.txt'
```

覆盖表由 `make-cbs-overrides.ps1` 从一次对拍的差集生成（`set`/`remove`/`add-key`/`remove-key` 四种行），
用法：`pwsh -NoProfile -File .\make-cbs-overrides.ps1 -Dump <组装后的 hive-dump> -Truth <真机转储> -Out <输出.csv>`。

### 6.2 事务化落盘（KTM）

`cbs-write` 支持 `--transactional`：整轮写入包进一个 KTM 事务
（`CreateTransaction` → `RegOpenKeyTransactedW` / `RegCreateKeyTransactedW` → `CommitTransaction`），
任何一行落键/写值失败就 `RollbackTransaction`，hive 文件保持原样。实测：

```
# 正常提交：内容与非事务完全一致（对真机 0/0）
ocid_manifest cbs-write plan-all.txt txn.hiv --transactional
  → written keys=43097 values=120978 badLines=0 transaction=committed
  → reg load + cbs-dump + cbs-diff 真机：only-in-a 0/0、only-in-b 0/0

# 注入坏行（前 500 行有效 + 1 行垃圾）：整体回滚
ocid_manifest cbs-write plan-bad.txt rollback.hiv --transactional
  → written keys=500 values=0 badLines=1 transaction=rolled-back（exit 1）
  → reg load 后该 hive 里没有任何键（500 个键全部回滚）
```

需要 Win8+ 的 KTM 服务（桌面版/Server 默认运行）；`--transactional` 不加则退回普通写入。`assemble-cbs-store.ps1 -Transactional` 会把开关透传下来（ARM 8061 走事务提交后对真机仍 0/0）。

### 6.3 文件 + 台账的原子性（顺序约定 + 提交前核对）

台账只描述"WinSxS 里应该有什么文件"。如果先写台账、文件却没落成，镜像就是坏的；
反过来文件多落了一些只是孤儿（台账没引用，不破坏任何组件）。所以原子性靠**顺序约定**：

```
1. 把载荷落进 <ImageRoot>\Windows\WinSxS\<组件键>\   （CSI 落位；失败就中止，此时台账还没动）
2. ocid_manifest cbs-verify-files plan-all.txt <WinSxS>  （提交前核对）
3. ocid_manifest cbs-write plan-all.txt COMPONENTS.hiv --transactional  （KTM 提交台账）
```

`cbs-verify-files` 逐个组件核对：`f!` 值条数 = 该组件目录里的文件数（清单文件
`<组件键>.manifest` 不计），缺目录 / 缺文件 / 条数不等都返回 1。`assemble-cbs-store.ps1`
默认在 `cbs-write` 之前跑这一步，不过就 `throw`（不写台账）；`-SkipFileVerify` 可跳过。

实测（两个镜像的"已落位"镜像）：

```
8061 x86  Q:\Windows\WinSxS ：components=13325 componentsWithFiles=9492 files=19006 dirsMissing=0 filesMissing=0 countMismatch=0
8061 ARM  P:\Windows\WinSxS ：components=118   componentsWithFiles=79   files=316   dirsMissing=0 filesMissing=0 countMismatch=0
反例（计划的组件目录不存在）           → dirsMissing=1，exit 1
反例（真实组件目录里换成不存在的文件名）→ filesMissing=1 + countMismatch=1，exit 1
```

这条核对同时是"计划正确性"的第二把尺子：`f!` 集合与磁盘文件集合必须一一对应，
任何清单解析偏差（多一条 f!、少一条、名字大小写错）都会在这里暴露，而不用等挂载 hive 后对拍。

### 6.4 台账自洽性核对（cbs-verify-store，不需要任何参照样本）

`ocid_manifest cbs-verify-store <hiveSpec>` 在**没有微软原版可对拍**时也能证明一份
COMPONENTS 是自洽的 —— OCID 自己从包集装出来的镜像走这一关即可：

| 检查 | 规则 |
|---|---|
| 组件键名 | `键名 == CbsComponentKeyName(记录里的 identity)` |
| `f!` 值名 | `值名 == CbsFileValueName(原始大小写文件名)`；16299+ 的 `f!` 是 REG_DWORD 文件状态位（1/0x21/0x41/0x61），只统计成 `modern-file-flags`，不按文件名推导 |
| 部署键名 | `键名 == CbsDeploymentKeyName(appid)` |
| `p!/s!/i!` 值名 | `值名 == CbsPayloadValueName(前缀, 载荷身份)`；出现 `modern-file-flags` 时按 16299+ 规则（连续 `_` 折叠、总长 64）|
| `c!` 引用闭合 | 每个 `c!<部署键>` 指向的部署键必须存在 |
| 部署 → 组件 | 每个部署的 appid 身份都有一条同名（同 pseudo-key）组件记录 |
| Catalogs | 键都是 64 个小写 hex |
| VersionedIndex | 每个家庭键都能由某个组件的 identity 折叠出来 |

实测（我方产物与真机都跑）：

```
我方 x86  OCIDX11.hiv ：components=13325 deployments=2283 catalogs=792 families=13259 result=OK
真机 x86  OCIDCOMP    ：components=13325 deployments=2283 catalogs=792 families=13259 result=OK
真机 ARM  OCIDCOMPP   ：components=118   deployments=12   catalogs=0   families=235   result=OK
反例（给真实组件加一条指向不存在部署的 c!）→ missing-mark-target=2，exit 1
```

`assemble-cbs-store.ps1` 默认在 `cbs-write` 之后挂载新 hive 跑这一步（`-SkipStoreVerify`
可跳过），不通过就 `throw`。注意：两个桶里一共 13,259 个家庭键（当前桶 + 被更早服务栈暂存
的第二个桶，见第 3 节 `--buckets`），它们全部都能由组件身份折叠出来（`orphan-family=0`），
所以这一关不受"同一家庭挂在两个桶下"影响。

## 7. 剩余工作

1. **PCM 表构建器**（`BuildPcmTables`）：把清单 XML 直接编译成 35 张表。容器、TOC、槽位、
   串池桶序、type6 规模定律、type 2/6/8/9/A/B 表编解码已自研并逐字节自证（3,041 份语料全过）。
   反编译路线（"路线二"）已把字段语义推进到下面这一步（详见 `E:\ocid_verify\pcm_schema_route2.md`）：
   - **先纠正一个前提**：`0x100B3E40–0x100BC7AE` 的 114 个函数**不是**表写入者，而是
     `CMicrodomManifestWalker` 的遍历/校验回调（产出 12 字节 `(ns,name,value)` 三元组）；
     真正的构建器是 `CCdfManifestBuilder::GeneratePCM`（x86 VA `0x100AB672`，
     `base\wcp\manifestparser\pcmc_pcmbuilder.cpp`），按 39 个固定 key 顺序 build + AddBlob。
   - **68 B 文档记录：完全解出，纯清单可重建（600/600 逐字节 identical）**。`[3]`=dependency 数
     （2757/2757）、`[9]`=有 `<mvid>` 则 7、`[11]`=有 `<deployment>` 则 `2*ndep+nfile`、
     `[1]`=标志位（bit0 恒置、bit3=`<deployment>`、bit4=`<noInheritable>` 或 `<gac>` 且无
     `sourcePath=`）；`[14]` 仍未解（2,509 份 -1 / 248 份 0）。
   - **96 B 文件记录**：24 dword 中 18 个有公式；`[2][3][4][10][15][16][18]` 是 builder 内部
     intern 小索引（`[14]=3+4k`、`[16]=base+30k`、`[18]≈[4]`），需复刻 intern 顺序。
   - 48 B/16 B dependency、20 B（`<file hash=>` 的 SHA-1）、32 B（`<dsig:DigestValue>` 的
     SHA-256）、36 B（`<gac>`）布局全部解出，删除对应 XML 即整条消失（knockout 证实）。
   - 仍不可推导：MdHd `b0` bit4..6（未初始化栈字节）、type6 flags.bit0、外层 28 B 包装头
     h0/h8/h10。
   - 后续任务（"路线四"，已完成到 C++ 构建器）：读 `GeneratePCM` 的 intern helpers + ladder 实验，做到"无 oracle"
     的整份 PCM 重建。**当前进度**：C++ 构建器 `src/PcmBuild.cpp`（`ocid_manifest cbs-pcm-build`）
     直接由清单 XML 产出整份 `PcmH`，回归闸门是 `ocid_manifest cbs-pcm-diff`（T1 槽数 / T2 type+len /
     T3 payload / T4 整文件）。全语料 2,757 对：
     **T1 2,752（99.8%）、T2 2,751（99.8%）、T3 2,746（99.6%）、T4 整文件 2,746（99.6%）**，
     剩余 11 份都是**单样本形态**：`00367` t11 身份表 (列,id) 对顺序、`00487` 多一组句柄记录、
     `00491/00494/01426` t10 行序与 catRank/t9 f1 语义耦合、`01633` 16B uidx 列表、`01769` 池内 tie 交换、
     `02496/02512` 槽数 −2/−3、`02962` dir 20B dword1=10、`03040` 28B installerRegistration payload；
     语料里各只出现一次，写规则就是打补丁 ⇒ 如实留白。
     **已实现**：表槽插入序 = `PCM_KEYS` 序（1,2,9,17,666,210…）、`f2` = 前置记录槽号定律、
     sdddef 16B / `<directory>` 20B / `<memberships>` / `<string>` / `<rescache>` / `<file><hash>` 16B
     记录布局、表头 flags（0x10/0x20/0x40/0x01）、两个 intern 时序修正、`<signatureInfo>` 8B
     `[flags,0]` 记录、96B `[14]`=自身槽号−1（3,244/3,244 实证）、96B `[19]`/16B hnum 槽号、
     依赖记录前置与 `<gac>` 位置、`<firewallRule>` 聚合、key-17 `f0`=`Hash64CodePoints`。
     **容器物理布局**：entry 哈希表不是固定 7/72/722 阶梯，而是增长模型——初始 7 桶 / capacity 35；
     每插入一项后若 `count > capacity` 则 `newN = 2*count`、`capacity = 5*newN`，旧桶按桶序
     tail-append 重建；物理序 = 桶 0..N-1 依次展开。桶索引 = `RtlHashLBlob(payload)` 的低 32 位
     mod N（`h = h*0x1FFFFFFF7 + word_le32`）。
     **唯一不能推导的输入**：文档第 0 元素记录的 `b0` bit4..6 来自真机未初始化栈字节
     （0..7 都出现，2,757 份里 984 份只有给对 `b0_extra` 才逐字节一致）⇒ `cbs-pcm-build` 的
     第三个参数就是它，按输入处理，不做猜测。
     中间产物：`E:\ocid_verify\pcm_route9\report.md`（逐条规则 + 真机字节依据 + 剩余单样本）、
     `E:\ocid_verify\pcm_route7\`（Python 参考实现 `buildpcm_self*.py`、`build_all.py` 语料回归）。
   - **仍开放（11 份，单样本）**：t11 身份表 (列,id) 对顺序 1、多一组句柄记录 1、t10 行序 × catRank 3、
     16B uidx 列表 1、池内 tie 交换 1、槽数 −2/−3 共 2、dir 20B dword1 1、28B installerRegistration payload 1。
     前 34 份（原六族）已在 route9 解出：t8 行按 kind GUID `Data1` 升序 + 头部 GUID 数组按 wcp 固定
     kind 表序、key-17 行按 `f0` 升序且目录路径 id 按 `SitOrder(h32)`、`<gac>` 文档序、categoryMembership
     8B + subcategory 串 id、key-9 `<string id>` 按 `SitOrder`、t10 按 `Hash64(subcategory)` 升序、
     68B `[11]` = 20B deployment blob 槽号。报告 `E:\ocid_verify\pcm_route9\report.md`。
   - 注意**PCM 不影响 COMPONENTS**（`MCP_*`/`(default)` 已经走 `--mcp`/`--version-flags` 输入）。
   - **已知失败白名单**：11 份单样本的完整文件名与形态固化在
     `Source/Native/OcidServicing/pcm-known-fails.txt`。全量 diff 的失败集必须恰好等于这份清单，
     否则视为回归或新形态，解释清楚再提交。
   - **第二套语料（6.3.9600）已接入**：把 `E:\windesktop\6.3.9600.16384…\backup1.wim` 只读挂载后，
     `Windows\WinSxS\ManifestCache\cd9d9638180dfb77_blobs.bin`（18,089,221 B）按 LZNT1 拆出
     **2,787 个 PCM 块**（2,784 个可用池键）；同镜像 3,025 份清单用
     `cbs-pcm-build --dcm-dictionary <9600 的 9,066 B 字典>` 构建后按串池（MdSp）配对：
     **2,784 对，T1 2,657（95.4%）/ T2 2,650（95.2%）/ T3 2,331（83.7%）/ T4 2,331（83.7%）**。
     453 份未过的差异按「槽数/文件大小是否相等」先分两桶：**325 份同槽数同大小**、**128 份少槽（-1/-2/-3 槽，-72/-80 字节）**。同大小桶再细分：
     ① **171 份只要把 `b0_extra` 给对（0..7 逐个试）就逐字节一致**（真机是未初始化栈字节，如 `0x81` vs `0xB1`；命中分布 3×48、1×32、2×35、7×27、6×24、5×3、4×2；和 8419 语料里 984 份需要 `b0_extra` 属同一类输入，无法从清单推导）⇒ 9600 实际 T4 = 2,331 + 171 = **2,502 / 2,784（89.9%）**；
     ② 282 份有真实内容差异（抽样见 8 字节槽我方写全零、真机是 `(1, uidx)` 一类记录，如 `1394.inf` 35 槽 firstdiff=8、`b..iagnostic.resources` 少 2 槽 firstdiff=7）。
     少槽桶仍是 MdHd 段偏移小 0x30/0x50、t10 记录对顺序、我方多一份 MdHd 三类。⇒ `b0_extra` 是跨版本共性输入（8419 约 36%、9600 这里 38%），剩下的真实规则差异（282）才是下一轮要修的项。
     复现：`E:\ocid_verify\pcm9600\_b0.py`（对 453 份失败逐个试 b0=0..7，输出 `_real_fails.json`）。
     另有 241 份清单没有配到块（多语种/缺块）。9600 的清单是 XML（少数 2,525 B 的是 DCM 压缩，需字典），
     9600 的 PCM 规则与 8419 有版本差异，这些差异是下一轮可直接开工的条目（有配对可逐字节验收）。
     复现脚本 `E:\ocid_verify\pcm9600\run9600.py`（拆块 `E:\ocid_verify\mcp\split8419b.py`）。

     **152 份真实内容差异的形态聚类（2026-10-20，按槽内 TOC 逐表对比）**：

     | 形态 | 特征 | 例子 |
     | --- | --- | --- |
     | `type 10` 记录对顺序 | 12/18 字节记录，前 4 字节（`7002…`/`7003…`）一致，后面是 u16 序列，真机与我们的**排列顺序不同** | `01341.pcm` slot 25：真机 `7002 0000 0300 0200 1800 1600` vs 我方 `7002 0000 0200 0300 1600 1800`（成对倒置）；`01014.pcm` slot 13：真机 `2,3,1 / 10,12,8` vs 我方 `1,2,3 / 8,10,12` |
     | `type 1` 单值 | 4/8/20 字节表，真机有非零值，我方写 0 或写另一个数 | `00777.pcm` slot 1：真机 `0x12` vs 我方 `0x10`；`01914.pcm` slot 8：真机 `(1, 0x514)` vs 我方 `(0,0)`；`02643.pcm` slot 10：真机第二 dword `-1` vs 我方 `9` |
     | 元素记录 `b0` bit4..6 | 1 字节未初始化栈位（`0x81` vs `0xB1`），会连带改变桶哈希 → 整块表的排放顺序全变（单文件看起来差 1500+ 字节） | `01166.pcm`：按槽比较只差 1 字节，按原始字节比较差 1534 字节 |

     前两类是 wcp 写侧的取值/排序规则（`b0` 类是输入依赖，不算规则差异），要继续逆 `wcp!PcmWrite*`
     才能定；已有可逐字节验收的配对语料。

     **逆向素材已就位（2026-10-20）**：`E:\ocid_dism\16299\x86\wcp.dll`（10.0.16299，2,680,728 B，x86，imageBase 0x10000000）
     有公共符号 `wcp.pdb`（key `DD6E26801E24039B6AB8AFC11F9D75831`，4,246,528 B），直接喂 dbghelp 可枚举
     **14,276** 个名字（注意：`symfull.exe` 要传 **pdb 路径**而不是 dll，传 dll 只拿到 434 个导出）。
     PCM 生成入口 `CCdfManifestBuilder::GeneratePCM` 在 **RVA 0x976AD**（0x100976AD，3,500 B），
     它把每张表交给 `CCdfTableBuilder<traits>` 的 `BuildTableIntoBlob`，再按 section id 登记进表清单。
     section id → traits 的一手映射（与我们的 `PcmBuild.cpp` 对得上）：

     | section | traits |
     | --- | --- |
     | 1 / 4 / 17 / 210 | `CStringTableCaseInsensitive` |
     | 7 / 8 / 10 / 11 / 13 | `CStringTableCaseSensitive` |
     | 3 / 5 / 666 | `CGuidTableTraits` |
     | 6 | `CReferenceIdentityTableTraits` |
     | 19 / 20–50 / 201 等整数表 | `CUlongTableTraits` |

     `type 10` 差异的直接原因已经逆到（2026-10-20，wcpwork 库）：

     - `CCdfTableBuilder<CStringTableCaseSensitive>::BuildTableIntoBlob`（RVA **0x9EE15**）在写表之前
       先 `BUCL::Implementation::QuickSort(TableEntries, SortCompare)`，排序键是每行第一个 dword；
     - `SortCompare`（RVA **0x9F710**）的实体只有一句：
       `result = *a2 != *a1 ? (*a1 < *a2 ? -1 : 1) : 0`——**按 f0 升序**；
     - 载荷格式（与我们的 `PcmEncodeRecordTable` 一致）：`u32 header = (count<<8)|flags`，
       `flags&0x10` = f1 用 u16、`flags&0x20` = f2 用 u16、`flags&0x40` = count<7（此时 **f0 数组不落盘**），
       然后 f0 数组（仅 count≥7）、f1 数组（4 字节对齐）、f2 数组。

     这解释 9600 的 `type 10` 残差：差异样本全是 `count=2/3/4`（`0x270/0x370`），**f0 不落盘**，
     而我们的行序来自 `subHash`（`PcmBuild.cpp` 的 type-10 分支按 `subHash` 排、`f0` 填的是
     `Hash64(to_string(f1))`），真机是「按自己的 f0 排」。要复现就得知道真机给这些行填的 f0 是什么：
     下一步反编译 `GeneratePCM`（RVA 0x976AD，已导出 `decomp_wcp_000976ad.c`）里 add 到 section-10 表的
     调用点，看 f0 取自哪个 id（字符串 id / uidx / 父键），再据此改 `PcmBuild.cpp` 的行序。

     **2026-10-20 续查（关键结论：f0 在这批语料里不可观测）**：

     - `GeneratePCM` 里 section 10 的表就是 `this+2101` 的
       `CCdfTableBuilder<CStringTableCaseSensitive>`，写表时按 `SortCompare`（只比 f0）排序；
       小表走 `BUCL::Implementation::InternalShortSort`（0xA2575），第一个比较相等就直接返回，
       **因此并列的行保持插入序**——真机差异样本都是「全部 subcategory 相同」的并列，行序=插入序。
     - 扫了全部可用语料（9600 2,787 块 + 8419 3,041 块 + route7/route9 8,245 块）：
       **没有一张 `type 10` 的 count ≥ 7**，所以 f0 数组从来不落盘，无法从样本里读出 f0。
     - 对 57 张差异表穷举了「行序 vs 候选键升序」：`IdentHash` 低 32 位 24/57、
       名字码点哈希高 32 位 30/57、`h32(name|typeName)` 37/57，均不是稳定规则；
       57 张表里 39 张是「全并列」，其中 n=3 的多数真机序是 `[1,2,0]`、n=2 多数是 `[1,0]`。
     - 结论：`type 10` 的行序需要知道真机**填行的插入序**（不是 f0 值），而插入序发生在 CMI 侧
       （`Handler_assembly_memberships_categoryMembership` 只做属性收集，不建表），
       下一轮应从 Cdf 表的 `AddObject` 调用者往回找。当前 `PcmBuild.cpp` 保持 `subHash` 序不动
       （单样本不硬凑），`OCID_PCM_DEBUG_T10=1` 可让 `cbs-pcm-build` 把 (f1,name,subcategory)
       打到 stderr，便于继续对拍。

     **2026-10-20 再进一步（`type 9` 串表才是主因）**：把 `type 10` 的行按
     (f1, nameStringId) 完整解出来对比后发现，**多数差异表行序本来就一致**，差异在 `nameStringId`
     上整体差 1（抽查 8 张：`02895.pcm` 真机 `[(2,0),(3,16),(1,18)]`、我方 `[(2,0),(3,15),(1,17)]`，
     `02432/00798/01774/01135/02191/00044` 同形；只有 `01341.pcm` 是真正的行序差——
     真机 `(3,24),(2,22)` vs 我方 `(2,22),(3,24)`）。

     原因在 `type 9`（串池）：真机把多组串合**一个** `type 9` 表（`3006…` 6 槽：
     `0100 0200 0c00 2100 d200 9a02 0c00 1300 0b00 1600 1500 1400`），
     我方拆成 5 个各 1 槽的表 + 1 个 5 槽表。串池合并方式不同 → 串 id 整体错位
     → `type 10` 的 `nameStringId` 差 1，并级联到其他引用串 id 的表。
     `01341` 的行序差则与 `nameStringId`（24 vs 22）同向：疑似真机按 f0=串 id 降序/升序排列，
     待串池对齐后再验。⇒ 下一步应先复刻 `type 9` 的串池合并规则（按串来源/表分组），
     再回头验 `type 10` 行序。

     **2026-10-20 更正（串池本身一致，缺的是一条 `type 2` 串槽）**：把 MdSp 串池
     （`type 1` 槽里的 `MdHd…MdSp` 段，count 在 `MdSp+8`、串从 `MdSp+12` 起 NUL 分隔）两侧
     逐串对比，`02895.pcm` **82 串完全相同**；真正的差异是 `type 2` 串槽少 1 条：
     真机 27 条、我方 26 条，缺的是 **`$(runtime.windows)\boot`**——它来自清单末尾的
     `<bfsvc xmlns="urn:schemas-microsoft-com:asm.v3" Flags="17" Source="$(runtime.windows)\boot" />`
     的 `Source` 属性。我方把这个元素的属性写进了元素记录但**没有对它做 string interning**，
     于是 `type 2` 少一条、所有串 id 整体后移 1（`type 9` 里真机 6 行含 `f1=33`，我方 5 行），
     `type 10` 的 `nameStringId` 也就跟着差 1。⇒ 修复点是把 `<bfsvc>`（以及同类
     `<rescache>`）的属性值按普通属性 interning 一次，而不是只写进元素记录。

     453 份未过样本按清单特征分类（`E:\ocid_verify\pcm9600\featclass.py`）：
     `languagePack` 族 168、`bfsvc/rescache` 族 89、**82 份是「非 DCM 的普通 XML 清单」**
     （`cbs-dcm-decode` 对其报 `not a DCM manifest`，属分类脚本把「解码器拒绝」误记成
     「DCM 解码失败」，实际走的就是普通 XML 路径，差异按下面同一套内容规则处理）、
     `categoryMembership+file` 61、无特殊元素 16、`signatureInfo/infFile` 等其余 37。
     即 9600 残差不是一条规则，而是五六个独立形态；其中 `bfsvc` 族已有上面这条明确机制。
     抽样 `…c..-controls.resources_6595b64144ccf1df_5.82.9600.16384_ar-sa_…`（`01966.pcm`）：
     46 槽/4,564 B 全同，只差 slot 15 的 `type 10` 18 B 记录——真机
     `7003 0000 0100 0200 0300 0000 0a00 0c00 0e00` vs 我方 `7003 0000 0100 0300 0200 0000 0a00 0e00 0c00`，
     本质是两行的 `(f1,f2)` 配对不同（真机 `(1,12),(2,14)`，我方 `(1,14),(3,12)`），
     不是单纯排序 ⇒ 归入 t10/f1 语义耦合一类。

     **已修（提交 `b2f8e2c`）**：`<bfsvc>` 的 `Source` 属性现按普通属性做 string interning
     （`PcmBuild.cpp` 的 bfsvc 分支），`Flags` 不入池。效果：`02895.pcm` 的池/串 id/`type 9`/`type 10`
     全部对齐、单份逐字节 consistent；但 9600 全语料 T4 仍是 **2,331**（该族 89 份里多数还差别的形态），
     属预期内的部分修复，零回归（33/33 单测通过）。

     同一份 `02895.pcm` 还揭示真机在 `<bfsvc>` 处有**两条**记录：`{Flags, N}`（8 B，`11000000 06000000`）
     + `{uidx}`（4 B，`60000000`），而合成器只写第二条。91 份 9600 bfsvc 差异样本全是这个 8 B 记录：
     `(17,6)`×49、`(17,7)`×35、`(16,8)`×2、`(16,7)`×2、`(24,2)`×1、`(16,2)`×1、`(16,10)`×1。
     第二字段 N 与清单元素计数的相关性（91 份全量统计，`E:\ocid_verify\pcm9600\bfs_rows.json`）：
     **N 等于 `categoryMembership` 条数 49/91**，等于 `categoryMembership+1` 37/91（即 `N = cat + [1]` 覆盖 86/91），
     其余 5 份是 `(24,2)`/`(16,2)`/`(16,10)` 等离群样本（cat=0/4）。
     同理 `diagnostic.resources` 族（无 categoryMembership）的 N=6，对不上 cat，说明 N 不是单纯的 cat 计数，
     更像 walker 内部的行号（含固定偏移）。**已定（16299 x86 wcp 反编译）**：
     `CCdfManifestBuilder::AddBfsvc`（RVA `0x9C719`）发的 8 B 记录是
     `{ Flags, InternString(Source) }`——第二字段不是计数，而是 `Source` 值在
     `CSingleInstanceTable`（vtable+160 的串表）里 `FindOrAdd` 得到的**顺序 id**。
     实测 4 份（`02895/02107/02676/00751`）：我方 `inter.String(Source)` 返回 **6/8/2/2**，
     与真机记录的 N 完全一致（`Flags` 分别是 17/16/24/16）。
     ⇒ 已按此实现（`PcmBuild.cpp` 在 `MdHd` 槽前发 `{Flags, inter.String(Source)}`），
     91 份 bfsvc 样本的记录字节/位置与真机一致（`02895` 我方 5836 / 真机 5844，偏移差 8 B 来自另一条缺记录）。
     9600 全量 T4 仍 **2,331**（零回归、零提升）：该族还缺真机在 `MdHd` 前的 28 B `type 9` 记录
     （`3006 0000 0100 0200 0c00 2100 d200 9a02 0c00 1300 0b00 1600 1500 1400`，
     头部 `(count<<8)|0x30`），且两条 4 B uidx 仍被并成 8 B，尚未落地。

     **key-33 表（已修）**：上面那张 28 B `type 9` 的**第 4 行**就是 key 33，行内容为
     `(uidx, 记录槽号)`——`00044`（fi-fi diagnostic）是 `(96, 7)`（槽 7 即 8 B bfsvc 记录），
     `00024`（de-de manager-efi）是 `(129, 11)`；第一列正是该 `<bfsvc>` 元素在微域里的
     uidx（我方 `e.uidx` 实测同为 96 / 129），第二列是记录槽下标。全量里 84 个含 key-33 的
     9600 块只有三种取值（`(96,7)`×48、`(129,11)`×35、`(99,7)`×1），与 uidx 规则吻合。
     ⇒ `PcmBuild.cpp` 现在在注册表之前发射这张两列 CUlong 表并写 `reg[33]`。
     9600 全量：**T1 2,744（98.6%）/ T2 2,737 / T3 2,417 / T4 2,417（86.8%）**（原先 2,331）；
     8419 route9 全量 **T4 2,747/2,757（+1）**，零回归；三个原生测试套全过。

     **t10 行序（已修）**：`type 10` 的行序 = 该成员身份的 `IdentHash`
     （`CalculateKeysForBuiltinAttributes`）**低 32 位升序**——和算 catRank 用的是同一个哈希，
     但这里按**值**排，不是 `SitOrder` 的桶序。证据：9600 配对语料里 `type 10` 行数 ≥2 的
     表共 **642 张**，这条规则 **642/642** 命中真机行序。反例对照（同一 642 张）：
     按 `Hash64(subcategory)` 升序 264、按 f1 降序 418、按「全属性 64 位 key1」升序 182、
     按 f1 升序 99；按 `IdentHash` 的 `SitOrder` 桶序也不对（00022/00023 会翻成 `[3,1,2]`）。
     注意 **Culture=neutral 必须按身份表的定义形态省掉**（`IdentRows(..., true)`），补上 neutral 会
     破坏 00018/00028；`buildType` 也**不能**当 Type 折进去（会把 00018 打回去，且 02392 的真机
     type-4 身份表只有 5 行、不含 Type）。此前「57 张差异表里 IdentHash 低 32 位只中 24」是因为
     那批表的 `(f1,f2)` 配对本身也错（串 id 错位），不是行序判据无效。
     `PcmBuild.cpp` 的 key-2 分支现用 `rowKey[i] = IdentHash(IdentRows(AttrsOf(elems[cats[i]]), true)) & kM32`
     做 `stable_sort`（`subHash` 已删）。9600 全量：**T1 2,744（98.6%）/ T2 2,737 / T3 2,534 / T4 2,534（91.0%）**
     （原先 2,417，+117）；五个行序样本 `00018/00022/00023/00028/02392` 的 `type 10` 全部逐字节一致；
     33/33 原生单测通过。复现：`E:\ocid_verify\re\t10_collect.py`（全语料 doc 序 dump）→
     `E:\ocid_verify\re\t10_search_all.py`（穷举属性掩码/排序算子求唯一规则）；单样本对拍用
     `E:\ocid_verify\re\t10_check5.py`、`t10_one.py`。语料里仍无 count ≥7 的表，f0 列不可观测。

     **9600 收尾（`ff2af92` 之后的 41 份真实残差里修掉 45 份）**：全量
     **T1 2,763（99.2%）/ T2 2,756 / T3 2,578 / T4 2,578（92.6%）**（`ff2af92` 时 2,534），
     8419 route9 **2,751/2,757**（`ff2af92` 时 2,750）。逐族结论：
     - `<signatureInfo><signatureDescriptor DRMLevel="1300">`：8 B 签名 blob 是
       `{flags, DRMLevel}`，`flags` bit0 = 有 DRMLevel；去重键 `(flags<<16)|(level&0xFFFF)`（`0dc977b`）。
     - `f0` 撞车定序：取 `f1` **降序**（`OCID_SORT_MODE` 默认 1）；升序 2,544、`std::sort` 只比
       `f0` 2,545、降序 2,546 ⇒ 降序最优，但真机不是简单排序（01344 同表内 3 组升 1 组降），仍开放。
     - `<instrumentation>` 每个子节点（`events`/`counters`/`settings`）各发一条
       `{uidx}` 记录；`<counters>` 的 cache GUID 是 `218a49b1-0504-5949-adaa-7e78686acfbe`（`03672e4`）。
     - `<directory>` 无 sdd 时 ref 是 `-1`；文档记录 `[14]` 是有 `resourceType` 依赖时的**序号**，
       否则 `-1`；`REG_NONE` 的值类型是 0；依赖的 `wild` 是**清单级**（任一依赖 `language="*"`），
       无 `language` 属性按 neutral 且带 `0x10`，`optional="yes"` 再加 `0x01`（`67c62ab`）。
     - `<fveUpdateAI fveCommand>`：bfsvc 8 B 之后一条 4 B 记录（interned 值 id）、cache 记录
       GUID `bacf4a08-714f-4d8a-a69a-fe4e9c8ce62d`（rank 45）、自己的 (uidx,槽) `type 9` 表，
       **注册键 35**（bfsvc 是 33），必须在固定键序里发射（`292112b`）。
     - 根 `<registryKeys>`：只把**带 `perUserVirtualization` 属性**的直接子 `<registryKey>` 的 uidx
       拼成记录（00149 的 16 个 key 里唯一没有该属性的 `HKEY_CURRENT_USER\…` 被排除 → 15 项；
       02857 两项 → 8 B `{0x12,0x1f}`；驱动清单的 key 没有该属性 ⇒ 真机不发这条记录）。
       它是 cache 记录，GUID `a39bfc2e-d678-8c45-ac19-0b5b6bdae296`（rank 46）（`3f150c4`）。
     - `<installerRegistrations><installerRegistration factoryClsid=…>`：每个 registration
        （a）在**依赖组之后、文件组之前**发一条 28 B 常量记录 `{0,-1,-1,-1,2,-1,0}`；
        （b）让 `<file>` 的**文件名先入串池**（于是 96 B `[4]=[18]=0`、sdd 8 B 记录是 `{1,-1}`、
        sdddef 名字 id=1，与没有该元素时相反）；（c）把 `factoryClsid`（GUID 文本 → Data1/2/3
        小端）作为 cache GUID 放进头部 GUID 表**最前**；（d）发一张 `type 8` 表 `{guidIdx, 槽}`
        并以**注册键 3** 登记（`kKeys` 里紧跟 key 1 之后）。10 份里 7 份整文件一致
        （`01493/02774/02229/02781/01109/01761/03074`）；`01078`（SMBServer）仍开放（`6468de6`）。
        `01247`（isolationautomation）的 `<comClass progid=…>` 复用同一张 key-3 表，已由
        `32064aa` 解决（见 §10.1）。
     - `<genericCommands>`：按 `install` 属性**分两组**，每组一条记录 = 组内 `<genericCommand>` 的 uidx 数组（4 B 起，10 条一组就是 20 B）；发射位置在 migration 记录**之后**、`sppInstaller` 之**前**（真机 01879 槽序 82 migration / 83 install / 84 uninstall / 85 sppInstaller）；cache GUID install `104aa381-5642-6a43-89d6-794b97ca407c`、uninstall `5dfd45c1-9b1b-3847-9961-64034a3da28f`，按 `…sppInstaller < uninstall < install` 排进 GUID 表并注册进注册键 666（`79a323a`）。另：key-666 表的 `f0` 列 = 该行 **cache GUID 的 Data1**（不是行号），只有行数 ≥7 才落盘（`00549` 的 7 行真机就是逐行 Data1 升序）。
     - 仍开放（185 份 = 171 份只差文档根记录 `b0` bit4..6 的不可复现样本 + 14 份真实残差，
       9600 T4 2,599/2,784 = 93.4%，`58275a5` 之后）：
       `t6` 撞车 4 份（`00852/01220/01344/01897`）与 `type 11` 撞车 1 份（`00611`）；`t2` 串池 intern 次序 6 份（`00003` 里 `HKEY_LOCAL_MACHINE` 与 `ja` 对调、`02995` 里 `amd64` 与中文串对调）；`<WinsockAppPermittedLspCategories>` 4 份（`00028/01888/01915/02232`，12 B `{Path,PermittedLspCategories,Extra}` 记录与 cache GUID 已发，只差 key-30 表的 `f1`）；`00549`（+5 槽，两条 4 B 记录）；`00028` 的 cache 簇多 1 槽（serviceData/sdd 记录位置）；其余 −1~−7 槽若干。
       已定论但**不可由比较器复现**的部分：`00852`（profsvc，85 行 event 本地化表）三组 `f0` 撞车的真机次序是「2 组 f1 降、1 组 f1 升」，`00611` 的 `type 11` 两表一升一降 ⇒ 真机用的是**不稳定排序 + 哈希表迭代序**，取决于输入向量的构造顺序；`E:\ocid_verify\pcm9600\_tiescan.py` 扫全语料已确认每条撞车组内部单调、组间方向不一致，所以改比较器永远对不上，必须知道真机输入序（或换一条记录生成路径）。`<windowClass>` 侧：真机把每个 class 名入池并发 `type 2` 串槽外，还发 4 张 `type 4/5` 表（行 `{-1, a, b}` 三元组、b/c 引用串 id）；这 4 张表的语义（哪种元素被折叠成 `{-1,a,b}`）尚未定。（已解：见 §10.1 —— 它们就是身份表，类名先占串池 id 2..28/2..30，其后全部 id 整体 +27。）

     **8419 语料里的 install 记录极端样本**（`x86_…pnpplugininstaller_1122334455667788_6.2.8419.0_neutral_~_…`，
     对应 `03040.pcm`，是该语料 2,757 个配对里唯一带 `<installerRegistrations>` 的清单）：
     它曾是 9600/8419 两批 install 族的样本来源——真机 37 槽 / 我方 35 槽，slot0 是 28 B
     `{0,-1,-1,-1,2,-1,0}`、slot1 是 8 B `{1,-1}`，`MdHd` 段内偏移相差 0x60（多一条 96 B）。
     现已由 `6468de6` 的规则整文件对齐（8419 route9 2,750 → 2,751 即这一份）。

     **9600 最大的单一残差族（51/282）**：`…b..iagnostic.resources_31bf3856ad364e35_6.3.9600.16384_<lang>_…`
     （49 份，槽数 53 vs 55）+ 2 份 `…re-memorydiagnostic…`（62/63 vs 68/69）。以 `de-de` 为例，
     真机比我们多的正是两条记录，且位置不同：
     槽 7 真机 `11000000 06000000`（8 B `{17,6}`）在我方不存在；
     槽 8～10 真机是 `MdHd(4428)`、`65000000`（4 B）、`60000000`（4 B），
     我方是 `MdHd(4428)`、`65000000`、`11000000 60000000`（8 B `{17,0x60}`）。
     ⇒ 即「每个 `<file>` 元素前少一条 8 B `{Flags,N}` 记录」＋「两条 4 B uidx 被错误并成一条 8 B」；
     与 bfsvc 的 `{Flags,N}` 是同一族记录（把 N 语义定出来可同时消掉这两族），
     对应的清单元素是 `<file>` + `<securityDescriptor name="WRP_FILE_DEFAULT_SDDL"/>`。

2. 覆盖表瘦身：第 4 节剩下的行已逐条试过推导（f256 段序试了架构/文化/CF/组件键哈希序，
   只能解释 56/60 的顺序且方向不定；CT 的归属包不在 `.mum` 声明集合里）⇒ 保留为输入。
   16299 的 865 行分类（`E:\ocid_verify\one16299g\overrides.csv`，按 `override` 种类 × 路径顶层）：

   | 种类 | 顶层 | 行数 | 说明 |
   | --- | --- | --- | --- |
   | `remove` | `Drivers` | 357 | 驱动台账的虚拟登记（`Drivers\amd64\<inf>\<hash>` 指向某个包） |
   | `remove-key` | `Drivers` | 28 | 同上，整键 |
   | `set` | `CanonicalData` | 323 | 部署记录（321 `Deployments` + 2 `Catalogs`） |
   | `remove` | `CanonicalData` | 58 | 部署记录（41 `Deployments` + 17 `Catalogs`） |
   | `remove-key` | `CanonicalData` | 2 | `Catalogs` |
   | `set` | `DerivedData` | 83 | `TransformSettingsVersion\{Ari,WpnData,…}` 的 `InvokeMap`/`SettingsVersion` |
   | `remove` | `DerivedData` | 12 | 同上 |
   | `set` | `CCPInterface` | 2 | 根区形态 |

   合计 865，与覆盖表一致。`CanonicalData\Deployments` 的 321 个 `set` 按值前缀再分：
   `CatalogThumbprint` 250、`i!`/`p!`/`s!` 各 20、`appid` 11——即部署状态（安装顺序、组件→包映射、
   目录指纹）与派生转换设置，都是**安装历史输入**，不能从清单推导。⇒ 「覆盖表瘦身」的可做部分
   只剩把 385 行 `Drivers` 换成确定性生成（14 条虚拟驱动规则已知），其余 480 行保持输入。
3. KTM 事务化：**注册表写入侧已完成**（`cbs-write --transactional`，见 6.1，提交/回滚都实测过）。
   "文件 + 注册表"的原子性已用顺序约定 + 提交前核对解决（见 6.2，`cbs-verify-files` 已接进
   `assemble-cbs-store.ps1`）；真正把文件复制也包进 KTM（`CreateFileTransacted`）没有做，
   因为顺序约定已经满足"落位失败则台账不变"，而事务化文件 API 对 WinSxS 的 TI ACL 不友好。

## 8. 现代镜像（16299+）：DCM 已解，全流程打通

16299 amd64 现已走通同一条 `ocid-assemble.ps1` 管道（`E:\ocid_verify\one16299c`）：
**73,535 键 / 215,978 值，对真机 `cbs-diff` 0/0**。关键在四处规则变更：

| 层 | 8061/8419（旧） | 16299+（现代） |
| --- | --- | --- |
| 组件清单 | 明文 XML | **DCM 压缩**（`DCM\x01` + `PA30` = msdelta 补丁） |
| `S256H` | 清单文件整份字节的 SHA-256 | **解压后清单 XML 整份字节**的 SHA-256 |
| `f!` 值 | 类型 3，数据是原始大小写文件名的 UTF-16LE | **类型 4（REG_DWORD）文件状态位**：1（29,725）/0x21（3,521）/0x41（1,038）/0x61（1）；非 1 由 `--file-flags` 输入 |
| `f256!` 值 | SHA-256(磁盘文件字节) 的 base64 | **清单里 `<dsig:DigestValue>` 声明哈希**的 base64（落位文件被补丁/压缩过时两者不同） |
| `CF` | 掩码 0x3F（sparsed/corrupt/closure） | 出现 0x3F 之外的新位（如 0x200）⇒ 全 DWORD 照写 |
| `p!/s!/i!` 值名 | 截 64，保留连续 `_` | **连续 `_` 先折叠成一个**，再截 64（例 `Product-onecore__…` → `product-onecore_…`） |
| `Sessions` | 有 `<Resolve>` 安装会话 | 常常是空目录 ⇒ 不是错误，退回声明集合选 catalog |

DCM 解码（`src/Dcm.cpp`）：字典 = `wcp.dll` 资源 type 614（0x266）id 1（`MANIFESTDICTIONARY`），
`msdelta` 的 `ApplyDeltaB(DELTA_FLAG_NONE, dict, file[4:])`。工具链上是一条全局开关：
`--dcm-dictionary <wcp.dll>`（各 plan 命令都认，`assemble-cbs-store.ps1 -DcmDictionary` 会自动
在 `<ImageRoot>\\Windows\\WinSxS\\*_servicingstack_*\\wcp.dll` 里找）。老镜像的 `wcp.dll` 没有该资源，
加载失败只提示、不清空管道（8061 清单本来也没压缩）。实测全量 22,167 份清单解压 72 秒、`bad=0`。

**字典也可以脱离 `wcp.dll`**：`ocid_manifest dcm-dump-dictionary <wcp.dll> <manifest-dictionary.bin>`
把资源导出成 9,066 字节的独立文件，之后 `--dcm-dictionary` 直接给这份 `.bin`（开关先按 DLL 读，
失败再按裸文件读）。`Extract-CbsInputs.ps1 -WcpDll <wcp.dll>` 会在输入目录里生成这份
`manifest-dictionary.bin`。16299 的 x86 与 amd64 servicing stack 导出的字典逐字节相同
（SHA-256 `0AD8F100…`），用它跑全量 22,167 份清单与用 `wcp.dll` 得到的计划文本 **逐字节一致**
（22,299,528 字节、`bad=0`）。这样装配期不再需要 `LoadLibraryEx` 去读微软二进制里的资源。


本轮两条大规则修好后，pass 1 的差额从 61,942/66,936 值降到 **779/420 值**（键 73,559/73,537），
自动覆盖表从 67,001 行降到 **865 行**，pass 2 之后 0/0。

### 8.1 `Drivers` 台账（16299+ 独有子系统）

真机 16299 多出一棵 `Drivers` 树：**686 个父键 / 686 个叶键 / 686 个值**（占当时覆盖表的大头）。

```
Drivers\<arch>\<INF 名>\<base64(SHA-256(INF 文件字节))>
    值名 = <包身份>~<.mum 里 <update name="…">>   数据 = REG_DWORD 0
```

- `<arch>` 取**驱动自己**声明的架构（WOW64 包里 `<driver>` 可能声明 `x86`，此时落 `Drivers\x86`，
  尽管宿主包是 amd64）。
- INF 文件按组件目录找：`<arch>_<inf>_…`、`amd64_dual_<inf>_…`、`amd64_dual_c_<inf>_…` 都要试
  （dual 目录里可能放的是 `c_display.inf` 而不是 `display.inf`），逐个候选目录检查文件存在。
- `--installed <csv>`（`CurrentState & 0x40`，CBS installed 位）过滤：只收置位包声明的驱动。
- 命令：`ocid_manifest cbs-plan-drivers <packagesDir> <winSxsDir> <outFile> [--installed <csv>]`；
  `assemble-cbs-store.ps1` 只在**现代镜像**（清单以 `DCM\x01` 开头）时自动跑这一步，
  或显式 `-ForceDrivers`。老镜像（8061/8419）的 `.mum` 里同样有 `<driver>` 声明，但真机台账里
  没有 `Drivers` 树 ⇒ 绝不能跑。
- 残余：14 条 Hyper-V/WVMS 虚拟驱动（`mpio.inf`、`msdm.inf` 一类）真机不收录，判据未知；
  已在覆盖表删掉。

### 8.2 根键形态与 `CCPInterface`

同一版本线里根区的形状也不一致，`Extract-CbsInputs.ps1` 会把形态记进 `root_info.json`，
`assemble-cbs-store.ps1 -RootInfo` 自动展开：

| 键 | 8061 x86 | 8061 ARM | 8419 | 16299 |
| --- | --- | --- | --- | --- |
| `ExecutionState` / `PendingXmlIdentifier` | 有 | 无 | 无 | 无 |
| `Configuration` | 空键 | 空键 | 空键 | 无 |
| `ccpinterface` / `CCPInterface` | 小写空键 | 无 | 无 | `CCPInterface` |
| `Installers` | 有（`Ngen\TicklerArchWoW`） | 无 | `Installers\Shortcut` | `Installers` |
| `NonCanonicalData` / `TransformSettingsVersion` | 无 | 无 | 无 | 有 |

对应开关：`--minimal`、`--ccpinterface-name <名>`、`--no-configuration`、`--noncanonical-data`、
`--transform-settings`、`--installers-root`。

### 8.3 现代 `CatalogThumbprint`

8061 的驱动部署**不写** `CatalogThumbprint`（也不写 `Catalogs` 标记）；16299 的驱动部署**要写**。
判定：`modernCatalog = !g_manifestDictionary.empty()`（给了 `--dcm-dictionary` 即现代镜像）。




## 9. 版本兼容矩阵（按构建号分档）

同一份台账格式在 Windows 7 → 11 之间改了好几轮，**不能用一个"现代/老"开关糊过去**：
6.3（Win8.1）就已经是 DCM 清单 + `f!` 写状态位，10.0.16212 才有 `Drivers` 台账，
10.0.16299 才把载荷值名折叠 `__`、把 identity 归一。`CbsProfile`（`include/ocid/CbsProfile.h`）
把这些差异做成一张按构建号取档位的表，所有计划命令开头都会打印当前档位：

```
版本档位：build=9600 family=win81 dcm=on f!dword=on cf32=on drvthumb=on drvledger=off payload-collapse=on identity-modern=off
```

| 构建号 | 清单 | `f!` | `CF` | `Drivers` | 其他 | 覆盖表 |
| --- | --- | --- | --- | --- | --- | --- |
| 6.1.7268 | 明文 | 字符串 | 无 | 无 | 有 `ccpinterface`/`Installers`/`ServicingStackVersions` | 未组装 |
| 6.2.8061 | 明文 | 字符串（带 `_<H39>` 后缀） | `4`/`5`/`12`（≤0x3F） | 无 | 无 `NonCanonicalData` | 54 / 73 |
| 6.2.8220 / 8400 / 8419 | 明文 | 字符串 | `5` 为主 | 无 | 8220 有 `LastScavengeCookie` | 289（8419） |
| 6.3.9600 | **DCM** | **DWORD 状态位** | 有 `0x100` 等超过 0x3F 的位 | 无 | 无 `ccpinterface`，identity 原文大小写 | 71 |
| 10.0.10586 / 14357 | DCM | DWORD | 同上 | 无 | 有 `NonCanonicalData` | 未组装 |
| 10.0.16212 | DCM | DWORD | 同上 | **有（骨架）** | `CCPInterface`（大写）、`TransformSettingsVersion` | 未组装 |
| 10.0.16299 | DCM | DWORD | 同上 | 1,374 键 | 载荷名折叠 `__`、identity 归一 | 865 |
| 10.0.17046 | DCM | DWORD | 同上 | 723(arm64)+5(arm)+5(x86) | **复合架构**（`arm64.arm`/`arm64.x86`/`wow64` 各占独立组件键前缀）、`CCPInterface`、无 `Configuration` | 245 |

档位阈值（`CbsProfileForBuild`）：

* `>= 9600`：`dcmManifests`、`fileValuesAreFlags`、`configFlagsFullDword`、`catalogForDrivers`、
  `sha256OfDecodedManifest`、`f256FromDigestValue`、`payloadNameCollapse`；
* `>= 16212`：`driversLedger`（`Drivers\<arch>\<INF>\<base64>` 台账）；
* `>= 16299`：`modernIdentity`（`.inf.resources` 整名小写、`en-us`→`en-US`）。

构建号来源（按优先级）：`--os-build <N>` → 镜像里的 `Windows\System32\ntoskrnl.exe` 版本资源 →
`--dcm-dictionary` 指向的 wcp.dll → 退化成"有字典当 9600、否则当 legacy"。
WinSxS 里没有 `ntoskrnl.exe` 时用 `--os-build` 明说。

**接一张新镜像的流程**：`Extract-CbsInputs.ps1` 抽输入（它会读 `ntoskrnl.exe` 写进
`root_info.json`）→ `ocid-assemble.ps1 -ImageRoot <挂载点> -InputsDir <输入> -AutoOverrides`。
若差额集中在某一整类值（例如所有 `f!`、所有 `S256H`），先核对打印出来的档位是否对得上这张
镜像的世代，再决定是补一条规则还是当安装历史写进覆盖表。

## 10. PCM 生成器（`cbs-pcm-build`）：当前状态与残差（2026-10-21）

两个语料的整文件（T4）命中率：

| 语料 | 对 | T1 槽数 | T2 type+len | T3 载荷 | T4 整文件 |
| --- | --- | --- | --- | --- | --- |
| 6.3.9600（2,784 对） | 2,784 | 2,779（99.8%） | 2,778（99.8%） | 2,599（93.4%） | **2,599（93.4%）** |
| 6.2.8419 route9（2,757 对） | 2,757 | 2,755（99.9%） | 2,755（99.9%） | 2,753（99.9%） | **2,753（99.9%）** |

9600 的未命中由 `E:\ocid_verify\pcm9600\_b0split.py` 分诊（对每份把 `b0_extra`
0..7 全试一遍）：**171 份只差文档根元素记录的 `b0` bit4..6**（真机未初始化栈字节，见下），
其余 14 份有真实内容差。所以 9600 的"可推导上限"是 **2,613 / 2,784（93.9%）**。

### 10.1 本轮新解出并已实现的规则

| 规则 | 提交 |
| --- | --- |
| t10（type 10）行序 = `IdentHash(...)` 低 32 位升序（全语料 642 张多行表 642/642） | `ff2af92` |
| `<signatureDescriptor DRMLevel=>` → 8B `{flags,DRMLevel}`（flags.bit0，9 份 INF） | `0dc977b` |
| `<instrumentation>` 每子节点一条 `{uidx}`；`<counters>` 用 GUID `218a49b1-0504-4959-adaa-7e78686acfbe`；68B 文档记录 `[14]` = resourceType 依赖序号（无则 −1）；根身份缺 language 时 flags |= 0x10 | `03672e4` |
| `REG_NONE` 值类型 = 0；依赖 `language="*"` 是清单级（全表生效 0x10）；无 sdd 的 `<directory>` ref = −1 | `67c62ab` |
| `<fveUpdateAI>` 三条记录：4B interned `fveCommand`、cache GUID `bacf4a08-714f-4d8a-a69a-fe4e9c8ce62d`、注册键 35 的 t9 表 | `292112b` |
| `<registryKeys>` 只登记**带 `perUserVirtualization`** 的直接子键，一条 uidx 数组记录 + cache GUID `a39bfc2e-d678-8c45-ac19-0b5b6bdae296`（注册键 46） | `3f150c4` |
| `<installerRegistrations>`：每个 `<installerRegistration>` 一条 28B `{0,-1,-1,-1,2,-1,0}`，`factoryClsid` 进 GUID 表，注册键 3 的 t8 表登记到该 28B 槽，文件名先于 sdd 入池 | `6468de6` |
| `<genericCommands>` 按 `install` 属性分组，每组一条 uidx 数组记录，位置在 migration 之后、sppInstaller 之前；GUID 安装 `104aa381-…` / 卸载 `5dfd45c1-…`；t8 的 `f0` = 该行 GUID 的 `Data1` 且按它升序（≥7 行才落盘） | `79a323a` |
| t2 字符串槽的 `SitOrder` 哈希用**码点哈希**（UTF-8 解码成码点后 4 路 65599，不折大小写），不是字节哈希（含非 ASCII 的 6 份） | `45e0c01` |
| 20B `<directory>` 记录按 20B 内容去重（重叠的 `<directory>` refs 共享一条） | `45e0c01` |
| `<memberships>`：每个 `<categoryInstance>` 各一条 8B `{uidx,-1}`（按内容去重），t9 按 `subcategory` 分组、`f1` = 该 8B 记录在发射序中的序号（0 起、去重前计数），同一 membership 再发一张 t6（`f0=Hash64(subcategory)&kM32`） | `45e0c01` |
| `<serviceData>` 只发一条 cache 记录，内容 = 全部 serviceData 元素的 uidx 数组，GUID `ad04d551-6848-4c46-a504-e6cc8a210a97` | `45e0c01` |
| `<windowClass>`（`01286/01287` 两份 9600 全对）：类名在 `BuildFileRecord` 里、`importPath` 之后按 `SitOrder(Hash64(name)&kM32)`（7 桶、插入序=文档序）入池，正好占 id 2..28/2..30；每个含类的 `<file>` 在摘要 blob 之前发一条 8B `{0, 该类是否 versioned="yes"}`；整张类名表 = key 8 的 t6（`f0=Hash64(name)&kM32`、`f1=串 id`、`f2=2`、`bit0=false`），排在 t11 之后；注册键 8 自然落到该表槽号。注意：多 `<file>` 且各自带类的样本至今未见，当前实现把全部类名累积进**同一张** key-8 表，每个含类的 `<file>` 各发一条 8B `{0,versioned}` | `22df921` |
| `<lodctrDat>`（`02037/00733` 两份 9600 全对）：一条 cache 记录 = 该清单**全部** `<lodctrDat>` 元素的 uidx 数组（02037 4 个 → 16 B `{117,151,185,219}`），GUID `9a3dcb9b-a796-41df-b455-77eba6a1ec1a`（rank 21，落在 `lodctr`=20 与 `rescache`=23 之间） | `32064aa` |
| `<comClass progid=…>`（`01247` 全对）：`progid` 属性与子 `<progid>` 文本在 `BuildFileRecord` 里、`importPath` 之后入池（id 2/3）；摘要 blob 之前发 28 B `{0,-1,-1,tlbidGuidIdx,threading,−1,0}` + 4 B `{clsidGuidIdx}`；`tlbid`/`clsid` 两个 GUID 都进头部 GUID 表（插入序），key-3 的 t8 登记 `{clsidGuidIdx, 28B 槽}`，key-4 的 t6 行 = F1 列放**槽号**、F2 恒 3（`{(slot4,3),(slot28,3)}`）。**没有 `progid` 属性的裸 `<comClass clsid=… />` 不发任何记录**（`01493/02229/01109/01761/03074/02774/02781` 七份） | `32064aa` |
| 缓存 GUID 表全序：147 块成对约束反推的最小改动序（t8 的 `f1` = GUID 表下标，行按 `Data1` 升序） | `58275a5` |
| `registryValue` 的 `operationHint`：`append` = 2，其余/缺省 = 1（20B 记录第 2 个 dword） | `58275a5` |

### 10.2 仍不可推导（已用全语料证据确认，不写"猜规则"的补丁）

* **MdDl 根元素记录 `b0` bit4..6**：实测同一 `(min,max)` 组合在不同样本上给出不同值
  （`_b0root.py`：`mn=8,mx=9` 的样本出现过 bit4 和 bit1；`mn=8,mx=9` 与 `mn=8,mx=15`
  各样本 bit 分别 4/1 和 7）⇒ 真机是未初始化栈字节，只能当 `b0_extra` 输入。
  `cbs-pcm-build` 的第 3 个参数就是它；**不要**用 `min/max` 之类去拟合。
* **f0 撞车的行序**（9600 剩 5 份：`00852`/`01220`/`01344`/`01897`/`00611`）：
  `_tiescan.py` 扫全语料，每组撞车内部单调、组与组之间方向不一致；这是 MSVC `std::sort`
  在未知输入序上的确定性打乱，输入向量不可观测 ⇒ 不追。
* **`02463` 的 membership 组序**：4 个 subcategory 组的发射序不是文档序、也不是任何
  哈希升降序（`t6` 的 `f1` 映射 LocalService=1/netsvcs=2/LSNR=3/NetworkService=4）。

复现脚本：`E:\ocid_verify\pcm9600\run9600.py`（全量）、`_mk.py <tag…>`（现编对拍）、
`_fams.py` / `_b0split.py`（分诊）、`_b0root.py`（b0 证据）、`_tiescan.py`（撞车扫描）、
`_ir2.py` / `_tp.py` / `_sl.py` / `_hex.py`（逐槽/逐表/逐字节）。`run9600.py` 写
`built/` 与 `fails.json`，**不能并发跑**。
