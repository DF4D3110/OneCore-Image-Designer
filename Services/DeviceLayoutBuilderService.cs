using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using OneCore_Image_Designer.Models.DeviceLayout;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// 造包时代（不同 ADK 时期内部差异大，用户已知；两套范式都来自真实样本实证）：
    ///  - Phone    ：早期 WPAK（QC8960 / QC8998，14xxx~17xxx），主组件名带 0 后缀、6288 token、
    ///               DeviceLayout 落在 windows\ImageUpdate、SOCPackages+Features 的 FM。
    ///  - FactoryOs：现代 10x/ModernPC（18xxx+，如 20279），布局包用 31bf token 且名无 0 后缀、
    ///               DeviceLayout 落在组件目录根、mum=Feature Pack 带 sourceComponent，另有独立
    ///               FIP 桩包（纯 mum+cat、declareCapability），FM 带 ID=owner/SchemaVersion1.3 三段 *Packages。
    /// </summary>
    public enum DeviceLayoutEra { Phone, FactoryOs }

    public class DeviceLayoutBuildRequest
    {
        public DeviceLayoutDoc Layout { get; set; } = new();
        public bool LayoutIsV2 { get; set; } = true;
        /// <summary>
        /// 若指定，打包时「原样」复制该 DeviceLayout.xml（保留 StoragePools/Stores 多 LUN 等
        /// 原始形状），而非从 <see cref="Layout"/> 重序列化。模板载入的复杂布局必须走这条路。
        /// </summary>
        public string? LayoutSourceXml { get; set; }
        public DeviceLayoutEra Era { get; set; } = DeviceLayoutEra.FactoryOs;

        /// <summary>包 ID / 名（Phone：点分如 Microsoft.DEVICELAYOUT_XXX；FactoryOs：连字符布局包名，见下）。</summary>
        public string PackageId { get; set; } = string.Empty;
        public string Version { get; set; } = "10.0.19041.1000";
        public string Architecture { get; set; } = "arm64";
        /// <summary>FM 里 PackageFile.SOC 与 OEMInput 的 SOC/DeviceLayoutType 短名。</summary>
        public string Soc { get; set; } = string.Empty;
        public string Partition { get; set; } = "MainOS";

        // ── 仅 Phone 时代用：publicKeyToken（测试/社区包统一 6288…）──
        public string PublicKeyToken { get; set; } = "628844477771337a";

        // ── 仅 FactoryOs 时代字段（留空则从 PackageId/Soc 自动推导）──
        public string OwnerName = "Microsoft";        // phoneOwner / FM OwnerName
        public string Fmid = string.Empty;           // FM 根 ID，如 FOSGENDEV
        public string CapabilityName = string.Empty; // declareCapability 名，如 DEVICELAYOUT_GPT_SPACES_512
        public string LayoutPackageName = string.Empty; // 连字符布局包名，如 Microsoft-OneCore-...-Package
        public string FipPackageName = string.Empty;    // 点分 FIP 名，如 Microsoft.FactoryOS.DEVICELAYOUT_...FOSGENDEV.FIP

        public string OutputDirectory { get; set; } = string.Empty;
        public string CabarcPath { get; set; } = string.Empty;
        public string MakecatPath { get; set; } = string.Empty;
        public string SignToolPath { get; set; } = string.Empty;
        public string? SigningPfx { get; set; }
        public string? SigningPfxPassword { get; set; }
    }

    public class DeviceLayoutBuildResult
    {
        public bool Success { get; set; }
        public string CabPath { get; set; } = string.Empty;          // 主产物（Phone 布局包 / FactoryOs 布局包）
        public string FipCabPath { get; set; } = string.Empty;        // FactoryOs 额外 FIP 桩包
        public string FmPath { get; set; } = string.Empty;
        public string StagingDir { get; set; } = string.Empty;
        public List<string> Log { get; } = new();
        public string? Error { get; set; }
    }

    /// <summary>
    /// 「设备布局创建」核心服务：从零造 DeviceLayout FM + CAB（打包→makecat→可选签名）。
    /// 打包工具链按 E:\wcos\Tool README 的重打包流程；两种时代的包/FM/manifest/mum 模板全部取自真实样本，
    /// keyform 文件名哈希复用已实测 100% 命中的 WinSxSHashCalculator。
    /// </summary>
    public static class DeviceLayoutBuilderService
    {
        const string TokenMicrosoft = "31bf3856ad364e35";  // 现代布局包 assemblyIdentity 用
        const string TokenTestDefault = "628844477771337a"; // Phone/社区 + 现代 FIP 用

        // 现代样本 WRP 安全描述符（照抄，imageapp 需要）
        const string WrpSddl = "O:S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464G:S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464D:P(A;;FA;;;S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464)(A;;GRGX;;;BA)(A;;GRGX;;;SY)(A;;GRGX;;;BU)(A;;GRGX;;;S-1-15-2-1)(A;;GRGX;;;S-1-15-2-2)S:(AU;FASA;0x000D0116;;;WD)";

        public static DeviceLayoutBuildResult Build(DeviceLayoutBuildRequest req)
        {
            var res = new DeviceLayoutBuildResult();
            try
            {
                Validate(req);
                return req.Era == DeviceLayoutEra.Phone ? BuildPhone(req, res) : BuildFactoryOs(req, res);
            }
            catch (Exception ex)
            {
                res.Error = ex.Message;
                res.Log.Add("错误: " + ex.Message);
                return res;
            }
        }

        /// <summary>
        /// 布局 XML 落盘：优先「原样复制」<see cref="DeviceLayoutBuildRequest.LayoutSourceXml"/>
        /// （模板载入且未改形状时，保留 StoragePools/Stores 等原结构），否则从 doc 重序列化。
        /// </summary>
        static void WriteLayoutXml(DeviceLayoutBuildRequest req, string layoutAbs, DeviceLayoutBuildResult res)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(layoutAbs)!);
            if (!string.IsNullOrWhiteSpace(req.LayoutSourceXml) && File.Exists(req.LayoutSourceXml))
            {
                File.Copy(req.LayoutSourceXml, layoutAbs, true);
                res.Log.Add("① DeviceLayout.xml 原样写入（保留模板/导入的存储池/多 LUN 形状）");
            }
            else
            {
                DeviceLayoutService.Serialize(req.Layout, layoutAbs, req.LayoutIsV2);
                res.Log.Add("① DeviceLayout.xml 已生成（自分区表重序列化）");
            }
        }

        static void Validate(DeviceLayoutBuildRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.PackageId)) throw new InvalidOperationException("包 ID 不能为空");
            if (!ValidateIdentifier(req.PackageId)) throw new InvalidOperationException("包 ID 只能包含字母数字与 . _ -");
            if (string.IsNullOrWhiteSpace(req.Soc)) throw new InvalidOperationException("SOC 短名不能为空");
            if (string.IsNullOrWhiteSpace(req.OutputDirectory)) throw new InvalidOperationException("输出目录未选择");
        }

        // ════════════════════════ 早期 Phone 时代（保留原实证逻辑） ════════════════════════
        static DeviceLayoutBuildResult BuildPhone(DeviceLayoutBuildRequest req, DeviceLayoutBuildResult res)
        {
            string pkgId = req.PackageId.Trim();
            string token = string.IsNullOrWhiteSpace(req.PublicKeyToken) ? TokenTestDefault : req.PublicKeyToken.Trim();
            string mainName = pkgId + "0";
            string deployName = pkgId + ".Deployment";

            string staging = Path.Combine(req.OutputDirectory, pkgId + "~" + token + "~" + req.Architecture + "~~");
            res.StagingDir = staging;
            Recreate(staging);

            string mainFolderName = req.Architecture + "_" + ComponentTail(mainName, token, req.Version, req.Architecture);
            string mainManifestName = mainFolderName + ".manifest";
            string deployManifestName = req.Architecture + "_" + ComponentTail(deployName, token, req.Version, req.Architecture) + ".manifest";
            string componentDir = Path.Combine(staging, mainFolderName, "windows", "ImageUpdate");
            Directory.CreateDirectory(componentDir);

            string layoutRel = Path.Combine(mainFolderName, "windows", "ImageUpdate", "DeviceLayout.xml");
            string layoutAbs = Path.Combine(staging, layoutRel);
            WriteLayoutXml(req, layoutAbs, res);

            string digestB64 = Sha256Base64(layoutAbs);
            string digestHex = Sha256Hex(layoutAbs);
            WriteXml(Path.Combine(staging, mainManifestName), PhoneMainManifest(mainName, token, req, digestB64));
            WriteXml(Path.Combine(staging, deployManifestName), PhoneDeploymentManifest(mainName, deployName, token, req));
            res.Log.Add("② 主/部署 manifest 已生成（明文）");

            WriteXml(Path.Combine(staging, "update.mum"), PhoneMum(pkgId, mainName, deployName, token, req, mainManifestName, deployManifestName, digestHex, layoutAbs, mainFolderName));
            res.Log.Add("③ update.mum 已生成");

            string fmPath = Path.Combine(req.OutputDirectory, pkgId + "FM.xml");
            File.WriteAllText(fmPath, PhoneFm(pkgId, req), new UTF8Encoding(false));
            res.FmPath = fmPath;
            res.Log.Add("④ FM 已生成: " + Path.GetFileName(fmPath));

            string cdf = Path.Combine(req.OutputDirectory, pkgId + ".cdf");
            File.WriteAllText(cdf, CdfText(staging, pkgId, "update.mum", mainManifestName, deployManifestName, layoutRel), new UTF8Encoding(false));
            bool catOk = RunMakecat(req, cdf, staging, res);
            res.Log.Add(catOk ? "⑤ update.cat 已生成" : "⑤ ⚠ 未生成 update.cat（makecat 不可用；Test 镜像可先不签名）");

            string cabPath = Path.Combine(req.OutputDirectory, pkgId + ".cab");
            bool ok = PackCab(req, staging, cabPath, res, requireLayout: true);
            res.CabPath = ok ? cabPath : string.Empty;
            res.Success = ok;
            if (ok && !string.IsNullOrWhiteSpace(req.SigningPfx)) RunSigntool(req, cabPath, res);
            return res;
        }

        // ════════════════════════ 现代 FactoryOS 时代 ════════════════════════
        static DeviceLayoutBuildResult BuildFactoryOs(DeviceLayoutBuildRequest req, DeviceLayoutBuildResult res)
        {
            // 名字推导
            string arch = req.Architecture;
            string layoutPkg = !string.IsNullOrWhiteSpace(req.LayoutPackageName) ? req.LayoutPackageName.Trim()
                : DefaultLayoutPackageName(req.PackageId);                 // 连字符，以 -Package 结尾
            string layoutComponent = StripPackageSuffix(layoutPkg);         // 去 -Package
            string deployComponent = layoutComponent + "-Deployment";
            string fip = !string.IsNullOrWhiteSpace(req.FipPackageName) ? req.FipPackageName.Trim()
                : DefaultFipName(req.PackageId, req.Soc, req.Fmid);         // 点分，.FIP 结尾
            string fmid = !string.IsNullOrWhiteSpace(req.Fmid) ? req.Fmid.Trim() : DefaultFmid(req.PackageId);
            string capability = !string.IsNullOrWhiteSpace(req.CapabilityName) ? req.CapabilityName.Trim()
                : (req.Soc.StartsWith("DEVICELAYOUT_", StringComparison.OrdinalIgnoreCase) ? req.Soc : "DEVICELAYOUT_" + req.Soc.ToUpperInvariant());
            string owner = string.IsNullOrWhiteSpace(req.OwnerName) ? "Microsoft" : req.OwnerName.Trim();

            string cabKeyformSuffix = $"~{TokenMicrosoft}~{arch}~~";        // 布局包 cab/目录名后缀（31bf）
            string fipKeyformSuffix = $"~{TokenMicrosoft}~{arch}~~";      // FIP 文件名也用 31bf（样本实测；其内部 mum assemblyIdentity 仍用 6288）

            // ── 布局包 ──
            string staging = Path.Combine(req.OutputDirectory, layoutPkg + cabKeyformSuffix);
            res.StagingDir = staging;
            Recreate(staging);

            string mainFolderName = arch + "_" + ComponentTail(layoutComponent, TokenMicrosoft, req.Version, arch);
            string mainManifestName = mainFolderName + ".manifest";
            string deployManifestName = arch + "_" + ComponentTail(deployComponent, TokenMicrosoft, req.Version, arch) + ".manifest";
            Directory.CreateDirectory(Path.Combine(staging, mainFolderName));

            // DeviceLayout.xml（或 MSDeviceLayout 形状时官方名为 DeviceLayoutMS.xml）在组件目录根
            string layoutRel = Path.Combine(mainFolderName, LayoutFileName(req));
            string layoutAbs = Path.Combine(staging, layoutRel);
            WriteLayoutXml(req, layoutAbs, res);

            string digestB64 = Sha256Base64(layoutAbs);
            WriteXml(Path.Combine(staging, mainManifestName), ModernMainManifest(layoutComponent, deployComponent, req, mainManifestName, deployManifestName, digestB64));
            WriteXml(Path.Combine(staging, deployManifestName), ModernDeploymentManifest(layoutComponent, deployComponent, req));
            res.Log.Add("② 主/部署 manifest 已生成");

            string updateName = Dotted32(layoutPkg);
            WriteXml(Path.Combine(staging, "update.mum"), ModernLayoutMum(layoutPkg, layoutComponent, deployComponent, mainManifestName, deployManifestName, layoutRel, layoutAbs, updateName, req, owner));
            res.Log.Add("③ update.mum 已生成（Feature Pack）");

            string cdf1 = Path.Combine(req.OutputDirectory, layoutPkg + ".cdf");
            File.WriteAllText(cdf1, CdfText(staging, layoutPkg, "update.mum", mainManifestName, deployManifestName, layoutRel), new UTF8Encoding(false));
            bool cat1 = RunMakecat(req, cdf1, staging, res);
            res.Log.Add(cat1 ? "④ 布局包 update.cat 已生成" : "④ ⚠ 布局包 cat 未生成（makecat 不可用）");

            string cabPath = Path.Combine(req.OutputDirectory, layoutPkg + cabKeyformSuffix + ".cab");
            bool okLayout = PackCab(req, staging, cabPath, res, requireLayout: true);
            res.CabPath = okLayout ? cabPath : string.Empty;
            res.Log.Add(okLayout ? "⑤ 布局包 CAB 完成: " + Path.GetFileName(cabPath) : "⑤ ❌ 布局包打包失败");

            // ── FIP 桩包（纯 mum + cat，无 XML/manifest）──
            string fipStaging = Path.Combine(req.OutputDirectory, fip + fipKeyformSuffix);
            Recreate(fipStaging);
            WriteXml(Path.Combine(fipStaging, "update.mum"), ModernFipMum(fip, fmid, capability, layoutComponent, layoutPkg, req, owner));
            string cdf2 = Path.Combine(req.OutputDirectory, fip + ".cdf");
            File.WriteAllText(cdf2, CdfText(fipStaging, fip, "update.mum"), new UTF8Encoding(false));
            bool cat2 = RunMakecat(req, cdf2, fipStaging, res);
            string fipCab = Path.Combine(req.OutputDirectory, fip + fipKeyformSuffix + ".cab");
            bool okFip = PackCab(req, fipStaging, fipCab, res, requireLayout: false);
            res.FipCabPath = okFip ? fipCab : string.Empty;
            res.Log.Add((cat2 && okFip ? "⑥ FIP 桩包完成: " : "⑥ ⚠ FIP 桩包不完整: ") + Path.GetFileName(fipCab));

            // ── FM（现代三段，ID=owner，无 Features/SOCPackages）──
            string fmPath = Path.Combine(req.OutputDirectory, fmid + "FM.xml");
            File.WriteAllText(fmPath, ModernFm(fmid, owner, capability, layoutComponent, layoutPkg, fip, req, arch), new UTF8Encoding(false));
            res.FmPath = fmPath;
            res.Log.Add("⑦ FM 已生成: " + Path.GetFileName(fmPath));

            res.Success = okLayout;   // 布局包成功即整体可用；FIP/cat 失败在日志提示
            if (okLayout && !string.IsNullOrWhiteSpace(req.SigningPfx)) RunSigntool(req, cabPath, res);
            return res;
        }

        // ── 现代名字推导 ──
        static string DefaultLayoutPackageName(string pkg)
        {
            string baseName = pkg.Replace('.', '-');
            return baseName.EndsWith("-Package", StringComparison.OrdinalIgnoreCase) ? baseName : baseName + "-Package";
        }
        static string StripPackageSuffix(string pkg) =>
            pkg.EndsWith("-Package", StringComparison.OrdinalIgnoreCase) ? pkg[..^"-Package".Length] : pkg;
        /// <summary>SpaceSplit（MSDeviceLayout）官方进包文件名是 DeviceLayoutMS.xml，其余为 DeviceLayout.xml。</summary>
        static string LayoutFileName(DeviceLayoutBuildRequest req) =>
            req.Layout?.RootTag == "MSDeviceLayout" ? "DeviceLayoutMS.xml" : "DeviceLayout.xml";
        static string DefaultFmid(string dotted)
        {
            var parts = dotted.Split('.');
            return parts.Length >= 3 ? parts[^2] : parts[^1]; // ...{FOSGENDEV}.FIP → FOSGENDEV
        }
        static string DefaultFipName(string layoutPkgOrDotted, string soc, string fmid)
        {
            string cap = soc.StartsWith("DEVICELAYOUT_", StringComparison.OrdinalIgnoreCase) ? soc : "DEVICELAYOUT_" + soc.ToUpperInvariant();
            // 现代布局包名是连字符式（Microsoft-OneCore-…-Package），owner = 第一段；早期点分式取第一段等价
            string owner = layoutPkgOrDotted.Split('-', '.')[0];
            string fam = owner.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) ? "FactoryOS" : owner + ".FactoryOS";
            string fm = string.IsNullOrWhiteSpace(fmid) ? DefaultFmid(layoutPkgOrDotted) : fmid;
            return $"{fam}.{cap}.{fm}.FIP";
        }
        static string Dotted32(string seed)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
            return Convert.ToHexString(bytes).Substring(0, 32).ToLowerInvariant();
        }

        // ════════════════════════ manifest / mum / fm 模板 ════════════════════════

        static string ComponentTail(string innerName, string token, string version, string arch)
        {
            string cleaned = WinSxSHashCalculator.NormalizeNameForFolder(innerName);
            string hash = WinSxSHashCalculator.CalculateWinSxSHash(innerName, token, arch, version, culture: "neutral", versionScope: "nonSxS");
            return $"{cleaned}_{token}_{version.ToLowerInvariant()}_none_{hash}";
        }

        // ── Phone 模板 ──
        static string PhoneMainManifest(string mainName, string token, DeviceLayoutBuildRequest req, string digestB64) =>
$@"<assembly xmlns=""urn:schemas-microsoft-com:asm.v3"" manifestVersion=""1.0"">
  <assemblyIdentity name=""{mainName}"" version=""{req.Version}"" processorArchitecture=""{req.Architecture}"" language=""neutral"" buildType=""release"" publicKeyToken=""{token}"" versionScope=""nonSxS"" />
  <file name=""windows\ImageUpdate\DeviceLayout.xml"" destinationPath=""$(runtime.systemroot)\ImageUpdate\"" attributes=""archive"">
    <asmv2:hash xmlns:asmv2=""urn:schemas-microsoft-com:asm.v2"">
      <dsig:Transforms xmlns:dsig=""http://www.w3.org/2000/09/xmldsig#""><dsig:Transform Algorithm=""urn:schemas-microsoft-com:HashTransforms.Identity"" /></dsig:Transforms>
      <dsig:DigestMethod xmlns:dsig=""http://www.w3.org/2000/09/xmldsig#"" Algorithm=""http://www.w3.org/2000/09/xmldsig#sha256"" />
      <dsig:DigestValue xmlns:dsig=""http://www.w3.org/2000/09/xmldsig#"">{digestB64}</dsig:DigestValue>
    </asmv2:hash>
    <securityDescriptor name=""SD0"" />
  </file>
  <trustInfo><security><accessControl><securityDescriptorDefinitions>
    <securityDescriptorDefinition name=""SD0"" sddl=""(inherit)"" />
  </securityDescriptorDefinitions></accessControl></security></trustInfo>
</assembly>";

        static string PhoneDeploymentManifest(string mainName, string deployName, string token, DeviceLayoutBuildRequest req) =>
$@"<assembly xmlns=""urn:schemas-microsoft-com:asm.v3"" manifestVersion=""1.0"">
  <assemblyIdentity name=""{deployName}"" version=""{req.Version}"" processorArchitecture=""{req.Architecture}"" language=""neutral"" buildType=""release"" publicKeyToken=""{token}"" versionScope=""nonSxS"" />
  <deployment />
  <dependency><dependentAssembly dependencyType=""install"">
    <assemblyIdentity name=""{mainName}"" version=""{req.Version}"" processorArchitecture=""{req.Architecture}"" language=""neutral"" buildType=""release"" publicKeyToken=""{token}"" versionScope=""nonSxS"" />
  </dependentAssembly></dependency>
</assembly>";

        static string PhoneMum(string pkgId, string mainName, string deployName, string token, DeviceLayoutBuildRequest req,
            string mainManifestName, string deployManifestName, string digestHex, string layoutAbs, string mainFolderName)
        {
            string layoutSize = new FileInfo(layoutAbs).Length.ToString();
            string owner = pkgId.Split('.')[0];
            string component = string.Join(".", pkgId.Split('.')[1..]);
            return $@"<assembly xmlns=""urn:schemas-microsoft-com:asm.v3"" manifestVersion=""1.0"" displayName=""default"" company=""{owner}"" copyright=""{owner}"">
  <assemblyIdentity name=""{pkgId}"" version=""{req.Version}"" language=""neutral"" processorArchitecture=""{req.Architecture}"" publicKeyToken=""{token}"" buildType=""release"" />
  <package identifier=""{pkgId}"" releaseType=""Update"" restart=""possible"" targetPartition=""{req.Partition}"" binaryPartition=""false"" >
    <customInformation>
      <phoneInformation phoneRelease=""Production"" phoneOwnerType=""OEM"" phoneOwner=""{owner}"" phoneComponent=""{component}"" phoneSubComponent=""{req.Partition}"" phoneGroupingKey=""{component}""/>
      <file name=""\update.mum"" size=""0"" staged=""0"" compressed=""0"" cabpath=""update.mum""/>
      <file name=""\Windows\System32\catroot\{{F750E6C3-38EE-11D1-85E5-00C04FC295EE}}\update.cat"" size=""0"" staged=""0"" compressed=""0"" cabpath=""update.cat""/>
      <file name=""$(runtime.systemroot)\ImageUpdate\DeviceLayout.xml"" size=""{layoutSize}"" staged=""{layoutSize}"" compressed=""{layoutSize}"" cabpath=""{mainFolderName}\windows\ImageUpdate\DeviceLayout.xml"" hash=""{digestHex}""/>
      <file name=""{mainManifestName}"" size=""0"" staged=""0"" compressed=""0"" cabpath=""{mainManifestName}""/>
      <file name=""{deployManifestName}"" size=""0"" staged=""0"" compressed=""0"" cabpath=""{deployManifestName}""/>
    </customInformation>
    <update name=""{pkgId.Replace('.', '_')}"">
      <component><assemblyIdentity name=""{deployName}"" version=""{req.Version}"" processorArchitecture=""{req.Architecture}"" language=""neutral"" buildType=""release"" publicKeyToken=""{token}"" versionScope=""nonSxS"" /></component>
    </update>
  </package>
</assembly>";
        }

        // ── 现代模板（逐字段抄 20279 样本）──
        static string ModernMainManifest(string layoutComponent, string deployComponent, DeviceLayoutBuildRequest req,
            string mainManifestName, string deployManifestName, string digestB64)
        {
            string arch = req.Architecture;
            return $@"<assembly xmlns=""urn:schemas-microsoft-com:asm.v3"" manifestVersion=""1.0"" copyright=""Copyright (c) Microsoft Corporation. All Rights Reserved."">
  <assemblyIdentity name=""{layoutComponent}"" version=""{req.Version}"" processorArchitecture=""{arch}"" language=""neutral"" buildType=""release"" publicKeyToken=""{TokenMicrosoft}"" versionScope=""nonSxS"" />
  <file name=""{LayoutFileName(req)}"" destinationPath=""$(runtime.windows)\ImageUpdate\"" sourcePath="".\"">
    <securityDescriptor name=""WRP_FILE_DEFAULT_SDDL"" />
    <asmv2:hash xmlns:asmv2=""urn:schemas-microsoft-com:asm.v2"" xmlns:dsig=""http://www.w3.org/2000/09/xmldsig#"">
      <dsig:Transforms><dsig:Transform Algorithm=""urn:schemas-microsoft-com:HashTransforms.Identity"" /></dsig:Transforms>
      <dsig:DigestMethod Algorithm=""http://www.w3.org/2000/09/xmldsig#sha256"" />
      <dsig:DigestValue>{digestB64}</dsig:DigestValue>
    </asmv2:hash>
  </file>
  <trustInfo><security><accessControl><securityDescriptorDefinitions>
    <securityDescriptorDefinition name=""WRP_FILE_DEFAULT_SDDL"" sddl=""{WrpSddl}"" operationHint=""replace"" description=""Default SDDL for Windows Resource Protected file"" />
  </securityDescriptorDefinitions></accessControl></security></trustInfo>
</assembly>";
        }

        static string ModernDeploymentManifest(string layoutComponent, string deployComponent, DeviceLayoutBuildRequest req) =>
$@"<assembly xmlns=""urn:schemas-microsoft-com:asm.v3"" manifestVersion=""1.0"" copyright=""Copyright (c) Microsoft Corporation. All Rights Reserved."">
  <assemblyIdentity name=""{deployComponent}"" version=""{req.Version}"" processorArchitecture=""{req.Architecture}"" language=""neutral"" buildType=""release"" publicKeyToken=""{TokenMicrosoft}"" versionScope=""nonSxS"" />
  <deployment xmlns=""urn:schemas-microsoft-com:asm.v3"" />
  <dependency discoverable=""yes""><dependentAssembly dependencyType=""install"">
    <assemblyIdentity name=""{layoutComponent}"" version=""{req.Version}"" processorArchitecture=""{req.Architecture}"" language=""neutral"" buildType=""release"" publicKeyToken=""{TokenMicrosoft}"" versionScope=""nonSxS"" />
  </dependentAssembly></dependency>
</assembly>";

        static string ModernLayoutMum(string layoutPkg, string layoutComponent, string deployComponent,
            string mainManifestName, string deployManifestName, string layoutRel, string layoutAbs, string updateName,
            DeviceLayoutBuildRequest req, string owner)
        {
            int layoutLen = (int)new FileInfo(layoutAbs).Length;
            return $@"<assembly manifestVersion=""1.0"" xmlns=""urn:schemas-microsoft-com:asm.v3"">
  <assemblyIdentity name=""{layoutPkg}"" version=""{req.Version}"" processorArchitecture=""{req.Architecture}"" language=""neutral"" publicKeyToken=""{TokenMicrosoft}"" buildType=""release"" />
  <package identifier=""{layoutPkg}"" releaseType=""Feature Pack"" binaryPartition=""false"" targetPartition=""{req.Partition}"">
    <customInformation>
      <phoneInformation phoneRelease=""Production"" phoneOwner=""{owner}"" phoneOwnerType=""{owner}"" phoneComponent=""{ComponentOf(layoutPkg, owner)}"" phoneSubComponent="""""" phoneGroupingKey="""""" />
      <file name=""\Windows\servicing\Packages\update.mum"" size=""{layoutLen}"" staged=""{layoutLen}"" compressed=""{layoutLen}"" cabpath=""update.mum"" sourceComponent=""{layoutPkg}"" />
      <file name=""\windows\system32\catroot\{{F750E6C3-38EE-11D1-85E5-00C04FC295EE}}\update.cat"" size=""0"" staged=""0"" compressed=""0"" cabpath=""update.cat"" sourceComponent=""{layoutPkg}"" />
      <file name=""\windows\WinSxS\Manifests\{deployManifestName}"" size=""0"" staged=""0"" compressed=""0"" sourcePackage=""{layoutPkg}"" cabpath=""{deployManifestName}"" sourceComponent=""{deployComponent}"" />
      <file name=""\windows\WinSxS\Manifests\{mainManifestName}"" size=""0"" staged=""0"" compressed=""0"" sourcePackage=""{layoutPkg}"" cabpath=""{mainManifestName}"" sourceComponent=""{layoutComponent}"" />
      <file name=""\Windows\ImageUpdate\{LayoutFileName(req)}"" size=""{layoutLen}"" staged=""{layoutLen}"" compressed=""{layoutLen}"" sourcePackage=""{layoutPkg}"" cabpath=""{layoutRel.Replace('\\', '/').Replace('/', '\\')}"" sourceComponent=""{layoutComponent}"" />
    </customInformation>
    <update name=""{updateName}"">
      <component><assemblyIdentity name=""{deployComponent}"" version=""{req.Version}"" processorArchitecture=""{req.Architecture}"" language=""neutral"" publicKeyToken=""{TokenMicrosoft}"" buildType=""release"" versionScope=""nonSxS"" /></component>
    </update>
  </package>
</assembly>";
        }

        static string ModernFipMum(string fip, string fmid, string capability, string layoutComponent, string layoutPkg,
            DeviceLayoutBuildRequest req, string owner)
        {
            string capOwner = owner == "Microsoft" ? "Microsoft" : owner;
            return $@"<assembly manifestVersion=""1.0"" xmlns=""urn:schemas-microsoft-com:asm.v3"">
  <assemblyIdentity name=""{fip}"" version=""{req.Version}"" processorArchitecture=""{req.Architecture}"" language=""neutral"" publicKeyToken=""{TokenTestDefault}"" buildType=""release"" />
  <package identifier=""{fip}"" releaseType=""Feature Pack"" binaryPartition=""false"" targetPartition=""{req.Partition}"">
    <customInformation>
      <declareCapability>
        <capability group=""{capOwner}"" fmid=""{fmid}"">
          <capabilityIdentity name=""{capability}"" />
          <featurePackage>
            <package satelliteType=""Base"" targetPartition=""MAINOS"" binaryPartition=""false"">
              <assemblyIdentity name=""{layoutPkg}"" processorArchitecture=""{req.Architecture}"" publicKeyToken=""{TokenMicrosoft}"" version=""{req.Version}"" />
            </package>
            <package satelliteType=""Base"" targetPartition=""MainOS"" binaryPartition=""false"">
              <assemblyIdentity name=""{fip}"" processorArchitecture=""{req.Architecture}"" publicKeyToken=""{TokenTestDefault}"" version=""{req.Version}"" />
            </package>
          </featurePackage>
        </capability>
      </declareCapability>
      <phoneInformation phoneRelease=""Production"" phoneOwner=""{owner}"" phoneOwnerType=""{owner}"" phoneComponent=""{ComponentOf(fip, owner)}"" phoneSubComponent=""FIP"" phoneGroupingKey="""" />
      <file name=""\Windows\servicing\Packages\update.mum"" size=""0"" staged=""0"" compressed=""0"" cabpath=""update.mum"" sourceComponent=""{fip}"" />
      <file name=""\windows\system32\catroot\{{F750E6C3-38EE-11D1-85E5-00C04FC295EE}}\update.cat"" size=""0"" staged=""0"" compressed=""0"" cabpath=""update.cat"" sourceComponent=""{fip}"" />
    </customInformation>
  </package>
</assembly>";
        }

        static string ComponentOf(string dottedPkg, string owner)
        {
            var parts = dottedPkg.Split('.');
            // Microsoft.FactoryOS.DEVICELAYOUT_X.FOSGENDEV.FIP → FactoryOS.DEVICELAYOUT_X.FOSGENDEV
            var seg = parts.SkipWhile(p => p.Equals(owner, StringComparison.OrdinalIgnoreCase)).ToList();
            if (seg.Count > 0 && seg[^1] == "FIP") seg.RemoveAt(seg.Count - 1);
            if (seg.Count > 0 && seg[^1] == "Package") seg.RemoveAt(seg.Count - 1);
            return seg.Count > 0 ? string.Join(".", seg) : dottedPkg;
        }

        static string PhoneFm(string pkgId, DeviceLayoutBuildRequest req)
        {
            string relPath = $"Merged\\{req.Architecture}\\fre";
            string cab = pkgId + ".cab";
            string cpu = req.Architecture.ToUpperInvariant();
            return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<FeatureManifest xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns=""http://schemas.microsoft.com/embedded/2004/10/ImageUpdate"">
  <DeviceLayoutPackages>
    <PackageFile Path=""$(mspackageroot)\{relPath}"" Name=""{cab}"" CPUType=""{cpu}"" Partition=""{req.Partition}"" ID=""{pkgId}"" Version=""{req.Version}"" SOC=""{req.Soc}"" />
  </DeviceLayoutPackages>
  <SOCPackages>
    <PackageFile Path=""$(mspackageroot)\{relPath}"" Name=""{cab}"" FeatureIdentifierPackage=""true"" CPUType=""{cpu}"" Partition=""{req.Partition}"" ID=""{pkgId}"" Version=""{req.Version}"" SOC=""{req.Soc}"" />
  </SOCPackages>
  <Features><Microsoft>
    <PackageFile Path=""$(mspackageroot)\{relPath}"" Name=""{cab}"" Partition=""{req.Partition}"" ID=""{pkgId}"" Version=""{req.Version}"">
      <FeatureIDs><FeatureID>DeviceLayout_{req.Soc}</FeatureID></FeatureIDs>
    </PackageFile>
  </Microsoft></Features>
</FeatureManifest>
";
        }

        static string ModernFm(string fmid, string owner, string capability, string layoutComponent, string layoutPkg,
            string fip, DeviceLayoutBuildRequest req, string arch)
        {
            string cabDir = req.OutputDirectory; // 现代 FM 用绝对路径（样本即如此）
            string fipCabName = fip + $"~{TokenTestDefault}~{arch}~~.cab";
            return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<FeatureManifest xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" Revision=""1"" SchemaVersion=""1.3"" ReleaseType=""Production"" OwnerType=""{owner}"" ID=""{fmid}"" BuildInfo=""fe_release"" OwnerName=""{owner}"" OSVersion=""{req.Version}"" xmlns=""http://schemas.microsoft.com/embedded/2004/10/ImageUpdate"">
  <DeviceLayoutPackages>
    <PackageFile Path=""{cabDir}"" FeatureIdentifierPackage=""true"" ID=""{layoutComponent}"" SOC=""{req.Soc}"" />
    <PackageFile Path=""{cabDir}"" Name=""{fipCabName}"" ID=""{fip}"" SOC=""{req.Soc}"" />
  </DeviceLayoutPackages>
</FeatureManifest>
";
        }

        // ── 共用：mum 里 layoutRel 已是 {componentDir}\DeviceLayout.xml ──
        static string CdfText(string staging, string pkgId, params string[] relFiles)
        {
            string P(string rel) => $"<hash>\\\\?\\{staging}\\{rel}=\\\\?\\{staging}\\{rel}";
            var sb = new StringBuilder();
            sb.Append($@"[CatalogHeader]
Name=update.cat
ResultDir={staging}
PublicVersion=0x00000001
EncodingType=0x00010001
PageHashes=True
CatalogVersion=2
HashAlgorithms=SHA256
CATATTR1=0x00010001:OSAttr:2:10.0,2:6.3,2:6.2,2:6.1,2:6.0,2:5.2,2:5.1
CATATTR2=0x00010001:PackageName:{pkgId}
CATATTR3=0x00010001:PE:TRUSTED

[CatalogFiles]
");
            foreach (var f in relFiles) sb.AppendLine(P(f));
            return sb.ToString();
        }

        // CdfText 兼容旧 5 参调用
        static string CdfText(string staging, string pkgId, string layoutRel)
            => CdfText(staging, pkgId, new[] { layoutRel });

        static string Sha256Base64(string file) { using var sha = SHA256.Create(); return Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(file))); }
        static string Sha256Hex(string file) { using var sha = SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(file))); }
        static void WriteXml(string path, string body) => File.WriteAllText(path, "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\r\n" + body, new UTF8Encoding(false));
        static void Recreate(string dir) { if (Directory.Exists(dir)) Directory.Delete(dir, true); Directory.CreateDirectory(dir); }
        static bool ValidateIdentifier(string id) => !string.IsNullOrEmpty(id) && id.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-');

        // ── 外部工具 ──
        static bool RunMakecat(DeviceLayoutBuildRequest req, string cdfPath, string staging, DeviceLayoutBuildResult res)
        {
            string? exe = FindTool(req.MakecatPath, "makecat.exe");
            if (exe == null) { res.Log.Add("⚠ 未找到 makecat.exe（装 WDK/SDK 或指定路径）"); return false; }
            if (Run(exe, $"-v \"{cdfPath}\"", staging, 60_000, res) != 0) return false;
            return File.Exists(Path.Combine(staging, "update.cat"));
        }

        static bool RunCabarc(DeviceLayoutBuildRequest req, string staging, string cabPath, DeviceLayoutBuildResult res)
        {
            string? exe = FindTool(req.CabarcPath, "cabarc.exe");
            if (exe == null) return false;
            foreach (var leftover in Directory.GetFiles(staging, "*.cab")) { try { File.Delete(leftover); } catch { } }
            if (Run(exe, $"-p -r N \"{cabPath}\" *", staging, 120_000, res) != 0) return false;
            return File.Exists(cabPath);
        }

        /// <summary>
        /// 打包 CAB：先试 cabarc，再对产物做完整性校验；不合格（Phone 时代超长组件目录名会让
        /// cabarc 的 <c>-r</c> 递归静默跳过深层 DeviceLayout.xml 却仍返回 0）则回退长路径安全的托管引擎，
        /// 托管仍不合格才判失败。绝不产出缺布局/mum 的坏 CAB。
        /// </summary>
        static bool PackCab(DeviceLayoutBuildRequest req, string staging, string cabPath, DeviceLayoutBuildResult res,
            bool requireLayout)
        {
            string layoutName = requireLayout ? LayoutFileName(req) : "";
            if (RunCabarc(req, staging, cabPath, res))
            {
                if (CabIsComplete(cabPath, layoutName, out string why0)) return true;
                res.Log.Add("⚠ cabarc 产物完整性不足（" + why0 + "），改用托管引擎重打…");
            }
            if (CabManaged(staging, cabPath, res))
            {
                if (CabIsComplete(cabPath, layoutName, out string why1)) return true;
                res.Log.Add("❌ CAB 完整性校验失败：" + why1);
            }
            try { if (File.Exists(cabPath)) File.Delete(cabPath); } catch { }
            return false;
        }

        /// <summary>读 CAB 条目清单（托管 API，跨长路径安全），确认关键成员齐全。</summary>
        static bool CabIsComplete(string cabPath, string layoutName, out string reason)
        {
            reason = "";
            try
            {
                if (!File.Exists(cabPath)) { reason = "CAB 不存在"; return false; }
                var names = new Microsoft.Deployment.Compression.Cab.CabInfo(cabPath)
                    .GetFiles().Select(f => f.Name.Replace('\\', '/')).ToList();
                bool hasMum = names.Any(n => n.EndsWith("update.mum", StringComparison.OrdinalIgnoreCase));
                if (!hasMum) { reason = "缺 update.mum"; return false; }
                if (layoutName.Length > 0 && !names.Any(n => Path.GetFileName(n)
                        .Equals(layoutName, StringComparison.OrdinalIgnoreCase)))
                { reason = "缺 " + layoutName; return false; }
                return true;
            }
            catch (Exception ex) { reason = "枚举 CAB 失败: " + ex.Message; return false; }
        }

        static bool RunSigntool(DeviceLayoutBuildRequest req, string cabPath, DeviceLayoutBuildResult res)
        {
            string? exe = FindTool(req.SignToolPath, "signtool.exe");
            if (exe == null) { res.Log.Add("⚠ 未找到 signtool.exe，跳过签名"); return false; }
            string args = $"sign /fd SHA256 /f \"{req.SigningPfx}\"";
            if (!string.IsNullOrEmpty(req.SigningPfxPassword)) args += $" /p \"{req.SigningPfxPassword}\"";
            args += $" \"{cabPath}\"";
            bool ok = Run(exe, args, null, 60_000, res) == 0;
            res.Log.Add(ok ? "cab 已签名" : "⚠ 签名失败");
            return ok;
        }

        static int Run(string exe, string args, string? workDir, int timeoutMs, DeviceLayoutBuildResult res)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                if (workDir != null) psi.WorkingDirectory = workDir;
                using var p = Process.Start(psi)!;
                string outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                if (!p.WaitForExit(timeoutMs)) { res.Log.Add(Path.GetFileName(exe) + " 超时"); return -1; }
                if (!string.IsNullOrWhiteSpace(outp)) res.Log.Add($"{Path.GetFileName(exe)}: {outp.Trim()}");
                return p.ExitCode;
            }
            catch (Exception ex) { res.Log.Add($"{Path.GetFileName(exe)} 失败: {ex.Message}"); return -1; }
        }

        static string? FindTool(string? explicitPath, string fileName)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)) return explicitPath;
            string[] roots = { @"E:\wcos\Tool", @"E:\Assessment and Deployment Kit\build_tools", @"F:\Program Files", @"C:\Program Files (x86)\Windows Kits\10\bin" };
            foreach (var r in roots)
            {
                if (!Directory.Exists(r)) continue;
                try { var hit = Directory.EnumerateFiles(r, fileName, SearchOption.AllDirectories).FirstOrDefault(); if (hit != null) return hit; }
                catch { }
            }
            return null;
        }

        static bool CabManaged(string staging, string cabPath, DeviceLayoutBuildResult res)
        {
            try
            {
                if (File.Exists(cabPath)) File.Delete(cabPath);
                var files = Directory.GetFiles(staging, "*", SearchOption.AllDirectories);
                var info = new Microsoft.Deployment.Compression.Cab.CabInfo(cabPath);
                info.PackFiles(staging, files.Select(f => f.Substring(staging.Length + 1)).ToArray(), null,
                    Microsoft.Deployment.Compression.CompressionLevel.Normal, null);
                res.Log.Add("已用托管引擎打包 CAB（cabarc 兜底）");
                return File.Exists(cabPath);
            }
            catch (Exception ex) { res.Log.Add("托管打包失败: " + ex.Message); return false; }
        }
    }
}
