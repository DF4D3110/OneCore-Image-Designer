#include "ocid/Delta.h"

#include <algorithm>
#include <cstring>
#include <limits>
#include <memory>
#include <stdexcept>

namespace ocid {
namespace {

// Raised for malformed or unsupported deltas; converted to `error` by ApplyPa30Delta.
class DeltaFailure : public std::runtime_error {
 public:
  explicit DeltaFailure(const std::string& what) : std::runtime_error(what) {}
};

// LSB-first bit reader matching compo::BitReaderObject. Reads take the low bits of a
// little-endian 32-bit window; BitReaderOpen always consumes three bits first.
class BitReader {
 public:
  BitReader(const std::uint8_t* data, std::size_t size, int skipBits = 0)
      : data_(data), size_(size), position_(skipBits) {}

  int position() const { return position_; }

  std::uint32_t Peek32() const {
    const std::size_t start = static_cast<std::size_t>(position_) / 8;
    std::uint64_t value = 0;
    for (int i = 0; i < 5; ++i) {
      const std::size_t index = start + static_cast<std::size_t>(i);
      const std::uint64_t byte = index < size_ ? data_[index] : 0;
      value |= byte << (8 * i);
    }
    return static_cast<std::uint32_t>(value >> (position_ & 7));
  }

  std::uint32_t Read(int bits) {
    if (bits == 0) {
      return 0;
    }
    if (bits < 0 || bits > 32) {
      throw DeltaFailure("PA30: unsupported bit read");
    }
    const std::uint64_t mask = (1ULL << bits) - 1;
    const std::uint32_t value = static_cast<std::uint32_t>(Peek32() & mask);
    position_ += bits;
    return value;
  }

  void Consume(int bits) { position_ += bits; }

  void AlignToByte() { position_ = (position_ + 7) & ~7; }

  std::uint64_t ReadBit() {
    const std::uint32_t peek = Peek32();
    const int z = LowestSetBit(peek | 0x10000u);
    if (z == 16) {
      throw DeltaFailure("PA30: invalid variable-length integer prefix");
    }
    position_ += z + 1;
    if (z >= 8) {
      const std::uint64_t low = Read(32);
      const std::uint64_t high = Read(4 * z - 28);
      return low | (high << 32);
    }
    return Read(4 * (z + 1));
  }

  std::uint64_t ReadNumber() {
    const std::uint32_t peek = Peek32();
    if (peek == 0) {
      throw DeltaFailure("PA30: zero prefix in ReadNumber");
    }
    const int k = LowestSetBit(peek);
    if (31 - k < 8) {
      throw DeltaFailure("PA30: invalid ReadNumber prefix");
    }
    const int n = k + 8;
    position_ += k + 1;
    return (1ULL << n) | Read(n);
  }

  std::vector<std::uint8_t> ReadBuffer() {
    const std::uint64_t length = ReadBit();
    AlignToByte();
    const std::size_t start = static_cast<std::size_t>(position_) / 8;
    if (length > size_ - start) {
      throw DeltaFailure("PA30: buffer length exceeds the delta payload");
    }
    position_ += static_cast<int>(length) * 8;
    return std::vector<std::uint8_t>(data_ + start, data_ + start + length);
  }

  static int LowestSetBit(std::uint32_t value) {
    if (value == 0) {
      return 32;
    }
    int index = 0;
    while ((value & 1u) == 0) {
      value >>= 1;
      ++index;
    }
    return index;
  }

 private:
  const std::uint8_t* data_;
  std::size_t size_;
  int position_;
};

// Canonical Huffman code lengths plus the bit-reversed codes msdelta uses.
struct Codes {
  std::vector<std::uint8_t> lengths;
  std::vector<std::uint32_t> codes;

  explicit Codes(const std::vector<std::uint8_t>& input)
      : lengths(input), codes(input.size(), 0) {
    int maxLength = 0;
    for (std::uint8_t length : lengths) {
      maxLength = std::max<int>(maxLength, length);
    }
    std::vector<int> counts(static_cast<std::size_t>(maxLength) + 1, 0);
    for (std::uint8_t length : lengths) {
      if (length != 0) {
        ++counts[length];
      }
    }
    int remaining = 2;
    for (int length = 1; length <= maxLength; ++length) {
      if (counts[static_cast<std::size_t>(length)] > remaining) {
        throw DeltaFailure("PA30: invalid Huffman code lengths");
      }
      remaining = 2 * (remaining - counts[static_cast<std::size_t>(length)]);
    }
    std::vector<int> start(static_cast<std::size_t>(maxLength) + 1, 0);
    int current = 0;
    for (int length = maxLength; length > 0; --length) {
      start[static_cast<std::size_t>(length)] = current;
      current = (counts[static_cast<std::size_t>(length)] + current) >> 1;
    }
    for (std::size_t symbol = 0; symbol < lengths.size(); ++symbol) {
      const int length = lengths[symbol];
      if (length == 0) {
        continue;
      }
      int value = start[static_cast<std::size_t>(length)]++;
      std::uint32_t code = 0;
      for (int bit = 0; bit < length; ++bit) {
        code = static_cast<std::uint32_t>((value & 1) | (code << 1));
        value >>= 1;
      }
      codes[symbol] = code;
    }
  }
};

// Lookup table keyed by the lowest set bit of the peeked window.
class DecoderTable {
 public:
  explicit DecoderTable(const Codes& codes) {
    const std::vector<std::uint8_t>& lengths = codes.lengths;
    const std::vector<std::uint32_t>& reserved = codes.codes;
    const int count = static_cast<int>(lengths.size());

    int width[32] = {};
    int effective = count;
    int maxLow = 0;
    for (int symbol = 0; symbol < count; ++symbol) {
      const int length = lengths[static_cast<std::size_t>(symbol)];
      if (length == 0) {
        continue;
      }
      const std::uint32_t code = reserved[static_cast<std::size_t>(symbol)];
      if (code != 0) {
        const int low = BitReader::LowestSetBit(code);
        if (low >= 32) {
          throw DeltaFailure("PA30: bad Huffman code");
        }
        const int extra = length - low - 1;
        if (extra < 0) {
          throw DeltaFailure("PA30: bad Huffman code length");
        }
        maxLow = std::max(maxLow, low);
        width[low] = std::max(width[low], extra);
      } else {
        if (effective != count) {
          throw DeltaFailure("PA30: duplicate all-zero Huffman code");
        }
        effective = symbol;
      }
    }

    const int maxBits = maxLow + 1;
    int total = 0;
    for (int low = 0; low < maxBits; ++low) {
      mask_[low] = (1 << width[low]) - 1;
      base_[low] = total;
      total += 1 << width[low];
    }
    for (int low = maxBits; low < 32; ++low) {
      mask_[low] = 0;
      base_[low] = total;
    }

    symbols_.assign(static_cast<std::size_t>(total) + 1, 0);
    for (int symbol = 0; symbol < count; ++symbol) {
      const int length = lengths[static_cast<std::size_t>(symbol)];
      const std::uint32_t code = reserved[static_cast<std::size_t>(symbol)];
      if (length == 0 || code == 0) {
        continue;
      }
      const int low = BitReader::LowestSetBit(code);
      const int stride = 1 << (length - low - 1);
      int index = base_[low] + static_cast<int>(code >> (low + 1));
      const int repeats = 1 << (width[low] - (length - low - 1));
      for (int repetition = 0; repetition < repeats; ++repetition) {
        symbols_[static_cast<std::size_t>(index)] = symbol;
        index += stride;
      }
    }
    if (count > effective) {
      symbols_[static_cast<std::size_t>(total)] = effective;
    }
  }

  int Decode(const BitReader& reader) const {
    const std::uint32_t peek = reader.Peek32() | 0x80000000u;
    const int low = BitReader::LowestSetBit(peek);
    const std::size_t index = static_cast<std::size_t>(base_[low]) +
                              ((peek >> (low + 1)) & static_cast<std::uint32_t>(mask_[low]));
    return symbols_[index];
  }

 private:
  int base_[32] = {};
  int mask_[32] = {};
  std::vector<int> symbols_;
};

// compo::IntFormat: 252-entry adaptive integer alphabet.
class IntFormat {
 public:
  explicit IntFormat(BitReader& reader) {
    const int explicitCount = static_cast<int>(reader.Read(8));
    const int runCount = static_cast<int>(reader.Read(8));
    const int runLength = static_cast<int>(reader.Read(8));
    if (explicitCount > 0x7E || runCount > 0x7E ||
        252 - runCount - explicitCount < runLength) {
      throw DeltaFailure("PA30: bad integer alphabet header");
    }

    lengths_.assign(252, 0);
    for (int i = 0; i < explicitCount; ++i) {
      lengths_[static_cast<std::size_t>(i)] = static_cast<std::uint8_t>(reader.Read(4) + 1);
    }

    int index = 0;
    int last = 0;
    for (;;) {
      last = static_cast<int>(reader.Read(4));
      if (index >= runCount) {
        break;
      }
      lengths_[static_cast<std::size_t>(126 + index)] = static_cast<std::uint8_t>(last + 1);
      ++index;
    }

    int value = last + 1;
    int remaining = runLength;
    for (int position = explicitCount; position < 0x7E; ++position) {
      const int current = remaining;
      --remaining;
      if (current == 0) {
        --value;
        remaining = 252 - position - runCount;
      }
      lengths_[static_cast<std::size_t>(position)] = static_cast<std::uint8_t>(value);
    }

    remaining = runLength;
    for (int position = runCount; position < 0x7E; ++position) {
      const int current = remaining;
      std::uint8_t digit;
      if (current != 0) {
        digit = static_cast<std::uint8_t>(value);
        remaining = current - 1;
      } else {
        digit = static_cast<std::uint8_t>(value - 1);
        remaining = 126 - position;
      }
      lengths_[static_cast<std::size_t>(126 + position)] = digit;
      value = digit;
    }

    if (std::find_if(lengths_.begin(), lengths_.end(), [](std::uint8_t v) { return v > 16; }) !=
        lengths_.end()) {
      throw DeltaFailure("PA30: bad integer alphabet length");
    }
    codes_ = std::make_unique<Codes>(lengths_);
    table_ = std::make_unique<DecoderTable>(*codes_);
  }

  std::int64_t ReadNumber(BitReader& reader) const {
    const int symbol = table_->Decode(reader);
    reader.Consume(lengths_[static_cast<std::size_t>(symbol)]);
    const int value = symbol < 0x7E ? symbol : symbol - 0x7E;
    if (value < 4) {
      return value;
    }
    const int shift = (value >> 1) - 1;
    const std::uint64_t baseValue = static_cast<std::uint64_t>(((value & 1) + 2)) << shift;
    std::uint64_t extra = 0;
    if (shift > 32) {
      const std::uint64_t low = reader.Read(32);
      const std::uint64_t high = reader.Read(shift - 32);
      extra = low | (high << 32);
    } else {
      extra = reader.Read(shift);
    }
    const std::int64_t result = static_cast<std::int64_t>(extra | baseValue);
    return symbol < 0x7E ? result : ~result;
  }

 private:
  std::vector<std::uint8_t> lengths_;
  std::unique_ptr<Codes> codes_;
  std::unique_ptr<DecoderTable> table_;
};

struct RiftEntry {
  std::int64_t threshold;
  std::int64_t delta;
};

// compo::RiftTable. Empty for RAW payloads; the non-empty encoding is not reproduced.
class RiftTable {
 public:
  explicit RiftTable(BitReader& reader) {
    if (reader.Read(1) == 0) {
      return;
    }
    const IntFormat positions(reader);
    const IntFormat lengths(reader);
    const std::uint64_t count = reader.ReadBit();
    if (count > 0x0FFFFFFFFFFFFFFFULL) {
      throw DeltaFailure("PA30: bad rift table entry count");
    }
    std::int64_t threshold = 0;
    std::int64_t length = 0;
    for (std::uint64_t i = 0; i < count; ++i) {
      threshold += positions.ReadNumber(reader);
      length += lengths.ReadNumber(reader);
      entries_.push_back({threshold + length, threshold});
    }
  }

  std::int64_t DeltaAt(std::int64_t position) const {
    std::int64_t delta = 0;
    for (const RiftEntry& entry : entries_) {
      if (entry.threshold > position) {
        break;
      }
      delta = entry.delta;
    }
    return delta;
  }

 private:
  std::vector<RiftEntry> entries_;
};

// One pseudo-LZX compression format: 600 main lengths, 256 length lengths, 16 aligned.
class CompressionFormat {
 public:
  CompressionFormat(std::vector<std::uint8_t> mainLengths,
                    std::vector<std::uint8_t> lengthLengths,
                    std::vector<std::uint8_t> alignedLengths)
      : main_lengths_(std::move(mainLengths)),
        length_lengths_(std::move(lengthLengths)),
        aligned_lengths_(std::move(alignedLengths)),
        main_table_(std::make_unique<DecoderTable>(Codes(main_lengths_))),
        length_table_(std::make_unique<DecoderTable>(Codes(length_lengths_))),
        aligned_table_(std::make_unique<DecoderTable>(Codes(aligned_lengths_))) {}

  static CompressionFormat CreateDefault() {
    std::vector<std::uint8_t> main(600, 0);
    std::fill(main.begin(), main.begin() + 424, 9);
    std::fill(main.begin() + 424, main.end(), 10);
    std::vector<std::uint8_t> lengths(256, 8);
    std::vector<std::uint8_t> aligned(16, 4);
    return CompressionFormat(std::move(main), std::move(lengths), std::move(aligned));
  }

  void ReadSymbol(BitReader& reader, std::uint32_t* marker, int* length) const {
    const int symbol = Decode(reader, *main_table_, main_lengths_);
    if (symbol < 0x100) {
      *marker = static_cast<std::uint32_t>(symbol);
      *length = 1;
      return;
    }

    const int value = symbol - 0x100;
    const int slot = value >> 3;
    const int lengthCode = value & 7;
    std::uint32_t result = 0;
    if (slot < 3) {
      if (slot == 0) {
        result = reader.Read(14) - 0x2000u + 0x2A000u;
      } else if (slot == 1) {
        const std::uint32_t v = reader.Read(16) - 0x8000u;
        result = (v < 0x8000 ? v + 0x2000u : v - 0x2000u) + 0x2A000u;
      } else {
        const std::uint32_t v = reader.Read(18) - 0x20000u;
        result = v < 0x20000 ? v + 0x34000u : (v - 40960u) + 0x2A000u;
      }
    } else if (slot < 7) {
      result = 0x54000u + static_cast<std::uint32_t>(slot - 3);
    } else {
      int v21 = slot - 7;
      if (v21 == 0) {
        if (reader.Read(1) != 0) {
          v21 = reader.Read(1) != 0 ? static_cast<int>(reader.Read(4)) + 48
                                    : static_cast<int>(reader.Read(3)) + 40;
        } else {
          v21 = static_cast<int>(reader.Read(2)) + 36;
        }
      }
      if (v21 < 4) {
        result = 0x54003u + static_cast<std::uint32_t>(v21);
      } else {
        const int v22 = (v21 >> 1) - 1;
        int v23 = (v21 & 1) + 2;
        if (v22 >= 4) {
          const int v24 = (v21 >> 1) - 5;
          if ((v21 >> 1) != 5) {
            v23 = static_cast<int>(reader.Read(v24)) | (v23 << v24);
          }
          const int aligned = Decode(reader, *aligned_table_, aligned_lengths_);
          v21 = aligned | (16 * v23);
        } else {
          v21 = static_cast<int>(reader.Read(v22)) | (v23 << v22);
        }
        if (static_cast<std::uint32_t>(v21) > 0xFFFABFFCu) {
          throw DeltaFailure("PA30: invalid match distance");
        }
        result = static_cast<std::uint32_t>(v21) + 344067u;
      }
    }

    if (lengthCode != 0) {
      *marker = result;
      *length = lengthCode + 1;
      return;
    }

    int symbol2 = Decode(reader, *length_table_, length_lengths_);
    if (symbol2 == 0) {
      const std::uint64_t extended = reader.ReadNumber();
      if (extended > 0xFFFFFFF7ULL) {
        throw DeltaFailure("PA30: invalid match length");
      }
      symbol2 = static_cast<int>(extended);
    }
    *marker = result;
    *length = symbol2 + 8;
  }

 private:
  static int Decode(BitReader& reader, const DecoderTable& table,
                    const std::vector<std::uint8_t>& lengths) {
    const int symbol = table.Decode(reader);
    reader.Consume(lengths[static_cast<std::size_t>(symbol)]);
    return symbol;
  }

  std::vector<std::uint8_t> main_lengths_;
  std::vector<std::uint8_t> length_lengths_;
  std::vector<std::uint8_t> aligned_lengths_;
  std::unique_ptr<DecoderTable> main_table_;
  std::unique_ptr<DecoderTable> length_table_;
  std::unique_ptr<DecoderTable> aligned_table_;
};

// The 872 code lengths of one format, delta-coded against the previous format.
class CompressionLengths {
 public:
  CompressionLengths(BitReader& reader, const Codes& codes, const DecoderTable& table,
                     const std::vector<std::uint8_t>& previous) {
    lengths_.assign(872, 0);
    std::size_t index = 0;
    while (index < 872) {
      const int symbol = table.Decode(reader);
      reader.Consume(codes.lengths[static_cast<std::size_t>(symbol)]);
      if (symbol < 0x11) {
        lengths_[index++] = static_cast<std::uint8_t>(symbol);
      } else if (symbol < 0x17) {
        const int delta = symbol - 0x11;
        const int prior = previous[index];
        const int value = delta < 3 ? prior + delta + 1 : prior - delta + 2;
        if (value < 0 || value > 16 || (delta < 3 ? value <= prior : value >= prior)) {
          throw DeltaFailure("PA30: bad code length delta");
        }
        lengths_[index++] = static_cast<std::uint8_t>(value);
      } else {
        const int v19 = symbol - 0x17;
        const int bits = v19 & 7;
        const int count =
            bits >= 3 ? (1 << (bits - 1)) | static_cast<int>(reader.Read(bits - 1)) : bits + 1;
        if (index + static_cast<std::size_t>(count) > 872) {
          throw DeltaFailure("PA30: code length run overruns the table");
        }
        if (v19 < 8) {
          if (index == 0) {
            throw DeltaFailure("PA30: code length run at offset zero");
          }
          for (int k = 0; k < count; ++k) {
            lengths_[index + static_cast<std::size_t>(k)] = lengths_[index - 1 + static_cast<std::size_t>(k)];
          }
        } else {
          for (int k = 0; k < count; ++k) {
            lengths_[index + static_cast<std::size_t>(k)] = previous[index + static_cast<std::size_t>(k)];
          }
        }
        index += static_cast<std::size_t>(count);
      }
    }
  }

  const std::vector<std::uint8_t>& lengths() const { return lengths_; }

 private:
  std::vector<std::uint8_t> lengths_;
};

// One or more compression formats switched by output position.
class CompositeFormat {
 public:
  explicit CompositeFormat(BitReader& reader) {
    if (reader.Read(1) != 0) {
      formats_.push_back(CompressionFormat::CreateDefault());
      bounds_.push_back(std::numeric_limits<std::int64_t>::max());
      return;
    }

    const int count = static_cast<int>(reader.ReadBit());
    std::int64_t accumulated = 0;
    for (int i = 0; i < count; ++i) {
      accumulated += static_cast<std::int64_t>(reader.ReadBit());
      bounds_.push_back(accumulated);
    }

    std::vector<std::uint8_t> codeLengths(39);
    for (int i = 0; i < 39; ++i) {
      codeLengths[static_cast<std::size_t>(i)] = static_cast<std::uint8_t>(reader.Read(4));
    }
    const Codes codes(codeLengths);
    const DecoderTable table(codes);

    std::vector<std::uint8_t> previous(872, 0);
    for (int i = 0; i < count; ++i) {
      const CompressionLengths lengths(reader, codes, table, previous);
      const std::vector<std::uint8_t>& all = lengths.lengths();
      std::vector<std::uint8_t> main(all.begin(), all.begin() + 600);
      std::vector<std::uint8_t> lengthLengths(all.begin() + 600, all.begin() + 856);
      std::vector<std::uint8_t> aligned(all.begin() + 856, all.begin() + 872);
      formats_.push_back(CompressionFormat(std::move(main), std::move(lengthLengths),
                                          std::move(aligned)));
      previous = all;
    }
  }

  // Mirror of CompositeFormat::GetCompressionFormatRead (0x18002392C): the last format whose
  // cumulative bound is <= position wins (each bound starts the next format), and the final
  // format extends to the end. Positions are unified-buffer offsets (source then target).
  const CompressionFormat& FormatAt(std::int64_t position) const {
    std::size_t index = 0;
    for (std::size_t i = 0; i < bounds_.size(); ++i) {
      if (bounds_[i] <= position) {
        index = i;
      }
    }
    return formats_[index];
  }

 private:
  std::vector<CompressionFormat> formats_;
  std::vector<std::int64_t> bounds_;
};

constexpr std::uint32_t kAlignedSourceCopy = 0x54000;
constexpr std::uint32_t kSourceDistanceBase = 0x2A000;
constexpr std::uint32_t kRunDistanceBase = 0x54000;

}  // namespace

bool IsPa30Delta(const std::vector<std::uint8_t>& delta) {
  return delta.size() >= 4 && std::memcmp(delta.data(), "PA30", 4) == 0;
}

bool ApplyPa30Delta(const std::vector<std::uint8_t>& source,
                    const std::vector<std::uint8_t>& delta,
                    std::vector<std::uint8_t>& output,
                    std::string& error) {
  output.clear();
  if (!IsPa30Delta(delta) || delta.size() < 12) {
    error = "not a PA30 delta";
    return false;
  }

  try {
    BitReader reader(delta.data() + 12, delta.size() - 12, 3);
    const std::uint64_t fileTypeSet = reader.ReadBit();
    const std::uint64_t fileType = reader.ReadBit();
    (void)fileTypeSet;
    if (fileType != 1) {
      error = "PA30 delta needs the PE pre-processor (file type " + std::to_string(fileType) +
              "), which is not implemented yet";
      return false;
    }
    (void)reader.ReadBit();  // flags
    const std::uint64_t targetSizeValue = reader.ReadBit();
    (void)reader.ReadBit();  // hash algorithm id
    if (targetSizeValue > 0x7FFFFFFFULL) {
      error = "PA30 target size is out of range";
      return false;
    }
    const std::size_t targetSize = static_cast<std::size_t>(targetSizeValue);

    (void)reader.ReadBuffer();  // target hash
    (void)reader.ReadBuffer();  // pre-process buffer
    const std::vector<std::uint8_t> patch = reader.ReadBuffer();

    BitReader patchReader(patch.data(), patch.size(), 3);
    const RiftTable rift(patchReader);
    const CompositeFormat format(patchReader);

    std::vector<std::uint8_t> buffer(source.size() + targetSize, 0);
    std::copy(source.begin(), source.end(), buffer.begin());

    const std::int64_t basePosition = static_cast<std::int64_t>(source.size());
    std::int64_t position = basePosition;
    const std::int64_t end = basePosition + static_cast<std::int64_t>(targetSize);
    std::uint32_t r0 = 0;
    std::uint32_t r1 = 0;
    std::uint32_t r2 = 0;

    while (position < end) {
      const CompressionFormat& current = format.FormatAt(position);  // unified buffer offset
      std::uint32_t marker = 0;
      int length = 0;
      current.ReadSymbol(patchReader, &marker, &length);

      if (length == 1) {
        buffer[static_cast<std::size_t>(position++)] = static_cast<std::uint8_t>(marker);
        continue;
      }

      if (position + length > end) {
        error = "PA30 match runs past the end of the target";
        return false;
      }

      if (marker == kAlignedSourceCopy) {
        // The source region is copied onto itself at the same offset; this still pushes the
        // source size into the R0/R1/R2 history.
        const std::int64_t sourceIndex = position - basePosition;
        for (int k = 0; k < length; ++k) {
          buffer[static_cast<std::size_t>(position + k)] =
              buffer[static_cast<std::size_t>(sourceIndex + k)];
        }
        const std::uint32_t alignedDistance = static_cast<std::uint32_t>(basePosition);
        if (r0 != alignedDistance) {
          if (r1 != alignedDistance) {
            r2 = r1;
          }
          r1 = r0;
          r0 = alignedDistance;
        }
        position += length;
        continue;
      }

      std::uint32_t distance = 0;
      if (marker >= kRunDistanceBase) {
        const std::uint32_t index = marker - (kRunDistanceBase + 1);
        distance = index == 0 ? r0 : index == 1 ? r1 : index == 2 ? r2 : marker - (kRunDistanceBase + 3);
      } else {
        const std::int64_t riftDelta = rift.DeltaAt(position - basePosition);
        distance = static_cast<std::uint32_t>(marker - kSourceDistanceBase - riftDelta);
      }

      if (distance == 0 || static_cast<std::int64_t>(distance) > position) {
        error = "PA30 match distance is outside the decoded data";
        return false;
      }

      if (r0 != distance) {
        if (r1 != distance) {
          r2 = r1;
        }
        r1 = r0;
        r0 = distance;
      }

      for (int k = 0; k < length; ++k) {
        buffer[static_cast<std::size_t>(position + k)] =
            buffer[static_cast<std::size_t>(position - static_cast<std::int64_t>(distance) + k)];
      }
      position += length;
    }

    output.assign(buffer.begin() + static_cast<std::ptrdiff_t>(basePosition),
                  buffer.begin() + static_cast<std::ptrdiff_t>(end));
    return true;
  } catch (const DeltaFailure& failure) {
    error = failure.what();
    return false;
  } catch (const std::exception& failure) {
    error = failure.what();
    return false;
  }
}

}  // namespace ocid
