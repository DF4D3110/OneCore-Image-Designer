#include "ocid/Pcm.h"

#include <cstring>

namespace ocid {
namespace {

constexpr std::uint64_t kFoldMultiplier = 0x1FFFFFFF7ull;

constexpr std::uint32_t kHeaderMagic = 0x486D6350u;  // "PcmH"
constexpr std::size_t kHashOffset = 0x0Cu;
constexpr std::size_t kTableCountOffset = 0x14u;
constexpr std::size_t kTocOffsetOffset = 0x18u;

std::uint32_t ReadU32(const std::uint8_t* data, std::size_t offset) {
    return static_cast<std::uint32_t>(data[offset]) |
           (static_cast<std::uint32_t>(data[offset + 1]) << 8) |
           (static_cast<std::uint32_t>(data[offset + 2]) << 16) |
           (static_cast<std::uint32_t>(data[offset + 3]) << 24);
}

void WriteU32(std::uint8_t* data, std::size_t offset, std::uint32_t value) {
    data[offset] = static_cast<std::uint8_t>(value & 0xFFu);
    data[offset + 1] = static_cast<std::uint8_t>((value >> 8) & 0xFFu);
    data[offset + 2] = static_cast<std::uint8_t>((value >> 16) & 0xFFu);
    data[offset + 3] = static_cast<std::uint8_t>((value >> 24) & 0xFFu);
}

std::size_t Align4(std::size_t value) { return (value + 3u) & ~static_cast<std::size_t>(3u); }

// 真机的 entry 哈希表：初始 7 个桶、capacity=35；插入 count 超过 capacity 时
// newN = 2*count、capacity = 5*newN，旧桶按序 tail-append 到新表（顺序稳定）。
// 迭代序就是载荷的物理排放顺序。
std::vector<std::size_t> BucketOrder(const std::vector<PcmTable>& tables) {
    struct Entry {
        std::size_t index = 0;
        std::uint32_t hash = 0;
    };
    std::vector<std::vector<Entry>> buckets(7);
    std::size_t capacity = 35;
    for (std::size_t i = 0; i < tables.size(); ++i) {
        const std::uint32_t hash =
            static_cast<std::uint32_t>(PcmFold64(tables[i].payload.data(), tables[i].payload.size()));
        buckets[hash % buckets.size()].push_back(Entry{i, hash});
        if (i + 1 > capacity) {
            const std::size_t newBucketCount = 2 * (i + 1);
            std::vector<std::vector<Entry>> grown(newBucketCount);
            for (const auto& bucket : buckets) {
                for (const Entry& entry : bucket) {
                    grown[entry.hash % newBucketCount].push_back(entry);
                }
            }
            buckets = std::move(grown);
            capacity = 5 * newBucketCount;
        }
    }
    std::vector<std::size_t> order;
    order.reserve(tables.size());
    for (const auto& bucket : buckets) {
        for (const Entry& entry : bucket) {
            order.push_back(entry.index);
        }
    }
    return order;
}

}  // namespace

std::uint64_t PcmFold64(const std::uint8_t* data, std::size_t size) {
    std::uint64_t hash = 0;
    std::size_t i = 0;
    if (data == nullptr) {
        return 0;
    }
    while (size - i >= 4) {
        hash = hash * kFoldMultiplier + ReadU32(data, i);
        i += 4;
    }
    while (i < size) {
        hash = hash * kFoldMultiplier + data[i];
        ++i;
    }
    return hash;
}

bool PcmReadHeader(const std::vector<std::uint8_t>& pcm, std::uint32_t& tableCount,
                   std::uint32_t& tocOffset, std::string& error) {
    if (pcm.size() < kTocOffsetOffset + 4) {
        error = "PcmH 流太短";
        return false;
    }
    if (ReadU32(pcm.data(), 0) != kHeaderMagic) {
        error = "PcmH 魔数不匹配";
        return false;
    }
    tableCount = ReadU32(pcm.data(), kTableCountOffset);
    tocOffset = ReadU32(pcm.data(), kTocOffsetOffset);
    if (tocOffset < kTocOffsetOffset + 4 || tocOffset > pcm.size()) {
        error = "TOC 偏移越界";
        return false;
    }
    if (static_cast<std::size_t>(tocOffset) + 8ull * tableCount > pcm.size()) {
        error = "TOC 超出流长度";
        return false;
    }
    return true;
}

std::vector<std::uint8_t> PcmEncode(const std::vector<std::uint8_t>& headerPrefix,
                                    const std::vector<PcmTable>& tables, std::string& error) {
    error.clear();
    if (headerPrefix.size() < kTocOffsetOffset + 4) {
        error = "头部前缀太短";
        return {};
    }
    if (ReadU32(headerPrefix.data(), 0) != kHeaderMagic) {
        error = "头部前缀的 PcmH 魔数不匹配";
        return {};
    }
    const std::uint32_t tocOffset = ReadU32(headerPrefix.data(), kTocOffsetOffset);
    if (tocOffset != headerPrefix.size()) {
        error = "头部前缀长度必须等于 TOC 偏移";
        return {};
    }

    std::vector<std::uint8_t> out = headerPrefix;
    out.resize(static_cast<std::size_t>(tocOffset) + 8ull * tables.size(), 0);

    std::vector<std::uint32_t> offsets(tables.size(), 0);
    for (std::size_t index : BucketOrder(tables)) {
        if (out.size() % 4 != 0) {
            out.resize(Align4(out.size()), 0);
        }
        offsets[index] = static_cast<std::uint32_t>(out.size());
        out.insert(out.end(), tables[index].payload.begin(), tables[index].payload.end());
        if (out.size() % 4 != 0) {
            out.resize(Align4(out.size()), 0);
        }
    }

    for (std::size_t i = 0; i < tables.size(); ++i) {
        const std::uint32_t length = static_cast<std::uint32_t>(tables[i].payload.size());
        if (length > 0xFFFFFFu) {
            error = "单张表载荷超过 24 位长度上限";
            return {};
        }
        WriteU32(out.data(), tocOffset + 8 * i,
                 (static_cast<std::uint32_t>(tables[i].type) << 24) | length);
        WriteU32(out.data(), tocOffset + 8 * i + 4, offsets[i]);
    }

    WriteU32(out.data(), kTableCountOffset, static_cast<std::uint32_t>(tables.size()));
    WriteU32(out.data(), kHashOffset, 0);
    WriteU32(out.data(), kHashOffset + 4, 0);
    const std::uint64_t hash = PcmFold64(out.data(), out.size());
    WriteU32(out.data(), kHashOffset, static_cast<std::uint32_t>(hash & 0xFFFFFFFFu));
    WriteU32(out.data(), kHashOffset + 4, static_cast<std::uint32_t>(hash >> 32));
    return out;
}


// ---------------------------------------------------------------------------
// 通用表骨架（type 6/8/9/A/B）
// ---------------------------------------------------------------------------
namespace {

bool IsRecordTableType(std::uint8_t type) {
    return type == 6 || type == 8 || type == 9 || type == 0xA || type == 0xB;
}

}  // namespace

bool PcmDecodeRecordTable(const PcmTable& table, PcmRecordTable& out, std::string& error) {
    error.clear();
    out = PcmRecordTable{};
    if (!IsRecordTableType(table.type)) {
        error = "不是通用记录表（type " + std::to_string(table.type) + "）";
        return false;
    }
    const auto& p = table.payload;
    if (p.size() < 4) {
        error = "表载荷不足 4 字节";
        return false;
    }
    const std::uint32_t header = ReadU32(p.data(), 0);
    const std::uint32_t count = header >> 8;
    const std::uint32_t flags = header & 0xFFu;
    const std::size_t width1 = (flags & 0x10u) != 0 ? 2 : 4;
    const std::size_t width2 = (flags & 0x20u) != 0 ? 2 : 4;
    const bool hasF0 = count >= 7 && table.type != 9;
    std::size_t offset = 4;
    if (hasF0) {
        if (offset + 4ull * count > p.size()) {
            error = "f0 数组越界";
            return false;
        }
        out.f0.resize(count);
        for (std::uint32_t i = 0; i < count; ++i) out.f0[i] = ReadU32(p.data(), offset + 4ull * i);
        offset += 4ull * count;
    }
    if (offset + width1 * count > p.size()) {
        error = "f1 数组越界";
        return false;
    }
    out.f1.resize(count);
    for (std::uint32_t i = 0; i < count; ++i) {
        if (width1 == 2) {
            const std::size_t at = offset + 2ull * i;
            out.f1[i] = static_cast<std::uint32_t>(p[at]) | (static_cast<std::uint32_t>(p[at + 1]) << 8);
        } else {
            out.f1[i] = ReadU32(p.data(), offset + 4ull * i);
        }
    }
    offset = Align4(offset + width1 * count);
    if (offset + width2 * count > p.size()) {
        error = "f2 数组越界";
        return false;
    }
    out.f2.resize(count);
    for (std::uint32_t i = 0; i < count; ++i) {
        if (width2 == 2) {
            const std::size_t at = offset + 2ull * i;
            out.f2[i] = static_cast<std::uint32_t>(p[at]) | (static_cast<std::uint32_t>(p[at + 1]) << 8);
        } else {
            out.f2[i] = ReadU32(p.data(), offset + 4ull * i);
        }
    }
    out.type = table.type;
    out.bit0 = (flags & 0x01u) != 0;
    return true;
}

std::vector<std::uint8_t> PcmEncodeRecordTable(const PcmRecordTable& table, std::string& error) {
    error.clear();
    if (!IsRecordTableType(table.type)) {
        error = "不是通用记录表（type " + std::to_string(table.type) + "）";
        return {};
    }
    const std::uint32_t count = static_cast<std::uint32_t>(table.f1.size());
    if (table.f2.size() != table.f1.size()) {
        error = "f1/f2 长度不一致";
        return {};
    }
    const bool hasF0 = count >= 7 && table.type != 9;
    if (hasF0 && table.f0.size() != count) {
        error = "f0 长度与 count 不一致";
        return {};
    }
    std::uint32_t maxF1 = 0, maxF2 = 0;
    for (std::uint32_t v : table.f1) maxF1 = v > maxF1 ? v : maxF1;
    for (std::uint32_t v : table.f2) maxF2 = v > maxF2 ? v : maxF2;
    const bool narrow1 = count > 0 && maxF1 <= 0xFFFFu;
    const bool narrow2 = count > 0 && maxF2 <= 0xFFFFu;
    std::uint32_t flags = 0;
    if (table.type == 6 && table.bit0) flags |= 0x01u;
    if (narrow1) flags |= 0x10u;
    if (narrow2) flags |= 0x20u;
    if (count < 7 && table.type != 9) flags |= 0x40u;
    std::vector<std::uint8_t> out(4);
    WriteU32(out.data(), 0, (count << 8) | flags);
    const auto appendU32 = [&out](std::uint32_t value) {
        const std::size_t at = out.size();
        out.resize(at + 4);
        WriteU32(out.data(), at, value);
    };
    if (hasF0) {
        for (std::uint32_t v : table.f0) appendU32(v);
    }
    for (std::uint32_t v : table.f1) {
        if (narrow1) {
            out.push_back(static_cast<std::uint8_t>(v & 0xFFu));
            out.push_back(static_cast<std::uint8_t>((v >> 8) & 0xFFu));
        } else {
            appendU32(v);
        }
    }
    while ((out.size() % 4) != 0) out.push_back(0);
    for (std::uint32_t v : table.f2) {
        if (narrow2) {
            out.push_back(static_cast<std::uint8_t>(v & 0xFFu));
            out.push_back(static_cast<std::uint8_t>((v >> 8) & 0xFFu));
        } else {
            appendU32(v);
        }
    }
    return out;
}

}  // namespace ocid

