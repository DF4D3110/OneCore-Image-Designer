using System.Xml.Serialization;
using MobilePackageGen;
using MobilePackageGen.GZip;

namespace MobilePackageGen.GUI
{
    public class PackageEnumerator
    {
        public static List<PackageInfo> EnumerateAll(IEnumerable<IDisk> disks, string outputPath)
        {
            var packages = new List<PackageInfo>();
            var updateHistory = BuildMetadataHandler.GetUpdateHistory(disks);

            packages.AddRange(EnumerateCBS(disks, outputPath, updateHistory));
            packages.AddRange(EnumerateSPKG(disks, outputPath, updateHistory));
            packages.AddRange(EnumerateDrivers(disks, outputPath, updateHistory));
            packages.AddRange(EnumerateAppx(disks, outputPath));

            return packages;
        }

        public static List<PackageInfo> EnumerateAppx(IEnumerable<IDisk> disks, string outputPath)
        {
            var packages = new List<PackageInfo>();

            // PreInstalled partitions expose extracted APPX in WindowsApps and licenses in AppData.
            foreach (var partition in disks.SelectMany(d => d.Partitions))
            {
                var fs = partition.FileSystem;
                if (fs == null) continue;

                try
                {
                    if (!(fs.DirectoryExists("AppData") && fs.DirectoryExists("WindowsApps")))
                    {
                        continue;
                    }

                    var appFolders = fs.GetDirectories("WindowsApps", "*", SearchOption.TopDirectoryOnly);
                    foreach (var appFolder in appFolders)
                    {
                        string folderName = Path.GetFileName(appFolder);
                        if (folderName.Equals("deleted", StringComparison.InvariantCultureIgnoreCase))
                        {
                            continue;
                        }

                        // Ex: "Microsoft.Dynamics365.Guides_1.0.100.0_arm__8wekyb3d8bbwe"
                        string[] parts = folderName.Split('_');
                        string appName = parts.Length > 0 ? parts[0] : folderName;
                        string architecture = parts.Length >= 3 ? parts[^3] : "";
                        string version = parts.Length >= 2 ? parts[1] : "";

                        packages.Add(new PackageInfo
                        {
                            Type = PackageType.Appx,
                            Name = folderName,
                            CabFileName = folderName,
                            CabFilePath = Path.Combine(outputPath, "PreInstalled", "Apps", folderName),
                            PartitionName = partition.Name.Replace("\0", "-"),
                            Architecture = architecture,
                            Version = version,
                            SourceManifest = appFolder
                        });
                    }
                }
                catch { }
            }

            return packages;
        }

        private static List<IPartition> GetPartitionsWithServicing(IEnumerable<IDisk> disks, string path)
        {
            var result = new List<IPartition>();
            foreach (var disk in disks)
            {
                foreach (var partition in disk.Partitions)
                {
                    var fs = partition.FileSystem;
                    if (fs != null)
                    {
                        try
                        {
                            if (fs.DirectoryExists(path))
                            {
                                result.Add(partition);
                            }
                        }
                        catch { }
                    }
                }
            }
            return result;
        }

        public static List<PackageInfo> EnumerateCBS(IEnumerable<IDisk> disks, string outputPath, UpdateHistory.UpdateHistory? updateHistory)
        {
            var packages = new List<PackageInfo>();
            var partitions = GetPartitionsWithServicing(disks, @"Windows\Servicing\Packages");

            foreach (var partition in partitions)
            {
                var fs = partition.FileSystem!;
                var manifestFiles = fs.GetFilesWithNtfsIssueWorkaround(@"Windows\servicing\Packages", "*.mum", SearchOption.TopDirectoryOnly);

                foreach (var manifestFile in manifestFiles)
                {
                    try
                    {
                        using var stream = fs.OpenFile(manifestFile, FileMode.Open, FileAccess.Read);
                        var serializer = new XmlSerializer(typeof(XmlMum.Assembly));
                        var cbs = (XmlMum.Assembly)serializer.Deserialize(stream)!;

                        var (cabFileName, cabFile) = BuildMetadataHandler.GetPackageNamingForCBS(cbs, updateHistory);
                        string partitionName = partition.Name.Replace("\0", "-");

                        if (string.IsNullOrEmpty(cabFileName) && string.IsNullOrEmpty(cabFile))
                        {
                            string packageName = $"{cbs.AssemblyIdentity.Name.Replace($"_{cbs.AssemblyIdentity.Language}", "", StringComparison.InvariantCultureIgnoreCase)}";
                            if (!packageName.Contains("InboxCompDB"))
                            {
                                packageName = $"{packageName}~{cbs.AssemblyIdentity.PublicKeyToken.Replace("628844477771337a", "31bf3856ad364e35", StringComparison.InvariantCultureIgnoreCase)}~{cbs.AssemblyIdentity.ProcessorArchitecture}~{(cbs.AssemblyIdentity.Language == "neutral" ? "" : cbs.AssemblyIdentity.Language)}~";
                            }
                            if (!string.IsNullOrEmpty(cbs.Package.TargetPartition))
                            {
                                partitionName = cbs.Package.TargetPartition;
                            }
                            cabFileName = Path.Combine(partitionName, packageName);
                            cabFile = Path.Combine(outputPath, $"{cabFileName}.cab");
                        }
                        else
                        {
                            cabFile = Path.Combine(outputPath, cabFile);
                        }

                        packages.Add(new PackageInfo
                        {
                            Type = PackageType.CBS,
                            Name = Path.GetFileName(cabFileName),
                            CabFileName = cabFileName,
                            CabFilePath = cabFile,
                            PartitionName = partitionName,
                            Architecture = cbs.AssemblyIdentity.ProcessorArchitecture,
                            Version = cbs.AssemblyIdentity.Version,
                            PublicKeyToken = cbs.AssemblyIdentity.PublicKeyToken,
                            Language = cbs.AssemblyIdentity.Language,
                            SourceManifest = manifestFile
                        });
                    }
                    catch { }
                }
            }
            return packages;
        }

        public static List<PackageInfo> EnumerateSPKG(IEnumerable<IDisk> disks, string outputPath, UpdateHistory.UpdateHistory? updateHistory)
        {
            var packages = new List<PackageInfo>();
            var partitions = GetPartitionsWithServicing(disks, @"Windows\Packages\DsmFiles");

            foreach (var partition in partitions)
            {
                var fs = partition.FileSystem!;
                var manifestFiles = fs.GetFilesWithNtfsIssueWorkaround(@"Windows\Packages\DsmFiles", "*.xml", SearchOption.TopDirectoryOnly);

                foreach (var manifestFile in manifestFiles)
                {
                    try
                    {
                        XmlDsm.Package? dsm = null;
                        try
                        {
                            using var stream = fs.OpenFileAndDecompressAsGZip(manifestFile);
                            var serializer = new XmlSerializer(typeof(XmlDsm.Package));
                            dsm = (XmlDsm.Package)serializer.Deserialize(stream)!;
                        }
                        catch (InvalidDataException)
                        {
                            using var stream = fs.OpenFile(manifestFile, FileMode.Open, FileAccess.Read);
                            var serializer = new XmlSerializer(typeof(XmlDsm.Package));
                            dsm = (XmlDsm.Package)serializer.Deserialize(stream)!;
                        }

                        var (cabFileName, cabFile) = BuildMetadataHandler.GetPackageNamingForSPKG(dsm, updateHistory);
                        string partitionName = partition.Name.Replace("\0", "-");

                        if (string.IsNullOrEmpty(cabFileName) && string.IsNullOrEmpty(cabFile))
                        {
                            if (!string.IsNullOrEmpty(dsm.Partition))
                            {
                                partitionName = dsm.Partition.Replace("\0", "-");
                            }
                            string packageName = GetSPKGComponentName(dsm);
                            cabFileName = Path.Combine(partitionName, packageName);
                            cabFile = Path.Combine(outputPath, $"{cabFileName}.spkg");
                        }
                        else
                        {
                            cabFile = Path.Combine(outputPath, cabFile);
                        }

                        packages.Add(new PackageInfo
                        {
                            Type = PackageType.SPKG,
                            Name = Path.GetFileName(cabFileName),
                            CabFileName = cabFileName,
                            CabFilePath = cabFile,
                            PartitionName = partitionName,
                            SourceManifest = manifestFile
                        });
                    }
                    catch { }
                }
            }
            return packages;
        }

        private static string GetSPKGComponentName(XmlDsm.Package dsm)
        {
            if (dsm.Identity != null)
            {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(dsm.Identity.Owner)) parts.Add(dsm.Identity.Owner);
                if (!string.IsNullOrEmpty(dsm.Identity.Component)) parts.Add(dsm.Identity.Component);
                if (!string.IsNullOrEmpty(dsm.Identity.SubComponent)) parts.Add(dsm.Identity.SubComponent);
                if (parts.Count > 0) return string.Join(".", parts);
            }
            if (!string.IsNullOrEmpty(dsm.Partition)) return dsm.Partition;
            if (!string.IsNullOrEmpty(dsm.GroupingKey)) return dsm.GroupingKey;
            return "UnknownSPKG";
        }

        public static List<PackageInfo> EnumerateDrivers(IEnumerable<IDisk> disks, string outputPath, UpdateHistory.UpdateHistory? updateHistory)
        {
            var packages = new List<PackageInfo>();
            var partitions = GetPartitionsWithServicing(disks, @"Windows\System32\DriverStore\FileRepository");

            foreach (var partition in partitions)
            {
                var fs = partition.FileSystem!;
                var manifestFiles = fs.GetFilesWithNtfsIssueWorkaround(@"Windows\System32\DriverStore\FileRepository", "*.inf", SearchOption.AllDirectories);

                foreach (var manifestFile in manifestFiles)
                {
                    try
                    {
                        var (cabFileName, cabFile) = BuildMetadataHandler.GetPackageNamingForINF(manifestFile, updateHistory);
                        string partitionName = partition.Name.Replace("\0", "-");

                        if (string.IsNullOrEmpty(cabFileName) && string.IsNullOrEmpty(cabFile))
                        {
                            string packageName = Path.GetFileNameWithoutExtension(manifestFile);
                            cabFileName = Path.Combine(partitionName, packageName);
                            cabFile = Path.Combine(outputPath, $"{cabFileName}.cab");
                        }
                        else
                        {
                            cabFile = Path.Combine(outputPath, cabFile);
                        }

                        packages.Add(new PackageInfo
                        {
                            Type = PackageType.Driver,
                            Name = Path.GetFileName(cabFileName),
                            CabFileName = cabFileName,
                            CabFilePath = cabFile,
                            PartitionName = partitionName,
                            SourceManifest = manifestFile
                        });
                    }
                    catch { }
                }
            }
            return packages;
        }
    }
}
