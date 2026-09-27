using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml.Linq;
using MobilePackageGen;
using MobilePackageGen.Adapters;
using Img2Ffu.Reader.Data;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// 固件服务：提供分区类型识别、raw分区提取、UEFI变量解析等功能
    /// 共享给FFU浏览器和系统重构使用
    /// </summary>
    public static class FirmwareService
    {
        #region 分区类型识别表

        /// <summary>
        /// 分区类型GUID映射表：GUID → (名称, 简短描述, 详细说明, 是否为固件分区)
        /// </summary>
        private static readonly Dictionary<Guid, (string Name, string Description, string Detail, bool IsFirmware)> _partitionTypeMap = new()
        {
            // 高通固件分区
            { new Guid("DEA0BA2C-CBDD-4805-B4F9-F428251C3E98"), ("XBL", "可扩展引导加载程序 (UEFI BIOS)", "XBL (eXtensible Boot Loader) 是高通平台的UEFI BIOS实现。负责硬件初始化、启动管理器、安全启动验证。包含PEI/DXE阶段固件，通常为ELF或UEFI FV格式。是设备启动的第一个可执行固件。", true) },
            { new Guid("5A325AE4-4276-B66D-0ADD-3494DF27706A"), ("xbl_config", "XBL配置数据", "XBL配置分区存储UEFI变量、启动配置、安全策略等运行时配置数据。与XBL分离，便于单独更新配置而不重刷BIOS。", true) },
            { new Guid("D69E90A5-4CAB-0071-F6DF-AB977F141A7F"), ("AOP", "始终在线处理器固件 (电源管理/传感器)", "AOP (Always On Processor) 是高通平台的低功耗协处理器固件。负责电源管理、传感器集线器、按键检测、振动控制等。即使主CPU休眠时AOP仍在运行。", true) },
            { new Guid("A053AA7F-40B8-4B1C-BA08-2F68AC71A4F4"), ("TZ", "可信执行环境固件 (TrustZone)", "TZ (TrustZone) 是ARM可信执行环境固件。运行在安全世界，负责密钥管理、加密操作、安全支付、DRM、指纹认证等安全敏感功能。与普通世界操作系统隔离。", true) },
            { new Guid("E1A6A689-0C8D-4CC6-B4E8-55A4320FBD8A"), ("HYP", "虚拟机监控程序固件 (Hypervisor)", "HYP (Hypervisor) 是虚拟化层固件。负责创建和管理虚拟机，隔离不同执行环境（如可信OS与普通OS）。在支持虚拟化的高通平台上运行于EL2特权级。", true) },
            { new Guid("7EFE5010-2A1A-4A1A-B8BC-990257813512"), ("DSP", "数字信号处理器固件", "DSP (Digital Signal Processor) 固件负责音频处理、语音识别、降噪、回声消除等信号处理任务。独立于主CPU运行，低功耗高性能。", true) },
            { new Guid("C00EEF24-7709-43D6-9799-DD2B411E7A3C"), ("PMIC", "电源管理芯片固件", "PMIC (Power Management IC) 固件控制电源管理芯片的电压调节、充电管理、电池监控、电源序列等。确保各硬件模块获得正确的电压和时序。", true) },
            { new Guid("F65D4B16-343D-4E25-AAFC-BE99B6556A6D"), ("DEVCFG", "设备配置数据 (GPIO/时钟)", "DEVCFG (Device Configuration) 存储硬件平台的设备配置数据，包括GPIO引脚映射、时钟树配置、总线配置、外设寄存器初始值等。由BootLoader读取用于硬件初始化。", true) },
            { new Guid("14D11C40-2A3D-4F97-882D-103A1EC09333"), ("TZAPPS", "可信区应用 (安全支付/DRM)", "TZAPPS (TrustZone Applications) 存储运行在TrustZone安全世界中的可信应用。包括安全支付、数字版权管理(DRM)、安全存储、生物识别认证等安全敏感应用。", true) },
            { new Guid("17911177-C9E6-4372-933C-804B678E666F"), ("RESERVEDRO", "保留只读分区", "RESERVEDRO (Reserved Read-Only) 是厂商保留的只读分区，用于存储未来扩展或特定厂商功能。通常不被普通系统访问，内容由厂商定义。", true) },
            { new Guid("21D1219F-2ED1-4AB4-930A-41A16AE75F7F"), ("QUP", "高通通用外设固件", "QUP (Qualcomm Universal Peripheral) 固件管理高通平台的通用外设接口，包括I2C、SPI、UART等串行总线的低功耗管理和DMA传输。", true) },
            { new Guid("11406F35-1173-4869-807B-27DF71802812"), ("DPO", "设备编程对象", "DPO (Device Programming Object) 存储设备编程和配置数据，用于工厂编程和设备个性化设置。包含设备特定的校准数据、序列号、MAC地址等。", true) },
            { new Guid("A3381699-350C-465E-BD5D-FA3AB901A39A"), ("BACKUP", "固件备份分区", "BACKUP分区存储主固件分区的备份副本。当主固件损坏或更新失败时，系统可以从备份分区启动，实现A/B更新或故障恢复。", true) },
            { new Guid("97745ABA-135A-44C3-9ADC-05616173C24C"), ("TOOLSFV", "工具固件卷", "TOOLSFV (Tools Firmware Volume) 存储工厂工具和诊断固件。包括生产测试工具、硬件诊断程序、烧录工具等。仅在工厂模式或工程模式下使用。", true) },
            { new Guid("F0B4F48B-AEBA-4ECF-9142-5DC30CDC3E77"), ("UEFI_BS_NV", "UEFI引导服务非易失性存储 (UEFI变量)", "UEFI_BS_NV存储UEFI引导服务的非易失性变量。包括启动顺序、安全启动密钥、平台配置等。注意：FFU镜像中此分区通常为空，UEFI变量在设备首次启动时运行时写入。", true) },
            { new Guid("2C86E742-745E-4FDD-BFD8-B6A7AC638772"), ("SSD", "安全软件下载配置", "SSD (Secure Software Download) 存储安全软件下载的配置和密钥。用于验证和授权固件更新、工厂烧录等操作，防止未经授权的软件写入。", true) },
            { new Guid("303E6AC3-AF15-4C54-9E9B-D9A8FBECF401"), ("SEC", "安全配置分区", "SEC (Security Configuration) 存储平台安全配置数据，包括安全启动策略、调试权限控制、安全熔丝配置等。决定设备的安全级别和可调试性。", true) },
            { new Guid("E6E98DA2-E22A-4D12-AB33-169E7DEAA507"), ("APDP", "应用调试策略", "APDP (Application Debug Policy) 存储应用层调试策略配置。控制哪些应用可以被调试、调试权限级别、调试端口开关等。用于保护应用安全。", true) },
            { new Guid("ED9E8101-05FA-46B7-82AA-8D58770D200B"), ("MSADP", "调制解调器安全认证调试策略", "MSADP (Modem Secure Authentication Debug Policy) 存储调制解调器子系统的安全认证和调试策略。控制基带固件的调试权限和安全认证，防止基带被篡改。", true) },
            { new Guid("0E91AB96-8339-4AE4-A310-ED94357FD7D0"), ("BOOTMODE", "引导模式配置", "BOOTMODE存储引导模式配置数据。决定设备启动时进入的模式（正常启动、恢复模式、快速启动、工厂模式等）。由BootLoader读取。", true) },
            { new Guid("BC0330EB-3410-4951-A617-03898DBE3372"), ("LOGFS", "日志文件系统", "LOGFS (Log File System) 是专用的日志文件系统分区。用于存储系统日志、崩溃转储、调试信息等。支持掉电安全的日志写入。", false) },
            { new Guid("543C031A-4CB6-4897-BFFE-4B485768A8AD"), ("PLAT", "平台数据分区", "PLAT (Platform Data) 存储平台特定的配置数据，包括硬件版本、校准数据、设备特性等。由驱动程序读取用于适配不同硬件版本。", false) },
            { new Guid("66C9B323-F7FC-48B6-BF96-6F32E335A428"), ("SVRawDump", "芯片验证原始转储", "SVRawDump (Silicon Validation Raw Dump) 用于芯片验证阶段的原始数据转储。存储硬件验证测试的原始数据，仅在工程样本和验证阶段使用，量产设备通常为空或未使用。", true) },

            // 标准UEFI/Windows分区
            { new Guid("C12A7328-F81F-11D2-BA4B-00A0C93EC93B"), ("EFIESP", "EFI系统分区 (ESP)", "EFIESP (EFI System Partition) 是UEFI标准的EFI系统分区。FAT32格式，存储UEFI引导加载程序、启动配置、驱动程序等。是UEFI系统启动必需的分区。", false) },
            { new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7"), ("BasicData", "基本数据分区", "BasicData是Windows标准的基本数据分区类型。通常格式化为NTFS，存储操作系统、应用程序、用户数据等。是Windows系统中最常见的分区类型。", false) },
            { new Guid("E75CAF8F-F680-4CEE-AFA3-B001E56EFC2D"), ("StoragePool", "存储池分区 (Spaces)", "StoragePool是Windows Storage Spaces的存储池分区。包含多个物理磁盘的聚合，支持弹性、精简配置、分层存储等。内部包含虚拟磁盘和文件系统。", false) },

            // WCOS专用分区类型 (57434f53开头)
            { new Guid("57434F53-E3E3-4631-A5C5-26D2243873AA"), ("WCOS_EFIESP", "WCOS EFI系统分区", "WCOS专用的EFI系统分区类型，用于Windows Core OS的UEFI引导。与标准EFIESP功能相同，但使用WCOS专用GUID，便于WCOS镜像工具识别和管理。", false) },
            { new Guid("57434F53-8F45-405E-8A23-186D8A4330D3"), ("WCOS_MainOS", "WCOS主操作系统分区", "WCOS专用的主操作系统分区类型，包含Windows Core OS核心系统文件。使用WCOS专用GUID，支持BitLocker加密和状态分离，是WCOS系统运行的核心分区。", false) },
            { new Guid("57434F53-4DF9-45B9-8E9E-2370F006457C"), ("WCOS_BSP", "WCOS板级支持包分区", "WCOS专用的板级支持包分区，存储硬件平台特定的驱动程序和固件配置。使用WCOS专用GUID，支持BitLocker加密，与主系统分离便于单独更新。", false) },
            { new Guid("57434F53-7FE0-4196-9B42-427B51643484"), ("WCOS_PreInstalled", "WCOS预安装应用分区", "WCOS专用的预安装应用分区，存储设备出厂时预装的应用程序。使用WCOS专用GUID，与系统和用户数据分离，便于应用单独更新和管理。", false) },
            { new Guid("57434F53-23F2-44D5-A830-67BBDAA609F9"), ("WCOS_OSData", "WCOS操作系统数据分区", "WCOS专用的操作系统数据分区，存储系统运行时数据和配置。使用WCOS专用GUID，与主系统分离，系统更新时不会影响此分区的数据。", false) },
            { new Guid("5708A6E0-9001-4B99-B064-1FE564896BDB"), ("WCOS_OSPool", "WCOS操作系统存储池", "WCOS专用的操作系统存储池分区，是Storage Spaces的存储池，内部包含EFIESP、MainOS、BSP、PreInstalled、OSData、Data等多个虚拟磁盘分区。实现WCOS的状态分离和弹性存储。", false) },
        };

        /// <summary>
        /// 获取分区类型信息
        /// </summary>
        public static (string Name, string Description, string Detail, bool IsFirmware) GetPartitionTypeInfo(Guid typeGuid)
        {
            if (_partitionTypeMap.TryGetValue(typeGuid, out var info))
            {
                return info;
            }
            return ("未知", $"未知分区类型 ({typeGuid})", "未识别的分区类型。可能是厂商自定义分区或新的标准分区类型。建议查看厂商文档或分区表获取更多信息。", false);
        }

        /// <summary>
        /// 获取分区显示名称（包含描述）
        /// </summary>
        public static string GetPartitionDisplayName(string partitionName, Guid typeGuid)
        {
            var info = GetPartitionTypeInfo(typeGuid);
            if (string.IsNullOrEmpty(partitionName))
            {
                return info.Name;
            }
            // 如果分区名和类型名相同，只显示描述
            if (partitionName.Equals(info.Name, StringComparison.OrdinalIgnoreCase))
            {
                return $"{partitionName} ({info.Description})";
            }
            return $"{partitionName} [{info.Name}] ({info.Description})";
        }

        /// <summary>
        /// 判断是否为固件分区
        /// </summary>
        public static bool IsFirmwarePartition(Guid typeGuid)
        {
            return GetPartitionTypeInfo(typeGuid).IsFirmware;
        }

        #endregion

        #region 固件格式识别

        /// <summary>
        /// 固件格式类型
        /// </summary>
        public enum FirmwareFormat
        {
            Unknown,        // 未知格式
            Empty,          // 空分区（全0）
            Erased,         // 已擦除（全FF）
            UefiFirmwareVolume, // UEFI固件卷（FV）
            Elf,            // ELF可执行文件
            PeCoff,         // PE/COFF可执行文件（MZ头）
            QualcommMbn,    // 高通MBN格式
            DcmCompressed,  // DCM压缩格式
            UefiVariableStore, // UEFI变量存储
            RawData,        // 原始数据（无特定格式）
        }

        /// <summary>
        /// 固件格式信息
        /// </summary>
        public class FirmwareFormatInfo
        {
            public FirmwareFormat Format { get; set; }
            public string FormatName { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public string? MagicHex { get; set; }
            public long DataSize { get; set; }
            public long NonZeroSize { get; set; }
        }

        /// <summary>
        /// 识别分区的固件格式（读取魔数判断）
        /// </summary>
        public static FirmwareFormatInfo DetectFirmwareFormat(IPartition partition)
        {
            var info = new FirmwareFormatInfo();
            try
            {
                if (partition == null || partition.Stream == null)
                {
                    info.Format = FirmwareFormat.Unknown;
                    info.FormatName = "未知";
                    info.Description = "分区数据不可访问";
                    return info;
                }

                Stream stream = partition.Stream;
                info.DataSize = stream.Length;
                stream.Seek(0, SeekOrigin.Begin);

                // 读取前512字节用于魔数识别
                int headerSize = (int)Math.Min(512, stream.Length);
                byte[] header = new byte[headerSize];
                int read = stream.Read(header, 0, headerSize);

                if (read == 0 || stream.Length == 0)
                {
                    info.Format = FirmwareFormat.Empty;
                    info.FormatName = "空分区";
                    info.Description = "分区大小为0或无数据";
                    return info;
                }

                // 统计非零字节（用于判断空分区/已擦除）
                long nonZero = 0;
                // 只扫描前64KB判断是否为空，避免大分区卡顿
                int scanSize = (int)Math.Min(64 * 1024, stream.Length);
                stream.Seek(0, SeekOrigin.Begin);
                byte[] scanBuffer = new byte[scanSize];
                int scanRead = stream.Read(scanBuffer, 0, scanSize);
                for (int i = 0; i < scanRead; i++)
                {
                    if (scanBuffer[i] != 0) nonZero++;
                }
                info.NonZeroSize = nonZero;

                // 检查是否全0（空分区）
                if (nonZero == 0)
                {
                    info.Format = FirmwareFormat.Empty;
                    info.FormatName = "空分区";
                    info.Description = "分区内容全为0，可能是未使用的保留分区";
                    info.MagicHex = "00 00 00 00 ...";
                    return info;
                }

                // 检查是否全FF（已擦除）
                bool allFF = true;
                for (int i = 0; i < Math.Min(1024, scanRead); i++)
                {
                    if (scanBuffer[i] != 0xFF) { allFF = false; break; }
                }
                if (allFF)
                {
                    info.Format = FirmwareFormat.Erased;
                    info.FormatName = "已擦除";
                    info.Description = "分区内容全为0xFF，处于Flash已擦除状态，可能是未使用的备份分区";
                    info.MagicHex = "FF FF FF FF ...";
                    return info;
                }

                // 魔数识别
                // 1. ELF: 7F 45 4C 46
                if (headerSize >= 4 && header[0] == 0x7F && header[1] == 0x45 && header[2] == 0x4C && header[3] == 0x46)
                {
                    info.Format = FirmwareFormat.Elf;
                    info.FormatName = "ELF可执行文件";
                    info.Description = "ELF (Executable and Linkable Format) 可执行文件格式，常见于XBL、TZ、HYP等ARM固件";
                    info.MagicHex = "7F 45 4C 46";
                    return info;
                }

                // 2. PE/COFF: 4D 5A (MZ)
                if (headerSize >= 2 && header[0] == 0x4D && header[1] == 0x5A)
                {
                    info.Format = FirmwareFormat.PeCoff;
                    info.FormatName = "PE/COFF可执行文件";
                    info.Description = "PE (Portable Executable) / COFF 可执行文件格式，常见于UEFI驱动和DXE模块";
                    info.MagicHex = "4D 5A (MZ)";
                    return info;
                }

                // 3. UEFI固件卷: FFS文件系统魔数在偏移0处为 _FVH (5F 46 56 48)，或者FV签名
                if (headerSize >= 48)
                {
                    // FV签名在偏移40处: 5F 46 56 48 (_FVH)
                    if (header[40] == 0x5F && header[41] == 0x46 && header[42] == 0x56 && header[43] == 0x48)
                    {
                        info.Format = FirmwareFormat.UefiFirmwareVolume;
                        info.FormatName = "UEFI固件卷 (FV)";
                        info.Description = "UEFI Firmware Volume，包含PEI/DXE模块、UEFI驱动等固件组件，常见于TOOLSFV等分区";
                        info.MagicHex = "5F 46 56 48 (_FVH) @偏移40";
                        return info;
                    }
                    // FV头签名: D9 54 93 7A (在偏移0处)
                    if (header[0] == 0xD9 && header[1] == 0x54 && header[2] == 0x93 && header[3] == 0x7A)
                    {
                        info.Format = FirmwareFormat.UefiFirmwareVolume;
                        info.FormatName = "UEFI固件卷 (FV)";
                        info.Description = "UEFI Firmware Volume，包含PEI/DXE模块、UEFI驱动等固件组件";
                        info.MagicHex = "D9 54 93 7A";
                        return info;
                    }
                }

                // 4. DCM压缩: 44 43 4D 01 50 41 33 30 (DCM\x01PA30)
                if (headerSize >= 8 && header[0] == 0x44 && header[1] == 0x43 && header[2] == 0x4D && header[3] == 0x01 &&
                    header[4] == 0x50 && header[5] == 0x41 && header[6] == 0x33 && header[7] == 0x30)
                {
                    info.Format = FirmwareFormat.DcmCompressed;
                    info.FormatName = "DCM压缩格式";
                    info.Description = "DCM (Delta Compression Method) 压缩格式，微软用于manifest和系统文件压缩";
                    info.MagicHex = "44 43 4D 01 50 41 33 30";
                    return info;
                }

                // 5. UEFI变量存储: 53 54 41 52 54 5F 56 41 52 (START_VAR) 或 VSS签名
                if (headerSize >= 9)
                {
                    string headerStr = Encoding.ASCII.GetString(header, 0, Math.Min(16, headerSize));
                    if (headerStr.StartsWith("START_VAR") || headerStr.StartsWith("VSS"))
                    {
                        info.Format = FirmwareFormat.UefiVariableStore;
                        info.FormatName = "UEFI变量存储";
                        info.Description = "UEFI变量存储格式，包含启动顺序、安全启动密钥等UEFI运行时变量";
                        info.MagicHex = Encoding.ASCII.GetString(header, 0, 8);
                        return info;
                    }
                }

                // 6. 高通MBN格式: 通常以特定的SBL头开头，检查是否有"QCOM"或MBN特征
                if (headerSize >= 4)
                {
                    string ascii4 = Encoding.ASCII.GetString(header, 0, 4);
                    if (ascii4 == "QCOM" || ascii4 == "SBL1" || ascii4 == "XBL_")
                    {
                        info.Format = FirmwareFormat.QualcommMbn;
                        info.FormatName = "高通MBN固件";
                        info.Description = "Qualcomm MBN (Mobile Binary) 固件格式，高通平台的引导加载程序和固件镜像";
                        info.MagicHex = ascii4;
                        return info;
                    }
                }

                // 无法识别，显示前16字节
                info.Format = FirmwareFormat.RawData;
                info.FormatName = "原始数据";
                info.Description = "无法识别的固件格式，可能是厂商自定义格式或加密数据";
                info.MagicHex = BitConverter.ToString(header, 0, Math.Min(16, headerSize)).Replace("-", " ");
                return info;
            }
            catch (Exception ex)
            {
                info.Format = FirmwareFormat.Unknown;
                info.FormatName = "识别失败";
                info.Description = $"格式识别出错: {ex.Message}";
                return info;
            }
        }

        #endregion

        #region WCOS分区名称说明

        /// <summary>
        /// WCOS常见分区名称说明表：分区名 → (简短描述, 详细说明)
        /// 用于类型GUID为BasicData等通用类型但名称有特定含义的分区
        /// </summary>
        private static readonly Dictionary<string, (string Description, string Detail)> _wcosPartitionNameMap = new(StringComparer.OrdinalIgnoreCase)
        {
            { "MainOS", ("主操作系统分区", "MainOS是Windows Core OS的主系统分区，包含Windows核心系统文件、系统注册表、驱动程序、系统服务等。是设备启动后运行的主要操作系统分区，相当于桌面Windows的C盘Windows目录。") },
            { "BSP", ("板级支持包分区", "BSP (Board Support Package) 分区存储板级支持包文件，包含硬件平台特定的驱动程序、固件配置、平台适配层等。由OEM/硬件厂商提供，用于让Windows适配特定硬件平台。") },
            { "PreInstalled", ("预安装应用分区", "PreInstalled分区存储预安装的应用程序和应用数据。设备出厂时预装的应用（如系统工具、OEM应用等）存放在此分区，与主系统分离，便于单独更新和管理。") },
            { "OSData", ("操作系统数据分区", "OSData分区存储操作系统运行时数据和配置，包括系统状态、临时文件、系统缓存、运行时配置等。与MainOS分离，系统更新时不会影响此分区的数据。") },
            { "Data", ("用户数据分区", "Data分区是用户数据分区，存储用户文件、应用数据、用户配置、下载内容等。是用户可访问的主要数据存储区域，相当于桌面Windows的C盘Users目录。设备重置时此分区数据可能被清除。") },
            { "DPP", ("设备平台配置分区", "DPP (Device Platform Profile) 分区存储设备特定的平台配置数据，包括硬件配置、设备特性、校准数据、生产测试数据等。由工厂编程，每台设备可能不同。") },
            { "EFIESP", ("EFI系统分区", "EFIESP (EFI System Partition) 是UEFI标准的EFI系统分区，FAT32格式。存储UEFI引导加载程序、启动配置数据库(BCD)、UEFI驱动程序、启动相关文件。是UEFI系统启动的必需分区。") },
            { "PLAT", ("平台数据分区", "PLAT (Platform Data) 分区存储平台特定的配置数据，包括硬件版本信息、传感器校准数据、设备特性标志、平台特定参数等。由驱动程序读取用于适配不同硬件版本。") },
            { "LogFS", ("日志文件系统分区", "LogFS (Log File System) 是专用的日志文件系统分区，用于存储系统日志、崩溃转储、调试信息、事件追踪等。支持掉电安全的日志写入，确保系统崩溃时日志数据不丢失。") },
            { "SVRawDump", ("芯片验证原始转储分区", "SVRawDump (Silicon Validation Raw Dump) 用于芯片验证阶段的原始数据转储。存储硬件验证测试的原始数据和诊断信息。仅在工程样本和验证阶段使用，量产设备通常为空或未使用。") },
            { "UpdateOS", ("更新操作系统分区", "UpdateOS是用于系统更新的最小操作系统环境。系统更新时启动到UpdateOS，在独立环境中安装更新包，避免主系统被更新过程中断。通常以WIM镜像形式存储，不是独立的物理分区。") },
            { "MainOS-UpdateOS-0", ("UpdateOS WIM内容", "这是从UpdateOS.wim中提取的虚拟分区，不是真实的物理分区。包含UpdateOS更新环境的文件系统内容，用于系统更新时启动到独立更新环境。大小通常很小(几十KB)。") },
            { "TZAPPS", ("可信区应用分区", "TZAPPS (TrustZone Applications) 存储运行在ARM TrustZone安全世界中的可信应用，包括安全支付、数字版权管理(DRM)、安全存储、生物识别认证、密钥管理等安全敏感应用。与普通操作系统隔离。") },
            { "TOOLSFV", ("工具固件卷分区", "TOOLSFV (Tools Firmware Volume) 存储工厂工具和诊断固件，包括生产测试工具、硬件诊断程序、烧录工具、工程模式工具等。仅在工厂模式或工程模式下使用，正常用户模式下不可访问。") },
            { "UEFI_BS_NV", ("UEFI引导服务非易失存储", "UEFI_BS_NV存储UEFI引导服务的非易失性变量，包括启动顺序、安全启动密钥、平台配置、UEFI设置等。注意：FFU镜像中此分区通常为空，UEFI变量在设备首次启动时运行时写入。") },
            { "RESERVEDRO", ("保留只读分区", "RESERVEDRO (Reserved Read-Only) 是厂商保留的只读分区，用于存储未来扩展功能或特定厂商功能。通常不被普通系统访问，内容由厂商定义，可能包含预留的固件或配置数据。") },
            { "BOOTMODE", ("引导模式配置分区", "BOOTMODE分区存储引导模式配置数据，决定设备启动时进入的模式（正常启动、恢复模式、快速启动、工厂模式、工程模式等）。由BootLoader读取，用于控制启动流程。") },
            { "SEC", ("安全配置分区", "SEC (Security Configuration) 分区存储平台安全配置数据，包括安全启动策略、调试权限控制、安全熔丝配置、设备安全级别等。决定设备的安全级别和可调试性。") },
            { "VIRT_EFIESP", ("虚拟EFI系统分区", "VIRT_EFIESP (Virtual EFI System Partition) 是WCOS存储池内的虚拟EFI系统分区。位于OSPool存储池内部，作为虚拟磁盘存在，存储UEFI引导相关文件。与物理BS_EFIESP配合使用。") },
            { "BS_EFIESP", ("引导服务EFI系统分区", "BS_EFIESP (Boot Services EFI System Partition) 是物理磁盘上的EFI系统分区，FAT32格式。存储UEFI引导加载程序和启动配置，是设备启动时首先访问的引导分区。与存储池内的VIRT_EFIESP配合。") },
            { "OSPool", ("操作系统存储池", "OSPool (Operating System Pool) 是WCOS的操作系统存储池，基于Storage Spaces技术。内部包含多个虚拟磁盘分区：EFIESP、MainOS、BSP、PreInstalled、OSData、Data等。实现WCOS的状态分离、弹性存储和统一管理。") },
        };

        /// <summary>
        /// 根据分区名称获取WCOS分区说明
        /// </summary>
        public static (string Description, string Detail)? GetWcosPartitionInfo(string partitionName)
        {
            if (string.IsNullOrEmpty(partitionName)) return null;
            if (_wcosPartitionNameMap.TryGetValue(partitionName, out var info))
                return info;
            return null;
        }

        #endregion

        #region raw分区提取

        /// <summary>
        /// 提取单个分区为bin文件
        /// </summary>
        public static bool ExtractPartitionToBin(IPartition partition, string outputPath)
        {
            try
            {
                if (partition == null || partition.Stream == null) return false;

                using var outputStream = File.Create(outputPath);
                partition.Stream.Seek(0, SeekOrigin.Begin);
                partition.Stream.CopyTo(outputStream);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 批量提取所有固件分区为bin文件
        /// </summary>
        public static int ExtractAllFirmwarePartitions(IEnumerable<IDisk> disks, string outputDirectory)
        {
            int count = 0;
            var manifestEntries = new List<(string FileName, string Name, Guid Type, string Description, long Size, Guid ID, int DiskIndex)>();

            try
            {
                if (!Directory.Exists(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                int diskIndex = 0;
                foreach (var disk in disks)
                {
                    if (disk.Partitions == null) continue;

                    foreach (var partition in disk.Partitions)
                    {
                        try
                        {
                            // 只提取固件分区和无文件系统的分区
                            bool isFirmware = IsFirmwarePartition(partition.Type);
                            bool hasFileSystem = partition.FileSystem != null;

                            if (!isFirmware && hasFileSystem) continue;

                            string fileName = string.IsNullOrEmpty(partition.Name)
                                ? $"partition_{diskIndex}_{partition.Type:N}.bin"
                                : $"{partition.Name}.bin";

                            // 处理文件名中的非法字符
                            foreach (char c in Path.GetInvalidFileNameChars())
                            {
                                fileName = fileName.Replace(c, '_');
                            }

                            string outputPath = Path.Combine(outputDirectory, fileName);

                            if (ExtractPartitionToBin(partition, outputPath))
                            {
                                count++;
                                var typeInfo = GetPartitionTypeInfo(partition.Type);
                                manifestEntries.Add((fileName, partition.Name ?? "", partition.Type, typeInfo.Description, partition.Size, partition.ID, diskIndex));
                            }
                        }
                        catch { }
                    }
                    diskIndex++;
                }

                // 生成固件清单XML
                if (manifestEntries.Count > 0)
                {
                    string manifestPath = Path.Combine(outputDirectory, "firmware_manifest.xml");
                    var sb = new StringBuilder();
                    sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
                    sb.AppendLine("<FirmwareManifest>");
                    sb.AppendLine($"  <Generated>{DateTime.Now:yyyy-MM-dd HH:mm:ss}</Generated>");
                    sb.AppendLine($"  <TotalPartitions>{manifestEntries.Count}</TotalPartitions>");
                    sb.AppendLine("  <Partitions>");
                    foreach (var entry in manifestEntries)
                    {
                        sb.AppendLine("    <Partition>");
                        sb.AppendLine($"      <Name>{System.Security.SecurityElement.Escape(entry.Name)}</Name>");
                        sb.AppendLine($"      <FileName>{entry.FileName}</FileName>");
                        sb.AppendLine($"      <TypeGuid>{entry.Type:B}</TypeGuid>");
                        sb.AppendLine($"      <Description>{System.Security.SecurityElement.Escape(entry.Description)}</Description>");
                        sb.AppendLine($"      <Size>{entry.Size}</Size>");
                        sb.AppendLine($"      <PartitionId>{entry.ID:B}</PartitionId>");
                        sb.AppendLine($"      <DiskIndex>{entry.DiskIndex}</DiskIndex>");
                        sb.AppendLine("    </Partition>");
                    }
                    sb.AppendLine("  </Partitions>");
                    sb.AppendLine("</FirmwareManifest>");
                    File.WriteAllText(manifestPath, sb.ToString(), new UTF8Encoding(false));
                }
            }
            catch { }
            return count;
        }

        #endregion

        #region UEFI变量解析

        /// <summary>
        /// UEFI变量信息
        /// </summary>
        public class UefiVariable
        {
            public string Name { get; set; } = string.Empty;
            public Guid VendorGuid { get; set; }
            public uint Attributes { get; set; }
            public byte[] Data { get; set; } = [];
            public int DataSize => Data?.Length ?? 0;
            public string AttributesDisplay => FormatAttributes(Attributes);
            public string DataHexPreview => Data != null && Data.Length > 0
                ? BitConverter.ToString(Data.Take(Math.Min(32, Data.Length)).ToArray()).Replace("-", " ")
                : "(空)";
            public string DataStringPreview => TryGetString(Data);

            private static string FormatAttributes(uint attrs)
            {
                var parts = new List<string>();
                if ((attrs & 0x00000001) != 0) parts.Add("NV");
                if ((attrs & 0x00000002) != 0) parts.Add("BS");
                if ((attrs & 0x00000004) != 0) parts.Add("RT");
                if ((attrs & 0x00000008) != 0) parts.Add("HW_ERROR_RECORD");
                if ((attrs & 0x00000010) != 0) parts.Add("AUTH_WRITE");
                if ((attrs & 0x00000020) != 0) parts.Add("TIME_BASED_AUTH_WRITE");
                if ((attrs & 0x00000040) != 0) parts.Add("APPEND_WRITE");
                return parts.Count > 0 ? string.Join("+", parts) : "0x" + attrs.ToString("X8");
            }

            private static string TryGetString(byte[]? data)
            {
                if (data == null || data.Length == 0) return "(空)";
                try
                {
                    // 尝试Unicode字符串
                    var text = Encoding.Unicode.GetString(data).TrimEnd('\0');
                    if (!string.IsNullOrEmpty(text) && text.All(c => char.IsLetterOrDigit(c) || char.IsPunctuation(c) || c == ' ' || c == ':' || c == '\\' || c == '/' || c == '.'))
                    {
                        if (text.Length > 64) text = text.Substring(0, 64) + "...";
                        return $"\"{text}\"";
                    }
                }
                catch { }
                return "(二进制)";
            }
        }

        // UEFI变量存储签名
        private static readonly byte[] VARIABLE_STORE_SIGNATURE = [0x4E, 0x56, 0x53, 0x53]; // "NVSS"
        private static readonly byte[] VARIABLE_HEADER_SIGNATURE = [0x55, 0x46, 0x44, 0x32]; // "UFD2" (AuthenticatedVariableHeader)

        /// <summary>
        /// 解析UEFI变量存储分区，提取所有UEFI变量
        /// </summary>
        public static List<UefiVariable> ParseUefiVariables(IPartition partition)
        {
            var variables = new List<UefiVariable>();
            try
            {
                if (partition == null || partition.Stream == null) return variables;

                Stream stream = partition.Stream;
                stream.Seek(0, SeekOrigin.Begin);
                byte[] header = new byte[Math.Min(4096, stream.Length)];
                int read = stream.Read(header, 0, header.Length);

                if (read < 4) return variables;

                // 检查变量存储签名
                if (!MatchSignature(header, 0, VARIABLE_STORE_SIGNATURE))
                {
                    // 不是标准的变量存储格式，尝试搜索
                    return SearchVariablesInStream(stream);
                }

                // 解析变量存储头
                int offset = 28; // VARIABLE_STORE_HEADER大小（通常28字节）

                while (offset + 32 < stream.Length)
                {
                    stream.Seek(offset, SeekOrigin.Begin);
                    byte[] varHeader = new byte[32];
                    int hdrRead = stream.Read(varHeader, 0, 32);
                    if (hdrRead < 32) break;

                    // 检查变量头签名
                    if (!MatchSignature(varHeader, 0, VARIABLE_HEADER_SIGNATURE))
                    {
                        // 可能是空闲空间，跳过
                        offset += 4;
                        continue;
                    }

                    // 解析AuthenticatedVariableHeader
                    uint attrs = BitConverter.ToUInt32(varHeader, 4);
                    uint nameSize = BitConverter.ToUInt32(varHeader, 12);
                    uint dataSize = BitConverter.ToUInt32(varHeader, 16);
                    Guid vendorGuid = new Guid(
                        BitConverter.ToUInt32(varHeader, 20),
                        BitConverter.ToUInt16(varHeader, 24),
                        BitConverter.ToUInt16(varHeader, 26),
                        varHeader[28], varHeader[29], varHeader[30], varHeader[31],
                        0, 0, 0, 0);

                    // 读取变量名
                    if (nameSize > 0 && nameSize < 1024)
                    {
                        byte[] nameBytes = new byte[nameSize];
                        stream.Read(nameBytes, 0, (int)nameSize);
                        string name = Encoding.Unicode.GetString(nameBytes).TrimEnd('\0');

                        // 读取变量数据
                        byte[] data = [];
                        if (dataSize > 0 && dataSize < 1024 * 1024)
                        {
                            data = new byte[dataSize];
                            stream.Read(data, 0, (int)dataSize);
                        }

                        if (!string.IsNullOrEmpty(name))
                        {
                            variables.Add(new UefiVariable
                            {
                                Name = name,
                                VendorGuid = vendorGuid,
                                Attributes = attrs,
                                Data = data
                            });
                        }
                    }

                    // 计算下一个变量的偏移（需要对齐到4字节）
                    int totalSize = 32 + (int)nameSize + (int)dataSize;
                    offset += (totalSize + 3) & ~3;
                }
            }
            catch { }
            return variables;
        }

        /// <summary>
        /// 在流中搜索UEFI变量（用于非标准格式的分区）
        /// </summary>
        private static List<UefiVariable> SearchVariablesInStream(Stream stream)
        {
            var variables = new List<UefiVariable>();
            try
            {
                stream.Seek(0, SeekOrigin.Begin);
                byte[] buffer = new byte[Math.Min(64 * 1024, stream.Length)];
                int read = stream.Read(buffer, 0, buffer.Length);

                for (int i = 0; i < read - 32; i++)
                {
                    if (MatchSignature(buffer, i, VARIABLE_HEADER_SIGNATURE))
                    {
                        try
                        {
                            uint attrs = BitConverter.ToUInt32(buffer, i + 4);
                            uint nameSize = BitConverter.ToUInt32(buffer, i + 12);
                            uint dataSize = BitConverter.ToUInt32(buffer, i + 16);

                            if (nameSize > 0 && nameSize < 512 && i + 32 + nameSize < read)
                            {
                                string name = Encoding.Unicode.GetString(buffer, i + 32, (int)nameSize).TrimEnd('\0');
                                if (!string.IsNullOrEmpty(name) && name.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == ' '))
                                {
                                    byte[] data = [];
                                    if (dataSize > 0 && dataSize < 65536 && i + 32 + nameSize + dataSize < read)
                                    {
                                        data = new byte[dataSize];
                                        Array.Copy(buffer, i + 32 + nameSize, data, 0, (int)dataSize);
                                    }

                                    variables.Add(new UefiVariable
                                    {
                                        Name = name,
                                        VendorGuid = Guid.Empty,
                                        Attributes = attrs,
                                        Data = data
                                    });
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return variables;
        }

        private static bool MatchSignature(byte[] data, int offset, byte[] signature)
        {
            if (offset + signature.Length > data.Length) return false;
            for (int i = 0; i < signature.Length; i++)
            {
                if (data[offset + i] != signature[i]) return false;
            }
            return true;
        }

        #endregion

        #region 导出格式增强

        /// <summary>
        /// 导出为C语言数组格式
        /// </summary>
        public static bool ExportToCArray(IPartition partition, string outputPath, string? arrayName = null)
        {
            try
            {
                if (partition == null || partition.Stream == null) return false;

                string name = arrayName ?? SanitizeIdentifier(partition.Name);
                using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(false));

                writer.WriteLine($"// Auto-generated from partition: {partition.Name}");
                writer.WriteLine($"// Size: {partition.Size} bytes");
                writer.WriteLine($"// Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                writer.WriteLine();
                writer.WriteLine($"const unsigned char {name}[] = {{");

                partition.Stream.Seek(0, SeekOrigin.Begin);
                byte[] buffer = new byte[16];
                long totalRead = 0;
                bool firstLine = true;

                while (totalRead < partition.Size)
                {
                    int toRead = (int)Math.Min(16, partition.Size - totalRead);
                    int read = partition.Stream.Read(buffer, 0, toRead);
                    if (read <= 0) break;

                    if (!firstLine) writer.WriteLine(",");
                    firstLine = false;

                    writer.Write("    ");
                    for (int i = 0; i < read; i++)
                    {
                        writer.Write($"0x{buffer[i]:X2}");
                        if (i < read - 1) writer.Write(", ");
                    }
                    totalRead += read;
                }

                writer.WriteLine();
                writer.WriteLine("};");
                writer.WriteLine();
                writer.WriteLine($"const unsigned int {name}_size = {partition.Size};");
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 导出为Intel HEX格式
        /// </summary>
        public static bool ExportToIntelHex(IPartition partition, string outputPath)
        {
            try
            {
                if (partition == null || partition.Stream == null) return false;

                using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(false));

                partition.Stream.Seek(0, SeekOrigin.Begin);
                byte[] buffer = new byte[16];
                long offset = 0;
                ushort segment = 0;

                while (offset < partition.Size)
                {
                    // 扩展段地址记录（每64KB）
                    if (offset / 65536 != segment)
                    {
                        segment = (ushort)(offset / 65536);
                        byte[] extRecord = new byte[4];
                        extRecord[0] = 0x02; // 数据长度
                        extRecord[1] = 0x00; // 偏移高字节
                        extRecord[2] = 0x00; // 偏移低字节
                        extRecord[3] = 0x04; // 记录类型: 扩展线性地址
                        byte high = (byte)(segment >> 8);
                        byte low = (byte)(segment & 0xFF);
                        writer.WriteLine($":02000004{high:X2}{low:X2}{CalculateHexChecksum(new byte[] { 0x02, 0x00, 0x00, 0x04, high, low }):X2}");
                    }

                    int toRead = (int)Math.Min(16, partition.Size - offset);
                    int read = partition.Stream.Read(buffer, 0, toRead);
                    if (read <= 0) break;

                    ushort localOffset = (ushort)(offset % 65536);
                    byte[] record = new byte[read + 4];
                    record[0] = (byte)read;
                    record[1] = (byte)(localOffset >> 8);
                    record[2] = (byte)(localOffset & 0xFF);
                    record[3] = 0x00; // 数据记录
                    Array.Copy(buffer, 0, record, 4, read);

                    byte checksum = CalculateHexChecksum(record);
                    var sb = new StringBuilder();
                    sb.Append(':');
                    foreach (byte b in record) sb.Append(b.ToString("X2"));
                    sb.Append(checksum.ToString("X2"));
                    writer.WriteLine(sb.ToString());

                    offset += read;
                }

                // 文件结束记录
                writer.WriteLine(":00000001FF");
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 计算校验和（MD5/SHA1/SHA256）
        /// </summary>
        public static (string MD5, string SHA1, string SHA256) CalculateChecksums(IPartition partition)
        {
            try
            {
                if (partition == null || partition.Stream == null)
                    return ("", "", "");

                partition.Stream.Seek(0, SeekOrigin.Begin);

                using var md5 = System.Security.Cryptography.MD5.Create();
                using var sha1 = System.Security.Cryptography.SHA1.Create();
                using var sha256 = System.Security.Cryptography.SHA256.Create();

                byte[] buffer = new byte[1024 * 1024]; // 1MB buffer
                int read;
                while ((read = partition.Stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    md5.TransformBlock(buffer, 0, read, null, 0);
                    sha1.TransformBlock(buffer, 0, read, null, 0);
                    sha256.TransformBlock(buffer, 0, read, null, 0);
                }
                md5.TransformFinalBlock(buffer, 0, 0);
                sha1.TransformFinalBlock(buffer, 0, 0);
                sha256.TransformFinalBlock(buffer, 0, 0);

                return (
                    BitConverter.ToString(md5.Hash ?? Array.Empty<byte>()).Replace("-", "").ToLower(),
                    BitConverter.ToString(sha1.Hash ?? Array.Empty<byte>()).Replace("-", "").ToLower(),
                    BitConverter.ToString(sha256.Hash ?? Array.Empty<byte>()).Replace("-", "").ToLower()
                );
            }
            catch
            {
                return ("", "", "");
            }
        }

        private static byte CalculateHexChecksum(byte[] data)
        {
            int sum = 0;
            foreach (byte b in data) sum += b;
            return (byte)(~sum + 1);
        }

        private static string SanitizeIdentifier(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '_')
                    sb.Append(c);
                else
                    sb.Append('_');
            }
            if (sb.Length == 0 || char.IsDigit(sb[0]))
                sb.Insert(0, '_');
            return sb.ToString();
        }

        #endregion

        #region FFU文件头解析

        /// <summary>
        /// FFU文件头信息
        /// </summary>
        public class FFUHeaderInfo
        {
            public string Signature { get; set; } = "";
            public uint HeaderSize { get; set; }
            public uint ManifestLength { get; set; }
            public uint ChunkSize { get; set; }
            public int StoreCount { get; set; }
            public string Platform { get; set; } = "";
            public string Manufacturer { get; set; } = "";
            public string Version { get; set; } = "";
            public string BuildTime { get; set; } = "";
            public string DeviceTargetInfo { get; set; } = "";
            public string ManifestXml { get; set; } = "";
        }

        /// <summary>
        /// 解析FFU文件头信息
        /// </summary>
        public static FFUHeaderInfo? ParseFFUHeader(string ffuPath)
        {
            try
            {
                using var stream = new FileStream(ffuPath, FileMode.Open, FileAccess.Read);
                var imageFlash = new ImageFlash(stream);

                var info = new FFUHeaderInfo
                {
                    Signature = imageFlash.ImageHeader.Signature.Trim(),
                    HeaderSize = imageFlash.ImageHeader.Size,
                    ManifestLength = imageFlash.ImageHeader.ManifestLength,
                    ChunkSize = imageFlash.ImageHeader.ChunkSize,
                    StoreCount = imageFlash.Stores.Count,
                    ManifestXml = imageFlash.ManifestData
                };

                // 解析Manifest XML提取元信息
                try
                {
                    var doc = XDocument.Parse(imageFlash.ManifestData);
                    var root = doc.Root;
                    if (root != null)
                    {
                        // 尝试提取常见字段
                        info.Platform = GetXmlValue(root, "Platform") ?? GetXmlValue(root, "PlatformID") ?? "";
                        info.Manufacturer = GetXmlValue(root, "Manufacturer") ?? GetXmlValue(root, "OEM") ?? "";
                        info.Version = GetXmlValue(root, "Version") ?? GetXmlValue(root, "ImageVersion") ?? "";
                        info.BuildTime = GetXmlValue(root, "BuildTime") ?? GetXmlValue(root, "Created") ?? "";
                    }
                }
                catch { }

                // 解析设备目标信息
                if (imageFlash.DeviceTargetInfos.Count > 0)
                {
                    var dti = imageFlash.DeviceTargetInfos[0];
                    info.DeviceTargetInfo = $"定位信息: {dti.GetType().Name}";
                }

                return info;
            }
            catch
            {
                return null;
            }
        }

        private static string? GetXmlValue(XElement root, string name)
        {
            // 尝试直接子元素
            var elem = root.Element(name);
            if (elem != null) return elem.Value.Trim();

            // 尝试所有后代元素
            var descendant = root.Descendants(name).FirstOrDefault();
            return descendant?.Value.Trim();
        }

        #endregion
    }
}
