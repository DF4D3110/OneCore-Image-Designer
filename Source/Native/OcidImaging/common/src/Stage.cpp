// Stage.cpp —— 阶段（版本分支）描述表。
//
// 同一个源文件被编译两次：
//   /DOCID_STAGE_FE   → 产出 ocid_stage.fe.wcos.dll   （StageCurrent() 返回 fe）
//   /DOCID_STAGE_RS5  → 产出 ocid_stage.rs5.wcos.dll  （StageCurrent() 返回 rs5）
//
// 表里两条记录都是纯数据（没有加载任何 ADK DLL），所以两个阶段都能看到对方的描述，
// 便于总控入口 ocid_imagegen.all.exe 打印清单；真正的隔离发生在"每个 exe 只链接自己
// 阶段的模块 DLL + 只把本阶段 ADK 根写进 OCID_ADK_ROOT"。
#include "ocid/imaging/Stage.h"

#include <cstdlib>
#include <cstring>
#include <string>

#if !defined(OCID_STAGE_FE) && !defined(OCID_STAGE_RS5)
#  error "Stage.cpp must be compiled with /DOCID_STAGE_FE or /DOCID_STAGE_RS5"
#endif

namespace
{

const ocid::imaging::StageInfo kStageTable[] = {
    // name, display, dllSuffix, adkRoot, osVersion, arch, layoutSchema, poolApi, ffuVer, appx
    { "fe",
      "FE / 20348 分支（10X，WCOS 存储池 ABI）",
      "fe",
      "D:\\ADK2022Wrokspace",
      "10.0.20279.1002",
      "arm64",
      "DeviceLayout.xsd",
      ocid::imaging::StagePoolApiWcosSpaces,
      2,
      true },
    { "rs5",
      "RS5 / 17763 分支（Andromeda/Talkman，早期 space ABI）",
      "rs5",
      "D:\\ADK2022Wrokspace\\RS5",
      "10.0.17705.1000",
      "arm",
      "DeviceLayout.xsd",
      ocid::imaging::StagePoolApiLegacyRS5,
      2,
      true },
};

const std::size_t kStageCount = sizeof(kStageTable) / sizeof(kStageTable[0]);

bool StageNameEquals(const std::string& a, const char* b)
{
    if (b == nullptr) return false;
    const std::size_t n = std::strlen(b);
    if (a.size() != n) return false;
    for (std::size_t i = 0; i < n; ++i)
    {
        char x = a[i];
        char y = b[i];
        if (x >= 'A' && x <= 'Z') x = static_cast<char>(x - 'A' + 'a');
        if (y >= 'A' && y <= 'Z') y = static_cast<char>(y - 'A' + 'a');
        if (x != y) return false;
    }
    return true;
}

std::string TrimTrailingSlash(std::string text)
{
    while (text.size() > 3 && (text.back() == '\\' || text.back() == '/')) text.pop_back();
    return text;
}

} // namespace

namespace ocid
{
namespace imaging
{

const StageInfo& StageCurrent()
{
#if defined(OCID_STAGE_RS5)
    return kStageTable[1];
#else
    return kStageTable[0];
#endif
}

const char* StageName()
{
    return StageCurrent().name;
}

const StageInfo* StageFind(const std::string& name)
{
    for (std::size_t i = 0; i < kStageCount; ++i)
    {
        if (StageNameEquals(name, kStageTable[i].name)) return &kStageTable[i];
    }
    return nullptr;
}

void StageAll(std::vector<const StageInfo*>& stages)
{
    stages.clear();
    for (std::size_t i = 0; i < kStageCount; ++i) stages.push_back(&kStageTable[i]);
}

bool StageVerifyEnvironment(std::string& error)
{
    const StageInfo& stage = StageCurrent();

    const char* envStage = std::getenv("OCID_STAGE");
    if (envStage != nullptr && envStage[0] != 0 && !StageNameEquals(envStage, stage.name))
    {
        error = std::string("当前 exe 属于 ") + stage.name + " 阶段，但环境里 OCID_STAGE=" +
                envStage + "；不同阶段的 ADK 不允许在同一进程里混用（请换 ocid_imagegen." +
                envStage + ".exe 或清掉该变量）";
        return false;
    }

    const char* envAdk = std::getenv("OCID_ADK_ROOT");
    if (envAdk != nullptr && envAdk[0] != 0)
    {
        const std::string given = TrimTrailingSlash(envAdk);
        const std::string own = TrimTrailingSlash(stage.adkRoot);
        if (!StageNameEquals(given, own.c_str()))
        {
            error = std::string("OCID_ADK_ROOT=``") + given + "`` 与本阶段（" + stage.name +
                    "）的 ADK 根 ``" + own + "`` 不一致；为防止两套 ADK 混用，这里直接拒绝运行";
            return false;
        }
    }
    return true;
}

bool StageApplyEnvironment(std::string& error)
{
    const StageInfo& stage = StageCurrent();
    if (!StageVerifyEnvironment(error)) return false;

    _putenv_s("OCID_STAGE", stage.name);
    _putenv_s("OCID_ADK_ROOT", stage.adkRoot);

#if defined(OCID_STAGE_RS5)
    // RS5 工具集布局是 <root>\bin\i386（没有 Windows Kits\10\Tools\bin\i386 这一层），
    // 下层 PoolFindAdkDll 的候选列表里已加了 <root>\bin\i386\<dll>。
#endif
    return true;
}

} // namespace imaging
} // namespace ocid
