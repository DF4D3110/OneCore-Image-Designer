using System.Diagnostics;

namespace WPMT.Core.Logging;

public static class Log
{
    public static void InfoDebug(string message)
    {
        Debug.WriteLine(message);
    }

    public static void Error(string message)
    {
        Debug.WriteLine($"[ERROR] {message}");
    }
}
