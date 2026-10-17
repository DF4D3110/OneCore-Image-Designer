// OCID DriverInf - read a driver INF and turn it into DriverDatabase records.
//
// This is the second half of DMI import. DriverPackage.h decides *where* a package
// goes (StoreId) and *which files* it carries; this file decides *what the driver
// database says about it*.
//
// Everything here was derived from a real 8061 x86 client image (E:\cbs_8061x86c.vhd,
// volume label C8061X86) whose SYSTEM hive holds 158 imported packages. Every value
// name, type and encoding below was read back out of that hive - not from a Win10/Win11
// image, whose DriverDatabase uses friendly value names that 8061 cannot read.
//
//   HKLM\SYSTEM\DriverDatabase\DriverPackages\<StoreId>
//       0002 REG_SZ      original INF file name          "1394.inf"
//       0005 REG_DWORD   inbox                           1
//       0006 REG_DWORD   (unidentified, constant)        1
//       0007 REG_SZ      signer name                     "Microsoft Windows"
//       0008 REG_DWORD   signer score                    0x0D000003
//       000a REG_DWORD   architecture                    0 (x86) / 5 (arm)
//       000b REG_DWORD   locale                          0
//       000c REG_SZ      provider, %token% NOT expanded  "%LSI%", "Microsoft"
//       000d REG_BINARY  class GUID, 16 bytes, raw order  {6BDD1FC1-...} -> C11FDD6B...
//       000e REG_BINARY  driver date, 8 bytes FILETIME
//       000f REG_BINARY  driver version, 8 bytes          [build<<16|rev][major<<16|min]
//       Configurations\<section>   Service / ConfigFlags / HardwareKey\...
//       Descriptors\<bus>\<hw id>  Configuration / Manufacturer / Description
//       Strings\<token>            resolved %token%
//
//   ...\DriverInfFiles\<original INF name>      (NOT the published oemN.inf name)
//       0002 REG_MULTI_SZ  StoreIds this INF name serves
//       0003 REG_SZ        active StoreId
//       0004 REG_MULTI_SZ  configuration section names
//
// The published name (oemN.inf) only ever appears under <Windows>\INF; the database
// keys stay on the package's own INF name. Measured: the store holds oem0.inf
// (prnms001.inf, published) and DriverInfFiles has no oem0.inf key at all.
//
// Keep this file pure ASCII.

#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <utility>
#include <vector>

namespace ocid {

// ---------------------------------------------------------------------------
// INF text form.
//
// Real 8061 INFs are UTF-16LE with a BOM (measured on 1394.inf: FF FE followed by
// ';' 0x00). Declaration order inside a section matters (Registry add-lines are
// applied in order) and duplicate keys exist, so entries are a vector, not a map.
struct InfEntry {
    std::wstring key;     // left of '=' ; for a bare line this is the whole line
    std::wstring value;   // right of '=' ; empty for a bare line
    bool hasValue = false;
};

struct InfSection {
    std::wstring name;
    std::vector<InfEntry> entries;
};

struct InfDocument {
    bool ok = false;
    std::wstring error;
    std::vector<InfSection> sections;   // document order

    // Case-insensitive lookups. Return nullptr / empty when missing.
    const InfSection* FindSection(const std::wstring& name) const;
    // First value of `key` in `section` (case-insensitive). Empty when missing.
    std::wstring GetValue(const std::wstring& section, const std::wstring& key) const;
    // True when the section exists.
    bool HasSection(const std::wstring& name) const;

    // Resolve %token% against [Strings] (surrounding quotes stripped). A token that is
    // not defined stays literal - silently dropping it would store a wrong provider.
    std::wstring ResolveTokens(const std::wstring& text) const;
    bool TryGetString(const std::wstring& token, std::wstring& out) const;

    // Strip one layer of surrounding double quotes (INF string values are often quoted).
    static std::wstring Unquote(const std::wstring& text);
};

// Decode INF bytes (UTF-16LE/BE with BOM, otherwise the ANSI code page) and parse.
bool ParseInf(const std::vector<std::uint8_t>& bytes, InfDocument& out, std::wstring& err);
bool ParseInfText(const std::wstring& text, InfDocument& out, std::wstring& err);
bool ParseInfFile(const std::wstring& path, InfDocument& out, std::wstring& err);

// ---------------------------------------------------------------------------
// [Version] decoding.
//
//   DriverVer=06/21/2006,6.2.8061.0
//
// The date half is stored as a raw 8-byte FILETIME; the version half as two DWORDs,
// little-endian, [build<<16 | revision][major<<16 | minor] - measured on 158 packages,
// e.g. 6.2.8061.0 -> 00 00 7D 1F 02 00 06 00.
struct InfVersionInfo {
    bool ok = false;
    std::wstring error;

    // The store keeps the RESOLVED provider: measured 158/158 on the 8061 x86 image,
    // %MSFT% is stored as "Microsoft". The raw text is kept for diagnostics only.
    std::wstring provider;
    std::wstring providerRaw;
    std::wstring className;       // Class=
    std::vector<std::uint8_t> classGuid;   // 16 bytes, registry order; empty if absent

    unsigned int major = 0;
    unsigned int minor = 0;
    unsigned int build = 0;
    unsigned int revision = 0;
    std::uint64_t driverDate = 0;  // FILETIME
    bool hasDriverVer = false;
};

bool ReadInfVersion(const InfDocument& inf, InfVersionInfo& out);

// "06/21/2006,6.2.8061.0" -> date FILETIME + 4 numbers. Separator may be ',' or ';'.
bool ParseDriverVer(const std::wstring& spec,
                    InfVersionInfo& out,
                    std::wstring& err);

// "06/21/2006" (MM/DD/YYYY) -> FILETIME. Also accepts "6/21/2006".
bool ParseInfDateFileTime(const std::wstring& text, std::uint64_t& out, std::wstring& err);

// "{6BDD1FC1-810F-11D0-BEC7-08002BE2092F}" -> 16 bytes in registry order
// (Data1 little-endian, Data2 little-endian, Data3 little-endian, Data4 verbatim).
bool ParseInfGuid(const std::wstring& text, std::vector<std::uint8_t>& out, std::wstring& err);

// The two binary property payloads, exactly as the store holds them.
std::vector<std::uint8_t> DriverVersionPropertyData(unsigned int major,
                                                    unsigned int minor,
                                                    unsigned int build,
                                                    unsigned int revision);
std::vector<std::uint8_t> DriverDatePropertyData(std::uint64_t filetime);

// ---------------------------------------------------------------------------
// The ledger plan: DriverDatabase keys and values for one imported package.
//
// The plan is pure data - it says what to write, never writes it. The host applies it
// to the offline hive (same split as everywhere else in this project: rules are native
// and testable, committing is the caller's job).
enum class LedgerValueType { String, MultiString, DWord, Binary };

struct LedgerValue {
    std::wstring name;          // "%04x" property id
    LedgerValueType type = LedgerValueType::String;
    std::wstring text;          // String, and MultiString (items joined with '\0')
    unsigned int dword = 0;     // DWord
    std::vector<std::uint8_t> binary;   // Binary
};

struct LedgerKey {
    std::wstring relativePath;          // under DriverDatabase, e.g. L"DriverPackages\<StoreId>"
    std::vector<LedgerValue> values;
    std::vector<std::wstring> notes;    // why a value is absent / unverified
};

struct DriverLedgerPlan {
    bool ok = false;
    std::wstring error;
    std::vector<LedgerKey> keys;        // parents first
    std::vector<std::wstring> notes;
};

struct DriverLedgerRequest {
    const InfDocument* inf = nullptr;   // required
    std::wstring storeId;               // FileRepository directory name (lower case)
    std::wstring infFileName;           // original INF title, e.g. L"1394.inf"
    unsigned int architecture = 0;      // 0 x86 / 5 arm / 6 ia64 / 9 amd64
    unsigned int importFlags = 0;       // see DriverPackage.h kImportFlag*
    std::wstring signerName;            // empty = unknown (the value is then omitted)
    unsigned int signerScore = 0;       // 0 = unsigned
};

DriverLedgerPlan BuildDriverLedgerPlan(const DriverLedgerRequest& request);

} // namespace ocid
