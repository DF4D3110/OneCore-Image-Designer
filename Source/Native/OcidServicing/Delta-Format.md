# PA30 delta 格式（反编译记录，未实现）

> 状态：**只读反编译结论，尚无实现**。目标是用 OCID 自带代码替换 `msdelta.dll!ApplyDeltaB`，
> 让 `Dcm.cpp` 的清单解码与 `Pa30Delta.cs` 的 container CAB 还原都不再依赖微软的 delta 组件。
> 所有结论来自对 `C:\Windows\System32\msdelta.dll`（559,072 B，10.0.19041）与其公共符号
> （`msdelta.pdb`，键 `7B8B6A22A43F1187433E929B2C5944921`）的 IDA 9.4 反编译，未参考第三方实现。

## 1. 导出入口

| 导出 | RVA | 说明 |
| --- | --- | --- |
| `ApplyDeltaB` | 0x0B7C0 | 把 `DELTA_INPUT` 转 `BufferObject` 后调 `PullcapiContext::ApplyDeltaA`，结果写回 `DELTA_OUTPUT` |
| `PullcapiContext::ApplyDeltaA` | 0x0B8B4 | 内存版主入口 |
| `ApplyDeltaA` | 0x2E250 | 文件版，包装同一函数 |
| `GetDeltaInfoA` | 0x0C018 | 只读头部：`SplitPatchHeader` 组件取 8 个输出后回填 `_DELTA_HEADER_INFO` |
| `DeltaFree` | 0x262F0 | 释放输出 |

`ApplyDeltaA` 的分支：`*(DWORD*)delta == 0x39314150`（`"PA19"`）→ 老格式 `ApplyLegacyDelta`（要求
`flags & 1`）；否则 `Object::CreateCopy` → `GetDeltaInfoA` → 按 `FileTypeSet & 0x100` 选
`g_applyReversePatchFactory` / `g_applyPatchFactory` → `ComponentFactory::ProcessComponent(factory, 2,
{source, delta}, 1, &out)`。

## 2. PA30 头（`compo` 断言字符串还原的构造顺序）

`msdelta.dll` 里保留了大量 `cdp::Assert` 文本，直接给出组件里每个中间值的来源。构造补丁头的那段是：

```
full( CopyBuffer ): input[0];
asciiPatchSignature( TToAscii ): "PA30";
asciiPatchSignatureSize( BufferSize ): asciiPatchSignature[0];
signature( Unconcat ): full[0], asciiPatchSignatureSize[0];
checkSignature( CheckBuffersIdentity ): signature[0], asciiPatchSignature[0], 0x044601;
intFixedBuffer( IntToFixedBuffer ): 0;
intFixedBufferSize( BufferSize ): intFixedBuffer[0];
fileTimeBuffer( Unconcat ): signature[1], intFixedBufferSize[0];
fileTime( IntFromFixedBuffer ): fileTimeBuffer[0];
bitReader( BitReaderOpen ): fileTimeBuffer[1];
version( BitReadInt ): bitReader[0];
code( BitReadInt ): version[0];
flagsInt( BitReadInt ): code[0];
targetSize( BitReadInt ): flagsInt[0];
hashAlgId( BitReadInt ): targetSize[0];
hash( BitReadBuffer ): hashAlgId[0];
output: hash[0], version[1], code[1], flagsInt[1], targetSize[1], fileTime[0], hashAlgId[1], hash[1];
```

即文件布局：`"PA30"` + 8 字节固定整数区（前 4 字节是 FILETIME 低位，其余进位流）+
位流内依次 `version`、`code`、`flags`、`targetSize`、`hashAlgId`、`hash`（长度由 `hashAlgId` 决定，
SHA-256 时 32 字节）。`hash` 是**目标内容的哈希**。

## 3. 应用流程（同一批断言字符串）

正向应用（`ApplyPatch` 组件）：

```
source( PassBuffer ): input[0]; patch( PassBuffer ): input[1]; targetBuffer( PassBuffer ): input[2];
split( SplitPatchHeader ): patch[0];
targetBufferSize( BufferSize ): targetBuffer[0];
checkTargetSize( CheckIntsIdentity ): split[4], targetBufferSize[0], 0x040303;
apply( ApplyPayload ): split[2], split[3], source[0], split[0], targetBuffer[0], 0;
bitReaderClose( BitReaderClose ): apply[0], 1;
hash( CalculateHash ): split[6], apply[1];
checkHashIdentity( CheckBuffersIdentity ): hash[0], split[7], 0x040302;
output: apply[1];
```

- `SplitPatchHeader` 的 8 个输出：`[0]` 位流/头上下文、`[2]`、`[3]` 传给 `ApplyPayload`，
  `[4]` = 目标大小，`[6]` = 哈希算法，`[7]` = 头部里的目标哈希。
- `ApplyPayload(source, bitReader, target0, header, targetBuffer, reverseRequested)`（反向补丁用
  最后一个参数 `1`；`ApplyReversePatch` 还会再用 `CreateReversePatch` 生成反补丁）。
- `ApplyReversalData` 单独存在：`source, split[0], targetSize`，用于只应用 LZX reversal 表。
- 哈希校验失败 = 数据损坏，`0x040302`/`0x040303` 是 `cdp::Exception` 码。

`ApplyPayload` 的断言：

```
source( PassBuffer ): input[0]; bitReader( BitReaderOpen ): input[1]; target0( PassBuffer ): input[2];
reverseRequested( CopyInt ): input[4]; sourceSize( BufferSize ): source[0]; targetSize: target0[0];
baseRiftTable( BitReadRiftTable ): bitReader[0];
riftTable( SumRiftTables ): ...
```

即载荷流开头不是符号，而是 **rift table**（基文件偏移的增删表），之后才进入符号解码。

## 4. 符号流（pseudo-LZX）

- 类：`compo::PseudoLzxCompress`（`InternalProcess` 0x2190、`AddCandidate` 0x2EF8、
  `FindCandidates` 0x27CC、`Flush` 0x1574、`UpdateCompressionFormatWrite` 0x2B574）、
  `compo::PseudoLzxSearch`（`Init` 0x1008、`Reset` 0x14B4、`Lookup` 0x2FB0、`Serialize` 0x3DCF0、
  `UpdatePatternLengths` 0x3070）。⇒ delta 的压缩既不是 LZMS，也不是 cabinet 里的 LZX，
  而是同一个 LZX 熵编码框架上的“伪 LZX”（符号携带偏移/长度而不是单纯的匹配）。
- `compo::CompressionFormat`：`ReadSymbol` 0x5760、`CreateDecoderTables` 0x6F0C、
  `InternalFromBitReader` 0x3DE30（位流里的格式表：`Read(1)` 为 1 时单格式、按 Reset 初值读长度；
  为 0 时先读格式数量与边界，再读 39 个 4-bit 长度作为“长度表的长度表”）。
- `ReadSymbol` 返回 64 位符号：低 32 位值、高 32 位前进字节数；`DecompressComponent::InternalProcess`
  （0x4C0B0，反编译在 `E:\ocid_verify\re\decomp_18004c0b0.c`）的用法：
  `v97[1] == 1` 表示**字面字节**；否则 `v97[1]` 是拷贝长度，`v97[0]` 是偏移标记：
  普通值按 `OffsetRiftTable` 的 16 字节条目（`+8` 偏移、`+16` 长度）把基文件区间映射到输出；
  `0x54000` 是“从输出流自身拷贝”的特殊标记。`lzx_range_copy` 记录两路来源。
- `CompressionLengths`：872 字节 = 600 + 256 + 16；`Reset(false)` 填 424×9、176×10、256×8、16×0。
  长度用 23 个符号增量编码（17/18/19 加、20/21/22 减、≥23 是复制游程）。
- 位读取器 `BitReaderObject`：`+0x18` 剩余位数、`+0x20` 当前指针、`+0x28` 结束、`+0x30` 位缓冲、
  `+0x38` 缓冲位数；`Read(n) = buffer >> (32-n)`，缓冲不足时 `*(u32*)ptr` 小端装入 ⇒ 流按字节自然
  顺序、MSB 先出。

## 5. 待补的缺口

1. `BitReadInt` / `BitReadBuffer` / `BitReadRiftTable` 的精确语义（变长整数编码、rift 条目字节布局）。
2. `SplitPatchHeader` 组件本体（`0x18005a1e8`、`0x18005bf7b`、`0x18008691c` 三处
   `InternalFromBitReader` 调用点尚未归入函数）。
3. `ApplyPayload` 的 rift table 求和与被删除区间处理（`SumRiftTables`）。
4. 用 `ApplyDeltaB` 造 (basis, delta, target) 语料做逐字节对拍，再接到
   `Source/Native/OcidServicing` 的 `Delta.cpp` 与托管 `Pa30Delta.cs`。

## 6. 语料与验证入口

- 现有 `Pa30Delta.TryApply`（`Source/Managed/OcidCabinet/Pa30Delta.cs`）直接 P/Invoke `msdelta.dll`，
  可作为（过渡期）真值来源：同一个 basis + delta 用微软 API 解出的内容即目标。
- 22,167 份现代清单的 DCM 载荷、container CAB 的 102 个 PA30 条目都是现成语料；
  `E:\ocid_verify\cabtest\sparse\selfupdate.cab` + `cix` 哈希可做端到端校验。
- trick：container CAB 里 PA30 的 basis 不写在清单里，需在柜内其余条目中试解并用清单哈希确认
  （见 `E:\ocid_verify\re\PA30-NOTES.md`）。

## 实现与覆盖（2026-10-19）

自研实现：

- 原生：`src/Delta.cpp` + `include/ocid/Delta.h`（`ApplyPa30Delta`），`src/Dcm.cpp` 的
  `DcmDecodeManifest` 先走它。
- 托管：`Source/Managed/OcidCabinet/Delta/`（`BitReader`、`Huffman`、`IntFormat`、
  `CompressionFormat`、`Pa30Decoder`），`Pa30Delta.TryApply` 对外仍是同一签名。

验收数字：

- 16299 全量清单：`cbs-plan-components` 22,167 份、`bad=0`、计划文件 22,299,528 字节，SHA-256
  `293E2824BC219D96…`，与 `msdelta` 结果逐字节相同；其中 22,099 份由自研解码器直接解出。
- 12 份 oracle 样例 + 200 份随机 DCM 清单：自研解码结果与 `ApplyDeltaB` 逐字节一致。
- container CAB（WU selfupdate 包）：`ocid_cab verify` 696 个文件、10,186,062 字节，SHA-1 与清单全一致。

尚未覆盖（当前回退 `msdelta.dll`，并在代码中注明是过渡）：

- `fileType != 1` 的载荷（PE 差分，需要 `ApplyPreProcessSource_<mnemonic>` 与
  `ApplyPostProcessTarget`，含 E8 变换）。
- 少数多格式 `CompositeFormat` 补丁（22,167 份里 68 份）：格式边界取值为源大小、切换后符号流解错的
  问题尚未定位；单格式分支已验证 200/200。
- 非空 `RiftTable` 的 delta（目前见到的 DCM/container 载荷基表都为空）。
