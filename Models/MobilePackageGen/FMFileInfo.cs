namespace OneCore_Image_Designer.Models.MobilePackageGen
{
    public class FMFileInfo
    {
        public string Name { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public string DestinationPath { get; set; } = "";
        public string Vendor { get; set; } = "Microsoft";
        public string PartitionName { get; set; } = "";
        public long Size { get; set; }
        public bool Selected { get; set; } = true;

        /// <summary>分组列表一级键：按厂商（官方 GUI 的 FM_&lt;Vendor&gt; 分组范式）。</summary>
        public string GroupKey => string.IsNullOrWhiteSpace(Vendor) ? "FM" : $"FM · {Vendor}";
    }
}
