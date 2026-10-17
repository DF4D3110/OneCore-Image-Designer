// OCID - CBS 台账（COMPONENTS）记录编解码自检
//
// 钉的是"编错了就会写坏组件台账"的字节语义，全部来自真机：
//   E:\cbs_8061x86c.vhd 的 config\COMPONENTS（20,971,520 B，x86 8061 client），
//   挂到 HKLM\OCIDCOMP 后用 reg export / RegistryKey 读出来的原始 REG_BINARY。
//
// 覆盖：
//   1. 根值：StoreFormatVersion（UTF-16 "0.0.0.6"）、StoreArchitecture（4 字节）、
//      ExecutionState（DWORD）、PendingXmlIdentifier（UTF-16 的 32 个 hex）
//   2. 组件记录：S256H(32 B)、identity(ASCII)、c!(空值)、f!(UTF-16 无 NUL)、CF(DWORD)
//   3. 部署记录：appid(ASCII)、CatalogThumbprint(REG_SZ 的 hex 文本)、
//      p!/s!/i! = 8 字节小端长度前缀（u32 长度 + u32 0）+ ASCII 包身份
//   4. 编码器自证：解码 -> 再编码 -> 与真机字节逐字节相等

#include "ocid/CbsStore.h"
#include "ocid/ConsoleUtf8.h"

#include <cstdio>
#include <string>
#include <vector>

using namespace ocid;

static int g_pass = 0;
static int g_fail = 0;

static void Check(bool condition, const char* what)
{
    if (condition) { ++g_pass; printf("  [ok]   %s\n", what); }
    else { ++g_fail; printf("  [FAIL] %s\n", what); }
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

static void CheckAscii(const std::string& got, const std::string& want, const char* what)
{
    if (got == want) { ++g_pass; printf("  [ok]   %s\n", what); }
    else
    {
        ++g_fail;
        printf("  [FAIL] %s\n         got  = %s\n         want = %s\n",
               what, got.c_str(), want.c_str());
    }
}

static std::string HexOf(const std::vector<std::uint8_t>& bytes)
{
    static const char* digits = "0123456789abcdef";
    std::string out;
    for (const std::uint8_t b : bytes)
    {
        out.push_back(digits[(b >> 4) & 0xF]);
        out.push_back(digits[b & 0xF]);
    }
    return out;
}

static std::vector<std::uint8_t> FromHex(const std::string& hex)
{
    std::vector<std::uint8_t> out;
    for (std::size_t i = 0; i + 1 < hex.size(); i += 2)
    {
        const int hi = (hex[i] >= 'a') ? (hex[i] - 'a' + 10)
                                       : ((hex[i] >= 'A') ? (hex[i] - 'A' + 10) : (hex[i] - '0'));
        const int lo = (hex[i + 1] >= 'a') ? (hex[i + 1] - 'a' + 10)
                       : ((hex[i + 1] >= 'A') ? (hex[i + 1] - 'A' + 10) : (hex[i + 1] - '0'));
        out.push_back(static_cast<std::uint8_t>((hi << 4) | lo));
    }
    return out;
}

// 真机根值
static const char* kStoreFormatVersionHex = "30002e0030002e0030002e003600";
static const char* kPendingXmlIdentifierHex =
    "610034006400660065003600640066003800330035003200630063003000310033003100300065003000"
    "30003000300034006300300038003400380030006400";

// 真机组件（msil_accessibility_b03f5f7f11d50a3a_4.0.8061.0_none_77a15b1d27ec8860）
static const char* kComponentName =
    "msil_accessibility_b03f5f7f11d50a3a_4.0.8061.0_none_77a15b1d27ec8860";
static const char* kS256HHex =
    "19ea39fc96e25f975933ef5947945231aa6875b55d7c2f85fec93e42fe8f9493";
static const char* kIdentityText =
    "Accessibility, Culture=neutral, Version=4.0.8061.0, PublicKeyToken=b03f5f7f11d50a3a, "
    "ProcessorArchitecture=msil";
static const char* kFileNameHex =
    "4100630063006500730073006900620069006c006900740079002e0064006c006c00";

// 真机部署（1394.inf-languagepack_..._7f85aaa2395fa72e）
static const char* kDeploymentName = "1394.inf-languagepack_31bf3856ad364e35_6.2.8061.0_7f85aaa2395fa72e";
static const char* kAppIdText =
    "1394.inf-LanguagePack, Culture=en-US, Version=6.2.8061.0, PublicKeyToken=31bf3856ad364e35, "
    "ProcessorArchitecture=x86, versionScope=NonSxS";
static const char* kCatalogThumbprint =
    "13a5b296db74a2756d919189e85c981f984edfa2c79293c24da409dc2f777f8b";
static const char* kPayloadHex =
    "5f000000000000004d6963726f736f66742d57696e646f77732d436f6d6d6f6e2d447269766572732d"
    "5061636b6167652d647269766572737e333162663338353661643336346533357e7838367e656e2d"
    "55537e362e322e383036312e302e494e465f31333934";
static const char* kPayloadText =
    "Microsoft-Windows-Common-Drivers-Package-drivers~31bf3856ad364e35~x86~en-US~6.2.8061.0.INF_1394";

static void StoreHeader()
{
    printf("\n-- 台账根值 --\n");
    std::vector<CbsValue> values;
    values.push_back(CbsValue{"StoreFormatVersion", FromHex(kStoreFormatVersionHex)});
    values.push_back(CbsValue{"StoreArchitecture", FromHex("00000000")});
    values.push_back(CbsValue{"ExecutionState", CbsEncodeDword(6)});
    values.push_back(CbsValue{"PendingXmlIdentifier", FromHex(kPendingXmlIdentifierHex)});

    CbsStoreHeader header;
    std::string error;
    Check(CbsDecodeStoreHeader(values, header, error), "根值解码成功");
    CheckStr(header.formatVersion, L"0.0.0.6", "StoreFormatVersion = 0.0.0.6");
    Check(header.hasArchitecture, "带 StoreArchitecture");
    CheckInt(header.architecture, 0, "x86 真机的 StoreArchitecture = 0");
    CheckInt(header.executionState, 6, "ExecutionState = 6");
    CheckStr(header.pendingXmlIdentifier, L"a4dfe6df8352cc01310e00004c08480d",
             "PendingXmlIdentifier 解出 32 个 hex");

    const std::vector<CbsValue> back = CbsEncodeStoreHeader(header);
    bool shaEqual = false, archEqual = false, execEqual = false, pendingEqual = false;
    for (const CbsValue& value : back)
    {
        if (value.name == "StoreFormatVersion")
            shaEqual = HexOf(value.data) == kStoreFormatVersionHex;
        else if (value.name == "StoreArchitecture")
            archEqual = HexOf(value.data) == "00000000";
        else if (value.name == "ExecutionState")
            execEqual = HexOf(value.data) == "06000000";
        else if (value.name == "PendingXmlIdentifier")
            pendingEqual = HexOf(value.data) == kPendingXmlIdentifierHex;
    }
    Check(shaEqual, "StoreFormatVersion 再编码与原字节相同");
    Check(archEqual, "StoreArchitecture 再编码与原字节相同");
    Check(execEqual, "ExecutionState 再编码 = 06000000");
    Check(pendingEqual, "PendingXmlIdentifier 再编码与原字节相同");

    // ARM 映像的表头形态（StoreArchitecture = 5）
    CbsStoreHeader arm;
    arm.formatVersion = L"0.0.0.6";
    arm.hasArchitecture = true;
    arm.architecture = 5;
    const std::vector<CbsValue> armValues = CbsEncodeStoreHeader(arm);
    bool armOk = false;
    for (const CbsValue& value : armValues)
    {
        if (value.name == "StoreArchitecture") armOk = HexOf(value.data) == "05000000";
    }
    Check(armOk, "ARM 架构编码为 05000000");

    // 缺 StoreFormatVersion 必须报错，不能默认成 0.0.0.6 糊过去
    std::vector<CbsValue> broken;
    broken.push_back(CbsValue{"StoreArchitecture", FromHex("00000000")});
    CbsStoreHeader scratch;
    std::string brokenError;
    Check(!CbsDecodeStoreHeader(broken, scratch, brokenError), "缺 StoreFormatVersion 时报错");
}

static void ComponentRecord()
{
    printf("\n-- 组件记录 --\n");
    const std::string identityText = kIdentityText;
    std::vector<CbsValue> values;
    values.push_back(CbsValue{"S256H", FromHex(kS256HHex)});
    values.push_back(CbsValue{"identity", CbsEncodeAsciiText(identityText)});
    values.push_back(CbsValue{"c!microsoft-w..-deployment_31bf3856ad364e35_6.2.8061.0_2a3c52b50797ba5a", {}});
    // 真机原样：值名是文件名的小写，值数据才是原始大小写。
    values.push_back(CbsValue{"f!accessibility.dll", FromHex(kFileNameHex)});
    values.push_back(CbsValue{"CF", CbsEncodeDword(12)});
    values.push_back(CbsValue{"zz-unknown", FromHex("0102")});

    CbsComponentRecord record;
    std::string error;
    Check(CbsDecodeComponentRecord(FromUtf8OrAnsi(kComponentName), values, record, error),
          "组件记录解码成功");
    CheckInt(static_cast<long long>(record.manifestSha256.size()), 32, "S256H 是 32 字节");
    CheckAscii(HexOf(record.manifestSha256), kS256HHex, "S256H 字节与真机一致");
    CheckAscii(record.identity, identityText, "identity 原样保留");
    CheckInt(static_cast<long long>(record.deploymentMarks.size()), 1, "识别 1 个 c! 标记");
    CheckAscii(record.deploymentMarks.empty() ? "" : record.deploymentMarks[0].deploymentName,
               "microsoft-w..-deployment_31bf3856ad364e35_6.2.8061.0_2a3c52b50797ba5a",
               "c! 标记名去掉前缀");
    CheckAscii(record.deploymentMarks.empty() ? "" : record.deploymentMarks[0].valueName,
               "c!microsoft-w..-deployment_31bf3856ad364e35_6.2.8061.0_2a3c52b50797ba5a",
               "c! 完整值名原样保留");
    CheckInt(static_cast<long long>(record.files.size()), 1, "识别 1 个 f! 文件名");
    CheckAscii(record.files.empty() ? "" : record.files[0].valueName, "f!accessibility.dll",
               "f! 完整值名（小写）原样保留");
    CheckStr(record.files.empty() ? L"" : record.files[0].fileName, L"Accessibility.dll",
             "f! 解出文件名（UTF-16 无结尾 NUL、原始大小写）");
    Check(record.hasConfigFlags, "带 CF");
    CheckInt(record.configFlags, 12, "CF = 12");
    CheckInt(static_cast<long long>(record.extraValues.size()), 1, "不认识的值原样留下");

    // 编码器必须产出与真机同字节的 S256H / identity / f!（连值名都要一样）
    const std::vector<CbsValue> back = CbsEncodeComponentRecord(record);
    bool shaOk = false, idOk = false, fileOk = false, cfOk = false, unknownOk = false;
    for (const CbsValue& value : back)
    {
        if (value.name == "S256H") shaOk = HexOf(value.data) == kS256HHex;
        else if (value.name == "identity") idOk = HexOf(value.data) == HexOf(CbsEncodeAsciiText(identityText));
        else if (value.name == "f!accessibility.dll") fileOk = HexOf(value.data) == kFileNameHex;
        else if (value.name == "CF") cfOk = HexOf(value.data) == "0c000000";
        else if (value.name == "zz-unknown") unknownOk = HexOf(value.data) == "0102";
    }
    Check(shaOk, "S256H 再编码与原字节相同");
    Check(idOk, "identity 再编码与原字节相同");
    Check(fileOk, "f! 再编码还原小写值名 + 原始大小写数据");
    Check(cfOk, "CF 再编码 = 0c000000");
    Check(unknownOk, "未知值在再编码里保留");

    // 写新记录时只给文件名，值名要按真机规则造（小写）
    CbsComponentRecord fresh;
    fresh.name = L"x_y";
    fresh.manifestSha256 = FromHex(kS256HHex);
    fresh.identity = "A";
    CbsFile newFile;
    newFile.fileName = L"AddInProcess.exe";
    fresh.files.push_back(newFile);
    bool freshNameOk = false, freshDataOk = false;
    for (const CbsValue& value : CbsEncodeComponentRecord(fresh))
    {
        if (value.name == "f!addinprocess.exe")
        {
            freshNameOk = true;
            freshDataOk = HexOf(value.data) == HexOf(CbsEncodeUtf16Text(L"AddInProcess.exe"));
        }
    }
    Check(freshNameOk, "新建记录的 f! 值名 = 文件名 ASCII 小写");
    Check(freshDataOk, "新建记录的 f! 数据 = 原始大小写 UTF-16");
    CheckAscii(CbsFileValueName(L"AspNetMMCExt.dll"), "f!aspnetmmcext.dll",
               "CbsFileValueName 造名规则");

    // 身份文本哈希（wcp.dll 的 RtlHashEncodedLBlob）+ 派生值名，全部对照 8061 真机：
    //   p!/s!/i! 共 4,892 条、f! 共 9,996 条实测 100% 命中。
    {
        const std::string payloadIdentity =
            "Microsoft-Windows-Common-Drivers-Package-drivers~31bf3856ad364e35~x86~en-US~6.2.8061.0.INF_1394";
        char buffer[32] = {0};
        std::snprintf(buffer, sizeof(buffer), "%016llx",
                      static_cast<unsigned long long>(CbsHashEncodedText(payloadIdentity)));
        CheckAscii(buffer, "c1a886620aabcbe2", "CbsHashEncodedText 命中真机载荷身份后缀");
        CheckAscii(CbsPayloadValueName("p!", payloadIdentity),
                   "p!CBS_microsoft-windows-common-drivers-package-drivers~31bf3856ad3_c1a886620aabcbe2",
                   "CbsPayloadValueName 造名规则（CBS_ + 小写截断 64 + _ + 16 hex）");
        CheckAscii(CbsPayloadValueName("i!", payloadIdentity),
                   "i!CBS_microsoft-windows-common-drivers-package-drivers~31bf3856ad3_c1a886620aabcbe2",
                   "CbsPayloadValueName 对 i!/s! 同样适用");
        CheckAscii(CbsFileValueName(L"PrintAdvancedInstaller.dll"),
                   "f!printadvancedinstaller.dl_b7ff1ded0fdd6573",
                   "文件名超过 25 字符 => 前 25 + _ + 16 hex");
        CheckAscii(CbsFileValueName(L"ConfigureIEOptionalComponentsAI.dll"),
                   "f!configureieoptionalcompon_1d4e2ab98ae3320c",
                   "更长的文件名同样按 25 字符截断");
        CheckAscii(CbsFileValueName(L"no_i.cur"), "f!no_i.cur",
                   "短文件名保持 f!<小写全名>");
    }

    // 错形态必须报错（这些错法在真机上不会出现，但写坏了会静默糟蹋台账）
    std::vector<CbsValue> badSha = values;
    badSha[0].data = FromHex("0011");
    CbsComponentRecord scratch;
    std::string badError;
    Check(!CbsDecodeComponentRecord(L"x_y", badSha, scratch, badError), "S256H 长度错时报错");

    std::vector<CbsValue> badMark;
    badMark.push_back(CbsValue{"S256H", FromHex(kS256HHex)});
    badMark.push_back(CbsValue{"identity", CbsEncodeAsciiText("A")});
    badMark.push_back(CbsValue{"c!dep", FromHex("01")});
    Check(!CbsDecodeComponentRecord(L"x_y", badMark, scratch, badError), "c! 不是空值时报错");

    std::vector<CbsValue> oddFile;
    oddFile.push_back(CbsValue{"S256H", FromHex(kS256HHex)});
    oddFile.push_back(CbsValue{"identity", CbsEncodeAsciiText("A")});
    oddFile.push_back(CbsValue{"f!a.dll", FromHex("4100ff")});
    Check(!CbsDecodeComponentRecord(L"x_y", oddFile, scratch, badError), "f! 字节数为奇数时报错");

    std::vector<CbsValue> noIdentity;
    noIdentity.push_back(CbsValue{"S256H", FromHex(kS256HHex)});
    Check(!CbsDecodeComponentRecord(L"x_y", noIdentity, scratch, badError), "缺 identity 时报错");

    // 真机里有 9,010 个长度为 0 的 f! 值，必须当"空文件名"处理而不是报错
    std::vector<CbsValue> emptyFile;
    emptyFile.push_back(CbsValue{"S256H", FromHex(kS256HHex)});
    emptyFile.push_back(CbsValue{"identity", CbsEncodeAsciiText("A")});
    emptyFile.push_back(CbsValue{"f!empty.dll", {}});
    CbsComponentRecord emptyRecord;
    Check(CbsDecodeComponentRecord(L"x_y", emptyFile, emptyRecord, badError), "空 f! 值可解码");
    CheckInt(static_cast<long long>(emptyRecord.files.size()), 1, "空 f! 值也保留一条");
}

static void DeploymentRecord()
{
    printf("\n-- 部署记录 --\n");
    std::vector<CbsValue> values;
    values.push_back(CbsValue{"appid", CbsEncodeAsciiText(kAppIdText)});
    values.push_back(CbsValue{"CatalogThumbprint", CbsEncodeAsciiText(kCatalogThumbprint)});
    values.push_back(CbsValue{"p!CBS_microsoft-windows-common-drivers-package-drivers~31bf3856ad3_c1a886620aabcbe2",
                              FromHex(kPayloadHex)});
    values.push_back(CbsValue{"s!CBS_microsoft-windows-common-drivers-package-drivers~31bf3856ad3_c1a886620aabcbe2",
                              FromHex(kPayloadHex)});
    values.push_back(CbsValue{"i!CBS_microsoft-windows-common-drivers-package-drivers~31bf3856ad3_c1a886620aabcbe2",
                              FromHex(kPayloadHex)});

    CbsDeploymentRecord record;
    std::string error;
    Check(CbsDecodeDeploymentRecord(FromUtf8OrAnsi(kDeploymentName), values, record, error),
          "部署记录解码成功");
    CheckAscii(record.appId, kAppIdText, "appid 原样保留");
    Check(record.hasCatalogThumbprint, "带 CatalogThumbprint");
    CheckAscii(record.catalogThumbprint, kCatalogThumbprint, "CatalogThumbprint 是 64 hex 文本");
    CheckInt(static_cast<long long>(record.payloads.size()), 3, "识别 p!/s!/i! 三个载荷");
    if (record.payloads.size() == 3)
    {
        CheckAscii(record.payloads[0].prefix, "p!", "第 1 个载荷前缀是 p!");
        CheckAscii(record.payloads[0].identity, kPayloadText, "载荷文本解出包身份");
        CheckAscii(record.payloads[0].valueName,
                   "p!CBS_microsoft-windows-common-drivers-package-drivers~31bf3856ad3_c1a886620aabcbe2",
                   "载荷完整值名（含派生后缀）原样保留");
    }

    const std::vector<CbsValue> back = CbsEncodeDeploymentRecord(record);
    bool payloadOk = false;
    for (const CbsValue& value : back)
    {
        if (value.name.rfind("p!", 0) == 0) payloadOk = HexOf(value.data) == kPayloadHex;
    }
    Check(payloadOk, "p! 载荷再编码与原字节相同（8 字节长度前缀 + ASCII）");

    // 长度前缀不匹配必须报错
    std::vector<uint8_t> truncated = FromHex(kPayloadHex);
    truncated.pop_back();
    std::vector<CbsValue> bad;
    bad.push_back(CbsValue{"appid", CbsEncodeAsciiText("A")});
    bad.push_back(CbsValue{"p!x", truncated});
    CbsDeploymentRecord scratch;
    std::string badError;
    Check(!CbsDecodeDeploymentRecord(L"x_y", bad, scratch, badError), "载荷被截断时报错");

    std::vector<CbsValue> noAppId;
    noAppId.push_back(CbsValue{"CatalogThumbprint", CbsEncodeAsciiText(kCatalogThumbprint)});
    Check(!CbsDecodeDeploymentRecord(L"x_y", noAppId, scratch, badError), "缺 appid 时报错");
}

static void IdentityRules()
{
    printf("\n-- 身份文本 --\n");
    CbsIdentity identity;
    std::string error;
    Check(CbsParseIdentity(kIdentityText, identity, error), "身份文本可解析");
    CheckAscii(identity.name, "Accessibility", "第一段是名字");
    CheckInt(static_cast<long long>(identity.attributes.size()), 4, "解析出 4 个属性");
    CheckAscii(CbsFormatIdentity(identity), kIdentityText, "身份文本可原样复原");

    CbsIdentity appId;
    Check(CbsParseIdentity(kAppIdText, appId, error), "带 versionScope 的身份可解析");
    CheckAscii(appId.name, "1394.inf-LanguagePack", "名字里可以带点号与短横线");

    CbsIdentity bad;
    Check(!CbsParseIdentity("", bad, error), "空身份报错");
    Check(!CbsParseIdentity("Name, Broken", bad, error), "属性段缺 '=' 报错");
    Check(!CbsParseIdentity("Name=1, Version=2", bad, error), "第一段带 '=' 报错");
}

static void ComponentNames()
{
    printf("\n-- 组件名形态 --\n");
    Check(CbsIsComponentName(FromUtf8OrAnsi(kComponentName)), "真机组件名形态合法");
    Check(!CbsIsComponentName(L"no-suffix"), "没有 16hex 后缀时不合法");
    Check(!CbsIsComponentName(L"name_31bf3856ad364e35_6.2.8061.0_none_77A15B1D27EC8860"),
          "大写 hex 不合法（真机一律小写）");
    Check(!CbsIsComponentName(L"name_31bf3856ad364e35_6.2.8061.0_none_77a15b1d27ec886"),
          "后缀长度不足 16 时不合法");
}

static void PseudoKeySuffix()
{
    // 组件键尾部 16 个 hex = wcp 对身份的折叠哈希。以下 5 条全部来自真机
    // DerivedData\Components（键名后缀与这里期望的 16 hex 逐字符相同）。
    printf("\n-- 组件伪键后缀 --\n");
    struct Golden
    {
        const char* identity;
        const char* suffix;
        const char* what;
    };
    const Golden goldens[] = {
        {"Accessibility, Culture=neutral, Version=4.0.8061.0, "
         "PublicKeyToken=b03f5f7f11d50a3a, ProcessorArchitecture=msil",
         "77a15b1d27ec8860", "Culture=neutral 跳过 culture 后命中"},
        {"AddInProcess, Culture=neutral, Version=4.0.8061.0, "
         "PublicKeyToken=b77a5c561934e089, ProcessorArchitecture=msil",
         "43abafe8d035e47d", "第二条 neutral 身份命中"},
        {"AddInProcess32, Culture=neutral, Version=6.2.8061.0, "
         "PublicKeyToken=b77a5c561934e089, ProcessorArchitecture=x86, "
         "versionScope=NonSxS",
         "0816495b60763e9c", "带 versionScope 的身份命中"},
        {"1394.inf, Culture=neutral, Type=driverUpdate, Version=6.2.8061.0, "
         "PublicKeyToken=31bf3856ad364e35, ProcessorArchitecture=x86, "
         "versionScope=NonSxS",
         "8235efbd79774ccd", "带 Type=driverUpdate 的驱动身份命中"},
        {"AddInUtil.Resources, Culture=en-us, Version=4.0.8061.0, "
         "PublicKeyToken=b77a5c561934e089, ProcessorArchitecture=msil",
         "af4e75efac64cc91", "非 neutral culture 参与折叠后命中"},
    };
    for (const Golden& golden : goldens)
    {
        CbsIdentity identity;
        std::string error;
        if (!CbsParseIdentity(golden.identity, identity, error))
        {
            Check(false, golden.what);
            continue;
        }
        const std::uint64_t want = std::stoull(golden.suffix, nullptr, 16);
        CheckInt(static_cast<long long>(CbsPseudoKeySuffix(identity)),
                 static_cast<long long>(want), golden.what);
    }

    // 折叠只认身份里出现的属性：Culture=neutral 与"没有 Culture"必须同值。
    CbsIdentity withNeutral;
    CbsIdentity withoutCulture;
    std::string error;
    Check(CbsParseIdentity(
              "X, Culture=neutral, Version=1.0.0.0, PublicKeyToken=31bf3856ad364e35",
              withNeutral, error),
          "neutral 身份可解析");
    Check(CbsParseIdentity("X, Version=1.0.0.0, PublicKeyToken=31bf3856ad364e35",
                           withoutCulture, error),
          "无 culture 身份可解析");
    CheckInt(static_cast<long long>(CbsPseudoKeySuffix(withNeutral)),
             static_cast<long long>(CbsPseudoKeySuffix(withoutCulture)),
             "Culture=neutral 与缺省 culture 折叠结果相同");
}

static void KeyNames()
{
    // 写侧的最后一公里：身份文本 -> 真机键名（含小写、字符过滤、超长省略）。
    printf("\n-- 键名生成 --\n");
    struct Golden
    {
        const char* identity;
        bool deployment;
        const char* key;
        const char* what;
    };
    const Golden goldens[] = {
        {"1394.inf, Culture=neutral, Type=driverUpdate, Version=6.2.8061.0, "
         "PublicKeyToken=31bf3856ad364e35, ProcessorArchitecture=x86, "
         "versionScope=NonSxS",
         false, "x86_1394.inf_31bf3856ad364e35_6.2.8061.0_none_8235efbd79774ccd",
         "组件键：neutral culture 写成 none"},
        {"microsoft-windows-base technologies-file systems (core), Culture=neutral, "
         "Version=6.2.8061.0, PublicKeyToken=31bf3856ad364e35, "
         "ProcessorArchitecture=x86, versionScope=NonSxS",
         false,
         "x86_microsoft-windows-b..ies-filesystemscore_31bf3856ad364e35_6.2.8061.0_none_"
         "333d9236635da6a7",
         "组件键：删空格/括号 + 名字省略成前 19 + .. + 后 19"},
        {"microsoft-windows-comctl32-v5.resources, Culture=sr-Latn-CS, "
         "Version=6.2.8061.0, PublicKeyToken=31bf3856ad364e35, "
         "ProcessorArchitecture=x86, versionScope=NonSxS",
         false,
         "x86_microsoft-windows-comctl32-v5.resources_31bf3856ad364e35_6.2.8061.0_sr-..-cs_"
         "70a81795c983c3dd",
         "组件键：culture 省略成前 3 + .. + 后 3"},
        {"1394.inf-LanguagePack, Culture=qps-ploc, Version=6.2.8061.0, "
         "PublicKeyToken=31bf3856ad364e35, ProcessorArchitecture=x86, "
         "versionScope=NonSxS",
         true, "1394.inf-languagepack_31bf3856ad364e35_6.2.8061.0_3e21a2537caa932d",
         "部署键：没有架构/culture 段"},
        {"atiilhag.inf-LanguagePack, Culture=qps-ploc, Version=6.2.8061.0, "
         "PublicKeyToken=31bf3856ad364e35, ProcessorArchitecture=x86, "
         "versionScope=NonSxS",
         true, "atiilhag.in..anguagepack_31bf3856ad364e35_6.2.8061.0_8b70cf47ccb5c24f",
         "部署键：名字省略成前 11 + .. + 后 11"},
    };
    for (const Golden& golden : goldens)
    {
        CbsIdentity identity;
        std::string error;
        if (!CbsParseIdentity(golden.identity, identity, error))
        {
            Check(false, golden.what);
            continue;
        }
        CheckAscii(golden.deployment ? CbsDeploymentKeyName(identity)
                                     : CbsComponentKeyName(identity),
                   golden.key, golden.what);
    }
}

int main()
{
    SetupConsoleUtf8();
    printf("OCID CBS 台账编解码自检\n");

    StoreHeader();
    ComponentRecord();
    DeploymentRecord();
    IdentityRules();
    ComponentNames();
    PseudoKeySuffix();
    KeyNames();

    printf("\n== 结果 ==\n  通过 %d / 失败 %d\n", g_pass, g_fail);
    return g_fail == 0 ? 0 : 1;
}
