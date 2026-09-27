namespace MobilePackageGen
{
    public class AppxPackage
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string License { get; set; }
        public string ID { get; set; }

        public string[] Architectures { get; set; }

        public AppxPackage(string name, string path, string license, string id, string[] architectures)
        {
            Name = name;
            Path = path;
            License = license;
            ID = id;
            Architectures = architectures;
        }

        public override string ToString()
        {
            return ID ?? System.IO.Path.Combine(Path, Name);
        }
    }
}
