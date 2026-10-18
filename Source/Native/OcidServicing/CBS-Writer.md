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

## 3. 必须当"安装历史"输入的值

下面这些值**在镜像内容里找不到依据**——它们记录的是"这台机器那次安装发生过什么"。
写侧把它们做成显式输入（CSV），而不是猜：

| 值 | 输入 | 为什么推不出 |
| --- | --- | --- |
| `c!<部署键>` | `--claims` | 组件由哪个部署的载荷安装，权威来源是 cab 里的 update.mum；8061 镜像已剥掉 cab |
| `CF` | `--flags` | 闭包校验位图（`corruptionrepair.cpp` 的 6 个位），只有走过安装/校验路径的组件才有值 |
| `MCP_<id>` | `--mcp` | 指向 ManifestCache 文件内的块偏移；块间有空隙 ⇒ 编码的是写入历史 |
| 版本节点 `(default)` | `--version-flags` | ManifestCache 写完清单后由工作栈置的位图 |
| `ManifestCacheIdentifier` / `UtilizedSpace_MCP_<id>` | `--cache` | 存储创建时生成，与 `H39` 无关（试过 30+ 候选全灭） |
| 旧桶归属（`SomeUnparsedVersionsExist`） | `--buckets` | 被更早服务栈暂存过的家庭挂在旧桶下 |
| `CT` 的不可推导部分 | `--ct` | 归属包不一定声明该组件（912 条） |
| `CatalogThumbprint` 选择 | `--sessions` | 同一组件被多个包声明时，选哪个 `.cat` 由安装会话顺序决定 |
| `p!/s!/i!` 标记 | `--markers` | `i!` ≈ 包已安装位，但 185 条例外来自功能启用状态 |

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

## 5. 与真机对拍结果（2026-10，两个架构都做到 0 差异）

把"安装历史"那一组输入喂满（`--markers/--installed/--claims/--flags/--buckets/--cache/
--ct/--version-flags/--mcp/--sessions`）之后，再对残余套一层**覆盖表**（`-Override`，
把只能从安装历史抄的值写死），组装结果与微软原版台账在逻辑层（键/值/类型/字节）完全一致：

| 镜像 | 真机规模 | 组装结果 | cbs-diff |
| --- | --- | --- | --- |
| 8061 x86（`Q:`） | 43,100 键 / 120,978 值 | 43,100 键 / 120,978 值 | **only-in-a 0 / 0、only-in-b 0 / 0** |
| 8061 ARM（`P:`） | 613 键 / 1,408 值 | 613 键 / 1,408 值 | **only-in-a 0 / 0、only-in-b 0 / 0** |

**没有覆盖表时**（纯清单 + 安装历史输入）的差额：x86 `only-in-a 1 键 / 42 值`、
`only-in-b 0 键 / 57 值`；ARM `only-in-a 0 键 / 20 值`、`only-in-b 0 键 / 53 值`。
按成因分（x86）：f256 段序 24/25、servicing stack payload 17、identity 大小写 8/8、
appid 大小写 5/5、多余 CT 3、`UnstagedFiles` 2、多余 `c!` 1、多余 `CatalogThumbprint` 1、
多余 Catalogs 键 1 —— **全部落在上面第 4 节的不可推导清单里**，没有一项属于算法缺口。

覆盖表规模：x86 100 行（88 `set` + 11 `remove` + 1 `remove-key`）、ARM 73 行（全是 `set`）。
它只承担第 4 节那几类历史事实，不含任何"本可以推导"的值。

两个桶的规则（`--buckets` 把家庭指到更早的服务栈桶时）：真机在**两个桶**里都有这个家庭——
内容只落在真正暂存过它的桶（由 `--mcp` 的行反推），另一个桶只留骨架
（家庭键 + 空版本节点 + `SomeUnparsedVersionsExist = 0x00`）；当前桶的根键总是存在。

## 6. 复现

```powershell
cd D:\GitHub_Projects\OneCore-Image-Designer\Source\Native
pwsh -NoProfile -File .\build-servicing.ps1

pwsh -NoProfile -File .\assemble-cbs-store.ps1 `
    -ImageRoot 'Q:\' `
    -StackVersion '6.2.8061.0 (fbl_core1_soc.110803-1830)' `
    -OutDir 'E:\ocid_verify\asmx' -HiveName 'OCIDX.hiv' `
    -Markers  'E:\ocid_verify\deployments_full.csv' `
    -Installed 'E:\ocid_verify\packages_state.txt' `
    -Claims   'E:\ocid_verify\cbs_claims.csv' `
    -Flags    'E:\ocid_verify\cbs_flags.csv' `
    -Buckets  'E:\ocid_verify\buckets.csv' `
    -Cache    'E:\ocid_verify\cache.csv' `
    -Ct       'E:\ocid_verify\ct.csv' `
    -VersionFlags 'E:\ocid_verify\version_flags.csv' `
    -Mcp      'E:\ocid_verify\mcp_values.csv' `
    -Pending  'a4dfe6df8352cc01310e00004c08480d' `
    -Override 'E:\ocid_verify\overrides_x86.csv' `
    -DiffAgainst 'E:\ocid_verify\truth_all.txt'

# ARM（8061）：根值走 --minimal（首会话状态在本镜像里不存在），历史栈条目单独给
pwsh -NoProfile -File .\assemble-cbs-store.ps1 `
    -ImageRoot 'P:\' `
    -StackVersion '6.2.8061.0 (fbl_core1_soc.110803-1830)' `
    -OutDir 'E:\ocid_verify\asmb' -HiveName 'OCIDA.hiv' `
    -Markers 'E:\ocid_verify\arm_markers.csv' -Installed 'E:\ocid_verify\packages_state_arm.txt' `
    -Claims 'E:\ocid_verify\arm_claims.csv' -Flags 'E:\ocid_verify\arm_flags.csv' `
    -Buckets 'E:\ocid_verify\arm_buckets.csv' -Cache 'E:\ocid_verify\arm_cache.csv' `
    -VersionFlags 'E:\ocid_verify\arm_vflags.csv' -Mcp 'E:\ocid_verify\arm_mcp.csv' `
    -RootMinimal `
    -StackEntries '6.2.7992.0 (fbl_fun_lad.110407-1318)|2011/8/4:6:42:41.654 6.2.7992.0 (fbl_fun_lad.110407-1318)' `
    -Override 'E:\ocid_verify\overrides_arm.csv' `
    -DiffAgainst 'E:\ocid_verify\truth_arm.txt'
```

覆盖表由 `make-cbs-overrides.ps1` 从一次对拍的差集生成（`set`/`remove`/`remove-key` 三种行），
用法：`pwsh -NoProfile -File .\make-cbs-overrides.ps1 -Dump <组装后的 hive-dump> -Truth <真机转储> -Out <输出.csv>`。

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
   - 后续任务：读 `GeneratePCM` 的 intern helpers + ladder 实验，交付"无 oracle"的
     `buildpcm_self.py`（≥200 份语料整份逐字节一致），产物在 `E:\ocid_verify\pcm_route3\`。
   - 注意**PCM 不影响 COMPONENTS**（`MCP_*`/`(default)` 已经走 `--mcp`/`--version-flags` 输入）。
2. 覆盖表瘦身：第 4 节剩下的行已逐条试过推导（f256 段序试了架构/文化/CF/组件键哈希序，
   只能解释 56/60 的顺序且方向不定；CT 的归属包不在 `.mum` 声明集合里）⇒ 保留为输入。
3. 然后进入 KTM 事务化（把上面所有写入包进事务，失败可回滚）。
