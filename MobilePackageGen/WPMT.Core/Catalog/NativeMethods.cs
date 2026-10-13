using System.Runtime.InteropServices;

namespace WPMT.Core.Catalog;

internal static partial class NativeMethods
{
    // PFN_CDF_PARSE_ERROR_CALLBACK
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void CdfParseErrorCallback(
        CryptCatErrorArea dwErrorArea,
        CryptCatErrorCdf dwLocalError,
        [MarshalAs(UnmanagedType.LPWStr)]
        string pwszLine);

    public enum CryptCatErrorArea : uint
    {
        Header = 0x00000000, // CRYPTCAT_E_AREA_HEADER
        Member = 0x00010000, // CRYPTCAT_E_AREA_MEMBER
        Attribute = 0x00020000 // CRYPTCAT_E_AREA_ATTRIBUTE
    }

    public enum CryptCatErrorCdf : uint
    {
        Unsupported = 0x00000001, // CRYPTCAT_E_CDF_UNSUPPORTED
        Duplicate = 0x00000002, // CRYPTCAT_E_CDF_DUPLICATE
        TagNotFound = 0x00000004, // CRYPTCAT_E_CDF_TAGNOTFOUND
        MemberFilePath = 0x00010001, // CRYPTCAT_E_CDF_MEMBER_FILE_PATH
        MemberIndirectData = 0x00010002, // CRYPTCAT_E_CDF_MEMBER_INDIRECTDATA
        MemberFileNotFound = 0x00010004, // CRYPTCAT_E_CDF_MEMBER_FILENOTFOUND
        BadGuidConv = 0x00020001, // CRYPTCAT_E_CDF_BAD_GUID_CONV
        AttrTooFewValues = 0x00020002, // CRYPTCAT_E_CDF_ATTR_TOOFEWVALUES
        AttrTypeCombo = 0x00020004, // CRYPTCAT_E_CDF_ATTR_TYPECOMBO
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CRYPT_ATTR_BLOB
    {
        public uint cbData;
        public IntPtr pbData;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CRYPTCATMEMBER
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pwszReferenceTag;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pwszFileName;
        public Guid gSubjectType;
        public uint fdwMemberFlags;
        public IntPtr pIndirectData;
        public uint dwCertVersion;
        public uint dwReserved;
        public IntPtr hReserved;
        public CRYPT_ATTR_BLOB sEncodedIndirectData;
        public CRYPT_ATTR_BLOB sEncodedMemberInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CRYPTCATATTRIBUTE
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pwszReferenceTag;
        public uint dwAttrTypeAndAction;
        public uint cbValue;
        public IntPtr pbValue;
        public uint dwReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CRYPTCATCDF
    {
        public uint cbStruct;
        public IntPtr hFile;
        public uint dwCurFilePos;
        public uint dwLastMemberOffset;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fEOF;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pwszResultDir;
        public IntPtr hCATStore;
    }
}