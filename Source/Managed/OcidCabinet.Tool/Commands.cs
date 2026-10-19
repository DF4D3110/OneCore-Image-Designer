// 五个命令的实现：info / list / extract / cat / verify。
//
// 设计约束：
//  * 容器读取只走 MobilePackageGen/Cabinet 的公共 API（open → Files / FolderInfo →
//    ExtractAllFiles / ReadFile），压缩解码由它内部调用 OcidCabinet。
//  * 不静默降级：container CAB（含 _manifest_.cix.xml）与 sparse CAB（iFolder
//    0xFFFD..0xFFFF）如实拒绝或提醒，因为本读取器给不出真实文件名 / 拼不回分段。
//  * 路径安全：柜内条目名来自不可信输入，解包前拒绝绝对路径与 ".."。

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CabinetReader = Cabinet.Cabinet;
using CabinetEntry = Cabinet.CabinetFile;

namespace OcidCabinet.Tool;

/// <summary>命令行层面的错误（用法错误之外的、需要一行中文诊断的失败）。</summary>
internal sealed class CabToolException(string message) : Exception(message);

internal static class Commands
{
    private const string ContainerManifest = "_manifest_.cix.xml";

    internal static int PrintVersion()
    {
        Console.WriteLine($"ocid_cab {Program.Version} —— CAB 控制台（自带 MSZIP / LZX 解码）");
        Console.WriteLine("容器读取：MobilePackageGen/Cabinet；压缩解码：Source/Managed/OcidCabinet。");
        return 0;
    }

    // ── info ────────────────────────────────────────────────────────────────

    internal static int Info(string[] args)
    {
        ParsedArgs parsed = ParsedArgs.Parse(args);
        if (parsed.Positional.Count != 1)
        {
            return UsageError("ocid_cab info <cab>");
        }

        string cabPath = parsed.Positional[0];
        using FileStream stream = OpenStream(cabPath);
        CabinetReader cab = new(stream);
        (ushort setId, ushort index, byte major, byte minor, int folders, int files, uint size) = cab.HeaderInfo;

        Console.WriteLine($"柜文件：{Path.GetFullPath(cabPath)}");
        Console.WriteLine($"格式版本：{major}.{minor}    柜集 ID：0x{setId:X4}    柜序号：{index}");
        Console.WriteLine($"文件夹：{folders}    条目：{files}    柜长度：{Number(size)} 字节");

        long total = cab.Files.Sum(entry => (long)entry.UncompressedSize);
        Console.WriteLine($"条目总大小：{Number(total)} 字节（未压缩）");

        Console.WriteLine();
        Console.WriteLine("文件夹布局：");
        foreach ((int folderIndex, string compression, byte option, int blocks, uint firstOffset) in cab.FolderInfo)
        {
            Console.WriteLine($"  [{folderIndex}] {CompressionName(compression, option),-18} 数据块 {blocks}    首块偏移 {firstOffset}");
        }

        // 两种超出本读取器的包，在这里说清楚而不是留到解包时才炸。
        List<string> notes = [];
        if (cab.IsSparse)
        {
            notes.Add("sparse CAB：文件用 iFolder 0xFFFD..0xFFFF 分段存放，本读取器不会拼接。");
        }
        if (HasContainerManifest(cab))
        {
            notes.Add("container CAB：条目是数字 id，真实文件名与 PA30 delta 都在 " + ContainerManifest + " 里；");
            notes.Add("             只有 expand.exe 会按清单还原真实文件，本工具只能原样取出载荷。");
        }
        if (notes.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("提醒：");
            foreach (string note in notes)
            {
                Console.WriteLine($"  {note}");
            }
        }

        return 0;
    }

    // ── list ────────────────────────────────────────────────────────────────

    internal static int List(string[] args)
    {
        ParsedArgs parsed = ParsedArgs.Parse(args);
        if (parsed.Positional.Count != 1)
        {
            return UsageError("ocid_cab list <cab> [--long]");
        }

        bool longFormat = parsed.Flags.Contains("long") || parsed.Flags.Contains("l");
        string cabPath = parsed.Positional[0];

        using FileStream stream = OpenStream(cabPath);
        CabinetReader cab = new(stream);
        List<CabinetEntry> entries = cab.Files.ToList();
        if (entries.Count == 0)
        {
            Console.WriteLine($"柜文件：{Path.GetFullPath(cabPath)}");
            Console.WriteLine("（柜里没有条目）");
            return 0;
        }

        Dictionary<int, string> folderNames = cab.FolderInfo
            .ToDictionary(folder => folder.Index, folder => CompressionName(folder.Compression, folder.Option));

        int nameWidth = Math.Clamp(entries.Max(entry => DisplayWidth(entry.FileName)), 8, 60);
        StringBuilder header = new();
        header.Append(PadVisual("名称", nameWidth));
        header.Append(PadLeftVisual("大小", 14));
        header.Append("  修改时间          属性");
        if (longFormat)
        {
            header.Append("  folder / 压缩");
        }
        Console.WriteLine(header.ToString());

        long total = 0;
        foreach (CabinetEntry entry in entries)
        {
            total += entry.UncompressedSize;
            StringBuilder line = new();
            line.Append(PadVisual(entry.FileName, nameWidth));
            line.Append(PadLeftVisual(Number(entry.UncompressedSize), 14));
            line.Append("  ");
            line.Append(entry.TimeStamp.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            line.Append("  ");
            line.Append(Attributes(entry.FileAttributes));
            if (longFormat)
            {
                string folder = folderNames.TryGetValue(entry.FolderIndex, out string? name) ? name : "?";
                line.Append($"    [{entry.FolderIndex}] {folder}");
            }
            Console.WriteLine(line.ToString());
        }

        Console.WriteLine();
        Console.WriteLine($"共 {entries.Count} 个条目，未压缩总计 {Number(total)} 字节。");
        return 0;
    }

    // ── extract ─────────────────────────────────────────────────────────────

    internal static int Extract(string[] args)
    {
        ParsedArgs parsed = ParsedArgs.Parse(args);
        if (parsed.Positional.Count != 2)
        {
            return UsageError("ocid_cab extract <cab> <目录> [--force] [--verbose]");
        }

        bool force = parsed.Flags.Contains("force");
        bool verbose = parsed.Flags.Contains("verbose");
        string cabPath = parsed.Positional[0];
        string outputDirectory = parsed.Positional[1];

        using FileStream stream = OpenStream(cabPath);
        CabinetReader cab = new(stream);

        if (cab.IsSparse)
        {
            Console.Error.WriteLine("错误：这是 sparse CAB（iFolder 0xFFFD..0xFFFF），本读取器不会拼接分段文件；请用 expand.exe 解这类包。");
            return 2;
        }

        if (HasContainerManifest(cab) && !force)
        {
            Console.Error.WriteLine("错误：这是 container CAB（含 " + ContainerManifest + "）。条目是数字 id 与 PA30 delta，");
            Console.Error.WriteLine("      直接解出的载荷不是真实文件；先用 expand.exe 解，或加 --force 原样取载荷。");
            return 2;
        }

        EnsureSafeEntryNames(cab);

        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any() && !force)
        {
            Console.Error.WriteLine($"错误：目标目录不是空的：{Path.GetFullPath(outputDirectory)}");
            Console.Error.WriteLine("      继续会覆盖同名文件；确认要写就加 --force。");
            return 2;
        }

        Directory.CreateDirectory(outputDirectory);
        long total = cab.Files.Sum(entry => (long)entry.UncompressedSize);
        int count = cab.Files.Count;

        Stopwatch watch = Stopwatch.StartNew();
        cab.ExtractAllFiles(
            outputDirectory,
            verbose ? (percent, name) => Console.Error.WriteLine($"  [{percent,3}%] {name}") : null);
        watch.Stop();

        Console.WriteLine($"已解包 {count} 个文件，共 {Number(total)} 字节 → {Path.GetFullPath(outputDirectory)}");
        Console.WriteLine($"用时 {watch.Elapsed.TotalSeconds:F2} 秒。");
        return 0;
    }

    // ── cat ─────────────────────────────────────────────────────────────────

    internal static int Cat(string[] args)
    {
        ParsedArgs parsed = ParsedArgs.Parse(args, "output");
        if (parsed.Positional.Count != 2)
        {
            return UsageError("ocid_cab cat <cab> <条目> [--output <文件>]");
        }

        string cabPath = parsed.Positional[0];
        string entryName = parsed.Positional[1];

        using FileStream stream = OpenStream(cabPath);
        CabinetReader cab = new(stream);

        CabinetEntry? entry = cab.Files.FirstOrDefault(candidate =>
            string.Equals(Normalise(candidate.FileName), Normalise(entryName), StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            Console.Error.WriteLine($"错误：柜里没有条目“{entryName}”。用 ocid_cab list 查看条目名。");
            return 2;
        }

        byte[] data = cab.ReadFile(entry.FileName);
        if (parsed.Values.TryGetValue("output", out string? outputPath))
        {
            File.WriteAllBytes(outputPath, data);
            Console.WriteLine($"已写出 {Number(data.Length)} 字节 → {Path.GetFullPath(outputPath)}");
            return 0;
        }

        using Stream stdout = Console.OpenStandardOutput();
        stdout.Write(data);
        stdout.Flush();
        Console.Error.WriteLine($"已写出 {Number(data.Length)} 字节到 stdout。");
        return 0;
    }

    // ── verify ──────────────────────────────────────────────────────────────

    internal static int Verify(string[] args)
    {
        ParsedArgs parsed = ParsedArgs.Parse(args);
        if (parsed.Positional.Count != 1)
        {
            return UsageError("ocid_cab verify <cab>");
        }

        string cabPath = parsed.Positional[0];
        using FileStream stream = OpenStream(cabPath);
        CabinetReader cab = new(stream);

        if (cab.IsSparse)
        {
            Console.Error.WriteLine("错误：这是 sparse CAB（iFolder 0xFFFD..0xFFFF），本读取器不会拼接分段文件；请用 expand.exe 解这类包。");
            return 2;
        }

        EnsureSafeEntryNames(cab);
        int expected = cab.Files.Count;

        string temporary = Path.Combine(Path.GetTempPath(), "ocid_cab_verify_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            Stopwatch watch = Stopwatch.StartNew();
            cab.ExtractAllFiles(temporary);
            watch.Stop();

            List<(string Relative, long Size, string Hash)> results = [];
            foreach (string path in Directory.EnumerateFiles(temporary, "*", SearchOption.AllDirectories))
            {
                FileInfo info = new(path);
                using FileStream file = File.OpenRead(path);
                results.Add((Path.GetRelativePath(temporary, path), info.Length,
                             Convert.ToHexString(SHA256.HashData(file))));
            }

            int nameWidth = Math.Clamp(results.Count == 0 ? 8 : results.Max(item => DisplayWidth(item.Relative)), 8, 60);
            foreach ((string relative, long size, string hash) in results.OrderBy(item => item.Relative, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"{PadVisual(relative, nameWidth)} {PadLeftVisual(Number(size), 14)}  {hash}");
            }

            Console.WriteLine();
            if (results.Count != expected)
            {
                Console.Error.WriteLine($"错误：柜内声明 {expected} 个条目，实际解出 {results.Count} 个。");
                return 2;
            }
            Console.WriteLine($"校验通过：{results.Count} 个文件、{Number(results.Sum(item => item.Size))} 字节全部解出（SHA-256 如上），用时 {watch.Elapsed.TotalSeconds:F2} 秒。");
            return 0;
        }
        finally
        {
            try
            {
                Directory.Delete(temporary, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响结论。
            }
        }
    }

    // ── 共用 ────────────────────────────────────────────────────────────────

    private static FileStream OpenStream(string cabPath)
    {
        if (!File.Exists(cabPath))
        {
            throw new CabToolException($"找不到柜文件：{cabPath}");
        }

        // 读取器会一直拿着这个流做后续 Seek，所以此处不能 using（由调用方的 using 管生命周期）。
        return new FileStream(cabPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private static bool HasContainerManifest(CabinetReader cab) =>
        cab.Files.Any(entry => string.Equals(Path.GetFileName(entry.FileName), ContainerManifest, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 柜内条目名是外部数据：绝对路径与 ".." 段会让解包写到目标目录之外，直接拒绝。
    /// </summary>
    private static void EnsureSafeEntryNames(CabinetReader cab)
    {
        foreach (CabinetEntry entry in cab.Files)
        {
            string name = Normalise(entry.FileName);
            if (Path.IsPathRooted(name))
            {
                throw new CabToolException($"柜内含绝对路径条目：{entry.FileName}");
            }

            if (name.Split('\\').Any(segment => segment == ".."))
            {
                throw new CabToolException($"柜内含越界条目（.. 段）：{entry.FileName}");
            }
        }
    }

    private static string Normalise(string name) => name.Replace('/', '\\');

    private static string CompressionName(string compression, byte option) => compression switch
    {
        "LZX" => $"LZX（窗口 {option}）",
        "MSZIP" => "MSZIP（DEFLATE）",
        "QUANTUM" => "Quantum（不支持）",
        "NONE" => "无压缩",
        _ => compression,
    };

    private static string Attributes(FileAttributes attributes)
    {
        List<string> names = [];
        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            names.Add("只读");
        }
        if (attributes.HasFlag(FileAttributes.Hidden))
        {
            names.Add("隐藏");
        }
        if (attributes.HasFlag(FileAttributes.System))
        {
            names.Add("系统");
        }
        if (attributes.HasFlag(FileAttributes.Archive))
        {
            names.Add("存档");
        }
        return names.Count == 0 ? "—" : string.Join("|", names);
    }

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Number(ulong value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>控制台上的显示宽度：CJK 与全角字符占两列。</summary>
    private static int DisplayWidth(string text)
    {
        int width = 0;
        foreach (char c in text)
        {
            width += c is >= '\u1100' and <= '\u115F'
                  or >= '\u2E80' and <= '\uA4CF'
                  or >= '\uAC00' and <= '\uD7A3'
                  or >= '\uF900' and <= '\uFAFF'
                  or >= '\uFE30' and <= '\uFE6F'
                  or >= '\uFF00' and <= '\uFF60'
                  or >= '\uFFE0' and <= '\uFFE6'
                ? 2 : 1;
        }
        return width;
    }

    private static string PadVisual(string text, int width)
    {
        int padding = width - DisplayWidth(text);
        return padding > 0 ? text + new string(' ', padding) : text;
    }

    private static string PadLeftVisual(string text, int width)
    {
        int padding = width - DisplayWidth(text);
        return padding > 0 ? new string(' ', padding) + text : text;
    }

    private static int UsageError(string usage)
    {
        Console.Error.WriteLine($"错误：参数不对。用法：{usage}");
        Console.Error.WriteLine("用 ocid_cab help <命令> 看选项与示例。");
        return 1;
    }
}

/// <summary>
/// 极简参数解析：位置参数、--flag、--key value（含 --key=value）。
/// 只支持本工具需要的形态，解析失败时把说明放进 Positional 供各命令报用法。
/// </summary>
internal sealed class ParsedArgs
{
    internal List<string> Positional { get; } = [];
    internal HashSet<string> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal static ParsedArgs Parse(string[] args, params string[] valueOptions)
    {
        ParsedArgs parsed = new();
        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                parsed.Positional.Add(argument);
                continue;
            }

            string name = argument[2..];
            int equals = name.IndexOf('=');
            if (equals >= 0)
            {
                parsed.Values[name[..equals]] = name[(equals + 1)..];
                continue;
            }

            if (valueOptions.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    throw new CabToolException($"选项 --{name} 缺参数。");
                }
                parsed.Values[name] = args[++i];
                continue;
            }

            parsed.Flags.Add(name);
        }
        return parsed;
    }
}
