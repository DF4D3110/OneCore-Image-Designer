using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// 固件结构解析器：支持ELF、PE/COFF、UEFI固件卷(FV)格式
    /// </summary>
    public static class FirmwareParser
    {
        /// <summary>
        /// 解析结果
        /// </summary>
        public class FirmwareInfo
        {
            public string Format { get; set; } = "未知";
            public string Architecture { get; set; } = "";
            public string EntryPoint { get; set; } = "";
            public long CodeSize { get; set; }
            public long DataSize { get; set; }
            public List<SectionInfo> Sections { get; set; } = new();
            public List<ModuleInfo> Modules { get; set; } = new();
            public Dictionary<string, string> AdditionalInfo { get; set; } = new();
            public string Summary { get; set; } = "";
        }

        public class SectionInfo
        {
            public string Name { get; set; } = "";
            public string Type { get; set; } = "";
            public long VirtualAddress { get; set; }
            public long VirtualSize { get; set; }
            public long RawOffset { get; set; }
            public long RawSize { get; set; }
            public string Flags { get; set; } = "";
        }

        public class ModuleInfo
        {
            public string Name { get; set; } = "";
            public Guid TypeGuid { get; set; }
            public long Offset { get; set; }
            public long Size { get; set; }
            public string Type { get; set; } = "";
        }

        /// <summary>
        /// 自动检测并解析固件
        /// </summary>
        public static FirmwareInfo Parse(Stream stream, long length)
        {
            var info = new FirmwareInfo();
            try
            {
                if (length < 4) return info;

                byte[] header = new byte[Math.Min(64, length)];
                stream.Seek(0, SeekOrigin.Begin);
                int read = stream.Read(header, 0, header.Length);
                if (read < 4) return info;

                // ELF
                if (header[0] == 0x7F && header[1] == 0x45 && header[2] == 0x4C && header[3] == 0x46)
                {
                    return ParseElf(stream, length, header);
                }

                // MZ/PE
                if (header[0] == 0x4D && header[1] == 0x5A)
                {
                    return ParsePe(stream, length, header);
                }

                // UEFI FV
                if (header[0] == 0x5F && header[1] == 0x46 && header[2] == 0x56 && header[3] == 0x48)
                {
                    return ParseUefiFv(stream, length, 0);
                }

                // 搜索FV签名（前4KB内8字节对齐）
                // 注意：标准FV头中_FVH签名在偏移40处，所以找到签名后需要回退40字节作为FV头起始位置
                long searchLen = Math.Min(4096, length);
                byte[] searchBuf = new byte[searchLen];
                stream.Seek(0, SeekOrigin.Begin);
                stream.Read(searchBuf, 0, (int)searchLen);
                for (int i = 0; i < searchLen - 4; i += 8)
                {
                    if (searchBuf[i] == 0x5F && searchBuf[i + 1] == 0x46 &&
                        searchBuf[i + 2] == 0x56 && searchBuf[i + 3] == 0x48)
                    {
                        // _FVH在标准FV头内偏移40处，回退40字节作为FV头起始位置
                        // 如果i < 40，则从0开始（非标准格式）
                        long fvStart = Math.Max(0, i - 40);
                        return ParseUefiFv(stream, length, fvStart);
                    }
                }

                info.Format = "原始二进制 (raw)";
                info.Summary = $"未识别的固件格式，大小 {FormatSize(length)}";
            }
            catch (Exception ex)
            {
                info.Format = "解析错误";
                info.Summary = ex.Message;
            }
            return info;
        }

        #region ELF解析

        private static FirmwareInfo ParseElf(Stream stream, long length, byte[] header)
        {
            var info = new FirmwareInfo { Format = "ELF" };

            bool is64Bit = header[4] == 2;
            bool isLittleEndian = header[5] == 1;

            info.AdditionalInfo["位数"] = is64Bit ? "64-bit" : "32-bit";
            info.AdditionalInfo["字节序"] = isLittleEndian ? "Little-Endian" : "Big-Endian";
            info.AdditionalInfo["ELF版本"] = header[6].ToString();

            // 读取完整ELF头
            int ehdrSize = is64Bit ? 64 : 52;
            byte[] ehdr = new byte[ehdrSize];
            stream.Seek(0, SeekOrigin.Begin);
            stream.Read(ehdr, 0, ehdrSize);

            ushort e_type = ReadUInt16(ehdr, 16, isLittleEndian);
            ushort e_machine = ReadUInt16(ehdr, 18, isLittleEndian);
            uint e_version = ReadUInt32(ehdr, 20, isLittleEndian);
            ulong e_entry = is64Bit ? ReadUInt64(ehdr, 24, isLittleEndian) : ReadUInt32(ehdr, 24, isLittleEndian);
            ulong e_phoff = is64Bit ? ReadUInt64(ehdr, 32, isLittleEndian) : ReadUInt32(ehdr, 28, isLittleEndian);
            ulong e_shoff = is64Bit ? ReadUInt64(ehdr, 40, isLittleEndian) : ReadUInt32(ehdr, 32, isLittleEndian);
            ushort e_phentsize = ReadUInt16(ehdr, is64Bit ? 54 : 42, isLittleEndian);
            ushort e_phnum = ReadUInt16(ehdr, is64Bit ? 56 : 44, isLittleEndian);
            ushort e_shentsize = ReadUInt16(ehdr, is64Bit ? 58 : 46, isLittleEndian);
            ushort e_shnum = ReadUInt16(ehdr, is64Bit ? 60 : 48, isLittleEndian);
            ushort e_shstrndx = ReadUInt16(ehdr, is64Bit ? 62 : 50, isLittleEndian);

            info.Architecture = GetElfMachineName(e_machine);
            info.EntryPoint = $"0x{e_entry:X}";
            info.AdditionalInfo["类型"] = GetElfTypeName(e_type);
            info.AdditionalInfo["程序头"] = $"{e_phnum} 个 (偏移 0x{e_phoff:X})";
            info.AdditionalInfo["节头"] = $"{e_shnum} 个 (偏移 0x{e_shoff:X})";

            // 解析程序头（嵌入式固件通常strip掉节头，只保留程序头）
            if (e_phnum > 0 && e_phoff > 0)
            {
                int phdrSize = is64Bit ? 56 : 32;
                for (int i = 0; i < e_phnum; i++)
                {
                    try
                    {
                        long phOffset = (long)e_phoff + i * phdrSize;
                        byte[] phdr = new byte[phdrSize];
                        stream.Seek(phOffset, SeekOrigin.Begin);
                        stream.Read(phdr, 0, phdrSize);

                        uint p_type = ReadUInt32(phdr, 0, isLittleEndian);
                        uint p_flags = is64Bit ? ReadUInt32(phdr, 4, isLittleEndian) : ReadUInt32(phdr, 24, isLittleEndian);
                        ulong p_offset2 = is64Bit ? ReadUInt64(phdr, 8, isLittleEndian) : ReadUInt32(phdr, 4, isLittleEndian);
                        ulong p_vaddr = is64Bit ? ReadUInt64(phdr, 16, isLittleEndian) : ReadUInt32(phdr, 8, isLittleEndian);
                        ulong p_filesz = is64Bit ? ReadUInt64(phdr, 32, isLittleEndian) : ReadUInt32(phdr, 16, isLittleEndian);
                        ulong p_memsz = is64Bit ? ReadUInt64(phdr, 40, isLittleEndian) : ReadUInt32(phdr, 20, isLittleEndian);

                        string typeName = GetElfProgramHeaderTypeName(p_type);
                        string flags = GetElfProgramHeaderFlags(p_flags);

                        // 把程序头当作段显示（名称用类型+索引）
                        var segment = new SectionInfo
                        {
                            Name = $"[{typeName} #{i}]",
                            Type = typeName,
                            VirtualAddress = (long)p_vaddr,
                            VirtualSize = (long)p_memsz,
                            RawOffset = (long)p_offset2,
                            RawSize = (long)p_filesz,
                            Flags = flags
                        };
                        info.Sections.Add(segment);

                        // 通过PT_LOAD段统计代码/数据大小
                        if (p_type == 1) // PT_LOAD
                        {
                            if ((p_flags & 0x1) != 0) // PF_X 可执行
                            {
                                info.CodeSize += (long)p_filesz;
                            }
                            else // 不可执行 = 数据
                            {
                                info.DataSize += (long)p_filesz;
                            }
                        }
                    }
                    catch { break; }
                }
            }

            // 读取节名字符串表
            string[]? sectionNames = null;
            if (e_shnum > 0 && e_shstrndx < e_shnum && e_shoff > 0)
            {
                try
                {
                    long shstrOffset = (long)e_shoff + e_shstrndx * e_shentsize;
                    byte[] shstrShdr = new byte[e_shentsize];
                    stream.Seek(shstrOffset, SeekOrigin.Begin);
                    stream.Read(shstrShdr, 0, e_shentsize);

                    uint sh_name = ReadUInt32(shstrShdr, 0, isLittleEndian);
                    ulong sh_offset = is64Bit ? ReadUInt64(shstrShdr, 24, isLittleEndian) : ReadUInt32(shstrShdr, 16, isLittleEndian);
                    ulong sh_size = is64Bit ? ReadUInt64(shstrShdr, 32, isLittleEndian) : ReadUInt32(shstrShdr, 20, isLittleEndian);

                    if (sh_size > 0 && sh_size < 1024 * 1024)
                    {
                        byte[] strtab = new byte[sh_size];
                        stream.Seek((long)sh_offset, SeekOrigin.Begin);
                        stream.Read(strtab, 0, (int)sh_size);
                        sectionNames = ExtractStringsFromTable(strtab);
                    }
                }
                catch { }
            }

            // 解析节头
            if (e_shnum > 0 && e_shoff > 0)
            {
                for (int i = 0; i < e_shnum; i++)
                {
                    try
                    {
                        long shOffset = (long)e_shoff + i * e_shentsize;
                        byte[] shdr = new byte[e_shentsize];
                        stream.Seek(shOffset, SeekOrigin.Begin);
                        stream.Read(shdr, 0, e_shentsize);

                        uint sh_name_idx = ReadUInt32(shdr, 0, isLittleEndian);
                        uint sh_type = ReadUInt32(shdr, 4, isLittleEndian);
                        ulong sh_addr = is64Bit ? ReadUInt64(shdr, 16, isLittleEndian) : ReadUInt32(shdr, 12, isLittleEndian);
                        ulong sh_offset2 = is64Bit ? ReadUInt64(shdr, 24, isLittleEndian) : ReadUInt32(shdr, 16, isLittleEndian);
                        ulong sh_size2 = is64Bit ? ReadUInt64(shdr, 32, isLittleEndian) : ReadUInt32(shdr, 20, isLittleEndian);
                        uint sh_flags = is64Bit ? (uint)ReadUInt64(shdr, 8, isLittleEndian) : ReadUInt32(shdr, 8, isLittleEndian);

                        string name = sectionNames != null && sh_name_idx < sectionNames.Length
                            ? sectionNames[sh_name_idx]
                            : $"section_{i}";

                        if (string.IsNullOrEmpty(name)) name = $"section_{i}";

                        var section = new SectionInfo
                        {
                            Name = name,
                            Type = GetElfSectionTypeName(sh_type),
                            VirtualAddress = (long)sh_addr,
                            VirtualSize = (long)sh_size2,
                            RawOffset = (long)sh_offset2,
                            RawSize = (long)sh_size2,
                            Flags = GetElfSectionFlags(sh_flags)
                        };
                        info.Sections.Add(section);

                        // 统计代码/数据大小
                        if (name.Equals(".text", StringComparison.OrdinalIgnoreCase) ||
                            section.Type == "SHT_PROGBITS" && (sh_flags & 0x4) != 0)
                        {
                            info.CodeSize += (long)sh_size2;
                        }
                        else if (name.Equals(".data", StringComparison.OrdinalIgnoreCase) ||
                                 name.Equals(".rodata", StringComparison.OrdinalIgnoreCase) ||
                                 name.Equals(".bss", StringComparison.OrdinalIgnoreCase))
                        {
                            info.DataSize += (long)sh_size2;
                        }
                    }
                    catch { break; }
                }
            }

            info.Summary = $"ELF {(is64Bit ? "64" : "32")}-bit {info.Architecture} | 入口: 0x{e_entry:X} | {info.Sections.Count} 个节 | 代码: {FormatSize(info.CodeSize)} | 数据: {FormatSize(info.DataSize)}";
            return info;
        }

        private static string GetElfMachineName(ushort machine)
        {
            return machine switch
            {
                0x00 => "None",
                0x02 => "SPARC",
                0x03 => "x86",
                0x08 => "MIPS",
                0x14 => "PowerPC",
                0x28 => "ARM (32-bit)",
                0x2A => "SuperH",
                0x32 => "IA-64",
                0x3E => "x86-64",
                0x47 => "OpenRISC",
                0x50 => "RISC-V",
                0xB7 => "AArch64 (ARM64)",
                _ => $"0x{machine:X4}"
            };
        }

        private static string GetElfTypeName(ushort type)
        {
            return type switch
            {
                0 => "ET_NONE (无类型)",
                1 => "ET_REL (可重定位)",
                2 => "ET_EXEC (可执行)",
                3 => "ET_DYN (共享库)",
                4 => "ET_CORE (核心转储)",
                _ => $"0x{type:X}"
            };
        }

        private static string GetElfSectionTypeName(uint type)
        {
            return type switch
            {
                0 => "SHT_NULL",
                1 => "SHT_PROGBITS",
                2 => "SHT_SYMTAB",
                3 => "SHT_STRTAB",
                4 => "SHT_RELA",
                5 => "SHT_HASH",
                6 => "SHT_DYNAMIC",
                7 => "SHT_NOTE",
                8 => "SHT_NOBITS",
                9 => "SHT_REL",
                11 => "SHT_DYNSYM",
                _ => $"SHT_0x{type:X}"
            };
        }

        private static string GetElfSectionFlags(uint flags)
        {
            var sb = new StringBuilder();
            if ((flags & 0x1) != 0) sb.Append("W ");  // SHF_WRITE
            if ((flags & 0x2) != 0) sb.Append("A ");  // SHF_ALLOC
            if ((flags & 0x4) != 0) sb.Append("X ");  // SHF_EXECINSTR
            return sb.ToString().Trim();
        }

        private static string GetElfProgramHeaderTypeName(uint type)
        {
            return type switch
            {
                0 => "PT_NULL",
                1 => "PT_LOAD",
                2 => "PT_DYNAMIC",
                3 => "PT_INTERP",
                4 => "PT_NOTE",
                5 => "PT_SHLIB",
                6 => "PT_PHDR",
                7 => "PT_TLS",
                0x60000000 => "PT_LOOS",
                0x6FFFFFFF => "PT_HIOS",
                0x70000000 => "PT_LOPROC",
                0x7FFFFFFF => "PT_HIPROC",
                _ => $"PT_0x{type:X}"
            };
        }

        private static string GetElfProgramHeaderFlags(uint flags)
        {
            var sb = new StringBuilder();
            if ((flags & 0x4) != 0) sb.Append("R ");  // PF_R
            if ((flags & 0x2) != 0) sb.Append("W ");  // PF_W
            if ((flags & 0x1) != 0) sb.Append("X ");  // PF_X
            return sb.ToString().Trim();
        }

        #endregion

        #region PE/COFF解析

        private static FirmwareInfo ParsePe(Stream stream, long length, byte[] header)
        {
            var info = new FirmwareInfo { Format = "PE/COFF" };

            // 读取DOS头获取PE偏移
            if (length < 0x40)
            {
                info.Format = "MZ (DOS)";
                info.Summary = "DOS可执行文件，无PE头";
                return info;
            }

            int peOffset = BitConverter.ToInt32(header, 0x3C);
            if (peOffset <= 0 || peOffset + 24 > length)
            {
                info.Format = "MZ (DOS)";
                info.Summary = "DOS可执行文件，无效PE偏移";
                return info;
            }

            // 读取PE签名和COFF头
            byte[] peHeader = new byte[24];
            stream.Seek(peOffset, SeekOrigin.Begin);
            stream.Read(peHeader, 0, 24);

            if (peHeader[0] != 0x50 || peHeader[1] != 0x45 || peHeader[2] != 0 || peHeader[3] != 0)
            {
                info.Format = "MZ (DOS)";
                info.Summary = "DOS可执行文件，PE签名无效";
                return info;
            }

            ushort machine = BitConverter.ToUInt16(peHeader, 4);
            ushort numSections = BitConverter.ToUInt16(peHeader, 6);
            uint timeDateStamp = BitConverter.ToUInt32(peHeader, 8);
            ushort sizeOfOptionalHeader = BitConverter.ToUInt16(peHeader, 20);
            ushort characteristics = BitConverter.ToUInt16(peHeader, 22);

            info.Architecture = GetPeMachineName(machine);
            info.AdditionalInfo["PE偏移"] = $"0x{peOffset:X}";
            info.AdditionalInfo["编译时间"] = DateTimeOffset.FromUnixTimeSeconds(timeDateStamp).ToString("yyyy-MM-dd HH:mm:ss");
            info.AdditionalInfo["节区数量"] = numSections.ToString();
            info.AdditionalInfo["特性"] = GetPeCharacteristics(characteristics);

            // 读取可选头
            if (sizeOfOptionalHeader >= 2)
            {
                byte[] optHeader = new byte[Math.Min((int)sizeOfOptionalHeader, 120)];
                stream.Seek(peOffset + 24, SeekOrigin.Begin);
                stream.Read(optHeader, 0, optHeader.Length);

                ushort magic = BitConverter.ToUInt16(optHeader, 0);
                bool isPe32Plus = magic == 0x20B;
                info.AdditionalInfo["可选头类型"] = isPe32Plus ? "PE32+" : "PE32";

                uint addressOfEntryPoint = BitConverter.ToUInt32(optHeader, 16);
                uint imageBase = isPe32Plus ? BitConverter.ToUInt32(optHeader, 24) : BitConverter.ToUInt32(optHeader, 28);
                uint sectionAlignment = BitConverter.ToUInt32(optHeader, 32);
                uint fileAlignment = BitConverter.ToUInt32(optHeader, 36);
                ushort subsystem = BitConverter.ToUInt16(optHeader, 68);
                uint sizeOfImage = BitConverter.ToUInt32(optHeader, 56);
                uint sizeOfHeaders = BitConverter.ToUInt32(optHeader, 60);

                info.EntryPoint = $"0x{addressOfEntryPoint:X}";
                info.AdditionalInfo["镜像基址"] = $"0x{imageBase:X}";
                info.AdditionalInfo["节对齐"] = $"0x{sectionAlignment:X}";
                info.AdditionalInfo["文件对齐"] = $"0x{fileAlignment:X}";
                info.AdditionalInfo["子系统"] = GetPeSubsystemName(subsystem);
                info.AdditionalInfo["镜像大小"] = FormatSize(sizeOfImage);
                info.AdditionalInfo["头大小"] = FormatSize(sizeOfHeaders);
            }

            // 读取节表
            long sectionTableOffset = peOffset + 24 + sizeOfOptionalHeader;
            for (int i = 0; i < numSections; i++)
            {
                try
                {
                    byte[] secHeader = new byte[40];
                    stream.Seek(sectionTableOffset + i * 40, SeekOrigin.Begin);
                    stream.Read(secHeader, 0, 40);

                    string name = Encoding.ASCII.GetString(secHeader, 0, 8).TrimEnd('\0');
                    uint virtualSize = BitConverter.ToUInt32(secHeader, 8);
                    uint virtualAddress = BitConverter.ToUInt32(secHeader, 12);
                    uint sizeOfRawData = BitConverter.ToUInt32(secHeader, 16);
                    uint pointerToRawData = BitConverter.ToUInt32(secHeader, 20);
                    uint characteristics2 = BitConverter.ToUInt32(secHeader, 36);

                    var section = new SectionInfo
                    {
                        Name = name,
                        Type = "PE Section",
                        VirtualAddress = virtualAddress,
                        VirtualSize = virtualSize,
                        RawOffset = pointerToRawData,
                        RawSize = sizeOfRawData,
                        Flags = GetPeSectionFlags(characteristics2)
                    };
                    info.Sections.Add(section);

                    if (name.Equals(".text", StringComparison.OrdinalIgnoreCase))
                        info.CodeSize += sizeOfRawData;
                    else if (name.Equals(".data", StringComparison.OrdinalIgnoreCase) ||
                             name.Equals(".rdata", StringComparison.OrdinalIgnoreCase) ||
                             name.Equals(".bss", StringComparison.OrdinalIgnoreCase))
                        info.DataSize += sizeOfRawData;
                }
                catch { break; }
            }

            info.Summary = $"PE/COFF {info.Architecture} | 入口: {info.EntryPoint} | {info.Sections.Count} 个节区 | 代码: {FormatSize(info.CodeSize)} | 数据: {FormatSize(info.DataSize)}";
            return info;
        }

        private static string GetPeMachineName(ushort machine)
        {
            return machine switch
            {
                0x014C => "x86 (i386)",
                0x0200 => "IA-64",
                0x8664 => "x64 (AMD64)",
                0x01C0 => "ARM",
                0xAA64 => "ARM64",
                0x01C4 => "ARMNT (Thumb)",
                _ => $"0x{machine:X4}"
            };
        }

        private static string GetPeSubsystemName(ushort subsystem)
        {
            return subsystem switch
            {
                1 => "NATIVE (UEFI驱动/应用)",
                2 => "WINDOWS_GUI",
                3 => "WINDOWS_CUI",
                7 => "POSIX_CUI",
                9 => "WINDOWS_CE_GUI",
                10 => "EFI_APPLICATION",
                11 => "EFI_BOOT_SERVICE_DRIVER",
                12 => "EFI_RUNTIME_DRIVER",
                13 => "EFI_ROM",
                _ => $"0x{subsystem:X}"
            };
        }

        private static string GetPeCharacteristics(ushort chars)
        {
            var sb = new StringBuilder();
            if ((chars & 0x0002) != 0) sb.Append("EXECUTABLE_IMAGE ");
            if ((chars & 0x0020) != 0) sb.Append("LARGE_ADDRESS_AWARE ");
            if ((chars & 0x0100) != 0) sb.Append("BYTES_REVERSED_LO ");
            if ((chars & 0x0200) != 0) sb.Append("32BIT_MACHINE ");
            if ((chars & 0x2000) != 0) sb.Append("DLL ");
            return sb.ToString().Trim();
        }

        private static string GetPeSectionFlags(uint flags)
        {
            var sb = new StringBuilder();
            if ((flags & 0x20000000) != 0) sb.Append("CODE ");
            if ((flags & 0x40000000) != 0) sb.Append("INITIALIZED_DATA ");
            if ((flags & 0x80000000) != 0) sb.Append("UNINITIALIZED_DATA ");
            if ((flags & 0x20000000) != 0) sb.Append("EXEC ");
            if ((flags & 0x40000000) != 0) sb.Append("READ ");
            if ((flags & 0x80000000) != 0) sb.Append("WRITE ");
            return sb.ToString().Trim();
        }

        #endregion

        #region UEFI FV解析

        private static FirmwareInfo ParseUefiFv(Stream stream, long length, long fvOffset)
        {
            var info = new FirmwareInfo { Format = "UEFI 固件卷 (FV)" };

            try
            {
                // 读取FV头（标准EFI_FIRMWARE_VOLUME_HEADER，共56字节）
                byte[] fvHeader = new byte[56];
                stream.Seek(fvOffset, SeekOrigin.Begin);
                int read = stream.Read(fvHeader, 0, 56);
                if (read < 56) return info;

                // 标准UEFI FV头结构:
                // 偏移0-15:   ZeroVector (16字节，全0)
                // 偏移16-31:  FileSystemGuid (16字节)
                // 偏移32-39:  FvLength (8字节)
                // 偏移40-43:  Signature "_FVH" (4字节)
                // 偏移44-47:  Attributes (4字节)
                // 偏移48-49:  HeaderLength (2字节)
                // 偏移50-51:  Checksum (2字节)
                // 偏移52-53:  ExtHeaderOffset (2字节)
                // 偏移54:      Reserved (1字节)
                // 偏移55:      Revision (1字节)

                var fsGuid = new Guid(
                    BitConverter.ToUInt32(fvHeader, 16),
                    BitConverter.ToUInt16(fvHeader, 20),
                    BitConverter.ToUInt16(fvHeader, 22),
                    fvHeader[24], fvHeader[25], fvHeader[26], fvHeader[27],
                    fvHeader[28], fvHeader[29], fvHeader[30], fvHeader[31]);

                ulong fvLength = BitConverter.ToUInt64(fvHeader, 32);
                uint attributes = BitConverter.ToUInt32(fvHeader, 44);
                ushort headerLength = BitConverter.ToUInt16(fvHeader, 48);
                byte revision = fvHeader[55];

                info.AdditionalInfo["FV偏移"] = $"0x{fvOffset:X}";
                info.AdditionalInfo["文件系统GUID"] = fsGuid.ToString();
                info.AdditionalInfo["FV大小"] = FormatSize((long)fvLength);
                info.AdditionalInfo["头大小"] = $"{headerLength} 字节";
                info.AdditionalInfo["版本"] = revision.ToString();
                info.AdditionalInfo["属性"] = GetFvAttributes(attributes);

                // 解析FV中的文件
                long fileOffset = fvOffset + headerLength;
                long fvEnd = fvOffset + (long)fvLength;
                int moduleCount = 0;

                while (fileOffset + 24 < fvEnd && fileOffset + 24 < length)
                {
                    byte[] fileHeader = new byte[24];
                    stream.Seek(fileOffset, SeekOrigin.Begin);
                    int fileRead = stream.Read(fileHeader, 0, 24);
                    if (fileRead < 24) break;

                    // FV文件头
                    var fileGuid = new Guid(
                        BitConverter.ToUInt32(fileHeader, 0),
                        BitConverter.ToUInt16(fileHeader, 4),
                        BitConverter.ToUInt16(fileHeader, 6),
                        fileHeader[8], fileHeader[9], fileHeader[10], fileHeader[11],
                        fileHeader[12], fileHeader[13], fileHeader[14], fileHeader[15]);

                    ulong fileSize = (ulong)fileHeader[20] | ((ulong)fileHeader[21] << 8) | ((ulong)fileHeader[22] << 16);
                    byte fileType = fileHeader[18];
                    byte fileAttributes = fileHeader[19];

                    // 空闲块 (全零GUID)
                    if (fileGuid == Guid.Empty)
                    {
                        if (fileSize == 0) break;
                        fileOffset += (long)fileSize;
                        continue;
                    }

                    // 填充块 (全fGUID 或 标准FFS填充GUID a980f8b3-1f6a-47f3-8b39-3a475640173c)
                    Guid ffsPadGuid = new("a980f8b3-1f6a-47f3-8b39-3a475640173c");
                    if (fileGuid == Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff") || fileGuid == ffsPadGuid)
                    {
                        if (fileSize == 0 || fileSize == 0xFFFFFF) break;
                        fileOffset += (long)fileSize;
                        // 8字节对齐
                        if (fileOffset % 8 != 0)
                            fileOffset += 8 - (fileOffset % 8);
                        continue;
                    }

                    // 文件结束标记
                    if (fileType == 0xF0) break;

                    // 大小合理性检查（不能超过FV大小）
                    if (fileSize == 0 || fileSize > (ulong)(fvEnd - fileOffset))
                    {
                        break;
                    }

                    string moduleName = GetUefiModuleName(fileGuid, fileType);
                    string moduleType = GetUefiFileType(fileType);

                    var module = new ModuleInfo
                    {
                        Name = moduleName,
                        TypeGuid = fileGuid,
                        Offset = fileOffset - fvOffset,
                        Size = (long)fileSize,
                        Type = moduleType
                    };
                    info.Modules.Add(module);
                    moduleCount++;

                    if (fileSize == 0) break;
                    fileOffset += (long)fileSize;

                    // 8字节对齐
                    if (fileOffset % 8 != 0)
                        fileOffset += 8 - (fileOffset % 8);
                }

                info.Summary = $"UEFI FV | {moduleCount} 个模块 | 大小: {FormatSize((long)fvLength)} | 版本: {revision}";
            }
            catch (Exception ex)
            {
                info.Summary = $"FV解析错误: {ex.Message}";
            }

            return info;
        }

        private static string GetFvAttributes(uint attrs)
        {
            var sb = new StringBuilder();
            if ((attrs & 0x01) != 0) sb.Append("READ_LOCK ");
            if ((attrs & 0x02) != 0) sb.Append("WRITE_LOCK ");
            if ((attrs & 0x04) != 0) sb.Append("WRITE_POLICY_RELIABLE ");
            if ((attrs & 0x10) != 0) sb.Append("READ_LOCK_CAP ");
            if ((attrs & 0x20) != 0) sb.Append("WRITE_LOCK_CAP ");
            if ((attrs & 0x40) != 0) sb.Append("WRITE_POLICY_RELIABLE_CAP ");
            return sb.ToString().Trim();
        }

        private static string GetUefiFileType(byte type)
        {
            return type switch
            {
                0x00 => "ALL",
                0x01 => "FIRMWARE_VOLUME_IMAGE",
                0x02 => "FREEFORM",
                0x03 => "SECURITY_CORE",
                0x04 => "PEI_CORE",
                0x05 => "DXE_CORE",
                0x06 => "PEIM",
                0x07 => "DRIVER",
                0x08 => "COMBINED_PEIM_DRIVER",
                0x09 => "APPLICATION",
                0x0A => "MM",
                0x0B => "FIRMWARE_MANAGEMENT",
                0x0C => "COMBINED_MM_DXE",
                0x0D => "COMBINED_SMM_DXE",
                0x0E => "SMM_CORE",
                0xF0 => "FFS_EOF",
                _ => $"0x{type:X2}"
            };
        }

        private static string GetUefiModuleName(Guid guid, byte type)
        {
            // 常见UEFI模块GUID映射
            return guid.ToString().ToUpper() switch
            {
                "1B45F0A0-7B3A-48A5-85A8-4E14F0C353C3" => "SecCore",
                "EDKII_PEI_CORE" => "PeiCore",
                "D6A2CB7F-6A18-4E2F-B43B-9920A733700A" => "DxeCore",
                "781FE4A0-04C4-481E-9EE1-0C8C-2C8C-2C8C" => "MmCore",
                "8C6F28E0-1481-4E3C-91E3-41F7B7C8C8C8" => "SmmCore",
                _ => $"模块_{type:X2}_{guid.ToString().Substring(0, 8)}"
            };
        }

        #endregion

        #region 辅助方法

        private static string[] ExtractStringsFromTable(byte[] table)
        {
            var strings = new List<string>();
            var sb = new StringBuilder();
            foreach (byte b in table)
            {
                if (b == 0)
                {
                    strings.Add(sb.ToString());
                    sb.Clear();
                }
                else if (b >= 0x20 && b <= 0x7E)
                {
                    sb.Append((char)b);
                }
                else
                {
                    sb.Append('?');
                }
            }
            strings.Add(sb.ToString());
            return strings.ToArray();
        }

        private static ushort ReadUInt16(byte[] buf, int offset, bool littleEndian)
        {
            return littleEndian
                ? BitConverter.ToUInt16(buf, offset)
                : (ushort)((buf[offset] << 8) | buf[offset + 1]);
        }

        private static uint ReadUInt32(byte[] buf, int offset, bool littleEndian)
        {
            return littleEndian
                ? BitConverter.ToUInt32(buf, offset)
                : ((uint)buf[offset] << 24) | ((uint)buf[offset + 1] << 16) | ((uint)buf[offset + 2] << 8) | buf[offset + 3];
        }

        private static ulong ReadUInt64(byte[] buf, int offset, bool littleEndian)
        {
            return littleEndian
                ? BitConverter.ToUInt64(buf, offset)
                : ((ulong)ReadUInt32(buf, offset, false) << 32) | ReadUInt32(buf, offset + 4, false);
        }

        private static string FormatSize(long size)
        {
            return size switch
            {
                > 1073741824 => $"{size / 1073741824.0:F2} GB",
                > 1048576 => $"{size / 1048576.0:F2} MB",
                > 1024 => $"{size / 1024.0:F2} KB",
                _ => $"{size} B"
            };
        }

        #endregion
    }
}
