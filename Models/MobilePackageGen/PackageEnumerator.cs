using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using MobilePackageGen;
using MobilePackageGen.GZip;
using UpdateHistoryClass = MobilePackageGen.UpdateHistory.UpdateHistory;
using XmlDsmPackage = MobilePackageGen.XmlDsm.Package;
using XmlMum = MobilePackageGen.XmlMum;
using OneCore_Image_Designer.Models.MobilePackageGen;

namespace OneCore_Image_Designer.Models.MobilePackageGen
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

        public static List<PackageInfo> EnumerateCBS(IEnumerable<IDisk> disks, string outputPath, UpdateHistoryClass? updateHistory)
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

        public static List<PackageInfo> EnumerateSPKG(IEnumerable<IDisk> disks, string outputPath, UpdateHistoryClass? updateHistory)
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
                        XmlDsmPackage? dsm = null;
                        try
                        {
                            using var stream = fs.OpenFileAndDecompressAsGZip(manifestFile);
                            var serializer = new XmlSerializer(typeof(XmlDsmPackage));
                            dsm = (XmlDsmPackage)serializer.Deserialize(stream)!;
                        }
                        catch (InvalidDataException)
                        {
                            using var stream = fs.OpenFile(manifestFile, FileMode.Open, FileAccess.Read);
                            var serializer = new XmlSerializer(typeof(XmlDsmPackage));
                            dsm = (XmlDsmPackage)serializer.Deserialize(stream)!;
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

        private static string GetSPKGComponentName(XmlDsmPackage dsm)
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

        public static List<PackageInfo> EnumerateDrivers(IEnumerable<IDisk> disks, string outputPath, UpdateHistoryClass? updateHistory)
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
                            SourceManifest = manifestFile,
                            // 官方驱动 INF 基名以 microsoft 开头（22621 Common-Drivers 包实证：
                            // microsoft-* 为 inbox，dual_*/厂商名 为第三方）
                            IsThirdPartyDriver = !Path.GetFileNameWithoutExtension(manifestFile)
                                .StartsWith("microsoft", StringComparison.OrdinalIgnoreCase)
                        });
                    }
                    catch { }
                }
            }
            return packages;
        }
    }
}


