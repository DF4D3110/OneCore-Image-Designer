// OCID - 安全描述符（SDDL）测试
//
// 用的 SDDL 全是**从 8400 x86 语料里抓出来的原文**（`E:\sxs_lib` 的
// `<securityDescriptorDefinition>`），不是编的：
//   WRP_REGKEY_DEFAULT_SDDL / WRP_DIR_DEFAULT_SDDL / WRP_FILE_DEFAULT_SDDL
//   SHORTCUT_REGISTRY_DEFAULT_SDDL / WRP_PARENT_DIR_DEFAULT_SDDL / WRP_FILE_DIR_DEFAULT_SDDL
//   ADMIN_SDDL / PARENT_ADMIN_SDDL / EHOME_EXTENSIBILITY_KEY_SDDL
//   SHORTCUT_FILE_DEFAULT_SDDL / #MutableFileDefaultSecurityDescriptor
//
// 最想钉死的四件事：
//   ① `(inherit)` **不是** SDDL —— 它是"保持继承、别显式设置"，必须被识别出来，
//      否则会被当成畸形 SDDL 报错或者写出怪东西。
//   ② `D:NO_ACCESS_CONTROL` 是 **NULL DACL**（谁都全权），不是空 DACL（没人有权）；
//      两者在 SDDL 里长得很像，语义正相反。
//   ③ `D:P` / `D:AI` / `S:` 这些标志位与实际 ACE 计数要分得清：`S:` = 有 SACL 但 0 条 ACE。
//   ④ 文本 ↔ 二进制往返之后，结构事实必须一模一样 —— 这是"编码没写错"的唯一凭据。

#include "ocid/ConsoleUtf8.h"
#include "ocid/SecurityDescriptor.h"

#include <cstdio>
#include <string>
#include <vector>

using namespace ocid;

static int g_fail = 0, g_pass = 0;

static void Check(bool c, const char* what)
{
    if (c) { ++g_pass; printf("  [ok]   %s\n", what); }
    else   { ++g_fail; printf("  [FAIL] %s\n", what); }
}

static void CheckInt(int got, int want, const char* what)
{
    if (got == want) { ++g_pass; printf("  [ok]   %s\n", what); }
    else { ++g_fail; printf("  [FAIL] %s (got %d, want %d)\n", what, got, want); }
}

static void CheckStr(const std::wstring& got, const wchar_t* want, const char* what)
{
    if (got == want) { ++g_pass; printf("  [ok]   %s\n", what); }
    else
    {
        ++g_fail;
        printf("  [FAIL] %s\n         got  = %s\n         want = %s\n",
               what, ToUtf8(got).c_str(), ToUtf8(want).c_str());
    }
}

static bool SameShape(const SddlInfo& a, const SddlInfo& b)
{
    return a.hasOwner == b.hasOwner && a.hasGroup == b.hasGroup
        && a.hasDacl == b.hasDacl && a.hasSacl == b.hasSacl
        && a.daclPresent == b.daclPresent
        && a.daclProtected == b.daclProtected
        && a.daclAutoInherited == b.daclAutoInherited
        && a.daclNoAccessControl == b.daclNoAccessControl
        && a.saclProtected == b.saclProtected
        && a.saclAutoInherited == b.saclAutoInherited
        && a.saclNoAccessControl == b.saclNoAccessControl
        && a.daclAceCount == b.daclAceCount
        && a.saclAceCount == b.saclAceCount
        && a.ownerSid == b.ownerSid
        && a.groupSid == b.groupSid;
}

// ── 语料原文 ────────────────────────────────────────────────────────────────
static const wchar_t* SD_WRP_KEY =
    L"O:S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"
    L"G:S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"
    L"D:P(A;CI;GA;;;S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464)"
    L"(A;CI;GR;;;SY)(A;CI;GR;;;BA)(A;CI;GR;;;BU)(A;CI;GR;;;S-1-15-2-1)";

static const wchar_t* SD_WRP_DIR =
    L"O:S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"
    L"G:S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"
    L"D:P(A;CIOI;GA;;;S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464)"
    L"(A;CIOI;GRGX;;;SY)(A;CIOI;GRGX;;;BA)(A;CIOI;GRGX;;;BU)(A;CIOI;GRGX;;;S-1-15-2-1)"
    L"S:(AU;FASA;SDWDWOFW;;;WD)";

static const wchar_t* SD_WRP_FILE =
    L"O:S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"
    L"G:S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"
    L"D:P(A;;FA;;;S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464)"
    L"(A;;GRGX;;;BA)(A;;GRGX;;;SY)(A;;GRGX;;;BU)(A;;GRGX;;;S-1-15-2-1)"
    L"S:(AU;FASA;0x000D0116;;;WD)";

static const wchar_t* SD_WRP_PARENT_DIR =
    L"O:S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"
    L"G:S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"
    L"D:P(A;CI;GA;;;S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464)"
    L"(A;;0x1301bf;;;SY)(A;IOCIOI;GA;;;SY)(A;;0x1301bf;;;BA)(A;IOCIOI;GA;;;BA)"
    L"(A;CIOI;GRGX;;;BU)(A;OICIIO;GA;;;CO)(A;CIOI;GRGX;;;S-1-15-2-1)";

static const wchar_t* SD_SHORTCUT_REGISTRY =
    L"O:S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"
    L"G:S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"
    L"D:P(A;CI;GA;;;S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464)"
    L"(A;CI;GR;;;SY)(A;CI;GR;;;BA)(A;CI;GR;;;BU)(A;CI;GR;;;S-1-15-2-1)";

static const wchar_t* SD_ADMIN = L"O:BAG:SYD:(A;;GA;;;BA)(A;;GA;;;SY)(A;;GRGX;;;BU)(A;;GRGX;;;S-1-15-2-1)S:";

static const wchar_t* SD_PARENT_ADMIN =
    L"O:BAG:SYD:(A;OICI;GA;;;BA)(A;OICI;GA;;;SY)(A;OICI;GRGX;;;BU)(A;OICI;GRGX;;;S-1-15-2-1)";

static const wchar_t* SD_EHOME =
    L"D:PAI(A;OICI;GA;;;BA)(A;OICI;GA;;;SY)(A;OICI;GR;;;BU)(A;OICI;GR;;;IU)(A;OICIIO;GA;;;CO)";

// 这两个的 `D:` 段一条 ACE 都没有，只有 AI 标志
static const wchar_t* SD_SHORTCUT_FILE = L"O:SYG:SYD:AIS:AI";
static const wchar_t* SD_MUTABLE_FILE  = L"O:SYG:SYD:AIS:";

int main()
{
    SetupConsoleUtf8();

    // ── 1. (inherit) ────────────────────────────────────────────────────────
    printf("\n===== 1. (inherit) 不是 SDDL =====\n");
    Check(IsInheritSddl(L"(inherit)"),                      "(inherit)");
    Check(IsInheritSddl(L"(INHERIT)"),                      "大小写不敏感");
    Check(IsInheritSddl(L"  (inherit)  "),                  "两侧空白被忽略");
    Check(!IsInheritSddl(L""),                              "空串不是 (inherit)");
    Check(!IsInheritSddl(L"D:(A;;FA;;;BA)"),                "真 SDDL 不是 (inherit)");
    {
        SddlInfo info; std::wstring err;
        Check(!DescribeSddl(L"(inherit)", info, err),       "DescribeSddl 拒绝 (inherit)");
        Check(!err.empty(),                                 "并给出原因");
        std::vector<unsigned char> bytes;
        Check(!SddlToSelfRelative(L"(inherit)", bytes, err), "SddlToSelfRelative 拒绝 (inherit)");
        Check(bytes.empty(),                                "拒绝时不留半截字节");
    }

    // ── 2. 定义表解析 ───────────────────────────────────────────────────────
    printf("\n===== 2. 按引用名解析定义 =====\n");
    {
        SddlDefinitions defs;
        defs[L"WRP_REGKEY_DEFAULT_SDDL"] = SD_WRP_KEY;
        defs[L"SHORTCUT_FILE_DEFAULT_SDDL"] = SD_SHORTCUT_FILE;

        std::wstring sddl, err;
        Check(ResolveSddl(defs, L"WRP_REGKEY_DEFAULT_SDDL", sddl, err),  "精确名命中");
        CheckStr(sddl, SD_WRP_KEY,                                        "取到的是同一条");
        Check(ResolveSddl(defs, L"wrp_regkey_default_sddl", sddl, err),   "大小写不同也能命中");
        Check(!ResolveSddl(defs, L"WRP_DIR_DEFAULT_SDDL", sddl, err),     "没定义过的名字失败");
        Check(sddl.empty(),                                               "失败时不返回半截");
        Check(err.find(L"WRP_DIR_DEFAULT_SDDL") != std::wstring::npos,    "错误里带上缺失的名字");
        Check(!ResolveSddl(defs, L"", sddl, err),                         "空名字失败");
    }

    // ── 3. 语料原文的文本解读 ───────────────────────────────────────────────
    printf("\n===== 3. 语料原文解读 =====\n");
    {
        SddlInfo i; std::wstring err;
        Check(DescribeSddl(SD_WRP_KEY, i, err),                     "WRP_REGKEY_DEFAULT_SDDL 可解析");
        Check(i.hasOwner && i.hasGroup && i.hasDacl,                "O/G/D 三段都在");
        Check(!i.hasSacl,                                           "键的默认 SDDL 没有 SACL");
        Check(i.daclProtected,                                      "D:P —— DACL 受保护");
        Check(!i.daclAutoInherited,                                 "没有 AI");
        Check(!i.daclNoAccessControl,                               "不是 NULL DACL");
        CheckInt(i.daclAceCount, 5,                                 "5 条 DACL ACE");
        CheckInt(i.saclAceCount, 0,                                 "0 条 SACL ACE");
        CheckInt(i.AceCount(), 5,                                   "总 ACE 数");
        CheckStr(i.ownerSid, L"S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",
                 "owner SID = TrustedInstaller 那一族的 S-1-5-80");
        CheckStr(i.groupSid, L"S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",
                 "group SID 同 owner");
    }
    {
        SddlInfo i; std::wstring err;
        Check(DescribeSddl(SD_WRP_DIR, i, err),                     "WRP_DIR_DEFAULT_SDDL 可解析");
        Check(i.hasSacl,                                            "目录版带 SACL");
        Check(i.saclProtected == false,                             "S: 段没有 P 标志");
        CheckInt(i.daclAceCount, 5,                                 "5 条 DACL ACE");
        CheckInt(i.saclAceCount, 1,                                 "1 条 SACL ACE（AU;FASA…WD）");
    }
    {
        SddlInfo i; std::wstring err;
        Check(DescribeSddl(SD_WRP_FILE, i, err),                    "WRP_FILE_DEFAULT_SDDL 可解析");
        CheckInt(i.daclAceCount, 5,                                 "5 条 DACL ACE（TI/BA/SY/BU/S-1-15-2-1）");
        CheckInt(i.saclAceCount, 1,                                 "1 条 SACL ACE");
    }
    {
        SddlInfo i; std::wstring err;
        Check(DescribeSddl(SD_WRP_PARENT_DIR, i, err),              "WRP_PARENT_DIR_DEFAULT_SDDL 可解析");
        CheckInt(i.daclAceCount, 8,                                 "8 条 DACL ACE");
        Check(!i.hasSacl,                                           "没有 SACL");
    }
    {
        SddlInfo i; std::wstring err;
        Check(DescribeSddl(SD_SHORTCUT_REGISTRY, i, err),           "SHORTCUT_REGISTRY_DEFAULT_SDDL 可解析");
        CheckInt(i.daclAceCount, 5,                                 "5 条 DACL ACE");
        Check(i.daclProtected,                                      "D:P");
    }
    {
        SddlInfo i; std::wstring err;
        Check(DescribeSddl(SD_ADMIN, i, err),                       "ADMIN_SDDL 可解析");
        CheckStr(i.ownerSid, L"BA",                                 "owner = BA");
        CheckStr(i.groupSid, L"SY",                                 "group = SY");
        CheckInt(i.daclAceCount, 4,                                 "4 条 DACL ACE");
        Check(i.hasSacl,                                            "`S:` 段存在");
        CheckInt(i.saclAceCount, 0,                                 "`S:` = 有 SACL、0 条 ACE");
        Check(!i.daclProtected,                                     "D: 段没有 P/AI 标志");
    }
    {
        SddlInfo i; std::wstring err;
        Check(DescribeSddl(SD_PARENT_ADMIN, i, err),                "PARENT_ADMIN_SDDL 可解析");
        CheckInt(i.daclAceCount, 4,                                 "4 条 DACL ACE");
        Check(!i.hasSacl,                                           "没有 SACL 段");
    }
    {
        SddlInfo i; std::wstring err;
        Check(DescribeSddl(SD_EHOME, i, err),                       "EHOME_EXTENSIBILITY_KEY_SDDL 可解析");
        Check(i.daclProtected && i.daclAutoInherited,               "D:PAI = P + AI 两个标志");
        CheckInt(i.daclAceCount, 5,                                 "5 条 DACL ACE");
        Check(!i.hasOwner,                                          "没有 O: 段");
        Check(!i.hasGroup,                                          "没有 G: 段");
    }
    {
        SddlInfo i; std::wstring err;
        Check(DescribeSddl(SD_SHORTCUT_FILE, i, err),               "SHORTCUT_FILE_DEFAULT_SDDL 可解析");
        Check(i.daclAutoInherited,                                  "D:AI");
        Check(i.saclAutoInherited,                                  "S:AI");
        CheckInt(i.daclAceCount, 0,                                 "0 条 DACL ACE");
        CheckInt(i.saclAceCount, 0,                                 "0 条 SACL ACE");
    }
    {
        SddlInfo i; std::wstring err;
        Check(DescribeSddl(SD_MUTABLE_FILE, i, err),                "#MutableFileDefaultSecurityDescriptor 可解析");
        Check(i.hasOwner && i.hasGroup,                             "O:/G: 都在");
        Check(i.daclAutoInherited,                                  "D:AI");
        // ★ `D:AIS:` 要读成 **`D:AI` + `S:`（空 SACL）**，不是 `D:AIS`。
        //   所以这里是 hasSacl=true 但 saclAutoInherited=false —— 一个字符之差，
        //   把 `S` 当成 DACL 的标志就会把"有 SACL"整个丢掉。
        Check(i.hasSacl,                                            "有 SACL 段");
        Check(!i.saclAutoInherited,                                 "SACL 段没有 AI 标志");
        CheckInt(i.saclAceCount, 0,                                 "SACL 0 条 ACE");
    }

    // ── 4. NO_ACCESS_CONTROL 与畸形输入 ─────────────────────────────────────
    printf("\n===== 4. NULL DACL 与畸形输入 =====\n");
    {
        SddlInfo i; std::wstring err;
        Check(DescribeSddl(L"D:NO_ACCESS_CONTROL", i, err),         "D:NO_ACCESS_CONTROL 可解析");
        Check(i.daclNoAccessControl,                                "识别为 NULL DACL");
        Check(i.daclPresent,                                        "DACL 存在");
        CheckInt(i.daclAceCount, 0,                                 "0 条 ACE");
    }
    {
        SddlInfo i; std::wstring err;
        Check(DescribeSddl(L"D:", i, err),                          "空 DACL `D:` 可解析");
        Check(!i.daclNoAccessControl,                               "空 DACL **不是** NULL DACL");
        CheckInt(i.daclAceCount, 0,                                 "0 条 ACE");
    }
    {
        SddlInfo i; std::wstring err;
        Check(!DescribeSddl(L"", i, err),                           "空串失败");
    }
    {
        SddlInfo i; std::wstring err;
        Check(!DescribeSddl(L"X:(A;;FA;;;BA)", i, err),             "未知段前缀失败");
    }
    {
        SddlInfo i; std::wstring err;
        Check(!DescribeSddl(L"D:(A;;FA;;;BA", i, err),              "ACE 括号不匹配失败");
    }
    {
        SddlInfo i; std::wstring err;
        Check(!DescribeSddl(L"D:PAI(A;;FA;;;BA))", i, err),         "多一个右括号失败");
    }

    // ── 5. 文本 ↔ 二进制往返 ────────────────────────────────────────────────
    printf("\n===== 5. SDDL ↔ 自相对描述符往返 =====\n");
    {
        const wchar_t* all[] = {
            SD_WRP_KEY, SD_WRP_DIR, SD_WRP_FILE, SD_WRP_PARENT_DIR,
            SD_SHORTCUT_REGISTRY, SD_ADMIN, SD_PARENT_ADMIN, SD_EHOME,
            SD_SHORTCUT_FILE, SD_MUTABLE_FILE
        };
        const char* labels[] = {
            "WRP_REGKEY", "WRP_DIR", "WRP_FILE", "WRP_PARENT_DIR",
            "SHORTCUT_REGISTRY", "ADMIN", "PARENT_ADMIN", "EHOME",
            "SHORTCUT_FILE", "MUTABLE_FILE"
        };
        for (int k = 0; k < 10; ++k)
        {
            char what[128];
            std::vector<unsigned char> bytes;
            std::wstring err;

            snprintf(what, sizeof(what), "%s：SDDL → 自相对字节", labels[k]);
            if (!SddlToSelfRelative(all[k], bytes, err))
            {
                printf("  [FAIL] %s（%s）\n", what, ToUtf8(err).c_str());
                ++g_fail;
                continue;
            }
            Check(true, what);
            Check(bytes.size() >= sizeof(SECURITY_DESCRIPTOR), "  长度至少一个 SECURITY_DESCRIPTOR");

            snprintf(what, sizeof(what), "%s：自相对字节 → SDDL", labels[k]);
            std::wstring back;
            if (!SelfRelativeToSddl(bytes.data(), bytes.size(), back, err))
            {
                printf("  [FAIL] %s（%s）\n", what, ToUtf8(err).c_str());
                ++g_fail;
                continue;
            }
            Check(true, what);

            // 结构事实必须一模一样：这才是"编码没写错"的凭据。
            SddlInfo a, b;
            std::wstring e1, e2;
            snprintf(what, sizeof(what), "%s：往返后结构完全一致", labels[k]);
            if (DescribeSddl(all[k], a, e1) && DescribeSddl(back, b, e2) && SameShape(a, b))
            {
                Check(true, what);
            }
            else
            {
                ++g_fail;
                printf("  [FAIL] %s\n         orig = %s\n         back = %s\n",
                       what, ToUtf8(all[k]).c_str(), ToUtf8(back).c_str());
            }
        }
    }
    {
        std::wstring out, err;
        Check(!SelfRelativeToSddl(nullptr, 0, out, err),            "空指针被拒");
        const unsigned char junk[64] = {0};
        Check(!SelfRelativeToSddl(junk, sizeof(junk), out, err),    "全 0 字节被拒");
        const unsigned char tiny[3] = {1, 2, 3};
        Check(!SelfRelativeToSddl(tiny, sizeof(tiny), out, err),    "太短被拒");
    }

    printf("\n通过 %d / 失败 %d\n", g_pass, g_fail);
    return g_fail == 0 ? 0 : 1;
}
