# 未处理：16299 的 PCM 字符串池（t2）槽序

> 状态：**未处理（OPEN）**，2026-10-21 记录。
> 影响范围：只有 `ManifestCache`（PCM/CDF）的 16299 语料；**不影响**六镜像的
> `COMPONENTS` 台账目标（9600/8419/16299 台账均已通过各自的验收路径）。
> 结论先行：16299 的 `<file>` 记录已经从 96 B 改成 104 B（见 `PcmBuild.cpp` 的
> `PcmBuildAtLeast(16299)`），但 `t2` 字符串池的**槽序**仍无法复现，因此所有
> 记录里"引用槽号"的字段都跟着错位，16299 语料的 T2/T3/T4 恒为 0。

## 1. 现象

同一份清单，真机 PCM 与我方构建的 PCM：

- **string id（interning 插入序）完全一致**，例如 clusbflt 样本的
  `id0..15 = ClusBflt.inf, $(build.nttree)\driver_infs\, ClusBFlt.sys,
  $(build.nttree)\, Name, clusbflt.inf, Type, driverUpdate, Version,
  10.0.16299.15, PublicKeyToken, 31bf3856ad364e35, ProcessorArchitecture,
  amd64, VersionScope, NonSxS`（两边逐项相同）。
- **页头 `nStr` 相同、t2 槽数相同、字符串多重集相同**，只是**排列不同**：
  - 真机 t2 槽序（按 id）= `[11, 3, 12, 2, 4, 1, 5, 7, 9, 14, 15, 0, 13, 6, 8, 10]`
  - 我方（现行规则）= `[5, 4, 6, 10, 12, 14, 2, 3, 7, 0, 1, 9, 13, 11, 8, 15]`
- 语义澄清（已实测确认，别搞反）：
  - 页头 `nStr @ 0x1C`，串表起点 `0x54`，每条 4 B = **string id → 槽号**；
  - **身份表（t4/t5）里的 a/b 是 string id**；
  - **其它记录（96/104 B 文件记录、8 B `{0,versioned}` 等）里的串引用是槽号**。
- 16 条记录里只有这种"引用槽号"的字段会随之错位；所以失败统计呈现
  `T1 280/329、T2 0`（槽数与 type+len 里含槽号的部分先崩）。

## 2. 我方现行规则（已确认，且 9600/8419 语料全对）

`PcmBuild.cpp` §11：

```cpp
hashes[i] = Hash64CodePointsRaw(strs[i]) & 0xFFFFFFFF;   // kM32
order = SitOrder(hashes);      // 4 路 ×65599 折叠，n0=7，capacity=5n，满了重建
按 order 发 t2 槽
```

`SitOrder`：初始 7 桶 / 容量 35；插入第 `i` 项后若 `i+1 > capacity` 则
`newN = 2*(i+1)` 重建，旧桶按桶号升序尾插；输出按桶号升序，桶内保持插入序。

## 3. 已排除的可能（全部为实测零命中，勿重复）

1. 哈希变体：码点 / 字节 / u16 × 原样 / A–Z 折小写 × 多项式
   `65599, 33, 37, 31, 131, 16777619, 0x01000193` × 车道数 1..4 ×
   掩码 `2^64, 0xFFFFFFFF, 0x1FFFFFFF` × 折叠系数 24 种排列 × 位移 0..28 ×
   取反 × 前缀（长度 / UTF-16 LBlob / 对齐 pad）× 模数 2..6000。
2. 桶数：`2..199`、`{7, 72, 722, 1024, 2048}` × 桶序（升/降/创建序）×
   桶内序（id / 哈希 / 长度 / 字符串 升或降）。
3. 增长策略：`n0 ∈ {4,5,7,8,11,16,17,32}` × 负载因子 1..7 ×
   `n = 2*(i+1)` 重建 × FIFO / LIFO 重插。
4. 经典哈希排序（crc32 / adler32 / jenkins / murmur3 / fnv1a / djb2 / sdbm）
   × UTF-8 / UTF-16LE / 不区分大小写 × 掩码 × 升降。
5. 微 DOM 串表（`MdHd → MdSp`）下标序；开放寻址（线性 / 步长 1/3/5/7，
   n=16..209）；`MdHd` 内字符串首次出现偏移序；等分组桶序假设。

## 4. 常量线索（供 IDA 逆向用）

- 16299 的 `wcp.dll`（`E:\ocid_dism\16299\x86\wcp.dll`，2,680,728 B，**无符号**）
  里 `imul ..., 0x1003F`（即 65599）共 **32 处**，集中在：
  `0x197fa0`（字节循环 + `add eax,0x20` 折小写）、`0x1980e4` / `0x1982c8`
  （码点版：`movzx ... ax` + `cmp ax,dx`）、`0xb7117`、`0x1b5cbd`、`0x1b8f49`、
  `0x1fb198`、`0x1fbf39`。
- 32 位立即数都在（`FFFFFFF7`×2、`1E5`×15、`FFFFFFDC`×2、`1003F`×32、
  `1FFFFFFF`×5），**但没有** 8061 那种"64 位折叠常量成对出现"的
  `f7ffffff_01000000`（只出现在 19041 amd64）⇒ 说明折叠方式或组合方式不同。
- 8061 侧已知（仅供对照，**不要直接外推**，用户明确提示过 cbs 代数已迭代很多）：
  `E:\ocid_decomp\sym8061x86_wcp.c`，`CCdfManifestBuilder::AddTableObject`、
  `BUCL::HashTable::CTable<CCdfTableBuilder<CStringTableCaseSensitive,...>>`、
  `FindOrInsertIfNotPresent @0x100B2024`（`RtlHashEncodedLBlob(utf8)` 得到 32 位，
  桶 = `hash % *(table+12)`）、`CBucketChain::Insert @0x100B29FD`。

## 5. 16299 语料的取法（可复现）

镜像里 `ManifestCache` 只有旧 bucket（2.59 MB），MCP 数据所在的 27 MB bucket 缺失；
但旧 bucket 里仍能抠出 **329 份真机 PCM**：

1. `I:` 挂 `…16299.15.rs3_release…RS3_RELEASE_SERVERDATACENTER_VOL_X64FRE_EN-US.iso`；
   `install.wim` 只有 1 个索引。
2. `dism /Mount-Image /ImageFile:I:\sources\install.wim /Index:1
   /MountDir:E:\ocid_verify\mnt16299b /ReadOnly`；挂载后那份
   `Windows\WinSxS\ManifestCache\44cb0f7e4f16664a_blobs.bin`（2,589,102 B）可读
   （`E:\ocid_dism\mnt16299` 的老挂载带 SparseFile+ReparsePoint，读不了）。
3. 记录格式：`[16B hash][u32 ?][u32 outSize @0x14][u32 alg==2 @0x18][LZNT1 @0x1C]`；
   `E:\ocid_dism\inputs16299\mcp.csv`（8075 行）的 offset 指向记录头，其中
   871 个落在文件内、329 个能解出合法 `PcmH`。
4. **LZNT1 必须用 ntdll**（`E:\ocid_verify\mcp\unlz.py` 的纯 Python 实现在第一个
   stored chunk 之后就错位）：
   ```python
   f = ctypes.WinDLL("ntdll").RtlDecompressBuffer
   f(2, out, out_size, src, len(src), byref(fin))   # 2 = COMPRESSION_FORMAT_LZNT1
   ```
5. 清单配对键 = `(arch, name, pkt, version, lang)`（**不能用文件名 hash**：
   mcp 里的 hash 与清单文件名的 hash 不是一回事）。
6. 构建：`ocid_manifest cbs-pcm-build <man> <out> 0
   --dcm-dictionary E:\ocid_dism\dcm\dict.bin --os-build 16299`。

脚本：`E:\ocid_verify\pcm16299\run16299.py`（抽取 + 配对 + 构建 + 统计）、
`probe_whichrule.py` / `probe_strorder.py` / `probe_bucket.py` /
`probe_hashsearch.py` / `probe_mono*.py` / `probe_perm.py` / `probe_openaddr.py`
（全部为已否决实验的探针，留着以免重复劳动）。

## 6. 建议的下一步（单开会话）

1. 在 16299 的 `wcp.dll` 上定位"字符串池 → t2 槽"的建表函数。切入点：
   上面 5 个 `imul 0x1003F` 站点附近的函数，重点看**桶数来源**（栈上常量 / 表对象
   字段）与 **桶链表插入方向**；8061 的 `FindOrInsertIfNotPresent` 形态可作对照。
2. 如果能拿到 10.0.16299 的私有符号（PDB / 内部符号服务器），优先用符号而不是硬猜。
3. 解开 t2 槽序后再看剩下的 **49 份槽数不同**（`2097/2098`、`1477/1478`、
   `425/424`、`22/17` …）——它们很可能在 t2 之后才会显形，属于第二个未知族。

## 7. 现状数字（2026-10-21）

| 语料 | 对数 | T1 槽数 | T2 type+len | T3 payload | T4 整文件 |
| --- | --- | --- | --- | --- | --- |
| 9600 | 2,784 | 2,780（99.9%） | 2,779（99.8%） | 2,600（93.4%） | **2,600（93.4%）** |
| 8419 route9 | 2,757 | 2,756（100%） | 2,756（100%） | 2,754（99.9%） | **2,754（99.9%）** |
| 16299 | 329 | 280（85.1%） | 0 | 0 | **0** |

提交：`7f4a9be`（HTTPAI/AddUrl 族）、`31f96ff`（16299 文件记录 104 B）、
`d7774dc`（文档）。
