// OCID Imaging —— AppX 落地的小帮手（32 位 .NET，托管 ADK 的 appximaging.dll）。
//
// 【为什么必须有这个 exe】
//   AppX 的解包/注册逻辑在 ADK 的 appximaging.dll / appxcommon.dll 里，而它们是
//   **i386 的 .NET 程序集**（……\Windows Kits\10\tools\bin\i386\）。ocid_imagegen.exe 是 x64，
//   同一个进程里装不进 i386 的程序集，所以只能起一个 32 位子进程来跑这一段。
//
// 【它做什么】
//   stage  : 把 .appx/.appxbundle 用 MakeAppx unpack/unbundle 解到 staging，按架构挑包，
//            再移动成 <staging>\appx\<PackageFullName>
//   commit : 把 <staging>\appx\* 搬进 <PreInstalled>\WindowsApps，做 WOF 压缩，
//            并写 <PreInstalled>\WindowsApps\AppxAllUserStore.dat（Applications/Staged/Config），
//            同时把 license 复制到 <PreInstalled>\AppData\<ID>.xml
//   这两步完全复用 ADK 的实现，行为与 imageapp 的 AppXCommon/AppXImaging 一致。
//
// 【参数】
//   OcidAppX stage  --list <tsv> --staging <dir> [--arch ARM64] [--languages "en-US;zh-CN"] [--log <file>]
//   OcidAppX commit --list <tsv> --staging <dir> --preinstalled <volume root> [--arch ...] [--languages ...] [--log <file>]
//   --list 是 TSV，每行：ID \t PackagePath \t Name \t LicensePath \t Optional(0|1) \t CpuType \t PreferStub(0|1) \t Region
//   （空字段留空。OcidImaging 的 appx 模块负责从 FM/OEMInput 里把这个表算出来。）
//
// 【约定】退出码 0 成功、1 失败、2 参数错误；stdout 就是日志（C++ 侧原样转发）。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Composition.ToolBox;
using Microsoft.ImagingTools.AppX;
using Microsoft.WindowsPhone.FeatureAPI.AppX;
using Microsoft.WindowsPhone.ImageUpdate.Tools.Common;

internal static class OcidAppX
{
    private static readonly string[] RequiredFiles =
    {
        "makeappx.exe",
        "appximaging.dll",
        "appxcommon.dll",
        "appxpackaging.dll",
        "featureapi.dll",
        "imagecommon.dll",
        "toolscommon.dll",
        "pkgtoolbox.dll",
        "ImgToolsCommon.dll",
    };

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("OcidAppX: 失败：" + ex);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length < 2) return Usage();
        string mode = args[0].ToLowerInvariant();
        string listPath = null;
        string stagingDir = null;
        string preInstalled = null;
        string archName = "ARM64";
        string languagesArg = null;
        string logFile = null;

        for (int i = 1; i < args.Length; ++i)
        {
            string a = args[i];
            if (a == "--list" && i + 1 < args.Length) listPath = args[++i];
            else if (a == "--staging" && i + 1 < args.Length) stagingDir = args[++i];
            else if (a == "--preinstalled" && i + 1 < args.Length) preInstalled = args[++i];
            else if (a == "--arch" && i + 1 < args.Length) archName = args[++i];
            else if (a == "--languages" && i + 1 < args.Length) languagesArg = args[++i];
            else if (a == "--log" && i + 1 < args.Length) logFile = args[++i];
            else { Console.Error.WriteLine("OcidAppX: 未知参数 " + a); return Usage(); }
        }
        if (mode != "stage" && mode != "commit") return Usage();
        if (string.IsNullOrEmpty(listPath)) { Console.Error.WriteLine("OcidAppX: 缺少 --list"); return 2; }
        if (string.IsNullOrEmpty(stagingDir)) { Console.Error.WriteLine("OcidAppX: 缺少 --staging"); return 2; }
        if (mode == "commit" && string.IsNullOrEmpty(preInstalled))
        {
            Console.Error.WriteLine("OcidAppX: commit 缺少 --preinstalled");
            return 2;
        }

        // ADK 的托管依赖都在 exe 同目录；native 的 appxpackaging.dll 靠 SetDllDirectory 找。
        string baseDir = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
        SetDllDirectory(baseDir);
        // 兜底：万一上游把 \\?\Volume{…} 直接传进来，.NET 默认会以"路径中具有非法字符"拒绝，
        // 打开长路径支持后 File/StreamReader 才能处理。
        try
        {
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
        }
        catch (Exception) { }
        foreach (string name in RequiredFiles)
        {
            if (!File.Exists(Path.Combine(baseDir, name)))
            {
                Console.Error.WriteLine("OcidAppX: 同目录缺少 " + name + "（应把 ADK i386 的这几个文件放到 exe 旁）");
                return 1;
            }
        }

        TextWriter tee = null;
        TextWriter origOut = Console.Out;
        if (!string.IsNullOrEmpty(logFile))
        {
            StreamWriter logWriter = new StreamWriter(new FileStream(logFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite));
            logWriter.AutoFlush = true;
            tee = new TeeWriter(origOut, logWriter);
            Console.SetOut(tee);
        }

        try
        {
            CpuArch arch = ParseArch(archName);
            List<string> languages = ParseLanguages(languagesArg);
            if (languages.Count == 0)
            {
                // AppxInfoManager 的 ctor 会对语言串做 ToString(1, len-1)，传空列表直接抛
                // ArgumentOutOfRangeException（ADK 的既有缺陷），所以这里兜一个默认值。
                Console.Error.WriteLine("OcidAppX: 警告：没有传 --languages，回落为 en-US（ADK 的 AppxInfoManager 不接受空语言表）");
                languages.Add("en-US");
            }
            List<AppXFMPkgInfo> packages = LoadList(listPath);

            IULogger logger = new IULogger();
            logger.LogInfo("OcidAppX: {0} 开始，{1} 个包，架构 {2}，语言 {3}", mode, packages.Count, arch,
                           languages.Count == 0 ? "(无)" : string.Join(";", languages.ToArray()));
            AppXImaging imaging = new AppXImaging();
            if (mode == "stage")
            {
                imaging.StageAppXPackages(logger, packages, stagingDir, arch, languages);
                Console.Out.WriteLine("OcidAppX: stage 完成");
            }
            else
            {
                imaging.CommitAppXPackages(logger, packages, stagingDir, preInstalled, arch, languages);
                Console.Out.WriteLine("OcidAppX: commit 完成");
            }
            return 0;
        }
        finally
        {
            Console.Out.Flush();
            if (tee != null)
            {
                Console.SetOut(origOut);
                tee.Dispose();
            }
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("用法：OcidAppX stage|commit --list <tsv> --staging <dir> [--preinstalled <root>] [--arch ARM64] [--languages \"en-US;zh-CN\"] [--log <file>]");
        return 2;
    }

    private static CpuArch ParseArch(string name)
    {
        CpuArch arch;
        if (!Enum.TryParse(name, true, out arch) || arch == CpuArch.Invalid)
        {
            throw new ArgumentException("未知架构：" + name);
        }
        return arch;
    }

    private static List<string> ParseLanguages(string text)
    {
        List<string> result = new List<string>();
        if (string.IsNullOrEmpty(text)) return result;
        foreach (string part in text.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = part.Trim();
            if (trimmed.Length > 0) result.Add(trimmed);
        }
        return result;
    }

    // TSV：ID \t PackagePath \t Name \t LicensePath \t Optional \t CpuType \t PreferStub \t Region
    private static List<AppXFMPkgInfo> LoadList(string listPath)
    {
        List<AppXFMPkgInfo> result = new List<AppXFMPkgInfo>();
        string[] lines = File.ReadAllLines(listPath, Encoding.UTF8);
        for (int i = 0; i < lines.Length; ++i)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;
            string[] parts = line.Split('\t');
            AppXPkgFile file = new AppXPkgFile();
            file.ID = Field(parts, 0);
            string packagePath = Field(parts, 1);
            if (string.IsNullOrEmpty(packagePath))
            {
                throw new ArgumentException("第 " + (i + 1) + " 行缺少 PackagePath");
            }
            file.Name = Path.GetFileName(packagePath);
            file.Directory = Path.GetDirectoryName(packagePath);
            string licensePath = Field(parts, 3);
            file.LicenseFile = string.IsNullOrEmpty(licensePath) ? null : Path.GetFileName(licensePath);
            // AppXImaging 的 commit 阶段是拿包族名（PFN，如 Microsoft.WindowsAlarms_8wekyb3d8bbwe）
            // 跟 AppXFMPkgInfo.ID 做**字符串相等**比较的，所以 ID 必须是完整 PFN。
            // FM 里的 ID 本来就是 PFN，优先用它；只有 FM 没写 ID 时才退回 license 文件名，
            // 且要剥掉 ".arm/.x64/.arm64" 这类架构后缀（例：Microsoft.HEIFImageExtension_8wekyb3d8bbwe.arm.xml）。
            if (string.IsNullOrEmpty(file.ID) && !string.IsNullOrEmpty(licensePath))
            {
                file.ID = StripArchSuffix(Path.GetFileNameWithoutExtension(licensePath));
            }
            file.Optional = Field(parts, 4) == "1";
            file.CPUType = NullIfEmpty(Field(parts, 5));
            file.PreferStub = Field(parts, 6) == "1";
            file.Region = NullIfEmpty(Field(parts, 7));

            AppXFMPkgInfo info = new AppXFMPkgInfo(file, PhoneOwnerType.Microsoft, file.CPUType);
            info.PackagePath = packagePath;                  // 覆盖：不做 $(...) 变量展开（C++ 侧已经算好绝对路径）
            if (!string.IsNullOrEmpty(licensePath)) info.LicenseFilePath = licensePath;
            result.Add(info);
        }
        return result;
    }

    private static string Field(string[] parts, int index)
    {
        return index < parts.Length ? (parts[index] ?? string.Empty).Trim() : string.Empty;
    }

    // 把 "..._8wekyb3d8bbwe.arm" / ".x64" / ".arm64" / ".x86" 之类的架构后缀去掉。
    private static string StripArchSuffix(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        string[] suffixes = { ".arm64", ".arm", ".x64", ".x86" };
        foreach (string suffix in suffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return name.Substring(0, name.Length - suffix.Length);
            }
        }
        return name;
    }

    private static string NullIfEmpty(string value)
    {
        return string.IsNullOrEmpty(value) ? null : value;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string path);

    // 把 stdout 同时写给控制台和日志文件（C++ 侧捕获 stdout，日志文件留档排查）。
    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _first;
        private readonly TextWriter _second;

        public TeeWriter(TextWriter first, TextWriter second)
        {
            _first = first;
            _second = second;
        }

        public override Encoding Encoding
        {
            get { return _first.Encoding; }
        }

        public override void Write(char value)
        {
            _first.Write(value);
            _second.Write(value);
        }

        public override void Write(string value)
        {
            _first.Write(value);
            _second.Write(value);
        }

        public override void WriteLine(string value)
        {
            _first.WriteLine(value);
            _second.WriteLine(value);
        }

        public override void Flush()
        {
            _first.Flush();
            _second.Flush();
        }
    }
}
