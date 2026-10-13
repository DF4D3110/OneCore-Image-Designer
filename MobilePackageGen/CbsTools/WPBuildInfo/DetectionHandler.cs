using DiscUtils.Registry;

namespace WPBuildInfo
{
    internal class DetectionHandler
    {
        public static string ExtractVersionInfo2(Stream hiveStream)
        {
            string BranchName = "";
            ulong BuildNumber = 0;
            string CompileDate = "";
            ulong DeltaVersion = 0;
            ulong MajorVersion = 0;
            ulong MinorVersion = 0;

            using (RegistryHive hive = new(hiveStream))
            {
                try
                {
                    RegistryKey subkey = hive.Root.OpenSubKey(@"Microsoft\Windows NT\CurrentVersion");

                    string buildLab = (string)subkey.GetValue("BuildLab");
                    string buildLabEx = (string)subkey.GetValue("BuildLabEx");

                    string releaseId = (string)subkey.GetValue("ReleaseId");

                    int? major = (int?)subkey.GetValue("CurrentMajorVersionNumber");
                    string build = (string)subkey.GetValue("CurrentBuildNumber");
                    int? minor = (int?)subkey.GetValue("CurrentMinorVersionNumber");
                    int? ubr = (int?)subkey.GetValue("UBR");
                    string branch = null;

                    subkey = hive.Root.OpenSubKey(
                        @"Microsoft\Windows NT\CurrentVersion\Update\TargetingInfo\Installed");
                    if (subkey != null)
                    {
                        foreach (RegistryKey sub in subkey.SubKeys)
                        {
                            if (!sub.Name.Contains(".OS."))
                            {
                                continue;
                            }

                            branch = sub.GetValue("Branch") as string;
                        }
                    }

                    if (!string.IsNullOrEmpty(buildLab) && buildLab.Count(x => x == '.') == 2)
                    {
                        string[] splitLab = buildLab.Split('.');

                        BranchName = splitLab[1];
                        CompileDate = splitLab[2];
                        BuildNumber = ulong.Parse(splitLab[0]);
                    }

                    if (!string.IsNullOrEmpty(buildLabEx) && buildLabEx.Count(x => x == '.') == 4)
                    {
                        string[] splitLabEx = buildLabEx.Split('.');

                        BranchName = splitLabEx[3];
                        CompileDate = splitLabEx[4];
                        DeltaVersion = ulong.Parse(splitLabEx[1]);
                        BuildNumber = ulong.Parse(splitLabEx[0]);
                    }

                    if (major.HasValue)
                    {
                        MajorVersion = (ulong)major.Value;
                    }

                    if (minor.HasValue)
                    {
                        MinorVersion = (ulong)minor.Value;
                    }

                    if (!string.IsNullOrEmpty(build))
                    {
                        BuildNumber = ulong.Parse(build);
                    }

                    if (ubr.HasValue)
                    {
                        DeltaVersion = (ulong)ubr.Value;
                    }

                    if (!string.IsNullOrEmpty(branch))
                    {
                        BranchName = branch;
                    }
                }
                catch
                {
                }
            }

            return $"{MajorVersion}.{MinorVersion}.{BuildNumber}.{DeltaVersion} ({BranchName}.{CompileDate})";
        }
    }
}
