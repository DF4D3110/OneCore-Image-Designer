using System.ComponentModel;
using System.Runtime.InteropServices;
using WPMT.Core.Logging;
using static WPMT.Core.Catalog.NativeMethods;

namespace WPMT.Core.Catalog;

public static class CatalogGenerator
{
    internal static readonly CdfParseErrorCallback cdfParseErrorCallback =
        ParseError;

    public static bool ContinueOnError { get; set; }

    private static void ParseError(
        CryptCatErrorArea errorArea,
        CryptCatErrorCdf localError,
        string line)
    {
        Log.Error(
            $"CDF parse error: " +
            $"Area={errorArea}, " +
            $"Error={localError}, " +
            $"Line={line}");
    }

    public static void GenerateCatalog(string cdfFile)
    {
        IntPtr cdf = WinTrust.CryptCATCDFOpen(cdfFile, cdfParseErrorCallback);

        if (cdf == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        Log.InfoDebug($"CDF opened: {cdfFile}");

        EnumCatalogAttributes(cdf);
        EnumMembers(cdf);

        if (!WinTrust.CryptCATCDFClose(cdf))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static void EnumCatalogAttributes(IntPtr cdf)
    {
        Log.InfoDebug("Catalog Attributes:");

        IntPtr attr = IntPtr.Zero;
        while (true)
        {
            attr = WinTrust.CryptCATCDFEnumCatAttributes(cdf, attr, cdfParseErrorCallback);

            if (attr == IntPtr.Zero)
                break;

#if DEBUG
            CRYPTCATATTRIBUTE a = Marshal.PtrToStructure<CRYPTCATATTRIBUTE>(attr);
            Log.InfoDebug($"CAT attribute: {a.pwszReferenceTag}");
            Log.InfoDebug($"  Type : 0x{a.dwAttrTypeAndAction:X8}");
            Log.InfoDebug($"  Size : {a.cbValue}");
#endif
        }
    }

    private static void EnumMembers(IntPtr pCDF)
    {
        Log.InfoDebug("Catalog Files:");

        IntPtr tag = IntPtr.Zero;
        while (true)
        {
            tag = WinTrust.CryptCATCDFEnumMembersByCDFTagEx(
                pCDF,
                tag,
                cdfParseErrorCallback,
                out IntPtr member,
                ContinueOnError,
                IntPtr.Zero);

            if (tag == IntPtr.Zero)
                break;

            string cdfTag = Marshal.PtrToStringUni(tag) ?? string.Empty;

            if (member != IntPtr.Zero)
            {
#if DEBUG
                CRYPTCATMEMBER m = Marshal.PtrToStructure<CRYPTCATMEMBER>(member);
                Log.InfoDebug("Member:");
                Log.InfoDebug($"  File : {m.pwszFileName}");
                Log.InfoDebug($"  Tag  : {m.pwszReferenceTag}");
#endif
                EnumMemberAttributes(pCDF, member, cdfTag);
            }
        }
    }

    private static void EnumMemberAttributes(IntPtr cdf, IntPtr member, string memberTag)
    {
        IntPtr attr = IntPtr.Zero;
        while (true)
        {
            attr = WinTrust.CryptCATCDFEnumAttributesWithCDFTag(
                cdf,
                memberTag,
                member,
                attr,
                cdfParseErrorCallback);

            if (attr == IntPtr.Zero)
                break;

#if DEBUG
            CRYPTCATATTRIBUTE a = Marshal.PtrToStructure<CRYPTCATATTRIBUTE>(attr);
            Log.InfoDebug("  Attribute:");
            Log.InfoDebug($"    Name : {a.pwszReferenceTag}");
            Log.InfoDebug($"    Type : 0x{a.dwAttrTypeAndAction:X8}");
            Log.InfoDebug($"    Size : {a.cbValue}");
#endif
        }
    }
}