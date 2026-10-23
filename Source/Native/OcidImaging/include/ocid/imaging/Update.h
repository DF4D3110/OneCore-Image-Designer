// OCID Imaging —— 落包（UpdateDLL 编排）
//
// 【职责】把"包清单 + 已建好盘/池/space 的镜像"交给微软的 UpdateDLL 做三阶段落包：
//         初始化 → 暂存（PrepareUpdate）→ 提交（ExecuteUpdate）。
//
// 【调用序列来源】反编译 imageapp 的 Imaging.StageImage / Imaging.CommitImage：
//   StageImage : CreateUpdateContext → Initialize(InitForInput) → SetAdditionalUpdateHistoryPath
//                → 每个 space 一次 AddImagingSpacesOverride → SetOverlapFileList
//                → SetAllDeviceLayoutPaths + SetDriveLetterConfigPath → PrepareUpdate
//   CommitImage: CreateUpdateContext → Initialize(InitForOutput) → SetAdditionalUpdateHistoryPath
//                → 每个 space 一次 AddImagingSpacesOverride
//                → SetAllDeviceLayoutPaths + SetDriveLetterConfigPath → ExecuteUpdate
//   所有函数都是 cdecl + Unicode；失败判据是 hr < 0（UpdateMain.FAILED）。
//
// 【precondition】镜像的盘/池/space 已经建好（disk build + pool build + pool prepare），
//                并且调用方已给出包根目录、ActionList、UpdateInput、UpdateOutput。
#pragma once

#include <string>
#include <vector>

namespace ocid
{
namespace imaging
{

#if defined(OCID_UPDATE_BUILD)
#define OCID_UPDATE_API __declspec(dllexport)
#else
#define OCID_UPDATE_API __declspec(dllimport)
#endif

struct UpdateRequest
{
    std::wstring imagePath;             // 基座虚拟盘（如 Default.vhdx）
    std::wstring poolName = L"OSPool";
    unsigned int poolVersion = 24;
    std::wstring stagingDirectory;      // 服务临时目录 + UpdateDLL 的 AlternateStagingLocation
    std::vector<std::wstring> spaces;   // 要挂上并取 store id 的 space 名（布局里的 StoreType）
    std::wstring mainOsSpaceName;       // 含 MainOS 分区的 space 名（AssignMountPoints 需要它的 store 句柄）
    std::vector<std::wstring> deviceLayoutPaths;  // 传给 SetAllDeviceLayoutPaths
    std::wstring driveLetterConfigPath; // 可为空
    std::wstring actionListFile;
    std::wstring packagesRoot;
    std::wstring updateInputFile;
    std::wstring oemInputFile;            // original OEMInput.xml copied into <MainOS>\Windows\ImageUpdate
    std::wstring updateOutputFile;
    std::wstring updateHistoryFile;
    std::string storeStyle;            // onecore / state-separated / desktop (env var for UpdateDLL)
    std::wstring logFile;
    std::wstring storeArch;
    std::wstring overlapFile;           // 可为空
    bool stageOnly = false;             // true = 只跑 PrepareUpdate（暂存）
};

// 跑完整落包（暂存 + 提交）。report 是给人看的进度行。
OCID_UPDATE_API bool UpdateApply(const UpdateRequest& request,
                                 std::vector<std::string>& report,
                                 std::string& error);

OCID_UPDATE_API bool UpdateCreateEmptyUpdateOsWim(const std::wstring& mainOsPath,
                                              const std::wstring& tempRoot,
                                              std::string& error);

// 生成 ActionList：调用 UpdateAgent.dll 的 UA_CreateActionList
// （imageapp 的 Imaging.CreateActionList 用的同一入口；deviceType 传 1）。
OCID_UPDATE_API bool UpdateCreateActionList(const std::wstring& compDbFolder,
                                            const std::wstring& deviceDbPath,
                                            unsigned int deviceType,
                                            std::wstring& actionListPath,
                                            std::string& error);

OCID_UPDATE_API const char* UpdateVersion();

}  // namespace imaging
}  // namespace ocid
