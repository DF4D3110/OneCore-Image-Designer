using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// BSP 组件生成请求：OEMDevicePlatform 包、DeviceInfo 注册表包、machineinfo.xml、
    /// SMBIOS 源包（IoT）、各自 FIP 桩包，以及整合全部组件的统一 FM。
    /// 所有模板逐字段抄自真实样本（QEMU ARM64 BSP / 15254 QC8960 / 官方 QCDB410C 的样本集），
    /// keyform 哈希与打包复用 DeviceLayoutBuilderService 的 internal 工具链。
    /// </summary>
    public class BspComponentRequest
    {
        // ── 公共 ──
        public string OutputDirectory { get; set; } = string.Empty;
        public string Architecture { get; set; } = "arm64";
        public string Version { get; set; } = "10.0.20279.1002";
        public string OwnerName { get; set; } = "QEMU";        // phoneOwner/FM OwnerName
        public string Fmid { get; set; } = string.Empty;       // FM 根 ID（QEMUGDEV）
        public string DeviceName { get; set; } = string.Empty; // QEMUArm64 → capability DEVICE_QEMUARM64
        public string Partition { get; set; } = "MainOS";
        public bool Modern { get; set; } = true;               // false=Phone/IoT 早期形态
        public string PublicKeyToken { get; set; } = "628844477771337a"; // 组件包默认 6288（样本实证）

        // 打包工具（留空=自动搜索）
        public string CabarcPath { get; set; } = string.Empty;
        public string MakecatPath { get; set; } = string.Empty;
        public string SignToolPath { get; set; } = string.Empty;
        public string? SigningPfx { get; set; }
        public string? SigningPfxPassword { get; set; }

        // ── 布局包联动（生成统一 FM 时需要；来自布局打包结果） ──
        public string LayoutComponentName { get; set; } = string.Empty; // QEMU-QEMUArm64DeviceLayout4096
        public string LayoutPackageName { get; set; } = string.Empty;   // ...-Package
        public string LayoutFipName { get; set; } = string.Empty;       // ...FIP
        public string LayoutSoc { get; set; } = string.Empty;

        // ── 组件开关 ──
        public bool BuildDevicePlatform { get; set; }
        public bool BuildDeviceInfo { get; set; }
        public bool BuildMachineInfo { get; set; }
        public bool BuildSmbios { get; set; }
        /// <summary>驱动处理：扫描该目录下全部 *.inf（按目录分组），为每组生成 Drivers.&lt;InfName&gt; 源包。</summary>
        public string DriverScanRoot { get; set; } = string.Empty;
        /// <summary>源包生成后是否调 PkgGen.exe 直接出 cab（false=只出 wm.xml 源包，交 addonkit 构建）。</summary>
        public bool DriverRunPkgGen { get; set; }
        /// <summary>PkgGen.exe 路径（空=从已安装 ADK 自动发现）。</summary>
        public string PkgGenPath { get; set; } = string.Empty;
        public string BuildType { get; set; } = "fre";
        /// <summary>驱动包命名空间段（NXP 用 iMX、addonkit 默认 Drivers）；包名=&lt;ns&gt;.&lt;InfName&gt;。</summary>
        public string DriverNamespace { get; set; } = "Drivers";
        /// <summary>FeatureID 分组：非空则所有驱动包共用该 ID（NXP 范式 IMX_DRIVERS）；空=每包一个 &lt;NS&gt;_&lt;INF&gt;（addonkit 范式）。</summary>
        public string DriverFeatureGroup { get; set; } = string.Empty;

        // OEMDevicePlatform 包
        public string DevicePlatformPackageName { get; set; } = string.Empty; // 空=自动推导
        public long MinSectorCount { get; set; } = 20971520;
        public List<string> DevicePlatformIDs { get; set; } = new() { "*" };
        public string BiosMode { get; set; } = "UEFI";                        // 空=不写（Phone 老样本无此节点）
        public long AdditionalMainOSFreeSectorsRequest { get; set; } = 0;     // 仅 Phone 形态写
        public long MainOSRTCDataReservedSectors { get; set; } = 0;           // 仅 Phone 形态写

        // DeviceInfo 注册表包
        public string DeviceInfoPackageName { get; set; } = string.Empty;     // 空=自动推导
        public string SocVersion { get; set; } = "arm64";
        public string HardwareVersion { get; set; } = "0.0.0.1";
        public string BspVersion { get; set; } = "20279.1002";
        public string DeviceManufacturer { get; set; } = "QEMU";
        public string DeviceModel { get; set; } = "virt arm64 Device";
        public string OemSoftwareVersion { get; set; } = "20279.1002";

        // machineinfo.xml（17666+ 才有此机制）
        public string MachineInfoManufacturer { get; set; } = "QEMU";
        public string MachineInfoFamily { get; set; } = "virt";
        public string MachineInfoProductName { get; set; } = "virt-arm64";
        public string MachineInfoSku { get; set; } = "1";

        // SMBIOS（IoT 源包：Custom.SMBIOS.wm.xml + SMBIOS.CFG，由 PkgGen 出包）
        public string SmbiosOemName { get; set; } = "";      // {OEMNAME}
        public string SmbiosProduct { get; set; } = "";      // {PRODUCT}
        public string SmbiosSku { get; set; } = "";          // {SKU}
        public string SmbiosFamily { get; set; } = "";       // {Family}
        public string SmbiosBbProd { get; set; } = "";       // {BbProd}
    }

    public class BspComponentResult
    {
        public bool Success { get; set; }
        public string DevicePlatformCabPath { get; set; } = string.Empty;
        public string DevicePlatformFipCabPath { get; set; } = string.Empty;
        public string DeviceInfoCabPath { get; set; } = string.Empty;
        public string DeviceInfoFipCabPath { get; set; } = string.Empty;
        public string MachineInfoPath { get; set; } = string.Empty;
        public string SmbiosSourceDir { get; set; } = string.Empty;
        public List<DriverPackageBuilder.DriverSourcePackage> DriverPackages { get; } = new();
        public string DriverCabDir { get; set; } = string.Empty;
        public string FmPath { get; set; } = string.Empty;
        public List<string> Log { get; } = new();
        public string? Error { get; set; }
    }

    /// <summary>
    /// BSP 组件生成服务。设备平台包/注册表包形态（现代 vs Phone 单包）按时代分支；
    /// machineinfo 带 17666 时代界限；SMBIOS 输出 addonkit/PkgGen 可消费的源包目录。
    /// </summary>
    public static class BspComponentBuilderService
    {
        const string TokenTest = "628844477771337a";

        /// <summary>machineinfo.xml 机制的最低 build（20348 时代 OEMInput 才有 MachineInfoFile 字段，17666 起类里出现）。</summary>
        public const int MachineInfoFirstBuild = 17666;

        public static int BuildOfVersion(string version)
        {
            var m = System.Text.RegularExpressions.Regex.Match(version ?? "", @"\d+\.\d+\.(\d+)");
            return m.Success && int.TryParse(m.Groups[1].Value, out int b) ? b : 0;
        }

        public static BspComponentResult BuildAll(BspComponentRequest req)
        {
            var res = new BspComponentResult();
            try
            {
                Validate(req);
                bool wantDrivers = !string.IsNullOrWhiteSpace(req.DriverScanRoot);
                bool any = req.BuildDevicePlatform || req.BuildDeviceInfo || req.BuildMachineInfo
                           || req.BuildSmbios || wantDrivers;
                if (!any) { res.Success = true; res.Log.Add("未选择任何组件。"); return res; }

                if (req.BuildDevicePlatform) BuildDevicePlatform(req, res);
                if (req.BuildDeviceInfo) BuildDeviceInfo(req, res);
                if (req.BuildMachineInfo)
                {
                    int b = BuildOfVersion(req.Version);
                    if (b > 0 && b < MachineInfoFirstBuild)
                        res.Log.Add($"⚠ machineinfo.xml 是 {MachineInfoFirstBuild}+ 的机制（目标 build {b} 的 imageapp 不读它），仍已生成供参考");
                    WriteMachineInfo(req, res);
                }
                if (req.BuildSmbios) BuildSmbios(req, res);
                if (wantDrivers) BuildDriverPackages(req, res);

                WriteUnifiedFm(req, res);
                res.Success = true;
                return res;
            }
            catch (Exception ex)
            {
                res.Error = ex.Message;
                res.Log.Add("错误: " + ex.Message);
                return res;
            }
        }

        static void Validate(BspComponentRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.OutputDirectory)) throw new InvalidOperationException("输出目录未选择");
            bool wantDrivers = !string.IsNullOrWhiteSpace(req.DriverScanRoot);
            bool onlyDrivers = wantDrivers && !req.BuildDevicePlatform && !req.BuildDeviceInfo
                               && !req.BuildMachineInfo && !req.BuildSmbios;
            if (!onlyDrivers)
            {
                if (string.IsNullOrWhiteSpace(req.DeviceName)) throw new InvalidOperationException("设备名（Device）不能为空");
                if (!DeviceLayoutBuilderService.ValidateIdentifier(req.DeviceName))
                    throw new InvalidOperationException("设备名只能包含字母数字与 . _ -");
            }
            if (string.IsNullOrWhiteSpace(req.Fmid))
                req.Fmid = DefaultFmid(req.OwnerName, string.IsNullOrWhiteSpace(req.DeviceName) ? "DRV" : req.DeviceName);
        }

        static string DefaultFmid(string owner, string device)
            => (owner + device.ToUpperInvariant() + "DEV").Replace(".", "").Replace("-", "").Replace("_", "");

        static string OwnerOf(string pkg) => pkg.Split('-', '.')[0];
        static string FipFamily(string owner) =>
            owner.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) ? "FactoryOS" : owner + ".FactoryOS";

        // ════════════════ OEMDevicePlatform 包（现代：独立包 + FIP） ════════════════

        static void BuildDevicePlatform(BspComponentRequest req, BspComponentResult res)
        {
            string arch = req.Architecture, ver = req.Version, token = req.PublicKeyToken;
            string pkg = !string.IsNullOrWhiteSpace(req.DevicePlatformPackageName)
                ? req.DevicePlatformPackageName.Trim()
                : $"{OwnerOf(req.LayoutPackageName.Length > 0 ? req.LayoutPackageName : req.DeviceName + "-Package")}-{req.DeviceName}-DevicePlatform-Package";
            string component = DeviceLayoutBuilderService.StripPackageSuffix(pkg);
            string deploy = component + "-Deployment";
            string owner = OwnerOf(pkg);

            string staging = Path.Combine(req.OutputDirectory, pkg + $"~{token}~{arch}~~");
            DeviceLayoutBuilderService.Recreate(staging);

            string mainFolder = arch + "_" + DeviceLayoutBuilderService.ComponentTail(component, token, ver, arch);
            string mainManifest = mainFolder + ".manifest";
            string deployManifest = arch + "_" + DeviceLayoutBuilderService.ComponentTail(deploy, token, ver, arch) + ".manifest";
            string compDir = Path.Combine(staging, mainFolder);
            Directory.CreateDirectory(compDir);

            string xmlPath = Path.Combine(compDir, "OEMDevicePlatform.xml");
            File.WriteAllText(xmlPath, ModernOemDevicePlatformXml(req), new UTF8Encoding(false));
            res.Log.Add("1 OEMDevicePlatform.xml 已生成");

            string b64 = DeviceLayoutBuilderService.Sha256Base64(xmlPath);
            DeviceLayoutBuilderService.WriteXml(Path.Combine(staging, mainManifest),
                FileMainManifest(component, token, req, "OEMDevicePlatform.xml", b64,
                    "$(runtime.windows)\\ImageUpdate\\"));
            DeviceLayoutBuilderService.WriteXml(Path.Combine(staging, deployManifest),
                SimpleDeploymentManifest(deploy, component, token, req));
            res.Log.Add("2 主/部署 manifest 已生成");

            string updateName = DeviceLayoutBuilderService.Dotted32(pkg);
            DeviceLayoutBuilderService.WriteXml(Path.Combine(staging, "update.mum"),
                ComponentMum(pkg, component, deploy, mainManifest, deployManifest,
                    "OEMDevicePlatform.xml", Path.Combine(mainFolder, "OEMDevicePlatform.xml"), updateName, req, owner, token));
            res.Log.Add("3 update.mum 已生成（Feature Pack）");

            string cdf = Path.Combine(req.OutputDirectory, pkg + ".cdf");
            File.WriteAllText(cdf, DeviceLayoutBuilderService.CdfText(staging, pkg, "update.mum", mainManifest, deployManifest,
                Path.Combine(mainFolder, "OEMDevicePlatform.xml")), new UTF8Encoding(false));
            bool cat = DeviceLayoutBuilderService.RunMakecat(req2CabReq(req), cdf, staging, res.Log);
            res.Log.Add(cat ? "4 update.cat 已生成" : "4 ⚠ cat 未生成（makecat 不可用）");

            string cab = Path.Combine(req.OutputDirectory, Path.GetFileName(staging) + ".cab");
            bool ok = DeviceLayoutBuilderService.PackCab(req2CabReq(req), staging, cab, res.Log, requireLayout: false);
            res.DevicePlatformCabPath = ok ? cab : string.Empty;
            res.Log.Add(ok ? "ⓤ DevicePlatform 包完成: " + Path.GetFileName(cab) : "ⓤ  DevicePlatform 打包失败");
            if (ok && !string.IsNullOrWhiteSpace(req.SigningPfx))
                DeviceLayoutBuilderService.RunSigntool(req2CabReq(req), cab, res.Log);
            if (ok) DeviceLayoutBuilderService.CleanPackageArtifacts(staging, cdf);

            // FIP 桩包（OEMDEVICEPLATFORM_{DEV}）
            string cap = "OEMDEVICEPLATFORM_" + req.DeviceName.ToUpperInvariant().Replace("-", "");
            string fip = $"{FipFamily(owner)}.{cap}.{req.Fmid}.FIP";
            BuildStubFip(req, res, fip, cap, pkg, component, token,
                v => res.DevicePlatformFipCabPath = v);
        }

        // ════════════════ DeviceInfo 注册表包（纯 registryKeys，无文件） ════════════════

        static void BuildDeviceInfo(BspComponentRequest req, BspComponentResult res)
        {
            string arch = req.Architecture, ver = req.Version, token = req.PublicKeyToken;
            string pkg = !string.IsNullOrWhiteSpace(req.DeviceInfoPackageName)
                ? req.DeviceInfoPackageName.Trim()
                : $"{OwnerOf(req.LayoutPackageName.Length > 0 ? req.LayoutPackageName : req.DeviceName + "-Package")}-{req.DeviceName}-DeviceInfo-Package";
            string component = DeviceLayoutBuilderService.StripPackageSuffix(pkg);
            string deploy = component + "-Deployment";
            string owner = OwnerOf(pkg);

            string staging = Path.Combine(req.OutputDirectory, pkg + $"~{token}~{arch}~~");
            DeviceLayoutBuilderService.Recreate(staging);

            string mainManifest = arch + "_" + DeviceLayoutBuilderService.ComponentTail(component, token, ver, arch) + ".manifest";
            string deployManifest = arch + "_" + DeviceLayoutBuilderService.ComponentTail(deploy, token, ver, arch) + ".manifest";

            DeviceLayoutBuilderService.WriteXml(Path.Combine(staging, mainManifest),
                RegistryMainManifest(component, token, req));
            DeviceLayoutBuilderService.WriteXml(Path.Combine(staging, deployManifest),
                SimpleDeploymentManifest(deploy, component, token, req));
            res.Log.Add("ⓐ DeviceInfo 注册表 manifest 已生成");

            string updateName = DeviceLayoutBuilderService.Dotted32(pkg);
            DeviceLayoutBuilderService.WriteXml(Path.Combine(staging, "update.mum"),
                ComponentMum(pkg, component, deploy, mainManifest, deployManifest,
                    null, null, updateName, req, owner, token));
            res.Log.Add("ⓑ update.mum 已生成");

            string cdf = Path.Combine(req.OutputDirectory, pkg + ".cdf");
            File.WriteAllText(cdf, DeviceLayoutBuilderService.CdfText(staging, pkg, "update.mum", mainManifest, deployManifest),
                new UTF8Encoding(false));
            bool cat = DeviceLayoutBuilderService.RunMakecat(req2CabReq(req), cdf, staging, res.Log);
            res.Log.Add(cat ? "ⓒ update.cat 已生成" : "ⓒ ⚠ cat 未生成");

            string cab = Path.Combine(req.OutputDirectory, Path.GetFileName(staging) + ".cab");
            bool ok = DeviceLayoutBuilderService.PackCab(req2CabReq(req), staging, cab, res.Log, requireLayout: false);
            res.DeviceInfoCabPath = ok ? cab : string.Empty;
            res.Log.Add(ok ? "ⓓ DeviceInfo 包完成: " + Path.GetFileName(cab) : "ⓓ ❌ DeviceInfo 打包失败");
            if (ok && !string.IsNullOrWhiteSpace(req.SigningPfx))
                DeviceLayoutBuilderService.RunSigntool(req2CabReq(req), cab, res.Log);
            if (ok) DeviceLayoutBuilderService.CleanPackageArtifacts(staging, cdf);

            string cap2 = "DEVICE_" + req.DeviceName.ToUpperInvariant().Replace("-", "");
            string fip = $"{FipFamily(owner)}.{cap2}.{req.Fmid}.FIP";
            BuildStubFip(req, res, fip, cap2, pkg, component, token,
                v => res.DeviceInfoFipCabPath = v);
        }

        static void BuildStubFip(BspComponentRequest req, BspComponentResult res,
            string fip, string capability, string pkg, string component, string token, Action<string> setResult)
        {
            string owner = OwnerOf(pkg);
            string fipStaging = Path.Combine(req.OutputDirectory, fip + $"~{token}~{req.Architecture}~~");
            DeviceLayoutBuilderService.Recreate(fipStaging);
            DeviceLayoutBuilderService.WriteXml(Path.Combine(fipStaging, "update.mum"),
                StubFipMum(fip, req.Fmid, capability, pkg, req, owner, token));
            string cdf = Path.Combine(req.OutputDirectory, fip + ".cdf");
            File.WriteAllText(cdf, DeviceLayoutBuilderService.CdfText(fipStaging, fip, "update.mum"), new UTF8Encoding(false));
            DeviceLayoutBuilderService.RunMakecat(req2CabReq(req), cdf, fipStaging, res.Log);
            string cab = Path.Combine(req.OutputDirectory, Path.GetFileName(fipStaging) + ".cab");
            bool ok = DeviceLayoutBuilderService.PackCab(req2CabReq(req), fipStaging, cab, res.Log, requireLayout: false);
            setResult(ok ? cab : string.Empty);
            res.Log.Add((ok ? "ⓔ FIP 完成: " : "ⓔ ⚠ FIP 不完整: ") + Path.GetFileName(cab));
            if (ok) DeviceLayoutBuilderService.CleanPackageArtifacts(fipStaging, cdf);
        }

        // ════════════════ machineinfo.xml 散件 ════════════════

        static void WriteMachineInfo(BspComponentRequest req, BspComponentResult res)
        {
            string path = Path.Combine(req.OutputDirectory, "machineinfo.xml");
            File.WriteAllText(path,
$@"<?xml version='1.0' encoding='utf-8'?>
<MachineInfo Manufacturer=""{Esc(req.MachineInfoManufacturer)}"" Family=""{Esc(req.MachineInfoFamily)}"" ProductName=""{Esc(req.MachineInfoProductName)}"" SKUNumber=""{Esc(req.MachineInfoSku)}"" />
", new UTF8Encoding(false));
            res.MachineInfoPath = path;
            res.Log.Add("ⓕ machineinfo.xml 已生成（OEMInput 用 <MachineInfoFile> 引用绝对路径）");
        }

        // ════════════════ SMBIOS 源包（IoT：wm.xml + CFG，PkgGen 消费） ════════════════

        static void BuildSmbios(BspComponentRequest req, BspComponentResult res)
        {
            string dir = Path.Combine(req.OutputDirectory, "Custom.SMBIOS");
            DeviceLayoutBuilderService.Recreate(dir);

            File.WriteAllText(Path.Combine(dir, "Custom.SMBIOS.wm.xml"),
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<identity xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance""
    name=""SMBIOS""
    namespace=""Custom""
    owner=""$(OEMNAME)""
    legacyName=""$(OEMNAME).Custom.SMBIOS"" xmlns=""urn:Microsoft.CompPlat/ManifestSchema.v1.00"">
    <onecorePackageInfo
        targetPartition=""PLAT""
        releaseType=""Production""
        ownerType=""OEM"" />
    <files>
        <file
            destinationDir=""$(runtime.bootdrive)\SMBIOS\""
            source=""$(PRJDIR)\Products\$(PROD)\SMBIOS.CFG""
            name=""SMBIOS.CFG""/>
    </files>
</identity>", new UTF8Encoding(false));

            File.WriteAllText(Path.Combine(dir, "SMBIOS.CFG"), SmbiosCfg(req), new UTF8Encoding(false));
            res.SmbiosSourceDir = dir;
            res.Log.Add("ⓖ SMBIOS 源包已生成（Custom.SMBIOS.wm.xml + SMBIOS.CFG；用 PkgGen/addonkit buildpkg 出 cab，FM 里挂 CUSTOM_SMBIOS feature）");
        }

        static string SmbiosCfg(BspComponentRequest req) =>
$@"#
# SMBios Config file generated by OneCore Image Designer
#

CFG:01.01
VER:02.03
VER:02.06

# Type 1, Offset 04 System Manufacturer
01,04,S,""{req.SmbiosOemName}""

# Type 1, Offset 05 System Product Name
01,05,S,""{req.SmbiosProduct}""

# Type 1, Product Serial Number
01,07,S,""01234567890""

# Type 1, Offset 19 System SKU Number
01,19,S,""{req.SmbiosSku}""

# Type 1, Offset 1A System Family
01,1A,S,""{req.SmbiosFamily}""

# Type 2, Offset 05 Baseboard Product
02,05,S,""{req.SmbiosBbProd}""

# Add System enclosure, Type 3
ADD:03 16 0000 01 08 02 03 04 01 01 01 03 00000000 00 00 00 00 00 3100 3200 3300 3400 00

#
# End of config
#
";

        // ════════════════ 驱动包（扫描 INF → Drivers.<InfName> 源包 → 可选 PkgGen 出 cab） ════════════════

        static void BuildDriverPackages(BspComponentRequest req, BspComponentResult res)
        {
            var found = DriverPackageBuilder.Scan(req.DriverScanRoot, req.DriverNamespace, req.DriverFeatureGroup);
            if (found.Count == 0)
            {
                res.Log.Add($"⚠ 驱动扫描目录未找到任何 *.inf：{req.DriverScanRoot}");
                return;
            }
            string pkgRoot = Path.Combine(req.OutputDirectory, "Packages");
            string cabDir = Path.Combine(req.OutputDirectory, "Drivers");
            res.DriverCabDir = cabDir;
            res.Log.Add($"ⓘ 驱动扫描：{found.Count} 个含 INF 的目录（源包→Packages，cab→Drivers）");

            string? pkggen = null;
            if (req.DriverRunPkgGen)
            {
                pkggen = FindPkgGen(req);
                if (pkggen == null)
                    res.Log.Add("⚠ 未找到 PkgGen.exe（请先在「ADK 工具下载」安装对应版本或指定路径），仅生成 wm.xml 源包");
            }

            foreach (var p in found)
            {
                DriverPackageBuilder.BuildSourcePackage(p, pkgRoot, req.Partition);
                res.Log.Add($"  源包: {p.OutputName}（{p.InfFiles.Count} 个 INF，FeatureID {p.FeatureId}）");
                if (pkggen != null)
                {
                    var cabs = DriverPackageBuilder.RunPkgGen(pkggen, p, req.OwnerName, req.Version,
                        req.Architecture, req.BuildType, pkgRoot, cabDir, res.Log);
                    if (cabs.Count > 0) { p.CabPath = cabs[0]; p.CabName = Path.GetFileName(cabs[0]); }
                }
                res.DriverPackages.Add(p);
            }        }

        static string? FindPkgGen(BspComponentRequest req)
        {
            if (!string.IsNullOrWhiteSpace(req.PkgGenPath) && File.Exists(req.PkgGenPath)) return req.PkgGenPath;
            int b = BuildOfVersion(req.Version);
            if (b > 0)
            {
                var hit = AdkToolService.FindToolPath(b, "PkgGen.exe");
                if (hit != null) return hit;
            }
            foreach (var inst in AdkToolService.GetInstalledBuilds().OrderByDescending(x => x))
            {
                var hit = AdkToolService.FindToolPath(inst, "PkgGen.exe");
                if (hit != null) return hit;
            }
            return DeviceLayoutBuilderService.FindTool(null, "PkgGen.exe");
        }

        // ════════════════ 统一 FM（布局 + 组件，现代三段 + Devices） ════════════════

        static void WriteUnifiedFm(BspComponentRequest req, BspComponentResult res)
        {
            bool hasLayout = !string.IsNullOrWhiteSpace(req.LayoutPackageName);
            if (!hasLayout && string.IsNullOrWhiteSpace(res.DevicePlatformCabPath)
                         && string.IsNullOrWhiteSpace(res.DeviceInfoCabPath)
                         && res.DriverPackages.Count == 0)
                return;   // 没东西可声明

            string owner = OwnerOf(req.LayoutPackageName.Length > 0 ? req.LayoutPackageName
                : (string.IsNullOrWhiteSpace(req.DeviceName) ? req.OwnerName + "-Package" : req.DeviceName + "-Package"));
            string arch = req.Architecture;
            string dir = req.OutputDirectory;
            string tokL = DeviceLayoutBuilderService.TokenMicrosoft;
            string tokC = req.PublicKeyToken;

            var sb = new StringBuilder();
            sb.Append($@"<?xml version=""1.0"" encoding=""utf-8""?>
<FeatureManifest xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" Revision=""1"" SchemaVersion=""1.3"" ReleaseType=""Production"" OwnerType=""{owner}"" ID=""{req.Fmid}"" BuildInfo=""bsp_build"" OwnerName=""{owner}"" OSVersion=""{req.Version}"" xmlns=""http://schemas.microsoft.com/embedded/2004/10/ImageUpdate"">
");
            void Pf(string section, string id, string? name, string token, string extra = "")
                => sb.AppendLine($"    <PackageFile Path=\"{dir}\"{(name != null ? $" Name=\"{name}\"" : "")} ID=\"{id}\"{extra} /> <!-- token {token} -->");

            if (hasLayout)
            {
                sb.AppendLine("  <DeviceLayoutPackages>");
                Pf("d", req.LayoutComponentName, null, tokL, $" FeatureIdentifierPackage=\"true\" SOC=\"{req.LayoutSoc}\"");
                if (!string.IsNullOrWhiteSpace(req.LayoutFipName))
                    Pf("d", req.LayoutFipName, req.LayoutFipName + $"~{tokL}~{arch}~~.cab", tokL, $" SOC=\"{req.LayoutSoc}\"");
                sb.AppendLine("  </DeviceLayoutPackages>");
            }
            if (!string.IsNullOrWhiteSpace(res.DevicePlatformCabPath))
            {
                string dpPkg = Path.GetFileNameWithoutExtension(res.DevicePlatformCabPath)
                    .Replace($"~{tokC}~{arch}~~", "");
                string dpCap = "OEMDEVICEPLATFORM_" + req.DeviceName.ToUpperInvariant().Replace("-", "");
                string dpFip = $"{FipFamily(owner)}.{dpCap}.{req.Fmid}.FIP";
                sb.AppendLine("  <OEMDevicePlatformPackages>");
                Pf("o", DeviceLayoutBuilderService.StripPackageSuffix(dpPkg), null, tokC, $" FeatureIdentifierPackage=\"true\" Device=\"{req.DeviceName}\"");
                Pf("o", dpFip, dpFip + $"~{tokC}~{arch}~~.cab", tokC, $" Device=\"{req.DeviceName}\"");
                sb.AppendLine("  </OEMDevicePlatformPackages>");
            }
            if (!string.IsNullOrWhiteSpace(res.DeviceInfoCabPath))
            {
                string diPkg = Path.GetFileNameWithoutExtension(res.DeviceInfoCabPath)
                    .Replace($"~{tokC}~{arch}~~", "");
                string diCap = "DEVICE_" + req.DeviceName.ToUpperInvariant().Replace("-", "");
                string diFip = $"{FipFamily(owner)}.{diCap}.{req.Fmid}.FIP";
                sb.AppendLine("  <DeviceSpecificPackages>");
                Pf("s", DeviceLayoutBuilderService.StripPackageSuffix(diPkg), null, tokC, $" FeatureIdentifierPackage=\"true\" Device=\"{req.DeviceName}\"");
                Pf("s", diFip, diFip + $"~{tokC}~{arch}~~.cab", tokC, $" Device=\"{req.DeviceName}\"");
                sb.AppendLine("  </DeviceSpecificPackages>");
            }
            if (hasLayout && !string.IsNullOrWhiteSpace(res.DevicePlatformCabPath))
            {
                string dpPkg = Path.GetFileNameWithoutExtension(res.DevicePlatformCabPath)
                    .Replace($"~{tokC}~{arch}~~", "");
                sb.AppendLine("  <Devices>");
                sb.AppendLine($"    <Device Name=\"{req.DeviceName}\">");
                sb.AppendLine($"      <DeviceLayoutPackageId>{req.LayoutPackageName}</DeviceLayoutPackageId>");
                sb.AppendLine($"      <OEMDevicePlatformPackageId>{dpPkg}</OEMDevicePlatformPackageId>");
                sb.AppendLine("    </Device>");
                sb.AppendLine("  </Devices>");
            }
            if (res.DriverPackages.Count > 0)
            {
                // addonkit 范式：驱动包挂 Features/OEM，FeatureID=OutputName 大写点转下划线；
                // 已 PkgGen 出包 → 指向真实 cab；仅源包 → 给 %PKGBLD_DIR% 模板行（buildpkg 后即生效）
                sb.AppendLine("  <Features>");
                sb.AppendLine("    <Microsoft />");
                sb.AppendLine("    <OEM>");
                foreach (var p in res.DriverPackages)
                {
                    string path = p.CabName.Length > 0 ? dir : "%PKGBLD_DIR%";
                    string name = p.CabName.Length > 0 ? p.CabName : $"%OEM_NAME%.{p.OutputName}.cab";
                    sb.AppendLine($"      <PackageFile Path=\"{path}\" Name=\"{name}\">");
                    sb.AppendLine($"        <FeatureIDs><FeatureID>{p.FeatureId}</FeatureID></FeatureIDs>");
                    sb.AppendLine("      </PackageFile>");
                }
                sb.AppendLine("    </OEM>");
                sb.AppendLine("  </Features>");
            }
            sb.AppendLine("</FeatureManifest>");

            string fmPath = Path.Combine(dir, req.Fmid + "FM.xml");
            File.WriteAllText(fmPath, sb.ToString(), new UTF8Encoding(false));
            res.FmPath = fmPath;
            res.Log.Add("ⓗ 统一 FM 已生成: " + Path.GetFileName(fmPath));
        }

        // ════════════════ 模板（逐字段抄样本） ════════════════

        static string ModernOemDevicePlatformXml(BspComponentRequest req)
        {
            var ids = req.DevicePlatformIDs.Count > 0 ? req.DevicePlatformIDs : new List<string> { "*" };
            var sb = new StringBuilder();
            sb.Append($@"<?xml version=""1.0"" encoding=""utf-8""?>
<OEMDevicePlatform xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns=""http://schemas.microsoft.com/embedded/2004/10/ImageUpdate"">
  <MinSectorCount>{req.MinSectorCount}</MinSectorCount>
");
            if (!req.Modern)
                sb.AppendLine($"  <AdditionalMainOSFreeSectorsRequest>{req.AdditionalMainOSFreeSectorsRequest}</AdditionalMainOSFreeSectorsRequest>");
            sb.AppendLine("  <DevicePlatformIDs>");
            foreach (var id in ids) sb.AppendLine($"    <ID>{Esc(id)}</ID>");
            sb.AppendLine("  </DevicePlatformIDs>");
            if (!string.IsNullOrWhiteSpace(req.BiosMode))
                sb.AppendLine($"  <BIOSMode>{Esc(req.BiosMode)}</BIOSMode>");
            if (!req.Modern)
                sb.AppendLine($"  <MainOSRTCDataReservedSectors>{req.MainOSRTCDataReservedSectors}</MainOSRTCDataReservedSectors>");
            sb.AppendLine("</OEMDevicePlatform>");
            return sb.ToString();
        }

        static string FileMainManifest(string component, string token, BspComponentRequest req,
            string fileName, string digestB64, string destPath) =>
$@"<assembly xmlns=""urn:schemas-microsoft-com:asm.v3"" manifestVersion=""1.0"" copyright=""Copyright (c) {Esc(req.OwnerName)} Project. All Rights Reserved."">
  <assemblyIdentity name=""{component}"" version=""{req.Version}"" processorArchitecture=""{req.Architecture}"" language=""neutral"" buildType=""release"" publicKeyToken=""{token}"" versionScope=""nonSxS"" />
  <file name=""{fileName}"" destinationPath=""{destPath}"" sourcePath="".\"">
    <securityDescriptor name=""WRP_FILE_DEFAULT_SDDL"" />
    <asmv2:hash xmlns:asmv2=""urn:schemas-microsoft-com:asm.v2"" xmlns:dsig=""http://www.w3.org/2000/09/xmldsig#"">
      <dsig:Transforms><dsig:Transform Algorithm=""urn:schemas-microsoft-com:HashTransforms.Identity"" /></dsig:Transforms>
      <dsig:DigestMethod Algorithm=""http://www.w3.org/2000/09/xmldsig#sha256"" />
      <dsig:DigestValue>{digestB64}</dsig:DigestValue>
    </asmv2:hash>
  </file>
  <trustInfo><security><accessControl><securityDescriptorDefinitions>
    <securityDescriptorDefinition name=""WRP_FILE_DEFAULT_SDDL"" sddl=""{DeviceLayoutBuilderService.WrpSddl}"" operationHint=""replace"" description=""Default SDDL for Windows Resource Protected file"" />
  </securityDescriptorDefinitions></accessControl></security></trustInfo>
</assembly>";

        static string RegistryMainManifest(string component, string token, BspComponentRequest req) =>
$@"<assembly xmlns=""urn:schemas-microsoft-com:asm.v3"" manifestVersion=""1.0"" copyright=""Copyright (c) {Esc(req.OwnerName)} Project. All Rights Reserved."">
  <assemblyIdentity name=""{component}"" version=""{req.Version}"" processorArchitecture=""{req.Architecture}"" language=""neutral"" buildType=""release"" publicKeyToken=""{token}"" versionScope=""nonSxS"" />
  <registryKeys>
    <registryKey keyName=""HKEY_LOCAL_MACHINE\SYSTEM\ControlSet001\Control\SiliconInformation"">
      <registryValue name=""SocVersion"" valueType=""REG_SZ"" value=""{Esc(req.SocVersion)}"" />
      <registryValue name=""HardwareVersion"" valueType=""REG_SZ"" value=""{Esc(req.HardwareVersion)}"" />
      <registryValue name=""BspVersion"" valueType=""REG_SZ"" value=""{Esc(req.BspVersion)}"" />
    </registryKey>
    <registryKey keyName=""HKEY_LOCAL_MACHINE\SYSTEM\ControlSet001\Control\ManufacturerInformation"">
      <registryValue name=""DeviceManufacturer"" valueType=""REG_SZ"" value=""{Esc(req.DeviceManufacturer)}"" />
      <registryValue name=""DeviceModel"" valueType=""REG_SZ"" value=""{Esc(req.DeviceModel)}"" />
      <registryValue name=""OemSoftwareVersion"" valueType=""REG_SZ"" value=""{Esc(req.OemSoftwareVersion)}"" />
    </registryKey>
  </registryKeys>
  <trustInfo><security><accessControl><securityDescriptorDefinitions>
    <securityDescriptorDefinition name=""WRP_FILE_DEFAULT_SDDL"" sddl=""{DeviceLayoutBuilderService.WrpSddl}"" operationHint=""replace"" description=""Default SDDL for Windows Resource Protected file"" />
  </securityDescriptorDefinitions></accessControl></security></trustInfo>
</assembly>";

        static string SimpleDeploymentManifest(string deploy, string component, string token, BspComponentRequest req) =>
$@"<assembly xmlns=""urn:schemas-microsoft-com:asm.v3"" manifestVersion=""1.0"">
  <assemblyIdentity name=""{deploy}"" version=""{req.Version}"" processorArchitecture=""{req.Architecture}"" language=""neutral"" buildType=""release"" publicKeyToken=""{token}"" versionScope=""nonSxS"" />
  <deployment />
  <dependency><dependentAssembly dependencyType=""install"">
    <assemblyIdentity name=""{component}"" version=""{req.Version}"" processorArchitecture=""{req.Architecture}"" language=""neutral"" buildType=""release"" publicKeyToken=""{token}"" versionScope=""nonSxS"" />
  </dependentAssembly></dependency>
</assembly>";

        static string ComponentMum(string pkg, string component, string deploy,
            string mainManifest, string deployManifest, string? xmlName, string? xmlCabRel,
            string updateName, BspComponentRequest req, string owner, string token)
        {
            string phoneComponent = PhoneComponentOf(pkg, owner);
            var sb = new StringBuilder();
            sb.Append($@"<assembly manifestVersion=""1.0"" xmlns=""urn:schemas-microsoft-com:asm.v3"">
  <assemblyIdentity name=""{pkg}"" version=""{req.Version}"" processorArchitecture=""{req.Architecture}"" language=""neutral"" publicKeyToken=""{token}"" buildType=""release"" />
  <package identifier=""{pkg}"" releaseType=""Feature Pack"" binaryPartition=""false"" targetPartition=""{req.Partition.ToUpperInvariant()}"">
    <customInformation>
      <phoneInformation phoneRelease=""Production"" phoneOwner=""{owner}"" phoneOwnerType=""{owner}"" phoneComponent=""{phoneComponent}"" phoneSubComponent="""" phoneGroupingKey="""" />
      <file name=""\Windows\servicing\Packages\update.mum"" size=""0"" staged=""0"" compressed=""0"" cabpath=""update.mum"" sourceComponent=""{pkg}"" />
      <file name=""\windows\system32\catroot\{{F750E6C3-38EE-11D1-85E5-00C04FC295EE}}\update.cat"" size=""0"" staged=""0"" compressed=""0"" cabpath=""update.cat"" sourceComponent=""{pkg}"" />
      <file name=""\windows\WinSxS\Manifests\{deployManifest}"" size=""0"" staged=""0"" compressed=""0"" sourcePackage=""{pkg}"" cabpath=""{deployManifest}"" sourceComponent=""{deploy}"" />
      <file name=""\windows\WinSxS\Manifests\{mainManifest}"" size=""0"" staged=""0"" compressed=""0"" sourcePackage=""{pkg}"" cabpath=""{mainManifest}"" sourceComponent=""{component}"" />
");
            if (xmlName != null && xmlCabRel != null)
                sb.AppendLine($@"      <file name=""\Windows\ImageUpdate\{xmlName}"" size=""0"" staged=""0"" compressed=""0"" sourcePackage=""{pkg}"" cabpath=""{xmlCabRel}"" sourceComponent=""{component}"" />");
            sb.Append($@"    </customInformation>
    <update name=""{updateName}"">
      <component><assemblyIdentity name=""{deploy}"" version=""{req.Version}"" processorArchitecture=""{req.Architecture}"" language=""neutral"" publicKeyToken=""{token}"" buildType=""release"" versionScope=""nonSxS"" /></component>
    </update>
  </package>
</assembly>");
            return sb.ToString();
        }

        static string StubFipMum(string fip, string fmid, string capability, string pkg,
            BspComponentRequest req, string owner, string token)
        {
            string component = DeviceLayoutBuilderService.StripPackageSuffix(pkg);
            return $@"<assembly manifestVersion=""1.0"" xmlns=""urn:schemas-microsoft-com:asm.v3"">
  <assemblyIdentity name=""{fip}"" version=""{req.Version}"" processorArchitecture=""{req.Architecture}"" language=""neutral"" publicKeyToken=""{token}"" buildType=""release"" />
  <package identifier=""{fip}"" releaseType=""Feature Pack"" binaryPartition=""false"" targetPartition=""{req.Partition.ToUpperInvariant()}"">
    <customInformation>
      <declareCapability>
        <capability group=""{owner}"" fmid=""{fmid}"">
          <capabilityIdentity name=""{capability}"" />
          <featurePackage>
            <package satelliteType=""Base"" targetPartition=""MAINOS"" binaryPartition=""false"">
              <assemblyIdentity name=""{pkg}"" processorArchitecture=""{req.Architecture}"" publicKeyToken=""{token}"" version=""{req.Version}"" />
            </package>
            <package satelliteType=""Base"" targetPartition=""MainOS"" binaryPartition=""false"">
              <assemblyIdentity name=""{fip}"" processorArchitecture=""{req.Architecture}"" publicKeyToken=""{token}"" version=""{req.Version}"" />
            </package>
          </featurePackage>
        </capability>
      </declareCapability>
      <phoneInformation phoneRelease=""Production"" phoneOwner=""{owner}"" phoneOwnerType=""{owner}"" phoneComponent=""{PhoneComponentOf(fip, owner)}"" phoneSubComponent=""FIP"" phoneGroupingKey="""" />
      <file name=""\Windows\servicing\Packages\update.mum"" size=""0"" staged=""0"" compressed=""0"" cabpath=""update.mum"" sourceComponent=""{fip}"" />
      <file name=""\windows\system32\catroot\{{F750E6C3-38EE-11D1-85E5-00C04FC295EE}}\update.cat"" size=""0"" staged=""0"" compressed=""0"" cabpath=""update.cat"" sourceComponent=""{fip}"" />
    </customInformation>
  </package>
</assembly>";
        }

        /// <summary>QEMU-QEMUArm64-DevicePlatform4096-Package → QEMUArm64.DevicePlatform4096（样本实证）。</summary>
        static string PhoneComponentOf(string pkg, string owner)
        {
            string s = pkg;
            if (s.StartsWith(owner + "-", StringComparison.OrdinalIgnoreCase)) s = s[(owner.Length + 1)..];
            if (s.EndsWith("-Package", StringComparison.OrdinalIgnoreCase)) s = s[..^"-Package".Length];
            if (s.EndsWith(".FIP", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
            return s.Replace('-', '.');
        }

        // ── builder 工具参数适配（builder 的 internal 方法收 DeviceLayoutBuildRequest，这里做薄适配） ──
        static DeviceLayoutBuildRequest req2CabReq(BspComponentRequest r) => new()
        {
            Architecture = r.Architecture,
            Version = r.Version,
            CabarcPath = r.CabarcPath,
            MakecatPath = r.MakecatPath,
            SignToolPath = r.SignToolPath,
            SigningPfx = r.SigningPfx,
            SigningPfxPassword = r.SigningPfxPassword,
            Partition = r.Partition,
            OutputDirectory = r.OutputDirectory,
        };

        static string Esc(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
