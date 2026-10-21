// OCID Imaging —— 选包实现
//
// 【为什么这一层值得单独存在】imageapp 的"要装哪些包"是整条流水线里唯一纯逻辑、
// 又最容易错的部分：它决定包集，包集错了后面建盘、装包、注册表全跟着错。把它抽成
// 无副作用的纯函数（输入 XML、输出有序包清单），就能对着参照产物反复对拍。
#include "ocid/imaging/Plan.h"

#include "ocid/imaging/Common.h"

#include <algorithm>
#include <cstdint>
#include <filesystem>
#include <map>
#include <set>

namespace ocid::imaging
{
namespace
{

const char* const kPackageToken = "31bf3856ad364e35";  // Microsoft 包的公钥令牌

// ── 小工具 ────────────────────────────────────────────────────────────────

bool StartsWithNoCase(const std::string& text, const char* prefix)
{
    const std::size_t size = std::char_traits<char>::length(prefix);
    if (text.size() < size)
    {
        return false;
    }
    for (std::size_t i = 0; i < size; ++i)
    {
        const unsigned char a = static_cast<unsigned char>(text[i]);
        const unsigned char b = static_cast<unsigned char>(prefix[i]);
        const unsigned char la = (a >= 'A' && a <= 'Z') ? static_cast<unsigned char>(a + 32) : a;
        const unsigned char lb = (b >= 'A' && b <= 'Z') ? static_cast<unsigned char>(b + 32) : b;
        if (la != lb)
        {
            return false;
        }
    }
    return true;
}

std::string Lower(const std::string& text)
{
    std::string out = text;
    for (char& ch : out)
    {
        if (ch >= 'A' && ch <= 'Z')
        {
            ch = static_cast<char>(ch + 32);
        }
    }
    return out;
}

std::string Upper(const std::string& text)
{
    std::string out = text;
    for (char& ch : out)
    {
        if (ch >= 'a' && ch <= 'z')
        {
            ch = static_cast<char>(ch - 32);
        }
    }
    return out;
}

// `<package>~<token>~<arch>~<lang>~.cab` 的第二段（架构）
std::string ArchOfCab(const std::string& cab)
{
    std::size_t first = cab.find('~');
    if (first == std::string::npos)
    {
        return std::string();
    }
    std::size_t second = cab.find('~', first + 1);
    if (second == std::string::npos)
    {
        return std::string();
    }
    std::size_t third = cab.find('~', second + 1);
    if (third == std::string::npos)
    {
        return std::string();
    }
    return cab.substr(second + 1, third - second - 1);
}

// `<package>~<token>~<arch>~<lang>~.cab` 的第三段（语言，空 = neutral）
std::string LanguageOfCab(const std::string& cab)
{
    std::size_t first = cab.find('~');
    if (first == std::string::npos)
    {
        return std::string();
    }
    std::size_t second = cab.find('~', first + 1);
    if (second == std::string::npos)
    {
        return std::string();
    }
    std::size_t third = cab.find('~', second + 1);
    if (third == std::string::npos)
    {
        return std::string();
    }
    std::size_t fourth = cab.find('~', third + 1);
    if (fourth == std::string::npos)
    {
        return std::string();
    }
    return cab.substr(third + 1, fourth - third - 1);
}

std::string FileNameOf(const std::string& path)
{
    const std::size_t pos = path.find_last_of("\\/");
    return pos == std::string::npos ? path : path.substr(pos + 1);
}

// 目标架构 → 允许出现在 cab 名里的架构记号
std::set<std::string> AllowedArchTokens(const std::string& arch)
{
    const std::string target = Lower(arch);
    std::set<std::string> tokens;
    if (target == "amd64" || target == "x64")
    {
        tokens.insert("amd64");
        tokens.insert("wow64");
    }
    else if (target == "arm64")
    {
        tokens.insert("arm64");
        tokens.insert("arm64.arm");
        tokens.insert("arm64.x86");
        tokens.insert("wow64");
    }
    else if (target == "x86" || target == "arm")
    {
        tokens.insert(target);
    }
    else
    {
        tokens.insert(target);
    }
    // 中性/未知架构的包（没有波浪号）永远保留
    return tokens;
}

// 枚举目录下的 *.xml（不递归），按文件名排序，保证可复现
bool ListXmlFiles(const std::string& dir, std::vector<std::string>& out, std::string& error)
{
    const std::wstring pattern = CombinePath(Utf8ToWide(dir), L"*.xml");
    WIN32_FIND_DATAW data = {};
    HANDLE find = ::FindFirstFileW(pattern.c_str(), &data);
    if (find == INVALID_HANDLE_VALUE)
    {
        error = "无法枚举目录：" + dir;
        return false;
    }
    do
    {
        if ((data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
        {
            continue;
        }
        out.push_back(WideToUtf8(data.cFileName));
    } while (::FindNextFileW(find, &data) != 0);
    ::FindClose(find);
    std::sort(out.begin(), out.end());
    return true;
}

// ── OEMInput 读取 ─────────────────────────────────────────────────────────

std::vector<std::string> TextsOf(const XmlNode* parent, const char* local)
{
    std::vector<std::string> out;
    if (parent == nullptr)
    {
        return out;
    }
    for (const XmlNode* kid : Children(parent, local))
    {
        const std::string value = Trim(Text(kid));
        if (!value.empty())
        {
            out.push_back(value);
        }
    }
    return out;
}

// ── FM 读取结果 ───────────────────────────────────────────────────────────

struct FmPackage
{
    std::string id;
    std::string name;
    std::string language;
    std::string wow;
    std::string cpuType;
    std::string releaseType;
    std::string featureName;
    std::string device;
    std::string soc;
    bool featureIdentifier = false;
    std::set<std::string> featureIds;
};

struct FmSection
{
    PackageSection section = PackageSection::Base;
    std::vector<FmPackage> packages;
};

struct FmFile
{
    std::string path;
    std::string file;
    std::string id;
    std::vector<FmSection> sections;
};

bool SectionFromName(const std::string& local, PackageSection& section)
{
    if (local == "BasePackages") { section = PackageSection::Base; return true; }
    if (local == "Features") { section = PackageSection::Feature; return true; }
    if (local == "OEMDevicePlatformPackages") { section = PackageSection::DevicePlatform; return true; }
    if (local == "DeviceSpecificPackages") { section = PackageSection::DeviceSpecific; return true; }
    if (local == "DeviceLayoutPackages") { section = PackageSection::DeviceLayout; return true; }
    if (local == "LanguagePacks") { section = PackageSection::LanguagePack; return true; }
    if (local == "UserInstallableOptionalFeatures") { section = PackageSection::UserInstallable; return true; }
    if (local == "ReleasePackages") { section = PackageSection::Release; return true; }
    return false;
}

// FeatureID 不是 PackageFile 的直接子节点，而是里面一格：
//   <PackageFile ...><FeatureIDs><FeatureID>A</FeatureID>…</FeatureIDs></PackageFile>
// 所以要递归找（只找直接子节点会一条都匹配不上，features 全部落空）。
void CollectFeatureIds(const XmlNode* node, std::set<std::string>& ids)
{
    for (const XmlNode* kid : ChildrenAny(node))
    {
        if (kid->local == "FeatureID")
        {
            const std::string value = Trim(Text(kid));
            if (!value.empty())
            {
                ids.insert(value);
            }
        }
        else
        {
            CollectFeatureIds(kid, ids);
        }
    }
}

void CollectPackage(const XmlNode* node, FmPackage& package)
{
    package.id = Attr(node, "ID");
    package.name = Attr(node, "Name");
    package.language = Attr(node, "Language");
    package.wow = Attr(node, "Wow");
    package.cpuType = Attr(node, "CPUType");
    package.releaseType = Attr(node, "ReleaseType");
    package.featureName = Attr(node, "FeatureName");
    package.device = Attr(node, "Device");
    package.soc = Attr(node, "SOC");
    package.featureIdentifier = EqualsNoCase(Attr(node, "FeatureIdentifierPackage"), "true");
    CollectFeatureIds(node, package.featureIds);

}

bool LoadFm(const std::string& path, FmFile& fm, std::string& error)
{
    std::unique_ptr<XmlDoc> doc = XmlParseFile(Utf8ToWide(path), error);
    if (doc == nullptr)
    {
        return false;
    }
    const XmlNode* root = doc->Root();
    if (root == nullptr || root->local != "FeatureManifest")
    {
        error = "不是 FeatureManifest：" + path;
        return false;
    }
    fm.path = path;
    fm.file = FileNameOf(path);
    fm.id = Attr(root, "ID");
    for (const XmlNode* sectionNode : ChildrenAny(root))
    {
        PackageSection section = PackageSection::Base;
        if (!SectionFromName(sectionNode->local, section))
        {
            continue;
        }
        FmSection sectionOut;
        sectionOut.section = section;
        if (section == PackageSection::Feature)
        {
            // <Features><Microsoft><PackageFile/></Microsoft><OEM>…</OEM></Features>
            for (const XmlNode* group : ChildrenAny(sectionNode))
            {
                for (const XmlNode* node : Children(group, "PackageFile"))
                {
                    FmPackage package;
                    CollectPackage(node, package);
                    sectionOut.packages.push_back(package);
                }
            }
        }
        else
        {
            for (const XmlNode* node : Children(sectionNode, "PackageFile"))
            {
                FmPackage package;
                CollectPackage(node, package);
                sectionOut.packages.push_back(package);
            }
        }
        fm.sections.push_back(sectionOut);
    }
    return true;
}

// ── 判定：这个条目要不要收 ────────────────────────────────────────────────

struct OemFilters
{
    std::set<std::string> features;
    std::set<std::string> userInstallable;
    std::set<std::string> devices;      // 大写
    std::set<std::string> languages;    // 大写
    std::string layout;                 // 大写
    std::string releaseType;
};

std::set<std::string> SplitUpper(const std::string& text)
{
    std::set<std::string> out;
    std::string current;
    for (char ch : text + ";")
    {
        if (ch == ';' || ch == ',')
        {
            const std::string item = Trim(current);
            if (!item.empty())
            {
                out.insert(Upper(item));
            }
            current.clear();
        }
        else
        {
            current.push_back(ch);
        }
    }
    return out;
}

bool KeepPackage(PackageSection section, const FmPackage& package, const OemFilters& filters)
{
    switch (section)
    {
    case PackageSection::Base:
        return true;
    case PackageSection::Feature:
    {
        for (const std::string& id : package.featureIds)
        {
            if (filters.features.count(id) != 0)
            {
                return true;
            }
        }
        return false;
    }
    case PackageSection::DevicePlatform:
    case PackageSection::DeviceSpecific:
    {
        const std::set<std::string> devices = SplitUpper(package.device);
        for (const std::string& device : devices)
        {
            if (filters.devices.count(device) != 0)
            {
                return true;
            }
        }
        return false;
    }
    case PackageSection::DeviceLayout:
        return filters.layout.empty() ? false : Upper(Trim(package.soc)) == filters.layout;
    case PackageSection::LanguagePack:
        return filters.languages.count(Upper(Trim(package.language))) != 0;
    case PackageSection::UserInstallable:
        return filters.userInstallable.count(Trim(package.featureName)) != 0;
    case PackageSection::Release:
        return EqualsNoCase(Trim(package.releaseType), filters.releaseType);
    }
    return false;
}

// ── 展开：一条 PackageFile 可能变成多条 cab ───────────────────────────────
// 规则实测（2026-10）：显式 Name 原样；否则 `<ID>~31bf3856ad364e35~<arch>~<lang>~.cab`；
// Language="*" → neutral + 全部 OEM 语言；Wow="*" → AMD64 + wow64；
// 顺序 = 语言在外层、架构在内层（先出全部 neutral 的各架构，再出各语言的）。
std::vector<PackageRef> ExpandPackage(const FmPackage& package, PackageSection section,
                                      const std::string& fmId, const std::string& fmFile,
                                      const std::vector<std::string>& languages)
{
    std::vector<PackageRef> out;
    if (!package.name.empty())
    {
        PackageRef ref;
        ref.cab = package.name;
        ref.id = package.id;
        ref.fmId = fmId;
        ref.fmFile = fmFile;
        ref.section = section;
        ref.arch = ArchOfCab(package.name);
        ref.language = LanguageOfCab(package.name);
        ref.featureIdentifier = package.featureIdentifier;
        out.push_back(ref);
        return out;
    }
    if (package.id.empty())
    {
        return out;
    }
    std::vector<std::string> archs{"AMD64"};
    if (package.wow == "*")
    {
        archs.push_back("wow64");
    }
    std::vector<std::string> langs{std::string()};
    if (package.language == "*")
    {
        langs.insert(langs.end(), languages.begin(), languages.end());
    }
    for (const std::string& language : langs)
    {
        for (const std::string& arch : archs)
        {
            PackageRef ref;
            ref.cab = package.id + "~" + kPackageToken + "~" + arch + "~" + language + "~.cab";
            ref.id = package.id;
            ref.fmId = fmId;
            ref.fmFile = fmFile;
            ref.section = section;
            ref.arch = arch;
            ref.language = language;
            ref.featureIdentifier = package.featureIdentifier;
            out.push_back(ref);
        }
    }
    return out;
}

} // namespace

const char* SectionName(PackageSection section)
{
    switch (section)
    {
    case PackageSection::Base: return "BasePackages";
    case PackageSection::Feature: return "Features";
    case PackageSection::DevicePlatform: return "OEMDevicePlatformPackages";
    case PackageSection::DeviceSpecific: return "DeviceSpecificPackages";
    case PackageSection::DeviceLayout: return "DeviceLayoutPackages";
    case PackageSection::LanguagePack: return "LanguagePacks";
    case PackageSection::UserInstallable: return "UserInstallableOptionalFeatures";
    case PackageSection::Release: return "ReleasePackages";
    }
    return "?";
}

bool LoadOemInput(const std::string& path, OemInput& oem, std::string& err)
{
    std::unique_ptr<XmlDoc> doc = XmlParseFile(Utf8ToWide(path), err);
    if (doc == nullptr)
    {
        return false;
    }
    const XmlNode* root = doc->Root();
    if (root == nullptr || root->local != "OEMInput")
    {
        err = "不是 OEMInput：" + path;
        return false;
    }
    oem.path = path;
    oem.sv = TextOf(root, "SV");
    oem.product = TextOf(root, "Product");
    oem.device = TextOf(root, "Device");
    oem.deviceLayoutType = TextOf(root, "DeviceLayoutType");
    oem.releaseType = TextOf(root, "ReleaseType");
    oem.buildType = TextOf(root, "BuildType");
    oem.languages = TextsOf(Child(root, "Languages"), "Language");
    oem.additionalFms.clear();
    for (const std::string& fm : TextsOf(Child(root, "AdditionalFMs"), "AdditionalFM"))
    {
        oem.additionalFms.push_back(FileNameOf(fm));
    }
    if (const XmlNode* features = Child(root, "Features"))
    {
        oem.features.clear();
        for (const XmlNode* group : ChildrenAny(features))
        {
            for (const std::string& feature : TextsOf(group, "Feature"))
            {
                oem.features.push_back(feature);
            }
        }
    }
    if (const XmlNode* uif = Child(root, "UserInstallableFeatures"))
    {
        oem.userInstallableFeatures.clear();
        for (const XmlNode* group : ChildrenAny(uif))
        {
            for (const std::string& feature : TextsOf(group, "Feature"))
            {
                oem.userInstallableFeatures.push_back(feature);
            }
        }
        if (oem.userInstallableFeatures.empty())
        {
            oem.userInstallableFeatures = TextsOf(uif, "Feature");
        }
        if (oem.userInstallableFeatures.empty())
        {
            oem.userInstallableFeatures = TextsOf(uif, "UserInstallableFeature");
        }
    }
    oem.appxOptionalPackages = TextsOf(root, "AppXOptionalPackage");
    if (oem.appxOptionalPackages.empty())
    {
        oem.appxOptionalPackages = TextsOf(root, "AppX");
    }
    return true;
}

bool BuildPlan(const PlanInput& input, PlanResult& result, std::string& err)
{
    if (input.oemInputPath.empty())
    {
        err = "没有给 OEMInput.xml 路径";
        return false;
    }
    OemInput oem;
    if (!LoadOemInput(input.oemInputPath, oem, err))
    {
        return false;
    }

    // FM 列表：显式给定优先（保持顺序）；否则"OEMInput.AdditionalFM 顺序 + 目录里其余 FM（排序）"。
    std::vector<std::string> fmPaths;
    if (!input.fmFiles.empty())
    {
        fmPaths = input.fmFiles;
    }
    else
    {
        std::string dir = input.fmDir;
        if (dir.empty())
        {
            const std::size_t pos = input.oemInputPath.find_last_of("\\/");
            dir = pos == std::string::npos ? std::string(".") : input.oemInputPath.substr(0, pos);
        }
        std::vector<std::string> found;
        if (!ListXmlFiles(dir, found, err))
        {
            return false;
        }
        std::set<std::string> used;
        for (const std::string& name : oem.additionalFms)
        {
            for (const std::string& file : found)
            {
                if (EqualsNoCase(file, name) && used.insert(Lower(file)).second)
                {
                    fmPaths.push_back(dir + "\\" + file);
                }
            }
        }
        for (const std::string& file : found)
        {
            if (EqualsNoCase(file, "OEMInput.xml"))
            {
                continue;
            }
            if (used.insert(Lower(file)).second)
            {
                fmPaths.push_back(dir + "\\" + file);
            }
        }
    }

    OemFilters filters;
    for (const std::string& feature : oem.features)
    {
        filters.features.insert(feature);
    }
    for (const std::string& feature : oem.userInstallableFeatures)
    {
        filters.userInstallable.insert(Trim(feature));
    }
    filters.devices = SplitUpper(oem.device);
    for (const std::string& language : oem.languages)
    {
        filters.languages.insert(Upper(Trim(language)));
    }
    filters.layout = Upper(Trim(oem.deviceLayoutType));
    filters.releaseType = Trim(oem.releaseType);

    const std::set<std::string> allowedArch = AllowedArchTokens(input.arch);
    std::set<std::string> seen;
    std::map<PackageSection, std::size_t> perSection;

    for (const std::string& fmPath : fmPaths)
    {
        FmFile fm;
        if (!LoadFm(fmPath, fm, err))
        {
            return false;
        }
        ++result.stats.fmCount;
        for (const FmSection& section : fm.sections)
        {
            for (const FmPackage& package : section.packages)
            {
                ++result.stats.examined;
                if (!KeepPackage(section.section, package, filters))
                {
                    ++result.stats.skippedFilter;
                    continue;
                }
                ++result.stats.selected;
                for (PackageRef& ref : ExpandPackage(package, section.section, fm.id, fm.file, oem.languages))
                {
                    ++result.stats.expanded;
                    const std::string archToken = Lower(ArchOfCab(ref.cab));
                    if (!archToken.empty() && allowedArch.count(archToken) == 0)
                    {
                        ++result.stats.skippedArch;
                        continue;
                    }
                    const std::string key = Lower(ref.cab);
                    if (!input.keepDuplicates && !seen.insert(key).second)
                    {
                        ++result.stats.duplicates;
                        continue;
                    }
                    seen.insert(key);
                    ++perSection[ref.section];
                    result.packages.push_back(ref);
                }
            }
        }
    }

    result.stats.unique = result.packages.size();
    if (result.packages.empty())
    {
        err = "选包结果为空：检查 OEMInput 的 Features/Languages/Device/DeviceLayoutType/ReleaseType";
        return false;
    }
    // 记录各 section 的贡献，排查"某个 FM 没生效"时很有用
    for (const auto& item : perSection)
    {
        result.warnings.push_back(std::string("section ") + SectionName(item.first) + " = " +
                                  std::to_string(item.second));
    }
    return true;
}

bool WritePlanXml(const PlanResult& plan, const std::string& path, std::string& err)
{
    std::string out;
    out += "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";
    out += "<OcidPlan>\n";
    out += "  <Packages>\n";
    for (const PackageRef& ref : plan.packages)
    {
        out += "    <Package cab=\"";
        XmlEscapeTo(out, ref.cab);
        out += "\" id=\"";
        XmlEscapeTo(out, ref.id);
        out += "\" fm=\"";
        XmlEscapeTo(out, ref.fmId);
        out += "\" section=\"";
        XmlEscapeTo(out, SectionName(ref.section));
        out += "\" arch=\"";
        XmlEscapeTo(out, ref.arch);
        out += "\" lang=\"";
        XmlEscapeTo(out, ref.language);
        out += "\" fip=\"";
        out += ref.featureIdentifier ? "true" : "false";
        out += "\"/>\n";
    }
    out += "  </Packages>\n";
    out += "</OcidPlan>\n";
    return WriteFileText(Utf8ToWide(path), out, err);
}

bool WriteCabList(const PlanResult& plan, const std::string& path, std::string& err)
{
    std::string out;
    for (const PackageRef& ref : plan.packages)
    {
        out += ref.cab;
        out += "\r\n";
    }
    return WriteFileText(Utf8ToWide(path), out, err);
}

} // namespace ocid::imaging
