using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace MobilePackageGen
{
    /// <summary>
    /// TrustedInstaller elevation helper based on NSudo Shared Library
    /// (NSudoAPI.dll, M2-Team / Mouri_Naruto, MIT License).
    /// Detects whether the current process runs in the TrustedInstaller
    /// security context and, if not, relaunches itself as TrustedInstaller
    /// via NSudoCreateProcess.
    /// </summary>
    public static class TrustedInstallerHelper
    {
        // Well-known SIDs of the elevated contexts we accept.
        // The TrustedInstaller service itself runs as LocalSystem, so NSudo's
        // TRUSTED_INSTALLER mode yields a SYSTEM user SID (S-1-5-18) with the
        // TI service privileges; the dedicated TI SID appears only for the
        // service account name. Accept both to avoid relaunch loops.
        private static readonly string[] ElevatedSids =
        {
            "S-1-5-18", // SYSTEM
            "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", // TrustedInstaller
        };

        // TokenInformationClass.TokenUser
        private const int TokenUser = 1;

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(
            IntPtr ProcessHandle,
            uint DesiredAccess,
            out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(
            IntPtr TokenHandle,
            int TokenInformationClass,
            IntPtr TokenInformation,
            uint TokenInformationLength,
            out uint ReturnLength);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool ConvertSidToStringSid(
            IntPtr Sid,
            out IntPtr StringSid);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);

        private const uint TOKEN_QUERY = 0x0008;

        /// <summary>
        /// Returns true when the current process token's user SID is the
        /// TrustedInstaller service account.
        /// </summary>
        public static bool IsTrustedInstaller()
        {
            IntPtr tokenHandle = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out tokenHandle))
                {
                    return false;
                }

                // First call to get the required buffer size.
                uint size = 0;
                GetTokenInformation(tokenHandle, TokenUser, IntPtr.Zero, 0, out size);
                if (size == 0)
                {
                    return false;
                }

                IntPtr buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (!GetTokenInformation(tokenHandle, TokenUser, buffer, size, out size))
                    {
                        return false;
                    }

                    // TOKEN_USER: first member is SID_AND_ATTRIBUTES (Sid + Attributes).
                    IntPtr sid = Marshal.ReadIntPtr(buffer);
                    IntPtr sidStringPtr;
                    if (!ConvertSidToStringSid(sid, out sidStringPtr))
                    {
                        return false;
                    }

                    try
                    {
                        // ConvertSidToStringSid returns an ANSI string.
                        string sidString = Marshal.PtrToStringAnsi(sidStringPtr);
                        foreach (string elevatedSid in ElevatedSids)
                        {
                            if (string.Equals(
                                sidString,
                                elevatedSid,
                                StringComparison.OrdinalIgnoreCase))
                            {
                                return true;
                            }
                        }

                        return false;
                    }
                    finally
                    {
                        LocalFree(sidStringPtr);
                    }
                }
                finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
            finally
            {
                if (tokenHandle != IntPtr.Zero)
                {
                    CloseHandle(tokenHandle);
                }
            }
        }

        /// <summary>
        /// Relaunches the current process under the TrustedInstaller security
        /// context using the NSudo Shared Library. Returns 0 on success;
        /// throws when NSudoAPI.dll cannot be loaded or creation fails.
        /// </summary>
        /// <param name="commandLine">
        /// Full command line for the new process (executable path plus
        /// arguments). Pass Environment.CommandLine to relaunch with the
        /// same arguments.
        /// </param>
        /// <param name="currentDirectory">Working directory of the new process; pass null to inherit.</param>
        /// <param name="waitInterval">Wait interval in ms; use uint.MaxValue (INFINITE) to wait for the child.</param>
        public static int ElevateToTrustedInstaller(
            string commandLine,
            string currentDirectory,
            uint waitInterval = uint.MaxValue)
        {
            var binaryPaths = new Dictionary<Architecture, string>
            {
                [Architecture.X64] = System.IO.Path.Combine(AppContext.BaseDirectory, "x64", "NSudoAPI.dll"),
                [Architecture.X86] = System.IO.Path.Combine(AppContext.BaseDirectory, "Win32", "NSudoAPI.dll"),
                [Architecture.Arm64] = System.IO.Path.Combine(AppContext.BaseDirectory, "ARM64", "NSudoAPI.dll"),
            };

            // NSudoInstance releases the library handle via its finalizer.
            var nsudo = new M2.NSudo.NSudoInstance(binaryPaths);

            nsudo.CreateProcess(
                M2.NSudo.NSUDO_USER_MODE_TYPE.TRUSTED_INSTALLER,
                M2.NSudo.NSUDO_PRIVILEGES_MODE_TYPE.ENABLE_ALL_PRIVILEGES,
                M2.NSudo.NSUDO_MANDATORY_LABEL_TYPE.SYSTEM,
                M2.NSudo.NSUDO_PROCESS_PRIORITY_CLASS_TYPE.NORMAL,
                M2.NSudo.NSUDO_SHOW_WINDOW_MODE_TYPE.DEFAULT,
                waitInterval,
                false,
                commandLine,
                currentDirectory);

            return 0;
        }

        /// <summary>
        /// Convenience wrapper: relaunches the current process with its
        /// original command line under the TrustedInstaller context.
        /// </summary>
        /// <param name="entryAssemblyPath">
        /// Absolute path of the entry assembly (the CLI/GUI .dll) to launch.
        /// </param>
        /// <param name="args">Original command-line arguments.</param>
        public static void ElevateSelf(string entryAssemblyPath, string[] args)
        {
            // The apphost (.exe) cannot locate the .NET runtime inside the
            // TrustedInstaller service context (System.Runtime load fails),
            // while "dotnet <assembly> ..." works. Rebuild the command line
            // to launch our own assembly via the dotnet muxer.
            string dotnet = FindDotNet();
            string argumentText = string.Join(
                " ",
                args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));

            ElevateToTrustedInstaller(
                "\"" + dotnet + "\" \"" + entryAssemblyPath + "\" " + argumentText,
                Environment.CurrentDirectory,
                uint.MaxValue);
        }

        private static string FindDotNet()
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string candidate = System.IO.Path.Combine(
                windows,
                "Program Files",
                "dotnet",
                "dotnet.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            // Fall back to PATH resolution.
            return "dotnet";
        }
    }
}
