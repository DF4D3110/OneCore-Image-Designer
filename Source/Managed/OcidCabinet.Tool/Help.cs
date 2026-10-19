// 命令表与帮助文本。与 ocid_manifest 的 help 结构保持一致：不带参数列全部命令
// （按分组），带命令名给用途、用法、选项与示例。这是命令说明唯一的人读来源：
// 新增命令时在这里补一条，并在 Program.cs 的分发里接上。

namespace OcidCabinet.Tool;

internal sealed record CommandHelp(string Name, string Group, string Summary, string Usage, string Notes);

internal static class Help
{
    private static readonly CommandHelp[] Commands =
    [
        new("info", "查看",
            "柜头、文件夹压缩与块布局、总大小；container / sparse 包会给出提醒。",
            "ocid_cab info <cab>",
            "例：ocid_cab info E:\\windesktop\\8400_x86_repair_content\\kb777777-x86-en-us-merged_*.cab"),

        new("list", "查看",
            "列出条目：名称、未压缩大小、修改时间、属性。",
            "ocid_cab list <cab> [--long]",
            "--long：额外输出每个条目所在的 folder 序号与压缩算法。\n" +
            "例：ocid_cab list a.cab --long"),

        new("extract", "解包",
            "把柜里全部条目解包到目录（支持 MSZIP 与 LZX）。",
            "ocid_cab extract <cab> <目录> [--force] [--verbose]",
            "--force：目标目录非空时仍继续（同名文件会被覆盖）。\n" +
            "--verbose：逐条目输出进度到 stderr。\n" +
            "container CAB（含 _manifest_.cix.xml）默认拒绝：条目是数字 id 与 PA30 delta，\n" +
            "真实文件名只有 expand.exe 会按清单还原；确需原样取载荷时加 --force。\n" +
            "sparse CAB（iFolder 0xFFFD..0xFFFF）不支持，请用 expand.exe。\n" +
            "例：ocid_cab extract a.cab out --verbose"),

        new("cat", "解包",
            "把单个条目写到 stdout 或文件（适合抽 _manifest_.cix.xml 之类的小文件）。",
            "ocid_cab cat <cab> <条目> [--output <文件>]",
            "条目名大小写不敏感，/ 与 \\ 等价。不给 --output 时是二进制 stdout，诊断在 stderr。\n" +
            "例：ocid_cab cat a.cab _manifest_.cix.xml --output cix.xml"),

        new("verify", "解包",
            "在临时目录把整柜解一遍并算每文件 SHA-256，确认全部可解码后清理。",
            "ocid_cab verify <cab>",
            "用来做回归：任何解码错误都会让命令以非零码失败，并打印出错条目。\n" +
            "例：ocid_cab verify a.cab"),

        new("help", "工具",
            "命令说明：不带参数列全部命令，带命令名看细节。",
            "ocid_cab help [命令]",
            "例：ocid_cab help extract"),
    ];

    internal static bool HasCommand(string name) => Find(name) is not null;

    internal static CommandHelp? Find(string name)
    {
        foreach (CommandHelp command in Commands)
        {
            if (string.Equals(command.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return command;
            }
        }
        return null;
    }

    internal static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            PrintAll(Console.Out);
            return 0;
        }

        CommandHelp? command = Find(args[0]);
        if (command is null)
        {
            Console.Error.WriteLine($"错误：没有命令“{args[0]}”。用 ocid_cab help 查看全部命令。");
            return 1;
        }

        PrintOne(command, Console.Out);
        return 0;
    }

    internal static void PrintAll(TextWriter writer)
    {
        writer.WriteLine("ocid_cab —— CAB 控制台（自带 MSZIP / LZX 解码）");
        writer.WriteLine();
        writer.WriteLine("用法：ocid_cab <命令> [参数...]");
        writer.WriteLine("      ocid_cab help            列出全部命令");
        writer.WriteLine("      ocid_cab help <命令>     看某个命令的用法、选项与示例");
        writer.WriteLine();

        string[] groups = ["查看", "解包", "工具"];
        foreach (string group in groups)
        {
            bool first = true;
            foreach (CommandHelp command in Commands)
            {
                if (command.Group != group)
                {
                    continue;
                }
                if (first)
                {
                    writer.WriteLine($"【{group}】");
                    first = false;
                }
                writer.WriteLine($"  {command.Name,-8} {command.Summary}");
            }
            if (!first)
            {
                writer.WriteLine();
            }
        }

        writer.WriteLine("退出码：0 成功；1 用法错误；2 柜文件或解包失败。");
        writer.WriteLine("数据走 stdout，诊断走 stderr。");
    }

    private static void PrintOne(CommandHelp command, TextWriter writer)
    {
        writer.WriteLine($"命令：{command.Name}（{command.Group}）");
        writer.WriteLine();
        writer.WriteLine("用途：");
        writer.WriteLine($"  {command.Summary}");
        writer.WriteLine();
        writer.WriteLine("用法：");
        writer.WriteLine($"  {command.Usage}");
        if (!string.IsNullOrEmpty(command.Notes))
        {
            writer.WriteLine();
            writer.WriteLine("选项与说明：");
            foreach (string line in command.Notes.Split('\n'))
            {
                writer.WriteLine($"  {line}");
            }
        }
    }
}
