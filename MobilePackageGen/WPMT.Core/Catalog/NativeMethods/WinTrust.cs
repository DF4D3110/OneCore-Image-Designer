using System.Runtime.InteropServices;

namespace WPMT.Core.Catalog;

internal static partial class NativeMethods
{
    internal static class WinTrust
    {
        private const string LibraryName = "wintrust.dll";
        internal static readonly bool IsAvailable = NativeLibrary.TryLoad(LibraryName, out _);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
        internal static extern IntPtr CryptCATCDFOpen(
            [MarshalAs(UnmanagedType.LPWStr)]
            string pwszFilePath,
            CdfParseErrorCallback pfnParseError);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
        internal static extern bool CryptCATCDFClose(
            IntPtr pCDF);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
        internal static extern IntPtr CryptCATCDFEnumCatAttributes(
            IntPtr pCDF,
            IntPtr pPrevAttr,
            CdfParseErrorCallback pfnParseError);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
        internal static extern IntPtr CryptCATCDFEnumMembersByCDFTagEx(
            IntPtr pCDF,
            IntPtr pwszPrevCDFTag,
            CdfParseErrorCallback pfnParseError,
            out IntPtr ppMember,
            [MarshalAs(UnmanagedType.Bool)]
            bool fContinueOnError,
            IntPtr pvReserved);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
        internal static extern IntPtr CryptCATCDFEnumAttributesWithCDFTag(
            IntPtr pCDF,
            [MarshalAs(UnmanagedType.LPWStr)]
            string pwszMemberTag,
            IntPtr pMember,
            IntPtr pPrevAttr,
            CdfParseErrorCallback pfnParseError);
    }
}