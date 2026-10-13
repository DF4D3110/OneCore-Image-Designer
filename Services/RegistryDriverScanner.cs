using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using DiscUtils;
using DiscUtils.Registry;
using MobilePackageGen;
using OneCore_Image_Designer.Models.MobilePackageGen;

namespace OneCore_Image_Designer.Services
{
    /// <summary>驱动扫描通道（重构页/编辑器可单独选择或全开）。</summary>
    [Flags]
    public enum DriverScanChannels
    {
        None = 0,
        /// <summary>FileRepository 里的 *.inf（现有默认通道）。</summary>
        InfFile = 1,
        /// <summary>包 manifest 注册表节点识别驱动包（WCOS 无 inf 可扫时的主通道）。</summary>
        ManifestRegistry = 2,
        /// <summary>离线 SYSTEM hive 的驱动注册项（Services/Control Class/DriverDatabase）。</summary>
        OfflineHive = 4,
        All = InfFile | ManifestRegistry | OfflineHive
    }

    /// <summary>离线 hive 扫出的驱动注册条目。</summary>
    public sealed class DriverRegistryEntry
    {
        public string Name = "";
        /// <summary>Services 键名 / Class 子键名。</summary>
        public string SourceKey = "";
        /// <summary>ImagePath 值（相对 system32 的 .sys 路径，若有）。</summary>
        public string ImagePath = "";
        /// <summary>InfPath 值（Control\Class 注册时，若有）。</summary>
        public string InfPath = "";
        /// <summary>驱动类 GUID（Class 通道时有）。</summary>
        public string ClassGuid = "";
        /// <summary>hive 所在分区名（BSP/PreInstalled 分区条目即第三方驱动）。</summary>
        public string SourcePartition = "";
        /// <summary>第三方判定：来自 BSP/PreInstalled 分区 → 第三方；MainOS/UpdateOS → inbox。</summary>
        public bool IsThirdParty;
    }

    /// <summary>
    /// 注册表驱动的扫描通道：WCOS 镜像里 FileRepository 可能没有 *.inf 可扫，
    /// 此时用两条注册表路径识别驱动——
    /// ① 包 manifest 的 registryKeys/registryValues 节点（驱动包注册 \DriverDatabase、
    ///    CurrentControlSet\Services、Class\{GUID} 等键）；
    /// ② 离线 SYSTEM hive（Windows\System32\config\SYSTEM）直接枚举驱动注册项。
    /// </summary>
    public static class RegistryDriverScanner
    {
        // manifest 里出现这些注册表键前缀 → 判定为驱动包
        private static readonly string[] DriverKeyMarkers =
        {
            @"\DriverDatabase",
            @"CurrentControlSet\Services",
            @"\ControlSet",
            @"Control\Class\{",
            @"Control\DeviceClasses\{",
            @"\Enum\"
        };

        // ══════════════ 通道 A：包 manifest 注册表节点 ══════════════

        /// <summary>
        /// 扫描 servicing\Packages 下的包 manifest，把注册表节点含驱动键的包识别为驱动包。
        /// 与 InfFile 通道结果按包名去重（同一包两条通道都命中时保留 inf 条目）。
        /// </summary>
        public static List<PackageInfo> ScanManifestRegistry(
            IEnumerable<IDisk> disks, string outputPath, Action<string>? log = null)
        {
            var found = new List<PackageInfo>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // 诊断计数：帮用户判断镜像里到底有没有可扫的东西
            int totalMums = 0, driverNameMums = 0, noManifestRef = 0, manifestMissing = 0, dcmFail = 0, otherFail = 0, noDriverKey = 0;

            foreach (var disk in disks)
            {
                foreach (var part in disk.Partitions)
                {
                    var fs = part.FileSystem;
                    if (fs == null) continue;
                    string partitionName = part.Name.Replace("\0", "-");
                    IEnumerable<string> mums;
                    try
                    {
                        // 物化：DiscUtils 对不存在目录的异常在枚举时才抛，必须收进 try
                        mums = fs.GetFilesWithNtfsIssueWorkaround(
                            @"Windows\servicing\Packages", "*.mum", SearchOption.AllDirectories).ToList();
                    }
                    catch { continue; }

                    // Phone 时代驱动包名可能不含 "Driver"（如 MSAsOEM.DEVICE_QC8960...），
                    // mum 总量小（≤4000）时全量解析，大镜像才走名字闸门
                    bool parseAll = mums.Count() <= 4000;

                    foreach (var mumPath in mums)
                    {
                        totalMums++;
                        string mumName = Path.GetFileName(mumPath);
                        bool nameHit = parseAll
                            || mumName.Contains("Driver", StringComparison.OrdinalIgnoreCase)
                            || mumName.Contains("Drivers", StringComparison.OrdinalIgnoreCase);
                        if (!nameHit) continue;
                        driverNameMums++;
                        try
                        {
                            string? xml = ReadPackageManifestXml(fs, mumPath, out string? why, out string? mumXml);
                            if (xml == null)
                            {
                                if (why != null && why.Contains("无 manifest")) noManifestRef++;
                                else if (why != null && why.Contains("缺失")) manifestMissing++;
                                else if (why != null && (why.Contains("DCM") || why.Contains("解压"))) dcmFail++;
                                else otherFail++;
                                continue;
                            }
                            if (!ContainsDriverKey(xml)) { noDriverKey++; continue; }

                            // 包身份取 mum 顶层 assemblyIdentity（manifest 里的是组件身份，不能用）
                            var pkg = ParseMumIdentity(mumXml ?? "", mumName);
                            if (pkg == null || !seenNames.Add(pkg.Name)) continue;
                            pkg.Type = PackageType.Driver;
                            pkg.PartitionName = partitionName;
                            pkg.SourceManifest = mumPath;
                            pkg.CabFileName = partitionName + "\\" + pkg.Name;
                            pkg.CabFilePath = Path.Combine(outputPath, pkg.Name + ".cab");
                            pkg.Description = "manifest注册表通道识别（包内 registry 节点含驱动键）";
                            pkg.IsThirdPartyDriver = !pkg.Name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase);
                            found.Add(pkg);
                            log?.Invoke($"  [manifest注册表] {pkg.Name}");
                        }
                        catch { otherFail++; }
                    }
                }
            }
            log?.Invoke($"  [manifest注册表] mum 总数 {totalMums}，驱动名 {driverNameMums}，" +
                $"命中 {found.Count}（无manifest引用 {noManifestRef} / manifest缺失 {manifestMissing} / 解压失败 {dcmFail} / 无驱动键 {noDriverKey} / 其它 {otherFail}）");
            return found;
        }

        /// <summary>读包 manifest 文本：mum 引用的 fileGroups manifest 可能是 DCM 压缩，走 ManifestDecompressor。</summary>
        private static string? ReadPackageManifestXml(IFileSystem fs, string mumPath, out string? reason, out string? mumXmlOut)
        {
            reason = null;
            mumXmlOut = null;
            try
            {
                using var s = fs.OpenFile(mumPath, FileMode.Open, FileAccess.Read);
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                string mumXml = System.Text.Encoding.UTF8.GetString(ms.ToArray());
                mumXmlOut = mumXml;

                // mum → 主 manifest 文件名（官方 mum 命名空间 urn:schemas-microsoft-com:asm.v3；
                // 按 LocalName 匹配避免 ns 差异导致漏配）
                var doc = new XmlDocument();
                doc.LoadXml(mumXml);
                var manifestFile = doc.GetElementsByTagName("file").Cast<XmlNode>()
                    .Select(n => n.Attributes?["name"]?.Value)
                    .FirstOrDefault(v => v != null && v.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase));
                if (manifestFile == null) { reason = "mum 无 manifest 引用"; return null; }

                string dir = Path.GetDirectoryName(mumPath)?.Replace('\\', '/') ?? "";
                // 实证：mum 的 file@name 是分区内绝对路径（\windows\WinSxS\Manifests\...），
                // 也可能是相对文件名——两种都要能解析
                string manifestPath = manifestFile.StartsWith("\\", StringComparison.Ordinal)
                    ? manifestFile.TrimStart('\\').Replace('\\', '/')
                    : dir + "/" + manifestFile;

                var tmp = Path.Combine(Path.GetTempPath(), "rdscan_" + Guid.NewGuid().ToString("N")[..8] + ".manifest");
                try
                {
                    if (fs.FileExists(manifestPath))
                    {
                        using (var src = fs.OpenFile(manifestPath, FileMode.Open, FileAccess.Read))
                        using (var dst = File.Create(tmp))
                            src.CopyTo(dst);
                    }
                    else
                    {
                        // Phone 时代 staged="0"：manifest 不在文件系统里，在包 cab 内部
                        // （cabpath 形如 <keyform目录>\<文件>.manifest，cab 即同目录 <包目录名>.cab）
                        string? extracted = TryExtractManifestFromCab(fs, mumPath, manifestFile, tmp);
                        if (extracted == null) { reason = "manifest 缺失"; return null; }
                    }
                    string? text = ManifestDecompressor.ReadXmlText(tmp, out reason);
                    return text;
                }
                finally { try { File.Delete(tmp); } catch { } }
            }
            catch (Exception ex) { reason = ex.Message; return null; }
        }

        /// <summary>
        /// 从包 cab 里抽 manifest：cab 与 mum 同目录、文件名为 mum 目录名（…~~.cab），
        /// 内部 manifest 在 keyform 子目录下（实证：arm_msasoem…_none_xxx\arm_msasoem….manifest）。
        /// </summary>
        private static string? TryExtractManifestFromCab(IFileSystem fs, string mumPath, string manifestFile, string tmpTarget)
        {
            string? tmpCab = null;
            try
            {
                string dir = (Path.GetDirectoryName(mumPath) ?? "").Replace('\\', '/');
                string dirName = Path.GetFileName(dir.TrimEnd('/'));
                string cabPath = dir + "/" + dirName + ".cab";
                if (dirName.Length == 0 || !fs.FileExists(cabPath)) return null;

                // vendored CabInfo 只吃磁盘路径：先把 cab 拷到临时文件
                tmpCab = Path.Combine(Path.GetTempPath(), "rdscan_cab_" + Guid.NewGuid().ToString("N")[..8] + ".cab");
                using (var src = fs.OpenFile(cabPath, FileMode.Open, FileAccess.Read))
                using (var dst = File.Create(tmpCab))
                    src.CopyTo(dst);

                string want = Path.GetFileName(manifestFile);
                var ci = new Microsoft.Deployment.Compression.Cab.CabInfo(tmpCab);
                foreach (var f in ci.GetFiles())
                {
                    if (!string.Equals(Path.GetFileName(f.Path), want, StringComparison.OrdinalIgnoreCase)) continue;
                    using var s = ci.OpenRead(f.Path);
                    using var d = File.Create(tmpTarget);
                    s.CopyTo(d);
                    return tmpTarget;
                }
                return null;
            }
            catch { return null; }
            finally { if (tmpCab != null) { try { File.Delete(tmpCab); } catch { } } }
        }

        private static bool LooksLikeDriverManifest(string xml) =>
            xml.Contains("driver", StringComparison.OrdinalIgnoreCase);

        private static bool ContainsDriverKey(string xml)
        {
            foreach (var marker in DriverKeyMarkers)
                if (xml.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
            // registryValues 里的 InfPath/DeviceIds/Class 值名也是强信号
            return xml.Contains("InfPath", StringComparison.OrdinalIgnoreCase)
                || xml.Contains("DeviceIds", StringComparison.OrdinalIgnoreCase);
        }

        private static PackageInfo? ParseMumIdentity(string mumXml, string fallbackName)
        {
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(mumXml);
                var ai = doc.GetElementsByTagName("assemblyIdentity").Cast<XmlElement>().FirstOrDefault();
                string name = ai?.GetAttribute("name");
                if (string.IsNullOrEmpty(name))
                    name = Path.GetFileNameWithoutExtension(fallbackName);
                return new PackageInfo
                {
                    Name = name,
                    Version = ai?.GetAttribute("version") ?? "",
                    Architecture = ai?.GetAttribute("processorArchitecture") ?? "",
                    PublicKeyToken = ai?.GetAttribute("publicKeyToken") ?? "",
                    Language = ai?.GetAttribute("language") ?? ""
                };
            }
            catch { return null; }
        }

        // ══════════════ 通道 B：离线 SYSTEM hive ══════════════

        private static readonly string[] HivePaths =
        {
            @"Windows\System32\config\SYSTEM",
            @"Windows\System32\config\SYSTEM.SYSTEM",
            @"Windows\System32\CONFIG\SYSTEM"
        };

        /// <summary>
        /// 从镜像分区读离线 SYSTEM hive，枚举三类驱动注册点：
        /// Services（内核驱动服务项，ImagePath 指 .sys）、Control\Class（InfPath 注册）、
        /// DriverDatabase（驱动存储索引存在性）。
        /// </summary>
        public static List<DriverRegistryEntry> ScanOfflineHive(IEnumerable<IDisk> disks, Action<string>? log = null)
        {
            var entries = new List<DriverRegistryEntry>();
            foreach (var disk in disks)
            {
                foreach (var part in disk.Partitions)
                {
                    var fs = part.FileSystem;
                    if (fs == null) continue;
                    string? hivePath = HivePaths.FirstOrDefault(p => SafeExists(fs, p));
                    if (hivePath == null) continue;

                    try
                    {
                        using var s = fs.OpenFile(hivePath, FileMode.Open, FileAccess.Read);
                        using var ms = new MemoryStream();
                        s.CopyTo(ms);
                        ms.Position = 0;
                        using var hive = new RegistryHive(ms);
                        log?.Invoke($"  [离线hive] {part.Name.Replace("\0","-")}\\{hivePath} 已装载");

                        CollectServices(hive, entries, part.Name.Replace("\0", "-"), log);
                        CollectClassInfPaths(hive, entries, part.Name.Replace("\0", "-"), log);
                        CheckDriverDatabase(hive, log);
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"  [离线hive] 解析失败: {ex.Message}");
                    }
                }
            }
            return entries;
        }

        private static bool SafeExists(IFileSystem fs, string path)
        {
            try { return fs.FileExists(path); } catch { return false; }
        }

        private static RegistryKey? Open(RegistryKey root, string sub)
        {
            try { return root.OpenSubKey(sub); } catch { return null; }
        }

        /// <summary>BSP/PreInstalled 分区的 hive 条目视为第三方（inbox 驱动在 MainOS）。</summary>
        private static bool ThirdPartyByPartition(string partition) =>
            partition.Contains("BSP", StringComparison.OrdinalIgnoreCase)
            || partition.Contains("PreInstalled", StringComparison.OrdinalIgnoreCase);

        private static void CollectServices(RegistryHive hive, List<DriverRegistryEntry> entries, string partition, Action<string>? log)
        {
            int n = 0;
            foreach (var ctrl in new[] { "ControlSet001", "ControlSet002", "CurrentControlSet" })
            {
                var services = Open(hive.Root, ctrl + "\\Services");
                if (services == null) continue;
                foreach (var keyName in services.GetSubKeyNames())
                {
                    try
                    {
                        var k = services.OpenSubKey(keyName);
                        if (k == null) continue;
                        var svcType = k.GetValue("Type");
                        var img = k.GetValue("ImagePath") as string;
                        // SERVICE_KERNEL_DRIVER = 0x1 / SERVICE_FILE_SYSTEM_DRIVER = 0x2
                        int type = svcType is int i ? i : -1;
                        if ((type & 0x3) == 0 || string.IsNullOrEmpty(img)) continue;
                        entries.Add(new DriverRegistryEntry
                        {
                            Name = keyName,
                            SourceKey = $"{ctrl}\\Services\\{keyName}",
                            ImagePath = img!,
                            SourcePartition = partition,
                            IsThirdParty = ThirdPartyByPartition(partition)
                        });
                        n++;
                    }
                    catch { }
                }
            }
            log?.Invoke($"  [离线hive] Services 内核驱动项: {n}");
        }

        private static void CollectClassInfPaths(RegistryHive hive, List<DriverRegistryEntry> entries, string partition, Action<string>? log)
        {
            int n = 0;
            var control = Open(hive.Root, "ControlSet001\\Control\\Class");
            if (control != null)
            {
                foreach (var guid in control.GetSubKeyNames())
                {
                    try
                    {
                        var cls = control.OpenSubKey(guid);
                        if (cls == null) continue;
                        foreach (var idx in cls.GetSubKeyNames())
                        {
                            try
                            {
                                var k = cls.OpenSubKey(idx);
                                var inf = k?.GetValue("InfPath") as string;
                                if (string.IsNullOrEmpty(inf)) continue;
                                entries.Add(new DriverRegistryEntry
                                {
                                    Name = inf!,
                                    SourceKey = $"ControlSet001\\Control\\Class\\{guid}\\{idx}",
                                    InfPath = inf!,
                                    ClassGuid = guid,
                                    SourcePartition = partition,
                                    IsThirdParty = ThirdPartyByPartition(partition)
                                        || !inf!.StartsWith("microsoft", StringComparison.OrdinalIgnoreCase)
                                });
                                n++;
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            log?.Invoke($"  [离线hive] Control\\Class InfPath 注册: {n}");
        }

        private static void CheckDriverDatabase(RegistryHive hive, Action<string>? log)
        {
            try
            {
                var dd = Open(hive.Root, "DriverDatabase");
                if (dd != null)
                    log?.Invoke("  [离线hive] DriverDatabase 索引存在（驱动存储可用）");
            }
            catch { }
        }
    }
}
