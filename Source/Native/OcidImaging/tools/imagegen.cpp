// OCID Imaging 入口：ocid_imagegen.exe
//
// 【职责】把 imageapp 那条流水线拆成可以单独跑、单独对拍的几步：
//   plan      选包（OEMInput + FM → 有序包清单 + 统计）
//   schema    用仓库里的 XSD 校验输入 XML（OEMInput / FeatureManifest）
//   cab       读 .cab（列目录 / 抽单个条目 / 全解）
//   version   版本
// 后续会加：stage（装包/建盘编排）、finalize（收尾）。当前先把"决策"这一层做扎实，
// 因为它决定后面所有阶段对不对。
//
// 【约定】数据走 stdout（UTF-8），诊断走 stderr；退出码 0 成功、1 失败、2 用法错误。
#include "ocid/imaging/Cab.h"
#include "ocid/imaging/Common.h"
#include "ocid/imaging/Plan.h"

#include <algorithm>
#include <cctype>
#include <cstdio>
#include <memory>
#include <cstring>
#include <filesystem>
#include <set>
#include <string>
#include <vector>

using namespace ocid::imaging;

namespace
{

const char* const kVersion = "0.4.0";

void PrintUsage()
{
    OutLn("ocid_imagegen " + std::string(kVersion) + " —— 镜像构建（选包 / 校验 / 读包）");
    OutLn("");
    OutLn("用法：");
    OutLn("  ocid_imagegen plan --oem-input <OEMInput.xml> [选项]");
    OutLn("  ocid_imagegen schema validate <XML> --schema <XSD> [--schema <XSD>...]");
    OutLn("  ocid_imagegen schema pick --build <构建号> [--family V1|V2] [--schema-dir <目录>]");
    OutLn("  ocid_imagegen packagelist --oem-input <OEMInput.xml> --cabs <cab 目录> --out <PackageList.xml>");
    OutLn("  ocid_imagegen cab list <文件.cab>");
    OutLn("  ocid_imagegen cab entry <文件.cab> <条目名> --out <文件>");
    OutLn("  ocid_imagegen cab extract <文件.cab> --out <目录>");
    OutLn("  ocid_imagegen layout show <DeviceLayout.xml|OEMDevicePlatform.xml>");
    OutLn("  ocid_imagegen layout validate <文件> [--schema-dir <目录>]");
    OutLn("  ocid_imagegen layout check <文件> [--disk-sectors <N>] [--min-sector-count <N>]");
    OutLn("  ocid_imagegen version");
    OutLn("");
    OutLn("plan 选项：");
    OutLn("  --oem-input <文件>       OEMInput.xml（必需）");
    OutLn("  --fm-dir <目录>          FeatureManifest 目录；不给则用 OEMInput 所在目录");
    OutLn("  --fm <文件>              显式指定一个 FM（可重复；给了就不再枚举目录）");
    OutLn("  --arch <架构>            目标架构：amd64（默认）/ arm64 / x86");
    OutLn("  --out <文件>             写 plan.xml（本工具的中间格式）");
    OutLn("  --list <文件>            写 cab 名清单（每行一个，便于和参照产物 diff）");
    OutLn("  --keep-duplicates        保留同一 cab 的多次出现（对齐 imageapp 的 UpdateInput）");
    OutLn("  --log <文件>             写流水日志（{时间}  消息）");
    OutLn("  --build <构建号>         XSD 家族选择用（如 20279；不给按 V2 基线）");
    OutLn("  --family V1|V2           OEMInput schema 家族（默认 V2）");
    OutLn("  --no-validate            跳过 XSD 校验（默认会校验 OEMInput + 每个 FM）");
    OutLn("  --validate               严格校验：schema 找不到也算失败");
    OutLn("");
    OutLn("packagelist 选项：");
    OutLn("  --cabs <目录>           包集所在目录（按 plan 选出的 cab 名去找）");
    OutLn("  --out <文件>            写 PackageList.xml（对拍 imageapp 产物）");
    OutLn("  元数据取自每个包内的 update.mum（phoneInformation / targetPartition / version）");
    OutLn("");
    OutLn("cab 说明：");
    OutLn("  list     打印压缩法、块数、包内条目（名字 / 大小 / folder / 时间）");
    OutLn("  entry    抽出一个条目；--out 给文件则写文件，不给则把内容写到 stdout");
    OutLn("  extract  全解到 --out 目录，保留 CAB 内相对路径");
    OutLn("  NONE / MSZIP 用自带解码器；LZX / Quantum 走系统 cabinet.dll 的 FDI（结果与 expand.exe 逐字节一致）");
    OutLn("");
    OutLn("layout 说明：");
    OutLn("  show      解析 DeviceLayout（v1 / v2 / MSDeviceLayout）或 OEMDevicePlatform，按 store/分区打印");
    OutLn("  validate  按根元素选 XSD 校验：DeviceLayout→DeviceLayout.xsd/v2、MSDeviceLayout→DeviceLayoutv2MS.xsd、");
    OutLn("            OEMDevicePlatform→OEMDevicePlatform.xsd（都在 Schemas\\adk\\，由 build-imaging.ps1 复制到 exe\\schemas）");
    OutLn("  check     语义检查（不是 XSD）：池 store 尺寸一致性、UseAllSpace 与 store 同尺寸、BS_EFIESP 最小尺寸、");
    OutLn("            物理盘必须有 OSPool 分区、OEMDevicePlatform.MinSectorCount 与目标盘容量");
    OutLn("");
    OutLn("通用选项：");
    OutLn("  --schema-dir <目录>      放 XSD 的目录（默认找 <exe>\\schemas 再找仓库 Schemas\\）");
    OutLn("  --help                   显示本帮助");
}

// 按 build 选 OEMInput schema 家族（与 Services/OemInputSchemaFamilyService.cs 一致）
std::string OemSchemaFamily(int build, const std::string& version)
{
    const bool v2 = EqualsNoCase(version, "V2");
    if (build <= 0)
    {
        return v2 ? "OEMInput.V2.xsd" : "OEMInput.V1.xsd";
    }
    if (!v2)
    {
        if (build <= 14959) return "OEMInput.V1.10527-14959.xsd";
        if (build <= 17040) return "OEMInput.V1.14986-17040.xsd";
        if (build <= 17125) return "OEMInput.V1.17047-17125.xsd";
        if (build <= 18362) return "OEMInput.V1.17666-18362.xsd";
        if (build <= 18941) return "OEMInput.V1.18890-18941.xsd";
        if (build == 18963) return "OEMInput.V1.18963.xsd";
        return "OEMInput.V1.xsd";
    }
    if (build < 18963) return "OEMInput.V1.xsd";
    if (build == 18963) return "OEMInput.V2.18963.xsd";
    if (build <= 19041) return "OEMInput.V2.18985-19041.xsd";
    if (build < 22621) return "OEMInput.V2.xsd";
    return "OEMInput.V2.22621.xsd";
}

std::wstring FindSchema(const std::string& schemaDir, const std::string& name)
{
    std::vector<std::wstring> dirs;
    if (!schemaDir.empty())
    {
        dirs.push_back(Utf8ToWide(schemaDir));
        dirs.push_back(CombinePath(Utf8ToWide(schemaDir), L"adk"));
    }
    const std::wstring exeDir = ExeDirectory();
    // 运行时：<exe>\schemas（构建脚本发布）；开发时：仓库 Schemas\ 与 Schemas\adk\
    // （Schemas\adk 是从 ADK 工具集 DLL 嵌入资源抽出的 28 个 XSD，见 build-imaging.ps1）
    const std::wstring bases[] = {
        CombinePath(exeDir, L"schemas"),
        CombinePath(exeDir, L"..\\schemas"),
        CombinePath(exeDir, L"..\\..\\..\\..\\Schemas"),
        CombinePath(exeDir, L"..\\..\\..\\Schemas"),
    };
    for (const std::wstring& base : bases)
    {
        dirs.push_back(base);
        dirs.push_back(CombinePath(base, L"adk"));
    }
    for (const std::wstring& dir : dirs)
    {
        const std::wstring candidate = CombinePath(dir, Utf8ToWide(name));
        if (FileExists(candidate))
        {
            return candidate;
        }
    }
    return std::wstring();
}

int CmdPlan(const std::vector<std::string>& args)
{
    PlanInput input;
    std::string outPath;
    std::string listPath;
    std::string logPath;
    std::string schemaDir;
    bool validate = true;          // 默认就用现成 XSD 校验（规则不在这里重写）
    bool validateStrict = false;   // --validate：schema 找不到也算错
    int build = 0;
    std::string family = "V2";
    for (std::size_t i = 2; i < args.size(); ++i)
    {
        const std::string& arg = args[i];
        auto next = [&](std::string& target) -> bool {
            if (i + 1 >= args.size())
            {
                Err("缺少值：" + arg);
                return false;
            }
            target = args[++i];
            return true;
        };
        if (arg == "--oem-input") { if (!next(input.oemInputPath)) return 2; }
        else if (arg == "--fm-dir") { if (!next(input.fmDir)) return 2; }
        else if (arg == "--fm") { std::string value; if (!next(value)) return 2; input.fmFiles.push_back(value); }
        else if (arg == "--arch") { if (!next(input.arch)) return 2; }
        else if (arg == "--out") { if (!next(outPath)) return 2; }
        else if (arg == "--list") { if (!next(listPath)) return 2; }
        else if (arg == "--log") { if (!next(logPath)) return 2; }
        else if (arg == "--schema-dir") { if (!next(schemaDir)) return 2; }
        else if (arg == "--family") { if (!next(family)) return 2; }
        else if (arg == "--build") { std::string value; if (!next(value)) return 2; build = std::atoi(value.c_str()); }
        else if (arg == "--keep-duplicates") { input.keepDuplicates = true; }
        else if (arg == "--validate") { validate = true; validateStrict = true; }
        else if (arg == "--no-validate") { validate = false; }
        else if (arg == "--help" || arg == "-h") { PrintUsage(); return 0; }
        else { Err("未知参数：" + arg); return 2; }
    }
    if (input.oemInputPath.empty())
    {
        Err("必须给 --oem-input");
        return 2;
    }
    if (!logPath.empty())
    {
        std::string error;
        if (!LogOpen(Utf8ToWide(logPath), error))
        {
            Err(error);
            return 1;
        }
    }
    LogWrite(LogLevel::Info, "plan: oem-input = " + input.oemInputPath);
    LogWrite(LogLevel::Info, "plan: arch = " + input.arch);

    // XSD 校验是默认开的：用的是 ADK 工具集里的原版 XSD（Schemas\adk\，由 DLL 嵌入资源
    // 抽出），规则不在这里重写。注意 MSXML 的语义——文档根命名空间与 schema 的
    // targetNamespace 对不上时它会"静默通过"，所以必须按构建号选对家族；
    // 这里只断言"结构是否合法"，语义规则（targeting / overlap / 盘符）另行实现。
    if (validate)
    {
        const std::string name = OemSchemaFamily(build, family);
        const std::wstring schema = FindSchema(schemaDir, name);
        if (schema.empty())
        {
            if (validateStrict)
            {
                Err("找不到 XSD：" + name + "（用 --schema-dir 指定 Schemas 目录）");
                LogClose();
                return 1;
            }
            Err("提示：找不到 XSD " + name + "，跳过 OEMInput 结构校验");
        }
        else
        {
            std::string error;
            if (!ValidateXmlAgainstSchema(Utf8ToWide(input.oemInputPath), {schema}, error))
            {
                // 10x（build >= 20000）的 OEMInput 变体 XSD 目前不可得：本机 ADK 只到 22621，
                // 10x 的 featureapi.dll 也没随包集提供。基线 V2 schema 对这种文档只会报
                // "空容器元素不能为空"（0xC00CE011，例如 <AppXOptionalPackages/>），属 schema
                // 变体差异而非文档错误。这种情况按提示处理并继续（FM 仍严格校验）。
                const bool tenX = (build == 0 || build >= 20000);
                if (tenX && error.find("0xC00CE011") != std::string::npos)
                {
                    Err("提示：OEMInput 与基线 V2 schema 存在变体差异（10x 的 OEMInput XSD 未随"
                        "本机 ADK 提供），跳过这一条继续：" + error);
                    LogWrite(LogLevel::Warn, "oem-input schema variant skipped: " + error);
                }
                else
                {
                    Err("OEMInput 校验失败：" + error);
                    LogWrite(LogLevel::Error, "oem-input invalid: " + error);
                    LogClose();
                    return 1;
                }
            }
            else
            {
                OutLn("OEMInput 校验通过（schema=" + name + "）");
                LogWrite(LogLevel::Info, "oem-input validated with " + name);
            }
        }

        // 每个 FM 也按 FeatureManifest.xsd 校验（FM 根元素 = FeatureManifest，
        // 命名空间 http://schemas.microsoft.com/embedded/2004/10/ImageUpdate，一致）。
        const std::wstring fmSchema = FindSchema(schemaDir, "FeatureManifest.xsd");
        // FeatureManifest.xsd 是"多文件同命名空间"的 schema 集：它引用的 AppXType 等类型
        // 声明在同目录的 Appx.FMAddOnAppX.xsd / Drivers.FMAddOnDrivers.xsd 里，只 add 一份
        // 会被 MSXML 拒绝（未声明的 XSD 类型）。这里把能找到的同族文件一起交给校验器，
        // 由它生成 xsd:include 包装文档再 add 一次。
        std::vector<std::wstring> fmSchemas;
        if (!fmSchema.empty())
        {
            fmSchemas.push_back(fmSchema);
            for (const char* extraName : {"Appx.FMAddOnAppX.xsd", "Drivers.FMAddOnDrivers.xsd"})
            {
                const std::wstring extra = FindSchema(schemaDir, extraName);
                if (!extra.empty() && extra != fmSchema) fmSchemas.push_back(extra);
            }
        }
        std::vector<std::string> fmFiles = input.fmFiles;
        if (fmFiles.empty())
        {
            const std::wstring oemName =
                std::filesystem::path(Utf8ToWide(input.oemInputPath)).filename().wstring();
            const std::wstring dir = input.fmDir.empty()
                ? std::filesystem::path(Utf8ToWide(input.oemInputPath)).parent_path().wstring()
                : Utf8ToWide(input.fmDir);
            std::vector<std::wstring> found;
            std::error_code ec;
            for (const auto& entry : std::filesystem::directory_iterator(std::filesystem::path(dir), ec))
            {
                if (!entry.is_regular_file()) continue;
                const std::wstring ext = entry.path().extension().wstring();
                if (ext == L".xml" || ext == L".XML") found.push_back(entry.path().wstring());
            }
            std::sort(found.begin(), found.end());
            for (const std::wstring& p : found)
            {
                if (std::filesystem::path(p).filename().wstring() == oemName) continue;
                fmFiles.push_back(WideToUtf8(p));
            }
        }
        if (fmSchema.empty())
        {
            if (validateStrict)
            {
                Err("找不到 XSD：FeatureManifest.xsd（用 --schema-dir 指定 Schemas 目录）");
                LogClose();
                return 1;
            }
            Err("提示：找不到 FeatureManifest.xsd，跳过 FM 结构校验");
        }
        else
        {
            std::size_t okCount = 0;
            std::size_t badCount = 0;
            for (const std::string& fm : fmFiles)
            {
                std::string error;
                if (ValidateXmlAgainstSchema(Utf8ToWide(fm), fmSchemas, error))
                {
                    ++okCount;
                    continue;
                }
                ++badCount;
                Err("FM 校验失败：" + fm + " -> " + error);
            }
            OutLn("FM 校验：" + std::to_string(okCount) + " 通过，" +
                  std::to_string(badCount) + " 失败");
            LogWrite(LogLevel::Info, "fm validated: ok=" + std::to_string(okCount) +
                                     " bad=" + std::to_string(badCount));
            if (badCount != 0)
            {
                LogClose();
                return 1;
            }
        }
    }

    PlanResult plan;
    std::string error;
    if (!BuildPlan(input, plan, error))
    {
        Err(error);
        LogClose();
        return 1;
    }

    const PlanStats& stats = plan.stats;
    OutLn("FM 数 = " + std::to_string(stats.fmCount) +
          "，看过条目 = " + std::to_string(stats.examined) +
          "，判据命中 = " + std::to_string(stats.selected) +
          "，展开 = " + std::to_string(stats.expanded) +
          "，去重后 = " + std::to_string(stats.unique) +
          "（重复 " + std::to_string(stats.duplicates) +
          "，判据未中 " + std::to_string(stats.skippedFilter) +
          "，架构不匹配 " + std::to_string(stats.skippedArch) + "）");
    for (const std::string& warning : plan.warnings)
    {
        OutLn("  " + warning);
    }

    if (!outPath.empty())
    {
        if (!WritePlanXml(plan, outPath, error))
        {
            Err(error);
            LogClose();
            return 1;
        }
        OutLn("已写 plan：" + outPath);
        LogWrite(LogLevel::Info, "plan written: " + outPath);
    }
    if (!listPath.empty())
    {
        if (!WriteCabList(plan, listPath, error))
        {
            Err(error);
            LogClose();
            return 1;
        }
        OutLn("已写 cab 清单：" + listPath);
        LogWrite(LogLevel::Info, "cab list written: " + listPath);
    }
    LogClose();
    return 0;
}

int CmdSchema(const std::vector<std::string>& args)
{
    if (args.size() < 3)
    {
        Err("用法：ocid_imagegen schema validate <XML> --schema <XSD>…  |  schema pick --build <N>");
        return 2;
    }
    const std::string& mode = args[2];
    if (mode == "validate")
    {
        std::string xml;
        std::vector<std::wstring> schemas;
        for (std::size_t i = 3; i < args.size(); ++i)
        {
            if (args[i] == "--schema" && i + 1 < args.size())
            {
                schemas.push_back(Utf8ToWide(args[++i]));
            }
            else if (xml.empty())
            {
                xml = args[i];
            }
            else
            {
                Err("未知参数：" + args[i]);
                return 2;
            }
        }
        if (xml.empty() || schemas.empty())
        {
            Err("需要 XML 与至少一个 --schema");
            return 2;
        }
        std::string error;
        if (!ValidateXmlAgainstSchema(Utf8ToWide(xml), schemas, error))
        {
            Err("校验失败：" + error);
            return 1;
        }
        OutLn("校验通过：" + xml);
        return 0;
    }
    if (mode == "pick")
    {
        int build = 0;
        std::string family = "V2";
        std::string schemaDir;
        for (std::size_t i = 3; i < args.size(); ++i)
        {
            if (args[i] == "--build" && i + 1 < args.size()) build = std::atoi(args[++i].c_str());
            else if (args[i] == "--family" && i + 1 < args.size()) family = args[++i];
            else if (args[i] == "--schema-dir" && i + 1 < args.size()) schemaDir = args[++i];
            else { Err("未知参数：" + args[i]); return 2; }
        }
        const std::string name = OemSchemaFamily(build, family);
        OutLn(name);
        const std::wstring path = FindSchema(schemaDir, name);
        if (!path.empty())
        {
            OutLn("找到：" + WideToUtf8(path));
        }
        else
        {
            Err("目录里没有该 XSD：用 --schema-dir 指定 Schemas 目录");
            return 1;
        }
        return 0;
    }
    Err("未知 schema 子命令：" + mode);
    return 2;
}

// ── cab：读 .cab（列目录 / 抽条目 / 全解）─────────────────────────────────
int CmdCab(const std::vector<std::string>& args)
{
    if (args.size() < 3)
    {
        Err("用法：ocid_imagegen cab list|entry|extract <文件.cab> …");
        return 2;
    }
    const std::string& mode = args[2];
    if (mode == "list")
    {
        if (args.size() < 4)
        {
            Err("用法：ocid_imagegen cab list <文件.cab>");
            return 2;
        }
        CabInfo info;
        std::string error;
        if (!CabList(Utf8ToWide(args[3]), info, error))
        {
            Err(error);
            return 1;
        }
        OutLn("文件：" + args[3]);
        OutLn("结构：CAB 声明 " + std::to_string(info.bytes) + " 字节，folder " +
              std::to_string(info.folderCount) + " 个，块 " + std::to_string(info.blockCount) +
              " 个，条目 " + std::to_string(info.files.size()) + " 个");
        OutLn(std::string("压缩：") + CabCompressionName(info.compression) + "，解开后 " +
              std::to_string(info.totalSize) + " 字节");
        for (const CabEntry& entry : info.files)
        {
            OutLn("  " + entry.name + "  " + std::to_string(entry.size) + " 字节  folder " +
                  std::to_string(entry.folder) + " 偏移 " + std::to_string(entry.offset));
        }
        return 0;
    }
    if (mode == "entry")
    {
        if (args.size() < 5)
        {
            Err("用法：ocid_imagegen cab entry <文件.cab> <条目名> [--out <文件>]");
            return 2;
        }
        const std::string cabPath = args[3];
        const std::string entryName = args[4];
        std::string outPath;
        for (std::size_t i = 5; i < args.size(); ++i)
        {
            if (args[i] == "--out" && i + 1 < args.size()) outPath = args[++i];
            else { Err("未知参数：" + args[i]); return 2; }
        }
        std::string error;
        std::vector<std::uint8_t> bytes;
        if (!CabExtractEntry(Utf8ToWide(cabPath), entryName, bytes, error))
        {
            Err(error);
            return 1;
        }
        if (outPath.empty())
        {
            // 不给 --out 就写 stdout（适合抽 .manifest 直接看）。
            std::fwrite(bytes.data(), 1, bytes.size(), stdout);
            std::fflush(stdout);
        }
        else
        {
            if (!WriteFileBytes(Utf8ToWide(outPath), bytes.data(), bytes.size(), error))
            {
                Err(error);
                return 1;
            }
            Err("已写出 " + std::to_string(bytes.size()) + " 字节 → " + outPath);
        }
        return 0;
    }
    if (mode == "extract")
    {
        if (args.size() < 4)
        {
            Err("用法：ocid_imagegen cab extract <文件.cab> --out <目录>");
            return 2;
        }
        const std::string cabPath = args[3];
        std::string outDir;
        for (std::size_t i = 4; i < args.size(); ++i)
        {
            if (args[i] == "--out" && i + 1 < args.size()) outDir = args[++i];
            else { Err("未知参数：" + args[i]); return 2; }
        }
        if (outDir.empty())
        {
            Err("需要 --out <目录>");
            return 2;
        }
        std::string error;
        std::vector<std::string> written;
        if (!CabExtractAll(Utf8ToWide(cabPath), Utf8ToWide(outDir), written, error))
        {
            Err(error);
            return 1;
        }
        OutLn("解开 " + std::to_string(written.size()) + " 个条目 → " + outDir);
        return 0;
    }
    Err("未知 cab 子命令：" + mode);
    return 2;
}

// ── packagelist：按包集写 UpdateOSOutput（对拍 imageapp 的 <前缀>.PackageList.xml）──
// 【元数据从哪来】imageapp 的包清单不是从 cab 名猜的。每个包的元数据都在包内 update.mum：
//   <assemblyIdentity version="10.0.20279.1002">          → Identity/Version
//   <package targetPartition="EFIESP" ...>                → Partition
//   <customInformation>/<phoneInformation
//        phoneRelease="Production" phoneOwner="Microsoft"
//        phoneOwnerType="Microsoft" phoneComponent="OneCore.BcdCoreCore">
//                                                          → ReleaseType / Owner / OwnerType / Component
// 这三处来源是实测出来的：`WindowsDevicePortal-WCOS-Package` 的 phoneOwner=Microsoft、
// phoneComponent=WindowsDevicePortal.WCOS，靠 cab 名推不出来（点分包名都会推错）。
// Version 的映射同样是实测的：10.0.20279.1002 → Major=10 Minor=0 Build=1002 Qfe=0
// （取第 1/2/4 段——Build 不是第 3 段，这是 imageapp 的实际行为，照抄以免对不上）。
int CmdPackageList(const std::vector<std::string>& args)
{
    std::string oemInput;
    std::string fmDir;
    std::string cabsDir;
    std::string outPath;
    std::string logPath;
    std::string arch = "arm64";
    std::vector<std::string> fmFiles;

    for (std::size_t i = 2; i < args.size(); ++i)
    {
        const std::string& a = args[i];
        if (a == "--oem-input" || a == "--fm-dir" || a == "--fm" || a == "--arch" ||
            a == "--cabs" || a == "--out" || a == "--log")
        {
            if (i + 1 >= args.size())
            {
                Err("参数 " + a + " 需要一个值");
                return 2;
            }
            const std::string value = args[++i];
            if (a == "--oem-input") oemInput = value;
            else if (a == "--fm-dir") fmDir = value;
            else if (a == "--fm") fmFiles.push_back(value);
            else if (a == "--arch") arch = value;
            else if (a == "--cabs") cabsDir = value;
            else if (a == "--out") outPath = value;
            else logPath = value;
            continue;
        }
        Err("未知参数：" + a);
        return 2;
    }
    if (oemInput.empty() || cabsDir.empty() || outPath.empty())
    {
        Err("用法：ocid_imagegen packagelist --oem-input <OEMInput.xml> --cabs <cab 目录> "
            "--out <PackageList.xml> [--arch arm64] [--fm-dir <目录>] [--log <文件>]");
        return 2;
    }
    std::string error;
    if (!logPath.empty())
    {
        LogOpen(Utf8ToWide(logPath), error);
    }

    PlanInput input;
    input.oemInputPath = oemInput;
    input.fmDir = fmDir;
    input.fmFiles = fmFiles;
    input.arch = arch;

    PlanResult plan;
    if (!BuildPlan(input, plan, error))
    {
        Err(error);
        LogClose();
        return 1;
    }
    OutLn("包集：" + std::to_string(plan.packages.size()) + " 个 cab（架构 " + arch + "）");

    std::string xml;
    xml += "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n";
    xml += "<UpdateOSOutput xmlns=\"http://schemas.microsoft.com/embedded/2004/10/ImageUpdate\">\r\n";
    xml += "  <Description>List of packages currently in this image or on this device</Description>\r\n";
    xml += "  <OverallResult>0</OverallResult>\r\n";
    xml += "  <UpdateState>Update Complete</UpdateState>\r\n";
    xml += "  <Packages>\r\n";

    std::size_t okCount = 0;
    std::size_t badCount = 0;
    for (const PackageRef& ref : plan.packages)
    {
        const std::wstring cabPath = CombinePath(Utf8ToWide(cabsDir), Utf8ToWide(ref.cab));
        std::string mum;
        if (!CabExtractText(cabPath, "update.mum", mum, error))
        {
            ++badCount;
            Err("读 update.mum 失败：" + ref.cab + " -> " + error);
            continue;
        }
        std::unique_ptr<XmlDoc> doc = XmlParseText(mum, error);
        if (!doc || doc->root == nullptr)
        {
            ++badCount;
            Err("解 update.mum 失败：" + ref.cab + " -> " + error);
            continue;
        }
        const XmlNode* root = doc->root;
        const XmlNode* idNode = Child(root, "assemblyIdentity");
        const XmlNode* pkgNode = Child(root, "package");
        // phoneInformation 在 <package>/<customInformation> 之下（不是 <assembly> 的直系子节点）
        const XmlNode* ciNode = Child(pkgNode, "customInformation");
        const XmlNode* phoneNode = ciNode != nullptr ? Child(ciNode, "phoneInformation") : nullptr;

        std::string name = AttrOr(idNode, "name", "");
        const std::string version = AttrOr(idNode, "version", "");
        std::string pkt = AttrOr(idNode, "publicKeyToken", "31bf3856ad364e35");
        std::string archText = AttrOr(idNode, "processorArchitecture", ref.arch.c_str());
        std::string language = AttrOr(idNode, "language", "neutral");
        const std::string buildType = AttrOr(idNode, "buildType", "release");

        // 小写化（PackageIdentity 里架构/语言都是小写，cab 文件名里架构是大写）
        for (char& c : archText) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
        for (char& c : pkt) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
        if (EqualsNoCase(language, "neutral")) language.clear();

        // Version：10.0.20279.1002 → 10 / 0 / 1002 / 0
        std::string major = "0", minor = "0", build = "0";
        {
            std::vector<std::string> parts;
            std::string cur;
            for (char c : version)
            {
                if (c == '.') { parts.push_back(cur); cur.clear(); }
                else cur.push_back(c);
            }
            if (!cur.empty()) parts.push_back(cur);
            if (parts.size() >= 1) major = parts[0];
            if (parts.size() >= 2) minor = parts[1];
            if (parts.size() >= 4) build = parts[3];
        }

        xml += "    <Package>\r\n";
        xml += "      <PackageType>Canonical</PackageType>\r\n";
        xml += "      <PackageFile>\\\\?\\";
        XmlEscapeTo(xml, WideToUtf8(cabPath));
        xml += "</PackageFile>\r\n";
        xml += "      <PackageIdentity>";
        XmlEscapeTo(xml, name + "~" + pkt + "~" + archText + "~" + language + "~" + version);
        xml += "</PackageIdentity>\r\n";
        xml += "      <Identity>\r\n        <Owner>";
        XmlEscapeTo(xml, AttrOr(phoneNode, "phoneOwner", "Microsoft"));
        xml += "</Owner>\r\n        <Component>";
        XmlEscapeTo(xml, AttrOr(phoneNode, "phoneComponent", ""));
        xml += "</Component>\r\n        <Version Major=\"" + major + "\" Minor=\"" + minor +
               "\" Build=\"" + build + "\" Qfe=\"0\" />\r\n      </Identity>\r\n";
        xml += "      <ReleaseType>";
        XmlEscapeTo(xml, AttrOr(phoneNode, "phoneRelease", "Production"));
        xml += "</ReleaseType>\r\n      <OwnerType>";
        XmlEscapeTo(xml, AttrOr(phoneNode, "phoneOwnerType", "Microsoft"));
        xml += "</OwnerType>\r\n      <BuildType>";
        xml += EqualsNoCase(buildType, "release") ? "Retail" : buildType;
        xml += "</BuildType>\r\n      <HostCpuType>";
        /* HostCpuType / GuestCpuType 是实测的：包内 processorArchitecture 决定
               arm64 → ARM64 / Invalid、arm64.arm → ARM64 / ARM、
               amd64 → AMD64 / Invalid、wow64 → AMD64 / X86（wow64 表示来宾是 x86）。 */
        std::string hostCpu;
        std::string guestCpu;
        {
            std::string first = archText;
            std::string second;
            const std::size_t dot = archText.find('.');
            if (dot != std::string::npos)
            {
                first = archText.substr(0, dot);
                second = archText.substr(dot + 1);
            }
            for (char& c : first) c = static_cast<char>(std::toupper(static_cast<unsigned char>(c)));
            for (char& c : second) c = static_cast<char>(std::toupper(static_cast<unsigned char>(c)));
            if (archText == "wow64")
            {
                hostCpu = "AMD64";
                guestCpu = "X86";
            }
            else
            {
                hostCpu = first;
                guestCpu = second.empty() ? "Invalid" : second;
            }
        }
        xml += hostCpu;
        xml += "</HostCpuType>\r\n      <GuestCpuType>";
        xml += guestCpu;
        xml += "</GuestCpuType>\r\n      <Partition>";
        XmlEscapeTo(xml, AttrOr(pkgNode, "targetPartition", ""));
        xml += "</Partition>\r\n      <Result>0</Result>\r\n      <CpuType>";
        xml += archText;
        xml += "</CpuType>\r\n    </Package>\r\n";
        ++okCount;
    }
    xml += "  </Packages>\r\n</UpdateOSOutput>\r\n";

    if (!WriteFileText(Utf8ToWide(outPath), xml, error))
    {
        Err(error);
        LogClose();
        return 1;
    }
    OutLn("已写 PackageList：" + outPath + "（条目 " + std::to_string(okCount) +
          "，失败 " + std::to_string(badCount) + "）");
    LogWrite(LogLevel::Info, "packagelist written: " + outPath + " entries=" + std::to_string(okCount));
    LogClose();
    return badCount == 0 ? 0 : 1;
}

// ── layout：设备布局（DeviceLayout v1/v2 + OEMDevicePlatform）──────────────
//
// 为什么要解析它：M1 建盘的全部输入都在这里 —— 每 store 一个虚拟盘的容量、
// 分区类型 GUID、文件系统、对齐、以及物理盘上 OSPool 分区的位置。
// 校验分两层：XSD（结构）用 ADK 自带的 DeviceLayout*.xsd；语义规则（尺寸/对齐）
// 是我们从参照产物与 DeviceLayoutValidation 的行为里反推出来的，放在 check。

unsigned long long ParseNum(const std::string& text)
{
    if (text.empty()) return 0;
    return std::strtoull(text.c_str(), nullptr, 0);
}

std::string NumText(unsigned long long value)
{
    return std::to_string(value);
}

std::string FormatBytes(unsigned long long bytes)
{
    const char* units[] = { "B", "KiB", "MiB", "GiB", "TiB" };
    double value = static_cast<double>(bytes);
    int unit = 0;
    while (value >= 1024.0 && unit < 4) { value /= 1024.0; ++unit; }
    char buffer[64] = { 0 };
    std::snprintf(buffer, sizeof(buffer) - 1, "%.1f %s", value, units[unit]);
    return buffer;
}

struct LayoutPartition
{
    std::string name, id, type, fileSystem, byteAlignment, clusterSize;
    unsigned long long totalSectors = 0;
    unsigned long long minFreeSectors = 0;
    // 10.0.19602 起的布局用字节表达尺寸（SizeInBytes/TotalBytes），与旧的扇区表达并存
    unsigned long long totalBytes = 0;
    unsigned long long minFreeBytes = 0;
    bool hasTotalBytes = false;
    bool hasTotal = false;
    bool useAllSpace = false, prepareFveMetadata = false, bootable = false;
    bool requiredToFlash = false, attachDriveLetter = false, singleSectorAlignment = false;
};

struct LayoutStore
{
    std::string id, storeType, devicePath;
    unsigned long long sizeInSectors = 0;
    unsigned long long sizeInBytes = 0;
    bool hasSize = false;
    bool hasSizeBytes = false;
    std::vector<LayoutPartition> partitions;
};

struct LayoutModel
{
    bool v2 = false, msSplit = false;
    std::string rootName, namespaceUri;
    unsigned long long sectorSize = 512, chunkSize = 0, defaultAlignment = 0, stateSeparation = 0;
    bool hasSectorSize = false, hasAlignment = false;
    std::string alignmentText;   // 原始写法（0x200000），输出时照抄
    std::string mergeVersionTag;
    bool updateByOSTag = false;
    bool byteUnits = false;
    // MergeVersionTag 只是合并协议标记，最终布局产物里不出现（v2 schema 无此元素）
    bool emitMergeTag = false;
    std::vector<std::string> oemDevicePaths;  // OEM 半份的 DevicePath（仅信息）
    std::vector<LayoutStore> pools;
    std::vector<LayoutStore> stores;
    unsigned long long minSectorCount = 0;
    bool hasMinSectorCount = false;
    std::vector<std::string> platformIds;
    std::string biosMode;
};

std::string NodeText(const XmlNode* node, const char* name)
{
    const XmlNode* child = Child(node, name);
    if (child == nullptr) return std::string();
    return Trim(Text(child));
}

bool ReadFlag(const XmlNode* node, const char* name)
{
    const std::string value = NodeText(node, name);
    return EqualsNoCase(value, "true");
}

LayoutPartition ParsePartition(const XmlNode* node)
{
    LayoutPartition out;
    out.name = NodeText(node, "Name");
    out.id = NodeText(node, "Id");
    out.type = NodeText(node, "Type");
    out.fileSystem = NodeText(node, "FileSystem");
    out.byteAlignment = NodeText(node, "ByteAlignment");
    out.clusterSize = NodeText(node, "ClusterSize");
    const std::string total = NodeText(node, "TotalSectors");
    out.hasTotal = !total.empty();
    out.totalSectors = ParseNum(total);
    out.minFreeSectors = ParseNum(NodeText(node, "MinFreeSectors"));
    const std::string totalBytes = NodeText(node, "TotalBytes");
    out.hasTotalBytes = !totalBytes.empty();
    out.totalBytes = ParseNum(totalBytes);
    out.minFreeBytes = ParseNum(NodeText(node, "MinFreeBytes"));
    out.useAllSpace = ReadFlag(node, "UseAllSpace");
    out.prepareFveMetadata = ReadFlag(node, "PrepareFveMetadata");
    out.bootable = ReadFlag(node, "Bootable");
    out.requiredToFlash = ReadFlag(node, "RequiredToFlash");
    out.attachDriveLetter = ReadFlag(node, "AttachDriveLetter");
    out.singleSectorAlignment = ReadFlag(node, "SingleSectorAlignment");
    return out;
}

void ParsePartitions(const XmlNode* holder, LayoutStore& store)
{
    const XmlNode* list = Child(holder, "Partitions");
    if (list == nullptr) return;
    for (const XmlNode* node = Child(list, "Partition"); node != nullptr; node = Next(node, "Partition"))
    {
        store.partitions.push_back(ParsePartition(node));
    }
}

LayoutStore ParseStore(const XmlNode* node)
{
    LayoutStore store;
    store.id = NodeText(node, "Id");
    store.storeType = NodeText(node, "StoreType");
    store.devicePath = NodeText(node, "DevicePath");
    const std::string size = NodeText(node, "SizeInSectors");
    store.hasSize = !size.empty();
    store.sizeInSectors = ParseNum(size);
    const std::string sizeBytes = NodeText(node, "SizeInBytes");
    store.hasSizeBytes = !sizeBytes.empty();
    store.sizeInBytes = ParseNum(sizeBytes);
    ParsePartitions(node, store);
    return store;
}

bool LoadLayout(const std::string& path, LayoutModel& model, std::string& error)
{
    std::unique_ptr<XmlDoc> doc;
    doc = XmlParseFile(Utf8ToWide(path), error);
    if (!doc) return false;
    const XmlNode* root = doc->root;
    if (root == nullptr) { error = "XML 根节点为空"; return false; }
    model.rootName = root->local;
    // Common.cpp 的 DOM 不保留命名空间 URI（只存局部名），所以用"结构特征"判 v2：
    // v2 一定有 <StoragePools>（MSDeviceLayout 是 v2 的 MS 侧半成品）。
    const bool hasStoragePools = Child(root, "StoragePools") != nullptr;
    model.v2 = EqualsNoCase(root->local, "MSDeviceLayout") || hasStoragePools;
    model.msSplit = EqualsNoCase(root->local, "MSDeviceLayout");
    model.sectorSize = ParseNum(NodeText(root, "SectorSize"));
    model.hasSectorSize = !NodeText(root, "SectorSize").empty();
    model.chunkSize = ParseNum(NodeText(root, "ChunkSize"));
    model.defaultAlignment = ParseNum(NodeText(root, "DefaultPartitionByteAlignment"));
    model.alignmentText = NodeText(root, "DefaultPartitionByteAlignment");
    model.hasAlignment = !model.alignmentText.empty();
    model.stateSeparation = ParseNum(NodeText(root, "StateSeparationLevel"));
    model.mergeVersionTag = NodeText(root, "MergeVersionTag");
    model.updateByOSTag = ReadFlag(root, "UpdateByOSTag");


    if (EqualsNoCase(root->local, "OEMDevicePlatform"))
    {
        const std::string minSectors = NodeText(root, "MinSectorCount");
        model.hasMinSectorCount = !minSectors.empty();
        model.minSectorCount = ParseNum(minSectors);
        model.biosMode = NodeText(root, "BIOSMode");
        if (const XmlNode* ids = Child(root, "DevicePlatformIDs"))
        {
            for (const XmlNode* id = Child(ids, "ID"); id != nullptr; id = Next(id, "ID"))
            {
                model.platformIds.push_back(Trim(Text(id)));
            }
        }
        return true;
    }

    // v1：<Partitions> 直接挂在根上；v2：<StoragePools><StoragePool><Stores> + 顶层 <Stores>
    if (const XmlNode* pools = Child(root, "StoragePools"))
    {
        for (const XmlNode* pool = Child(pools, "StoragePool"); pool != nullptr; pool = Next(pool, "StoragePool"))
        {
            const XmlNode* stores = Child(pool, "Stores");
            if (stores == nullptr) continue;
            for (const XmlNode* store = Child(stores, "Store"); store != nullptr; store = Next(store, "Store"))
            {
                model.pools.push_back(ParseStore(store));
            }
        }
    }
    if (const XmlNode* stores = Child(root, "Stores"))
    {
        for (const XmlNode* store = Child(stores, "Store"); store != nullptr; store = Next(store, "Store"))
        {
            model.stores.push_back(ParseStore(store));
        }
    }
    for (const LayoutStore& s : model.pools)
    {
        if (s.hasSizeBytes) model.byteUnits = true;
        for (const LayoutPartition& p : s.partitions) if (p.hasTotalBytes) model.byteUnits = true;
    }
    for (const LayoutStore& s : model.stores)
    {
        if (s.hasSizeBytes) model.byteUnits = true;
        for (const LayoutPartition& p : s.partitions) if (p.hasTotalBytes) model.byteUnits = true;
    }
    if (const XmlNode* flat = Child(root, "Partitions"))
    {
        LayoutStore store;
        store.storeType = "(v1 平铺分区)";
        ParsePartitions(root, store);
        model.stores.push_back(store);
        (void)flat;
    }
    return true;
}

void PrintPartition(const LayoutPartition& p, unsigned long long sectorSize, const char* indent)
{
    std::string line = std::string(indent) + p.name;
    if (!p.type.empty()) line += "  type=" + p.type;
    if (!p.id.empty()) line += "  id=" + p.id;
    if (!p.fileSystem.empty()) line += "  fs=" + p.fileSystem;
    if (p.hasTotalBytes)
    {
        line += "  bytes=" + NumText(p.totalBytes) + " (" + FormatBytes(p.totalBytes) + ")";
        if (sectorSize > 0) line += "  sectors=" + NumText(p.totalBytes / sectorSize);
    }
    else if (p.hasTotal)
    {
        line += "  sectors=" + NumText(p.totalSectors);
        if (sectorSize > 0) line += " (" + FormatBytes(p.totalSectors * sectorSize) + ")";
    }
    if (p.minFreeSectors > 0)
    {
        line += "  minFree=" + NumText(p.minFreeSectors);
        if (sectorSize > 0) line += " (" + FormatBytes(p.minFreeSectors * sectorSize) + ")";
    }
    if (p.useAllSpace) line += "  UseAllSpace";
    if (p.prepareFveMetadata) line += "  Fve";
    if (p.bootable) line += "  Bootable";
    if (p.requiredToFlash) line += "  RequiredToFlash";
    if (p.attachDriveLetter) line += "  AttachDriveLetter";
    if (!p.byteAlignment.empty()) line += "  align=" + p.byteAlignment;
    if (!p.clusterSize.empty()) line += "  cluster=" + p.clusterSize;
    OutLn(line);
}

void PrintStore(const LayoutStore& store, unsigned long long sectorSize, const char* tag)
{
    std::string line = std::string("  ") + tag + " " + store.storeType;
    if (!store.id.empty()) line += "  id=" + store.id;
    if (store.hasSizeBytes)
    {
        line += "  bytes=" + NumText(store.sizeInBytes) + " (" + FormatBytes(store.sizeInBytes) + ")";
        if (sectorSize > 0) line += "  sectors=" + NumText(store.sizeInBytes / sectorSize);
    }
    else if (store.hasSize)
    {
        line += "  sectors=" + NumText(store.sizeInSectors);
        if (sectorSize > 0) line += " (" + FormatBytes(store.sizeInSectors * sectorSize) + ")";
    }
    if (!store.devicePath.empty()) line += "  device=" + store.devicePath;
    OutLn(line);
    for (const LayoutPartition& p : store.partitions) PrintPartition(p, sectorSize, "      ");
}


// ── 布局合并：MS 半份 + OEM 半份 ──────────────────────────────────────────
//
// 依据（2026-10-21 从 20274 Surface / 19602 Monza / 19541 WSK 四份样本实测）：
//   MS 半份  = 布局包 payload 的 DeviceLayout.xml（完整）或 DeviceLayoutMS.xml
//              （只有 <StoragePools>，没有物理盘 <Stores>、没有 SectorSize）
//   OEM 半份 = *-DeviceLayout-Customization-Package payload 的 DeviceLayoutOEM.xml
//              （物理盘的 Id/StoreType/DevicePath/SizeInBytes + SectorSize + UpdateByOSTag）
// imageapp 把它们合并成一份完整布局后再造盘；本函数复现这个合并。
std::string Esc(const std::string& text)
{
    std::string out;
    XmlEscapeTo(out, text);
    return out;
}

void FillUnits(LayoutModel& m)
{
    if (m.sectorSize == 0) return;
    for (std::vector<LayoutStore>* list : {&m.pools, &m.stores})
    {
        for (LayoutStore& s : *list)
        {
            if (s.hasSizeBytes && !s.hasSize) { s.sizeInSectors = s.sizeInBytes / m.sectorSize; s.hasSize = true; }
            if (s.hasSize && !s.hasSizeBytes) { s.sizeInBytes = s.sizeInSectors * m.sectorSize; s.hasSizeBytes = true; }
            for (LayoutPartition& p : s.partitions)
            {
                if (p.hasTotalBytes && !p.hasTotal) { p.totalSectors = p.totalBytes / m.sectorSize; p.hasTotal = true; }
                if (p.hasTotal && !p.hasTotalBytes) { p.totalBytes = p.totalSectors * m.sectorSize; p.hasTotalBytes = true; }
            }
        }
    }
}

bool MergeLayouts(const LayoutModel& ms, const LayoutModel& oem, LayoutModel& out, std::string& error,
                  bool dropOpp = true)
{
    out.oemDevicePaths.clear();
    out = ms;
    out.rootName = "DeviceLayout";
    out.msSplit = false;
    out.v2 = true;
    if (oem.hasSectorSize) { out.sectorSize = oem.sectorSize; out.hasSectorSize = true; }
    if (!out.hasSectorSize)
    {
        error = "布局缺少 SectorSize：MS 半份没给，OEM 半份必须提供";
        return false;
    }
    if (!ms.mergeVersionTag.empty() && !oem.mergeVersionTag.empty() &&
        !EqualsNoCase(ms.mergeVersionTag, oem.mergeVersionTag))
    {
        error = "MergeVersionTag 不一致：MS=" + ms.mergeVersionTag + "，OEM=" + oem.mergeVersionTag;
        return false;
    }
    if (out.mergeVersionTag.empty()) out.mergeVersionTag = oem.mergeVersionTag;
    if (oem.updateByOSTag) out.updateByOSTag = true;

    std::vector<std::string> oemPaths;
    // 物理盘：OEM 侧按 Id 覆盖尺寸；MS 侧没有的盘整条追加。
    for (const LayoutStore& oemStore : oem.stores)
    {
        if (oemStore.storeType.empty()) continue;   // OEM 文件里只有 <Stores><Store>
        LayoutStore* hit = nullptr;
        for (LayoutStore& s : out.stores)
        {
            if (!s.id.empty() && EqualsNoCase(s.id, oemStore.id)) { hit = &s; break; }
        }
        if (hit == nullptr)
        {
            out.stores.push_back(oemStore);
            continue;
        }
        // OEM 半份的 DevicePath 只用于 UFP 刷机（见 19602 OEM 布局里的注释：
        // "ignored by DISM, but must match the EFI path for the main disk for UFP"），
        // 不写进合并后的布局——20274 参照产物里 Default 的 DevicePath 仍是模板的 VenHw(...)。
        if (!oemStore.devicePath.empty()) out.oemDevicePaths.push_back(oemStore.storeType + " -> " + oemStore.devicePath);
        if (hit->devicePath.empty() && !oemStore.devicePath.empty()) hit->devicePath = oemStore.devicePath;
        if (oemStore.hasSizeBytes) { hit->hasSizeBytes = true; hit->sizeInBytes = oemStore.sizeInBytes; }
        if (oemStore.hasSize) { hit->hasSize = true; hit->sizeInSectors = oemStore.sizeInSectors; }
    }
    // imageapp 的参照产物里物理盘的 OPP 分区不见了（20274 Surface / 20279 QEMU 两份都对得上
    // 其它所有 store/分区/尺寸/标志），所以合并时按同样规则去掉；留 --keep-opp 可保留。
    if (dropOpp)
    {
        for (LayoutStore& s : out.stores)
        {
            s.partitions.erase(std::remove_if(s.partitions.begin(), s.partitions.end(),
                [](const LayoutPartition& p) { return EqualsNoCase(p.name, "OPP"); }), s.partitions.end());
        }
    }
    FillUnits(out);
    return true;
}

std::string XmlIndent(int level) { return std::string(static_cast<std::size_t>(level) * 4, ' '); }

void AppendPartitionXml(std::string& xml, const LayoutPartition& p, int level, bool byteUnits)
{
    xml += XmlIndent(level) + "<Partition>\r\n";
    const std::string i1 = XmlIndent(level + 1);
    xml += i1 + "<Name>" + Esc(p.name) + "</Name>\r\n";
    if (!p.id.empty()) xml += i1 + "<Id>" + Esc(p.id) + "</Id>\r\n";
    if (!p.type.empty()) xml += i1 + "<Type>" + Esc(p.type) + "</Type>\r\n";
    if (byteUnits && p.hasTotalBytes) xml += i1 + "<TotalBytes>" + NumText(p.totalBytes) + "</TotalBytes>\r\n";
    else if (p.hasTotal) xml += i1 + "<TotalSectors>" + NumText(p.totalSectors) + "</TotalSectors>\r\n";
    if (!p.fileSystem.empty()) xml += i1 + "<FileSystem>" + Esc(p.fileSystem) + "</FileSystem>\r\n";
    if (!p.byteAlignment.empty()) xml += i1 + "<ByteAlignment>" + Esc(p.byteAlignment) + "</ByteAlignment>\r\n";
    if (!p.clusterSize.empty()) xml += i1 + "<ClusterSize>" + Esc(p.clusterSize) + "</ClusterSize>\r\n";
    if (p.useAllSpace) xml += i1 + "<UseAllSpace>true</UseAllSpace>\r\n";
    if (p.prepareFveMetadata) xml += i1 + "<PrepareFveMetadata>true</PrepareFveMetadata>\r\n";
    if (p.bootable) xml += i1 + "<Bootable>true</Bootable>\r\n";
    if (p.requiredToFlash) xml += i1 + "<RequiredToFlash>true</RequiredToFlash>\r\n";
    if (p.attachDriveLetter) xml += i1 + "<AttachDriveLetter>true</AttachDriveLetter>\r\n";
    if (p.singleSectorAlignment) xml += i1 + "<SingleSectorAlignment>true</SingleSectorAlignment>\r\n";
    xml += XmlIndent(level) + "</Partition>\r\n";
}

void AppendStoreXml(std::string& xml, const LayoutStore& s, int level, bool byteUnits)
{
    xml += XmlIndent(level) + "<Store>\r\n";
    const std::string i1 = XmlIndent(level + 1);
    if (!s.id.empty()) xml += i1 + "<Id>" + Esc(s.id) + "</Id>\r\n";
    if (!s.storeType.empty()) xml += i1 + "<StoreType>" + Esc(s.storeType) + "</StoreType>\r\n";
    if (!s.devicePath.empty()) xml += i1 + "<DevicePath>" + Esc(s.devicePath) + "</DevicePath>\r\n";
    if (byteUnits && s.hasSizeBytes) xml += i1 + "<SizeInBytes>" + NumText(s.sizeInBytes) + "</SizeInBytes>\r\n";
    else if (s.hasSize) xml += i1 + "<SizeInSectors>" + NumText(s.sizeInSectors) + "</SizeInSectors>\r\n";
    if (!s.partitions.empty())
    {
        xml += i1 + "<Partitions>\r\n";
        for (const LayoutPartition& p : s.partitions) AppendPartitionXml(xml, p, level + 2, byteUnits);
        xml += i1 + "</Partitions>\r\n";
    }
    xml += XmlIndent(level) + "</Store>\r\n";
}

bool WriteLayoutXml(const LayoutModel& m, const std::wstring& path, std::string& error)
{
    // 输出统一用扇区表达：v2 的 DeviceLayoutv2.xsd 只认 SizeInSectors/TotalSectors，
    // 而 19602 起的 DeviceLayoutMS.xml 用 SizeInBytes/TotalBytes（那是构建期工具的中间表达）。
    // 读的时候两种都收，写的时候一律换成扇区（SectorSize 由 MergeLayouts 保证非 0）。
    const bool byteUnits = false;
    (void)m.byteUnits;
    std::string xml;
    xml += "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n";
    xml += "<DeviceLayout xmlns=\"http://schemas.microsoft.com/embedded/2004/10/ImageUpdate/v2\">\r\n";
    if (!m.pools.empty())
    {
        xml += "    <StoragePools>\r\n";
        xml += "        <StoragePool>\r\n            <Name>OSPool</Name>\r\n            <Stores>\r\n";
        for (const LayoutStore& s : m.pools) AppendStoreXml(xml, s, 4, byteUnits);
        xml += "            </Stores>\r\n        </StoragePool>\r\n    </StoragePools>\r\n";
    }
    if (!m.stores.empty())
    {
        xml += "    <Stores>\r\n";
        for (const LayoutStore& s : m.stores) AppendStoreXml(xml, s, 2, byteUnits);
        xml += "    </Stores>\r\n";
    }
    if (m.hasSectorSize) xml += "    <SectorSize>" + NumText(m.sectorSize) + "</SectorSize>\r\n";
    if (m.chunkSize > 0) xml += "    <ChunkSize>" + NumText(m.chunkSize) + "</ChunkSize>\r\n";
    if (m.hasAlignment) xml += "    <DefaultPartitionByteAlignment>" +
        (m.alignmentText.empty() ? NumText(m.defaultAlignment) : m.alignmentText) + "</DefaultPartitionByteAlignment>\r\n";
    xml += "    <StateSeparationLevel>" + NumText(m.stateSeparation) + "</StateSeparationLevel>\r\n";
    if (m.emitMergeTag && !m.mergeVersionTag.empty()) xml += "    <MergeVersionTag>" + Esc(m.mergeVersionTag) + "</MergeVersionTag>\r\n";
    if (m.updateByOSTag) xml += "    <UpdateByOSTag>true</UpdateByOSTag>\r\n";
    xml += "</DeviceLayout>\r\n";
    return WriteFileText(path, xml, error);
}

int CmdLayoutMerge(const std::vector<std::string>& args)
{
    std::string msPath, oemPath, outPath;
    bool quiet = false;
    bool keepMergeTag = false;
    bool dropOpp = true;
    for (std::size_t i = 3; i < args.size(); ++i)
    {
        const std::string& a = args[i];
        if (a == "--help" || a == "-h") { PrintUsage(); return 0; }
        else if (a == "--oem" && i + 1 < args.size()) oemPath = args[++i];
        else if (a == "--out" && i + 1 < args.size()) outPath = args[++i];
        else if (a == "--quiet") quiet = true;
        else if (a == "--merge-tag") keepMergeTag = true;
        else if (a == "--keep-opp") dropOpp = false;
        else if (a.size() > 1 && a[0] == '-') { Err("layout merge: 未知选项 " + a); return 2; }
        else if (msPath.empty()) msPath = a;
        else { Err("layout merge: 只能给一个 MS 侧布局文件（多余的参数：" + a + "）"); return 2; }
    }
    if (msPath.empty()) { Err("用法：ocid_imagegen layout merge <MS 侧布局> [--oem <OEM 侧布局>] [--out <文件>]"); return 2; }
    std::string error;
    LayoutModel ms;
    if (!LoadLayout(msPath, ms, error)) { Err("读取 " + msPath + " 失败：" + error); return 1; }
    LayoutModel oem;
    const bool hasOem = !oemPath.empty();
    if (hasOem && !LoadLayout(oemPath, oem, error)) { Err("读取 " + oemPath + " 失败：" + error); return 1; }

    LayoutModel merged;
    if (!MergeLayouts(ms, oem, merged, error, dropOpp)) { Err("合并失败：" + error); return 1; }
    merged.emitMergeTag = keepMergeTag;
    if (!hasOem) LogWrite(LogLevel::Warn, "只有 MS 半份：物理盘 DevicePath/SizeInBytes 与 SectorSize 都没补（造盘会缺信息）");

    if (!quiet)
    {
        OutLn("MS 半份：" + msPath + "（" + std::string(ms.msSplit ? "拆分半份 MSDeviceLayout" : "完整布局") + "）");
        if (hasOem) OutLn("OEM 半份：" + oemPath);
        for (const std::string& p : merged.oemDevicePaths) OutLn("OEM 设备路径（仅刷机用，不写入布局）：" + p);
        if (dropOpp) OutLn("合并规则：已去掉物理盘 OPP 分区（imageapp 参照行为；--keep-opp 可保留）");
        OutLn("合并结果：扇区大小 " + NumText(merged.sectorSize) + "，存储池 store " + NumText(merged.pools.size()) +
              " 个，物理盘 " + NumText(merged.stores.size()) + " 块，MergeVersionTag=" +
              (merged.mergeVersionTag.empty() ? std::string("(无)") : merged.mergeVersionTag));
        for (const LayoutStore& s : merged.stores) PrintStore(s, merged.sectorSize, "物理盘");
        for (const LayoutStore& s : merged.pools) PrintStore(s, merged.sectorSize, "池 store");
    }
    if (!outPath.empty())
    {
        const std::wstring wide = Utf8ToWide(outPath);
        if (!WriteLayoutXml(merged, wide, error)) { Err("写 " + outPath + " 失败：" + error); return 1; }
        if (!quiet) OutLn("已写出：" + outPath);
    }
    return 0;
}

// ── 布局计划：把布局翻译成"要建什么"的表 ──────────────────────────────────
int CmdLayoutPlan(const std::vector<std::string>& args)
{
    std::string path;
    for (std::size_t i = 3; i < args.size(); ++i)
    {
        const std::string& a = args[i];
        if (a == "--help" || a == "-h") { PrintUsage(); return 0; }
        else if (a.size() > 1 && a[0] == '-') { Err("layout plan: 未知选项 " + a); return 2; }
        else if (path.empty()) path = a;
        else { Err("layout plan: 只能给一个布局文件"); return 2; }
    }
    if (path.empty()) { Err("用法：ocid_imagegen layout plan <布局文件>"); return 2; }
    std::string error;
    LayoutModel m;
    if (!LoadLayout(path, m, error)) { Err("读取 " + path + " 失败：" + error); return 1; }
    OutLn("源文件：" + path);
    OutLn("根元素：" + m.rootName + (m.msSplit ? "（拆分半份，缺物理盘/SectorSize）" : ""));
    OutLn("扇区大小：" + (m.hasSectorSize ? NumText(m.sectorSize) : std::string("(未给，需 OEM 半份补)")));
    OutLn("ChunkSize：" + NumText(m.chunkSize) + "  默认字节对齐：" + (m.hasAlignment ? NumText(m.defaultAlignment) : std::string("(未给)")));
    OutLn("");
    OutLn("物理盘（" + NumText(m.stores.size()) + " 块）：");
    for (const LayoutStore& s : m.stores) PrintStore(s, m.sectorSize, "磁盘");
    OutLn("");
    OutLn("存储池 store（" + NumText(m.pools.size()) + " 个）：");
    unsigned long long sum = 0;
    for (const LayoutStore& s : m.pools)
    {
        PrintStore(s, m.sectorSize, "store");
        const unsigned long long bytes = s.hasSizeBytes ? s.sizeInBytes : s.sizeInSectors * m.sectorSize;
        if (s.storeType.rfind("VIRT_", 0) != 0) sum += bytes;
    }
    OutLn("");
    OutLn("数据盘合计（不含 VIRT_）：" + FormatBytes(sum));
    return 0;
}
int CmdLayout(const std::vector<std::string>& args)
{
    std::string mode = args.size() > 2 ? args[2] : std::string();
    if (mode.empty() || mode == "--help" || mode == "-h")
    {
        PrintUsage();
        return mode.empty() ? 2 : 0;
    }
    if (mode == "merge") return CmdLayoutMerge(args);
    if (mode == "plan") return CmdLayoutPlan(args);
    std::string path, schemaDir;
    unsigned long long diskSectors = 0, wantMinSectors = 0;
    bool hasDiskSectors = false, hasWantMin = false;
    for (size_t i = 3; i < args.size(); ++i)
    {
        const std::string& a = args[i];
        auto next = [&](std::string& target) -> bool
        {
            if (i + 1 >= args.size()) return false;
            target = args[++i];
            return true;
        };
        if (a == "--schema-dir") { if (!next(schemaDir)) return 2; }
        else if (a == "--disk-sectors") { std::string v; if (!next(v)) return 2; diskSectors = ParseNum(v); hasDiskSectors = true; }
        else if (a == "--min-sector-count") { std::string v; if (!next(v)) return 2; wantMinSectors = ParseNum(v); hasWantMin = true; }
        else if (a == "--help" || a == "-h") { PrintUsage(); return 0; }
        else if (!a.empty() && a[0] == '-') { Err("未知选项：" + a); return 2; }
        else if (path.empty()) path = a;
        else { Err("多余的参数：" + a); return 2; }
    }
    if (path.empty())
    {
        Err("用法：ocid_imagegen layout show|validate|check <文件> [选项]");
        return 2;
    }

    std::string error;
    LayoutModel model;
    if (!LoadLayout(path, model, error))
    {
        Err(error);
        return 1;
    }

    if (mode == "show")
    {
        OutLn("文件：" + path);
        OutLn("根元素：" + model.rootName + "（v2=" + (model.v2 ? "true" : "false") +
              (model.msSplit ? "，MS 侧半成品" : "") + "）");
        if (model.hasSectorSize) OutLn("SectorSize：" + NumText(model.sectorSize));
        if (model.chunkSize > 0) OutLn("ChunkSize：" + NumText(model.chunkSize) + " KiB");
        if (model.hasAlignment) OutLn("DefaultPartitionByteAlignment：0x" + [&]() {
            char buffer[32] = { 0 };
            std::snprintf(buffer, sizeof(buffer) - 1, "%llx", model.defaultAlignment);
            return std::string(buffer);
        }() + " (" + FormatBytes(model.defaultAlignment) + ")");
        OutLn("StateSeparationLevel：" + NumText(model.stateSeparation));
        if (!model.platformIds.empty() || model.hasMinSectorCount)
        {
            if (model.hasMinSectorCount)
                OutLn("MinSectorCount：" + NumText(model.minSectorCount) + " (" +
                      FormatBytes(model.minSectorCount * (model.hasSectorSize ? model.sectorSize : 512)) + ")");
            std::string ids;
            for (const std::string& id : model.platformIds) { if (!ids.empty()) ids += " | "; ids += id; }
            OutLn("DevicePlatformIDs：" + ids);
            if (!model.biosMode.empty()) OutLn("BIOSMode：" + model.biosMode);
        }
        if (!model.pools.empty())
        {
            OutLn("");
            OutLn("存储池 store：" + std::to_string(model.pools.size()) + " 个");
            unsigned long long total = 0;
            for (const LayoutStore& store : model.pools)
            {
                PrintStore(store, model.sectorSize, "store");
                if (store.hasSize) total += store.sizeInSectors * model.sectorSize;
            }
            OutLn("  池合计（薄置备上限）：" + FormatBytes(total));
        }
        if (!model.stores.empty())
        {
            OutLn("");
            OutLn("物理/平铺分区：");
            for (const LayoutStore& store : model.stores) PrintStore(store, model.sectorSize, "store");
        }
        return 0;
    }

    if (mode == "validate")
    {
        std::string schemaName;
        if (EqualsNoCase(model.rootName, "OEMDevicePlatform")) schemaName = "OEMDevicePlatform.xsd";
        else if (EqualsNoCase(model.rootName, "MSDeviceLayout")) schemaName = "DeviceLayoutv2MS.xsd";
        else if (model.v2) schemaName = "DeviceLayoutv2.xsd";
        else schemaName = "DeviceLayout.xsd";
        const std::wstring schemaPath = FindSchema(schemaDir, schemaName);
        if (schemaPath.empty())
        {
            Err("找不到 schema：" + schemaName + "（用 --schema-dir 指定，或先跑 build-imaging.ps1 复制 schemas）");
            return 1;
        }
        std::string validateError;
        if (!ValidateXmlAgainstSchema(Utf8ToWide(path), { schemaPath }, validateError))
        {
            Err("校验失败：" + validateError);
            return 1;
        }
        OutLn("校验通过：" + schemaName);
        return 0;
    }

    if (mode != "check")
    {
        Err("未知 layout 子命令：" + mode);
        return 2;
    }

    // check：语义规则（XSD 之外的尺寸/一致性约束）
    int failed = 0, warned = 0;
    auto pass = [](const std::string& text) { OutLn("[通过] " + text); };
    auto warn = [&](const std::string& text) { OutLn("[提示] " + text); ++warned; };
    auto fail = [&](const std::string& text) { OutLn("[失败] " + text); ++failed; };

    if (EqualsNoCase(model.rootName, "OEMDevicePlatform"))
    {
        if (model.hasMinSectorCount && model.minSectorCount > 0)
            pass("MinSectorCount = " + NumText(model.minSectorCount) + "（" +
                 FormatBytes(model.minSectorCount * 512) + " @512B）");
        else
            fail("缺 MinSectorCount（imageapp 用它判断目标盘是否够大）");
        if (model.platformIds.empty()) fail("DevicePlatformIDs 为空");
        else pass("DevicePlatformIDs = " + std::to_string(model.platformIds.size()) + " 条");
        const unsigned long long floor = hasWantMin ? wantMinSectors
            : (hasDiskSectors ? diskSectors : 0);
        if (floor > 0)
        {
            if (model.minSectorCount <= floor) pass("MinSectorCount 不超过目标盘容量");
            else fail("MinSectorCount（" + NumText(model.minSectorCount) + "）大于目标盘（" + NumText(floor) + "）");
        }
        OutLn("");
        OutLn("结论：" + std::to_string(failed) + " 失败，" + std::to_string(warned) + " 提示");
        return failed == 0 ? 0 : 1;
    }

    if (!model.hasSectorSize)
    {
        // MS 侧半成品（DeviceLayoutMS.xml）故意不带 SectorSize：尺寸/扇区由 OEM 那一半补，
        // imageapp 校验前会先把两份合并。所以半成品缺扇区只提示，不判失败。
        if (model.msSplit) warn("MS 侧半成品布局没有 SectorSize（由 OEM 侧 DeviceLayout.xml 补，合并后再校验）");
        else fail("缺 SectorSize");
    }
    else if (model.sectorSize != 512 && model.sectorSize != 4096)
        fail("SectorSize 不是 512 / 4096（= " + NumText(model.sectorSize) + "）");
    else
        pass("SectorSize = " + NumText(model.sectorSize));

    if (model.v2)
    {
        if (model.pools.empty()) fail("v2 布局没有 <StoragePools>（池 store 是建盘的主体）");
        else
        {
            pass("池 store 数量 = " + std::to_string(model.pools.size()));
            // SizeInSectors 的分组：数据盘（EFIESP/MainOS/BSP/PreInstalled/OSData/Data/Reserve）
            // 在参照产物里全都等大；VIRT_EFIESP 是独立的 262144（128 MiB @512），必须分开看。
            std::set<unsigned long long> dataSizes;
            for (const LayoutStore& store : model.pools)
            {
                if (!store.hasSize)
                {
                    if (model.msSplit) continue;
                    fail("store " + store.storeType + " 缺 SizeInSectors");
                    continue;
                }
                if (store.storeType.find("VIRT_") == 0) continue;
                dataSizes.insert(store.sizeInSectors);
            }
            if (dataSizes.size() == 1)
                pass("数据盘 store 尺寸一致 = " + NumText(*dataSizes.begin()) + "（" +
                     FormatBytes(*dataSizes.begin() * model.sectorSize) + "）");
            else if (dataSizes.size() > 1)
            {
                std::string list;
                for (unsigned long long v : dataSizes) { if (!list.empty()) list += " / "; list += NumText(v); }
                warn("数据盘 store 尺寸不一致（" + list + "）；imageapp 按每 store 各自建盘");
            }
            for (const LayoutStore& store : model.pools)
            {
                if (store.partitions.size() != 1)
                    warn("store " + store.storeType + " 有 " + std::to_string(store.partitions.size()) +
                         " 个分区（参照产物里池 store 都只有 1 个）");
                for (const LayoutPartition& p : store.partitions)
                {
                    if (!p.hasTotal || !store.hasSize) continue;
                    if (p.useAllSpace)
                    {
                        // UseAllSpace = 分区会长到铺满 store；TotalSectors 只是起始/最小尺寸。
                        if (p.totalSectors > store.sizeInSectors)
                            fail("store " + store.storeType + " 的分区 " + p.name + " 起始尺寸（" +
                                 NumText(p.totalSectors) + "）大于 store（" + NumText(store.sizeInSectors) + "）");
                    }
                    else if (p.totalSectors != store.sizeInSectors)
                    {
                        warn("store " + store.storeType + " 的分区 " + p.name + " 固定尺寸（" +
                             NumText(p.totalSectors) + "）小于 store（" + NumText(store.sizeInSectors) +
                             "），剩余空间不会被使用");
                    }
                }
            }
            if (model.stores.empty()) fail("v2 布局没有顶层 <Stores>（物理盘承载 OSPool 分区）");
            else
            {
                // 物理盘可能有多个（参照 HoloLens 有 XBL / BACKUP_XBL / Protected_ReadOnly / Default），
                // 要按分区名找，不能只看第一块。
                const LayoutPartition* pool = nullptr;
                const LayoutPartition* bsEfiesp = nullptr;
                const LayoutPartition* opp = nullptr;
                std::string poolStore, bsStore, oppStore;
                for (const LayoutStore& store : model.stores)
                {
                    for (const LayoutPartition& p : store.partitions)
                    {
                        if (EqualsNoCase(p.name, "OSPool") && pool == nullptr) { pool = &p; poolStore = store.storeType; }
                        if (EqualsNoCase(p.name, "BS_EFIESP") && bsEfiesp == nullptr) { bsEfiesp = &p; bsStore = store.storeType; }
                        if (EqualsNoCase(p.name, "OPP") && opp == nullptr) { opp = &p; oppStore = store.storeType; }
                    }
                }
                if (pool == nullptr) warn("没有一个物理盘带 OSPool 分区（池空间落点不明；HoloLens 这类刷机布局没有它）");
                else pass("物理盘 " + poolStore + " 有 OSPool 分区");
                if (bsEfiesp == nullptr) warn("没有 BS_EFIESP 分区（引导用 ESP，ModernPC 参照产物里有）");
                // 规则来源：DeviceLayoutValidation 的 BS_EFIESP 下限按 4096 扇区单位写
                // greater_or_equal(66560)，小扇区布局在"缩放到 4K 单位"后才比。实测参照产物
                // 20274（512 布局，BS_EFIESP=65536）能通过，WSK 4096 模板（8192×4096=32MiB）
                // 也在库里流通 ⇒ 这条只能是"提示"，不能当失败判据。
                if (bsEfiesp != nullptr && bsEfiesp->hasTotal && model.sectorSize > 0)
                {
                    const unsigned long long bytes = bsEfiesp->totalSectors * model.sectorSize;
                    const unsigned long long need = 66560ull * 4096ull;
                    if (bytes < need)
                        warn("BS_EFIESP 只有 " + FormatBytes(bytes) + "，低于 66560×4096=" +
                             FormatBytes(need) + "（参照产物 20274 也是 65536 扇区，实际能过；这条按提示处理）");
                    else
                        pass("BS_EFIESP 尺寸 " + FormatBytes(bytes) + " ≥ " + FormatBytes(need));
                }
                if (opp == nullptr) pass("没有 OPP 分区（ModernPC/QEMU 参照产物都去掉了）");
                else warn("物理盘 " + oppStore + " 有 OPP 分区（参照产物 20274/20279 已去掉）");
            }
        }
    }
    else
    {
        pass("v1 布局（MBR 平铺分区）");
        if (model.stores.empty() || model.stores.front().partitions.empty()) fail("没有分区定义");
        bool mbrOk = true;
        for (const LayoutPartition& p : model.stores.empty() ? std::vector<LayoutPartition>() : model.stores.front().partitions)
        {
            if (p.type.rfind("0x", 0) != 0) mbrOk = false;
            if (!p.hasTotal && p.minFreeSectors == 0 && !p.useAllSpace)
                warn("分区 " + p.name + " 既无 TotalSectors 也无 MinFreeSectors/UseAllSpace");
        }
        if (mbrOk) pass("所有分区 Type 都是 MBR 0xNN");
        else warn("v1 布局里出现了非 0xNN 的分区类型（应使用 v2）");
    }

    OutLn("");
    OutLn("结论：" + std::to_string(failed) + " 失败，" + std::to_string(warned) + " 提示");
    return failed == 0 ? 0 : 1;
}

} // namespace

// 用 wmain 取宽字符命令行：
// 清单/镜像路径经常带中文（例如 ...\成品\ProcessedFiles），ANSI 的 main 会按当前代码页
// 折成本地编码，再转回 UTF-8 时字符就丢了。宽字符直接保真。
int wmain(int argc, wchar_t** wargv)
{
    ConsoleInit();
    std::vector<std::string> args;
    for (int i = 0; i < argc; ++i)
    {
        args.push_back(WideToUtf8(wargv[i]));
    }
    if (args.size() < 2)
    {
        PrintUsage();
        return 2;
    }
    const std::string command = args[1];
    if (command == "help" || command == "--help" || command == "-h" || command == "/?")
    {
        PrintUsage();
        return 0;
    }
    if (command == "version" || command == "--version")
    {
        OutLn("ocid_imagegen " + std::string(kVersion) + "（ocid_common " + CommonVersion() + "）");
        return 0;
    }
    if (command == "plan")
    {
        return CmdPlan(args);
    }
    if (command == "packagelist")
    {
        return CmdPackageList(args);
    }
    if (command == "schema")
    {
        return CmdSchema(args);
    }
    if (command == "cab")
    {
        return CmdCab(args);
    }
    if (command == "layout")
    {
        return CmdLayout(args);
    }
    Err("未知命令：" + command);
    PrintUsage();
    return 2;
}
