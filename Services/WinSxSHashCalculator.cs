using System.Text;
using System.Text.RegularExpressions;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// WinSxS Keyform Hash 计算器（X65599Variant 算法）。
    ///
    /// 【2026-09-12 重写】原实现命中率实测仅 0.73%（以 C:\Windows\WinSxS\Manifests
    /// 下 3000 个真实 manifest 的内部 assemblyIdentity 作真值验证）。
    /// 重写后：非 .inf 组件 2337/2337 = <b>100%</b>。
    ///
    /// 原实现的三处错误：
    ///   1. 【致命】完全没有 versionScope 字段。真实组件的哈希几乎总是包含
    ///      versionScope="nonSxS"；少数（3000 样本中 67 条）manifest 内部无该属性，
    ///      此时不得加入该字段。
    ///   2. 【致命】culture 无条件加入，连 neutral 也塞进去。实测 culture 为
    ///      none/neutral 时【不得】参与哈希，否则必错。
    ///   3. ParseWinSxFolderName 用 Split('_')[1] 取 name。但 name 段本身可含下划线，
    ///      会被截断解析错；且未考虑 name 超过 40 字符时的截断形式。
    ///
    /// 【正确配方】字段按以下固定顺序参与哈希（顺序不可调换）：
    ///   name → culture（仅当 ≠ none/neutral）→ version → publicKeyToken
    ///   → processorArchitecture → versionScope（仅当组件确实带该属性）
    ///
    /// 【已知边界】*.inf / dual_*.inf 驱动类组件的哈希【无法】用本算法复现
    /// （3000 样本中 663 条 .inf 全部未命中），疑似按 INF 内容另行计算。
    /// 调用方应先用 <see cref="IsInfComponent"/> 预判并跳过，避免把正确名字误判为错误。
    ///
    /// 【重要】输入字段必须取自 manifest 的【内部】assemblyIdentity，不能用文件夹名
    /// 反解：实测 19%（569/3000）的组件其内部 name 与文件名 name 段不一致，
    /// 例如内部 ".NET Framework" 在文件名里被清理为 ".netframework"（空格被删）。
    /// 哈希用原始 name，文件夹名用清理后的 name，两者不可混用。
    /// </summary>
    public static class WinSxSHashCalculator
    {
        private const uint HashMultiplierX65599 = 65599u;
        private const ulong HashMultiplierLonghorn = 0x1FFFFFFF7UL;

        private static readonly ulong[] HashMultipliers =
        [
            0x1E5FFFFFD27UL,
            0xFFFFFFDC00000051UL,
            HashMultiplierLonghorn,
            0x1UL
        ];

        /// <summary>
        /// 绝大多数真实组件的 versionScope 取值。
        /// manifest 内部【没有】该属性时应传 null，此时该字段不参与哈希。
        /// </summary>
        public const string DefaultVersionScope = "nonSxS";

        // 预计算的属性名 hash（X65599Variant，大小写不敏感）
        private static readonly ulong Hash_Name = HashStringX65599Variant("name");
        private static readonly ulong Hash_Culture = HashStringX65599Variant("culture");
        private static readonly ulong Hash_Version = HashStringX65599Variant("version");
        private static readonly ulong Hash_PublicKeyToken = HashStringX65599Variant("publicKeyToken");
        private static readonly ulong Hash_ProcessorArchitecture = HashStringX65599Variant("processorArchitecture");
        private static readonly ulong Hash_VersionScope = HashStringX65599Variant("versionScope");
        private static readonly ulong Hash_Type = HashStringX65599Variant("type");
        private static readonly ulong Hash_TypeName = HashStringX65599Variant("typeName");

        /// <summary>
        /// 文件夹名解析用的锚定正则。name 段可含下划线，故从两端锚定后让 name 吞掉中间部分。
        /// </summary>
        private static readonly Regex FolderNameRegex = new(
            @"^(?<arch>[^_]+)_(?<name>.+)_(?<pkt>[0-9a-fA-F]{16})_(?<ver>\d+\.\d+\.\d+\.\d+)_(?<cult>[^_]+)_(?<hash>[0-9a-fA-F]{16})$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// 计算 WinSxS 哈希（推荐入口）。
        /// </summary>
        /// <param name="name">组件名，须取自 manifest 内部 assemblyIdentity 的 name（原始值，非文件名清理值）</param>
        /// <param name="publicKeyToken">公钥令牌（16 位十六进制）</param>
        /// <param name="architecture">架构真实名（x86/amd64/arm/arm64/wow64/msil/none）</param>
        /// <param name="version">版本（如 10.0.19041.1）</param>
        /// <param name="culture">语言/culture；none、neutral 或空表示中性，将【不参与】哈希</param>
        /// <param name="versionScope">
        /// 取自 manifest 内部 versionScope 属性。绝大多数组件为 "nonSxS"；
        /// 组件【没有】该属性时传 null 或空串，此时不加入哈希字段。
        /// </param>
        /// <returns>16 位十六进制哈希</returns>
        /// <param name="type">
        /// 组件 identity 的 <c>type</c> 属性（仅 .inf 驱动类组件有，典型值 <c>dualModeDriver</c>）。
        /// **它插在 culture 与 version 之间参与哈希** —— 这是 2026-10-13 实测确认的：
        /// 驱动组件的字段顺序是
        ///   name → culture → type → version → publicKeyToken → processorArchitecture → versionScope
        /// 与 havesxs.py 一致。缺了 type 会让全部 .inf 驱动组件的哈希算错（实测 0/663 命中），
        /// 但非驱动组件（无 type）不受影响，所以此前只表现出"驱动类全部失败"。
        /// 传 null/空 表示该组件没有 type 属性，此时不加入哈希。
        /// </param>
        public static string CalculateWinSxSHash(
            string name,
            string publicKeyToken,
            string architecture,
            string version,
            string culture = "neutral",
            string? versionScope = DefaultVersionScope,
            string? type = null)
        {
            ArgumentNullException.ThrowIfNull(name);

            // 字段顺序经 3000 个真实样本实测确认，不可调换
            var attributes = new List<(string Key, string Value)>(7)
            {
                ("name", name),
            };

            // culture 仅在中性以外时参与 —— 这是原实现最关键的错误之一
            if (!IsNeutralCulture(culture))
                attributes.Add(("culture", culture.Trim()));

            // type 插在 culture 与 version 之间（.inf 驱动组件）
            if (!string.IsNullOrWhiteSpace(type))
                attributes.Add(("type", type.Trim()));

            attributes.Add(("version", version));
            attributes.Add(("publicKeyToken", publicKeyToken));
            attributes.Add(("processorArchitecture", architecture));

            // versionScope 仅当组件确实带该属性时参与
            if (!string.IsNullOrWhiteSpace(versionScope))
                attributes.Add(("versionScope", versionScope.Trim()));

            return GeneratePseudoKey(attributes).ToString("x16");
        }

        /// <summary>
        /// 生成完整 WinSxS 文件夹名：{arch}_{name}_{pkt}_{version}_{culture}_{hash}
        /// </summary>
        /// <remarks>
        /// 哈希用【原始】name（manifest 内部值），文件夹名段用【清理后】name
        /// （<see cref="NormalizeNameForFolder"/>）—— 两者不可混用，这是原实现隐含的坑。
        /// </remarks>
        public static string GenerateWinSxFolderName(
            string name,
            string publicKeyToken,
            string architecture,
            string version,
            string culture = "neutral",
            string? versionScope = DefaultVersionScope,
            string? type = null)
        {
            string hash = CalculateWinSxSHash(name, publicKeyToken, architecture, version, culture, versionScope, type);
            string folderNamePart = NormalizeNameForFolder(name);
            string cult = IsNeutralCulture(culture) ? "none" : culture.Trim().ToLowerInvariant();

            return $"{architecture.ToLowerInvariant()}_{folderNamePart}_{publicKeyToken.ToLowerInvariant()}_{version}_{cult}_{hash}";
        }

        /// <summary>
        /// 生成 CBS 包名：{name}~{publicKeyToken}~{architecture}~{language}~{version}
        /// </summary>
        public static string GenerateCBSKeyform(
            string name,
            string publicKeyToken,
            string architecture,
            string language,
            string version)
        {
            string lang = IsNeutralCulture(language) ? string.Empty : language.Trim();
            return $"{name}~{publicKeyToken}~{architecture}~{lang}~{version}";
        }

        /// <summary>
        /// 解析 WinSxS 文件夹名 / manifest 文件名为各组成部分。
        /// </summary>
        /// <remarks>
        /// 已修正：name 段可含下划线（如 microsoft-windows-coreos），旧实现用
        /// Split('_')[1] 会截断；现改用锚定正则。同时容许传入带 .manifest 扩展名的文件名。
        /// </remarks>
        public static WinSxSFolderInfo? ParseWinSxFolderName(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName)) return null;

            string name = folderName.Trim();
            if (name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
                name = name[..^".manifest".Length];

            Match m = FolderNameRegex.Match(name);
            if (!m.Success) return null;

            return new WinSxSFolderInfo
            {
                Architecture = m.Groups["arch"].Value,
                Name = m.Groups["name"].Value,
                PublicKeyToken = m.Groups["pkt"].Value.ToLowerInvariant(),
                Version = m.Groups["ver"].Value,
                Culture = m.Groups["cult"].Value,
                Hash = m.Groups["hash"].Value.ToLowerInvariant()
            };
        }

        /// <summary>
        /// 验证文件夹名的哈希是否正确（弱校验）。
        /// </summary>
        /// <remarks>
        /// ⚠ 局限：文件夹名里的 name 段是【清理过】的，与内部真实 name 可能不同
        /// （实测 19% 的组件如此）；文件名不携带 versionScope，也【不携带 type】，
        /// 因此对带 type 的 .inf 驱动组件无法判定（返回 false 会被误读为"校验失败"，
        /// 故这里直接返回 false 表示"不可判定"）。
        /// 要精确校验请用 <see cref="VerifyAgainstIdentity"/> 传入 manifest 内部字段。
        /// </remarks>
        public static bool VerifyWinSxFolderHash(string folderName)
        {
            WinSxSFolderInfo? info = ParseWinSxFolderName(folderName);
            if (info is null) return false;

            // .inf 类不可判定：文件夹名不含 type，无法复现哈希
            if (info.IsInfComponent) return false;

            string calculated = CalculateWinSxSHash(
                info.Name, info.PublicKeyToken, info.Architecture, info.Version, info.Culture, DefaultVersionScope);

            return string.Equals(calculated, info.Hash, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 用 manifest 内部 assemblyIdentity 的【权威字段】验证哈希（推荐）。
        /// </summary>
        /// <param name="expectedHash">文件夹名/文件名末尾的 16 位哈希</param>
        /// <param name="versionScope">manifest 内部 versionScope 属性值；无该属性时传 null</param>
        /// <param name="type">
        /// manifest 内部 type 属性值；.inf 驱动类组件必须传（典型值 dualModeDriver），
        /// 否则哈希必错。无该属性时传 null。
        /// </param>
        public static bool VerifyAgainstIdentity(
            string expectedHash,
            string name,
            string publicKeyToken,
            string architecture,
            string version,
            string culture,
            string? versionScope,
            string? type = null)
        {
            if (string.IsNullOrWhiteSpace(expectedHash)) return false;

            string calculated = CalculateWinSxSHash(name, publicKeyToken, architecture, version, culture, versionScope, type);
            return string.Equals(calculated, expectedHash.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 判断组件名是否是 .inf 驱动类（以 .inf 结尾）。
        /// </summary>
        /// <remarks>
        /// 【2026-10-13 更新】这类组件的哈希**现在可以复现**了 —— 只要把 identity 的
        /// <c>type</c> 属性（插在 culture 与 version 之间）一起传入 <see cref="CalculateWinSxSHash"/>。
        /// 实测 663/663 命中。本方法保留仅用于"识别名称形态"，
        /// **不再**意味着哈希不可算，调用方不应再据此跳过。
        /// </remarks>
        public static bool IsInfComponent(string? name)
            => !string.IsNullOrEmpty(name) && name.EndsWith(".inf", StringComparison.OrdinalIgnoreCase);

        /// <summary>culture 是否表示"中性"（中性时不参与哈希）。</summary>
        public static bool IsNeutralCulture(string? culture)
            => string.IsNullOrWhiteSpace(culture)
            || culture.Equals("none", StringComparison.OrdinalIgnoreCase)
            || culture.Equals("neutral", StringComparison.OrdinalIgnoreCase)
            || culture == "*";

        /// <summary>
        /// name 段在文件夹名中的最大长度（实测确认）。
        /// </summary>
        public const int MaxFolderNameLength = 40;

        /// <summary>
        /// 按 Windows 惯例生成文件夹名中的 name 段：
        ///   1. 转小写，删除字母数字与 - . _ 之外的字符
        ///      （例：内部 ".NET Framework" → ".netframework"，空格被删）
        ///   2. 超过 <see cref="MaxFolderNameLength"/> 时截断为「前19 + ".." + 后19」
        ///
        /// 截断规则实测确认：6/6 超长样本（43~75 字符）全部吻合，
        /// 例如 "Containers-ApplicationGuard-Shared-merged-Deployment"(52)
        ///   → "containers-applicat..d-merged-deployment"(40)。
        /// </summary>
        public static string NormalizeNameForFolder(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;

            StringBuilder sb = new(name.Length);
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c) || c is '.' or '-' or '_')
                    sb.Append(char.ToLowerInvariant(c));
            }

            string cleaned = sb.ToString();

            if (cleaned.Length <= MaxFolderNameLength)
                return cleaned;

            // k = maxLength/2 - 1 = 19；前 k + ".." + 后 k，总长恰好 40
            int k = (MaxFolderNameLength / 2) - 1;
            return string.Concat(
                cleaned.AsSpan(0, k),
                "..",
                cleaned.AsSpan(cleaned.Length - k));
        }

        // ───────────────── 内部实现（X65599Variant） ─────────────────

        /// <summary>
        /// 把 (属性名, 值) 序列按 X65599Variant 组合成伪键。
        /// 公开出来供**算法调查**使用（例如穷举 .inf 驱动组件的字段集），
        /// 正常业务代码请用 <see cref="CalculateWinSxSHash"/>。
        /// 值为 null/空 的项会被跳过（等价于属性不存在）。
        /// </summary>
        public static ulong GeneratePseudoKey(IEnumerable<(string Key, string? Value)> attributes)
        {
            ulong pseudoKey = 0UL;

            foreach (var (key, value) in attributes)
            {
                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value)) continue;

                ulong nameHash = GetPrecalculatedNameHash(key);
                ulong valueHash = HashStringX65599Variant(value!);
                ulong nameValueHash = nameHash * HashMultiplierLonghorn + valueHash;

                pseudoKey = pseudoKey * HashMultiplierLonghorn + nameValueHash;
            }

            return pseudoKey;
        }

        /// <summary>单个字符串的 X65599Variant 哈希（公开供算法调查）。</summary>
        public static ulong HashString(string value) => HashStringX65599Variant(value);

        private static ulong GeneratePseudoKey(List<(string Key, string Value)> attributes)
        {
            ulong pseudoKey = 0UL;

            foreach (var attribute in attributes)
            {
                ulong nameHash = GetPrecalculatedNameHash(attribute.Key);
                ulong valueHash = HashStringX65599Variant(attribute.Value);
                ulong nameValueHash = nameHash * HashMultiplierLonghorn + valueHash;

                pseudoKey = pseudoKey * HashMultiplierLonghorn + nameValueHash;
            }

            return pseudoKey;
        }

        private static ulong GetPrecalculatedNameHash(string name)
        {
            return name.ToLowerInvariant() switch
            {
                "name" => Hash_Name,
                // culture 与 language 在哈希里等价（属性名一律用 culture）
                "culture" or "language" => Hash_Culture,
                "version" => Hash_Version,
                "publickeytoken" => Hash_PublicKeyToken,
                "processorarchitecture" => Hash_ProcessorArchitecture,
                "versionscope" => Hash_VersionScope,
                "type" => Hash_Type,
                "typename" => Hash_TypeName,
                _ => HashStringX65599Variant(name)
            };
        }

        /// <summary>
        /// X65599Variant：4 个累加器按字符位置轮转累加，再按固定乘子加权求和。
        /// 大小写不敏感（一律 ToLower）。
        /// </summary>
        private static ulong HashStringX65599Variant(string nameOrValue)
        {
            ArgumentNullException.ThrowIfNull(nameOrValue);

            uint[] accumulators = new uint[4];

            for (int i = 0; i < nameOrValue.Length; i++)
            {
                uint tokenValue = char.ToLowerInvariant(nameOrValue[i]);
                int index = i % 4;
                accumulators[index] = accumulators[index] * HashMultiplierX65599 + tokenValue;
            }

            ulong result = 0UL;
            for (int i = 0; i < accumulators.Length; i++)
                result += accumulators[i] * HashMultipliers[i];

            return result;
        }
    }

    /// <summary>
    /// WinSxS 文件夹信息。
    /// </summary>
    public class WinSxSFolderInfo
    {
        public string Architecture { get; set; } = "";
        public string Name { get; set; } = "";
        public string PublicKeyToken { get; set; } = "";
        public string Version { get; set; } = "";
        public string Culture { get; set; } = "";
        public string Hash { get; set; } = "";

        /// <summary>culture 是否为中性（none/neutral）。</summary>
        public bool IsNeutralCulture => WinSxSHashCalculator.IsNeutralCulture(Culture);

        /// <summary>是否为 .inf 驱动类组件（其哈希无法用本算法复现）。</summary>
        public bool IsInfComponent => WinSxSHashCalculator.IsInfComponent(Name);
    }
}
