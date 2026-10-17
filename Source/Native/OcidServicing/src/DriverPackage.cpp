// OCID DriverPackage - see include/ocid/DriverPackage.h for the evidence trail.
//
// Keep this file pure ASCII.

#include "ocid/DriverPackage.h"

#include <cstdio>
#include <cwchar>
#include <fstream>

namespace ocid {

namespace {

const wchar_t kHexDigits[] = L"0123456789abcdef";

} // namespace

std::uint64_t ComputeInfPgHash(const std::uint8_t* data, std::size_t size)
{
    std::uint64_t h = 0;
    for (std::size_t i = 0; i < size; ++i)
    {
        // pSetupGetFilePGHash, replayed: one multiply-add per byte. The multiply is
        // mod 2^64 (unsigned wraparound), which is what the 8-byte result implies.
        h = h * 39u + static_cast<std::uint64_t>(data[i]);
    }
    return h;
}

std::uint64_t ComputeInfPgHash(const std::vector<std::uint8_t>& bytes)
{
    return ComputeInfPgHash(bytes.empty() ? nullptr : bytes.data(), bytes.size());
}

std::wstring StoreIdHashSuffix(std::uint64_t hash)
{
    // ByteArrayToString (drvstore.c:24280): one output character per nibble, most
    // significant nibble of each byte first, but the *bytes* are emitted in
    // little-endian order - i.e. the low byte of the hash comes first.
    std::wstring out;
    out.reserve(16);
    for (int i = 0; i < 8; ++i)
    {
        const std::uint8_t b = static_cast<std::uint8_t>((hash >> (8 * i)) & 0xFFu);
        out.push_back(kHexDigits[(b >> 4) & 0xF]);
        out.push_back(kHexDigits[b & 0xF]);
    }
    return out;
}

std::wstring FileTitleOf(const std::wstring& path)
{
    const std::wstring::size_type pos = path.find_last_of(L"\\/");
    return pos == std::wstring::npos ? path : path.substr(pos + 1);
}

const wchar_t* ProcessorArchitectureString(unsigned int arch)
{
    // The four spellings the 8061 drvstore.dll actually contains. Anything else
    // (including arm64, which this build predates) has no StoreId spelling here.
    switch (arch)
    {
    case 0:  return L"x86";
    case 5:  return L"arm";
    case 6:  return L"ia64";
    case 9:  return L"amd64";
    default: return nullptr;
    }
}

bool BuildStoreId(const std::wstring& infPath,
                  const std::vector<std::uint8_t>& infBytes,
                  unsigned int arch,
                  std::wstring& outStoreId,
                  std::wstring& err)
{
    outStoreId.clear();
    err.clear();

    const std::wstring title = FileTitleOf(infPath);
    if (title.empty())
    {
        err = L"INF 路径为空，无法构造 StoreId";
        return false;
    }

    const wchar_t* archName = ProcessorArchitectureString(arch);
    if (archName == nullptr)
    {
        err = L"架构值 " + std::to_wstring(arch) + L" 没有 StoreId 拼写（8061 只认 0=x86/5=arm/6=ia64/9=amd64）";
        return false;
    }

    if (infBytes.empty())
    {
        err = L"INF 内容为空，无法计算 StoreId 哈希";
        return false;
    }

    std::wstring id = title;
    id.push_back(L'_');
    id.append(archName);
    id.push_back(L'_');
    id.append(StoreIdHashSuffix(ComputeInfPgHash(infBytes)));

    // GetDriverPackageStoreId lowercases the whole name with LCMapStringW; the hash
    // is already lower-case, the title is not.
    for (std::wstring::size_type i = 0; i < id.size(); ++i)
    {
        const wchar_t c = id[i];
        if (c >= L'A' && c <= L'Z')
        {
            id[i] = static_cast<wchar_t>(c - L'A' + L'a');
        }
    }

    outStoreId.swap(id);
    return true;
}

bool BuildStoreIdFromFile(const std::wstring& infPath,
                          unsigned int arch,
                          std::wstring& outStoreId,
                          std::wstring& err)
{
    outStoreId.clear();
    err.clear();

    std::ifstream file(infPath.c_str(), std::ios::binary);
    if (!file.is_open())
    {
        err = L"打不开 INF 文件：" + infPath;
        return false;
    }

    std::vector<std::uint8_t> bytes((std::istreambuf_iterator<char>(file)),
                                    std::istreambuf_iterator<char>());
    file.close();

    if (bytes.empty())
    {
        err = L"INF 文件为空：" + infPath;
        return false;
    }

    return BuildStoreId(infPath, bytes, arch, outStoreId, err);
}

std::wstring DriverDatabaseValueName(unsigned int propertyId)
{
    wchar_t buf[16] = {};
    swprintf_s(buf, L"%04x", propertyId);
    return std::wstring(buf);
}

const wchar_t* DriverDatabasePropertyName(DbObject object, unsigned int propertyId)
{
    switch (object)
    {
    case DbObject::Database:
        switch (propertyId)
        {
        case kDbPropProcessorArchitecture: return L"ProcessorArchitecture";
        case kDbPropOemDriverInfFileMap:   return L"OemDriverInfFileMap";
        default:                           return nullptr;
        }
    case DbObject::Package:
        switch (propertyId)
        {
        case kPkgPropDriverInfName:           return L"DriverInfName";
        case kPkgPropOriginalInfName:         return L"OriginalInfName";
        case kPkgPropSourceMediaPath:         return L"SourceMediaPath";
        case kPkgPropInbox:                   return L"Inbox";
        case kPkgPropPublished:               return L"Published";
        case kPkgPropSignerName:              return L"SignerName";
        case kPkgPropSignerScore:             return L"SignerScore";
        case kPkgPropF6:                      return L"F6";
        case kPkgPropProcessorArchitecture:   return L"ProcessorArchitecture";
        case kPkgPropLocale:                  return L"Locale";
        case kPkgPropProviderName:            return L"ProviderName";
        case kPkgPropClassGuid:               return L"ClassGuid";
        case kPkgPropDriverDate:              return L"DriverDate";
        case kPkgPropDriverVersion:           return L"DriverVersion";
        case kPkgPropConfigurableFlags:       return L"ConfigurableFlags";
        default:                              return nullptr;
        }
    case DbObject::InfFile:
        switch (propertyId)
        {
        case kInfPropDriverPackages:        return L"DriverPackages";
        case kInfPropActiveDriverPackage:   return L"ActiveDriverPackage";
        case kInfPropActiveConfigurations:  return L"ActiveConfigurations";
        default:                            return nullptr;
        }
    case DbObject::DeviceId:
        switch (propertyId)
        {
        case kDevPropDriverInfMatches:      return L"DriverInfMatches";
        default:                            return nullptr;
        }
    }
    return nullptr;
}

bool AcquireOemInfName(std::vector<std::uint8_t>& bitmap, OemInfAllocation& out, std::wstring& err)
{
    out = OemInfAllocation();
    err.clear();

    if (!bitmap.empty() && bitmap.size() > kOemDriverInfFileMapSize)
    {
        // drvstore itself only rejects a *bad* size on read (it requires 4099); a map
        // in memory that grew past the cap could never be stored again.
        err = L"oemN.inf 位图超过 " + std::to_wstring(kOemDriverInfFileMapSize) + L" 字节上限";
        return false;
    }

    // First clear bit, most significant bit of each byte first - exactly the scan in
    // AcquireOemDriverInfName (dump line 3463-3479).
    for (std::size_t byteIndex = 0; byteIndex < bitmap.size(); ++byteIndex)
    {
        for (int bit = 7; bit >= 0; --bit)
        {
            const std::uint8_t mask = static_cast<std::uint8_t>(1u << bit);
            if ((bitmap[byteIndex] & mask) == 0)
            {
                bitmap[byteIndex] = static_cast<std::uint8_t>(bitmap[byteIndex] | mask);
                out.index = static_cast<unsigned int>(byteIndex * 8 + (7 - bit));
                out.grew = false;
                out.name = L"oem" + std::to_wstring(out.index) + L".inf";
                return true;
            }
        }
    }

    // No free bit: append one byte and take its top bit (index = old size * 8).
    out.index = static_cast<unsigned int>(bitmap.size() * 8);
    out.grew = true;
    out.name = L"oem" + std::to_wstring(out.index) + L".inf";

    if (bitmap.size() >= kOemDriverInfFileMapSize)
    {
        err = L"oemN.inf 位图已满（" + std::to_wstring(kOemDriverInfFileMapSize) + L" 字节）";
        return false;
    }
    bitmap.push_back(0x80);
    return true;
}

bool ReleaseOemInfName(std::vector<std::uint8_t>& bitmap, const std::wstring& infName, std::wstring& err)
{
    err.clear();

    // swscanf_s(L"oem%u.inf") semantics: the whole string must match, and the digits
    // must be decimal with no sign and no leading junk.
    unsigned int index = 0;
    if (swscanf_s(infName.c_str(), L"oem%u.inf", &index) != 1)
    {
        err = L"不是 oemN.inf 形式的名字：" + infName;
        return false;
    }
    std::wstring expect = L"oem" + std::to_wstring(index) + L".inf";
    if (expect != infName)
    {
        err = L"不是规范形式的 oemN.inf 名字：" + infName;
        return false;
    }

    const std::size_t byteIndex = index / 8;
    if (byteIndex >= bitmap.size())
    {
        err = L"oemN.inf 位图里没有第 " + std::to_wstring(index) + L" 位（位图 " +
              std::to_wstring(bitmap.size()) + L" 字节）";
        return false;
    }

    const int bit = static_cast<int>(index % 8);
    const std::uint8_t mask = static_cast<std::uint8_t>(1u << (7 - bit));
    bitmap[byteIndex] = static_cast<std::uint8_t>(bitmap[byteIndex] & ~mask);
    return true;
}

bool ChoosePublishedInfName(unsigned int importFlags,
                            const std::wstring& originalInfTitle,
                            std::vector<std::uint8_t>& bitmap,
                            OemInfAllocation& out,
                            std::wstring& err)
{
    out = OemInfAllocation();
    err.clear();

    if (originalInfTitle.empty())
    {
        err = L"原始 INF 文件名为空";
        return false;
    }

    if (ImportKeepsOriginalInfName(importFlags))
    {
        out.name = originalInfTitle;
        out.index = 0;
        out.grew = false;
        return true;
    }

    return AcquireOemInfName(bitmap, out, err);
}

} // namespace ocid
