using static WPMT.Core.Compression.Common;

namespace WPMT.Core.Common;

public static class OtherUtils
{
    public static string GetComponentFilePath(string componentDirectory, string fileName, ComponentFileCompressionType type)
    {
        return type switch
        {
            ComponentFileCompressionType.None => Path.Combine(componentDirectory, fileName),
            ComponentFileCompressionType.Forward => Path.Combine(componentDirectory, "f", fileName),
            ComponentFileCompressionType.Null => Path.Combine(componentDirectory, "n", fileName),
            ComponentFileCompressionType.Reverse => Path.Combine(componentDirectory, "r", fileName),
            ComponentFileCompressionType.Invalid or _ => throw new InvalidOperationException()
        };
    }

    public static ComponentFileCompressionType GetComponentFileCompressionType(string componentDirectory, string fileName, bool ignoreNonDelta = false)
    {
        if (!ignoreNonDelta && File.Exists(GetComponentFilePath(componentDirectory, fileName, ComponentFileCompressionType.None)))
            return ComponentFileCompressionType.None;

        if (File.Exists(GetComponentFilePath(componentDirectory, fileName, ComponentFileCompressionType.Forward)))
            return ComponentFileCompressionType.Forward;

        if (File.Exists(GetComponentFilePath(componentDirectory, fileName, ComponentFileCompressionType.Null)))
            return ComponentFileCompressionType.Null;
        /*
        if (File.Exists(GetComponentFilePath(componentDirectory, fileName, ComponentFileCompressionType.Reverse)))
            return ComponentFileCompressionType.Reverse;
        */

        return ComponentFileCompressionType.Invalid;
    }
}