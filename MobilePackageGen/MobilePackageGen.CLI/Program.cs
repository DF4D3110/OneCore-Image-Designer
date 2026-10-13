namespace MobilePackageGen
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            Console.WriteLine(@"
Mobile Package Generator Tool
OneCore Image Designer Preview
");

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

            if (args.Length < 2)
            {
                PrintHelp();
                return;
            }

            // Optional switches (removed from the input list before resolving inputs/output):
            //   --uncompressed-manifests : decompress DCM manifests to plain XML in output cabinets
            List<string> argList = [.. args];

            if (argList.Remove("--uncompressed-manifests") || argList.Remove("/u"))
            {
                CBSBuilder.UseUncompressedManifests = true;
                Console.WriteLine("Manifests will be decompressed to plain XML (DCM compression disabled).");
            }

            string[] inputArgs = argList[..^1].ToArray();
            string outputFolder = argList[^1];

            // Route a copy of all logs into the build output directory.
            Logging.SetLogFile(Path.Combine(outputFolder, "build.log"));

            IEnumerable<IDisk> disks = DiskLoader.LoadDisks(inputArgs);

            if (!disks.Any())
            {
                PrintHelp();
                return;
            }

            BuildMetadataHandler.GetOEMInput(disks, outputFolder);
            BuildMetadataHandler.GetFeatureManifests(disks, outputFolder);

            List<AppxPackage> appList = BuildMetadataHandler.GetAppList(disks);

            AppBuilder.BuildApps(disks, outputFolder, appList);

            UpdateHistory.UpdateHistory? updateHistory = BuildMetadataHandler.GetUpdateHistory(disks);

            CBSBuilder.BuildCBS(disks, outputFolder, updateHistory);

            SPKGBuilder.BuildSPKG(disks, outputFolder, updateHistory);

            DriverBuilder.BuildDrivers(disks, outputFolder, updateHistory);

            Console.WriteLine("The operation completed successfully.");
        }

        private static void PrintHelp()
        {
            Console.WriteLine("Remember to run the tool as Trusted Installer (TI).");
            Console.WriteLine();
            Console.WriteLine("You need to pass at least 2 parameters:");
            Console.WriteLine(@"	<Path to MainOS/Data/EFIESP> <Output folder CBSs>");
            Console.WriteLine("Examples:");
            Console.WriteLine(@"	 D: C:\OutputCabs");
            Console.WriteLine(@"	 D: E: F: C:\OutputCabs");
            Console.WriteLine();
            Console.WriteLine("Optional switch:  --uncompressed-manifests  (decompress DCM manifests to plain XML in output cabinets)");
            Console.WriteLine();
            Console.WriteLine("You need to pass 2 parameters:");
            Console.WriteLine(@"	<Path to FFU File> <Output folder CBSs>");
            Console.WriteLine("Example:");
            Console.WriteLine(@"	 D:\Flash.ffu C:\OutputCabs");
            Console.WriteLine();
            Console.WriteLine("You need to pass at least 2 parameters:");
            Console.WriteLine(@"	<Path to VHDx> <Output folder CBSs>");
            Console.WriteLine("Examples:");
            Console.WriteLine(@"	 D:\LUN0.vhdx C:\OutputCabs");
            Console.WriteLine(@"	 D:\LUN0.vhdx D:\LUN1.vhdx D:\LUN2.vhdx C:\OutputCabs");
            Console.WriteLine();
            Console.WriteLine("You need to pass 2 parameters:");
            Console.WriteLine(@"	<Path to WIM File> <Output folder CBSs>");
            Console.WriteLine("Example:");
            Console.WriteLine(@"	 D:\Flash.wim C:\OutputCabs");
        }
    }
}