// OCID - 组件清单解析测试
//
// 两部分：
//   ① 单元测试：用手写的小清单覆盖解析器的边界（属性、默认值、嵌套、注释、CDATA、转义、错误）
//   ② 真实数据：给一个目录，解析里面所有真实 .manifest 并汇总
//      —— 这部分才是真正的验收：镜像里的清单能不能全读进来、注册表值数得对不对。
//
// 用法：
//   ocid_manifest_tests                      只跑单元测试
//   ocid_manifest_tests <dir> [maxFiles]     额外解析真实清单

#include "ocid/Manifest.h"

#include <cstdio>
#include <string>
#include <vector>

#ifdef _WIN32
#include <windows.h>
#endif

using namespace ocid;

static int g_fail = 0, g_pass = 0;

static void Check(bool c, const char* what)
{
    if (c) { ++g_pass; printf("  [ok]   %s\n", what); }
    else   { ++g_fail; printf("  [FAIL] %s\n", what); }
}

static std::wstring W(const char* utf8)
{
    // 测试里的字面量都是 ASCII，直接逐字节扩宽即可。
    std::wstring r;
    for (const char* p = utf8; *p; ++p) r += (wchar_t)(unsigned char)*p;
    return r;
}

// ── 用手写小清单覆盖解析器 ──────────────────────────────────────────────

static void UnitTests()
{
    printf("=== 基本结构 ===\n");
    {
        const std::wstring xml =
            L"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
            L"<!-- a comment -->\n"
            L"<assembly xmlns=\"urn:schemas-microsoft-com:asm.v3\" manifestVersion=\"1.0\">\n"
            L"  <assemblyIdentity name=\"demo.pkg\" version=\"6.2.8061.0\" processorArchitecture=\"arm\"\n"
            L"                    language=\"neutral\" publicKeyToken=\"31bf3856ad364e35\" versionScope=\"nonSxS\" />\n"
            L"  <registryKeys>\n"
            L"    <registryKey keyName=\"HKEY_CLASSES_ROOT\\*\" owner=\"false\">\n"
            L"      <registryValue name=\"AlwaysShowExt\" valueType=\"REG_SZ\" value=\"\" operationHint=\"replace\" owner=\"true\" />\n"
            L"      <registryValue name=\"\" valueType=\"REG_SZ\" value=\"openas\" owner=\"true\" />\n"
            L"      <registryValue name=\"Dword\" valueType=\"REG_DWORD\" value=\"0x1f\" owner=\"false\" />\n"
            L"    </registryKey>\n"
            L"  </registryKeys>\n"
            L"</assembly>\n";

        ComponentManifest m;
        auto r = ParseComponentManifest(xml, m);
        Check(r.ok, "解析成功");
        CheckEq: ;
        Check(m.identity.Name() == L"demo.pkg", "identity.name");
        Check(m.identity.Version() == L"6.2.8061.0", "identity.version");
        Check(m.identity.Scope() == VersionScope::NonSxS, "identity.versionScope=nonSxS");
        Check(m.registryKeys.size() == 1, "1 个 registryKey");
        Check(m.registryKeys[0].keyName == L"HKEY_CLASSES_ROOT\\*", "keyName 反斜杠完整保留");
        Check(m.registryKeys[0].owner == false, "key owner=false");
        Check(m.registryKeys[0].values.size() == 3, "3 个 registryValue");
        Check(m.registryKeys[0].values[0].name == L"AlwaysShowExt", "值名");
        Check(m.registryKeys[0].values[1].IsDefault(), "name=\"\" ⇒ 默认值");
        Check(m.registryKeys[0].values[1].value == L"openas", "默认值数据 openas");
        Check(m.registryKeys[0].values[2].valueType == L"REG_DWORD", "REG_DWORD");
        Check(m.registryKeys[0].values[2].owner == false, "value owner=false");
        Check(m.registryKeys[0].values[0].operationHint == L"replace", "operationHint");
        Check(m.RegistryValueCount() == 3, "RegistryValueCount=3");
    }

    printf("\n=== 认不出的元素只警告，不失败 ===\n");
    {
        const std::wstring xml =
            L"<assembly manifestVersion=\"1.0\">"
            L"<assemblyIdentity name=\"p\" version=\"1.0.0.0\" processorArchitecture=\"x86\" publicKeyToken=\"aa\" language=\"neutral\" versionScope=\"nonSxS\"/>"
            L"<migXml><rules><merge/></rules></migXml>"
            L"<taskScheduler><Task><Actions><ComHandler><ClassId>abc</ClassId></ComHandler></Actions></Task></taskScheduler>"
            L"<registryKeys></registryKeys>"
            L"</assembly>";
        ComponentManifest m;
        auto r = ParseComponentManifest(xml, m);
        Check(r.ok, "仍然成功");
        Check(r.warnings.size() == 2, "记了 2 条未处理元素警告");
    }

    printf("\n=== 依赖与 supportedComponent ===\n");
    {
        const std::wstring xml =
            L"<assembly manifestVersion=\"1.0\">"
            L"<assemblyIdentity name=\"p\" version=\"1.0.0.0\" processorArchitecture=\"x86\" publicKeyToken=\"aa\" language=\"neutral\" versionScope=\"nonSxS\"/>"
            L"<dependency><dependentAssembly>"
            L"<assemblyIdentity name=\"dep\" version=\"2.0.0.0\" processorArchitecture=\"x86\" publicKeyToken=\"bb\" language=\"neutral\" versionScope=\"nonSxS\"/>"
            L"</dependentAssembly></dependency>"
            L"<supportedComponent><supportedComponentIdentity name=\"sc\" version=\"3.0.0.0\" processorArchitecture=\"x86\" publicKeyToken=\"cc\" language=\"neutral\" versionScope=\"nonSxS\"/></supportedComponent>"
            L"</assembly>";
        ComponentManifest m;
        auto r = ParseComponentManifest(xml, m);
        Check(r.ok, "解析成功");
        Check(m.dependencies.size() == 2, "2 条依赖");
        Check(m.dependencies[0].kind == L"dependency", "第一条是 dependency");
        Check(m.dependencies[0].identity.Name() == L"dep", "依赖名");
        Check(m.dependencies[1].kind == L"supportedComponent", "第二条是 supportedComponent");
        Check(m.dependencies[1].identity.Name() == L"sc", "supportedComponent 名");
    }

    printf("\n=== configurationSchema: 抽出 xsd:element 名 ===\n");
    {
        const std::wstring xml =
            L"<assembly manifestVersion=\"1.0\">"
            L"<assemblyIdentity name=\"p\" version=\"1.0.0.0\" processorArchitecture=\"x86\" publicKeyToken=\"aa\" language=\"neutral\" versionScope=\"nonSxS\"/>"
            L"<configurationSchema><xsd:schema xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">"
            L"<xsd:element name=\"HKEY_CLASSES_ROOT_Unknown_shell_defaultValue\" default=\"openas\" type=\"xsd:string\"/>"
            L"<xsd:element name=\"Other\" default=\"x\" type=\"xsd:string\"/>"
            L"</xsd:schema></configurationSchema>"
            L"</assembly>";
        ComponentManifest m;
        auto r = ParseComponentManifest(xml, m);
        Check(r.ok, "解析成功");
        Check(m.configurationSchema.elementNames.size() == 2, "抽出 2 个 xsd:element");
        Check(m.configurationSchema.elementNames[0] == L"HKEY_CLASSES_ROOT_Unknown_shell_defaultValue",
              "第一个 element 名正确（正是那个被 UCPD 拦的键）");
        Check(!m.configurationSchema.xsdText.empty(), "XSD 原文被保留");
    }

    printf("\n=== 转义 / 引号 / 自闭合 ===\n");
    {
        const std::wstring xml =
            L"<assembly manifestVersion='1.0'>"
            L"<assemblyIdentity name='p' version='1.0.0.0' processorArchitecture='x86' publicKeyToken='aa' language='neutral' versionScope='nonSxS'/>"
            L"<registryKeys><registryKey keyName='HKEY_CLASSES_ROOT\\Unknown\\shell' owner='false'>"
            L"<registryValue name='' valueType='REG_EXPAND_SZ' value='&quot;%SystemRoot%\\system32\\OpenWith.exe&quot; &amp; &lt;x&gt;' owner='true'/>"
            L"</registryKey></registryKeys>"
            L"</assembly>";
        ComponentManifest m;
        auto r = ParseComponentManifest(xml, m);
        Check(r.ok, "单引号属性也能解析");
        Check(m.registryKeys[0].values[0].value == L"\"%SystemRoot%\\system32\\OpenWith.exe\" & <x>",
              "实体转义还原正确");
    }

    printf("\n=== 必须失败的情形 ===\n");
    {
        ComponentManifest m;
        auto r1 = ParseComponentManifest(L"<assembly><foo></assembly>", m);
        Check(!r1.ok, "标签不匹配 ⇒ 失败");

        ComponentManifest m2;
        auto r2 = ParseComponentManifest(L"<root></root>", m2);
        Check(!r2.ok, "根元素不是 assembly ⇒ 失败");

        ComponentManifest m3;
        auto r3 = ParseComponentManifest(L"<assembly><registryKeys/></assembly>", m3);
        Check(!r3.ok, "没有 assemblyIdentity ⇒ 失败");

        ComponentManifest m4;
        auto r4 = ParseComponentManifest(
            L"<assembly><assemblyIdentity name=\"p\"/>"
            L"<registryKeys><registryKey keyName=\"K\"><registryValue name=\"v\"/></registryKey></registryKeys>"
            L"</assembly>", m4);
        Check(!r4.ok == false, "registryValue 缺 valueType 不导致整体失败（只跳过）");
    }
}

#ifdef _WIN32
static std::wstring ReadFileUtf8(const std::wstring& path, bool& ok)
{
    ok = false;
    FILE* f = _wfopen(path.c_str(), L"rb");
    if (!f) return L"";
    fseek(f, 0, SEEK_END);
    long n = ftell(f);
    fseek(f, 0, SEEK_SET);
    std::string buf(n, '\0');
    if (n > 0) fread(&buf[0], 1, (size_t)n, f);
    fclose(f);
    ok = true;

    // 去掉 UTF-8 BOM
    size_t start = 0;
    if (buf.size() >= 3 && (unsigned char)buf[0] == 0xEF && (unsigned char)buf[1] == 0xBB && (unsigned char)buf[2] == 0xBF)
        start = 3;

    // UTF-8 → UTF-16
    int wlen = MultiByteToWideChar(CP_UTF8, 0, buf.data() + start, (int)(buf.size() - start), nullptr, 0);
    std::wstring w((size_t)wlen, L'\0');
    if (wlen > 0) MultiByteToWideChar(CP_UTF8, 0, buf.data() + start, (int)(buf.size() - start), &w[0], wlen);
    return w;
}
#endif

// ── 真实清单汇总 ────────────────────────────────────────────────────────

static int RealManifests(const std::wstring& dir, int maxFiles)
{
#ifdef _WIN32
    printf("\n=== 真实清单解析：%ls ===\n", dir.c_str());

    WIN32_FIND_DATAW fd;
    std::wstring pattern = dir + L"\\*.manifest";
    HANDLE h = FindFirstFileW(pattern.c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) {
        printf("  找不到 .manifest 文件\n");
        return 1;
    }

    int total = 0, okCount = 0, failCount = 0;
    size_t totalRegValues = 0, totalRegKeys = 0, totalXsd = 0, totalSd = 0, totalFiles = 0, totalDirs = 0;
    std::vector<std::wstring> firstErrors;
    std::map<std::wstring, int> warnHistogram;

    do {
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
        if (maxFiles > 0 && total >= maxFiles) break;

        std::wstring path = dir + L"\\" + fd.cFileName;
        bool readOk = false;
        std::wstring text = ReadFileUtf8(path, readOk);
        if (!readOk) { ++failCount; ++total; continue; }

        ComponentManifest m;
        auto r = ParseComponentManifest(text, m);
        ++total;
        if (r.ok) {
            ++okCount;
            totalRegValues += m.RegistryValueCount();
            totalRegKeys   += m.registryKeys.size();
            totalXsd       += m.configurationSchema.elementNames.size();
            totalSd        += m.securityDescriptors.size() + m.securityDescriptorDefinitions.size();
            totalFiles     += m.files.size();
            totalDirs      += m.directories.size();
        } else {
            ++failCount;
            if (firstErrors.size() < 5) firstErrors.push_back(fd.cFileName + std::wstring(L" :: ") + r.error);
        }
        for (const auto& w : r.warnings) warnHistogram[w]++;

    } while (FindNextFileW(h, &fd));
    FindClose(h);

    printf("  扫描 %d 份清单：成功 %d，失败 %d\n", total, okCount, failCount);
    printf("  累计 registryKey %zu，registryValue %zu\n", totalRegKeys, totalRegValues);
    printf("  累计 xsd:element %zu，securityDescriptor %zu\n", totalXsd, totalSd);
    if (!firstErrors.empty()) {
        printf("  --- 前几个失败原因 ---\n");
        for (const auto& e : firstErrors) printf("    %ls\n", e.c_str());
    }
    if (!warnHistogram.empty()) {
        printf("  --- 未处理元素的种类（= 后续待实现清单） ---\n");
        int shown = 0;
        for (const auto& kv : warnHistogram) {
            if (shown++ >= 15) break;
            printf("    %-52ls %d\n", kv.first.c_str(), kv.second);
        }
    }

    g_fail += failCount;
    return failCount == 0 ? 0 : 1;
#else
    (void)dir; (void)maxFiles;
    return 0;
#endif
}

int main(int argc, char** argv)
{
    UnitTests();

    int rc = 0;
    if (argc >= 2) {
        std::wstring dir;
        for (const char* p = argv[1]; *p; ++p) dir += (wchar_t)(unsigned char)*p;
        int maxFiles = (argc >= 3) ? atoi(argv[2]) : 0;
        rc = RealManifests(dir, maxFiles);
    }

    printf("\n================================\n");
    printf("  通过 %d，失败 %d\n", g_pass, g_fail);
    printf("================================\n");
    return (g_fail == 0 && rc == 0) ? 0 : 1;
}
