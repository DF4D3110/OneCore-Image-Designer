// OCID DriverInf - implementation. See include/ocid/DriverInf.h for the evidence.
// Keep this file pure ASCII.

#include "ocid/DriverInf.h"

#include "ocid/DriverPackage.h"

#include <windows.h>

#include <cwctype>
#include <fstream>
#include <string.h>

namespace ocid {
namespace {

std::wstring TrimCopy(const std::wstring& text) {
    size_t begin = 0;
    size_t end = text.size();
    while (begin < end && iswspace(text[begin]) != 0) {
        ++begin;
    }
    while (end > begin && iswspace(text[end - 1]) != 0) {
        --end;
    }
    return text.substr(begin, end - begin);
}

std::wstring ToLowerCopy(const std::wstring& text) {
    std::wstring out = text;
    for (wchar_t& ch : out) {
        ch = static_cast<wchar_t>(towlower(ch));
    }
    return out;
}

bool IEquals(const std::wstring& left, const std::wstring& right) {
    return _wcsicmp(left.c_str(), right.c_str()) == 0;
}
// Real INFs put trailing comments after a value on the same line, e.g.
//   ClassGuid={14b62f50-3f15-11dd-ae16-0800200c9a66}    ; If class above is new, ...
// setupapi tolerates that, so we must too. A ';' inside double quotes is data, not a comment.
std::wstring StripTrailingComment(const std::wstring& text) {
    bool inQuotes = false;
    for (size_t i = 0; i < text.size(); ++i) {
        const wchar_t ch = text[i];
        if (ch == L'"') {
            inQuotes = !inQuotes;
            continue;
        }
        if (ch == L';' && !inQuotes) {
            return TrimCopy(text.substr(0, i));
        }
    }
    return TrimCopy(text);
}

int HexDigitValue(wchar_t ch) {
    if (ch >= L'0' && ch <= L'9') {
        return ch - L'0';
    }
    if (ch >= L'a' && ch <= L'f') {
        return 10 + (ch - L'a');
    }
    if (ch >= L'A' && ch <= L'F') {
        return 10 + (ch - L'A');
    }
    return -1;
}

bool ParseUnsignedField(const std::wstring& text, unsigned int& out) {
    if (text.empty()) {
        return false;
    }
    unsigned long long acc = 0;
    for (const wchar_t ch : text) {
        if (ch < L'0' || ch > L'9') {
            return false;
        }
        acc = acc * 10ull + static_cast<unsigned long long>(ch - L'0');
        if (acc > 0xFFFFFFFFull) {
            return false;
        }
    }
    out = static_cast<unsigned int>(acc);
    return true;
}

// Split the whole document into trimmed logical lines. INF line continuation is a
// trailing backslash: the physical newline is dropped and the next line is appended.
std::vector<std::wstring> SplitLogicalLines(const std::wstring& text) {
    std::vector<std::wstring> physical;
    std::wstring current;
    for (size_t i = 0; i < text.size(); ++i) {
        const wchar_t ch = text[i];
        if (ch == L'\r') {
            // CRLF is one break, not two: a bare CR is only a break on its own.
            const bool followedByLf = (i + 1 < text.size()) && (text[i + 1] == L'\n');
            physical.push_back(current);
            current.clear();
            if (followedByLf) {
                ++i;
            }
            continue;
        }
        if (ch == L'\n') {
            physical.push_back(current);
            current.clear();
            continue;
        }
        current.push_back(ch);
    }
    physical.push_back(current);

    std::vector<std::wstring> logical;
    std::wstring pending;
    for (const std::wstring& raw : physical) {
        std::wstring line = TrimCopy(raw);
        if (!pending.empty()) {
            line = pending + line;
            pending.clear();
        }
        if (!line.empty() && line.back() == L'\\') {
            line.pop_back();
            pending = line;
            continue;
        }
        logical.push_back(line);
    }
    if (!pending.empty()) {
        logical.push_back(pending);
    }
    return logical;
}

bool DecodeInfBytes(const std::vector<std::uint8_t>& bytes, std::wstring& text, std::wstring& error) {
    text.clear();
    error.clear();
    if (bytes.empty()) {
        return true;
    }
    if (bytes.size() >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) {
        const size_t count = (bytes.size() - 2) / 2;
        text.resize(count);
        for (size_t i = 0; i < count; ++i) {
            text[i] = static_cast<wchar_t>(static_cast<unsigned int>(bytes[2 + i * 2]) |
                                           (static_cast<unsigned int>(bytes[3 + i * 2]) << 8));
        }
        return true;
    }
    if (bytes.size() >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) {
        const size_t count = (bytes.size() - 2) / 2;
        text.resize(count);
        for (size_t i = 0; i < count; ++i) {
            text[i] = static_cast<wchar_t>((static_cast<unsigned int>(bytes[2 + i * 2]) << 8) |
                                           static_cast<unsigned int>(bytes[3 + i * 2]));
        }
        return true;
    }
    const int needed = MultiByteToWideChar(CP_ACP, 0, reinterpret_cast<const char*>(bytes.data()),
                                           static_cast<int>(bytes.size()), nullptr, 0);
    if (needed <= 0) {
        error = L"MultiByteToWideChar failed on the INF bytes";
        return false;
    }
    text.resize(static_cast<size_t>(needed));
    (void)MultiByteToWideChar(CP_ACP, 0, reinterpret_cast<const char*>(bytes.data()),
                              static_cast<int>(bytes.size()), &text[0], needed);
    return true;
}

LedgerValue MakeStringValue(const wchar_t* name, const std::wstring& text) {
    LedgerValue value;
    value.name = name;
    value.type = LedgerValueType::String;
    value.text = text;
    return value;
}

LedgerValue MakeDwordValue(const wchar_t* name, unsigned int dword) {
    LedgerValue value;
    value.name = name;
    value.type = LedgerValueType::DWord;
    value.dword = dword;
    return value;
}

LedgerValue MakeBinaryValue(const wchar_t* name, const std::vector<std::uint8_t>& binary) {
    LedgerValue value;
    value.name = name;
    value.type = LedgerValueType::Binary;
    value.binary = binary;
    return value;
}

} // namespace

// ---------------------------------------------------------------------------
// InfDocument lookups

const InfSection* InfDocument::FindSection(const std::wstring& name) const {
    for (const InfSection& section : sections) {
        if (IEquals(section.name, name)) {
            return &section;
        }
    }
    return nullptr;
}

std::wstring InfDocument::GetValue(const std::wstring& section, const std::wstring& key) const {
    const InfSection* found = FindSection(section);
    if (found == nullptr) {
        return std::wstring();
    }
    for (const InfEntry& entry : found->entries) {
        if (entry.hasValue && IEquals(entry.key, key)) {
            return entry.value;
        }
    }
    return std::wstring();
}

bool InfDocument::HasSection(const std::wstring& name) const {
    return FindSection(name) != nullptr;
}
bool InfDocument::TryGetString(const std::wstring& token, std::wstring& out) const {
    const InfSection* strings = FindSection(L"Strings");
    if (strings == nullptr) {
        return false;
    }
    for (const InfEntry& entry : strings->entries) {
        if (!entry.hasValue || !IEquals(entry.key, token)) {
            continue;
        }
        std::wstring value = entry.value;
        if (value.size() >= 2 && value.front() == L'"' && value.back() == L'"') {
            value = value.substr(1, value.size() - 2);
        }
        out = value;
        return true;
    }
    return false;
}

std::wstring InfDocument::Unquote(const std::wstring& text) {
    if (text.size() >= 2 && text.front() == L'"' && text.back() == L'"') {
        return text.substr(1, text.size() - 2);
    }
    return text;
}

std::wstring InfDocument::ResolveTokens(const std::wstring& text) const {
    std::wstring out;
    out.reserve(text.size());
    size_t i = 0;
    while (i < text.size()) {
        const size_t open = text.find(L'%', i);
        if (open == std::wstring::npos) {
            out.append(text, i, std::wstring::npos);
            break;
        }
        const size_t close = text.find(L'%', open + 1);
        if (close == std::wstring::npos) {
            out.append(text, i, std::wstring::npos);
            break;
        }
        out.append(text, i, open - i);
        const std::wstring token = text.substr(open + 1, close - open - 1);
        std::wstring value;
        if (!token.empty() && TryGetString(token, value)) {
            out.append(value);
        } else {
            out.append(text, open, close - open + 1);   // keep %unknown% literal
        }
        i = close + 1;
    }
    return out;
}

// ---------------------------------------------------------------------------
// Parsing

bool ParseInfText(const std::wstring& text, InfDocument& out, std::wstring& err) {
    out = InfDocument();
    err.clear();

    std::wstring body = text;
    // A decoded UTF-16 file keeps its BOM as a leading U+FEFF character.
    if (!body.empty() && body[0] == 0xFEFF) {
        body.erase(0, 1);
    }

    const std::vector<std::wstring> lines = SplitLogicalLines(body);
    InfSection* current = nullptr;
    for (const std::wstring& line : lines) {
        if (line.empty() || line[0] == L';') {
            continue;
        }
        if (line[0] == L'[') {
            const size_t close = line.find(L']');
            if (close == std::wstring::npos || close < 2) {
                err = L"unterminated section header: " + line;
                return false;
            }
            InfSection section;
            section.name = TrimCopy(line.substr(1, close - 1));
            out.sections.push_back(section);
            current = &out.sections.back();
            continue;
        }
        if (current == nullptr) {
            // Text before the first section header: real INFs sometimes carry a
            // stray comment or a build stamp. Ignore it rather than fail.
            continue;
        }
        InfEntry entry;
        const size_t equals = line.find(L'=');
        if (equals == std::wstring::npos) {
            entry.key = StripTrailingComment(line);
            entry.hasValue = false;
        } else {
            entry.key = TrimCopy(line.substr(0, equals));
            entry.value = StripTrailingComment(line.substr(equals + 1));
            entry.hasValue = true;
        }
        if (!entry.key.empty()) {
            current->entries.push_back(entry);
        }
    }

    out.ok = true;
    return true;
}

bool ParseInf(const std::vector<std::uint8_t>& bytes, InfDocument& out, std::wstring& err) {
    std::wstring text;
    if (!DecodeInfBytes(bytes, text, err)) {
        out = InfDocument();
        return false;
    }
    return ParseInfText(text, out, err);
}

bool ParseInfFile(const std::wstring& path, InfDocument& out, std::wstring& err) {
    std::ifstream stream(path.c_str(), std::ios::binary);
    if (!stream) {
        err = L"cannot open INF: " + path;
        out = InfDocument();
        return false;
    }
    std::vector<std::uint8_t> bytes((std::istreambuf_iterator<char>(stream)),
                                    std::istreambuf_iterator<char>());
    return ParseInf(bytes, out, err);
}

// ---------------------------------------------------------------------------
// [Version]

bool ParseInfDateFileTime(const std::wstring& text, std::uint64_t& out, std::wstring& err) {
    out = 0;
    err.clear();
    const std::wstring spec = TrimCopy(text);
    std::vector<std::wstring> parts;
    std::wstring current;
    for (const wchar_t ch : spec) {
        if (ch == L'/' || ch == L'-') {
            parts.push_back(current);
            current.clear();
            continue;
        }
        current.push_back(ch);
    }
    parts.push_back(current);
    if (parts.size() != 3) {
        err = L"driver date is not MM/DD/YYYY: " + spec;
        return false;
    }
    unsigned int month = 0;
    unsigned int day = 0;
    unsigned int year = 0;
    if (!ParseUnsignedField(TrimCopy(parts[0]), month) || !ParseUnsignedField(TrimCopy(parts[1]), day) ||
        !ParseUnsignedField(TrimCopy(parts[2]), year)) {
        err = L"driver date has a non-numeric field: " + spec;
        return false;
    }
    if (month < 1 || month > 12 || day < 1 || day > 31 || year < 1601 || year > 30827) {
        err = L"driver date field is out of range: " + spec;
        return false;
    }
    SYSTEMTIME systemTime = {};
    systemTime.wYear = static_cast<WORD>(year);
    systemTime.wMonth = static_cast<WORD>(month);
    systemTime.wDay = static_cast<WORD>(day);
    FILETIME fileTime = {};
    if (SystemTimeToFileTime(&systemTime, &fileTime) == FALSE) {
        err = L"SystemTimeToFileTime rejected the driver date: " + spec;
        return false;
    }
    out = (static_cast<std::uint64_t>(fileTime.dwHighDateTime) << 32) | fileTime.dwLowDateTime;
    return true;
}

bool ParseDriverVer(const std::wstring& spec, InfVersionInfo& out, std::wstring& err) {
    err.clear();
    out.hasDriverVer = false;
    out.driverDate = 0;
    out.major = 0;
    out.minor = 0;
    out.build = 0;
    out.revision = 0;

    const std::wstring text = TrimCopy(spec);
    if (text.empty()) {
        err = L"DriverVer is empty";
        return false;
    }
    const size_t separator = text.find_first_of(L",;");
    const std::wstring datePart = TrimCopy(separator == std::wstring::npos ? text : text.substr(0, separator));
    const std::wstring versionPart =
        separator == std::wstring::npos ? std::wstring() : TrimCopy(text.substr(separator + 1));

    if (!versionPart.empty()) {
        std::vector<std::wstring> fields;
        std::wstring current;
        for (const wchar_t ch : versionPart) {
            if (ch == L'.') {
                fields.push_back(current);
                current.clear();
                continue;
            }
            current.push_back(ch);
        }
        fields.push_back(current);
        // Five fields exist in the wild (iirsp.inf: 5.4.19.0.1). The store only has room
        // for four, so the extra ones are dropped - the same thing setupapi does.
        if (fields.empty()) {
            err = L"DriverVer version is empty: " + versionPart;
            return false;
        }
        unsigned int numbers[4] = {0, 0, 0, 0};
        const size_t fieldCount = (fields.size() < 4) ? fields.size() : 4;
        for (size_t i = 0; i < fieldCount; ++i) {
            if (!ParseUnsignedField(TrimCopy(fields[i]), numbers[i])) {
                err = L"DriverVer version field is not a number: " + versionPart;
                return false;
            }
        }
        out.major = numbers[0];
        out.minor = numbers[1];
        out.build = numbers[2];
        out.revision = numbers[3];
    }

    if (!datePart.empty()) {
        std::uint64_t fileTime = 0;
        if (!ParseInfDateFileTime(datePart, fileTime, err)) {
            return false;
        }
        out.driverDate = fileTime;
    }
    out.hasDriverVer = true;
    return true;
}

bool ParseInfGuid(const std::wstring& text, std::vector<std::uint8_t>& out, std::wstring& err) {
    out.clear();
    err.clear();
    std::vector<std::uint8_t> digits;
    for (const wchar_t ch : text) {
        if (ch == L'{' || ch == L'}' || ch == L'-' || iswspace(ch) != 0) {
            continue;
        }
        const int value = HexDigitValue(ch);
        if (value < 0) {
            err = L"GUID has a non-hex character: " + text;
            return false;
        }
        digits.push_back(static_cast<std::uint8_t>(value));
    }
    if (digits.size() != 32) {
        err = L"GUID must have 32 hex digits: " + text;
        return false;
    }
    // Registry order: Data1/Data2/Data3 little-endian, Data4 verbatim.
    std::vector<std::uint8_t> guid(16, 0);
    for (size_t i = 0; i < 4; ++i) {
        guid[i] = static_cast<std::uint8_t>((digits[(3 - i) * 2] << 4) | digits[(3 - i) * 2 + 1]);
    }
    for (size_t i = 0; i < 2; ++i) {
        guid[4 + i] = static_cast<std::uint8_t>((digits[(5 - i) * 2] << 4) | digits[(5 - i) * 2 + 1]);
        guid[6 + i] = static_cast<std::uint8_t>((digits[(7 - i) * 2] << 4) | digits[(7 - i) * 2 + 1]);
    }
    for (size_t i = 0; i < 8; ++i) {
        guid[8 + i] = static_cast<std::uint8_t>((digits[16 + i * 2] << 4) | digits[17 + i * 2]);
    }
    out = guid;
    return true;
}

bool ReadInfVersion(const InfDocument& inf, InfVersionInfo& out) {
    out = InfVersionInfo();
    if (!inf.ok) {
        out.error = L"INF document is not parsed";
        return false;
    }
    out.providerRaw = inf.GetValue(L"Version", L"Provider");
    out.provider = InfDocument::Unquote(inf.ResolveTokens(out.providerRaw));
    out.className = inf.GetValue(L"Version", L"Class");
    const std::wstring classGuid = inf.GetValue(L"Version", L"ClassGuid");
    if (!classGuid.empty()) {
        std::wstring guidError;
        if (!ParseInfGuid(classGuid, out.classGuid, guidError)) {
            out.error = guidError;
            return false;
        }
    }
    const std::wstring driverVer = inf.GetValue(L"Version", L"DriverVer");
    if (!driverVer.empty()) {
        std::wstring verError;
        if (!ParseDriverVer(driverVer, out, verError)) {
            out.error = verError;
            return false;
        }
    }
    out.ok = true;
    return true;
}

std::vector<std::uint8_t> DriverVersionPropertyData(unsigned int major,
                                                    unsigned int minor,
                                                    unsigned int build,
                                                    unsigned int revision) {
    const std::uint32_t low = (static_cast<std::uint32_t>(build) << 16) | (static_cast<std::uint32_t>(revision) & 0xFFFFu);
    const std::uint32_t high = (static_cast<std::uint32_t>(major) << 16) | (static_cast<std::uint32_t>(minor) & 0xFFFFu);
    std::vector<std::uint8_t> bytes(8, 0);
    bytes[0] = static_cast<std::uint8_t>(low & 0xFFu);
    bytes[1] = static_cast<std::uint8_t>((low >> 8) & 0xFFu);
    bytes[2] = static_cast<std::uint8_t>((low >> 16) & 0xFFu);
    bytes[3] = static_cast<std::uint8_t>((low >> 24) & 0xFFu);
    bytes[4] = static_cast<std::uint8_t>(high & 0xFFu);
    bytes[5] = static_cast<std::uint8_t>((high >> 8) & 0xFFu);
    bytes[6] = static_cast<std::uint8_t>((high >> 16) & 0xFFu);
    bytes[7] = static_cast<std::uint8_t>((high >> 24) & 0xFFu);
    return bytes;
}

std::vector<std::uint8_t> DriverDatePropertyData(std::uint64_t filetime) {
    std::vector<std::uint8_t> bytes(8, 0);
    for (size_t i = 0; i < 8; ++i) {
        bytes[i] = static_cast<std::uint8_t>((filetime >> (i * 8)) & 0xFFull);
    }
    return bytes;
}

// ---------------------------------------------------------------------------
// Ledger plan

DriverLedgerPlan BuildDriverLedgerPlan(const DriverLedgerRequest& request) {
    DriverLedgerPlan plan;
    if (request.inf == nullptr || !request.inf->ok) {
        plan.error = L"a parsed INF document is required";
        return plan;
    }
    if (request.storeId.empty()) {
        plan.error = L"store id is required";
        return plan;
    }
    if (request.infFileName.empty()) {
        plan.error = L"the INF file name is required";
        return plan;
    }

    InfVersionInfo version;
    if (!ReadInfVersion(*request.inf, version)) {
        plan.error = version.error;
        return plan;
    }

    const bool inbox = (request.importFlags & kImportFlagInbox) != 0;

    LedgerKey package;
    package.relativePath = L"DriverPackages\\" + request.storeId;
    package.values.push_back(MakeStringValue(L"0002", request.infFileName));
    package.values.push_back(MakeDwordValue(L"0005", inbox ? 1u : 0u));
    // 0006 reads 1 on every one of the 158 packages measured on the 8061 x86 image and
    // on the ARM image. It is written by the same code path as 0005; its meaning is not
    // identified yet, so it is reproduced rather than derived.
    package.values.push_back(MakeDwordValue(L"0006", 1u));
    if (!request.signerName.empty()) {
        package.values.push_back(MakeStringValue(L"0007", request.signerName));
        package.values.push_back(MakeDwordValue(L"0008", request.signerScore));
    } else {
        package.notes.push_back(L"0007/0008 (signer) omitted: caller did not supply a verified signer");
    }
    package.values.push_back(MakeDwordValue(L"000a", request.architecture));
    package.values.push_back(MakeDwordValue(L"000b", 0u));
    if (version.provider.empty()) {
        package.notes.push_back(L"000c (provider) omitted: [Version] has no Provider");
    } else {
        package.values.push_back(MakeStringValue(L"000c", version.provider));
    }
    if (version.classGuid.size() == 16) {
        package.values.push_back(MakeBinaryValue(L"000d", version.classGuid));
    } else {
        package.notes.push_back(L"000d (class GUID) omitted: [Version] has no ClassGuid");
    }
    if (version.hasDriverVer) {
        package.values.push_back(MakeBinaryValue(L"000e", DriverDatePropertyData(version.driverDate)));
        package.values.push_back(MakeBinaryValue(
            L"000f", DriverVersionPropertyData(version.major, version.minor, version.build, version.revision)));
    } else {
        package.notes.push_back(L"000e/000f (driver date/version) omitted: [Version] has no DriverVer");
    }
    plan.keys.push_back(package);

    // The database keys stay on the package's own INF name; the published oemN.inf name
    // never appears here (measured: oem0.inf exists in the store, no oem0.inf key).
    LedgerKey infKey;
    infKey.relativePath = L"DriverInfFiles\\" + request.infFileName;
    LedgerValue storeIds;
    storeIds.name = L"0002";
    storeIds.type = LedgerValueType::MultiString;
    storeIds.text = request.storeId;   // items joined with '\0'; no terminator
    infKey.values.push_back(storeIds);
    infKey.values.push_back(MakeStringValue(L"0003", request.storeId));
    infKey.notes.push_back(
        L"0004 (configuration sections) omitted: needs the models/decoration pass, which is not written yet");
    plan.keys.push_back(infKey);

    plan.ok = true;
    return plan;
}

} // namespace ocid
