// OCID Imaging —— AppX 落地（ocid_appx.wcos.dll）
//
// 【职责】把 OEMInput + FeatureManifest 里的 <AppX><PackageFile .../> 变成"要装进 PreInstalled
//         的 WindowsApps 的 appx 包清单"，然后分两步真正落进镜像：
//           stage  : .appxbundle/.appx → makeappx 解包 → <staging>\appx\<PackageFullName>
//           commit : 复制进 <PreInstalled>\WindowsApps，做 WOF 压缩，写 AppxAllUserStore.dat，
//                    复制 license 到 <PreInstalled>\AppData\<PFN>.xml
//
// 【为什么要单独一个 DLL】AppX 的解包/注册逻辑是 ADK 里 i386 的 .NET 程序集
//   (appximaging.dll / appxcommon.dll)，x64 的 ocid_imagegen 装不进去，只能起 32 位子进程
//   (OcidAppX.exe) 去调；本 DLL 只负责"算清单 + 拼命令行 + 收错误"，出错时一眼就能看出
//   是"清单算错"还是"helper 跑挂"，与造盘/落包完全隔离。
//
// 【与 ImageApp 的对应关系】
//   Imaging.cs:2003-2035 —— CommitImage() 里对 _storageManager.GetPartitionPath(PREINSTALLED)
//   取 PreInstalled 分区路径，staging 目录 = <PreInstalled>\Temp，AppX stage+commit 与
//   UpdateDLL 的 ExecuteUpdate **并发**；本工具按顺序做（先 UpdateDLL 再 AppX），结果一致、
//   定位更容易。
#pragma once

#include <string>
#include <vector>

namespace ocid
{
namespace imaging
{

#if defined(OCID_APPX_BUILD)
#define OCID_APPX_API __declspec(dllexport)
#else
#define OCID_APPX_API __declspec(dllimport)
#endif

// ── 从 FM 里读到的一个 AppX 包 ──────────────────────────────────────────
struct AppxPackageSpec
{
    std::string id;            // FM 里的 ID（包族名 PFN，如 Microsoft.WindowsAlarms_8wekyb3d8bbwe）
    std::string packagePath;   // .appx / .appxbundle 的绝对路径
    std::string name;          // 文件名
    std::string licensePath;   // license xml；framework 包为空
    bool optional = false;     // Optional="true" 的包只有 OEMInput 里点名才收
    std::string cpuType;       // FM 的 CPUType 属性（"*"/"arm64"/"amd64"），空按 "*" 处理
    bool preferStub = false;
    std::string region;
    std::string fmFile;        // 来源 FM（排查用）
};

// ── 输入 ────────────────────────────────────────────────────────────────
struct AppxRequest
{
    std::string oemInputPath;               // OEMInput.xml（必需：语言列表 + AppXOptionalPackage）
    std::string fmDir;                      // FeatureManifest 目录；空则用 OEMInput 同目录
    std::string packagesRoot;               // 包根目录（FM 里 Path 的相对根 / $(mspackageroot)）
    std::string arch = "arm64";              // arm64 / amd64 / x86 → 传给 helper 的 CpuArch
    std::string helperPath;                 // OcidAppX.exe 的完整路径
    std::string stagingDir;                 // 暂存根（<PreInstalled>\Temp）
    std::string preInstalledPath;           // PreInstalled 分区卷根（\\?\Volume{...}\）
    std::string logPath;                    // helper 自己的日志文件（可选）
    std::vector<std::string> languages;      // 空则从 OEMInput 取
};

// ── 结果 ────────────────────────────────────────────────────────────────
struct AppxPlan
{
    std::vector<AppxPackageSpec> packages;
    std::size_t examined = 0;      // FM 里看过的 PackageFile 条数
    std::size_t skippedArch = 0;   // 架构不匹配
    std::size_t skippedOptional = 0;  // Optional 但 OEMInput 没点名
    std::size_t skippedMissing = 0;   // 文件不存在
    std::size_t duplicates = 0;    // 同一文件出现多次
    std::size_t frameworks = 0;    // license 为空（framework）的条数
};

// 算清单：LoadOemInput → 枚举 FM → 逐 <AppX>/<PackageFile> 判定 → 解析路径 → 去重。
OCID_APPX_API bool AppxCollect(const AppxRequest& request, AppxPlan& plan,
                               std::vector<std::string>& report, std::string& error);

// 把清单写成 helper 要的 TSV（列：id \\t packagePath \\t name \\t licensePath \\t optional \\t cpuType \\t preferStub \\t region）。
OCID_APPX_API bool AppxWriteList(const AppxPlan& plan, const std::wstring& tsvPath, std::string& error);

// 跑 helper：bool commit=false → stage；true → commit（要求 preInstalledPath 非空）。
OCID_APPX_API bool AppxRunHelper(const AppxRequest& request, const AppxPlan& plan, bool commit,
                                 std::vector<std::string>& report, std::string& error);

// 一步做完（stage + commit）；helper 缺失或清单为空时返回 false 并给中文原因。
OCID_APPX_API bool AppxInstall(const AppxRequest& request, AppxPlan& plan,
                               std::vector<std::string>& report, std::string& error);

// 把 arm64/amd64/x86 映射成 ADK CpuArch 名字（ARM64/AMD64/X86），无法识别返回空。
OCID_APPX_API std::string AppxArchName(const std::string& arch);

} // namespace imaging
} // namespace ocid
