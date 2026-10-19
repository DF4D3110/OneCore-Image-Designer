#pragma once

// CBS 台账的"代"差异（版本兼容层）。
//
// 背景：从 6.1（Win7）到 10.0.2xxxx，CBS 的存储格式本身（COMPONENTS 键名、
// 值名、折叠算法）完全没变，变的是**同一批值里的表示方式**和**子系统的开关**。
// 例如：
//   * 6.2.8400 的 `f!` 值还是字符串、`CF` 只出现 5；6.3.9600 起 `f!` 变成
//     REG_DWORD 状态位、`CF` 出现 0x100；
//   * 6.3.9600 的清单已经是 DCM（PA30）压缩、`S256H` 是解压后 XML 的 SHA-256；
//   * 驱动部署的 `CatalogThumbprint` 在 6.2 不写、6.3.9600 起写；
//   * `Drivers\<arch>\...` 台账在 10.0.14357 还没有、10.0.16212 已有；
//   * `p!/s!/i!` 值名折叠连续 `_` 只在 10.0.16299 起出现。
//
// 因此所有"现代/老"判断都必须按**构建号**分档，而不是"有没有加载 DCM 字典"。
// 构建号来源（优先级从高到低）：
//   1. 命令行 `--os-build <n>`；
//   2. 镜像里的 `Windows\System32\ntoskrnl.exe`（PE 版本资源）；
//   3. `--stack-version '6.3.9600.16384 (...)'` 这类版本文字。
//
// 注意：这些阈值都是**实测**出来的（见每个字段的注释），不是猜的；
// 中间没有镜像覆盖的区间取"从下一个已知代开始"的保守做法。

#include <cstdint>
#include <string>

namespace ocid
{
    struct CbsVersionProfile
    {
        int build = 0;                       // 6.2.8061 -> 8061；10.0.16299 -> 16299
        const char* family = "legacy";       // 仅用于日志：win7 / win8 / win81 / rs1 / rs3 ...

        // 6.3.9600 实测：清单 = `DCM\x01PA30`，3,025 份全部可解。
        bool dcmManifests = false;

        // 6.3.9600 实测：`f!` = REG_DWORD（4 字节）；6.2.8400 还是字符串。
        bool fileValuesAreFlags = false;

        // 6.3.9600 实测：`CF` 出现 0x100（0x3F 之外）；6.2.8400 只有 5。
        bool configFlagsFullDword = false;

        // 6.3.9600 实测：227/227 个驱动部署都带 `CatalogThumbprint`；
        // 6.2.8220/8400 是 0/593、0/587（不写）。
        bool catalogForDrivers = false;

        // 6.3.9600 实测：`S256H` = SHA-256(解压后的清单 XML)，不是磁盘字节。
        bool sha256OfDecodedManifest = false;

        // 6.3.9600 的清单里已经有 `<file><hash><dsig:DigestValue>`；
        // 10.0.16299 实测 `f256!` = 这个 DigestValue（不是磁盘文件哈希）。
        bool f256FromDigestValue = false;

        // 10.0.14357 没有 `Drivers\<arch>`、10.0.16212 已有 1 个 ⇒ 取 15063 起。
        bool driversLedger = false;

        // `p!/s!/i!` 值名折叠连续 `_`：10.0.16299 实测；
        // 6.3.9600 的载荷名里没有 `_`，两种规则等价，按 16299 起算。
        bool payloadNameCollapse = false;

        // identity 文本归一：10.0.16299 实测 `<inf>.inf.resources` 组件名只保留
        // `Resources` 大写、`en-us` -> `en-US`；6.3.9600 是清单原文（`1394.inf.Resources`）。
        bool modernIdentity = false;
    };

    // 从 "6.3.9600.16384 (winblue_rtm_partner...)" / "10.0.16299.15" 里取构建号。
    bool CbsBuildFromVersionText(const std::string& text, std::int32_t& build);

    // 从 PE 文件的版本资源里取构建号（ntoskrnl.exe / wcp.dll 均可）。
    bool CbsBuildFromImageFile(const std::wstring& path, std::int32_t& build, std::string& error);

    // 由构建号给出档位；build <= 0 表示"未知"，按 legacy（最保守的 6.2 及以前）处理。
    CbsVersionProfile CbsProfileForBuild(std::int32_t build);

    // 一行可读描述，用于命令输出/日志。
    std::string CbsProfileSummary(const CbsVersionProfile& profile);
}
