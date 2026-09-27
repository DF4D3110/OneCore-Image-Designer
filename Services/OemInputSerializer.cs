using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using OneCore_Image_Designer.Models;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// OEMInput 双版本（V1 2004/10 / V2 2019/06）XML 读写。
    ///
    /// 为什么手写读写而不用 XmlSerializer：
    ///   · 官方 XSD 的根类型是 <c>xs:all</c> —— 子元素顺序任意。真实手写样本
    ///     （16202 input、17047 等）的顺序各不相同；XmlSerializer 一旦用 Order
    ///     就是顺序敏感的 sequence，乱序文件解析会整片读空（实测踩过）；不用 Order
    ///     又无法控制输出顺序与嵌套命名空间（子类型默认 xmlns=""，同样是坑）。
    ///   · 手写 XDocument：读取按元素名+命名空间精确匹配（顺序无关），
    ///     写出按官方样本顺序，命名空间统一继承根默认 ns。确定、可验证。
    ///   · 命名空间是版本的唯一判据（官方 OEMInputFactory 即按 ns 分流，
    ///     失败回退）；两版字段集在写入侧用两套独立逻辑，杜绝 2019/06 里
    ///     混进 SOC/BootUILanguage 这类旧实现的致命错配。
    /// </summary>
    public static class OemInputSerializer
    {
        // ══════════════════════ 写 ══════════════════════

        public static string Serialize(OEMInputDoc doc)
            => doc.IsSchemaV2 ? SerializeV2(doc) : SerializeV1(doc);

        /// <summary>V1：SOC + SupportedLanguages 三分组 + BootUILanguage/BootLocale + Resolutions。</summary>
        private static string SerializeV1(OEMInputDoc doc)
        {
            XNamespace ns = OEMInputDoc.NamespaceV1;
            var root = new XElement(ns + "OEMInput");

            Add(root, ns, "Description", doc.Description);
            Add(root, ns, "Product", string.IsNullOrWhiteSpace(doc.Product) ? OEMInputDoc.DefaultProductName : doc.Product);
            Add(root, ns, "SV", doc.SV);
            Add(root, ns, "SOC", doc.SOC);
            Add(root, ns, "Device", doc.Device);
            Add(root, ns, "ReleaseType", doc.ReleaseType);
            Add(root, ns, "BuildType", doc.BuildType);

            var ui = Clean(doc.SupportedLanguages.UserInterface);
            var kb = Clean(doc.SupportedLanguages.Keyboard);
            var sp = Clean(doc.SupportedLanguages.Speech);
            // 兜底：用户只填了扁平 Languages（V2 习惯）时，派生 V1 三分组
            if (ui.Count == 0 && doc.Languages.Languages.Count > 0)
            {
                ui = doc.Languages.Languages.Select(l => l.Code.Trim()).Where(s => s.Length > 0).Distinct().ToList();
                kb = new List<string>(ui);
                sp = new List<string>(ui);
            }
            if (ui.Count + kb.Count + sp.Count > 0)
            {
                var sl = new XElement(ns + "SupportedLanguages");
                AddGroup(sl, ns, "UserInterface", ui);
                AddGroup(sl, ns, "Keyboard", kb);
                AddGroup(sl, ns, "Speech", sp);
                root.Add(sl);
            }

            Add(root, ns, "BootUILanguage", doc.BootUILanguage);
            Add(root, ns, "BootLocale", doc.BootLocale);
            AddGroup(root, ns, "Resolutions", "Resolution", Clean(doc.Resolutions));
            AddTestContent(doc, root, ns);
            Add(root, ns, "MachineInfoFile", doc.MachineInfoFile);

            AddCommonLists(doc, root, ns);

            return Render(root);
        }

        /// <summary>V2：DeviceLayoutType + 扁平 Languages(Default)，无 SOC/BootUI/Resolutions。</summary>
        private static string SerializeV2(OEMInputDoc doc)
        {
            XNamespace ns = OEMInputDoc.NamespaceV2;
            var root = new XElement(ns + "OEMInput");

            Add(root, ns, "Description", doc.Description);
            Add(root, ns, "DeviceLayoutType", doc.SOC);      // SOC 的值 → DeviceLayoutType 元素
            Add(root, ns, "SV", doc.SV);
            Add(root, ns, "Device", doc.Device);
            Add(root, ns, "ReleaseType", doc.ReleaseType);
            Add(root, ns, "BuildType", doc.BuildType);

            if (doc.Languages.Languages.Count > 0)
            {
                var langs = new XElement(ns + "Languages");
                foreach (var l in doc.Languages.Languages.Where(x => !string.IsNullOrWhiteSpace(x.Code)))
                {
                    langs.Add(new XElement(ns + "Language",
                        new XAttribute("Default", l.IsDefault ? "true" : "false"),
                        l.Code.Trim()));
                }
                if (langs.HasElements) root.Add(langs);
            }

            AddTestContent(doc, root, ns);
            Add(root, ns, "RecoveryPackagePath", doc.RecoveryPackagePath);
            Add(root, ns, "ImageVersionString", doc.ImageVersionString);
            Add(root, ns, "MachineInfoFile", doc.MachineInfoFile);

            AddCommonLists(doc, root, ns);

            if (doc.DevicePlatformIDs.Count > 0)
                AddGroup(root, ns, "DevicePlatformIDs", "DevicePlatformID", Clean(doc.DevicePlatformIDs));

            if (doc.ExcludePrereleaseFeatures)
                root.Add(new XElement(ns + "ExcludePrereleaseFeatures", "true"));

            Add(root, ns, "Product", string.IsNullOrWhiteSpace(doc.Product) ? OEMInputDoc.DefaultProductName : doc.Product);

            return Render(root);
        }

        private static void AddCommonLists(OEMInputDoc doc, XElement root, XNamespace ns)
        {
            if (doc.AdditionalFMs.Count > 0)
                root.Add(new XElement(ns + "AdditionalFMs",
                    doc.AdditionalFMs.Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => new XElement(ns + "AdditionalFM", s.Trim()))));

            var features = new XElement(ns + "Features");
            var ms = Clean(doc.Features.MicrosoftFeatures);
            var oem = Clean(doc.Features.OEMFeatures);
            if (ms.Count > 0)
                features.Add(new XElement(ns + "Microsoft", ms.Select(x => new XElement(ns + "Feature", x))));
            if (oem.Count > 0)
                features.Add(new XElement(ns + "OEM", oem.Select(x => new XElement(ns + "Feature", x))));
            else if (doc.KeepEmptyOemSection)
                features.Add(new XElement(ns + "OEM"));
            if (features.HasElements) root.Add(features);

            if (doc.AppXOptionalPackages.Count > 0)
                root.Add(new XElement(ns + "AppXOptionalPackages",
                    Clean(doc.AppXOptionalPackages).Select(x => new XElement(ns + "AppXID", x))));
            else if (doc.KeepEmptyAppXSection)
                root.Add(new XElement(ns + "AppXOptionalPackages"));

            AddGroup(root, ns, "UserInstallableFeatures", "Feature", Clean(doc.UserInstallableFeatures));
            AddGroup(root, ns, "AdditionalSKUs", "AdditionalSKU", Clean(doc.AdditionalSKUs));
            AddGroup(root, ns, "InternalOptionalFeatures", "OptionalFeature", Clean(doc.InternalOptionalFeatures));
            AddGroup(root, ns, "ProductionOptionalFeatures", "OptionalFeatures", Clean(doc.ProductionOptionalFeatures));
            AddGroup(root, ns, "MSOptionalFeatures", "OptionalFeature", Clean(doc.MSOptionalFeatures));
        }

        /// <summary>TestContent 仅 ReleaseType=Test 时输出（官方校验规则）。</summary>
        private static void AddTestContent(OEMInputDoc doc, XElement root, XNamespace ns)
        {
            if (doc.ReleaseType == "Test" && !string.IsNullOrWhiteSpace(doc.TestContent))
                root.Add(new XElement(ns + "TestContent", doc.TestContent.Trim()));
        }

        // ══════════════════════ 读 ══════════════════════

        /// <summary>从 XML 探测 schema 版本（判据=根命名空间，与官方一致）。</summary>
        public static OemInputSchemaVersion? DetectVersion(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;
            try
            {
                var doc = XDocument.Parse(xml);
                string nsUri = doc.Root?.Name.Namespace.NamespaceName ?? "";
                if (nsUri == OEMInputDoc.NamespaceV2) return OemInputSchemaVersion.V2_2019_06;
                if (nsUri == OEMInputDoc.NamespaceV1) return OemInputSchemaVersion.V1_2004_10;
                return null;
            }
            catch (XmlException) { return null; }
        }

        /// <summary>解析 XML → 视图模型；版本按根命名空间自动判定。</summary>
        public static OEMInputDoc? Deserialize(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;
            XDocument xd;
            try { xd = XDocument.Parse(xml); }
            catch (XmlException) { return null; }
            if (xd.Root is null || xd.Root.Name.LocalName != "OEMInput") return null;

            XNamespace ns = xd.Root.Name.Namespace;
            bool isV2 = ns.NamespaceName == OEMInputDoc.NamespaceV2;
            // 命名空间不认识的，按特征元素兜底判版本（SOC→V1 / DeviceLayoutType→V2）
            if (!isV2 && ns.NamespaceName != OEMInputDoc.NamespaceV1)
            {
                if (xd.Root.Element(ns + "DeviceLayoutType") is not null) isV2 = true;
                else if (xd.Root.Element(ns + "SOC") is null) return null;
            }

            var doc = new OEMInputDoc
            {
                SchemaVersion = isV2 ? OemInputSchemaVersion.V2_2019_06 : OemInputSchemaVersion.V1_2004_10,
                Description = Text(xd.Root, ns, "Description"),
                Product = Text(xd.Root, ns, "Product"),
                SV = Text(xd.Root, ns, "SV"),
                SOC = isV2 ? Text(xd.Root, ns, "DeviceLayoutType") : Text(xd.Root, ns, "SOC"),
                Device = Text(xd.Root, ns, "Device"),
                ReleaseType = DefaultIf(Text(xd.Root, ns, "ReleaseType"), "Test"),
                BuildType = DefaultIf(Text(xd.Root, ns, "BuildType"), "fre"),
                TestContent = Text(xd.Root, ns, "TestContent"),
                MachineInfoFile = Text(xd.Root, ns, "MachineInfoFile"),
            };

            if (isV2)
            {
                doc.RecoveryPackagePath = Text(xd.Root, ns, "RecoveryPackagePath");
                doc.ImageVersionString = Text(xd.Root, ns, "ImageVersionString");
                string excl = Text(xd.Root, ns, "ExcludePrereleaseFeatures");
                doc.ExcludePrereleaseFeatures = excl.Equals("true", StringComparison.OrdinalIgnoreCase)
                                             || excl.Equals("REPLACEMENT", StringComparison.OrdinalIgnoreCase);
                doc.DevicePlatformIDs = Items(xd.Root, ns, "DevicePlatformIDs", "DevicePlatformID");

                foreach (var el in xd.Root.Element(ns + "Languages")?.Elements(ns + "Language") ?? Enumerable.Empty<XElement>())
                {
                    string def = (string?)el.Attribute("Default") ?? "false";
                    doc.Languages.Languages.Add(new LanguageItem { Code = el.Value.Trim(), Default = def });
                }
                doc.SupportedLanguages.UserInterface = doc.Languages.Languages.Select(l => l.Code).ToList();
                doc.SupportedLanguages.Keyboard = new List<string>(doc.SupportedLanguages.UserInterface);
                doc.SupportedLanguages.Speech = new List<string>(doc.SupportedLanguages.UserInterface);
                doc.BootUILanguage = doc.Languages.Languages.FirstOrDefault(x => x.IsDefault)?.Code ?? "en-us";
                doc.BootLocale = doc.BootUILanguage;
            }
            else // V1
            {
                doc.BootUILanguage = DefaultIf(Text(xd.Root, ns, "BootUILanguage"), "en-us");
                doc.BootLocale = DefaultIf(Text(xd.Root, ns, "BootLocale"), doc.BootUILanguage);
                doc.Resolutions = Items(xd.Root, ns, "Resolutions", "Resolution");

                var sl = xd.Root.Element(ns + "SupportedLanguages");
                if (sl is not null)
                {
                    doc.SupportedLanguages.UserInterface = Items(sl, ns, "UserInterface", "Language");
                    doc.SupportedLanguages.Keyboard = Items(sl, ns, "Keyboard", "Language");
                    doc.SupportedLanguages.Speech = Items(sl, ns, "Speech", "Language");
                }
                // 扁平 Languages 同步填充（语言页复用），Default 对齐 BootUILanguage
                foreach (var lang in doc.SupportedLanguages.UserInterface)
                    doc.Languages.Languages.Add(new LanguageItem
                    {
                        Code = lang,
                        Default = lang.Equals(doc.BootUILanguage, StringComparison.OrdinalIgnoreCase) ? "true" : "false"
                    });
            }

            doc.AdditionalFMs = Items(xd.Root, ns, "AdditionalFMs", "AdditionalFM");
            var features = xd.Root.Element(ns + "Features");
            if (features is not null)
            {
                doc.Features.MicrosoftFeatures = Items(features, ns, "Microsoft", "Feature");
                doc.Features.OEMFeatures = Items(features, ns, "OEM", "Feature");
            }
            doc.AppXOptionalPackages = Items(xd.Root, ns, "AppXOptionalPackages", "AppXID");
            doc.UserInstallableFeatures = Items(xd.Root, ns, "UserInstallableFeatures", "Feature");
            doc.AdditionalSKUs = Items(xd.Root, ns, "AdditionalSKUs", "AdditionalSKU");
            doc.InternalOptionalFeatures = Items(xd.Root, ns, "InternalOptionalFeatures", "OptionalFeature");
            doc.ProductionOptionalFeatures = Items(xd.Root, ns, "ProductionOptionalFeatures", "OptionalFeatures");
            doc.MSOptionalFeatures = Items(xd.Root, ns, "MSOptionalFeatures", "OptionalFeature");

            return doc;
        }

        // ══════════════════════ helpers ══════════════════════

        private static string DefaultIf(string? v, string fallback)
            => string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();

        private static void Add(XElement parent, XNamespace ns, string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                parent.Add(new XElement(ns + name, value.Trim()));
        }

        private static void AddGroup(XElement parent, XNamespace ns, string container, string item, List<string> values)
        {
            if (values.Count == 0) return;
            parent.Add(new XElement(ns + container, values.Select(v => new XElement(ns + item, v))));
        }

        /// <summary>三分组共用一个 item 名 Language。</summary>
        private static void AddGroup(XElement parent, XNamespace ns, string container, List<string> values)
            => AddGroup(parent, ns, container, "Language", values);

        private static List<string> Clean(List<string>? list)
            => list is null ? new List<string>()
               : list.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();

        private static string Text(XElement parent, XNamespace ns, string name)
            => (string?)parent.Element(ns + name) ?? string.Empty;

        private static List<string> Items(XElement parent, XNamespace ns, string container, string item)
            => parent.Element(ns + container)?.Elements(ns + item)
                     .Select(e => e.Value.Trim()).Where(s => s.Length > 0).ToList()
               ?? new List<string>();

        private static string Render(XElement root)
        {
            var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
            var sb = new StringBuilder();
            var settings = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                Encoding = new UTF8Encoding(false),
                OmitXmlDeclaration = false
            };
            using (var sw = new StringWriter(sb))
            using (var w = XmlWriter.Create(sw, settings))
                doc.WriteTo(w);
            return sb.ToString();
        }
    }
}
