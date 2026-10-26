// Stage.h —— 构建阶段（版本分支）描述：FE(10X/20348 分支) 与 RS5(17763 分支)
//
// 为什么要有这一层：
//   ImageApp 的做法是"每个 ADK 版本一套工具集"，FE 与 RS5 的 imagestorageservice.dll /
//   UpdateDLL.dll 导出与语义都不同（RS5 没有 AttachStorageSpace/OpenStoragePool，
//   只有 CreateStorageSpace/GetStorageSpace/RemoveStorageSpace…）。
//   同一个进程里绝不允许同时加载两套 ADK（同名 DLL 会互相顶替，出问题无法定位），
//   所以按阶段拆成独立的 DLL（ocid_*.fe.wcos.dll / ocid_*.rs5.wcos.dll）与独立 exe。
//
// 约定：
//   - 每个阶段编出自己的 ocid_stage.<stage>.wcos.dll，里面只有一张 StageInfo 表；
//   - 进程启动时 StageApplyEnvironment() 把自己的 ADK 根目录写进环境变量
//     OCID_ADK_ROOT（下层 PoolFindAdkDll 一直就是这么找的），并拒绝运行在"别的阶段"；
//   - 代码里需要按阶段分叉的地方一律用 OCID_STAGE_* 宏或 StageCurrent().poolApiFamily，
//     不要写 exe 路径判断。
#ifndef OCID_IMAGING_STAGE_H
#define OCID_IMAGING_STAGE_H

#include "Common.h"

#include <string>
#include <vector>

// 阶段 DLL 自己的导出宏：ocid_stage.<stage>.wcos.dll 内部编译时定义 OCID_STAGE_BUILD。
#if defined(OCID_STAGE_BUILD)
#  define OCID_STAGE_API __declspec(dllexport)
#else
#  define OCID_STAGE_API __declspec(dllimport)
#endif

namespace ocid
{
namespace imaging
{

// 池/space 服务 API 家族。
//   1 = WCOS 存储池（FE/20348 与 10X）：AttachStorageSpace/DetachStorageSpace/OpenStoragePool…
//   2 = 早期 RS5（17763）：CreateStorageSpace/GetStorageSpace/RemoveStorageSpace/AddDriveToStoragePool…
enum StagePoolApi
{
    StagePoolApiWcosSpaces = 1,
    StagePoolApiLegacyRS5 = 2
};

struct StageInfo
{
    const char* name;         // "fe" / "rs5"，命令行 --stage 用的名字
    const char* display;      // 人读描述
    const char* dllSuffix;    // DLL 名后缀：ocid_common.<dllSuffix>.wcos.dll
    const char* adkRoot;      // 该阶段专属 ADK 根目录（绝不与另一阶段混用）
    const char* osVersion;    // 默认 OSVersion（FFU manifest / UpdateInput 缺省值）
    const char* arch;         // 默认架构
    const char* layoutSchema; // 该阶段 DeviceLayout 的 XSD 名（诊断用）
    int poolApi;              // StagePoolApi
    int ffuManifestVersion;   // FFU manifest 的 Version 字段
    bool appxSupported;       // 是否有 AppX 落包（两阶段都有，但 helper 目录不同）
};

// 当前 DLL 所属阶段（编译期决定，见 src\Stage.cpp 的 OCID_STAGE_* 宏）。
OCID_STAGE_API const StageInfo& StageCurrent();

// 当前阶段名（"fe"/"rs5"）。
OCID_STAGE_API const char* StageName();

// 解析阶段名，返回对应 StageInfo；未知名返回 nullptr。
OCID_STAGE_API const StageInfo* StageFind(const std::string& name);

// 已知阶段清单（"fe"、"rs5"），用于总控入口 --list-stages。
OCID_STAGE_API void StageAll(std::vector<const StageInfo*>& stages);

// 把本阶段 ADK 根写进 OCID_ADK_ROOT（若环境里已有且不属于本阶段则报错），
// 并设置 OCID_STAGE 供下层 DLL 自检。exe 启动时第一件事就调它。
OCID_STAGE_API bool StageApplyEnvironment(std::string& error);

// 只校验不写入：OCID_ADK_ROOT/OCID_STAGE 与本阶段冲突时返回 false。
OCID_STAGE_API bool StageVerifyEnvironment(std::string& error);

} // namespace imaging
} // namespace ocid

#endif // OCID_IMAGING_STAGE_H
