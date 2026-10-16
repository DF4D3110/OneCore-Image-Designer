// OCID - CMI 注册表落地决策测试
//
// 断言来自 `cmiv2.dll` 的反编译（`CMI::Utility::RegistryInstallerImpl::InternalInstall`
// @ 0x1010E1AD 与 `RawRegistryValue::Merge` @ 0x10110423），不是"我觉得应该这样"。
//
// 这里最想钉死的是两条**反直觉**的：
//   ① 值已存在 + hint=replace + `owner=true` ⇒ **不覆盖**。
//      原先"照抄进 hive"的实现会把 owner 组件已有的值无条件盖掉。
//   ② `REG_QWORD` 在 append/prepend 时走"其它类型"分支 ⇒ **字节直接拼接，不是按位 OR**。
//      按直觉写成 OR 不会报错，只会得到完全不同的值。

#include "ocid/CmiInstaller.h"
#include "ocid/ConsoleUtf8.h"

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

static std::wstring W(const char* a)
{
    std::wstring s;
    for (const char* p = a; *p; ++p) s += (wchar_t)(unsigned char)*p;
    return s;
}

static std::vector<uint8_t> HexBytes(const char* hex)
{
    std::vector<uint8_t> v;
    auto val = [](char c) -> int {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    };
    for (size_t i = 0; hex[i] && hex[i + 1]; i += 2)
        v.push_back((uint8_t)((val(hex[i]) << 4) | val(hex[i + 1])));
    return v;
}

static void CheckHex(const std::vector<uint8_t>& got, const char* wantHex, const char* what)
{
    const std::wstring g = BytesToHex(got);
    std::wstring w;
    for (const char* p = wantHex; *p; ++p) w += (wchar_t)(unsigned char)*p;
    if (g == w) { ++g_pass; printf("  [ok]   %s\n", what); }
    else
    {
        ++g_fail;
        printf("  [FAIL] %s\n         got  = %ls\n         want = %ls\n", what, g.c_str(), w.c_str());
    }
}

/// 造一条 CMI 操作（`mechanism == "registryKeys"`）。
static WcmRegistryOperation CmiOp(const char* valueType, const char* data,
                                  const char* hint, bool owner)
{
    WcmRegistryOperation op;
    op.root = L"HKEY_LOCAL_MACHINE";
    op.subKey = L"SOFTWARE\\Some\\Key";
    op.valueName = L"V";
    op.valueType = W(valueType);
    op.data = W(data);
    op.mechanism = L"registryKeys";
    op.operationHint = hint ? W(hint) : std::wstring();
    op.owner = owner;
    return op;
}

static ExistingValue Exist(uint32_t typeCode, const char* hex)
{
    ExistingValue e;
    e.exists = true;
    e.typeCode = typeCode;
    e.data = HexBytes(hex);
    return e;
}

int main()
{
    SetupConsoleUtf8();
    printf("=== CMI 注册表落地决策 ===\n");

    // ── 值不存在：直接写，owner 完全不参与 ────────────────────────────────
    {
        WriteDecision d; std::wstring err;
        ExistingValue none;   // exists == false
        // owner=true 且值不存在 —— 仍然必须写。
        Check(DecideRegistryWrite(CmiOp("REG_SZ", "hello", "replace", true), none, d, &err),
              "值不存在 + owner=true：调用成功");
        Check(d.action == WriteAction::Write, "值不存在 + owner=true => 仍然写（owner 只在值已存在时参与）");
        CheckHex(d.data, "680065006C006C006F000000", "REG_SZ \"hello\" 编码（含结尾宽 NUL）");
        Check(d.typeCode == 1, "类型码 = 1 (REG_SZ)");
    }

    // ── ① 值已存在 + replace + owner=true ⇒ 不覆盖 ────────────────────────
    {
        WriteDecision d; std::wstring err;
        auto e = Exist(1, "6F00700065006E00610073000000");   // "openas"
        Check(DecideRegistryWrite(CmiOp("REG_SZ", "mine", "replace", true), e, d, &err),
              "replace + owner=true：调用成功");
        Check(d.action == WriteAction::SkipOwned, "replace + owner=true => SkipOwned（不覆盖）");
        printf("         note = %s\n", ToUtf8(d.note).c_str());
    }

    // ── 值已存在 + replace + owner=false ⇒ 覆盖 ──────────────────────────
    {
        WriteDecision d; std::wstring err;
        auto e = Exist(1, "6F00700065006E00610073000000");
        Check(DecideRegistryWrite(CmiOp("REG_SZ", "mine", "replace", false), e, d, &err), "replace + owner=false：调用成功");
        Check(d.action == WriteAction::Write, "replace + owner=false => 覆盖");
        CheckHex(d.data, "6D0069006E0065000000", "覆盖写入的是**新值** \"mine\"");
    }

    // ── 缺省 hint（空）按 replace 处理（反编译里初值是 1）────────────────
    {
        WriteDecision d; std::wstring err;
        auto e = Exist(1, "6F00700065006E00610073000000");
        Check(DecideRegistryWrite(CmiOp("REG_SZ", "mine", nullptr, true), e, d, &err), "hint 缺省：调用成功");
        Check(d.action == WriteAction::SkipOwned, "hint 缺省(空) => 等价 replace => owner=true 时 SkipOwned");
    }

    // ── ② append / prepend：只从先写者去掉【一个】宽 NUL ─────────────────
    {
        WriteDecision d; std::wstring err;
        auto e = Exist(1, "410042000000");                 // "AB" + NUL
        Check(DecideRegistryWrite(CmiOp("REG_SZ", "CD", "append", true), e, d, &err), "append：调用成功");
        Check(d.action == WriteAction::Write, "append => 写合并结果");
        CheckHex(d.data, "41004200430044000000", "append = existing(去尾NUL) ++ incoming");

        WriteDecision d2; std::wstring err2;
        Check(DecideRegistryWrite(CmiOp("REG_SZ", "CD", "prepend", true), e, d2, &err2), "prepend：调用成功");
        CheckHex(d2.data, "43004400410042000000", "prepend = incoming(去尾NUL) ++ existing");
    }

    // REG_MULTI_SZ 的 append：不能"两边都去 NUL 再补一个"，会少一个空项。
    {
        WriteDecision d; std::wstring err;
        // 现有 "a","b" = a\0 b\0 \0
        auto e = Exist(7, "61000000620000000000");
        Check(DecideRegistryWrite(CmiOp("REG_MULTI_SZ", "\"c\"", "append", true), e, d, &err), "MULTI_SZ append：调用成功");
        CheckHex(d.data, "6100000062000000630000000000", "MULTI_SZ append = a\\0b\\0c\\0\\0（项数不丢）");
    }

    // ── REG_DWORD 的 append ⇒ 按位 OR ────────────────────────────────────
    {
        WriteDecision d; std::wstring err;
        auto e = Exist(4, "0F000000");
        Check(DecideRegistryWrite(CmiOp("REG_DWORD", "0x00000030", "append", true), e, d, &err), "DWORD append：调用成功");
        CheckHex(d.data, "3F000000", "REG_DWORD append = 按位 OR (0x0F | 0x30 = 0x3F)");
    }

    // ── ★ REG_QWORD 的 append ⇒ 字节拼接，**不是**按位 OR ────────────────
    {
        WriteDecision d; std::wstring err;
        auto e = Exist(11, "0001000000000000");
        Check(DecideRegistryWrite(CmiOp("REG_QWORD", "0002000000000000", "append", true), e, d, &err), "QWORD append：调用成功");
        Check(d.action == WriteAction::Write, "QWORD append => 写");
        Check(d.data.size() == 16, "QWORD append 结果长度 = 16（拼接，不是 8）");
        CheckHex(d.data, "00010000000000000002000000000000",
                 "REG_QWORD append = 字节直接拼接（若写成按位 OR 会得到 8 字节且值不同）");
    }

    // ── 合并失败：类型不同 ⇒ 跳过（不是报错）──────────────────────────────
    {
        WriteDecision d; std::wstring err;
        auto e = Exist(1, "41000000");     // 现有是 REG_SZ
        Check(DecideRegistryWrite(CmiOp("REG_DWORD", "0x00000001", "append", true), e, d, &err), "类型不同的 append：调用成功");
        Check(d.action == WriteAction::SkipMergeFailed, "类型不同 => SkipMergeFailed（照 cmiv2：this[7] != a2[7]）");
    }

    // ── 合并失败：DWORD 长度不是 4 ⇒ 跳过 ────────────────────────────────
    {
        WriteDecision d; std::wstring err;
        auto e = Exist(4, "0F00");          // 长度 2
        Check(DecideRegistryWrite(CmiOp("REG_DWORD", "0x00000030", "append", true), e, d, &err), "DWORD 长度异常：调用成功");
        Check(d.action == WriteAction::SkipMergeFailed, "DWORD 长度非 4 => SkipMergeFailed（cmiv2 会抛 0x80070057，这里不抛只跳过）");
    }

    // ── operationHint 非法 ⇒ InvalidHint（跳过，不抛）────────────────────
    {
        WriteDecision d; std::wstring err;
        auto e = Exist(1, "41000000");
        Check(DecideRegistryWrite(CmiOp("REG_SZ", "x", "bogus", true), e, d, &err), "非法 hint：调用成功");
        Check(d.action == WriteAction::InvalidHint, "非法 hint => InvalidHint（对应 E_INVALIDARG，但不抛）");
        Check(!d.WillWrite(), "InvalidHint 不写");
    }
    {
        // 真实服务栈的 operationHint 表只有 replace/append/prepend 三个**名字**，
        // 没有数字形式 ⇒ 数字文本走"非法"分支。清单语料里 122722+24+1 条全是名字。
        WriteDecision d; std::wstring err;
        auto e = Exist(1, "41000000");
        Check(DecideRegistryWrite(CmiOp("REG_SZ", "x", "2", true), e, d, &err), "数字 hint \"2\"：调用成功");
        Check(d.action == WriteAction::InvalidHint, "hint 表是名字表 ⇒ \"2\" 非法（语料里也确实没有数字形态）");
    }

    // ── ★ 只对 CMI 生效：WCM/SMI 那套不看 owner、不解析 operationHint ─────
    {
        WriteDecision d; std::wstring err;
        auto op = CmiOp("REG_SZ", "openas", "replace", true);
        op.mechanism = L"wcm";                      // configurationSchema 那套
        auto e = Exist(1, "6F00700065006E00610073000000");
        Check(DecideRegistryWrite(op, e, d, &err), "WCM + 值已存在 + owner=true：调用成功");
        Check(d.action == WriteAction::Write, "WCM 机制 => 覆盖（**不**因 owner=true 而跳过）");
        CheckHex(d.data, "6F00700065006E00610073000000", "WCM 覆盖写入正确字节");
    }
    {
        WriteDecision d; std::wstring err;
        auto op = CmiOp("REG_SZ", "x", "bogus", true);
        op.mechanism = L"wcm";
        auto e = Exist(1, "41000000");
        Check(DecideRegistryWrite(op, e, d, &err), "WCM + 非法 hint：调用成功");
        Check(d.action == WriteAction::Write, "WCM 机制不解析 operationHint ⇒ 非法值也不影响（直接覆盖）");
    }

    // ── 操作本身有问题 ⇒ 返回 false ──────────────────────────────────────
    {
        WriteDecision d; std::wstring err;
        ExistingValue none;
        Check(!DecideRegistryWrite(CmiOp("REG_NOT_A_TYPE", "x", "replace", true), none, d, &err),
              "未知类型 => 返回 false");
        printf("         error = %s\n", ToUtf8(err).c_str());
    }
    {
        WriteDecision d; std::wstring err;
        ExistingValue none;
        Check(!DecideRegistryWrite(CmiOp("REG_DWORD", "not-a-number", "replace", true), none, d, &err),
              "REG_DWORD 文本非法 => 返回 false（不许用 0 顶替）");
        printf("         error = %s\n", ToUtf8(err).c_str());
    }

    // ── 现状的可读化（排障用）─────────────────────────────────────────────
    {
        const auto q = Exist(11, "0001000000000000");
        printf("         REG_QWORD 现状 = %s\n", ToUtf8(q.ToText()).c_str());
        Check(q.ToText().find(L"0x0000000000000100") != std::wstring::npos,
              "REG_QWORD 现状文本给出数值 0x100（= 镜像 0001000000000000）");

        const auto m = Exist(7, "61000000620000000000");
        printf("         REG_MULTI_SZ 现状 = %s\n", ToUtf8(m.ToText()).c_str());
        Check(m.ToText() == L"\"a\",\"b\"", "REG_MULTI_SZ 现状还原成 \"a\",\"b\"");

        const auto d4 = Exist(4, "3F000000");
        Check(d4.ToText() == L"0x0000003F", "REG_DWORD 现状还原成 0x0000003F");

        ExistingValue none;
        Check(none.ToText() == L"(不存在)", "不存在的值文本为 (不存在)");
    }

    printf("\n通过 %d / 失败 %d\n", g_pass, g_fail);
    return g_fail == 0 ? 0 : 1;
}
