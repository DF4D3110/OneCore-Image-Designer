using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace WPMT.Core.Compression;

internal static partial class NativeMethods
{
    internal static class MsDelta
    {
        private const string LibraryName = "msdelta.dll";
        internal static readonly bool IsAvailable = NativeLibrary.TryLoad(LibraryName, out _);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateDeltaB(
            DeltaFileType FileTypeSet,
            DeltaFlagType SetFlags,
            DeltaFlagType ResetFlags,
            DELTA_INPUT Source,
            DELTA_INPUT Target,
            DELTA_INPUT SourceOptions,
            DELTA_INPUT TargetOptions,
            DELTA_INPUT GlobalOptions,
            in FILETIME TargetFileTime,
            AlgorithmId HashAlgId,
            out DELTA_OUTPUT lpDelta);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ApplyDeltaB(
            DeltaFlagType ApplyFlags,
            DELTA_INPUT Source,
            DELTA_INPUT Delta,
            out DELTA_OUTPUT lpTarget);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ApplyDeltaProvidedB(
            DeltaFlagType ApplyFlags,
            DELTA_INPUT Source,
            DELTA_INPUT Delta,
            IntPtr lpTarget,
            UIntPtr uTargetSize);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetDeltaInfoB(
            DELTA_INPUT Delta,
            out DeltaHeaderInfo lpHeaderInfo);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetDeltaSignatureB(
            DeltaFileType FileTypeSet,
            AlgorithmId HashAlgId,
            DELTA_INPUT Source,
            out DeltaHash lpHash);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeltaNormalizeProvidedB(
            DeltaFileType FileTypeSet,
            DeltaFlagType NormalizeFlags,
            DELTA_INPUT NormalizeOptions,
            IntPtr Source,
            UIntPtr uSourceSize);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ApplyDeltaGetReverseB(
            DeltaFlagType ApplyFlags,
            DELTA_INPUT Source,
            DELTA_INPUT Delta,
            in FILETIME ReverseFileTime,
            out DELTA_OUTPUT lpTarget,
            out DELTA_OUTPUT lpTargetReverse);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeltaFree(
            IntPtr lpMemory);
    }
}