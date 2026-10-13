namespace WPMT.Core.Common;

internal static class Config
{
    internal const string ConfigFileName = "config.json";


    internal static void Load()
    {
        if (File.Exists(ConfigFileName))
        {
            string json = File.ReadAllText(ConfigFileName);
        }
    }
}
