// microdom.h —— Windows 微 DOM（MdHd）序列化：清单 XML → MdHd 段字节
//
// 规范：MdHd 微 DOM 序列化（CbsStore/Pcm 同一套 CBS 台账写侧的一部分）
// 行为以真机 8061/8419 ManifestCache 样本为准（2,988 B 小样本与 3,041 份 8419 语料逐字节验证）
//
// 用法：
//     std::string error;
//     std::vector<std::uint8_t> blob;
//     if (!ocid::BuildMicrodomLayout(xmlBytes, blob, error)) { /* error 里有中文原因 */ }
//
// 产出 = 完整 MdHd 段：24 字节头 + MdSp + MdDl + MdDt + MdLc，段间 4 字节对齐（§2）。
// 不依赖 MSXML / Windows XML 库，自带最小 XML 词法/结构扫描器。
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace ocid {

// 把清单 XML 的 UTF-8 原始字节构建为完整 MdHd 段字节。
//
// 支持的 XML 子集（覆盖 WinSxS manifest）：元素、属性、文本（含纯空白）、CDATA、
// 注释、处理指令（<?xml ?> 声明不进 DOM）、<!DOCTYPE>（声明本身忽略，不支持内部子集）。
// 字符串按 §3.3 的 intern 顺序入池；节点记录按 §4 输出；位置表按 §5。
//
// 说明：元素记录 b0 的 bit4..6 是写入器的未初始化栈字节（§4.2.1，don't-care），
//       本实现恒写 0（= 8061 小样本的实际取值）；b0 的 bit7 是 §4.2 的
//       "属性数 < 内容子数" 标志（原型写作 ((0x08 << 4))，实测落在 bit7）。
//
// 成功返回 true 并把字节写入 out（先清空）；失败返回 false 并写 error。
bool BuildMicrodomLayout(const std::string& xmlUtf8,
                         std::vector<std::uint8_t>& out,
                         std::string& error);

}  // namespace ocid

