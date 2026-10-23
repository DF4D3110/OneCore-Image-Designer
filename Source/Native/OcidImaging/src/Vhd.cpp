// OCID Imaging —— 虚拟磁盘（VHDX）层实现。
//
// 依赖 windows 自带的 VirtDisk.dll（imageapp 的 ImageStorageService 也是用它）：
//   CreateVirtualDisk / OpenVirtualDisk / AttachVirtualDisk / DetachVirtualDisk /
//   GetVirtualDiskPhysicalPath / SetVirtualDiskInformation
// 分区表用 IOCTL_DISK_SET_DRIVE_LAYOUT_EX 交给内核写（自动算 GPT 的 CRC32），
// 这样盘上的 GPT 就是 Windows 自己的格式，便于和 imageapp 的产物逐段比对。
#ifndef NOMINMAX
#define NOMINMAX
#endif
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winioctl.h>
#include <virtdisk.h>
#include <objbase.h>
#include <restartmanager.h>
#include <cwctype>

#include "ocid/imaging/Vhd.h"

#include <cstdio>
#include <cstring>

namespace ocid
{
namespace imaging
{
    namespace
    {
        // {xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx} / 无花括号 / 去掉短横线都能解析。
        bool ParseGuid(const std::string& text, GUID& out)
        {
            std::string t;
            t.reserve(text.size());
            for (char c : text)
            {
                if (c == '{' || c == '}' || c == '-' || c == ' ') continue;
                t.push_back(c);
            }
            if (t.size() != 32) return false;
            auto nib = [](char c) -> int
            {
                if (c >= '0' && c <= '9') return c - '0';
                if (c >= 'a' && c <= 'f') return c - 'a' + 10;
                if (c >= 'A' && c <= 'F') return c - 'A' + 10;
                return -1;
            };
            std::uint8_t bytes[16];
            for (int i = 0; i < 16; ++i)
            {
                const int hi = nib(t[i * 2]);
                const int lo = nib(t[i * 2 + 1]);
                if (hi < 0 || lo < 0) return false;
                bytes[i] = static_cast<std::uint8_t>((hi << 4) | lo);
            }
            // 前 8 字节按小端拼回 Data1/Data2/Data3。
            // 文本里的 16 个字节是"大端书写"：Data1 直接按 hex 值放，靠结构体
            // 自身的 LE 内存布局落盘（EFI_GUID 与 Windows GUID 内存布局一致）。
            out.Data1 = (static_cast<unsigned long>(bytes[0]) << 24) |
                        (static_cast<unsigned long>(bytes[1]) << 16) |
                        (static_cast<unsigned long>(bytes[2]) << 8) |
                        static_cast<unsigned long>(bytes[3]);
            out.Data2 = static_cast<unsigned short>((bytes[4] << 8) | bytes[5]);
            out.Data3 = static_cast<unsigned short>((bytes[6] << 8) | bytes[7]);
            for (int i = 0; i < 8; ++i) out.Data4[i] = bytes[8 + i];
            return true;
        }

        std::string FormatGuid(const GUID& g)
        {
            char buf[64];
            std::snprintf(buf, sizeof(buf),
                          "{%08X-%04X-%04X-%02X%02X-%02X%02X%02X%02X%02X%02X}",
                          g.Data1, g.Data2, g.Data3,
                          g.Data4[0], g.Data4[1], g.Data4[2], g.Data4[3],
                          g.Data4[4], g.Data4[5], g.Data4[6], g.Data4[7]);
            return buf;
        }

        std::string LastErrorText(const char* what)
        {
            const DWORD code = GetLastError();
            char text[512];
            std::snprintf(text, sizeof(text), "%s 失败（win32=%lu）", what, static_cast<unsigned long>(code));
            return text;
        }

        GUID VendorMicrosoft()
        {
            GUID value{};
            value.Data1 = 0xec984aec;
            value.Data2 = 0xa0f9;
            value.Data3 = 0x47e9;
            value.Data4[0] = 0x90;
            value.Data4[1] = 0x1f;
            value.Data4[2] = 0x71;
            value.Data4[3] = 0x41;
            value.Data4[4] = 0x5a;
            value.Data4[5] = 0x66;
            value.Data4[6] = 0x34;
            value.Data4[7] = 0x5b;
            return value;
        }

        // 按扩展名选设备类型：.vhd 必须用 VIRTUAL_STORAGE_TYPE_DEVICE_VHD，用 VHDX 打开会被
        // VirtDisk 判为 ERROR_FILE_CORRUPT(1392)（实测连 diskpart 生成的正品 .vhd 也报错）。
        ULONG StorageDeviceFor(const std::wstring& path)
        {
            std::wstring lower = path;
            for (wchar_t& ch : lower)
            {
                if (ch >= L'A' && ch <= L'Z') ch = static_cast<wchar_t>(ch - L'A' + L'a');
            }
            if (lower.size() >= 4 && lower.compare(lower.size() - 4, 4, L".vhd") == 0)
                return VIRTUAL_STORAGE_TYPE_DEVICE_VHD;
            return VIRTUAL_STORAGE_TYPE_DEVICE_VHDX;
        }

        bool OpenVirtual(const std::wstring& path, VIRTUAL_DISK_ACCESS_MASK access,
                         HANDLE& handle, std::string& error)
        {
            VIRTUAL_STORAGE_TYPE storage{};
            storage.DeviceId = StorageDeviceFor(path);
            storage.VendorId = VendorMicrosoft();
            // 用 version 1 打开：version 2 的 GetInfoOnly 语义在 GET_INFO 访问下会被拒
            // （实测 OpenVirtualDisk 返回成功但句柄无效、win32=0）；version 1 稳定可用。
            OPEN_VIRTUAL_DISK_PARAMETERS params{};
            params.Version = OPEN_VIRTUAL_DISK_VERSION_1;
            params.Version1.RWDepth = 0;
            handle = INVALID_HANDLE_VALUE;
            SetLastError(0);
            // 注意：VirtDisk 的 Open/Create/Attach 系列返回的是 Win32 错误码（ERROR_SUCCESS=0），
            // 不是 BOOL；早先按 BOOL 判断把成功当失败。
            SetLastError(0);
            const DWORD openResult = OpenVirtualDisk(&storage, path.c_str(), access,
                                                     OPEN_VIRTUAL_DISK_FLAG_NONE, &params, &handle);
            if (openResult != ERROR_SUCCESS || handle == INVALID_HANDLE_VALUE)
            {
                char detail[256];
                std::snprintf(detail, sizeof(detail),
                              "OpenVirtualDisk (rc=%lu, win32=%lu, access=%lu)",
                              static_cast<unsigned long>(openResult),
                              static_cast<unsigned long>(GetLastError()),
                              static_cast<unsigned long>(access));
                error = detail;
                return false;
            }
            return true;
        }

        // 从 \\.\PhysicalDriveN / \\?\PhysicalDriveN 里取出盘号。
        // virtdisk.h 的 VIRTUAL_STORAGE_TYPE_VENDOR_MICROSOFT 是 DEFINE_GUID，ADK 的
        // uuid.lib/virtdisk.lib 都不导出它；按 GUID 定义本地构造
        // ec984aec-a0f9-47e9-901f-71415a66345b。
        std::string NormalizeDevicePath(const std::wstring& physical)
        {
            const std::size_t at = physical.find(L"PhysicalDrive");
            std::string out;
            out.push_back(static_cast<char>(92));
            out.push_back(static_cast<char>(92));
            out.push_back('.');
            out.push_back(static_cast<char>(92));
            out.append("PhysicalDrive");
            if (at != std::wstring::npos)
            {
                for (std::size_t k = at + 13; k < physical.size(); ++k)
                {
                    const wchar_t c = physical[k];
                    if (c < L'0' || c > L'9') break;
                    out.push_back(static_cast<char>(c));
                }
            }
            return out;
        }
    }

    std::string VhdVersion()
    {
        return "1.0.0";
    }

    namespace
    {
        // ---- 自带固定 VHD 写入器 -------------------------------------------------
        // 为什么不用 CreateVirtualDisk 造 VHD：本机该 API 对 VHD 设备返回 win32=2。
        // 固定 VHD 的格式很简单（尾部 512 字节 footer + 裸数据），自己写既可控又能被
        // VirtDisk 打开、被微软的 ImgDump.exe 识别。
        void PutBe16(unsigned char* p, unsigned int v) { p[0] = (unsigned char)(v >> 8); p[1] = (unsigned char)v; }
        void PutBe32(unsigned char* p, unsigned int v)
        {
            p[0] = (unsigned char)(v >> 24); p[1] = (unsigned char)(v >> 16);
            p[2] = (unsigned char)(v >> 8);  p[3] = (unsigned char)v;
        }
        void PutBe64(unsigned char* p, std::uint64_t v)
        {
            for (int i = 0; i < 8; ++i) p[i] = (unsigned char)(v >> (56 - 8 * i));
        }

        // Virtual PC 的 CHS 换算（VHD footer 里要填，读盘的工具会拿它做 sanity check）。
        void VhdGeometry(std::uint64_t bytes, unsigned int& cylinders, unsigned int& heads, unsigned int& sectors)
        {
            std::uint64_t total = bytes / 512;
            const std::uint64_t maxChs = 65535ull * 16ull * 255ull;
            if (total > maxChs) total = maxChs;
            sectors = 17;
            heads = 4;
            if (total >= 65535ull * 16ull * 63ull)
            {
                sectors = 255;
                heads = 16;
            }
            else
            {
                std::uint64_t cth = total / sectors;
                heads = static_cast<unsigned int>((cth + 1023) / 1024);
                if (heads < 4) heads = 4;
                if (cth >= static_cast<std::uint64_t>(heads) * 65535ull) { sectors = 255; heads = 16; }
            }
            cylinders = static_cast<unsigned int>(total / (static_cast<std::uint64_t>(heads) * sectors));
            if (cylinders > 65535) cylinders = 65535;
        }

        // ---------- 动态 VHD（type=3）----------
        // 固定 VHD 必须整盘分配（42 GiB 就是 42 GiB 磁盘），而 VHD 又只能 512 字节扇区，
        // 所以这里自己写一个动态 VHD 头部：footer(offset 0) + dynamic header(512) + BAT(1536) +
        // footer(文件尾)。盘挂上以后块分配由 VirtDisk 驱动完成，文件按写入量增长。
        bool VhdWriteAt(HANDLE file, std::uint64_t offset, const void* data, std::uint32_t length, std::string& error)
        {
            LARGE_INTEGER pos;
            pos.QuadPart = static_cast<LONGLONG>(offset);
            if (!SetFilePointerEx(file, pos, nullptr, FILE_BEGIN))
            {
                error = LastErrorText("SetFilePointerEx(VHD)");
                return false;
            }
            const unsigned char* bytes = static_cast<const unsigned char*>(data);
            std::uint32_t written = 0;
            while (written < length)
            {
                DWORD chunk = 0;
                if (!WriteFile(file, bytes + written, length - written, &chunk, nullptr) || chunk == 0)
                {
                    error = LastErrorText("WriteFile(VHD)");
                    return false;
                }
                written += chunk;
            }
            return true;
        }

        std::uint32_t VhdChecksum(const unsigned char* data, std::size_t length)
        {
            std::uint32_t sum = 0;
            for (std::size_t i = 0; i < length; ++i) sum += data[i];
            return ~sum;
        }

        void VhdFillFooter(unsigned char* footer, std::uint64_t bytes, std::uint32_t type,
                           std::uint64_t dataOffset, const unsigned char* uniqueId)
        {
            std::memset(footer, 0, 512);
            std::memcpy(footer, "conectix", 8);
            PutBe32(footer + 8, 2);            // features
            PutBe32(footer + 12, 0x00010000u); // file format version
            PutBe64(footer + 16, dataOffset);
            std::uint32_t timestamp = 0;
            SYSTEMTIME st;
            GetSystemTime(&st);
            FILETIME ft;
            SystemTimeToFileTime(&st, &ft);
            ULARGE_INTEGER ticks;
            ticks.LowPart = ft.dwLowDateTime;
            ticks.HighPart = ft.dwHighDateTime;
            const std::uint64_t epoch = 125911584000000000ull; // 2000-01-01 UTC
            if (ticks.QuadPart > epoch)
                timestamp = static_cast<std::uint32_t>((ticks.QuadPart - epoch) / 10000000ull);
            PutBe32(footer + 24, timestamp);
            std::memcpy(footer + 28, "ocid", 4);
            PutBe32(footer + 32, 0x00010000u);
            std::memcpy(footer + 36, "Wi2k", 4);
            PutBe64(footer + 40, bytes);
            PutBe64(footer + 48, bytes);
            const std::uint64_t totalSectors = bytes / 512ull;
            std::uint32_t cylinders = 0;
            std::uint32_t heads = 0;
            std::uint32_t sectors = 0;
            if (totalSectors >= 65535ull * 16ull * 63ull)
            {
                sectors = 255;
                heads = 16;
                cylinders = static_cast<std::uint32_t>(totalSectors / (heads * sectors));
            }
            else
            {
                sectors = 17;
                const std::uint64_t cth = totalSectors / sectors;
                heads = static_cast<std::uint32_t>((cth + 1023ull) / 1024ull);
                if (heads < 4) heads = 4;
                if (cth >= static_cast<std::uint64_t>(heads) * 65535ull)
                {
                    heads = 16;
                    sectors = 255;
                    cylinders = static_cast<std::uint32_t>(totalSectors / (static_cast<std::uint64_t>(heads) * sectors));
                }
                else
                {
                    cylinders = static_cast<std::uint32_t>(cth / heads);
                }
            }
            if (cylinders > 65535) cylinders = 65535;
            PutBe16(footer + 56, cylinders);
            footer[58] = static_cast<unsigned char>(heads);
            footer[59] = static_cast<unsigned char>(sectors);
            PutBe32(footer + 60, type);
            if (uniqueId != nullptr) std::memcpy(footer + 68, uniqueId, 16);
            PutBe32(footer + 64, 0);
            PutBe32(footer + 64, VhdChecksum(footer, 512));
        }

        bool CreateDynamicVhdFile(const std::wstring& path, std::uint64_t bytes, std::string& error)
        {
            if (bytes == 0 || bytes % 512 != 0) { error = "VHD 虚拟容量必须是 512 的整数倍"; return false; }
            const std::uint64_t blockSize = 2ull * 1024 * 1024;
            const std::uint32_t tableEntries = static_cast<std::uint32_t>((bytes + blockSize - 1) / blockSize);
            unsigned char uniqueId[16] = {0};
            GUID guid{};
            if (SUCCEEDED(CoCreateGuid(&guid)))
                std::memcpy(uniqueId, &guid, 16);

            unsigned char footer[512];
            unsigned char footerEnd[512];
            VhdFillFooter(footer, bytes, 3, 512, uniqueId);
            VhdFillFooter(footerEnd, bytes, 3, 0xFFFFFFFFFFFFFFFFull, uniqueId);

            unsigned char header[1024];
            std::memset(header, 0, sizeof(header));
            std::memcpy(header, "cxsparse", 8);
            PutBe64(header + 8, 0xFFFFFFFFFFFFFFFFull); // data offset（动态盘固定 ~0）
            PutBe64(header + 16, 1536);                 // BAT 表偏移
            PutBe32(header + 24, 0x00010000u);          // header version
            PutBe32(header + 28, tableEntries);         // max table entries
            PutBe32(header + 32, static_cast<std::uint32_t>(blockSize));
            PutBe32(header + 36, 0);
            PutBe32(header + 36, VhdChecksum(header, 1024));

            HANDLE file = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                                      FILE_ATTRIBUTE_NORMAL, nullptr);
            if (file == INVALID_HANDLE_VALUE) { error = LastErrorText("CreateFile(VHD)"); return false; }

            const std::uint64_t batOffset = 1536;
            // BAT 之后要按 512 字节对齐（VirtDisk 要求 footer 落在扇区边界上，否则判为损坏）。
            const std::uint64_t batBytes = ((static_cast<std::uint64_t>(tableEntries) * 4ull + 511ull) / 512ull) * 512ull;
            const std::uint64_t footerOffset = batOffset + batBytes;
            std::vector<unsigned char> bat(static_cast<std::size_t>(batBytes), 0xFF);
            bool ok = VhdWriteAt(file, 0, footer, 512, error) &&
                      VhdWriteAt(file, 512, header, 1024, error) &&
                      VhdWriteAt(file, batOffset, bat.data(), static_cast<std::uint32_t>(batBytes), error);
            LARGE_INTEGER end;
            end.QuadPart = static_cast<LONGLONG>(footerOffset + 512);
            if (ok && (!SetFilePointerEx(file, end, nullptr, FILE_BEGIN) || !SetEndOfFile(file)))
            {
                error = LastErrorText("SetEndOfFile(VHD)");
                ok = false;
            }
            if (ok) ok = VhdWriteAt(file, footerOffset, footerEnd, 512, error);
            CloseHandle(file);
            return ok;
        }

        bool CreateFixedVhdFile(const std::wstring& path, std::uint64_t bytes, std::string& error)
        {
            if (bytes == 0 || bytes % 512 != 0) { error = "VHD 容量必须是 512 的整数倍"; return false; }
            HANDLE file = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                                      FILE_ATTRIBUTE_NORMAL, nullptr);
            if (file == INVALID_HANDLE_VALUE) { error = LastErrorText("CreateFile(VHD)"); return false; }
            // 不能把固定 VHD 标成 sparse：实测 VirtDisk 打开 sparse 的固定 VHD 会返回
            // 0xC03A001A（ERROR_VHD_INVALID_SIZE），清掉 sparse 标志后即可正常打开。
            // 非 sparse 的 SetEndOfFile 在 NTFS 上是惰性分配的，未写的区域不占物理空间。
            LARGE_INTEGER end;
            end.QuadPart = static_cast<LONGLONG>(bytes);
            if (!SetFilePointerEx(file, end, nullptr, FILE_BEGIN) || !SetEndOfFile(file))
            {
                error = LastErrorText("SetEndOfFile(VHD)");
                CloseHandle(file);
                return false;
            }

            unsigned char footer[512] = { 0 };
            std::memcpy(footer, "conectix", 8);
            PutBe32(footer + 8, 2);                    // features: reserved bit
            PutBe32(footer + 12, 0x00010000);          // file format version 1.0
            PutBe64(footer + 16, 0xFFFFFFFFFFFFFFFFull);  // data offset: none for fixed
            SYSTEMTIME now;
            GetSystemTime(&now);
            FILETIME ft{};
            SystemTimeToFileTime(&now, &ft);
            ULARGE_INTEGER ticks;
            ticks.LowPart = ft.dwLowDateTime;
            ticks.HighPart = ft.dwHighDateTime;
            const unsigned long long vhdEpoch100ns = 125911584000000000ull;  // 2000-01-01T00:00:00Z
            const unsigned long long seconds100 = ticks.QuadPart > vhdEpoch100ns ? ticks.QuadPart - vhdEpoch100ns : 0;
            PutBe32(footer + 24, static_cast<std::uint32_t>(seconds100 / 10000000ull));
            std::memcpy(footer + 28, "ocid", 4);       // creator application
            PutBe32(footer + 32, 0x00010000);          // creator version
            std::memcpy(footer + 36, "Wi2k", 4);       // creator host OS
            PutBe64(footer + 40, bytes);               // original size
            PutBe64(footer + 48, bytes);               // current size
            unsigned int cylinders = 0, heads = 0, sectors = 0;
            VhdGeometry(bytes, cylinders, heads, sectors);
            PutBe16(footer + 56, cylinders);
            footer[58] = static_cast<unsigned char>(heads);
            footer[59] = static_cast<unsigned char>(sectors);
            PutBe32(footer + 60, 2);                   // disk type: fixed
            // 68..83 unique id：沿用磁盘 GUID 的随机数即可
            GUID id;
            if (CoCreateGuid(&id) == S_OK)
            {
                unsigned char* p = footer + 68;
                p[0] = static_cast<unsigned char>(id.Data1 >> 24); p[1] = static_cast<unsigned char>(id.Data1 >> 16);
                p[2] = static_cast<unsigned char>(id.Data1 >> 8);  p[3] = static_cast<unsigned char>(id.Data1);
                p[4] = static_cast<unsigned char>(id.Data2 >> 8);  p[5] = static_cast<unsigned char>(id.Data2);
                p[6] = static_cast<unsigned char>(id.Data3 >> 8);  p[7] = static_cast<unsigned char>(id.Data3);
                std::memcpy(p + 8, id.Data4, 8);
            }
            footer[84] = 0;                            // saved state
            unsigned int sum = 0;
            for (int i = 0; i < 512; ++i) sum += footer[i];
            PutBe32(footer + 64, ~sum);

            LARGE_INTEGER at;
            at.QuadPart = static_cast<LONGLONG>(bytes);
            DWORD written = 0;
            if (!SetFilePointerEx(file, at, nullptr, FILE_BEGIN) ||
                !WriteFile(file, footer, 512, &written, nullptr) || written != 512)
            {
                error = LastErrorText("WriteFile(VHD footer)");
                CloseHandle(file);
                return false;
            }
            FlushFileBuffers(file);
            CloseHandle(file);
            return true;
        }
    }

    bool VhdCreate(const std::wstring& path,
                   std::uint64_t virtualBytes,
                   VhdKind kind,
                   std::uint32_t blockSize,
                   std::uint32_t physicalSectorSize,
                   std::string& error,
                   VhdFormat format)
    {
        if (virtualBytes < 3ull * 1024 * 1024)
        {
            error = "虚拟容量至少 3 MiB";
            return false;
        }
        if (virtualBytes % (1024ull * 1024) != 0)
        {
            error = "虚拟容量必须是 1 MiB 的整数倍（VirtDisk 的硬要求）";
            return false;
        }
        const bool vhd = (format == VhdFormat::Vhd);
        if (vhd && physicalSectorSize != 0 && physicalSectorSize != 512)
        {
            error = "VHD 只支持 512 字节扇区（当前布局是 " + std::to_string(physicalSectorSize) +
                    "）；需要 4K 扇区请用 VHDX";
            return false;
        }
        if (vhd && kind == VhdKind::Fixed)
        {
            // 固定 VHD 由我们自己写：CreateVirtualDisk 的 FULL_PHYSICAL_ALLOCATION 对 VHD 不生效。
            return CreateFixedVhdFile(path, virtualBytes, error);
        }
        VIRTUAL_STORAGE_TYPE storage{};
        storage.DeviceId = vhd ? VIRTUAL_STORAGE_TYPE_DEVICE_VHD : VIRTUAL_STORAGE_TYPE_DEVICE_VHDX;
        storage.VendorId = VendorMicrosoft();

        CREATE_VIRTUAL_DISK_PARAMETERS params{};
        if (vhd)
        {
            params.Version = CREATE_VIRTUAL_DISK_VERSION_1;
            params.Version1.UniqueId = GUID_NULL;
            params.Version1.MaximumSize = virtualBytes;
            params.Version1.BlockSizeInBytes = 0;
            params.Version1.SectorSizeInBytes = 512;
        }
        else
        {
            params.Version = CREATE_VIRTUAL_DISK_VERSION_2;
            params.Version2.UniqueId = GUID_NULL;
            params.Version2.MaximumSize = virtualBytes;
            params.Version2.BlockSizeInBytes = (blockSize == 0) ? 0 : blockSize;
            params.Version2.SectorSizeInBytes = (physicalSectorSize == 0) ? 512 : physicalSectorSize;
            params.Version2.PhysicalSectorSizeInBytes = params.Version2.SectorSizeInBytes;
        }

        HANDLE handle = INVALID_HANDLE_VALUE;
        const CREATE_VIRTUAL_DISK_FLAG flags =
            (kind == VhdKind::Fixed) ? CREATE_VIRTUAL_DISK_FLAG_FULL_PHYSICAL_ALLOCATION : CREATE_VIRTUAL_DISK_FLAG_NONE;
        const VIRTUAL_DISK_ACCESS_MASK createAccess = vhd ? VIRTUAL_DISK_ACCESS_ALL : VIRTUAL_DISK_ACCESS_NONE;
        const DWORD rc = CreateVirtualDisk(&storage, path.c_str(), createAccess, nullptr,
                                           flags, 0, &params, nullptr, &handle);
        if (rc != ERROR_SUCCESS)
        {
            error = LastErrorText("CreateVirtualDisk");
            return false;
        }
        CloseHandle(handle);
        return true;
    }

    namespace
    {
        // 非永久挂载的盘靠“保持 OpenVirtualDisk 句柄”存活：句柄一关盘就卸载。
        // 进程正常退出时 VhdReleaseAll() 逐个 detach + close；被杀时系统自动回收。
        std::vector<HANDLE>& AttachHandles()
        {
            static std::vector<HANDLE> handles;
            return handles;
        }
    }

    void VhdReleaseAll()
    {
        std::vector<HANDLE>& handles = AttachHandles();
        for (std::size_t i = 0; i < handles.size(); ++i)
        {
            if (handles[i] != INVALID_HANDLE_VALUE)
            {
                (void)DetachVirtualDisk(handles[i], DETACH_VIRTUAL_DISK_FLAG_NONE, 0);
                CloseHandle(handles[i]);
            }
        }
        handles.clear();
    }

    bool VhdAttach(const std::wstring& path, bool readOnly, std::string& devicePath, std::string& error,
                    bool permanent)
    {
        HANDLE handle = INVALID_HANDLE_VALUE;
        if (!OpenVirtual(path, VIRTUAL_DISK_ACCESS_ALL, handle, error)) return false;

        ATTACH_VIRTUAL_DISK_PARAMETERS params{};
        params.Version = ATTACH_VIRTUAL_DISK_VERSION_1;
        // 默认非永久挂载：生命周期绑定本进程，进程正常退出或被杀时由系统自动卸载。
        // 只有 CLI 的 vhd attach（跨命令）才用 PERMANENT_LIFETIME。
        const ATTACH_VIRTUAL_DISK_FLAG attachFlags = static_cast<ATTACH_VIRTUAL_DISK_FLAG>(
            (permanent ? ATTACH_VIRTUAL_DISK_FLAG_PERMANENT_LIFETIME : ATTACH_VIRTUAL_DISK_FLAG_NONE) |
            (readOnly ? ATTACH_VIRTUAL_DISK_FLAG_READ_ONLY : ATTACH_VIRTUAL_DISK_FLAG_NONE));
        const DWORD rc = AttachVirtualDisk(handle, nullptr, attachFlags, 0, &params, nullptr);
        if (rc != ERROR_SUCCESS)
        {
            const DWORD already = ERROR_ACCESS_DENIED;
            if (rc != already)
            {
                error = LastErrorText("AttachVirtualDisk");
                CloseHandle(handle);
                return false;
            }
        }

        wchar_t physical[512];
        ULONG bytes = sizeof(physical);
        if (GetVirtualDiskPhysicalPath(handle, &bytes, physical) != ERROR_SUCCESS)
        {
            error = LastErrorText("GetVirtualDiskPhysicalPath");
            CloseHandle(handle);
            return false;
        }
        devicePath = NormalizeDevicePath(physical);
        // permanent：跨进程挂载，关句柄不影响；否则必须保持句柄，句柄关=盘卸载。
        if (permanent) CloseHandle(handle);
        else AttachHandles().push_back(handle);
        return true;
    }

    bool VhdDetach(const std::wstring& path, std::string& error)
    {
        HANDLE handle = INVALID_HANDLE_VALUE;
        // DetachVirtualDisk 要求句柄用 VIRTUAL_DISK_ACCESS_DETACH 打开（用 ALL 会得到
        // win32=21 ERROR_NOT_READY）。
        if (!OpenVirtual(path, VIRTUAL_DISK_ACCESS_DETACH, handle, error)) return false;
        const DWORD rc = DetachVirtualDisk(handle, DETACH_VIRTUAL_DISK_FLAG_NONE, 0);
    // 对 PERMANENT_LIFETIME 挂载的盘还要按永久语义再卸一次（此 SDK 头里没有该枚举值，1 取自 API 文档）。
    DetachVirtualDisk(handle, static_cast<DETACH_VIRTUAL_DISK_FLAG>(1), 0);
        CloseHandle(handle);
        // ERROR_NOT_READY：这块盘本来就没挂着（已经是目标状态），按成功处理。
        if (rc != ERROR_SUCCESS && rc != ERROR_NOT_READY)
        {
            error = LastErrorText("DetachVirtualDisk");
            return false;
        }
        return true;
    }

    bool VhdQuery(const std::wstring& path, VhdInfo& info, std::string& error)
    {
        HANDLE handle = INVALID_HANDLE_VALUE;
        if (!OpenVirtual(path, VIRTUAL_DISK_ACCESS_GET_INFO, handle, error)) return false;

        GET_VIRTUAL_DISK_INFO sizeInfo{};
        ULONG outSize = sizeof(sizeInfo);
        sizeInfo.Version = GET_VIRTUAL_DISK_INFO_SIZE;
        if (GetVirtualDiskInformation(handle, &outSize, &sizeInfo, nullptr) == ERROR_SUCCESS)
        {
            info.virtualBytes = sizeInfo.Size.VirtualSize;
            info.blockSize = sizeInfo.Size.BlockSize;
            info.physicalSectorSize = sizeInfo.Size.SectorSize;
        }

        wchar_t physical[512];
        ULONG bytes = sizeof(physical);
        if (GetVirtualDiskPhysicalPath(handle, &bytes, physical) == ERROR_SUCCESS)
        {
            info.attached = true;
            info.devicePath = NormalizeDevicePath(physical);
        }
        CloseHandle(handle);
        info.path = std::string(path.begin(), path.end());
        WIN32_FILE_ATTRIBUTE_DATA data{};
        if (GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &data))
        {
            info.fileBytes = (static_cast<std::uint64_t>(data.nFileSizeHigh) << 32) | data.nFileSizeLow;
        }
        return true;
    }

    // 安全约束：只允许对虚拟磁盘（VHD/VHDX）操作，检测到物理磁盘直接拒绝。
    bool VhdEnsureVirtualDisk(const std::string& devicePath, std::string& error)
    {
        const std::wstring wide(devicePath.begin(), devicePath.end());
        HANDLE disk = CreateFileW(wide.c_str(), 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                  nullptr, OPEN_EXISTING, 0, nullptr);
        if (disk == INVALID_HANDLE_VALUE)
        {
            error = "只允许对虚拟磁盘操作，但无法打开设备：" + LastErrorText("CreateFileW");
            return false;
        }
        STORAGE_PROPERTY_QUERY query{};
        query.PropertyId = StorageDeviceProperty;
        query.QueryType = PropertyStandardQuery;
        STORAGE_DEVICE_DESCRIPTOR descriptor{};
        DWORD returned = 0;
        const BOOL ok = DeviceIoControl(disk, IOCTL_STORAGE_QUERY_PROPERTY, &query, sizeof(query),
                                        &descriptor, sizeof(descriptor), &returned, nullptr);
        const DWORD code = ok ? 0 : GetLastError();
        CloseHandle(disk);
        if (!ok)
        {
            error = "无法查询设备总线类型（Win32=" + std::to_string(code) + "）";
            return false;
        }
        // Storage Spaces 的 space（BusType=Spaces）也是本工具自己造出来的软件磁盘，
        // 池内分区的 GPT 就要写到它上面；真正的物理盘（SATA/NVMe/USB…）一律拒绝。
        if (descriptor.BusType != BusTypeVirtual && descriptor.BusType != BusTypeFileBackedVirtual &&
            descriptor.BusType != BusTypeSpaces)
        {
            error = "拒绝操作：" + devicePath + " 不是虚拟磁盘（BusType=" +
                    std::to_string(static_cast<int>(descriptor.BusType)) +
                    "）。本工具只允许写入虚拟磁盘。";
            return false;
        }
        return true;
    }

    // 真写一次 GPT。刚 attach 的盘会被内核重新枚举，紧跟的 IOCTL 常返回
    // 433 ERROR_NO_SUCH_DEVICE / 1167 ERROR_DEVICE_NOT_CONNECTED / 21 ERROR_NOT_READY，
    // 这里把当时的 win32 码带出去，由 VhdWriteGpt 退避重试。
    static bool WriteGptOnce(const std::string& devicePath,
                             const std::string& diskId,
                             const std::vector<VhdPartition>& parts,
                             std::uint32_t sectorSize,
                             std::string& error,
                             std::uint32_t& win32)
    {
        win32 = 0;
        if (parts.empty())
        {
            error = "分区表为空";
            return false;
        }
        if (sectorSize == 0) sectorSize = 512;

        const std::wstring wide(devicePath.begin(), devicePath.end());
        HANDLE disk = CreateFileW(wide.c_str(), GENERIC_READ | GENERIC_WRITE,
                                  FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr);
        if (disk == INVALID_HANDLE_VALUE)
        {
            win32 = GetLastError();
            error = LastErrorText("打开物理盘");
            return false;
        }

        // 先用一次 GET 拿到驱动期望的缓冲大小，再按分区数重分配。
        const std::size_t n = parts.size();
        const std::size_t bytes = sizeof(DRIVE_LAYOUT_INFORMATION_EX) + (n - 1) * sizeof(PARTITION_INFORMATION_EX);
        std::vector<std::uint8_t> buffer(bytes, 0);
        auto* layout = reinterpret_cast<DRIVE_LAYOUT_INFORMATION_EX*>(buffer.data());
        layout->PartitionStyle = PARTITION_STYLE_GPT;
        layout->PartitionCount = static_cast<DWORD>(n);
        GUID diskGuid{};
        if (!diskId.empty())
        {
            if (!ParseGuid(diskId, diskGuid))
            {
                CloseHandle(disk);
                error = "磁盘 GUID 解析失败：" + diskId;
                return false;
            }
        }
        else if (CoCreateGuid(&diskGuid) != S_OK)
        {
            CloseHandle(disk);
            error = LastErrorText("生成磁盘 GUID");
            return false;
        }
        layout->Gpt.DiskId = diskGuid;

        for (std::size_t i = 0; i < n; ++i)
        {
            const VhdPartition& src = parts[i];
            PARTITION_INFORMATION_EX& dst = layout->PartitionEntry[i];
            dst.PartitionStyle = PARTITION_STYLE_GPT;
            dst.StartingOffset.QuadPart = static_cast<LONGLONG>(src.offsetBytes);
            dst.PartitionLength.QuadPart = static_cast<LONGLONG>(src.sizeBytes);
            dst.PartitionNumber = static_cast<DWORD>(i + 1);
            dst.RewritePartition = TRUE;
            GUID type{};
            if (!ParseGuid(src.type, type))
            {
                CloseHandle(disk);
                error = "分区类型 GUID 解析失败：" + src.type;
                return false;
            }
            dst.Gpt.PartitionType = type;
            if (!src.id.empty())
            {
                if (!ParseGuid(src.id, dst.Gpt.PartitionId))
                {
                    CloseHandle(disk);
                    error = "分区 GUID 解析失败：" + src.id;
                    return false;
                }
            }
            else if (CoCreateGuid(&dst.Gpt.PartitionId) != S_OK)
            {
                CloseHandle(disk);
                error = LastErrorText("生成分区 GUID");
                return false;
            }
            dst.Gpt.Attributes = src.attributes;
            const std::wstring name(src.name.begin(), src.name.end());
            wcsncpy_s(dst.Gpt.Name, 36, name.c_str(), 35);
            dst.Gpt.Name[35] = 0;
        }

        DWORD returned = 0;
        const BOOL ok = DeviceIoControl(disk, IOCTL_DISK_SET_DRIVE_LAYOUT_EX,
                                        layout, static_cast<DWORD>(bytes), nullptr, 0, &returned, nullptr);
        if (!ok)
        {
            win32 = GetLastError();
            error = LastErrorText("IOCTL_DISK_SET_DRIVE_LAYOUT_EX");
            CloseHandle(disk);
            return false;
        }
        CloseHandle(disk);
        return true;
    }

    bool VhdWriteGpt(const std::string& devicePath,
                     const std::string& diskId,
                     const std::vector<VhdPartition>& parts,
                     std::uint32_t sectorSize,
                     std::string& error)
    {
        if (!VhdEnsureVirtualDisk(devicePath, error))
            return false;

        const std::uint32_t transient[] = {2u, 433u, 1167u, 21u, 31u, 1117u, 5u};
        std::string lastError;
        for (int attempt = 0; attempt < 15; ++attempt)
        {
            std::uint32_t win32 = 0;
            std::string attemptError;
            if (WriteGptOnce(devicePath, diskId, parts, sectorSize, attemptError, win32))
            {
                error.clear();
                return true;
            }
            lastError = attemptError;
            bool retry = false;
            for (std::size_t i = 0; i < sizeof(transient) / sizeof(transient[0]); ++i)
                if (win32 == transient[i]) retry = true;
            if (!retry) break;
            Sleep(static_cast<DWORD>(200 + attempt * 100));
        }
        error = lastError;
        return false;
    }

    bool VhdReadGpt(const std::string& devicePath,
                    std::string& diskId,
                    std::vector<VhdPartition>& parts,
                    std::uint32_t& sectorSize,
                    std::string& error)
    {
        if (!VhdEnsureVirtualDisk(devicePath, error))
            return false;

        const std::wstring wide(devicePath.begin(), devicePath.end());
        HANDLE disk = CreateFileW(wide.c_str(), GENERIC_READ,
                                  FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr);
        if (disk == INVALID_HANDLE_VALUE)
        {
            error = LastErrorText("打开物理盘");
            return false;
        }

        DISK_GEOMETRY_EX geometry{};
        DWORD returned = 0;
        if (DeviceIoControl(disk, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, nullptr, 0,
                            &geometry, sizeof(geometry), &returned, nullptr))
        {
            sectorSize = geometry.Geometry.BytesPerSector;
        }
        if (sectorSize == 0) sectorSize = 512;

        const std::size_t capacity = 128;
        const std::size_t bytes = sizeof(DRIVE_LAYOUT_INFORMATION_EX) + (capacity - 1) * sizeof(PARTITION_INFORMATION_EX);
        std::vector<std::uint8_t> buffer(bytes, 0);
        auto* layout = reinterpret_cast<DRIVE_LAYOUT_INFORMATION_EX*>(buffer.data());
        if (!DeviceIoControl(disk, IOCTL_DISK_GET_DRIVE_LAYOUT_EX, nullptr, 0,
                             layout, static_cast<DWORD>(bytes), &returned, nullptr))
        {
            CloseHandle(disk);
            error = LastErrorText("IOCTL_DISK_GET_DRIVE_LAYOUT_EX");
            return false;
        }
        CloseHandle(disk);

        parts.clear();
        if (layout->PartitionStyle != PARTITION_STYLE_GPT)
        {
            error = "磁盘不是 GPT";
            return false;
        }
        diskId = FormatGuid(layout->Gpt.DiskId);
        for (DWORD i = 0; i < layout->PartitionCount; ++i)
        {
            const PARTITION_INFORMATION_EX& src = layout->PartitionEntry[i];
            if (src.PartitionLength.QuadPart <= 0) continue;
            VhdPartition part;
            char name[64] = {0};
            WideCharToMultiByte(CP_UTF8, 0, src.Gpt.Name, -1, name, sizeof(name), nullptr, nullptr);
            part.name = name;
            part.type = FormatGuid(src.Gpt.PartitionType);
            part.id = FormatGuid(src.Gpt.PartitionId);
            part.offsetBytes = static_cast<std::uint64_t>(src.StartingOffset.QuadPart);
            part.sizeBytes = static_cast<std::uint64_t>(src.PartitionLength.QuadPart);
            part.attributes = src.Gpt.Attributes;
            parts.push_back(part);
        }
        return true;
    }

    namespace
    {
        // 刚改过 GPT 的盘会被内核重新枚举，紧跟其后的裸读写可能短暂报
        // ERROR_NO_SUCH_DEVICE（433）/ ERROR_DEVICE_NOT_CONNECTED（1167）。
        // 这里统一做「重开句柄 + 退避重试」。
        bool TransientDeviceError(DWORD code)
        {
            return code == ERROR_NO_SUCH_DEVICE || code == ERROR_DEVICE_NOT_CONNECTED ||
                   code == ERROR_NOT_READY || code == ERROR_BUSY;
        }

        bool WriteBytesOnce(const std::wstring& wide, std::uint64_t offset, const std::uint8_t* bytes,
                            std::size_t size, std::string& error, DWORD& code)
        {
            HANDLE device = CreateFileW(wide.c_str(), GENERIC_READ | GENERIC_WRITE,
                                        FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr);
            if (device == INVALID_HANDLE_VALUE)
            {
                code = GetLastError();
                error = LastErrorText("open physical disk");
                return false;
            }
            LARGE_INTEGER pos{};
            pos.QuadPart = static_cast<LONGLONG>(offset);
            if (!SetFilePointerEx(device, pos, nullptr, FILE_BEGIN))
            {
                code = GetLastError();
                error = LastErrorText("seek");
                CloseHandle(device);
                return false;
            }
            std::size_t done = 0;
            while (done < size)
            {
                const std::size_t left = size - done;
                const DWORD chunk = static_cast<DWORD>(left > (1u << 20) ? (1u << 20) : left);
                DWORD written = 0;
                if (!WriteFile(device, bytes + done, chunk, &written, nullptr) || written == 0)
                {
                    code = GetLastError();
                    error = LastErrorText("write");
                    CloseHandle(device);
                    return false;
                }
                done += written;
            }
            code = ERROR_SUCCESS;
            CloseHandle(device);
            return true;
        }
    }

    bool VhdWriteBytes(const std::string& devicePath,
                       std::uint64_t offset,
                       const void* data,
                       std::size_t size,
                       std::string& error)
    {
        if (size == 0) return true;
        if (!VhdEnsureVirtualDisk(devicePath, error))
            return false;
        const std::wstring wide(devicePath.begin(), devicePath.end());
        const std::uint8_t* bytes = static_cast<const std::uint8_t*>(data);
        DWORD code = ERROR_SUCCESS;
        for (int attempt = 0; attempt < 25; ++attempt)
        {
            if (WriteBytesOnce(wide, offset, bytes, size, error, code))
                return true;
            if (!TransientDeviceError(code)) return false;
            Sleep(100);
        }
        return false;
    }

    bool VhdReadBytes(const std::string& devicePath,
                      std::uint64_t offset,
                      void* data,
                      std::size_t size,
                      std::string& error)
    {
        if (size == 0) return true;
        const std::wstring wide(devicePath.begin(), devicePath.end());
        HANDLE device = CreateFileW(wide.c_str(), GENERIC_READ,
                                    FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr);
        if (device == INVALID_HANDLE_VALUE)
        {
            error = LastErrorText("open physical disk");
            return false;
        }
        LARGE_INTEGER pos{};
        pos.QuadPart = static_cast<LONGLONG>(offset);
        if (!SetFilePointerEx(device, pos, nullptr, FILE_BEGIN))
        {
            error = LastErrorText("seek");
            CloseHandle(device);
            return false;
        }
        std::uint8_t* bytes = static_cast<std::uint8_t*>(data);
        std::size_t done = 0;
        while (done < size)
        {
            const std::size_t left = size - done;
            const DWORD chunk = static_cast<DWORD>(left > (1u << 20) ? (1u << 20) : left);
            DWORD got = 0;
            if (!ReadFile(device, bytes + done, chunk, &got, nullptr) || got == 0)
            {
                error = LastErrorText("read");
                CloseHandle(device);
                return false;
            }
            done += got;
        }
        CloseHandle(device);
        return true;
    }
    namespace
    {
        void LbaToChs(std::uint64_t lba, std::uint8_t out[3])
        {
            const std::uint64_t heads = 255, sectors = 63;
            std::uint64_t c = lba / (heads * sectors);
            std::uint64_t h = (lba / sectors) % heads;
            std::uint64_t s = lba % sectors + 1;
            if (c > 1023) { c = 1023; h = 254; s = 63; }
            out[0] = static_cast<std::uint8_t>(h);
            out[1] = static_cast<std::uint8_t>((s & 0x3F) | ((c >> 2) & 0xC0));
            out[2] = static_cast<std::uint8_t>(c & 0xFF);
        }
    }

    bool VhdWriteMbr(const std::string& devicePath,
                     const std::vector<VhdPartition>& parts,
                     std::uint32_t sectorSize,
                     std::string& error)
    {
        if (parts.empty())
        {
            error = "分区表为空";
            return false;
        }
        if (parts.size() > 4)
        {
            error = "MBR 最多 4 个主分区（布局给了 " + std::to_string(parts.size()) + " 个）";
            return false;
        }
        if (sectorSize == 0) sectorSize = 512;
        if (!VhdEnsureVirtualDisk(devicePath, error)) return false;

        const std::size_t sectorBytes = sectorSize;
        std::vector<std::uint8_t> sector(sectorBytes, 0);
        for (std::size_t i = 0; i < parts.size(); ++i)
        {
            const VhdPartition& p = parts[i];
            if (p.offsetBytes % sectorSize != 0 || p.sizeBytes % sectorSize != 0)
            {
                error = "分区 " + p.name + " 的偏移/大小不是扇区整数倍";
                return false;
            }
            const std::uint64_t startLba = p.offsetBytes / sectorSize;
            const std::uint64_t countLba = p.sizeBytes / sectorSize;
            if (startLba > 0xFFFFFFFFull || countLba > 0xFFFFFFFFull)
            {
                error = "分区 " + p.name + " 超出 MBR 32 位 LBA 范围";
                return false;
            }
            std::uint8_t* entry = sector.data() + 446 + 16 * i;
            entry[0] = p.bootable ? 0x80 : 0x00;
            LbaToChs(startLba, entry + 1);
            entry[4] = static_cast<std::uint8_t>(p.mbrType & 0xFF);
            LbaToChs(startLba + countLba - 1, entry + 5);
            std::memcpy(entry + 8, &startLba, 4);
            const std::uint32_t count32 = static_cast<std::uint32_t>(countLba);
            std::memcpy(entry + 12, &count32, 4);
        }
        sector[510] = 0x55;
        sector[511] = 0xAA;

        if (!VhdWriteBytes(devicePath, 0, sector.data(), sectorBytes, error)) return false;

        std::vector<std::uint8_t> check(sectorBytes, 0);
        if (!VhdReadBytes(devicePath, 0, check.data(), sectorBytes, error)) return false;
        if (check[510] != 0x55 || check[511] != 0xAA || std::memcmp(check.data(), sector.data(), 446) != 0)
        {
            error = "MBR 回读校验失败";
            return false;
        }
        return true;
    }

    bool VhdReadMbr(const std::string& devicePath,
                    std::vector<VhdPartition>& parts,
                    std::uint32_t sectorSize,
                    std::string& error)
    {
        if (sectorSize == 0) sectorSize = 512;
        parts.clear();
        if (!VhdEnsureVirtualDisk(devicePath, error)) return false;
        std::vector<std::uint8_t> sector(sectorSize, 0);
        if (!VhdReadBytes(devicePath, 0, sector.data(), sectorSize, error)) return false;
        if (sector[510] != 0x55 || sector[511] != 0xAA)
        {
            error = "扇区 0 没有 MBR 签名";
            return false;
        }
        for (std::size_t i = 0; i < 4; ++i)
        {
            const std::uint8_t* entry = sector.data() + 446 + 16 * i;
            const std::uint8_t type = entry[4];
            if (type == 0) continue;
            std::uint32_t start32 = 0, count32 = 0;
            std::memcpy(&start32, entry + 8, 4);
            std::memcpy(&count32, entry + 12, 4);
            VhdPartition p;
            p.name = "partition" + std::to_string(i + 1);
            p.mbrType = type;
            p.bootable = (entry[0] & 0x80) != 0;
            p.offsetBytes = static_cast<std::uint64_t>(start32) * sectorSize;
            p.sizeBytes = static_cast<std::uint64_t>(count32) * sectorSize;
            parts.push_back(p);
        }
        return true;
    }


    // ================= 挂载状态与强制卸载 =================
    namespace
    {
        std::string WideToUtf8Text(const std::wstring& text)
        {
            if (text.empty()) return std::string();
            const int need = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, nullptr, 0, nullptr, nullptr);
            if (need <= 1) return std::string();
            std::string out(static_cast<std::size_t>(need - 1), '\0');
            WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, &out[0], need, nullptr, nullptr);
            return out;
        }

        std::wstring ProcessImagePathW(unsigned long pid)
        {
            HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
            if (process == nullptr) return std::wstring();
            wchar_t buffer[1024];
            DWORD size = 1024;
            std::wstring out;
            if (QueryFullProcessImageNameW(process, 0, buffer, &size)) out.assign(buffer, size);
            CloseHandle(process);
            return out;
        }

        std::wstring LowerName(const std::wstring& path)
        {
            const std::size_t at = path.find_last_of(L"\\/");
            std::wstring name = (at == std::wstring::npos) ? path : path.substr(at + 1);
            for (wchar_t& ch : name) ch = static_cast<wchar_t>(towlower(ch));
            return name;
        }

        // 只结束我们自己的工具进程：不动系统与其他软件。
        bool IsKillableImage(const std::wstring& path)
        {
            const std::wstring name = LowerName(path);
            return name == L"ocid_imagegen.exe" || name == L"nsudo.exe" || name == L"nsudoc.exe" ||
                   name == L"nsudog.exe" || name == L"dism.exe";
        }
    }

    bool VhdIsAttached(const std::wstring& imagePath, bool& attached, std::string& error)
    {
        attached = false;
        HANDLE file = CreateFileW(imagePath.c_str(), GENERIC_READ, 0, nullptr, OPEN_EXISTING,
                                  FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file != INVALID_HANDLE_VALUE)
        {
            CloseHandle(file);
            return true;
        }
        const DWORD rc = GetLastError();
        if (rc == ERROR_SHARING_VIOLATION || rc == ERROR_LOCK_VIOLATION)
        {
            attached = true;
            return true;
        }
        if (rc == ERROR_FILE_NOT_FOUND || rc == ERROR_PATH_NOT_FOUND)
        {
            error = "镜像文件不存在";
            return false;
        }
        error = LastErrorText("检查镜像占用");
        return false;
    }

    bool VhdHolders(const std::wstring& imagePath, std::vector<unsigned long>& pids, std::string& error)
    {
        pids.clear();
        DWORD session = 0;
        wchar_t key[CCH_RM_SESSION_KEY + 1] = {};
        DWORD rc = RmStartSession(&session, 0, key);
        if (rc != ERROR_SUCCESS)
        {
            error = "RmStartSession 失败（win32=" + std::to_string(rc) + "）";
            return false;
        }
        const wchar_t* files[1] = { imagePath.c_str() };
        rc = RmRegisterResources(session, 1, files, 0, nullptr, 0, nullptr);
        if (rc != ERROR_SUCCESS)
        {
            RmEndSession(session);
            error = "RmRegisterResources 失败（win32=" + std::to_string(rc) + "）";
            return false;
        }
        UINT needed = 0;
        UINT count = 0;
        rc = RmGetList(session, &needed, &count, nullptr, nullptr);
        if (rc == ERROR_SUCCESS && needed == 0)
        {
            RmEndSession(session);
            return true;
        }
        if (rc != ERROR_MORE_DATA && rc != ERROR_SUCCESS)
        {
            RmEndSession(session);
            error = "RmGetList 失败（win32=" + std::to_string(rc) + "）";
            return false;
        }
        std::vector<RM_PROCESS_INFO> info(static_cast<std::size_t>(needed) + 4);
        count = static_cast<UINT>(info.size());
        rc = RmGetList(session, &needed, &count, info.data(), nullptr);
        if (rc != ERROR_SUCCESS)
        {
            RmEndSession(session);
            error = "RmGetList 失败（win32=" + std::to_string(rc) + "）";
            return false;
        }
        for (UINT k = 0; k < count; ++k) pids.push_back(static_cast<unsigned long>(info[k].Process.dwProcessId));
        RmEndSession(session);
        return true;
    }

    bool VhdForceDetach(const std::wstring& imagePath, bool killHolders,
                        std::vector<std::string>& report, std::string& error)
    {
        error.clear();
        bool attached = false;
        if (!VhdIsAttached(imagePath, attached, error)) return false;
        if (!attached)
        {
            report.push_back("镜像未处于挂载状态");
            return true;
        }

        DWORD lastDetachRc = ERROR_SUCCESS;
        for (int attempt = 0; attempt < 3; ++attempt)
        {
            HANDLE handle = INVALID_HANDLE_VALUE;
            std::string detachError;
            if (OpenVirtual(imagePath, VIRTUAL_DISK_ACCESS_DETACH, handle, detachError))
            {
                const DWORD rc = DetachVirtualDisk(handle, DETACH_VIRTUAL_DISK_FLAG_NONE, 0);
                lastDetachRc = rc;
                // 对 PERMANENT_LIFETIME 挂载的盘还要按永久语义再卸一次（此 SDK 头里没有该枚举值，取值 1）。
                DetachVirtualDisk(handle, static_cast<DETACH_VIRTUAL_DISK_FLAG>(1), 0);
                CloseHandle(handle);
                char text[256];
                std::snprintf(text, sizeof(text), "DetachVirtualDisk rc=%lu", static_cast<unsigned long>(rc));
                report.push_back(text);
            }
            else
            {
                report.push_back("打开镜像失败：" + detachError);
            }

            if (!VhdIsAttached(imagePath, attached, error)) return false;
            if (!attached)
            {
                report.push_back("已卸载");
                return true;
            }

            std::vector<unsigned long> holders;
            std::string holderError;
            if (VhdHolders(imagePath, holders, holderError))
            {
                for (unsigned long pid : holders)
                {
                    const std::wstring image = ProcessImagePathW(pid);
                    std::string line = "占用进程 pid=" + std::to_string(pid) + " " +
                                       (image.empty() ? std::string("(无法读取)") : WideToUtf8Text(image));
                    report.push_back(line);
                    if (killHolders && IsKillableImage(image))
                    {
                        HANDLE process = OpenProcess(PROCESS_TERMINATE, FALSE, pid);
                        if (process != nullptr)
                        {
                            const BOOL killed = TerminateProcess(process, 0xE0000001);
                            CloseHandle(process);
                            report.push_back(killed ? "已结束占用进程"
                                                    : "结束占用进程失败");
                        }
                    }
                }
            }
            else
            {
                report.push_back("查询占用进程失败：" + holderError);
            }
            Sleep(300);
        }

        if (!VhdIsAttached(imagePath, attached, error)) return false;
        if (!attached)
        {
            report.push_back("已卸载");
            return true;
        }
        // DetachVirtualDisk returns 21 (ERROR_NOT_READY) when already detached.
        if (lastDetachRc == ERROR_NOT_READY)
        {
            report.push_back("detach: kernel reported not-attached (rc=21); treated as detached");
            return true;
        }
        error = "镜像仍处于挂载状态"
                "（可能有内核侧 space/池依赖"
                "，需重启或先 detach space）";
        return false;
    }


}


}
