// ocid_cab —— OCID 的独立 CAB 控制台。
//
// 容器读取来自 MobilePackageGen/Cabinet（vendored 的 WPMT fork），压缩解码来自
// Source/Managed/OcidCabinet（MSZIP 的 DEFLATE 与 LZX，全部是项目自有实现）。这个
// 入口只为 CAB 服务：不引用 WPF 主程序、不依赖 expand.exe（container / sparse /
// 多卷包会如实拒绝并说明原因）。
//
// 约定与 ocid_manifest 一致：命令名保持英文，脚本可直接用；说明默认 zh-CN；
// 数据走 stdout，诊断走 stderr；退出码 0=成功、1=用法错误、2=柜文件/解包失败。

using System.Text;

namespace OcidCabinet.Tool;

internal static class Program
{
    internal const string Version = "1.0.0";

    private static int Main(string[] args)
    {
        // 中文输出在 Windows 控制台需要 UTF-8 代码页；重定向时同样是 UTF-8 字节。
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (IOException)
        {
            // 没有控制台（被管道接走）时保持默认编码即可。
        }

        if (args.Length == 0)
        {
            Help.PrintAll(Console.Error);
            return 1;
        }

        string command = args[0];
        string[] rest = args[1..];

        if (command is "help" or "-h" or "--help")
        {
            return Help.Run(rest);
        }

        if (command is "--version" or "version")
        {
            return Commands.PrintVersion();
        }

        if (!Help.HasCommand(command))
        {
            Console.Error.WriteLine($"错误：没有命令“{command}”。");
            Console.Error.WriteLine("用法：ocid_cab help 查看全部命令。");
            return 1;
        }

        try
        {
            return command.ToLowerInvariant() switch
            {
                "info" => Commands.Info(rest),
                "list" or "ls" => Commands.List(rest),
                "extract" or "x" => Commands.Extract(rest),
                "cat" => Commands.Cat(rest),
                "verify" => Commands.Verify(rest),
                _ => Unreachable(command),
            };
        }
        catch (CabToolException ex)
        {
            Console.Error.WriteLine($"错误：{ex.Message}");
            return 2;
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"错误：柜文件损坏或格式不受支持 —— {ex.Message}");
            return 2;
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine($"错误：找不到文件 —— {ex.FileName ?? ex.Message}");
            return 2;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"错误：读写失败 —— {ex.Message}");
            return 2;
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"错误：访问被拒绝 —— {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            // 读取器对坏容器抛普通 Exception（"Bad Cabinet: ..."）；这里统一成一行诊断，
            // 不吞掉消息，也不假装成功。
            Console.Error.WriteLine($"错误：{ex.Message}");
            return 2;
        }
    }

    private static int Unreachable(string command)
    {
        Console.Error.WriteLine($"错误：命令“{command}”没有实现。");
        return 1;
    }
}
