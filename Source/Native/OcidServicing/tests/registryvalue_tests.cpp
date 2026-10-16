// OCID - 注册表值类型与编码测试
//
// 这些断言不是"我觉得应该是这样"，而是从三处硬证据抄下来的（见 RegistryValue.h）：
//   [表]   wcp.dll 只读表 dump 出来的 14 个合法类型名与 operationHint/systemProtection
//   [语料] 70224 份真实组件清单的取值分布
//   [活机] 拿清单声明查本机真实注册表逐条比对
//
// 最要命的一条是 REG_QWORD：清单里 `0001000000000000` 在活注册表里是 0x100，
// 也就是这 16 个 hex 字符**就是那 8 个字节**，不是数值。写成数值就会静默错 2^48 倍。

#include "ocid/RegistryValue.h"

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

static void CheckHex(const std::vector<uint8_t>& got, const char* wantHex, const char* what)
{
    std::wstring h = BytesToHex(got);
    std::wstring want;
    for (const char* p = wantHex; *p; ++p) want += (wchar_t)(unsigned char)*p;
    if (h == want) { ++g_pass; printf("  [ok]   %s\n", what); }
    else {
        ++g_fail;
        printf("  [FAIL] %s\n         got  = %ls\n         want = %ls\n", what, h.c_str(), want.c_str());
    }
}

static std::wstring W(const char* a)
{
    std::wstring s;
    for (const char* p = a; *p; ++p) s += (wchar_t)(unsigned char)*p;
    return s;
}

// ── 类型名解析 ──────────────────────────────────────────────────────────────

static void TestTypeNames()
{
    printf("=== valueType 名（wcp TRegValueTypeMap 的 14 条） ===\n");

    struct { const char* name; RegValueType t; } cases[] = {
        { "REG_NONE",                       RegValueType::None },
        { "REG_SZ",                         RegValueType::Sz },
        { "REG_EXPAND_SZ",                  RegValueType::ExpandSz },
        { "REG_MULTI_SZ",                   RegValueType::MultiSz },
        { "REG_BINARY",                     RegValueType::Binary },
        { "REG_DWORD",                      RegValueType::Dword },
        { "REG_DWORD_LITTLE_ENDIAN",        RegValueType::DwordLittleEndian },
        { "REG_DWORD_BIG_ENDIAN",           RegValueType::DwordBigEndian },
        { "REG_LINK",                       RegValueType::Link },
        { "REG_RESOURCE_LIST",              RegValueType::ResourceList },
        { "REG_FULL_RESOURCE_DESCRIPTOR",   RegValueType::FullResourceDescriptor },
        { "REG_RESOURCE_REQUIREMENTS_LIST", RegValueType::ResourceRequirementsList },
        { "REG_QWORD",                      RegValueType::Qword },
        { "REG_QWORD_LITTLE_ENDIAN",        RegValueType::QwordLittleEndian },
    };

    for (const auto& c : cases) {
        RegValueType t = RegValueType::Unknown;
        bool ok = ParseRegValueType(W(c.name), t);
        Check(ok && t == c.t, c.name);
    }

    RegValueType t = RegValueType::Unknown;
    Check(ParseRegValueType(L"reg_qword", t) && t == RegValueType::Qword, "大小写不敏感（wcp 用 FsnpDowncaseChar）");
    Check(!ParseRegValueType(L"REG_WHAT", t), "未知类型必须失败，不许猜近似的");
    Check(!ParseRegValueType(L"", t), "空类型必须失败");
    Check(ParseRegValueType(L"REG_QWORD_LITTLE_ENDIAN", t) && t == RegValueType::QwordLittleEndian, "QWORD_LE 是独立名字");
}

static void TestWin32Codes()
{
    printf("=== wcp 枚举 → 真实 Win32 REG_* 码（两套不是一回事） ===\n");
    // 关键：wcp 的 REG_BINARY=4 / REG_DWORD=5，Win32 的是 3 / 4。
    Check(static_cast<int>(RegValueType::Binary) == 4, "wcp 枚举里 REG_BINARY = 4");
    Check(static_cast<int>(RegValueType::Dword) == 5,  "wcp 枚举里 REG_DWORD  = 5");
    Check(Win32RegType(RegValueType::Binary) == 3, "Win32: REG_BINARY = 3");
    Check(Win32RegType(RegValueType::Dword) == 4,  "Win32: REG_DWORD  = 4");
    Check(Win32RegType(RegValueType::DwordLittleEndian) == 4, "REG_DWORD_LITTLE_ENDIAN 折叠到 REG_DWORD");
    Check(Win32RegType(RegValueType::DwordBigEndian) == 5, "REG_DWORD_BIG_ENDIAN = 5");
    Check(Win32RegType(RegValueType::Qword) == 11, "Win32: REG_QWORD = 11");
    Check(Win32RegType(RegValueType::QwordLittleEndian) == 11, "REG_QWORD_LITTLE_ENDIAN 折叠到 REG_QWORD");
    Check(Win32RegType(RegValueType::MultiSz) == 7, "Win32: REG_MULTI_SZ = 7");
    Check(Win32RegType(RegValueType::None) == 0, "Win32: REG_NONE = 0");
    Check(Win32RegType(RegValueType::Unknown) == 0xFFFFFFFFu, "未知类型返回 0xFFFFFFFF（不是 REG_NONE！）");
}

// ── 编码 ────────────────────────────────────────────────────────────────────

static void TestEncode()
{
    printf("=== 值编码 ===\n");

    // REG_SZ：UTF-16LE + 结尾 NUL。'A' = 0x41。
    {
        auto b = EncodeRegValue(L"REG_SZ", L"A");
        Check(b.ok, "REG_SZ 编码成功");
        CheckHex(b.bytes, "41000000", "REG_SZ 'A' -> 41 00 00 00");
        Check(b.typeCode == 1, "REG_SZ typeCode=1");
    }

    // REG_DWORD：100% 是 0x%08X（15641/15641 实测）。
    {
        auto b = EncodeRegValue(L"REG_DWORD", L"0x00000004");
        Check(b.ok, "REG_DWORD 编码成功");
        CheckHex(b.bytes, "04000000", "REG_DWORD 0x00000004 -> 04 00 00 00");
    }
    {
        auto b = EncodeRegValue(L"REG_DWORD", L"0x00000192");
        CheckHex(b.bytes, "92010000", "REG_DWORD 0x00000192 -> 92 01 00 00");
    }
    {
        auto b = EncodeRegValue(L"REG_DWORD", L"7");
        Check(b.ok, "REG_DWORD 也接受十进制");
        CheckHex(b.bytes, "07000000", "REG_DWORD 十进制 7 -> 07 00 00 00");
    }
    {
        auto b = EncodeRegValue(L"REG_DWORD", L"not-a-number");
        Check(!b.ok, "REG_DWORD 非法文本必须失败");
    }

    // ★ REG_QWORD：16 个 hex 字符 = 8 个字节，按低地址→高地址。活机 23/23 命中。
    {
        auto b = EncodeRegValue(L"REG_QWORD", L"0001000000000000");
        Check(b.ok, "REG_QWORD 编码成功");
        CheckHex(b.bytes, "0001000000000000", "REG_QWORD 0001000000000000 -> 00 01 00 00 00 00 00 00");
        Check(b.typeCode == 11, "REG_QWORD typeCode=11");
    }
    {
        auto b = EncodeRegValue(L"REG_QWORD", L"0800000000000000");
        CheckHex(b.bytes, "0800000000000000", "REG_QWORD 0800000000000000（活机 = 0x8）");
    }
    {
        auto b = EncodeRegValue(L"REG_QWORD", L"0000000200000000");
        CheckHex(b.bytes, "0000000200000000", "REG_QWORD 0000000200000000（活机 = 0x2000000）");
    }
    {
        auto b = EncodeRegValue(L"REG_QWORD", L"0x100");
        Check(!b.ok, "REG_QWORD 必须 16 位 hex，拒绝 0x 数值写法");
    }
    {
        auto b = EncodeRegValue(L"REG_QWORD", L"00000000000001");
        Check(!b.ok, "REG_QWORD 拒绝 14 位");
    }
    {
        auto b = EncodeRegValue(L"REG_QWORD", L"00000000000000GG");
        Check(!b.ok, "REG_QWORD 拒绝非 hex 字符");
    }

    // REG_BINARY：裸 hex 串 = 字节序列。
    {
        auto b = EncodeRegValue(L"REG_BINARY", L"0100048060000000");
        Check(b.ok, "REG_BINARY 编码成功");
        CheckHex(b.bytes, "0100048060000000", "REG_BINARY 原样");
        Check(b.typeCode == 3, "REG_BINARY typeCode=3");
    }
    {
        auto b = EncodeRegValue(L"REG_BINARY", L"ABC");
        Check(!b.ok, "REG_BINARY 拒绝奇数长度（不许悄悄补 0）");
    }

    // REG_NONE：158/158 全是空。
    {
        auto b = EncodeRegValue(L"REG_NONE", L"");
        Check(b.ok, "REG_NONE 编码成功");
        Check(b.bytes.empty(), "REG_NONE -> 零字节");
        Check(b.typeCode == 0, "REG_NONE typeCode=0");
    }

    // ★ REG_MULTI_SZ：引号是【定界符】，不是数据（活机证明引号消失）。
    {
        auto b = EncodeRegValue(L"REG_MULTI_SZ", L"\"storflt\"");
        Check(b.ok, "REG_MULTI_SZ 单条编码成功");
        // s t o r f l t 各 2 字节 + 结尾 NUL + 列表终止符 NUL
        CheckHex(b.bytes,
                 "730074006F00720066006C00740000000000",
                 "REG_MULTI_SZ \"storflt\" -> 去掉引号 + 双 NUL 终止");
        Check(b.typeCode == 7, "REG_MULTI_SZ typeCode=7");
    }
    {
        auto b = EncodeRegValue(L"REG_MULTI_SZ", L"\"html\",\"htm\"");
        Check(b.ok, "REG_MULTI_SZ 多条编码成功");
        CheckHex(b.bytes,
                 "680074006D006C000000680074006D0000000000",
                 "REG_MULTI_SZ \"html\",\"htm\" -> html\\0 htm\\0 \\0");
    }
    {
        auto b = EncodeRegValue(L"REG_MULTI_SZ", L"");
        Check(b.ok, "REG_MULTI_SZ 空值编码成功");
        CheckHex(b.bytes, "0000", "REG_MULTI_SZ 空 -> 只有终止符");
    }

    // 资源类型：和 BINARY 一样是裸 hex。
    {
        auto b = EncodeRegValue(L"REG_RESOURCE_REQUIREMENTS_LIST", L"6804000000000000");
        Check(b.ok, "REG_RESOURCE_REQUIREMENTS_LIST 编码成功");
        Check(b.typeCode == 10, "REG_RESOURCE_REQUIREMENTS_LIST typeCode=10");
    }
    {
        auto b = EncodeRegValue(L"REG_RESOURCE_LIST", L"01000000");
        Check(b.ok, "REG_RESOURCE_LIST 编码成功");
        Check(b.typeCode == 8, "REG_RESOURCE_LIST typeCode=8");
    }

    // 未展开的 $(…) 宏必须被标出来（语料里 REG_SZ 有 5277 条含宏）
    {
        auto b = EncodeRegValue(L"REG_EXPAND_SZ", L"$(runtime.startMenu)\\Programs\\A.lnk");
        Check(b.ok, "REG_EXPAND_SZ 带宏仍能编码");
        Check(b.hasUnresolvedMacro, "标出 hasUnresolvedMacro（不许静默写下去）");
        Check(b.typeCode == 2, "REG_EXPAND_SZ typeCode=2");
    }
    {
        auto b = EncodeRegValue(L"REG_SZ", L"plain");
        Check(!b.hasUnresolvedMacro, "无宏时不标记");
    }
}

// ── operationHint / merge ───────────────────────────────────────────────────

static void TestMerge()
{
    printf("=== operationHint 与 Merge（RawRegistryValue::Merge 照抄） ===\n");

    RegValueOperationHint h = RegValueOperationHint::Unknown;
    Check(ParseRegValueOperationHint(L"replace", h) && h == RegValueOperationHint::Replace, "replace = 1");
    Check(ParseRegValueOperationHint(L"append",  h) && h == RegValueOperationHint::Append,  "append  = 2");
    Check(ParseRegValueOperationHint(L"prepend", h) && h == RegValueOperationHint::Prepend, "prepend = 3");
    Check(!ParseRegValueOperationHint(L"merge", h), "非法 hint 必须失败（CBS 会抛 E_INVALIDARG 0x80070057）");
    Check(static_cast<int>(RegValueOperationHint::Replace) == 1, "replace 枚举值 = 1（InternalInstall 的默认值）");
    Check(static_cast<int>(RegValueOperationHint::Append)  == 2, "append 枚举值 = 2");
    Check(static_cast<int>(RegValueOperationHint::Prepend) == 3, "prepend 枚举值 = 3");

    // REG_DWORD：按位 OR
    {
        std::vector<uint8_t> a = { 0x0F, 0x00, 0x00, 0x00 };
        std::vector<uint8_t> b = { 0xF0, 0x01, 0x00, 0x00 };
        std::vector<uint8_t> out;
        Check(MergeRegValueBytes(4, a, b, false, out), "DWORD 合并成功");
        CheckHex(out, "FF010000", "DWORD append -> 按位 OR");
    }

    // 字符串：去尾 NUL 后拼接
    {
        std::vector<uint8_t> a = { 'A', 0, 0, 0 };   // L"A\0"
        std::vector<uint8_t> b = { 'B', 0, 0, 0 };   // L"B\0"
        std::vector<uint8_t> out;
        Check(MergeRegValueBytes(1, a, b, false, out), "字符串合并成功");
        CheckHex(out, "410042000000", "REG_SZ append -> AB\\0");
    }

    // ★ prepend 的顺序：incoming 在前
    {
        std::vector<uint8_t> existing  = { 'B', 0, 0, 0 };
        std::vector<uint8_t> incoming  = { 'A', 0, 0, 0 };
        std::vector<uint8_t> out;
        Check(MergeRegValueBytes(1, existing, incoming, true, out), "prepend 成功");
        CheckHex(out, "410042000000", "prepend -> incoming 在前（A 然后 B）");
    }

    // MULTI_SZ 也走字符串通路。★ 期望值是照反编译算出来的，不是照"看起来对"算的：
    //   a = "a" 编码 = 61 00 | 00 00(项NUL) | 00 00(列表NUL) = 61 00 00 00 00 00
    //   Merge 只从 target 去掉【一个】宽 NUL ⇒ 61 00 00 00，再把 source 整体接上
    //   ⇒ 61 00 00 00 62 00 00 00 00 00
    //   （若按"两边都 strip 再补一个 NUL"会得到 61 00 62 00 00 00，少一个空项。）
    {
        auto ba = EncodeRegValue(L"REG_MULTI_SZ", L"\"a\"");
        auto bb = EncodeRegValue(L"REG_MULTI_SZ", L"\"b\"");
        std::vector<uint8_t> out;
        Check(MergeRegValueBytes(7, ba.bytes, bb.bytes, false, out), "MULTI_SZ 合并成功");
        CheckHex(out, "61000000620000000000", "MULTI_SZ append -> 只从 target 去一个宽 NUL，source 整体接上");
    }

    // REG_LINK(6) 也在字符串通路里（cmiv2 L240598 `v8 > 5 && v8 <= 7`）
    {
        std::vector<uint8_t> a = { 'X', 0, 0, 0 };
        std::vector<uint8_t> b = { 'Y', 0, 0, 0 };
        std::vector<uint8_t> out;
        Check(MergeRegValueBytes(6, a, b, false, out), "REG_LINK 走字符串通路");
        CheckHex(out, "580059000000", "REG_LINK append -> XY\\0");
    }

    // REG_BINARY(3) 不在字符串通路 ⇒ 直接拼接（不去 NUL）
    {
        std::vector<uint8_t> a = { 0x01, 0x00 };
        std::vector<uint8_t> b = { 0x02, 0x00 };
        std::vector<uint8_t> out;
        Check(MergeRegValueBytes(3, a, b, false, out), "REG_BINARY 合并成功");
        CheckHex(out, "01000200", "REG_BINARY append -> 直接拼接（不去 NUL）");
    }

    // target 长度 < 2 时字符串通路拒绝（cmiv2 L240606 抛 E_INVALIDARG）
    {
        std::vector<uint8_t> a = { 0x41 };
        std::vector<uint8_t> b = { 'B', 0, 0, 0 };
        std::vector<uint8_t> out;
        Check(!MergeRegValueBytes(1, a, b, false, out), "字符串通路 target 长度 < 2 时拒绝");
    }

    // DWORD 长度不对 ⇒ 拒绝（cmiv2 L240580/L240585 抛 E_INVALIDARG；宁可跳过，不要写坏）
    {
        std::vector<uint8_t> a = { 0x01, 0x02 };
        std::vector<uint8_t> b = { 0x03, 0x04 };
        std::vector<uint8_t> out;
        Check(!MergeRegValueBytes(4, a, b, false, out), "DWORD 长度不是 4 时拒绝合并");
    }

    // DWORD 一边长度不对也要拒绝
    {
        std::vector<uint8_t> a = { 0x01, 0x02, 0x03, 0x04 };
        std::vector<uint8_t> b = { 0x03, 0x04 };
        std::vector<uint8_t> out;
        Check(!MergeRegValueBytes(4, a, b, false, out), "DWORD 任一边长度不是 4 都拒绝");
    }
}

// ── systemProtection ────────────────────────────────────────────────────────

static void TestProtection()
{
    printf("=== systemProtection（TRegKeyProtectionFlagsMap 的 10 条） ===\n");

    struct { const char* name; RegKeyProtection p; } cases[] = {
        { "readOnlyIgnoreWrites",          RegKeyProtection::ReadOnlyIgnoreWrites },
        { "readOnlyFailWrites",            RegKeyProtection::ReadOnlyFailWrites },
        { "OSOnlyIgnoreWrites",            RegKeyProtection::OSOnlyIgnoreWrites },
        { "OSOnlyFailWrites",              RegKeyProtection::OSOnlyFailWrites },
        { "transacted",                    RegKeyProtection::Transacted },
        { "applicationVirtualized",        RegKeyProtection::ApplicationVirtualized },
        { "userVirtualized",               RegKeyProtection::UserVirtualized },
        { "applicationAndUserVirtualized", RegKeyProtection::ApplicationAndUserVirtualized },
        { "inherit",                       RegKeyProtection::Inherit },
        { "notProtected",                  RegKeyProtection::NotProtected },
    };
    for (const auto& c : cases) {
        RegKeyProtection p = RegKeyProtection::Unknown;
        bool ok = ParseRegKeyProtection(W(c.name), p);
        Check(ok && p == c.p, c.name);
    }

    RegKeyProtection p = RegKeyProtection::Unknown;
    Check(!ParseRegKeyProtection(L"readonly", p), "未列出的保护名必须失败");
    Check(static_cast<int>(RegKeyProtection::ReadOnlyIgnoreWrites) == 1, "枚举值从 1 开始");
    Check(static_cast<int>(RegKeyProtection::NotProtected) == 10, "notProtected = 10");
}

int main()
{
    TestTypeNames();
    TestWin32Codes();
    TestEncode();
    TestMerge();
    TestProtection();

    printf("\n================================\n");
    printf("  通过 %d，失败 %d\n", g_pass, g_fail);
    printf("================================\n");
    return g_fail == 0 ? 0 : 1;
}
