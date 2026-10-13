namespace MobilePackageGen.GUI
{
    public enum PackageType
    {
        CBS,
        SPKG,
        Driver,
        Appx
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

        public string TypeDisplay => Type switch
        {
            PackageType.CBS => "CBS",
            PackageType.SPKG => "SPKG",
            PackageType.Driver => "Driver",
            PackageType.Appx => "Appx",
            _ => "Unknown"
        };

        public string SizeDisplay => EstimatedSize switch
        {
            > 1073741824 => $"{EstimatedSize / 1073741824.0:F2} GB",
            > 1048576 => $"{EstimatedSize / 1048576.0:F2} MB",
            > 1024 => $"{EstimatedSize / 1024.0:F2} KB",
            _ => $"{EstimatedSize} B"
        };
    }
}
