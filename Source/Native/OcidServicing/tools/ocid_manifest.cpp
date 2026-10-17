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
#include "ocid/FilePlacement.h"
#include "ocid/MacroExpander.h"
#include "ocid/Manifest.h"
#include "ocid/RegistryValue.h"
#include "ocid/WcmSchema.h"

#include <cstdio>
#include <cstdlib>
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
    OutLn("      --image-arch: arm|arm64|amd64|x86|none（默认自动探测 ntoskrnl.exe；none = 不红移）");
    OutLn("  ocid_manifest verify-names <dir> [maxFiles]   (check component names against real file names)");
    OutLn("  ocid_manifest expand <windowsDir> <text...>   expand $(runtime.*); --paths to also show base paths");
    OutLn("  ocid_manifest macros                          list known $(runtime.*) macro names");
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

    if (cmd == L"macros")
        return CmdMacros();

    Usage();
    return 1;
}
