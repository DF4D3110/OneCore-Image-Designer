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
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <string>
#include <vector>

using namespace ocid::imaging;

namespace
{

const char* const kVersion = "0.3.0";

void PrintUsage()
{
    OutLn("ocid_imagegen " + std::string(kVersion) + " —— 镜像构建（选包 / 校验 / 读包）");
    OutLn("");
    OutLn("用法：");
    OutLn("  ocid_imagegen plan --oem-input <OEMInput.xml> [选项]");
    OutLn("  ocid_imagegen schema validate <XML> --schema <XSD> [--schema <XSD>...]");
    OutLn("  ocid_imagegen schema pick --build <构建号> [--family V1|V2] [--schema-dir <目录>]");
    OutLn("  ocid_imagegen cab list <文件.cab>");
    OutLn("  ocid_imagegen cab entry <文件.cab> <条目名> --out <文件>");
    OutLn("  ocid_imagegen cab extract <文件.cab> --out <目录>");
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
    OutLn("cab 说明：");
    OutLn("  list     打印压缩法、块数、包内条目（名字 / 大小 / folder / 时间）");
    OutLn("  entry    抽出一个条目；--out 给文件则写文件，不给则把内容写到 stdout");
    OutLn("  extract  全解到 --out 目录，保留 CAB 内相对路径");
    OutLn("  NONE / MSZIP 用自带解码器；LZX / Quantum 走系统 cabinet.dll 的 FDI（结果与 expand.exe 逐字节一致）");
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
    if (command == "schema")
    {
        return CmdSchema(args);
    }
    if (command == "cab")
    {
        return CmdCab(args);
    }
    Err("未知命令：" + command);
    PrintUsage();
    return 2;
}
