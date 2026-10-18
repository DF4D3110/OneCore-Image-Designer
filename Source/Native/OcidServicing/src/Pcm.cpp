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

}  // namespace ocid
