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

#include "ocid/ConsoleUtf8.h"
#include "ocid/CbsStore.h"
#include "ocid/DriverPackage.h"
#include "ocid/DriverInf.h"
#include "ocid/FilePlacement.h"
#include "ocid/MacroExpander.h"
#include "ocid/Manifest.h"
#include "ocid/RegistryValue.h"
#include "ocid/WcmSchema.h"

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <iterator>
#include <map>
#include <set>
#include <string>
#include <vector>

using namespace ocid;

// ── 窄/宽字符串与控制台输出 ─────────────────────────────────────────────
//
// UTF-8 控制台设置与宽→UTF-8 转换都在 `ocid/ConsoleUtf8.h`（测试也用同一份，
// 见那里的说明：CRT 的 `printf("%ls")` 在 C locale 下会把中文变成 `?` 或直接吞掉）。

namespace {

    void SetupConsole() { SetupConsoleUtf8(); }

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
    // 编码统计：这是"数据真正能不能写进 hive"的判据，必须报出来。
    //   encodeFailed > 0 ⇒ 有值我们解不了，**不能当成功**（宁可显式失败，也不写错字节）
    //   macroOps     > 0 ⇒ 有 $(…) 宏未展开，写下去一定是错的，需要上层决策
    size_t encodedOps = 0, encodeFailed = 0, macroOps = 0;
    size_t keyOnlyOps = 0, sdOps = 0;
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
            // 值数据在这里就**编码成最终字节**，不把"怎么解释 valueType"这件事留给下游。
            // 理由：这是服务栈语义（见 RegistryValue.h 的三重证据），只允许有一份实现；
            // C# 侧只负责把 typeCode + bytes 原样交给 RegSetValueExW。
            // ★ `keyOnly`（只有键、没有值，实测占带 ACL 的键的 18.3%）**不能**送去编码：
            //   那会把"建一个空键"变成"把默认值写成空串"，在镜像里凭空多出一条
            //   `(Default)=""`，而且看起来像成功。
            RegValueBlob blob{};
            if (o.keyOnly) {
                blob.ok = true;
                blob.typeCode = 0;
            } else {
                blob = EncodeRegValue(o.valueType, o.data);
            }

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
                             + "\",\"sourceItem\":\"" + JsonEscape(o.sourceItem)
                             // 键级属性：ACL 属于键、不属于值，所以随每条 op 带出去。
                             // `keySddl` 为空 = 这个键没引用安全描述符，落盘层不要动 ACL。
                             + ",\"keyOnly\":" + (o.keyOnly ? "true" : "false")
                             + ",\"keySddl\":\"" + JsonEscape(o.keySddl) + "\""
                             + ",\"keySdName\":\"" + JsonEscape(o.keySdName) + "\""
                             + ",\"keySdBuildFilter\":\"" + JsonEscape(o.keySdBuildFilter) + "\""
                             + ",\"perUserVirtualization\":\"" + JsonEscape(o.perUserVirtualization) + "\""
                             + ",\"encoded\":" + (blob.ok ? "true" : "false")
                             + ",\"typeCode\":" + std::to_string(blob.ok ? blob.typeCode : 0u)
                             + ",\"dataHex\":\"" + ToUtf8(BytesToHex(blob.bytes)) + "\""
                             + ",\"hasMacro\":" + (blob.hasUnresolvedMacro ? "true" : "false")
                             + ",\"encodeError\":\"" + JsonEscape(blob.error) + "\"}";
            OutLn(line);
            ++totalOps;
            if (blob.ok) ++encodedOps; else ++encodeFailed;
            if (blob.hasUnresolvedMacro) ++macroOps;
            if (o.keyOnly) ++keyOnlyOps;
            if (!o.keySddl.empty()) ++sdOps;
        }
    }

    Err("manifests=" + std::to_string(okManifests)
      + " failed=" + std::to_string(badManifests)
      + " noSchema=" + std::to_string(noSchema)
      + " ops=" + std::to_string(totalOps)
      + " (registryKeys=" + std::to_string(fromRegistryKeys)
      + " wcm=" + std::to_string(fromWcm) + ")"
      + " encoded=" + std::to_string(encodedOps)
      + " encodeFailed=" + std::to_string(encodeFailed)
      + " macro=" + std::to_string(macroOps)
      + " keyOnly=" + std::to_string(keyOnlyOps)
      + " withSd=" + std::to_string(sdOps));
    // 编码失败就是失败：不能返回 0 让上层以为这一批都能写。
    return (badManifests == 0 && encodeFailed == 0) ? 0 : 1;
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

// ── expand / macros ──────────────────────────────────────────────────────
//
// 清单里的 `$(runtime.*)` 展开。基础路径从 Windows 目录推（离线服务时形如
// `P:\Windows`），因为离线镜像的 SYSTEM hive 里**没有** SystemRoot / ProgramFiles /
// ProgramData（实测只有 windir=%SystemRoot% 这种循环项，且 CurrentControlSet 离线不存在）。
// 详见 `ocid/MacroExpander.h`。

static int CmdExpand(const std::wstring& windowsDir, const std::wstring& text, bool showPaths)
{
    const RuntimePaths p = RuntimePaths::FromWindowsDir(windowsDir);

    if (showPaths) {
        Err("windowsDir        = " + ToUtf8(p.windows));
        Err("bootDrive         = " + ToUtf8(p.bootDrive));
        Err("programFiles      = " + ToUtf8(p.programFiles));
        Err("programFilesX86   = " + ToUtf8(p.programFilesX86));
        Err("commonFiles       = " + ToUtf8(p.commonFiles));
        Err("programData       = " + ToUtf8(p.programData));
        Err("users             = " + ToUtf8(p.users));
        Err("public            = " + ToUtf8(p.publicDir));
        Err("startMenu         = " + ToUtf8(p.startMenu));
    }

    const MacroExpansion r = ExpandMacros(text, p);
    OutLn(ToUtf8(r.text));

    if (!r.unresolved.empty()) {
        std::string s;
        for (size_t i = 0; i < r.unresolved.size(); ++i) {
            if (i) s += ";";
            s += ToUtf8(r.unresolved[i]);
        }
        Err("未展开的宏（非路径/未知，原样保留）: " + s);
    }
    return 0;
}

static int CmdMacros()
{
    size_t n = 0;
    const wchar_t* const* names = KnownRuntimeMacroNames(&n);
    for (size_t i = 0; i < n; ++i)
        OutLn(ToUtf8(std::wstring(names[i])));
    return 0;
}

// ─────────────────────────────────────────────────────────────────────────

// ── place-ops ────────────────────────────────────────────────────────────
//
// `ocid_manifest place-ops --windows <windowsDir> --list <fileOfPaths> [--packages <root>] [--image-arch <arch>]`
// `ocid_manifest place-ops --windows <windowsDir> <dir> [maxFiles]`
//
// 清单 → **CSI 文件落位计划**（JSONL，给 C# 侧 `Services/CsiFileService.cs` 消费）。
//
// 组件目录名取**清单文件名**去掉 `.manifest`，包展开根取清单所在目录（`--packages`
// 可显式覆盖）—— 实测镜像里平铺的 `packages\<组件目录>.manifest` 与
// `Windows\WinSxS\Manifests\<组件目录>.manifest` 都符合这条规则。
//
// `--image-arch` 不给时**自动探测**（读 `<windowsDir>\System32\ntoskrnl.exe` 的 PE Machine）；
// 探测不出来就按"不红移"走，并在 stderr 说出来。给 `none` 可显式关掉红移。
//
// 机制与证据见 `ocid/FilePlacement.h` 顶部（落点是硬链接、正本在 WinSxS、
// `writeableType` 决定拷贝还是链接、非原生架构的 System32 落点红移到 SysWOW64）。
static int CmdPlaceOps(const std::wstring& dir, const std::wstring& listFile,
                       const std::wstring& windowsDir, const std::wstring& packageRootOverride,
                       const std::wstring& imageArchArg,
                       int maxFiles)
{
#ifdef _WIN32
    if (windowsDir.empty()) { Err("--windows <windowsDir> is required (macro base)"); return 2; }

    // 架构红移的判定基准。`none` = 显式不红移；空 = 自动探测。
    std::wstring imageArch = imageArchArg;
    bool archDetected = false;
    if (_wcsicmp(imageArch.c_str(), L"none") == 0) {
        imageArch.clear();
        Err("imageArch=none（显式关闭架构红移）");
    } else if (imageArch.empty()) {
        imageArch = DetectImageArch(windowsDir);
        archDetected = !imageArch.empty();
        if (archDetected) {
            Err("imageArch=" + ToUtf8(imageArch) + "（自动探测 <windowsDir>\\System32\\ntoskrnl.exe 的 PE Machine）");
        } else {
            Err("imageArch=(探测失败：读不到 <windowsDir>\\System32\\ntoskrnl.exe 的 PE 头)"
                " —— 按不红移处理；可用 --image-arch <arch> 显式指定");
        }
    } else {
        Err("imageArch=" + ToUtf8(imageArch) + "（--image-arch 指定）");
    }

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
    if (files.empty()) { Err("no .manifest found"); return 2; }

    const RuntimePaths paths = RuntimePaths::FromWindowsDir(windowsDir);

    size_t okM = 0, badM = 0, nFiles = 0, nDirs = 0, noDest = 0;
    size_t unresolved = 0, sddlBad = 0, missingPayload = 0, execFiles = 0;
    size_t writeable = 0, redirected = 0;

    for (const auto& path : files) {
        std::wstring text, err;
        if (!ReadFileUtf8(path, text, err)) { ++badM; continue; }

        ComponentManifest m;
        auto r = ParseComponentManifest(text, m);
        if (!r.ok) { ++badM; continue; }

        // 组件目录名 = **清单文件名** 去掉 `.manifest`；包展开根 = 清单所在目录。
        //
        // 为什么不能按目录名推：实测 8061 ARM 镜像里 `packages\` 下有 **5664 份平铺**的
        // `<组件目录>.manifest`（其中 4359 份有同名载荷目录 `packages\<组件目录>\`），
        // 另有 118 份在 `Windows\WinSxS\Manifests\<组件目录>.manifest`。
        // 两处都是"文件名 = 组件目录名"，按目录名推会把这 5664 份一律算成 "packages"
        // —— 载荷路径变成 `packages\packages\x.dll`，错得彻底又看不出来。
        // （唯一形如 `packages\<组件目录>\<别的名字>.manifest` 的 2 份是**载荷**
        //   `WindowsShell.Manifest` / `XPThemes.manifest`，本来就不该按组件清单收。
        //   `WinSxS\Manifests\` 里那些 `microsoft-windows-b..c-misc-tools-minwin` 缩写名
        //   也正好与 Identity 的缩写算法一致，反过来印证了这条规则。）
        const size_t p = path.find_last_of(L"\\/");
        if (p == std::wstring::npos) { ++badM; continue; }
        const std::wstring fileName = path.substr(p + 1);
        const std::wstring suffix = L".manifest";
        if (fileName.size() <= suffix.size() ||
            _wcsicmp(fileName.c_str() + fileName.size() - suffix.size(), suffix.c_str()) != 0) {
            ++badM;
            continue;
        }
        const std::wstring compDir = fileName.substr(0, fileName.size() - suffix.size());
        std::wstring pkgRoot = path.substr(0, p);
        if (!packageRootOverride.empty()) pkgRoot = packageRootOverride;

        ++okM;
        const PlacementPlan plan = BuildPlacementPlan(m, compDir, pkgRoot, paths, imageArch);
        const std::wstring compName = m.identity.ComponentNamePrefix();

        for (const auto& f : plan.files) {
            const std::string line =
                "{\"component\":\"" + JsonEscape(compName)
                + "\",\"identity\":\"" + JsonEscape(plan.identity)
                + "\",\"componentDir\":\"" + JsonEscape(plan.componentDir)
                + "\",\"kind\":\"file\""
                + ",\"name\":\"" + JsonEscape(f.name)
                + "\",\"storeDir\":\"" + JsonEscape(plan.storeDir)
                + "\",\"storePath\":\"" + JsonEscape(f.storePath)
                + "\",\"destinationPath\":\"" + JsonEscape(f.destinationPath)
                + "\",\"payloadPath\":\"" + JsonEscape(f.payloadPath)
                + "\",\"sddl\":\"" + JsonEscape(f.sddl)
                + "\",\"sdName\":\"" + JsonEscape(f.sdName)
                + "\",\"writeableType\":\"" + JsonEscape(f.writeableType)
                + "\",\"writeable\":" + (f.writeable ? "true" : "false")
                + ",\"redirected\":" + (f.redirected ? "true" : "false")
                + ",\"unresolved\":\"" + JsonEscape(f.unresolved) + "\"}";
            OutLn(line);
            ++nFiles;
        }

        for (const auto& d : plan.directories) {
            const std::string line =
                "{\"component\":\"" + JsonEscape(compName)
                + "\",\"identity\":\"" + JsonEscape(plan.identity)
                + "\",\"componentDir\":\"" + JsonEscape(plan.componentDir)
                + "\",\"kind\":\"directory\""
                + ",\"path\":\"" + JsonEscape(d.path)
                + "\",\"attributes\":\"" + JsonEscape(d.attributes)
                + "\",\"owner\":" + (d.owner ? "true" : "false")
                + ",\"sddl\":\"" + JsonEscape(d.sddl)
                + "\",\"sdName\":\"" + JsonEscape(d.sdName)
                + "\",\"unresolved\":\"" + JsonEscape(d.unresolved) + "\"}";
            OutLn(line);
            ++nDirs;
        }

        noDest         += plan.filesWithoutDestination;
        unresolved     += plan.unresolvedCount;
        sddlBad        += plan.sddlUnresolved;
        missingPayload += plan.missingPayload;
        execFiles      += plan.executableFiles();
        writeable      += plan.writeableFiles;
        redirected     += plan.redirectedFiles;

        for (const auto& n : plan.notes) Err("note: " + ToUtf8(n));
    }

    Err("manifests=" + std::to_string(okM)
      + " failed=" + std::to_string(badM)
      + " files=" + std::to_string(nFiles)
      + " dirs=" + std::to_string(nDirs)
      + " executableFiles=" + std::to_string(execFiles)
      + " noDestination=" + std::to_string(noDest)
      + " missingPayload=" + std::to_string(missingPayload)
      + " writeable=" + std::to_string(writeable)
      + " redirected=" + std::to_string(redirected)
      + " imageArch=" + (imageArch.empty() ? std::string("(none)") : ToUtf8(imageArch))
      + (archDetected ? "(detected)" : "")
      + " unresolvedMacro=" + std::to_string(unresolved)
      + " unresolvedSddl=" + std::to_string(sddlBad));
    // 未展开的宏与查不到的 SD 引用都**不能**当成功：写下去一定是错的（或静默丢 ACL）。
    return (badM == 0 && unresolved == 0 && sddlBad == 0) ? 0 : 1;
#else
    (void)dir; (void)listFile; (void)windowsDir; (void)packageRootOverride; (void)imageArchArg;
    (void)maxFiles;
    return 2;
#endif
}

// ── dmi-ops：驱动包（type="driverUpdate"）的导入计划 ─────────────────────────
//
// 与 place-ops 的区别：驱动组件的 <file> 没有 destinationPath，落点由 importPath 决定
// （见 include/ocid/DriverPackage.h 的真机直方图）。所以这里不落 System32/WinSxS，
// 而是给出 FileRepository 的目录名与"哪些文件进库"。
//
// 载荷：包展开目录 `<pkgRoot>\<组件目录>\<sourceName>`。真机 8061 的包库是**平铺**的
// （5664 份 manifest + 350 cat/mum，**没有一个 .inf/.sys**），所以这里会如实报
// payloadMissing —— 离线导入的 INF 源必须另有出处，这是本轮明确留出的问题。
static bool ArchIdFromName(const std::wstring& name, unsigned int& id)
{
    struct { const wchar_t* n; unsigned int v; } kMap[] = {
        { L"x86", 0 }, { L"arm", 5 }, { L"ia64", 6 }, { L"amd64", 9 }, { L"arm64", 12 },
    };
    for (const auto& e : kMap) {
        if (_wcsicmp(name.c_str(), e.n) == 0) { id = e.v; return true; }
    }
    return false;
}

// INF 是**字节**（StoreId 按字节算），不能用按行/按 UTF-8 处理的读法。
static bool ReadRawBytes(const std::wstring& path, std::vector<std::uint8_t>& out, std::wstring& error)
{
    FILE* f = _wfopen(path.c_str(), L"rb");
    if (!f) { error = L"打不开：" + path; return false; }
    std::uint8_t buf[65536];
    size_t n;
    while ((n = fread(buf, 1, sizeof(buf), f)) > 0) out.insert(out.end(), buf, buf + n);
    const bool bad = ferror(f) != 0;
    fclose(f);
    if (bad) { error = L"读出错：" + path; return false; }
    return true;
}

static int CmdDmiOps(const std::wstring& dir,
                     const std::wstring& listFile,
                     const std::wstring& packageRootOverride,
                     int maxFiles)
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
    if (files.empty()) { Err("no .manifest found"); return 2; }

    size_t okM = 0, driverPkgs = 0, planned = 0, planFailed = 0;
    size_t payloadMissing = 0, notImported = 0, noArch = 0;

    for (const auto& path : files) {
        std::wstring text, err;
        if (!ReadFileUtf8(path, text, err)) continue;

        ComponentManifest m;
        auto r = ParseComponentManifest(text, m);
        if (!r.ok) continue;
        if (!IsDriverUpdateComponent(m.identity.Type(), m.identity.ScopeString())) continue;
        ++okM;
        ++driverPkgs;

        const size_t p = path.find_last_of(L"\\/");
        if (p == std::wstring::npos) continue;
        const std::wstring fileName = path.substr(p + 1);
        const std::wstring compDir  = fileName.substr(0, fileName.size() - 9);  // 去掉 ".manifest"
        std::wstring pkgRoot = path.substr(0, p);
        if (!packageRootOverride.empty()) pkgRoot = packageRootOverride;

        unsigned int archId = 0;
        if (!ArchIdFromName(m.identity.Architecture(), archId)) {
            ++noArch;
            Err("note: " + ToUtf8(std::wstring(L"架构串不认识，跳过：") + m.identity.Architecture() + L"（" + compDir + L"）"));
            continue;
        }

        std::vector<DriverImportFileSpec> specs;
        specs.reserve(m.files.size());
        for (const auto& f : m.files) specs.push_back({ f.name, f.sourceName, f.importPath });

        // INF 的载荷（若包展开目录里真有）
        std::wstring infPath;
        std::vector<std::uint8_t> infBytes;
        for (const auto& s : specs) {
            if (ClassifyDriverImportFile(s.importPath) != DriverImportFileKind::Inf) continue;
            const std::wstring src = s.sourceName.empty() ? s.name : s.sourceName;
            infPath = pkgRoot + L"\\" + compDir + L"\\" + src;
            std::wstring rerr;
            if (ReadRawBytes(infPath, infBytes, rerr)) {
                // ok：字节已到手
            } else {
                ++payloadMissing;
                Err("note: 包展开目录里没有 INF 载荷（算不出 StoreId）：" + ToUtf8(infPath));
            }
            break;
        }

        const DriverImportPlan plan = BuildDriverImportPlan(specs, infPath, infBytes, archId);
        notImported += (size_t)plan.notImported;
        for (const auto& n : plan.notes) Err("note: " + ToUtf8(n));

        std::string line = "{\"componentDir\":\"" + JsonEscape(compDir)
                         + "\",\"identity\":\"" + JsonEscape(m.identity.ToString())
                         + "\",\"arch\":\"" + JsonEscape(m.identity.Architecture())
                         + "\",\"infName\":\"" + JsonEscape(plan.infName)
                         + "\",\"storeId\":\"" + JsonEscape(plan.storeId)
                         + "\",\"ok\":" + (plan.ok ? "true" : "false")
                         + ",\"error\":\"" + JsonEscape(plan.error)
                         + "\",\"fileCount\":" + std::to_string(plan.files.size())
                         + ",\"notImported\":" + std::to_string(plan.notImported);
        for (size_t i = 0; i < plan.files.size(); ++i) {
            line += ",\"file" + std::to_string(i) + "\":\""
                  + JsonEscape(plan.files[i].name.empty() ? plan.files[i].sourceName
                                                          : plan.files[i].name)
                  + "\"";
        }
        line += "}";
        OutLn(line);
        if (plan.ok) ++planned; else ++planFailed;
    }

    Err("driverManifests=" + std::to_string(driverPkgs)
      + " planned=" + std::to_string(planned)
      + " planFailed=" + std::to_string(planFailed)
      + " payloadMissing=" + std::to_string(payloadMissing)
      + " notImportedFiles=" + std::to_string(notImported)
      + " archUnknown=" + std::to_string(noArch)
      + " manifestsSeen=" + std::to_string(files.size()));
    // 算不出 StoreId（没有 INF 载荷）不算成功：那是"没做"，不是"做对了"。
    return (planFailed == 0 && payloadMissing == 0) ? 0 : 1;
#else
    (void)dir; (void)listFile; (void)packageRootOverride; (void)maxFiles;
    return 2;
#endif
}

// ── dmi-ledger：驱动库记录算成 JSONL（C# 侧用 OfflineRegistryService 落盘） ──
//
// 输入是一个**已存在**的 FileRepository 目录。目录名是权威的 StoreId；这里同时按
// INF 字节重算一遍并比对 —— 算法漂移必须当场暴露，不能让目录名和记录悄悄对不上。
//
// 输出（每行一个驱动包）：
//   storeDir / infName / arch / storeIdComputed / storeIdMatches / ok / error / notes / keys
//   keys[].path 是 DriverDatabase 下的相对路径；values[] 的 type 取 sz|multi|dword|binary。
static std::string HexOfBytes(const std::vector<std::uint8_t>& bytes)
{
    static const char* digits = "0123456789abcdef";
    std::string out;
    out.reserve(bytes.size() * 2);
    for (const std::uint8_t b : bytes) {
        out.push_back(digits[(b >> 4) & 0xF]);
        out.push_back(digits[b & 0xF]);
    }
    return out;
}

static std::string LedgerValueJson(const LedgerValue& value)
{
    std::string json = "{\"name\":\"" + JsonEscape(value.name) + "\",\"type\":\"";
    switch (value.type) {
    case LedgerValueType::String:
        json += "sz\",\"text\":\"" + JsonEscape(value.text) + "\"";
        break;
    case LedgerValueType::MultiString: {
        json += "multi\",\"strings\":[";
        bool first = true;
        size_t start = 0;
        while (true) {
            const size_t sep = value.text.find(L'\0', start);
            const std::wstring item = (sep == std::wstring::npos) ? value.text.substr(start)
                                                                 : value.text.substr(start, sep - start);
            if (!first) json += ",";
            json += "\"" + JsonEscape(item) + "\"";
            first = false;
            if (sep == std::wstring::npos) break;
            start = sep + 1;
        }
        json += "]";
        break;
    }
    case LedgerValueType::DWord:
        json += "dword\",\"dword\":" + std::to_string(value.dword);
        break;
    case LedgerValueType::Binary:
        json += "binary\",\"hex\":\"" + HexOfBytes(value.binary) + "\"";
        break;
    }
    return json + "}";
}

static const wchar_t* ArchNameFromId(unsigned int id)
{
    switch (id) {
    case 0:  return L"x86";
    case 5:  return L"arm";
    case 6:  return L"ia64";
    case 9:  return L"amd64";
    case 12: return L"arm64";
    default: return L"";
    }
}

// 一个驱动包目录里应当**恰好**一个 INF。多于一个就不猜（那是清单/展开出了问题）。
static bool FindSingleInf(const std::wstring& dir, std::wstring& infPath, std::wstring& infName,
                          size_t& infCount)
{
    infCount = 0;
    WIN32_FIND_DATAW fd;
    const std::wstring pattern = dir + L"\\*";
    HANDLE h = FindFirstFileW(pattern.c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return false;
    do {
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
        std::wstring name = fd.cFileName;
        if (name.size() < 4) continue;
        std::wstring ext = name.substr(name.size() - 4);
        for (wchar_t& ch : ext) ch = (wchar_t)towlower(ch);
        if (ext != L".inf") continue;
        ++infCount;
        infName = name;
        infPath = dir + L"\\" + name;
    } while (FindNextFileW(h, &fd));
    FindClose(h);
    return infCount == 1;
}

static int CmdDmiLedger(const std::wstring& repoRoot,
                        const std::wstring& listFile,
                        const std::wstring& archName,
                        bool inbox,
                        int maxFiles)
{
#ifdef _WIN32
    std::vector<std::wstring> dirs;
    if (!listFile.empty()) {
        std::wstring text, err;
        if (!ReadFileUtf8(listFile, text, err)) { Err(ToUtf8(err)); return 2; }
        size_t start = 0;
        while (start <= text.size()) {
            size_t nl = text.find(L'\n', start);
            std::wstring line = (nl == std::wstring::npos) ? text.substr(start)
                                                           : text.substr(start, nl - start);
            while (!line.empty() && (line.back() == L'\r' || line.back() == L' ')) line.pop_back();
            if (!line.empty()) dirs.push_back(line);
            if (nl == std::wstring::npos) break;
            start = nl + 1;
        }
    } else {
        WIN32_FIND_DATAW fd;
        const std::wstring pattern = repoRoot + L"\\*";
        HANDLE h = FindFirstFileW(pattern.c_str(), &fd);
        if (h == INVALID_HANDLE_VALUE) { Err("no FileRepository directory found"); return 2; }
        do {
            if ((fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0) continue;
            if (fd.cFileName[0] == L'.') continue;
            if (maxFiles > 0 && (int)dirs.size() >= maxFiles) break;
            dirs.push_back(repoRoot + L"\\" + fd.cFileName);
        } while (FindNextFileW(h, &fd));
        FindClose(h);
    }
    if (dirs.empty()) { Err("no FileRepository directory found"); return 2; }

    unsigned int forcedArch = 0;
    const bool haveForcedArch = !archName.empty();
    if (haveForcedArch && !ArchIdFromName(archName, forcedArch)) {
        Err("unknown --arch: " + ToUtf8(archName));
        return 2;
    }

    size_t okCount = 0, failed = 0, noInf = 0, multiInf = 0, mismatch = 0, noArch = 0;

    for (const std::wstring& dir : dirs) {
        const size_t slash = dir.find_last_of(L"\\/");
        const std::wstring storeDir = (slash == std::wstring::npos) ? dir : dir.substr(slash + 1);

        std::wstring infPath, infName;
        size_t infCount = 0;
        if (!FindSingleInf(dir, infPath, infName, infCount)) {
            if (infCount == 0) { ++noInf; Err("note: 目录里没有 INF：" + ToUtf8(dir)); }
            else { ++multiInf; Err("note: 目录里有 " + std::to_string(infCount) + " 个 INF，不猜：" + ToUtf8(dir)); }
            continue;
        }

        std::vector<std::uint8_t> infBytes;
        std::wstring readErr;
        if (!ReadRawBytes(infPath, infBytes, readErr)) { ++failed; Err(ToUtf8(readErr)); continue; }

        unsigned int archId = 0;
        if (haveForcedArch) {
            archId = forcedArch;
        } else {
            // 目录名形如 `<INF 名>_<架构>_<16 hex>`；架构取中段。
            const size_t last = storeDir.find_last_of(L'_');
            const size_t prev = (last == std::wstring::npos || last == 0)
                                    ? std::wstring::npos
                                    : storeDir.find_last_of(L'_', last - 1);
            if (last == std::wstring::npos || prev == std::wstring::npos) {
                ++noArch;
                Err("note: 目录名里看不出架构：" + ToUtf8(storeDir));
                continue;
            }
            const std::wstring mid = storeDir.substr(prev + 1, last - prev - 1);
            if (!ArchIdFromName(mid, archId)) {
                ++noArch;
                Err("note: 架构串不认识（" + ToUtf8(mid) + "）：" + ToUtf8(storeDir));
                continue;
            }
        }

        std::wstring computed, storeErr;
        if (!BuildStoreId(infPath, infBytes, archId, computed, storeErr)) {
            ++failed;
            Err("note: " + ToUtf8(storeErr));
            continue;
        }
        const bool matches = (computed == storeDir);
        if (!matches) {
            ++mismatch;
            Err("note: 目录名与重算不一致：" + ToUtf8(storeDir) + " != " + ToUtf8(computed));
        }

        InfDocument inf;
        std::wstring parseErr;
        if (!ParseInf(infBytes, inf, parseErr)) {
            ++failed;
            Err("note: INF 解析失败（" + ToUtf8(parseErr) + "）：" + ToUtf8(infPath));
            continue;
        }

        DriverLedgerRequest request;
        request.inf = &inf;
        request.storeId = storeDir;
        request.infFileName = infName;
        request.architecture = archId;
        if (inbox) {
            request.importFlags = kImportFlagInbox | kImportFlagUseOriginalInfName;
            request.signerName = L"Microsoft Windows";
            request.signerScore = 0x0D000003u;
        }
        const DriverLedgerPlan plan = BuildDriverLedgerPlan(request);
        for (const auto& note : plan.notes) Err("note: " + ToUtf8(storeDir) + ": " + ToUtf8(note));

        std::string line = "{\"storeDir\":\"" + JsonEscape(storeDir)
                         + "\",\"infName\":\"" + JsonEscape(infName)
                         + "\",\"arch\":\"" + JsonEscape(ArchNameFromId(archId))
                         + "\",\"storeIdComputed\":\"" + JsonEscape(computed)
                         + "\",\"storeIdMatches\":" + (matches ? "true" : "false")
                         + ",\"ok\":" + (plan.ok ? "true" : "false")
                         + ",\"error\":\"" + JsonEscape(plan.error) + "\",\"notes\":[";
        for (size_t i = 0; i < plan.notes.size(); ++i) {
            if (i > 0) line += ",";
            line += "\"" + JsonEscape(plan.notes[i]) + "\"";
        }
        line += "],\"keys\":[";
        for (size_t i = 0; i < plan.keys.size(); ++i) {
            if (i > 0) line += ",";
            line += "{\"path\":\"" + JsonEscape(plan.keys[i].relativePath) + "\",\"values\":[";
            for (size_t j = 0; j < plan.keys[i].values.size(); ++j) {
                if (j > 0) line += ",";
                line += LedgerValueJson(plan.keys[i].values[j]);
            }
            line += "]}";
        }
        line += "]}";
        OutLn(line);
        if (plan.ok) ++okCount; else ++failed;
    }

    Err("storeDirs=" + std::to_string(dirs.size())
      + " ledgerOk=" + std::to_string(okCount)
      + " failed=" + std::to_string(failed)
      + " noInf=" + std::to_string(noInf)
      + " multiInf=" + std::to_string(multiInf)
      + " storeIdMismatch=" + std::to_string(mismatch)
      + " archUnknown=" + std::to_string(noArch));
    // 目录名对不上、没有 INF、多个 INF 都不算成功。
    return (failed == 0 && noInf == 0 && multiInf == 0 && mismatch == 0 && noArch == 0) ? 0 : 1;
#else
    (void)repoRoot; (void)listFile; (void)archName; (void)inbox; (void)maxFiles;
    return 2;
#endif
}

// ── CBS 台账清点（cbs-scan）──────────────────────────────────────────────
//
// 读一个**已挂载**的 COMPONENTS hive，把每条记录按 CbsStore 的语义解码，再原样
// 编码回去，和注册表里的原始字节逐值比对。这样"编解码对不对"就不是抽样而是
// 全量自证：真机 8061 x86 有 13,325 个组件、2,283 个部署、792 个目录。
//
// 用法：ocid_manifest cbs-scan HKLM\OCIDCOMP [--limit N]
#ifdef _WIN32
namespace {

struct CbsRegValue
{
    std::string name;
    DWORD type = 0;
    std::vector<std::uint8_t> data;
};

bool ParseHivePath(const std::wstring& spec, HKEY& root, std::wstring& sub, std::wstring& err)
{
    struct RootAlias { const wchar_t* prefix; HKEY key; };
    static const RootAlias aliases[] = {
        { L"HKLM\\", HKEY_LOCAL_MACHINE },
        { L"HKEY_LOCAL_MACHINE\\", HKEY_LOCAL_MACHINE },
        { L"HKCU\\", HKEY_CURRENT_USER },
        { L"HKEY_CURRENT_USER\\", HKEY_CURRENT_USER },
    };
    for (const RootAlias& alias : aliases)
    {
        const size_t n = wcslen(alias.prefix);
        if (spec.size() > n && _wcsnicmp(spec.c_str(), alias.prefix, n) == 0)
        {
            root = alias.key;
            sub = spec.substr(n);
            return true;
        }
    }
    err = L"挂载路径要写成 HKLM\\<名称> 或 HKCU\\<名称>";
    return false;
}

bool ReadAllValues(HKEY key, std::vector<CbsRegValue>& out, std::wstring& err)
{
    DWORD count = 0, maxName = 0, maxData = 0;
    if (RegQueryInfoKeyW(key, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr,
                         &count, &maxName, &maxData, nullptr, nullptr) != ERROR_SUCCESS)
    {
        err = L"RegQueryInfoKey 失败";
        return false;
    }
    std::wstring name(maxName + 2, L'\0');
    std::vector<std::uint8_t> data(maxData + 8);
    out.reserve(count);
    for (DWORD i = 0; i < count; ++i)
    {
        DWORD nameLen = static_cast<DWORD>(name.size());
        DWORD dataLen = static_cast<DWORD>(data.size());
        DWORD type = 0;
        const LSTATUS rc = RegEnumValueW(key, i, &name[0], &nameLen, nullptr, &type,
                                         data.data(), &dataLen);
        if (rc != ERROR_SUCCESS)
        {
            err = L"RegEnumValue 失败（错误码 " + std::to_wstring(rc) + L"）";
            return false;
        }
        CbsRegValue value;
        value.name = ToUtf8(std::wstring(name.c_str(), nameLen));
        value.type = type;
        value.data.assign(data.begin(), data.begin() + dataLen);
        out.push_back(std::move(value));
    }
    return true;
}

// 注册表值 -> 台账字节：REG_SZ 的宽文本按 ASCII 取字节，DWORD 是 4 字节小端。
std::vector<std::uint8_t> ToCbsBytes(const CbsRegValue& value)
{
    if (value.type == REG_SZ || value.type == REG_EXPAND_SZ)
    {
        std::vector<std::uint8_t> out;
        const size_t chars = value.data.size() / sizeof(wchar_t);
        const wchar_t* text = reinterpret_cast<const wchar_t*>(value.data.data());
        for (size_t i = 0; i < chars; ++i)
        {
            if (text[i] == 0) break;
            out.push_back(text[i] <= 0x7F ? static_cast<std::uint8_t>(text[i])
                                          : static_cast<std::uint8_t>('?'));
        }
        return out;
    }
    if (value.type == REG_DWORD)
        return std::vector<std::uint8_t>(value.data.begin(),
                                         value.data.begin() + (std::min)(size_t{4}, value.data.size()));
    return value.data;
}

std::vector<CbsValue> ToCbsValues(const std::vector<CbsRegValue>& values)
{
    std::vector<CbsValue> out;
    out.reserve(values.size());
    for (const CbsRegValue& value : values)
        out.push_back(CbsValue{value.name, ToCbsBytes(value)});
    return out;
}

// 值集合比较（顺序无关）：个数、每个名字、每个字节都必须一致。
bool SameValueSet(const std::vector<CbsValue>& want, const std::vector<CbsValue>& got,
                  std::string& why)
{
    std::map<std::string, std::vector<std::uint8_t>> a, b;
    for (const CbsValue& v : want) a[v.name] = v.data;
    for (const CbsValue& v : got)  b[v.name] = v.data;
    if (a.size() != b.size())
    {
        why = "值个数不同（原 " + std::to_string(a.size()) + "，编码后 " + std::to_string(b.size()) + "）";
        return false;
    }
    for (const auto& kv : a)
    {
        const auto it = b.find(kv.first);
        if (it == b.end()) { why = "编码后缺少值 " + kv.first; return false; }
        if (it->second != kv.second) { why = "值字节不同：" + kv.first; return false; }
    }
    return true;
}

bool EnumSubKeys(HKEY parent, std::vector<std::wstring>& names, std::wstring& err)
{
    DWORD count = 0, maxName = 0;
    if (RegQueryInfoKeyW(parent, nullptr, nullptr, nullptr, &count, &maxName, nullptr,
                         nullptr, nullptr, nullptr, nullptr, nullptr) != ERROR_SUCCESS)
    {
        err = L"RegQueryInfoKey 失败";
        return false;
    }
    std::wstring name(maxName + 2, L'\0');
    names.clear();
    names.reserve(count);
    for (DWORD i = 0; i < count; ++i)
    {
        DWORD len = static_cast<DWORD>(name.size());
        const LSTATUS rc = RegEnumKeyExW(parent, i, &name[0], &len, nullptr, nullptr, nullptr, nullptr);
        if (rc != ERROR_SUCCESS)
        {
            err = L"RegEnumKeyEx 失败（错误码 " + std::to_wstring(rc) + L"）";
            return false;
        }
        names.emplace_back(name.c_str(), len);
    }
    return true;
}

struct CbsScanCounters
{
    size_t keys = 0, decoded = 0, decodeFailed = 0, mismatch = 0, nameViolations = 0;
    size_t cf4 = 0, cf5 = 0, cf12 = 0, cfOther = 0;
    size_t marks = 0, files = 0, emptyFiles = 0, payloads = 0, catalogs = 0;
};

} // namespace
#endif

static int CmdCbsScan(const std::wstring& hiveSpec, int limit)
{
#ifdef _WIN32
    HKEY rootKey = nullptr;
    std::wstring sub, err;
    if (!ParseHivePath(hiveSpec, rootKey, sub, err)) { Err(ToUtf8(err)); return 2; }

    HKEY store = nullptr;
    const LSTATUS openRc = RegOpenKeyExW(rootKey, sub.c_str(), 0, KEY_READ, &store);
    if (openRc != ERROR_SUCCESS) { Err("打不开台账根（reg load 了吗？）"); return 2; }

    int failures = 0;

    // 1) 根值
    {
        std::vector<CbsRegValue> raw;
        if (!ReadAllValues(store, raw, err)) { Err(ToUtf8(err)); RegCloseKey(store); return 2; }
        const std::vector<CbsValue> values = ToCbsValues(raw);
        CbsStoreHeader header;
        std::string decodeErr;
        if (!CbsDecodeStoreHeader(values, header, decodeErr))
        {
            Err("根值解码失败：" + decodeErr);
            ++failures;
        }
        else
        {
            std::string why;
            if (!SameValueSet(values, CbsEncodeStoreHeader(header), why))
            {
                Err("根值再编码不一致：" + why);
                ++failures;
            }
            else
            {
                OutLn("root: format=" + ToUtf8(header.formatVersion) +
                      " arch=" + std::to_string(header.architecture) +
                      " exec=" + std::to_string(header.executionState) +
                      " values=" + std::to_string(values.size()));
            }
        }
    }

    // 2) 组件
    CbsScanCounters comp;
    {
        HKEY key = nullptr;
        if (RegOpenKeyExW(store, L"DerivedData\\Components", 0, KEY_READ, &key) != ERROR_SUCCESS)
        {
            Err("缺少 DerivedData\\Components");
            ++failures;
        }
        else
        {
            std::vector<std::wstring> names;
            if (!EnumSubKeys(key, names, err)) { Err(ToUtf8(err)); ++failures; }
            for (const std::wstring& name : names)
            {
                if (limit > 0 && static_cast<int>(comp.keys) >= limit) break;
                ++comp.keys;
                HKEY item = nullptr;
                if (RegOpenKeyExW(key, name.c_str(), 0, KEY_READ, &item) != ERROR_SUCCESS)
                {
                    ++comp.decodeFailed;
                    if (failures < 10) Err("打不开组件键 " + ToUtf8(name));
                    continue;
                }
                std::vector<CbsRegValue> raw;
                if (!ReadAllValues(item, raw, err)) { ++comp.decodeFailed; RegCloseKey(item); continue; }
                const std::vector<CbsValue> values = ToCbsValues(raw);
                CbsComponentRecord record;
                std::string decodeErr;
                if (!CbsDecodeComponentRecord(name, values, record, decodeErr))
                {
                    ++comp.decodeFailed;
                    if (failures < 10) Err("组件解码失败 " + ToUtf8(name) + "：" + decodeErr);
                    RegCloseKey(item);
                    continue;
                }
                ++comp.decoded;
                comp.marks += record.deploymentMarks.size();
                comp.files += record.files.size();
                for (const CbsFile& f : record.files) if (f.fileName.empty()) ++comp.emptyFiles;
                if (record.hasConfigFlags)
                {
                    if (record.configFlags == 4) ++comp.cf4;
                    else if (record.configFlags == 5) ++comp.cf5;
                    else if (record.configFlags == 12) ++comp.cf12;
                    else ++comp.cfOther;
                }
                if (!CbsIsComponentName(name)) ++comp.nameViolations;
                std::string why;
                if (!SameValueSet(values, CbsEncodeComponentRecord(record), why))
                {
                    ++comp.mismatch;
                    if (failures < 10) Err("组件再编码不一致 " + ToUtf8(name) + "：" + why);
                }
                RegCloseKey(item);
            }
            RegCloseKey(key);
        }
    }

    // 3) 部署
    CbsScanCounters dep;
    {
        HKEY key = nullptr;
        if (RegOpenKeyExW(store, L"CanonicalData\\Deployments", 0, KEY_READ, &key) != ERROR_SUCCESS)
        {
            Err("缺少 CanonicalData\\Deployments");
            ++failures;
        }
        else
        {
            std::vector<std::wstring> names;
            if (!EnumSubKeys(key, names, err)) { Err(ToUtf8(err)); ++failures; }
            for (const std::wstring& name : names)
            {
                if (limit > 0 && static_cast<int>(dep.keys) >= limit) break;
                ++dep.keys;
                HKEY item = nullptr;
                if (RegOpenKeyExW(key, name.c_str(), 0, KEY_READ, &item) != ERROR_SUCCESS)
                {
                    ++dep.decodeFailed;
                    continue;
                }
                std::vector<CbsRegValue> raw;
                if (!ReadAllValues(item, raw, err)) { ++dep.decodeFailed; RegCloseKey(item); continue; }
                const std::vector<CbsValue> values = ToCbsValues(raw);
                CbsDeploymentRecord record;
                std::string decodeErr;
                if (!CbsDecodeDeploymentRecord(name, values, record, decodeErr))
                {
                    ++dep.decodeFailed;
                    if (failures < 10) Err("部署解码失败 " + ToUtf8(name) + "：" + decodeErr);
                    RegCloseKey(item);
                    continue;
                }
                ++dep.decoded;
                dep.payloads += record.payloads.size();
                std::string why;
                if (!SameValueSet(values, CbsEncodeDeploymentRecord(record), why))
                {
                    ++dep.mismatch;
                    if (failures < 10) Err("部署再编码不一致 " + ToUtf8(name) + "：" + why);
                }
                RegCloseKey(item);
            }
            RegCloseKey(key);
        }
    }

    // 4) 目录（Catalog：键名是指纹，值是 c!<部署> 且恒为空）
    CbsScanCounters cat;
    {
        HKEY key = nullptr;
        if (RegOpenKeyExW(store, L"CanonicalData\\Catalogs", 0, KEY_READ, &key) == ERROR_SUCCESS)
        {
            std::vector<std::wstring> names;
            if (!EnumSubKeys(key, names, err)) { Err(ToUtf8(err)); ++failures; }
            for (const std::wstring& name : names)
            {
                if (limit > 0 && static_cast<int>(cat.keys) >= limit) break;
                ++cat.keys;
                HKEY item = nullptr;
                if (RegOpenKeyExW(key, name.c_str(), 0, KEY_READ, &item) != ERROR_SUCCESS) continue;
                std::vector<CbsRegValue> raw;
                if (ReadAllValues(item, raw, err)) { cat.decoded += raw.size(); }
                RegCloseKey(item);
            }
            RegCloseKey(key);
        }
    }

    OutLn("components: keys=" + std::to_string(comp.keys) +
          " decoded=" + std::to_string(comp.decoded) +
          " decodeFailed=" + std::to_string(comp.decodeFailed) +
          " mismatch=" + std::to_string(comp.mismatch) +
          " nameViolations=" + std::to_string(comp.nameViolations));
    OutLn("  marks=" + std::to_string(comp.marks) + " files=" + std::to_string(comp.files) +
          " emptyFiles=" + std::to_string(comp.emptyFiles));
    OutLn("  CF: 4=" + std::to_string(comp.cf4) + " 5=" + std::to_string(comp.cf5) +
          " 12=" + std::to_string(comp.cf12) + " other=" + std::to_string(comp.cfOther));
    OutLn("deployments: keys=" + std::to_string(dep.keys) +
          " decoded=" + std::to_string(dep.decoded) +
          " decodeFailed=" + std::to_string(dep.decodeFailed) +
          " mismatch=" + std::to_string(dep.mismatch) +
          " payloads=" + std::to_string(dep.payloads));
    OutLn("catalogs: keys=" + std::to_string(cat.keys) +
          " marks=" + std::to_string(cat.decoded));

    failures += static_cast<int>(comp.decodeFailed + comp.mismatch + dep.decodeFailed + dep.mismatch);
    RegCloseKey(store);
    OutLn(failures == 0 ? "result: OK（全量解码→再编码逐字节一致）"
                        : "result: FAILED（失败 " + std::to_string(failures) + "）");
    return failures == 0 ? 0 : 1;
#else
    (void)hiveSpec; (void)limit;
    return 2;
#endif
}

// ── 台账规范化转储 / 对拍（cbs-dump、cbs-diff）──────────────────────────
//
// cbs-dump 把整个（已挂载的）COMPONENTS 台账按“路径 + 值名 + 类型 + 原始字节”摊平成
// 文本：一行一个键（`K\t<相对路径>`）、一行一个值（`V\t<相对路径>\t<值名>\t<类型>\t<hex>`）。
// 行按字典序排序，同一份内容在任何机器上转储都逐字节相同 —— 这是“OCID 装出来的台账
// 与微软原版逐字节一致”的量化闸门：cbs-diff 逐个比两份转储。
#ifdef _WIN32
namespace {

void HexAppend(const std::vector<std::uint8_t>& data, std::string& out)
{
    static const char* digits = "0123456789abcdef";
    for (std::uint8_t b : data)
    {
        out.push_back(digits[b >> 4]);
        out.push_back(digits[b & 0x0F]);
    }
}

bool DumpKeyRecursive(HKEY key, const std::wstring& relPath, DWORD depth,
                      std::vector<std::string>& lines, std::wstring& err)
{
    if (depth > 32) { err = L"键层数超过 32，疑似环"; return false; }

    const std::string path = ToUtf8(relPath);
    lines.push_back("K\t" + path);

    std::vector<CbsRegValue> values;
    if (!ReadAllValues(key, values, err)) return false;
    for (const CbsRegValue& value : values)
    {
        std::string line = "V\t" + path + "\t" + value.name + "\t" +
                           std::to_string(value.type) + "\t";
        HexAppend(value.data, line);
        lines.push_back(std::move(line));
    }

    DWORD subCount = 0;
    if (RegQueryInfoKeyW(key, nullptr, nullptr, nullptr, &subCount, nullptr, nullptr,
                         nullptr, nullptr, nullptr, nullptr, nullptr) != ERROR_SUCCESS)
    {
        err = L"RegQueryInfoKey 失败（子键）";
        return false;
    }
    for (DWORD i = 0; i < subCount; ++i)
    {
        std::wstring name(512, L'\0');
        DWORD nameLen = static_cast<DWORD>(name.size());
        const LSTATUS rc = RegEnumKeyExW(key, i, &name[0], &nameLen, nullptr, nullptr,
                                         nullptr, nullptr);
        if (rc != ERROR_SUCCESS)
        {
            err = L"RegEnumKeyEx 失败（错误码 " + std::to_wstring(rc) + L"）";
            return false;
        }
        name.resize(nameLen);

        HKEY child = nullptr;
        if (RegOpenKeyExW(key, name.c_str(), 0, KEY_READ, &child) != ERROR_SUCCESS)
        {
            err = L"打不开子键：" + name;
            return false;
        }
        const std::wstring childPath = relPath.empty() ? name : (relPath + L"\\" + name);
        const bool ok = DumpKeyRecursive(child, childPath, depth + 1, lines, err);
        RegCloseKey(child);
        if (!ok) return false;
    }
    return true;
}

bool WriteLinesUtf8(const std::wstring& path, const std::vector<std::string>& lines,
                    std::wstring& err)
{
    FILE* f = _wfopen(path.c_str(), L"wb");
    if (!f) { err = L"写不了文件：" + path; return false; }
    for (const std::string& line : lines)
    {
        fwrite(line.data(), 1, line.size(), f);
        fputc('\n', f);
    }
    fclose(f);
    return true;
}

} // namespace
#endif

static int CmdCbsDump(const std::wstring& hiveSpec, const std::wstring& outPath)
{
#ifdef _WIN32
    HKEY rootKey = nullptr;
    std::wstring sub, err;
    if (!ParseHivePath(hiveSpec, rootKey, sub, err)) { Err(ToUtf8(err)); return 2; }

    HKEY store = nullptr;
    if (RegOpenKeyExW(rootKey, sub.c_str(), 0, KEY_READ, &store) != ERROR_SUCCESS)
    {
        Err("打不开台账根（reg load 了吗？）");
        return 2;
    }

    std::vector<std::string> lines;
    const bool ok = DumpKeyRecursive(store, L"", 0, lines, err);
    RegCloseKey(store);
    if (!ok) { Err(ToUtf8(err)); return 1; }

    std::sort(lines.begin(), lines.end());
    if (!WriteLinesUtf8(outPath, lines, err)) { Err(ToUtf8(err)); return 1; }

    size_t keys = 0, values = 0;
    for (const std::string& line : lines)
        (line[0] == 'K' ? keys : values)++;
    OutLn("dumped keys=" + std::to_string(keys) + " values=" + std::to_string(values) +
          " lines=" + std::to_string(lines.size()));
    return 0;
#else
    (void)hiveSpec; (void)outPath;
    return 2;
#endif
}

static int CmdCbsDiff(const std::wstring& pathA, const std::wstring& pathB, int limit)
{
#ifdef _WIN32
    std::wstring rawA, rawB, err;
    if (!ReadFileUtf8(pathA, rawA, err)) { Err(ToUtf8(err)); return 2; }
    if (!ReadFileUtf8(pathB, rawB, err)) { Err(ToUtf8(err)); return 2; }

    auto toSet = [](const std::wstring& raw) {
        std::set<std::string> out;
        const std::string text = ToUtf8(raw);
        std::size_t pos = 0;
        while (pos < text.size())
        {
            std::size_t end = text.find('\n', pos);
            if (end == std::string::npos) end = text.size();
            std::string line = text.substr(pos, end - pos);
            pos = end + 1;
            if (!line.empty() && line.back() == '\r') line.pop_back();
            if (!line.empty()) out.insert(std::move(line));
        }
        return out;
    };
    const std::set<std::string> a = toSet(rawA);
    const std::set<std::string> b = toSet(rawB);

    std::vector<std::string> onlyA, onlyB;
    std::set_difference(a.begin(), a.end(), b.begin(), b.end(), std::back_inserter(onlyA));
    std::set_difference(b.begin(), b.end(), a.begin(), a.end(), std::back_inserter(onlyB));

    auto countPrefix = [](const std::vector<std::string>& lines, char kind) {
        return std::count_if(lines.begin(), lines.end(),
                             [kind](const std::string& s) { return !s.empty() && s[0] == kind; });
    };
    const size_t aKeys = countPrefix(onlyA, 'K'), aVals = onlyA.size() - aKeys;
    const size_t bKeys = countPrefix(onlyB, 'K'), bVals = onlyB.size() - bKeys;

    OutLn("a lines=" + std::to_string(a.size()) + "  b lines=" + std::to_string(b.size()));
    OutLn("only-in-a: keys=" + std::to_string(aKeys) + " values=" + std::to_string(aVals));
    OutLn("only-in-b: keys=" + std::to_string(bKeys) + " values=" + std::to_string(bVals));

    const int shown = (limit > 0) ? limit : 20;
    int printed = 0;
    for (const std::string& line : onlyA)
    {
        if (printed++ >= shown) break;
        OutLn("  - " + line);
    }
    printed = 0;
    for (const std::string& line : onlyB)
    {
        if (printed++ >= shown) break;
        OutLn("  + " + line);
    }
    return (onlyA.empty() && onlyB.empty()) ? 0 : 1;
#else
    (void)pathA; (void)pathB; (void)limit;
    return 2;
#endif
}

static void Usage()
{
    OutLn("OCID servicing console");
    OutLn("");
    OutLn("  ocid_manifest info <manifest>");
    OutLn("  ocid_manifest ops  <manifest> [--json]");
    OutLn("  ocid_manifest scan <dir> [maxFiles]");
    OutLn("  ocid_manifest dump-xsd <manifest> <outFile>");
    OutLn("  ocid_manifest export-ops <dir> [maxFiles]    registry ops as JSONL (C# consumer)");
    OutLn("  ocid_manifest place-ops --windows <winDir> --list <pathsFile> [--packages <root>] [--image-arch <arch>]");
    OutLn("  ocid_manifest place-ops --windows <winDir> <dir> [maxFiles]");
    OutLn("  ocid_manifest dmi-ops --list <pathsFile> [--packages <root>]   driver import plan as JSONL");
    OutLn("  ocid_manifest dmi-ledger <fileRepoDir> [--list <dirsFile>] [--arch x86] [--inbox]   DriverDatabase records as JSONL");
    OutLn("      --image-arch: arm|arm64|amd64|x86|none（默认自动探测 ntoskrnl.exe；none = 不红移）");
    OutLn("  ocid_manifest verify-names <dir> [maxFiles]   (check component names against real file names)");
    OutLn("  ocid_manifest expand <windowsDir> <text...>   expand $(runtime.*); --paths to also show base paths");
    OutLn("  ocid_manifest macros                          list known $(runtime.*) macro names");
    OutLn("  ocid_manifest cbs-scan HKLM\\OCIDCOMP [--limit N]   decode+re-encode every store record");
    OutLn("  ocid_manifest cbs-keys <csvFile> [--names component|deployment]   check pseudo-key suffix / full key name");
    OutLn("  ocid_manifest cbs-dump HKLM\\OCIDCOMP <outFile>   canonical (sorted) dump of the whole store");
    OutLn("  ocid_manifest cbs-diff <a.txt> <b.txt> [--limit N]   compare two cbs-dump files");
    OutLn("");
    OutLn("Data goes to stdout, diagnostics to stderr.");
}

// ── 组件键伪键校验（cbs-keys）────────────────────────────────────────────
//
// 输入是从真机导出的两列 CSV：`"组件键","身份文本"`（E:\ocid_verify\components_all.csv
// 就是这种形态）。逐行算 CbsPseudoKeySuffix 并与键尾的 16 个 hex 比对——真机 13,325
// 条必须 0 不一致，这是"与微软 COMPONENTS 逐字节一致"验收里伪键那一道闸。
//
// 用法：ocid_manifest cbs-keys <csv> [--names component|deployment]
int CmdCbsFamilyCheck(const std::wstring& componentsPath, const std::wstring& familiesPath)
{
    // 用组件身份算出"家庭键"，核对它是否真的出现在真机 VersionedIndex 的家庭键集合里。
    // components 是 `"key","identity"` 两列 CSV；families 是每行一个家庭键。
    std::wstring rawFamilies, readErr;
    if (!ReadFileUtf8(familiesPath, rawFamilies, readErr)) { Err(ToUtf8(readErr)); return 1; }
    std::set<std::string> families;
    {
        const std::string text = ToUtf8(rawFamilies);
        std::size_t pos = 0;
        while (pos < text.size())
        {
            std::size_t end = text.find('\n', pos);
            if (end == std::string::npos) end = text.size();
            std::string line = text.substr(pos, end - pos);
            pos = end + 1;
            if (!line.empty() && line.back() == '\r') line.pop_back();
            if (!line.empty()) families.insert(line);
        }
    }

    std::wstring rawComponents;
    if (!ReadFileUtf8(componentsPath, rawComponents, readErr)) { Err(ToUtf8(readErr)); return 1; }
    const std::string text = ToUtf8(rawComponents);
    long long rows = 0, matched = 0, unknown = 0, unparsable = 0;
    std::set<std::string> produced;
    std::size_t pos = 0;
    while (pos < text.size())
    {
        std::size_t lineEnd = text.find('\n', pos);
        if (lineEnd == std::string::npos) lineEnd = text.size();
        std::string line = text.substr(pos, lineEnd - pos);
        pos = lineEnd + 1;
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (line.empty()) continue;

        std::vector<std::string> fields;
        std::string field;
        bool inQuotes = false;
        for (std::size_t i = 0; i < line.size(); ++i)
        {
            const char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.size() && line[i + 1] == '"') { field.push_back('"'); ++i; }
                    else { inQuotes = false; }
                }
                else { field.push_back(c); }
            }
            else if (c == '"') { inQuotes = true; }
            else if (c == ',') { fields.push_back(field); field.clear(); }
            else { field.push_back(c); }
        }
        fields.push_back(field);
        if (fields.size() != 2) continue;
        if (fields[0] == "key" && fields[1] == "identity") continue;
        ++rows;
        CbsIdentity identity;
        std::string parseErr;
        if (!CbsParseIdentity(fields[1], identity, parseErr))
        {
            ++unparsable;
            if (unparsable <= 10) Err("身份解析失败 " + fields[0] + "：" + parseErr);
            continue;
        }
        const std::string family = CbsFamilyKeyName(identity);
        produced.insert(family);
        if (families.count(family) != 0) { ++matched; }
        else
        {
            ++unknown;
            if (unknown <= 10) Err("家庭键不存在：" + family + "（来自 " + fields[0] + "）");
        }
    }
    long long neverProduced = 0;
    for (const std::string& family : families)
    {
        if (produced.count(family) == 0)
        {
            ++neverProduced;
            if (neverProduced <= 10) OutLn("未被组件覆盖：" + family);
        }
    }
    OutLn("rows=" + std::to_string(rows) + " matched=" + std::to_string(matched) +
          " unknown=" + std::to_string(unknown) + " unparsable=" + std::to_string(unparsable) +
          " families=" + std::to_string(families.size()) + " neverProduced=" + std::to_string(neverProduced));
    return (unknown == 0 && unparsable == 0 && neverProduced == 0) ? 0 : 1;
}

int CmdCbsKeyCheck(const std::wstring& path, const std::wstring& namesMode)
{
    std::wstring raw;
    std::wstring readErr;
    if (!ReadFileUtf8(path, raw, readErr))
    {
        Err(ToUtf8(readErr));
        return 1;
    }
    const std::string text = ToUtf8(raw);
    long long rows = 0, matched = 0, bad = 0, unparsable = 0;
    std::size_t pos = 0;
    while (pos < text.size())
    {
        std::size_t lineEnd = text.find('\n', pos);
        if (lineEnd == std::string::npos) lineEnd = text.size();
        std::string line = text.substr(pos, lineEnd - pos);
        pos = lineEnd + 1;
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (line.empty()) continue;

        // 两列都带引号；字段内的引号用 "" 转义。
        std::vector<std::string> fields;
        std::string field;
        bool inQuotes = false;
        for (std::size_t i = 0; i < line.size(); ++i)
        {
            const char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.size() && line[i + 1] == '"') { field.push_back('"'); ++i; }
                    else { inQuotes = false; }
                }
                else { field.push_back(c); }
            }
            else if (c == '"') { inQuotes = true; }
            else if (c == ',') { fields.push_back(field); field.clear(); }
            else { field.push_back(c); }
        }
        fields.push_back(field);
        if (fields.size() != 2)
        {
            ++bad;
            if (bad <= 10) Err("列数不是 2：" + line);
            continue;
        }
        ++rows;
        const std::string& key = fields[0];
        if (key == "key" && fields[1] == "identity") { --rows; continue; }  // 表头
        CbsIdentity identity;
        std::string parseErr;
        if (!CbsParseIdentity(fields[1], identity, parseErr))
        {
            ++unparsable;
            if (unparsable <= 10) Err("身份解析失败 " + key + "：" + parseErr);
            continue;
        }
        const std::size_t underscore = key.rfind('_');
        const std::string suffix =
            (underscore == std::string::npos) ? std::string() : key.substr(underscore + 1);
        std::string want = suffix;
        if (namesMode == L"component") {
            want = CbsComponentKeyName(identity);
        } else if (namesMode == L"deployment") {
            want = CbsDeploymentKeyName(identity);
        }
        const std::string& actual = namesMode.empty() ? suffix : key;
        if (want == actual)
        {
            ++matched;
        }
        else
        {
            ++bad;
            if (bad <= 10) Err("键名不一致：" + key + "（算出 " + want + "）");
        }
    }
    OutLn("rows=" + std::to_string(rows) + " matched=" + std::to_string(matched) +
          " bad=" + std::to_string(bad) + " unparsable=" + std::to_string(unparsable));
    return (bad == 0 && unparsable == 0) ? 0 : 1;
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

    if (cmd == L"place-ops") {
        std::wstring dir, listFile, winDir, pkgRoot, imageArch;
        int mx = 0;
        for (int i = 2; i < argc; ++i) {
            std::string a = argv[i];
            if (a == "--list" && i + 1 < argc)              { listFile = FromUtf8OrAnsi(argv[++i]); }
            else if (a == "--windows" && i + 1 < argc)      { winDir = FromUtf8OrAnsi(argv[++i]); }
            else if (a == "--packages" && i + 1 < argc)     { pkgRoot = FromUtf8OrAnsi(argv[++i]); }
            else if (a == "--image-arch" && i + 1 < argc)   { imageArch = FromUtf8OrAnsi(argv[++i]); }
            else if (a.rfind("--", 0) == 0)                 { /* 其它开关忽略 */ }
            else if (dir.empty())                           { dir = FromUtf8OrAnsi(argv[i]); }
            else                                            { mx = atoi(argv[i]); }
        }
        return CmdPlaceOps(dir, listFile, winDir, pkgRoot, imageArch, mx);
    }

    if (cmd == L"dmi-ops") {
        std::wstring dir, listFile, pkgRoot;
        int mx = 0;
        for (int i = 2; i < argc; ++i) {
            std::string a = argv[i];
            if (a == "--list" && i + 1 < argc)          { listFile = FromUtf8OrAnsi(argv[++i]); }
            else if (a == "--packages" && i + 1 < argc) { pkgRoot = FromUtf8OrAnsi(argv[++i]); }
            else if (a.rfind("--", 0) == 0)             { ++i; }
            else if (dir.empty())                       { dir = FromUtf8OrAnsi(argv[i]); }
            else                                        { mx = atoi(argv[i]); }
        }
        return CmdDmiOps(dir, listFile, pkgRoot, mx);
    }

    if (cmd == L"dmi-ledger") {
        std::wstring dir, listFile, arch;
        bool inbox = false;
        int mx = 0;
        for (int i = 2; i < argc; ++i) {
            std::string a = argv[i];
            if (a == "--list" && i + 1 < argc)      { listFile = FromUtf8OrAnsi(argv[++i]); }
            else if (a == "--arch" && i + 1 < argc) { arch = FromUtf8OrAnsi(argv[++i]); }
            else if (a == "--inbox")                { inbox = true; }
            else if (a.rfind("--", 0) == 0)         { ++i; }
            else if (dir.empty())                   { dir = FromUtf8OrAnsi(argv[i]); }
            else                                    { mx = atoi(argv[i]); }
        }
        return CmdDmiLedger(dir, listFile, arch, inbox, mx);
    }
    if (cmd == L"verify-names" && argc >= 3) {
        int mx = (argc >= 4) ? atoi(argv[3]) : 0;
        return CmdVerifyNames(FromUtf8OrAnsi(argv[2]), mx);
    }

    if (cmd == L"dump-xsd" && argc >= 4)
        return CmdDumpXsd(FromUtf8OrAnsi(argv[2]), FromUtf8OrAnsi(argv[3]));

    if (cmd == L"expand" && argc >= 4) {
        bool paths = false;
        std::wstring text;
        for (int i = 3; i < argc; ++i) {
            if (std::string(argv[i]) == "--paths") { paths = true; continue; }
            if (!text.empty()) text += L' ';
            text += FromUtf8OrAnsi(argv[i]);
        }
        return CmdExpand(FromUtf8OrAnsi(argv[2]), text, paths);
    }

    if (cmd == L"cbs-scan" && argc >= 3) {
        int mx = 0;
        for (int i = 3; i < argc; ++i) {
            const std::string a = argv[i];
            if (a == "--limit" && i + 1 < argc) { mx = atoi(argv[++i]); }
        }
        return CmdCbsScan(FromUtf8OrAnsi(argv[2]), mx);
    }

    if (cmd == L"cbs-keys" && argc >= 3) {
        std::wstring namesMode;
        for (int i = 3; i < argc; ++i) {
            if (std::string(argv[i]) == "--names" && i + 1 < argc) {
                namesMode = FromUtf8OrAnsi(argv[++i]);
            }
        }
        return CmdCbsKeyCheck(FromUtf8OrAnsi(argv[2]), namesMode);
    }

    if (cmd == L"cbs-families" && argc >= 4) {
        return CmdCbsFamilyCheck(FromUtf8OrAnsi(argv[2]), FromUtf8OrAnsi(argv[3]));
    }

    if (cmd == L"cbs-dump" && argc >= 4) {
        return CmdCbsDump(FromUtf8OrAnsi(argv[2]), FromUtf8OrAnsi(argv[3]));
    }

    if (cmd == L"cbs-diff" && argc >= 4) {
        int mx = 0;
        for (int i = 4; i < argc; ++i) {
            if (std::string(argv[i]) == "--limit" && i + 1 < argc) { mx = atoi(argv[++i]); }
        }
        return CmdCbsDiff(FromUtf8OrAnsi(argv[2]), FromUtf8OrAnsi(argv[3]), mx);
    }

    if (cmd == L"macros")
        return CmdMacros();

    Usage();
    return 1;
}



