using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// 驱动包处理：扫描 INF 目录，按官方 addonkit 范式（Add-IoTDriverPackage / IoTWMWriter 逐字段实证）
    /// 为每个含 INF 的目录生成 PkgGen 源包（Drivers.&lt;InfName&gt;\ 命名空间.名字.wm.xml + 全部伴随文件），
    /// 可选调用 ADK 的 PkgGen.exe 出 cab（DCM manifest / FileRepository 哈希等现代包内部结构由 PkgGen
    /// 官方算法生成，本程序不伪造——已实证 FileRepository 后缀哈希无法从 INF 内容反推）。
    /// 产出的包以 FeatureID（DRIVERS_X，addonkit 规则：OutputName 大写、点转下划线）挂入 FM 的 Features/OEM。
    /// </summary>
    public static class DriverPackageBuilder
    {
        public class DriverSourcePackage
        {
            public string SourceDir { get; set; } = "";      // 含 INF 的源目录
            public string OutputName { get; set; } = "";     // Drivers.<InfBaseName>
            public string Namespace { get; set; } = "Drivers";
            public string Name { get; set; } = "";           // OutputName 第二段
            public string FeatureId { get; set; } = "";      // DRIVERS_<INF>
            public List<string> InfFiles { get; } = new();   // 目录内全部 INF（文件名）
            public string PackageDir { get; set; } = "";     // 生成的源包目录
            public string WmXmlPath { get; set; } = "";
            public string CabPath { get; set; } = "";        // PkgGen 产物（空=未出包）
            public string CabName { get; set; } = "";
        }

        /// <summary>扫描 root 下全部 *.inf，按所在目录分组：一个目录=一个驱动源包（多 INF 合并进同一 wm.xml）。
        /// ns=包命名空间段（默认 Drivers，NXP 用 iMX）；featureGroup 非空=所有包共用该 FeatureID（NXP 范式），
        /// 空=每包 &lt;NS&gt;_&lt;INF&gt;（addonkit 范式）。</summary>
        public static List<DriverSourcePackage> Scan(string root, string ns = "Drivers", string featureGroup = "")
        {
            var list = new List<DriverSourcePackage>();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return list;
            if (string.IsNullOrWhiteSpace(ns)) ns = "Drivers";
            var groups = Directory.EnumerateFiles(root, "*.inf", SearchOption.AllDirectories)
                .GroupBy(Path.GetDirectoryName, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var g in groups)
            {
                string dir = g.Key!;
                var infs = g.Select(Path.GetFileName).Where(x => !string.IsNullOrEmpty(x))
                            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                if (infs.Count == 0) continue;
                // 官方默认包名取第一个 INF 的基名
                string infBase = Sanitize(Path.GetFileNameWithoutExtension(infs[0]!));
                if (infBase.Length == 0) infBase = "pkg";
                var p = new DriverSourcePackage
                {
                    SourceDir = dir,
                    Namespace = ns,
                    Name = infBase,
                    OutputName = ns + "." + infBase,
                };
                p.FeatureId = string.IsNullOrWhiteSpace(featureGroup)
                    ? MakeFeatureId(p.OutputName)
                    : MakeFeatureId(featureGroup);
                foreach (var i in infs) p.InfFiles.Add(i!);
                list.Add(p);
            }
            return list;
        }

        static string Sanitize(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_');
            return sb.ToString();
        }

        /// <summary>addonkit 规则：OutputName.ToUpper().Replace('.','_')，再清掉非法字符。</summary>
        static string MakeFeatureId(string outputName)
        {
            var sb = new StringBuilder();
            foreach (char c in outputName.ToUpperInvariant())
                sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            return sb.ToString();
        }

        /// <summary>
        /// 生成源包目录：复制 INF 目录全部文件（保留子目录结构）+ 写 wm.xml。
        /// wm.xml 结构逐字段抄 IoTWMWriter.Start/AddDriver/Finish。
        /// </summary>
        public static void BuildSourcePackage(DriverSourcePackage p, string packagesRoot, string partition)
        {
            string pkgDir = Path.Combine(packagesRoot, p.OutputName);
            DeviceLayoutBuilderService.Recreate(pkgDir);

            // 复制源目录全部文件（addonkit: Copy-Item $srcdir\* -Recurse -Exclude *.wm.xml）
            foreach (var f in Directory.GetFiles(p.SourceDir, "*", SearchOption.AllDirectories))
            {
                if (f.EndsWith(".wm.xml", StringComparison.OrdinalIgnoreCase)) continue;
                string rel = f.Substring(p.SourceDir.TrimEnd('\\', '/').Length + 1);
                string dst = Path.Combine(pkgDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(f, dst, true);
            }

            var sb = new StringBuilder();
            sb.Append($@"<?xml version=""1.0"" encoding=""utf-8""?>
<identity xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance""
    name=""{p.Name}""
    namespace=""{p.Namespace}""
    owner=""$(OEMNAME)""
    legacyName=""$(OEMNAME).{p.OutputName}"" xmlns=""urn:Microsoft.CompPlat/ManifestSchema.v1.00"">
    <onecorePackageInfo
        targetPartition=""{partition}""
        releaseType=""Production""
        ownerType=""OEM"" />
    <drivers>
");
            foreach (var inf in p.InfFiles)
                sb.AppendLine($"        <driver>\r\n            <inf source=\"{inf}\" />\r\n        </driver>");
            sb.Append(@"    </drivers>
</identity>");
            p.PackageDir = pkgDir;
            p.WmXmlPath = Path.Combine(pkgDir, p.OutputName + ".wm.xml");
            File.WriteAllText(p.WmXmlPath, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>
        /// 调 PkgGen.exe 出 cab（官方参数：pkggen wm.xml /output: /version: /build: /cpu: /variables: /onecore /universalbsp，
        /// 工作目录=包目录）。返回新生成的 cab 完整路径列表（目录快照差分，不猜命名）。
        /// </summary>
        public static List<string> RunPkgGen(string pkgGenExe, DriverSourcePackage p,
            string ownerName, string version, string arch, string buildType,
            string packagesRoot, string outCabDir, List<string> log)
        {
            Directory.CreateDirectory(outCabDir);
            var before = new HashSet<string>(Directory.GetFiles(outCabDir, "*.cab"), StringComparer.OrdinalIgnoreCase);

            string vars = $"_RELEASEDIR=.\\;PROD={ownerName};PRJDIR={packagesRoot};COMDIR={packagesRoot};" +
                          $"BSPVER={version};BSPARCH={arch};OEMNAME={ownerName};" +
                          $"BUILDTIME={DateTime.Now:yyyyMMddHHmm};BLDDIR={outCabDir}";
            string args = $"\"{p.WmXmlPath}\" /output:\"{outCabDir}\" /version:{version} /build:{buildType} " +
                          $"/cpu:{arch} /variables:\"{vars}\" /onecore /universalbsp";
            try
            {
                var psi = new ProcessStartInfo(pkgGenExe, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = p.PackageDir,
                };
                // PkgGen 内部调 makecat.exe 走 PATH（实证：缺之则 0x80070002 且不出 cab）。
                // 把 makecat 所在目录（PkgGen 同目录 + 本机 Kits bin）注入子进程 PATH。
                string extraPath = Path.GetDirectoryName(pkgGenExe) ?? "";
                string? mk = DeviceLayoutBuilderService.FindTool(null, "makecat.exe");
                if (mk != null) extraPath += ";" + Path.GetDirectoryName(mk);
                if (!string.IsNullOrEmpty(extraPath))
                    psi.EnvironmentVariables["PATH"] =
                        (Environment.GetEnvironmentVariable("PATH") ?? "") + ";" + extraPath;
                using var proc = Process.Start(psi)!;
                string outp = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
                if (!proc.WaitForExit(180_000))
                {
                    log.Add($"⚠ PkgGen 超时（{p.OutputName}）");
                    return new List<string>();
                }
                if (proc.ExitCode != 0)
                {
                    string hint = "";
                    if (outp.Contains("SignFiles") || outp.Contains("GenerateCatalogs") || outp.Contains("sign", StringComparison.OrdinalIgnoreCase))
                        hint = "（签名阶段失败：PkgGen 生成目录需测试证书，请先跑 addonkit 的 New-IoTOemCerts 装入证书存储，或取消勾选「直接出 cab」只生成源包）";
                    else if (outp.Contains("BuildUtils"))
                        hint = "（该版本 PkgGen 缺 BuildUtils.dll 依赖，建议改用本机 Windows Kits 的 PkgGen 或旧版 ADK）";
                    log.Add($"⚠ PkgGen 退出码 {proc.ExitCode}（{p.OutputName}）{hint}：{Trunc(outp)}");
                }
            }
            catch (Exception ex)
            {
                log.Add($"⚠ PkgGen 启动失败（{p.OutputName}）：{ex.Message}");
                return new List<string>();
            }
            var created = Directory.GetFiles(outCabDir, "*.cab")
                .Where(f => !before.Contains(f)).ToList();
            foreach (var c in created) log.Add("  PkgGen 产物: " + Path.GetFileName(c));
            return created;
        }

        static string Trunc(string s)
        {
            s = (s ?? "").Trim();
            return s.Length > 300 ? s[..300] + "…" : s;
        }
    }
}
