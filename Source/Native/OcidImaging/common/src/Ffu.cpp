// OCID Imaging / Ffu —— FFU 容器的读与写（见 Ffu.h 的说明）。
#include "ocid/imaging/Ffu.h"

#include "ocid/imaging/Common.h"
#include "ocid/imaging/Vhd.h"

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <string>
#include <vector>

#include <winioctl.h>
#include <bcrypt.h>

namespace ocid
{
namespace imaging
{
namespace
{
    const unsigned int kFfuChunkKb = 128;
    const unsigned int kFfuChunkBytes = kFfuChunkKb * 1024;
    const char* const kFfuSecuritySignature = "SignedImage ";
    const char* const kFfuImageSignature = "ImageFlash  ";
    const std::uint32_t kFfuHashSha256 = 0x800C;

    // ── payload 哈希（对齐 imagecommon 的 SecurityWrapper）────────────────
    // ADK 的 ImageSigner/SecurityWrapper：把 security header 之后的所有字节（store 元数据 +
    // 描述符块 + 数据块）按 imageHeader.ChunkSize（128 KiB）切块，每块算一次 SHA256，
    // 依次拼成哈希表；哈希表长度 = 块数 × 32。CatalogSize=0 表示未签名（没微软证书签不了）。
    class ChunkHasher
    {
    public:
        ~ChunkHasher()
        {
            if (hash_ != nullptr) BCryptDestroyHash(hash_);
            if (alg_ != nullptr) BCryptCloseAlgorithmProvider(alg_, 0);
        }

        bool Init(std::uint64_t expectedBytes, std::string& error)
        {
            expected_ = expectedBytes;
            buffer_.assign(kFfuChunkBytes, 0);
            used_ = 0;
            if (BCryptOpenAlgorithmProvider(&alg_, BCRYPT_SHA256_ALGORITHM, nullptr, 0) != 0)
            {
                error = "BCryptOpenAlgorithmProvider(SHA256) 失败";
                return false;
            }
            return true;
        }

        bool Update(const unsigned char* data, std::size_t size, std::string& error)
        {
            while (size > 0)
            {
                const std::size_t room = kFfuChunkBytes - used_;
                const std::size_t take = (size < room) ? size : room;
                std::memcpy(&buffer_[used_], data, take);
                used_ += take;
                data += take;
                size -= take;
                total_ += take;
                if (used_ == kFfuChunkBytes && !HashCurrentChunk(error)) return false;
            }
            return true;
        }

        bool Finish(std::vector<unsigned char>& table, std::string& error)
        {
            if (used_ != 0)
            {
                error = "payload 大小不是 " + std::to_string(kFfuChunkKb) + " KiB 的整数倍（余 " +
                        std::to_string(used_) + " 字节）";
                return false;
            }
            if (total_ != expected_)
            {
                error = "哈希字节数 " + std::to_string(total_) + " 与 payload 大小 " +
                        std::to_string(expected_) + " 不一致";
                return false;
            }
            table = table_;
            return true;
        }

    private:
        bool HashCurrentChunk(std::string& error)
        {
            if (BCryptCreateHash(alg_, &hash_, nullptr, 0, nullptr, 0, 0) != 0)
            {
                error = "BCryptCreateHash 失败";
                return false;
            }
            if (BCryptHashData(hash_, &buffer_[0], static_cast<unsigned long>(kFfuChunkBytes), 0) != 0)
            {
                error = "BCryptHashData 失败";
                return false;
            }
            unsigned char digest[32] = {0};
            if (BCryptFinishHash(hash_, digest, 32, 0) != 0)
            {
                error = "BCryptFinishHash 失败";
                return false;
            }
            BCryptDestroyHash(hash_);
            hash_ = nullptr;
            table_.insert(table_.end(), digest, digest + 32);
            used_ = 0;
            return true;
        }

        BCRYPT_ALG_HANDLE alg_ = nullptr;
        BCRYPT_HASH_HANDLE hash_ = nullptr;
        std::vector<unsigned char> buffer_;
        std::vector<unsigned char> table_;
        std::size_t used_ = 0;
        std::uint64_t total_ = 0;
        std::uint64_t expected_ = 0;
    };

    // ── 小工具 ─────────────────────────────────────────────────────────
    void PackU16(std::vector<unsigned char>& buffer, unsigned int value)
    {
        buffer.push_back(static_cast<unsigned char>(value & 0xFF));
        buffer.push_back(static_cast<unsigned char>((value >> 8) & 0xFF));
    }

    void PackU32(std::vector<unsigned char>& buffer, std::uint32_t value)
    {
        for (int i = 0; i < 4; ++i) buffer.push_back(static_cast<unsigned char>((value >> (8 * i)) & 0xFF));
    }

    void PackU64(std::vector<unsigned char>& buffer, std::uint64_t value)
    {
        for (int i = 0; i < 8; ++i) buffer.push_back(static_cast<unsigned char>((value >> (8 * i)) & 0xFF));
    }

    std::uint32_t ReadU32At(const std::vector<unsigned char>& buffer, std::size_t offset)
    {
        if (offset + 4 > buffer.size()) return 0;
        return static_cast<std::uint32_t>(buffer[offset]) |
               (static_cast<std::uint32_t>(buffer[offset + 1]) << 8) |
               (static_cast<std::uint32_t>(buffer[offset + 2]) << 16) |
               (static_cast<std::uint32_t>(buffer[offset + 3]) << 24);
    }

    std::string UpperAscii(const std::string& text)
    {
        std::string out = text;
        for (std::size_t i = 0; i < out.size(); ++i)
            if (out[i] >= 'a' && out[i] <= 'z') out[i] = static_cast<char>(out[i] - 'a' + 'A');
        return out;
    }

    std::string BoolText(bool value)
    {
        return value ? "True" : "False";
    }

    std::string HumanBytes(unsigned long long bytes)
    {
        char text[64];
        if (bytes >= 1024ull * 1024 * 1024)
            std::snprintf(text, sizeof(text), "%.1f GB", static_cast<double>(bytes) / (1024.0 * 1024.0 * 1024.0));
        else if (bytes >= 1024ull * 1024)
            std::snprintf(text, sizeof(text), "%.1f MB", static_cast<double>(bytes) / (1024.0 * 1024.0));
        else if (bytes >= 1024)
            std::snprintf(text, sizeof(text), "%.1f KB", static_cast<double>(bytes) / 1024.0);
        else
            std::snprintf(text, sizeof(text), "%llu B", bytes);
        return text;
    }

    std::string TrimSpaceAscii(const std::string& text)
    {
        std::size_t begin = 0;
        std::size_t end = text.size();
        while (begin < end && (text[begin] == ' ' || text[begin] == '\t' || text[begin] == '\r' || text[begin] == '\n'))
            ++begin;
        while (end > begin && (text[end - 1] == ' ' || text[end - 1] == '\t' || text[end - 1] == '\r' || text[end - 1] == '\n'))
            --end;
        return text.substr(begin, end - begin);
    }

    bool BlockIsZero(const std::vector<unsigned char>& buffer)
    {
        for (std::size_t i = 0; i < buffer.size(); ++i)
            if (buffer[i] != 0) return false;
        return true;
    }

    // ── 盘设备读写 ─────────────────────────────────────────────────────
    bool OpenDevice(const std::string& device, HANDLE& handle, std::string& error)
    {
        handle = CreateFileW(Utf8ToWide(device).c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                             nullptr, OPEN_EXISTING, 0, nullptr);
        if (handle == INVALID_HANDLE_VALUE)
        {
            error = "打开设备失败（win32=" + std::to_string(GetLastError()) + "）：" + device;
            return false;
        }
        return true;
    }

    bool DeviceBytes(HANDLE handle, unsigned long long& bytes, std::string& error)
    {
        GET_LENGTH_INFORMATION info;
        DWORD returned = 0;
        if (DeviceIoControl(handle, IOCTL_DISK_GET_LENGTH_INFO, nullptr, 0, &info, sizeof(info), &returned, nullptr))
        {
            bytes = static_cast<unsigned long long>(info.Length.QuadPart);
            return true;
        }
        LARGE_INTEGER fileSize;
        if (!GetFileSizeEx(handle, &fileSize))
        {
            error = "取磁盘大小失败（IOCTL_DISK_GET_LENGTH_INFO / GetFileSizeEx 都失败）";
            return false;
        }
        bytes = static_cast<unsigned long long>(fileSize.QuadPart);
        return true;
    }

    // 读一个 payload 块。盘设备要求扇区对齐读，所以盘尾不足一块时按 512 对齐读入、其余零填充。
    bool ReadBlock(HANDLE handle, unsigned long long diskBytes, unsigned long long blockIndex,
                   unsigned int bytesPerBlock, std::vector<unsigned char>& buffer, std::string& error)
    {
        buffer.assign(bytesPerBlock, 0);
        const unsigned long long offset = blockIndex * static_cast<unsigned long long>(bytesPerBlock);
        if (offset >= diskBytes) return true;
        unsigned long long room = diskBytes - offset;
        if (room > bytesPerBlock) room = bytesPerBlock;
        const unsigned int want = static_cast<unsigned int>(room & ~static_cast<unsigned long long>(511));
        if (want == 0) return true;
        LARGE_INTEGER position;
        position.QuadPart = static_cast<LONGLONG>(offset);
        if (!SetFilePointerEx(handle, position, nullptr, FILE_BEGIN))
        {
            error = "SetFilePointerEx 失败（块 " + std::to_string(blockIndex) + "）";
            return false;
        }
        DWORD read = 0;
        if (!ReadFile(handle, buffer.data(), want, &read, nullptr) || read != want)
        {
            error = "读块 " + std::to_string(blockIndex) + " 失败（win32=" + std::to_string(GetLastError()) + "）";
            return false;
        }
        return true;
    }

    // ── manifest 生成 ─────────────────────────────────────────────────
    void AppendPartition(std::string& manifest, const char* category, const FfuPartitionSpec& part)
    {
        manifest += "[";
        manifest += category;
        manifest += "]\r\n";
        if (part.byteAlignment != 0) manifest += "ByteAlignment      = " + std::to_string(part.byteAlignment) + "\r\n";
        if (part.clusterSize != 0) manifest += "ClusterSize        = " + std::to_string(part.clusterSize) + "\r\n";
        if (!part.fileSystem.empty()) manifest += "FileSystem         = " + part.fileSystem + "\r\n";
        if (!part.id.empty()) manifest += "Id                 = " + part.id + "\r\n";
        manifest += "Name               = " + part.name + "\r\n";
        manifest += "Primary            = " + (part.primary.empty() ? part.name : part.primary) + "\r\n";
        manifest += "Type               = " + part.type + "\r\n";
        // FullFlashUpdateStore.AddPartition：TotalSectors 与 UseAllSpace 二者只能出现一个，
        // 同时出现会抛 "cannot contain both a 'TotalSectors' and a 'UseAllSpace' key/value pair"。
        if (part.useAllSpace)
        {
            manifest += "UseAllSpace        = True\r\n\r\n";
        }
        else
        {
            if (part.totalSectors != 0) manifest += "TotalSectors       = " + std::to_string(part.totalSectors) + "\r\n";
            if (part.totalSectors != 0) manifest += "UsedSectors        = " + std::to_string(part.usedSectors) + "\r\n";
            manifest += "\r\n";
        }
    }

    // FullFlashUpdateStore.Fixup 要求 MinSectorCount*SectorSize 是 image block size 的整数倍
    // （否则抛 "The image size, specified by MinSectorCount, needs to be a multiple of N sectors"）。
    unsigned long long AlignSectors(unsigned long long sectors, unsigned int bytesPerBlock, unsigned long long sectorSize)
    {
        if (sectorSize == 0) return sectors;
        const unsigned long long perBlock = bytesPerBlock / sectorSize;
        if (perBlock == 0) return sectors;
        return ((sectors + perBlock - 1) / perBlock) * perBlock;
    }

    void AppendStore(std::string& manifest, const char* category, const char* spaceCategory, const FfuStoreSpec& store,
                     unsigned long long minSectorCount)
    {
        manifest += "[";
        manifest += category;
        manifest += "]\r\n";
        if (!store.devicePath.empty()) manifest += "DevicePath                    = " + store.devicePath + "\r\n";
        manifest += "IsMainOSStore                 = " + BoolText(store.isMainOSStore) + "\r\n";
        if (spaceCategory != nullptr) manifest += "IsSpace                       = " + BoolText(store.isSpace) + "\r\n";
        manifest += "MinSectorCount                = " + std::to_string(minSectorCount) + "\r\n";
        manifest += "OnlyAllocateDefinedGptEntries = " + BoolText(store.onlyAllocateDefinedGptEntries) + "\r\n";
        manifest += "SectorSize                    = " + std::to_string(store.sectorSize) + "\r\n";
        manifest += "StoreId                       = " + store.storeId + "\r\n";
        manifest += "StoreType                     = " + store.storeType + "\r\n\r\n";
    }

    // ── 读端：头部 + manifest ──────────────────────────────────────────
    bool ParseImage(const std::wstring& path, FfuImageInfo& info, std::string& manifest, std::string& error)
    {
        manifest.clear();
        std::ifstream in(path.c_str(), std::ios::binary);
        if (!in)
        {
            error = "打开 FFU 失败：" + WideToUtf8(path);
            return false;
        }
        in.seekg(0, std::ios::end);
        info.fileBytes = static_cast<std::uint64_t>(in.tellg());
        in.seekg(0, std::ios::beg);
        std::vector<unsigned char> head(4u * 1024u * 1024u, 0);
        in.read(reinterpret_cast<char*>(head.data()), static_cast<std::streamsize>(head.size()));
        const std::size_t got = static_cast<std::size_t>(in.gcount());
        head.resize(got);

        if (got < 0x20 || std::memcmp(head.data() + 4, kFfuSecuritySignature, 12) != 0)
        {
            error = "不是 FFU（缺少 SignedImage 签名）：" + WideToUtf8(path);
            return false;
        }
        info.securityHeaderBytes = ReadU32At(head, 0);
        info.securityChunkKb = ReadU32At(head, 0x10);
        info.hashAlgorithmId = ReadU32At(head, 0x14);
        info.catalogBytes = ReadU32At(head, 0x18);
        info.hashTableBytes = ReadU32At(head, 0x1C);

        std::size_t imageBase = 0;
        for (std::size_t i = 4; i + 12 <= head.size(); ++i)
        {
            if (std::memcmp(head.data() + i, kFfuImageSignature, 12) == 0)
            {
                imageBase = i - 4;
                break;
            }
        }
        if (imageBase == 0)
        {
            error = "FFU 里找不到 ImageFlash 镜像头";
            return false;
        }
        info.imageHeaderBytes = ReadU32At(head, imageBase);
        info.manifestBytes = ReadU32At(head, imageBase + 0x10);
        info.imageChunkKb = ReadU32At(head, imageBase + 0x14);
        const std::size_t manifestOffset = imageBase + 0x18;
        if (manifestOffset + info.manifestBytes > head.size())
        {
            error = "manifest 超出读取窗口（ManifestLength 过大）";
            return false;
        }
        info.manifestOffset = manifestOffset;
        manifest.assign(reinterpret_cast<const char*>(head.data() + manifestOffset), info.manifestBytes);

        std::uint64_t payloadOffset = manifestOffset + info.manifestBytes;
        const std::uint64_t unit = static_cast<std::uint64_t>(info.imageChunkKb ? info.imageChunkKb : info.securityChunkKb) * 1024ull;
        if (unit > 0) payloadOffset = (payloadOffset + unit - 1) / unit * unit;
        info.payloadOffset = payloadOffset;

        // 逐段统计（INI：[Section] + key=value）
        std::size_t pos = 0;
        std::string section;
        std::size_t keys = 0;
        while (pos <= manifest.size())
        {
            const std::size_t lineEnd = manifest.find('\n', pos);
            std::string raw = manifest.substr(pos, lineEnd == std::string::npos ? std::string::npos : lineEnd - pos);
            pos = (lineEnd == std::string::npos) ? manifest.size() + 1 : lineEnd + 1;
            const std::string trimmed = TrimSpaceAscii(raw);
            if (trimmed.empty() || trimmed[0] == ';') continue;
            if (trimmed.front() == '[' && trimmed.back() == ']')
            {
                if (!section.empty()) info.sections.push_back(std::make_pair(section, keys));
                section = trimmed.substr(1, trimmed.size() - 2);
                keys = 0;
                continue;
            }
            if (!section.empty() && trimmed.find('=') != std::string::npos) ++keys;
        }
        if (!section.empty()) info.sections.push_back(std::make_pair(section, keys));
        return true;
    }
}   // namespace

bool FfuReadManifest(const std::wstring& path, std::string& manifest, std::string& error)
{
    FfuImageInfo info;
    return ParseImage(path, info, manifest, error);
}

bool FfuReadInfo(const std::wstring& path, FfuImageInfo& info, std::string& error)
{
    std::string manifest;
    return ParseImage(path, info, manifest, error);
}

bool FfuVerify(const std::wstring& path, std::vector<std::string>& report, std::string& error)
{
    FfuImageInfo info;
    std::string manifest;
    if (!ParseImage(path, info, manifest, error)) return false;

    const unsigned long long chunk = (info.imageChunkKb != 0 ? info.imageChunkKb : kFfuChunkKb) * 1024ull;
    if (info.hashTableBytes == 0)
    {
        report.push_back("镜像没有哈希表（HashTableSize = 0），无法校验（未签名镜像可以这样）");
        return true;
    }
    if (chunk == 0 || (info.hashTableBytes % 32u) != 0)
    {
        error = "哈希表大小 " + std::to_string(info.hashTableBytes) + " 不是 32 的整数倍";
        return false;
    }

    std::ifstream in(path.c_str(), std::ios::binary);
    if (!in)
    {
        error = "打开 FFU 失败：" + WideToUtf8(path);
        return false;
    }
    const unsigned long long tableOffset = static_cast<unsigned long long>(info.securityHeaderBytes) + info.catalogBytes;
    std::vector<unsigned char> table(info.hashTableBytes);
    in.seekg(static_cast<std::streamoff>(tableOffset), std::ios::beg);
    in.read(reinterpret_cast<char*>(&table[0]), static_cast<std::streamsize>(table.size()));
    if (in.gcount() != static_cast<std::streamsize>(table.size()))
    {
        error = "读哈希表失败（文件被截断？）";
        return false;
    }
    in.seekg(static_cast<std::streamoff>(info.payloadOffset), std::ios::beg);

    const unsigned long long chunkCount = info.hashTableBytes / 32ull;
    report.push_back("哈希表 " + std::to_string(chunkCount) + " 个块 × " + HumanBytes(chunk) + "（SHA256）");

    std::vector<unsigned char> buffer(static_cast<std::size_t>(chunk), 0);
    unsigned long long index = 0;
    unsigned long long bad = 0;
    BCRYPT_ALG_HANDLE alg = nullptr;
    if (BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, 0) != 0)
    {
        error = "BCryptOpenAlgorithmProvider(SHA256) 失败";
        return false;
    }
    bool ok = true;
    while (index < chunkCount)
    {
        in.read(reinterpret_cast<char*>(&buffer[0]), static_cast<std::streamsize>(buffer.size()));
        if (in.gcount() != static_cast<std::streamsize>(buffer.size()))
        {
            error = "payload 在第 " + std::to_string(index) + " 块处被截断";
            ok = false;
            break;
        }
        BCRYPT_HASH_HANDLE hash = nullptr;
        unsigned char digest[32] = {0};
        if (BCryptCreateHash(alg, &hash, nullptr, 0, nullptr, 0, 0) != 0 ||
            BCryptHashData(hash, &buffer[0], static_cast<unsigned long>(buffer.size()), 0) != 0 ||
            BCryptFinishHash(hash, digest, 32, 0) != 0)
        {
            if (hash != nullptr) BCryptDestroyHash(hash);
            error = "计算第 " + std::to_string(index) + " 块哈希失败";
            ok = false;
            break;
        }
        BCryptDestroyHash(hash);
        if (std::memcmp(digest, &table[static_cast<std::size_t>(index) * 32u], 32) != 0)
        {
            if (bad < 5) report.push_back("  第 " + std::to_string(index) + " 块哈希不一致");
            ++bad;
        }
        ++index;
    }
    BCryptCloseAlgorithmProvider(alg, 0);
    if (!ok) return false;
    report.push_back(bad == 0 ? "全部 " + std::to_string(index) + " 块哈希一致"
                              : std::to_string(bad) + " / " + std::to_string(index) + " 块哈希不一致");
    return bad == 0;
}

bool FfuPack(const FfuPackRequest& request, std::vector<std::string>& report, std::string& error)
{
    const unsigned int bytesPerBlock = request.bytesPerBlock ? request.bytesPerBlock : kFfuChunkBytes;

    // ① 挂载（如果给的是文件）；② 读真实 GPT —— manifest 的分区尺寸一律以盘上 GPT 为准
    const bool fromFile = request.image.size() < 4 || request.image.compare(0, 4, "\\\\.\\") != 0;
    std::string device = request.image;
    if (fromFile)
    {
        std::string attachError;
        std::string attached;
        if (!VhdAttach(Utf8ToWide(request.image), false, attached, attachError))
        {
            error = "挂载镜像失败：" + attachError;
            return false;
        }
        device = attached;
    }

    std::vector<std::string> detachReport;
    std::string detachError;
    const bool needDetach = fromFile;

    std::string diskId;
    std::vector<VhdPartition> physicalParts;
    std::uint32_t gptSector = 512;
    std::string gptError;
    if (!VhdReadGpt(device, diskId, physicalParts, gptSector, gptError))
    {
        error = "读物理 GPT 失败：" + gptError;
        if (needDetach) VhdForceDetach(Utf8ToWide(request.image), false, detachReport, detachError);
        return false;
    }

    HANDLE diskHandle = INVALID_HANDLE_VALUE;
    std::string openError;
    if (!OpenDevice(device, diskHandle, openError))
    {
        error = openError;
        if (needDetach) VhdForceDetach(Utf8ToWide(request.image), false, detachReport, detachError);
        return false;
    }

    bool ok = false;
    unsigned long long diskBytes = 0;
    std::string stageError;
    do
    {
        if (!DeviceBytes(diskHandle, diskBytes, stageError))
        {
            error = stageError;
            break;
        }
        const unsigned long long totalBlocks = (diskBytes + bytesPerBlock - 1) / bytesPerBlock;
        report.push_back("磁盘大小 " + HumanBytes(diskBytes) + "（" + std::to_string(diskBytes / 512ull) +
                         " × 512B 扇区）");
        report.push_back("payload 块 " + HumanBytes(bytesPerBlock) + " × " + std::to_string(totalBlocks));

        // 整盘扫描：block 0 单独进 Phase2，其余非零块进 Phase3
        std::vector<unsigned long long> dataBlocks;
        {
            std::vector<unsigned char> buffer;
            for (unsigned long long i = 1; i < totalBlocks; ++i)
            {
                std::string readError;
                if (!ReadBlock(diskHandle, diskBytes, i, bytesPerBlock, buffer, readError))
                {
                    stageError = "扫描磁盘失败：" + readError;
                    break;
                }
                if (!BlockIsZero(buffer)) dataBlocks.push_back(i);
                if ((i % 8192ull) == 0)
                    report.push_back("  已扫描 " + std::to_string(i) + "/" + std::to_string(totalBlocks) +
                                     " 块，保留 " + std::to_string(dataBlocks.size()));
            }
            if (!stageError.empty()) break;
        }
        if (totalBlocks > 1 && (dataBlocks.empty() || dataBlocks.back() != totalBlocks - 1))
            dataBlocks.push_back(totalBlocks - 1);   // 盘尾块里有备份 GPT，必须带上
        report.push_back("数据块 " + std::to_string(dataBlocks.size()) + "（不含 Phase2 的 block 0）");

        // 每个物理分区的 UsedSectors = 落在该分区里的数据块字节数
        std::vector<unsigned long long> usedSectors(physicalParts.size(), 0);
        for (std::size_t pi = 0; pi < physicalParts.size(); ++pi)
        {
            const VhdPartition& part = physicalParts[pi];
            unsigned long long kept = 0;
            for (std::size_t k = 0; k < dataBlocks.size(); ++k)
            {
                const unsigned long long offset = dataBlocks[k] * static_cast<unsigned long long>(bytesPerBlock);
                if (offset >= part.offsetBytes && offset < part.offsetBytes + part.sizeBytes) ++kept;
            }
            if (0ull >= part.offsetBytes && 0ull < part.offsetBytes + part.sizeBytes) ++kept;   // block 0
            usedSectors[pi] = kept * (bytesPerBlock / 512ull);
            report.push_back("  分区 " + part.name + " " + HumanBytes(part.sizeBytes) + "，载荷 " +
                             HumanBytes(kept * static_cast<unsigned long long>(bytesPerBlock)));
        }

        // ── manifest ──
        std::string manifest;
        manifest += "[FullFlash]\r\nAntiTheftVersion     = 1.1\r\nArchitecture         = " + UpperAscii(request.arch) +
                    "\r\nDescription          = " + request.description + "\r\n\r\nDevicePlatformId0    = *\r\nOSVersion            = " +
                    request.osVersion + "\r\nReleaseType          = Test\r\nStateSeparationLevel = " +
                    std::to_string(request.stateSeparationLevel) + "\r\nUEFI                 = True\r\nVersion              = 2.0\r\n\r\n";

        if (!request.spaces.empty())
        {
            manifest += "[StoragePool]\r\nName = " + request.poolName + "\r\n\r\n";
            for (std::size_t i = 0; i < request.spaces.size(); ++i)
            {
                const FfuStoreSpec& space = request.spaces[i];
                unsigned long long spaceSectors = space.minSectorCount;
                if (spaceSectors == 0 && space.sectorSize != 0) spaceSectors = space.sizeBytes / space.sectorSize;
                spaceSectors = AlignSectors(spaceSectors, bytesPerBlock, space.sectorSize);
                AppendStore(manifest, "StoragePoolStore", "space", space, spaceSectors);
                for (std::size_t p = 0; p < space.partitions.size(); ++p)
                    AppendPartition(manifest, "StoragePoolPartition", space.partitions[p]);
            }
        }

        FfuStoreSpec physical = request.physicalStore;
        if (physical.storeId.empty()) physical.storeId = diskId;
        if (physical.storeType.empty()) physical.storeType = "Default";
        if (physical.sectorSize == 0) physical.sectorSize = gptSector ? gptSector : 512;
        physical.minSectorCount = diskBytes / physical.sectorSize;
        physical.isMainOSStore = false;
        physical.partitions.clear();
        for (std::size_t pi = 0; pi < physicalParts.size(); ++pi)
        {
            const VhdPartition& part = physicalParts[pi];
            FfuPartitionSpec spec;
            spec.name = part.name;
            spec.primary = part.name;
            spec.type = part.type;
            spec.id = part.id;
            spec.totalSectors = part.sizeBytes / 512ull;
            spec.usedSectors = usedSectors[pi];
            // ImageConstants.STORAGE_POOL_PARTITION_GUID —— ADK 的 FullFlashUpdatePartition.IsStoragePool
            // 只认这一个值：物理 store 里承载池的那个分区必须写成它（官方 Pro7 manifest 里 OSPool 就是
            // {5708A6E0-9001-4b99-b064-1fe564896bdb}，而盘上 GPT 里是微软 19H1 池类型 F75CAF8F）。
            // 同时照抄官方：池分区只写 TotalSectors=32、UsedSectors=0、不带 FileSystem / Id。
            const std::string poolPartitionType = "{5708A6E0-9001-4b99-b064-1fe564896bdb}";
            if (UpperAscii(part.type) == "{F75CAF8F-F680-4CEE-AFA3-B001E56EFC2D}" || part.name == request.poolName)
            {
                spec.name = part.name;
                spec.primary = part.name;
                spec.type = poolPartitionType;
                spec.id.clear();
                spec.fileSystem.clear();
                spec.clusterSize = 0;
                spec.byteAlignment = 0;
                spec.totalSectors = 32;
                spec.usedSectors = 0;
                physical.partitions.push_back(spec);
                continue;
            }
            // 布局里的 FileSystem / ClusterSize / ByteAlignment 作为提示补上
            for (std::size_t k = 0; k < request.physicalStore.partitions.size(); ++k)
            {
                const FfuPartitionSpec& hint = request.physicalStore.partitions[k];
                if (!hint.name.empty() && hint.name == part.name)
                {
                    spec.fileSystem = hint.fileSystem;
                    spec.clusterSize = hint.clusterSize;
                    spec.byteAlignment = hint.byteAlignment;
                    break;
                }
            }
            physical.partitions.push_back(spec);
        }
        const unsigned long long physicalSectors = AlignSectors(diskBytes / physical.sectorSize, bytesPerBlock, physical.sectorSize);
        if (physicalSectors != diskBytes / physical.sectorSize)
            report.push_back("  盘大小不是 payload 块整数倍：" + std::to_string(diskBytes / physical.sectorSize) + " → " +
                             std::to_string(physicalSectors) + " 扇区（ImgDump 的 Fixup 要求按块对齐）");
        AppendStore(manifest, "Store", nullptr, physical, physicalSectors);
        for (std::size_t pi = 0; pi < physical.partitions.size(); ++pi)
            AppendPartition(manifest, "Partition", physical.partitions[pi]);

        // ── 先造 store 元数据：payload 大小 → 哈希表大小 → 安全头 padding，都得先算出来 ──
        const unsigned int phase1Count = (bytesPerBlock <= 131072u) ? (131072u / bytesPerBlock) : 1u;
        const unsigned long long phase3Count = static_cast<unsigned long long>(dataBlocks.size());
        const unsigned long long entryCount = static_cast<unsigned long long>(phase1Count) + 1ull + phase3Count;
        const unsigned long long descriptorBytes =
            static_cast<unsigned long long>(phase1Count) * 24ull + 16ull + phase3Count * 16ull;
        const std::string devicePath = request.physicalStore.devicePath;

        std::vector<unsigned char> meta;
        PackU32(meta, 0);                       // UpdateType = FullUpdate
        PackU16(meta, 2);                       // MajorVersion
        PackU16(meta, 0);                       // MinorVersion
        PackU16(meta, 2);                       // FullFlash Major
        PackU16(meta, 0);                       // FullFlash Minor
        for (int i = 0; i < 192; ++i) meta.push_back(0);
        meta[12] = 0x2A;                        // PlatformIdentifier = '*'
        PackU32(meta, bytesPerBlock);
        PackU32(meta, static_cast<std::uint32_t>(entryCount));
        PackU32(meta, static_cast<std::uint32_t>(descriptorBytes));
        PackU32(meta, 0);                       // ValidationEntryCount
        PackU32(meta, 0);                       // ValidationDataSize
        PackU32(meta, 0);                       // InitialPartitionTableBlockIndex
        PackU32(meta, phase1Count);             // InitialPartitionTableBlockCount
        PackU32(meta, phase1Count);             // FlashOnlyPartitionTableBlockIndex
        PackU32(meta, 1);                       // FlashOnlyPartitionTableBlockCount
        PackU32(meta, phase1Count + 1);         // FinalPartitionTableBlockIndex
        PackU32(meta, static_cast<std::uint32_t>(phase3Count));   // FinalPartitionTableBlockCount
        PackU16(meta, 1);                       // NumberOfStores
        PackU16(meta, 1);                       // StoreIndex
        PackU64(meta, static_cast<std::uint64_t>(entryCount) * bytesPerBlock);   // StorePayloadSize
        PackU16(meta, static_cast<unsigned int>(devicePath.size()));
        for (std::size_t i = 0; i < devicePath.size(); ++i)
        {
            meta.push_back(static_cast<unsigned char>(devicePath[i]));
            meta.push_back(0);
        }
        for (unsigned int i = 0; i < phase1Count; ++i)   // Phase1：盘首 + 盘尾各一次零块
        {
            PackU32(meta, 2);
            PackU32(meta, 1);
            PackU32(meta, 0);
            PackU32(meta, i);
            PackU32(meta, 2);
            PackU32(meta, i);
        }
        PackU32(meta, 1);                       // Phase2：真实主 GPT 所在块
        PackU32(meta, 1);
        PackU32(meta, 0);
        PackU32(meta, 0);
        for (std::size_t k = 0; k < dataBlocks.size(); ++k)   // Phase3：其余非零块
        {
            PackU32(meta, 1);
            PackU32(meta, 1);
            PackU32(meta, 0);
            PackU32(meta, static_cast<std::uint32_t>(dataBlocks[k]));
        }
        while (meta.size() % bytesPerBlock != 0) meta.push_back(0);

        const unsigned long long blockBytes = static_cast<unsigned long long>(bytesPerBlock);
        const unsigned long long payloadSize = static_cast<unsigned long long>(meta.size()) +
            static_cast<unsigned long long>(phase1Count + 1) * blockBytes + phase3Count * blockBytes;
        if (payloadSize % kFfuChunkBytes != 0)
        {
            stageError = "payload 大小不是 " + std::to_string(kFfuChunkKb) + " KiB 的整数倍";
            break;
        }
        const unsigned long long hashCount = payloadSize / kFfuChunkBytes;
        const std::uint32_t hashTableBytes = static_cast<std::uint32_t>(hashCount * 32ull);
        const std::uint32_t securityPadding = static_cast<std::uint32_t>(
            (kFfuChunkBytes - ((32ull + hashTableBytes) % kFfuChunkBytes)) % kFfuChunkBytes);

        // ── 头部 + payload ──
        std::ofstream out(request.outPath.c_str(), std::ios::binary | std::ios::trunc);
        if (!out)
        {
            stageError = "创建 FFU 失败：" + WideToUtf8(request.outPath);
            break;
        }

        std::vector<unsigned char> header;
        PackU32(header, 32);
        header.insert(header.end(), kFfuSecuritySignature, kFfuSecuritySignature + 12);
        PackU32(header, kFfuChunkKb);           // 注意：该字段单位是 KB
        PackU32(header, kFfuHashSha256);
        PackU32(header, 0);                     // CatalogSize：未签名（没有微软证书，签不了）
        PackU32(header, hashTableBytes);        // HashTableSize
        header.insert(header.end(), static_cast<std::size_t>(hashTableBytes), 0);   // 哈希表：写完 payload 回填
        header.insert(header.end(), static_cast<std::size_t>(securityPadding), 0);  // SecurityPadding
        PackU32(header, 24);                    // ImageHeaderSize
        header.insert(header.end(), kFfuImageSignature, kFfuImageSignature + 12);
        PackU32(header, static_cast<std::uint32_t>(manifest.size()));
        PackU32(header, kFfuChunkKb);
        header.insert(header.end(), manifest.begin(), manifest.end());
        while (header.size() % kFfuChunkBytes != 0) header.push_back(0);
        out.write(reinterpret_cast<const char*>(header.data()), static_cast<std::streamsize>(header.size()));
        report.push_back("头部 " + HumanBytes(header.size()) + "（哈希表 " + std::to_string(hashCount) + " × 32 = " +
                         HumanBytes(hashTableBytes) + "，padding " + HumanBytes(securityPadding) + "，未签名）");

        ChunkHasher hasher;
        if (!hasher.Init(payloadSize, stageError)) break;

        out.write(reinterpret_cast<const char*>(&meta[0]), static_cast<std::streamsize>(meta.size()));
        if (!hasher.Update(&meta[0], meta.size(), stageError)) break;
        report.push_back("store 元数据 " + HumanBytes(meta.size()) + "（" + std::to_string(entryCount) + " 条描述符）");
        report.push_back("payload " + HumanBytes(payloadSize) + "（" + std::to_string(phase1Count + 1) + " + " +
                         std::to_string(phase3Count) + " 个 " + HumanBytes(bytesPerBlock) + " 块）");

        {
            const std::vector<unsigned char> zero(bytesPerBlock, 0);
            for (unsigned int i = 0; i < phase1Count; ++i)
            {
                out.write(reinterpret_cast<const char*>(&zero[0]), static_cast<std::streamsize>(zero.size()));
                if (!hasher.Update(&zero[0], zero.size(), stageError)) break;
            }
            if (!stageError.empty()) break;
            std::vector<unsigned char> block;
            std::string readError;
            if (!ReadBlock(diskHandle, diskBytes, 0, bytesPerBlock, block, readError))
            {
                stageError = "读 block 0 失败：" + readError;
                break;
            }
            out.write(reinterpret_cast<const char*>(&block[0]), static_cast<std::streamsize>(block.size()));
            if (!hasher.Update(&block[0], block.size(), stageError)) break;
            for (std::size_t k = 0; k < dataBlocks.size(); ++k)
            {
                if (!ReadBlock(diskHandle, diskBytes, dataBlocks[k], bytesPerBlock, block, readError))
                {
                    stageError = "读块 " + std::to_string(dataBlocks[k]) + " 失败：" + readError;
                    break;
                }
                out.write(reinterpret_cast<const char*>(&block[0]), static_cast<std::streamsize>(block.size()));
                if (!hasher.Update(&block[0], block.size(), stageError)) break;
            }
            if (!stageError.empty()) break;
        }

        std::vector<unsigned char> hashTable;
        if (!hasher.Finish(hashTable, stageError)) break;
        if (hashTable.size() != hashTableBytes)
        {
            stageError = "哈希表实际大小 " + std::to_string(hashTable.size()) + " 与头部声明 " +
                         std::to_string(hashTableBytes) + " 不一致";
            break;
        }
        out.seekp(32, std::ios::beg);
        out.write(reinterpret_cast<const char*>(&hashTable[0]), static_cast<std::streamsize>(hashTable.size()));
        out.close();
        if (!out)
        {
            stageError = "写 FFU 失败（磁盘满或路径不可写）：" + WideToUtf8(request.outPath);
            break;
        }
        ok = true;

    } while (false);

    CloseHandle(diskHandle);
    if (needDetach)
    {
        VhdForceDetach(Utf8ToWide(request.image), false, detachReport, detachError);
        for (std::size_t i = 0; i < detachReport.size(); ++i) report.push_back(detachReport[i]);
    }
    if (!ok)
    {
        error = stageError.empty() ? std::string("打包失败") : stageError;
        return false;
    }
    return true;
}
}
}
