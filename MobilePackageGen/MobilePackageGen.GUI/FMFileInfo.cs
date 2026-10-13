namespace MobilePackageGen.GUI
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
    }
}
