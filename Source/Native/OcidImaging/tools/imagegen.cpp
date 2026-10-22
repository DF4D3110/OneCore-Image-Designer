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
#include "ocid/imaging/Vhd.h"
#include "ocid/imaging/Pool.h"

#include <algorithm>
#include <cctype>
#include <cstdio>
#include <map>
#include <memory>
#include <cstring>
#include <filesystem>
#include <fstream>
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
    OutLn("  ocid_imagegen devicedb    --oem-input <OEMInput.xml> --cabs <cab 目录> --out <DeviceDB.xml>");
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

// ── devicedb：设备 CompDB（imageapp 的 <前缀>.DeviceDB.xml）─────────────────
//
// 语义来源：20279 QEMU 参照产物 + ADK 反编译
// （compdbcommon\Microsoft.WindowsPhone.CompDB\DeviceCompDB.cs 的 CreateDBFromPackages）。
// 整份 DeviceDB 都由【包内 update.mum】推出来，不需要 imageapp：
//   · 每个 FM 一条 BASE 特性：包 = 该 FM 被选中的 BasePackages（文档序）+ FM 自身包（最后）；
//   · 每个 *.FIP 包一条特性：FeatureID/FMID/Group 取自 update.mum 的 <declareCapability>，
//     包 = <featurePackage><package> 里"真正存在于镜像里"的那些（文档序），FIP 自身最后；
//   · 顶层 Packages = 特性包并集 + 驱动 .inf，按 ID 不区分大小写升序；
//   · EnumeratedPackages = 选中的包（去掉后续阶段会过滤掉的产品包）。

struct MumInfo
{
    std::string cab;
    std::string name;
    std::string version;
    std::string arch;
    std::string language;
    std::string pkt;
    std::string partition;
    std::string identity;
    std::string fmid;
    std::string group;
    std::string featureId;
    std::string satelliteLang;
    std::vector<std::string> declared;
    bool hasCapability = false;
};

static std::string LowerText(std::string text)
{
    for (char& c : text) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return text;
}

static std::string UpperText(std::string text)
{
    for (char& c : text) c = static_cast<char>(std::toupper(static_cast<unsigned char>(c)));
    return text;
}

// DeviceDB 的 <Partition> 口径与包内 mum 不同：mainos / updateos 用规范小写，
// EFIESP / BSP 保持大写（由参照 Falsh.DeviceDB.xml 反推）。
static std::string NormalizePartition(const std::string& text)
{
    if (EqualsNoCase(text, "mainos")) return "mainos";
    if (EqualsNoCase(text, "updateos")) return "updateos";
    if (EqualsNoCase(text, "efiesp")) return "EFIESP";
    if (EqualsNoCase(text, "bsp")) return "BSP";
    return text;
}

static std::string IdentityText(const std::string& name, const std::string& pkt,
                                const std::string& arch, const std::string& lang)
{
    // 语言段保留 mum 里的写法（参照里 en-US 与 zh-cn 并存）；架构与 pkt 用小写。
    return name + "~" + LowerText(pkt) + "~" + LowerText(arch) + "~" + lang + "~";
}

static std::string MumKey(const std::string& name, const std::string& arch, const std::string& lang)
{
    return LowerText(name) + "|" + LowerText(arch) + "|" + LowerText(lang);
}

static std::string NowText()
{
    SYSTEMTIME t;
    GetLocalTime(&t);
    char buffer[64] = {0};
    _snprintf_s(buffer, sizeof(buffer), _TRUNCATE, "%04d-%02d-%02dT%02d:%02d:%02d",
                static_cast<int>(t.wYear), static_cast<int>(t.wMonth), static_cast<int>(t.wDay),
                static_cast<int>(t.wHour), static_cast<int>(t.wMinute), static_cast<int>(t.wSecond));
    return std::string(buffer);
}

// 包内可能有多个 update.mum（FIP 包会把载荷包的 mum 一起带进来）。
// 真正描述"这个包自己"的那份带 <declareCapability>，优先取它。
static bool ReadUpdateMum(const std::wstring& cabPath, std::string& mum, std::string& error)
{
    CabInfo info;
    if (!CabList(cabPath, info, error)) return CabExtractText(cabPath, "update.mum", mum, error);
    std::vector<std::string> names;
    for (const CabEntry& entry : info.files)
    {
        const std::string lower = LowerText(entry.name);
        if (lower.size() >= 10 && lower.compare(lower.size() - 10, 10, "update.mum") == 0)
        {
            names.push_back(entry.name);
        }
    }
    if (names.empty()) return CabExtractText(cabPath, "update.mum", mum, error);
    std::string first;
    for (const std::string& name : names)
    {
        std::string text;
        std::string extractError;
        if (!CabExtractText(cabPath, name, text, extractError)) continue;
        if (first.empty()) first = text;
        if (text.find("declareCapability") != std::string::npos)
        {
            mum = text;
            return true;
        }
    }
    if (first.empty())
    {
        error = "包内没有可解的 update.mum：" + WideToUtf8(cabPath);
        return false;
    }
    mum = first;
    return true;
}

// 递归找后代（仅按局部名）——清单里 declareCapability 有时嵌在 customInformation 之下。
static const XmlNode* FindDeep(const XmlNode* node, const char* local)
{
    if (node == nullptr) return nullptr;
    for (const XmlNode* kid : ChildrenAny(node))
    {
        if (EqualsNoCase(kid->local, local)) return kid;
        const XmlNode* found = FindDeep(kid, local);
        if (found != nullptr) return found;
    }
    return nullptr;
}

int CmdDeviceDb(const std::vector<std::string>& args)
{
    std::string oemInput;
    std::string fmDir;
    std::string cabsDir;
    std::string outPath;
    std::string logPath;
    std::string arch = "arm64";
    std::string buildInfo = "fe_release_10x";
    std::string buildBranch = "fe_release_10x";
    std::string osVersion;
    std::vector<std::string> fmFiles;

    for (std::size_t i = 2; i < args.size(); ++i)
    {
        const std::string& a = args[i];
        if (a == "--oem-input" || a == "--fm-dir" || a == "--fm" || a == "--arch" ||
            a == "--cabs" || a == "--out" || a == "--log" || a == "--build-info" ||
            a == "--build-branch" || a == "--os-version")
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
            else if (a == "--log") logPath = value;
            else if (a == "--build-info") buildInfo = value;
            else if (a == "--build-branch") buildBranch = value;
            else osVersion = value;
            continue;
        }
        Err("未知参数：" + a);
        return 2;
    }
    if (oemInput.empty() || cabsDir.empty() || outPath.empty())
    {
        Err("用法：ocid_imagegen devicedb --oem-input <OEMInput.xml> --cabs <cab 目录> "
            "--out <DeviceDB.xml> [--arch arm64] [--fm-dir <目录>] [--build-info <文本>] "
            "[--build-branch <分支>] [--os-version <版本>] [--log <文件>]");
        return 2;
    }

    std::string error;
    if (!logPath.empty()) LogOpen(Utf8ToWide(logPath), error);

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

    OemInput oem;
    LoadOemInput(oemInput, oem, error);

    // 1) 逐包读 update.mum
    struct Pkg
    {
        PackageRef ref;
        MumInfo mum;
        bool haveMum = false;
    };
    std::vector<Pkg> packages;
    std::map<std::string, std::size_t> byKey;
    std::size_t badCount = 0;
    for (const PackageRef& ref : plan.packages)
    {
        Pkg p;
        p.ref = ref;
        const std::wstring cabPath = CombinePath(Utf8ToWide(cabsDir), Utf8ToWide(ref.cab));
        std::string mum;
        if (!ReadUpdateMum(cabPath, mum, error))
        {
            ++badCount;
            Err("读 update.mum 失败：" + ref.cab + " -> " + error);
            packages.push_back(p);
            continue;
        }
        std::unique_ptr<XmlDoc> doc = XmlParseText(mum, error);
        if (!doc || doc->root == nullptr)
        {
            ++badCount;
            Err("解 update.mum 失败：" + ref.cab + " -> " + error);
            packages.push_back(p);
            continue;
        }
        const XmlNode* root = doc->root;
        const XmlNode* idNode = Child(root, "assemblyIdentity");
        const XmlNode* pkgNode = Child(root, "package");

        MumInfo& m = p.mum;
        m.cab = ref.cab;
        m.name = AttrOr(idNode, "name", ref.id.c_str());
        m.version = AttrOr(idNode, "version", "");
        m.arch = AttrOr(idNode, "processorArchitecture", ref.arch.c_str());
        m.language = AttrOr(idNode, "language", "neutral");
        m.pkt = AttrOr(idNode, "publicKeyToken", "31bf3856ad364e35");
        m.partition = NormalizePartition(AttrOr(pkgNode, "targetPartition", ""));
        if (EqualsNoCase(m.language, "neutral")) m.language.clear();
        m.identity = IdentityText(m.name, m.pkt, m.arch, m.language);

        // 结构是 assembly/package/declareCapability/capability[@group,@fmid]/capabilityIdentity[@name]
        const XmlNode* dc = Child(pkgNode, "declareCapability");
        if (dc == nullptr)
        {
            const XmlNode* ci = Child(pkgNode, "customInformation");
            if (ci != nullptr) dc = Child(ci, "declareCapability");
        }
        if (dc == nullptr) dc = FindDeep(pkgNode, "declareCapability");
        if (dc != nullptr)
        {
            m.hasCapability = true;
            const XmlNode* cap = Child(dc, "capability");
            if (cap == nullptr) cap = dc;
            m.fmid = AttrOr(cap, "fmid", "");
            m.group = AttrOr(cap, "group", "");
            const XmlNode* capId = Child(cap, "capabilityIdentity");
            if (capId == nullptr) capId = Child(dc, "capabilityIdentity");
            m.featureId = AttrOr(capId, "Name", "");
            if (m.featureId.empty()) m.featureId = AttrOr(capId, "name", "");
            const XmlNode* fpk = Child(cap, "featurePackage");
            if (fpk == nullptr) fpk = Child(dc, "featurePackage");
            if (fpk != nullptr)
            {
                for (const XmlNode* node : ChildrenAny(fpk))
                {
                    if (!EqualsNoCase(node->local, "package")) continue;
                    const XmlNode* aid = Child(node, "assemblyIdentity");
                    const std::string dname = AttrOr(aid, "name", "");
                    if (dname.empty()) continue;
                    const std::string darch = AttrOr(aid, "processorArchitecture", m.arch.c_str());
                    std::string dlang = AttrOr(aid, "language", "neutral");
                    if (EqualsNoCase(dlang, "neutral")) dlang.clear();
                    const std::string dpkt = AttrOr(aid, "publicKeyToken", "31bf3856ad364e35");
                    m.declared.push_back(MumKey(dname, darch, dlang));
                    if (EqualsNoCase(dname, "Microsoft-WindowsCore-LanguagePack-Package") &&
                        !dlang.empty() && m.satelliteLang.empty())
                    {
                        m.satelliteLang = dlang;
                    }
                    (void)dpkt;
                }
            }
        }

        p.haveMum = true;
        byKey[MumKey(m.name, m.arch, m.language)] = packages.size();
        packages.push_back(p);
        if (osVersion.empty() && !m.version.empty() &&
            (ref.section == PackageSection::Base) && !ref.featureIdentifier) osVersion = m.version;
    }

    // 2) 特性：先 BASE（每 FM 一条），再 FIP（每包一条）

    struct Feature
    {
        std::string featureId;
        std::string fmid;
        std::string group;
        std::vector<std::string> ids;
        std::vector<std::string> keyForms;
        std::vector<std::string> partitions;
        std::string satelliteLang;
    };
    std::vector<Feature> features;

    std::vector<std::string> fmOrder;
    for (const Pkg& p : packages)
    {
        bool seen = false;
        for (const std::string& f : fmOrder) if (f == p.ref.fmId) { seen = true; break; }
        if (!seen) fmOrder.push_back(p.ref.fmId);
    }

    for (const std::string& fmid : fmOrder)
    {
        std::vector<std::string> fmFiles;
        Feature f;
        f.featureId = "BASE";
        f.fmid = fmid;
        f.group = "Microsoft";
        for (const Pkg& p : packages)
        {
            if (!p.haveMum || p.ref.fmId != fmid) continue;
            if (p.ref.section != PackageSection::Base) continue;
            if (p.ref.featureIdentifier) continue;
            f.ids.push_back(p.mum.identity);
            f.keyForms.push_back(p.mum.identity + p.mum.version);
            f.partitions.push_back(p.mum.partition);
        }
        for (const Pkg& p : packages)
{
    if (!p.haveMum || p.ref.fmId != fmid) continue;
    bool seenFile = false;
    for (const std::string& f : fmFiles) if (EqualsNoCase(f, p.ref.fmFile)) { seenFile = true; break; }
    if (!seenFile) fmFiles.push_back(p.ref.fmFile);
}
// FM 自身包：identity = "Microsoft.ModernPC." + FM 文件名主干（固定前缀，按 FM 文件而非 FMID）
for (const std::string& fmFile : fmFiles)
{
    std::string stem = fmFile;
    const std::size_t dot = stem.find_last_of('.');
    if (dot != std::string::npos) stem = stem.substr(0, dot);
    const std::string want = "Microsoft.ModernPC." + stem;
    for (const Pkg& p : packages)
    {
        if (!p.haveMum || !EqualsNoCase(p.ref.fmFile, fmFile)) continue;
        if (!EqualsNoCase(p.mum.name, want)) continue;
        if (!EqualsNoCase(p.mum.pkt, "628844477771337a")) continue;
        if (!p.mum.language.empty()) continue;
        f.ids.push_back(p.mum.identity);
        f.keyForms.push_back(p.mum.identity + p.mum.version);
        f.partitions.push_back(p.mum.partition);
        break;
    }
}
        features.push_back(f);
    }

    for (const Pkg& p : packages)
    {
        if (!p.haveMum || !p.mum.hasCapability) continue;
        if (p.mum.featureId.empty()) continue;
        // 非 FIP 包的 mum 里也有 declareCapability，只有 *.FIP 包才成特性
        if (LowerText(p.ref.cab).find(".fip~") == std::string::npos) continue;
        Feature f;
        f.featureId = p.mum.featureId;
        f.fmid = p.mum.fmid.empty() ? p.ref.fmId : p.mum.fmid;
        f.group = p.mum.group.empty() ? "Microsoft" : p.mum.group;
        f.satelliteLang = p.mum.satelliteLang;
        for (const std::string& key : p.mum.declared)
        {
            auto it = byKey.find(key);
            if (it == byKey.end()) continue;
            const MumInfo& declared = packages[it->second].mum;
            f.ids.push_back(declared.identity);
            f.keyForms.push_back(declared.identity + declared.version);
            f.partitions.push_back(declared.partition);
        }
        f.ids.push_back(p.mum.identity);
        f.keyForms.push_back(p.mum.identity + p.mum.version);
        f.partitions.push_back(p.mum.partition);
        features.push_back(f);
    }

    // 排序：FMID 不区分大小写升序（空 FMID 最后），同组内 BASE 在前、其余保持 plan 顺序。
    // 实测参照就是这个次序：FOSMOBSFM → LANGUAGEPACKFM → MODERNPCEMULATOR → … → WCPFM → 空 FMID。
    std::stable_sort(features.begin(), features.end(), [](const Feature& a, const Feature& b) {
        const bool ae = a.fmid.empty();
        const bool be = b.fmid.empty();
        if (ae != be) return be;
        // 用 ASCII 原样比较：参照里 MODERNPCEMULATOR 在 MODERNPC_CORE_OPTIONAL 之前（'E' < '_'）。
        if (a.fmid != b.fmid) return a.fmid < b.fmid;
        const bool ab = (a.featureId == "BASE");
        const bool bb = (b.featureId == "BASE");
        if (ab != bb) return ab;
        return false;
    });

    // 3) 顶层 Packages：特性包并集，按 ID 不区分大小写升序
    std::vector<std::string> unionIds;
    std::vector<std::string> unionKeys;
    for (const Feature& f : features)
    {
        for (const std::string& id : f.ids)
        {
            const std::string key = LowerText(id);
            bool seen = false;
            for (const std::string& k : unionKeys) if (k == key) { seen = true; break; }
            if (seen) continue;
            unionKeys.push_back(key);
            unionIds.push_back(id);
        }
    }
    std::stable_sort(unionIds.begin(), unionIds.end(), [](const std::string& a, const std::string& b) {
        return LowerText(a) < LowerText(b);
    });

    std::map<std::string, const MumInfo*> byIdentity;
    for (const Pkg& p : packages) if (p.haveMum) byIdentity[LowerText(p.mum.identity)] = &p.mum;

    // 4) 写 XML
    std::string xml;
    xml += "<?xml version='1.0' encoding='utf-8'?>\r\n";
    xml += "<CompDB xmlns=\"http://schemas.microsoft.com/embedded/2004/10/ImageUpdate\" CreatedDate=\"";
    xml += NowText();
    xml += "\" Revision=\"1\" SchemaVersion=\"1.0\" BuildInfo=\"";
    XmlEscapeTo(xml, buildInfo);
    xml += "\" OSVersion=\"";
    XmlEscapeTo(xml, osVersion);
    xml += "\" Type=\"Device\">\r\n<Features>\r\n";
    for (const Feature& f : features)
    {
        xml += "<Feature Type=\"MobileFeature\" FeatureID=\"";
        XmlEscapeTo(xml, f.featureId);
        xml += "\" FMID=\"";
        XmlEscapeTo(xml, f.fmid);
        xml += "\" Group=\"";
        XmlEscapeTo(xml, f.group);
        xml += "\">\r\n<Packages>\r\n";
        for (const std::string& id : f.ids)
        {
            xml += "<Package ID=\"";
            XmlEscapeTo(xml, id);
            xml += "\"/>\r\n";
        }
        xml += "</Packages>\r\n<Token value=\"INSTALLED\"/>\r\n<Intent value=\"PREINSTALL\"/>\r\n";
        xml += (f.featureId.compare(0, 9, "Language.") == 0) ? "<Intent value=\"USER\"/>\r\n"
                                                              : "<Intent value=\"SYSTEM\"/>\r\n";
        if (!f.satelliteLang.empty())
        {
            xml += "<DeclareSatellite type=\"LANGUAGE\" value=\"";
            XmlEscapeTo(xml, f.satelliteLang);
            xml += "\"/>\r\n";
        }
        xml += "</Feature>\r\n";
    }
    xml += "</Features>\r\n<Packages>\r\n";
    for (const std::string& id : unionIds)
    {
        const auto it = byIdentity.find(LowerText(id));
        if (it == byIdentity.end()) continue;
        xml += "<Package ID=\"";
        XmlEscapeTo(xml, id);
        xml += "\" KeyForm=\"";
        XmlEscapeTo(xml, id + it->second->version);
        xml += "\" Partition=\"";
        XmlEscapeTo(xml, it->second->partition);
        xml += "\"/>\r\n";
    }
    xml += "</Packages>\r\n<EnumeratedPackages>\r\n";
    // 参照里 EnumeratedPackages 按 ID 不区分大小写升序。
    std::vector<const Pkg*> enumerated;
    for (const Pkg& p : packages)
    {
        if (!p.haveMum) continue;
        // 后续阶段会过滤掉产品包（参照里 194 个 cab 只剩 193）
        if (EqualsNoCase(p.mum.name, "Microsoft-OneCoreUpdateOS-Product-Package") && !p.mum.language.empty())
        {
            continue;
        }
        enumerated.push_back(&p);
    }
    std::stable_sort(enumerated.begin(), enumerated.end(), [](const Pkg* a, const Pkg* b) {
        return LowerText(a->mum.identity) < LowerText(b->mum.identity);
    });
    std::size_t enumCount = 0;
    for (const Pkg* pp : enumerated)
    {
        const Pkg& p = *pp;
        xml += "<Package ID=\"";
        XmlEscapeTo(xml, p.mum.identity);
        xml += "\" KeyForm=\"";
        XmlEscapeTo(xml, p.mum.identity + p.mum.version);
        xml += "\" Partition=\"";
        XmlEscapeTo(xml, p.mum.partition);
        xml += "\">\r\n<Token value=\"INSTALLED\"/>\r\n</Package>\r\n";
        ++enumCount;
    }
    xml += "</EnumeratedPackages>\r\n<FailedDiffs/>\r\n<DeferredUpdates/>\r\n<Languages>\r\n";
    std::vector<std::string> languages = oem.languages;
    std::stable_sort(languages.begin(), languages.end(), [](const std::string& a, const std::string& b) {
        return LowerText(a) < LowerText(b);
    });
    for (const std::string& lang : languages)
    {
        xml += "<Language Id=\"";
        XmlEscapeTo(xml, lang);
        xml += "\"/>\r\n";
    }
    xml += "</Languages>\r\n<Architectures/>\r\n<ValuePair>\r\n";
    xml += "<Pair Name=\"Arch\" Value=\"x86\"/>\r\n";
    xml += "<Pair Name=\"BuildBranch\" Value=\"" + buildBranch + "\"/>\r\n";
    xml += "<Pair Name=\"BuildLayer.Branch.ModernPC\" Value=\"" + buildBranch + "\"/>\r\n";
    xml += "<Pair Name=\"BuildLayer.Branch.OnecoreUAP\" Value=\"" + buildBranch + "\"/>\r\n";
    xml += "<Pair Name=\"BuildLayer.OrderNumber.ModernPC\" Value=\"8192\"/>\r\n";
    xml += "<Pair Name=\"BuildLayer.OrderNumber.OnecoreUAP\" Value=\"4096\"/>\r\n";
    xml += "<Pair Name=\"BuildLayer.TopLevel\" Value=\"ModernPC\"/>\r\n";
    xml += "<Pair Name=\"BuildLayer.Version.ModernPC\" Value=\"" + osVersion + "\"/>\r\n";
    xml += "<Pair Name=\"BuildLayer.Version.OnecoreUAP\" Value=\"" + osVersion + "\"/>\r\n";
    xml += "<Pair Name=\"CompactOS\" Value=\"1\"/>\r\n";
    xml += "<Pair Name=\"FreeDataStorage\" Value=\"0\"/>\r\n";
    xml += "<Pair Name=\"FreeSystemStorage\" Value=\"0\"/>\r\n";
    xml += "<Pair Name=\"MobileStoreVersion\" Value=\"4\"/>\r\n";
    xml += "<Pair Name=\"OSVersion\" Value=\"" + osVersion + "\"/>\r\n";
    xml += "<Pair Name=\"ProcessorArchitecture\" Value=\"" + UpperText(arch) + "\"/>\r\n";
    xml += "<Pair Name=\"TotalRam\" Value=\"0\"/>\r\n";
    xml += "<Pair Name=\"TotalStorage\" Value=\"0\"/>\r\n";
    xml += "<Pair Name=\"WinREVersion\"/>\r\n";
    xml += "</ValuePair>\r\n</CompDB>\r\n";

    if (!WriteFileText(Utf8ToWide(outPath), xml, error))
    {
        Err(error);
        LogClose();
        return 1;
    }
    OutLn("已写 DeviceDB：" + outPath + "（特性 " + std::to_string(features.size()) +
          "，顶层包 " + std::to_string(unionIds.size()) + "，枚举包 " + std::to_string(enumCount) +
          "，mum 失败 " + std::to_string(badCount) + "）");
    LogWrite(LogLevel::Info, "devicedb written: " + outPath);
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
    unsigned long long sectorSize = 0;
    bool hasSectorSize = false;
    bool onlyAllocateDefinedGptEntries = false;
    std::vector<LayoutPartition> partitions;
};

struct LayoutPool
{
    std::string name;
    unsigned long long version = 0;
    std::vector<LayoutStore> stores;
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
    std::vector<LayoutPool> poolGroups;
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
    const std::string storeSector = NodeText(node, "SectorSize");
    store.hasSectorSize = !storeSector.empty();
    store.sectorSize = ParseNum(storeSector);
    store.onlyAllocateDefinedGptEntries = ReadFlag(node, "OnlyAllocateDefinedGptEntries");
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
            LayoutPool group;
            group.name = NodeText(pool, "Name");
            group.version = ParseNum(NodeText(pool, "PoolVersion"));
            if (group.version == 0) group.version = ParseNum(NodeText(pool, "Version"));
            const XmlNode* stores = Child(pool, "Stores");
            if (stores != nullptr)
            {
                for (const XmlNode* store = Child(stores, "Store"); store != nullptr; store = Next(store, "Store"))
                {
                    group.stores.push_back(ParseStore(store));
                    model.pools.push_back(ParseStore(store));
                }
            }
            if (group.name.empty()) group.name = "OSPool";
            model.poolGroups.push_back(group);
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
    out.poolGroups.clear();
    if (!out.pools.empty())
    {
        LayoutPool group;
        if (!ms.poolGroups.empty())
        {
            group.name = ms.poolGroups.front().name;
            group.version = ms.poolGroups.front().version;
        }
        else
        {
            group.name = "OSPool";
        }
        group.stores = out.pools;
        out.poolGroups.push_back(group);
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

    // ── vhd：虚拟磁盘（VirtDisk.dll）+ GPT 分区表 ─────────────────────────────
    //
    // 对应 imageapp 的 CreateVirtualHardDisk / AttachVirtualHardDisk /
    // PartitionVirtualHardDisk 这三件事的 B 方案（单 VHDX + GPT，见 IMAGING-VHD-STUDY.md）。
    static std::uint64_t VhdScaled(const std::string& text)
    {
        if (text.empty()) return 0;
        char* end = nullptr;
        const unsigned long long base = std::strtoull(text.c_str(), &end, 0);
        std::uint64_t mul = 1;
        if (end != nullptr && *end != 0)
        {
            const char c = static_cast<char>(std::toupper(static_cast<unsigned char>(*end)));
            if (c == 'K') mul = 1024ull;
            else if (c == 'M') mul = 1024ull * 1024;
            else if (c == 'G') mul = 1024ull * 1024 * 1024;
            else if (c == 'T') mul = 1024ull * 1024 * 1024 * 1024;
        }
        return static_cast<std::uint64_t>(base) * mul;
    }

    static void PrintVhdUsage()
    {
        OutLn("用法：ocid_imagegen vhd <子命令> [参数]");
        OutLn("");
        OutLn("  create <文件.vhdx> --size <容量> [--fixed] [--block <字节>] [--sector 512|4096]");
        OutLn("      建一个 VHDX。容量可写 8G / 42G / 4096M；默认动态扩展，--fixed 建固定盘。");
        OutLn("  attach <文件.vhdx> [--readonly]");
        OutLn("      挂载并打印物理盘路径（\\\\.\\PhysicalDriveN）。");
        OutLn("  detach <文件.vhdx>");
        OutLn("      卸载。");
        OutLn("  info <文件.vhdx> [--partition]");
        OutLn("      打印容量/块大小；--partition 时再读回盘上的 GPT 分区表。");
        OutLn("  partition <文件.vhdx> [--layout <DeviceLayout.xml>] [--device <\\\\.\\PhysicalDriveN>]");
        OutLn("            [--disk-id <GUID>] [--size <容量>] [--dry] [--keep-attached]");
        OutLn("      按设备布局写 GPT：分区顺序/类型 GUID/分区 GUID/字节对齐都取自布局；");
        OutLn("      不给 --device 会先挂载（结果保持挂载，用 vhd detach 收回）。");
        OutLn("");
        OutLn("说明：虚拟容量必须是 1 MiB 的整数倍；分区起始 1 MiB 之后、按 DefaultPartitionByteAlignment 对齐。");
    }

    // 按"存储池(Store)"分配：Store 内分区用 <UseAllSpace> 吃满该 Store 剩余空间，
    // 否则用 <TotalSectors>/<TotalBytes>；分区起始按自身 <ByteAlignment>，Store 之间按
    // <DefaultPartitionByteAlignment>。WSK 布局里分区统一写 41943040 扇区（20 GiB）+ UseAllSpace=true，
    // 真实语义是"吃满所在 Store"，不能直接拿 TotalSectors 当分区大小。
    static bool ParseHexOrDec(const std::string& text, std::uint64_t& value)
    {
        std::string t;
        for (const char c : text)
        {
            if (c == ' ' || c == '\t') continue;
            t.push_back(c);
        }
        if (t.empty()) return false;
        try
        {
            std::size_t used = 0;
            const bool hex = t.size() > 2 && t[0] == '0' && (t[1] == 'x' || t[1] == 'X');
            value = hex ? std::stoull(t.substr(2), &used, 16) : std::stoull(t, &used, 10);
            return used != 0;
        }
        catch (...)
        {
            return false;
        }
    }

    // 多盘布局里每个物理盘的扇区可能不同（QC 用 4096），这里按 store 取。
    static std::uint64_t StoreSectorBytes(const LayoutModel& model, const LayoutStore& store)
    {
        if (store.hasSectorSize && store.sectorSize != 0) return store.sectorSize;
        if (model.hasSectorSize && model.sectorSize != 0) return model.sectorSize;
        return 512;
    }

    // 布局里的 <Partition Name="OSPool"> 只是语义标记：真正的池由 ImageStorageService 追加，
    // 我们不写它的 GPT 项（等价于 imageapp 的做法）。
    static bool IsPoolPartition(const LayoutPartition& p)
    {
        if (LowerText(p.name) == "ospool") return true;
        const std::string t = LowerText(p.type);
        return t.find("e75caf8f-f680-4cee-afa3-b001e56efc2d") != std::string::npos;
    }

    // 选一个存储池：布局里有多个池时必须用 --pool 指定。
    static const LayoutPool* SelectLayoutPool(const LayoutModel& model, const std::string& want,
                                              std::string& error)
    {
        if (model.poolGroups.empty()) return nullptr;
        if (!want.empty())
        {
            for (const LayoutPool& pool : model.poolGroups)
            {
                if (EqualsNoCase(pool.name, want)) return &pool;
            }
            error = "布局里没有名为 " + want + " 的存储池";
            return nullptr;
        }
        if (model.poolGroups.size() == 1) return &model.poolGroups.front();
        error = "布局里有多个存储池，请用 --pool 指定：";
        for (const LayoutPool& pool : model.poolGroups) error += pool.name + " ";
        return nullptr;
    }

    static std::wstring LayoutPoolName(const LayoutPool* pool)
    {
        return pool != nullptr ? Utf8ToWide(pool->name) : std::wstring(L"OSPool");
    }

    static unsigned int LayoutPoolVersion(const LayoutPool* pool, unsigned int fallback)
    {
        if (pool != nullptr && pool->version != 0) return static_cast<unsigned int>(pool->version);
        return fallback != 0 ? fallback : 24u;
    }

    struct PhysicalDiskPlan
    {
        std::string storeType;
        std::string devicePath;
        std::string fileName;
        std::uint64_t bytes = 0;
        std::uint64_t sector = 512;
        bool holdsPool = false;
        bool onlyDefinedEntries = false;
        std::vector<VhdPartition> parts;
    };

    // 一块物理盘（或 v1 平铺盘）的分区表：池载体分区不写，UseAllSpace 分区吃掉剩余空间。
    static bool BuildPhysicalDiskParts(const LayoutModel& model, const LayoutStore& store,
                                       std::uint64_t diskBytes, std::vector<VhdPartition>& parts,
                                       bool& holdsPool, std::string& error)
    {
        const std::uint64_t sector = StoreSectorBytes(model, store);
        const std::uint64_t defaultAlignment = model.defaultAlignment != 0 ? model.defaultAlignment : (1024ull * 1024);
        auto align = [](std::uint64_t v, std::uint64_t a) { return a == 0 ? v : (v + a - 1) / a * a; };

        for (const LayoutPartition& p : store.partitions)
        {
            if (IsPoolPartition(p)) holdsPool = true;
        }
        // 尺寸紧凑的盘（OnlyAllocateDefinedGptEntries=true，如 XBL 只有 1024 个 4K 扇区）
        // 不能从 1 MiB 起，只需要让开 MBR/GPT 头。
        std::uint64_t cursor = store.onlyAllocateDefinedGptEntries ? align(2 * sector, defaultAlignment)
                                                                  : align(1024ull * 1024, defaultAlignment);

        auto partAlign = [&](const LayoutPartition& p) -> std::uint64_t
        {
            std::uint64_t alignment = defaultAlignment;
            std::uint64_t custom = 0;
            if (!p.byteAlignment.empty() && ParseHexOrDec(p.byteAlignment, custom) && custom != 0) alignment = custom;
            if (p.singleSectorAlignment) alignment = sector;
            return alignment;
        };
        // 先按顺序压固定尺寸分区，求出真实已用空间，再给 UseAllSpace 分区分剩余。
        std::uint64_t fixedEnd = cursor;
        std::size_t flexible = 0;
        for (const LayoutPartition& p : store.partitions)
        {
            if (IsPoolPartition(p)) continue;
            std::uint64_t size = 0;
            if (p.hasTotalBytes && p.totalBytes != 0) size = p.totalBytes;
            else if (p.totalSectors != 0) size = p.totalSectors * sector;
            if (size == 0 && p.minFreeBytes != 0) size = p.minFreeBytes;
            if (size == 0 && p.minFreeSectors != 0) size = p.minFreeSectors * sector;
            if (p.useAllSpace || size == 0) { ++flexible; continue; }
            size = align(size, sector);
            fixedEnd = align(fixedEnd, partAlign(p)) + size;
        }
        // 末尾要给备份 GPT 留位（容错 33 个扇区，普通盘按 1 MiB）。
        const std::uint64_t tailReserve = store.onlyAllocateDefinedGptEntries ? 33 * sector : (1024ull * 1024);
        std::uint64_t available = diskBytes > fixedEnd + tailReserve ? diskBytes - fixedEnd - tailReserve : 0;
        std::uint64_t flexibleBytes = 0;
        if (flexible != 0 && available != 0)
        {
            flexibleBytes = available / flexible / sector * sector;
            if (flexibleBytes == 0) flexibleBytes = sector;
        }

        for (const LayoutPartition& p : store.partitions)
        {
            if (IsPoolPartition(p)) continue;
            std::uint64_t size = 0;
            if (p.hasTotalBytes && p.totalBytes != 0) size = p.totalBytes;
            else if (p.totalSectors != 0) size = p.totalSectors * sector;
            if (size == 0 && p.minFreeBytes != 0) size = p.minFreeBytes;
            if (size == 0 && p.minFreeSectors != 0) size = p.minFreeSectors * sector;
            if (p.useAllSpace || size == 0) size = flexibleBytes;
            if (size == 0) { error = "分区 " + p.name + " 没有可用尺寸"; return false; }
            size = align(size, sector);

            const std::uint64_t alignment = partAlign(p);

            VhdPartition vp;
            vp.name = p.name;
            vp.type = p.type;
            vp.id = p.id;
            vp.offsetBytes = align(cursor, alignment);
            vp.sizeBytes = size;
            vp.mbrType = static_cast<unsigned int>(ParseNum(p.type) & 0xFF);
            vp.bootable = p.bootable;
            parts.push_back(vp);
            cursor = vp.offsetBytes + size;
        }
        if (parts.empty()) { error = "物理存储 " + store.storeType + " 没有可分配的分区"; return false; }
        if (cursor + tailReserve > diskBytes)
        {
            error = "物理存储 " + store.storeType + " 的分区总长超出磁盘（需要 " +
                    FormatBytes(cursor + tailReserve) + "，磁盘 " + FormatBytes(diskBytes) + "）";
            return false;
        }
        return true;
    }

    // 全部分区都带 MBR 类型码 ⇒ 这张盘用 MBR 分区表（v1 平铺 / BIOS 引导）。
    static bool AllMbrTypes(const std::vector<VhdPartition>& parts)
    {
        if (parts.empty()) return false;
        for (const VhdPartition& vp : parts)
        {
            if (vp.mbrType == 0) return false;
        }
        return true;
    }

    // v1 平铺布局没给 <SizeInSectors> 时，按分区尺寸和推一个默认盘大小。
    static std::uint64_t DefaultFlatDiskBytes(const LayoutModel& model)
    {
        const std::uint64_t defaultAlignment = model.defaultAlignment != 0 ? model.defaultAlignment : (1024ull * 1024);
        auto align = [](std::uint64_t v, std::uint64_t a) { return a == 0 ? v : (v + a - 1) / a * a; };
        std::uint64_t total = align(1024ull * 1024, defaultAlignment) * 2;
        for (const LayoutStore& store : model.stores)
        {
            const std::uint64_t sector = StoreSectorBytes(model, store);
            for (const LayoutPartition& p : store.partitions)
            {
                std::uint64_t size = 0;
                if (p.hasTotalBytes && p.totalBytes != 0) size = p.totalBytes;
                else if (p.totalSectors != 0) size = p.totalSectors * sector;
                if (size == 0 && p.minFreeBytes != 0) size = p.minFreeBytes;
                if (size == 0 && p.minFreeSectors != 0) size = p.minFreeSectors * sector;
                if (size == 0) size = 512ull * 1024 * 1024;
                total += align(size, 1024ull * 1024) + 1024ull * 1024;
            }
        }
        if (model.minSectorCount != 0)
        {
            const std::uint64_t min = model.minSectorCount * (model.hasSectorSize ? model.sectorSize : 512);
            if (total < min) total = min;
        }
        if (total < 64ull * 1024 * 1024) total = 64ull * 1024 * 1024;
        return align(total, 1024ull * 1024);
    }

    bool WantsHelp(const std::vector<std::string>& args)
    {
        for (const std::string& a : args)
        {
            if (a == "--help" || a == "-h" || a == "/?") return true;
        }
        return false;
    }

    static bool BuildPartitionsFromLayout(const LayoutModel& model, std::uint64_t diskBytes,
                                          std::vector<VhdPartition>& parts, std::string& error)
    {
        const std::uint64_t sector = model.sectorSize != 0 ? model.sectorSize : 512;
        const std::uint64_t defaultAlignment = model.defaultAlignment != 0 ? model.defaultAlignment : (1024ull * 1024);
        auto align = [](std::uint64_t v, std::uint64_t a) { return a == 0 ? v : (v + a - 1) / a * a; };
        std::uint64_t cursor = align(1024ull * 1024, defaultAlignment);

        for (const LayoutStore& store : model.pools)
        {
            if (store.storeType.rfind("VIRT_", 0) == 0) continue;
            if (store.partitions.empty()) continue;

            std::uint64_t storeBytes = 0;
            if (store.hasSizeBytes && store.sizeInBytes != 0) storeBytes = store.sizeInBytes;
            else if (store.sizeInSectors != 0) storeBytes = store.sizeInSectors * sector;

            auto partAlign = [&](const LayoutPartition& p) -> std::uint64_t
        {
            std::uint64_t alignment = defaultAlignment;
            std::uint64_t custom = 0;
            if (!p.byteAlignment.empty() && ParseHexOrDec(p.byteAlignment, custom) && custom != 0) alignment = custom;
            if (p.singleSectorAlignment) alignment = sector;
            return alignment;
        };
        // 先按顺序把固定尺寸分区「排一遍」，得到剩余空间的真实起点（含对齐空档）。
        std::uint64_t fixedEnd = cursor;
        std::size_t flexible = 0;
        for (const LayoutPartition& p : store.partitions)
        {
            if (IsPoolPartition(p)) continue;
            std::uint64_t size = 0;
            if (p.hasTotalBytes && p.totalBytes != 0) size = p.totalBytes;
            else if (p.totalSectors != 0) size = p.totalSectors * sector;
            if (size == 0 && p.minFreeBytes != 0) size = p.minFreeBytes;
            if (size == 0 && p.minFreeSectors != 0) size = p.minFreeSectors * sector;
            if (p.useAllSpace || size == 0) { ++flexible; continue; }
            size = align(size, sector);
            fixedEnd = align(fixedEnd, partAlign(p)) + size;
        }
        // 尾部留备份 GPT：紧尺寸盘 33 个扇区，普通盘 1 MiB。
        const std::uint64_t tailReserve = store.onlyAllocateDefinedGptEntries ? 33 * sector : (1024ull * 1024);
        std::uint64_t available = diskBytes > fixedEnd + tailReserve ? diskBytes - fixedEnd - tailReserve : 0;
        std::uint64_t flexibleBytes = 0;
        if (flexible != 0 && available != 0)
        {
            flexibleBytes = available / flexible / sector * sector;
            if (flexibleBytes == 0) flexibleBytes = sector;
        }

        for (const LayoutPartition& p : store.partitions)
        {
            if (IsPoolPartition(p)) continue;
            std::uint64_t size = 0;
            if (p.hasTotalBytes && p.totalBytes != 0) size = p.totalBytes;
            else if (p.totalSectors != 0) size = p.totalSectors * sector;
            if (size == 0 && p.minFreeBytes != 0) size = p.minFreeBytes;
            if (size == 0 && p.minFreeSectors != 0) size = p.minFreeSectors * sector;
            if (p.useAllSpace || size == 0) size = flexibleBytes;
            if (size == 0) { error = "分区 " + p.name + " 没有可用尺寸"; return false; }
            size = align(size, sector);

            const std::uint64_t alignment = partAlign(p);

            VhdPartition vp;
            vp.name = p.name;
            vp.type = p.type;
            vp.id = p.id;
            vp.offsetBytes = align(cursor, alignment);
            vp.sizeBytes = size;
            vp.mbrType = static_cast<unsigned int>(ParseNum(p.type) & 0xFF);
            vp.bootable = p.bootable;
            parts.push_back(vp);
            cursor = vp.offsetBytes + size;
        }
        if (parts.empty()) { error = "物理存储 " + store.storeType + " 没有可分区的分区项"; return false; }
        if (cursor + tailReserve > diskBytes)
        {
            error = "物理存储 " + store.storeType + " 的分区总长超过磁盘容量（需要 " +
                    FormatBytes(cursor + tailReserve) + "，磁盘 " + FormatBytes(diskBytes) + "）";
            return false;
        }
        }
        return true;
    }

    static bool PlanPhysicalDisks(const LayoutModel& model, const std::string& outDir,
                                  std::vector<PhysicalDiskPlan>& plans, std::uint64_t flatBytes,
                                  std::string& error)
    {
        std::size_t physical = 0;
        for (const LayoutStore& store : model.stores)
        {
            if (store.devicePath.empty()) continue;
            ++physical;
            PhysicalDiskPlan plan;
            plan.storeType = store.storeType;
            plan.devicePath = store.devicePath;
            plan.onlyDefinedEntries = store.onlyAllocateDefinedGptEntries;
            plan.sector = StoreSectorBytes(model, store);
            plan.bytes = (store.hasSizeBytes && store.sizeInBytes != 0) ? store.sizeInBytes
                                                                        : store.sizeInSectors * plan.sector;
            if (plan.bytes == 0) { error = "物理存储 " + store.storeType + " 没有尺寸"; return false; }
            if (!BuildPhysicalDiskParts(model, store, plan.bytes, plan.parts, plan.holdsPool, error)) return false;
            std::string leaf = store.storeType.empty() ? ("disk" + std::to_string(physical)) : store.storeType;
            if (physical > 1)
            {
                bool clash = false;
                for (const PhysicalDiskPlan& other : plans) if (EqualsNoCase(other.storeType, leaf)) clash = true;
                if (clash) leaf += "_" + std::to_string(physical);
            }
            plan.fileName = outDir.empty() ? (leaf + ".vhdx") : (outDir + "\\" + leaf + ".vhdx");
            plans.push_back(plan);
        }
        if (physical == 0)
        {
            // v1 平铺布局（GenericGPT / VMGen1）：没有 Store，所有分区直接铺在一块盘上。
            const LayoutStore* flat = nullptr;
            for (const LayoutStore& s : model.stores) if (s.storeType.find("v1") != std::string::npos) flat = &s;
            if (flat == nullptr && model.pools.size() == 1) flat = &model.pools[0];
            if (flat != nullptr)
            {
                PhysicalDiskPlan plan;
                plan.storeType = "Default";
                plan.sector = model.sectorSize != 0 ? model.sectorSize : 512;
                plan.bytes = flatBytes != 0 ? flatBytes : DefaultFlatDiskBytes(model);
                plan.bytes = (plan.bytes + plan.sector - 1) / plan.sector * plan.sector;
                if (!BuildPhysicalDiskParts(model, *flat, plan.bytes, plan.parts, plan.holdsPool, error)) return false;
                plan.fileName = outDir.empty() ? std::string("Default.vhdx") : (outDir + "\\Default.vhdx");
                plans.push_back(plan);
                return true;
            }
            error = "布局里没有带 <DevicePath> 的物理 <Store>；单盘池布局请用 pool build";
            return false;
        }
        return true;
    }

    // 固件分区内容：按分区名在目录里找 <Name>.bin/.img/.raw（大小写不敏感）。
    static bool LoadFirmwareBlob(const std::string& dir, const std::string& name, std::vector<std::uint8_t>& blob)
    {
        if (dir.empty() || name.empty()) return false;
        std::error_code ec;
        const std::filesystem::path root = std::filesystem::u8path(dir);
        if (!std::filesystem::is_directory(root, ec)) return false;
        const std::string want = LowerText(name);
        for (const auto& entry : std::filesystem::directory_iterator(root, ec))
        {
            if (!entry.is_regular_file(ec)) continue;
            const std::string whole = LowerText(entry.path().filename().u8string());
            std::string stem = whole;
            std::string ext;
            const std::size_t dot = whole.find_last_of('.');
            if (dot != std::string::npos) { stem = whole.substr(0, dot); ext = whole.substr(dot); }
            if (stem != want && whole != want) continue;
            if (!ext.empty() && ext != ".bin" && ext != ".img" && ext != ".raw" && ext != ".fw") continue;
            std::ifstream in(entry.path(), std::ios::binary);
            if (!in) continue;
            blob.assign(std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>());
            return !blob.empty();
        }
        return false;
    }

    // A firmware package declares <package binaryPartition="true" targetPartition="X">
    // and a payload <file name="$(runtime.bootdrive)\Y" cabpath="...">. The payload is
    // written as a raw image at the start of partition X.
    struct FirmwarePayload
    {
        std::string cabPath;
        std::string entryName;
        std::string fileName;
    };

    static std::string TagAttr(const std::string& tag, const std::string& attr)
    {
        const std::string key = " " + attr + "=\"";
        const std::size_t at = tag.find(key);
        if (at == std::string::npos) return std::string();
        const std::size_t from = at + key.size();
        const std::size_t to = tag.find('"', from);
        if (to == std::string::npos) return std::string();
        return tag.substr(from, to - from);
    }

    static bool LoadFirmwarePackages(const std::string& cabDir,
                                     std::vector<std::pair<std::string, FirmwarePayload>>& payloads,
                                     std::string& error)
    {
        payloads.clear();
        std::error_code ec;
        const std::filesystem::path root = std::filesystem::u8path(cabDir);
        if (!std::filesystem::is_directory(root, ec))
        {
            error = "not a directory: " + cabDir;
            return false;
        }
        for (const auto& entry : std::filesystem::directory_iterator(root, ec))
        {
            if (!entry.is_regular_file(ec)) continue;
            if (LowerText(entry.path().extension().u8string()) != ".cab") continue;
            std::string mum;
            std::string ignored;
            if (!CabExtractText(entry.path().wstring(), "update.mum", mum, ignored)) continue;
            const std::size_t pkg = mum.find("<package ");
            if (pkg == std::string::npos) continue;
            const std::size_t pkgEnd = mum.find('>', pkg);
            if (pkgEnd == std::string::npos) continue;
            const std::string pkgTag = mum.substr(pkg, pkgEnd - pkg + 1);
            if (pkgTag.find("binaryPartition=\"true\"") == std::string::npos) continue;
            const std::string partition = TagAttr(pkgTag, "targetPartition");
            if (partition.empty()) continue;
            const std::string bootPrefix = "$(runtime.bootdrive)\\";
            std::size_t pos = pkgEnd + 1;
            while (true)
            {
                const std::size_t f = mum.find("<file ", pos);
                if (f == std::string::npos) break;
                const std::size_t fe = mum.find('>', f);
                if (fe == std::string::npos) break;
                const std::string fileTag = mum.substr(f, fe - f + 1);
                pos = fe + 1;
                std::string name = TagAttr(fileTag, "name");
                const std::string cabpath = TagAttr(fileTag, "cabpath");
                if (name.empty() || cabpath.empty()) continue;
                if (name.compare(0, bootPrefix.size(), bootPrefix) == 0)
                {
                    name = name.substr(bootPrefix.size());
                }
                while (!name.empty() && (name.front() == '\\' || name.front() == '/'))
                {
                    name = name.substr(1);
                }
                if (name.empty() || name.find('\\') != std::string::npos || name.find('/') != std::string::npos)
                {
                    continue;
                }
                FirmwarePayload payload;
                payload.cabPath = entry.path().u8string();
                payload.entryName = cabpath;
                payload.fileName = name;
                payloads.push_back(std::make_pair(LowerText(partition), payload));
                break;
            }
        }
        return true;
    }

    static const FirmwarePayload* FindFirmwarePayload(
        const std::vector<std::pair<std::string, FirmwarePayload>>& payloads, const std::string& partition)
    {
        const std::string key = LowerText(partition);
        for (const auto& item : payloads)
        {
            if (item.first == key) return &item.second;
        }
        return nullptr;
    }

    int CmdDisk(const std::vector<std::string>& args)
    {
        if (args.size() < 3 || WantsHelp(args))
        {
            OutLn("用法：ocid_imagegen disk plan|build --layout <DeviceLayout.xml> [--outdir <目录>]");
            OutLn("                              [--store <StoreType>] [--dry] [--packages <dir>] [--firmware <目录>]");
        OutLn("");
        OutLn("  --firmware <目录>  固件分区灌内容：目录里 <分区名>.bin/.img/.raw 原样写入该分区");
            OutLn("  --packages <dir>  firmware payloads from package cabs (update.mum binaryPartition/targetPartition)");
            OutLn("  --size <n>        v1 平铺布局（无 Store 尺寸）的盘容量；缺省 = 固定分区之和 + 8 GiB");
            OutLn("  --pool <name>     storage pool name from the layout (default: the only <StoragePool>); --version 24|26");
            OutLn("");
            OutLn("  plan   只打印将要生成的物理磁盘（每个带 <DevicePath> 的 <Store> 一块）。");
            OutLn("  build  逐块建 VHDX + 写 GPT；带 OSPool 分区的那块随后建池与 space。");
            OutLn("");
            OutLn("说明：QC（SDM845/QC8998）这类布局一个 FFU 里有四块盘：XBL / BACKUP_XBL /");
            OutLn("      Protected_ReadOnly / Default。前三块是裸分区盘，Default 是池载体盘。");
            OutLn("      虚拟容量按布局 <SectorSize>（QC 是 4096）换算。");
            return args.size() < 3 ? 2 : 0;
        }
        const std::string sub = args[2];
        std::string layoutPath, outDir, onlyStore, firmwareDir, packagesDir, poolName;
        unsigned int version = 24;
        std::uint64_t sizeOption = 0;
        bool dry = false;
        for (std::size_t i = 3; i < args.size(); ++i)
        {
            const std::string& a = args[i];
            auto next = [&](std::string& out) -> bool { if (i + 1 >= args.size()) return false; out = args[++i]; return true; };
            if (a == "--layout") { if (!next(layoutPath)) { Err("--layout 缺少值"); return 2; } }
            else if (a == "--outdir") { if (!next(outDir)) { Err("--outdir 缺少值"); return 2; } }
            else if (a == "--store") { if (!next(onlyStore)) { Err("--store 缺少值"); return 2; } }
            else if (a == "--packages") { if (!next(packagesDir)) { Err("--packages missing value"); return 2; } }
            else if (a == "--pool") { if (!next(poolName)) { Err("--pool missing value"); return 2; } }
            else if (a == "--version")
            {
                std::string v;
                if (!next(v)) { Err("--version missing value"); return 2; }
                version = static_cast<unsigned int>(std::strtoul(v.c_str(), nullptr, 0));
            }
            else if (a == "--firmware") { if (!next(firmwareDir)) { Err("--firmware 缺少值"); return 2; } }
            else if (a == "--size")
            {
                std::string v;
                if (!next(v)) { Err("--size 缺少值"); return 2; }
                sizeOption = ParseNum(v);
                if (sizeOption == 0) { Err("--size 无法解析：" + v); return 2; }
            }
            else if (a == "--dry") dry = true;
            else if (!a.empty() && a[0] == '-') { Err("未知参数：" + a); return 2; }
            else { Err("多余的位置参数：" + a); return 2; }
        }
        if (layoutPath.empty()) { Err("disk " + sub + " 需要 --layout <布局 XML>"); return 2; }
        LayoutModel model;
        std::string error;
        if (!LoadLayout(layoutPath, model, error)) { Err("读布局失败：" + error); return 1; }

        std::vector<PhysicalDiskPlan> plans;
        if (!PlanPhysicalDisks(model, outDir, plans, sizeOption, error)) { Err(error); return 1; }

        std::string poolError;
        const LayoutPool* poolGroup = SelectLayoutPool(model, poolName, poolError);
        if (poolGroup == nullptr && !poolError.empty()) { Err(poolError); return 1; }
        const std::wstring widePool = LayoutPoolName(poolGroup);
        const unsigned int poolVersion = LayoutPoolVersion(poolGroup, version);
        const std::vector<LayoutStore>& spaceStores = poolGroup != nullptr ? poolGroup->stores : model.pools;
        std::vector<PoolSpaceSpec> spaces;
        for (const LayoutStore& store : spaceStores)
        {
            const std::uint64_t storeSector = StoreSectorBytes(model, store);
            unsigned long long spaceSize = (store.hasSizeBytes && store.sizeInBytes != 0)
                                               ? store.sizeInBytes
                                               : store.sizeInSectors * storeSector;
            if (spaceSize == 0 || store.storeType.empty()) continue;
            PoolSpaceSpec spec;
            spec.name = Utf8ToWide(store.storeType);
            spec.sizeBytes = spaceSize;
            spec.sectorSize = static_cast<unsigned int>(storeSector);
            spaces.push_back(spec);
        }

        bool any = false;
        std::size_t built = 0;
        for (const PhysicalDiskPlan& plan : plans)
        {
            if (!onlyStore.empty() && !EqualsNoCase(plan.storeType, onlyStore)) continue;
            any = true;
            OutLn("磁盘 " + plan.storeType + "  " + plan.devicePath);
            OutLn("  文件：" + plan.fileName);
            OutLn("  容量：" + FormatBytes(plan.bytes));
            for (const VhdPartition& vp : plan.parts)
            {
                OutLn("  分区 " + vp.name + "  type=" + vp.type + "  offset=" + FormatBytes(vp.offsetBytes) +
                      "  size=" + FormatBytes(vp.sizeBytes) +
                      (vp.mbrType != 0 ? ("  mbr=" + NumText(vp.mbrType)) : std::string()) +
                      (vp.bootable ? "  active" : ""));
            }
            OutLn(AllMbrTypes(plan.parts) ? "  分区表：MBR（v1 平铺 / BIOS 引导）" : "  分区表：GPT");
            OutLn(plan.holdsPool ? "  角色：池载体（GPT 只写非池分区，OSPool 由服务追加）"
                                 : "  角色：裸分区盘（RequiredToFlash）");
        }
        if (!any) { Err("没有匹配 --store 的物理磁盘"); return 1; }
        if (!EqualsNoCase(sub, "build"))
        {
            if (!EqualsNoCase(sub, "plan")) { Err("未知子命令：" + sub); return 2; }
            OutLn("（plan 不碰磁盘；要落盘用 disk build）");
            return 0;
        }
        std::vector<std::pair<std::string, FirmwarePayload>> fwPayloads;
        if (!packagesDir.empty())
        {
            if (!LoadFirmwarePackages(packagesDir, fwPayloads, error))
            {
                Err("--packages failed: " + error);
                return 2;
            }
            OutLn("package payloads: " + NumText(fwPayloads.size()));
        }
        if (dry)
        {
            OutLn("（演练）未碰磁盘；去掉 --dry 执行。");
            return 0;
        }

        if (!outDir.empty())
        {
            if (!EnsureDirectory(Utf8ToWide(outDir), error))
            {
                Err("创建输出目录失败：" + error);
                return 1;
            }
        }

        for (const PhysicalDiskPlan& plan : plans)
        {
            if (!onlyStore.empty() && !EqualsNoCase(plan.storeType, onlyStore)) continue;
            const std::wstring path = Utf8ToWide(plan.fileName);
            // 同名文件可能还挂着（上次运行永久挂载），先卸载并删除。
            if (std::filesystem::exists(path))
            {
                std::string ignored;
                VhdDetach(path, ignored);
                std::error_code ec;
                std::filesystem::remove(path, ec);
                if (ec)
                {
                    Err("删除旧文件失败：" + plan.fileName + "（" + ec.message() + "）");
                    return 1;
                }
            }
            const std::uint32_t planSector = static_cast<std::uint32_t>(plan.sector != 0 ? plan.sector : 512);
            if (!VhdCreate(path, plan.bytes, VhdKind::Dynamic, 0, planSector, error))
            {
                Err("创建 " + plan.fileName + " 失败：" + error);
                return 1;
            }
            std::string devicePath;
            if (!VhdAttach(path, false, devicePath, error))
            {
                Err("挂载 " + plan.fileName + " 失败：" + error);
                return 1;
            }
            OutLn(plan.storeType + " 已挂载：" + devicePath);
            const bool mbrTable = AllMbrTypes(plan.parts);
            const bool wroteTable = mbrTable ? VhdWriteMbr(devicePath, plan.parts, planSector, error)
                                             : VhdWriteGpt(devicePath, std::string(), plan.parts, planSector, error);
            if (!wroteTable)
            {
                std::string ignored;
                VhdDetach(path, ignored);
                Err(std::string("写") + (mbrTable ? "MBR" : "GPT") + " 失败：" + error);
                return 1;
            }
            if (!firmwareDir.empty() || !packagesDir.empty())
            {
                std::size_t wrote = 0;
                std::string skipped;
                for (const VhdPartition& vp : plan.parts)
                {
                    std::vector<std::uint8_t> blob;
                    if (!LoadFirmwareBlob(firmwareDir, vp.name, blob) && !packagesDir.empty())
                    {
                        const FirmwarePayload* fp = FindFirmwarePayload(fwPayloads, vp.name);
                        if (fp != nullptr)
                        {
                            std::string cabError;
                            if (!CabExtractEntry(std::filesystem::u8path(fp->cabPath).wstring(), fp->entryName, blob, cabError))
                            {
                                std::string ignored;
                                VhdDetach(path, ignored);
                                Err("cab payload extract failed: " + fp->entryName + " (" + cabError + ")");
                                return 1;
                            }
                        }
                    }
                    if (blob.empty())
                    {
                        if (!skipped.empty()) skipped += " ";
                        skipped += vp.name;
                        continue;
                    }
                    if (blob.size() > vp.sizeBytes)
                    {
                        std::string ignored;
                        VhdDetach(path, ignored);
                        Err("固件镜像 " + vp.name + " 有 " + FormatBytes(blob.size()) +
                            "，超过分区容量 " + FormatBytes(vp.sizeBytes));
                        return 1;
                    }
                    std::vector<std::uint8_t> padded = blob;
                    const std::size_t sector = static_cast<std::size_t>(plan.sector > 512 ? plan.sector : 512);
                    const std::size_t aligned = ((padded.size() + sector - 1) / sector) * sector;
                    if (aligned > vp.sizeBytes)
                    {
                        std::string ignored;
                        VhdDetach(path, ignored);
                        Err("固件镜像 " + vp.name + " 按扇区对齐后超过分区容量");
                        return 1;
                    }
                    padded.resize(aligned, 0);
                    if (!VhdWriteBytes(devicePath, vp.offsetBytes, padded.data(), padded.size(), error))
                    {
                        std::string ignored;
                        VhdDetach(path, ignored);
                        Err("写固件分区 " + vp.name + " 失败：" + error);
                        return 1;
                    }
                    std::vector<std::uint8_t> check(padded.size(), 0);
                    if (!VhdReadBytes(devicePath, vp.offsetBytes, check.data(), check.size(), error) ||
                        !std::equal(padded.begin(), padded.end(), check.begin()))
                    {
                        std::string ignored;
                        VhdDetach(path, ignored);
                        Err("回读校验失败：" + vp.name);
                        return 1;
                    }
                    OutLn("  固件 " + vp.name + " <- " + FormatBytes(blob.size()) + "（已回读校验）");
                    ++wrote;
                }
                OutLn("  固件分区：写入 " + NumText(wrote) + " 个" +
                      (skipped.empty() ? std::string() : (std::string("；仅几何（无内容）：") + skipped)));
            }
            if (plan.holdsPool)
            {
                std::vector<std::string> created;
                if (!PoolBuild(Utf8ToWide(devicePath), widePool, poolVersion, Utf8ToWide(outDir), spaces, created, error))
                {
                    Err("建池失败：" + error);
                    return 1;
                }
                OutLn("  池已建：space " + NumText(created.size()) + " 个");
            }
            if (!VhdDetach(path, error)) { Err("卸载 " + plan.fileName + " 失败：" + error); return 1; }
            ++built;
        }
        OutLn("完成：物理磁盘 " + NumText(built) + " 块。");
        return 0;
    }

    int CmdVhd(const std::vector<std::string>& args)
    {
        if (args.size() < 3 || WantsHelp(args))
        {
            PrintVhdUsage();
            return args.size() < 3 ? 2 : 0;
        }
        const std::string sub = LowerText(args[2]);
        std::string file, device, layout, diskId;
        std::uint64_t size = 0, block = 0;
        std::uint32_t sectorSize = 512;
        bool fixed = false, readOnly = false, dry = false, keepAttached = false, wantPartitions = false;
        bool apply = false, physicalView = false;

        for (std::size_t i = 3; i < args.size(); ++i)
        {
            const std::string& a = args[i];
            auto next = [&](std::string& out) -> bool
            {
                if (i + 1 >= args.size()) return false;
                out = args[++i];
                return true;
            };
            if (a == "--readonly") readOnly = true;
            else if (a == "--fixed") fixed = true;
            else if (a == "--dry") dry = true;
            else if (a == "--keep-attached") keepAttached = true;
            else if (a == "--apply") apply = true;
            else if (a == "--physical") physicalView = true;
            else if (a == "--partition") wantPartitions = true;
            else if (a == "--size") { std::string v; if (!next(v)) { Err("--size 缺少值"); return 2; } size = VhdScaled(v); }
            else if (a == "--block") { std::string v; if (!next(v)) { Err("--block 缺少值"); return 2; } block = VhdScaled(v); }
            else if (a == "--sector") { std::string v; if (!next(v)) { Err("--sector 缺少值"); return 2; } sectorSize = static_cast<std::uint32_t>(VhdScaled(v)); }
            else if (a == "--device") { if (!next(device)) { Err("--device 缺少值"); return 2; } }
            else if (a == "--layout") { if (!next(layout)) { Err("--layout 缺少值"); return 2; } }
            else if (a == "--disk-id") { if (!next(diskId)) { Err("--disk-id 缺少值"); return 2; } }
            else if (!a.empty() && a[0] == '-') { Err("未知参数：" + a); return 2; }
            else if (file.empty()) file = a;
            else { Err("多余的位置参数：" + a); return 2; }
        }

        if (file.empty())
        {
            Err("缺少 VHDX 文件路径");
            return 2;
        }
        const std::wstring wideFile = Utf8ToWide(file);
        std::string error;

        if (sub == "create")
        {
            if (size == 0) { Err("create 需要 --size"); return 2; }
            if (!VhdCreate(wideFile, size, fixed ? VhdKind::Fixed : VhdKind::Dynamic,
                           static_cast<std::uint32_t>(block), sectorSize, error))
            {
                Err("建盘失败：" + error);
                return 1;
            }
            VhdInfo info;
            if (VhdQuery(wideFile, info, error))
                OutLn("已创建：" + file + "（虚拟容量 " + FormatBytes(info.virtualBytes) + "，文件 " + FormatBytes(info.fileBytes) + "）");
            else
                OutLn("已创建：" + file);
            return 0;
        }

        if (sub == "attach")
        {
            std::string devicePath;
            if (!VhdAttach(wideFile, readOnly, devicePath, error))
            {
                Err("挂载失败：" + error);
                return 1;
            }
            OutLn(devicePath);
            return 0;
        }

        if (sub == "detach")
        {
            if (!VhdDetach(wideFile, error))
            {
                Err("卸载失败：" + error);
                return 1;
            }
            OutLn("已卸载：" + file);
            return 0;
        }

        if (sub == "info")
        {
            VhdInfo info;
            if (!VhdQuery(wideFile, info, error))
            {
                Err("读取失败：" + error);
                return 1;
            }
            OutLn("文件：" + info.path);
            OutLn("文件大小：" + FormatBytes(info.fileBytes));
            OutLn("虚拟容量：" + FormatBytes(info.virtualBytes));
            OutLn(std::string("挂载：") + (info.attached ? info.devicePath : "否"));
            if (wantPartitions && info.attached)
            {
                std::vector<VhdPartition> parts;
                std::string id;
                std::uint32_t sector = 512;
                if (!VhdReadGpt(info.devicePath, id, parts, sector, error))
                {
                    Err("读分区表失败：" + error);
                    return 1;
                }
                OutLn("磁盘 GUID：" + id + "  扇区 " + NumText(sector) + "  分区 " + NumText(parts.size()));
                for (std::size_t i = 0; i < parts.size(); ++i)
                {
                    OutLn("  [" + NumText(i + 1) + "] " + parts[i].name +
                          "  type=" + parts[i].type +
                          "  id=" + parts[i].id +
                          "  offset=" + FormatBytes(parts[i].offsetBytes) +
                          "  size=" + FormatBytes(parts[i].sizeBytes));
                }
            }
            return 0;
        }

        if (sub == "partition")
        {
            if (layout.empty()) { Err("partition 需要 --layout"); return 2; }
            LayoutModel model;
            if (!LoadLayout(layout, model, error))
            {
                Err("读布局失败：" + error);
                return 1;
            }
            std::uint64_t diskBytes = size;
            if (diskBytes == 0)
            {
                VhdInfo info;
                if (VhdQuery(wideFile, info, error)) diskBytes = info.virtualBytes;
            }
            std::vector<VhdPartition> parts;
            if (physicalView)
            {
                // 物理盘面视图（与真机 Flash.vhdx 一致）：只放 BS_EFIESP + OSPool。
                // MainOS/BSP/OSData/Data 等都在 OSPool 池内的 space 里，不能写进物理 GPT。
                const std::uint64_t mb = 1024ull * 1024ull;
                const std::uint64_t efiespOffset = 2 * mb;
                const std::uint64_t efiespSize = 260 * mb;
                VhdPartition boot;
                boot.name = "BS_EFIESP";
                boot.type = "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}";
                boot.offsetBytes = efiespOffset;
                boot.sizeBytes = efiespSize;
                parts.push_back(boot);
                VhdPartition pool;
                pool.name = "OSPool";
                pool.type = "{e75caf8f-f680-4cee-afa3-b001e56efc2d}";
                pool.offsetBytes = efiespOffset + efiespSize;
                // 末尾要给备份 GPT 留位（至少 33 扇区），否则 SET_DRIVE_LAYOUT 报 87。
                const std::uint64_t tail = 1 * mb;
                pool.sizeBytes = diskBytes > (pool.offsetBytes + tail) ? (diskBytes - pool.offsetBytes - tail) : 0;
                parts.push_back(pool);
            }
            else if (!BuildPartitionsFromLayout(model, diskBytes, parts, error))
            {
                Err("排分区失败：" + error);
                return 1;
            }
            std::uint64_t end = 0;
            OutLn("分区计划（共 " + NumText(parts.size()) + " 个）：");
            for (std::size_t i = 0; i < parts.size(); ++i)
            {
                OutLn("  [" + NumText(i + 1) + "] " + parts[i].name +
                      "  type=" + parts[i].type +
                      "  offset=" + FormatBytes(parts[i].offsetBytes) +
                      "  size=" + FormatBytes(parts[i].sizeBytes));
                end = parts[i].offsetBytes + parts[i].sizeBytes;
            }
            OutLn("合计占用：" + FormatBytes(end) + "（虚拟盘 " + FormatBytes(diskBytes) + "）");
            if (!apply)
            {
                OutLn("（演练）默认不写盘：确认无误后加 --apply。");
                return 0;
            }

            bool attachedHere = false;
            if (device.empty())
            {
                if (!VhdAttach(wideFile, false, device, error))
                {
                    Err("挂载失败：" + error);
                    return 1;
                }
                attachedHere = true;
                OutLn("已挂载：" + device);
            }
            if (!VhdWriteGpt(device, diskId, parts, sectorSize, error))
            {
                Err("写 GPT 失败：" + error);
                if (attachedHere && !keepAttached) (void)VhdDetach(wideFile, error);
                return 1;
            }
            OutLn("已写 GPT：" + device);

            std::vector<VhdPartition> readBack;
            std::string readId;
            std::uint32_t readSector = sectorSize;
            if (VhdReadGpt(device, readId, readBack, readSector, error))
            {
                OutLn("读回：磁盘 GUID " + readId + "，分区 " + NumText(readBack.size()));
                for (std::size_t i = 0; i < readBack.size(); ++i)
                {
                    OutLn("  [" + NumText(i + 1) + "] " + readBack[i].name +
                          "  type=" + readBack[i].type +
                          "  offset=" + FormatBytes(readBack[i].offsetBytes) +
                          "  size=" + FormatBytes(readBack[i].sizeBytes));
                }
            }
            else
            {
                OutLn("读回失败（不影响写盘）：" + error);
            }
            if (attachedHere && !keepAttached)
            {
                if (VhdDetach(wideFile, error)) OutLn("已卸载：" + file);
                else Err("卸载失败：" + error);
            }
            return 0;
        }

        Err("未知子命令：" + args[2] + "（可用 create / attach / detach / info / partition）");
        return 2;
    }

    int CmdPoolBuild(const std::vector<std::string>& args)
    {
        std::string file, layoutPath, directory, diskId, poolName;
        unsigned int version = 24;
        bool dry = false;
        for (std::size_t i = 3; i < args.size(); ++i)
        {
            const std::string& a = args[i];
            auto next = [&](std::string& out) -> bool
            {
                if (i + 1 >= args.size()) return false;
                out = args[++i];
                return true;
            };
            if (a == "--vhd") { if (!next(file)) { Err("--vhd 缺少值"); return 2; } }
            else if (a == "--layout") { if (!next(layoutPath)) { Err("--layout 缺少值"); return 2; } }
            else if (a == "--pool") { if (!next(poolName)) { Err("--pool missing value"); return 2; } }
            else if (a == "--disk-id") { if (!next(diskId)) { Err("--disk-id 缺少值"); return 2; } }
            else if (a == "--dir") { if (!next(directory)) { Err("--dir 缺少值"); return 2; } }
            else if (a == "--version")
            {
                std::string v;
                if (!next(v)) { Err("--version 缺少值"); return 2; }
                version = static_cast<unsigned int>(std::strtoul(v.c_str(), nullptr, 0));
            }
            else if (a == "--dry") dry = true;
            else if (!a.empty() && a[0] == '-') { Err("未知参数：" + a); return 2; }
            else { Err("多余的位置参数：" + a); return 2; }
        }
        if (file.empty() || layoutPath.empty())
        {
            Err("pool build 需要 --vhd <虚拟磁盘> --layout <布局 XML>");
            return 2;
        }
        LayoutModel model;
        std::string error;
        if (!LoadLayout(layoutPath, model, error)) { Err("读布局失败：" + error); return 1; }
        const std::uint64_t sector = model.sectorSize != 0 ? model.sectorSize : 512;
        const std::uint64_t defaultAlignment = model.defaultAlignment != 0 ? model.defaultAlignment : (1024ull * 1024);
        auto align = [](std::uint64_t v, std::uint64_t a) { return a == 0 ? v : (v + a - 1) / a * a; };

        // 物理 GPT 只写非池分区：布局里名为 OSPool 的那条是“池占位”语义标记，
        // 真正写入时由 ImageStorageService 自己追加（预置会 0xC0380013 / 0x800700B7）。
        std::vector<VhdPartition> parts;
        std::uint64_t cursor = align(1024ull * 1024, defaultAlignment);
        for (const LayoutStore& store : model.stores)
        {
            for (const LayoutPartition& p : store.partitions)
            {
                if (IsPoolPartition(p)) continue;
                std::uint64_t size = 0;
                if (p.hasTotalBytes && p.totalBytes != 0) size = p.totalBytes;
                else if (p.totalSectors != 0) size = p.totalSectors * sector;
                if (size == 0) continue;
                size = align(size, sector);
                std::uint64_t alignment = defaultAlignment;
                std::uint64_t custom = 0;
                if (!p.byteAlignment.empty() && ParseHexOrDec(p.byteAlignment, custom) && custom != 0) alignment = custom;
                VhdPartition vp;
                vp.name = p.name;
                vp.type = p.type;
                vp.id = p.id;
                vp.offsetBytes = align(cursor, alignment);
                vp.sizeBytes = size;
                parts.push_back(vp);
                cursor = vp.offsetBytes + size;
            }
        }
        if (parts.empty()) { Err("布局的物理 <Stores> 里没有可写分区"); return 1; }

        std::string poolError;
        const LayoutPool* poolGroup = SelectLayoutPool(model, poolName, poolError);
        if (poolGroup == nullptr && !poolError.empty()) { Err(poolError); return 1; }
        const std::wstring widePool = LayoutPoolName(poolGroup);
        const unsigned int poolVersion = LayoutPoolVersion(poolGroup, version);
        const std::vector<LayoutStore>& spaceStores = poolGroup != nullptr ? poolGroup->stores : model.pools;
        std::vector<PoolSpaceSpec> spaces;
        for (const LayoutStore& store : spaceStores)
        {
            const std::uint64_t storeSector = StoreSectorBytes(model, store);
            unsigned long long spaceSize = 0;
            if (store.hasSizeBytes && store.sizeInBytes != 0) spaceSize = store.sizeInBytes;
            else if (store.sizeInSectors != 0) spaceSize = store.sizeInSectors * storeSector;
            if (spaceSize == 0 || store.storeType.empty()) continue;
            PoolSpaceSpec spec;
            spec.name = Utf8ToWide(store.storeType);
            spec.sizeBytes = spaceSize;
            spec.sectorSize = static_cast<unsigned int>(storeSector);
            spaces.push_back(spec);
        }

        OutLn("物理分区（写入 GPT）：" + NumText(parts.size()) + " 个");
        for (const VhdPartition& vp : parts)
        {
            OutLn("  " + vp.name + "  type=" + vp.type + "  offset=" + FormatBytes(vp.offsetBytes) +
                  "  size=" + FormatBytes(vp.sizeBytes));
        }
        OutLn("存储池 space（thin，名字取 StoreType）：" + NumText(spaces.size()) + " 个");
        for (const PoolSpaceSpec& spec : spaces)
        {
            OutLn("  " + std::string(spec.name.begin(), spec.name.end()) + "  " + FormatBytes(spec.sizeBytes));
        }
        if (dry)
        {
            OutLn("（演练）未碰磁盘；确认后去掉 --dry。");
            return 0;
        }

        const std::wstring wideFile = Utf8ToWide(file);
        std::string device;
        if (!VhdAttach(wideFile, false, device, error)) { Err("挂载失败：" + error); return 1; }
        if (!VhdEnsureVirtualDisk(device, error))
        {
            std::string detachError;
            VhdDetach(wideFile, detachError);
            Err("拒绝操作：" + error);
            return 1;
        }
        if (!VhdWriteGpt(device, diskId, parts, static_cast<std::uint32_t>(sector), error))
        {
            std::string detachError;
            VhdDetach(wideFile, detachError);
            Err("写 GPT 失败：" + error);
            return 1;
        }
        OutLn("已写物理 GPT：" + device);
        const std::wstring wideDevice(device.begin(), device.end());
        std::vector<std::string> created;
        const bool built = PoolBuild(wideDevice, widePool, poolVersion, Utf8ToWide(directory), spaces, created, error);
        std::string detachError;
        const bool detached = VhdDetach(wideFile, detachError);
        for (const std::string& name : created) OutLn("  space 已建：" + name);
        if (!detached) Err("卸载虚拟磁盘失败：" + detachError);
        if (!built) { Err("建池失败：" + error); return 1; }
        OutLn("建池完成：" + file + "（space " + NumText(created.size()) + " 个）");
        return 0;
    }

    int CmdPoolClose(const std::vector<std::string>& args)
    {
        std::string file, directory;
        bool keepSpaces = false;
        for (std::size_t i = 3; i < args.size(); ++i)
        {
            const std::string& a = args[i];
            auto next = [&](std::string& out) -> bool
            {
                if (i + 1 >= args.size()) return false;
                out = args[++i];
                return true;
            };
            if (a == "--vhd") { if (!next(file)) { Err("--vhd 缺少值"); return 2; } }
            else if (a == "--dir") { if (!next(directory)) { Err("--dir 缺少值"); return 2; } }
            else if (a == "--keep-spaces") keepSpaces = true;
            else if (!a.empty() && a[0] == '-') { Err("未知参数：" + a); return 2; }
            else { Err("多余的位置参数：" + a); return 2; }
        }
        if (file.empty())
        {
            Err("pool close 需要 --vhd <虚拟磁盘>");
            return 2;
        }
        std::vector<std::string> report;
        std::string error;
        const bool ok = PoolClose(Utf8ToWide(file), Utf8ToWide(directory), !keepSpaces, report, error);
        for (const std::string& line : report) OutLn(line);
        if (!ok) { Err("关闭池失败：" + error); return 1; }
        return 0;
    }

    int CmdPoolPrepare(const std::vector<std::string>& args)
    {
        std::string file, layoutPath, directory, onlySpace, poolName;
        unsigned int version = 24;
        bool dry = false;
        bool doFormat = false;
        for (std::size_t i = 3; i < args.size(); ++i)
        {
            const std::string& a = args[i];
            auto next = [&](std::string& out) -> bool
            {
                if (i + 1 >= args.size()) return false;
                out = args[++i];
                return true;
            };
            if (a == "--vhd") { if (!next(file)) { Err("--vhd 缺少值"); return 2; } }
            else if (a == "--layout") { if (!next(layoutPath)) { Err("--layout 缺少值"); return 2; } }
            else if (a == "--dir") { if (!next(directory)) { Err("--dir 缺少值"); return 2; } }
            else if (a == "--pool") { if (!next(poolName)) { Err("--pool missing value"); return 2; } }
            else if (a == "--space") { if (!next(onlySpace)) { Err("--space 缺少值"); return 2; } }
            else if (a == "--version")
            {
                std::string v;
                if (!next(v)) { Err("--version 缺少值"); return 2; }
                version = static_cast<unsigned int>(std::strtoul(v.c_str(), nullptr, 0));
            }
            else if (a == "--format") doFormat = true;
            else if (a == "--dry") dry = true;
            else if (!a.empty() && a[0] == '-') { Err("未知参数：" + a); return 2; }
            else { Err("多余的位置参数：" + a); return 2; }
        }
        if ((file.empty() && !dry) || layoutPath.empty())
        {
            Err("pool prepare 需要 --vhd <虚拟磁盘> --layout <布局 XML>");
            return 2;
        }
        LayoutModel model;
        std::string error;
        if (!LoadLayout(layoutPath, model, error)) { Err("读布局失败：" + error); return 1; }
        const std::uint64_t sector = model.sectorSize != 0 ? model.sectorSize : 512;
        const std::uint64_t defaultAlignment = model.defaultAlignment != 0 ? model.defaultAlignment : (1024ull * 1024);
        auto align = [](std::uint64_t v, std::uint64_t a) { return a == 0 ? v : (v + a - 1) / a * a; };

        std::string poolError;
        const LayoutPool* poolGroup = SelectLayoutPool(model, poolName, poolError);
        if (poolGroup == nullptr && !poolError.empty()) { Err(poolError); return 1; }
        const std::wstring widePool = LayoutPoolName(poolGroup);
        const unsigned int poolVersion = LayoutPoolVersion(poolGroup, version);
        const std::vector<LayoutStore>& spaceStores = poolGroup != nullptr ? poolGroup->stores : model.pools;
        std::vector<SpacePrepareSpec> spaces;
        for (const LayoutStore& store : spaceStores)
        {
            if (store.storeType.empty()) continue;
            if (!onlySpace.empty() && !EqualsNoCase(store.storeType, onlySpace)) continue;
            const std::uint64_t storeSector = StoreSectorBytes(model, store);
            unsigned long long spaceSize = 0;
            if (store.hasSizeBytes && store.sizeInBytes != 0) spaceSize = store.sizeInBytes;
            else if (store.sizeInSectors != 0) spaceSize = store.sizeInSectors * storeSector;
            if (spaceSize == 0) continue;
            SpacePrepareSpec spec;
            spec.name = Utf8ToWide(store.storeType);
            spec.sectorSize = static_cast<unsigned int>(storeSector);
            std::uint64_t cursor = align(1024ull * 1024, defaultAlignment);
            for (const LayoutPartition& p : store.partitions)
            {
                std::uint64_t alignment = defaultAlignment;
                std::uint64_t custom = 0;
                if (!p.byteAlignment.empty() && ParseHexOrDec(p.byteAlignment, custom) && custom != 0) alignment = custom;
                const std::uint64_t offset = align(cursor, alignment);
                std::uint64_t size = 0;
                if (p.useAllSpace)
                {
                    // space 内部署 GPT 时末尾要给备份 GPT 留位，否则 SET_DRIVE_LAYOUT 返回 87
                    const std::uint64_t tailReserve = 1024ull * 1024;
                    size = spaceSize > offset + tailReserve ? spaceSize - offset - tailReserve : 0;
                }
                else if (p.hasTotalBytes && p.totalBytes != 0)
                {
                    size = p.totalBytes;
                }
                else if (p.totalSectors != 0)
                {
                    size = p.totalSectors * sector;
                }
                if (size == 0) continue;
                size = align(size, sector);
                SpacePartitionSpec part;
                part.name = Utf8ToWide(p.name);
                part.typeGuid = Utf8ToWide(p.type);
                part.partitionGuid = Utf8ToWide(p.id);
                part.offsetBytes = offset;
                part.sizeBytes = size;
                part.fileSystem = Utf8ToWide(p.fileSystem);
                unsigned long long cluster = 0;
                if (!p.clusterSize.empty() && ParseHexOrDec(p.clusterSize, cluster)) part.clusterSize = static_cast<unsigned int>(cluster);
                spec.partitions.push_back(part);
                cursor = offset + size;
            }
            spaces.push_back(spec);
        }
        if (spaces.empty()) { Err("布局的 <StoragePool> 里没有可处理的 space"); return 1; }

        OutLn("池内分区计划：" + NumText(spaces.size()) + " 个 space");
        for (const SpacePrepareSpec& spec : spaces)
        {
            OutLn("  space " + std::string(spec.name.begin(), spec.name.end()));
            for (const SpacePartitionSpec& p : spec.partitions)
            {
                OutLn("    " + std::string(p.name.begin(), p.name.end()) + "  type=" +
                      std::string(p.typeGuid.begin(), p.typeGuid.end()) + "  offset=" + FormatBytes(p.offsetBytes) +
                      "  size=" + FormatBytes(p.sizeBytes) + "  fs=" + std::string(p.fileSystem.begin(), p.fileSystem.end()));
            }
        }
        if (dry)
        {
            OutLn("（演练）未碰磁盘；确认后去掉 --dry。");
            return 0;
        }

        const std::wstring wideFile = Utf8ToWide(file);
        VhdInfo vhdInfo;
        if (!VhdQuery(wideFile, vhdInfo, error))
        {
            Err("拒绝操作：不是可识别的虚拟磁盘（" + file + "）：" + error);
            return 1;
        }
        std::vector<std::string> report;
        const bool prepared = PoolPrepareSpaces(wideFile, widePool, poolVersion, Utf8ToWide(directory), spaces,
                                                doFormat, report, error);
        for (const std::string& line : report) OutLn("  " + line);
        if (!prepared) { Err("池内分区/格式化失败：" + error); return 1; }
        OutLn("池内准备完成：" + file);
        return 0;
    }


    int CmdPool(const std::vector<std::string>& args)
    {
        const std::string sub = args.size() > 2 ? args[2] : std::string();
        if (sub == "help" || sub == "--help" || sub.empty())
        {
            OutLn("用法：");
            OutLn("  pool probe [--all]            探测 ImageStorageService.dll 的导出函数（不碰磁盘；--all 列全部）");
            OutLn("  pool plan --layout <xml>      打印存储池 / space 计划（只读 XML）");
            OutLn("  pool create --vhd <vhdx> [--disk <设备>] [--name OSPool] [--version 24|26] [--dir <临时目录>] [--space <名> --size 1G]");
            OutLn("                                在虚拟磁盘上真建池（服务句柄 → 池 → 可选 space），仅限虚拟盘");
            OutLn("  pool build --vhd <vhdx> --layout <xml> [--version 24|26] [--disk-id <guid>] [--dir <临时目录>] [--dry]");
            OutLn("                                写物理 GPT（只含 BS_EFIESP）→ 建池 → 按布局 <StoragePool>/<Stores> 逐 store 建 space");
            OutLn("  pool prepare --vhd <vhdx> --layout <xml> [--space <名>] [--format] [--dir <临时目录>] [--dry]");
            OutLn("  pool close --vhd <vhdx> [--dir <临时目录>] [--keep-spaces]");
            OutLn("                                打开已有池 → 每个 space 内写 GPT（按布局 ByteAlignment/DefaultPartitionByteAlignment）");
            OutLn("                                加 --format 才按布局 FileSystem/ClusterSize 格式化分区");
            return sub.empty() ? 2 : 0;
        }
        if (sub == "build") return CmdPoolBuild(args);
        if (sub == "prepare") return CmdPoolPrepare(args);
        if (sub == "close") return CmdPoolClose(args);
        if (sub == "probe")
        {
            std::string dll, error;
            std::vector<std::string> found, missing;
            if (!PoolProbe(dll, found, missing, error))
            {
                Err("探测失败：" + error);
                return 1;
            }
            OutLn("ImageStorageService.dll：" + dll);
            OutLn("导出命中 " + NumText(found.size()) + " 个，缺失 " + NumText(missing.size()) + " 个");
            for (const std::string& name : found) OutLn("  + " + name);
            for (const std::string& name : missing) OutLn("  - " + name);

            bool dumpAll = false;
            for (std::size_t i = 3; i < args.size(); ++i) if (args[i] == "--all") dumpAll = true;
            if (dumpAll)
            {
                std::vector<std::string> names;
                std::string exportError;
                if (!PoolExports(dll, names, exportError)) { Err("列导出失败：" + exportError); return 1; }
                std::sort(names.begin(), names.end());
                OutLn("全部导出 " + NumText(names.size()) + " 个：");
                for (const std::string& name : names) OutLn("  " + name);
            }
            return 0;
        }
                if (sub == "plan")
        {
            std::string layout, error;
            for (std::size_t i = 3; i < args.size(); ++i)
            {
                if (args[i] == "--layout" && i + 1 < args.size()) layout = args[++i];
            }
            if (layout.empty()) { Err("pool plan 需要 --layout <DeviceLayout.xml>"); return 2; }
            std::unique_ptr<XmlDoc> doc = XmlParseFile(Utf8ToWide(layout), error);
            if (!doc) { Err("读布局失败：" + error); return 1; }
            auto kidsOf = [](const XmlNode* node, const char* local) {
                std::vector<const XmlNode*> out;
                if (node != nullptr)
                {
                    for (const std::unique_ptr<XmlNode>& kid : node->kids)
                    {
                        if (kid->local == local) out.push_back(kid.get());
                    }
                }
                return out;
            };
            const XmlNode* rootNode = doc->Root() == nullptr ? nullptr : doc->Root();
            std::vector<const XmlNode*> pools = kidsOf(rootNode, "StoragePools");
            if (pools.empty()) { Err("布局里没有 <StoragePools>（v1 布局无存储池）"); return 1; }
            int printed = 0;
            unsigned long long rootSector = 512;
            const std::string rootSectorText = TextOf(rootNode, "SectorSize");
            if (!rootSectorText.empty())
            {
                unsigned long long v = std::strtoull(rootSectorText.c_str(), nullptr, 0);
                if (v != 0) rootSector = v;
            }
            for (const XmlNode* poolNode : kidsOf(pools[0], "StoragePool"))
            {
                const std::string name = TextOf(poolNode, "Name");
                std::vector<const XmlNode*> stores = kidsOf(poolNode, "Stores");
                OutLn("存储池：" + (name.empty() ? std::string("(无名)") : name));
                OutLn("版本：低于 Manganese 用 24（19H1），否则 >= Manganese 用 26");
                unsigned long long total = 0;
                int index = 0;
                for (const XmlNode* store : stores.empty() ? std::vector<const XmlNode*>() : kidsOf(stores[0], "Store"))
                {
                    ++index;
                    const std::string type = TextOf(store, "StoreType");
                    const std::string sid = TextOf(store, "Id");
                    const std::string storeSectorText = TextOf(store, "SectorSize");
                    unsigned long long storeSector = rootSector;
                    if (!storeSectorText.empty())
                    {
                        unsigned long long v = std::strtoull(storeSectorText.c_str(), nullptr, 0);
                        if (v != 0) storeSector = v;
                    }
                    unsigned long long bytes = 0;
                    const std::string sizeBytesText = TextOf(store, "SizeInBytes");
                    if (!sizeBytesText.empty()) bytes = std::strtoull(sizeBytesText.c_str(), nullptr, 0);
                    if (bytes == 0)
                    {
                        unsigned long long sectors = 0;
                        const std::string sectorsText = TextOf(store, "SizeInSectors");
                        if (!sectorsText.empty()) sectors = std::strtoull(sectorsText.c_str(), nullptr, 0);
                        bytes = sectors * storeSector;
                    }
                    std::vector<const XmlNode*> parts = kidsOf(store, "Partitions");
                    std::string partNames;
                    for (const XmlNode* part : parts.empty() ? std::vector<const XmlNode*>() : kidsOf(parts[0], "Partition"))
                    {
                        if (!partNames.empty()) partNames += ",";
                        partNames += TextOf(part, "Name");
                    }
                    total += bytes;
                    OutLn("  [" + NumText(static_cast<unsigned long long>(index)) + "] " + type + "  " + FormatBytes(bytes) +
                          "  thin=1  id=" + (sid.empty() ? std::string("(无)") : sid) + "  分区=" + (partNames.empty() ? std::string("(空)") : partNames));
                }
                OutLn("合计：" + FormatBytes(total) + "；另加 IU_RESERVE_DISK（thin，预留）");
                ++printed;
            }
            if (printed == 0) { Err("布局的 <StoragePool> 为空"); return 1; }
            return 0;
        }
        if (sub == "create")
        {
            std::wstring vhdPath;
            std::wstring poolName = L"OSPool";
            std::wstring spaceName;
            std::wstring directory;
            unsigned long long spaceSize = 0;
            unsigned int version = 24;
            std::string deviceArg;
            for (std::size_t i = 3; i < args.size(); ++i)
            {
                const std::string& a = args[i];
                if (a == "--vhd" && i + 1 < args.size()) vhdPath = Utf8ToWide(args[++i]);
                else if (a == "--disk" && i + 1 < args.size()) deviceArg = args[++i];
                else if (a == "--name" && i + 1 < args.size()) poolName = Utf8ToWide(args[++i]);
                else if (a == "--space" && i + 1 < args.size()) spaceName = Utf8ToWide(args[++i]);
                else if (a == "--dir" && i + 1 < args.size()) directory = Utf8ToWide(args[++i]);
                else if (a == "--version" && i + 1 < args.size()) version = static_cast<unsigned int>(std::strtoul(args[++i].c_str(), nullptr, 0));
                else if (a == "--size" && i + 1 < args.size())
                {
                    std::string text = args[++i];
                    unsigned long long mult = 1;
                    const char last = text.empty() ? 0 : text.back();
                    if (last == 'G' || last == 'g') { mult = 1024ull * 1024ull * 1024ull; text.pop_back(); }
                    else if (last == 'M' || last == 'm') { mult = 1024ull * 1024ull; text.pop_back(); }
                    else if (last == 'K' || last == 'k') { mult = 1024ull; text.pop_back(); }
                    spaceSize = std::strtoull(text.c_str(), nullptr, 0) * mult;
                }
            }
            std::string device;
            std::string error;
            const bool fromDisk = !deviceArg.empty();
            if (fromDisk) device = deviceArg;
            else if (!vhdPath.empty())
            {
                if (!VhdAttach(vhdPath, false, device, error)) { Err("挂载虚拟磁盘失败：" + error); return 1; }
            }
            if (device.empty())
            {
                Err("pool create 需要 --vhd <虚拟磁盘> 或 --disk <设备路径>");
                return 2;
            }
            if (!VhdEnsureVirtualDisk(device, error))
            {
                std::string detachError;
                if (!fromDisk) VhdDetach(vhdPath, detachError);
                Err("拒绝操作：" + error);
                return 1;
            }
            PoolCreateOptions options;
            options.devicePath = Utf8ToWide(device);
            options.poolName = poolName;
            options.version = version;
            options.directory = directory;
            options.spaceName = spaceName;
            options.spaceSize = spaceSize;
            std::string detail;
            const bool ok = PoolCreateSandbox(options, detail, error);
            std::string detachError;
            const bool detached = fromDisk ? true : VhdDetach(vhdPath, detachError);
            if (!detail.empty()) Out(detail);
            if (!detached) Err("卸载虚拟磁盘失败：" + detachError);
            if (!ok) { Err("建池失败：" + error); return 1; }
            OutLn("建池成功：" + (fromDisk ? device : std::string(vhdPath.begin(), vhdPath.end())) + "（池 " +
                  std::string(poolName.begin(), poolName.end()) + "，版本 " + NumText(static_cast<unsigned long long>(version)) + "）");
            return 0;
        }
        Err("未知 pool 子命令：" + sub);
        return 2;
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
    if (command == "devicedb")
    {
        return CmdDeviceDb(args);
    }
    if (command == "vhd") return CmdVhd(args);
    if (command == "disk") return CmdDisk(args);
    if (command == "pool") return CmdPool(args);
    if (command == "layout")
    {
        return CmdLayout(args);
    }
    Err("未知命令：" + command);
    PrintUsage();
    return 2;
}
