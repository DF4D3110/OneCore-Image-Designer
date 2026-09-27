using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// ELF 解析器
    /// 解析 ELF (Executable and Linkable Format) 文件，用于 XBL 等固件分区
    /// 支持 32位和64位 ELF
    /// </summary>
    public static class ElfParser
    {
        #region 数据结构

        public class ElfFile
        {
            public ElfHeader Header { get; set; } = new();
            public List<ProgramHeader> ProgramHeaders { get; } = new();
            public List<SectionHeader> SectionHeaders { get; } = new();
            public List<Symbol> Symbols { get; } = new();
            public List<string> Errors { get; } = new();
            public bool IsValid { get; set; }
        }

        public class ElfHeader
        {
            public byte[] Magic { get; set; } = Array.Empty<byte>(); // 7F 45 4C 46
            public byte Class { get; set; }       // 1=32位, 2=64位
            public byte Data { get; set; }        // 1=小端, 2=大端
            public byte Version { get; set; }
            public byte OsAbi { get; set; }
            public byte AbiVersion { get; set; }
            public ushort Type { get; set; }      // 1=可重定位, 2=可执行, 3=共享, 4=核心
            public ushort Machine { get; set; }   // 架构 (0x28=ARM, 0xB7=AArch64, 0x03=x86, 0x3E=x86_64)
            public uint Version2 { get; set; }
            public ulong EntryPoint { get; set; }
            public ulong ProgramHeaderOffset { get; set; }
            public ulong SectionHeaderOffset { get; set; }
            public uint Flags { get; set; }
            public ushort HeaderSize { get; set; }
            public ushort ProgramHeaderEntrySize { get; set; }
            public ushort ProgramHeaderCount { get; set; }
            public ushort SectionHeaderEntrySize { get; set; }
            public ushort SectionHeaderCount { get; set; }
            public ushort SectionNameStringTableIndex { get; set; }

            public string ClassName => Class == 1 ? "ELF32" : Class == 2 ? "ELF64" : "Unknown";
            public string DataName => Data == 1 ? "Little Endian" : Data == 2 ? "Big Endian" : "Unknown";
            public string TypeName => Type switch
            {
                1 => "Relocatable",
                2 => "Executable",
                3 => "Shared Object",
                4 => "Core File",
                _ => $"Unknown (0x{Type:X4})"
            };
            public string MachineName => Machine switch
            {
                0x00 => "None",
                0x03 => "x86",
                0x28 => "ARM",
                0x3E => "x86_64",
                0xB7 => "AArch64",
                0xF3 => "RISC-V",
                _ => $"Unknown (0x{Machine:X4})"
            };
        }

        public class ProgramHeader
        {
            public uint Type { get; set; }
            public uint Flags { get; set; }       // 64位专用
            public ulong Offset { get; set; }
            public ulong VirtualAddress { get; set; }
            public ulong PhysicalAddress { get; set; }
            public ulong FileSize { get; set; }
            public ulong MemorySize { get; set; }
            public ulong Alignment { get; set; }

            public string TypeName => Type switch
            {
                0x00000000 => "PT_NULL",
                0x00000001 => "PT_LOAD",
                0x00000002 => "PT_DYNAMIC",
                0x00000003 => "PT_INTERP",
                0x00000004 => "PT_NOTE",
                0x00000005 => "PT_SHLIB",
                0x00000006 => "PT_PHDR",
                0x00000007 => "PT_TLS",
                0x60000000 => "PT_LOOS",
                0x6FFFFFFF => "PT_HIOS",
                0x70000000 => "PT_LOPROC",
                0x7FFFFFFF => "PT_HIPROC",
                _ => $"0x{Type:X8}"
            };

            public string FlagsName
            {
                get
                {
                    var sb = new StringBuilder();
                    if ((Flags & 0x4) != 0) sb.Append('R');
                    if ((Flags & 0x2) != 0) sb.Append('W');
                    if ((Flags & 0x1) != 0) sb.Append('X');
                    return sb.ToString();
                }
            }
        }

        public class SectionHeader
        {
            public uint NameOffset { get; set; }
            public string Name { get; set; } = "";
            public uint Type { get; set; }
            public ulong Flags { get; set; }
            public ulong Address { get; set; }
            public ulong Offset { get; set; }
            public ulong Size { get; set; }
            public uint Link { get; set; }
            public uint Info { get; set; }
            public ulong Alignment { get; set; }
            public ulong EntrySize { get; set; }

            public string TypeName => Type switch
            {
                0x00 => "SHT_NULL",
                0x01 => "SHT_PROGBITS",
                0x02 => "SHT_SYMTAB",
                0x03 => "SHT_STRTAB",
                0x04 => "SHT_RELA",
                0x05 => "SHT_HASH",
                0x06 => "SHT_DYNAMIC",
                0x07 => "SHT_NOTE",
                0x08 => "SHT_NOBITS",
                0x09 => "SHT_REL",
                0x0A => "SHT_SHLIB",
                0x0B => "SHT_DYNSYM",
                0x0E => "SHT_INIT_ARRAY",
                0x0F => "SHT_FINI_ARRAY",
                0x10 => "SHT_PREINIT_ARRAY",
                0x11 => "SHT_GROUP",
                0x12 => "SHT_SYMTAB_SHNDX",
                _ => $"0x{Type:X8}"
            };

            public string FlagsName
            {
                get
                {
                    var sb = new StringBuilder();
                    if ((Flags & 0x1) != 0) sb.Append('W'); // SHF_WRITE
                    if ((Flags & 0x2) != 0) sb.Append('A'); // SHF_ALLOC
                    if ((Flags & 0x4) != 0) sb.Append('X'); // SHF_EXECINSTR
                    if ((Flags & 0x10) != 0) sb.Append('M'); // SHF_MERGE
                    if ((Flags & 0x20) != 0) sb.Append('S'); // SHF_STRINGS
                    if ((Flags & 0x40) != 0) sb.Append('I'); // SHF_INFO_LINK
                    if ((Flags & 0x80) != 0) sb.Append('L'); // SHF_LINK_ORDER
                    return sb.ToString();
                }
            }
        }

        public class Symbol
        {
            public uint NameOffset { get; set; }
            public string Name { get; set; } = "";
            public byte Info { get; set; }
            public byte Other { get; set; }
            public ushort SectionIndex { get; set; }
            public ulong Value { get; set; }
            public ulong Size { get; set; }

            public byte Bind => (byte)(Info >> 4);
            public byte Type => (byte)(Info & 0x0F);

            public string BindName => Bind switch
            {
                0 => "LOCAL",
                1 => "GLOBAL",
                2 => "WEAK",
                10 => "LOOS",
                12 => "HIOS",
                13 => "LOPROC",
                15 => "HIPROC",
                _ => $"Unknown({Bind})"
            };

            public string TypeName => Type switch
            {
                0 => "NOTYPE",
                1 => "OBJECT",
                2 => "FUNC",
                3 => "SECTION",
                4 => "FILE",
                5 => "COMMON",
                6 => "TLS",
                10 => "LOOS",
                12 => "HIOS",
                13 => "LOPROC",
                15 => "HIPROC",
                _ => $"Unknown({Type})"
            };
        }

        #endregion

        #region 主解析入口

        /// <summary>
        /// 从文件解析 ELF
        /// </summary>
        public static ElfFile ParseFile(string filePath)
        {
            byte[] data = File.ReadAllBytes(filePath);
            return Parse(data);
        }

        /// <summary>
        /// 从字节数组解析 ELF
        /// </summary>
        public static ElfFile Parse(byte[] data)
        {
            var elf = new ElfFile();

            try
            {
                if (data.Length < 16)
                {
                    elf.Errors.Add("数据太短，不是有效的 ELF 文件");
                    return elf;
                }

                // 验证魔数
                if (data[0] != 0x7F || data[1] != 0x45 || data[2] != 0x4C || data[3] != 0x46)
                {
                    elf.Errors.Add("无效的 ELF 魔数");
                    return elf;
                }

                // 解析 ELF 头
                elf.Header = ParseElfHeader(data);
                elf.IsValid = true;

                // 解析程序头
                ParseProgramHeaders(data, elf);

                // 解析节头
                ParseSectionHeaders(data, elf);

                // 解析符号表
                ParseSymbols(data, elf);
            }
            catch (Exception ex)
            {
                elf.Errors.Add($"解析异常: {ex.Message}");
            }

            return elf;
        }

        /// <summary>
        /// 判断是否为 ELF 文件
        /// </summary>
        public static bool IsElfFile(byte[] data)
        {
            return data.Length >= 4 &&
                   data[0] == 0x7F && data[1] == 0x45 &&
                   data[2] == 0x4C && data[3] == 0x46;
        }

        #endregion

        #region 内部解析方法

        private static ElfHeader ParseElfHeader(byte[] data)
        {
            var header = new ElfHeader
            {
                Magic = new byte[4],
                Class = data[4],
                Data = data[5],
                Version = data[6],
                OsAbi = data[7],
                AbiVersion = data[8]
            };
            Array.Copy(data, 0, header.Magic, 0, 4);

            bool is64 = header.Class == 2;
            bool isLittleEndian = header.Data == 1;

            if (is64)
            {
                // ELF64 头
                header.Type = ReadUInt16(data, 16, isLittleEndian);
                header.Machine = ReadUInt16(data, 18, isLittleEndian);
                header.Version2 = ReadUInt32(data, 20, isLittleEndian);
                header.EntryPoint = ReadUInt64(data, 24, isLittleEndian);
                header.ProgramHeaderOffset = ReadUInt64(data, 32, isLittleEndian);
                header.SectionHeaderOffset = ReadUInt64(data, 40, isLittleEndian);
                header.Flags = ReadUInt32(data, 48, isLittleEndian);
                header.HeaderSize = ReadUInt16(data, 52, isLittleEndian);
                header.ProgramHeaderEntrySize = ReadUInt16(data, 54, isLittleEndian);
                header.ProgramHeaderCount = ReadUInt16(data, 56, isLittleEndian);
                header.SectionHeaderEntrySize = ReadUInt16(data, 58, isLittleEndian);
                header.SectionHeaderCount = ReadUInt16(data, 60, isLittleEndian);
                header.SectionNameStringTableIndex = ReadUInt16(data, 62, isLittleEndian);
            }
            else
            {
                // ELF32 头
                header.Type = ReadUInt16(data, 16, isLittleEndian);
                header.Machine = ReadUInt16(data, 18, isLittleEndian);
                header.Version2 = ReadUInt32(data, 20, isLittleEndian);
                header.EntryPoint = ReadUInt32(data, 24, isLittleEndian);
                header.ProgramHeaderOffset = ReadUInt32(data, 28, isLittleEndian);
                header.SectionHeaderOffset = ReadUInt32(data, 32, isLittleEndian);
                header.Flags = ReadUInt32(data, 36, isLittleEndian);
                header.HeaderSize = ReadUInt16(data, 40, isLittleEndian);
                header.ProgramHeaderEntrySize = ReadUInt16(data, 42, isLittleEndian);
                header.ProgramHeaderCount = ReadUInt16(data, 44, isLittleEndian);
                header.SectionHeaderEntrySize = ReadUInt16(data, 46, isLittleEndian);
                header.SectionHeaderCount = ReadUInt16(data, 48, isLittleEndian);
                header.SectionNameStringTableIndex = ReadUInt16(data, 50, isLittleEndian);
            }

            return header;
        }

        private static void ParseProgramHeaders(byte[] data, ElfFile elf)
        {
            bool is64 = elf.Header.Class == 2;
            bool isLittleEndian = elf.Header.Data == 1;
            ulong offset = elf.Header.ProgramHeaderOffset;
            int entrySize = elf.Header.ProgramHeaderEntrySize;
            int count = elf.Header.ProgramHeaderCount;

            for (int i = 0; i < count; i++)
            {
                ulong entryOffset = offset + (ulong)(i * entrySize);
                if (entryOffset + (ulong)entrySize > (ulong)data.Length) break;

                var ph = new ProgramHeader();

                if (is64)
                {
                    ph.Type = ReadUInt32(data, (int)entryOffset, isLittleEndian);
                    ph.Flags = ReadUInt32(data, (int)entryOffset + 4, isLittleEndian);
                    ph.Offset = ReadUInt64(data, (int)entryOffset + 8, isLittleEndian);
                    ph.VirtualAddress = ReadUInt64(data, (int)entryOffset + 16, isLittleEndian);
                    ph.PhysicalAddress = ReadUInt64(data, (int)entryOffset + 24, isLittleEndian);
                    ph.FileSize = ReadUInt64(data, (int)entryOffset + 32, isLittleEndian);
                    ph.MemorySize = ReadUInt64(data, (int)entryOffset + 40, isLittleEndian);
                    ph.Alignment = ReadUInt64(data, (int)entryOffset + 48, isLittleEndian);
                }
                else
                {
                    ph.Type = ReadUInt32(data, (int)entryOffset, isLittleEndian);
                    ph.Offset = ReadUInt32(data, (int)entryOffset + 4, isLittleEndian);
                    ph.VirtualAddress = ReadUInt32(data, (int)entryOffset + 8, isLittleEndian);
                    ph.PhysicalAddress = ReadUInt32(data, (int)entryOffset + 12, isLittleEndian);
                    ph.FileSize = ReadUInt32(data, (int)entryOffset + 16, isLittleEndian);
                    ph.MemorySize = ReadUInt32(data, (int)entryOffset + 20, isLittleEndian);
                    ph.Flags = ReadUInt32(data, (int)entryOffset + 24, isLittleEndian);
                    ph.Alignment = ReadUInt32(data, (int)entryOffset + 28, isLittleEndian);
                }

                elf.ProgramHeaders.Add(ph);
            }
        }

        private static void ParseSectionHeaders(byte[] data, ElfFile elf)
        {
            bool is64 = elf.Header.Class == 2;
            bool isLittleEndian = elf.Header.Data == 1;
            ulong offset = elf.Header.SectionHeaderOffset;
            int entrySize = elf.Header.SectionHeaderEntrySize;
            int count = elf.Header.SectionHeaderCount;

            // 先解析所有节头
            for (int i = 0; i < count; i++)
            {
                ulong entryOffset = offset + (ulong)(i * entrySize);
                if (entryOffset + (ulong)entrySize > (ulong)data.Length) break;

                var sh = new SectionHeader();

                if (is64)
                {
                    sh.NameOffset = ReadUInt32(data, (int)entryOffset, isLittleEndian);
                    sh.Type = ReadUInt32(data, (int)entryOffset + 4, isLittleEndian);
                    sh.Flags = ReadUInt64(data, (int)entryOffset + 8, isLittleEndian);
                    sh.Address = ReadUInt64(data, (int)entryOffset + 16, isLittleEndian);
                    sh.Offset = ReadUInt64(data, (int)entryOffset + 24, isLittleEndian);
                    sh.Size = ReadUInt64(data, (int)entryOffset + 32, isLittleEndian);
                    sh.Link = ReadUInt32(data, (int)entryOffset + 40, isLittleEndian);
                    sh.Info = ReadUInt32(data, (int)entryOffset + 44, isLittleEndian);
                    sh.Alignment = ReadUInt64(data, (int)entryOffset + 48, isLittleEndian);
                    sh.EntrySize = ReadUInt64(data, (int)entryOffset + 56, isLittleEndian);
                }
                else
                {
                    sh.NameOffset = ReadUInt32(data, (int)entryOffset, isLittleEndian);
                    sh.Type = ReadUInt32(data, (int)entryOffset + 4, isLittleEndian);
                    sh.Flags = ReadUInt32(data, (int)entryOffset + 8, isLittleEndian);
                    sh.Address = ReadUInt32(data, (int)entryOffset + 12, isLittleEndian);
                    sh.Offset = ReadUInt32(data, (int)entryOffset + 16, isLittleEndian);
                    sh.Size = ReadUInt32(data, (int)entryOffset + 20, isLittleEndian);
                    sh.Link = ReadUInt32(data, (int)entryOffset + 24, isLittleEndian);
                    sh.Info = ReadUInt32(data, (int)entryOffset + 28, isLittleEndian);
                    sh.Alignment = ReadUInt32(data, (int)entryOffset + 32, isLittleEndian);
                    sh.EntrySize = ReadUInt32(data, (int)entryOffset + 36, isLittleEndian);
                }

                elf.SectionHeaders.Add(sh);
            }

            // 解析节名（从字符串表）
            int strTabIndex = elf.Header.SectionNameStringTableIndex;
            if (strTabIndex > 0 && strTabIndex < elf.SectionHeaders.Count)
            {
                var strTab = elf.SectionHeaders[strTabIndex];
                foreach (var sh in elf.SectionHeaders)
                {
                    sh.Name = ReadString(data, (int)(strTab.Offset + sh.NameOffset));
                }
            }
        }

        private static void ParseSymbols(byte[] data, ElfFile elf)
        {
            bool is64 = elf.Header.Class == 2;
            bool isLittleEndian = elf.Header.Data == 1;

            // 查找符号表节 (SHT_SYMTAB 或 SHT_DYNSYM)
            foreach (var sh in elf.SectionHeaders)
            {
                if (sh.Type != 0x02 && sh.Type != 0x0B) continue; // SHT_SYMTAB / SHT_DYNSYM
                if (sh.Size == 0 || sh.Offset == 0) continue;

                // 关联的字符串表
                int strTabIndex = (int)sh.Link;
                SectionHeader? strTab = null;
                if (strTabIndex >= 0 && strTabIndex < elf.SectionHeaders.Count)
                {
                    strTab = elf.SectionHeaders[strTabIndex];
                }

                int entrySize = is64 ? 24 : 16;
                if (sh.EntrySize > 0) entrySize = (int)sh.EntrySize;

                int count = (int)(sh.Size / (ulong)entrySize);
                for (int i = 0; i < count; i++)
                {
                    ulong entryOffset = sh.Offset + (ulong)(i * entrySize);
                    if (entryOffset + (ulong)entrySize > (ulong)data.Length) break;

                    var sym = new Symbol();

                    if (is64)
                    {
                        sym.NameOffset = ReadUInt32(data, (int)entryOffset, isLittleEndian);
                        sym.Info = data[(int)entryOffset + 4];
                        sym.Other = data[(int)entryOffset + 5];
                        sym.SectionIndex = ReadUInt16(data, (int)entryOffset + 6, isLittleEndian);
                        sym.Value = ReadUInt64(data, (int)entryOffset + 8, isLittleEndian);
                        sym.Size = ReadUInt64(data, (int)entryOffset + 16, isLittleEndian);
                    }
                    else
                    {
                        sym.NameOffset = ReadUInt32(data, (int)entryOffset, isLittleEndian);
                        sym.Value = ReadUInt32(data, (int)entryOffset + 4, isLittleEndian);
                        sym.Size = ReadUInt32(data, (int)entryOffset + 8, isLittleEndian);
                        sym.Info = data[(int)entryOffset + 12];
                        sym.Other = data[(int)entryOffset + 13];
                        sym.SectionIndex = ReadUInt16(data, (int)entryOffset + 14, isLittleEndian);
                    }

                    if (strTab != null && sym.NameOffset > 0)
                    {
                        sym.Name = ReadString(data, (int)(strTab.Offset + sym.NameOffset));
                    }

                    elf.Symbols.Add(sym);
                }
            }
        }

        #endregion

        #region 工具方法

        private static ushort ReadUInt16(byte[] data, int offset, bool littleEndian)
        {
            if (offset + 2 > data.Length) return 0;
            return littleEndian
                ? (ushort)(data[offset] | (data[offset + 1] << 8))
                : (ushort)((data[offset] << 8) | data[offset + 1]);
        }

        private static uint ReadUInt32(byte[] data, int offset, bool littleEndian)
        {
            if (offset + 4 > data.Length) return 0;
            return littleEndian
                ? (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24))
                : (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
        }

        private static ulong ReadUInt64(byte[] data, int offset, bool littleEndian)
        {
            if (offset + 8 > data.Length) return 0;
            uint low = ReadUInt32(data, offset, littleEndian);
            uint high = ReadUInt32(data, offset + 4, littleEndian);
            return littleEndian
                ? ((ulong)high << 32) | low
                : ((ulong)low << 32) | high;
        }

        private static string ReadString(byte[] data, int offset)
        {
            if (offset < 0 || offset >= data.Length) return "";
            int end = offset;
            while (end < data.Length && data[end] != 0) end++;
            return Encoding.UTF8.GetString(data, offset, end - offset);
        }

        /// <summary>
        /// 生成 ELF 解析报告
        /// </summary>
        public static string GenerateReport(ElfFile elf)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== ELF 解析报告 ===");
            sb.AppendLine();

            if (!elf.IsValid)
            {
                sb.AppendLine("❌ 无效的 ELF 文件");
                foreach (var err in elf.Errors)
                    sb.AppendLine($"  错误: {err}");
                return sb.ToString();
            }

            var h = elf.Header;
            sb.AppendLine("--- ELF 头 ---");
            sb.AppendLine($"  格式: {h.ClassName} ({h.DataName})");
            sb.AppendLine($"  类型: {h.TypeName}");
            sb.AppendLine($"  架构: {h.MachineName}");
            sb.AppendLine($"  入口点: 0x{h.EntryPoint:X}");
            sb.AppendLine($"  程序头: 偏移=0x{h.ProgramHeaderOffset:X}, 数量={h.ProgramHeaderCount}, 大小={h.ProgramHeaderEntrySize}");
            sb.AppendLine($"  节头: 偏移=0x{h.SectionHeaderOffset:X}, 数量={h.SectionHeaderCount}, 大小={h.SectionHeaderEntrySize}");
            sb.AppendLine();

            if (elf.ProgramHeaders.Count > 0)
            {
                sb.AppendLine($"--- 程序头 ({elf.ProgramHeaders.Count}) ---");
                for (int i = 0; i < elf.ProgramHeaders.Count; i++)
                {
                    var ph = elf.ProgramHeaders[i];
                    sb.AppendLine($"  [{i}] {ph.TypeName}");
                    sb.AppendLine($"       偏移=0x{ph.Offset:X}, 虚拟地址=0x{ph.VirtualAddress:X}, 物理地址=0x{ph.PhysicalAddress:X}");
                    sb.AppendLine($"       文件大小={ph.FileSize:N0}, 内存大小={ph.MemorySize:N0}, 对齐=0x{ph.Alignment:X}, 标志={ph.FlagsName}");
                }
                sb.AppendLine();
            }

            if (elf.SectionHeaders.Count > 0)
            {
                sb.AppendLine($"--- 节头 ({elf.SectionHeaders.Count}) ---");
                for (int i = 0; i < elf.SectionHeaders.Count; i++)
                {
                    var sh = elf.SectionHeaders[i];
                    sb.AppendLine($"  [{i}] {sh.Name,-24} {sh.TypeName,-20} 地址=0x{sh.Address:X16} 偏移=0x{sh.Offset:X8} 大小={sh.Size,12:N0} 标志={sh.FlagsName}");
                }
                sb.AppendLine();
            }

            if (elf.Symbols.Count > 0)
            {
                sb.AppendLine($"--- 符号表 ({elf.Symbols.Count}) ---");
                int funcCount = 0;
                int objCount = 0;
                foreach (var sym in elf.Symbols)
                {
                    if (sym.Type == 2) funcCount++;
                    else if (sym.Type == 1) objCount++;
                }
                sb.AppendLine($"  函数: {funcCount}, 数据: {objCount}");
                sb.AppendLine();

                // 只显示有名字的全局符号
                foreach (var sym in elf.Symbols)
                {
                    if (!string.IsNullOrEmpty(sym.Name) && sym.Bind >= 1)
                    {
                        sb.AppendLine($"  {sym.TypeName,-8} {sym.BindName,-8} 0x{sym.Value:X16} {sym.Size,8:N0} {sym.Name}");
                    }
                }
            }

            return sb.ToString();
        }

        #endregion
    }
}
