namespace OneCore_Image_Designer.Models.MobilePackageGen
{
    public enum PackageType
    {
        CBS,
        SPKG,
        Driver
    }

    public class PackageInfo
    {
        public PackageType Type { get; set; }
        public string Name { get; set; } = "";
        public string CabFileName { get; set; } = "";
        public string CabFilePath { get; set; } = "";
        public string PartitionName { get; set; } = "";
        public string Architecture { get; set; } = "";
        public string Version { get; set; } = "";
        public string PublicKeyToken { get; set; } = "";
        public string Language { get; set; } = "";
        public string SourceManifest { get; set; } = "";
        public bool Selected { get; set; } = true;
        public long EstimatedSize { get; set; }
        public string Description { get; set; } = "";

        // 磁盘和存储池信息
        public int DiskIndex { get; set; } = -1;
        public bool IsFromStoragePool { get; set; }

        // 特殊项标记
        public bool IsOEMInput { get; set; }

        public string DiskDisplay => DiskIndex >= 0 ? $"磁盘{DiskIndex}" : "未知磁盘";
        public string StoragePoolDisplay => IsFromStoragePool ? " [存储池]" : "";
        public string FullLocationDisplay => $"{DiskDisplay}{StoragePoolDisplay}\\{PartitionName}";

        public string TypeDisplay
        {
            get
            {
                if (IsOEMInput) return "OEMInput";
                return Type switch
                {
                    PackageType.CBS => "CBS",
                    PackageType.SPKG => "SPKG",
                    PackageType.Driver => "Driver",
                    _ => "Unknown"
                };
            }
        }

        public string SizeDisplay => EstimatedSize switch
        {
            > 1073741824 => $"{EstimatedSize / 1073741824.0:F2} GB",
            > 1048576 => $"{EstimatedSize / 1048576.0:F2} MB",
            > 1024 => $"{EstimatedSize / 1024.0:F2} KB",
            _ => $"{EstimatedSize} B"
        };
    }
}
