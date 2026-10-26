// dispatch.cpp —— 总控入口 ocid_imagegen.all.exe
//
// 目的：一个入口调度"不同版本阶段"的工具链，但**不在同一个进程里混用 ADK**——
// 它只是把命令转发给对应阶段自己的 exe（各阶段 exe 只链接本阶段的 ocid_*.<stage>.wcos.dll，
// 启动时又把自己的 ADK 根写进 OCID_ADK_ROOT）。
//
// 用法：
//   ocid_imagegen.all.exe --list-stages
//   ocid_imagegen.all.exe --stage rs5 pool probe
//   ocid_imagegen.all.exe pool probe                  （默认 fe）
//   ocid_imagegen.all.exe --adk D:\ADK2022Wrokspace\RS5 --stage rs5 stage
//
// 退出码：子进程退出码；找不到子 exe 返回 2。
#include <windows.h>

#include <cstdio>
#include <string>
#include <vector>

namespace
{

const char* kStages[][3] = {
    // stage, exe 名, 说明
    { "fe", "fe\\ocid_imagegen.exe", "FE / 20348 分支（10X）" },
    { "rs5", "rs5\\ocid_imagegen.rs5.exe", "RS5 / 17763 分支（Andromeda/Talkman）" },
};

std::string ExeDirectory()
{
    wchar_t module[MAX_PATH] = { 0 };
    if (GetModuleFileNameW(nullptr, module, MAX_PATH) == 0) return std::string();
    std::wstring full(module);
    const std::size_t slash = full.find_last_of(L"\\/");
    if (slash == std::wstring::npos) return std::string();
    const std::wstring dir = full.substr(0, slash);
    std::string utf8;
    const int need = WideCharToMultiByte(CP_UTF8, 0, dir.c_str(), static_cast<int>(dir.size()), nullptr, 0, nullptr, nullptr);
    if (need <= 0) return std::string();
    utf8.resize(static_cast<std::size_t>(need));
    WideCharToMultiByte(CP_UTF8, 0, dir.c_str(), static_cast<int>(dir.size()), &utf8[0], need, nullptr, nullptr);
    return utf8;
}

std::wstring Widen(const std::string& text)
{
    if (text.empty()) return std::wstring();
    const int need = MultiByteToWideChar(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), nullptr, 0);
    std::wstring wide(static_cast<std::size_t>(need), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), &wide[0], need);
    return wide;
}

const char* StageDisplay(const std::string& name)
{
    for (const auto& item : kStages)
    {
        if (name == item[0]) return item[2];
    }
    return nullptr;
}

std::string StageExe(const std::string& name)
{
    for (const auto& item : kStages)
    {
        if (name == item[0]) return item[1];
    }
    return std::string();
}

void PrintUsage()
{
    std::printf("ocid_imagegen.all.exe —— 多阶段总控入口（不会在进程内混用两套 ADK）\n");
    std::printf("用法：\n");
    std::printf("  ocid_imagegen.all.exe --list-stages\n");
    std::printf("  ocid_imagegen.all.exe [--stage fe|rs5] [--adk <ADK 根>] <子命令> [参数...]\n");
    std::printf("示例：\n");
    std::printf("  ocid_imagegen.all.exe --stage rs5 pool probe\n");
    std::printf("  ocid_imagegen.all.exe image build --layout ... --outdir ...\n");
    std::printf("各阶段入口：\n");
    for (const auto& item : kStages)
    {
        std::printf("  %-4s %-32s %s\n", item[0], item[1], item[2]);
    }
}

std::string Quote(const std::string& text)
{
    return std::string("\"") + text + "\"";
}

} // namespace

int wmain(int argc, wchar_t** wargv)
{
    std::vector<std::string> args;
    for (int i = 0; i < argc; ++i)
    {
        std::wstring wide(wargv[i]);
        std::string utf8;
        const int need = WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), static_cast<int>(wide.size()), nullptr, 0, nullptr, nullptr);
        if (need > 0)
        {
            utf8.resize(static_cast<std::size_t>(need));
            WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), static_cast<int>(wide.size()), &utf8[0], need, nullptr, nullptr);
        }
        args.push_back(utf8);
    }

    std::string stage = "fe";
    std::string adkRoot;
    std::vector<std::string> child;
    for (std::size_t i = 1; i < args.size(); ++i)
    {
        const std::string& a = args[i];
        if (a == "--list-stages")
        {
            for (const auto& item : kStages)
            {
                std::printf("%-4s %s\n", item[0], item[2]);
            }
            return 0;
        }
        if ((a == "--stage" || a == "-s") && i + 1 < args.size())
        {
            stage = args[++i];
            continue;
        }
        if (a == "--adk" && i + 1 < args.size())
        {
            adkRoot = args[++i];
            continue;
        }
        if (a == "help" || a == "--help" || a == "-h" || a == "/?")
        {
            PrintUsage();
            return 0;
        }
        child.push_back(a);
    }

    if (child.empty())
    {
        PrintUsage();
        return 2;
    }
    if (StageDisplay(stage) == nullptr)
    {
        std::fprintf(stderr, "未知阶段：%s（可选 fe / rs5）\n", stage.c_str());
        return 2;
    }

    const std::string dir = ExeDirectory();
    std::string exePath = dir + "\\" + StageExe(stage);
    if (GetFileAttributesW(Widen(exePath).c_str()) == INVALID_FILE_ATTRIBUTES)
    {
        // 兼容"两个 exe 平铺在同一个目录"的布局
        std::string flat = dir + "\\ocid_imagegen" + (stage == "fe" ? std::string() : "." + stage) + ".exe";
        if (GetFileAttributesW(Widen(flat).c_str()) == INVALID_FILE_ATTRIBUTES)
        {
            std::fprintf(stderr, "找不到 %s 阶段的入口：%s\n", stage.c_str(), exePath.c_str());
            return 2;
        }
        exePath = flat;
    }

    // 阶段环境：子 exe 自己也会设置；这里只把用户的 --adk 覆盖传下去。
    if (!adkRoot.empty())
    {
        SetEnvironmentVariableW(L"OCID_ADK_ROOT", Widen(adkRoot).c_str());
    }

    std::string commandLine = Quote(exePath);
    for (const std::string& item : child)
    {
        commandLine += " " + Quote(item);
    }

    std::printf("[all] 阶段 %s -> %s\n", stage.c_str(), exePath.c_str());
    std::fflush(stdout);

    std::wstring cmd = Widen(commandLine);
    STARTUPINFOW startup = { 0 };
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process = { 0 };
    std::vector<wchar_t> buffer(cmd.begin(), cmd.end());
    buffer.push_back(L'\0');
    const BOOL ok = CreateProcessW(nullptr, buffer.data(), nullptr, nullptr, TRUE, 0, nullptr, nullptr, &startup, &process);
    if (!ok)
    {
        std::fprintf(stderr, "启动失败（win32=%lu）：%s\n", GetLastError(), exePath.c_str());
        return 2;
    }
    WaitForSingleObject(process.hProcess, INFINITE);
    DWORD exitCode = 0;
    GetExitCodeProcess(process.hProcess, &exitCode);
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    std::printf("[all] 阶段 %s 退出码：%lu\n", stage.c_str(), static_cast<unsigned long>(exitCode));
    return static_cast<int>(exitCode);
}
