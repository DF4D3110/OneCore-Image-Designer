namespace WPMT.Core.Catalog;

public class CatalogAttribute
{
    public int Type { get; set; }
    public string Name { get; set; }
    public string Value { get; set; }

    public CatalogAttribute(int type, string name, string value)
    {
        Type = type;
        Name = name;
        Value = value;
    }

    public override string ToString()
    {
        return $"0x{Type:X8}:{Name}:{Value}";
    }
}