// OCID DriverPackage - the offline half of DMI (driver package import).
//
// DMI is not one operation but two, and only the first one can be done offline:
//
//   import      put the package into  <Windows>\System32\DriverStore\FileRepository\<StoreId>
//               and record it in  HKLM\SYSTEM\DriverDatabase
//   install     bind the package to a device (active package, device ids, ".pnf")
//
// Everything in this module is *import*. It was derived from the symbolized
// 8061 ARM drvstore.dll (see E:\ocid_ida\drvstore_dmi.txt) and, where noted, from
// the x86 servicing stack used elsewhere in this project:
//
//   DriverStoreOfflineAddDriverPackageW -> DriverStoreImportW -> ImportDriverPackageWorker
//       -> ImportDriverPackage -> GetDriverPackageCopyFilesWorker / CopyDriverPackageFiles
//       -> PublishDriverInfFile
//
// Scope of this file: the pieces that are pure computation and therefore testable
// without an image - the package directory name (StoreId), the DriverDatabase
// value naming, and the oemN.inf name allocator. The file copy set and the
// registry write order live in the tool/host layer, not here.
//
// Keep this file pure ASCII.

#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace ocid {

// ---------------------------------------------------------------------------
// StoreId - the FileRepository directory name.
//
//   StoreId = lower( "<INF file name>_<architecture>_<16 hex digits>" )
//
// The 16 hex digits are the INF *content* hash: drvstore's GetDriverPackageStoreId
// (drvstore.c:24378) formats L"%ws_%ws_%ws" from (file title, architecture string,
// GetFileHashString) and lowercases the whole thing with LCMapStringW.
//
// GetFileHashString (drvstore.c:24328) asks setupapi's pSetupGetFilePGHash for the
// package hash and renders it with ByteArrayToString (drvstore.c:24280) as 8 bytes
// -> 16 upper-case hex digits. Verified against a real image and a real host store:
//
//     h = 0;  for every byte b of the INF:  h = h * 39 + b     (mod 2^64)
//     the 16 digits are the 8 bytes of h in little-endian order
//
// Ground truth:  P:\Windows\INF\ax88772.inf  ->  "ax88772.inf_arm_f9bac5c487a52340"
// Cross-checked on 742 packages of a real DriverStore (amd64 and x86 of the same
// INF share the hash, so the hash is over the bytes and is architecture-independent).
//
// The hash must be taken over the INF exactly as it is written: a single byte of
// difference moves the directory *and* the database entry.
std::uint64_t ComputeInfPgHash(const std::uint8_t* data, std::size_t size);
std::uint64_t ComputeInfPgHash(const std::vector<std::uint8_t>& bytes);

// The 16 lower-case hex digits of a hash (little-endian byte order).
std::wstring StoreIdHashSuffix(std::uint64_t hash);

// File title = the last path component, kept verbatim (case included).
std::wstring FileTitleOf(const std::wstring& path);

// Architecture number -> the string used in a StoreId. The four strings below are
// the ones the 8061 drvstore.dll actually carries; anything else has no spelling
// in this build and must be refused rather than guessed.
//   0 = x86, 5 = arm, 6 = ia64, 9 = amd64
const wchar_t* ProcessorArchitectureString(unsigned int arch);

// StoreId for an INF whose bytes are already in memory.
bool BuildStoreId(const std::wstring& infPath,
                  const std::vector<std::uint8_t>& infBytes,
                  unsigned int arch,
                  std::wstring& outStoreId,
                  std::wstring& err);

// StoreId for an INF on disk (reads the file).
bool BuildStoreIdFromFile(const std::wstring& infPath,
                          unsigned int arch,
                          std::wstring& outStoreId,
                          std::wstring& err);

// ---------------------------------------------------------------------------
// HKLM\SYSTEM\DriverDatabase value naming.
//
// ★ 8061 stores every property under a "%04x" of its property id, NOT under the
// Win10/Win11 friendly names. The friendly schema (Version REG_BINARY, Provider,
// SignerScore, StatusFlags, Active, Configurations, ...) is a *later* schema;
// porting it onto 8061 would produce a database this build cannot read.
//
//   DrvDbGetRegValueMappedPropertyKeys / ...MappedProperty / ...SetMappedProperty
//     RtlStringCchPrintfExW(..., L"%04x", propertyId)     (drvstore.c:72960, :73049, :73122)
//     then _RegRtlQueryValue / _RegRtlSetValue            (drvstore.c:72970, :73144)
//
// Anchor verified on the target image: DEVPKEY_DriverDatabase_ProcessorArchitecture
// has property id 3, and the shipped 8061 ARM image has
// HKLM\SYSTEM\DriverDatabase : "0003" = REG_DWORD 5   (5 = ARM).
std::wstring DriverDatabaseValueName(unsigned int propertyId);

// Property ids as decoded from the symbolized drvstore. The registry keeps only the
// number, so the same number means different things on different object types; the
// object type is part of the name here to keep the call sites honest.
enum class DbObject {
    Database,      // HKLM\SYSTEM\DriverDatabase
    Package,       // ...\DriverPackages\<StoreId>
    InfFile,       // ...\DriverInfFiles\<oemN.inf>
    DeviceId,      // ...\DeviceIds\<hardware id>
};

// Property ids. Values are the ones read out of the symbolized functions
// (CreateDriverPackageObject, InsertDriverInfFileDriverPackage, UpdateDriverPackageDeviceIds,
// AcquireOemDriverInfName, DriverStoreSetObjectProperty).
enum : unsigned int {
    kDbPropOemDriverInfFileMap         = 4,  // Database : REG_BINARY, 4099 bytes, oemN.inf in-use bitmap
    kDbPropProcessorArchitecture       = 3,  // Database : REG_DWORD  (5 = arm)
    kPkgPropDriverInfName              = 2,  // Package  : REG_SZ     "oem1.inf"
    kPkgPropOriginalInfName            = 3,  // Package  : REG_SZ     "ax88772.inf"
    kPkgPropSourceMediaPath            = 4,  // Package  : REG_SZ
    kPkgPropInbox                      = 5,  // Package  : REG_BINARY 1 byte, 0xFF
    kPkgPropPublished                  = 6,  // Package  : REG_BINARY 1 byte  (install half, not import)
    kPkgPropSignerName                 = 7,  // Package  : REG_SZ
    kPkgPropSignerScore                = 8,  // Package  : REG_DWORD
    kPkgPropF6                         = 9,  // Package  : REG_BINARY 1 byte
    kPkgPropProcessorArchitecture      = 10, // Package  : REG_DWORD
    kPkgPropLocale                     = 11, // Package  : REG_DWORD
    kPkgPropProviderName               = 12, // Package  : REG_SZ
    kPkgPropClassGuid                  = 13, // Package  : REG_SZ (GUID text)
    kPkgPropDriverDate                 = 14, // Package  : REG_BINARY FILETIME
    kPkgPropDriverVersion              = 15, // Package  : REG_QWORD
    kPkgPropConfigurableFlags          = 17, // Package  : REG_DWORD
    kInfPropDriverPackages             = 2,  // InfFile  : REG_MULTI_SZ, StoreIds this oem name serves
    kInfPropActiveDriverPackage        = 3,  // InfFile  : REG_SZ       (install half)
    kInfPropActiveConfigurations       = 4,  // InfFile  : REG_MULTI_SZ (install half)
    kDevPropDriverInfMatches           = 3,  // DeviceId : REG_MULTI_SZ
};

// Human readable name for diagnostics; nullptr for unknown ids.
const wchar_t* DriverDatabasePropertyName(DbObject object, unsigned int propertyId);

// Size of the oemN.inf in-use bitmap property. AcquireOemDriverInfName rejects a
// stored map whose size is not exactly this.
constexpr unsigned int kOemDriverInfFileMapSize = 4099;

// ---------------------------------------------------------------------------
// oemN.inf name allocation.
//
// An inbox driver keeps its own name; a third-party package gets "oemN.inf" where N
// is the first clear bit of the in-use bitmap, scanned MSB-first inside each byte
// (AcquireOemDriverInfName, drvstore.c:9531-9641 / dump line 3390). Numbers are
// never reused while their bit is set, which is why the bitmap must travel with the
// database rather than be recomputed from the directory listing.
//
// The returned index is also the next free bit. On a full map the bitmap grows by
// one byte and the new bit (old size * 8) is taken.
struct OemInfAllocation {
    std::wstring name;      // "oem0.inf"
    unsigned int index = 0;
    bool grew = false;      // true when the bitmap had to grow by one byte
};

bool AcquireOemInfName(std::vector<std::uint8_t>& bitmap, OemInfAllocation& out, std::wstring& err);

// Clears the bit named by "oem<N>.inf". Rejects other spellings (drvstore uses
// swscanf_s(L"oem%u.inf")).
bool ReleaseOemInfName(std::vector<std::uint8_t>& bitmap, const std::wstring& infName, std::wstring& err);

// Import flags that decide whether the package gets an oem name at all: an
// inbox package (flag 0x200) or an OS/boot INF (flag 0x100) keeps its original
// file title and takes no name from the bitmap (drvstore.c:11493-11502).
constexpr unsigned int kImportFlagUseOriginalInfName = 0x100;
constexpr unsigned int kImportFlagInbox = 0x200;

inline bool ImportKeepsOriginalInfName(unsigned int importFlags)
{
    return (importFlags & (kImportFlagUseOriginalInfName | kImportFlagInbox)) != 0;
}

// The name the published INF gets under <Windows>\INF. `bitmap` is only consulted
// when the package needs a new name.
bool ChoosePublishedInfName(unsigned int importFlags,
                            const std::wstring& originalInfTitle,
                            std::vector<std::uint8_t>& bitmap,
                            OemInfAllocation& out,
                            std::wstring& err);

// ---------------------------------------------------------------------------
// Driver store import file set.
//
// Ground truth, 8061 package store: a driver component is a manifest whose identity
// is  type="driverUpdate" versionScope="nonSxS"  and whose files carry NO
// destinationPath at all - where each file goes is decided by `importPath`:
//
//   <file name="acpi.inf" sourceName="acpi.inf" importPath="$(build.nttree)\driver_infs\" />
//   <file name="acpi.sys" sourceName="acpi.sys" importPath="$(build.nttree)\" />
//
//   Inf          -> copied into <StoreId>, then published as
//                   <Windows>\INF\<original name | oemN.inf>  (ChoosePublishedInfName)
//   DriverBinary -> copied into <StoreId> only
//   NotImported  -> not part of the import (no importPath, or another tree)
//
// The INF is the file staged in an INF staging directory, so the distinction is
// structural and needs no guessing. Measured on a real 8061 package store
// (5664 manifests, 231 of them type="driverUpdate", 973 file entries in total):
//
//   $(build.nttree)\                  724    payload (the .sys / .dll etc.)
//   $(build.nttree)\driver_infs\      208  } one INF per package: 208 + 22 + 1 = 231
//   $(build.nttree)\printer_infs\      22  } (printer and "manifested" drivers stage
//   $(build.nttree)\manifested_infs\    1  }  their INF somewhere else)
//   $(build.nttree)\multi\             17    NOT an INF staging dir (see below)
//   $(build.nttree)\ua\                 1    NOT an INF staging dir (see below)
//
// Every driver package carries exactly one INF under one of the three staging
// directories; the 18 entries under `multi` and `ua` are not accounted for by that
// identity and are therefore treated as payload for now - do not mistake them for INFs
// without new evidence.
// ---------------------------------------------------------------------------
enum class DriverImportFileKind { NotImported = 0, Inf, DriverBinary };

// importPath -> kind. Case-insensitive; a trailing separator is ignored; an empty
// string is NotImported.
DriverImportFileKind ClassifyDriverImportFile(const std::wstring& importPath);

// The INF staging directory names (import path segment, case-insensitive). Exposed so
// callers can explain *why* a file was classified as an INF.
bool IsInfStagingSegment(const std::wstring& segment);

// Is this component a driver package at all? Both attributes are compared
// case-insensitively; a missing attribute means "no".
bool IsDriverUpdateComponent(const std::wstring& type, const std::wstring& versionScope);

// ---------------------------------------------------------------------------
// The import plan: which files of one driver component go into the store.
//
// A driver component declares exactly one INF (measured 231/231 in the real 8061
// package store), and that INF decides the store directory name. Everything else the
// component stages in the build tree is payload copied along with it. Files that are
// not staged in the build tree are *not* part of the import, and they are reported
// rather than dropped silently - a driver package that suddenly loses its payload would
// otherwise import as an empty store directory and only fail at device install time.
// ---------------------------------------------------------------------------
struct DriverImportFileSpec
{
    std::wstring name;         // <file name>（目标文件名；空则用 sourceName）
    std::wstring sourceName;   // <file sourceName>
    std::wstring importPath;   // <file importPath>
};

struct DriverImportPlan
{
    bool         ok = false;   // false 时只看 error，不要用其余字段
    std::wstring error;

    std::wstring storeId;      // FileRepository 的子目录名（小写）
    std::wstring infName;      // 被导入的那个 INF
    std::vector<DriverImportFileSpec> files;   // Inf + DriverBinary，保持清单顺序
    std::vector<std::wstring>          notes;  // 未导入的文件等（解释，不静默丢）
    int          notImported = 0;
};

// infPath/infBytes: 那个 INF 的路径（只用于报错）与字节（StoreId 由字节算）。
// architecture: 装配目标架构（0/5/6/9），必须能算成架构串，否则失败。
DriverImportPlan BuildDriverImportPlan(const std::vector<DriverImportFileSpec>& files,
                                       const std::wstring& infPath,
                                       const std::vector<std::uint8_t>& infBytes,
                                       unsigned int architecture);

} // namespace ocid
