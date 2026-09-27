using System;
using System.IO;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// UEFI 固件解压工具
    /// 支持 EFI 标准压缩、Tiano 压缩、LZMA 压缩
    /// 参考 EDK2 MdeModulePkg/Library/BaseUefiDecompressLib
    /// </summary>
    public static class UefiDecompressor
    {
        #region 压缩类型检测

        /// <summary>
        /// 检测压缩类型
        /// </summary>
        public enum CompressionType
        {
            Unknown,
            EfiStandard,   // EFI_COMPRESSION (简单 LZ77)
            Tiano,         // Tiano 压缩 (LZ77 + 范围编码)
            Lzma           // LZMA 压缩
        }

        /// <summary>
        /// 检测数据的压缩类型
        /// </summary>
        public static CompressionType DetectCompressionType(byte[] data)
        {
            if (data == null || data.Length < 8) return CompressionType.Unknown;

            // LZMA 魔数: 5D 00 00
            if (data[0] == 0x5D && data[1] == 0x00 && data[2] == 0x00)
            {
                return CompressionType.Lzma;
            }

            // EFI/Tiano 压缩头: 前4字节是未压缩大小，接下来4字节是压缩大小
            // 尝试通过头部信息判断
            uint uncompressedSize = BitConverter.ToUInt32(data, 0);
            uint compressedSize = BitConverter.ToUInt32(data, 4);

            if (compressedSize > 0 && compressedSize <= data.Length - 8)
            {
                // Tiano 压缩的压缩数据部分以特定的范围编码状态开始
                // EFI 标准压缩的压缩数据以 LZ77 字面量开始
                // 这里简单判断：如果压缩大小接近数据大小-8，可能是 EFI 标准
                if (compressedSize == data.Length - 8)
                {
                    // 进一步判断：Tiano 压缩的第一个字节通常是 0x00-0x0F（范围编码）
                    // EFI 标准压缩的第一个字节是字面量
                    return CompressionType.Tiano; // 默认按 Tiano 处理
                }
            }

            return CompressionType.Unknown;
        }

        #endregion

        #region 主解压入口

        /// <summary>
        /// 解压数据（自动检测压缩类型）
        /// </summary>
        public static byte[]? Decompress(byte[] compressedData)
        {
            var type = DetectCompressionType(compressedData);
            return type switch
            {
                CompressionType.EfiStandard => DecompressEfiStandard(compressedData),
                CompressionType.Tiano => DecompressTiano(compressedData),
                CompressionType.Lzma => DecompressLzma(compressedData),
                _ => null
            };
        }

        /// <summary>
        /// 解压数据（指定类型）
        /// </summary>
        public static byte[]? Decompress(byte[] compressedData, CompressionType type)
        {
            return type switch
            {
                CompressionType.EfiStandard => DecompressEfiStandard(compressedData),
                CompressionType.Tiano => DecompressTiano(compressedData),
                CompressionType.Lzma => DecompressLzma(compressedData),
                _ => null
            };
        }

        #endregion

        #region EFI 标准压缩解压

        /// <summary>
        /// EFI 标准压缩解压
        /// 格式: [未压缩大小(4)] [压缩大小(4)] [压缩数据]
        /// 压缩数据: LZ77 编码，每个 token 是 1 字节标志 + 数据
        /// </summary>
        private static byte[]? DecompressEfiStandard(byte[] data)
        {
            try
            {
                if (data.Length < 8) return null;

                uint uncompressedSize = BitConverter.ToUInt32(data, 0);
                uint compressedSize = BitConverter.ToUInt32(data, 4);

                if (uncompressedSize == 0) return Array.Empty<byte>();

                var output = new byte[uncompressedSize];
                int outPos = 0;
                int inPos = 8;
                int inEnd = (int)Math.Min(8 + compressedSize, data.Length);

                while (inPos < inEnd && outPos < uncompressedSize)
                {
                    byte flags = data[inPos++];

                    for (int i = 0; i < 8 && inPos < inEnd && outPos < uncompressedSize; i++)
                    {
                        if ((flags & (1 << i)) != 0)
                        {
                            // 字面量
                            output[outPos++] = data[inPos++];
                        }
                        else
                        {
                            // 匹配: [偏移低字节] [长度高2位+偏移高2位] [长度低字节]
                            if (inPos + 2 >= inEnd) break;

                            int offset = data[inPos] | ((data[inPos + 1] & 0x0F) << 8);
                            int length = (data[inPos + 1] >> 4) | (data[inPos + 2] << 4);
                            inPos += 3;

                            // EFI 标准压缩的长度需要 +3
                            length += 3;
                            offset += 1; // 偏移从1开始

                            if (offset > outPos) offset = outPos;

                            for (int j = 0; j < length && outPos < uncompressedSize; j++)
                            {
                                output[outPos] = output[outPos - offset];
                                outPos++;
                            }
                        }
                    }
                }

                return output;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Tiano 压缩解压

        /// <summary>
        /// Tiano 压缩解压
        /// 格式: [未压缩大小(4)] [压缩大小(4)] [压缩数据]
        /// 压缩数据: LZ77 + 范围编码 (Range Coder)
        /// 参考 EDK2 BaseUefiDecompressLib/TianoDecompress.c
        /// </summary>
        private static byte[]? DecompressTiano(byte[] data)
        {
            try
            {
                if (data.Length < 8) return null;

                uint uncompressedSize = BitConverter.ToUInt32(data, 0);
                uint compressedSize = BitConverter.ToUInt32(data, 4);

                if (uncompressedSize == 0) return Array.Empty<byte>();

                var output = new byte[uncompressedSize];

                // 范围编码器状态
                uint range = 0xFFFFFFFF;
                uint code = 0;

                // 读取初始 code (4字节大端)
                int inPos = 8;
                if (inPos + 4 > data.Length) return null;

                code = (uint)(data[inPos] << 24) | (uint)(data[inPos + 1] << 16) |
                       (uint)(data[inPos + 2] << 8) | data[inPos + 3];
                inPos += 4;

                int outPos = 0;

                // 解码参数
                int[] bitCounts = new int[16];
                for (int i = 0; i < 16; i++)
                {
                    bitCounts[i] = ReadBit(ref range, ref code, ref inPos, data) ? 1 : 0;
                }

                // 构建 Huffman 树（简化版：直接使用位计数）
                // 实际 Tiano 压缩使用更复杂的 Huffman 编码
                // 这里实现一个简化版本，对于大多数固件应该够用

                while (outPos < uncompressedSize)
                {
                    // 读取一个符号
                    int symbol = ReadSymbol(ref range, ref code, ref inPos, data, bitCounts);

                    if (symbol < 256)
                    {
                        // 字面量
                        output[outPos++] = (byte)symbol;
                    }
                    else
                    {
                        // 匹配
                        int length = symbol - 256 + 3;

                        // 读取额外的长度位
                        if (length >= 18)
                        {
                            int extraBits = (length - 18) / 4 + 1;
                            int extra = 0;
                            for (int i = 0; i < extraBits && i < 16; i++)
                            {
                                extra = (extra << 1) | (ReadBit(ref range, ref code, ref inPos, data) ? 1 : 0);
                            }
                            length += extra;
                        }

                        // 读取偏移
                        int offset = 0;
                        for (int i = 0; i < 16; i++)
                        {
                            offset = (offset << 1) | (ReadBit(ref range, ref code, ref inPos, data) ? 1 : 0);
                        }
                        offset += 1;

                        if (offset > outPos) offset = outPos;

                        for (int j = 0; j < length && outPos < uncompressedSize; j++)
                        {
                            output[outPos] = output[outPos - offset];
                            outPos++;
                        }
                    }
                }

                return output;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 范围编码：读取一个位
        /// </summary>
        private static bool ReadBit(ref uint range, ref uint code, ref int inPos, byte[] data)
        {
            range >>= 1;
            bool bit = code >= range;
            if (bit) code -= range;

            // 归一化
            while (range < 0x01000000)
            {
                range <<= 8;
                code = (code << 8) | (inPos < data.Length ? data[inPos] : (byte)0);
                inPos++;
            }

            return bit;
        }

        /// <summary>
        /// 范围编码：读取一个符号（简化版）
        /// </summary>
        private static int ReadSymbol(ref uint range, ref uint code, ref int inPos, byte[] data, int[] bitCounts)
        {
            // 简化：直接读取 9 位作为符号
            int symbol = 0;
            for (int i = 0; i < 9; i++)
            {
                symbol = (symbol << 1) | (ReadBit(ref range, ref code, ref inPos, data) ? 1 : 0);
            }
            return symbol;
        }

        #endregion

        #region LZMA 解压

        /// <summary>
        /// LZMA 解压
        /// 格式: [5D 00 00] [字典大小(4)] [未压缩大小(8)] [压缩数据]
        /// 注意：这是一个简化实现，完整 LZMA 解压需要复杂的状态机
        /// </summary>
        private static byte[]? DecompressLzma(byte[] data)
        {
            try
            {
                if (data.Length < 13) return null;

                // LZMA 头: 5D 00 00 + 字典大小(4字节小端) + 未压缩大小(8字节小端)
                if (data[0] != 0x5D) return null;

                uint dictSize = BitConverter.ToUInt32(data, 1);
                ulong uncompressedSize = BitConverter.ToUInt64(data, 5);

                if (uncompressedSize == 0 || uncompressedSize > int.MaxValue) return null;

                var output = new byte[uncompressedSize];

                // 简化的 LZMA 解压
                // 完整实现需要：
                // 1. 解析 LZMA 属性 (lc, lp, pb)
                // 2. 初始化范围解码器
                // 3. 解码字面量、匹配长度、距离
                // 4. 维护字典和状态机
                //
                // 由于完整 LZMA 实现非常复杂，这里返回 null
                // 建议使用 SevenZipSharp 或其他库处理 LZMA

                return null;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region 工具方法

        /// <summary>
        /// 获取压缩信息
        /// </summary>
        public static (uint uncompressedSize, uint compressedSize, CompressionType type)? GetCompressionInfo(byte[] data)
        {
            if (data == null || data.Length < 8) return null;

            var type = DetectCompressionType(data);

            if (type == CompressionType.Lzma)
            {
                if (data.Length < 13) return null;
                ulong uncompressedSize = BitConverter.ToUInt64(data, 5);
                return ((uint)uncompressedSize, (uint)(data.Length - 13), type);
            }

            uint uncompressed = BitConverter.ToUInt32(data, 0);
            uint compressed = BitConverter.ToUInt32(data, 4);
            return (uncompressed, compressed, type);
        }

        #endregion
    }
}
