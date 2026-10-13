using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Schema;
using OneCore_Image_Designer.Models;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// OEMInput 生成 / 加载 / 校验服务。
    ///
    /// 2026-09-13 重写要点：
    ///   · 序列化交给 <see cref="OemInputSerializer"/>（V1/V2 双 DTO），
    ///     去掉旧的「XmlSerializer + 字符串 Replace」——那种做法一旦序列化器
    ///     输出格式微调就会静默失效。
    ///   · 校验升级为两层：① 官方嵌入 XSD 真实校验（带命名空间前置比对，
    ///     规避 .NET「文档命名空间不在 schema 集内即不报错」的假通过陷阱）；
    ///     ② XSD 表达不了的语义规则（枚举大小写、TestContent 仅 Test、
    ///     Feature 无空格、语言唯一默认等，均来自 featureapi 反编译实证）。
    ///   · Validate 仍返回 List&lt;string&gt;（保持既有调用方契约），
    ///     另提供 <see cref="ValidateDetailed"/> 返回结构化结果供界面做内联汇总。
    /// </summary>
    public class OEMInputService
    {
        /// <summary>生成 OEMInput XML（按 doc.SchemaVersion 选 V1/V2）。</summary>
        public static string GenerateXml(OEMInputDoc doc)
            => OemInputSerializer.Serialize(doc);

        /// <summary>保存到文件（UTF-8 无 BOM，与真实手写样本一致）。</summary>
        public static bool SaveToFile(OEMInputDoc doc, string filePath)
        {
            try
            {
                File.WriteAllText(filePath, GenerateXml(doc), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                LastSaveError = ex.Message;
                return false;
            }
        }

        /// <summary>最近一次保存失败原因（供 UI 提示）。</summary>
        public static string LastSaveError { get; private set; } = string.Empty;

        /// <summary>从文件加载（自动按根命名空间探测 V1/V2）。</summary>
        public static OEMInputDoc? LoadFromFile(string filePath)
        {
            try
            {
                string xml = File.ReadAllText(filePath);
                return OemInputSerializer.Deserialize(xml);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or InvalidOperationException)
            {
                return null;
            }
        }

        // ══════════════════════ 校验 ══════════════════════

        /// <summary>校验（保持旧契约：错误字符串列表）。</summary>
        public static List<string> Validate(OEMInputDoc doc)
            => ValidateDetailed(doc).Errors;

        /// <summary>结构化校验结果。</summary>
        public class ValidationReport
        {
            public List<string> Errors { get; } = new();
            public List<string> Warnings { get; } = new();
            public OemInputSchemaVersion SchemaVersion { get; set; }
            public bool SchemaValid { get; set; } = true;   // XSD 层是否通过
        }

        /// <summary>
        /// 两层校验：语义规则 + 官方 XSD。语义规则先行（错误信息更友好），
        /// 再跑 XSD 结构校验兜住字段非法/枚举不符等。
        /// </summary>
        public static ValidationReport ValidateDetailed(OEMInputDoc doc)
        {
            var r = new ValidationReport { SchemaVersion = doc.SchemaVersion };

            // ── 第一层：语义规则（featureapi 反编译实证）──
            if (string.IsNullOrWhiteSpace(doc.SOC))
                r.Errors.Add(doc.IsSchemaV2 ? "DeviceLayoutType 不能为空" : "SOC 不能为空");
            if (string.IsNullOrWhiteSpace(doc.Device))
                r.Errors.Add("Device 不能为空");
            if (string.IsNullOrWhiteSpace(doc.ReleaseType))
                r.Errors.Add("ReleaseType 不能为空");
            else if (doc.ReleaseType != "Test" && doc.ReleaseType != "Production")
                r.Errors.Add($"ReleaseType 只能是 Test 或 Production（当前：{doc.ReleaseType}）");

            if (string.IsNullOrWhiteSpace(doc.BuildType))
                r.Errors.Add("BuildType 不能为空");
            else if (doc.BuildType != "fre" && doc.BuildType != "chk" && doc.BuildType != "%BUILDTYPE%")
                r.Errors.Add($"BuildType 只能是 fre / chk / %BUILDTYPE%（当前：{doc.BuildType}）");

            if (doc.AdditionalFMs.Count == 0)
                r.Errors.Add("AdditionalFMs 至少需要一个 FM 文件");

            // 语言：V2 校验扁平表，V1 校验 SupportedLanguages + BootUILanguage
            if (doc.IsSchemaV2)
            {
                var langs = doc.Languages.Languages;
                if (langs.Count == 0)
                    r.Errors.Add("Languages 至少需要一个语言");
                int defCount = langs.Count(l => l.IsDefault);
                if (defCount != 1)
                    r.Errors.Add($"Languages 必须有且仅有一个 Default=true（当前 {defCount} 个）");
                foreach (var lang in langs)
                    if (IsBadLanguageCode(lang.Code))
                        r.Errors.Add($"语言代码格式不正确: {lang.Code}");
            }
            else // V1
            {
                var ui = doc.SupportedLanguages.UserInterface;
                if (ui.Count == 0)
                    r.Errors.Add("SupportedLanguages.UserInterface 至少需要一个语言");
                if (string.IsNullOrWhiteSpace(doc.BootUILanguage))
                    r.Errors.Add("BootUILanguage 不能为空");
                else if (ui.Count > 0 && !ui.Any(x => string.Equals(x, doc.BootUILanguage, StringComparison.OrdinalIgnoreCase)))
                    r.Errors.Add($"BootUILanguage ({doc.BootUILanguage}) 必须是 UserInterface 列表中的语言之一");
                foreach (var lang in ui.Concat(doc.SupportedLanguages.Keyboard).Concat(doc.SupportedLanguages.Speech))
                    if (IsBadLanguageCode(lang))
                        r.Errors.Add($"语言代码格式不正确: {lang}");
            }

            // TestContent 仅 Test（官方 OEMInput.cs:581-588）
            if (!string.IsNullOrWhiteSpace(doc.TestContent) && doc.ReleaseType != "Test")
                r.Errors.Add("TestContent 仅当 ReleaseType=Test 时才能填写");

            // Feature 名不得含首尾空格（官方 ValidateFeatureList）
            foreach (var f in doc.Features.MicrosoftFeatures.Concat(doc.Features.OEMFeatures))
                if (f != f.Trim())
                    r.Errors.Add($"Feature 名含首尾空格: \"{f}\"");

            // FM 文件存在性 → 降级为警告（迁移机器时路径常失效，不应阻断）
            foreach (var fm in doc.AdditionalFMs)
                if (!File.Exists(fm))
                    r.Warnings.Add($"FM 文件当前不存在: {fm}");

            if (string.IsNullOrWhiteSpace(doc.Product))
                r.Warnings.Add($"Product 为空，将写入默认值 {OEMInputDoc.DefaultProductName}");

            // ── 第 1.5 层：时代门控（目标 build 早于字段出现时代时提示）──
            foreach (var w in OemInputEraRulesService.CheckEra(doc))
                r.Warnings.Add(w);

            // ── 第二层：官方 XSD 结构校验（按 TargetBuild 选 schema 家族）──
            try
            {
                string xml = GenerateXml(doc);
                (bool ok, List<string> xsdErrs) = ValidateAgainstXsd(xml, doc.SchemaVersion, doc.TargetBuild);
                r.SchemaValid = ok;
                if (!ok)
                    foreach (var e in xsdErrs) r.Errors.Add("[XSD] " + e);
            }
            catch (Exception ex) when (ex is InvalidOperationException or XmlException)
            {
                r.SchemaValid = false;
                r.Errors.Add("[XSD] 序列化/校验异常: " + ex.Message);
            }

            return r;
        }

        private static bool IsBadLanguageCode(string code)
            => !Regex.IsMatch(code ?? "", "^[a-zA-Z]{2,3}(-[a-zA-Z0-9]{2,8}){1,3}$");

        // ══════════════════════ XSD 加载与校验 ══════════════════════

        /// <summary>
        /// 用官方嵌入 XSD 校验 XML。关键：先比对根元素命名空间与目标版本，
        /// 不一致直接判失败——否则 .NET 的 XmlSchemaSet 对「命名空间不在集合内」
        /// 会静默通过（假 PASS，实测踩过）。
        /// build&gt;0 时按该 build 选择对应时代的 schema 家族（见 OemInputSchemaFamilyService）。
        /// </summary>
        public static (bool ok, List<string> errors) ValidateAgainstXsd(
            string xml, OemInputSchemaVersion version, int build = 0)
        {
            var errors = new List<string>();
            string wantNs = version == OemInputSchemaVersion.V2_2019_06
                ? OEMInputDoc.NamespaceV2 : OEMInputDoc.NamespaceV1;

            // 1) 根命名空间必须与所选版本一致
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore };
                using (var r = XmlReader.Create(new StringReader(xml), settings))
                {
                    while (r.Read())
                        if (r.NodeType == XmlNodeType.Element)
                            break;
                    string actualNs = r.NamespaceURI;
                    if (!string.Equals(actualNs, wantNs, StringComparison.Ordinal))
                    {
                        errors.Add($"根元素命名空间({actualNs}) 与所选 schema 版本({version}) 期望的({wantNs}) 不符");
                        return (false, errors);
                    }
                }
            }
            catch (XmlException ex)
            {
                errors.Add("XML 语法错误: " + ex.Message);
                return (false, errors);
            }

            // 2) 载入对应 XSD 做结构校验（按 build 选家族，缺失回退基线）
            Stream? xsdStream = OemInputSchemaFamilyService.OpenSchemaStream(build, version);
            if (xsdStream is null)
            {
                // 资源缺失不应让校验整体失败——降级为警告级
                errors.Add("未找到嵌入的 OEMInput XSD，跳过结构校验（仅语义校验）");
                return (true, errors);
            }

            try
            {
                var set = new XmlSchemaSet { XmlResolver = null };
                using (xsdStream)
                using (var xsdReader = XmlReader.Create(xsdStream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore }))
                    set.Add(null, xsdReader);

                var vsettings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Ignore,
                    ValidationType = ValidationType.Schema,
                    Schemas = set
                };
                vsettings.ValidationEventHandler += (s, e) =>
                {
                    string m = e.Message;
                    if (m.Length > 200) m = m[..200] + "…";
                    errors.Add(m);
                };

                using (var r = XmlReader.Create(new StringReader(xml), vsettings))
                    while (r.Read()) { }
            }
            catch (XmlSchemaException ex)
            {
                errors.Add("XSD 加载失败: " + ex.Message);
                return (false, errors);
            }
            catch (XmlException ex)
            {
                errors.Add("XML 校验异常: " + ex.Message);
                return (false, errors);
            }

            return (errors.Count == 0, errors);
        }

        public static string GenerateDescription(OEMInputDoc doc)
            => $"{doc.Device} {doc.BuildType} generated by OneCore Image Designer at {DateTime.Now:yyyyMMdd-HHmm}";
    }
}
