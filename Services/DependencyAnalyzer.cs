using System.IO;
using System.Xml;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// 包依赖关系分析器
    /// 解析manifest中的dependency元素，构建依赖图谱
    /// </summary>
    public static class DependencyAnalyzer
    {
        /// <summary>
        /// 依赖节点
        /// </summary>
        public class DependencyNode
        {
            public string Name { get; set; } = "";
            public string Version { get; set; } = "";
            public string Architecture { get; set; } = "";
            public string PublicKeyToken { get; set; } = "";
            public string Language { get; set; } = "";
            public List<DependencyNode> Dependencies { get; } = new();
            public List<DependencyNode> Dependents { get; } = new(); // 被哪些包依赖
            public bool IsMissing { get; set; } // 依赖的包在镜像中不存在
        }

        /// <summary>
        /// 分析结果
        /// </summary>
        public class AnalysisResult
        {
            public List<DependencyNode> AllNodes { get; } = new();
            public List<DependencyNode> MissingDependencies { get; } = new();
            public List<List<DependencyNode>> CircularDependencies { get; } = new();
            public int TotalPackages { get; set; }
            public int TotalDependencies { get; set; }
        }

        /// <summary>
        /// 从manifest目录分析所有包的依赖关系
        /// </summary>
        /// <param name="manifestDirectory">manifest文件目录</param>
        /// <returns>分析结果</returns>
        public static AnalysisResult AnalyzeFromDirectory(string manifestDirectory)
        {
            var result = new AnalysisResult();
            var nodeMap = new Dictionary<string, DependencyNode>(StringComparer.OrdinalIgnoreCase);

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

                // 第一遍：收集所有包
                foreach (var manifestFile in manifestFiles)
                {
                    try
                    {
                        var node = ParseManifestIdentity(manifestFile);
                        if (node != null && !string.IsNullOrEmpty(node.Name))
                        {
                            string key = GetNodeKey(node);
                            if (!nodeMap.ContainsKey(key))
                            {
                                nodeMap[key] = node;
                                result.AllNodes.Add(node);
                            }
                        }
                    }
                    catch { }
                }

                // 第二遍：解析依赖关系
                foreach (var manifestFile in manifestFiles)
                {
                    try
                    {
                        var packageNode = ParseManifestIdentity(manifestFile);
                        if (packageNode == null || string.IsNullOrEmpty(packageNode.Name)) continue;

                        string key = GetNodeKey(packageNode);
                        if (!nodeMap.TryGetValue(key, out var currentNode)) continue;

                        var dependencies = ParseDependencies(manifestFile);
                        foreach (var dep in dependencies)
                        {
                            string depKey = GetNodeKey(dep);

                            // 查找依赖的包是否存在
                            if (nodeMap.TryGetValue(depKey, out var depNode))
                            {
                                currentNode.Dependencies.Add(depNode);
                                depNode.Dependents.Add(currentNode);
                            }
                            else
                            {
                                // 缺失的依赖
                                dep.IsMissing = true;
                                currentNode.Dependencies.Add(dep);
                                result.MissingDependencies.Add(dep);
                                if (!result.AllNodes.Contains(dep))
                                {
                                    result.AllNodes.Add(dep);
                                }
                            }
                            result.TotalDependencies++;
                        }
                    }
                    catch { }
                }

                result.TotalPackages = nodeMap.Count;

                // 检测循环依赖
                DetectCircularDependencies(result);
            }
            catch { }

            return result;
        }

        /// <summary>
        /// 解析manifest中的assemblyIdentity
        /// </summary>
        private static DependencyNode? ParseManifestIdentity(string manifestPath)
        {
            try
            {
                var doc = new XmlDocument();
                doc.Load(manifestPath);

                var nsmgr = new XmlNamespaceManager(doc.NameTable);
                nsmgr.AddNamespace("asm", "urn:schemas-microsoft-com:asm.v3");

                // 查找assemblyIdentity（在assembly根元素下）
                var identityNode = doc.SelectSingleNode("//asm:assemblyIdentity", nsmgr);
                if (identityNode == null) return null;

                return new DependencyNode
                {
                    Name = identityNode.Attributes?["name"]?.Value ?? "",
                    Version = identityNode.Attributes?["version"]?.Value ?? "",
                    Architecture = identityNode.Attributes?["processorArchitecture"]?.Value ?? "",
                    PublicKeyToken = identityNode.Attributes?["publicKeyToken"]?.Value ?? "",
                    Language = identityNode.Attributes?["language"]?.Value ?? "neutral"
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 解析manifest中的dependency元素
        /// </summary>
        private static List<DependencyNode> ParseDependencies(string manifestPath)
        {
            var dependencies = new List<DependencyNode>();
            try
            {
                var doc = new XmlDocument();
                doc.Load(manifestPath);

                var nsmgr = new XmlNamespaceManager(doc.NameTable);
                nsmgr.AddNamespace("asm", "urn:schemas-microsoft-com:asm.v3");

                // 查找所有dependency/dependentAssembly/assemblyIdentity
                var depNodes = doc.SelectNodes("//asm:dependency/asm:dependentAssembly/asm:assemblyIdentity", nsmgr);
                if (depNodes == null) return dependencies;

                foreach (XmlNode node in depNodes)
                {
                    var dep = new DependencyNode
                    {
                        Name = node.Attributes?["name"]?.Value ?? "",
                        Version = node.Attributes?["version"]?.Value ?? "",
                        Architecture = node.Attributes?["processorArchitecture"]?.Value ?? "",
                        PublicKeyToken = node.Attributes?["publicKeyToken"]?.Value ?? "",
                        Language = node.Attributes?["language"]?.Value ?? "neutral"
                    };
                    if (!string.IsNullOrEmpty(dep.Name))
                    {
                        dependencies.Add(dep);
                    }
                }
            }
            catch { }
            return dependencies;
        }

        /// <summary>
        /// 获取节点的唯一键
        /// </summary>
        private static string GetNodeKey(DependencyNode node)
        {
            return $"{node.Name}|{node.Version}|{node.Architecture}|{node.PublicKeyToken}|{node.Language}";
        }

        /// <summary>
        /// 检测循环依赖
        /// </summary>
        private static void DetectCircularDependencies(AnalysisResult result)
        {
            var visited = new HashSet<string>();
            var recursionStack = new HashSet<string>();
            var path = new List<DependencyNode>();

            foreach (var node in result.AllNodes)
            {
                if (node.IsMissing) continue;
                string key = GetNodeKey(node);
                if (!visited.Contains(key))
                {
                    DFS_Circular(node, visited, recursionStack, path, result);
                }
            }
        }

        private static void DFS_Circular(
            DependencyNode node,
            HashSet<string> visited,
            HashSet<string> recursionStack,
            List<DependencyNode> path,
            AnalysisResult result)
        {
            string key = GetNodeKey(node);
            visited.Add(key);
            recursionStack.Add(key);
            path.Add(node);

            foreach (var dep in node.Dependencies)
            {
                if (dep.IsMissing) continue;
                string depKey = GetNodeKey(dep);

                if (!visited.Contains(depKey))
                {
                    DFS_Circular(dep, visited, recursionStack, path, result);
                }
                else if (recursionStack.Contains(depKey))
                {
                    // 发现循环依赖
                    int cycleStart = path.FindIndex(n => GetNodeKey(n) == depKey);
                    if (cycleStart >= 0)
                    {
                        var cycle = path.Skip(cycleStart).ToList();
                        cycle.Add(dep);
                        result.CircularDependencies.Add(cycle);
                    }
                }
            }

            path.Remove(node);
            recursionStack.Remove(key);
        }

        /// <summary>
        /// 生成依赖关系文本报告
        /// </summary>
        public static string GenerateReport(AnalysisResult result)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== 包依赖关系分析报告 ===");
            sb.AppendLine($"分析时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine($"--- 统计 ---");
            sb.AppendLine($"包总数: {result.TotalPackages}");
            sb.AppendLine($"依赖关系总数: {result.TotalDependencies}");
            sb.AppendLine($"缺失依赖: {result.MissingDependencies.Count}");
            sb.AppendLine($"循环依赖: {result.CircularDependencies.Count}");
            sb.AppendLine();

            if (result.MissingDependencies.Count > 0)
            {
                sb.AppendLine("--- 缺失依赖 ---");
                foreach (var dep in result.MissingDependencies)
                {
                    sb.AppendLine($"  {dep.Name} (v{dep.Version}, {dep.Architecture}, {dep.Language})");
                }
                sb.AppendLine();
            }

            if (result.CircularDependencies.Count > 0)
            {
                sb.AppendLine("--- 循环依赖 ---");
                for (int i = 0; i < result.CircularDependencies.Count; i++)
                {
                    sb.AppendLine($"  循环 {i + 1}:");
                    foreach (var node in result.CircularDependencies[i])
                    {
                        sb.AppendLine($"    -> {node.Name}");
                    }
                }
                sb.AppendLine();
            }

            sb.AppendLine("--- 所有包的依赖关系 ---");
            foreach (var node in result.AllNodes.Where(n => !n.IsMissing).OrderBy(n => n.Name))
            {
                sb.AppendLine($"📦 {node.Name} (v{node.Version})");
                sb.AppendLine($"  依赖 ({node.Dependencies.Count}):");
                foreach (var dep in node.Dependencies)
                {
                    string status = dep.IsMissing ? "❌ 缺失" : "✅ 存在";
                    sb.AppendLine($"    {status} {dep.Name} (v{dep.Version})");
                }
                sb.AppendLine($"  被依赖 ({node.Dependents.Count}):");
                foreach (var dependent in node.Dependents)
                {
                    sb.AppendLine($"    <- {dependent.Name} (v{dependent.Version})");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }
    }
}
