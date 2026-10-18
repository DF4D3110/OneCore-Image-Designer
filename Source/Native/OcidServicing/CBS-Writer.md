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
| `p!/s!/i!` 值名 | `<前缀>CBS_<包身份小写截断64>_<H(包身份)>`，数据 = 8 字节小端长度前缀 + ASCII | 4,892/4,892 |
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
| `p!/s!/i!` 标记 | `--markers` | `i!` ≈ 包已安装位，但 185 条例外来自功能启用状态 |
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

## 5. 与真机对拍结果（2026-10，四个镜像都做到 0 差异）

把"安装历史"那一组输入喂满（`--markers/--installed/--claims/--flags/--buckets/--cache/
--ct/--version-flags/--mcp/--sessions`）之后，再对残余套一层**覆盖表**（`-Override`，
把只能从安装历史抄的值写死），组装结果与微软原版台账在逻辑层（键/值/类型/字节）完全一致：

| 镜像 | 真机规模 | 组装结果 | cbs-diff | 覆盖表 |
| --- | --- | --- | --- | --- |
| 8061 x86（`Q:`） | 43,100 键 / 120,978 值 | 43,100 键 / 120,978 值 | **only-in-a 0 / 0、only-in-b 0 / 0** | 54 行 |
| 8061 ARM（`P:`） | 613 键 / 1,408 值 | 613 键 / 1,408 值 | **only-in-a 0 / 0、only-in-b 0 / 0** | 73 行 |
| 8419 ARM（`E:\ocid_verify\mnt8419`） | 23,328 键 / 61,277 值 | 23,328 键 / 61,277 值 | **only-in-a 0 / 0、only-in-b 0 / 0** | 289 行 |
| 16299 amd64（挂载的 WIM） | 73,535 键 / 215,978 值 | 73,535 键 / 215,978 值 | **only-in-a 0 / 0、only-in-b 0 / 0** | 889 行 |

四个镜像的 `cbs-verify-store` 也全部 `result=OK`（8419：`components=7350 deployments=911
catalogs=393 families=7310`）。

**没有覆盖表时**（纯清单 + 安装历史输入）的差额：x86 `only-in-a 1 键 / 42 值`、
`only-in-b 0 键 / 42 值`；ARM `only-in-a 0 键 / 20 值`、`only-in-b 0 键 / 53 值`；
8419 `only-in-a 2 键 / 89 值`、`only-in-b 0 键 / 253 值`；
16299 `only-in-a 30 键 / 779 值`、`only-in-b 2 键 / 420 值`。
按成因分（x86）：f256 段序 24/25、identity 大小写 8/8、appid 大小写 5/5、多余 CT 3、
`UnstagedFiles` 2、多余 `c!` 1、多余 `CatalogThumbprint` 1、多余 Catalogs 键 1 ——
**全部落在上面第 4 节的不可推导清单里**，没有一项属于算法缺口。
16299 的 779/420 里最大一块同样是第 4 节第 9 条（60 条 identity、14 条虚拟驱动、会话派生
`CatalogThumbprint`），其余是 `f256` 段序与个别 `c!`。
ARM 侧多出的 33 条 `p!/s!/i!` 是因为 ARM 镜像只有 4 个包、没有 Foundation 包，
清单里推出的部署组件拿不到 Windows Foundation 交付名字（x86 侧已由"清单独有部署继承
Foundation 交付 payload"复刻）。8419 的 253 条里最大一类是"骨架家庭"（已由
`--skeleton-families` 消掉 216 条），其余是第 4 节第 8 条那些安装历史。

覆盖表规模：x86 54 行、ARM 73 行、8419 289 行、16299 889 行（含 `set`/`remove`/`add-key`/`remove-key`）。
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
#   pass 1: 73,559 键 / 216,337 值，差 779+420 值；覆盖表 889 行；pass 2: 0/0
#   现代镜像会自动启用：DCM 解码、f! DWORD、Drivers 台账、驱动部署 CatalogThumbprint
```

参数含义：`-SourceHive` 触发 `Extract-CbsInputs.ps1`（也可用 `-InputsDir` 复用已导出的输入，或逐个传 `-Markers/-Claims/...`）；`-ExtractDefaultBucket` 缺省取 `-StackVersion`（当前栈桶即默认桶）；`-AutoOverrides` 会用 pass 1 的 `hive-dump.txt` 与 `-DiffAgainst`（缺省 `inputs\truth.txt`）生成覆盖表再跑 pass 2；`-Transactional` 把注册表写入包进 KTM 事务。

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
| `f!` 值名 | `值名 == CbsFileValueName(原始大小写文件名)` |
| 部署键名 | `键名 == CbsDeploymentKeyName(appid)` |
| `p!/s!/i!` 值名 | `值名 == CbsPayloadValueName(前缀, 载荷身份)` |
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
   - 后续任务（"路线四"，已推进到 4b）：读 `GeneratePCM` 的 intern helpers + ladder 实验，做到"无 oracle"
     的整份 PCM 重建。**当前进度**（严格 200 对验证集 T0..T4 = 199/200 不回退；全语料 2,757 对）：
     T1 槽数相同 2,105（76.4%）、T2 type+len 2,012（73.0%）、T3 payload 全同 1,711（62.1%）、
     T4 整文件同 1,711（62.1%，容器物理序修好后由 26.6% 提升；T3 通过者现在全部整文件一致）。已实现：表槽插入序 = `PCM_KEYS` 序（1,2,9,17,666,210…）、
     `f2` = 前置记录槽号定律、sdddef 16B / `<directory>` 20B / `<memberships>` / `<string>` /
     `<rescache>` / `<file><hash>` 16B 记录布局、表头 flags（0x10/0x20/0x40/0x01）与两个 intern 时序修正。
     **卡点①已解（容器物理布局）**：entry 哈希表不是"固定 7/72/722 阶梯"，而是**增长模型**——
初始 7 桶 / capacity 35；每插入一项后若 `count > capacity` 则 `newN = 2*count`、
`capacity = 5*newN`，旧桶**按桶序 tail-append** 重建；最终物理序 = 桶 0..N-1 依次展开。
桶索引 = `RtlHashLBlob(payload)` 的**低 32 位** mod N（`h = h*0x1FFFFFFF7 + word_le32`）。
实测 3,041 份真实 PCM：**2,969 份（97.6%）物理序完全一致**；剩 72 份（n 恰好刚过 36 的样本）
的差异是"缓存重写历史"——它们的 TOC 槽号（插入序）与物理序不同源，属安装历史，不可由清单推导。
这也解释了路线四 4b 的误判：它用固定阶梯近似增长模型，对刚好跨越增长的样本等价于"桶内逆序"。
中间产物在 `E:\ocid_verify\pcm_route3\`（`buildpcm_self.py`）、`E:\ocid_verify\pcm_route4\`
（`buildpcm_self2.py`、`pcm_route4b.md`）与 `E:\ocid_verify\pcm_schema_route2.md`。
仓库侧 `src/Pcm.cpp` 的 `BucketOrder()` 从一开始就是增长模型，可直接对拍（本次已用 Python 复核）。
**仍开放**：② MdHd `b0` bit4..6 是写侧未初始化栈字节（卡 684 份，只差这 1 字节，已反编译
`MicrodomWriterImplementation` 六个函数确认二进制里没有对它的位组合，属真 UB）；
③ count≥7 时落盘的表 `f0` 哈希函数仍有反例（卡 110+ 份）；④ 元素记录（`<file>`/`<registryKeys>`/
`<membership>`/`<localization>`）驱动的 key `1/2/9/16/17/201/210/666/34` 表内容（大族 0%）。
   - 注意**PCM 不影响 COMPONENTS**（`MCP_*`/`(default)` 已经走 `--mcp`/`--version-flags` 输入）。
2. 覆盖表瘦身：第 4 节剩下的行已逐条试过推导（f256 段序试了架构/文化/CF/组件键哈希序，
   只能解释 56/60 的顺序且方向不定；CT 的归属包不在 `.mum` 声明集合里）⇒ 保留为输入。
   16299 的 889 行里最大一块是 `Drivers` 台账的 14 条虚拟驱动 + identity 71 条历史写法。
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
| `Sessions` | 有 `<Resolve>` 安装会话 | 常常是空目录 ⇒ 不是错误，退回声明集合选 catalog |

DCM 解码（`src/Dcm.cpp`）：字典 = `wcp.dll` 资源 type 614（0x266）id 1（`MANIFESTDICTIONARY`），
`msdelta` 的 `ApplyDeltaB(DELTA_FLAG_NONE, dict, file[4:])`。工具链上是一条全局开关：
`--dcm-dictionary <wcp.dll>`（各 plan 命令都认，`assemble-cbs-store.ps1 -DcmDictionary` 会自动
在 `<ImageRoot>\\Windows\\WinSxS\\*_servicingstack_*\\wcp.dll` 里找）。老镜像的 `wcp.dll` 没有该资源，
加载失败只提示、不清空管道（8061 清单本来也没压缩）。实测全量 22,167 份清单解压 72 秒、`bad=0`。

本轮两条大规则修好后，pass 1 的差额从 61,942/66,936 值降到 **779/420 值**（键 73,559/73,537），
自动覆盖表从 67,001 行降到 **889 行**，pass 2 之后 0/0。

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
- `--installed <csv>`（`CurrentState & 0x40`）过滤：只收已安装包声明的驱动。
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



