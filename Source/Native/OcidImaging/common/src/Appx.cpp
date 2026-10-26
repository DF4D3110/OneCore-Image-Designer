// OCID Imaging —— AppX 落地（见 include\ocid\imaging\Appx.h 的职责说明）

#include "ocid/imaging/Appx.h"
#include "ocid/imaging/Common.h"
#include "ocid/imaging/Plan.h"

#include <algorithm>
#include <cstdlib>
#include <filesystem>
#include <set>
#include <string>
#include <vector>

#include <windows.h>

namespace ocid
{
namespace imaging
{

// 定义在后面，这里先声明（ExpandPackagePath 里要用）
std::string ReplaceAllText(const std::string& text, const std::string& from, const std::string& to);

namespace
{

std::string LowerAscii(const std::string& text)
{
    std::string out = text;
    for (char& c : out)
    {
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    return out;
}

std::string UpperAscii(const std::string& text)
{
    std::string out = text;
    for (char& c : out)
    {
        if (c >= 'a' && c <= 'z') c = static_cast<char>(c - 'a' + 'A');
    }
    return out;
}

std::string TrimAscii(const std::string& text)
{
    std::size_t begin = 0;
    std::size_t end = text.size();
    while (begin < end && (text[begin] == ' ' || text[begin] == '\t' || text[begin] == '\r' || text[begin] == '\n'))
        ++begin;
    while (end > begin && (text[end - 1] == ' ' || text[end - 1] == '\t' || text[end - 1] == '\r' || text[end - 1] == '\n'))
        --end;
    return text.substr(begin, end - begin);
}

std::string WideToUtf8Local(const std::wstring& text)
{
    if (text.empty()) return std::string();
    const int need = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
    if (need <= 0) return std::string();
    std::string out(static_cast<std::size_t>(need), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), &out[0], need, nullptr, nullptr);
    return out;
}

std::wstring Utf8ToWideLocal(const std::string& text)
{
    if (text.empty()) return std::wstring();
    const int need = MultiByteToWideChar(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), nullptr, 0);
    if (need <= 0) return std::wstring();
    std::wstring out(static_cast<std::size_t>(need), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), &out[0], need);
    return out;
}

std::string FileNameOf(const std::string& path)
{
    const std::size_t pos = path.find_last_of("\\/");
    return pos == std::string::npos ? path : path.substr(pos + 1);
}

std::string DirNameOf(const std::string& path)
{
    const std::size_t pos = path.find_last_of("\\/");
    return pos == std::string::npos ? std::string() : path.substr(0, pos);
}

bool EndsWithNoCase(const std::string& text, const std::string& suffix)
{
    if (text.size() < suffix.size()) return false;
    return LowerAscii(text.substr(text.size() - suffix.size())) == LowerAscii(suffix);
}

bool FileExists(const std::string& path)
{
    const DWORD attrs = GetFileAttributesW(Utf8ToWideLocal(path).c_str());
    return attrs != INVALID_FILE_ATTRIBUTES && (attrs & FILE_ATTRIBUTE_DIRECTORY) == 0;
}

bool DirExists(const std::string& path)
{
    const DWORD attrs = GetFileAttributesW(Utf8ToWideLocal(path).c_str());
    return attrs != INVALID_FILE_ATTRIBUTES && (attrs & FILE_ATTRIBUTE_DIRECTORY) != 0;
}

std::string JoinPath(const std::string& base, const std::string& leaf)
{
    if (base.empty()) return leaf;
    if (leaf.empty()) return base;
    if (!base.empty() && (base.back() == '\\' || base.back() == '/')) return base + leaf;
    return base + "\\" + leaf;
}

// 去掉扩展名（只认最后一个 '.'，且必须在文件名里而不是目录名里）。
std::string StripExtension(const std::string& path)
{
    const std::string name = FileNameOf(path);
    const std::size_t dot = name.find_last_of('.');
    if (dot == std::string::npos) return path;
    return path.substr(0, path.size() - (name.size() - dot));
}

std::string ExtensionOf(const std::string& path)
{
    const std::string name = FileNameOf(path);
    const std::size_t dot = name.find_last_of('.');
    return dot == std::string::npos ? std::string() : name.substr(dot);
}

// 列目录里的文件/子目录名（不递归）。
std::vector<std::string> ListDirectory(const std::string& directory)
{
    std::vector<std::string> names;
    WIN32_FIND_DATAW data;
    const std::wstring pattern = Utf8ToWideLocal(directory) + L"\\*";
    HANDLE handle = FindFirstFileW(pattern.c_str(), &data);
    if (handle == INVALID_HANDLE_VALUE) return names;
    do
    {
        const std::wstring name(data.cFileName);
        if (name == L"." || name == L"..") continue;
        names.push_back(WideToUtf8Local(name));
    } while (FindNextFileW(handle, &data) != 0);
    FindClose(handle);
    return names;
}

bool IsAbsolutePath(const std::string& path)
{
    if (path.size() >= 2 && path[1] == ':') return true;
    if (path.size() >= 2 && path[0] == '\\' && path[1] == '\\') return true;
    return false;
}

// 把 FM 里 Path 属性里的 $(mspackageroot) 一类宏替换成 packagesRoot；再展开 %ENV%。
std::string ExpandPackagePath(const std::string& raw, const std::string& packagesRoot, std::string& error)
{
    std::string text = TrimAscii(raw);
    text = ReplaceAllText(text, "$(mspackageroot)", packagesRoot);
    text = ReplaceAllText(text, "$(MSPackageRoot)", packagesRoot);
    text = ReplaceAllText(text, "$(MSPACKAGEROOT)", packagesRoot);
    text = ReplaceAllText(text, "$(buildroot)", packagesRoot);
    if (!packagesRoot.empty())
    {
        text = ReplaceAllText(text, "$(_RELEASEDIR)", packagesRoot);
    }
    // %VAR% 展开（SetEnvironmentVariable 级别的简单替换）
    std::string expanded;
    expanded.reserve(text.size());
    for (std::size_t i = 0; i < text.size(); ++i)
    {
        if (text[i] == '%')
        {
            const std::size_t close = text.find('%', i + 1);
            if (close != std::string::npos && close > i + 1)
            {
                const std::string name = text.substr(i + 1, close - i - 1);
                const DWORD need = GetEnvironmentVariableW(Utf8ToWideLocal(name).c_str(), nullptr, 0);
                if (need > 0)
                {
                    std::vector<wchar_t> buffer(need);
                    GetEnvironmentVariableW(Utf8ToWideLocal(name).c_str(), buffer.data(), need);
                    expanded += WideToUtf8Local(std::wstring(buffer.data()));
                    i = close;
                    continue;
                }
            }
        }
        expanded.push_back(text[i]);
    }
    text = expanded;
    if (text.empty())
    {
        error = "FM 里的 AppX PackageFile 既有 Path 为空又无法用 --packages-root 兜底";
        return std::string();
    }
    if (!IsAbsolutePath(text))
    {
        text = JoinPath(packagesRoot, text);
    }
    return text;
}

// CPUType 判定：FM 里常见 "*"（全收）、"arm64"/"AMD64"、以及 "arm;arm64" 这种列表。
bool CpuTypeMatches(const std::string& cpuType, const std::string& arch)
{
    const std::string want = LowerAscii(AppxArchName(arch));
    const std::string wantRaw = LowerAscii(arch);
    const std::string text = LowerAscii(TrimAscii(cpuType));
    if (text.empty() || text == "*") return true;
    std::size_t begin = 0;
    while (begin <= text.size())
    {
        const std::size_t comma = text.find_first_of(",;", begin);
        const std::string piece = TrimAscii(text.substr(begin, comma == std::string::npos ? std::string::npos : comma - begin));
        if (!piece.empty() && (piece == "*" || piece == want || piece == wantRaw))
        {
            return true;
        }
        if (comma == std::string::npos) break;
        begin = comma + 1;
    }
    return false;
}

bool ContainsNoCase(const std::vector<std::string>& values, const std::string& want)
{
    for (const std::string& value : values)
    {
        if (LowerAscii(TrimAscii(value)) == LowerAscii(TrimAscii(want))) return true;
    }
    return false;
}

// 递归找名叫 "AppX" 的节点（FM 里 AppX 挂在根下面，也可能多层）。
void CollectAppxNodes(const XmlNode* node, std::vector<const XmlNode*>& out)
{
    if (node == nullptr) return;
    if (LowerAscii(node->name) == "appx") out.push_back(node);
    for (const std::unique_ptr<XmlNode>& kid : node->kids)
    {
        CollectAppxNodes(kid.get(), out);
    }
}

// AppX 节点下的 <AppXPackages>/<ConditionalAppXPackages> 里都是 <PackageFile>；
// 条件包的 <ConditionalAppXPackage> 只带 <Condition>，不含 PackageFile，所以递归收 PackageFile 是安全的。
void CollectPackageFiles(const XmlNode* node, std::vector<const XmlNode*>& out)
{
    if (node == nullptr) return;
    if (LowerAscii(node->name) == "packagefile") out.push_back(node);
    for (const std::unique_ptr<XmlNode>& kid : node->kids)
    {
        CollectPackageFiles(kid.get(), out);
    }
}

std::string QuoteArg(const std::string& text)
{
    return "\"" + text + "\"";
}

} // namespace

std::string ReplaceAllText(const std::string& text, const std::string& from, const std::string& to)
{
    if (from.empty()) return text;
    std::string out;
    out.reserve(text.size());
    std::size_t pos = 0;
    while (true)
    {
        const std::size_t hit = text.find(from, pos);
        if (hit == std::string::npos)
        {
            out.append(text, pos, std::string::npos);
            break;
        }
        out.append(text, pos, hit - pos);
        out += to;
        pos = hit + from.size();
    }
    return out;
}

std::string AppxArchName(const std::string& arch)
{
    const std::string text = LowerAscii(TrimAscii(arch));
    if (text == "arm64") return "ARM64";
    if (text == "arm") return "ARM";
    if (text == "amd64" || text == "x64") return "AMD64";
    if (text == "x86" || text == "i386") return "X86";
    if (text == "wow64") return "WOW64";
    if (text == "msil" || text == "neutral") return "MSIL";
    return std::string();
}

bool AppxCollect(const AppxRequest& request, AppxPlan& plan, std::vector<std::string>& report, std::string& error)
{
    plan = AppxPlan();
    report.clear();
    error.clear();
    if (request.oemInputPath.empty())
    {
        error = "没有给 OEMInput.xml 路径，无法确定语言表与 Optional AppX";
        return false;
    }
    OemInput oem;
    if (!LoadOemInput(request.oemInputPath, oem, error))
    {
        error = "读 OEMInput 失败：" + error;
        return false;
    }
    std::vector<std::string> languages = request.languages;
    if (languages.empty()) languages = oem.languages;

    std::string fmDir = request.fmDir;
    if (fmDir.empty())
    {
        fmDir = DirNameOf(request.oemInputPath);
    }
    if (!DirExists(fmDir))
    {
        error = "FeatureManifest 目录不存在：" + fmDir;
        return false;
    }

    // FM 顺序：OEMInput.AdditionalFMs 里点名的先处理，其余（排序）随后。
    std::vector<std::string> fmFiles;
    {
        std::error_code ec;
        std::vector<std::string> found;
        for (const std::filesystem::directory_entry& entry : std::filesystem::directory_iterator(std::filesystem::u8path(fmDir), ec))
        {
            if (ec) break;
            if (!entry.is_regular_file(ec)) continue;
            const std::string name = entry.path().filename().u8string();
            if (!EndsWithNoCase(name, ".xml")) continue;
            if (LowerAscii(name) == "oeminput.xml") continue;
            found.push_back(name);
        }
        if (ec)
        {
            error = "枚举 FM 目录失败：" + ec.message();
            return false;
        }
        std::sort(found.begin(), found.end());
        std::set<std::string> used;
        for (const std::string& wanted : oem.additionalFms)
        {
            for (const std::string& name : found)
            {
                if (LowerAscii(name) == LowerAscii(wanted) && used.insert(LowerAscii(name)).second)
                {
                    fmFiles.push_back(name);
                }
            }
        }
        for (const std::string& name : found)
        {
            if (used.insert(LowerAscii(name)).second) fmFiles.push_back(name);
        }
    }

    std::set<std::string> seenPath;
    for (const std::string& fmName : fmFiles)
    {
        const std::string fmPath = JoinPath(fmDir, fmName);
        std::string parseError;
        std::unique_ptr<XmlDoc> doc = XmlParseFile(Utf8ToWideLocal(fmPath), parseError);
        if (!doc || doc->Root() == nullptr)
        {
            report.push_back("跳过 " + fmName + "（不是 XML：" + parseError + "）");
            continue;
        }
        if (LowerAscii(doc->Root()->name) != "featuremanifest")
        {
            continue;
        }
        std::vector<const XmlNode*> appxNodes;
        CollectAppxNodes(doc->Root(), appxNodes);
        for (const XmlNode* appx : appxNodes)
        {
            std::vector<const XmlNode*> packageFiles;
            CollectPackageFiles(appx, packageFiles);
            for (const XmlNode* file : packageFiles)
            {
                ++plan.examined;
                AppxPackageSpec spec;
                spec.id = TrimAscii(Attr(file, "ID"));
                spec.name = TrimAscii(AttrOr(file, "Name", ""));
                // ADK 的语义（FMAddOnAppX.GetAllAppXPackages）：FM 条目带 CPUType 属性且与 OEMInput.CPUType
                // 匹配时，AppXFMPkgInfo.CpuType = OEMInput.CPUType（形如 "arm64"）；没有该属性时留空，
                // AppxUtils.GetApplicableArchitecturesId 把空串当 NEUTRAL。传 "arm;arm64"、 "*" 都会抛
                // NoSuchArgumentException("Invalid cpuType")。
                const std::string declaredCpu = TrimAscii(AttrOr(file, "CPUType", ""));
                spec.cpuType = declaredCpu.empty() ? std::string() : request.arch;
                spec.region = TrimAscii(AttrOr(file, "Region", ""));
                spec.preferStub = LowerAscii(TrimAscii(AttrOr(file, "PreferStub", "false"))) == "true";
                spec.optional = LowerAscii(TrimAscii(AttrOr(file, "Optional", "false"))) == "true";
                spec.fmFile = fmName;

                if (!CpuTypeMatches(declaredCpu, request.arch))
                {
                    ++plan.skippedArch;
                    continue;
                }
                if (spec.optional && !ContainsNoCase(oem.appxOptionalPackages, spec.id) &&
                    !ContainsNoCase(oem.appxOptionalPackages, FileNameOf(spec.name)))
                {
                    ++plan.skippedOptional;
                    continue;
                }

                const std::string dirText = ExpandPackagePath(AttrOr(file, "Path", ""), request.packagesRoot, error);
                if (dirText.empty())
                {
                    error = "FM " + fmName + " 里的 AppX " + spec.id + " 解析 Path 失败：" + error;
                    return false;
                }
                if (spec.name.empty())
                {
                    error = "FM " + fmName + " 里的 AppX " + spec.id + " 缺 Name 属性";
                    return false;
                }
                spec.packagePath = JoinPath(dirText, spec.name);

                const std::string licenseFile = TrimAscii(AttrOr(file, "LicenseFile", ""));
                if (!licenseFile.empty())
                {
                    spec.licensePath = JoinPath(dirText, licenseFile);
                }
                else
                {
                    // 没有 LicenseFile 时按 ImageApp 的约定找 <PFN>.xml（apps 目录里就是这个布局）。
                    const std::string guess = JoinPath(dirText, spec.id + ".xml");
                    if (!spec.id.empty() && FileExists(guess)) spec.licensePath = guess;
                }
                // FM 里写的 LicenseFile 在包目录里不一定存在（例如 Edge 的
                // Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe.xml 被改名成 EdgeStable_ARM64.xml）。
                // ADK 的 CommitAppXPackages 会直接 LongPathFile.Copy 而崩，所以这里先落回退：
                // <id>.xml → <包名去扩展名>.xml → 目录里唯一的 xml。
                if (!spec.licensePath.empty() && !FileExists(spec.licensePath))
                {
                    const std::string wanted = spec.licensePath;
                    spec.licensePath.clear();
                    const std::string byId = JoinPath(dirText, spec.id + ".xml");
                    const std::string byPackage = JoinPath(dirText, StripExtension(spec.name) + ".xml");
                    if (!spec.id.empty() && FileExists(byId))
                    {
                        spec.licensePath = byId;
                    }
                    else if (FileExists(byPackage))
                    {
                        spec.licensePath = byPackage;
                    }
                    else
                    {
                        std::string onlyXml;
                        std::size_t xmlCount = 0;
                        for (const std::string& entry : ListDirectory(dirText))
                        {
                            if (LowerAscii(ExtensionOf(entry)) == ".xml")
                            {
                                ++xmlCount;
                                onlyXml = JoinPath(dirText, entry);
                            }
                        }
                        if (xmlCount == 1) spec.licensePath = onlyXml;
                    }
                    if (spec.licensePath.empty())
                    {
                        report.push_back("license 缺失（按无 license 处理）：" + spec.id + " → " + wanted);
                    }
                    else
                    {
                        report.push_back("license 回退：" + spec.id + " → " + spec.licensePath);
                    }
                }

                if (!FileExists(spec.packagePath))
                {
                    ++plan.skippedMissing;
                    report.push_back("缺包：" + spec.id + " → " + spec.packagePath);
                    continue;
                }
                if (!seenPath.insert(LowerAscii(spec.packagePath)).second)
                {
                    ++plan.duplicates;
                    continue;
                }
                if (spec.licensePath.empty()) ++plan.frameworks;
                plan.packages.push_back(spec);
            }
        }
    }

    report.push_back("AppX 清单：FM " + std::to_string(fmFiles.size()) + " 个，看过 " + std::to_string(plan.examined) +
                     " 条 PackageFile，收 " + std::to_string(plan.packages.size()) + " 个包（framework " +
                     std::to_string(plan.frameworks) + " 个）；跳过：架构 " + std::to_string(plan.skippedArch) +
                     "、Optional 未点名 " + std::to_string(plan.skippedOptional) + "、文件缺失 " +
                     std::to_string(plan.skippedMissing) + "、重复 " + std::to_string(plan.duplicates));
    if (!languages.empty())
    {
        std::string joined;
        for (const std::string& language : languages)
        {
            if (!joined.empty()) joined += ";";
            joined += language;
        }
        report.push_back("AppX 语言表：" + joined);
    }
    return true;
}

bool AppxWriteList(const AppxPlan& plan, const std::wstring& tsvPath, std::string& error)
{
    error.clear();
    std::string text;
    std::string reported;
    for (const AppxPackageSpec& spec : plan.packages)
    {
        text += spec.id;
        text += '\t';
        text += spec.packagePath;
        text += '\t';
        text += spec.name;
        text += '\t';
        text += spec.licensePath;
        text += '\t';
        text += spec.optional ? "1" : "0";
        text += '\t';
        text += spec.cpuType;
        text += '\t';
        text += spec.preferStub ? "1" : "0";
        text += '\t';
        text += spec.region;
        text += "\r\n";
    }
    if (!WriteFileBytes(tsvPath, text.c_str(), text.size(), error))
    {
        error = "写 AppX 清单失败：" + error;
        return false;
    }
    (void)reported;
    return true;
}

namespace
{

// 起 32 位 helper（OcidAppX.exe）。stdout/stderr 直接继承控制台，helper 的日志同时写 --log 文件。
bool RunHelperProcess(const std::string& exePath, const std::string& commandLine, unsigned long timeoutSec, std::string& error)
{
    std::wstring exeW = Utf8ToWideLocal(exePath);
    std::wstring cmdW = Utf8ToWideLocal(commandLine);
    if (exeW.empty())
    {
        error = "helper 路径为空";
        return false;
    }
    std::vector<wchar_t> mutableCmd(cmdW.begin(), cmdW.end());
    mutableCmd.push_back(L'\0');
    STARTUPINFOW si = {};
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi = {};
    if (!CreateProcessW(exeW.c_str(), mutableCmd.data(), nullptr, nullptr, TRUE, 0, nullptr, nullptr, &si, &pi))
    {
        error = "CreateProcess(" + exePath + ") 失败，win32=" + std::to_string(GetLastError());
        return false;
    }
    const DWORD waitMs = static_cast<DWORD>(timeoutSec == 0 ? INFINITE : timeoutSec * 1000ul);
    const DWORD wait = WaitForSingleObject(pi.hProcess, waitMs);
    if (wait == WAIT_TIMEOUT)
    {
        TerminateProcess(pi.hProcess, 9);
        WaitForSingleObject(pi.hProcess, 30000);
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        error = "helper 超时（" + std::to_string(timeoutSec) + " 秒）";
        return false;
    }
    DWORD exitCode = 1;
    GetExitCodeProcess(pi.hProcess, &exitCode);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    if (exitCode != 0)
    {
        error = "helper 退出码 " + std::to_string(exitCode);
        return false;
    }
    return true;
}

} // namespace

bool AppxRunHelper(const AppxRequest& request, const AppxPlan& plan, bool commit,
                   std::vector<std::string>& report, std::string& error)
{
    error.clear();
    if (request.helperPath.empty() || !FileExists(request.helperPath))
    {
        error = "找不到 OcidAppX.exe（--appx-helper）：" + request.helperPath;
        return false;
    }
    if (plan.packages.empty())
    {
        report.push_back(std::string("AppX ") + (commit ? "commit" : "stage") + "：清单为空，跳过");
        return true;
    }
    if (request.stagingDir.empty())
    {
        error = "AppX 暂存目录为空（应为 <PreInstalled>\\Temp）";
        return false;
    }
    if (commit && request.preInstalledPath.empty())
    {
        error = "AppX commit 需要 PreInstalled 分区路径";
        return false;
    }
    const std::wstring helperDir = Utf8ToWideLocal(DirNameOf(request.helperPath));
    std::string tsv = request.stagingDir + "\\ocid-appx-list.tsv";
    if (!AppxWriteList(plan, Utf8ToWideLocal(tsv), error)) return false;

    std::string languages;
    for (const std::string& language : request.languages)
    {
        if (!languages.empty()) languages += ";";
        languages += language;
    }

    std::string command = QuoteArg(request.helperPath) + (commit ? " commit" : " stage");
    command += " --list " + QuoteArg(tsv);
    command += " --staging " + QuoteArg(request.stagingDir);
    if (commit) command += " --preinstalled " + QuoteArg(request.preInstalledPath);
    command += " --arch " + QuoteArg(AppxArchName(request.arch));
    if (!languages.empty()) command += " --languages " + QuoteArg(languages);
    if (!request.logPath.empty()) command += " --log " + QuoteArg(request.logPath);

    report.push_back(std::string("AppX ") + (commit ? "commit" : "stage") + "：" + std::to_string(plan.packages.size()) +
                     " 个包 → " + (commit ? request.preInstalledPath : request.stagingDir));
    const unsigned long timeout = commit ? 7200ul : 7200ul;
    if (!RunHelperProcess(request.helperPath, command, timeout, error))
    {
        error = std::string("AppX ") + (commit ? "commit" : "stage") + " 失败：" + error;
        return false;
    }
    return true;
}

bool AppxInstall(const AppxRequest& request, AppxPlan& plan, std::vector<std::string>& report, std::string& error)
{
    report.clear();
    // 调用方（`appx install` 的 CmdAppx）已经 collect 过；只有直接走 AppxInstall 时才自己收集，
    // 免得把调用方裁过的清单（例如 --limit）又覆盖回全量。
    if (plan.packages.empty())
    {
        if (!AppxCollect(request, plan, report, error)) return false;
    }
    if (plan.packages.empty())
    {
        report.push_back("AppX：没有要装的包，跳过 stage/commit");
        return true;
    }
    if (!AppxRunHelper(request, plan, false, report, error)) return false;
    if (!AppxRunHelper(request, plan, true, report, error)) return false;
    report.push_back("AppX：stage + commit 完成");
    return true;
}

} // namespace imaging
} // namespace ocid
