using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace WPMT.Core.Compression;

internal static partial class NativeMethods
{
    public static bool PreferUpdateCompression = true;

    internal static bool ApplyDeltaB(
        DeltaFlagType ApplyFlags,
        DELTA_INPUT Source,
        DELTA_INPUT Delta,
        out DELTA_OUTPUT lpTarget)
    {
        if ((PreferUpdateCompression || !MsDelta.IsAvailable) && UpdateCompression.IsAvailable)
            return UpdateCompression.ApplyDeltaB(ApplyFlags, Source, Delta, out lpTarget);
        else if (MsDelta.IsAvailable)
            return MsDelta.ApplyDeltaB(ApplyFlags, Source, Delta, out lpTarget);
        throw new FileNotFoundException("UpdateCompression.dll or msdelta.dll not found.");
    }

    internal static bool CreateDeltaB(
        DeltaFileType FileTypeSet,
        DeltaFlagType SetFlags,
        DeltaFlagType ResetFlags,
        DELTA_INPUT Source,
        DELTA_INPUT Target,
        DELTA_INPUT SourceOptions,
        DELTA_INPUT TargetOptions,
        DELTA_INPUT GlobalOptions,
        FILETIME TargetFileTime,
        AlgorithmId HashAlgId,
        out DELTA_OUTPUT lpDelta)
    {
        if ((PreferUpdateCompression || !MsDelta.IsAvailable) && UpdateCompression.IsAvailable)
            return UpdateCompression.CreateDeltaB(
                FileTypeSet,
                SetFlags,
                ResetFlags,
                Source,
                Target,
                SourceOptions,
                TargetOptions,
                GlobalOptions,
                TargetFileTime,
                HashAlgId,
                out lpDelta);

        if (MsDelta.IsAvailable)
            return MsDelta.CreateDeltaB(
                FileTypeSet,
                SetFlags,
                ResetFlags,
                Source,
                Target,
                SourceOptions,
                TargetOptions,
                GlobalOptions,
                TargetFileTime,
                HashAlgId,
                out lpDelta);

        throw new FileNotFoundException("UpdateCompression.dll or msdelta.dll not found.");
    }

    internal static bool ApplyDeltaProvidedB(
        DeltaFlagType ApplyFlags,
        DELTA_INPUT Source,
        DELTA_INPUT Delta,
        IntPtr lpTarget,
        UIntPtr uTargetSize)
    {
        if ((PreferUpdateCompression || !MsDelta.IsAvailable) && UpdateCompression.IsAvailable)
            return UpdateCompression.ApplyDeltaProvidedB(ApplyFlags, Source, Delta, lpTarget, uTargetSize);

        if (MsDelta.IsAvailable)
            return MsDelta.ApplyDeltaProvidedB(ApplyFlags, Source, Delta, lpTarget, uTargetSize);

        throw new FileNotFoundException("UpdateCompression.dll or msdelta.dll not found.");
    }

    internal static bool GetDeltaInfoB(
        DELTA_INPUT Delta,
        out DeltaHeaderInfo lpHeaderInfo)
    {
        if ((PreferUpdateCompression || !MsDelta.IsAvailable) && UpdateCompression.IsAvailable)
            return UpdateCompression.GetDeltaInfoB(Delta, out lpHeaderInfo);

        if (MsDelta.IsAvailable)
            return MsDelta.GetDeltaInfoB(Delta, out lpHeaderInfo);

        throw new FileNotFoundException("UpdateCompression.dll or msdelta.dll not found.");
    }

    internal static bool GetDeltaInfoExB(
        DELTA_INPUT Delta,
        out DeltaHeaderInfoEx lpHeaderInfo,
        UIntPtr HeaderInfoSize)
    {
        if (UpdateCompression.IsAvailable)
            return UpdateCompression.GetDeltaInfoExB(Delta, out lpHeaderInfo, HeaderInfoSize);

        throw new FileNotFoundException("UpdateCompression.dll not found.");
    }

    internal static bool GetDeltaSignatureB(
        DeltaFileType FileTypeSet,
        AlgorithmId HashAlgId,
        DELTA_INPUT Source,
        out DeltaHash lpHash)
    {
        if ((PreferUpdateCompression || !MsDelta.IsAvailable) && UpdateCompression.IsAvailable)
            return UpdateCompression.GetDeltaSignatureB(FileTypeSet, HashAlgId, Source, out lpHash);

        if (MsDelta.IsAvailable)
            return MsDelta.GetDeltaSignatureB(FileTypeSet, HashAlgId, Source, out lpHash);

        throw new FileNotFoundException("UpdateCompression.dll or msdelta.dll not found.");
    }

    internal static bool DeltaNormalizeProvidedB(
        DeltaFileType FileTypeSet,
        DeltaFlagType NormalizeFlags,
        DELTA_INPUT NormalizeOptions,
        IntPtr Source,
        UIntPtr uSourceSize)
    {
        if ((PreferUpdateCompression || !MsDelta.IsAvailable) && UpdateCompression.IsAvailable)
            return UpdateCompression.DeltaNormalizeProvidedB(FileTypeSet, NormalizeFlags, NormalizeOptions, Source, uSourceSize);

        if (MsDelta.IsAvailable)
            return MsDelta.DeltaNormalizeProvidedB(FileTypeSet, NormalizeFlags, NormalizeOptions, Source, uSourceSize);

        throw new FileNotFoundException("UpdateCompression.dll or msdelta.dll not found.");
    }

    internal static bool ApplyDeltaGetReverseB(
        DeltaFlagType ApplyFlags,
        DELTA_INPUT Source,
        DELTA_INPUT Delta,
        FILETIME ReverseFileTime,
        out DELTA_OUTPUT lpTarget,
        out DELTA_OUTPUT lpTargetReverse)
    {
        if ((PreferUpdateCompression || !MsDelta.IsAvailable) && UpdateCompression.IsAvailable)
            return UpdateCompression.ApplyDeltaGetReverseB(ApplyFlags, Source, Delta, ReverseFileTime, out lpTarget, out lpTargetReverse);

        if (MsDelta.IsAvailable)
            return MsDelta.ApplyDeltaGetReverseB(ApplyFlags, Source, Delta, ReverseFileTime, out lpTarget, out lpTargetReverse);

        throw new FileNotFoundException("UpdateCompression.dll or msdelta.dll not found.");
    }

    internal static bool DeltaFree(IntPtr lpMemory)
    {
        if ((PreferUpdateCompression || !MsDelta.IsAvailable) && UpdateCompression.IsAvailable)
            return UpdateCompression.DeltaFree(lpMemory);

        if (MsDelta.IsAvailable)
            return MsDelta.DeltaFree(lpMemory);

        throw new FileNotFoundException("UpdateCompression.dll or msdelta.dll not found.");
    }

    public enum AlgorithmId : uint
    {
        None = 0,
        CRC32 = 32
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DELTA_INPUT
    {
        public IntPtr lpStart; // lpcStart
        public UIntPtr uSize;
        [MarshalAs(UnmanagedType.Bool)]
        public bool Editable;

        public DELTA_INPUT()
        {
            lpStart = IntPtr.Zero;
            uSize = UIntPtr.Zero;
            Editable = false;
        }

        public DELTA_INPUT(byte[]? array, int offset, bool editable)
        {
            if (array is null)
            {
                lpStart = IntPtr.Zero;
                uSize = UIntPtr.Zero;
                Editable = false;
                return;
            }
            lpStart = Marshal.UnsafeAddrOfPinnedArrayElement(array, offset);
            uSize = (UIntPtr)(array.Length - offset);
            Editable = editable;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DELTA_OUTPUT
    {
        public IntPtr lpStart;
        public UIntPtr uSize;
    }

#pragma warning disable IDE0079
#pragma warning disable CA1069
    [Flags]
    public enum DeltaFileType : long
    {
        None = 0,
        Raw = 1 << 0, // DELTA_FILE_TYPE_RAW
        I386 = 1 << 1, // DELTA_FILE_TYPE_I386
        Ia64 = 1 << 2, // DELTA_FILE_TYPE_IA64
        Amd64 = 1 << 3, // DELTA_FILE_TYPE_AMD64
        Cli4I386 = 1 << 4, // DELTA_FILE_TYPE_CLI4_I386
        Cli4Amd64 = 1 << 5, // DELTA_FILE_TYPE_CLI4_AMD64
        Cli4Arm = 1 << 6, // DELTA_FILE_TYPE_CLI4_ARM
        Cli4Arm64 = 1 << 7, // DELTA_FILE_TYPE_CLI4_ARM64
        ReverseAny = 1 << 8, // DELTA_FILE_TYPE_REVERSE_ANY

        SetRawOnly = Raw, // DELTA_FILE_TYPE_SET_RAW_ONLY
        SetExecutables_1 = Raw | I386 | Ia64 | Amd64, // DELTA_FILE_TYPE_SET_EXECUTABLES_1
        SetExecutables = SetExecutables_1, // DELTA_FILE_TYPE_SET_EXECUTABLES
        SetExecutables_2 = Raw | Cli4I386 | Ia64 | Cli4Amd64 | Cli4Arm, // DELTA_FILE_TYPE_SET_EXECUTABLES_2
        SetExecutables_3 = Raw | Cli4I386 | Ia64 | Cli4Amd64 | Cli4Arm | Cli4Arm64, // DELTA_FILE_TYPE_SET_EXECUTABLES_3
        SetExecutablesLatest = SetExecutables_3 // DELTA_FILE_TYPE_SET_EXECUTABLES_LATEST
    }

    [Flags]
    public enum DeltaFlagType : long // DELTA_FLAG_TYPE
    {
        None = 0, // DELTA_FLAG_NONE

        // Allow application of legacy PA19 deltas by mspatcha.dll.
        ApplyAllowPA19 = 1 << 0, // DELTA_APPLY_FLAG_ALLOW_PA19

        // Transform E8 pieces (relative calls in x86) of target file.
        E8 = 1 << 0, // DELTA_FLAG_E8,

        // Mark non-executable parts of source PE.
        Mark = 1 << 1, // DELTA_FLAG_MARK,

        // Transform imports of source PE.
        Imports = 1 << 2, // DELTA_FLAG_IMPORTS,

        // Transform exports of source PE.
        Exports = 1 << 3, // DELTA_FLAG_EXPORTS,

        // Transform resources of source PE.
        Resources = 1 << 4, // DELTA_FLAG_RESOURCES,

        // Transform relocations of source PE.
        Relocations = 1 << 5, // DELTA_FLAG_RELOCS,

        // Smash lock prefixes of source PE.
        I386SmashLock = 1 << 6, // DELTA_FLAG_I386_SMASH_LOCK,

        // Transform relative jumps of source I386 (x86) PE.
        I386Jumps = 1 << 7, // DELTA_FLAG_I386_JUMPS,

        // Transform relative calls of source I386 (x86) PE.
        I386Calls = 1 << 8, // DELTA_FLAG_I386_CALLS,

        // Transform instructions of source AMD64 (x86-64) PE.
        Amd64Disasm = 1 << 9, // DELTA_FLAG_AMD64_DISASM,

        // Transform pdata of source AMD64 (x86-64) PE.
        Amd64Pdata = 1 << 10, // DELTA_FLAG_AMD64_PDATA,

        // Transform instructions of source IA64 (Itanium) PE.
        Ia64Disasm = 1 << 11, // DELTA_FLAG_IA64_DISASM,

        // Transform pdata of source IA64 (Itanium) PE.
        Ia64Pdata = 1 << 12, // DELTA_FLAG_IA64_PDATA,

        // Unbind source PE.
        Unbind = 1 << 13, // DELTA_FLAG_UNBIND,

        // Transform CLI instructions of source PE.
        CliDisasm = 1 << 14, // DELTA_FLAG_CLI_DISASM,

        // Transform CLI Metadata of source PE.
        CliMetadata = 1 << 15, // DELTA_FLAG_CLI_METADATA,

        // Transform headers of source PE.
        Headers = 1 << 16, // DELTA_FLAG_HEADERS,

        // Allow source or target file or buffer to exceed its default size limit.
        IgnoreFileSizeLimit = 1 << 17, // DELTA_FLAG_IGNORE_FILE_SIZE_LIMIT

        // Allow options buffer or file to exceed its default size limit.
        IgnoreOptionsSizeLimit = 1 << 18, // DELTA_FLAG_IGNORE_OPTIONS_SIZE_LIMIT

        // Transform instructions of source ARM PE.
        ArmDisasm = 1 << 19, // DELTA_FLAG_ARM_DISASM,

        // Transform pdata of source ARM PE.
        ArmPdata = 1 << 20, // DELTA_FLAG_ARM_PDATA,

        // Transform CLI4 Metadata of source PE.
        Cli4Metadata = 1 << 21, // DELTA_FLAG_CLI4_METADATA,

        // Transform CLI4 instructions of source PE.
        Cli4Disasm = 1 << 22, // DELTA_FLAG_CLI4_DISASM,

        // Transform instructions of source ARM PE.
        Arm64Disasm = 1 << 23, // DELTA_FLAG_ARM64_DISASM,

        // Transform pdata of source ARM PE.
        Arm64Pdata = 1 << 24, // DELTA_FLAG_ARM64_PDATA,

        // Requires Reserved3 and excludes Reserved2 in SetFlags.
        // Undocumented flag with unknown purpose.
        Reserved1 = 1 << 25,

        // Undocumented flag with unknown purpose.
        Reserved2 = 1 << 26,

        // Undocumented flag with unknown purpose.
        Reserved3 = 1 << 27,

        // Undocumented flag. Appears to allow a custom hash algorithm; defaults to CALG_SHA1 when unset.
        CustomAlg = 1 << 28,

        // Default flags for RAW transformation.
        DefaultRaw = None, // DELTA_DEFAULT_FLAGS_RAW

        // List the default transform combination for I386.
        // DELTA_DEFAULT_FLAGS_I386
        DefaultI386 = Mark
            | Imports
            | Exports
            | Resources
            | Relocations
            | I386SmashLock
            | I386Jumps
            | I386Calls
            | Unbind
            | CliDisasm
            | CliMetadata,

        // List the default transform combination for IA64.
        // DELTA_DEFAULT_FLAGS_IA64
        DefaultIa64 = Mark
            | Imports
            | Exports
            | Resources
            | Relocations
            | Ia64Disasm
            | Ia64Pdata
            | Unbind
            | CliDisasm
            | CliMetadata,

        // List the default transform combination for AMD64.
        // DELTA_DEFAULT_FLAGS_AMD64
        DefaultAmd64 = Mark
            | Imports
            | Exports
            | Resources
            | Relocations
            | Amd64Disasm
            | Amd64Pdata
            | Unbind
            | CliDisasm
            | CliMetadata,

        // Transform combination for CLI4 on I386.
        // DELTA_CLI4_FLAGS_I386
        Cli4I386 = Mark
            | Imports
            | Exports
            | Resources
            | Relocations
            | I386SmashLock
            | I386Jumps
            | I386Calls
            | Unbind
            | Cli4Disasm
            | Cli4Metadata,

        // Transform combination for CLI4 on AMD64.
        // DELTA_CLI4_FLAGS_AMD64
        Cli4Amd64 = Mark
            | Imports
            | Exports
            | Resources
            | Relocations
            | Amd64Disasm
            | Amd64Pdata
            | Unbind
            | Cli4Disasm
            | Cli4Metadata,

        // Transform combination for CLI4 on ARM.
        // DELTA_CLI4_FLAGS_ARM
        Cli4Arm = Mark
            | Imports
            | Exports
            | Resources
            | Relocations
            | ArmDisasm
            | ArmPdata
            | Unbind
            | Cli4Disasm
            | Cli4Metadata,

        // Transform combination for CLI4 on ARM64.
        // DELTA_CLI4_FLAGS_ARM64
        Cli4Arm64 = Mark
            | Imports
            | Exports
            | Resources
            | Relocations
            | Arm64Disasm
            | Arm64Pdata
            | Unbind
            | Cli4Disasm
            | Cli4Metadata
    }
#pragma warning restore CA1069
#pragma warning restore IDE0079

    [StructLayout(LayoutKind.Sequential)]
    public struct DeltaHash
    {
        public uint HashSize;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] HashValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DeltaHeaderInfo
    {
        public DeltaFileType FileTypeSet;
        public DeltaFileType FileType;
        public DeltaFlagType Flags;
        public UIntPtr TargetSize;
        public FILETIME TargetFileTime;
        public AlgorithmId TargetHashAlgId;
        public DeltaHash TargetHash;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DeltaHeaderInfoEx
    {
        public uint HeaderSize;
        public uint HeaderFlags;
        public uint DeltaClientMinVersion;
        public uint Reserved;
        public DeltaFileType FileTypeSet;
        public DeltaFileType FileType;
        public DeltaFlagType Flags;
        public UIntPtr TargetSize;
        public FILETIME TargetFileTime;
        public AlgorithmId TargetHashAlgId;
        public DeltaHash TargetHash;
        public DeltaHash SourceHash;
        public uint Reserved2;
    }
}