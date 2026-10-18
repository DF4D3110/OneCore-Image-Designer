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
#include "ocid/Pcm.h"
#include "ocid/Dcm.h"
#include "ocid/Microdom.h"
#include "ocid/RegistryValue.h"
#include "ocid/Sha256.h"
#include "ocid/WcmSchema.h"
#include "../src/detail/XmlPull.h"

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <functional>
#include <iterator>
#include <map>
#include <set>
#include <string>
#include <vector>

#ifdef _WIN32
#include <ktmw32.h>  // CreateTransaction / CommitTransaction / RollbackTransaction（KTM）
#endif

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
    // 现代镜像（16299+）的组件清单是 DCM 压缩容器；给了解压字典时在这里透明解码，
    // 上层（清单解析、计划生成）不需要知道清单是否被压过。
    std::vector<std::uint8_t> g_manifestDictionary;
    // 现代镜像（16299+）的 f! 值是 REG_DWORD 文件状态位；非 1 的状态由 --file-flags
    // 输入（安装历史），键是 "<组件键>|<f! 值名>"。
    std::map<std::string, std::uint32_t> g_fileFlags;

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

        if (!g_manifestDictionary.empty() && buf.size() >= 8
            && (unsigned char)buf[0] == 'D' && (unsigned char)buf[1] == 'C'
            && (unsigned char)buf[2] == 'M' && (unsigned char)buf[3] == 0x01) {
            std::vector<std::uint8_t> compressed(buf.begin(), buf.end());
            std::vector<std::uint8_t> decoded;
            std::string decodeError;
            if (!DcmDecodeManifest(compressed, g_manifestDictionary, decoded, decodeError)) {
                err = L"DCM 清单解压失败：" + path;
                return false;
            }
            buf.assign(decoded.begin(), decoded.end());
        }

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

// REG_DWORD 的小端 hex（真机 cbs-dump 的行格式用同一个表示）。
std::string HexDword(std::uint32_t value)
{
    static const char* digits = "0123456789abcdef";
    std::string text(8, '0');
    for (int i = 0; i < 4; ++i)
    {
        const std::uint8_t byte = static_cast<std::uint8_t>((value >> (8 * i)) & 0xFFu);
        text[2 * i] = digits[byte >> 4];
        text[2 * i + 1] = digits[byte & 0x0F];
    }
    return text;
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

// ── 台账自洽性核对（cbs-verify-store）──────────────────────────────────
//
// 手上没有"微软原版"时，仍然可以证明一份 COMPONENTS 是**自洽**的：每个键名都
// 能从它自己的 identity 重算出来，每个值名都能从它自己的数据重算出来，部署与
// 组件之间的引用都闭合。OCID 自己组装的镜像走这一关，就不必依赖对拍样本。
//
// 查的东西：
//   1) 组件键名 == CbsComponentKeyName(record.identity)
//   2) f! 值名 == CbsFileValueName(原始大小写文件名)
//   3) 部署键名 == CbsDeploymentKeyName(appid)
//   4) p!/s!/i! 值名 == CbsPayloadValueName(prefix, payload 身份)
//   5) 每个 c! 指向的部署键存在
//   6) 每个部署 appid 都有一条 CF=12 的组件记录（真机 2,276/2,276 成立）
//   7) Catalogs 键都是 64 个 hex；VersionedIndex 的家庭键都由某个组件产生
static int CmdCbsVerifyStore(const std::wstring& hiveSpec)
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

    auto report = [](const char* label, std::size_t count) {
        if (count != 0) Err(std::string(label) + "=" + std::to_string(count));
    };

    std::size_t componentCount = 0, deploymentCount = 0, catalogCount = 0, familyCount = 0;
    std::size_t badComponentKey = 0, badFileName = 0, badDeploymentKey = 0, badPayloadName = 0;
    std::size_t missingMarkTarget = 0, missingAppIdComponent = 0, badCatalogKey = 0, orphanFamily = 0;
    std::vector<std::string> samples;

    std::set<std::string> componentKeys;
    std::set<std::string> deploymentKeys;
    std::set<std::string> appIdComponentKeys;   // CF=12 的组件键（部署 appid 的落点）
    std::set<std::string> familyKeys;           // 由组件产生的家庭键

    auto note = [&samples](const std::string& text) {
        if (samples.size() < 20) samples.push_back(text);
    };

    // 1) 组件
    {
        HKEY key = nullptr;
        if (RegOpenKeyExW(store, L"DerivedData\\Components", 0, KEY_READ, &key) == ERROR_SUCCESS)
        {
            std::vector<std::wstring> names;
            if (EnumSubKeys(key, names, err))
            {
                for (const std::wstring& name : names)
                {
                    HKEY item = nullptr;
                    if (RegOpenKeyExW(key, name.c_str(), 0, KEY_READ, &item) != ERROR_SUCCESS) continue;
                    std::vector<CbsRegValue> raw;
                    if (!ReadAllValues(item, raw, err)) { RegCloseKey(item); continue; }
                    const std::vector<CbsValue> values = ToCbsValues(raw);
                    RegCloseKey(item);
                    ++componentCount;

                    const std::string keyName = ToUtf8(name);
                    componentKeys.insert(keyName);

                    CbsComponentRecord record;
                    std::string decodeErr;
                    if (!CbsDecodeComponentRecord(name, values, record, decodeErr)) { ++badComponentKey; note("decode " + keyName); continue; }
                    if (!CbsIsComponentName(name)) ++badComponentKey;

                    CbsIdentity identity;
                    if (!CbsParseIdentity(record.identity, identity, decodeErr) ||
                        CbsComponentKeyName(identity) != keyName)
                    {
                        ++badComponentKey;
                        note("key " + keyName + " != " + (record.identity.empty() ? "<none>" : CbsComponentKeyName(identity)));
                    }
                    else
                    {
                        familyKeys.insert(CbsFamilyKeyName(identity));
                    }

                    for (const CbsFile& file : record.files)
                    {
                        if (file.fileName.empty()) continue;
                        if (CbsFileValueName(file.fileName) != file.valueName)
                        {
                            ++badFileName;
                            note("fname " + keyName + " " + file.valueName);
                        }
                    }
                    if (record.hasConfigFlags && record.configFlags == 12) appIdComponentKeys.insert(keyName);
                }
            }
            RegCloseKey(key);
        }
    }

    // 2) 部署
    {
        HKEY key = nullptr;
        if (RegOpenKeyExW(store, L"CanonicalData\\Deployments", 0, KEY_READ, &key) == ERROR_SUCCESS)
        {
            std::vector<std::wstring> names;
            if (EnumSubKeys(key, names, err))
            {
                for (const std::wstring& name : names)
                {
                    HKEY item = nullptr;
                    if (RegOpenKeyExW(key, name.c_str(), 0, KEY_READ, &item) != ERROR_SUCCESS) continue;
                    std::vector<CbsRegValue> raw;
                    if (!ReadAllValues(item, raw, err)) { RegCloseKey(item); continue; }
                    const std::vector<CbsValue> values = ToCbsValues(raw);
                    RegCloseKey(item);
                    ++deploymentCount;

                    const std::string keyName = ToUtf8(name);
                    deploymentKeys.insert(keyName);

                    CbsDeploymentRecord record;
                    std::string decodeErr;
                    if (!CbsDecodeDeploymentRecord(name, values, record, decodeErr)) { ++badDeploymentKey; note("dep-decode " + keyName); continue; }

                    CbsIdentity identity;
                    if (!CbsParseIdentity(record.appId, identity, decodeErr) ||
                        CbsDeploymentKeyName(identity) != keyName)
                    {
                        ++badDeploymentKey;
                        note("dep-key " + keyName);
                    }
                    else if (componentKeys.count(CbsComponentKeyName(identity)) == 0)
                    {
                        ++missingAppIdComponent;
                        note("appid-component " + keyName + " -> " + CbsComponentKeyName(identity));
                    }

                    for (const CbsPayload& payload : record.payloads)
                    {
                        if (CbsPayloadValueName(payload.prefix, payload.identity) != payload.valueName)
                        {
                            ++badPayloadName;
                            note("payload " + keyName + " " + payload.valueName);
                        }
                    }
                }
            }
            RegCloseKey(key);
        }
    }

    // 3) c! 引用闭合
    {
        HKEY key = nullptr;
        if (RegOpenKeyExW(store, L"DerivedData\\Components", 0, KEY_READ, &key) == ERROR_SUCCESS)
        {
            std::vector<std::wstring> names;
            if (EnumSubKeys(key, names, err))
            {
                for (const std::wstring& name : names)
                {
                    HKEY item = nullptr;
                    if (RegOpenKeyExW(key, name.c_str(), 0, KEY_READ, &item) != ERROR_SUCCESS) continue;
                    std::vector<CbsRegValue> raw;
                    if (!ReadAllValues(item, raw, err)) { RegCloseKey(item); continue; }
                    const std::vector<CbsValue> values = ToCbsValues(raw);
                    RegCloseKey(item);
                    CbsComponentRecord record;
                    std::string decodeErr;
                    if (!CbsDecodeComponentRecord(name, values, record, decodeErr)) continue;
                    for (const CbsMark& mark : record.deploymentMarks)
                    {
                        if (deploymentKeys.count(mark.deploymentName) == 0)
                        {
                            ++missingMarkTarget;
                            note("mark " + ToUtf8(name) + " -> " + mark.deploymentName);
                        }
                    }
                }
            }
            RegCloseKey(key);
        }
    }

    // 4) Catalogs 键形态
    {
        HKEY key = nullptr;
        if (RegOpenKeyExW(store, L"CanonicalData\\Catalogs", 0, KEY_READ, &key) == ERROR_SUCCESS)
        {
            std::vector<std::wstring> names;
            if (EnumSubKeys(key, names, err))
            {
                for (const std::wstring& name : names)
                {
                    ++catalogCount;
                    const std::string text = ToUtf8(name);
                    bool ok = text.size() == 64;
                    for (char c : text)
                        if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) ok = false;
                    if (!ok) { ++badCatalogKey; note("catalog " + text); }
                }
            }
            RegCloseKey(key);
        }
    }

    // 5) VersionedIndex 家庭键必须由某个组件产生
    {
        HKEY key = nullptr;
        if (RegOpenKeyExW(store, L"DerivedData\\VersionedIndex", 0, KEY_READ, &key) == ERROR_SUCCESS)
        {
            std::vector<std::wstring> buckets;
            if (EnumSubKeys(key, buckets, err))
            {
                for (const std::wstring& bucket : buckets)
                {
                    HKEY bucketKey = nullptr;
                    const std::wstring familiesPath = L"DerivedData\\VersionedIndex\\" + bucket + L"\\ComponentFamilies";
                    if (RegOpenKeyExW(store, familiesPath.c_str(), 0, KEY_READ, &bucketKey) != ERROR_SUCCESS) continue;
                    std::vector<std::wstring> families;
                    if (EnumSubKeys(bucketKey, families, err))
                    {
                        for (const std::wstring& family : families)
                        {
                            ++familyCount;
                            const std::string text = ToUtf8(family);
                            if (familyKeys.count(text) == 0)
                            {
                                ++orphanFamily;
                                note("family " + text);
                            }
                        }
                    }
                    RegCloseKey(bucketKey);
                }
            }
            RegCloseKey(key);
        }
    }

    RegCloseKey(store);

    report("bad-component-key", badComponentKey);
    report("bad-file-name", badFileName);
    report("bad-deployment-key", badDeploymentKey);
    report("bad-payload-name", badPayloadName);
    report("missing-mark-target", missingMarkTarget);
    report("missing-appid-component", missingAppIdComponent);
    report("bad-catalog-key", badCatalogKey);
    report("orphan-family", orphanFamily);
    for (const std::string& sample : samples) Err("  - " + sample);

    const bool ok = (badComponentKey + badFileName + badDeploymentKey + badPayloadName +
                     missingMarkTarget + missingAppIdComponent + badCatalogKey + orphanFamily) == 0;
    OutLn("components=" + std::to_string(componentCount) +
          " deployments=" + std::to_string(deploymentCount) +
          " catalogs=" + std::to_string(catalogCount) +
          " families=" + std::to_string(familyCount) +
          " result=" + (ok ? "OK" : "MISMATCH"));
    return ok ? 0 : 1;
#else
    (void)hiveSpec;
    return 2;
#endif
}

// ── 文件落位核对（cbs-verify-files）────────────────────────────────────
//
// 台账与文件必须同时成立：一个组件的 f! 值条数 = 它在 WinSxS 下的文件数。
// 组装顺序约定是"先把文件落进 WinSxS，再提交台账"，这个命令在提交前核对
// 一遍：缺组件目录、缺文件、条数不等都算失败，调用方据此放弃写台账 ——
// 这就是"文件 + 台账"的事务边界（不用 CreateFileTransacted 也能保证不留
// 半截状态：文件多了是孤儿，台账不会被改）。
static int CmdCbsVerifyFiles(const std::wstring& planPath, const std::wstring& winSxsDir)
{
#ifdef _WIN32
    std::wstring raw, err;
    if (!ReadFileUtf8(planPath, raw, err)) { Err(ToUtf8(err)); return 2; }

    auto nibble = [](char c) -> int {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    };
    auto lowerAscii = [](std::string text) {
        for (char& c : text)
            if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
        return text;
    };

    std::map<std::string, std::vector<std::string>> expected; // 组件键 → 文件名（小写）
    std::set<std::string> componentKeys;

    const std::string text = ToUtf8(raw);
    std::size_t pos = 0;
    while (pos < text.size())
    {
        std::size_t end = text.find('\n', pos);
        if (end == std::string::npos) end = text.size();
        std::string line = text.substr(pos, end - pos);
        pos = end + 1;
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (line.size() < 2 || line[0] != 'V') continue;

        std::vector<std::string> fields;
        std::size_t p = 0;
        for (;;)
        {
            const std::size_t tab = line.find('\t', p);
            if (tab == std::string::npos) { fields.push_back(line.substr(p)); break; }
            fields.push_back(line.substr(p, tab - p));
            p = tab + 1;
        }
        if (fields.size() < 5) continue;

        const std::string& keyPath = fields[1];
        const std::string& valueName = fields[2];
        static const std::string kComponents = "DerivedData\\Components\\";
        if (keyPath.rfind(kComponents, 0) != 0) continue;
        const std::string componentKey = keyPath.substr(kComponents.size());
        if (componentKey.empty()) continue;
        if (componentKey.find('\\') != std::string::npos) continue;
        componentKeys.insert(componentKey);

        if (valueName.rfind("f!", 0) != 0) continue;
        std::vector<std::string>& names = expected[componentKey];

        const std::string& hex = fields[4];
        std::string name;
        if (hex.empty())
        {
            name = valueName.substr(2); // 空数据 = 值名后缀本身就是（小写）文件名
        }
        else if (hex.size() % 4 == 0)
        {
            std::wstring wide;
            wide.reserve(hex.size() / 4);
            bool ok = true;
            for (std::size_t i = 0; i < hex.size(); i += 4)
            {
                const int b0 = (nibble(hex[i]) << 4) | nibble(hex[i + 1]);   // 低字节
                const int b1 = (nibble(hex[i + 2]) << 4) | nibble(hex[i + 3]); // 高字节
                if (b0 < 0 || b1 < 0) { ok = false; break; }
                wide.push_back(static_cast<wchar_t>(b1 * 256 + b0));
            }
            if (!ok) continue;
            name = ToUtf8(wide);
        }
        else
        {
            continue;
        }
        names.push_back(lowerAscii(name.substr(name.find_last_of('\\') == std::string::npos
                                              ? 0
                                              : name.find_last_of('\\') + 1)));
    }

    namespace fs = std::filesystem;
    const fs::path root(winSxsDir);
    std::error_code ec;

    std::size_t withFiles = 0, filesExpected = 0;
    std::size_t dirsMissing = 0, filesMissing = 0, countMismatch = 0, strayDirs = 0;
    std::vector<std::string> samples;

    for (const auto& item : expected)
    {
        const std::string& key = item.first;
        ++withFiles;
        filesExpected += item.second.size();

        const fs::path dir = root / fs::path(key);
        if (!fs::is_directory(dir, ec))
        {
            ++dirsMissing;
            if (samples.size() < 20) samples.push_back("no-dir " + key);
            continue;
        }

        std::set<std::string> actual;
        for (const auto& e : fs::recursive_directory_iterator(dir, ec))
        {
            if (!e.is_regular_file(ec)) continue;
            const std::string leaf = ToUtf8(e.path().filename().wstring());
            if (leaf == key + ".manifest") continue; // 组件清单不算载荷
            actual.insert(lowerAscii(leaf));
        }

        for (const std::string& want : item.second)
        {
            if (actual.count(want) == 0)
            {
                ++filesMissing;
                if (samples.size() < 20) samples.push_back("no-file " + key + "\\" + want);
            }
        }
        if (actual.size() != item.second.size())
        {
            ++countMismatch;
            if (samples.size() < 20)
                samples.push_back("count " + key + " expected=" + std::to_string(item.second.size()) +
                                  " actual=" + std::to_string(actual.size()));
        }
    }

    // 反向核对：台账里没有 f! 的组件，不该在 WinSxS 下留下目录。
    // 真机 8061 两个镜像都恰好成立（有目录 ⇔ 有 f!），所以这里只报数、不判失败，
    // 免得"只落了部分载荷"的中间态被误判成计划错误。
    for (const auto& key : componentKeys)
    {
        if (expected.count(key) != 0) continue;
        if (fs::is_directory(root / fs::path(key), ec)) ++strayDirs;
    }

    OutLn("components=" + std::to_string(componentKeys.size()) +
          " componentsWithFiles=" + std::to_string(withFiles) +
          " files=" + std::to_string(filesExpected) +
          " dirsMissing=" + std::to_string(dirsMissing) +
          " filesMissing=" + std::to_string(filesMissing) +
          " countMismatch=" + std::to_string(countMismatch) +
          " strayDirs=" + std::to_string(strayDirs));
    for (const std::string& s : samples) OutLn("  - " + s);
    return (dirsMissing == 0 && filesMissing == 0 && countMismatch == 0) ? 0 : 1;
#else
    (void)planPath; (void)winSxsDir;
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

// ── 计划 → 台账文件（cbs-write）──────────────────────────────────────────
//
// 输入就是 cbs-dump 那种规范化文本（K/V 行），所以 cbs-plan-components /
// cbs-plan-deployments / cbs-plan-versioned 的产物可以合并后直接落成 hive。
// 造空壳 hive 的办法：临时键 → RegSaveKey 出文件 → RegLoadKey 挂起来写 →
// RegUnloadKey 落盘。挂载名用 OCIDOUT（不以 SOFTWARE 结尾，绕开 UCPD 硬编码）。
#ifdef _WIN32
namespace {

bool HexToBytes(const std::string& hex, std::vector<std::uint8_t>& out)
{
    if (hex.size() % 2 != 0) return false;
    out.clear();
    out.reserve(hex.size() / 2);
    auto nib = [](char c) -> int {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    };
    for (std::size_t i = 0; i < hex.size(); i += 2)
    {
        const int hi = nib(hex[i]);
        const int lo = nib(hex[i + 1]);
        if (hi < 0 || lo < 0) return false;
        out.push_back(static_cast<std::uint8_t>((hi << 4) | lo));
    }
    return true;
}

std::vector<std::string> SplitTabs(const std::string& line)
{
    std::vector<std::string> out;
    std::size_t pos = 0;
    while (true)
    {
        const std::size_t end = line.find('\t', pos);
        if (end == std::string::npos) { out.push_back(line.substr(pos)); break; }
        out.push_back(line.substr(pos, end - pos));
        pos = end + 1;
    }
    return out;
}

// RegSaveKey / RegLoadKey 要备份/还原特权；没有就 1314 (ERROR_PRIVILEGE_NOT_HELD)。
bool EnablePrivilege(const wchar_t* name, std::wstring& err)
{
    HANDLE token = nullptr;
    if (!::OpenProcessToken(::GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, &token))
    {
        err = L"OpenProcessToken 失败：" + std::to_wstring(::GetLastError());
        return false;
    }
    LUID luid{};
    if (!::LookupPrivilegeValueW(nullptr, name, &luid))
    {
        const DWORD rc = ::GetLastError();
        ::CloseHandle(token);
        err = std::wstring(L"LookupPrivilegeValue(") + name + L") 失败：" + std::to_wstring(rc);
        return false;
    }
    TOKEN_PRIVILEGES tp{};
    tp.PrivilegeCount = 1;
    tp.Privileges[0].Luid = luid;
    tp.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;
    ::SetLastError(ERROR_SUCCESS);
    ::AdjustTokenPrivileges(token, FALSE, &tp, sizeof(tp), nullptr, nullptr);
    const DWORD rc = ::GetLastError();
    ::CloseHandle(token);
    if (rc != ERROR_SUCCESS)
    {
        err = std::wstring(L"启用特权 ") + name + L" 失败：" + std::to_wstring(rc);
        return false;
    }
    return true;
}

// 建（或开）相对键。rel 用 UTF-8、以 '\\' 分段；空串即根键。
// txn 非空时走 RegCreateKeyTransacted —— 键/值写入都进同一个 KTM 事务，
// 失败可整体回滚（hive 文件保持原样）。
HKEY EnsureKeyCached(HKEY root, const std::string& relUtf8, std::map<std::string, HKEY>& cache,
                     HANDLE txn)
{
    if (relUtf8.empty()) return root;
    auto it = cache.find(relUtf8);
    if (it != cache.end()) return it->second;

    const std::wstring rel = FromUtf8OrAnsi(relUtf8.c_str());
    HKEY key = nullptr;
    const LSTATUS rc = (txn != nullptr && txn != INVALID_HANDLE_VALUE)
                           ? RegCreateKeyTransactedW(root, rel.c_str(), 0, nullptr, 0,
                                                     KEY_ALL_ACCESS, nullptr, &key, nullptr, txn,
                                                     nullptr)
                           : RegCreateKeyExW(root, rel.c_str(), 0, nullptr, 0, KEY_ALL_ACCESS,
                                             nullptr, &key, nullptr);
    if (rc != ERROR_SUCCESS)
    {
        return nullptr;
    }
    cache[relUtf8] = key;
    return key;
}

} // namespace

// KTM 事务化写入：CreateTransaction → RegOpenKeyTransacted/RegCreateKeyTransacted →
// CommitTransaction（失败路径 RollbackTransaction，hive 文件不变）。
// 需要 Win8+ 的 KTM 服务（桌面版/Server 默认运行）；离线 hive 必须先 RegLoadKey 挂起来。
// 只用事务不会改变落盘字节：提交后 cbs-dump 与不事务的结果相同（有测试对拍）。
#pragma comment(lib, "ktmw32.lib")
#endif

static int CmdCbsWrite(const std::wstring& planPath, const std::wstring& hivePath,
                       bool transactional)
{
#ifdef _WIN32
    std::wstring raw, err;
    if (!ReadFileUtf8(planPath, raw, err)) { Err(ToUtf8(err)); return 2; }
    const std::string text = ToUtf8(raw);

    std::wstring privErr;
    if (!EnablePrivilege(L"SeBackupPrivilege", privErr) ||
        !EnablePrivilege(L"SeRestorePrivilege", privErr))
    {
        Err(ToUtf8(privErr) + "（cbs-write 改动的是离线 hive 文件，需要这两个特权）");
        return 2;
    }

    // 1) 空壳 hive 文件
    const wchar_t* kSeed = L"SOFTWARE\\OCIDHiveSeed";
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, kSeed);
    HKEY seed = nullptr;
    if (RegCreateKeyExW(HKEY_LOCAL_MACHINE, kSeed, 0, nullptr, 0, KEY_ALL_ACCESS, nullptr,
                        &seed, nullptr) != ERROR_SUCCESS)
    {
        Err("造临时种子键失败（需要管理员）");
        return 2;
    }
    DeleteFileW(hivePath.c_str());
    const LSTATUS saveRc = RegSaveKeyW(seed, hivePath.c_str(), nullptr);
    RegCloseKey(seed);
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, kSeed);
    if (saveRc != ERROR_SUCCESS)
    {
        Err("RegSaveKey 失败：" + std::to_string(saveRc));
        return 2;
    }

    // 2) 挂起来写
    const wchar_t* kMount = L"OCIDOUT";
    const LSTATUS loadRc = RegLoadKeyW(HKEY_LOCAL_MACHINE, kMount, hivePath.c_str());
    if (loadRc != ERROR_SUCCESS)
    {
        Err("RegLoadKey 失败：" + std::to_string(loadRc));
        return 2;
    }

    HKEY root = nullptr;
    HANDLE txn = nullptr;
    if (transactional)
    {
        txn = ::CreateTransaction(nullptr, 0, 0, 0, 0, 0, nullptr);
        if (txn == nullptr || txn == INVALID_HANDLE_VALUE)
        {
            const DWORD rc = ::GetLastError();
            RegUnLoadKeyW(HKEY_LOCAL_MACHINE, kMount);
            Err("CreateTransaction 失败：" + std::to_string(rc) +
                "（KTM 服务不可用？可去掉 --transactional）");
            return 2;
        }
    }
    const LSTATUS openRc =
        (txn != nullptr)
            ? RegOpenKeyTransactedW(HKEY_LOCAL_MACHINE, kMount, 0, KEY_ALL_ACCESS, &root, txn,
                                    nullptr)
            : RegOpenKeyExW(HKEY_LOCAL_MACHINE, kMount, 0, KEY_ALL_ACCESS, &root);
    if (openRc != ERROR_SUCCESS)
    {
        if (txn != nullptr) ::CloseHandle(txn);
        RegUnLoadKeyW(HKEY_LOCAL_MACHINE, kMount);
        Err("打不开挂载点");
        return 2;
    }

    // 3) 逐行落键/值
    std::map<std::string, HKEY> cache;
    long long keys = 0, values = 0, badLines = 0;
    std::size_t pos = 0;
    while (pos < text.size())
    {
        std::size_t end = text.find('\n', pos);
        if (end == std::string::npos) end = text.size();
        std::string line = text.substr(pos, end - pos);
        pos = end + 1;
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (line.empty()) continue;

        const std::vector<std::string> fields = SplitTabs(line);
        if (fields.size() < 2) { ++badLines; continue; }

        if (fields[0] == "K")
        {
            if (EnsureKeyCached(root, fields[1], cache, txn) == nullptr) { ++badLines; continue; }
            ++keys;
        }
        else if (fields[0] == "V" && fields.size() >= 5)
        {
            HKEY key = EnsureKeyCached(root, fields[1], cache, txn);
            if (key == nullptr) { ++badLines; continue; }
            std::vector<std::uint8_t> data;
            if (!HexToBytes(fields[4], data)) { ++badLines; continue; }
            const std::wstring name = FromUtf8OrAnsi(fields[2].c_str());
            const DWORD type = static_cast<DWORD>(std::strtoul(fields[3].c_str(), nullptr, 10));
            const BYTE* bytes = data.empty() ? nullptr : data.data();
            if (RegSetValueExW(key, name.c_str(), 0, type, bytes,
                               static_cast<DWORD>(data.size())) != ERROR_SUCCESS)
            {
                ++badLines;
                continue;
            }
            ++values;
        }
        else
        {
            ++badLines;
        }
    }

    for (auto& entry : cache) RegCloseKey(entry.second);
    RegCloseKey(root);

    // 事务提交/回滚必须在句柄都关掉之后做。
    bool rolledBack = false;
    if (txn != nullptr)
    {
        if (badLines == 0)
        {
            if (!::CommitTransaction(txn))
            {
                const DWORD rc = ::GetLastError();
                ::RollbackTransaction(txn);
                ::CloseHandle(txn);
                RegUnLoadKeyW(HKEY_LOCAL_MACHINE, kMount);
                Err("CommitTransaction 失败：" + std::to_string(rc) + "（已回滚）");
                return 1;
            }
        }
        else
        {
            ::RollbackTransaction(txn);
            rolledBack = true;
        }
        ::CloseHandle(txn);
    }

    // 4) 落盘
    const LSTATUS flushRc = RegUnLoadKeyW(HKEY_LOCAL_MACHINE, kMount);
    if (flushRc != ERROR_SUCCESS)
    {
        Err("RegUnloadKey 失败：" + std::to_string(flushRc));
        return 1;
    }

    OutLn("written keys=" + std::to_string(keys) + " values=" + std::to_string(values) +
          " badLines=" + std::to_string(badLines) + " hive=" + ToUtf8(hivePath) +
          (txn != nullptr ? (rolledBack ? " transaction=rolled-back" : " transaction=committed")
                          : ""));
    return badLines == 0 ? 0 : 1;
#else
    (void)planPath; (void)hivePath;
    return 2;
#endif
}

// 把一段已解压的 `PcmH` 流拆成 TOC 里的表载荷，再按真机容器语义重编码。
// 用途：用真机样本自证 `ocid::PcmEncode` 能逐字节重建（cbs-pcm-reencode）。
// cbs-microdom <manifest.xml> <out.mdhd>：把清单 XML 编译成 MdHd（微 DOM）段。
// 用途：和真机 ManifestCache 里的 t11 表逐字节对拍（8061 小样本 2,988 B 已验证相同）。
static int CmdCbsMicrodom(const std::wstring& manifestPath, const std::wstring& outPath)
{
    std::wstring xml;
    std::wstring readError;
    if (!ReadFileUtf8(manifestPath, xml, readError))
    {
        OutLn("读不了清单：" + ToUtf8(readError));
        return 1;
    }
    std::vector<std::uint8_t> microdom;
    std::string buildError;
    if (!ocid::BuildMicrodomLayout(ToUtf8(xml), microdom, buildError))
    {
        OutLn("微 DOM 编译失败：" + buildError);
        return 1;
    }
    FILE* out = nullptr;
    if (_wfopen_s(&out, outPath.c_str(), L"wb") != 0 || out == nullptr)
    {
        OutLn("写不了文件：" + ToUtf8(outPath));
        return 1;
    }
    const bool written = microdom.empty() ||
                         std::fwrite(microdom.data(), 1, microdom.size(), out) == microdom.size();
    std::fclose(out);
    if (!written)
    {
        OutLn("写文件失败：" + ToUtf8(outPath));
        return 1;
    }
    OutLn("microdom bytes=" + std::to_string(microdom.size()));
    return 0;
}

// cbs-pcm-tables <dir>：对整个语料目录里的 *.pcm 做"通用表骨架"解码→按 canonical
// 规则重编码，统计每类型 byte-exact 命中率（真机 20,000 张表应为 100%，
// type6 的 bit0 是子变体位、由解码值原样带回，所以也应 100%）。
static int CmdCbsPcmTables(const std::wstring& dirPath)
{
#ifdef _WIN32
    std::map<std::uint8_t, long long> totals;  // type → 表数
    std::map<std::uint8_t, long long> exact;   // type → byte-exact 表数
    WIN32_FIND_DATAW found{};
    HANDLE find = FindFirstFileW((dirPath + L"\\*.pcm").c_str(), &found);
    if (find == INVALID_HANDLE_VALUE)
    {
        OutLn("目录里没有 .pcm：" + ToUtf8(dirPath));
        return 1;
    }
    long long files = 0;
    do
    {
        if ((found.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) continue;
        ++files;
        std::vector<std::uint8_t> pcm;
        {
            FILE* in = nullptr;
            const std::wstring path = dirPath + L"\\" + found.cFileName;
            if (_wfopen_s(&in, path.c_str(), L"rb") != 0 || in == nullptr) continue;
            std::fseek(in, 0, SEEK_END);
            const long size = std::ftell(in);
            std::fseek(in, 0, SEEK_SET);
            if (size > 0)
            {
                pcm.resize(static_cast<std::size_t>(size));
                if (std::fread(pcm.data(), 1, pcm.size(), in) != pcm.size()) pcm.clear();
            }
            std::fclose(in);
        }
        std::uint32_t tableCount = 0, tocOffset = 0;
        std::string error;
        if (!ocid::PcmReadHeader(pcm, tableCount, tocOffset, error)) continue;
        const auto readU32 = [&pcm](std::size_t offset) {
            return static_cast<std::uint32_t>(pcm[offset]) |
                   (static_cast<std::uint32_t>(pcm[offset + 1]) << 8) |
                   (static_cast<std::uint32_t>(pcm[offset + 2]) << 16) |
                   (static_cast<std::uint32_t>(pcm[offset + 3]) << 24);
        };
        for (std::uint32_t i = 0; i < tableCount; ++i)
        {
            const std::uint32_t header = readU32(tocOffset + 8ull * i);
            const std::uint32_t offset = readU32(tocOffset + 8ull * i + 4);
            ocid::PcmTable table;
            table.type = static_cast<std::uint8_t>(header >> 24);
            const std::size_t length = header & 0xFFFFFFu;
            if (offset + length > pcm.size()) continue;
            table.payload.assign(pcm.begin() + offset, pcm.begin() + offset + length);
            if (table.type != 6 && table.type != 8 && table.type != 9 && table.type != 0xA &&
                table.type != 0xB)
            {
                continue;
            }
            ocid::PcmRecordTable record;
            if (!ocid::PcmDecodeRecordTable(table, record, error)) continue;
            ++totals[table.type];
            const std::vector<std::uint8_t> reencoded = ocid::PcmEncodeRecordTable(record, error);
            if (reencoded == table.payload) ++exact[table.type];
        }
    } while (FindNextFileW(find, &found) != 0);
    FindClose(find);
    OutLn("files=" + std::to_string(files));
    for (const auto& entry : totals)
    {
        OutLn("type " + std::to_string(entry.first) + ": total=" + std::to_string(entry.second) +
              " byteexact=" + std::to_string(exact[entry.first]));
    }
    return 0;
#else
    (void)dirPath;
    return 1;
#endif
}
static int CmdCbsPcmReencode(const std::wstring& pcmPath, const std::wstring& outPath)
{
    std::vector<std::uint8_t> pcm;
    {
        FILE* in = nullptr;
        if (_wfopen_s(&in, pcmPath.c_str(), L"rb") != 0 || in == nullptr)
        {
            OutLn("打不开文件：" + ToUtf8(pcmPath));
            return 1;
        }
        std::fseek(in, 0, SEEK_END);
        const long size = std::ftell(in);
        std::fseek(in, 0, SEEK_SET);
        if (size < 0)
        {
            std::fclose(in);
            OutLn("取文件大小失败：" + ToUtf8(pcmPath));
            return 1;
        }
        pcm.resize(static_cast<std::size_t>(size));
        const std::size_t read = pcm.empty() ? 0 : std::fread(pcm.data(), 1, pcm.size(), in);
        std::fclose(in);
        if (read != pcm.size())
        {
            OutLn("读文件失败：" + ToUtf8(pcmPath));
            return 1;
        }
    }
    std::uint32_t tableCount = 0;
    std::uint32_t tocOffset = 0;
    std::string error;
    if (!ocid::PcmReadHeader(pcm, tableCount, tocOffset, error))
    {
        OutLn("PcmH 头解析失败：" + error);
        return 1;
    }

    const auto readU32 = [&pcm](std::size_t offset) {
        return static_cast<std::uint32_t>(pcm[offset]) |
               (static_cast<std::uint32_t>(pcm[offset + 1]) << 8) |
               (static_cast<std::uint32_t>(pcm[offset + 2]) << 16) |
               (static_cast<std::uint32_t>(pcm[offset + 3]) << 24);
    };

    std::vector<ocid::PcmTable> tables;
    tables.reserve(tableCount);
    for (std::uint32_t i = 0; i < tableCount; ++i)
    {
        const std::uint32_t header = readU32(tocOffset + 8ull * i);
        const std::uint32_t offset = readU32(tocOffset + 8ull * i + 4);
        const std::uint32_t length = header & 0xFFFFFFu;
        if (offset > pcm.size() || length > pcm.size() - offset)
        {
            char buffer[64] = {};
            std::snprintf(buffer, sizeof(buffer), "TOC 项 %u 越界", i);
            OutLn(buffer);
            return 1;
        }
        ocid::PcmTable table;
        table.type = static_cast<std::uint8_t>(header >> 24);
        table.payload.assign(pcm.begin() + offset, pcm.begin() + offset + length);
        tables.push_back(std::move(table));
    }

    const std::vector<std::uint8_t> headerPrefix(pcm.begin(), pcm.begin() + tocOffset);
    const std::vector<std::uint8_t> encoded = ocid::PcmEncode(headerPrefix, tables, error);
    if (encoded.empty())
    {
        OutLn("重编码失败：" + error);
        return 1;
    }

    const bool identical = encoded == pcm;
    FILE* stream = nullptr;
    if (_wfopen_s(&stream, outPath.c_str(), L"wb") != 0 || stream == nullptr)
    {
        OutLn("写文件失败：" + ToUtf8(outPath));
        return 1;
    }
    std::fwrite(encoded.data(), 1, encoded.size(), stream);
    std::fclose(stream);
    char summary[160] = {};
    std::snprintf(summary, sizeof(summary),
                  "tables=%u toc=0x%X original=%zu reencoded=%zu identical=%s",
                  tableCount, tocOffset, pcm.size(), encoded.size(),
                  identical ? "yes" : "no");
    OutLn(summary);
    return identical ? 0 : 2;
}

// 16299+ 的组件清单是 DCM 压缩容器（"DCM\x01" + msdelta PA30 补丁）。
// 这个命令把一份 DCM 清单解回原始 XML，方便取证；日常路径里 ReadFileUtf8 会透明解码。
static int CmdCbsDcmDecode(const std::wstring& inPath, const std::wstring& outPath)
{
#ifdef _WIN32
    if (g_manifestDictionary.empty()) {
        Err("需要 --dcm-dictionary <wcp.dll>");
        return 2;
    }
    FILE* f = _wfopen(inPath.c_str(), L"rb");
    if (!f) { Err("打不开文件：" + ToUtf8(inPath)); return 2; }
    fseek(f, 0, SEEK_END);
    long n = ftell(f);
    fseek(f, 0, SEEK_SET);
    std::vector<std::uint8_t> input((size_t)(n > 0 ? n : 0));
    if (n > 0) fread(input.data(), 1, (size_t)n, f);
    fclose(f);

    std::vector<std::uint8_t> output;
    std::string error;
    if (!DcmDecodeManifest(input, g_manifestDictionary, output, error)) {
        Err("解压失败：" + error);
        return 1;
    }
    FILE* o = _wfopen(outPath.c_str(), L"wb");
    if (!o) { Err("写不了文件：" + ToUtf8(outPath)); return 2; }
    if (!output.empty()) fwrite(output.data(), 1, output.size(), o);
    fclose(o);
    OutLn("compressed=" + std::to_string(input.size()) + " decompressed=" + std::to_string(output.size()));
    return 0;
#else
    (void)inPath; (void)outPath;
    Err("仅支持 Windows");
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
    OutLn("  ocid_manifest cbs-plan-components <manifestDir> <outFile> [--staged <winSxsDir>] [--limit N]");
    OutLn("  ocid_manifest cbs-plan-deployments <packagesDir> <manifestDir> <outFile> [--markers <csv>]");
    OutLn("      [--installed <csv>] [--claims <csv>] [--flags <csv>] [--sessions <dir>]");
    OutLn("      --claims: 组件键|部署键（c! 归属，安装计划输入）；--flags: 组件键|CF 十进制");
    OutLn("      --sessions: Windows\\servicing\\Sessions（按 <Resolve> 顺序决定 CatalogThumbprint）");
    OutLn("  ocid_manifest cbs-plan-versioned <manifestDir> <packagesDir> <stagedDir> <stackVersion> <outFile>");
    OutLn("      [--buckets <csv>] [--cache <csv>] [--ct <csv>] [--version-flags <csv>] [--mcp <csv>]");
    OutLn("      [--skeleton-families <csv>]  家庭键列表：只发家庭键/空版本节点/SomeUnparsedVersionsExist");
    OutLn("      --buckets: 家庭键|桶文本（安装历史：被更早服务栈暂存的组件留在那个桶）");
    OutLn("      --cache: 桶文本|缓存 id 8hex|已用字节（ManifestCacheIdentifier/UtilizedSpace）");
    OutLn("  ocid_manifest cbs-plan-root <x86|arm|arm64|amd64|ia64> <stackVersion> <outFile>");
    OutLn("      [--pending <32hex>] [--time <timestamp 文本>]  根键值（会话态可显式给）");
    OutLn("      [--minimal] [--ccpinterface] [--installers-shortcut]  根键形态开关");
    OutLn("      --markers: key|appid|thumb|markers（真机导出的安装/启用状态，决定 p!/s!/i!）");
    OutLn("  ocid_manifest cbs-diff <a.txt> <b.txt> [--limit N]   compare two cbs-dump files");
    OutLn("  ocid_manifest cbs-dcm-decode <inDcm> <outXml> --dcm-dictionary <wcp.dll>");
    OutLn("  ocid_manifest cbs-verify-files <plan.txt> <winSxsDir>   check every f! value has its file staged");
    OutLn("  ocid_manifest cbs-verify-store <hiveSpec>   self-consistency of a COMPONENTS hive (no reference needed)");
    OutLn("  ocid_manifest cbs-write <plan.txt> <hivePath> [--transactional]   rebuild a hive file from a cbs-dump text");
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

// ── cbs-plan-components：从 WinSxS 清单生成 DerivedData\Components 计划 ──────
//
// “逐字节一致”的验收方式：先只复刻**能由清单文件确定性算出**的那部分，再用
// `cbs-dump` 的真机导出做 diff。计划行格式与 cbs-dump 相同：
//   K\tDerivedData\Components\<组件键>
//   V\t…\<键>\tidentity\t3\t<hex ASCII 身份文本>
//   V\t…\<键>\tS256H\t3\t<hex SHA-256（解压后清单 XML 整份字节）>
//   V\t…\<键>\tf!<文件名>\t3\t<hex UTF-16LE 原始大小写文件名>
// 还差的部分（c! / CF / 部署记录 / 版本索引）会以“真机有、计划无”的形式出现，
// 正好就是待办清单。

namespace {

#ifdef _WIN32
bool ReadFileBytes(const std::wstring& path,
                   std::vector<std::uint8_t>& out,
                   std::wstring& err)
{
    HANDLE handle = CreateFileW(path.c_str(), GENERIC_READ,
                                FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                                OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (handle == INVALID_HANDLE_VALUE)
    {
        err = L"打不开文件：" + path;
        return false;
    }
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(handle, &size))
    {
        CloseHandle(handle);
        err = L"取文件大小失败：" + path;
        return false;
    }
    out.resize(static_cast<std::size_t>(size.QuadPart));
    std::size_t done = 0;
    while (done < out.size())
    {
        DWORD read = 0;
        const DWORD want = static_cast<DWORD>(
            std::min<std::size_t>(out.size() - done, 1u << 20));
        if (!ReadFile(handle, out.data() + done, want, &read, nullptr) || read == 0)
        {
            CloseHandle(handle);
            err = L"读文件失败：" + path;
            return false;
        }
        done += read;
    }
    CloseHandle(handle);
    return true;
}
#endif

// UTF-16LE 字节的十六进制（小端：低字节先）。
std::string Utf16LeHex(const std::wstring& text)
{
    static const char* digits = "0123456789abcdef";
    std::string out;
    out.reserve(text.size() * 4);
    for (wchar_t ch : text)
    {
        const std::uint16_t unit = static_cast<std::uint16_t>(ch);
        out.push_back(digits[(unit >> 4) & 0xF]);
        out.push_back(digits[unit & 0xF]);
        out.push_back(digits[(unit >> 12) & 0xF]);
        out.push_back(digits[(unit >> 8) & 0xF]);
    }
    return out;
}

// 清单的 <assemblyIdentity> → 真机 identity 值的文本形态（属性顺序固定）：
//   name, Culture=…, [Type=…,] Version=…, PublicKeyToken=…, ProcessorArchitecture=…[, versionScope=NonSxS]
// 真机样例： `Microsoft-Windows-ServicingStack, Culture=neutral, Version=6.2.8061.0,
//            PublicKeyToken=31bf3856ad364e35, ProcessorArchitecture=x86, versionScope=NonSxS`
std::string IdentityTextFromManifest(const AssemblyIdentity& identity)
{
    std::string out = ToUtf8(identity.Name());
    auto add = [&out](const char* key, const std::string& value) {
        out += ", ";
        out += key;
        out += "=";
        out += value;
    };
    std::string language = ToUtf8(identity.Language());
    // 真机 13,325 条 identity 值逐条对拍得到的规则：
    //   * 清单 `language=""` 或 `language="*"`（.NET 中性程序集）→ `neutral`；
    //   * 英语文化（`en-*`，大小写任意）**照抄清单**——台账里两种写法都真实存在：
    //     `en-US`（x86 驱动语言包/资源组件）与 `en-us`（msil 组件）；
    //   * 其他文化一律小写——`cs-CZ`→`cs-cz`、`sr-Latn-CS`→`sr-latn-cs`、
    //     `qps-ploc` 保持原样。
    if (language.empty() || language == "*")
        language = "neutral";
    else
    {
        const bool english = language.size() >= 3 &&
                             (language[0] == 'e' || language[0] == 'E') &&
                             (language[1] == 'n' || language[1] == 'N') && language[2] == '-';
        if (!english)
            for (char& c : language)
                if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    add("Culture", language);
    const std::string type = ToUtf8(identity.Type());
    if (!type.empty()) add("Type", type);
    add("Version", ToUtf8(identity.Version()));
    add("PublicKeyToken", ToUtf8(identity.PublicKeyToken()));
    const std::string architecture = ToUtf8(identity.Architecture());
    if (!architecture.empty()) add("ProcessorArchitecture", architecture);
    std::string scope = ToUtf8(identity.ScopeString());
    if (!scope.empty())
    {
        std::string lower = scope;
        for (char& c : lower)
            if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
        add("versionScope", lower == "nonsxs" ? "NonSxS" : scope);
    }
    return out;
}

}  // namespace

static int CmdCbsPlanComponents(const std::wstring& manifestDir,
                                const std::wstring& outPath,
                                const std::wstring& winSxsDir,
                                int limit)
{
#ifdef _WIN32
    WIN32_FIND_DATAW found{};
    const std::wstring pattern = manifestDir + L"\\*.manifest";
    HANDLE find = FindFirstFileW(pattern.c_str(), &found);
    if (find == INVALID_HANDLE_VALUE)
    {
        Err("目录里没有 .manifest：" + ToUtf8(manifestDir));
        return 2;
    }

    std::vector<std::string> lines;
    std::vector<std::wstring> skipped;
    long long manifests = 0, components = 0, staged = 0, files = 0, bad = 0, identityBad = 0;
    do
    {
        if ((found.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) continue;
        if (limit > 0 && manifests >= limit) break;
        const std::wstring path = manifestDir + L"\\" + found.cFileName;

        std::vector<std::uint8_t> bytes;
        std::wstring readErr;
        if (!ReadFileBytes(path, bytes, readErr))
        {
            ++bad;
            if (skipped.size() < 10) skipped.push_back(found.cFileName);
            continue;
        }
        std::wstring text;
        if (!ReadFileUtf8(path, text, readErr))
        {
            ++bad;
            if (skipped.size() < 10) skipped.push_back(found.cFileName);
            continue;
        }
        ComponentManifest manifest;
        const ManifestParseResult parsed = ParseComponentManifest(text, manifest);
        if (!parsed.ok)
        {
            ++bad;
            Err("清单解析失败 " + ToUtf8(found.cFileName) + "：" + ToUtf8(parsed.error));
            if (skipped.size() < 10) skipped.push_back(found.cFileName);
            continue;
        }
        ++manifests;

        const std::string identityText = IdentityTextFromManifest(manifest.identity);
        CbsIdentity identity;
        std::string parseErr;
        if (!CbsParseIdentity(identityText, identity, parseErr))
        {
            ++identityBad;
            Err("身份解析失败 " + ToUtf8(found.cFileName) + "：" + parseErr);
            continue;
        }

        // 组件键就是 WinSxS 里的组件目录名；目录存在 ⇒ 载荷已落位 ⇒ 才有 f! 值。
        const std::string key = CbsComponentKeyName(identity);
        bool isStaged = true;
        if (!winSxsDir.empty())
        {
            const std::wstring componentDir =
                winSxsDir + L"\\" + FromUtf8OrAnsi(key.c_str());
            const DWORD attrs = GetFileAttributesW(componentDir.c_str());
            isStaged = attrs != INVALID_FILE_ATTRIBUTES &&
                       (attrs & FILE_ATTRIBUTE_DIRECTORY) != 0;
        }
        if (isStaged) ++staged;

        const std::string keyPath = "DerivedData\\Components\\" + key;
        lines.push_back("K\t" + keyPath);

        std::string identityHex;
        HexAppend(std::vector<std::uint8_t>(identityText.begin(), identityText.end()),
                  identityHex);
        lines.push_back("V\t" + keyPath + "\tidentity\t3\t" + identityHex);

        // 清单本身是 DCM 压缩 ⇒ 现代镜像（16299+）：f! 值改成 REG_DWORD 状态位。
        const bool modernFileValues = !g_manifestDictionary.empty() && DcmIsCompressed(bytes);
        std::string shaHex;
        // 16299+ 的清单是 DCM 压缩（msdelta 补丁）；真机 S256H = 解压后清单 XML 的 SHA-256，
        // 8061 的清单本来就是明文，两种情形都由这里的"解码后再哈希"统一覆盖。
        std::vector<std::uint8_t> manifestHashBytes = bytes;
        if (!g_manifestDictionary.empty() && DcmIsCompressed(manifestHashBytes))
        {
            std::vector<std::uint8_t> decoded;
            std::string dcmErr;
            if (DcmDecodeManifest(manifestHashBytes, g_manifestDictionary, decoded, dcmErr))
            {
                manifestHashBytes.swap(decoded);
            }
            else
            {
                Err("DCM 解码失败（S256H 将退化）" + ToUtf8(found.cFileName) + "：" + dcmErr);
            }
        }
        HexAppend(Sha256(manifestHashBytes), shaHex);
        lines.push_back("V\t" + keyPath + "\tS256H\t3\t" + shaHex);

        if (!isStaged) continue;

        for (const FileSpec& file : manifest.files)
        {
            if (file.name.empty()) continue;
            // 值的名字是文件名的小写；值数据只在“名字无法由值名还原”时写
            // （即值名后缀 != 原始文件名）——真机 19,006 条 f! 值一条不差：
            //   c2wtshost.exe（本来就全小写）=> 数据为空
            //   Accessibility.dll / 超 25 字符被截断 => 数据是原始大小写的 UTF-16
            // 16299+（清单是 DCM 压缩）= 现代 f! 语义：值类型 4（REG_DWORD），
            // 数据是文件状态位（真机分布 1:29,725 / 0x21:3,521 / 0x41:1,038 / 0x61:1）；
            // 缺省写 1，非 1 的状态由 --file-flags 输入（安装历史）。
            // 8061/8419（明文清单）= 旧语义：值类型 3，数据是原始大小写文件名的 UTF-16LE。
            const std::string valueName = CbsFileValueName(file.name);
            if (modernFileValues)
            {
                const std::string flagKey = key + "|" + valueName;
                const auto flagIt = g_fileFlags.find(flagKey);
                const std::uint32_t flagValue =
                    flagIt != g_fileFlags.end() ? flagIt->second : 1u;
                lines.push_back("V\t" + keyPath + "\t" + valueName + "\t4\t" +
                                HexDword(flagValue));
                ++files;
                continue;
            }
            const std::string suffix =
                valueName.size() > 2 ? valueName.substr(2) : std::string();
            const std::string nameUtf8 = ToUtf8(file.name);
            lines.push_back("V\t" + keyPath + "\t" + valueName + "\t3\t" +
                            (suffix == nameUtf8 ? std::string() : Utf16LeHex(file.name)));
            ++files;
        }
        ++components;
    } while (FindNextFileW(find, &found) != 0);
    FindClose(find);

    std::sort(lines.begin(), lines.end());
    std::wstring writeErr;
    if (!WriteLinesUtf8(outPath, lines, writeErr))
    {
        Err(ToUtf8(writeErr));
        return 1;
    }

    for (const std::wstring& name : skipped) Err("跳过（读/解析失败）：" + ToUtf8(name));
    OutLn("manifests=" + std::to_string(manifests) + " staged=" + std::to_string(staged) +
          " componentsWithFiles=" + std::to_string(components) +
          " files=" + std::to_string(files) + " bad=" + std::to_string(bad) +
          " identityBad=" + std::to_string(identityBad));
    return 0;
#else
    (void)manifestDir; (void)outPath; (void)winSxsDir; (void)limit;
    return 2;
#endif
}

// 一个部署键的"计划"：同一个部署键可能被多个包声明，所以先收集再输出。
struct DeploymentPlan
{
    // 声明过本部署组件的包（按 .mum 文件名升序追加，即"声明顺序"）。
    // 为什么要留住全部候选：真机选哪个 .cat 当 CatalogThumbprint 既不是"最后声明"、
    // 也不是"已安装优先"，而是**安装会话里最后被 <Resolve> 的那个包**（见
    // ChooseDeploymentCatalog），所以选择必须推迟到读完会话日志之后做。
    struct Candidate
    {
        std::string packageId;   // 包身份（与 Sessions\*.xml 的 `<Resolve package="…">` 同口径）
        std::string catalogHex;  // 该包同名 .cat 的 SHA-256
        bool installed = false;  // --installed 的 CurrentState & 0x40
    };
    std::string appId;             // store 里组件的 identity 文本（真机 appid 就取这个）
    bool driver = false;           // appid 含 ", Type=driverUpdate" ⇒ 不写 catalog
    std::vector<Candidate> candidates;
    std::set<std::string> payloads;  // 已安装包声明的 `<包身份>.<update 名>`
};

// 语言包部署（身份文本里 Culture≠neutral）的候选范围与中性部署不同：
// 真机 en-US 的 MSMQ 语言包部署，CatalogThumbprint 指向的是**未安装**的
// home-premium 包（会话里它最后被 Resolve），qps-ploc 那一组三个候选全未安装、
// 也照样都留了 catalog 标记。所以这里按 culture 判断"是不是语言包部署"。
// 包身份的比较必须大小写无关：.mum 里的语言标签是小写（`language="qps-ploc"`），
// 而会话日志的 `<Resolve package="…">` 抄的是 CBS 的原样大写（`qps-PLOC`）。
// 按原样比较会让 qps-ploc 的语言包部署一条都匹配不上，悄悄退回"最后声明"规则
// （真机却选了会话里最后 Resolve 的那个），而 en-US 因为两边写法一致反而看不出问题。
static std::string AsciiLowerCopy(const std::string& text)
{
    std::string out = text;
    for (char& c : out)
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    return out;
}

static bool IsLanguageScopedIdentity(const std::string& identityText)
{
    const std::size_t at = identityText.find("Culture=");
    if (at == std::string::npos) return false;
    const std::size_t begin = at + 8;
    const std::size_t end = identityText.find(',', begin);
    const std::string culture =
        identityText.substr(begin, end == std::string::npos ? std::string::npos : end - begin);
    return !culture.empty() && culture != "neutral";
}

// 选 CatalogThumbprint。
//
// 有 --sessions 时：真机那次安装是谁最后被 `<Resolve>` 谁就落成 thumbprint。
//   * 语言包部署 → 全部声明候选里取 Resolve 序号最大者（同序取后声明）；
//   * 中性部署 → 只在**已安装**候选里挑：真机 MSMQ 的 9 条中性部署记录选的是已安装的
//     主体包，而不是名字更大的 home-basic/home-premium（未安装的派生包）。
// 会话里没出现过的部署（所有候选都查不到序号）退回老规则，保证不给 --sessions 时
// 与原行为逐字节一致：已安装候选里最后声明，没已安装候选就退回最后声明的那个。
static std::string ChooseDeploymentCatalog(const DeploymentPlan& planned,
                                           const std::map<std::string, long long>& resolveOrder)
{
    const bool languageScoped = IsLanguageScopedIdentity(planned.appId);
    if (!resolveOrder.empty())
    {
        long long bestSeq = -1;
        std::string bestCatalog;
        for (const DeploymentPlan::Candidate& candidate : planned.candidates)
        {
            if (!languageScoped && !candidate.installed) continue;
            const auto found = resolveOrder.find(AsciiLowerCopy(candidate.packageId));
            if (found == resolveOrder.end()) continue;
            // >= 而不是 >：候选按声明顺序排列，同序号时后声明的覆盖（真机同序取后声明）。
            if (found->second >= bestSeq)
            {
                bestSeq = found->second;
                bestCatalog = candidate.catalogHex;
            }
        }
        if (!bestCatalog.empty()) return bestCatalog;
    }
    std::string installedCatalog;
    std::string lastCatalog;
    for (const DeploymentPlan::Candidate& candidate : planned.candidates)
    {
        if (candidate.catalogHex.empty()) continue;
        lastCatalog = candidate.catalogHex;
        if (candidate.installed) installedCatalog = candidate.catalogHex;
    }
    return installedCatalog.empty() ? lastCatalog : installedCatalog;
}

// `CanonicalData\Catalogs\<sha>` 要挂哪些 `c!<部署键>` 标记。
//
// 真机的标记 = "参与过这次安装的 catalog"，不是只挑一个：
//   * 语言包部署 → 全部声明候选（en-US/qps-ploc 的 home-basic/home-premium 即使
//     未安装、甚至从没在会话里出现过，也照样有标记）；
//   * 其余部署 → 已安装候选；一个已安装候选都没有时退回选中的那一个
//     （真机这 749 个部署只有一个标记，与"最后声明"的旧行为一致）。
// 不给 --sessions 时保持旧语义：已安装候选逐个标记，否则只标退回的那个。
static std::set<std::string> ChooseCatalogMarks(const DeploymentPlan& planned,
                                                const std::string& chosenCatalog,
                                                bool useSessions)
{
    std::set<std::string> marks;
    if (useSessions)
    {
        const bool allCandidates = IsLanguageScopedIdentity(planned.appId);
        for (const DeploymentPlan::Candidate& candidate : planned.candidates)
        {
            if (candidate.catalogHex.empty()) continue;
            if (candidate.installed || allCandidates) marks.insert(candidate.catalogHex);
        }
    }
    else
    {
        for (const DeploymentPlan::Candidate& candidate : planned.candidates)
            if (candidate.installed && !candidate.catalogHex.empty())
                marks.insert(candidate.catalogHex);
    }
    if (marks.empty() && !chosenCatalog.empty()) marks.insert(chosenCatalog);
    return marks;
}

// ── cbs-plan-deployments：从包清单（.mum）生成 CanonicalData 计划 ────────────
//
// 真机 2,283 条部署记录的来源（实测 2,278/2,283，差的 5 条是 servicing stack 那批
// 名字大小写特殊的部署，图像里没有任何 .mum 声明它们）：
//   * `Windows\servicing\Packages\*.mum` 的每个 `<update>` 声明它安装哪些组件
//     （`<component><assemblyIdentity …/>` 或 `<driver><assemblyIdentity type="driverUpdate" …/>`）；
//   * 组件必须真的在 store 里（有清单）——按身份（名字/版本/token/架构/文化）匹配；
//   * 部署 identity 用 **store 里那份文本**（不是 .mum 里的写法），所以
//     `…-Base-Deployment` 会落成台账里的 `…-base-Deployment`；
//   * payload 身份 = `<包身份>.<update 名>`，包身份的 culture 段 neutral 时写空；
//   * 值：`appid`（裸 ASCII）、`CatalogThumbprint`（同包 `.cat` 的 SHA-256，REG_SZ 文本）、
//     `p!/s!/i!`（8 字节长度前缀 + ASCII payload）。`s!/i!` 是安装/启用状态，
//     由 `--markers` 输入（真机导出的 `key|appid|thumb|markers` 四列 CSV）。
static std::string MumAttr(const detail::XmlNode& node, const wchar_t* name)
{
    const std::wstring* value = node.FindAttr(name);
    return value ? ToUtf8(*value) : std::string();
}

// .mum 的 <assemblyIdentity> → 与清单同口径的 identity 文本。
static std::string MumIdentityText(const detail::XmlNode& identity)
{
    std::string out = MumAttr(identity, L"name");
    auto add = [&out](const char* key, const std::string& value) {
        out += ", ";
        out += key;
        out += "=";
        out += value;
    };
    std::string language = MumAttr(identity, L"language");
    if (language.empty() || language == "*")
        language = "neutral";
    else
    {
        const bool english = language.size() >= 3 &&
                             (language[0] == 'e' || language[0] == 'E') &&
                             (language[1] == 'n' || language[1] == 'N') && language[2] == '-';
        if (!english)
            for (char& c : language)
                if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    add("Culture", language);
    const std::string type = MumAttr(identity, L"type");
    if (!type.empty()) add("Type", type);
    add("Version", MumAttr(identity, L"version"));
    const std::string token = MumAttr(identity, L"publicKeyToken");
    if (!token.empty()) add("PublicKeyToken", token);
    const std::string architecture = MumAttr(identity, L"processorArchitecture");
    if (!architecture.empty()) add("ProcessorArchitecture", architecture);
    std::string scope = MumAttr(identity, L"versionScope");
    if (!scope.empty())
    {
        std::string lower = scope;
        for (char& c : lower)
            if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
        add("versionScope", lower == "nonsxs" ? "NonSxS" : scope);
    }
    return out;
}

static void CollectXmlByLocalName(const detail::XmlNode& node,
                                  const std::wstring& local,
                                  std::vector<const detail::XmlNode*>& out)
{
    detail::CollectByLocalName(node, local.c_str(), out);
}

static int CmdCbsPlanDeployments(const std::wstring& packagesDir,
                                 const std::wstring& manifestDir,
                                 const std::wstring& markersPath,
                                 const std::wstring& installedPath,
                                 const std::wstring& claimsPath,
                                 const std::wstring& flagsPath,
                                 const std::wstring& sessionsDir,
                                 const std::wstring& outPath)
{
#ifdef _WIN32
    long long manifests = 0, badManifest = 0;
    std::map<std::string, std::string> store;
    {
        WIN32_FIND_DATAW found{};
        const std::wstring pattern = manifestDir + L"\\*.manifest";
        HANDLE find = FindFirstFileW(pattern.c_str(), &found);
        if (find == INVALID_HANDLE_VALUE)
        {
            Err("清单目录里没有 .manifest：" + ToUtf8(manifestDir));
            return 2;
        }
        do
        {
            if ((found.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) continue;
            std::wstring text, readErr;
            if (!ReadFileUtf8(manifestDir + L"\\" + found.cFileName, text, readErr))
            {
                ++badManifest;
                continue;
            }
            ComponentManifest manifest;
            if (!ParseComponentManifest(text, manifest).ok)
            {
                ++badManifest;
                continue;
            }
            const std::string identityText = IdentityTextFromManifest(manifest.identity);
            CbsIdentity identity;
            std::string parseErr;
            if (!CbsParseIdentity(identityText, identity, parseErr))
            {
                ++badManifest;
                continue;
            }
            store[CbsComponentKeyName(identity)] = identityText;
            ++manifests;
        } while (FindNextFileW(find, &found) != 0);
        FindClose(find);
    }

    // 安装/启用状态（真机导出：key|appid|thumb|markers；markers 含 p/s/i 字母）
    std::map<std::string, std::string> markers;
    if (!markersPath.empty())
    {
        std::wstring raw, readErr;
        if (!ReadFileUtf8(markersPath, raw, readErr))
        {
            Err(ToUtf8(readErr));
            return 1;
        }
        const std::string text = ToUtf8(raw);
        std::size_t pos = 0;
        bool first = true;
        while (pos < text.size())
        {
            std::size_t end = text.find('\n', pos);
            if (end == std::string::npos) end = text.size();
            std::string line = text.substr(pos, end - pos);
            pos = end + 1;
            if (!line.empty() && line.back() == '\r') line.pop_back();
            if (first) { first = false; if (line.rfind("key|", 0) == 0) continue; }
            std::vector<std::string> fields;
            std::size_t start = 0;
            while (start <= line.size())
            {
                std::size_t bar = line.find('|', start);
                if (bar == std::string::npos)
                {
                    fields.push_back(line.substr(start));
                    break;
                }
                fields.push_back(line.substr(start, bar - start));
                start = bar + 1;
            }
            // appid 大小写可能与我们拼出来的身份文本不同（真机有 8 条大小写异常：
            // servicing stack 的 base/admin/ds/net/termsrv 部署与 pseudotool），
            // 键统一小写存放、查询也小写，否则整个部署会丢掉 p!/s!/i! 与 payload。
            if (fields.size() >= 4) markers[AsciiLowerCopy(fields[1])] = fields[3];
        }
    }

    // 已安装包过滤（可选）：真机 SOFTWARE hive 的
    // `Microsoft\Windows\CurrentVersion\Component Based Servicing\Packages\<包键>` 的
    // `CurrentState`，bit 0x40 = 已安装。只有已安装包的 update 会留下部署记录——
    // 不过滤的话，像 MSMQ 的 home-basic/home-premium 这类"未安装的派生包"会往同一个
    // 部署记录里多写一整套 p!/s!/i!（真机只有已安装那个包的那套）。
    std::set<std::string> installedPackages;
    if (!installedPath.empty())
    {
        std::wstring raw, readErr;
        if (!ReadFileUtf8(installedPath, raw, readErr))
        {
            Err(ToUtf8(readErr));
            return 1;
        }
        const std::string text = ToUtf8(raw);
        std::size_t pos = 0;
        while (pos < text.size())
        {
            std::size_t end = text.find('\n', pos);
            if (end == std::string::npos) end = text.size();
            std::string line = text.substr(pos, end - pos);
            pos = end + 1;
            if (!line.empty() && line.back() == '\r') line.pop_back();
            if (line.empty()) continue;
            const std::size_t bar = line.find('|');
            const std::string key = line.substr(0, bar);
            bool installed = true;
            if (bar != std::string::npos)
            {
                const std::string stateText = line.substr(bar + 1);
                char* stop = nullptr;
                const unsigned long long state =
                    std::strtoull(stateText.c_str(), &stop, 0);
                installed = stop != stateText.c_str() && (state & 0x40ULL) != 0;
            }
            if (installed) installedPackages.insert(key);
        }
        if (installedPackages.empty())
        {
            Err("--installed 里没有任何已安装包（CurrentState bit 0x40）");
            return 1;
        }
    }

    std::set<std::string> lines;
    lines.insert("K\tCanonicalData");
    lines.insert("K\tCanonicalData\\Catalogs");
    lines.insert("K\tCanonicalData\\Deployments");

    // --flags 预读：部署组件默认要写 CF=12（清单在|文件在），但真机有 7 个部署组
    // 件的闭包状态是 4（只有清单在位，例如 NetFx2/NetFx3/WPF 的部署组件）。
    // 既然 --flags 是安装态输入，就让它压过这里的默认值：先读进来，后面部署
    // 组件写 CF=12 时跳过被 --flags 覆盖的键，最后再按 --flags 逐条写。
    std::map<std::string, std::string> flagOverrides;
    if (!flagsPath.empty())
    {
        std::wstring rawText;
        std::wstring readError;
        if (!ReadFileUtf8(flagsPath, rawText, readError))
        {
            Err("读不了 --flags：" + ToUtf8(readError));
            return 2;
        }
        const std::string utf8Text = ToUtf8(rawText);
        std::size_t cursor = 0;
        while (cursor < utf8Text.size())
        {
            std::size_t end = utf8Text.find('\n', cursor);
            if (end == std::string::npos) end = utf8Text.size();
            std::string line = utf8Text.substr(cursor, end - cursor);
            cursor = end + 1;
            if (!line.empty() && line.back() == '\r') line.pop_back();
            const std::size_t bar = line.find('|');
            if (bar == std::string::npos) continue;
            flagOverrides[line.substr(0, bar)] = line.substr(bar + 1);
        }
    }

    // ── 安装会话日志（--sessions）────────────────────────────────────────────
    // 真机那次安装到底选中了哪个包的 .cat，取决于 `Windows\servicing\Sessions` 里
    // `<Resolve package="…">` 的**出现顺序**：同一个组件被多个包声明时，最后被
    // Resolve 的包才留下 CatalogThumbprint（实测 1,694/1,703，而"最后声明"只有
    // 1,671/1,703）。所以要建"包身份 → 最后出现序号"的表。
    //
    // 目录里的坑（8061 x86 实测）：
    //   * 11 对 `<id>.xml` / `<id>.back.xml`：.back 是上一版备份，要跳过；
    //   * 还有聚合文件 `Sessions.xml`（根是 <Sessions>，内含全部 11 个会话的副本），
    //     不跳过就会把每个 Resolve 数两遍 ⇒ 按根元素本地名过滤，只认 <Session>；
    //   * 文件名形如 `2012_510515`（<年>_<序号>），按名字升序排就是时间序。
    std::map<std::string, long long> resolveOrder;
    long long resolves = 0;
    if (!sessionsDir.empty())
    {
        std::vector<std::wstring> sessionFiles;
        {
            WIN32_FIND_DATAW sessionFound{};
            HANDLE sessionFind = FindFirstFileW((sessionsDir + L"\\*.xml").c_str(), &sessionFound);
            if (sessionFind == INVALID_HANDLE_VALUE)
            {
                Err("会话目录里没有 .xml：" + ToUtf8(sessionsDir));
                return 2;
            }
            do
            {
                if ((sessionFound.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) continue;
                const std::wstring name = sessionFound.cFileName;
                const std::wstring suffix = L".back.xml";
                if (name.size() > suffix.size() &&
                    _wcsicmp(name.c_str() + name.size() - suffix.size(), suffix.c_str()) == 0)
                    continue;
                sessionFiles.push_back(name);
            } while (FindNextFileW(sessionFind, &sessionFound) != 0);
            FindClose(sessionFind);
        }
        std::sort(sessionFiles.begin(), sessionFiles.end());
        for (const std::wstring& name : sessionFiles)
        {
            std::wstring text, readErr;
            if (!ReadFileUtf8(sessionsDir + L"\\" + name, text, readErr)) continue;
            detail::XmlParser parser(text);
            detail::XmlNode root;
            if (!parser.ParseDocument(root)) continue;
            if (_wcsicmp(root.LocalName().c_str(), L"Session") != 0) continue;  // 聚合文件
            std::vector<const detail::XmlNode*> resolveNodes;
            CollectXmlByLocalName(root, L"Resolve", resolveNodes);
            for (const detail::XmlNode* node : resolveNodes)
            {
                ++resolves;
                const std::string package = MumAttr(*node, L"package");
                if (package.empty()) continue;
                // 后出现直接覆盖 ⇒ 表里存的就是"最后出现序号"。
                // 键统一小写，见 AsciiLowerCopy 的注释（.mum 与 Session 的语言标签大小写不同）。
                resolveOrder[AsciiLowerCopy(package)] = resolves;
            }
        }
        if (resolveOrder.empty())
        {
            // 现代镜像常常没有任何会话记录（镜像里 Windows\servicing\Sessions 为空）：
            // 这不是错误，退回"按 .mum 声明集合选 catalog"的老规则。
            Err("提示：--sessions 目录里没有 <Resolve>，按无会话数据继续：" + ToUtf8(sessionsDir));
            resolveOrder.clear();
        }
    }
    long long packages = 0, updates = 0, declared = 0, resolved = 0, payloads = 0, noCat = 0;
    long long deployments = 0;
    const bool useSessions = !resolveOrder.empty();
    std::set<std::string> deploymentKeys;
    std::map<std::string, DeploymentPlan> plan;
    long long flagDeployments = 0;  // 由 --flags 的 CF=12 补出来的部署记录条数
    std::wstring currentMum;

    WIN32_FIND_DATAW found{};
    FILE* candidateLog = nullptr;
    {
        wchar_t debugPath[512] = {};
        if (GetEnvironmentVariableW(L"OCID_PLAN_CANDIDATES", debugPath, 512) > 0)
            _wfopen_s(&candidateLog, debugPath, L"wb");
    }
    HANDLE find = FindFirstFileW((packagesDir + L"\\*.mum").c_str(), &found);
    if (find == INVALID_HANDLE_VALUE)
    {
        Err("包目录里没有 .mum：" + ToUtf8(packagesDir));
        return 2;
    }
    try
    {
    do
    {
        if ((found.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) continue;
        ++packages;
        const std::wstring mumPath = packagesDir + L"\\" + found.cFileName;
        currentMum = found.cFileName;
        std::wstring xml, readErr;
        if (!ReadFileUtf8(mumPath, xml, readErr)) continue;

        detail::XmlParser parser(xml);
        detail::XmlNode root;
        if (!parser.ParseDocument(root)) continue;

        std::vector<const detail::XmlNode*> identities;
        CollectXmlByLocalName(root, L"assemblyIdentity", identities);
        if (identities.empty()) continue;
        const detail::XmlNode& packageIdentity = *identities.front();

        std::string packageId = MumAttr(packageIdentity, L"name");
        packageId += "~";
        packageId += MumAttr(packageIdentity, L"publicKeyToken");
        packageId += "~";
        packageId += MumAttr(packageIdentity, L"processorArchitecture");
        packageId += "~";
        {
            std::string language = MumAttr(packageIdentity, L"language");
            if (language == "neutral" || language == "*") language.clear();
            packageId += language;
        }
        packageId += "~";
        packageId += MumAttr(packageIdentity, L"version");
        // 注意：不在这里跳过"未安装包"——它们的部署记录（appid / CatalogThumbprint）
        // 在真机台账里照样存在（864 个包对应 2,283 条部署，远多于 502 个已安装包）；
        // "已安装"只用于决定 payload 与 catalog 优先取哪一套。

        // 同包同名 .cat 的 SHA-256（CatalogThumbprint / Catalogs 键）
        std::string catalogHex;
        {
            std::wstring stem = mumPath;
            const std::wstring suffix = L".mum";
            if (stem.size() > suffix.size() &&
                _wcsicmp(stem.c_str() + stem.size() - suffix.size(), suffix.c_str()) == 0)
                stem.resize(stem.size() - suffix.size());
            std::vector<std::uint8_t> catBytes;
            std::wstring catErr;
            if (ReadFileBytes(stem + L".cat", catBytes, catErr))
                catalogHex = Sha256Hex(catBytes);
        }
        if (catalogHex.empty()) ++noCat;

        std::vector<const detail::XmlNode*> updateNodes;
        CollectXmlByLocalName(root, L"update", updateNodes);
        for (const detail::XmlNode* update : updateNodes)
        {
            ++updates;
            const std::string updateName = MumAttr(*update, L"name");
            if (updateName.empty()) continue;

            std::vector<const detail::XmlNode*> declaredNodes;
            CollectXmlByLocalName(*update, L"component", declaredNodes);
            CollectXmlByLocalName(*update, L"driver", declaredNodes);

            for (const detail::XmlNode* node : declaredNodes)
            {
                std::vector<const detail::XmlNode*> ids;
                CollectXmlByLocalName(*node, L"assemblyIdentity", ids);
                if (ids.empty()) continue;
                ++declared;
                const std::string declaredText = MumIdentityText(*ids.front());

                CbsIdentity declaredIdentity;
                std::string parseErr;
                if (!CbsParseIdentity(declaredText, declaredIdentity, parseErr)) continue;
                const std::string componentKey = CbsComponentKeyName(declaredIdentity);
                const auto stored = store.find(componentKey);
                if (stored == store.end()) continue;
                ++resolved;

                CbsIdentity storeIdentity;
                if (!CbsParseIdentity(stored->second, storeIdentity, parseErr)) continue;
                const std::string deploymentKey = CbsDeploymentKeyName(storeIdentity);
                deploymentKeys.insert(deploymentKey);
                const bool packageInstalled = installedPackages.empty() ||
                                              installedPackages.count(packageId) != 0;
                DeploymentPlan& planned = plan[deploymentKey];
                planned.appId = stored->second;
                planned.driver = stored->second.find(", Type=driverUpdate") != std::string::npos;
                if (!catalogHex.empty())
                {
                    // 同一个部署键可能被多个包声明（MSMQ 的 home-basic/home-premium/主体
                    // 三个包都声明同一个 -Deployment 组件）。真机选谁当 CatalogThumbprint
                    // 由安装会话顺序决定（见 ChooseDeploymentCatalog），所以这里只把候选
                    // 收集起来，等读完会话日志、输出时再定。
                    planned.candidates.push_back(
                        DeploymentPlan::Candidate{packageId, catalogHex, packageInstalled});
                }
                if (packageInstalled)
                {
                    // payload 身份 = `<包身份>.<update 名>`；只有已安装包的那套写进台账。
                    planned.payloads.insert(packageId + "." + updateName);
                }
                if (candidateLog != nullptr)
                {
                    // 诊断用：每次"某包声明了某部署"都留一行，便于反推 catalog 选择规则。
                    std::fprintf(candidateLog, "%s|%s|%d|%s|%s\n",
                                 deploymentKey.c_str(), packageId.c_str(),
                                 packageInstalled ? 1 : 0, catalogHex.c_str(),
                                 updateName.c_str());
                }
            }
        }
    } while (FindNextFileW(find, &found) != 0);
    FindClose(find);
    if (candidateLog != nullptr) std::fclose(candidateLog);

    // 部署记录的第二个来源：`--flags` 里 CF=12 的组件。
    //
    // 真机有 5 条部署记录（`Microsoft-Windows-ServicingStack-{base,admin,ds,net,termsrv}
    // -Deployment`）在 864 个 .mum 里一条声明都没有（Foundation-Package.mum 只声明
    // `Microsoft-Windows-Foundation`，不声明这些），但它们在真机 `DerivedData\Components`
    // 里是 CF=12（清单在 | 文件在）。所以按"CF=12 的组件如果不是任何 .mum 声明的部署，
    // 就补一条只有 appid 的部署记录"来复刻——纯清单/flags 驱动，不读真机台账。
    //
    // 如实记录缺口：8061 图像里符合条件的只有 ds 一条（它同时有 .manifest、有 flags 行）；
    // 另外 4 条在清单目录和 --flags 里都完全不存在，从镜像无从重建；而且 Foundation-
    // Package.mum 没有声明这些组件，"按组件键反查声明包"也拿不到 payload，只能只发 appid。
    std::set<std::string> flagDerivedKeys;
    for (const auto& flagEntry : flagOverrides)
    {
        const long long closure = std::strtoll(flagEntry.second.c_str(), nullptr, 10);
        if (closure != 12) continue;
        const auto stored = store.find(flagEntry.first);
        if (stored == store.end()) continue;
        CbsIdentity flagIdentity;
        std::string flagParseErr;
        if (!CbsParseIdentity(stored->second, flagIdentity, flagParseErr)) continue;
        const std::string flagDeploymentKey = CbsDeploymentKeyName(flagIdentity);
        if (plan.count(flagDeploymentKey) != 0) continue;  // .mum 已经产出的不重复
        DeploymentPlan& planned = plan[flagDeploymentKey];
        planned.appId = stored->second;
        planned.driver = stored->second.find(", Type=driverUpdate") != std::string::npos;
        deploymentKeys.insert(flagDeploymentKey);
        flagDerivedKeys.insert(flagDeploymentKey);
        ++flagDeployments;
    }

    // 上述"只在清单里、没有 .mum 声明"的部署组件（真机 x86 5 条、ARM 11 条 servicing stack
    // 部署）在真机台账里带的是同一个 payload 集合 —— Windows Foundation 包的
    // `WindowsFoundationDelivery` 更新。这两个镜像是用同一套基础交付装出来的，所以
    // 它们被那次交付一并落位。纯 .mum 反查不到（Foundation-Package.mum 只声明
    // Microsoft-Windows-Foundation 一个组件），因此按"继承了 Foundation 交付"实现：
    // 找到 appid 名为 Microsoft-Windows-Foundation 的部署，把它的 payload 集合复制给
    // 这些部署。若无该部署则保持空（不猜）。
    if (!flagDerivedKeys.empty())
    {
        std::set<std::string> foundationPayloads;
        for (const auto& entry : plan)
        {
            CbsIdentity candidateIdentity;
            std::string candidateErr;
            if (!CbsParseIdentity(entry.second.appId, candidateIdentity, candidateErr)) continue;
            if (candidateIdentity.name != "Microsoft-Windows-Foundation") continue;
            foundationPayloads = entry.second.payloads;
            break;
        }
        if (!foundationPayloads.empty())
        {
            for (const std::string& key : flagDerivedKeys)
            {
                plan[key].payloads = foundationPayloads;
            }
        }
    }

    for (const auto& entry : plan)
    {
        const std::string& deploymentKey = entry.first;
        const DeploymentPlan& planned = entry.second;
        const std::string deploymentPath = "CanonicalData\\Deployments\\" + deploymentKey;
        lines.insert("K\t" + deploymentPath);

        std::string appIdHex;
        {
            const std::vector<std::uint8_t> bytes = CbsEncodeAsciiText(planned.appId);
            HexAppend(bytes, appIdHex);
        }
        lines.insert("V\t" + deploymentPath + "\tappid\t3\t" + appIdHex);
        ++deployments;

        // 驱动部署（Type=driverUpdate）不写 CatalogThumbprint / Catalogs 标记：
        // 真机 574 个驱动部署全部没有这两个东西（1,703 个带 thumbprint 的部署
        // 由 1,330 个语言包 + 357 个功能部署 + 16 个其它组成，一个驱动都没有）。
        const std::string catalogHex = ChooseDeploymentCatalog(planned, resolveOrder);
        if (!catalogHex.empty() && !planned.driver)
        {
            const std::wstring catalogText = FromUtf8OrAnsi(catalogHex.c_str());
            lines.insert("V\t" + deploymentPath + "\tCatalogThumbprint\t1\t" +
                         Utf16LeHex(catalogText));
        }
        if (!planned.driver)
        {
            for (const std::string& catalog : ChooseCatalogMarks(planned, catalogHex, useSessions))
            {
                const std::string catalogPath = "CanonicalData\\Catalogs\\" + catalog;
                lines.insert("K\t" + catalogPath);
                lines.insert("V\t" + catalogPath + "\t" + CbsMarkValueName(deploymentKey) +
                             "\t3\t");
            }
        }

        const auto markerIt = markers.find(AsciiLowerCopy(planned.appId));
        // 有 --markers 时以它为准（没记录 = 该部署没有 p!/s!/i!）；
        // 没给 markers 时退化成"只写 p!"，方便看结构。
        const std::string markerList =
            markerIt != markers.end()
                ? markerIt->second
                : (markersPath.empty() ? std::string("p") : std::string());

        // 部署的 appid 自身也是一条组件记录：真机对**全部** 2,283 个部署组件写
        // `CF=12`（0x4 清单在 | 0x8 文件在 的闭包状态），其中真正装了载荷的
        // 1,532 个再写一条 `c!<部署键>`（数据为空，表示"这个组件由该部署安装"）。
        // 2,276/2,283 命中（剩下 5 个 servicing stack 部署与 2 个没有清单的
        // .mum 虚构声明）。
        {
            CbsIdentity deploymentIdentity;
            std::string deploymentIdentityErr;
            if (CbsParseIdentity(planned.appId, deploymentIdentity, deploymentIdentityErr))
            {
                const std::string componentPath =
                    "DerivedData\\Components\\" + CbsComponentKeyName(deploymentIdentity);
                lines.insert("K\t" + componentPath);
                if (flagOverrides.count(CbsComponentKeyName(deploymentIdentity)) == 0)
                    lines.insert("V\t" + componentPath + "\tCF\t4\t0c000000");
                if (!markerList.empty() && !planned.payloads.empty())
                    lines.insert("V\t" + componentPath + "\t" +
                                 CbsMarkValueName(deploymentKey) + "\t3\t");
            }
        }

        if (markerList.empty()) continue;
        for (const std::string& payloadText : planned.payloads)
        {
            std::string payloadHex;
            const std::vector<std::uint8_t> bytes = CbsEncodeLengthPrefixedAscii(payloadText);
            HexAppend(bytes, payloadHex);
            for (const char prefix : std::string("psi"))
            {
                if (markerList.find(prefix) == std::string::npos) continue;
                const std::string valueName =
                    CbsPayloadValueName(std::string(1, prefix) + "!", payloadText);
                lines.insert("V\t" + deploymentPath + "\t" + valueName + "\t3\t" + payloadHex);
                ++payloads;
            }
        }
    }
    FindClose(find);
    }
    catch (const std::exception& e)
    {
        Err("处理 " + ToUtf8(currentMum) + " 时抛出异常：" + e.what());
        return 3;
    }

    // 安装态补充输入（由"安装计划"提供，纯清单推不出来）：
    //   --claims <csv>：`<组件键>|<部署键>`，写 `c!<部署键>`（空数据 = 该组件由该部署安装）。
    //     真机 10,342 条 c! 里只有 1,532 条是部署组件自认领，其余来自包载荷（cab 内
    //     的 update.mum 组件列表），8061 图像把 cab 剥掉了，只能作为安装输入。
    //   --flags  <csv>：`<组件键>|<CF 十进制>`，写 `CF`（闭包状态位：0x1 sparsed、
    //     0x4 清单在、0x8 文件在）。真机 2,767 条里有 491 条不是部署组件（msil
    //     程序集的 4/5），属于安装/暂存状态。
    long long claimValues = 0, flagValues = 0;
    const auto readTwoColumn = [](const std::wstring& path,
                                  const std::function<void(const std::string&, const std::string&)>& fn,
                                  std::string& error) -> bool {
        if (path.empty()) return true;
        std::wstring raw;
        std::wstring wError;
        if (!ReadFileUtf8(path, raw, wError))
        {
            error = ToUtf8(wError);
            return false;
        }
        const std::string utf8 = ToUtf8(raw);
        std::size_t start = 0;
        while (start < utf8.size())
        {
            std::size_t end = utf8.find('\n', start);
            if (end == std::string::npos) end = utf8.size();
            std::string line = utf8.substr(start, end - start);
            start = end + 1;
            if (!line.empty() && line.back() == '\r') line.pop_back();
            if (line.empty()) continue;
            const std::size_t bar = line.find('|');
            if (bar == std::string::npos) continue;
            fn(line.substr(0, bar), line.substr(bar + 1));
        }
        return true;
    };
    {
        std::string readErr;
        if (!readTwoColumn(claimsPath, [&](const std::string& componentKey, const std::string& deploymentKey) {
                if (componentKey.empty() || deploymentKey.empty()) return;
                const std::string componentPath = "DerivedData\\Components\\" + componentKey;
                lines.insert("K\t" + componentPath);
                lines.insert("V\t" + componentPath + "\t" + CbsMarkValueName(deploymentKey) + "\t3\t");
                ++claimValues;
            }, readErr))
        {
            Err("读不了 --claims：" + readErr);
            return 2;
        }
        for (const auto& overrideEntry : flagOverrides)
        {
            const std::string& componentKey = overrideEntry.first;
            const std::string& valueText = overrideEntry.second;
            if (componentKey.empty() || valueText.empty()) continue;
            const long long value = std::strtoll(valueText.c_str(), nullptr, 10);
            // 现代镜像（16299+）的 CF 有 0x3F 之外的新位（如 0x200 = 已缓存），
            // 所以这里不再限掩码：只要是真机导出的 DWORD 就照写。
            if (value <= 0 || value > 0xFFFFFFFFLL) continue;
            std::uint8_t bytes[4] = {static_cast<std::uint8_t>(value & 0xFF),
                                     static_cast<std::uint8_t>((value >> 8) & 0xFF),
                                     static_cast<std::uint8_t>((value >> 16) & 0xFF),
                                     static_cast<std::uint8_t>((value >> 24) & 0xFF)};
            const std::string componentPath = "DerivedData\\Components\\" + componentKey;
            lines.insert("K\t" + componentPath);
            std::string hex;
            HexAppend(std::vector<std::uint8_t>(bytes, bytes + 4), hex);
            lines.insert("V\t" + componentPath + "\tCF\t4\t" + hex);
            ++flagValues;
        }
    }

    {
        FILE* out = _wfopen(outPath.c_str(), L"wb");
        if (!out) { Err("写不了文件：" + ToUtf8(outPath)); return 1; }
        for (const std::string& line : lines)
        {
            std::fwrite(line.data(), 1, line.size(), out);
            std::fputc('\n', out);
        }
        std::fclose(out);
    }

    OutLn("manifests=" + std::to_string(manifests) + " badManifest=" + std::to_string(badManifest) +
          " mums=" + std::to_string(packages) + " updates=" + std::to_string(updates) +
          " declared=" + std::to_string(declared) + " resolved=" + std::to_string(resolved) +
          " deployments=" + std::to_string(deploymentKeys.size()) +
          " payloadValues=" + std::to_string(payloads) +
          " withoutCat=" + std::to_string(noCat) +
          " markerAppIds=" + std::to_string(markers.size()) +
          " claims=" + std::to_string(claimValues) + " flags=" + std::to_string(flagValues) +
          " resolves=" + std::to_string(resolves) +
          " sessionPackages=" + std::to_string(resolveOrder.size()) +
          " flagDeployments=" + std::to_string(flagDeployments));
    return 0;
#else
    (void)packagesDir; (void)manifestDir; (void)markersPath; (void)outPath;
    return 2;
#endif
}

// ── cbs-plan-versioned：清单 + 已落位文件 + 包目录 → DerivedData\VersionedIndex ──
// 真机实测（8061 x86：13,159 家庭 / 13,325 版本节点）：
//   * 家庭键 = 去掉 version 属性的身份折叠（CbsFamilyKeyName），挂在 ComponentFamilies 下；
//   * 家庭层的 `f256!<文件值名>` 值 = UTF-16 "<base64(SHA-256(已落位文件字节))>!<组件版本>#"，
//     例 1394.inf → "Zv8Bza1RLHEQQI6LLjKzIWML8sDNutSfKNWECxSLiaw=!6.2.8061.0#"；
//   * 版本节点 `v!<version>` 的 `CT` = 该组件所属包的 .cat 的 SHA-256（UTF-16 的 64 hex 文本）；
//   * `MCP_<8hex>`（12 字节 {0,offset,length}）与版本节点 `(default)` 是 ManifestCache 的
//     CDF 打包偏移与闭包状态位图，依赖本图已被剥掉的 blob，尚未复刻（故意不发射）。
static int CmdCbsPlanVersioned(const std::wstring& manifestDir,
                               const std::wstring& packagesDir,
                               const std::wstring& stagedDir,
                               const std::wstring& stackVersion,
                               const std::wstring& bucketsPath,
                               const std::wstring& cachePath,
                               const std::wstring& ctPath,
                               const std::wstring& versionFlagsPath,
                               const std::wstring& mcpPath,
                               const std::wstring& skeletonPath,
                               const std::wstring& outPath)
{
#ifdef _WIN32
    // 1) .mum → 组件键 → 所属包的 .cat 的 SHA-256（CT 值的来源）。
    // 组件键前缀 → (包语言小写 → 该包的 .cat SHA-256)。同一个语言包组件常被
    // 中性包 + 各语言包同时声明（Common-Drivers 的 `~~`/`~en-US~`/`~qps-PLOC~`），
    // 真机 CT 取的是**语言与组件文化一致**的那个包（3ware.inf-LanguagePack en-us
    // ⇒ Common-Drivers ~en-US~ 的 cat），所以按文化分桶再选。
    std::map<std::string, std::map<std::string, std::string>> catalogByComponent;
    // 组件键尾 `_<16hex>` 是属性折叠；.mum 声明里的属性集合（缺 versionScope 等）
    // 与清单不一定相同，所以用"去掉尾哈希的键前缀"当匹配键。
    const auto keyPrefix = [](const std::string& key) {
        return key.size() > 17 ? key.substr(0, key.size() - 17) : key;
    };
    long long mums = 0, declared = 0, catalogComponents = 0;
    // 诊断：OCID_CT_CANDIDATES=<csv> 时把每个 .mum 的组件声明与最终选择都导出来，
    // 用来反推"真机 CT 选的是哪个包的 cat"（真机 912 条 CT 我们还没覆盖）。
    FILE* ctLog = nullptr;
    {
        wchar_t logPath[1024]{};
        const DWORD logLen = GetEnvironmentVariableW(L"OCID_CT_CANDIDATES", logPath, 1024);
        if (logLen > 0 && logLen < 1024) _wfopen_s(&ctLog, logPath, L"wb");
    }
    {
        WIN32_FIND_DATAW found{};
        HANDLE find = FindFirstFileW((packagesDir + L"\\*.mum").c_str(), &found);
        if (find != INVALID_HANDLE_VALUE)
        {
            do
            {
                if ((found.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) continue;
                ++mums;
                const std::wstring mumPath = packagesDir + L"\\" + found.cFileName;
                std::wstring xml, readErr;
                if (!ReadFileUtf8(mumPath, xml, readErr)) continue;

                detail::XmlParser parser(xml);
                detail::XmlNode root;
                if (!parser.ParseDocument(root)) continue;

                // 包语言（根 assemblyIdentity 的 language；neutral/* ⇒ 空）
                std::string packageCulture;
                {
                    std::vector<const detail::XmlNode*> packageIds;
                    CollectXmlByLocalName(root, L"assemblyIdentity", packageIds);
                    if (!packageIds.empty())
                    {
                        packageCulture = MumAttr(*packageIds.front(), L"language");
                        for (char& ch : packageCulture)
                            if (ch >= 'A' && ch <= 'Z')
                                ch = static_cast<char>(ch - 'A' + 'a');
                        if (packageCulture == "neutral" || packageCulture == "*")
                            packageCulture.clear();
                    }
                }

                std::string catalogHex;
                {
                    std::wstring stem = mumPath;
                    if (stem.size() > 4 &&
                        _wcsicmp(stem.c_str() + stem.size() - 4, L".mum") == 0)
                        stem.resize(stem.size() - 4);
                    std::vector<std::uint8_t> catBytes;
                    std::wstring catErr;
                    if (ReadFileBytes(stem + L".cat", catBytes, catErr))
                        catalogHex = Sha256Hex(catBytes);
                }
                if (catalogHex.empty()) continue;

                std::vector<const detail::XmlNode*> updateNodes;
                CollectXmlByLocalName(root, L"update", updateNodes);
                for (const detail::XmlNode* update : updateNodes)
                {
                    std::vector<const detail::XmlNode*> declaredNodes;
                    CollectXmlByLocalName(*update, L"component", declaredNodes);
                    CollectXmlByLocalName(*update, L"driver", declaredNodes);
                    for (const detail::XmlNode* node : declaredNodes)
                    {
                        std::vector<const detail::XmlNode*> ids;
                        CollectXmlByLocalName(*node, L"assemblyIdentity", ids);
                        if (ids.empty()) continue;
                        ++declared;
                        CbsIdentity declaredIdentity;
                        std::string parseErr;
                        if (!CbsParseIdentity(MumIdentityText(*ids.front()), declaredIdentity,
                                              parseErr))
                            continue;
                        // 键名把 name/culture 段统一小写，所以 .mum 的写法能和
                        // 清单算出来的组件键对上（plan-deployments 里已验证）。
                        // 同一个语言包常被多个包声明（Anytime-Upgrade 与
                        // Client-LanguagePack 都声明 Anytime-Upgrade-Deployment-LanguagePack），
                        // 真机对这两种情况的取舍不一致（驱动语言包按文化取、功能语言包
                        // 有时取"名字更小"的包），没有单一规则 ⇒ 这里取常见的那条：
                        // 同文化里**最后声明**者（实测 1,330/1,697 命中，其余记为偏差）。
                        catalogByComponent[keyPrefix(CbsComponentKeyName(declaredIdentity))]
                                          [packageCulture] = catalogHex;
                        if (ctLog != nullptr)
                        {
                            const std::string prefix = keyPrefix(CbsComponentKeyName(declaredIdentity));
                            std::fprintf(ctLog, "decl|%s|%s|%s|%s\n", prefix.c_str(),
                                         packageCulture.c_str(),
                                         ToUtf8(found.cFileName).c_str(), catalogHex.c_str());
                        }
                        ++catalogComponents;
                    }
                }
            } while (FindNextFileW(find, &found) != 0);
            FindClose(find);
        }
    }

    // 2) 清单 → 家庭键 / 版本节点 / f256。
    // 家庭归属的 build 桶是**安装历史**：被更早的服务栈暂存过的组件留在那个栈的桶里
    // （8061 x86 有 100 个家庭落在 `6.2.7992.0 (fbl_fun_lad.110407-1318)`，全是引导组件）。
    // 纯清单推不出来 ⇒ 用 --buckets <csv>（行 `家庭键|桶文本`）当输入覆盖默认桶。
    std::map<std::string, std::string> bucketOverride;
    if (!bucketsPath.empty())
    {
        std::wstring wideText, readErr;
        if (!ReadFileUtf8(bucketsPath, wideText, readErr))
        {
            Err("读不了 --buckets 文件：" + ToUtf8(bucketsPath));
            return 2;
        }
        const std::string text = ToUtf8(wideText);
        std::size_t pos = 0;
        while (pos < text.size())
        {
            std::size_t end = text.find('\n', pos);
            if (end == std::string::npos) end = text.size();
            std::string line = text.substr(pos, end - pos);
            pos = end + 1;
            if (!line.empty() && line.back() == '\r') line.pop_back();
            if (line.empty()) continue;
            const std::size_t bar = line.find('|');
            if (bar == std::string::npos) continue;
            bucketOverride[line.substr(0, bar)] = line.substr(bar + 1);
        }
    }

    std::set<std::string> lines;
    // "骨架家庭"输入（行 = 家庭键，见发射处的说明）。8419 ARM 有 112 个此类家庭：
    // 清单与载荷都在，但版本化索引里只有家庭键/空版本节点/`SomeUnparsedVersionsExist`。
    std::set<std::string> skeletonFamilies;
    if (!skeletonPath.empty())
    {
        std::wstring wideText, readErr;
        if (!ReadFileUtf8(skeletonPath, wideText, readErr))
        {
            Err("读不了 --skeleton-families 文件：" + ToUtf8(skeletonPath));
            return 2;
        }
        const std::string text = ToUtf8(wideText);
        std::size_t pos = 0;
        while (pos < text.size())
        {
            std::size_t end = text.find('\n', pos);
            if (end == std::string::npos) end = text.size();
            std::string line = text.substr(pos, end - pos);
            pos = end + 1;
            if (!line.empty() && line.back() == '\r') line.pop_back();
            if (line.empty()) continue;
            const std::size_t bar = line.find('|');
            if (bar != std::string::npos) line.resize(bar);
            if (!line.empty()) skeletonFamilies.insert(line);
        }
    }
    const std::string defaultBucketText = ToUtf8(stackVersion);
    const std::string bucket = "DerivedData\\VersionedIndex\\" + defaultBucketText;
    std::set<std::string> cacheBucketTexts;

    // 桶级缓存标识（ManifestCacheIdentifier = 4 字节小端；UtilizedSpace_MCP_<id> = 8 字节小端）。
    // 两者都是存储创建/写入时生成的，不由清单推导 ⇒ --cache <csv>（行 `桶文本|id十六进制|已用字节`）。
    if (!cachePath.empty())
    {
        std::wstring wideText, readErr;
        if (!ReadFileUtf8(cachePath, wideText, readErr))
        {
            Err("读不了 --cache 文件：" + ToUtf8(cachePath));
            return 2;
        }
        const std::string text = ToUtf8(wideText);
        std::size_t pos = 0;
        while (pos < text.size())
        {
            std::size_t end = text.find('\n', pos);
            if (end == std::string::npos) end = text.size();
            std::string line = text.substr(pos, end - pos);
            pos = end + 1;
            if (!line.empty() && line.back() == '\r') line.pop_back();
            if (line.empty()) continue;
            std::vector<std::string> fields;
            std::size_t start = 0;
            for (;;)
            {
                const std::size_t bar = line.find('|', start);
                if (bar == std::string::npos)
                {
                    fields.push_back(line.substr(start));
                    break;
                }
                fields.push_back(line.substr(start, bar - start));
                start = bar + 1;
            }
            if (fields.size() < 3) continue;
            const std::string bucketText = fields[0];
            cacheBucketTexts.insert(bucketText);
            const std::string bucketPath = "DerivedData\\VersionedIndex\\" + bucketText;
            lines.insert("K\t" + bucketPath);
            std::string idHex = fields[1];
            for (char& ch : idHex)
                if (ch >= 'A' && ch <= 'Z') ch = static_cast<char>(ch - 'A' + 'a');
            // 行格式：
            //   `桶文本|<8hex>|-1`            → ManifestCacheIdentifier（落盘是它的 4 字节小端）
            //   `桶文本|<8hex>|<已用字节>`     → UtilizedSpace_MCP_<8hex>（8 字节小端）
            if (fields[2] == "-1")
            {
                if (idHex.size() == 8)
                {
                    const std::string reversed = idHex.substr(6, 2) + idHex.substr(4, 2) +
                                                 idHex.substr(2, 2) + idHex.substr(0, 2);
                    lines.insert("V\t" + bucketPath + "\tManifestCacheIdentifier\t3\t" + reversed);
                }
                continue;
            }
            if (idHex.size() != 8) continue;
            unsigned long long used = 0;
            try
            {
                used = std::stoull(fields[2]);
            }
            catch (...)
            {
                used = 0;
            }
            std::string usedHex;
            static const char* digits = "0123456789abcdef";
            for (int i = 0; i < 8; ++i)
            {
                const unsigned byte = static_cast<unsigned>((used >> (8 * i)) & 0xFF);
                usedHex.push_back(digits[byte >> 4]);
                usedHex.push_back(digits[byte & 0xF]);
            }
            lines.insert("V\t" + bucketPath + "\tUtilizedSpace_MCP_" + idHex +
                         "\t3\t" + usedHex);
        }
    }

    // 当前桶（= 本镜像服务栈版本那个桶）的根键总是存在：即使所有家庭都被更早的服务栈
    // 暂存到旧桶，真机也会在当前桶留下"家庭键 + 空版本节点 + SomeUnparsedVersionsExist"的骨架
    // （ARM 8061 镜像 118 个家庭全在旧桶，当前桶仍有 118 个只带标记的家庭键）。
    lines.insert("K\t" + bucket);
    lines.insert("K\t" + bucket + "\\ComponentFamilies");

    // --ct <csv>：行 `家庭键|版本|64hex`（安装历史里真机记录的 CT，纯清单推不出那 912 条）。
    std::map<std::string, std::string> ctOverride;
    if (!ctPath.empty())
    {
        std::wstring wideText, readErr;
        if (!ReadFileUtf8(ctPath, wideText, readErr))
        {
            Err("读不了 --ct 文件：" + ToUtf8(ctPath));
            return 2;
        }
        const std::string text = ToUtf8(wideText);
        std::size_t pos = 0;
        while (pos < text.size())
        {
            std::size_t end = text.find('\n', pos);
            if (end == std::string::npos) end = text.size();
            std::string line = text.substr(pos, end - pos);
            pos = end + 1;
            if (!line.empty() && line.back() == '\r') line.pop_back();
            if (line.empty()) continue;
            const std::size_t bar1 = line.find('|');
            if (bar1 == std::string::npos) continue;
            const std::size_t bar2 = line.find('|', bar1 + 1);
            if (bar2 == std::string::npos) continue;
            ctOverride[line.substr(0, bar1) + "|" + line.substr(bar1 + 1, bar2 - bar1 - 1)] =
                line.substr(bar2 + 1);
        }
    }

    // --version-flags <csv>：行 `相对版本节点路径|DWORD 十进制`（版本节点的 `(default)`
    // 是 ManifestCache 写完清单后由工作栈置的闭包状态位图，纯清单推不出，先当输入；
    // 等 PCM 编码器落地后应改为由缓存内容算出）。
    std::map<std::string, std::string> versionFlagOverride;
    if (!versionFlagsPath.empty())
    {
        std::wstring wideText, readErr;
        if (!ReadFileUtf8(versionFlagsPath, wideText, readErr))
        {
            Err("读不了 --version-flags 文件：" + ToUtf8(versionFlagsPath));
            return 2;
        }
        const std::string text = ToUtf8(wideText);
        std::size_t pos = 0;
        while (pos < text.size())
        {
            std::size_t end = text.find('\n', pos);
            if (end == std::string::npos) end = text.size();
            std::string line = text.substr(pos, end - pos);
            pos = end + 1;
            if (!line.empty() && line.back() == '\r') line.pop_back();
            if (line.empty()) continue;
            const std::size_t bar = line.find('|');
            if (bar == std::string::npos) continue;
            const std::string path = line.substr(0, bar);
            const std::string flagText = line.substr(bar + 1);
            const auto flagValue = std::strtoul(flagText.c_str(), nullptr, 10);
            std::vector<std::uint8_t> bytes(4);
            bytes[0] = static_cast<std::uint8_t>(flagValue & 0xFF);
            bytes[1] = static_cast<std::uint8_t>((flagValue >> 8) & 0xFF);
            bytes[2] = static_cast<std::uint8_t>((flagValue >> 16) & 0xFF);
            bytes[3] = static_cast<std::uint8_t>((flagValue >> 24) & 0xFF);
            std::string flagHex;
            HexAppend(bytes, flagHex);
            versionFlagOverride[path] = flagHex;
        }
    }

    // --mcp <csv>：行 `相对版本节点路径|MCP id 8hex|offset|length`（ManifestCache 块的
    // 位置由工作栈按写入历史决定——同一文件里的块偶有空隙，说明历史上被回收过，
    // 所以纯清单无法推出 offset；这里当安装历史输入，用于验证其余内容已逐字节对齐）。
    std::map<std::string, std::string> mcpValueOverride;
    if (!mcpPath.empty())
    {
        std::wstring wideText, readErr;
        if (!ReadFileUtf8(mcpPath, wideText, readErr))
        {
            Err("读不了 --mcp 文件：" + ToUtf8(mcpPath));
            return 2;
        }
        const std::string text = ToUtf8(wideText);
        std::size_t pos = 0;
        while (pos < text.size())
        {
            std::size_t end = text.find('\n', pos);
            if (end == std::string::npos) end = text.size();
            std::string line = text.substr(pos, end - pos);
            pos = end + 1;
            if (!line.empty() && line.back() == '\r') line.pop_back();
            if (line.empty()) continue;
            std::vector<std::string> parts;
            std::size_t start = 0;
            for (;;)
            {
                const std::size_t bar = line.find('|', start);
                if (bar == std::string::npos) { parts.push_back(line.substr(start)); break; }
                parts.push_back(line.substr(start, bar - start));
                start = bar + 1;
            }
            if (parts.size() != 4) continue;
            const std::uint32_t offset = static_cast<std::uint32_t>(std::strtoul(parts[2].c_str(), nullptr, 10));
            const std::uint32_t length = static_cast<std::uint32_t>(std::strtoul(parts[3].c_str(), nullptr, 10));
            std::vector<std::uint8_t> bytes(12, 0);
            bytes[4] = static_cast<std::uint8_t>(offset & 0xFF);
            bytes[5] = static_cast<std::uint8_t>((offset >> 8) & 0xFF);
            bytes[6] = static_cast<std::uint8_t>((offset >> 16) & 0xFF);
            bytes[7] = static_cast<std::uint8_t>((offset >> 24) & 0xFF);
            bytes[8] = static_cast<std::uint8_t>(length & 0xFF);
            bytes[9] = static_cast<std::uint8_t>((length >> 8) & 0xFF);
            bytes[10] = static_cast<std::uint8_t>((length >> 16) & 0xFF);
            bytes[11] = static_cast<std::uint8_t>((length >> 24) & 0xFF);
            std::string hex;
            HexAppend(bytes, hex);
            mcpValueOverride[parts[0] + "|" + parts[1]] = hex;
        }
    }

    long long manifests = 0, stagedFiles = 0, f256 = 0, ct = 0, bad = 0;
    // 家庭层 f256 值可能由多个版本的条目拼接而成（键 = "家庭路径\t值名"）。
    std::map<std::string, std::map<std::string, std::string>> fileSegments;
    // `--mcp` 的行带完整版本节点路径 ⇒ 可以反推"某个家庭在哪些桶里真有内容"。
    // 真机规则：内容只落在最后一次暂存它的那个/那些桶里；被打包的旧家庭在别的桶里
    // 只留骨架（家庭键 + 空版本节点 + SomeUnparsedVersionsExist）。
    std::map<std::string, std::set<std::string>> familyContentBuckets;
    {
        static const std::string kBucketPrefix = "DerivedData\\VersionedIndex\\";
        static const std::string kFamiliesMarker = "\\ComponentFamilies\\";
        for (const auto& mcpEntry : mcpValueOverride)
        {
            const std::size_t bar = mcpEntry.first.find('|');
            const std::string versionPath =
                bar == std::string::npos ? mcpEntry.first : mcpEntry.first.substr(0, bar);
            if (versionPath.compare(0, kBucketPrefix.size(), kBucketPrefix) != 0) continue;
            const std::size_t familyAt = versionPath.find(kFamiliesMarker, kBucketPrefix.size());
            if (familyAt == std::string::npos) continue;
            const std::string bucketText =
                versionPath.substr(kBucketPrefix.size(), familyAt - kBucketPrefix.size());
            const std::size_t familyEnd = versionPath.find("\\v!", familyAt + kFamiliesMarker.size());
            if (familyEnd == std::string::npos) continue;
            const std::string familyKey = versionPath.substr(familyAt + kFamiliesMarker.size(),
                                                             familyEnd - familyAt - kFamiliesMarker.size());
            familyContentBuckets[familyKey].insert(bucketText);
        }
    }
    WIN32_FIND_DATAW found{};
    HANDLE find = FindFirstFileW((manifestDir + L"\\*.manifest").c_str(), &found);
    if (find == INVALID_HANDLE_VALUE)
    {
        Err("目录里没有 .manifest：" + ToUtf8(manifestDir));
        return 2;
    }
    do
    {
        if ((found.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) continue;
        const std::wstring path = manifestDir + L"\\" + found.cFileName;
        std::wstring text, readErr;
        if (!ReadFileUtf8(path, text, readErr)) { ++bad; continue; }
        ComponentManifest manifest;
        const ManifestParseResult parsed = ParseComponentManifest(text, manifest);
        if (!parsed.ok) { ++bad; continue; }
        ++manifests;

        CbsIdentity identity;
        std::string parseErr;
        if (!CbsParseIdentity(IdentityTextFromManifest(manifest.identity), identity, parseErr))
        {
            ++bad;
            continue;
        }
        const std::string componentKey = CbsComponentKeyName(identity);
        const std::wstring componentDir = stagedDir + L"\\" +
                                          FromUtf8OrAnsi(componentKey.c_str());

        // 家庭键/版本节点对**所有**组件都写（真机 13,159 个家庭 / 13,325 个版本节点，
        // 与 Components 记录数一致）；f256 只有载荷真落位（组件目录存在）才有。
        const std::string familyKey = CbsFamilyKeyName(identity);
        std::string bucketText = defaultBucketText;
        const auto overrideIt = bucketOverride.find(familyKey);
        const bool movedToOlderBucket = overrideIt != bucketOverride.end();
        if (movedToOlderBucket) bucketText = overrideIt->second;
        const std::string version = ToUtf8(manifest.identity.Version());
        // 被更早服务栈暂存过的家庭（`--buckets` 指定的那 100 个）在真机里**同时挂在两个桶下**：
        // 旧桶里有完整的家庭键/版本节点/MCP/f256，当前桶里也有同样一整套内容，只是当前桶
        // 的家庭键额外带 `SomeUnparsedVersionsExist` = 0x00（实测 100/100；例
        // `x86_microsoft-windows-os-kernel…` 在两个桶下都有 `f256!ntoskrnl.exe` 与 `v!6.2.8061.0`）。
        struct FamilyEntry
        {
            std::string bucketPath;
            std::string familyKey;
            bool content;
        };
        // 内容桶 = `--mcp` 里出现过该家庭版本节点的桶；没有 mcp 数据时只当当前桶有内容。
        std::set<std::string> contentBuckets;
        const auto contentIt = familyContentBuckets.find(familyKey);
        if (contentIt != familyContentBuckets.end())
        {
            contentBuckets = contentIt->second;
        }
        else
        {
            contentBuckets.insert(bucketText);
        }
        std::vector<FamilyEntry> families;
        // "骨架家庭"：组件/清单存在，但版本化索引里只有家庭键 + 空版本节点 +
        // `SomeUnparsedVersionsExist` = 0x00，没有任何内容（MCP/CT/f256/(default) 都没有）。
        // 8419 ARM 有 112 个这种家庭（其中 55 个磁盘上有载荷目录），来源是"更早的服务栈
        // 把版本内容暂存到了已被裁剪的旧桶"，无法由清单推导，只能作为安装历史输入。
        if (skeletonFamilies.count(familyKey) != 0)
        {
            lines.insert("K\t" + bucket);
            lines.insert("K\t" + bucket + "\\ComponentFamilies");
            const std::string familyPath = bucket + "\\ComponentFamilies\\" + familyKey;
            lines.insert("K\t" + familyPath);
            lines.insert("K\t" + familyPath + "\\v!" + version);
            lines.insert("V\t" + familyPath + "\tSomeUnparsedVersionsExist\t3\t00");
            continue;
        }
        for (const std::string& contentBucket : contentBuckets)
        {
            families.push_back({ "DerivedData\\VersionedIndex\\" + contentBucket, familyKey, true });
        }
        if (movedToOlderBucket && !contentBuckets.count(defaultBucketText))
        {
            // 被更早服务栈暂存过的家庭：当前桶只留骨架（真机 ARM 118 个家庭全在旧桶，
            // 当前桶仍有 118 个只带 `SomeUnparsedVersionsExist` 的家庭键）。
            families.push_back({ bucket, familyKey, false });
        }
        std::vector<std::string> familyPaths;
        std::vector<std::string> versionPaths;
        std::string primaryFamilyPath;
        std::string primaryVersionPath;
        for (const FamilyEntry& entry : families)
        {
            lines.insert("K\t" + entry.bucketPath);
            lines.insert("K\t" + entry.bucketPath + "\\ComponentFamilies");
            const std::string familyPath = entry.bucketPath + "\\ComponentFamilies\\" + entry.familyKey;
            lines.insert("K\t" + familyPath);
            const std::string versionPath = familyPath + "\\v!" + version;
            lines.insert("K\t" + versionPath);
            if (entry.bucketPath == bucket && movedToOlderBucket)
            {
                // 当前桶的那份家庭键上多一个 `SomeUnparsedVersionsExist` = 0x00（真机 100/100）。
                lines.insert("V\t" + familyPath + "\tSomeUnparsedVersionsExist\t3\t00");
            }
            if (!entry.content) continue;
            familyPaths.push_back(familyPath);
            versionPaths.push_back(versionPath);
            if (primaryFamilyPath.empty())
            {
                primaryFamilyPath = familyPath;
                primaryVersionPath = versionPath;
            }
            const auto versionFlag = versionFlagOverride.find(versionPath);
            if (versionFlag != versionFlagOverride.end())
            {
                lines.insert("V\t" + versionPath + "\t\t4\t" + versionFlag->second);
            }
            for (const auto& mcpEntry : mcpValueOverride)
            {
                const std::size_t mcpBar = mcpEntry.first.find('|');
                if (mcpBar == std::string::npos) continue;
                if (mcpEntry.first.substr(0, mcpBar) != versionPath) continue;
                lines.insert("V\t" + versionPath + "\tMCP_" + mcpEntry.first.substr(mcpBar + 1) +
                             "\t3\t" + mcpEntry.second);
            }
        }
        const std::string familyPath = primaryFamilyPath;
        const std::string versionPath = primaryVersionPath;

        for (const FileSpec& file : manifest.files)
        {
            if (file.name.empty()) continue;
            std::vector<std::uint8_t> bytes;
            std::wstring fileErr;
            if (!ReadFileBytes(componentDir + L"\\" + file.name, bytes, fileErr)) continue;
            ++stagedFiles;
            // 值名是 `f256!<文件值名去掉 f! 前缀>`（f!1394.inf ⇒ f256!1394.inf）；
            // 同一家庭里同名文件可能存在多个版本 ⇒ 条目按版本从高到低**拼接**：
            // 实测 msil_comsvcconfig 的 f256!comsvcconfig.exe =
            //   "<hash6.2>!6.2.8061.0#<hash4.0>!4.0.8061.0#"
            const std::string fileValueName = CbsFileValueName(file.name);
            const std::string f256Name =
                "f256" + (fileValueName.size() > 1 ? fileValueName.substr(1) : std::string());
            // 真机 f256 的 base64 来自清单里声明的 <dsig:DigestValue>（声明哈希），
            // 不是磁盘文件的哈希：8061 两者恰好相等，16299 的落位文件被补丁/压缩过，
            // 只有声明哈希能对上（实测 aagwrapper.dll：磁盘 eaa320ff… / 声明 4qq8JdW9…）。
            std::wstring declaredDigest = file.digestBase64;
            std::wstring segment = declaredDigest.empty()
                ? FromUtf8OrAnsi(Base64Encode(Sha256(bytes)).c_str())
                : declaredDigest;
            segment += L"!";
            segment += FromUtf8OrAnsi(version.c_str());
            segment += L"#";
            // 两桶并存的家庭在**两个桶**下都有同一批 f256 值（真机 os-kernel 的
            // `f256!ntoskrnl.exe` 在旧桶与当前桶各一份）。
            for (const std::string& path : familyPaths)
                fileSegments[path + "\t" + f256Name][version] = ToUtf8(segment);
        }

        const auto catIt = catalogByComponent.find(keyPrefix(componentKey));
        const auto ctOverrideIt = ctOverride.find(familyKey + "|" + version);
        if (ctOverrideIt != ctOverride.end())
        {
            // --ct 显式给出的 CT（安装历史：真机 912 条 CT 的归属包不在清单声明集合里）。
            lines.insert("V\t" + versionPath + "\tCT\t3\t" +
                         Utf16LeHex(FromUtf8OrAnsi(ctOverrideIt->second.c_str())));
            ++ct;
        }
        else if (catIt != catalogByComponent.end() && !catIt->second.empty())
        {
            // 真机 1,697 条 CT 全部落在 `*-languagepack` 组件上（驱动/功能语言包，
            // 由包声明），类型是 3（REG_BINARY），数据是 UTF-16 的 64 hex 文本。
            std::string lowered = ToUtf8(manifest.identity.Name());
            for (char& ch : lowered)
                if (ch >= 'A' && ch <= 'Z') ch = static_cast<char>(ch - 'A' + 'a');
            static const std::string suffix = "-languagepack";
            const bool isLanguagePack =
                lowered.size() >= suffix.size() &&
                lowered.compare(lowered.size() - suffix.size(), suffix.size(), suffix) == 0;
            if (isLanguagePack)
            {
                // 先按组件文化选包；没有同文化的包时退回中性包（空文化），再退回任意一个。
                std::string culture = ToUtf8(manifest.identity.Language());
                for (char& ch : culture)
                    if (ch >= 'A' && ch <= 'Z') ch = static_cast<char>(ch - 'A' + 'a');
                if (culture == "neutral" || culture == "*") culture.clear();
                std::string catalogHex;
                const auto exact = catIt->second.find(culture);
                if (exact != catIt->second.end()) catalogHex = exact->second;
                if (catalogHex.empty())
                {
                    const auto neutral = catIt->second.find(std::string());
                    if (neutral != catIt->second.end()) catalogHex = neutral->second;
                }
                if (catalogHex.empty()) catalogHex = catIt->second.begin()->second;
                if (ctLog != nullptr)
                    std::fprintf(ctLog, "pick|%s|%s|%s\n", componentKey.c_str(),
                                 culture.c_str(), catalogHex.c_str());
                lines.insert("V\t" + versionPath + "\tCT\t3\t" +
                             Utf16LeHex(FromUtf8OrAnsi(catalogHex.c_str())));
                ++ct;
            }
        }
    } while (FindNextFileW(find, &found) != 0);
    FindClose(find);

    // 把按版本收集的 f256 条目拼接进 lines。
    // 段序 = 真机的安装顺序（不可由版本排序推导：既有 5.82→6.0 这样的升序，
    // 也有 6.2→4.0 这样的降序）。实测 4,712 条多段值里升序占绝大多数
    // （35 组 5.82→6.0、8 组 1.0.0.0→6.2，只有 12 组是 6.2→4.0），所以这里
    // 按版本升序输出，剩下的降序案例当安装历史差异处理。
    for (const auto& entry : fileSegments)
    {
        const std::size_t tab = entry.first.find('\t');
        if (tab == std::string::npos) continue;
        const std::string familyPath = entry.first.substr(0, tab);
        const std::string valueName = entry.first.substr(tab + 1);
        std::string joined;
        for (const auto& segment : entry.second) joined += segment.second;
        lines.insert("V\t" + familyPath + "\t" + valueName + "\t3\t" +
                     Utf16LeHex(FromUtf8OrAnsi(joined.c_str())));
        ++f256;
    }

    // 直接逐行写 std::set（别先拷进 vector：全量 864 个 .mum 时那一步曾 0xC0000409）。
    if (ctLog != nullptr) std::fclose(ctLog);
    {
        FILE* out = _wfopen(outPath.c_str(), L"wb");
        if (!out) { Err("写不了文件：" + ToUtf8(outPath)); return 1; }
        for (const std::string& line : lines)
        {
            std::fwrite(line.data(), 1, line.size(), out);
            std::fputc('\n', out);
        }
        std::fclose(out);
    }

    OutLn("manifests=" + std::to_string(manifests) + " mums=" + std::to_string(mums) +
          " declared=" + std::to_string(declared) +
          " catalogComponents=" + std::to_string(catalogComponents) +
          " stagedFiles=" + std::to_string(stagedFiles) + " f256=" + std::to_string(f256) +
          " ct=" + std::to_string(ct) + " bad=" + std::to_string(bad));
    return 0;
#else
    (void)manifestDir; (void)packagesDir; (void)stagedDir; (void)stackVersion; (void)outPath;
    return 2;
#endif
}

// ── 根值计划（cbs-plan-root）──────────────────────────────────────────────
//
// COMPONENTS 根键上的值由安装会话产生：StoreFormatVersion/StoreArchitecture 是
// 存储格式（固定），ExecutionState/PendingXmlIdentifier 是"当前事务状态"（每次
// 安装都会变），ServicingStackVersions 记录用过这套栈的版本与时间。
// 组装新镜像时这些值由 OCID 自己写；与微软原版对拍时它们属于"会话态"，需要
// 用 --pending/--time 显式给（否则用零/固定时间）。
static int CmdCbsPlanRoot(const std::wstring& archText, const std::wstring& stackVersion,
                          const std::wstring& outPath, const std::wstring& pendingHex,
                          const std::wstring& stackTime, bool minimal,
                          bool emitCcpinterface, bool emitInstallersShortcut,
                          const std::vector<std::string>& stackEntries)
{
    const std::string arch = ToUtf8(archText);
    std::uint32_t archCode = 0;
    if (arch == "x86") archCode = 0;
    else if (arch == "arm") archCode = 5;
    else if (arch == "ia64") archCode = 6;
    else if (arch == "amd64") archCode = 9;
    else if (arch == "arm64") archCode = 12;
    else { Err("未知架构（x86|arm|arm64|amd64|ia64）：" + arch); return 2; }

    std::vector<std::string> lines;
    auto addK = [&lines](const std::string& path) { lines.push_back("K\t" + path); };
    auto addV = [&lines](const std::string& path, const std::string& name, const char* type,
                         const std::string& hex)
    { lines.push_back("V\t" + path + "\t" + name + "\t" + type + "\t" + hex); };

    std::string pending = ToUtf8(pendingHex);
    if (pending.empty()) pending = std::string(32, '0');

    char archHex[16];
    std::snprintf(archHex, sizeof(archHex), "%02x%02x%02x%02x",
                  static_cast<unsigned>(archCode & 0xFFu),
                  static_cast<unsigned>((archCode >> 8) & 0xFFu),
                  static_cast<unsigned>((archCode >> 16) & 0xFFu),
                  static_cast<unsigned>((archCode >> 24) & 0xFFu));

    const std::string stack = ToUtf8(stackVersion);
    const std::string time = stackTime.empty() ? std::string("2011/8/4:8:52:25.424")
                                               : ToUtf8(stackTime);
    const std::string stackLine = time + " " + stack;

    addK("");
    addV("", "StoreFormatVersion", "3", Utf16LeHex(L"0.0.0.6"));
    addV("", "StoreArchitecture", "3", archHex);
    // 真机 ARM 8061 的根只有 StoreFormatVersion / StoreArchitecture /
    // ServicingStackVersions（没有 ExecutionState/PendingXmlIdentifier/Installers/
    // ccpinterface）——那些是"第一次会话"才写进去的会话态。--minimal 用于在
    // 与这类镜像对拍时只发确实存在的根值。
    if (!minimal)
    {
        addV("", "ExecutionState", "4", "06000000");
        addV("", "PendingXmlIdentifier", "3", Utf16LeHex(FromUtf8OrAnsi(pending.c_str())));
    }
    addK("ServicingStackVersions");
    addV("ServicingStackVersions", stack, "1", Utf16LeHex(FromUtf8OrAnsi(stackLine.c_str()) + L'\0'));
    addV("ServicingStackVersions", "LastWCPVersionToAccessStore", "1",
         Utf16LeHex(FromUtf8OrAnsi(stack.c_str()) + L'\0'));
    // `--stack-entry <名字>|<时间 版本>`：真机可能记录过多个服务栈（旧栈暂存的
    // 家庭挂在旧桶下，栈版本表里也留着它），这些历史条目只能由输入给出。
    for (const std::string& entry : stackEntries)
    {
        const std::size_t bar = entry.find('|');
        if (bar == std::string::npos) continue;
        const std::string entryName = entry.substr(0, bar);
        const std::string entryText = entry.substr(bar + 1);
        if (entryName.empty() || entryText.empty()) continue;
        addV("ServicingStackVersions", entryName, "1",
             Utf16LeHex(FromUtf8OrAnsi(entryText.c_str()) + L'\0'));
    }
    if (!minimal)
    {
        addK("Installers");
        addK("Installers\\Ngen");
        addV("Installers\\Ngen", "TicklerArchWoW", "4", "01000000");
    }
    // 8419 的根有 `ccpinterface` 空键但没有任何会话值；8061 ARM 连这个键都没有
    // => 单独一个开关，别把它绑在 --minimal 上。
    if (!minimal || emitCcpinterface) addK("ccpinterface");
    // 8419 的根有 `Installers\\Shortcut` 空键（无 Ngen/TicklerArchWoW）。
    if (emitInstallersShortcut)
    {
        addK("Installers");
        addK("Installers\\Shortcut");
    }
    // `Configuration` 两个真机镜像都有（空的策略键），与 --minimal 无关。
    addK("Configuration");

    std::sort(lines.begin(), lines.end());
    std::wstring writeErr;
    if (!WriteLinesUtf8(outPath, lines, writeErr)) { Err(ToUtf8(writeErr)); return 1; }
    OutLn("root plan written " + ToUtf8(outPath) + " arch=" + arch +
          " stack=" + stack);
    return 0;
}

// 去掉首尾空白（Windows 换行也一并处理）。
std::wstring TrimCopy(const std::wstring& text)
{
    size_t begin = 0;
    size_t end = text.size();
    while (begin < end && (text[begin] == L' ' || text[begin] == L'\t' || text[begin] == L'\r')) ++begin;
    while (end > begin && (text[end - 1] == L' ' || text[end - 1] == L'\t' || text[end - 1] == L'\r')) --end;
    return text.substr(begin, end - begin);
}

int main(int argc, char** argv)
{
    SetupConsole();

    // 全局开关 --dcm-dictionary <wcp.dll>：16299+ 镜像的组件清单是 DCM 压缩容器，
    // 需要 servicing stack 的 wcp.dll 里那份字典来解压。解析后从 argv 里摘掉，
    // 各命令都不需要认识它。
    // 这两个容器必须活到 main 结束：argv 指向它们里面的字符缓冲区。
    std::vector<std::string> args;
    std::vector<char*> stripped;
    {
        args.reserve((size_t)argc);
        for (int i = 0; i < argc; ++i) {
            const std::string a = argv[i];
            if (a == "--dcm-dictionary" && i + 1 < argc) {
                const std::wstring wcpPath = FromUtf8OrAnsi(argv[i + 1]);
                std::string loadError;
#ifdef _WIN32
                if (!LoadManifestDictionary(wcpPath, g_manifestDictionary, loadError)) {
                    // 老镜像（8061 等）的 wcp.dll 里没有这份资源，清单本来也没压缩：
                    // 只要不是真的要解 DCM，就当没给过这个开关。
                    Err("提示：未能加载 DCM 字典（" + loadError + "），按未压缩清单处理");
                }
#else
                (void)wcpPath;
                (void)loadError;
#endif
                ++i;
                continue;
            }
            if (a == "--file-flags" && i + 1 < argc)
            {
                // 现代镜像的 f! 值是文件状态位（REG_DWORD）；非 1 的状态只能抄安装历史。
                // 行格式：<组件键>|<f! 值名>|<DWORD 十进制>
                std::wstring csvText;
                std::wstring csvErr;
                if (ReadFileUtf8(FromUtf8OrAnsi(argv[i + 1]), csvText, csvErr))
                {
                    size_t pos = 0;
                    while (pos < csvText.size())
                    {
                        const size_t end = csvText.find(L'\n', pos);
                        const std::wstring row = csvText.substr(pos, end == std::wstring::npos ? std::wstring::npos : end - pos);
                        pos = (end == std::wstring::npos) ? csvText.size() : end + 1;
                        const std::wstring trimmed = TrimCopy(row);
                        if (trimmed.empty() || trimmed[0] == L'#') continue;
                        const size_t bar1 = trimmed.find(L'|');
                        if (bar1 == std::wstring::npos) continue;
                        const size_t bar2 = trimmed.find(L'|', bar1 + 1);
                        if (bar2 == std::wstring::npos) continue;
                        const std::string flagKey = ToUtf8(trimmed.substr(0, bar1)) + "|" + ToUtf8(trimmed.substr(bar1 + 1, bar2 - bar1 - 1));
                        const std::string valueText = ToUtf8(trimmed.substr(bar2 + 1));
                        g_fileFlags[flagKey] = static_cast<std::uint32_t>(strtoul(valueText.c_str(), nullptr, 10));
                    }
                }
                else
                {
                    Err("无法读取 --file-flags：" + ToUtf8(csvErr));
                }
                ++i;
                continue;
            }

            args.push_back(a);
        }
        stripped.reserve(args.size());
        for (std::string& s : args) stripped.push_back(s.empty() ? const_cast<char*>("") : &s[0]);
        argc = (int)stripped.size();
        argv = stripped.data();
    }

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

    if (cmd == L"cbs-plan-components" && argc >= 4) {
        std::wstring winSxs;
        int mx = 0;
        for (int i = 4; i < argc; ++i) {
            const std::string a = argv[i];
            if (a == "--staged" && i + 1 < argc) { winSxs = FromUtf8OrAnsi(argv[++i]); }
            else if (a == "--limit" && i + 1 < argc) { mx = atoi(argv[++i]); }
        }
        return CmdCbsPlanComponents(FromUtf8OrAnsi(argv[2]), FromUtf8OrAnsi(argv[3]), winSxs, mx);
    }

    // cbs-plan-deployments <packagesDir> <manifestDir> <outFile> [--markers <csv>] [--installed <csv>]
    //                      [--claims <csv>] [--flags <csv>] [--sessions <dir>]
    if (cmd == L"cbs-plan-deployments" && argc >= 5) {
        std::wstring markersPath, installedPath, claimsPath, flagsPath, sessionsDir;
        for (int i = 5; i < argc; ++i) {
            if (std::string(argv[i]) == "--markers" && i + 1 < argc) {
                markersPath = FromUtf8OrAnsi(argv[++i]);
            } else if (std::string(argv[i]) == "--installed" && i + 1 < argc) {
                installedPath = FromUtf8OrAnsi(argv[++i]);
            } else if (std::string(argv[i]) == "--claims" && i + 1 < argc) {
                claimsPath = FromUtf8OrAnsi(argv[++i]);
            } else if (std::string(argv[i]) == "--flags" && i + 1 < argc) {
                flagsPath = FromUtf8OrAnsi(argv[++i]);
            } else if (std::string(argv[i]) == "--sessions" && i + 1 < argc) {
                sessionsDir = FromUtf8OrAnsi(argv[++i]);
            }
        }
        return CmdCbsPlanDeployments(FromUtf8OrAnsi(argv[2]), FromUtf8OrAnsi(argv[3]),
                                     markersPath, installedPath, claimsPath, flagsPath,
                                     sessionsDir, FromUtf8OrAnsi(argv[4]));
    }

    // cbs-plan-versioned <manifestDir> <packagesDir> <stagedDir> <stackVersion> <outFile>
    //                   [--buckets <csv>] [--cache <csv>] [--ct <csv>] [--version-flags <csv>] [--mcp <csv>]
    if (cmd == L"cbs-plan-versioned" && argc >= 7) {
        std::wstring bucketsPath, cachePath, ctPath, versionFlagsPath, mcpPath, skeletonPath;
        for (int i = 7; i + 1 < argc; ++i)
        {
            const std::wstring opt = FromUtf8OrAnsi(argv[i]);
            if (opt == L"--buckets") bucketsPath = FromUtf8OrAnsi(argv[++i]);
            else if (opt == L"--cache") cachePath = FromUtf8OrAnsi(argv[++i]);
            else if (opt == L"--ct") ctPath = FromUtf8OrAnsi(argv[++i]);
            else if (opt == L"--version-flags") versionFlagsPath = FromUtf8OrAnsi(argv[++i]);
            else if (opt == L"--mcp") mcpPath = FromUtf8OrAnsi(argv[++i]);
            else if (opt == L"--skeleton-families") skeletonPath = FromUtf8OrAnsi(argv[++i]);
        }
        return CmdCbsPlanVersioned(FromUtf8OrAnsi(argv[2]), FromUtf8OrAnsi(argv[3]),
                                   FromUtf8OrAnsi(argv[4]), FromUtf8OrAnsi(argv[5]),
                                   bucketsPath, cachePath, ctPath, versionFlagsPath, mcpPath,
                                   skeletonPath, FromUtf8OrAnsi(argv[6]));
    }

    // cbs-plan-root <arch> <stackVersion> <outFile> [--pending <32hex>] [--time <text>]
    //                [--minimal] [--stack-entry <name>|<text>]...
    //                [--ccpinterface] [--installers-shortcut]
    if (cmd == L"cbs-plan-root" && argc >= 5) {
        std::wstring pending, when;
        bool minimal = false;
    bool emitCcpinterface = false;
    bool emitInstallersShortcut = false;
        std::vector<std::string> stackEntries;
        for (int i = 5; i < argc; ++i) {
            const std::wstring opt = FromUtf8OrAnsi(argv[i]);
            if (opt == L"--minimal") { minimal = true; }
        else if (opt == L"--ccpinterface") { emitCcpinterface = true; }
        else if (opt == L"--installers-shortcut") { emitInstallersShortcut = true; }
            else if (opt == L"--pending" && i + 1 < argc) pending = FromUtf8OrAnsi(argv[++i]);
            else if (opt == L"--time" && i + 1 < argc) when = FromUtf8OrAnsi(argv[++i]);
            else if (opt == L"--stack-entry" && i + 1 < argc) stackEntries.push_back(argv[++i]);
        }
        return CmdCbsPlanRoot(FromUtf8OrAnsi(argv[2]), FromUtf8OrAnsi(argv[3]),
                              FromUtf8OrAnsi(argv[4]), pending, when, minimal, emitCcpinterface,
                              emitInstallersShortcut, stackEntries);
    }

    if (cmd == L"cbs-dcm-decode" && argc >= 4)
        return CmdCbsDcmDecode(FromUtf8OrAnsi(argv[2]), FromUtf8OrAnsi(argv[3]));
    if (cmd == L"cbs-diff" && argc >= 4) {
        int mx = 0;
        for (int i = 4; i < argc; ++i) {
            if (std::string(argv[i]) == "--limit" && i + 1 < argc) { mx = atoi(argv[++i]); }
        }
        return CmdCbsDiff(FromUtf8OrAnsi(argv[2]), FromUtf8OrAnsi(argv[3]), mx);
    }

    // cbs-verify-store <hiveSpec>：没有对拍样本时证明台账自洽
    if (cmd == L"cbs-verify-store" && argc >= 3) {
        return CmdCbsVerifyStore(FromUtf8OrAnsi(argv[2]));
    }

    // cbs-verify-files <planFile> <winSxsDir>：提交台账前核对文件是否都已落位
    if (cmd == L"cbs-verify-files" && argc >= 4) {
        return CmdCbsVerifyFiles(FromUtf8OrAnsi(argv[2]), FromUtf8OrAnsi(argv[3]));
    }

    // cbs-write <planFile> <hivePath> [--transactional]：把计划文本落成 hive 文件
    if (cmd == L"cbs-write" && argc >= 4) {
        bool transactional = false;
        for (int i = 4; i < argc; ++i) {
            if (FromUtf8OrAnsi(argv[i]) == L"--transactional") transactional = true;
        }
        return CmdCbsWrite(FromUtf8OrAnsi(argv[2]), FromUtf8OrAnsi(argv[3]), transactional);
    }

    // cbs-hash：算身份折叠 / 值名后缀用的 H39 文本哈希（调试用）。
    if (cmd == L"cbs-hash" && argc >= 3) {
        for (int i = 2; i < argc; ++i) {
            const std::wstring text = FromUtf8OrAnsi(argv[i]);
            const std::uint64_t value = CbsHashEncodedText(ToUtf8(text));
            char buf[32];
            std::snprintf(buf, sizeof(buf), "%016llx",
                static_cast<unsigned long long>(value));
            std::printf("%s -> %s\n", argv[i], buf);
        }
        return 0;
    }

    // cbs-pcm-reencode：拆 PcmH 的 TOC，再按真机容器语义重编码，报 identical。
    if (cmd == L"cbs-pcm-tables" && argc >= 3) {
        return CmdCbsPcmTables(FromUtf8OrAnsi(argv[2]));
    }

    if (cmd == L"cbs-pcm-reencode" && argc >= 4) {
        return CmdCbsPcmReencode(FromUtf8OrAnsi(argv[2]), FromUtf8OrAnsi(argv[3]));
    }

    // cbs-microdom：清单 XML → MdHd 段（和真机 ManifestCache 的 t11 表对拍）。
    if (cmd == L"cbs-microdom" && argc >= 4) {
        return CmdCbsMicrodom(FromUtf8OrAnsi(argv[2]), FromUtf8OrAnsi(argv[3]));
    }

    if (cmd == L"macros")
        return CmdMacros();

    Usage();
    return 1;
}



















