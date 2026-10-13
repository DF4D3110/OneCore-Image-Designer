namespace WPMT.Core.Catalog;

public class CatalogHash
{
    public string Identifier { get; set; }
    public string FilePath { get; set; }
    public bool PageHashes { get; set; }
    public List<CatalogAttribute> Attributes { get; set; }

    public CatalogHash(string identifier, string filePath)
    {
        Identifier = identifier;
        FilePath = filePath;
        PageHashes = false;
        Attributes = [];
    }

    public CatalogHash(string filePath) : this(filePath.Replace('=', '~'), filePath)
    {
    }

    public void Save(StreamWriter writer)
    {
        writer.WriteLine($"<hash>{Identifier}={FilePath}");
        if (PageHashes)
            writer.WriteLine($"<hash>{Identifier}PageHashes={PageHashes}");
        for (int i = 0; i < Attributes.Count; i++)
        {
            writer.WriteLine($"<hash>{Identifier}ATTR{i + 1}={Attributes[i]}");
        }
    }
}