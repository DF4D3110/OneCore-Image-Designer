// PcmBuild.cpp —— 从组件清单 XML 生成完整 `PcmH` 块的表构建语义。
//
// 这是 route6 Python 参考实现（buildpcm_self3.py + regbuild.py）的 C++17 移植。
// 所有"为什么"都以真机 8419 语料为依据：
//
//   * 元素记录/身份表/字符串池共用一个 `CSingleInstanceTable` 迭代模型
//     （7 桶、容量 5N、第 36 次插入时增长到 72 桶、旧桶升序 FIFO 重挂）。
//     它同时决定**载荷物理序**（PcmEncode 里的桶序；两边规则一致）、
//     **身份表发射序**和**字符串池发射序**。
//   * 96B `<file>` 记录的 [2][3][4][15][18] 是 interner 插入序 id，
//     [16] 是微 DOM 节点索引，[14] 是 "targets" 伪 blob id
//     （2 + 2*依赖数 + trustInfo + 4*序号 + gac/hash 修正）。
//   * 68B 文档记录的 [3] = 依赖数、[9] = mvid 标志、[11] = 部署清单的
//     2*依赖数 + 文件数（带 `<infFile>` 时每个文件算 4）。
//   * 通用表骨架直接复用 PcmEncodeRecordTable：type6 在 count<7 时省 f0、
//     type9 永不落 f0、f1/f2 按值域收窄。
//
// 说明：脚本里大量 "经验规则" 已注明来源样本数；这里保留同样的注释，方便以后
// 用新语料复核。不可推导的字段（MdHd b0 的未初始化位）由调用方给 b0Extra。
#include "ocid/PcmBuild.h"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <functional>
#include <map>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

#include "ocid/Microdom.h"
#include "ocid/Pcm.h"

namespace ocid {
namespace {

// ---------------------------------------------------------------------------
// 基础常量 / 小工具
// ---------------------------------------------------------------------------
constexpr std::uint32_t kM32 = 0xFFFFFFFFu;
constexpr std::uint64_t kM64 = 0xFFFFFFFFFFFFFFFFull;
constexpr std::uint64_t kFold = 0x1FFFFFFF7ull;
constexpr std::uint32_t kNoId = 0xFFFFFFFFu;  // -1：无字符串 / 无 blob

inline std::uint32_t ReadU32(const std::uint8_t* p, std::size_t off) {
    return static_cast<std::uint32_t>(p[off]) |
           (static_cast<std::uint32_t>(p[off + 1]) << 8) |
           (static_cast<std::uint32_t>(p[off + 2]) << 16) |
           (static_cast<std::uint32_t>(p[off + 3]) << 24);
}
inline std::uint16_t ReadU16(const std::uint8_t* p, std::size_t off) {
    return static_cast<std::uint16_t>(static_cast<std::uint16_t>(p[off]) |
                                      (static_cast<std::uint16_t>(p[off + 1]) << 8));
}
inline void WriteU32(std::vector<std::uint8_t>& v, std::size_t off, std::uint32_t x) {
    v[off + 0] = static_cast<std::uint8_t>(x & 0xFFu);
    v[off + 1] = static_cast<std::uint8_t>((x >> 8) & 0xFFu);
    v[off + 2] = static_cast<std::uint8_t>((x >> 16) & 0xFFu);
    v[off + 3] = static_cast<std::uint8_t>((x >> 24) & 0xFFu);
}
inline std::size_t Align4(std::size_t v) { return (v + 3u) & ~static_cast<std::size_t>(3u); }

std::string LowerAscii(std::string s) {
    for (char& c : s) {
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    return s;
}

bool IsSpaceByte(char c) {
    return c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f' || c == '\v';
}

std::string Strip(const std::string& s) {
    std::size_t b = 0, e = s.size();
    while (b < e && IsSpaceByte(s[b])) ++b;
    while (e > b && IsSpaceByte(s[e - 1])) --e;
    return s.substr(b, e - b);
}

// ---------------------------------------------------------------------------
// 哈希（8061 wcp.dll 的实现）
// ---------------------------------------------------------------------------
// 4 路 65599：lane = 字节位置 % 4；h64 再把 4 条 lane 按固定系数合成 64 位。
std::uint64_t Hash64(const std::string& s) {
    std::uint32_t lanes[4] = {0, 0, 0, 0};
    for (std::size_t i = 0; i < s.size(); ++i) {
        const std::uint32_t b = static_cast<std::uint8_t>(s[i]);
        lanes[i & 3u] = lanes[i & 3u] * 65599u + b;
    }
    return static_cast<std::uint64_t>(lanes[3]) +
           static_cast<std::uint64_t>(lanes[2]) * 0x1FFFFFFF7ull +
           static_cast<std::uint64_t>(lanes[1]) * 0xFFFFFFDC00000051ull +
           static_cast<std::uint64_t>(lanes[0]) * 0x1E5FFFFFD27ull;
}

// RtlHashEncodedLBlob over a UTF-16 LBlob：按"码点"喂 4 路 65599，A..Z 先降小写。
// Python 的 h64_u16 逐码点迭代；manifest 里全是 BMP 字符，这里按 UTF-8 码点解码即可。
std::uint64_t Hash64CodePoints(const std::string& s) {
    std::uint32_t lanes[4] = {0, 0, 0, 0};
    std::size_t i = 0, index = 0;
    while (i < s.size()) {
        const unsigned char c0 = static_cast<unsigned char>(s[i]);
        std::uint32_t cp = 0;
        std::size_t len = 1;
        if (c0 < 0x80) {
            cp = c0;
        } else if ((c0 & 0xE0u) == 0xC0u && i + 1 < s.size()) {
            cp = ((c0 & 0x1Fu) << 6) | (static_cast<unsigned char>(s[i + 1]) & 0x3Fu);
            len = 2;
        } else if ((c0 & 0xF0u) == 0xE0u && i + 2 < s.size()) {
            cp = ((c0 & 0x0Fu) << 12) |
                 ((static_cast<unsigned char>(s[i + 1]) & 0x3Fu) << 6) |
                 (static_cast<unsigned char>(s[i + 2]) & 0x3Fu);
            len = 3;
        } else if ((c0 & 0xF8u) == 0xF0u && i + 3 < s.size()) {
            cp = ((c0 & 0x07u) << 18) |
                 ((static_cast<unsigned char>(s[i + 1]) & 0x3Fu) << 12) |
                 ((static_cast<unsigned char>(s[i + 2]) & 0x3Fu) << 6) |
                 (static_cast<unsigned char>(s[i + 3]) & 0x3Fu);
            len = 4;
        } else {
            cp = c0;  // 非法字节按单字节处理，避免未定义行为
        }
        if (cp >= 0x41 && cp <= 0x5A) cp += 32;
        lanes[index & 3u] = lanes[index & 3u] * 65599u + cp;
        ++index;
        i += len;
    }
    return static_cast<std::uint64_t>(lanes[3]) +
           static_cast<std::uint64_t>(lanes[2]) * 0x1FFFFFFF7ull +
           static_cast<std::uint64_t>(lanes[1]) * 0xFFFFFFDC00000051ull +
           static_cast<std::uint64_t>(lanes[0]) * 0x1E5FFFFFD27ull;
}

// CSingleInstanceTable 桶数阶梯：≤35 → 7，≤360 → 72，否则 722。
std::uint32_t PoolN(std::size_t n) {
    return n <= 35 ? 7u : (n <= 360 ? 72u : 722u);
}

// 7 桶、容量 5N：插入第 i 个元素后若 i+1 > 容量，则扩到 2*(i+1) 个桶，
// 旧桶按升序把条目 tail-append 到新链（链内保持插入序）。这个"增长点"同时
// 决定载荷物理序、身份表发射序、字符串池序；用普通稳定排序会错位。
template <typename K>
std::vector<std::size_t> SitOrder(const std::vector<K>& keys) {
    std::size_t n = 7, capacity = 5 * n;
    std::vector<std::vector<std::size_t>> chains(7);
    for (std::size_t i = 0; i < keys.size(); ++i) {
        chains[static_cast<std::size_t>(keys[i] % n)].push_back(i);
        if (i + 1 > capacity) {
            const std::size_t newN = 2 * (i + 1);
            std::vector<std::vector<std::size_t>> grown(newN);
            for (const auto& chain : chains) {
                for (std::size_t j : chain) {
                    grown[static_cast<std::size_t>(keys[j] % newN)].push_back(j);
                }
            }
            chains = std::move(grown);
            n = newN;
            capacity = 5 * newN;
        }
    }
    std::vector<std::size_t> order;
    order.reserve(keys.size());
    for (const auto& chain : chains) {
        order.insert(order.end(), chain.begin(), chain.end());
    }
    return order;
}

// ---------------------------------------------------------------------------
// XML 实体反转义（只用于属性值；PCM 字符串池存的是解码后的文本）
// ---------------------------------------------------------------------------
void AppendUtf8(std::string& out, std::uint32_t cp) {
    if (cp < 0x80) {
        out.push_back(static_cast<char>(cp));
    } else if (cp < 0x800) {
        out.push_back(static_cast<char>(0xC0u | (cp >> 6)));
        out.push_back(static_cast<char>(0x80u | (cp & 0x3Fu)));
    } else if (cp < 0x10000) {
        out.push_back(static_cast<char>(0xE0u | (cp >> 12)));
        out.push_back(static_cast<char>(0x80u | ((cp >> 6) & 0x3Fu)));
        out.push_back(static_cast<char>(0x80u | (cp & 0x3Fu)));
    } else {
        out.push_back(static_cast<char>(0xF0u | (cp >> 18)));
        out.push_back(static_cast<char>(0x80u | ((cp >> 12) & 0x3Fu)));
        out.push_back(static_cast<char>(0x80u | ((cp >> 6) & 0x3Fu)));
        out.push_back(static_cast<char>(0x80u | (cp & 0x3Fu)));
    }
}

std::string UnescapeXml(const std::string& v) {
    if (v.find('&') == std::string::npos) return v;
    std::string out;
    out.reserve(v.size());
    std::size_t i = 0;
    while (i < v.size()) {
        if (v[i] != '&') {
            out.push_back(v[i++]);
            continue;
        }
        const std::size_t j = v.find(';', i + 1);
        if (j == std::string::npos) {
            out.push_back(v[i++]);
            continue;
        }
        const std::string body = v.substr(i + 1, j - i - 1);
        std::uint32_t cp = 0;
        bool converted = false;
        if (body.size() > 2 && body[0] == '#' && (body[1] == 'x' || body[1] == 'X')) {
            std::uint64_t value = 0;
            bool ok = body.size() > 2;
            for (std::size_t k = 2; k < body.size() && ok; ++k) {
                const char c = body[k];
                int digit = -1;
                if (c >= '0' && c <= '9') digit = c - '0';
                else if (c >= 'a' && c <= 'f') digit = c - 'a' + 10;
                else if (c >= 'A' && c <= 'F') digit = c - 'A' + 10;
                else ok = false;
                if (ok) value = value * 16 + static_cast<std::uint64_t>(digit);
                if (value > 0x10FFFFull) ok = false;
            }
            if (ok) { cp = static_cast<std::uint32_t>(value); converted = true; }
        } else if (body.size() > 1 && body[0] == '#') {
            std::uint64_t value = 0;
            bool ok = body.size() > 1;
            for (std::size_t k = 1; k < body.size() && ok; ++k) {
                const char c = body[k];
                if (c < '0' || c > '9') { ok = false; break; }
                value = value * 10 + static_cast<std::uint64_t>(c - '0');
                if (value > 0x10FFFFull) ok = false;
            }
            if (ok) { cp = static_cast<std::uint32_t>(value); converted = true; }
        } else if (body == "amp") { cp = '&'; converted = true; }
        else if (body == "lt") { cp = '<'; converted = true; }
        else if (body == "gt") { cp = '>'; converted = true; }
        else if (body == "quot") { cp = '"'; converted = true; }
        else if (body == "apos") { cp = '\''; converted = true; }
        if (!converted) {
            out.push_back(v[i++]);
            continue;
        }
        AppendUtf8(out, cp);
        i = j + 1;
    }
    return out;
}

// ---------------------------------------------------------------------------
// MdHd 解码：复用 BuildMicrodomLayout 的字节，反解出元素树。
//
// 这样 uidx、属性顺序、直接文本都与微 DOM 写入器**同源**，不会因为另写一套
// XML 解析器而在节点编号上跑偏。MdDl 记录流按 type 定长：元素/文档记录带
// 子节点 uidx 数组，其余记录定长。
// ---------------------------------------------------------------------------
constexpr std::uint16_t kMdElement = 0x01;
constexpr std::uint16_t kMdAttribute = 0x02;
constexpr std::uint16_t kMdPcData = 0x03;
constexpr std::uint16_t kMdPi = 0x05;
constexpr std::uint16_t kMdComment = 0x06;
constexpr std::uint16_t kMdDocument = 0x07;
constexpr std::uint16_t kMdCData = 0x13;

struct MdNode {
    std::uint16_t type = 0;
    std::uint32_t recordOffset = 0;
    std::uint32_t local = kNoId;
    std::uint32_t value = kNoId;
    std::vector<std::uint32_t> children;  // uidx 数组
};

struct MdDom {
    std::vector<std::string> strings;
    std::vector<MdNode> nodes;
};

struct MdPatchInfo {
    std::uint32_t firstElementUidx = 0;
    bool firstElementHasMin = false;  // min(属性数, 内容子数) != 0
};

bool DecodeMicrodom(const std::vector<std::uint8_t>& md, MdDom& dom, MdPatchInfo& patch,
                    std::string& error) {
    if (md.size() < 24 || ReadU32(md.data(), 0) != 0x6448644Du) {
        error = "MdHd 头不合法";
        return false;
    }
    const std::uint32_t offSp = ReadU32(md.data(), 0x08);
    const std::uint32_t offDl = ReadU32(md.data(), 0x0C);
    if (offSp + 12 > md.size() || offDl + 20 > md.size()) {
        error = "MdHd 段偏移越界";
        return false;
    }
    if (ReadU32(md.data(), offSp) != 0x7053644Du) {
        error = "MdSp 魔数不匹配";
        return false;
    }
    const std::uint32_t spLen = ReadU32(md.data(), offSp + 4);
    const std::uint32_t stringCount = ReadU32(md.data(), offSp + 8);
    if (offSp + spLen > md.size()) {
        error = "MdSp 长度越界";
        return false;
    }
    std::size_t p = static_cast<std::size_t>(offSp) + 12;
    const std::size_t spEnd = static_cast<std::size_t>(offSp) + spLen;
    dom.strings.reserve(stringCount);
    for (std::uint32_t i = 0; i < stringCount; ++i) {
        const std::size_t start = p;
        while (p < spEnd && md[p] != 0) ++p;
        if (p >= spEnd) {
            error = "MdSp 字符串未以 NUL 结束";
            return false;
        }
        dom.strings.emplace_back(reinterpret_cast<const char*>(md.data() + start), p - start);
        ++p;
    }

    if (ReadU32(md.data(), offDl) != 0x6C44644Du) {
        error = "MdDl 魔数不匹配";
        return false;
    }
    const std::uint32_t width = ReadU32(md.data(), offDl + 4);
    const std::uint32_t positioned = ReadU32(md.data(), offDl + 8);
    patch.firstElementUidx = ReadU32(md.data(), offDl + 12);
    if (width < 1 || width > 3) {
        error = "MdDl 宽度码不合法";
        return false;
    }
    p = static_cast<std::size_t>(offDl) + 20;
    dom.nodes.reserve(positioned);
    for (std::uint32_t i = 0; i < positioned; ++i) {
        if (p >= md.size()) {
            error = "MdDl 记录流越界";
            return false;
        }
        MdNode node;
        node.recordOffset = static_cast<std::uint32_t>(p);
        const std::uint8_t tag = md[p];
        node.type = tag;
        // 元素记录的"类型字节"就是 b0 本身（b0 = 1 | bit7 | b0Extra<<4），所以
        // 低 4 位为 1 的字节都按元素记录解析；bit7/b0Extra 位是元素标志，不是类型。
        const bool isElement = (tag & 0x0Fu) == kMdElement;
        if (isElement) node.type = kMdElement;
        switch (node.type) {
            case kMdDocument: {
                if (p + 4 > md.size()) { error = "MdDl 文档记录越界"; return false; }
                const std::uint32_t count = ReadU16(md.data(), p + 2);
                std::size_t q = p + 4;
                if (q + width * count > md.size()) { error = "MdDl 文档子数组越界"; return false; }
                node.children.resize(count);
                for (std::uint32_t k = 0; k < count; ++k) {
                    node.children[k] = width == 1 ? md[q + k]
                                      : width == 2 ? ReadU16(md.data(), q + 2ull * k)
                                                   : ReadU32(md.data(), q + 4ull * k);
                }
                p = Align4(q + width * count);
                break;
            }
            case kMdElement: {
                if (p + 24 > md.size()) { error = "MdDl 元素记录越界"; return false; }
                const bool extended = (md[p + 1] & 0x01u) != 0;
                const std::uint32_t packed = ReadU32(md.data(), p + 4);
                const std::uint32_t mn = extended ? packed : 0u;
                const std::uint32_t mx = extended ? ReadU32(md.data(), p + 24) : packed;
                const std::uint32_t count = mn + mx;
                std::size_t q = p + 24 + (extended ? 4 : 0);
                if (q + width * count > md.size()) { error = "MdDl 元素子数组越界"; return false; }
                node.children.resize(count);
                for (std::uint32_t k = 0; k < count; ++k) {
                    node.children[k] = width == 1 ? md[q + k]
                                      : width == 2 ? ReadU16(md.data(), q + 2ull * k)
                                                   : ReadU32(md.data(), q + 4ull * k);
                }
                node.local = ReadU32(md.data(), p + 20);
                p = Align4(q + width * count);
                break;
            }
            case kMdAttribute: {
                if (p + 20 > md.size()) { error = "MdDl 属性记录越界"; return false; }
                node.local = ReadU32(md.data(), p + 12);
                node.value = ReadU32(md.data(), p + 16);
                p += 20;
                break;
            }
            case kMdPcData:
            case kMdComment:
            case kMdCData: {
                if (p + 8 > md.size()) { error = "MdDl 叶记录越界"; return false; }
                node.value = ReadU32(md.data(), p + 4);
                p += 8;
                break;
            }
            case kMdPi: {
                if (p + 16 > md.size()) { error = "MdDl PI 记录越界"; return false; }
                node.local = ReadU32(md.data(), p + 4);
                node.value = ReadU32(md.data(), p + 8);
                p += 16;
                break;
            }
            default:
                error = "MdDl 未知记录类型 " + std::to_string(node.type);
                return false;
        }
        dom.nodes.push_back(std::move(node));
    }

    // 父子关系 + 第一个元素 min() 标志（b0Extra 只写进第一个元素记录）。
    patch.firstElementHasMin = false;
    uint32_t firstElementIndex = 0;
    bool foundFirst = false;
    for (std::size_t i = 0; i < dom.nodes.size(); ++i) {
        const MdNode& n = dom.nodes[i];
        std::uint32_t attrCount = 0, contentCount = 0;
        for (std::uint32_t c : n.children) {
            if (c >= dom.nodes.size()) { error = "MdDl 子节点索引越界"; return false; }
            if (dom.nodes[c].type == kMdAttribute) ++attrCount;
            else ++contentCount;
        }
        if (n.type == kMdElement && !foundFirst) {
            foundFirst = true;
            firstElementIndex = static_cast<uint32_t>(i);
            patch.firstElementHasMin = std::min(attrCount, contentCount) != 0;
        }
    }
    (void)firstElementIndex;
    patch.firstElementUidx = firstElementIndex;
    return true;
}

std::string GetString(const MdDom& dom, std::uint32_t id) {
    if (id >= dom.strings.size()) return std::string();
    return dom.strings[id];
}

// 临时诊断（OCID_PCM_DEBUG_DOM=1）：按 uidx 打印整棵微 DOM。
void DbgDumpDom(const MdDom& dom) {
    if (std::getenv("OCID_PCM_DEBUG_DOM") == nullptr) return;
    for (std::size_t i = 0; i < dom.nodes.size(); ++i) {
        const MdNode& n = dom.nodes[i];
        std::fprintf(stderr, "DOM %zu t=%u local=<%s> value=<%s> kids=%zu\n", i, n.type,
                     GetString(dom, n.local).c_str(), GetString(dom, n.value).c_str(),
                     n.children.size());
    }
}

// 元素视图：只保留生成 PCM 需要的字段（父/子都用 elems 下标）。
struct Elem {
    std::uint32_t node = 0;
    std::uint32_t uidx = 0;
    std::uint32_t parent = kNoId;
    std::string local;
    std::vector<std::pair<std::string, std::string>> attrs;  // 已做一次 XML 反转义
    std::vector<std::uint32_t> kids;                          // elems 下标
    std::string text;                                         // 直接 PCData 拼接
};

std::unordered_map<std::string, std::string> AttrsOf(const Elem& e) {
    std::unordered_map<std::string, std::string> map;
    for (const auto& kv : e.attrs) map[kv.first] = kv.second;  // dict() 语义：后者覆盖
    return map;
}

std::string Attr(const std::unordered_map<std::string, std::string>& attrs,
                 const std::string& name) {
    const auto it = attrs.find(name);
    return it == attrs.end() ? std::string() : it->second;
}

bool BuildElems(const MdDom& dom, std::vector<Elem>& elems, std::uint32_t& root,
                std::string& error) {
    std::vector<std::uint32_t> order;
    std::function<void(std::uint32_t)> walk = [&](std::uint32_t nodeIndex) {
        order.push_back(nodeIndex);
        for (std::uint32_t c : dom.nodes[nodeIndex].children) {
            if (dom.nodes[c].type == kMdElement) walk(c);
        }
    };
    bool haveRoot = false;
    for (std::uint32_t c : dom.nodes[0].children) {
        if (dom.nodes[c].type == kMdElement) {
            walk(c);
            haveRoot = true;
            break;
        }
    }
    if (!haveRoot) {
        error = "清单里没有根元素";
        return false;
    }

    std::unordered_map<std::uint32_t, std::uint32_t> elemOfNode;
    elems.clear();
    elems.reserve(order.size());
    for (std::size_t i = 0; i < order.size(); ++i) {
        const MdNode& n = dom.nodes[order[i]];
        Elem e;
        e.node = order[i];
        // Python 里 e.uidx 是微 DOM 的"有位置节点序号"（== MdDl 记录下标），
        // 不是元素序号；96B 记录 [16]、hash 记录、cache 记录都直接写这个值。
        e.uidx = order[i];
        e.local = GetString(dom, n.local);
        for (std::uint32_t c : n.children) {
            const MdNode& child = dom.nodes[c];
            if (child.type == kMdAttribute) {
                e.attrs.emplace_back(GetString(dom, child.local),
                                     UnescapeXml(GetString(dom, child.value)));
            } else if (child.type == kMdPcData) {
                e.text += GetString(dom, child.value);
            }
        }
        elemOfNode[order[i]] = static_cast<std::uint32_t>(i);
        elems.push_back(std::move(e));
    }
    for (std::size_t i = 0; i < elems.size(); ++i) {
        for (std::uint32_t c : dom.nodes[elems[i].node].children) {
            const MdNode& child = dom.nodes[c];
            if (child.type == kMdElement) {
                const auto it = elemOfNode.find(c);
                if (it == elemOfNode.end()) { error = "元素索引不一致"; return false; }
                elems[i].kids.push_back(it->second);
                elems[it->second].parent = static_cast<std::uint32_t>(i);
            }
        }
    }
    root = 0;
    return true;
}

// ---------------------------------------------------------------------------
// 字符串 interner / blob 序号
//
// ids 是**精确字符串**的 0 基插入序（参考实现的 dict；空串不入池，返回 -1）。
// blobs 只用于计算 96B 记录里的 blob id，不保留内容。
// ---------------------------------------------------------------------------
class Interner {
 public:
    std::int32_t String(const std::string& s) {
        if (s.empty()) return -1;
        const auto it = ids_.find(s);
        if (it != ids_.end()) return static_cast<std::int32_t>(it->second);
        const std::uint32_t id = static_cast<std::uint32_t>(order_.size());
        order_.push_back(s);
        ids_.emplace(s, id);
        return static_cast<std::int32_t>(id);
    }
    // 遍历器对空串也会 interning（<memberships> 的辅助表）。
    std::uint32_t StringEvenEmpty(const std::string& s) {
        const auto it = ids_.find(s);
        if (it != ids_.end()) return it->second;
        const std::uint32_t id = static_cast<std::uint32_t>(order_.size());
        order_.push_back(s);
        ids_.emplace(s, id);
        return id;
    }
    std::uint32_t Blob(const std::string& /*payload*/) {
        return static_cast<std::uint32_t>(blobCount_++);
    }
    const std::vector<std::string>& order() const { return order_; }

 private:
    std::vector<std::string> order_;
    std::unordered_map<std::string, std::uint32_t> ids_;
    std::size_t blobCount_ = 0;
};

// ---------------------------------------------------------------------------
// 槽 / 表 pack
// ---------------------------------------------------------------------------
struct Slot {
    std::uint8_t type = 1;
    std::string payload;
};

struct Row {
    std::uint32_t f0 = 0;
    std::uint32_t f1 = 0;
    std::uint32_t f2 = 0;
};

void AppendSlot(std::vector<Slot>& slots, std::uint8_t type, std::string payload) {
    slots.push_back(Slot{type, std::move(payload)});
}

std::string PackU32(std::initializer_list<std::uint32_t> values) {
    std::string out;
    out.resize(values.size() * 4);
    std::size_t i = 0;
    for (std::uint32_t v : values) {
        out[i + 0] = static_cast<char>(v & 0xFFu);
        out[i + 1] = static_cast<char>((v >> 8) & 0xFFu);
        out[i + 2] = static_cast<char>((v >> 16) & 0xFFu);
        out[i + 3] = static_cast<char>((v >> 24) & 0xFFu);
        i += 4;
    }
    return out;
}

// 通用表骨架：复用 PcmEncodeRecordTable（它会按值域自动收窄 f1/f2、按 count
// 决定省不省 f0；type9 永不落 f0）。
bool MakeRecordTable(std::uint8_t type, const std::vector<Row>& rows, bool bit0,
                     std::string& out, std::string& error) {
    PcmRecordTable table;
    table.type = type;
    table.bit0 = bit0;
    table.f0.reserve(rows.size());
    table.f1.reserve(rows.size());
    table.f2.reserve(rows.size());
    for (const Row& r : rows) {
        table.f0.push_back(r.f0);
        table.f1.push_back(r.f1);
        table.f2.push_back(r.f2);
    }
    std::string packError;
    const std::vector<std::uint8_t> bytes = PcmEncodeRecordTable(table, packError);
    if (!packError.empty()) {
        error = "表 pack 失败：" + packError;
        return false;
    }
    out.assign(bytes.begin(), bytes.end());
    return true;
}

// key-1/210/17 风格：按 f0 升序。
// f0（32 位哈希）撞车时真机的次序既非 f1 升也非 f1 降（01344 的 4 组 .fon 并列
// 里 3 组升、1 组降；00852 三组里两组降、一组升），也不是 64 位哈希序，看起来是
// 不稳定 std::sort 在不同输入序上的结果，尚未反出真规则。
// OCID_SORT_MODE 逐模式实测（9600 全量 T4）：0=f1 升 2544、1=f1 降 2546（默认）、
// 2=std::sort 只比 f0 2545、其余更低。mode 1 净 +12（修 13 坏 1，坏例
// securestartup-core）；确定真规则后应删掉这个开关。
int SortMode() {
    static const int mode = [] {
        const char* s = std::getenv("OCID_SORT_MODE");
        return s != nullptr ? std::atoi(s) : 1;
    }();
    return mode;
}

void SortAsc(std::vector<Row>& rows) {
    const int mode = SortMode();
    if (mode == 2) {
        std::sort(rows.begin(), rows.end(),
                  [](const Row& a, const Row& b) { return a.f0 < b.f0; });
        return;
    }
    if (mode == 3) {
        std::reverse(rows.begin(), rows.end());
        std::sort(rows.begin(), rows.end(),
                  [](const Row& a, const Row& b) { return a.f0 < b.f0; });
        return;
    }
    if (mode == 4) {
        std::stable_sort(rows.begin(), rows.end(),
                         [](const Row& a, const Row& b) { return a.f0 < b.f0; });
        return;
    }
    std::stable_sort(rows.begin(), rows.end(), [mode](const Row& a, const Row& b) {
        if (a.f0 != b.f0) return a.f0 < b.f0;
        return mode == 1 ? a.f1 > b.f1 : a.f1 < b.f1;
    });
}

// key-9 外层表风格：先按 f0 % pool_n 分桶，再按 f0、f1 升序（链内稳定）。
void SortStrTable(std::vector<Row>& rows) {
    const std::uint32_t n = PoolN(rows.size());
    std::stable_sort(rows.begin(), rows.end(), [n](const Row& a, const Row& b) {
        const std::uint32_t ba = a.f0 % n, bb = b.f0 % n;
        if (ba != bb) return ba < bb;
        if (a.f0 != b.f0) return a.f0 < b.f0;
        return a.f1 < b.f1;
    });
}

void SortByKeys(std::vector<Row>& rows, const std::vector<std::uint32_t>& keys) {
    std::vector<std::size_t> order(rows.size());
    for (std::size_t i = 0; i < order.size(); ++i) order[i] = i;
    std::stable_sort(order.begin(), order.end(),
                     [&keys](std::size_t a, std::size_t b) { return keys[a] < keys[b]; });
    std::vector<Row> sorted;
    sorted.reserve(rows.size());
    for (std::size_t i : order) sorted.push_back(rows[i]);
    rows = std::move(sorted);
}

// 临时诊断（OCID_PCM_DEBUG_TIE=1）：把一张 2 列表的最终行序连同 f1 串打出来，
// 用来确定 f0（32 位哈希）撞车时真机按什么定序。
void DbgDumpTable(const char* tag, const std::vector<Row>& rows, const Interner& inter) {
    if (std::getenv("OCID_PCM_DEBUG_TIE") == nullptr) return;
    std::fprintf(stderr, "TIE %s n=%zu\n", tag, rows.size());
    for (const Row& r : rows) {
        const char* s = r.f1 < inter.order().size() ? inter.order()[r.f1].c_str() : "?";
        std::fprintf(stderr, "TIE %s %u %u %u %s\n", tag, r.f0, r.f1, r.f2, s);
    }
}

// ---------------------------------------------------------------------------
// 身份表（type 4 = DefinitionIdentity，type 5 = ReferenceIdentity）
// 载荷 = u32 count + count * { -1, 属性名 id, 值 id }，不是通用骨架。
// ---------------------------------------------------------------------------
struct Ident {
    std::uint8_t type = 4;
    std::vector<std::pair<std::string, std::string>> rows;
};

const char* const kIdentAttrs[] = {"Name", "Culture", "TypeName", "Type", "Version",
                                   "PublicKeyToken", "ProcessorArchitecture", "VersionScope"};

std::vector<std::pair<std::string, std::string>> IdentRows(
    const std::unordered_map<std::string, std::string>& attrs, bool definition) {
    static const char* const kSources[] = {"name", "language", "typeName", "type", "version",
                                           "publicKeyToken", "processorArchitecture",
                                           "versionScope"};
    std::vector<std::pair<std::string, std::string>> rows;
    for (std::size_t i = 0; i < 8; ++i) {
        std::string v = Attr(attrs, kSources[i]);
        if (v.empty()) continue;
        if (std::string(kIdentAttrs[i]) == "Culture") {
            // "*"（资源通配）两种身份都不落；neutral 只对定义身份省略，
            // 引用身份仍序列化 Culture=neutral（02534 的 *-deployment 族）。
            if (v == "*") continue;
            if (LowerAscii(v) == "neutral" && definition) continue;
        } else if (std::string(kIdentAttrs[i]) == "VersionScope") {
            if (LowerAscii(v) == "nonsxs") v = "NonSxS";
        }
        rows.emplace_back(kIdentAttrs[i], v);
    }
    return rows;
}

// CalculateKeysForBuiltinAttributes：固定属性序折叠；Culture=neutral 不参与折叠
// （虽然引用身份表仍会序列化该行）。
std::uint64_t IdentHash(const std::vector<std::pair<std::string, std::string>>& rows) {
    static const char* const kOrder[] = {"Name", "Culture", "TypeName", "Type", "Version",
                                         "PublicKeyToken", "PublicKey",
                                         "ProcessorArchitecture", "VersionScope"};
    std::map<std::string, std::string> have;
    for (const auto& kv : rows) have[kv.first] = kv.second;
    const auto neutral = have.find("Culture");
    if (neutral != have.end() && LowerAscii(neutral->second) == "neutral") have.erase(neutral);
    std::uint64_t h = 0;
    for (const char* name : kOrder) {
        const auto it = have.find(name);
        if (it == have.end()) continue;
        const std::uint64_t pair =
            (Hash64CodePoints(name) * kFold + Hash64CodePoints(it->second)) & kM64;
        h = (h * kFold + pair) & kM64;
    }
    return h;
}

std::string BuildIdentTable(Interner& inter,
                            const std::vector<std::pair<std::string, std::string>>& rows) {
    std::string out = PackU32({static_cast<std::uint32_t>(rows.size())});
    for (const auto& kv : rows) {
        out += PackU32({kNoId, static_cast<std::uint32_t>(inter.String(kv.first)),
                        static_cast<std::uint32_t>(inter.String(kv.second))});
    }
    return out;
}

// ---------------------------------------------------------------------------
// 元素记录
// ---------------------------------------------------------------------------
std::string BuildFileRecord(Interner& inter, const Elem& e, int sddBlob, std::uint32_t targets,
                            int hashAttrBlob, int gacBlob, const std::string& sddName,
                            std::uint32_t writeType, std::uint32_t attrFlags, int sigBlob = -1) {
    const auto a = AttrsOf(e);
    // AddFile 的 interning 顺序：sdd 名、name、loadFrom、""、""、destinationPath、
    // sourceName、sourcePath、importPath。空串返回 -1 且不入池。
    const int nSdd = inter.String(sddName);
    const int idName = inter.String(Attr(a, "name"));
    inter.String(Attr(a, "loadFrom"));
    inter.String("");
    inter.String("");
    const int idDest = inter.String(Attr(a, "destinationPath"));
    const int idSrcName = inter.String(Attr(a, "sourceName"));
    const int idSrcPath = inter.String(Attr(a, "sourcePath"));
    const int idImpPath = inter.String(Attr(a, "importPath"));
    (void)nSdd;

    // [14] 是生成期的 "targets" 伪 blob id，等于**本条 96B 文件记录自己的
    // blob 号减一**：真机 3244 条 96B 记录全部满足 [14] == slotIndex-1
    // （8419 全语料，含 01368/01849 这些依赖/信任/摘要各异的样本）。
    // 之前的 2+2*nDep+trust+4*ordinal 只是它的常见情形近似：每文件恰好
    // 新建 4 个 blob 时两者相等，blob 被共享（同 sdd/同 digest）时就偏大。

    return PackU32({
        1u,                                               // [0]  writeableType
        kNoId,                                            // [1]  loadFrom 的 blob id 恒 -1
        static_cast<std::uint32_t>(idSrcPath),            // [2]  sourcePath
        static_cast<std::uint32_t>(idImpPath),            // [3]  importPath
        static_cast<std::uint32_t>(idSrcName),            // [4]  sourceName
        kNoId,                                            // [5]
        static_cast<std::uint32_t>(hashAttrBlob),         // [6]  <file hash="..."> 原始 blob id
        0u, 0u, 0u,                                       // [7][8][9]
        kNoId,                                            // [10]
        0u,                                               // [11] 属性字（语料上恒 0）
        kNoId,                                            // [12]
        writeType,                                        // [13] writeableType：缺省 1、systemData 3
        targets,                                          // [14]
        static_cast<std::uint32_t>(idDest),               // [15] destinationPath
        e.uidx,                                           // [16] 微 DOM 节点索引
        static_cast<std::uint32_t>(gacBlob),              // [17] 36B <gac> 记录 blob id
        static_cast<std::uint32_t>(idName),               // [18] name
        static_cast<std::uint32_t>(sddBlob),              // [19] 8B <securityDescriptor> blob id
        attrFlags,                                        // [20] FILE_ATTRIBUTE 位（缺省 0xA0）
        static_cast<std::uint32_t>(sigBlob),               // [21] <signatureInfo> 8B blob id
        kNoId, 0u,                                         // [22][23]
    });
}

// [11] 是**位置**而不是计数：它是 20B <deployment> 常量 blob 的槽号，也就是
// 该 blob 之前已发射的元素记录（依赖对、文件组、字符串值…）的条数。02734/02786/
// 02805 三个带 <localization> 的部署清单证明：2*nDep+4*nFile 的计数式公式少算了
// 字符串值槽（3/5/50 → 5 = 4+1、8 = 6+2、50 = 48+2）。
std::string BuildDocRecord(std::size_t nDep, std::uint32_t depBlobSlot, bool hasMvid,
                           bool hasDeployment, bool hasNoInheritable, bool hasGac,
                           std::int32_t resDepIdx, bool wild, bool hasHashFile,
                           bool hasSourcePath, bool legacyIdent) {
    std::uint32_t flags = 1;
    if (hasDeployment) flags |= 8;
    if ((hasNoInheritable && !hasSourcePath) || (hasGac && hasHashFile)) flags |= 16;
    // 0x10 还会被"没有 language 属性的旧式 Win32 身份"置位：gdiplus 1.0/1.1、
    // isolationautomation.proxystub、systemcompatible、isolationautomation 这五份
    // 全语料里唯一没有 language 的清单，真机 flags 都是 0x11。
    if (legacyIdent) flags |= 16;
    const std::uint32_t d11 = hasDeployment ? depBlobSlot : kNoId;
    return PackU32({
        1u,
        flags,
        0u,
        static_cast<std::uint32_t>(nDep),
        kNoId,
        1u,
        kNoId, kNoId, kNoId,
        hasMvid ? 7u : kNoId,   // [9] mvid 标志
        kNoId,
        d11,                    // [11]
        kNoId, kNoId,
        // [14]：有 resourceType 依赖时是它的**序号**，否则是通配符标志（0/-1）。
        // 03050/03058/03065（2 依赖、资源在第 1 个 → 1）、03063（9 依赖 → 2）、
        // 03074（6 依赖 → 5）这五份 WMI 清单证明它不是布尔位。
        resDepIdx >= 0 ? static_cast<std::uint32_t>(resDepIdx) : (wild ? 0u : kNoId),
        kNoId, kNoId,
    });
}

std::string BuildDepRecord(std::uint32_t ordinal, bool wild) {
    return PackU32({kNoId, kNoId, 0u, 0u, kNoId, 1u, wild ? 4u : 0u, kNoId, kNoId, kNoId,
                    kNoId, ordinal});
}

// 依赖子记录（16B）的 flag 只跟依赖清单里的 language 有关，跟 discoverable /
// resourceType 都无关：全语料 2206 条依赖的统计是
//   language="neutral"        → 0x10
//   language="*"              → 0x90 (0x80 表示通配，同时带 0x10)
//   其它（en-US/en-us/en-GB…）→ 0x00
// （02701/02768/02714 的 discoverable="yes"+neutral = 0x10，02543 的 "*" = 0x90。）
std::string BuildDepChild(std::uint32_t ordinal, const std::string& lang) {
    const bool wild = lang == "*";
    std::uint32_t flags = 0;
    if (wild || lang == "neutral") flags |= 0x10u;
    if (wild) flags |= 0x80u;
    return PackU32({flags, kNoId, 2u * ordinal, wild ? 2u : 1u});
}

std::string BuildHash20() { return PackU32({kNoId, kNoId, 16u, 0u, 0x00020001u}); }

// `<directory attributes="...">` / `<file attributes="...">` → 真机记录里的
// FILE_ATTRIBUTE 位。语料只出现四种文本：缺省、`system`、`readOnly`、
// `hidden system`。目录记录的缺省值是 FILE_ATTRIBUTE_NORMAL(0x80)，文件记录
// 的缺省值是 NORMAL|ARCHIVE(0xA0)，所以缺省值由调用方通过 fallback 传入
// （02963/02983 的 `hidden system` 文件 = 0x2|0x4 = 6）。
std::uint32_t AttrFlagsFromText(const std::string& text, std::uint32_t fallback) {
    std::uint32_t flags = 0;
    std::size_t i = 0;
    while (i < text.size()) {
        while (i < text.size() &&
               (IsSpaceByte(text[i]) || text[i] == ',' || text[i] == ';'))
            ++i;
        const std::size_t b = i;
        while (i < text.size() && !IsSpaceByte(text[i]) && text[i] != ',' &&
               text[i] != ';')
            ++i;
        if (i == b) break;
        const std::string tok = LowerAscii(text.substr(b, i - b));
        if (tok == "readonly") flags |= 0x001u;
        else if (tok == "hidden") flags |= 0x002u;
        else if (tok == "system") flags |= 0x004u;
        else if (tok == "directory") flags |= 0x010u;
        else if (tok == "archive") flags |= 0x020u;
        else if (tok == "normal") flags |= 0x080u;
        else if (tok == "temporary") flags |= 0x100u;
        else if (tok == "sparsefile") flags |= 0x200u;
        else if (tok == "reparsepoint") flags |= 0x400u;
        else if (tok == "compressed") flags |= 0x800u;
        else if (tok == "offline") flags |= 0x1000u;
        else if (tok == "notcontentindexed") flags |= 0x2000u;
        else if (tok == "encrypted") flags |= 0x4000u;
    }
    return flags != 0 ? flags : fallback;
}

// ---------------------------------------------------------------------------
// 二分 / 十六进制 / base64（照 Python 的宽松语义：非法输入 → 空）
// ---------------------------------------------------------------------------
bool HexDecode(const std::string& text, std::string& out) {
    out.clear();
    std::vector<int> digits;
    digits.reserve(text.size());
    for (char ch : text) {
        if (IsSpaceByte(ch)) continue;  // Python bytes.fromhex 跳过 ASCII 空白
        int d = -1;
        if (ch >= '0' && ch <= '9') d = ch - '0';
        else if (ch >= 'a' && ch <= 'f') d = ch - 'a' + 10;
        else if (ch >= 'A' && ch <= 'F') d = ch - 'A' + 10;
        if (d < 0) return false;
        digits.push_back(d);
    }
    if (digits.size() % 2 != 0) return false;
    out.resize(digits.size() / 2);
    for (std::size_t i = 0; i < out.size(); ++i) {
        out[i] = static_cast<char>((digits[2 * i] << 4) | digits[2 * i + 1]);
    }
    return true;
}

int Base64Value(char c) {
    if (c >= 'A' && c <= 'Z') return c - 'A';
    if (c >= 'a' && c <= 'z') return c - 'a' + 26;
    if (c >= '0' && c <= '9') return c - '0' + 52;
    if (c == '+') return 62;
    if (c == '/') return 63;
    return -1;
}

void Base64Decode(const std::string& text, std::string& out) {
    out.clear();
    std::uint32_t value = 0;
    int bits = 0;
    for (char c : text) {
        if (c == '=') break;
        const int d = Base64Value(c);
        if (d < 0) continue;  // b64decode 默认忽略非字母表字符
        value = (value << 6) | static_cast<std::uint32_t>(d);
        bits += 6;
        if (bits >= 8) {
            bits -= 8;
            out.push_back(static_cast<char>((value >> bits) & 0xFFu));
        }
    }
}

// ---------------------------------------------------------------------------
// <registryKeys>（CmiRegistry::CKey::BuildKeyTable）
//
// 后序 DFS：一个节点先发 securityDescriptor blob、自己的 value 记录 + value 表、
// 递归每个孩子的记录 + 孩子表，最后发自己的 24B key 记录。所有 blob/表都走
// BuildBlob 的内容寻址去重（同一份 build 里相同的字节复用已有 slot）。
// ---------------------------------------------------------------------------
struct RegVal {
    std::string name;
    std::uint32_t type = 1;
    std::string text;
};

struct RegNode {
    std::string name;
    int parent = -1;
    std::vector<std::string> childKeys;  // 小写名，创建序
    std::vector<int> children;
    std::unordered_map<std::string, int> childIndex;
    std::vector<RegVal> vals;
    bool declared = false;
    std::string sdd;
};

std::uint32_t RegistryValueType(const std::string& name) {
    if (name == "REG_SZ") return 1;
    if (name == "REG_EXPAND_SZ") return 2;
    if (name == "REG_MULTI_SZ") return 3;
    if (name == "REG_BINARY") return 4;
    if (name == "REG_DWORD") return 5;
    if (name == "REG_QWORD") return 12;
    return 1;
}

std::vector<std::string> SplitKeyPath(const std::string& key) {
    std::vector<std::string> parts;
    std::size_t start = 0;
    while (start <= key.size()) {
        const std::size_t p = key.find('\\', start);
        const std::string comp =
            key.substr(start, p == std::string::npos ? std::string::npos : p - start);
        if (!comp.empty()) parts.push_back(comp);
        if (p == std::string::npos) break;
        start = p + 1;
    }
    return parts;
}

class RegistryBuilder {
 public:
    RegistryBuilder(Interner& inter, std::vector<Slot>& slots)
        : inter_(inter), slots_(slots) {
        // BuildBlob 内容寻址：用当前所有 slot 的载荷做初值（首次出现者胜）。
        for (std::size_t i = 0; i < slots_.size(); ++i) {
            dedup_.emplace(slots_[i].payload, i);
        }
    }

    bool Build(const std::vector<Elem>& elems, std::string& error) {
        parse(elems);
        if (nodes_.empty()) {
            error = "registry 树为空";
            return false;
        }
        root_ = emitChildren(0);
        if (!ok_) {
            error = error_;
            return false;
        }
        return true;
    }

    int root() const { return root_; }

 private:
    void parse(const std::vector<Elem>& elems) {
        nodes_.push_back(RegNode{});
        std::unordered_map<std::uint32_t, std::vector<std::string>> pathCache;

        const auto pathOf = [](const Elem& e) {
            return SplitKeyPath(Attr(AttrsOf(e), "keyName"));
        };
        const auto find = [this](const std::vector<std::string>& path) {
            int cur = 0;
            for (const std::string& comp : path) {
                const std::string key = LowerAscii(comp);
                auto it = nodes_[static_cast<std::size_t>(cur)].childIndex.find(key);
                if (it == nodes_[static_cast<std::size_t>(cur)].childIndex.end()) {
                    RegNode node;
                    node.name = comp;
                    node.parent = cur;
                    nodes_.push_back(std::move(node));
                    const int idx = static_cast<int>(nodes_.size() - 1);
                    nodes_[static_cast<std::size_t>(cur)].childIndex.emplace(key, idx);
                    nodes_[static_cast<std::size_t>(cur)].childKeys.push_back(key);
                    nodes_[static_cast<std::size_t>(cur)].children.push_back(idx);
                    cur = idx;
                } else {
                    cur = it->second;
                }
            }
            return cur;
        };

        for (std::size_t i = 0; i < elems.size(); ++i) {
            const Elem& e = elems[i];
            if (e.local == "registryKey") {
                const std::vector<std::string> path = pathOf(e);
                if (path.empty()) continue;
                pathCache[static_cast<std::uint32_t>(i)] = path;
                const int node = find(path);
                nodes_[static_cast<std::size_t>(node)].declared = true;
                for (std::uint32_t kid : e.kids) {
                    if (elems[kid].local == "securityDescriptor") {
                        const std::string name = Attr(AttrsOf(elems[kid]), "name");
                        if (!name.empty()) nodes_[static_cast<std::size_t>(node)].sdd = name;
                    }
                }
            } else if (e.local == "registryValue") {
                // 所属 key = 最近的 registryKey 祖先的 keyName 路径。
                std::uint32_t p = e.parent;
                const std::vector<std::string>* path = nullptr;
                while (p != kNoId) {
                    if (elems[p].local == "registryKey") {
                        auto it = pathCache.find(p);
                        if (it == pathCache.end()) {
                            it = pathCache.emplace(p, pathOf(elems[p])).first;
                        }
                        path = &it->second;
                        break;
                    }
                    p = elems[p].parent;
                }
                if (path == nullptr || path->empty()) continue;
                const int node = find(*path);
                const auto at = AttrsOf(e);
                RegVal val;
                val.name = Attr(at, "name");
                val.type = RegistryValueType(Attr(at, "valueType"));
                val.text = Attr(at, "value");
                nodes_[static_cast<std::size_t>(node)].vals.push_back(std::move(val));
            }
        }
    }

    int Blob(const std::string& payload, std::uint8_t type) {
        auto it = dedup_.find(payload);
        if (it != dedup_.end()) return static_cast<int>(it->second);
        const int slot = static_cast<int>(slots_.size());
        slots_.push_back(Slot{type, payload});
        dedup_.emplace(payload, static_cast<std::size_t>(slot));
        return slot;
    }

    int Table(std::vector<Row> rows) {
        std::stable_sort(rows.begin(), rows.end(),
                         [](const Row& a, const Row& b) { return a.f0 < b.f0; });
        std::string payload, error;
        if (!MakeRecordTable(6, rows, true, payload, error)) {
            ok_ = false;
            error_ = error;
            return -1;
        }
        return Blob(payload, 6);
    }

    int emitChildren(int node) {
        const RegNode& n = nodes_[static_cast<std::size_t>(node)];
        if (n.children.empty()) return -1;
        std::vector<std::uint64_t> hashes;
        hashes.reserve(n.children.size());
        for (int c : n.children) {
            hashes.push_back(Hash64(LowerAscii(nodes_[static_cast<std::size_t>(c)].name)));
        }
        const std::vector<std::size_t> src = SitOrder(hashes);
        std::vector<Row> rows;
        rows.reserve(src.size());
        for (std::size_t i : src) {
            const int c = n.children[i];
            const int slot = emit(c);
            if (!ok_) return -1;
            rows.push_back(Row{static_cast<std::uint32_t>(
                                   Hash64(LowerAscii(nodes_[static_cast<std::size_t>(c)].name)) &
                                   kM32),
                               static_cast<std::uint32_t>(i), static_cast<std::uint32_t>(slot)});
        }
        // 名字在**输出表自己的桶序**里 interning（FIFO = 上面的 src 迭代序）。
        std::vector<std::uint32_t> f0s;
        f0s.reserve(rows.size());
        for (const Row& r : rows) f0s.push_back(r.f0);
        const std::vector<std::size_t> order = SitOrder(f0s);
        std::unordered_map<std::size_t, std::uint32_t> ids;
        for (std::size_t i : order) {
            ids[i] = inter_.StringEvenEmpty(
                nodes_[static_cast<std::size_t>(n.children[src[i]])].name);
        }
        for (std::size_t i = 0; i < rows.size(); ++i) rows[i].f1 = ids[i];
        return Table(std::move(rows));
    }

    int emit(int node) {
        const RegNode& n = nodes_[static_cast<std::size_t>(node)];
        int sdd = -1;
        if (!n.sdd.empty()) {
            const std::uint32_t nid = inter_.StringEvenEmpty(n.sdd);
            sdd = Blob(PackU32({nid, kNoId}), 1);
        }

        int vtbl = -1;
        if (!n.vals.empty()) {
            std::vector<std::uint64_t> hashes;
            hashes.reserve(n.vals.size());
            for (const RegVal& v : n.vals) hashes.push_back(Hash64(LowerAscii(v.name)));
            const std::vector<std::size_t> src = SitOrder(hashes);
            struct Rec {
                std::uint32_t f0;
                std::string name;
                int slot;
            };
            std::vector<Rec> recs;
            recs.reserve(src.size());
            for (std::size_t i : src) {
                const RegVal& v = n.vals[i];
                const std::uint32_t tid =
                    v.text.empty() ? kNoId : inter_.StringEvenEmpty(v.text);
                const int rec = Blob(PackU32({1u, 1u, v.type, tid, kNoId}), 1);
                recs.push_back(Rec{static_cast<std::uint32_t>(Hash64(LowerAscii(v.name)) & kM32),
                                   v.name, rec});
            }
            std::vector<std::uint32_t> f0s;
            f0s.reserve(recs.size());
            for (const Rec& r : recs) f0s.push_back(r.f0);
            const std::vector<std::size_t> order = SitOrder(f0s);
            std::unordered_map<std::size_t, std::uint32_t> ids;
            for (std::size_t i : order) ids[i] = inter_.StringEvenEmpty(recs[i].name);
            std::vector<Row> rows;
            rows.reserve(recs.size());
            for (std::size_t i = 0; i < recs.size(); ++i) {
                rows.push_back(Row{recs[i].f0, ids[i], static_cast<std::uint32_t>(recs[i].slot)});
            }
            vtbl = Table(std::move(rows));
            if (!ok_) return -1;
        }

        const int ktbl = emitChildren(node);
        if (!ok_) return -1;

        return Blob(PackU32({n.declared ? 2u : 0u, 0u, kNoId,
                             static_cast<std::uint32_t>(sdd),
                             static_cast<std::uint32_t>(vtbl),
                             static_cast<std::uint32_t>(ktbl)}),
                    1);
    }

    Interner& inter_;
    std::vector<Slot>& slots_;
    std::unordered_map<std::string, std::size_t> dedup_;
    std::vector<RegNode> nodes_;
    int root_ = -1;
    bool ok_ = true;
    std::string error_;
};

// ---------------------------------------------------------------------------
// cache 记录 / GUID
// ---------------------------------------------------------------------------
const char* const kCacheGuidMof = "9fc74e9f973a7247b6352899468900a2";
const char* const kCacheGuidLodctr = "eeed2ed84e859a4fb458ae1bc62a0e6b";
const char* const kCacheGuidBfsvc = "d7e9f0c544e8074589e4701b5a747221";
const char* const kCacheGuidRescache = "7ad7ecc41185184b89bfc6ebecad4e01";
const char* const kCacheGuidInstrumentation = "2bfdb93b1e359c4bb1fced0758805998";
// <instrumentation><counters> 用的是另一个 GUID（00777/00853/02759 三份 counters
// 清单的头部 GUID 表里是这串；<events> 用上面那个）。
const char* const kCacheGuidCounters = "218a49b105045949adaa7e78686acfbe";
const char* const kCacheGuidSppInstaller = "d25f261b1c72594ead559d102a5d1d7f";
const char* const kCacheGuidNgen = "276748d4364da545bc81b8935aed1429";
const char* const kCacheGuidRootDevices = "50d1073d3d2f84419793d0fd59b0c885";
const char* const kCacheGuidTimezone = "2a702873068d40498cd5e33ddaffd52b";
const char* const kCacheGuidServiceData = "ad04d55168484c46a504e6cc8a210a97";
const char* const kCacheGuidSecurityDescriptor = "60da5f28f4317c4194072f7655a965fc";
const char* const kCacheGuidTaskScheduler = "11596b389eb62d4da3ab796d2ec6af38";
// <firewallRule> 全族共用一个 cache 记录（uidx 列表）和一个 GUID；
// 该 GUID 从 02969/03001 的头部 GUID 表（offset 0x90）直接读出。
const char* const kCacheGuidFirewallRule = "698dce68ae269f4fbea750613e7ab5c1";
// 56 项 kinds 表 idx=21 的 GUID（上面 wcp_x86.dll 0x101F35C8 直接读出的第 21 项）；
// 02915 的头部出现过，元素 local 名未知（可能是 lodctrData 变体）。
const char* const kCacheGuidLodctrDat = "9bcb3d9a96a7df41b45577eba6a1ec1a";

bool IsCacheRecordElement(const std::string& local) {
    return local == "lodctr" || local == "bfsvc" || local == "rescache" ||
           local == "migration" || local == "sppInstaller" || local == "ngen" ||
           local == "rootDevices" || local == "timezoneresourceAI" ||
           local == "taskScheduler" || local == "serviceData" ||
           local == "securityDescriptor";
}

// 静态 "installer element kinds" 表的条目序号（wcp_x86.dll .data VA 0x101F35C8，
// 56 个 12 字节项）；数值来自 02532/02795/02709/00792/00487/00848/02959 的
// 发射序反推。migration 在 walk 之前发射，且不贡献 GUID。
int CacheRank(const std::string& local) {
    if (local == "migration") return -1;
    if (local == "serviceData") return 0;
    if (local == "securityDescriptor") return 1;
    if (local == "firewallRule") return 2;
    if (local == "ngen") return 3;
    if (local == "sppInstaller") return 6;
    if (local == "rootDevices") return 7;
    if (local == "taskScheduler") return 16;
    if (local == "instrumentation") return 17;
    if (local == "events") return 17;
    if (local == "mof") return 19;
    if (local == "lodctr") return 20;
    if (local == "rescache") return 23;
    if (local == "timezoneresourceAI") return 34;
    if (local == "bfsvc") return 44;
    return 999;
}

std::string CacheGuid(const std::string& local) {
    if (local == "mof") return kCacheGuidMof;
    if (local == "lodctr") return kCacheGuidLodctr;
    if (local == "lodctrDat") return kCacheGuidLodctrDat;
    if (local == "bfsvc") return kCacheGuidBfsvc;
    if (local == "rescache") return kCacheGuidRescache;
    if (local == "instrumentation" || local == "events") return kCacheGuidInstrumentation;
    if (local == "counters") return kCacheGuidCounters;
    if (local == "sppInstaller") return kCacheGuidSppInstaller;
    if (local == "ngen") return kCacheGuidNgen;
    if (local == "rootDevices") return kCacheGuidRootDevices;
    if (local == "timezoneresourceAI") return kCacheGuidTimezone;
    if (local == "serviceData") return kCacheGuidServiceData;
    if (local == "securityDescriptor") return kCacheGuidSecurityDescriptor;
    if (local == "taskScheduler") return kCacheGuidTaskScheduler;
    if (local == "firewallRule") return kCacheGuidFirewallRule;
    return std::string();
}

std::string GuidBytes(const std::string& hex) {
    std::string out;
    HexDecode(hex, out);
    return out;
}

// 头部 cache GUID 表的**条目顺序**：与发射序（CacheRank/56 项 kinds 表 idx）不同，
// 是另一张静态 kinds 表。证据（全语料 92 份多记录样本逐个两两约束，DAG 无环，
// 该线性扩展 92/92 命中）：bfsvc<instrumentation<lodctr<lodctrDat<
// securityDescriptor<serviceData<rootDevices<sppInstaller<mof<rescache<
// taskScheduler<timezoneresourceAI。典型反例：00487 真机 [bfsvc,rescache]
// （CacheRank 序相反）、02713 [instrumentation,taskScheduler]、02731
// [instrumentation,securityDescriptor,serviceData]（完全逆发射序）。
// 该顺序来自二进制里的静态表，清单 XML 里没有对应信息；未观测到的对
// （firewallRule/ngen 与其他 kind 的组合）按已知约束的线性扩展放在末尾。
const char* const kCacheGuidOrder[] = {
    "bfsvc", "instrumentation", "counters", "lodctr", "lodctrDat", "securityDescriptor",
    "serviceData", "rootDevices", "sppInstaller", "mof", "rescache",
    "taskScheduler", "timezoneresourceAI", "firewallRule", "ngen",
};

int CacheGuidRank(const std::string& guid) {
    for (int i = 0; i < static_cast<int>(sizeof(kCacheGuidOrder) / sizeof(*kCacheGuidOrder)); ++i) {
        if (GuidBytes(CacheGuid(kCacheGuidOrder[i])) == guid) return i;
    }
    return 1000;
}

// t8 行序：按 GUID 的 Data1（前 4 字节小端 u32）升序（全语料 92/92）。
std::uint32_t GuidData1(const std::string& guid) {
    if (guid.size() < 4) return 0;
    return static_cast<std::uint32_t>(static_cast<std::uint8_t>(guid[0])) |
           (static_cast<std::uint32_t>(static_cast<std::uint8_t>(guid[1])) << 8) |
           (static_cast<std::uint32_t>(static_cast<std::uint8_t>(guid[2])) << 16) |
           (static_cast<std::uint32_t>(static_cast<std::uint8_t>(guid[3])) << 24);
}

// ---------------------------------------------------------------------------
// 主构建
// ---------------------------------------------------------------------------
struct DirObj {
    std::size_t slot = 0;
    std::uint32_t elem = 0;
};

bool BuildCore(const std::string& xmlUtf8, std::uint8_t b0Extra,
               std::vector<std::uint8_t>& out, std::string& error) {
    std::vector<std::uint8_t> mdhd;
    if (!BuildMicrodomLayout(xmlUtf8, mdhd, error)) return false;

    MdDom dom;
    MdPatchInfo patch;
    if (!DecodeMicrodom(mdhd, dom, patch, error)) return false;
    DbgDumpDom(dom);
    if (patch.firstElementHasMin && b0Extra != 0) {
        // 真机写入器第一个元素记录的 b0 bit4..6 是未初始化栈字节；只在
        // min(属性数, 内容子数) != 0 时写（和 md_build 的 b0_extra 一致）。
        const std::size_t at = static_cast<std::size_t>(
            dom.nodes[patch.firstElementUidx].recordOffset);
        if (at < mdhd.size()) {
            mdhd[at] = static_cast<std::uint8_t>(
                (mdhd[at] & 0x8Fu) | ((b0Extra & 0x07u) << 4));
        }
    }

    std::vector<Elem> elems;
    std::uint32_t root = 0;
    if (!BuildElems(dom, elems, root, error)) return false;
    (void)root;

    Interner inter;
    std::vector<Slot> slots;

    // ---- 1. 文档扫描（元素顺序 = 文档序）----------------------------------
    std::vector<std::uint32_t> deps, files, cats, sdddefs, membElems, confEls, mofs;
    std::vector<std::pair<std::uint32_t, std::vector<std::uint32_t>>> reslist;
    std::unordered_map<std::uint32_t, std::size_t> resIx;
    std::unordered_map<std::uint32_t, std::size_t> strPath;
    std::unordered_map<std::string, std::string> asmAttrs;
    bool hasMvid = false, hasDeployment = false, hasNoInh = false, hasGac = false;
    bool hasTrust = false;
    std::string mvidVal;
    std::int32_t gacEl = -1;
    std::uint32_t depUidx = 0;

    if (std::getenv("OCID_PCM_DEBUG_UIDX") != nullptr) {
        for (std::size_t i = 0; i < elems.size(); ++i) {
            const Elem& e = elems[i];
            std::fprintf(stderr, "UIDX %u %s parent=%s\n", e.uidx, e.local.c_str(),
                         e.parent < elems.size() ? elems[e.parent].local.c_str() : "-");
        }
    }

    for (std::size_t i = 0; i < elems.size(); ++i) {
        const Elem& e = elems[i];
        const std::uint32_t idx = static_cast<std::uint32_t>(i);
        if (e.local == "assemblyIdentity" && e.parent == 0) {
            asmAttrs = AttrsOf(e);
        } else if (e.local == "dependency" && e.parent == 0) {
            deps.push_back(idx);
        } else if (e.local == "file" && e.parent == 0) {
            files.push_back(idx);
        } else if (e.local == "id" && e.parent != kNoId &&
                   elems[e.parent].local == "categoryMembership") {
            cats.push_back(idx);
        } else if (e.local == "securityDescriptorDefinition") {
            sdddefs.push_back(idx);
        } else if (e.local == "resources" && e.parent != kNoId &&
                   elems[e.parent].local == "localization") {
            resIx[idx] = reslist.size();
            reslist.emplace_back(idx, std::vector<std::uint32_t>{});
        } else if (e.local == "string" && e.parent != kNoId &&
                   elems[e.parent].local == "stringTable") {
            std::uint32_t p = e.parent;
            while (p != kNoId && elems[p].local != "resources") p = elems[p].parent;
            if (p != kNoId) {
                const auto it = resIx.find(p);
                if (it != resIx.end()) {
                    reslist[it->second].second.push_back(idx);
                    strPath[idx] = it->second;
                }
            }
        } else if (e.local == "mvid") {
            hasMvid = true;
            mvidVal = Attr(AttrsOf(e), "value");
        } else if (e.local == "deployment" && e.parent == 0) {
            hasDeployment = true;
            depUidx = e.uidx;
        } else if (e.local == "configuration" && e.parent == 0) {
            confEls.push_back(idx);
        } else if (e.local == "noInheritable" && e.parent == 0) {
            hasNoInh = true;
        } else if (e.local == "gac") {
            hasGac = true;
            gacEl = static_cast<std::int32_t>(i);
        }
        if (e.local == "trustInfo") hasTrust = true;
        if (e.local == "mof") mofs.push_back(idx);
    }

    // ---- 2. 身份表（创建序 = id 序）--------------------------------------
    std::vector<Ident> idents;
    const auto depIdentity = [&elems](std::uint32_t dep) -> std::int32_t {
        for (std::uint32_t k : elems[dep].kids) {
            if (elems[k].local == "assemblyIdentity") return static_cast<std::int32_t>(k);
        }
        for (std::uint32_t k : elems[dep].kids) {
            for (std::uint32_t kk : elems[k].kids) {
                if (elems[kk].local == "assemblyIdentity") return static_cast<std::int32_t>(kk);
            }
        }
        return -1;
    };
    for (std::uint32_t d : deps) {
        const std::int32_t de = depIdentity(d);
        const auto rows = IdentRows(de >= 0 ? AttrsOf(elems[de])
                                            : std::unordered_map<std::string, std::string>{},
                                    false);
        idents.push_back(Ident{5, rows});
    }
    idents.push_back(Ident{4, IdentRows(asmAttrs, true)});
    for (std::uint32_t c : cats) {
        idents.push_back(Ident{4, IdentRows(AttrsOf(elems[c]), true)});
    }

    // <categoryMembership> 身份的 id 跟着**发射序**走（00483：Multilingual 在
    // 文档里更靠前，但 Categories 表拿到 ident id 1）。53/61 个多 membership
    // 样本用"按 ident 哈希桶序重编号"复现；重编号后 key-2 的 f1 就是身份 id。
    const std::size_t baseCat = idents.size() - cats.size();
    std::vector<std::size_t> catRank(cats.size());
    for (std::size_t i = 0; i < catRank.size(); ++i) catRank[i] = i;
    if (cats.size() > 1) {
        std::vector<std::uint32_t> hashes;
        hashes.reserve(idents.size());
        for (const Ident& id : idents) {
            hashes.push_back(static_cast<std::uint32_t>(IdentHash(id.rows) & kM32));
        }
        const std::vector<std::size_t> order = SitOrder(hashes);
        std::vector<std::size_t> catOrd;
        for (std::size_t j : order) {
            if (j >= baseCat) catOrd.push_back(j);
        }
        bool changed = false;
        for (std::size_t k = 0; k < catOrd.size(); ++k) {
            if (catOrd[k] != baseCat + k) changed = true;
        }
        if (changed) {
            std::vector<Ident> reordered(idents.begin(), idents.begin() + baseCat);
            for (std::size_t k = 0; k < catOrd.size(); ++k) {
                reordered.push_back(idents[catOrd[k]]);
                catRank[catOrd[k] - baseCat] = k;
            }
            idents = std::move(reordered);
        }
    }

    std::vector<Row> fileRows;                  // key 1
    // 每个 <categoryMembership> 各有一条 8B 记录 {categoryInstance uidx, -1}，
    // 不是整个 <memberships> 共用一条（02531：槽 7/8/9 = -1/84/128）。
    std::unordered_map<std::uint32_t, std::size_t> membSlotByElem;
    std::vector<Row> dirRows;                   // key 17
    std::vector<DirObj> dirObjs;
    std::vector<Row> sddRows;                   // key 210
    std::vector<std::string> sddNames;
    std::unordered_map<std::uint32_t, std::size_t> locSlot;      // string elem → 4B slot
    std::unordered_map<std::int32_t, std::size_t> strValSlot;    // interned value id → 4B slot
    std::unordered_map<std::int32_t, std::size_t> sddSlot;       // interned sdd name id
    std::unordered_map<std::int32_t, std::size_t> dirSddSlot;
    std::uint32_t hseq = (hasTrust || gacEl >= 0) ? 1u : 0u;
    std::unordered_map<std::string, std::uint32_t> digestBlobSlot;  // 原始摘要字节 → 32B 槽号
    std::unordered_map<std::uint32_t, std::uint32_t> sigBlobSlot;  // (flags,DRMLevel) → blob id

    // ---- 3. 根 <dependency> 记录（文档序，永远排在最前）--------------------
    //
    // 依赖记录（48B + 16B）先于所有其它记录发出：00009/00018/02543 等 713 个
    // 带 <dependency> 的样本都是 slot 0/1、2/3… 成对开头。早期把它们放在
    // 元素循环里"顺便"发，遇到带 <gac> 的样本就错位（02543）。
    const std::size_t nDepTotal = deps.size();
    {
        std::uint32_t depOrd = 0;
        for (std::uint32_t d : deps) {
            const std::int32_t de = depIdentity(d);
            const std::string lang =
                de >= 0 ? Attr(AttrsOf(elems[static_cast<std::size_t>(de)]), "language")
                        : std::string();
            const bool wild = lang == "*";
            AppendSlot(slots, 1, BuildDepRecord(depOrd, wild));
            AppendSlot(slots, 1, BuildDepChild(depOrd, lang));
            ++depOrd;
            hseq += 2;
        }
    }

    // ---- 3b. <gac> 引导记录（36B）-----------------------------------------
    //
    // 它是**惰性**发射的：位置在"第一条引用它的 <file> 之前"，而不是固定 slot 0。
    // 41 个带 <gac> 的样本里 36 个第一条 <file> 就带 <gac>（slot 0），
    // 4 个恰好有 1 条根 <dependency> = 2 个槽（slot 2）；01742 的第一条
    // <file> 没有 <gac>，第二条才有 → 它排在第一条文件记录之后（slot 5），
    // 且 clrversion/managedculture 的 id 也顺延到 6/7。
    // dw0 只取决于两个可选属性：
    //   clrversion + managedculture → 22，managedculture → 6，clrversion → 31。
    // 它作为 blob 与槽同时入池（id == 槽号），文件记录 [17] 直接引用该槽号。
    std::int32_t gacBlob = -1;
    const auto emitGacBlob = [&]() -> bool {
        if (gacEl < 0) return false;
        const auto ga = AttrsOf(elems[static_cast<std::size_t>(gacEl)]);
        std::string mv = Attr(ga, "managedversion");
        if (mv.empty()) mv = "0.0.0.0";
        std::uint32_t mvi = 0;
        {
            std::vector<long long> parts;
            std::size_t start = 0;
            bool ok = true;
            while (start <= mv.size()) {
                const std::size_t p = mv.find('.', start);
                const std::string part =
                    mv.substr(start, p == std::string::npos ? std::string::npos : p - start);
                char* end = nullptr;
                const long long v = std::strtoll(part.c_str(), &end, 10);
                if (end == part.c_str() || *end != '\0') { ok = false; break; }
                parts.push_back(v);
                if (p == std::string::npos) break;
                start = p + 1;
            }
            if (ok && !parts.empty()) {
                mvi = (static_cast<std::uint32_t>(parts[0]) << 16) |
                      (parts.size() > 1 ? static_cast<std::uint32_t>(parts[1]) : 0u);
            }
        }
        const bool hc = !Attr(ga, "clrversion").empty();
        const bool hm = !Attr(ga, "managedculture").empty();
        const std::uint32_t idClr = hc ? inter.String(Attr(ga, "clrversion")) : -1;
        const std::uint32_t idCult = hm ? inter.String(Attr(ga, "managedculture")) : -1;
        std::string blob;
        if (hc && hm) {
            blob = PackU32({22u, mvi, 0u, idClr, kNoId, 0u, 0u, idCult, kNoId});
        } else if (hm) {
            blob = PackU32({6u, mvi, 0u, kNoId, kNoId, 0u, 0u, idCult, kNoId});
        } else if (hc) {
            blob = PackU32({31u, mvi, 0u, idClr, 1u, mvi, 0u, kNoId, kNoId});
        } else {
            blob = PackU32({0u, mvi, 0u, 0u, kNoId, 0u, 0u, 1u, kNoId});
        }
        AppendSlot(slots, 1, blob);
        gacBlob = static_cast<std::int32_t>(slots.size() - 1u);
        return true;
    };


    // ---- 4. 元素记录（文档序）--------------------------------------------
    std::uint32_t fileOrd = 0;
    for (std::size_t i = 0; i < elems.size(); ++i) {
        const Elem& e = elems[i];
        const std::uint32_t idx = static_cast<std::uint32_t>(i);
        if (e.local == "dependency" && e.parent == 0) {
            continue;  // 已在第 3 节统一发出
        } else if (e.local == "file" && e.parent == 0) {
            std::string sddName;
            std::string digest;
            bool haveDigest = false;
            const Elem* hashEl = nullptr;
            bool hGac = false;
            // <signatureInfo><signatureDescriptor .../>：真机在 16B hash 记录之后、
            // 该 hash 的 type-9 辅助表之前多发一条 8B 记录 [flags, level]。
            // flags 是属性位或：DRMLevel 存在=1、pageHash=2、PETrust=4；
            // level 是该 descriptor 的 DRMLevel 数值（无则 0）。
            // 4 个 pageHash/PETrust 样本：00486/02530/02532/02787。
            // DRMLevel 样本（9 个驱动 INF，DRMLevel=1300 ⇒ {1,1300}）：
            // 00153/00775/01543/01914/02039/02164/02397/02411/02027。
            // 相同 flags+level 只发一次（02532 两个 file 的 descriptor 都是 pageHash → 仅 1 条）。
            std::uint32_t sigFlags = 0;
            std::uint32_t sigLevel = 0;
            bool haveSig = false;
            std::int32_t sigBlob = -1;
            for (std::uint32_t k : e.kids) {
                if (elems[k].local == "gac") hGac = true;
                else if (elems[k].local == "securityDescriptor") {
                    sddName = Attr(AttrsOf(elems[k]), "name");
                } else if (elems[k].local == "hash") {
                    hashEl = &elems[k];
                    for (std::uint32_t kk : elems[k].kids) {
                        if (elems[kk].local == "DigestValue") {
                            digest = Strip(elems[kk].text);
                            haveDigest = !digest.empty();
                        }
                    }
                } else if (elems[k].local == "signatureInfo") {
                    for (std::uint32_t kk : elems[k].kids) {
                        if (elems[kk].local != "signatureDescriptor") continue;
                        const auto sa = AttrsOf(elems[kk]);
                        haveSig = true;
                        if (Attr(sa, "pageHash") == "true") sigFlags |= 2u;
                        if (Attr(sa, "PETrust") == "true") sigFlags |= 4u;
                        const std::string lvl = Attr(sa, "DRMLevel");
                        if (!lvl.empty()) {
                            sigFlags |= 1u;
                            sigLevel = static_cast<std::uint32_t>(std::strtoul(lvl.c_str(), nullptr, 10));
                        }
                    }
                }
            }

            // <gac>/<ngen> 子元素在"文档序"位置发射：字符串池的 id 也按这个
            // 顺序分配 —— 01742 第二条 file 的 gac 得到 clrversion/managedculture
            // 的 id 6/7（第一条 file 的属性串 0..5 之后）；02543 的 gac 在文件
            // 子元素最前，于是 clrversion=0，紧随其后的 ngen parameter=1，文件
            // 名才拿到 2。故这里按 e.kids 顺序交错处理，而不是在记录末尾补。
            for (std::uint32_t k : e.kids) {
                if (elems[k].local == "gac") {
                    if (gacBlob < 0) {
                        if (!emitGacBlob()) return false;
                    }
                } else if (elems[k].local == "ngen") {
                    const std::string prm = Attr(AttrsOf(elems[k]), "parameter");
                    if (!prm.empty()) inter.String(prm);
                }
            }

            // [19]：真机先按 sdd 名各入池一个 8B blob（影响后续 blob id），
            // [19] 就是这个 8B 记录的 blob 号（=槽号）。只有一个 sdd 名时它
            // 恰好等于 2*依赖数；02963/02983 有两个 sdd 名（#DefaultAdminSd 与
            // WRP_FILE_DEFAULT_SDDL），第二个 file 的 [19] 就是槽 5，不再是 0。
            std::int32_t sddBlob = -1;
            if (!sddName.empty()) {
                const std::int32_t nid = inter.String(sddName);
                if (sddSlot.find(nid) == sddSlot.end()) {
                    sddSlot[nid] = slots.size();
                    AppendSlot(slots, 1, PackU32({static_cast<std::uint32_t>(nid), kNoId}));
                }
                sddBlob = static_cast<std::int32_t>(sddSlot[nid]);
            }

            std::int32_t hashAttrBlob = -1;
            const std::string hAttr = Attr(AttrsOf(e), "hash");
            if (!hAttr.empty()) {
                std::string raw;
                HexDecode(hAttr, raw);
                if (!raw.empty()) {
                    // [6] �� `<file hash="...">` ԭʼֽڵ� blob �ţ��ڱ��������� blob ��
                    // ���ںţ�����ֱ��ȡ�۸�01001 �� [6]=1 ���Ǹ� 20B ���ڵ��±ꡣ
                    AppendSlot(slots, 1, raw);
                    hashAttrBlob = static_cast<std::int32_t>(slots.size() - 1u);
                    ++hseq;
                }
            }

            std::int32_t hashBlob = -1;
            std::string rawDigest;
            if (haveDigest) {
                Base64Decode(digest, rawDigest);
                if (!rawDigest.empty()) {
                    const auto it = digestBlobSlot.find(rawDigest);
                    if (it != digestBlobSlot.end()) {
                        hashBlob = static_cast<std::int32_t>(it->second);
                    } else {
                        AppendSlot(slots, 1, rawDigest);
                        hashBlob = static_cast<std::int32_t>(slots.size() - 1u);
                        digestBlobSlot.emplace(rawDigest,
                                               static_cast<std::uint32_t>(hashBlob));
                    }
                }
            }

            // 16B hash 记录 + 无 key 的 type-9 辅助表。
            if (hashEl != nullptr) {
                bool hasDigestMethod = false;
                for (std::uint32_t k : hashEl->kids) {
                    if (elems[k].local == "DigestMethod") hasDigestMethod = true;
                }
                const std::uint32_t dw0 =
                    0x00010000u | (hasDigestMethod ? 0x02000000u : 0x01000000u);
                // 16B 记录的 dword2 是 32B 摘要 blob 的 blob 号（=槽号）：
                // 02963 三条 hnum 1/6/10 正对摘要槽 1/6/10，01849 是 1/1/8。
                // 同一摘要重复出现时指向同一槽（01849 两个 file → hnum 都是 1）。
                // 没有 <DigestValue> 时退回老的 hseq 经验值。
                std::uint32_t hnum = 0;
                if (hashBlob >= 0) {
                    hnum = static_cast<std::uint32_t>(hashBlob);
                } else {
                    hnum = hseq;
                }
                // hseq 按 <hash> 子树的"元素后代数"推进（不含 hash 自身）：
                // 常见 Transforms/Transform/DigestMethod/DigestValue 共 4 个，
                // 这是无摘要时 dword2 的经验来源（2017 样本 1992 命中）。
                {
                    std::function<std::uint32_t(std::uint32_t)> ndesc =
                        [&](std::uint32_t node) -> std::uint32_t {
                        std::uint32_t n = 0;
                        for (std::uint32_t kk : elems[node].kids) n += 1 + ndesc(kk);
                        return n;
                    };
                    hseq += ndesc(static_cast<std::uint32_t>(hashEl - elems.data()));
                }
                const std::uint32_t h16 = static_cast<std::uint32_t>(slots.size());
                AppendSlot(slots, 1, PackU32({dw0, kNoId, hnum, hashEl->uidx}));
                if (haveSig) {
                    const std::uint32_t sigKey = (sigFlags << 16) | (sigLevel & 0xFFFFu);
                    const auto sit = sigBlobSlot.find(sigKey);
                    if (sit != sigBlobSlot.end()) {
                        sigBlob = static_cast<std::int32_t>(sit->second);
                    } else {
                        sigBlob = static_cast<std::int32_t>(slots.size());
                        AppendSlot(slots, 1, PackU32({sigFlags, sigLevel}));
                        sigBlobSlot.emplace(sigKey, static_cast<std::uint32_t>(sigBlob));
                        ++hseq;  // 8B signature 记录同样占一个 hash 序列号
                    }
                }
                std::vector<Row> hashRows;
                hashRows.push_back(Row{0u, 0u, h16});
                std::string hashPayload;
                if (!MakeRecordTable(9, hashRows, false, hashPayload, error)) return false;
                AppendSlot(slots, 9, hashPayload);
            }

            // <gac> 子元素：blob 已在本次迭代开头惰性发出。
            std::int32_t gblob = -1;
            if (hGac && gacEl >= 0) {
                gblob = gacBlob;
            }
            const std::int32_t nameId = inter.String(Attr(AttrsOf(e), "name"));
            fileRows.push_back(Row{
                static_cast<std::uint32_t>(Hash64(LowerAscii(Attr(AttrsOf(e), "name"))) & kM32),
                static_cast<std::uint32_t>(nameId), static_cast<std::uint32_t>(slots.size())});
            // [14] targets = 本条记录自己的 blob 号减一 = 追加前的槽数减一。
            const std::uint32_t targets =
                slots.empty() ? 0u : static_cast<std::uint32_t>(slots.size() - 1u);
            // [13] writeableType：语料只有缺省(1) 与 `systemData`(3) 两种；
            // [20] FILE_ATTRIBUTE：缺省 NORMAL|ARCHIVE = 0xA0，`hidden system` = 6。
            const std::uint32_t writeType =
                Attr(AttrsOf(e), "writeableType") == "systemData" ? 3u : 1u;
            const std::uint32_t attrFlags =
                AttrFlagsFromText(Attr(AttrsOf(e), "attributes"), 0x0A0u);
            AppendSlot(slots, 1,
                       BuildFileRecord(inter, e, sddBlob, targets, hashAttrBlob, gblob, sddName,
                                       writeType, attrFlags, sigBlob));
            ++fileOrd;
        } else if (e.local == "securityDescriptorDefinition") {
            const auto at = AttrsOf(e);
            const std::string name = Attr(at, "name");
            const std::string dn = Attr(at, "displayName");
            const std::string sddl = Attr(at, "sddl");
            const std::int32_t nid = inter.String(name);
            const std::uint32_t did = dn.empty() ? kNoId : inter.String(dn);
            const std::uint32_t sid = sddl.empty() ? kNoId : inter.String(sddl);
            sddRows.push_back(Row{static_cast<std::uint32_t>(Hash64(name) & kM32),
                                  static_cast<std::uint32_t>(nid),
                                  static_cast<std::uint32_t>(slots.size())});
            sddNames.push_back(name);
            AppendSlot(slots, 1,
                       PackU32({1u, static_cast<std::uint32_t>(nid), did, sid}));
        } else if (e.local == "memberships" && e.parent == 0) {
            // 每个 membership 一条 8B {categoryInstance uidx 或 -1, -1}；
            // 载荷完全相同的记录只发一条（01091 两个无 instance → 1 条；
            // 02531 三条互异 → 3 条；02772 只有带 instance 的那条）。
            std::map<std::string, std::size_t> membSeen;
            for (std::uint32_t k : e.kids) {
                if (elems[k].local != "categoryMembership") continue;
                std::uint32_t ci = kNoId;
                for (std::uint32_t kk : elems[k].kids) {
                    if (elems[kk].local == "categoryInstance") {
                        ci = elems[kk].uidx;
                        break;
                    }
                }
                const std::string key8 =
                    PackU32({ci, kNoId});
                auto it = membSeen.find(key8);
                if (it != membSeen.end()) {
                    membSlotByElem[k] = it->second;
                    continue;
                }
                const std::size_t sl = slots.size();
                AppendSlot(slots, 1, key8);
                membSeen[key8] = sl;
                membSlotByElem[k] = sl;
            }
        } else if (e.local == "categoryMembership") {
            membElems.push_back(idx);
            if (membSlotByElem.find(idx) == membSlotByElem.end()) {
                // 不在根 <memberships> 下的成员（正常语料不会走到）
                std::uint32_t ci = kNoId;
                for (std::uint32_t kk : e.kids) {
                    if (elems[kk].local == "categoryInstance") {
                        ci = elems[kk].uidx;
                        break;
                    }
                }
                membSlotByElem[idx] = slots.size();
                AppendSlot(slots, 1, PackU32({ci, kNoId}));
            }
        } else if (e.local == "directory") {
            const auto dat = AttrsOf(e);
            std::string dsdd;
            for (std::uint32_t k : e.kids) {
                if (elems[k].local == "securityDescriptor") {
                    dsdd = Attr(AttrsOf(elems[k]), "name");
                }
            }
            bool haveSdd = false;
            std::int32_t nid2 = -1;
            if (!dsdd.empty()) {
                haveSdd = true;
                nid2 = inter.String(dsdd);
                // 每个**不同的** sdd 名只发一对 (8B, 20B)；01104 有 37 个
                // <directory> 但只有两个不同名字，语料里正好两对。
                if (dirSddSlot.find(nid2) == dirSddSlot.end()) {
                    dirSddSlot[nid2] = slots.size();
                    AppendSlot(slots, 1, PackU32({static_cast<std::uint32_t>(nid2), kNoId}));
                }
                if (dirSddSlot[nid2] != slots.size() - 1) {
                    dirObjs.push_back(DirObj{dirSddSlot[nid2] + 1, idx});
                    continue;
                }
            }
            const std::size_t dslot = slots.size();
            const std::uint32_t ref =
                haveSdd ? static_cast<std::uint32_t>(dirSddSlot[nid2])
                        : static_cast<std::uint32_t>(dslot - 1);
            AppendSlot(slots, 1,
                       PackU32({(Attr(dat, "owner") == "true" || Attr(dat, "owner") == "yes" ||
                                 Attr(dat, "owner") == "1")
                                    ? 1u
                                    : 0u,
                                0u, kNoId, ref,
                                AttrFlagsFromText(Attr(dat, "attributes"), 0x080u)}));
            dirObjs.push_back(DirObj{dslot, idx});
        } else if (e.local == "string" && strPath.find(idx) != strPath.end()) {
            const std::string val = Attr(AttrsOf(e), "value");
            const std::int32_t nidv = inter.String(val);
            // 每个 interned 值只发一条 4B 记录（同值 <string> 共享）。
            if (strValSlot.find(nidv) == strValSlot.end()) {
                strValSlot[nidv] = slots.size();
                AppendSlot(slots, 1, PackU32({static_cast<std::uint32_t>(nidv)}));
            }
            locSlot[idx] = strValSlot[nidv];
        }
    }

    // 兜底：<gac> 存在但没有任何 <file> 引用它（语料 41/41 都有引用）。
    if (gacEl >= 0 && gacBlob < 0) {
        if (!emitGacBlob()) return false;
    }

    // ---- 5. 文档常量 / micro-DOM / 文档记录 ------------------------------
    // <deployment> 发 20B 常量 blob，每个 <configuration> 发 [0, uidx]；两者
    // 都是 handler 期发射，按文档位置排序（没有同时出现的样本）。
    std::vector<std::pair<std::uint32_t, int>> region;
    if (hasDeployment) region.emplace_back(depUidx, 0);
    for (std::uint32_t c : confEls) region.emplace_back(elems[c].uidx, 1);
    std::sort(region.begin(), region.end());
    std::vector<std::pair<std::uint32_t, std::size_t>> confSlots;
    std::size_t depBlobSlot = kNoId;
    for (const auto& item : region) {
        if (item.second == 0) {
            depBlobSlot = slots.size();
            AppendSlot(slots, 1, BuildHash20());
        } else {
            confSlots.emplace_back(item.first, slots.size());
            AppendSlot(slots, 1, PackU32({0u, item.first}));
        }
    }
    if (!mvidVal.empty()) {
        std::string raw;
        HexDecode(mvidVal, raw);
        if (!raw.empty()) {
            AppendSlot(slots, 1, raw);
            hasMvid = true;
        }
    }
    // <bfsvc> 的 8B 记录 {Flags, InternString(Source)}：真机在**元素记录末尾、
    // MdHd 之前**发射（16299 wcp 的 CCdfManifestBuilder::AddBfsvc 反编译）。
    // key-33 表（CUlong 两列）收集 `(uidx, 记录槽号)`：9600 的 00044/00024 实测
    // 真机 (96,7)/(129,11) 与上面的 uidx / 槽号逐一对应。
    std::vector<Row> bfsvcRows;
    {
        const char* dbg = std::getenv("OCID_PCM_DEBUG_BFSVC");
        for (std::size_t i = 0; i < elems.size(); ++i) {
            if (elems[i].local != "bfsvc") continue;
            const std::string flags = Attr(AttrsOf(elems[i]), "Flags");
            const std::string src = Attr(AttrsOf(elems[i]), "Source");
            const std::int32_t sid = inter.String(src);
            if (dbg != nullptr) {
                std::fprintf(stderr, "bfsvc flags=%s uidx=%u sid=%d nodes=%zu\n",
                             flags.c_str(), elems[i].uidx, sid, elems.size());
            }
            // 真机在同一个位置发这个 8B 记录（16299 wcp 的
            // CCdfManifestBuilder::AddBfsvc：{Flags, InternString(Source)}）。
            bfsvcRows.push_back(Row{0u, elems[i].uidx,
                                    static_cast<std::uint32_t>(slots.size())});
            AppendSlot(slots, 1,
                       PackU32({static_cast<std::uint32_t>(std::strtoul(flags.c_str(), nullptr, 10)),
                                static_cast<std::uint32_t>(sid)}));
            break;
        }
    }
    const std::size_t mdhdSlot = slots.size();
    AppendSlot(slots, 1, std::string(mdhd.begin(), mdhd.end()));

    if (std::getenv("OCID_PCM_DEBUG_POOL") != nullptr) {
        const std::vector<std::string>& pool = inter.order();
        for (std::size_t i = 0; i < pool.size(); ++i) {
            std::fprintf(stderr, "POOL %u %s\n", static_cast<unsigned>(i), pool[i].c_str());
        }
    }

    // ---- 6. cache 记录（静态 kinds 表顺序，不是文档序）-------------------
    std::vector<std::uint32_t> cacheSlots;
    std::vector<std::string> recGuids;   // 与 cacheSlots 平行：该记录对应的 GUID（migration 为空）
    std::vector<std::string> cacheGuids; // 头部 GUID 表（去重后按静态 kinds 表序）
    std::vector<std::size_t> celist;
    for (std::size_t i = 0; i < elems.size(); ++i) {
        const std::string& ln = elems[i].local;
        bool isCache = false;
        if (ln == "mof" || ln == "instrumentation" || ln == "events" ||
            IsCacheRecordElement(ln)) {
            isCache = true;
        }
        if (ln == "securityDescriptor") {
            isCache = elems[i].parent != kNoId &&
                      elems[elems[i].parent].local == "serviceData";
        }
        // events 的 cache 记录由 <instrumentation> 分支代表（用其 events 子节点的
        // uidx）；events 只要祖先里有 <instrumentation> 就不再单独发射
        // （02970 等 *-events 清单的 <provider><events> 嵌套层比真机多 1 个槽）。
        if (ln == "events") {
            for (std::uint32_t p = elems[i].parent; p != kNoId; p = elems[p].parent) {
                if (elems[p].local == "instrumentation") { isCache = false; break; }
            }
        }
        if (ln == "firewallRule") isCache = true;
        if (isCache) celist.push_back(i);
    }
    std::sort(celist.begin(), celist.end(), [&elems](std::size_t a, std::size_t b) {
        const int ra = CacheRank(elems[a].local), rb = CacheRank(elems[b].local);
        if (ra != rb) return ra < rb;
        return a < b;
    });
    std::size_t migrSlot = kNoId;
    bool mofSeen = false;
    bool fwSeen = false;
    for (std::size_t i : celist) {
        const Elem& e = elems[i];
        const std::string& ln = e.local;
        if (ln == "mof") {
            if (mofSeen) continue;
            mofSeen = true;
            cacheSlots.push_back(static_cast<std::uint32_t>(slots.size()));
            std::string record;
            for (std::uint32_t m : mofs) record += PackU32({elems[m].uidx});
            AppendSlot(slots, 1, record);
            recGuids.push_back(GuidBytes(kCacheGuidMof));
        } else if (ln == "instrumentation") {
            // 记录里写的是 `<instrumentation>` 每个 registers 子节点的 uidx，
            // 一个子节点一条记录：<events> 用 instrumentation GUID、<counters>
            // 用 counters GUID（00777/00853/02759 只有 counters；02758 同时有
            // events 与 counters，真机发两条 —— 写父元素自己的 uidx 会小 2，
            // 因为中间隔着一个空白 PcData 节点）。
            std::vector<std::uint32_t> kids;
            for (std::uint32_t k : e.kids) {
                const std::string& kl = elems[k].local;
                if (kl == "events" || kl == "counters" || kl == "settings") kids.push_back(k);
            }
            if (kids.empty()) {
                cacheSlots.push_back(static_cast<std::uint32_t>(slots.size()));
                AppendSlot(slots, 1, PackU32({e.uidx}));
                recGuids.push_back(GuidBytes(kCacheGuidInstrumentation));
            }
            for (std::uint32_t k : kids) {
                cacheSlots.push_back(static_cast<std::uint32_t>(slots.size()));
                AppendSlot(slots, 1, PackU32({elems[k].uidx}));
                recGuids.push_back(GuidBytes(elems[k].local == "counters"
                                                 ? kCacheGuidCounters
                                                 : kCacheGuidInstrumentation));
            }
        } else if (ln == "events") {
            cacheSlots.push_back(static_cast<std::uint32_t>(slots.size()));
            AppendSlot(slots, 1, PackU32({e.uidx}));
            recGuids.push_back(GuidBytes(kCacheGuidInstrumentation));
        } else if (ln == "firewallRule") {
            // <firewallRule> 全族合成**一个** uidx 列表槽（02969：7 条规则 → 28B；
            // 03001：2 条 → 8B；02972：14 条 → 56B），且不贡献 GUID
            // （真机 t4 ident 表与本实现一致，只是 key-666 多了这一行）。
            if (fwSeen) continue;
            fwSeen = true;
            cacheSlots.push_back(static_cast<std::uint32_t>(slots.size()));
            std::string record;
            for (const Elem& w : elems) {
                if (w.local == "firewallRule") record += PackU32({w.uidx});
            }
            AppendSlot(slots, 1, record);
            recGuids.push_back(GuidBytes(kCacheGuidFirewallRule));
        } else {
            const std::size_t slot = slots.size();
            cacheSlots.push_back(static_cast<std::uint32_t>(slot));
            // <bfsvc>：真机在发 cache 记录时把 Source 值做一次 string interning
            // （9600 的 02895：Source="$(runtime.windows)\boot" 进 type-2 串表；
            // Flags="17" 不入池）。缺这一条会让后续串 id 整体后移 1。
            if (ln == "bfsvc") {
                const std::string src = Attr(AttrsOf(e), "Source");
                if (!src.empty()) inter.String(src);
            }
            AppendSlot(slots, 1, PackU32({e.uidx}));
            if (ln != "migration") {
                recGuids.push_back(GuidBytes(CacheGuid(ln)));
            } else {
                migrSlot = slot;
                recGuids.push_back(std::string()); // 占位，保持与 cacheSlots 对齐
            }
        }
    }
    // 头部 GUID 表：去重后按静态 kinds 表顺序（不是发射序，也不是文档序）。
    {
        for (const std::string& g : recGuids) {
            if (g.empty()) continue;
            bool seen = false;
            for (const std::string& h : cacheGuids) {
                if (h == g) { seen = true; break; }
            }
            if (!seen) cacheGuids.push_back(g);
        }
        std::stable_sort(cacheGuids.begin(), cacheGuids.end(),
                         [](const std::string& a, const std::string& b) {
                             return CacheGuidRank(a) < CacheGuidRank(b);
                         });
    }
    // t8 行: {guid 表下标, 记录槽}；migration 不参与。
    struct CacheRow {
        std::uint32_t guidIdx = 0;
        std::uint32_t slot = 0;
    };
    std::vector<CacheRow> cacheRows;
    for (std::size_t i = 0; i < cacheSlots.size(); ++i) {
        if (cacheSlots[i] == migrSlot) continue;
        int idx = -1;
        for (std::size_t g = 0; g < cacheGuids.size(); ++g) {
            if (cacheGuids[g] == recGuids[i]) { idx = static_cast<int>(g); break; }
        }
        if (idx < 0) continue; // 无 GUID 的记录不进 key-666
        cacheRows.push_back(CacheRow{static_cast<std::uint32_t>(idx), cacheSlots[i]});
    }

    // ---- 7. 68B 文档记录 --------------------------------------------------
    const std::size_t docSlot = slots.size();
    {
        bool wild = false;
        for (std::uint32_t d : deps) {
            const std::int32_t de = depIdentity(d);
            if (de >= 0 && Attr(AttrsOf(elems[static_cast<std::size_t>(de)]), "language") == "*") {
                wild = true;
                break;
            }
        }
        bool hasHashFile = false, hasSourcePath = false;
        for (std::uint32_t f : files) {
            const auto at = AttrsOf(elems[f]);
            if (!Attr(at, "hash").empty()) hasHashFile = true;
            if (!Attr(at, "sourcePath").empty()) hasSourcePath = true;
        }
        std::int32_t resDepIdx = -1;
        for (std::size_t d = 0; d < deps.size(); ++d) {
            if (!Attr(AttrsOf(elems[deps[d]]), "resourceType").empty()) {
                resDepIdx = static_cast<std::int32_t>(d);
            }
        }
        AppendSlot(slots, 1,
                   BuildDocRecord(nDepTotal,
                                  static_cast<std::uint32_t>(depBlobSlot), hasMvid,
                                  hasDeployment, hasNoInh, hasGac, resDepIdx, wild,
                                  hasHashFile, hasSourcePath,
                                  Attr(asmAttrs, "language").empty()));
    }

    // ---- 8. GeneratePCM 表槽（PCM_KEYS 固定调用序）-----------------------
    static const int kKeys[] = {12, 1, 4, 5, 6, 7, 8, 2, 9, 10, 11, 13, 16, 17, 19,
                                666, 201, 210, 34, 20, 21, 22, 23, 24, 25, 26, 32,
                                31, 30, 27, 28, 33, 29, 35, 37, 37, 38};
    std::map<int, std::size_t> reg;
    // key 120 是 <migration> cache 记录本身（没有表）：注册表里直接指向它的槽。
    if (migrSlot != kNoId) reg[120] = migrSlot;
    for (int key : kKeys) {
        if (reg.find(key) != reg.end()) continue;
        if (key == 12) {
            reg[12] = docSlot;
            continue;
        }
        std::vector<Slot> tbls;
        if (key == 1 && !fileRows.empty()) {
            std::vector<Row> rows = fileRows;
            SortAsc(rows);
            DbgDumpTable("k1", rows, inter);
            std::string payload;
            if (!MakeRecordTable(6, rows, true, payload, error)) return false;
            tbls.push_back(Slot{6, payload});
        } else if (key == 210 && !sddRows.empty()) {
            std::vector<std::uint32_t> keys;
            keys.reserve(sddRows.size());
            for (const std::string& name : sddNames) {
                keys.push_back(static_cast<std::uint32_t>(Hash64(LowerAscii(name)) & kM32));
            }
            std::vector<Row> rows = sddRows;
            SortByKeys(rows, keys);
            DbgDumpTable("k210", rows, inter);
            std::string payload;
            if (!MakeRecordTable(6, rows, true, payload, error)) return false;
            tbls.push_back(Slot{6, payload});
        } else if (key == 2 && !membElems.empty() && !membSlotByElem.empty()) {
            const std::size_t base = idents.size() - cats.size();
            std::vector<std::uint32_t> hashes;
            hashes.reserve(idents.size());
            for (const Ident& id : idents) {
                hashes.push_back(static_cast<std::uint32_t>(IdentHash(id.rows) & kM32));
            }
            const std::vector<std::size_t> order = SitOrder(hashes);
            std::vector<std::size_t> catOrd;
            for (std::size_t j : order) {
                if (j >= base) catOrd.push_back(j);
            }
            std::unordered_map<std::size_t, std::size_t> rank;
            for (std::size_t pos = 0; pos < catOrd.size(); ++pos) rank[catOrd[pos]] = pos;
            const std::uint32_t nDep = static_cast<std::uint32_t>(base - 1);
            std::vector<std::size_t> seq(membElems.size());
            for (std::size_t i = 0; i < seq.size(); ++i) seq[i] = i;
            std::stable_sort(seq.begin(), seq.end(), [&catRank](std::size_t a, std::size_t b) {
                return catRank[a] < catRank[b];
            });
            std::vector<std::pair<std::uint32_t, std::size_t>> f1(membElems.size());
            std::vector<std::uint32_t> rowKey(membElems.size(), 0);
            for (std::size_t i : seq) {
                std::vector<Row> l9;
                l9.push_back(Row{0u, static_cast<std::uint32_t>(i),
                                 static_cast<std::uint32_t>(membSlotByElem[membElems[i]])});
                std::string l9payload;
                if (!MakeRecordTable(9, l9, false, l9payload, error)) return false;
                const std::uint32_t t9s = static_cast<std::uint32_t>(slots.size());
                AppendSlot(slots, 9, l9payload);
                const std::uint32_t t6s = static_cast<std::uint32_t>(slots.size());
                std::string sub;
                for (std::uint32_t kk : elems[membElems[i]].kids) {
                    if (elems[kk].local == "categoryInstance") {
                        sub = Attr(AttrsOf(elems[kk]), "subcategory");
                        break;
                    }
                }
                {
                    // t6 的 f1 是该 membership 的 <categoryInstance subcategory>
                    // 字符串 id；没有 categoryInstance / 没有 subcategory 时为
                    // 空串 id（02531: 15/16/17 = ''/AppMgmt/netsvcs）。
                    std::vector<Row> rows;
                    rows.push_back(Row{static_cast<std::uint32_t>(Hash64("") & kM32),
                                       inter.StringEvenEmpty(sub), t9s});
                    std::string payload;
                    if (!MakeRecordTable(6, rows, true, payload, error)) return false;
                    AppendSlot(slots, 6, payload);
                }
                f1[i] = {nDep + static_cast<std::uint32_t>(catRank[i]) + 1u, t6s};
                // 真机 t10 的行序 = 成员身份哈希（IdentHash 低 32 位）**升序**：
                // 与 catRank 同一个哈希，但按值排、不是 SitOrder 的桶序。
                // 9600 语料里 642/642 张多行表吻合（subHash / key1 都不对）。
                rowKey[i] = i < cats.size()
                                ? (IdentHash(IdentRows(AttrsOf(elems[cats[i]]), true)) & kM32)
                                : (Hash64(std::to_string(f1[i].first)) & kM32);
            }
            std::vector<std::size_t> rowOrd(membElems.size());
            for (std::size_t i = 0; i < rowOrd.size(); ++i) rowOrd[i] = i;
            std::stable_sort(rowOrd.begin(), rowOrd.end(),
                             [&rowKey](std::size_t a, std::size_t b) {
                                 return rowKey[a] < rowKey[b];
                             });
            std::vector<Row> rows;
            rows.reserve(membElems.size());
            for (std::size_t i : rowOrd) {
                rows.push_back(Row{static_cast<std::uint32_t>(
                                       Hash64(std::to_string(f1[i].first)) & kM32),
                                   f1[i].first, static_cast<std::uint32_t>(f1[i].second)});
            }
            // 临时诊断：OCID_PCM_DEBUG_T10=1 时按最终行序把 (f1, name, subcategory)
            // 打到 stderr，用来反推真机 f0 的取值（见 CBS-Writer.md §7）。
            if (std::getenv("OCID_PCM_DEBUG_T10") != nullptr) {
                for (std::size_t i : rowOrd) {
                    std::string nm;
                    std::string sub2;
                    for (std::uint32_t kk : elems[membElems[i]].kids) {
                        if (elems[kk].local == "id") {
                            nm = Attr(AttrsOf(elems[kk]), "name");
                        } else if (elems[kk].local == "categoryInstance") {
                            sub2 = Attr(AttrsOf(elems[kk]), "subcategory");
                        }
                    }
                    std::fprintf(stderr, "T10 %u|%s|%s\n", f1[i].first, nm.c_str(),
                                 sub2.c_str());
                }
            }
            std::string payload;
            if (!MakeRecordTable(10, rows, false, payload, error)) return false;
            tbls.push_back(Slot{10, payload});
        } else if (key == 17 && !dirObjs.empty()) {
            std::vector<std::string> paths;
            std::vector<std::uint32_t> pathHash;
            for (const DirObj& d : dirObjs) {
                const auto dat = AttrsOf(elems[d.elem]);
                const std::string imp = Attr(dat, "importPath");
                if (!imp.empty()) {
                    const std::uint32_t ipath = inter.StringEvenEmpty(imp);
                    Slot& target = slots[d.slot];
                    if (target.payload.size() >= 16) {
                        target.payload[12] = static_cast<char>(ipath & 0xFFu);
                        target.payload[13] = static_cast<char>((ipath >> 8) & 0xFFu);
                        target.payload[14] = static_cast<char>((ipath >> 16) & 0xFFu);
                        target.payload[15] = static_cast<char>((ipath >> 24) & 0xFFu);
                    }
                }
                paths.push_back(Attr(dat, "destinationPath"));
                // key-17 的 f0 用 UTF-16 码点哈希（A..Z 折小写），不是按字节的 Hash64：
                // 01104 的 37 个目录 f0 只有 Hash64CodePoints 能全中。
                pathHash.push_back(static_cast<std::uint32_t>(Hash64CodePoints(paths.back()) & kM32));
            }
            // 目录路径是**成批**入池的：参考实现的字符串表按自身桶序给这一批分配 id，
            // 不是按文档序（01104：37 个目录的 id 5..41 正好等于
            // SitOrder(Hash64CodePoints(path))）。已被文件记录提前入池的路径
            // （01048 的 id 2/16）沿用旧 id，所以这里按批序调用 String 是安全的。
            std::vector<std::uint32_t> pathIds(paths.size(), kNoId);
            {
                std::vector<std::uint64_t> keys;
                std::vector<std::size_t> idx;
                for (std::size_t i = 0; i < paths.size(); ++i) {
                    if (paths[i].empty()) continue;
                    keys.push_back(pathHash[i]);
                    idx.push_back(i);
                }
                for (std::size_t o : SitOrder(keys)) {
                    const std::size_t i = idx[o];
                    pathIds[i] = static_cast<std::uint32_t>(inter.String(paths[i]));
                }
            }
            for (std::size_t i = 0; i < dirObjs.size(); ++i) {
                dirRows.push_back(Row{pathHash[i], pathIds[i],
                                      static_cast<std::uint32_t>(dirObjs[i].slot)});
            }
            // 行序与 count 无关：始终按 f0（Hash64CodePoints(path) 低 32 位）
            // 升序。13/13 个目录数 ≥2 的样本都严格符合，包括 count<7 时 f0
            // 数组落盘虽被省略、但行的相对次序仍由 f0 决定（01049/02104/02496/
            // 02512 等）；此前按 64 位 h64 % 桶排是错的经验规则。
            std::vector<Row> rows = dirRows;
            SortAsc(rows);
            DbgDumpTable("k17", rows, inter);
            std::string payload;
            if (!MakeRecordTable(6, rows, true, payload, error)) return false;
            tbls.push_back(Slot{6, payload});
        } else if (key == 666 && !cacheRows.empty()) {
            // 真机行序 = 记录 GUID 的 Data1（前 4 字节小端）升序；f1 = 该 GUID 在
            // 头部 GUID 表里的下标，f2 = 记录槽。f0 只在 count>=7 落盘（语料里没有）。
            std::vector<std::size_t> order(cacheRows.size());
            for (std::size_t i = 0; i < order.size(); ++i) order[i] = i;
            std::stable_sort(order.begin(), order.end(),
                             [&cacheRows, &cacheGuids](std::size_t a, std::size_t b) {
                                 const std::uint32_t da =
                                     GuidData1(cacheGuids[cacheRows[a].guidIdx]);
                                 const std::uint32_t db =
                                     GuidData1(cacheGuids[cacheRows[b].guidIdx]);
                                 if (da != db) return da < db;
                                 if (cacheRows[a].slot != cacheRows[b].slot)
                                     return cacheRows[a].slot < cacheRows[b].slot;
                                 return a < b;
                             });
            std::vector<Row> rows;
            rows.reserve(cacheRows.size());
            for (std::size_t i = 0; i < order.size(); ++i) {
                const CacheRow& cr = cacheRows[order[i]];
                rows.push_back(Row{static_cast<std::uint32_t>(i), cr.guidIdx, cr.slot});
            }
            std::string payload;
            if (!MakeRecordTable(8, rows, false, payload, error)) return false;
            tbls.push_back(Slot{8, payload});
        } else if (key == 201 && !confSlots.empty()) {
            std::vector<std::pair<std::uint32_t, std::size_t>> rs = confSlots;
            if (rs.size() > 1) std::sort(rs.begin(), rs.end());
            std::vector<Row> rows;
            rows.reserve(rs.size());
            for (const auto& item : rs) {
                rows.push_back(Row{0u, item.first, static_cast<std::uint32_t>(item.second)});
            }
            std::string payload;
            if (!MakeRecordTable(9, rows, false, payload, error)) return false;
            tbls.push_back(Slot{9, payload});
        } else if (key == 9 && !reslist.empty()) {
            std::vector<std::pair<std::string, std::size_t>> aux;
            for (const auto& rel : reslist) {
                struct Pair {
                    std::uint32_t f0;
                    std::string sid;
                    std::size_t slot;
                };
                std::vector<Pair> pairs;
                for (std::uint32_t se : rel.second) {
                    const std::string sid = Attr(AttrsOf(elems[se]), "id");
                    const auto it = locSlot.find(se);
                    if (it != locSlot.end() && !sid.empty()) {
                        pairs.push_back(Pair{static_cast<std::uint32_t>(Hash64(sid) & kM32),
                                             sid, it->second});
                    }
                }
                // 该组字符串 id 在 interner 池的桶序里分配（SitOrder over
                // Hash64(id)&M32），不是文档序：02764/02797/02731/02716/02789/
                // 02743 六样本按此顺序逐 id 对齐。行本身按 f0 升序发射。
                std::vector<std::uint32_t> keys;
                keys.reserve(pairs.size());
                for (const Pair& p : pairs) keys.push_back(p.f0);
                const std::vector<std::size_t> order = SitOrder(keys);
                std::unordered_map<std::string, std::uint32_t> sidMap;
                for (std::size_t i : order) {
                    sidMap[pairs[i].sid] = inter.StringEvenEmpty(pairs[i].sid);
                }
                std::vector<Row> rows;
                rows.reserve(pairs.size());
                for (const Pair& p : pairs) {
                    rows.push_back(Row{p.f0, sidMap[p.sid], static_cast<std::uint32_t>(p.slot)});
                }
                SortAsc(rows);
                DbgDumpTable("k9", rows, inter);
                if (!rows.empty()) {
                    std::string payload;
                    if (!MakeRecordTable(6, rows, false, payload, error)) return false;
                    aux.emplace_back(Attr(AttrsOf(elems[rel.first]), "culture"),
                                     slots.size());
                    AppendSlot(slots, 6, payload);
                }
            }
            if (!aux.empty()) {
                std::vector<Row> k9;
                k9.reserve(aux.size());
                for (const auto& item : aux) {
                    k9.push_back(Row{static_cast<std::uint32_t>(Hash64(item.first) & kM32),
                                     inter.StringEvenEmpty(item.first),
                                     static_cast<std::uint32_t>(item.second)});
                }
                SortStrTable(k9);
                std::string payload;
                if (!MakeRecordTable(6, k9, false, payload, error)) return false;
                tbls.push_back(Slot{6, payload});
            }
        } else if (key == 16) {
            bool hasRegistry = false;
            for (const Elem& e : elems) {
                if (e.local == "registryKey" || e.local == "registryValue") {
                    hasRegistry = true;
                    break;
                }
            }
            if (hasRegistry) {
                RegistryBuilder rb(inter, slots);
                if (!rb.Build(elems, error)) return false;
                if (rb.root() >= 0) reg[16] = static_cast<std::size_t>(rb.root());
            }
            continue;
        }

        if (tbls.empty()) {
            // 通用路径只实现了 key 6（引用身份表）：每个根依赖一行，
            // f0 = 身份哈希低 32 位，f1 = 依赖序号，f2 = 2*序号+1（16B 子记录槽）。
            if (key == 6 && !deps.empty()) {
                std::vector<Row> rows;
                rows.reserve(deps.size());
                for (std::size_t i = 0; i < deps.size(); ++i) {
                    rows.push_back(Row{
                        static_cast<std::uint32_t>(IdentHash(idents[i].rows) & kM32),
                        static_cast<std::uint32_t>(i),
                        static_cast<std::uint32_t>(2 * i + 1)});
                }
                std::stable_sort(rows.begin(), rows.end(), [](const Row& a, const Row& b) {
                    if (a.f0 != b.f0) return a.f0 < b.f0;
                    if (a.f1 != b.f1) return a.f1 < b.f1;
                    return a.f2 < b.f2;
                });
                std::string payload;
                if (!MakeRecordTable(0x0B, rows, false, payload, error)) return false;
                tbls.push_back(Slot{0x0B, payload});
            }
        }
        if (tbls.empty()) continue;
        for (std::size_t t = 0; t < tbls.size(); ++t) {
            if (t + 1 == tbls.size()) reg[key] = slots.size();
            slots.push_back(std::move(tbls[t]));
        }
    }

    // ---- 8b. key-33 表（CUlong 两列：bfsvc 记录的 (uidx, 槽号)）---------
    // 9600 真机 00044/00024：表内容 `(96,7)` / `(129,11)`，与 bf svc 元素的
    // uidx 及其 8B 记录槽号完全一致；注册表里 key 33 指向该槽。
    if (!bfsvcRows.empty()) {
        std::string payload;
        if (!MakeRecordTable(9, bfsvcRows, false, payload, error)) return false;
        reg[33] = slots.size();
        AppendSlot(slots, 9, payload);
    }

    // ---- 9. key → slot 注册表（无 key 的 type-9 表）----------------------
    std::size_t s1 = 0;
    if (!reg.empty()) {
        std::vector<Row> rows;
        rows.reserve(reg.size());
        for (const auto& kv : reg) {
            rows.push_back(Row{0u, static_cast<std::uint32_t>(kv.first),
                               static_cast<std::uint32_t>(kv.second)});
        }
        std::string payload;
        if (!MakeRecordTable(9, rows, false, payload, error)) return false;
        s1 = slots.size();
        AppendSlot(slots, 9, payload);
    }

    // ---- 10. 身份表（t4/t5），按 ident 哈希桶序发射 -----------------------
    std::vector<std::uint32_t> identIds(idents.size(), 0);
    if (!idents.empty()) {
        std::vector<std::uint32_t> hashes;
        hashes.reserve(idents.size());
        for (const Ident& id : idents) {
            hashes.push_back(static_cast<std::uint32_t>(IdentHash(id.rows) & kM32));
        }
        const std::vector<std::size_t> order = SitOrder(hashes);
        for (std::size_t i : order) {
            identIds[i] = static_cast<std::uint32_t>(slots.size());
            AppendSlot(slots, idents[i].type, BuildIdentTable(inter, idents[i].rows));
        }
    }

    // ---- 11. 字符串池（type 2），同一套桶序 ------------------------------
    std::vector<std::uint32_t> strIds;
    if (!inter.order().empty()) {
        const std::vector<std::string>& strs = inter.order();
        std::vector<std::uint32_t> hashes;
        hashes.reserve(strs.size());
        for (const std::string& s : strs) {
            hashes.push_back(static_cast<std::uint32_t>(Hash64(s) & kM32));
        }
        const std::vector<std::size_t> order = SitOrder(hashes);
        const std::size_t base = slots.size();
        strIds.resize(strs.size(), 0);
        for (std::size_t pos = 0; pos < order.size(); ++pos) {
            strIds[order[pos]] = static_cast<std::uint32_t>(base + pos);
            AppendSlot(slots, 2, strs[order[pos]]);
        }
    }

    // ---- 12. 页头前缀 + 容器编码 -----------------------------------------
    const std::size_t nStr = strIds.size();
    const std::size_t nGuid = cacheGuids.size();
    const std::size_t nIdent = identIds.size();
    const std::size_t tocOff = 0x54 + 4 * nStr + 16 * nGuid + 4 * nIdent;
    std::vector<std::uint8_t> prefix(tocOff, 0);
    WriteU32(prefix, 0x00, 0x486D6350u);  // "PcmH"
    WriteU32(prefix, 0x04, 1u);
    WriteU32(prefix, 0x08, 0u);
    WriteU32(prefix, 0x0C, 0u);  // 哈希占位（PcmEncode 会重算）
    WriteU32(prefix, 0x10, 0u);
    WriteU32(prefix, 0x14, static_cast<std::uint32_t>(slots.size()));
    WriteU32(prefix, 0x18, static_cast<std::uint32_t>(tocOff));
    WriteU32(prefix, 0x1C, static_cast<std::uint32_t>(nStr));
    WriteU32(prefix, 0x20, 0x54u);
    WriteU32(prefix, 0x24, static_cast<std::uint32_t>(nGuid));
    WriteU32(prefix, 0x28, nGuid ? 0x54u + static_cast<std::uint32_t>(4 * nStr) : 0u);
    WriteU32(prefix, 0x2C, static_cast<std::uint32_t>(nIdent));
    WriteU32(prefix, 0x30, nIdent ? 0x54u + static_cast<std::uint32_t>(4 * nStr + 16 * nGuid)
                                  : 0u);
    WriteU32(prefix, 0x34, 0u);
    WriteU32(prefix, 0x38, 0u);
    WriteU32(prefix, 0x3C, static_cast<std::uint32_t>(s1));
    WriteU32(prefix, 0x40, static_cast<std::uint32_t>(mdhdSlot));
    {
        static const std::uint8_t kFixedGuid[16] = {0xB5, 0x27, 0x26, 0x7E, 0x6F, 0x28,
                                                   0xD9, 0x11, 0x8C, 0x56, 0x00, 0xE0,
                                                   0x81, 0x6A, 0x64, 0x77};
        std::copy(kFixedGuid, kFixedGuid + 16, prefix.begin() + 0x44);
    }
    const std::size_t strOff = 0x54;
    for (std::size_t i = 0; i < strIds.size(); ++i) {
        WriteU32(prefix, strOff + 4 * i, strIds[i]);
    }
    const std::size_t guidOff = strOff + 4 * nStr;
    for (std::size_t i = 0; i < cacheGuids.size(); ++i) {
        if (cacheGuids[i].size() != 16) {
            error = "cache GUID 长度不是 16 字节";
            return false;
        }
        std::copy(cacheGuids[i].begin(), cacheGuids[i].end(), prefix.begin() + guidOff + 16 * i);
    }
    const std::size_t identOff = guidOff + 16 * nGuid;
    for (std::size_t i = 0; i < identIds.size(); ++i) {
        WriteU32(prefix, identOff + 4 * i, identIds[i]);
    }

    std::vector<PcmTable> tables;
    tables.reserve(slots.size());
    for (const Slot& s : slots) {
        PcmTable t;
        t.type = s.type;
        t.payload.assign(s.payload.begin(), s.payload.end());
        tables.push_back(std::move(t));
    }
    std::string encodeError;
    out = PcmEncode(prefix, tables, encodeError);
    if (!encodeError.empty()) {
        error = "PcmH 编码失败：" + encodeError;
        return false;
    }
    return !out.empty();
}

}  // namespace

bool PcmBuildFromManifest(const std::string& xmlUtf8, std::uint8_t b0Extra,
                          std::vector<std::uint8_t>& out, std::string& error) {
    out.clear();
    error.clear();
    if (!BuildCore(xmlUtf8, b0Extra, out, error)) {
        out.clear();
        return false;
    }
    return true;
}

}  // namespace ocid

