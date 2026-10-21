// PcmBuild.h —— 从组件清单 XML 生成一份完整的 `PcmH` 块（ManifestCache 的 t11 表）。
//
// 容器层（84 字节头 + TOC + 记录表 pack/桶序）在 Pcm.h/Pcm.cpp；微 DOM（MdHd）在
// Microdom.h/Microdom.cpp。本模块只负责 `CCdfBuilder::GeneratePCM` 的**表构建语义**：
// 元素记录（96B `<file>`、68B 文档、48B 依赖、16B 依赖子记录、16B hash、cache 记录、
// securityDescriptor/directory/membership/localization 记录）、身份表（t4/t5）、
// `<registryKeys>` 子树、key→slot 注册表、字符串池，以及页头的 str/guid/ident 数组。
//
// 行为以 8419 ARM 真机语料（3,041 份 PcmH，2,757 份有配对清单）为验收基准；
// route6 Python 参考实现的全量成绩是 T4 96.9%。
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace ocid {

// 从组件清单 XML（UTF-8，DCM 已解码的明文）生成一份 `PcmH` 块的完整字节。
//
// b0Extra：微 DOM 第 0 个元素记录 b0 的 bit4..6（真机是未初始化栈字节，取值 0..7；
//          只在第一个元素 min(属性数, 内容子数) != 0 时才写入，和真机写入器一致）。
//          默认 0；需要逐字节对拍时由调用方给真机值。
//
// 成功返回 true 并写 out（先清空）；失败返回 false 并写 error（中文）。
// osBuild: os build number (0 = unknown). 16299+ file records carry two
// extra columns, see BuildFileRecord.
bool PcmBuildFromManifest(const std::string& xmlUtf8, std::uint8_t b0Extra,
                          std::vector<std::uint8_t>& out, std::string& error,
                          std::int32_t osBuild = 0);

}  // namespace ocid
