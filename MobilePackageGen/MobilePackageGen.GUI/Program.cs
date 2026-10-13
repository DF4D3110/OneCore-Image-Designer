using System.Runtime.InteropServices;

namespace MobilePackageGen.GUI
{
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll")]
        private static extern bool FreeConsole();

        [STAThread]
        static void Main(string[] args)
        {
            try { AllocConsole(); } catch { }

            // Elevate to TrustedInstaller when not already running as TI.
            if (!TrustedInstallerHelper.IsTrustedInstaller())
            {
                try
                {
                    Console.WriteLine("Relaunching under TrustedInstaller...");
                    TrustedInstallerHelper.ElevateSelf(typeof(Program).Assembly.Location, args);
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Warning] Failed to elevate to TrustedInstaller: {ex.Message}");
                    Console.WriteLine("Continuing with current privileges; some operations may fail.");
                }
            }

            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
            try { FreeConsole(); } catch { }
        }
    }
}
