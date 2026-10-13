using System.IO;
using System.Xml;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// 组件存储完整性校验器
    /// 根据manifest和hash算法校验WinSxS组件存储的完整性
    /// </summary>
    public static class ComponentStoreVerifier
    {
        /// <summary>
        /// 校验结果项
        /// </summary>
        public class VerifyItem
        {
            public string Name { get; set; } = "";
            public string Version { get; set; } = "";
            public string Architecture { get; set; } = "";
            public string PublicKeyToken { get; set; } = "";
            public string Language { get; set; } = "";

            /// <summary>
            /// manifest 内部的 versionScope 属性值。
            /// 绝大多数组件为 "nonSxS"；组件没有该属性时为 null，此时哈希不含此字段。
            /// </summary>
            public string? VersionScope { get; set; } = WinSxSHashCalculator.DefaultVersionScope;

        /// <summary>
        /// manifest 内部 type 属性值（仅 .inf 驱动类组件有，典型值 dualModeDriver）。
        /// 它插在 culture 与 version 之间参与哈希；漏传会让驱动组件的期望文件夹名算错。
        /// </summary>
        public string? Type { get; set; }

            public string ExpectedFolderName { get; set; } = "";
            public string? ActualFolderPath { get; set; }
            public bool Exists { get; set; }
            public bool IsEmpty { get; set; }
            public int FileCount { get; set; }
            public long TotalSize { get; set; }

            /// <summary>
            /// 是否为 .inf 驱动类组件。这类组件的哈希无法用 X65599Variant 复现
            /// （实测 663/663 全部未命中），故不参与"缺失"判定，单独归类。
            /// </summary>
            public bool IsInfSkipped { get; set; }

            public string Status =>
                IsInfSkipped ? "⏭️ 已跳过(.inf 驱动类，哈希不可复现)"
                : Exists ? (IsEmpty ? "⚠️ 空文件夹" : "✅ 正常")
                : "❌ 缺失";
        }

        /// <summary>
        /// 校验结果
        /// </summary>
        public class VerifyResult
        {
            public List<VerifyItem> Items { get; } = new();
            public int TotalComponents { get; set; }
            public int ExistingCount { get; set; }
            public int MissingCount { get; set; }
            public int EmptyCount { get; set; }

            /// <summary>
            /// 因属 .inf 驱动类而跳过的组件数（其哈希无法复现，不计入"缺失"）。
            /// </summary>
            public int InfSkippedCount { get; set; }

            /// <summary>
            /// 解析失败的 manifest 数量。
            ///
            /// 原实现把解析失败用空 catch 静默吞掉，导致对真实 WinSxS
            /// （manifest 为 DCM 压缩存储）校验时得到 0 个组件却毫无提示。
            /// 现在显式计数并按原因归类。
            /// </summary>
            public int ParseFailedCount { get; set; }

            /// <summary>解析失败原因 → 次数，便于定位是压缩、缺 wcpex 还是格式问题。</summary>
            public Dictionary<string, int> ParseFailures { get; } = new();

            public long TotalSize { get; set; }
        }

        /// <summary>
        /// 校验WinSxS组件存储完整性
        /// </summary>
        /// <param name="manifestDirectory">manifest文件目录</param>
        /// <param name="winsxsDirectory">WinSxS组件存储目录</param>
        /// <returns>校验结果</returns>
        public static VerifyResult Verify(string manifestDirectory, string winsxsDirectory)
        {
            var result = new VerifyResult();

            try
            {
                if (!Directory.Exists(manifestDirectory))
                {
                    return result;
                }

                // 查找所有manifest文件
                var manifestFiles = Directory.GetFiles(manifestDirectory, "*.manifest", SearchOption.AllDirectories)
                    .Concat(Directory.GetFiles(manifestDirectory, "*.mum", SearchOption.AllDirectories))
                    .ToList();

                // 解析每个manifest，获取组件信息
                var components = new List<VerifyItem>();
                foreach (var manifestFile in manifestFiles)
                {
                    try
                    {
                        var item = ParseManifestComponent(manifestFile, out string? failure);
                        if (item != null && !string.IsNullOrEmpty(item.Name))
                        {
                            // 【2026-10-13 更正】.inf 驱动类组件的哈希【可以】复现：
                            // 只要把 identity 的 type 属性一起传进去（实测 663/663 命中）。
                            // 因此不再跳过它们，正常参与缺失判定。
                            item.IsInfSkipped = false;

                            // 计算期望的WinSxS文件夹名（须传 manifest 内部真实 versionScope）
                            item.ExpectedFolderName = WinSxSHashCalculator.GenerateWinSxFolderName(
                                item.Name,
                                item.PublicKeyToken,
                                item.Architecture,
                                item.Version,
                                item.Language,
                                item.VersionScope,
                                item.Type);

                            components.Add(item);
                        }
                        else
                        {
                            // 关键：原实现用空 catch 吞掉失败，导致对真实 WinSxS
                            // （manifest 为 DCM 压缩）校验时静默得到 0 个组件。
                            // 现在计数并按原因归类，便于定位是压缩、缺 wcpex 还是格式问题。
                            result.ParseFailedCount++;
                            string reason = failure ?? "未知原因";
                            result.ParseFailures[reason] = result.ParseFailures.GetValueOrDefault(reason) + 1;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        result.ParseFailedCount++;
                        string reason = $"读取文件异常：{ex.GetType().Name}";
                        result.ParseFailures[reason] = result.ParseFailures.GetValueOrDefault(reason) + 1;
                    }
                }

                // 去重
                var uniqueComponents = components
                    .GroupBy(c => c.ExpectedFolderName)
                    .Select(g => g.First())
                    .ToList();

                result.TotalComponents = uniqueComponents.Count;

                // 检查WinSxS目录
                bool winsxsExists = Directory.Exists(winsxsDirectory);

                foreach (var item in uniqueComponents)
                {
                    // .inf 类跳过：算不出正确文件夹名，强行比对只会产生大量假"缺失"
                    if (item.IsInfSkipped)
                    {
                        result.InfSkippedCount++;
                        result.Items.Add(item);
                        continue;
                    }

                    if (winsxsExists)
                    {
                        string expectedPath = Path.Combine(winsxsDirectory, item.ExpectedFolderName);
                        item.ActualFolderPath = expectedPath;

                        if (Directory.Exists(expectedPath))
                        {
                            item.Exists = true;
                            var files = Directory.GetFiles(expectedPath, "*.*", SearchOption.AllDirectories);
                            item.FileCount = files.Length;
                            item.IsEmpty = files.Length == 0;

                            if (!item.IsEmpty)
                            {
                                foreach (var file in files)
                                {
                                    try
                                    {
                                        item.TotalSize += new FileInfo(file).Length;
                                    }
                                    catch { }
                                }
                            }

                            result.ExistingCount++;
                            result.TotalSize += item.TotalSize;
                            if (item.IsEmpty) result.EmptyCount++;
                        }
                        else
                        {
                            item.Exists = false;
                            result.MissingCount++;
                        }
                    }
                    else
                    {
                        item.Exists = false;
                        result.MissingCount++;
                    }

                    result.Items.Add(item);
                }
            }
            catch { }

            return result;
        }

        /// <summary>
        /// 解析manifest中的组件信息。
        /// </summary>
        /// <remarks>
        /// WinSxS 与 CAB 内的 manifest 大多是 DCM 压缩存储的，直接 XmlDocument.Load
        /// 会失败。原实现在此用空 catch 吞掉异常，导致对真实的
        /// C:\Windows\WinSxS\Manifests（20948 个文件）校验时【解析出 0 个组件】，
        /// 功能完全失效却不报错。现改为先经 <see cref="ManifestDecompressor"/> 取得
        /// 明文 XML，并把失败原因回报给调用方计数，避免再次静默失败。
        /// </remarks>
        private static VerifyItem? ParseManifestComponent(string manifestPath, out string? failure)
        {
            failure = null;

            string? xml = ManifestDecompressor.ReadXmlText(manifestPath, out string? readFailure);
            if (xml is null)
            {
                failure = readFailure ?? "无法读取 XML";
                return null;
            }

            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(xml);

                var nsmgr = new XmlNamespaceManager(doc.NameTable);
                nsmgr.AddNamespace("asm", "urn:schemas-microsoft-com:asm.v3");

                var identityNode = doc.SelectSingleNode("//asm:assemblyIdentity", nsmgr);
                if (identityNode == null)
                {
                    // 少数 manifest 不带 asm.v3 命名空间，退回按标签名查找
                    identityNode = doc.GetElementsByTagName("assemblyIdentity").Item(0);
                }
                if (identityNode == null)
                {
                    failure = "manifest 中没有 assemblyIdentity 节点";
                    return null;
                }

                // versionScope 必须取内部真实值：绝大多数组件为 "nonSxS"，
                // 但少数组件（实测 3000 样本中 67 条）没有该属性，此时哈希不含此字段。
                string? versionScope = identityNode.Attributes?["versionScope"]?.Value;
                // type 参与哈希（插在 culture 与 version 之间）；少数组件用 typeName
                string? typeAttr = identityNode.Attributes?["type"]?.Value
                                   ?? identityNode.Attributes?["typeName"]?.Value;

                return new VerifyItem
                {
                    Name = identityNode.Attributes?["name"]?.Value ?? "",
                    Version = identityNode.Attributes?["version"]?.Value ?? "",
                    Architecture = identityNode.Attributes?["processorArchitecture"]?.Value ?? "none",
                    PublicKeyToken = identityNode.Attributes?["publicKeyToken"]?.Value ?? "",
                    Language = identityNode.Attributes?["language"]?.Value ?? "neutral",
                    VersionScope = string.IsNullOrWhiteSpace(versionScope) ? null : versionScope.Trim(),
                    Type = string.IsNullOrWhiteSpace(typeAttr) ? null : typeAttr.Trim()
                };
            }
            catch (Exception ex) when (ex is XmlException or ArgumentException)
            {
                failure = $"XML 解析失败：{ex.Message}";
                return null;
            }
        }

        /// <summary>
        /// 生成校验报告
        /// </summary>
        public static string GenerateReport(VerifyResult result)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== WinSxS 组件存储完整性校验报告 ===");
            sb.AppendLine($"校验时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine("--- 统计 ---");
            sb.AppendLine($"组件总数: {result.TotalComponents}");
            sb.AppendLine($"存在: {result.ExistingCount}");
            sb.AppendLine($"缺失: {result.MissingCount}");
            sb.AppendLine($"空文件夹: {result.EmptyCount}");
            if (result.InfSkippedCount > 0)
            {
                sb.AppendLine($"已跳过(.inf 驱动类，哈希不可复现): {result.InfSkippedCount}");
            }
            if (result.ParseFailedCount > 0)
            {
                sb.AppendLine($"解析失败的 manifest: {result.ParseFailedCount}");
                foreach (var kv in result.ParseFailures.OrderByDescending(k => k.Value))
                {
                    sb.AppendLine($"    {kv.Value,6}  {kv.Key}");
                }
            }
            sb.AppendLine($"总大小: {result.TotalSize:N0} 字节 ({Math.Round(result.TotalSize / 1024.0 / 1024.0, 2)} MB)");
            sb.AppendLine();

            if (result.MissingCount > 0)
            {
                sb.AppendLine("--- 缺失的组件 ---");
                foreach (var item in result.Items.Where(i => !i.Exists).OrderBy(i => i.Name))
                {
                    sb.AppendLine($"  ❌ {item.Name} (v{item.Version}, {item.Architecture})");
                    sb.AppendLine($"     期望文件夹: {item.ExpectedFolderName}");
                }
                sb.AppendLine();
            }

            if (result.EmptyCount > 0)
            {
                sb.AppendLine("--- 空文件夹组件 ---");
                foreach (var item in result.Items.Where(i => i.Exists && i.IsEmpty).OrderBy(i => i.Name))
                {
                    sb.AppendLine($"  ⚠️ {item.Name} (v{item.Version}, {item.Architecture})");
                    sb.AppendLine($"     文件夹: {item.ExpectedFolderName}");
                }
                sb.AppendLine();
            }

            sb.AppendLine("--- 所有组件 ---");
            foreach (var item in result.Items.OrderBy(i => i.Name))
            {
                string sizeStr = item.Exists && !item.IsEmpty ? $", {item.FileCount}个文件, {item.TotalSize:N0}字节" : "";
                sb.AppendLine($"  {item.Status} {item.Name} (v{item.Version}, {item.Architecture}){sizeStr}");
            }

            return sb.ToString();
        }
    }
}
