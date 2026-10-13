namespace WPMT.Core.Common;

public static class DirectoryUtils
{
    public static void CopyDirectory(string sourceDir, string destinationDir, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(sourceDir);
        ArgumentNullException.ThrowIfNull(destinationDir);

        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"Source directory does not exist: {sourceDir}");

        Directory.CreateDirectory(destinationDir);

        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string destFile = Path.Combine(destinationDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite);
        }

        foreach (string subdir in Directory.GetDirectories(sourceDir))
        {
            string destSubdir = Path.Combine(destinationDir, Path.GetFileName(subdir));
            CopyDirectory(subdir, destSubdir, overwrite);
        }
    }
}