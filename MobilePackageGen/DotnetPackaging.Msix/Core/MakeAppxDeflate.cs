using System.Diagnostics;
using Zafiro.Reactive;

namespace DotnetPackaging.Msix.Core
{
    public static class MakeAppxDeflate
    {
        public static readonly bool USE_EXTERNAL_DEFLATE_VIA_MAKEAPPX = false;

        private static readonly string MAKEAPPX_LOCATION = @"RemakeAppxTestContent\i386\makeappx.exe";
        private static readonly string AppxContent = @"RemakeAppxTestContent\Minimal\Contents";
        private static readonly string AppxOutput = @"RemakeAppxTestContent\Minimal\App.appx";

        public static IObservable<byte[]> GetMakeAppxVersionOfDeflate(IByteSource source)
        {
            using MemoryStream MemoryStream = new();
            source.WriteTo(MemoryStream).GetAwaiter().GetResult();

            return ByteSource.FromBytes(GetMakeAppxVersionOfDeflate(MemoryStream.ToArray()).GetAwaiter().GetResult());
        }

        public static async Task<byte[]?> GetMakeAppxVersionOfDeflate(byte[] inputBuffer)
        {
            if (inputBuffer.Length == 0)
            {
                return [0x03, 0x00];
            }

            // 这条路径是"用真 makeappx 产出对照结果"的研究路径，依赖 RemakeAppxTestContent\i386 下的
            // Microsoft Windows SDK 夹具（makeappx.exe / opcservices.dll / appxpackaging.dll）。
            // 这些微软预编译二进制已从本仓库移除（公开仓库再分发），而
            // USE_EXTERNAL_DEFLATE_VIA_MAKEAPPX 为 false 时本方法根本不参与打包
            // （MsixEntryFactory / MsixPackager 走自带的 DeflateBlockCompressor）。
            // 夹具不在时明确报错，避免上游那种"路径写死、失败后拿到半截结果"的静默行为。
            if (!File.Exists(MAKEAPPX_LOCATION))
            {
                throw new FileNotFoundException(
                    $"外部 makeappx 对照夹具不存在：{MAKEAPPX_LOCATION}。" +
                    "该夹具（makeappx.exe / opcservices.dll / appxpackaging.dll 等微软 Windows SDK 二进制）" +
                    "已从本仓库移除；如需复现请自行从 Windows SDK 取得后放回 RemakeAppxTestContent\\i386，" +
                    "或改用自带的 DeflateBlockCompressor 实现（把 USE_EXTERNAL_DEFLATE_VIA_MAKEAPPX 保持为 false）。",
                    MAKEAPPX_LOCATION);
            }

            if (File.Exists(AppxOutput))
            {
                await Task.Delay(1000);

                File.Delete(AppxOutput);
            }

            File.WriteAllBytes(System.IO.Path.Combine(AppxContent, "HelloWorld.exe"), inputBuffer);

            string args = $"pack -d \"{AppxContent}\" -p \"{AppxOutput}\" -l";

            Process proc = new()
            {
                StartInfo = new ProcessStartInfo(MAKEAPPX_LOCATION, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardInput = true
                },
            };
            proc.Start();
            await proc.WaitForExitAsync();

            List<byte> compressed = [];

            using (Stream appxStream = File.OpenRead(AppxOutput))
            {
                appxStream.Seek(0x118, SeekOrigin.Begin);

                while (true)
                {
                    int b = appxStream.ReadByte();

                    if (b == -1)
                    {
                        break;
                    }

                    compressed.Add((byte)b);

                    if (compressed.Count > 4 && compressed[^1] == 0x08 && compressed[^2] == 0x07 && compressed[^3] == 0x4B && compressed[^4] == 0x50)
                    {
                        compressed.RemoveRange(compressed.Count - 5, 4);
                        break;
                    }
                }
            }

            if (File.Exists(AppxOutput))
            {
                File.Delete(AppxOutput);
            }

            if (compressed[^1] == 0x00 && compressed[^2] == 0x03 && compressed[^3] == 0xFF && compressed[^4] == 0xFF)
            {
                return [.. compressed];
            }

            if (compressed[^1] == 0x08 && compressed[^2] == 0x03 && compressed[^3] == 0xFF && compressed[^4] == 0xFF)
            {
                compressed[^1] = 0x00;
                return [.. compressed];
            }

            /*if (compressed[^1] == 0x00 && compressed[^2] == 0x03 && compressed[^3] == 0xFF && compressed[^4] == 0xFF)
            {
                return [.. compressed];
            }

            if (compressed[^1] == 0x08 && compressed[^2] == 0x03 && compressed[^3] == 0xFF && compressed[^4] == 0xFF)
            {
                compressed[^1] = 0x00;
                return [.. compressed];
            }

            // FF FF 03 00
            if (compressed[^1] == 0x00 && compressed[^2] == 0x03 && compressed[^3] == 0xFF && compressed[^4] == 0xFF)
            {
                return [.. compressed];
            }
            else if (compressed[^1] == 0x03 && compressed[^2] == 0xFF && compressed[^3] == 0xFF)
            {
                return [.. compressed, 0x00];
            }
            else if (compressed[^1] == 0xFF && compressed[^2] == 0xFF)
            {
                return [.. compressed, 0x03, 0x00];
            }
            else if (compressed[^1] == 0xFF)
            {
                return [.. compressed, 0xFF, 0x03, 0x00];
            }
            else
            {
                return [.. compressed, 0xFF, 0xFF, 0x03, 0x00];
            }*/
            return [.. compressed];
        }
    }
}
