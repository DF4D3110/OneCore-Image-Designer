using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Win32;

namespace OneCore_Image_Designer.Services
{
    /// <summary>addonkit 运行前检查（preflight）：在提交提权命令之前，本地核验命令成功的先决条件。</summary>
    /// <param name="IsWarning">true=提示/警告（不拦截），false=错误（拦截命令）。</param>
    public sealed record PreflightItem(bool IsWarning, string Message)
    {
        public string Glyph => IsWarning ? "⚠" : "✗";
    }

    public sealed class PreflightReport
    {
        public List<PreflightItem> Items { get; } = new();
        public bool HasErrors => Items.Any(i => !i.IsWarning);

        public string Summarize()
        {
            if (Items.Count == 0) return "检查通过：未发现异常项。";
            var ok = HasErrors ? "存在阻塞问题：" : "检查完成（仅提示）：";
            return ok + Environment.NewLine +
                   string.Join(Environment.NewLine, Items.Select(i => "  " + i.Glyph + " " + i.Message));
        }
    }

    /// <summary>
    /// 与 addonkit 官方 Open-IoTWorkspace 的启动校验对齐的检查项：
    /// 模块存在、工作区 XML 可解析、IoT Core Kits 注册表（须 17763）、
    /// MSPackages 包目录可达（MSPkgRoot 或 %KITSROOT%）、对应 build 的 ADK 工具已安装、
    /// Retail 配置需签名参数。
    /// </summary>
    public static class AddonKitPreflightService
    {
        public static PreflightReport Check(string workspaceXml, string buildNumberText, string configuration)
        {
            var r = new PreflightReport();

            // 1. 内置模块
            if (!AddonKitShellService.IsModuleAvailable)
                r.Items.Add(new PreflightItem(false,
                    "内置 IoTCoreImaging 模块缺失：" + AddonKitShellService.ModuleManifest));

            // 2. 工作区 XML + 架构 / 包根 / 签名参数
            string arch = "arm";
            string? mspkgRoot = null;
            string? retailSignParam = null;
            if (string.IsNullOrWhiteSpace(workspaceXml) || !File.Exists(workspaceXml))
            {
                r.Items.Add(new PreflightItem(false, "工作区 XML 不存在：" + workspaceXml));
            }
            else
            {
                try
                {
                    var doc = XDocument.Load(workspaceXml);
                    var cfg = doc.Root?.Element("Config");
                    arch = (string?)doc.Root?.Element("CurrentEnv") ?? "arm";
                    if (string.IsNullOrWhiteSpace(arch)) arch = "arm";
                    mspkgRoot = (string?)cfg?.Element("MSPkgRoot");
                    retailSignParam = (string?)cfg?.Element("RetailSignToolParam");
                }
                catch (Exception ex)
                {
                    r.Items.Add(new PreflightItem(false, "工作区 XML 解析失败：" + ex.Message));
                }
            }

            // 3. IoT Core Kits 注册表（官方模块同样要求 1809/17763）
            var kitVer = ReadIoTKitsVersion(arch);
            if (kitVer == null)
                r.Items.Add(new PreflightItem(false,
                    $"未检测到 Windows 10 IoT Core {arch} Kits（需 1809 / 10.0.17763.x）。" +
                    "安装页：https://www.microsoft.com/en-us/software-download/windows10iotcore"));
            else if (!kitVer.Contains("17763", StringComparison.Ordinal))
                r.Items.Add(new PreflightItem(false,
                    $"本机 IoT Core {arch} Kits 版本为 {kitVer}，addonkit 17763-v7 仅支持 10.0.17763.x。"));

            // 4. OS 包根：工作区 MSPkgRoot，留空则官方回退 %KITSROOT%
            var effectiveRoot = !string.IsNullOrWhiteSpace(mspkgRoot) ? mspkgRoot!.Trim() : Environment.GetEnvironmentVariable("KITSROOT");
            if (string.IsNullOrWhiteSpace(effectiveRoot))
            {
                r.Items.Add(new PreflightItem(false,
                    "未指定 OS 包根：工作区 MSPkgRoot 为空且环境变量 KITSROOT 未设置。" +
                    "请在 IoTWorkspace.xml 配置 MSPkgRoot，或设置 KITSROOT 指向 IoT Core Kits 根目录。"));
            }
            else
            {
                var pkgDir = Path.Combine(effectiveRoot, "MSPackages", "Retail", arch, "fre");
                if (!Directory.Exists(pkgDir))
                    r.Items.Add(new PreflightItem(false,
                        $"OS 包目录不存在：{pkgDir}（请检查包根路径与当前环境架构 {arch}）"));
            }

            // 5. ADK 工具（addonkit\adk\<build>，命令内部调用 imageapp/PkgGen）
            if (!int.TryParse(buildNumberText?.Trim(), out var build))
            {
                r.Items.Add(new PreflightItem(false, $"ADK build 号无效：\"{buildNumberText}\""));
            }
            else
            {
                var imageapp = AdkToolService.FindToolPath(build, "imageapp.exe");
                if (imageapp == null)
                    r.Items.Add(new PreflightItem(false,
                        $"ADK {build} 未安装或不含 imageapp.exe。请先在「ADK 工具下载」中下载并安装 build {build}。"));
            }

            // 6. Retail 配置需工作区签名参数
            if (string.Equals(configuration, "Retail", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(retailSignParam))
            {
                r.Items.Add(new PreflightItem(false,
                    "Retail 配置需要签名：IoTWorkspace.xml 的 RetailSignToolParam 为空。" +
                    "请先用 New-IoTOEMCerts / Import-IoTOEMCertificate 配好 OEM 证书。"));
            }

            // 全通过时给一条确认项，界面不至于空白
            if (r.Items.Count == 0)
                r.Items.Add(new PreflightItem(true, "✓ 模块、工作区、Kits、OS 包根、ADK 工具" +
                    (string.Equals(configuration, "Retail", StringComparison.OrdinalIgnoreCase) ? "、Retail 签名参数" : "") +
                    " 均就绪"));
            return r;
        }

        /// <summary>读 IoT Core Kits 注册表版本（HKCR\Installer\Dependencies，官方模块同款键）。64/32 位视图都试。</summary>
        private static string? ReadIoTKitsVersion(string arch)
        {
            var subKey = $"Installer\\Dependencies\\Microsoft.Windows.Windows_10_IoT_Core_{arch}_Packages.x86.10";
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
                    using var key = root.OpenSubKey(subKey);
                    if (key?.GetValue("Version") is string ver && !string.IsNullOrWhiteSpace(ver))
                        return ver.Replace("10.1.", "10.0.", StringComparison.Ordinal);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return null;
        }
    }
}
