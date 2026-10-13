namespace WPMT.Core.Catalog;

public class CdfBuilder
{
    public string CdfPath { get; set; }
    public string Name { get; set; }
    public string ResultDir { get; set; }
    public int PublicVersion { get; set; }
    public int EncodingType { get; set; }
    public bool PageHashes { get; set; }
    public int CatalogVersion { get; set; }
    public string HashAlgorithms { get; set; }
    public bool NonConformantFilesFallbackFlat { get; set; }
    public List<CatalogAttribute> Attributes { get; set; }
    public List<CatalogHash> Hashes { get; set; }

    public CdfBuilder(string path, string name, string resultDir, int encodingType, string hashAlgorithms = "SHA256")
    {
        CdfPath = path;
        Name = name;
        ResultDir = resultDir;
        PublicVersion = 1;
        EncodingType = encodingType;
        PageHashes = false;
        CatalogVersion = 2;
        HashAlgorithms = hashAlgorithms;
        NonConformantFilesFallbackFlat = false;
        Attributes = [];
        Hashes = [];
    }

    public void Save()
    {
        using StreamWriter writer = new(CdfPath);
        writer.WriteLine("[CatalogHeader]");
        writer.WriteLine($"Name={Name}");
        writer.WriteLine($"ResultDir={ResultDir}");
        writer.WriteLine($"PublicVersion=0x{PublicVersion:X8}");
        writer.WriteLine($"EncodingType=0x{EncodingType:X8}");
        writer.WriteLine($"PageHashes={PageHashes}");
        writer.WriteLine($"CatalogVersion={CatalogVersion}");
        writer.WriteLine($"HashAlgorithms={HashAlgorithms}");
        writer.WriteLine($"NonConformantFilesFallbackFlat={NonConformantFilesFallbackFlat}");
        for (int i = 0; i < Attributes.Count; i++)
        {
            writer.WriteLine($"CATATTR{i + 1}={Attributes[i]}");
        }
        writer.WriteLine();
        writer.WriteLine("[CatalogFiles]");
        foreach (CatalogHash hash in Hashes)
        {
            hash.Save(writer);
        }
        writer.WriteLine();
    }
}