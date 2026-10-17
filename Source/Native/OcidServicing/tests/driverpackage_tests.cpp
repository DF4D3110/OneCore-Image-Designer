// OCID - DMI（离线导入驱动包）自检
//
// 这个文件钉的是三件"算错就会静默写坏镜像"的事：
//   1. FileRepository 的目录名（StoreId）：`lower("<INF 名>_<架构>_<16 位十六进制>")`
//   2. DriverDatabase 的值名：8061 用 `%04x` 的**属性 id**，不是 Win10 的友好名
//   3. oemN.inf 的名分配：位图里**第一个 0 位**（每字节从高位到低位扫），号码不复用
//
// 证据链见 include/ocid/DriverPackage.h（符号化 drvstore.dll 的函数与行号）。
// 真机对照：`P:\Windows\INF\ax88772.inf`（8061 ARM 构建产物镜像）的目录名是
// `ax88772.inf_arm_f9bac5c487a52340`；镜像在就断言，不在就跳过（不伪装通过）。

#include "ocid/ConsoleUtf8.h"
#include "ocid/DriverPackage.h"

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

static bool FileExists(const wchar_t* path)
{
    FILE* f = nullptr;
    if (_wfopen_s(&f, path, L"rb") != 0 || f == nullptr) return false;
    fclose(f);
    return true;
}

static std::vector<std::uint8_t> Bytes(const char* text)
{
    std::vector<std::uint8_t> out;
    for (const char* p = text; *p; ++p) out.push_back(static_cast<std::uint8_t>(*p));
    return out;
}

// ---------------------------------------------------------------------------
// 1) 哈希：h = h*39 + byte，十六进制取小端 8 字节
// ---------------------------------------------------------------------------
static void HashAndStoreId()
{
    printf("\n== 哈希与 StoreId ==\n");

    // 手算：0x41 = 65 ⇒ ASCII 小端 = 41 00 00 00 00 00 00 00
    CheckInt(static_cast<long long>(ComputeInfPgHash(Bytes("A"))), 65, "单字节 0x41 的哈希 = 65");
    CheckStr(StoreIdHashSuffix(65), L"4100000000000000", "65 ⇒ 4100000000000000（小端、小写）");

    // 手算：'A'*39 + 'B' = 2535 + 66 = 2601 = 0x0A29 ⇒ 小端 = 29 0A ...
    CheckInt(static_cast<long long>(ComputeInfPgHash(Bytes("AB"))), 2601, "两字节 AB 的哈希 = 2601");
    CheckStr(StoreIdHashSuffix(2601), L"290a000000000000", "2601 ⇒ 290a000000000000（低字节在前）");

    // 顺序必须参与：AB 与 BA 必须不同（乘法-加法不是对称的）
    Check(ComputeInfPgHash(Bytes("AB")) != ComputeInfPgHash(Bytes("BA")), "字节顺序影响哈希（AB != BA）");

    // 十六进制必须是小写（StoreId 整体被 LCMapStringW 转小写）
    CheckStr(StoreIdHashSuffix(0xABCDEF0123456789ull), L"8967452301efcdab",
             "0xABCDEF0123456789 ⇒ 8967452301efcdab");

    // 文件名的"扩展名"参与、大小写不参与内容哈希但参与整体小写
    std::wstring id, err;
    Check(BuildStoreId(L"X:\\drivers\\AX88772.INF", Bytes("AB"), 5, id, err),
          "标题含大写、路径含目录时能算出 StoreId");
    CheckStr(id, L"ax88772.inf_arm_290a000000000000", "StoreId = 小写(标题_架构_哈希)");
    Check(err.empty(), "成功时没有错误文本");

    // 架构没有拼写 ⇒ 拒绝，而不是猜一个
    Check(!BuildStoreId(L"x\\a.inf", Bytes("AB"), 12, id, err), "架构 12（本版本无拼写）被拒绝");
    Check(!err.empty(), "拒绝时给出原因");

    // 空内容 ⇒ 拒绝（哈希会恒为 0，写出一个错误目录名）
    Check(!BuildStoreId(L"x\\a.inf", std::vector<std::uint8_t>(), 5, id, err), "空 INF 内容被拒绝");
    Check(!BuildStoreId(L"", Bytes("AB"), 5, id, err), "空路径被拒绝");
}

// ---------------------------------------------------------------------------
// 2) 架构字符串：8061 的 DLL 里只有这四种拼写
// ---------------------------------------------------------------------------
static void ArchStrings()
{
    printf("\n== 架构字符串 ==\n");
    CheckStr(ProcessorArchitectureString(0), L"x86", "0 ⇒ x86");
    CheckStr(ProcessorArchitectureString(5), L"arm", "5 ⇒ arm");
    CheckStr(ProcessorArchitectureString(6), L"ia64", "6 ⇒ ia64");
    CheckStr(ProcessorArchitectureString(9), L"amd64", "9 ⇒ amd64");
    Check(ProcessorArchitectureString(12) == nullptr, "12（arm64，本版本还没有）⇒ 没有拼写");
    Check(ProcessorArchitectureString(7) == nullptr, "7 ⇒ 没有拼写");
}

// ---------------------------------------------------------------------------
// 3) 真机对照：镜像里的 INF 必须算出镜像里的目录名
// ---------------------------------------------------------------------------
static void ImageGroundTruth()
{
    printf("\n== 真机对照（P: 上的 8061 ARM 镜像）==\n");

    const wchar_t* inf = L"P:\\Windows\\INF\\ax88772.inf";
    if (!FileExists(inf))
    {
        printf("  [skip] %s 不在（差分盘没挂）—— 跳过 StoreId 真机对照\n", ToUtf8(inf).c_str());
        return;
    }

    std::wstring id, err;
    Check(BuildStoreIdFromFile(inf, 5, id, err), "镜像里的 ax88772.inf 能算出 StoreId");
    // 这个目录名是镜像里真实存在的：P:\Windows\System32\DriverStore\FileRepository\<这个名字>
    CheckStr(id, L"ax88772.inf_arm_f9bac5c487a52340", "StoreId 与镜像里的目录名逐字符相同");
}

// ---------------------------------------------------------------------------
// 4) DriverDatabase 值名（8061 是属性 id，不是友好名）
// ---------------------------------------------------------------------------
static void DatabaseValueNames()
{
    printf("\n== DriverDatabase 值名 ==\n");

    CheckStr(DriverDatabaseValueName(3), L"0003", "属性 id 3 ⇒ 0003（镜像里 DriverDatabase\\0003 = 5）");
    CheckStr(DriverDatabaseValueName(4), L"0004", "属性 id 4 ⇒ 0004（oemN.inf 占用位图）");
    CheckStr(DriverDatabaseValueName(0x0a), L"000a", "属性 id 10 ⇒ 000a（小写十六进制）");
    CheckStr(DriverDatabaseValueName(0), L"0000", "0 ⇒ 0000");
    CheckStr(DriverDatabaseValueName(0x11), L"0011", "属性 id 17 ⇒ 0011");

    CheckStr(DriverDatabasePropertyName(DbObject::Database, 3), L"ProcessorArchitecture",
             "库级 3 = ProcessorArchitecture");
    CheckStr(DriverDatabasePropertyName(DbObject::Database, 4), L"OemDriverInfFileMap",
             "库级 4 = OemDriverInfFileMap");
    CheckStr(DriverDatabasePropertyName(DbObject::Package, 2), L"DriverInfName",
             "包级 2 = DriverInfName（oemN.inf）");
    CheckStr(DriverDatabasePropertyName(DbObject::Package, 3), L"OriginalInfName",
             "包级 3 = OriginalInfName（原始 INF 名）");
    CheckStr(DriverDatabasePropertyName(DbObject::Package, 15), L"DriverVersion", "包级 15 = DriverVersion");
    CheckStr(DriverDatabasePropertyName(DbObject::InfFile, 2), L"DriverPackages",
             "INF 文件级 2 = DriverPackages（StoreId 列表）");
    CheckStr(DriverDatabasePropertyName(DbObject::DeviceId, 3), L"DriverInfMatches",
             "设备级 3 = DriverInfMatches");
    Check(DriverDatabasePropertyName(DbObject::Package, 0x99) == nullptr, "未知属性 id ⇒ nullptr");
    CheckInt(kOemDriverInfFileMapSize, 4099, "oemN.inf 位图固定 4099 字节");
}

// ---------------------------------------------------------------------------
// 5) oemN.inf 分配：位图第一个 0 位（字节内从高位到低位）
// ---------------------------------------------------------------------------
static void OemInfNames()
{
    printf("\n== oemN.inf 名分配 ==\n");

    std::vector<std::uint8_t> map;
    OemInfAllocation a;
    std::wstring err;

    // 空位图（数据库里没有这个属性）= 从 oem0.inf 开始，并新建一个字节
    Check(AcquireOemInfName(map, a, err), "空位图上能分配");
    CheckStr(a.name, L"oem0.inf", "空位图 ⇒ oem0.inf");
    CheckInt(a.index, 0, "空位图 ⇒ 位序号 0");
    Check(a.grew, "空位图 ⇒ 位图增长");
    CheckInt(map.size(), 1, "分配后位图 1 字节");
    CheckInt(map[0], 0x80, "置的是最高位 0x80");

    Check(AcquireOemInfName(map, a, err), "第二次分配");
    CheckStr(a.name, L"oem1.inf", "同一字节里接着 ⇒ oem1.inf");
    CheckInt(map[0], 0xC0, "位图 = 0xC0（最高两位已占）");
    Check(!a.grew, "没有增长");

    // 补满第一个字节：0xC0 → 0xE0 → 0xF0 → 0xF8 → 0xFC → 0xFE → 0xFF
    const std::uint8_t expected[] = { 0xE0, 0xF0, 0xF8, 0xFC, 0xFE, 0xFF };
    for (int i = 0; i < 6; ++i)
    {
        Check(AcquireOemInfName(map, a, err), "继续分配");
        CheckInt(map[0], expected[i], "位图逐位补满");
    }
    CheckStr(a.name, L"oem7.inf", "第一个字节的最后一个号是 oem7.inf");

    // 全 0xFF ⇒ 追加一个字节，取它的最高位 ⇒ oem8.inf
    Check(AcquireOemInfName(map, a, err), "全 0xFF 时能分配");
    CheckStr(a.name, L"oem8.inf", "位图满 ⇒ 新字节的最高位 = oem8.inf");
    CheckInt(a.index, 8, "位序号 = 旧字节数 × 8");
    Check(a.grew, "位图增长了一个字节");
    CheckInt(map.size(), 2, "位图 2 字节");
    CheckInt(map[1], 0x80, "新字节是 0x80");

    // 低字节满、高字节有空位 ⇒ 跨字节找到那个 0 位
    std::vector<std::uint8_t> map2;
    map2.push_back(0xFF);
    map2.push_back(0x7F);   // 第 8 位（高字节最高位）是空的
    Check(AcquireOemInfName(map2, a, err), "跨字节找空位");
    CheckStr(a.name, L"oem8.inf", "高字节最高位空 ⇒ oem8.inf");
    CheckInt(map2[1], 0xFF, "那个位被置上");

    // 中间空位必须被选中（不能跳过）
    std::vector<std::uint8_t> map3;
    map3.push_back(0xF0);   // 低 4 位空
    Check(AcquireOemInfName(map3, a, err), "字节中间有空位");
    CheckStr(a.name, L"oem4.inf", "第一个 0 位是位 4 ⇒ oem4.inf");

    // 释放：清位，可复用
    std::vector<std::uint8_t> map4;
    map4.push_back(0xC0);
    Check(ReleaseOemInfName(map4, L"oem0.inf", err), "能释放 oem0.inf");
    CheckInt(map4[0], 0x40, "释放后位图 = 0x40");
    Check(AcquireOemInfName(map4, a, err), "释放后能重新分配");
    CheckStr(a.name, L"oem0.inf", "释放的号被复用（第一个 0 位）");

    // 释放的拒绝面：drvstore 用 swscanf_s(L"oem%u.inf")，非规范写法必须拒
    std::vector<std::uint8_t> map5;
    map5.push_back(0x80);
    Check(!ReleaseOemInfName(map5, L"oem01.inf", err), "拒绝 oem01.inf（非规范）");
    Check(!ReleaseOemInfName(map5, L"driver.inf", err), "拒绝不是 oemN.inf 的名字");
    Check(!ReleaseOemInfName(map5, L"oemX.inf", err), "拒绝非数字序号");
    Check(!ReleaseOemInfName(map5, L"oem9.inf", err), "拒绝位图里没有的序号（越界）");

    // 收件箱 / OS 引导 INF 保留原文件名，不占位图
    std::vector<std::uint8_t> map6;
    OemInfAllocation c;
    Check(ChoosePublishedInfName(0x200u, L"ax88772.inf", map6, c, err), "收件箱包能选名");
    CheckStr(c.name, L"ax88772.inf", "收件箱包保留原文件名");
    Check(map6.empty(), "收件箱包不动位图");
    Check(ChoosePublishedInfName(0x100u, L"layout.inf", map6, c, err), "OS/引导 INF 能选名");
    CheckStr(c.name, L"layout.inf", "OS/引导 INF 保留原文件名");
    Check(ChoosePublishedInfName(0u, L"ax88772.inf", map6, c, err), "第三方包能选名");
    CheckStr(c.name, L"oem0.inf", "第三方包拿 oem0.inf");
    CheckInt(map6.size(), 1, "第三方包占了位图");

    CheckInt(ImportKeepsOriginalInfName(0x100), 1, "标志 0x100 ⇒ 保留原名");
    CheckInt(ImportKeepsOriginalInfName(0x200), 1, "标志 0x200 ⇒ 保留原名");
    CheckInt(ImportKeepsOriginalInfName(0x40), 0, "标志 0x40 ⇒ 不保留");
    CheckInt(ImportKeepsOriginalInfName(0), 0, "无标志 ⇒ 不保留");

    // 位图上限：4099 字节
    std::vector<std::uint8_t> full(kOemDriverInfFileMapSize, 0xFF);
    Check(!AcquireOemInfName(full, a, err), "位图满到上限 ⇒ 拒绝，不越界增长");
    Check(!err.empty(), "位图满时给出原因");
}

// 驱动包清单里"哪些文件进 store"由 importPath 决定（真机 231 个 arm_*.inf_*.manifest）
static void ImportFileSet()
{
    CheckInt((long long)ClassifyDriverImportFile(L"$(build.nttree)\\driver_infs\\"),
             (long long)DriverImportFileKind::Inf, "末段 driver_infs ⇒ INF");
    CheckInt((long long)ClassifyDriverImportFile(L"$(build.nttree)\\printer_infs\\"),
             (long long)DriverImportFileKind::Inf, "末段 printer_infs ⇒ INF（真机 22 条）");
    CheckInt((long long)ClassifyDriverImportFile(L"$(build.nttree)\\manifested_infs\\"),
             (long long)DriverImportFileKind::Inf, "末段 manifested_infs ⇒ INF（真机 1 条）");
    CheckInt((long long)ClassifyDriverImportFile(L"$(build.nttree)\\multi\\"),
             (long long)DriverImportFileKind::DriverBinary, "multi 不是 INF 暂存目录（真机 17 条，当载荷）");
    CheckInt((long long)ClassifyDriverImportFile(L"$(build.nttree)\\ua\\"),
             (long long)DriverImportFileKind::DriverBinary, "ua 不是 INF 暂存目录（真机 1 条，当载荷）");
    Check(IsInfStagingSegment(L"PRINTER_INFS"), "INF 暂存目录名大小写不敏感");
    Check(!IsInfStagingSegment(L"multi"), "multi 不是暂存目录");
    Check(!IsInfStagingSegment(L""), "空段不是暂存目录");
    CheckInt((long long)ClassifyDriverImportFile(L"$(build.nttree)\\driver_infs"),
             (long long)DriverImportFileKind::Inf, "少了尾分隔符也算 INF");
    CheckInt((long long)ClassifyDriverImportFile(L"$(BUILD.NTTREE)\\DRIVER_INFS\\"),
             (long long)DriverImportFileKind::Inf, "宏大小写不敏感");
    CheckInt((long long)ClassifyDriverImportFile(L"$(build.nttree)\\"),
             (long long)DriverImportFileKind::DriverBinary, "构建树根 ⇒ 驱动载荷");
    CheckInt((long long)ClassifyDriverImportFile(L"$(build.nttree)"),
             (long long)DriverImportFileKind::DriverBinary, "构建树根本身 ⇒ 驱动载荷");
    CheckInt((long long)ClassifyDriverImportFile(L"$(build.nttree)\\drivers\\"),
             (long long)DriverImportFileKind::DriverBinary, "构建树子目录 ⇒ 驱动载荷");
    CheckInt((long long)ClassifyDriverImportFile(L""),
             (long long)DriverImportFileKind::NotImported, "没有 importPath ⇒ 不导入");
    CheckInt((long long)ClassifyDriverImportFile(L"$(runtime.system32)\\"),
             (long long)DriverImportFileKind::NotImported, "别的树 ⇒ 不导入");
    // `$(build.nttree)x` 不是构建树子路径，不能算载荷
    CheckInt((long long)ClassifyDriverImportFile(L"$(build.nttree)x\\driver_infs\\"),
             (long long)DriverImportFileKind::Inf, "只有末段说了算，前缀不匹配也算 INF");

    Check(IsDriverUpdateComponent(L"driverUpdate", L"nonSxS"), "driverUpdate/nonSxS ⇒ 驱动包");
    Check(IsDriverUpdateComponent(L"DRIVERUPDATE", L"NONSXS"), "identity 大小写不敏感");
    Check(!IsDriverUpdateComponent(L"", L""), "没有 type ⇒ 不是驱动包");
    Check(!IsDriverUpdateComponent(L"driverUpdate", L"SxS"), "versionScope=SxS ⇒ 不是驱动包");
    Check(!IsDriverUpdateComponent(L"win32", L"nonSxS"), "type=win32 ⇒ 不是驱动包");
}

// 导入计划：真机 arm_acpi.inf 的两个 <file> + 一条不在构建树里的干扰项
static void ImportPlan()
{
    const std::vector<std::uint8_t> inf = {'a', 'c', 'p', 'i'};

    std::vector<DriverImportFileSpec> files;
    files.push_back({L"acpi.inf", L"acpi.inf", L"$(build.nttree)\\driver_infs\\"});
    files.push_back({L"acpi.sys", L"acpi.sys", L"$(build.nttree)\\"});
    files.push_back({L"readme.txt", L"readme.txt", L"$(runtime.system32)\\"});

    const DriverImportPlan p = BuildDriverImportPlan(files, L"acpi.inf", inf, 5);
    Check(p.ok, "两个构建树文件 + 一个干扰项 ⇒ 计划成立");
    CheckInt((long long)p.files.size(), 2, "进库的只有 INF 与载荷");
    CheckInt((long long)p.notImported, 1, "干扰项被计数");
    Check(p.notes.size() == 1, "干扰项有解释（不静默丢）");
    Check(p.infName == L"acpi.inf", "认出那个 INF");

    // StoreId 必须与直接算的一致（同一份字节）
    std::wstring direct, derr;
    Check(BuildStoreId(L"acpi.inf", inf, 5, direct, derr), "直接算 StoreId 成功");
    Check(p.storeId == direct, "计划里的 StoreId == 直接算的");
    Check(p.storeId.find(L"acpi.inf_arm_") == 0, "目录名是 <标题>_<架构>_<16 hex> 的小写形式");
    Check(p.storeId == [&]{ std::wstring s; for (wchar_t c : p.storeId) s.push_back(c >= L'A' && c <= L'Z' ? (wchar_t)(c+32) : c); return s; }(),
          "目录名全小写");

    // 0 个 INF / 2 个 INF：都必须失败并说清
    std::vector<DriverImportFileSpec> none;
    none.push_back({L"acpi.sys", L"acpi.sys", L"$(build.nttree)\\"});
    const DriverImportPlan p0 = BuildDriverImportPlan(none, L"acpi.inf", inf, 5);
    Check(!p0.ok && p0.error.find(L"0 条") != std::wstring::npos, "没有 INF ⇒ 失败并给出条数");
    Check(p0.files.empty() || !p0.ok, "失败时不给出可用的库目录");

    std::vector<DriverImportFileSpec> two = files;
    two.push_back({L"acpi2.inf", L"acpi2.inf", L"$(build.nttree)\\printer_infs\\"});
    const DriverImportPlan p2 = BuildDriverImportPlan(two, L"acpi.inf", inf, 5);
    Check(!p2.ok, "两个 INF ⇒ 失败（不挑一个）");
    Check(p2.error.find(L"acpi.inf") != std::wstring::npos && p2.error.find(L"acpi2.inf") != std::wstring::npos,
          "指出是哪两个 INF");

    // 架构算不出来 ⇒ 不能默默按某个架构算目录名
    const DriverImportPlan pA = BuildDriverImportPlan(files, L"acpi.inf", inf, 7);
    Check(!pA.ok, "架构 7 不认识 ⇒ 失败");
    Check(!pA.error.empty(), "架构不认识时给出原因");
}

int main()
{
    printf("OCID DMI 自检\n");

    HashAndStoreId();
    ArchStrings();
    ImageGroundTruth();
    DatabaseValueNames();
    OemInfNames();
    ImportFileSet();
    ImportPlan();

    printf("\n== 结果 ==\n  通过 %d / 失败 %d\n", g_pass, g_fail);
    return g_fail == 0 ? 0 : 1;
}

