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

        bool OpenVirtual(const std::wstring& path, VIRTUAL_DISK_ACCESS_MASK access,
                         HANDLE& handle, std::string& error)
        {
            VIRTUAL_STORAGE_TYPE storage{};
            storage.DeviceId = VIRTUAL_STORAGE_TYPE_DEVICE_VHDX;
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

    bool VhdCreate(const std::wstring& path,
                   std::uint64_t virtualBytes,
                   VhdKind kind,
                   std::uint32_t blockSize,
                   std::uint32_t physicalSectorSize,
                   std::string& error)
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
        VIRTUAL_STORAGE_TYPE storage{};
        storage.DeviceId = VIRTUAL_STORAGE_TYPE_DEVICE_VHDX;
        storage.VendorId = VendorMicrosoft();

        CREATE_VIRTUAL_DISK_PARAMETERS params{};
        params.Version = CREATE_VIRTUAL_DISK_VERSION_2;
        params.Version2.UniqueId = GUID_NULL;
        params.Version2.MaximumSize = virtualBytes;
        params.Version2.BlockSizeInBytes = (blockSize == 0) ? 0 : blockSize;
        params.Version2.SectorSizeInBytes = (physicalSectorSize == 0) ? 512 : physicalSectorSize;
        params.Version2.PhysicalSectorSizeInBytes = params.Version2.SectorSizeInBytes;

        HANDLE handle = INVALID_HANDLE_VALUE;
        const CREATE_VIRTUAL_DISK_FLAG flags =
            (kind == VhdKind::Fixed) ? CREATE_VIRTUAL_DISK_FLAG_FULL_PHYSICAL_ALLOCATION : CREATE_VIRTUAL_DISK_FLAG_NONE;
        const DWORD rc = CreateVirtualDisk(&storage, path.c_str(), VIRTUAL_DISK_ACCESS_NONE, nullptr,
                                           flags, 0, &params, nullptr, &handle);
        if (rc != ERROR_SUCCESS)
        {
            error = LastErrorText("CreateVirtualDisk");
            return false;
        }
        CloseHandle(handle);
        return true;
    }

    bool VhdAttach(const std::wstring& path, bool readOnly, std::string& devicePath, std::string& error)
    {
        HANDLE handle = INVALID_HANDLE_VALUE;
        if (!OpenVirtual(path, VIRTUAL_DISK_ACCESS_ALL, handle, error)) return false;

        ATTACH_VIRTUAL_DISK_PARAMETERS params{};
        params.Version = ATTACH_VIRTUAL_DISK_VERSION_1;
        // 用 PERMANENT 挂载：CLI 的 attach / detach 是两条独立命令，非永久挂载会在
        // 进程退出时自动消失。
        const ATTACH_VIRTUAL_DISK_FLAG attachFlags = static_cast<ATTACH_VIRTUAL_DISK_FLAG>(
            ATTACH_VIRTUAL_DISK_FLAG_PERMANENT_LIFETIME |
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
        CloseHandle(handle);
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

    bool VhdWriteGpt(const std::string& devicePath,
                     const std::string& diskId,
                     const std::vector<VhdPartition>& parts,
                     std::uint32_t sectorSize,
                     std::string& error)
    {
        if (parts.empty())
        {
            error = "分区表为空";
            return false;
        }
        if (sectorSize == 0) sectorSize = 512;

        if (!VhdEnsureVirtualDisk(devicePath, error))
            return false;

        const std::wstring wide(devicePath.begin(), devicePath.end());
        HANDLE disk = CreateFileW(wide.c_str(), GENERIC_READ | GENERIC_WRITE,
                                  FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr);
        if (disk == INVALID_HANDLE_VALUE)
        {
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
        CloseHandle(disk);
        if (!ok)
        {
            error = LastErrorText("IOCTL_DISK_SET_DRIVE_LAYOUT_EX");
            return false;
        }
        return true;
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

}


}
