// OCID Imaging / Cab —— Microsoft Cabinet (.cab) 读取实现
//
// 结构（全部小端）：
//   CFHEADER : "MSCF" u32 reserved1 cbCabinet reserved2 coffFiles reserved3
//              u8 minor u8 major u16 cFolders u16 cFiles u16 flags u16 setID u16 iCabinet
//              若 flags&4：u16 cbCFHeader, u8 cbCFFolder, u8 cbCFData, 然后 cbCFHeader 字节
//   CFFOLDER[ cFolders ]（紧跟 header 的保留区之后）
//              u32 coffCabStart  u16 cCFData  u16 typeCompress（低 4 位 = 压缩法）
//   CFFILE[ cFiles ]（在 coffFiles 处）
//              u32 cbFile  u32 uoffFolderStart  u16 iFolder  u16 date u16 time u16 attribs
//              char szName[]（NUL 结尾，反斜杠分隔）
//   CFDATA 块（每个 folder 的块从该 folder 的 coffCabStart 起顺序排列）
//              u32 csum  u16 cbData  u16 cbUncomp  u8 data[cbData]  [保留字节]
//
// 压缩法：0 = NONE，1 = MSZIP（每块以 "CK" 开头，其后是 raw Deflate，跨块共享 32KB 字典），
//         2 = Quantum，3 = LZX。本实现只做 NONE / MSZIP；其余明确报错。
#include "ocid/imaging/Cab.h"

#include "ocid/imaging/Common.h"

#include <cstring>

namespace ocid
{
namespace imaging
{
namespace
{

// ── 小端读取 ──────────────────────────────────────────────────────────────
std::uint16_t ReadU16(const std::uint8_t* p) { return static_cast<std::uint16_t>(p[0] | (p[1] << 8)); }

std::uint32_t ReadU32(const std::uint8_t* p)
{
    return static_cast<std::uint32_t>(p[0]) | (static_cast<std::uint32_t>(p[1]) << 8) |
           (static_cast<std::uint32_t>(p[2]) << 16) | (static_cast<std::uint32_t>(p[3]) << 24);
}

std::string AsciiToUtf8(const char* text, std::size_t length)
{
    // CAB 里的文件名是 ASCII/本地代码页；这里按 ASCII 处理（超出的字节原样保留）。
    return std::string(text, length);
}

// ── Deflate 解码（puff 风格：规范 Huffman + 逐位解码）────────────────────────
struct BitReader
{
    const std::uint8_t* data = nullptr;
    std::size_t size = 0;
    std::size_t pos = 0;
    std::uint32_t buffer = 0;
    int bits = 0;
    bool error = false;

    int Bits(int need)
    {
        while (bits < need)
        {
            if (pos >= size)
            {
                error = true;
                return 0;
            }
            buffer |= static_cast<std::uint32_t>(data[pos++]) << bits;
            bits += 8;
        }
        const int value = static_cast<int>(buffer & ((1u << need) - 1u));
        buffer >>= need;
        bits -= need;
        return value;
    }

    // 丢掉剩余位（Deflate 块之间按字节对齐）。
    void AlignToByte()
    {
        buffer = 0;
        bits = 0;
    }
};

struct Huffman
{
    short count[16] = {};
    short symbol[288] = {};
};

bool BuildHuffman(Huffman& table, const std::uint8_t* lengths, int count)
{
    for (int i = 0; i < 16; ++i)
    {
        table.count[i] = 0;
    }
    for (int i = 0; i < count; ++i)
    {
        ++table.count[lengths[i]];
    }
    if (table.count[0] == count)
    {
        return true; // 整套长度为 0（该集合没被使用）
    }
    int left = 1;
    for (int length = 1; length < 16; ++length)
    {
        left <<= 1;
        left -= table.count[length];
        if (left < 0)
        {
            return false; // 过度订阅
        }
    }
    short offsets[16] = {};
    for (int length = 1; length < 15; ++length)
    {
        offsets[length + 1] = static_cast<short>(offsets[length] + table.count[length]);
    }
    for (int i = 0; i < count; ++i)
    {
        if (lengths[i] != 0)
        {
            table.symbol[offsets[lengths[i]]++] = static_cast<short>(i);
        }
    }
    return true;
}

int DecodeSymbol(BitReader& reader, const Huffman& table)
{
    int code = 0;
    int first = 0;
    int index = 0;
    for (int length = 1; length < 16; ++length)
    {
        code |= reader.Bits(1);
        if (reader.error)
        {
            return -1;
        }
        const int count = table.count[length];
        if (code - first < count)
        {
            return table.symbol[index + (code - first)];
        }
        index += count;
        first = (first + count) << 1;
        code <<= 1;
    }
    return -1;
}

const short kLengthBase[29] = {3,  4,  5,  6,  7,  8,  9,  10, 11,  13,  15,  17,  19,  23, 27,
                               31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258};
const short kLengthExtra[29] = {0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2,
                                2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0};
const short kDistBase[30] = {1,    2,    3,    4,    5,    7,     9,     13,    17,   25,
                             33,   49,   65,   97,   129,  193,   257,   385,   513,  769,
                             1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577};
const short kDistExtra[30] = {0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6,
                              6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13};

// 解一段 raw Deflate。out 进来时已含"字典"（MSZIP 的上一块输出尾部），返回时追加解压结果。
bool InflateRaw(const std::uint8_t* data, std::size_t size, std::vector<std::uint8_t>& out,
                std::string& error)
{
    BitReader reader;
    reader.data = data;
    reader.size = size;

    for (;;)
    {
        const int last = reader.Bits(1);
        const int type = reader.Bits(2);
        if (reader.error)
        {
            error = "Deflate 数据提前结束";
            return false;
        }

        if (type == 0)
        {
            reader.AlignToByte();
            // 已对齐到字节，但 Bits() 已经把整字节读进 buffer —— 用剩余字节数重算位置。
            const std::size_t consumed = reader.pos - static_cast<std::size_t>(reader.bits / 8);
            reader.pos = consumed;
            reader.buffer = 0;
            reader.bits = 0;
            if (reader.pos + 4 > reader.size)
            {
                error = "Deflate 存储块头损坏";
                return false;
            }
            const std::uint16_t length = ReadU16(data + reader.pos);
            const std::uint16_t complement = ReadU16(data + reader.pos + 2);
            if (static_cast<std::uint16_t>(~length) != complement)
            {
                error = "Deflate 存储块长度校验失败";
                return false;
            }
            reader.pos += 4;
            if (reader.pos + length > reader.size)
            {
                error = "Deflate 存储块数据越界";
                return false;
            }
            out.insert(out.end(), data + reader.pos, data + reader.pos + length);
            reader.pos += length;
        }
        else if (type == 1 || type == 2)
        {
            Huffman literal;
            Huffman distance;
            if (type == 1)
            {
                std::uint8_t lengths[288];
                for (int i = 0; i < 144; ++i) lengths[i] = 8;
                for (int i = 144; i < 256; ++i) lengths[i] = 9;
                for (int i = 256; i < 280; ++i) lengths[i] = 7;
                for (int i = 280; i < 288; ++i) lengths[i] = 8;
                if (!BuildHuffman(literal, lengths, 288))
                {
                    error = "Deflate 固定 Huffman 表非法";
                    return false;
                }
                std::uint8_t distLengths[30];
                for (int i = 0; i < 30; ++i) distLengths[i] = 5;
                if (!BuildHuffman(distance, distLengths, 30))
                {
                    error = "Deflate 固定距离表非法";
                    return false;
                }
            }
            else
            {
                const int literalCount = reader.Bits(5) + 257;
                const int distanceCount = reader.Bits(5) + 1;
                const int codeCount = reader.Bits(4) + 4;
                if (reader.error)
                {
                    error = "Deflate 动态表头损坏";
                    return false;
                }
                static const int order[19] = {16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15};
                std::uint8_t codeLengths[19] = {};
                for (int i = 0; i < codeCount; ++i)
                {
                    codeLengths[order[i]] = static_cast<std::uint8_t>(reader.Bits(3));
                }
                Huffman codeTable;
                if (!BuildHuffman(codeTable, codeLengths, 19))
                {
                    error = "Deflate 码长表非法";
                    return false;
                }

                std::vector<std::uint8_t> lengths(static_cast<std::size_t>(literalCount + distanceCount));
                for (int i = 0; i < literalCount + distanceCount;)
                {
                    const int symbol = DecodeSymbol(reader, codeTable);
                    if (symbol < 0)
                    {
                        error = "Deflate 码长解码失败";
                        return false;
                    }
                    if (symbol < 16)
                    {
                        lengths[i++] = static_cast<std::uint8_t>(symbol);
                    }
                    else
                    {
                        int repeat = 0;
                        std::uint8_t value = 0;
                        if (symbol == 16)
                        {
                            if (i == 0)
                            {
                                error = "Deflate 重复码在前";
                                return false;
                            }
                            value = lengths[i - 1];
                            repeat = 3 + reader.Bits(2);
                        }
                        else if (symbol == 17)
                        {
                            repeat = 3 + reader.Bits(3);
                        }
                        else
                        {
                            repeat = 11 + reader.Bits(7);
                        }
                        if (i + repeat > literalCount + distanceCount)
                        {
                            error = "Deflate 码长重复越界";
                            return false;
                        }
                        for (int k = 0; k < repeat; ++k)
                        {
                            lengths[i++] = value;
                        }
                    }
                }
                if (!BuildHuffman(literal, lengths.data(), literalCount) ||
                    !BuildHuffman(distance, lengths.data() + literalCount, distanceCount))
                {
                    error = "Deflate 动态 Huffman 表非法";
                    return false;
                }
            }

            for (;;)
            {
                const int symbol = DecodeSymbol(reader, literal);
                if (symbol < 0)
                {
                    error = "Deflate 字面量解码失败";
                    return false;
                }
                if (symbol < 256)
                {
                    out.push_back(static_cast<std::uint8_t>(symbol));
                }
                else if (symbol == 256)
                {
                    break;
                }
                else
                {
                    const int lengthIndex = symbol - 257;
                    if (lengthIndex >= 29)
                    {
                        error = "Deflate 长度码越界";
                        return false;
                    }
                    const int length = kLengthBase[lengthIndex] + reader.Bits(kLengthExtra[lengthIndex]);
                    const int distSymbol = DecodeSymbol(reader, distance);
                    if (distSymbol < 0 || distSymbol >= 30)
                    {
                        error = "Deflate 距离码非法";
                        return false;
                    }
                    const int dist = kDistBase[distSymbol] + reader.Bits(kDistExtra[distSymbol]);
                    if (reader.error || dist <= 0 || static_cast<std::size_t>(dist) > out.size())
                    {
                        error = "Deflate 距离越界";
                        return false;
                    }
                    const std::size_t from = out.size() - static_cast<std::size_t>(dist);
                    for (int i = 0; i < length; ++i)
                    {
                        out.push_back(out[from + static_cast<std::size_t>(i)]);
                    }
                }
            }
        }
        else
        {
            error = "Deflate 数据块类型非法";
            return false;
        }

        if (last != 0)
        {
            return true;
        }
    }
}

// ── CAB 结构 ──────────────────────────────────────────────────────────────
struct Folder
{
    std::uint32_t firstBlockOffset = 0;
    std::uint16_t blockCount = 0;
    std::uint16_t compression = 0;
};

struct CabFile
{
    std::vector<std::uint8_t> bytes;
    std::uint32_t coffFiles = 0;
    std::uint16_t folderCount = 0;
    std::uint16_t fileCount = 0;
    std::uint8_t reserveHeaderBytes = 0;
    std::uint8_t reserveFolderBytes = 0;
    std::uint8_t reserveDataBytes = 0;
    std::vector<Folder> folders;
    std::vector<CabEntry> entries;
    CabInfo info;
};

bool LoadCab(const std::wstring& path, CabFile& cab, std::string& error)
{
    const std::string shown = WideToUtf8(path);
    std::string readError;
    if (!ReadFileBytes(path, cab.bytes, readError))
    {
        error = readError.empty() ? ("读不了：" + shown) : readError;
        return false;
    }
    if (cab.bytes.size() < 36)
    {
        error = "不是 CAB（文件太小）：" + shown;
        return false;
    }
    const std::uint8_t* base = cab.bytes.data();
    const std::size_t size = cab.bytes.size();
    if (std::memcmp(base, "MSCF", 4) != 0)
    {
        error = "不是 CAB（签名不是 MSCF）：" + shown;
        return false;
    }

    cab.info.bytes = ReadU32(base + 8);
    cab.coffFiles = ReadU32(base + 16);
    const std::uint8_t versionMinor = base[24];
    const std::uint8_t versionMajor = base[25];
    cab.folderCount = ReadU16(base + 26);
    cab.fileCount = ReadU16(base + 28);
    const std::uint16_t flags = ReadU16(base + 30);

    std::size_t pos = 36;
    if ((flags & 0x0004) != 0) // cfheadRESERVE_PRESENT
    {
        if (pos + 4 > size)
        {
            error = "CAB header 保留区截断：" + shown;
            return false;
        }
        const std::uint16_t reserveHeader = ReadU16(base + pos);
        cab.reserveHeaderBytes = base[pos + 2];
        cab.reserveFolderBytes = base[pos + 3];
        cab.reserveDataBytes = 0;
        if (pos + 4 + reserveHeader <= size)
        {
            cab.reserveDataBytes = 0;
        }
        pos += 4 + reserveHeader;
    }
    if (versionMajor != 1 || versionMinor != 3)
    {
        // 老格式（major 1 minor 1）也是同样布局，这里只在明显不正确时提示。
        if (versionMajor != 1)
        {
            error = "不支持的 CAB 版本：" + std::to_string(versionMajor) + "." + std::to_string(versionMinor);
            return false;
        }
    }

    if (pos + static_cast<std::size_t>(cab.folderCount) * 8 > size)
    {
        error = "CFFOLDER 截断：" + shown;
        return false;
    }
    for (std::uint16_t i = 0; i < cab.folderCount; ++i)
    {
        Folder folder;
        folder.firstBlockOffset = ReadU32(base + pos);
        folder.blockCount = ReadU16(base + pos + 4);
        folder.compression = static_cast<std::uint16_t>(ReadU16(base + pos + 6) & 0x000F);
        cab.folders.push_back(folder);
        pos += 8 + cab.reserveFolderBytes;
    }

    // CFFILE
    pos = cab.coffFiles;
    for (std::uint16_t i = 0; i < cab.fileCount; ++i)
    {
        if (pos + 16 > size)
        {
            error = "CFFILE 截断：" + shown;
            return false;
        }
        CabEntry entry;
        entry.size = ReadU32(base + pos);
        entry.offset = ReadU32(base + pos + 4);
        entry.folder = ReadU16(base + pos + 8);
        entry.date = ReadU16(base + pos + 10);
        entry.time = ReadU16(base + pos + 12);
        entry.attribs = ReadU16(base + pos + 14);
        pos += 16;
        const std::size_t begin = pos;
        while (pos < size && base[pos] != 0)
        {
            ++pos;
        }
        if (pos >= size)
        {
            error = "CFFILE 名字未终止：" + shown;
            return false;
        }
        entry.name = AsciiToUtf8(reinterpret_cast<const char*>(base + begin), pos - begin);
        ++pos;
        cab.entries.push_back(entry);
        cab.info.totalSize += entry.size;
    }

    cab.info.folderCount = cab.folderCount;
    cab.info.files = cab.entries;
    cab.info.compression = CabCompression::Unknown;
    for (const Folder& folder : cab.folders)
    {
        switch (folder.compression)
        {
        case 0: cab.info.compression = CabCompression::None; break;
        case 1: cab.info.compression = CabCompression::MSZip; break;
        case 2: cab.info.compression = CabCompression::Quantum; break;
        case 3: cab.info.compression = CabCompression::LZX; break;
        default: cab.info.compression = CabCompression::Unknown; break;
        }
        cab.info.blockCount += folder.blockCount;
    }
    return true;
}

// 把一个 folder 的全部 CFDATA 块解成连续数据。
bool ExpandFolder(const CabFile& cab, std::uint16_t folderIndex, std::vector<std::uint8_t>& out,
                  std::string& error)
{
    if (folderIndex >= cab.folders.size())
    {
        error = "folder 下标越界";
        return false;
    }
    const Folder& folder = cab.folders[folderIndex];
    const std::uint8_t* base = cab.bytes.data();
    const std::size_t size = cab.bytes.size();

    if (folder.compression != 0 && folder.compression != 1)
    {
        error = std::string("不支持的 CAB 压缩法（") +
                (folder.compression == 2 ? "Quantum" : (folder.compression == 3 ? "LZX" : "未知")) +
                "）：本工具目前只解 NONE / MSZIP";
        return false;
    }

    out.clear();
    std::size_t pos = folder.firstBlockOffset;
    for (std::uint16_t block = 0; block < folder.blockCount; ++block)
    {
        if (pos + 8 > size)
        {
            error = "CFDATA 头截断";
            return false;
        }
        const std::uint16_t compressedSize = ReadU16(base + pos + 4);
        const std::uint16_t uncompressedSize = ReadU16(base + pos + 6);
        pos += 8;
        if (pos + compressedSize > size)
        {
            error = "CFDATA 数据越界";
            return false;
        }
        const std::uint8_t* data = base + pos;
        if (folder.compression == 0)
        {
            if (compressedSize != uncompressedSize)
            {
                error = "CAB 未压缩块长度不一致";
                return false;
            }
            out.insert(out.end(), data, data + compressedSize);
        }
        else
        {
            if (compressedSize < 2 || data[0] != 'C' || data[1] != 'K')
            {
                error = "MSZIP 块缺少 CK 标记";
                return false;
            }
            // 字典 = 目前输出内容的最后 32KB（第一块就是空字典/零）。
            const std::size_t window = 32768;
            std::vector<std::uint8_t> dictionary;
            const std::size_t take = out.size() < window ? out.size() : window;
            dictionary.assign(out.end() - static_cast<std::ptrdiff_t>(take), out.end());
            std::vector<std::uint8_t> merged = dictionary;
            const std::size_t dictionarySize = merged.size();
            if (!InflateRaw(data + 2, compressedSize - 2, merged, error))
            {
                error = "MSZIP 解压失败：" + error;
                return false;
            }
            const std::size_t produced = merged.size() - dictionarySize;
            if (produced != uncompressedSize)
            {
                error = "MSZIP 块长度与声明不符";
                return false;
            }
            out.insert(out.end(), merged.begin() + static_cast<std::ptrdiff_t>(dictionarySize), merged.end());
        }
        pos += compressedSize + cab.reserveDataBytes;
    }
    return true;
}

} // namespace

const char* CabCompressionName(CabCompression compression)
{
    switch (compression)
    {
    case CabCompression::None: return "NONE";
    case CabCompression::MSZip: return "MSZIP";
    case CabCompression::LZX: return "LZX";
    case CabCompression::Quantum: return "Quantum";
    default: return "未知";
    }
}

bool CabList(const std::wstring& path, CabInfo& info, std::string& error)
{
    error.clear();
    CabFile cab;
    if (!LoadCab(path, cab, error))
    {
        return false;
    }
    info = cab.info;
    return true;
}

bool CabExtractEntry(const std::wstring& path, const std::string& entryName, std::vector<std::uint8_t>& out,
                     std::string& error)
{
    error.clear();
    out.clear();
    CabFile cab;
    if (!LoadCab(path, cab, error))
    {
        return false;
    }

    const CabEntry* found = nullptr;
    for (const CabEntry& entry : cab.entries)
    {
        if (EqualsNoCase(entry.name, entryName))
        {
            found = &entry;
            break;
        }
    }
    if (found == nullptr)
    {
        error = "CAB 里没有这个条目：" + entryName;
        return false;
    }

    std::vector<std::uint8_t> folderData;
    if (!ExpandFolder(cab, found->folder, folderData, error))
    {
        return false;
    }
    if (static_cast<std::size_t>(found->offset) + found->size > folderData.size())
    {
        error = "条目超出 folder 数据范围：" + entryName;
        return false;
    }
    out.assign(folderData.begin() + found->offset, folderData.begin() + found->offset + found->size);
    return true;
}

bool CabExtractText(const std::wstring& path, const std::string& entryName, std::string& text, std::string& error)
{
    std::vector<std::uint8_t> bytes;
    if (!CabExtractEntry(path, entryName, bytes, error))
    {
        return false;
    }
    text.assign(reinterpret_cast<const char*>(bytes.data()), bytes.size());
    // 去掉 UTF-8 BOM（清单文件常见）。
    if (text.size() >= 3 && static_cast<unsigned char>(text[0]) == 0xEF &&
        static_cast<unsigned char>(text[1]) == 0xBB && static_cast<unsigned char>(text[2]) == 0xBF)
    {
        text.erase(0, 3);
    }
    return true;
}

bool CabExtractAll(const std::wstring& path, const std::wstring& targetDir, std::vector<std::string>& written,
                   std::string& error)
{
    error.clear();
    written.clear();
    CabFile cab;
    if (!LoadCab(path, cab, error))
    {
        return false;
    }

    // 按 folder 分组，避免同一个 folder 反复解压。
    std::vector<std::vector<std::uint8_t>> folderData(cab.folders.size());
    std::vector<bool> folderDone(cab.folders.size(), false);

    for (const CabEntry& entry : cab.entries)
    {
        if (entry.folder >= cab.folders.size())
        {
            error = "条目 folder 越界：" + entry.name;
            return false;
        }
        if (!folderDone[entry.folder])
        {
            if (!ExpandFolder(cab, entry.folder, folderData[entry.folder], error))
            {
                return false;
            }
            folderDone[entry.folder] = true;
        }
        const std::vector<std::uint8_t>& data = folderData[entry.folder];
        if (static_cast<std::size_t>(entry.offset) + entry.size > data.size())
        {
            error = "条目超出 folder 数据范围：" + entry.name;
            return false;
        }

        std::wstring target = targetDir;
        // CAB 内用反斜杠；这里统一成反斜杠拼接，不处理 ".."（CAB 里不应出现）。
        std::string normalized = entry.name;
        for (char& ch : normalized)
        {
            if (ch == '/')
            {
                ch = '\\';
            }
        }
        if (!target.empty() && target.back() != L'\\' && target.back() != L'/')
        {
            target.push_back(L'\\');
        }
        target += Utf8ToWide(normalized);

        const std::size_t slash = target.find_last_of(L"\\/");
        if (slash != std::wstring::npos)
        {
            const std::wstring dir = target.substr(0, slash);
            if (!EnsureDirectory(dir, error))
            {
                return false;
            }
        }
        if (!WriteFileBytes(target, data.data() + entry.offset, entry.size, error))
        {
            return false;
        }
        written.push_back(entry.name);
    }
    return true;
}

} // namespace imaging
} // namespace ocid
