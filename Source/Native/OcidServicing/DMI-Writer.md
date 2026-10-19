# DMI 写侧：驱动包落进离线镜像

本文描述 OCID 自己把驱动包从包集装进离线镜像的完整链路，不调度系统的 `pnputil` / DISM。
三步，全部由 `ocid_manifest.exe`（`Source/Native/OcidServicing/tools/ocid_manifest.cpp`）完成：

```
dmi-ops   清单目录 → 导入计划（JSONL，每行一个驱动包）
dmi-place 执行导入：载荷 → FileRepository，INF → Windows\INF\oemN.inf，并产出 DriverDatabase 台账计划
cbs-write --append  把 DriverDatabase 台账计划写进镜像的 SYSTEM hive
```

`Stage-OcidImage.ps1` 的 1.5 阶段会自动串这三步（`-SkipDrivers` 跳过，`-DriverOsVersion` /
`-DriverSigner` / `-DriverSignerScore` 是传给 `dmi-place` 的输入）。

## 1. 真机形态（8061 x86 实测）

- 驱动仓库：`Windows\System32\DriverStore\FileRepository\<INF 名小写>_<架构>_<16hex>\`。
  目录名里的 16hex 就是 DriverDatabase 的 `StoreId`（`<INF 名>_<arch>_<16hex>` 的小写形式）。
- 目录内容 = INF 原文（**保留原大小写**，例如 `prnms001.Inf`）+ 驱动载荷（`1394.inf`、`1394ohci.sys`）。
- `Windows\INF\oemN.inf` 是与仓库里那份 INF **逐字节相同**的副本（实测 `oem0.inf` 的 SHA-256
  等于 `prnms001.inf_.../prnms001.Inf`），不是重新生成的文件。
- `oemN.inf` 的占位由 `HKLM\SYSTEM\DriverDatabase\DriverInfFiles` 下的位图值 `0004` 决定：
  4099 字节、**MSB-first**，置位 = 已占用；`0003` 是架构（x86 = 0）。
- 驱动包登记在 `HKLM\SYSTEM\DriverDatabase\DriverPackages\<StoreId>`，值名是**属性 id 的
  `%04x` 文本**（如 `0003` = 架构、`0004` = 位图、`0005/0006` = 1、`000a/000b` = 0、
  `000c` = "Microsoft"、`000d/000e/000f` = 二进制）。

## 2. `dmi-ops`：清单 → 导入计划

```powershell
ocid_manifest.exe dmi-ops <清单目录> --packages <载荷根>            # 扫目录
ocid_manifest.exe dmi-ops --list <单个 .manifest> --packages <载荷根>
```

输出一行一个 JSON 对象：

```json
{"componentDir":"x86_1394.inf_31bf3856ad364e35_6.2.8061.0_none_8235efbd79774ccd",
 "identity":"1394.inf, Culture=neutral, Type=driverUpdate, ...",
 "arch":"x86","infName":"1394.inf","storeId":"1394.inf_x86_85b894cb1410e75c",
 "ok":true,"fileCount":2,"files":["1394.inf","1394ohci.sys"]}
```

`ok` 为假的行（缺 INF、缺载荷、架构不可识别等）会被 `dmi-place` 计入 `skipped`，不会瞎猜。
`--packages` 指向载荷根（镜像里就是 `Windows\WinSxS`，包展开目录的父目录）。

## 3. `dmi-place`：执行导入

```powershell
ocid_manifest.exe dmi-place <plan.jsonl> <镜像根> --packages <载荷根> \
    [--plan-out <driverdb.txt>] [--os 6.2] \
    [--signer "Microsoft Windows"] [--signer-score 0x0D000003] \
    [--inbox] [--dry-run]
```

做的事，按顺序：

1. 每个包：把载荷从 `<packages>\<componentDir>\<文件>` 拷进
   `<镜像>\Windows\System32\DriverStore\FileRepository\<StoreId>\`（大小写不敏感查找；
   任何一个载荷缺失 → 整包失败，计入 `missingFiles`）。
2. 用 INF 字节重新算一次 `StoreId`，必须与计划里的一致（防止计划与载荷不匹配）。
3. 发布 INF：从镜像里已有的 `oem*.inf` 重建位图（4099 字节、MSB-first），取第一个空闲位
   写 `<镜像>\Windows\INF\oem<N>.inf`（内容 = 仓库里那份 INF 的副本，不重新生成）。
   `--inbox` 时保留原名（收件箱驱动不占 `oemN` 号）。
4. 第二轮用最终位图，给第一个包发根值 `0003`（架构）与 `0004`（位图）。
5. 产出 DriverDatabase 台账计划（`--plan-out`），格式与 `cbs-dump` 相同
   （`K\t<相对路径>` / `V\t<相对路径>\t<值名>\t<类型>\t<hex>`），可直接喂 `cbs-write`。

输出统计行：`placed packages=N copied=N published=N planLines=N failed=N skipped=N missingFiles=N oemBitmapBytes=4099`。

### 签名者信息是输入

真机的 `DriverPackages\<StoreId>` 比清单可推的多两个值：

| 值名 | 真机内容 | 来源 |
| --- | --- | --- |
| `0007` | `Microsoft Windows`（String） | `.cat` 验签结果 |
| `0008` | `0x0D000003`（DWord） | 签名者等级 |

清单里没有这两项，验签又需要证书链，所以它们作为输入：`--signer` / `--signer-score`，
`--inbox` 时默认 `Microsoft Windows` / `0x0D000003`。

## 4. `cbs-write --append`：写进镜像的 SYSTEM hive

```powershell
ocid_manifest.exe cbs-write <driverdb.txt> <镜像>\Windows\System32\config\SYSTEM --append [--transactional]
```

`--append` 与默认的"造空壳"不同：目标 hive 必须已存在，工具直接 `RegLoadKeyW` 挂上去写，
写完卸载；`--transactional` 时整个写入包在 KTM 事务里（失败全部回滚）。

## 5. 已验证的事实与剩余缺口

- 1394.inf 真机实测：`dmi-ops` 出的 `StoreId = 1394.inf_x86_85b894cb1410e75c`；
  `dmi-place` 落 2 个文件、发布 `oem0.inf`、台账 115 行；与真机
  `DriverDatabase\DriverPackages\1394.inf_x86_85b894cb1410e75c` 对拍 **9/11 个值一致**，
  差的正是 `0007`/`0008`（签名者）。
- `Windows\INF\oemN.inf` 与仓库 INF 逐字节相同这一点已用 SHA-256 核对。
- 尚未做：`DriverDatabase` 的 `Properties` 子键走查（`cbs-dump` 在该子树会失败，
  `打不开子键：Properties`），所以对拍目前按 key 逐点读；以及"替换已有 oemN.inf 时清理旧文件"
  的策略（现在是取第一个空闲位，不覆盖）。
