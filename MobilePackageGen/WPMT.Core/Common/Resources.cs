using System.Reflection;

namespace WPMT.Core.Common;

public static class Resources
{
    public static byte[] LoadResource(string namespaceName, string resourceName)
    {
        string fullResourceName = $"{namespaceName}.{resourceName}";
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(fullResourceName) ?? throw new FileNotFoundException($"Resource '{fullResourceName}' not found.");
        using MemoryStream memoryStream = new();
        stream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }
}