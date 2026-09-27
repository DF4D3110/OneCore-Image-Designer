using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// 设备布局模板：一份经过官方样本实证的 DeviceLayout.xml，附带来源版本与适用设备类型，
    /// 供「设备布局创建」一键载入，避免用户从零手敲分区表导致校验不过。
    ///
    /// 存储位置：
    ///  · 内置模板 —— 程序目录 Assets\Templates\{目录名}\（DeviceLayout.xml + template.json）
    ///  · 自定义模板 —— %LOCALAPPDATA%\OneCoreImageDesigner\CustomTemplates\{目录名}\（同结构）
    /// </summary>
    public class DeviceLayoutTemplateInfo
    {
        public string Key { get; set; } = string.Empty;        // 目录名（唯一标识）
        public string Title { get; set; } = string.Empty;      // 界面显示名（中文）
        public string DeviceType { get; set; } = string.Empty; // 适用设备类型
        public string Architecture { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;     // 来源版本（必须写明）
        public string Era { get; set; } = "Modern";            // Modern / Phone
        public string Notes { get; set; } = string.Empty;
        public bool Custom { get; set; }                        // 是否用户自定义
        /// <summary>
        /// 绑定的官方校验规则（"时代/文件名"）。空串 = 官方对该场景无适用规则
        /// （如 VMGen1 被 Intel 规则 ExcludedScopes 显式排除），或无法精确证实。
        /// 绑定值全部经「官方模板 × 官方规则」全矩阵探针实测：非空即该组合 0 错误或差异已知。
        /// </summary>
        public string DefaultRule { get; set; } = string.Empty;

        [JsonIgnore]
        public bool HasDefaultRule => DefaultRule.Contains('/');

        /// <summary>解析 DefaultRule 为绝对路径；无绑定返回 null。</summary>
        public string? ResolveRulePath()
        {
            if (!HasDefaultRule) return null;
            var parts = DefaultRule.Split('/', 2);
            string p = Path.Combine(DeviceLayoutRuleChecker.RulesRoot, parts[0], parts[1]);
            return File.Exists(p) ? p : null;
        }

        [JsonIgnore]
        public string Directory { get; set; } = string.Empty;

        [JsonIgnore]
        public string LayoutXml => Path.Combine(Directory, "DeviceLayout.xml");

        [JsonIgnore]
        public string MetaJson => Path.Combine(Directory, "template.json");

        public string DisplayName => Custom
            ? $"[自定义] {Title}"
            : $"{Title}  ·  {DeviceType}  ·  {Source}";
    }

    /// <summary>
    /// 模板库。内置模板元数据在这里硬编码（与磁盘上的目录一一对应），
    /// 磁盘缺文件时该模板不列出，防止把半成品模板暴露给用户。
    /// </summary>
    public static class DeviceLayoutTemplates
    {
        public static string BuiltInRoot =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Templates");

        public static string CustomRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OneCoreImageDesigner", "CustomTemplates");

        /// <summary>内置模板元数据。来源全部为官方样本实证，Key 必须与 Assets\Templates 下目录同名。
        /// DefaultRule 经「官方模板 × 全部官方规则」全矩阵探针实测得出：
        ///  · 非空 = 已证实的匹配规则（官方模板本身 0 错，或差异仅作回归基线）
        ///  · 空   = 无适用手机规则（GenericGPT/VMGen1/Spaces 属 ModernPC 布局，
        ///           不被这批 WindowsMobile DeviceLayoutValidation 规则覆盖，VMGen1 更被
        ///           Intel 规则 ExcludedScopes 显式排除），仅做基础结构检查。
        /// </summary>
        static readonly List<DeviceLayoutTemplateInfo> BuiltInMeta = new()
        {
            new() { Key = "gpt_x86",          Title = "GenericGPT 标准布局",        DeviceType = "通用 PC（UEFI）",   Architecture = "x86",   Source = "10.0.18941.1001.rs_prerelease.190713-1700",   Era = "Modern",
                    Notes = "x86 出厂默认布局：EFIESP+MainOS+BSP+PreInstalled+OSData+Data，GPT 分区类型 GUID 按 Windows 可寻址空间私有前缀（57434f53-*）编排。属 ModernPC 布局，无对应手机规则，仅基础结构检查。" },
            new() { Key = "gpt_amd64",        Title = "GenericGPT 标准布局",        DeviceType = "通用 PC（UEFI）",   Architecture = "amd64", Source = "10.0.18941.1001.rs_prerelease.190713-1700",   Era = "Modern" },
            new() { Key = "gpt_arm",          Title = "GenericGPT 标准布局（arm 移植）", DeviceType = "arm 通用（基于amd64，非实体手机）", Architecture = "arm",   Source = "10.0.18941.1001.rs_prerelease.190713-1700",   Era = "Modern",
                    Notes = "arm/arm64 没有专用手机版通用布局，此包从 amd64 Generic 移植，单盘平铺。" },
            new() { Key = "gpt_arm64",        Title = "GenericGPT 标准布局（arm64）", DeviceType = "arm64 通用（非实体手机）", Architecture = "arm64", Source = "10.0.20279.1002.fe_release_10x.201214-1532", Era = "Modern",
                    Notes = "源目录名为 18941DeviceLayoutUEFIARM64，但包内 FM 实为 20279 fe_release；来源以包内权威 BuildInfo 为准。" },
            new() { Key = "gptspace512_x86",  Title = "GenericGPT-Spaces (512B 扇区)", DeviceType = "虚拟机 / ModernPC（存储池）", Architecture = "x86",   Source = "10.0.18941.1001.rs_prerelease.190713-1700", Era = "Modern",
                    Notes = "WSK 镜像创建流程的默认虚拟机布局：OSPool 存储池（每分区独占一个虚拟磁盘）+ Default 盘上的 BS_EFIESP 与 OSPool 容器分区。存为存储池（Spaces）形状时最接近 IoTUAP v2 规则（差 EFIESP 命名），属 ModernPC，仅基础结构检查。" },
            new() { Key = "gptspace512_amd64",Title = "GenericGPT-Spaces (512B 扇区)", DeviceType = "虚拟机 / ModernPC（存储池）", Architecture = "amd64", Source = "10.0.18941.1001.rs_prerelease.190713-1700", Era = "Modern" },
            new() { Key = "gptspace4096_x86", Title = "GenericGPT-Spaces 4096B（存储池）", DeviceType = "虚拟机 / ModernPC（4K扇区存储池）", Architecture = "x86", Source = "10.0.18941.1001.rs_prerelease.190713-1700", Era = "Modern",
                    Notes = "同 512B 版但 SectorSize=4096（4K 原生盘）。" },
            new() { Key = "gptspace4096_amd64",Title = "GenericGPT-Spaces 4096B（存储池）", DeviceType = "虚拟机 / ModernPC（4K扇区存储池）", Architecture = "amd64", Source = "10.0.18941.1001.rs_prerelease.190713-1700", Era = "Modern" },
            new() { Key = "spacesplit_arm",   Title = "GenericGPT-SpaceSplit（MS布局/字节制）", DeviceType = "arm 通用（存储池·拆分盘，基于amd64）", Architecture = "arm", Source = "10.0.18941.1001.rs_prerelease.190713-1700", Era = "Modern",
                    Notes = "SpaceSplit 变体：根标签 MSDeviceLayout、字节制（SizeInBytes/TotalBytes，无 SectorSize 属正常），池外 Default 盘多一个 OPP 分区。造包时按官方以 DeviceLayoutMS.xml 进包。" },
            new() { Key = "spacesplit_arm64", Title = "GenericGPT-SpaceSplit（MS布局/字节制）", DeviceType = "arm64 通用（存储池·拆分盘）", Architecture = "arm64", Source = "10.0.20279.1002.fe_release_10x.201214-1532", Era = "Modern" },
            new() { Key = "vmgen1_x86",       Title = "VMGen1 虚拟机布局",          DeviceType = "虚拟机（VHDX 直写）",  Architecture = "x86",   Source = "10.0.18941.1001.rs_prerelease.190713-1700",   Era = "Modern",
                    Notes = "旧式单盘 VM 布局：EFIESP/DPP/BP 固定扇区 + MainOS/BSP 最小空闲 + PreInstalled 固定 + OSData/Data 弹性。用 MBR 类型字节（0xEF/0x2C…），官方 Intel 规则以 ExcludedScopes 显式排除 VM，故无手机规则适用，仅基础结构检查。" },
            new() { Key = "vmgen1_amd64",     Title = "VMGen1 虚拟机布局",          DeviceType = "虚拟机（VHDX 直写）",  Architecture = "amd64", Source = "10.0.18941.1001.rs_prerelease.190713-1700",   Era = "Modern" },
            new() { Key = "nokia_arm32_14355",Title = "Nokia MainOS 测试布局 (Phone)", DeviceType = "arm32 Phone 实体机（早期 WPAK）", Architecture = "arm", Source = "10.0.14355.1000.rs1_release.160525-1714", Era = "Phone",
                    DefaultRule = "15032/Microsoft.ARM_Qualcomm_Test.DeviceLayoutValidation.xml",
                    Notes = "Phone 时代参考：点分包 ID、publicKeyToken 6288、文件落在 windows\\ImageUpdate。官方模板对 QC 测试规则 0 错，可硬拦截。生成时请切“早期 Phone”时代。" },
            new() { Key = "fakeos_arm32",     Title = "FakeOS SOC 测试布局 (arm32)", DeviceType = "微软测试布局（arm32，可用于虚拟机）", Architecture = "arm", Source = "10.0.14974.1000.rs_prerelease.161115-1700", Era = "Phone",
                    DefaultRule = "",
                    Notes = "微软 DEVICELAYOUT_FAKE_SOC_TEST：单盘平铺 4 分区（EFIESP/DPP/MainOS/Data）。arm32 才有 FakeOS；属 MS 测试设备布局，可用于虚拟机。官方 QC 规则以 ExcludedScopes（arm.*:qualcomm:fake.*）显式排除 Fake SOC，故不绑定手机规则，仅结构自检。" },
            new() { Key = "qc8998_15032",     Title = "QC8998 DeviceLayout_Test (arm64)", DeviceType = "开发板（QC8998 实体机，多LUN非VM）", Architecture = "arm64", Source = "10.0.15032.1009.rs_onecore_sigma_oc_and.170221-1600", Era = "Modern",
                    DefaultRule = "15032/Microsoft.ARM_Qualcomm_Test.DeviceLayoutValidation.xml",
                    Notes = "多 LUN UFS 结构（XBL/MainOSStore 等），4K 扇区。官方自身对当年规则有少量已知差异（XBL 备份类型、MainOS 簇 0x4000），校验采用回归基线：只拦你改出来的新违规。" },
            new() { Key = "qc8998_16202",     Title = "QC8998 DeviceLayout_Test (arm64)", DeviceType = "开发板（QC8998 实体机，多LUN非VM）", Architecture = "arm64", Source = "10.0.16202.1035.rs_devices_dev01mfg.170724-2311",  Era = "Modern",
                    DefaultRule = "16202/Microsoft.ARM_Qualcomm_Test.DeviceLayoutValidation.xml" },
            new() { Key = "sdm845_17047",     Title = "SDM845 StateSep-Test (arm64)", DeviceType = "开发板（SD845 状态隔离）", Architecture = "arm64", Source = "10.0.17047.1034.rs_devices_dev01mfg.180118-2131",  Era = "Modern",
                    DefaultRule = "17047/Microsoft.ARM_Qualcomm_Test.DeviceLayoutValidation.xml",
                    Notes = "含状态隔离（StateSeparationLevel），分区更多（48 个），可当 QC845 类设备起点。校验采用回归基线。" },
            new() { Key = "qc850_18259",      Title = "SDM845 StateSep-Space-Test (arm64)", DeviceType = "开发板（Andromeda/SD845 存储池）", Architecture = "arm64", Source = "10.0.18259.1002.rs_onecore_stack_mc5.181010-1731", Era = "Modern",
                    DefaultRule = "18941/Microsoft.ARM_Qualcomm.Andromeda.DeviceLayoutValidationv2.xml",
                    Notes = "官方布局包 Microsoft-OneCore-SDM845-DeviceLayout-StateSep-Space-Test-Package：StoragePools + 多 LUN，53 分区。对 Andromeda v2 规则 0 错，可硬拦截。" },
            new() { Key = "talkman_arm32_18968", Title = "Talkman-Space (arm32 Lumia 950)", DeviceType = "实体手机（Talkman = Lumia 950）", Architecture = "arm", Source = "10.0.18948.1001.rs_apps_validate.190726-1700", Era = "Modern",
                    DefaultRule = "18941/Microsoft.ARM_Qualcomm.Talkman.DeviceLayoutValidationv2.xml",
                    Notes = "官方包 Microsoft-OneCore-Talkman-Space-DeviceLayout-Package（arm），包内权威 BuildInfo 为 18948 rs_apps_validate（源工作区目录名 18968 只是工作区号，非产品 build）。Talkman 是 Lumia 950 实体手机（非 VM）：OSPool 存储池 + SV 盘，46 分区。对 Talkman v2 规则 0 错，可硬拦截。" },

            // ── 22000（co_release）独有：Ramboot 拆分盘与 Fixed 变体（MS 字节制）────────
            new() { Key = "ramboot_pre_oobe",  Title = "Ramboot Pre-OOBE Split（22000）", DeviceType = "Ramboot 虚拟机（OOBE 前，池+Ramdisk）", Architecture = "amd64", Source = "10.0.22000.1.co_release.210604-1628", Era = "Modern",
                    Notes = "Microsoft-OneCore-RambootPreOobeSplitDeviceLayout-Package：MSDeviceLayout 字节制，OSPool 池（无 BSP 盘）+ 池外 Ramdisk（BS_EFIESP + OSPool 容器）。Ramboot 启动的 OOBE 前形态。" },
            new() { Key = "ramboot_post_oobe", Title = "Ramboot Post-OOBE Split（22000）", DeviceType = "Ramboot 虚拟机（OOBE 后，池+Ramdisk）", Architecture = "amd64", Source = "10.0.22000.1.co_release.210604-1628", Era = "Modern",
                    Notes = "Microsoft-OneCore-RambootPostOobeSplitDeviceLayout-Package：比 Pre 形态多一个分区（OOBE 后 Data 类池盘）。" },
            new() { Key = "gptspace512_fixed_amd64", Title = "GenericGPT-Spaces 512B Fixed（22000）", DeviceType = "ModernPC（存储池·固定大小）", Architecture = "amd64", Source = "10.0.22000.1.co_release.210604-1628", Era = "Modern",
                    Notes = "Microsoft-OneCore-GenericGPTSpaceDeviceLayout512-Fixed-Package：池内各盘 UseAllSpace=false 的固定大小变体（22000 新增）。" },
        };

        public static List<DeviceLayoutTemplateInfo> ListAll()
        {
            var list = new List<DeviceLayoutTemplateInfo>();
            foreach (var meta in BuiltInMeta)
            {
                meta.Directory = Path.Combine(BuiltInRoot, meta.Key);
                meta.Custom = false;
                if (File.Exists(meta.LayoutXml)) list.Add(meta);
            }
            list.AddRange(ListCustom());
            return list;
        }

        static List<DeviceLayoutTemplateInfo> ListCustom()
        {
            var result = new List<DeviceLayoutTemplateInfo>();
            if (!Directory.Exists(CustomRoot)) return result;
            foreach (var dir in Directory.EnumerateDirectories(CustomRoot))
            {
                string key = Path.GetFileName(dir);
                if (!File.Exists(Path.Combine(dir, "DeviceLayout.xml"))) continue;
                DeviceLayoutTemplateInfo? info = null;
                string metaPath = Path.Combine(dir, "template.json");
                try
                {
                    if (File.Exists(metaPath))
                        info = JsonSerializer.Deserialize<DeviceLayoutTemplateInfo>(
                            File.ReadAllText(metaPath, Encoding.UTF8));
                }
                catch { /* 元数据坏了也照样列出，用目录名兜底 */ }
                info ??= new DeviceLayoutTemplateInfo { Title = key, DeviceType = "自定义", Source = "用户保存" };
                info.Key = key;
                info.Directory = dir;
                info.Custom = true;
                result.Add(info);
            }
            return result;
        }

        static readonly Regex SafeKey = new("^[A-Za-z0-9][A-Za-z0-9_.\\-]{0,60}$", RegexOptions.Compiled);

        /// <summary>
        /// 把当前编辑器里的布局存为自定义模板。XML 以「原形状」序列化并美化缩进。
        /// </summary>
        public static DeviceLayoutTemplateInfo SaveCustom(
            Models.DeviceLayout.DeviceLayoutDoc doc, bool isV2,
            DeviceLayoutTemplateInfo meta, Action<Models.DeviceLayout.DeviceLayoutDoc>? adjustForSave = null)
        {
            string key = SanitizeKey(string.IsNullOrWhiteSpace(meta.Key) ? meta.Title : meta.Key);
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("模板标识（目录名）不能为空");

            // 内置与自定义同名时，给自定义加后缀，绝不碰内置目录
            if (BuiltInMeta.Any(b => string.Equals(b.Key, key, StringComparison.OrdinalIgnoreCase)))
                key += "_custom";

            string dir = Path.Combine(CustomRoot, key);
            Directory.CreateDirectory(dir);

            var snapshot = doc;
            adjustForSave?.Invoke(snapshot);
            DeviceLayoutService.Serialize(snapshot, Path.Combine(dir, "DeviceLayout.xml"), isV2);

            meta.Key = key;
            meta.Custom = true;
            meta.Directory = dir;
            var json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(meta.MetaJson, json, Encoding.UTF8);
            return meta;
        }

        public static void DeleteCustom(string key)
        {
            string dir = Path.Combine(CustomRoot, SanitizeKey(key));
            if (Directory.Exists(dir))
                Directory.Delete(dir, true); // 只可能落在 CustomRoot 下（key 经白名单过滤）
        }

        static string SanitizeKey(string raw)
        {
            raw = (raw ?? string.Empty).Trim();
            if (SafeKey.IsMatch(raw)) return raw;
            var sb = new StringBuilder();
            foreach (char c in raw)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' ? c : '_');
            string s = sb.ToString().Trim('_');
            return s.Length == 0 ? "template_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") : s;
        }
    }
}
