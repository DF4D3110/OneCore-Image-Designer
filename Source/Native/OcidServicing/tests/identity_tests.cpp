// OCID - 装配标识的单元测试
//
// 这些用例都用**真实世界里出现过的标识形式**，因为这一块的唯一价值就是"和 Windows 对得上"：
// 规范字符串会被用作目录名、清单名、注册表键的一部分，错一个字符就和镜像里已有的东西对不上。

#include "ocid/Identity.h"

#include <cstdio>
#include <string>

using namespace ocid;

static int g_fail = 0;
static int g_pass = 0;

static void Check(bool cond, const char* what)
{
    if (cond) { ++g_pass; printf("  [ok]   %s\n", what); }
    else      { ++g_fail; printf("  [FAIL] %s\n", what); }
}

static void CheckEq(const std::wstring& got, const std::wstring& want, const char* what)
{
    if (got == want) {
        ++g_pass;
        printf("  [ok]   %s\n", what);
    } else {
        ++g_fail;
        // 用 %ls 打宽字符串（控制台若是 GBK 会显示乱码，但内容是对的）
        printf("  [FAIL] %s\n", what);
        printf("         got  = %ls\n", got.c_str());
        printf("         want = %ls\n", want.c_str());
    }
}

int main()
{
    printf("=== 规范字符串形式 ===\n");
    {
        AssemblyIdentity id;
        id.SetName(L"Microsoft-Windows-Shell32");
        id.SetVersion(L"6.2.8061.0");
        id.SetArchitecture(L"arm");
        id.SetPublicKeyToken(L"31bf3856ad364e35");
        id.SetLanguage(L"zh-CN");
        id.SetScope(VersionScope::NonSxS);

        CheckEq(id.ToString(),
                L"Microsoft-Windows-Shell32~6.2.8061.0~arm~31bf3856ad364e35~zh-CN~1",
                "完整六段");
        CheckEq(id.ToString(false),
                L"Microsoft-Windows-Shell32~~arm~31bf3856ad364e35~zh-CN~1",
                "versionless（版本段留空但分隔符保留）");
    }

    printf("\n=== 中立语言 / SxS ===\n");
    {
        AssemblyIdentity id;
        id.SetName(L"Microsoft-Windows-Foundation");
        id.SetVersion(L"10.0.19041.1");
        id.SetArchitecture(L"amd64");
        id.SetPublicKeyToken(L"31bf3856ad364e35");
        id.SetScope(VersionScope::SxS);
        CheckEq(id.ToString(),
                L"Microsoft-Windows-Foundation~10.0.19041.1~amd64~31bf3856ad364e35~~2",
                "language 留空、scope=2");
    }

    printf("\n=== 解析回环 ===\n");
    {
        const std::wstring s = L"Microsoft-Windows-Shell32~6.2.8061.0~arm~31bf3856ad364e35~zh-CN~1";
        AssemblyIdentity id;
        Check(AssemblyIdentity::TryParse(s, id), "TryParse 成功");
        CheckEq(id.ToString(), s, "解析后再输出，字符串完全一致（回环）");
        Check(id.Scope() == VersionScope::NonSxS, "scope 解析为 NonSxS");
    }
    {
        // 只有名字（moniker 形式）也要能解析
        AssemblyIdentity id;
        Check(AssemblyIdentity::TryParse(L"Some-Package", id), "单段 moniker 可解析");
        Check(id.IsMoniker() == false, "有名字就不算 IsMoniker");
    }

    printf("\n=== 属性大小写不敏感 ===\n");
    {
        AssemblyIdentity a, b;
        a.SetAttribute(L"Name", L"Foo");
        a.SetAttribute(L"VERSION", L"1.0.0.0");
        b.SetAttribute(L"name", L"Foo");
        b.SetAttribute(L"version", L"1.0.0.0");
        Check(a.IsEqual(b), "Name/name、VERSION/version 视为同一属性");
        CheckEq(a.ToString(), b.ToString(), "大小写不同但规范字符串相同");
    }

    printf("\n=== 空值等于无属性 ===\n");
    {
        AssemblyIdentity a, b;
        a.SetAttribute(L"name", L"Foo");
        a.SetAttribute(L"language", L"");       // 清单里常见的 language=""
        b.SetAttribute(L"name", L"Foo");
        Check(a.IsEqual(b), "language=\"\" 不影响相等性");
        Check(!a.HasAttribute(L"language"), "空值不留下属性");
    }

    printf("\n=== IsRelated（同名同架构不同版本/语言） ===\n");
    {
        AssemblyIdentity base;
        base.SetName(L"Microsoft-Windows-Shell32");
        base.SetVersion(L"6.2.8061.0");
        base.SetArchitecture(L"arm");
        base.SetPublicKeyToken(L"31bf3856ad364e35");
        base.SetLanguage(L"zh-CN");
        base.SetScope(VersionScope::NonSxS);

        AssemblyIdentity otherLang = base;
        otherLang.SetLanguage(L"en-US");

        AssemblyIdentity otherVer = base;
        otherVer.SetVersion(L"6.2.9000.0");

        AssemblyIdentity otherName = base;
        otherName.SetName(L"Microsoft-Windows-Other");

        Check(base.IsRelated(otherLang),     "语言不同 ⇒ 相关");
        Check(base.IsRelated(otherVer),      "版本不同 ⇒ 相关");
        Check(!base.IsRelated(otherName),    "名字不同 ⇒ 不相关");
        Check(!base.IsRelated(base),         "与自己相等 ⇒ 不算相关");
        Check(!base.IsEqual(otherLang),      "语言不同 ⇒ 不相等");
    }

    printf("\n=== IsFastEqual / IsVersionlessEqual ===\n");
    {
        AssemblyIdentity a, b;
        a.SetName(L"P"); a.SetVersion(L"1.0.0.0"); a.SetArchitecture(L"x86");
        b = a;
        Check(a.IsFastEqual(b), "同标识 IsFastEqual");
        b.SetVersion(L"2.0.0.0");
        Check(!a.IsFastEqual(b), "版本不同 ⇒ IsFastEqual 为假");
        Check(a.IsVersionlessEqual(b), "忽略版本后相等");
        Check(a.IsRelated(b), "版本不同 ⇒ 相关");
    }

    printf("\n=== IdentitySet 去重 ===\n");
    {
        IdentitySet set;
        AssemblyIdentity a;
        a.SetName(L"Foo"); a.SetVersion(L"1.0.0.0"); a.SetArchitecture(L"x86");
        AssemblyIdentity a2 = a;
        AssemblyIdentity b = a;
        b.SetName(L"Bar");

        Check(set.Add(a),       "首次加入成功");
        Check(!set.Add(a2),     "同标识重复加入被拒");
        Check(set.Add(b),       "不同标识加入成功");
        Check(set.Size() == 2,  "集合大小为 2");
        Check(set.Contains(a),  "Contains 命中");
    }

    printf("\n================================\n");
    printf("  通过 %d，失败 %d\n", g_pass, g_fail);
    printf("================================\n");
    return g_fail == 0 ? 0 : 1;
}
