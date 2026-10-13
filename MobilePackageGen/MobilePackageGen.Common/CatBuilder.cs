using System.Diagnostics;
using System.Runtime.InteropServices;
using WPMT.Core.Catalog;

namespace MobilePackageGen
{
    /// <summary>
    /// Inline catalog (.cat) builder for CBS packages.
    /// Pipeline: CDF (WPMT CdfBuilder, hashes the actual files) ->
    ///           makecat (produces the unsigned catalog with member hashes) ->
    ///           signtool (signs with the TestCatSigner certificate).
    /// Because the hashes come from the same files that go into the cabinet,
    /// the catalog always matches the cabinet content - imageapp hash checks pass.
    /// </summary>
    public static class CatBuilder
    {
        // Tool locations. Default to the 17763 ADK tools; override via properties.
        public static string MakecatPath { get; set; } = @"E:\Assessment and Deployment Kit\adk-src\adk\17763\Windows Kits\10\tools\bin\i386\makecat.exe";
        public static string SigntoolPath { get; set; } = @"E:\Assessment and Deployment Kit\adk-src\adk\17763\Windows Kits\10\tools\bin\i386\signtool.exe";

        // TestCatSigner certificate (CurrentUser\My)
        public const string TestCatSignerThumbprint = "BC92FF5B9634EA707AD592A38C871B29C9A8216D";

        /// <summary>
        /// Writes a makecat-compatible CDF that lists every file to be hashed.
        /// </summary>
        public static void WriteCdf(string cdfPath, string catalogName, string resultDir, IEnumerable<CatalogFileEntry> files)
        {
            CdfBuilder cdf = new(cdfPath, catalogName, resultDir, 0x00010001, "SHA256")
            {
                NonConformantFilesFallbackFlat = false,
                PageHashes = false,
                CatalogVersion = 2
            };

            foreach (CatalogFileEntry file in files)
            {
                // WPMT's CdfBuilder writes "<hash>{Identifier}={FilePath}" which makecat accepts
                // (verified: both <HASH> and <hash> prefixes parse, tag is arbitrary).
                cdf.Hashes.Add(new CatalogHash(file.Tag, file.FilePath));
            }

            cdf.Save();
        }

        /// <summary>
        /// Runs makecat on the CDF, then signs the produced catalog with signtool.
        /// </summary>
        /// <param name="cdfPath">Path to the CDF file.</param>
        /// <param name="catalogFullPath">Expected output catalog path (resultDir\name) - verified after build.</param>
        public static void BuildCatalog(string cdfPath, string catalogFullPath)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new PlatformNotSupportedException("Catalog building is only supported on Windows (makecat/signtool).");
            }

            Run(MakecatPath, $"-v \"{cdfPath}\"");

            if (!File.Exists(catalogFullPath))
            {
                throw new FileNotFoundException($"makecat did not produce the expected catalog: {catalogFullPath}");
            }

            Run(SigntoolPath, $"sign /sha1 {TestCatSignerThumbprint} /fd SHA256 \"{catalogFullPath}\"");

            if (!File.Exists(catalogFullPath) || new FileInfo(catalogFullPath).Length == 0)
            {
                throw new FileNotFoundException($"signtool failed to produce a signed catalog: {catalogFullPath}");
            }
        }

        /// <summary>
        /// Convenience: write CDF + build + sign in one call.
        /// </summary>
        public static void BuildCatalog(string cdfPath, string catalogName, string resultDir, IEnumerable<CatalogFileEntry> files)
        {
            WriteCdf(cdfPath, catalogName, resultDir, files);
            BuildCatalog(cdfPath, Path.Combine(resultDir, catalogName));
        }

        private static void Run(string exePath, string arguments)
        {
            ProcessStartInfo psi = new()
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using Process process = Process.Start(psi)!;

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            process.WaitForExit();

            Logging.Log(stdout.TrimEnd(), LoggingLevel.Information);
            if (!string.IsNullOrEmpty(stderr))
            {
                Logging.Log(stderr.TrimEnd(), LoggingLevel.Warning);
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"{Path.GetFileName(exePath)} failed with exit code {process.ExitCode}.\n{stderr}");
            }
        }
    }

    /// <summary>
    /// One catalog member: tag (name inside the catalog) + path to the file to hash.
    /// </summary>
    public readonly record struct CatalogFileEntry(string Tag, string FilePath);
}
