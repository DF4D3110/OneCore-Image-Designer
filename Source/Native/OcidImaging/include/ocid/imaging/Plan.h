// OCID Imaging —— 选包（Plan）
//
// 【职责】把 OEMInput.xml + 一组 FeatureManifest(*.xml) 变成"要装进镜像的包清单"。
//         这一步不碰包内容、不建盘、不写注册表，只做决策，所以可以单独跑、单独比对。
//
// 【规则来源】对 10.0.20274.1000 的参照产物（Flash.UpdateInput.xml 233 条 /
//             Flash.PackageList.xml 181 条）逐条反推，已做到 181/181 命中、0 多选。
//             OEM Input 里 8 类 section 的判据见 SelectPackage()。
//
// 【约定】section 的处理顺序 = FM 文档里出现的顺序（imageapp 就是这么走的）。
//         展开顺序 = 语言在外、架构在内（`Language="*"` × `Wow="*"` 时先出全部 neutral，
//         再出各语言），这一条也是实测出来的，写错会让 cab 名顺序整体错位。
#pragma once

#include <cstddef>
#include <string>
#include <vector>

namespace ocid
{
namespace imaging
{

#if defined(OCID_PLAN_BUILD)
#define OCID_PLAN_API __declspec(dllexport)
#else
#define OCID_PLAN_API __declspec(dllimport)
#endif

// ── 包在 FM 里的来源分类 ────────────────────────────────────────────────
enum class PackageSection
{
    Base,             // <BasePackages>：无条件全收
    Feature,          // <Features>：FeatureIDs 与 OEMInput.Features 有交集
    DevicePlatform,   // <OEMDevicePlatformPackages>：Device 命中 OEMInput.Device
    DeviceSpecific,   // <DeviceSpecificPackages>：同上
    DeviceLayout,     // <DeviceLayoutPackages>：SOC 命中 OEMInput.DeviceLayoutType
    LanguagePack,     // <LanguagePacks>：Language 命中 OEMInput.Languages
    UserInstallable,  // <UserInstallableOptionalFeatures>：FeatureName 命中
    Release,          // <ReleasePackages>：ReleaseType 命中 OEMInput.ReleaseType
};

OCID_PLAN_API const char* SectionName(PackageSection section);

// ── 一个被选中的包 ──────────────────────────────────────────────────────
struct PackageRef
{
    std::string cab;              // 展开后的 cab 文件名（含 ~31bf3856ad364e35~）
    std::string id;               // FM 里的 ID 属性
    std::string fmId;             // 来源 FM 的 ID 属性
    std::string fmFile;           // 来源 FM 文件（文件名，不含目录）
    PackageSection section = PackageSection::Base;
    std::string language;         // 空 = neutral
    std::string arch;             // AMD64 / wow64 / arm64 ...
    bool featureIdentifier = false;   // FeatureIdentifierPackage="true"
};

// ── 输入 ────────────────────────────────────────────────────────────────
struct PlanInput
{
    std::string oemInputPath;                // OEMInput.xml 路径（必需）
    std::string fmDir;                       // FM 目录；与 fmFiles 二选一
    std::vector<std::string> fmFiles;         // 显式 FM 文件（按给定顺序处理）
    std::string arch = "amd64";              // 目标架构：amd64 / arm64 / x86
    bool keepDuplicates = false;             // true = 保留同一 cab 的多次出现（对照 imageapp）
};

// ── 统计与结果 ──────────────────────────────────────────────────────────
struct PlanStats
{
    std::size_t fmCount = 0;        // 处理的 FM 数
    std::size_t examined = 0;       // 看过的 PackageFile 条目数
    std::size_t selected = 0;       // 选中的条目数（展开前）
    std::size_t expanded = 0;       // 展开后的 cab 数（去重前）
    std::size_t unique = 0;         // 去重后（= PlanResult.packages.size()）
    std::size_t duplicates = 0;     // 被去掉的重复条数
    std::size_t skippedFilter = 0;  // 判据不命中
    std::size_t skippedArch = 0;    // 架构不匹配
};

struct PlanResult
{
    std::vector<PackageRef> packages;       // 有序：FM 顺序 × 文档顺序
    PlanStats stats;
    std::vector<std::string> warnings;
};

// ── OEMInput 读出来的关键字段（也供 imagegen 打印/校验用） ──────────────
struct OemInput
{
    std::string path;
    std::string sv;
    std::string product;
    std::string device;
    std::string deviceLayoutType;
    std::string releaseType;
    std::string buildType;
    std::vector<std::string> languages;
    std::vector<std::string> features;               // <Features>/<Feature>
    std::vector<std::string> userInstallableFeatures;
    std::vector<std::string> additionalFms;          // 只保留文件名
    std::vector<std::string> appxOptionalPackages;
};

OCID_PLAN_API bool LoadOemInput(const std::string& path, OemInput& oem, std::string& err);

// 选包：LoadOemInput → 枚举/读取 FM → 逐 section 判定 → 展开 → 去重。
OCID_PLAN_API bool BuildPlan(const PlanInput& input, PlanResult& result, std::string& err);

// 写 plan.xml（本工具自己的中间格式，供后续 Cab / Vhd / Finalize 阶段消费）。
OCID_PLAN_API bool WritePlanXml(const PlanResult& plan, const std::string& path, std::string& err);

// 写 cab 名清单（每行一个，便于和参照产物做 diff）。
OCID_PLAN_API bool WriteCabList(const PlanResult& plan, const std::string& path, std::string& err);

} // namespace imaging
} // namespace ocid
