// OCID 服务栈 · 控制台入口
//
// 用户明确说过"有些东西有必要留个控制台入口方便操作也是可以的"，这个就是服务栈的第一个入口。
// 它让"清单 → 可执行的注册表操作"这条链可以在命令行上直接观察和验证，
// 不必每次都起 WPF 界面。
//
// 用法：
//   ocid_manifest info <manifest>              摘要（标识 / 各类计数）
//   ocid_manifest ops  <manifest>              输出推导出的注册表操作（TSV）
//   ocid_manifest ops  <manifest> --json       输出 JSON（便于 C# 侧消费）
//   ocid_manifest scan <dir> [maxFiles]        批量扫描并汇总
//   ocid_manifest dump-xsd <manifest> <out>    导出 configurationSchema 的 XSD 原文（排障用）
//
// 输出约定：**数据走 stdout，诊断走 stderr** —— 这样 C# 侧可以干净地消费 stdout。

#include "ocid/Manifest.h"
#include "ocid/WcmSchema.h"

#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>

#ifdef _WIN32
#include <windows.h>
#include <io.h>
#include <fcntl.h>
#endif

using namespace ocid;

// ── 窄/宽字符串与控制台输出 ─────────────────────────────────────────────
//
// 控制台是 GBK 的话直接打宽字符会乱码，所以统一转成 UTF-8 再写 stdout。
// 关键：把 stdout 设成 **二进制模式**（避免 CRLF 被再转换、也避免 0x1A 之类被当 EOF）。

namespace {

    void SetupConsole()
    {
#ifdef _WIN32
        _setmode(_fileno(stdout), _O_BINARY);
        SetConsoleOutputCP(CP_UTF8);
#endif
    }

    std::string ToUtf8(const std::wstring& w)
    {
#ifdef _WIN32
        if (w.empty()) return std::string();
        int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), nullptr, 0, nullptr, nullptr);
        std::string out((size_t)n, '\0');
        WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), &out[0], n, nullptr, nullptr);
        return out;
#else
        return std::string(w.begin(), w.end());
#endif
    }

    std::wstring FromUtf8OrAnsi(const char* s)
    {
#ifdef _WIN32
        if (!s) return L"";
        int n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, s, -1, nullptr, 0);
        if (n > 0) {
            std::wstring w((size_t)(n - 1), L'\0');
            MultiByteToWideChar(CP_UTF8, 0, s, -1, &w[0], n);
            return w;
        }
        // 不是合法 UTF-8：按 ANSI 处理（命令行参数常见来源）
        n = MultiByteToWideChar(CP_ACP, 0, s, -1, nullptr, 0);
        std::wstring w((size_t)(n - 1), L'\0');
        MultiByteToWideChar(CP_ACP, 0, s, -1, &w[0], n);
        return w;
#else
        std::string t(s ? s : "");
        return std::wstring(t.begin(), t.end());
#endif
    }

    void Out(const std::string& s)   { fwrite(s.data(), 1, s.size(), stdout); }
    void OutLn(const std::string& s) { Out(s); Out("\n"); }
    void Err(const std::string& s)   { fflush(stdout); fputs(s.c_str(), stderr); fputc('\n', stderr); }

#ifdef _WIN32
    bool ReadFileUtf8(const std::wstring& path, std::wstring& out, std::wstring& err)
    {
        FILE* f = _wfopen(path.c_str(), L"rb");
        if (!f) { err = L"打不开文件：" + path; return false; }
        fseek(f, 0, SEEK_END);
        long n = ftell(f);
        fseek(f, 0, SEEK_SET);
        std::string buf((size_t)(n > 0 ? n : 0), '\0');
        if (n > 0) fread(&buf[0], 1, (size_t)n, f);
        fclose(f);

        size_t start = 0;
        if (buf.size() >= 3 && (unsigned char)buf[0] == 0xEF
            && (unsigned char)buf[1] == 0xBB && (unsigned char)buf[2] == 0xBF)
            start = 3;

        int wlen = MultiByteToWideChar(CP_UTF8, 0, buf.data() + start, (int)(buf.size() - start), nullptr, 0);
        out.assign((size_t)wlen, L'\0');
        if (wlen > 0)
            MultiByteToWideChar(CP_UTF8, 0, buf.data() + start, (int)(buf.size() - start), &out[0], wlen);
        return true;
    }
#endif

    std::string JsonEscape(const std::wstring& w)
    {
        std::string s = ToUtf8(w);
        std::string out;
        out.reserve(s.size() + 8);
        for (unsigned char c : s) {
            switch (c) {
                case '"':  out += "\\\""; break;
                case '\\': out += "\\\\"; break;
                case '\n': out += "\\n";  break;
                case '\r': out += "\\r";  break;
                case '\t': out += "\\t";  break;
                default:
                    if (c < 0x20) { char b[8]; sprintf_s(b, "\\u%04x", c); out += b; }
                    else out += (char)c;
            }
        }
        return out;
    }

} // namespace

// ── 命令实现 ────────────────────────────────────────────────────────────

static int CmdInfo(const std::wstring& path)
{
#ifdef _WIN32
    std::wstring text, err;
    if (!ReadFileUtf8(path, text, err)) { Err(ToUtf8(err)); return 2; }

    ComponentManifest m;
    auto r = ParseComponentManifest(text, m);
    if (!r.ok) { Err("manifest parse failed: " + ToUtf8(r.error)); return 3; }

    OutLn("identity         : " + ToUtf8(m.identity.ToString()));
    OutLn("versionless      : " + ToUtf8(m.identity.ToString(false)));
    OutLn("effectiveVersion : " + ToUtf8(m.identity.EffectiveVersion()));
    OutLn("componentName    : " + ToUtf8(m.identity.ComponentNamePrefix()) + "_<hash>");
    OutLn("versionScope     : " + ToUtf8(m.identity.ScopeString()));
    OutLn("registryKeys     : " + std::to_string(m.registryKeys.size()));
    OutLn("registryValues   : " + std::to_string(m.RegistryValueCount()));
    OutLn("dependencies     : " + std::to_string(m.dependencies.size()));
    OutLn("files            : " + std::to_string(m.files.size()));
    OutLn("directories      : " + std::to_string(m.directories.size()));
    OutLn("sdDefinitions    : " + std::to_string(m.securityDescriptorDefinitions.size()));
    OutLn("xsd:element      : " + std::to_string(m.configurationSchema.elementNames.size()));
    if (!m.infFile.empty()) OutLn("infFile          : " + ToUtf8(m.infFile));

    WcmConfigurationSchema schema;
    auto sr = schema.Parse(m.configurationSchema.xsdText);
    if (sr.ok) {
        OutLn("wcmSettings      : " + std::to_string(schema.Size()));
        OutLn("wcmRegistryOps   : " + std::to_string(schema.RegistrySettingCount()));
    }
    if (!r.warnings.empty()) {
        Err("warnings (" + std::to_string(r.warnings.size()) + " unhandled elements/attrs):");
        size_t shown = 0;
        for (const auto& w : r.warnings) { if (shown++ >= 10) break; Err("  " + ToUtf8(w)); }
    }
    return 0;
#else
    (void)path; return 2;
#endif
}

static int CmdOps(const std::wstring& path, bool json)
{
#ifdef _WIN32
    std::wstring text, err;
    if (!ReadFileUtf8(path, text, err)) { Err(ToUtf8(err)); return 2; }

    ComponentManifest m;
    auto r = ParseComponentManifest(text, m);
    if (!r.ok) { Err("manifest parse failed: " + ToUtf8(r.error)); return 3; }

    WcmConfigurationSchema schema;
    auto sr = schema.Parse(m.configurationSchema.xsdText);
    if (!sr.ok) { Err("wcm schema parse failed: " + ToUtf8(sr.error)); return 4; }

    auto ops = ToRegistryOperations(schema);

    if (json) {
        OutLn("[");
        for (size_t i = 0; i < ops.size(); ++i) {
            const auto& o = ops[i];
            std::string line = "  {\"root\":\"" + JsonEscape(o.root)
                             + "\",\"subKey\":\"" + JsonEscape(o.subKey)
                             + "\",\"valueName\":\"" + JsonEscape(o.valueName)
                             + "\",\"valueType\":\"" + JsonEscape(o.valueType)
                             + "\",\"data\":\"" + JsonEscape(o.data)
                             + "\",\"scope\":\"" + JsonEscape(o.scope)
                             + "\",\"sourceItem\":\"" + JsonEscape(o.sourceItem) + "\"}";
            if (i + 1 < ops.size()) line += ",";
            OutLn(line);
        }
        OutLn("]");
    } else {
        // TSV：root \t subKey \t valueName \t valueType \t data \t scope
        OutLn("#root\tsubKey\tvalueName\tvalueType\tdata\tscope");
        for (const auto& o : ops) {
            OutLn(ToUtf8(o.root) + "\t" + ToUtf8(o.subKey) + "\t" + ToUtf8(o.valueName) + "\t"
                + ToUtf8(o.valueType) + "\t" + ToUtf8(o.data) + "\t" + ToUtf8(o.scope));
        }
    }

    Err("ops=" + std::to_string(ops.size()));
    return 0;
#else
    (void)path; (void)json; return 2;
#endif
}

static int CmdScan(const std::wstring& dir, int maxFiles)
{
#ifdef _WIN32
    WIN32_FIND_DATAW fd;
    std::wstring pattern = dir + L"\\*.manifest";
    HANDLE h = FindFirstFileW(pattern.c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) { Err("no .manifest found in that directory"); return 2; }

    int total = 0, okM = 0, badM = 0, okSchema = 0;
    size_t regValues = 0, xsdItems = 0, ops = 0;
    std::map<std::wstring, int> warnHistogram;
    int hkcr = 0, hklm = 0, hkcu = 0, allUsers = 0, perUser = 0;
    std::vector<std::wstring> firstErrors;

    do {
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
        if (maxFiles > 0 && total >= maxFiles) break;
        ++total;

        std::wstring text, err;
        if (!ReadFileUtf8(dir + L"\\" + fd.cFileName, text, err)) { ++badM; continue; }

        ComponentManifest m;
        auto r = ParseComponentManifest(text, m);
        if (!r.ok) {
            ++badM;
            if (firstErrors.size() < 5)
                firstErrors.push_back(std::wstring(fd.cFileName) + L" :: " + r.error);
            continue;
        }
        ++okM;
        regValues += m.RegistryValueCount();
        for (const auto& w : r.warnings) warnHistogram[w]++;

        WcmConfigurationSchema schema;
        auto sr = schema.Parse(m.configurationSchema.xsdText);
        if (sr.ok && schema.Size() > 0) {
            ++okSchema;
            xsdItems += schema.Size();
            auto v = ToRegistryOperations(schema);
            ops += v.size();
            for (const auto& o : v) {
                if (_wcsicmp(o.root.c_str(), L"HKEY_CLASSES_ROOT") == 0) ++hkcr;
                else if (_wcsicmp(o.root.c_str(), L"HKEY_LOCAL_MACHINE") == 0) ++hklm;
                else if (_wcsicmp(o.root.c_str(), L"HKEY_CURRENT_USER") == 0) ++hkcu;
                if (_wcsicmp(o.scope.c_str(), L"allUsers") == 0) ++allUsers;
                else if (_wcsicmp(o.scope.c_str(), L"perUser") == 0) ++perUser;
            }
        }
    } while (FindNextFileW(h, &fd));
    FindClose(h);

    OutLn("manifests        : " + std::to_string(total));
    OutLn("parsed ok        : " + std::to_string(okM));
    OutLn("parse failed     : " + std::to_string(badM));
    OutLn("with wcm schema  : " + std::to_string(okSchema));
    OutLn("registryValues   : " + std::to_string(regValues));
    OutLn("xsd settings     : " + std::to_string(xsdItems));
    OutLn("derived ops      : " + std::to_string(ops));
    OutLn("  HKCR/HKLM/HKCU : " + std::to_string(hkcr) + " / " + std::to_string(hklm) + " / " + std::to_string(hkcu));
    OutLn("  allUsers/perUser: " + std::to_string(allUsers) + " / " + std::to_string(perUser));
    if (!firstErrors.empty()) {
        Err("--- first parse failures ---");
        for (const auto& e : firstErrors) Err("  " + ToUtf8(e));
    }
    if (!warnHistogram.empty()) {
        Err("--- unhandled elements (backlog) ---");
        int shown = 0;
        for (const auto& kv : warnHistogram) {
            if (shown++ >= 15) break;
            Err("  " + ToUtf8(kv.first) + "  x" + std::to_string(kv.second));
        }
    }
    return badM == 0 ? 0 : 1;
#else
    (void)dir; (void)maxFiles; return 2;
#endif
}

// ── 用真实文件名反验组件名 ───────────────────────────────────────────────
//
// 清单文件的命名规则就是 `<组件名>_<hash>.manifest`，而组件名由 identity 决定：
//     <arch>_<name>_<token>_<version>_<culture>_<hash>
// ⇒ 所以每个真实清单文件都自带一个"期望值"，可以大规模反验 ComponentNamePrefix。
//   这条验证很值：组件名会用作目录名，错一个字符就和镜像里已有的东西对不上，
//   而且这类不匹配在别处极难发现（构建期看起来一切正常）。
static int CmdVerifyNames(const std::wstring& dir, int maxFiles)
{
#ifdef _WIN32
    WIN32_FIND_DATAW fd;
    std::wstring pattern = dir + L"\\*.manifest";
    HANDLE h = FindFirstFileW(pattern.c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) { Err("no .manifest found"); return 2; }

    int total = 0, match = 0, mismatch = 0, unreadable = 0;
    std::vector<std::wstring> samples;

    do {
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
        if (maxFiles > 0 && total >= maxFiles) break;
        ++total;

        std::wstring text, err;
        if (!ReadFileUtf8(dir + L"\\" + fd.cFileName, text, err)) { ++unreadable; continue; }

        ComponentManifest m;
        auto r = ParseComponentManifest(text, m);
        if (!r.ok) { ++unreadable; continue; }

        std::wstring fname = fd.cFileName;
        // 去掉 .manifest 后缀
        size_t dot = fname.rfind(L".manifest");
        if (dot != std::wstring::npos) fname = fname.substr(0, dot);

        std::wstring prefix = m.identity.ComponentNamePrefix();
        // 文件名应为 prefix + "_" + hash
        bool ok = fname.size() > prefix.size() + 1
               && _wcsnicmp(fname.c_str(), prefix.c_str(), prefix.size()) == 0
               && fname[prefix.size()] == L'_';

        if (ok) ++match;
        else {
            ++mismatch;
            if (samples.size() < 8) {
                std::wstring s = L"file  = " + fname + L"\n        derived = " + prefix;
                samples.push_back(s);
            }
        }
    } while (FindNextFileW(h, &fd));
    FindClose(h);

    OutLn("manifests   : " + std::to_string(total));
    OutLn("name match  : " + std::to_string(match));
    OutLn("mismatch    : " + std::to_string(mismatch));
    OutLn("unreadable  : " + std::to_string(unreadable));
    if (!samples.empty()) {
        Err("--- mismatches ---");
        for (const auto& s : samples) Err("  " + ToUtf8(s));
    }
    return mismatch == 0 ? 0 : 1;
#else
    (void)dir; (void)maxFiles; return 2;
#endif
}

// ── 批量导出注册表操作（JSONL，给 C# 侧流式消费）────────────────────────
//
// 为什么要有这个：镜像里 5664 份清单，每份都起一次进程太慢。
// 这里一次扫描整个目录（或读一份路径清单），把**每条操作打成一个 JSON 对象一行**（JSONL），
// 调用方可以边读边处理，也不必先把全部内容读进内存。
//
// 用法：
//   ocid_manifest export-ops <dir> [maxFiles]        目录里所有 *.manifest
//   ocid_manifest export-ops --list <fileOfPaths>    从文本文件读清单路径（一行一个）
static int CmdExportOps(const std::wstring& dir, const std::wstring& listFile, int maxFiles)
{
#ifdef _WIN32
    std::vector<std::wstring> files;

    if (!listFile.empty()) {
        std::wstring text, err;
        if (!ReadFileUtf8(listFile, text, err)) { Err(ToUtf8(err)); return 2; }
        size_t start = 0;
        while (start <= text.size()) {
            size_t nl = text.find(L'\n', start);
            std::wstring line = (nl == std::wstring::npos) ? text.substr(start)
                                                           : text.substr(start, nl - start);
            while (!line.empty() && (line.back() == L'\r' || line.back() == L' ')) line.pop_back();
            if (!line.empty()) files.push_back(line);
            if (nl == std::wstring::npos) break;
            start = nl + 1;
        }
    } else {
        WIN32_FIND_DATAW fd;
        std::wstring pattern = dir + L"\\*.manifest";
        HANDLE h = FindFirstFileW(pattern.c_str(), &fd);
        if (h == INVALID_HANDLE_VALUE) { Err("no .manifest found"); return 2; }
        do {
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
            if (maxFiles > 0 && (int)files.size() >= maxFiles) break;
            files.push_back(dir + L"\\" + fd.cFileName);
        } while (FindNextFileW(h, &fd));
        FindClose(h);
    }

    size_t totalOps = 0, okManifests = 0, badManifests = 0, noSchema = 0;
    size_t fromRegistryKeys = 0, fromWcm = 0;
    for (const auto& path : files) {
        std::wstring text, err;
        if (!ReadFileUtf8(path, text, err)) { ++badManifests; continue; }

        ComponentManifest m;
        auto r = ParseComponentManifest(text, m);
        if (!r.ok) { ++badManifests; continue; }
        ++okManifests;

        // ⚠ 两套机制都要收：`<registryKeys>`（5282 条量级）与 `<configurationSchema>`（533 条量级）。
        //   只做后者会静默丢掉一大片设置，而且日志看起来一切正常。
        if (m.configurationSchema.xsdText.empty() && m.registryKeys.empty()) { ++noSchema; continue; }

        std::wstring compName = m.identity.ComponentNamePrefix();
        auto batch = CollectManifestRegistryOperations(m);
        fromRegistryKeys += batch.fromRegistryKeys;
        fromWcm          += batch.fromWcmSchema;

        for (const auto& o : batch.operations) {
            // 一行一个对象，带上来源（组件名 + 机制 + 设置项名），排障时能直接定位到哪份清单的哪一条
            std::string line = "{\"component\":\"" + JsonEscape(compName)
                             + "\",\"identity\":\"" + JsonEscape(m.identity.ToString())
                             + "\",\"mechanism\":\"" + JsonEscape(o.mechanism)
                             + "\",\"root\":\"" + JsonEscape(o.root)
                             + "\",\"subKey\":\"" + JsonEscape(o.subKey)
                             + "\",\"valueName\":\"" + JsonEscape(o.valueName)
                             + "\",\"valueType\":\"" + JsonEscape(o.valueType)
                             + "\",\"data\":\"" + JsonEscape(o.data)
                             + "\",\"scope\":\"" + JsonEscape(o.scope)
                             + "\",\"owner\":" + (o.owner ? "true" : "false")
                             + ",\"operationHint\":\"" + JsonEscape(o.operationHint)
                             + "\",\"sourceItem\":\"" + JsonEscape(o.sourceItem) + "\"}";
            OutLn(line);
            ++totalOps;
        }
    }

    Err("manifests=" + std::to_string(okManifests)
      + " failed=" + std::to_string(badManifests)
      + " noSchema=" + std::to_string(noSchema)
      + " ops=" + std::to_string(totalOps)
      + " (registryKeys=" + std::to_string(fromRegistryKeys)
      + " wcm=" + std::to_string(fromWcm) + ")");
    return badManifests == 0 ? 0 : 1;
#else
    (void)dir; (void)listFile; (void)maxFiles; return 2;
#endif
}

static int CmdDumpXsd(const std::wstring& manifest, const std::wstring& outPath)
{
#ifdef _WIN32
    std::wstring text, err;
    if (!ReadFileUtf8(manifest, text, err)) { Err(ToUtf8(err)); return 2; }
    ComponentManifest m;
    auto r = ParseComponentManifest(text, m);
    if (!r.ok) { Err("manifest parse failed: " + ToUtf8(r.error)); return 3; }

    FILE* f = _wfopen(outPath.c_str(), L"wb");
    if (!f) { Err("cannot write output"); return 2; }
    std::string utf8 = ToUtf8(m.configurationSchema.xsdText);
    fwrite(utf8.data(), 1, utf8.size(), f);
    fclose(f);
    OutLn("wrote " + std::to_string(utf8.size()) + " bytes to " + ToUtf8(outPath));
    return 0;
#else
    (void)manifest; (void)outPath; return 2;
#endif
}

// ─────────────────────────────────────────────────────────────────────────

static void Usage()
{
    OutLn("OCID servicing console");
    OutLn("");
    OutLn("  ocid_manifest info <manifest>");
    OutLn("  ocid_manifest ops  <manifest> [--json]");
    OutLn("  ocid_manifest scan <dir> [maxFiles]");
    OutLn("  ocid_manifest dump-xsd <manifest> <outFile>");
    OutLn("  ocid_manifest verify-names <dir> [maxFiles]   (check component names against real file names)");
    OutLn("");
    OutLn("Data goes to stdout, diagnostics to stderr.");
}

int main(int argc, char** argv)
{
    SetupConsole();
    if (argc < 2) { Usage(); return 1; }

    std::wstring cmd = FromUtf8OrAnsi(argv[1]);

    if (cmd == L"info" && argc >= 3)
        return CmdInfo(FromUtf8OrAnsi(argv[2]));

    if (cmd == L"ops" && argc >= 3) {
        bool json = false;
        for (int i = 3; i < argc; ++i)
            if (std::string(argv[i]) == "--json") json = true;
        return CmdOps(FromUtf8OrAnsi(argv[2]), json);
    }

    if (cmd == L"scan" && argc >= 3) {
        int maxFiles = (argc >= 4) ? atoi(argv[3]) : 0;
        return CmdScan(FromUtf8OrAnsi(argv[2]), maxFiles);
    }

    if (cmd == L"export-ops") {
        std::wstring dir, listFile;
        int mx = 0;
        for (int i = 2; i < argc; ++i) {
            std::string a = argv[i];
            if (a == "--list" && i + 1 < argc) { listFile = FromUtf8OrAnsi(argv[++i]); }
            else if (a.rfind("--", 0) == 0) { /* 其它开关忽略 */ }
            else if (dir.empty()) { dir = FromUtf8OrAnsi(argv[i]); }
            else { mx = atoi(argv[i]); }
        }
        return CmdExportOps(dir, listFile, mx);
    }

    if (cmd == L"verify-names" && argc >= 3) {
        int mx = (argc >= 4) ? atoi(argv[3]) : 0;
        return CmdVerifyNames(FromUtf8OrAnsi(argv[2]), mx);
    }

    if (cmd == L"dump-xsd" && argc >= 4)
        return CmdDumpXsd(FromUtf8OrAnsi(argv[2]), FromUtf8OrAnsi(argv[3]));

    Usage();
    return 1;
}
