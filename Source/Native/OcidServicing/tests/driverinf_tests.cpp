// OCID - DMI 台账（INF 解析 + DriverDatabase 记录）自检
//
// 这个文件钉的是"算错就会静默写坏驱动库"的三件事：
//   1. INF 的文本形态：UTF-16LE BOM、`;` 注释、`[节]`、同键重复、行尾 `\` 续行
//   2. [Version] 的二进制编码：DriverDate 是 8 字节 FILETIME，DriverVersion 是
//      `[build<<16|rev][major<<16|min]`（实测 6.2.8061.0 = 00 00 7D 1F 02 00 06 00）
//   3. 记录里的值名/类型：`%04x` 属性 id、Provider **不解析** %token%、ClassGuid 存原始字节
//
// 真机对照：E:\cbs_8061x86c.vhd（Q:，8061 x86 client）里的
// `1394.inf_x86_85b894cb1410e75c\1394.inf` 与它在 SYSTEM hive 里的 DriverPackages 记录。
// 镜像不在就 [skip]，不伪装通过。

#include "ocid/ConsoleUtf8.h"
#include "ocid/DriverInf.h"
#include "ocid/DriverPackage.h"

#include <windows.h>

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

static void CheckInt(long long got, long long want, const char* what)
{
    if (got == want) { ++g_pass; printf("  [ok]   %s\n", what); }
    else { ++g_fail; printf("  [FAIL] %s (got %lld, want %lld)\n", what, got, want); }
}

static void CheckStr(const std::wstring& got, const std::wstring& want, const char* what)
{
    if (got == want) { ++g_pass; printf("  [ok]   %s\n", what); }
    else
    {
        ++g_fail;
        printf("  [FAIL] %s\n         got  = %s\n         want = %s\n",
               what, ToUtf8(got).c_str(), ToUtf8(want).c_str());
    }
}

static std::wstring HexOf(const std::vector<std::uint8_t>& bytes)
{
    static const wchar_t* digits = L"0123456789abcdef";
    std::wstring out;
    for (const std::uint8_t b : bytes)
    {
        out.push_back(digits[(b >> 4) & 0xF]);
        out.push_back(digits[b & 0xF]);
    }
    return out;
}

static std::vector<std::uint8_t> Utf16LeWithBom(const std::wstring& text)
{
    std::vector<std::uint8_t> bytes;
    bytes.push_back(0xFF);
    bytes.push_back(0xFE);
    for (const wchar_t ch : text)
    {
        bytes.push_back(static_cast<std::uint8_t>(ch & 0xFF));
        bytes.push_back(static_cast<std::uint8_t>((ch >> 8) & 0xFF));
    }
    return bytes;
}

// 真机镜像里找一个 FileRepository 目录下的 INF；找不到返回空串。
// FindFirstFileW 的通配符只在最后一段生效，所以先列目录再拼 INF 名。
static std::wstring FindImageInf(const wchar_t* storePrefix, const wchar_t* infName)
{
    const std::wstring repository = L"Q:\\Windows\\System32\\DriverStore\\FileRepository\\";
    std::wstring pattern = repository + storePrefix;
    WIN32_FIND_DATAW data = {};
    const HANDLE handle = FindFirstFileW(pattern.c_str(), &data);
    if (handle == INVALID_HANDLE_VALUE)
    {
        return std::wstring();
    }
    std::wstring directory;
    do
    {
        if ((data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0)
        {
            continue;
        }
        if (data.cFileName[0] == L'.')
        {
            continue;
        }
        directory = repository + data.cFileName;
        break;
    } while (FindNextFileW(handle, &data) != FALSE);
    FindClose(handle);
    if (directory.empty())
    {
        return std::wstring();
    }
    const std::wstring candidate = directory + L"\\" + infName;
    const DWORD attributes = GetFileAttributesW(candidate.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
    {
        return std::wstring();
    }
    return candidate;
}

// ---------------------------------------------------------------------------

static void ParseSynthetic()
{
    printf("\n[1] INF 文本形态\n");
    const std::wstring text =
        L"; build stamp\r\n"
        L"[Version]\r\n"
        L"Signature=\"$Windows NT$\"\r\n"
        L"Provider=%MSFT%\r\n"
        L"ClassGuid={6BDD1FC1-810F-11D0-BEC7-08002BE2092F}\r\n"
        L"DriverVer=06/21/2006,6.2.8061.0\r\n"
        L"\r\n"
        L"[Manufacturer]\r\n"
        L"%MSFT%=Microsoft,NTamd64\r\n"
        L"\r\n"
        L"[Generic.Install.NT]\r\n"
        L"AddReg = One, Two\r\n"
        L"AddReg = Three\r\n"
        L"  CopyFiles = @1394ohci.sys\r\n";

    InfDocument inf;
    std::wstring err;
    Check(ParseInfText(text, inf, err), "合成 INF 解析成功");
    Check(err.empty(), "成功时没有错误文本");
    CheckInt(static_cast<long long>(inf.sections.size()), 3, "三个节");

    Check(inf.HasSection(L"version"), "节名大小写不敏感");
    Check(inf.FindSection(L"Generic.Install.NT") != nullptr, "带点的节名能取到");
    CheckStr(inf.GetValue(L"Version", L"Provider"), L"%MSFT%", "Provider 原样保留 %token%");
    CheckStr(inf.GetValue(L"VERSION", L"DRIVERVER"), L"06/21/2006,6.2.8061.0", "键名大小写不敏感");

    const InfSection* install = inf.FindSection(L"Generic.Install.NT");
    Check(install != nullptr && install->entries.size() == 3, "同键重复不合并（三条都在）");
    Check(install != nullptr && install->entries[0].key == L"AddReg", "条目顺序按文件原序");
    Check(install != nullptr && install->entries[2].value == L"@1394ohci.sys", "等号右侧去引号前先原样保留");
    Check(inf.GetValue(L"Missing", L"Whatever").empty(), "缺节返回空");
    Check(inf.GetValue(L"Version", L"Missing").empty(), "缺键返回空");

    // 行尾反斜杠续行
    InfDocument joined;
    Check(ParseInfText(L"[S]\r\nA=one\\\r\ntwo\r\n", joined, err), "续行 INF 解析成功");
    CheckStr(joined.GetValue(L"S", L"A"), L"onetwo", "续行把两段拼起来（反斜杠吃掉）");

    // 节前垃圾不致命
    InfDocument loose;
    Check(ParseInfText(L"junk before any section\r\n[S]\r\nA=1\r\n", loose, err), "节前杂行不导致失败");
    CheckStr(loose.GetValue(L"S", L"A"), L"1", "杂行之后的正节能读");

    // 真机里就有的两种写法：值后面带注释、引号里的分号是数据
    InfDocument commented;
    Check(ParseInfText(L"[Version]\r\n"
                       L"ClassGuid={14b62f50-3f15-11dd-ae16-0800200c9a66}    ; If class above is new\r\n"
                       L"A=\"x;y\"\r\n"
                       L"B=plain; tail\r\n",
                       commented, err),
          "带行尾注释的 INF 解析成功");
    CheckStr(commented.GetValue(L"Version", L"ClassGuid"), L"{14b62f50-3f15-11dd-ae16-0800200c9a66}",
             "值后面的 ; 注释被剥掉");
    CheckStr(commented.GetValue(L"Version", L"A"), L"\"x;y\"", "引号里的分号保留");
    CheckStr(commented.GetValue(L"Version", L"B"), L"plain", "未加引号的分号之后是注释");

    // [Strings] 解析
    InfDocument strings;
    Check(ParseInfText(L"[Version]\r\nProvider=%MSFT%\r\n[Strings]\r\nMSFT=\"Microsoft\"\r\nLSI=\"LSI Corp\"\r\n", strings, err),
          "带 [Strings] 的 INF 解析成功");
    CheckStr(strings.ResolveTokens(L"%MSFT%"), L"Microsoft", "%MSFT% 解析成 Microsoft（引号剥掉）");
    CheckStr(strings.ResolveTokens(L"%LSI% / %NOPE%"), L"LSI Corp / %NOPE%", "未定义的 token 保持原样");
    CheckStr(strings.ResolveTokens(L"no tokens"), L"no tokens", "没有 token 的文本原样返回");

    // 真机 brmfport.inf 的写法：Provider="%MSFT%"（引号包住 token）⇒ 库里存不带引号的 Microsoft
    InfDocument quoted;
    Check(ParseInfText(L"[Version]\r\nProvider=\"%MSFT%\"\r\n[Strings]\r\nMSFT=\"Microsoft\"\r\n", quoted, err),
          "Provider 被引号包住的 INF 解析成功");
    CheckStr(InfDocument::Unquote(quoted.ResolveTokens(quoted.GetValue(L"Version", L"Provider"))), L"Microsoft",
             "Provider=\"%MSFT%\" 解析后要去掉外层引号（实测 brmfport.inf）");

    // 坏输入
    InfDocument bad;
    Check(!ParseInfText(L"[Unterminated\r\nA=1\r\n", bad, err), "未闭合的节头 ⇒ 失败");
    Check(!bad.ok, "失败时不置 ok");
    Check(!err.empty(), "失败时给原因");
}

static void DecodeEncodings()
{
    printf("\n[2] 字节解码\n");
    InfDocument inf;
    std::wstring err;

    const std::vector<std::uint8_t> utf16 = Utf16LeWithBom(L"[Version]\r\nProvider=A\r\n");
    Check(ParseInf(utf16, inf, err), "UTF-16LE + BOM 能解析");
    CheckStr(inf.GetValue(L"Version", L"Provider"), L"A", "UTF-16LE 的值正确");

    std::vector<std::uint8_t> utf16be;
    utf16be.push_back(0xFE);
    utf16be.push_back(0xFF);
    const std::wstring be = L"[Version]\r\nProvider=B\r\n";
    for (const wchar_t ch : be)
    {
        utf16be.push_back(static_cast<std::uint8_t>((ch >> 8) & 0xFF));
        utf16be.push_back(static_cast<std::uint8_t>(ch & 0xFF));
    }
    Check(ParseInf(utf16be, inf, err), "UTF-16BE + BOM 能解析");
    CheckStr(inf.GetValue(L"Version", L"Provider"), L"B", "UTF-16BE 的值正确");

    std::string ansi = "[Version]\r\nProvider=C\r\n";
    std::vector<std::uint8_t> ansiBytes(ansi.begin(), ansi.end());
    Check(ParseInf(ansiBytes, inf, err), "无 BOM 的 ANSI 能解析");
    CheckStr(inf.GetValue(L"Version", L"Provider"), L"C", "ANSI 的值正确");

    std::vector<std::uint8_t> empty;
    Check(ParseInf(empty, inf, err), "空文件不算错（只是没有内容）");
    CheckInt(static_cast<long long>(inf.sections.size()), 0, "空文件没有节");
}

static void DriverVerEncoding()
{
    printf("\n[3] [Version] 的二进制编码\n");
    InfVersionInfo version;
    std::wstring err;

    Check(ParseDriverVer(L"06/21/2006,6.2.8061.0", version, err), "真机 DriverVer 解析成功");
    CheckInt(version.major, 6, "major = 6");
    CheckInt(version.minor, 2, "minor = 2");
    CheckInt(version.build, 8061, "build = 8061");
    CheckInt(version.revision, 0, "revision = 0");
    CheckStr(HexOf(DriverVersionPropertyData(version.major, version.minor, version.build, version.revision)),
             L"00007d1f02000600", "000f 的字节：build<<16|rev 在前、major<<16|min 在后");
    CheckStr(HexOf(DriverDatePropertyData(version.driverDate)), L"00808ca3c594c601",
             "000e 的字节：06/21/2006 的 FILETIME");

    // 日期与版本的边界
    Check(ParseDriverVer(L"06/21/2006", version, err) && version.hasDriverVer, "只有日期也能解析");
    CheckInt(version.major, 0, "没有版本段 ⇒ 0.0.0.0");
    Check(ParseDriverVer(L"6/1/1999,1.0", version, err), "个位数月日、两段版本也接受");
    CheckInt(version.major, 1, "两段版本 major = 1");
    CheckInt(version.build, 0, "缺的段补 0");
    Check(ParseDriverVer(L"06/21/2006;2.3.4.5", version, err), "分隔符也接受分号");
    Check(!ParseDriverVer(L"", version, err), "空 DriverVer ⇒ 失败");
    Check(!ParseDriverVer(L"2006-13-01,1.0", version, err), "13 月 ⇒ 失败");
    Check(!ParseDriverVer(L"06/21/2006,6.x", version, err), "非数字版本 ⇒ 失败");
    Check(ParseDriverVer(L"06/21/2006,5.4.19.0.1", version, err), "五段版本也接受（真机 iirsp.inf 就是这样）");
    CheckInt(version.major, 5, "五段版本取前四段：major = 5");
    CheckInt(version.minor, 4, "minor = 4");
    CheckInt(version.build, 19, "build = 19");
    CheckInt(version.revision, 0, "revision = 0");

    std::uint64_t time = 0;
    Check(ParseInfDateFileTime(L"06/21/2006", time, err), "日期转 FILETIME 成功");
    CheckStr(HexOf(DriverDatePropertyData(time)), L"00808ca3c594c601", "同样的字节");
    Check(time != 0, "FILETIME 非零");
}

static void GuidEncoding()
{
    printf("\n[4] ClassGuid 的字节序\n");
    std::vector<std::uint8_t> guid;
    std::wstring err;
    Check(ParseInfGuid(L"{6BDD1FC1-810F-11D0-BEC7-08002BE2092F}", guid, err), "真机 GUID 解析成功");
    CheckInt(static_cast<long long>(guid.size()), 16, "16 字节");
    CheckStr(HexOf(guid), L"c11fdd6b0f81d011bec708002be2092f",
             "注册表顺序：前三段小端、后 8 字节原样");
    Check(ParseInfGuid(L"6BDD1FC1-810F-11D0-BEC7-08002BE2092F", guid, err), "不带大括号也接受");
    Check(!ParseInfGuid(L"{6BDD1FC1-810F-11D0-BEC7}", guid, err), "位数不够 ⇒ 失败");
    Check(!ParseInfGuid(L"{6BDD1FC1-810F-11D0-BEC7-08002BE2092G}", guid, err), "非十六进制 ⇒ 失败");
}

static void ImageVersionGroundTruth()
{
    printf("\n[5] 真机对照（1394.inf，8061 x86 client）\n");
    const std::wstring path = FindImageInf(L"1394.inf_x86_*", L"1394.inf");
    if (path.empty())
    {
        printf("  [skip] 没挂载 E:\\cbs_8061x86c.vhd（Q: 找不到 1394.inf）\n");
        return;
    }

    InfDocument inf;
    std::wstring err;
    Check(ParseInfFile(path, inf, err), "真机 1394.inf 能解析");
    InfVersionInfo version;
    Check(ReadInfVersion(inf, version), "版本节读取成功");
    CheckStr(version.provider, L"Microsoft", "000c：Provider 用 [Strings] 解析成 Microsoft（实测）");
    CheckStr(version.providerRaw, L"%MSFT%", "解析前原文保留（排障用）");
    CheckStr(HexOf(version.classGuid), L"c11fdd6b0f81d011bec708002be2092f",
             "000d：与 hive 里的 16 字节一致");
    CheckStr(HexOf(DriverDatePropertyData(version.driverDate)), L"00808ca3c594c601",
             "000e：与 hive 里的日期字节一致");
    CheckStr(HexOf(DriverVersionPropertyData(version.major, version.minor, version.build, version.revision)),
             L"00007d1f02000600", "000f：与 hive 里的版本字节一致");
    CheckStr(version.className, L"1394", "[Version] Class = 1394");

    // 目录名必须与真机一致（算法在 DriverPackage.h）
    std::wstring storeId;
    Check(BuildStoreIdFromFile(path, 0, storeId, err), "从真机 INF 算 StoreId 成功");
    CheckStr(storeId, L"1394.inf_x86_85b894cb1410e75c", "StoreId 与真机目录名一致");

    // 台账计划：值名/类型/数据全对齐
    DriverLedgerRequest request;
    request.inf = &inf;
    request.storeId = storeId;
    request.infFileName = L"1394.inf";
    request.architecture = 0;
    request.importFlags = kImportFlagInbox | kImportFlagUseOriginalInfName;
    request.signerName = L"Microsoft Windows";
    request.signerScore = 0x0D000003u;
    const DriverLedgerPlan plan = BuildDriverLedgerPlan(request);
    Check(plan.ok, "台账计划生成成功");
    // 2 fixed keys (package, DriverInfFiles) + 16 DeviceIds + 16 Descriptors + 1 Configuration
    // section for 1394.inf's [GenericModels.NTx86] block (counted on the real 8061 INF).
    CheckInt(static_cast<long long>(plan.keys.size()), 35, "35 个键：2 固定 + 16 DeviceIds + 16 Descriptors + 1 Configuration");
    const auto findKey = [&plan](const std::wstring& path) -> const LedgerKey* {
        for (const LedgerKey& key : plan.keys)
        {
            if (key.relativePath == path) return &key;
        }
        return nullptr;
    };
    if (!plan.keys.empty())
    {
        CheckStr(plan.keys[0].relativePath, L"DriverPackages\\1394.inf_x86_85b894cb1410e75c",
                 "包记录键名");
        CheckStr(plan.keys[1].relativePath, L"DriverInfFiles\\1394.inf",
                 "INFFile 记录用**原始** INF 名，不是 oemN.inf");
    }

    // DeviceIds：真机上 0003 = "<INF 名>\1"
    const LedgerKey* device = findKey(L"DeviceIds\\PCI\\CC_0C0010");
    Check(device != nullptr, "DeviceIds\\PCI\\CC_0C0010 存在（1394 的真机硬件 ID）");
    if (device != nullptr && !device->values.empty())
    {
        CheckStr(device->values[0].name, L"0003", "DeviceIds 值名 0003");
        Check(device->values[0].type == LedgerValueType::MultiString, "DeviceIds 0003 是 REG_MULTI_SZ");
        CheckStr(device->values[0].text, L"1394.inf\\1", "DeviceIds 0003 = 1394.inf\\1（实测 rank 1）");
    }

    // Descriptors：Configuration 用 .NT 装饰后的节名，两个 token 小写
    const LedgerKey* descriptor = findKey(L"DriverPackages\\1394.inf_x86_85b894cb1410e75c\\Descriptors\\PCI\\VEN_100B&CC_0C0010");
    Check(descriptor != nullptr, "Descriptors\\PCI\\VEN_100B&CC_0C0010 存在");
    if (descriptor != nullptr && descriptor->values.size() == 3)
    {
        CheckStr(descriptor->values[0].name, L"Configuration", "Descriptors 有 Configuration");
        CheckStr(descriptor->values[0].text, L"Generic.Install.NT", "Configuration = Generic.Install.NT（实测）");
        CheckStr(descriptor->values[1].name, L"Manufacturer", "Descriptors 有 Manufacturer");
        CheckStr(descriptor->values[1].text, L"%natsemi.mfg%", "Manufacturer 是 mfg token 的小写形式（实测）");
        CheckStr(descriptor->values[2].name, L"Description", "Descriptors 有 Description");
        CheckStr(descriptor->values[2].text, L"%pci\\ven_100b&cc_0c0010.devicedesc%",
                 "Description 是 desc token 的小写形式（实测）");
    }

    // Configurations：Service 来自 AddService
    const LedgerKey* configuration =
        findKey(L"DriverPackages\\1394.inf_x86_85b894cb1410e75c\\Configurations\\Generic.Install.NT");
    Check(configuration != nullptr, "Configurations\\Generic.Install.NT 存在");
    if (configuration != nullptr && configuration->values.size() == 2)
    {
        CheckStr(configuration->values[0].name, L"Service", "Configurations 有 Service");
        CheckStr(configuration->values[0].text, L"1394ohci", "Service = 1394ohci（AddService 的第一个字段）");
        CheckStr(configuration->values[1].name, L"ConfigFlags", "Configurations 有 ConfigFlags");
        CheckInt(configuration->values[1].dword, 0, "ConfigFlags = 0（实测）");
    }

    // DriverInfFiles 0004 = 配置节名列表
    const LedgerKey* infKey = findKey(L"DriverInfFiles\\1394.inf");
    Check(infKey != nullptr, "DriverInfFiles\\1394.inf 存在");
    if (infKey != nullptr)
    {
        bool has0004 = false;
        for (const LedgerValue& value : infKey->values)
        {
            if (value.name != L"0004") continue;
            has0004 = true;
            Check(value.type == LedgerValueType::MultiString, "0004 是 REG_MULTI_SZ");
            CheckStr(value.text, L"Generic.Install.NT", "0004 = Generic.Install.NT（与真机 hive 一致）");
        }
        Check(has0004, "0004 已产出（不再留 note）");
    }
}

static void LedgerPlan()
{
    printf("\n[6] 台账计划的值\n");
    InfDocument inf;
    std::wstring err;
    Check(ParseInfText(L"[Version]\r\n"
                       L"Provider=%MSFT%\r\n"
                       L"ClassGuid={6BDD1FC1-810F-11D0-BEC7-08002BE2092F}\r\n"
                       L"DriverVer=06/21/2006,6.2.8061.0\r\n",
                       inf, err),
          "合成 INF 解析成功");

    DriverLedgerRequest request;
    request.inf = &inf;
    request.storeId = L"acpi.inf_x86_9c3e2c46deadbeef";
    request.infFileName = L"acpi.inf";
    request.architecture = 0;
    const DriverLedgerPlan plan = BuildDriverLedgerPlan(request);
    Check(plan.ok, "计划生成成功");
    CheckInt(static_cast<long long>(plan.keys.size()), 2, "两个键");

    const std::vector<LedgerValue>* values = plan.keys.empty() ? nullptr : &plan.keys[0].values;
    Check(values != nullptr && !values->empty(), "包记录有值");
    if (values != nullptr)
    {
        CheckInt(static_cast<long long>(values->size()), 9, "九个值（没有签名信息）");
        CheckStr((*values)[0].name, L"0002", "第一个是 0002");
        CheckStr((*values)[0].text, L"acpi.inf", "0002 = 原始 INF 名");
        Check((*values)[0].type == LedgerValueType::String, "0002 是 REG_SZ");

        CheckStr((*values)[1].name, L"0005", "0005 存在");
        CheckInt((*values)[1].dword, 0, "没给 kImportFlagInbox ⇒ 0005 = 0");
        Check((*values)[1].type == LedgerValueType::DWord, "0005 是 REG_DWORD");
        CheckInt((*values)[2].dword, 1, "0006 恒为 1（实测，语义未定）");
        CheckStr((*values)[3].name, L"000a", "没有签名时 000a 紧跟 0006");
        CheckInt((*values)[3].dword, 0, "000a = 架构（x86 = 0）");
        CheckInt((*values)[4].dword, 0, "000b = 区域 0");
        CheckStr((*values)[5].name, L"000c", "000c = Provider");
        CheckStr((*values)[5].text, L"%MSFT%", "000c：token 没定义时保持 %token% 原样（不静默丢）");
        CheckStr((*values)[6].name, L"000d", "000d = ClassGuid");
        CheckStr(HexOf((*values)[6].binary), L"c11fdd6b0f81d011bec708002be2092f", "000d 的字节");
        CheckStr((*values)[7].name, L"000e", "000e = 日期");
        CheckStr((*values)[8].name, L"000f", "000f = 版本");
        CheckStr(HexOf((*values)[8].binary), L"00007d1f02000600", "000f 的字节");
        Check(plan.keys[0].notes.size() >= 1, "缺签名时留下说明（不静默省略）");
    }

    // 收件箱标志 + 签名
    DriverLedgerRequest inbox = request;
    inbox.importFlags = kImportFlagInbox;
    inbox.signerName = L"Microsoft Windows";
    inbox.signerScore = 0x0D000003u;
    const DriverLedgerPlan inboxPlan = BuildDriverLedgerPlan(inbox);
    Check(inboxPlan.ok, "收件箱计划生成成功");
    if (inboxPlan.ok && !inboxPlan.keys.empty())
    {
        const std::vector<LedgerValue>& v = inboxPlan.keys[0].values;
        CheckInt(static_cast<long long>(v.size()), 11, "十一个值（含签名）");
        for (const LedgerValue& value : v)
        {
            if (value.name == L"0005") CheckInt(value.dword, 1, "kImportFlagInbox ⇒ 0005 = 1");
            if (value.name == L"0007") CheckStr(value.text, L"Microsoft Windows", "0007 = 签名者");
            if (value.name == L"0008") CheckInt(static_cast<long long>(value.dword), 0x0D000003, "0008 = 签名分");
        }
    }

    // INFFile 记录
    if (inboxPlan.ok && inboxPlan.keys.size() == 2)
    {
        const std::vector<LedgerValue>& v = inboxPlan.keys[1].values;
        CheckInt(static_cast<long long>(v.size()), 2, "INFFile 记录两个值");
        CheckStr(v[0].name, L"0002", "0002 = StoreId 列表");
        Check((v[0].type == LedgerValueType::MultiString), "0002 是 REG_MULTI_SZ");
        CheckStr(v[0].text, inbox.storeId, "0002 的内容是 StoreId");
        CheckStr(v[1].text, inbox.storeId, "0003 也是 StoreId");
        Check(inboxPlan.keys[1].notes.size() >= 1, "0004 未生成时留下说明");
    }

    // 缺 ClassGuid / DriverVer 时不许瞎写
    InfDocument sparse;
    Check(ParseInfText(L"[Version]\r\nProvider=X\r\n", sparse, err), "极简 INF 解析成功");
    DriverLedgerRequest minimal = request;
    minimal.inf = &sparse;
    const DriverLedgerPlan minimalPlan = BuildDriverLedgerPlan(minimal);
    Check(minimalPlan.ok, "极简 INF 也能出计划");
    if (minimalPlan.ok && !minimalPlan.keys.empty())
    {
        bool hasGuid = false, hasDate = false;
        for (const LedgerValue& value : minimalPlan.keys[0].values)
        {
            if (value.name == L"000d") hasGuid = true;
            if (value.name == L"000e") hasDate = true;
        }
        Check(!hasGuid, "没有 ClassGuid ⇒ 不写 000d");
        Check(!hasDate, "没有 DriverVer ⇒ 不写 000e/000f");
        Check(minimalPlan.keys[0].notes.size() >= 2, "两处缺失都有说明");
    }

    // 拒绝明显坏输入
    DriverLedgerRequest bad = request;
    bad.storeId.clear();
    Check(!BuildDriverLedgerPlan(bad).ok, "没有 StoreId ⇒ 失败");
    bad = request;
    bad.infFileName.clear();
    Check(!BuildDriverLedgerPlan(bad).ok, "没有 INF 名 ⇒ 失败");
    bad = request;
    bad.inf = nullptr;
    Check(!BuildDriverLedgerPlan(bad).ok, "没有 INF ⇒ 失败");
}

static const LedgerKey* FindKey(const DriverLedgerPlan& plan, const std::wstring& path)
{
    for (const LedgerKey& key : plan.keys)
    {
        if (key.relativePath == path) return &key;
    }
    return nullptr;
}

static std::wstring ValueOf(const LedgerKey* key, const std::wstring& name)
{
    if (key == nullptr) return std::wstring();
    for (const LedgerValue& value : key->values)
    {
        if (value.name == name) return value.text;
    }
    return std::wstring();
}

static void SectionAndRankRules()
{
    printf("\n[7] 节选择、rank、token 与字段装饰规则\n");

    // 实测依据（8061 x86 真机台账，575 个商店目录逐值比对）：
    // - [Manufacturer] 里第一个可用的装饰名决定 models 节；"NT"/"NT.5.2" 不带架构，任何架构都接受
    //   （wdmvsc.inf: "%StdMfg%=Standard,NT.5.2,NTamd64..." ⇒ x86 用 Standard.NT.5.2）；
    // - rank 是硬件 ID 在 models 行里的字段序号（"Inst,,ID" ⇒ 2），第一次声明决定 rank；
    // - 描述符取最后一次声明（hal.inf 的 ACPIAPIC 第二次声明才带 ACPIAPIC_HAL）；
    // - 硬件 ID 里的 [Strings] token 会被展开（bfad.inf: PCI\%VENDOR_ID%... ⇒ PCI\VEN_1657...），
    //   展开不了的那条记录整条不落库；
    // - models 行的安装节如果根本不存在，数据库仍按 INF 自己的写法记下来（machine.inf 的
    //   AGPALi_Install 没有对应节，库里就是 "AGPALi_Install"）。
    InfDocument inf;
    std::wstring err;
    Check(ParseInfText(L"[Version]\r\n"
                       L"Provider=%MSFT%\r\n"
                       L"[Manufacturer]\r\n"
                       L"%Mfg%=Standard,NTx86.5.1,NTx86.6.2\r\n"
                       L"%Mfg2%=Neutral,NT.5.2\r\n"
                       L"[Standard]\r\n"
                       L"%Desc%=Old_Install,PCI\\VEN_1000\r\n"
                       L"[Standard.NTx86.5.1]\r\n"
                       L"%Desc%=V51_Install,PCI\\VEN_1000,PCI\\VEN_1001\r\n"
                       L"[Standard.NTx86.6.2]\r\n"
                       L"%Desc2%=V62_Install,PCI\\VEN_1000\r\n"
                       L"[Neutral.NT.5.2]\r\n"
                       L"%Desc3%=Neu_Install,PCI\\VEN_2000\r\n"
                       L"[V51_Install.NT]\r\n"
                       L"AddService=Svc,0x00000002,Service_Inst\r\n"
                       L"[V62_Install.NT]\r\n"
                       L"[Neu_Install.NT]\r\n"
                       L"[Old_Install]\r\n"
                       L"[Strings]\r\n"
                       L"MSFT=\"Microsoft\"\r\n"
                       L"Mfg=\"Test\"\r\n"
                       L"Mfg2=\"Test two\"\r\n"
                       L"Desc=\"Device\"\r\n"
                       L"Desc2=\"Device two\"\r\n"
                       L"Desc3=\"Device three\"\r\n",
                       inf, err),
          "座次规则 INF 解析成功");

    DriverLedgerRequest request;
    request.inf = &inf;
    request.storeId = L"rules.inf_x86_0123456789abcdef";
    request.infFileName = L"rules.inf";
    request.architecture = 0;
    const DriverLedgerPlan plan = BuildDriverLedgerPlan(request);
    Check(plan.ok, "座次规则计划生成成功");
    if (plan.ok)
    {
        // 目标 OS 6.2：最高的可用装饰 NTx86.6.2 胜出（真机 wvmbus.inf 就是这样落到 WIN8 组）。
        const LedgerKey* v62 = FindKey(plan, L"DriverPackages\\" + request.storeId + L"\\Descriptors\\PCI\\VEN_1000");
        CheckStr(ValueOf(v62, L"Configuration"), L"V62_Install.NT", "最高可用装饰决定 models 节");
        const LedgerKey* v1000 = FindKey(plan, L"DeviceIds\\PCI\\VEN_1000");
        CheckStr(ValueOf(v1000, L"0003"), L"rules.inf\\1", "VEN_1000 的 rank = 1（第一行第一个硬件 ID）");

        // 架构中立的 NT.5.2 对 x86 也算可用。
        const LedgerKey* neutral = FindKey(plan, L"DriverPackages\\" + request.storeId + L"\\Descriptors\\PCI\\VEN_2000");
        CheckStr(ValueOf(neutral, L"Configuration"), L"Neu_Install.NT", "NT.5.2 对 x86 可用");

        // 定义过的 token 存小写，且只出现在被引用的描述符里。
        CheckStr(ValueOf(v62, L"Manufacturer"), L"%mfg%", "定义过的 token 存小写");
        CheckStr(ValueOf(v62, L"Description"), L"%desc2%", "描述 token 也存小写");
        CheckStr(ValueOf(neutral, L"Description"), L"%desc3%", "中立节同样存 token");
    }

    // 同一份 INF 在 5.1 上落到 5.1 的 models 节：装饰是按运行 OS 版本挑的。
    DriverLedgerRequest request51 = request;
    request51.osMajor = 5;
    request51.osMinor = 1;
    request51.storeId = L"rules51.inf_x86_0123456789abcdef";
    const DriverLedgerPlan plan51 = BuildDriverLedgerPlan(request51);
    Check(plan51.ok, "5.1 计划生成成功");
    if (plan51.ok)
    {
        const LedgerKey* v51 = FindKey(plan51, L"DriverPackages\\" + request51.storeId + L"\\Descriptors\\PCI\\VEN_1000");
        CheckStr(ValueOf(v51, L"Configuration"), L"V51_Install.NT", "5.1 上落 NTx86.5.1 组");
        const LedgerKey* v1001 = FindKey(plan51, L"DeviceIds\\PCI\\VEN_1001");
        CheckStr(ValueOf(v1001, L"0003"), L"rules.inf\\2", "VEN_1001 的 rank = 2（同一行第二个硬件 ID）");
        const LedgerKey* service = FindKey(plan51, L"DriverPackages\\" + request51.storeId + L"\\Configurations\\V51_Install.NT");
        CheckStr(ValueOf(service, L"Service"), L"Svc", "Service 来自 AddService");
        CheckInt(service == nullptr ? -1 : 0, 0, "配置节存在");
    }

    // 引号 / 空字段 rank / 未解析 token / 不存在的安装节
    InfDocument inf2;
    Check(ParseInfText(L"[Version]\r\n"
                       L"Provider=X\r\n"
                       L"[Manufacturer]\r\n"
                       L"\"%HP%\"=HpModels,NTx86\r\n"
                       L"[HpModels.NTx86]\r\n"
                       L"%DevDesc%=Hp_Install,,\"USB\\VID_1%VID%\"\r\n"
                       L"%DevDesc%=Hp_Install,USB\\VID_2,USB\\VID_3\r\n"
                       L"%DevDesc%=Hp_Install,PCI\\%UNDEFINED_TOKEN%\r\n"
                       L"%DevDesc%=Missing_Section,ACPI\\FOO\r\n"
                       L"[Hp_Install]\r\n"
                       L"[Strings]\r\n"
                       L"HP=\"Hewlett-Packard\"\r\n"
                       L"VID=\"0BDB\"\r\n"
                       L"DevDesc=\"Speaker\"\r\n",
                       inf2, err),
          "引号规则 INF 解析成功");

    DriverLedgerRequest request2;
    request2.inf = &inf2;
    request2.storeId = L"hp.inf_x86_0123456789abcdef";
    request2.infFileName = L"hp.inf";
    request2.architecture = 0;
    const DriverLedgerPlan plan2 = BuildDriverLedgerPlan(request2);
    Check(plan2.ok, "引号规则计划生成成功");
    if (plan2.ok)
    {
        const LedgerKey* hp = FindKey(plan2, L"DriverPackages\\" + request2.storeId + L"\\Descriptors\\USB\\VID_10BDB");
        CheckStr(ValueOf(hp, L"Manufacturer"), L"%hp%", "带引号的 token 去引号后存小写");
        const LedgerKey* emptyRank = FindKey(plan2, L"DeviceIds\\USB\\VID_10BDB");
        CheckStr(ValueOf(emptyRank, L"0003"), L"hp.inf\\2", "空字段也占位（rank = 2）");
        const LedgerKey* vid2 = FindKey(plan2, L"DeviceIds\\USB\\VID_2");
        CheckStr(ValueOf(vid2, L"0003"), L"hp.inf\\1", "同行的第一个硬件 ID");
        const LedgerKey* vid3 = FindKey(plan2, L"DeviceIds\\USB\\VID_3");
        CheckStr(ValueOf(vid3, L"0003"), L"hp.inf\\2", "同行的第二个硬件 ID");
        Check(FindKey(plan2, L"DeviceIds\\PCI\\%UNDEFINED_TOKEN%") == nullptr, "未解析 token 的硬件 ID 不落库");
        bool skippedNote = false;
        for (const std::wstring& note : plan2.notes)
        {
            if (note.find(L"skipped") != std::wstring::npos) skippedNote = true;
        }
        Check(skippedNote, "跳过硬件 ID 时留下说明");
        const LedgerKey* missing = FindKey(plan2, L"DriverPackages\\" + request2.storeId + L"\\Configurations\\Missing_Section");
        Check(missing != nullptr, "安装节不存在时仍记录 models 行的写法");
    }
}

int main()
{
    printf("OCID DMI 台账自检\n");

    ParseSynthetic();

    DecodeEncodings();
    DriverVerEncoding();
    GuidEncoding();
    ImageVersionGroundTruth();
    LedgerPlan();
    SectionAndRankRules();

    printf("\n== 结果 ==\n  通过 %d / 失败 %d\n", g_pass, g_fail);
    return g_fail == 0 ? 0 : 1;
}
