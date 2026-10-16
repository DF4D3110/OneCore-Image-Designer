// OCID - WCM 配置模式测试
//
// 验收标准（这是整条链闭合的证据）：
//   真实清单里的 configurationSchema
//     → 解析出 533 条设置
//     → 其中 "HKEY_CLASSES_ROOT_Unknown_shell_defaultValue" 必须推导成
//         root=HKEY_CLASSES_ROOT, subKey=Unknown\shell,
//         valueName=(空/默认值), valueType=REG_SZ, data="openas"
//   而这**正是 CBS 在 Commit 时被 UCPD 拦下的那一笔**。
//   ⇒ 说明"清单 → 可执行注册表操作"这条路已经能自动走通，不再需要人工喂设置。
//
// 用法：
//   ocid_wcm_tests                     只跑单元测试
//   ocid_wcm_tests <manifest>          额外解析一份真实清单并做上述验收

#include "ocid/Manifest.h"
#include "ocid/WcmSchema.h"

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

static void CheckEqW(const std::wstring& got, const std::wstring& want, const char* what)
{
    if (got == want) { ++g_pass; printf("  [ok]   %s\n", what); }
    else {
        ++g_fail;
        printf("  [FAIL] %s\n         got  = %ls\n         want = %ls\n",
               what, got.c_str(), want.c_str());
    }
}

// ── 单元测试 ────────────────────────────────────────────────────────────

static void UnitTests()
{
    printf("=== handler 解析（regkey 形态） ===\n");
    {
        WcmConfigurationSchema s;
        const std::wstring xsd =
            L"<configurationSchema><xsd:schema xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\""
            L" xmlns:wcm=\"http://schemas.microsoft.com/wcm/2011\" targetNamespace=\"T\">"
            L"<xsd:element default=\"openas\" name=\"HKEY_CLASSES_ROOT_Unknown_shell_defaultValue\""
            L" type=\"xsd:string\" wcm:displayName=\"HKEY_CLASSES_ROOT\\Unknown\\shell\\defaultValue\""
            L" wcm:handler=\"regkey('HKEY_CLASSES_ROOT\\Unknown\\shell')\""
            L" wcm:legacyName=\"\" wcm:legacyType=\"REG_SZ\""
            L" wcm:scope=\"allUsers\" wcm:subScope=\"machineIndependent\" />"
            L"<xsd:element default=\"30040000\" name=\"X_EditFlags\" type=\"xsd:hexBinary\""
            L" wcm:handler=\"regkey('HKEY_CLASSES_ROOT\\batfile')\""
            L" wcm:legacyName=\"EditFlags\" wcm:legacyType=\"REG_BINARY\" wcm:scope=\"allUsers\" />"
            L"<xsd:element default=\"5\" name=\"Y_SomeDword\" type=\"xsd:unsignedInt\""
            L" wcm:handler=\"regkey('HKEY_LOCAL_MACHINE\\Software\\Foo')\""
            L" wcm:legacyName=\"Count\" wcm:legacyType=\"REG_DWORD\" wcm:scope=\"allUsers\" />"
            L"</xsd:schema></configurationSchema>";

        auto r = s.Parse(xsd);
        Check(r.ok, "解析成功");
        Check(s.Size() == 3, "3 个设置项");
        Check(s.RegistrySettingCount() == 3, "3 条都是注册表设置");

        const auto* it = s.Find(L"HKEY_CLASSES_ROOT_Unknown_shell_defaultValue");
        Check(it != nullptr, "能找到 Unknown\\shell 那条");
        if (it) {
            CheckEqW(it->RegistryKeyPath(), L"HKEY_CLASSES_ROOT\\Unknown\\shell", "handler 解出键路径");
            CheckEqW(it->legacyName, L"", "legacyName 为空 ⇒ 默认值");
            CheckEqW(it->legacyType, L"REG_SZ", "legacyType=REG_SZ");
            CheckEqW(it->defaultValue, L"openas", "default=openas");
            CheckEqW(it->scope, L"allUsers", "scope=allUsers");
        }

        auto ops = ToRegistryOperations(s);
        Check(ops.size() == 3, "转出 3 条操作");
        const WcmRegistryOperation* op = nullptr;
        for (const auto& o : ops) if (o.sourceItem == L"HKEY_CLASSES_ROOT_Unknown_shell_defaultValue") op = &o;
        Check(op != nullptr, "找到对应操作");
        if (op) {
            CheckEqW(op->root, L"HKEY_CLASSES_ROOT", "root=HKEY_CLASSES_ROOT");
            CheckEqW(op->subKey, L"Unknown\\shell", "subKey=Unknown\\shell（已去掉根）");
            CheckEqW(op->valueName, L"", "valueName 为空 ⇒ 写默认值");
            CheckEqW(op->valueType, L"REG_SZ", "valueType=REG_SZ");
            CheckEqW(op->data, L"openas", "data=openas");
        }
    }

    printf("\n=== 值转换 ===\n");
    {
        WcmSettingItem d;
        d.legacyType = L"REG_DWORD"; d.defaultValue = L"0x1f";
        auto cv = ConvertSettingValue(d);
        Check(cv.ok && cv.isDword && cv.dword == 31, "REG_DWORD 十六进制 0x1f ⇒ 31");

        WcmSettingItem d2;
        d2.legacyType = L"REG_DWORD"; d2.defaultValue = L"44";
        auto cv2 = ConvertSettingValue(d2);
        Check(cv2.ok && cv2.dword == 44, "REG_DWORD 十进制 44");

        WcmSettingItem b;
        b.legacyType = L"REG_BINARY"; b.defaultValue = L"30040000";
        auto cvb = ConvertSettingValue(b);
        Check(cvb.ok && cvb.isBinary && cvb.bytes.size() == 4, "REG_BINARY 4 字节");
        Check(cvb.bytes.size() == 4 && cvb.bytes[0] == 0x30 && cvb.bytes[1] == 0x04,
              "hexBinary 按字节序列原样保留（不翻转大小端）");

        WcmSettingItem sz;
        sz.legacyType = L"REG_EXPAND_SZ"; sz.defaultValue = L"%SystemRoot%\\System32\\x.dll";
        auto cvsz = ConvertSettingValue(sz);
        Check(cvsz.ok && cvsz.text == L"%SystemRoot%\\System32\\x.dll", "REG_EXPAND_SZ 原样");
    }

    printf("\n=== 认不出的 handler 只警告 ===\n");
    {
        WcmConfigurationSchema s;
        const std::wstring xsd =
            L"<configurationSchema><xsd:schema xmlns:xsd=\"x\" xmlns:wcm=\"y\">"
            L"<xsd:element name=\"A\" type=\"xsd:string\" default=\"1\" wcm:handler=\"weird('HKLM\\X')\" />"
            L"<xsd:element name=\"B\" type=\"xsd:string\" default=\"2\" />"
            L"</xsd:schema></configurationSchema>";
        auto r = s.Parse(xsd);
        Check(r.ok, "解析成功（不失败）");
        Check(r.warnings.size() == 2, "记了 2 条警告");
        Check(s.RegistrySettingCount() == 0, "可执行的注册表设置为 0");
    }

    printf("\n=== 缺少根前缀的键路径被拒 ===\n");
    {
        WcmConfigurationSchema s;
        const std::wstring xsd =
            L"<configurationSchema><xsd:schema xmlns:xsd=\"x\" xmlns:wcm=\"y\">"
            L"<xsd:element name=\"A\" type=\"xsd:string\" default=\"1\""
            L" wcm:handler=\"regkey('Software\\\\Foo')\" wcm:legacyName=\"V\" wcm:legacyType=\"REG_SZ\" />"
            L"</xsd:schema></configurationSchema>";
        s.Parse(xsd);
        std::vector<std::wstring> warn;
        auto ops = ToRegistryOperations(s, &warn);
        Check(ops.empty(), "没有可识别的根 ⇒ 不产出操作");
        Check(!warn.empty(), "并记了警告");
    }
}

// ── 真实清单验收 ────────────────────────────────────────────────────────

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

    size_t start = 0;
    if (buf.size() >= 3 && (unsigned char)buf[0] == 0xEF && (unsigned char)buf[1] == 0xBB && (unsigned char)buf[2] == 0xBF)
        start = 3;
    int wlen = MultiByteToWideChar(CP_UTF8, 0, buf.data() + start, (int)(buf.size() - start), nullptr, 0);
    std::wstring w((size_t)wlen, L'\0');
    if (wlen > 0) MultiByteToWideChar(CP_UTF8, 0, buf.data() + start, (int)(buf.size() - start), &w[0], wlen);
    return w;
}
#endif

static int RealManifest(const std::wstring& path)
{
#ifdef _WIN32
    printf("\n=== 真实清单验收：%ls ===\n", path.c_str());

    bool ok = false;
    std::wstring text = ReadFileUtf8(path, ok);
    if (!ok) { printf("  读文件失败\n"); return 1; }

    ComponentManifest m;
    auto mr = ParseComponentManifest(text, m);
    if (!mr.ok) {
        printf("  清单解析失败：%ls\n", mr.error.c_str());
        g_fail++;
        return 1;
    }
    printf("  清单 OK：identity=%ls，registryValue=%zu，xsd:element=%zu\n",
           m.identity.Name().c_str(), m.RegistryValueCount(),
           m.configurationSchema.elementNames.size());

    WcmConfigurationSchema schema;
    auto sr = schema.Parse(m.configurationSchema.xsdText);
    if (!sr.ok) {
        printf("  WCM parse failed: len=%zu\n", sr.error.size());
        {
            std::string nb;
            for (wchar_t c : sr.error) nb += (c < 128 ? (char)c : '.');
            printf("  err(ascii)=%s\n", nb.c_str());
        }
        g_fail++;
        return 1;
    }

    printf("  WCM：设置项 %zu，其中可执行注册表设置 %zu\n",
           schema.Size(), schema.RegistrySettingCount());
    if (!sr.warnings.empty()) {
        printf("  警告 %zu 条（前 3）：\n", sr.warnings.size());
        for (size_t i = 0; i < sr.warnings.size() && i < 3; ++i)
            printf("    %ls\n", sr.warnings[i].c_str());
    }

    auto ops = ToRegistryOperations(schema);
    printf("  推导出注册表操作 %zu 条\n", ops.size());

    // ★ 关键验收：那笔被 UCPD 拦下的操作能不能自动推出来
    const WcmRegistryOperation* target = nullptr;
    for (const auto& o : ops) {
        if (o.subKey == L"Unknown\\shell" && o.valueName.empty()) { target = &o; break; }
    }
    if (target) {
        printf("  ★ 找到 Unknown\\shell 的默认值操作：\n");
        printf("      root=%ls  subKey=%ls  valueName=(默认)  type=%ls  data=%ls  scope=%ls\n",
               target->root.c_str(), target->subKey.c_str(), target->valueType.c_str(),
               target->data.c_str(), target->scope.c_str());
        Check(target->root == L"HKEY_CLASSES_ROOT", "★ root 正确");
        Check(target->subKey == L"Unknown\\shell", "★ subKey 正确");
        Check(target->valueName.empty(), "★ 是默认值");
        Check(_wcsicmp(target->valueType.c_str(), L"REG_SZ") == 0, "★ 类型 REG_SZ");
        Check(target->data == L"openas", "★ 数据 openas —— 与 CBS 被拦的那一笔完全一致");
    } else {
        printf("  （这份清单里没有 Unknown\\shell）\n");
    }

    // 统计一下根与作用域分布
    size_t hkcr = 0, hklm = 0, hkcu = 0, allUsers = 0, perUser = 0;
    for (const auto& o : ops) {
        if (_wcsicmp(o.root.c_str(), L"HKEY_CLASSES_ROOT") == 0) ++hkcr;
        else if (_wcsicmp(o.root.c_str(), L"HKEY_LOCAL_MACHINE") == 0) ++hklm;
        else if (_wcsicmp(o.root.c_str(), L"HKEY_CURRENT_USER") == 0) ++hkcu;
        if (_wcsicmp(o.scope.c_str(), L"allUsers") == 0) ++allUsers;
        else if (_wcsicmp(o.scope.c_str(), L"perUser") == 0) ++perUser;
    }
    printf("  根分布：HKCR=%zu HKLM=%zu HKCU=%zu ； 作用域：allUsers=%zu perUser=%zu\n",
           hkcr, hklm, hkcu, allUsers, perUser);
    return 0;
#else
    (void)path;
    return 0;
#endif
}

int main(int argc, char** argv)
{
    UnitTests();

    int rc = 0;
    if (argc >= 2) {
        std::wstring p;
        for (const char* q = argv[1]; *q; ++q) p += (wchar_t)(unsigned char)*q;
        rc = RealManifest(p);
    }

    printf("\n================================\n");
    printf("  通过 %d，失败 %d\n", g_pass, g_fail);
    printf("================================\n");
    return (g_fail == 0 && rc == 0) ? 0 : 1;
}
