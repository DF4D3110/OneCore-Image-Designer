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
2. **ManifestCache 块外层的 28 字节头**（`+0x00/+0x08` 两个 u64、`+0x10` u32）：穷举
   fold64/H39/SHA1/256/512/MD5/CRC32/Adler32 × {压缩流、PcmH、清单 XML、XML 的 UTF-16、
   `ManifestCacheIdentifier`} 全灭。它只影响 `_blobs.bin` 缓存文件，不影响 COMPONENTS。
3. **PCM 里文档第 0 元素记录的 `b0` bit4..6**：真机来自未初始化栈字节（0..7 都出现），
   没有语义，只能照抄或任意填。
4. **type6 表的 `flags.bit0`**：清单里构造不出触发条件，按来源表照抄；重装时由安装计划带入。

## 5. 与真机对拍结果（2026-10）

| 镜像 | 真机规模 | 组装结果 | 差额 |
| --- | --- | --- | --- |
| 8061 x86（Q:） | 43,100 键 / 120,978 值 | 43,088 键 / 120,787 值 | only-in-a 1 键 / 72 值、only-in-b 13 键 / 263 值 |
| 8061 ARM（P:） | 613 键 / 1,408 值 | 602 键 / 1,364 值 | **only-in-a 0 键 / 0 值**、only-in-b 11 键 / 44 值 |

x86 的残余集中在 ManifestCache（`MCP_*` 16,296 + `(default)` 12,957，即 PCM 编码器还没
自研完）与会话相关的 catalog 选择；ARM 的残余全部是 11 条 servicing stack 部署记录
（它们不由 `.mum` 声明，需要"CF=12 组件身份也是部署 appid"这条规则）。

## 6. 复现

```powershell
cd D:\GitHub_Projects\OneCore-Image-Designer\Source\Native
pwsh -NoProfile -File .\build-servicing.ps1

pwsh -NoProfile -File .\assemble-cbs-store.ps1 `
    -ImageRoot 'Q:\' `
    -StackVersion '6.2.8061.0 (fbl_core1_soc.110803-1830)' `
    -OutDir 'E:\ocid_verify\asm' -HiveName 'OCIDOUT.hiv' `
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
    -DiffAgainst 'E:\ocid_verify\truth_all.txt'
```

## 7. 剩余工作

1. **PCM 表构建器**（`BuildPcmTables`）：把清单 XML 直接编译成 35 张表（现状只能复现
   容器与表骨架，表内容仍靠参考实现）。这一步做完，`MCP_*` 与 `(default)` 就能自研生成，
   x86 差额预计降到个位数。
2. `--sessions`：按安装会话顺序选 catalog；`CF=12 ⇒ 部署记录`：补 11 条 servicing stack 部署。
3. 然后进入 KTM 事务化（把上面所有写入包进事务，失败可回滚）。
