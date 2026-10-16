// OCID - $(runtime.*) 宏展开测试
//
// 每条断言的出处都在 MacroExpander.h 顶部：语料分布、winsockai.dll 的权威表、
// smiengine 的 ExpandDollarVar、目标镜像的实测目录。
//
// 这里最想钉死的三件事：
//   ① **大小写不敏感** —— 语料里 `$(runtime.system32)` / `$(Runtime.System32)` /
//      `$(runtime.System32)` 三种写法并存，只认一种会静默漏掉几千处。
//   ② `$(runtime.system)` 不是 system32，是 **`<Windows>\System`**（NTVDM 的 Win16 目录）。
//      按直觉写成 system32 会把 8 个 Win16 组件装错地方，而且不报错。
//   ③ 不认识的宏**原样保留并上报**，绝不替换成空串或半截路径。

#include "ocid/ConsoleUtf8.h"
#include "ocid/MacroExpander.h"

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

static void CheckStr(const std::wstring& got, const wchar_t* want, const char* what)
{
    if (got == want) { ++g_pass; printf("  [ok]   %s\n", what); }
    else
    {
        ++g_fail;
        printf("  [FAIL] %s\n         got  = %s\n         want = %s\n",
               what, ToUtf8(got).c_str(), ToUtf8(want).c_str());
    }
}

static void CheckExpand(const std::wstring& in, const wchar_t* want,
                        const RuntimePaths& p, const char* what)
{
    CheckStr(ExpandRuntimeMacros(in, p), want, what);
}

int main()
{
    SetupConsoleUtf8();

    // 离线服务的典型形态：镜像挂在 P:
    const RuntimePaths image = RuntimePaths::FromWindowsDir(L"P:\\Windows");

    printf("\n===== 1. 基础路径推导 =====\n");
    CheckStr(image.windows,          L"P:\\Windows",                            "windows");
    CheckStr(image.bootDrive,        L"P:",                                     "bootDrive = P:");
    CheckStr(image.programFiles,     L"P:\\Program Files",                      "programFiles");
    CheckStr(image.programFilesX86,  L"P:\\Program Files (x86)",                "programFilesX86");
    CheckStr(image.commonFiles,      L"P:\\Program Files\\Common Files",        "commonFiles");
    CheckStr(image.programData,      L"P:\\ProgramData",                        "programData");
    CheckStr(image.users,            L"P:\\Users",                              "users");
    CheckStr(image.publicDir,        L"P:\\Users\\Public",                      "publicDir");
    CheckStr(image.startMenu,        L"P:\\ProgramData\\Microsoft\\Windows\\Start Menu",
             "startMenu");
    {
        const RuntimePaths t = RuntimePaths::FromWindowsDir(L"P:\\Windows\\");
        CheckStr(t.windows, L"P:\\Windows", "尾随反斜杠被去掉");
    }
    {
        const RuntimePaths t = RuntimePaths::FromWindowsDir(L"\\Windows");
        CheckStr(t.windows, L"\\Windows", "没有盘符时 windows 仍然可用");
        CheckStr(t.bootDrive, L"", "没有盘符时 bootDrive 为空");
    }

    printf("\n===== 2. 19 个路径宏逐个展开（语料里的真实用法）=====\n");
    CheckExpand(L"$(runtime.system32)\\de\\",             L"P:\\Windows\\system32\\de\\",                 image, "system32 + \\de\\");
    CheckExpand(L"$(runtime.windows)\\Microsoft.NET\\Framework\\v4.0.30319\\",
                L"P:\\Windows\\Microsoft.NET\\Framework\\v4.0.30319\\",                                          image, "windows + Microsoft.NET");
    CheckExpand(L"$(runtime.programFiles)\\Windows Identity Foundation\\v3.5\\",
                L"P:\\Program Files\\Windows Identity Foundation\\v3.5\\",                                       image, "programFiles");
    CheckExpand(L"$(runtime.wbem)\\BITS\\",               L"P:\\Windows\\system32\\wbem\\BITS\\",         image, "wbem = system32\\wbem");
    CheckExpand(L"$(runtime.drivers)\\de-DE\\",           L"P:\\Windows\\system32\\drivers\\de-DE\\",     image, "drivers");
    CheckExpand(L"$(runtime.help)\\mui\\0407\\",          L"P:\\Windows\\help\\mui\\0407\\",             image, "help");
    CheckExpand(L"$(runtime.inf)\\BITS\\0407\\",          L"P:\\Windows\\inf\\BITS\\0407\\",             image, "inf");
    CheckExpand(L"$(runtime.fonts)\\",                    L"P:\\Windows\\Fonts\\",                       image, "fonts");
    CheckExpand(L"$(runtime.apppatch)\\Custom\\",         L"P:\\Windows\\AppPatch\\Custom\\",            image, "apppatch");
    CheckExpand(L"$(runtime.commonFiles)\\Microsoft Shared\\MSInfo\\de-DE\\",
                L"P:\\Program Files\\Common Files\\Microsoft Shared\\MSInfo\\de-DE\\",                            image, "commonFiles");
    CheckExpand(L"$(runtime.programData)\\Microsoft\\Event Viewer\\Views\\ServerRoles\\",
                L"P:\\ProgramData\\Microsoft\\Event Viewer\\Views\\ServerRoles\\",                                image, "programData");
    CheckExpand(L"$(runtime.startMenu)\\Programs\\Administrative Tools\\",
                L"P:\\ProgramData\\Microsoft\\Windows\\Start Menu\\Programs\\Administrative Tools\\",              image, "startMenu");
    CheckExpand(L"$(runtime.documentsSettings)\\Default\\AppData\\Roaming\\Microsoft\\Windows\\Start Menu\\Programs\\",
                L"P:\\Users\\Default\\AppData\\Roaming\\Microsoft\\Windows\\Start Menu\\Programs\\",              image, "documentsSettings = <盘>:\\Users");
    CheckExpand(L"$(runtime.public)\\Recorded TV\\",      L"P:\\Users\\Public\\Recorded TV\\",            image, "public");
    CheckExpand(L"$(runtime.bootDrive)\\",                L"P:\\",                                        image, "bootDrive + \\");
    CheckExpand(L"$(runtime.systemRoot)\\",               L"P:\\Windows\\",                               image, "systemRoot = windows");
    CheckExpand(L"$(runtime.windir)\\ADAM\\de-DE\\",      L"P:\\Windows\\ADAM\\de-DE\\",                  image, "windir = windows");
    CheckExpand(L"$(runtime.programFilesX86)\\Internet Explorer\\IEXPLORE.EXE",
                L"P:\\Program Files (x86)\\Internet Explorer\\IEXPLORE.EXE",                                      image, "programFilesX86");

    printf("\n===== 3. runtime.system 是 <Windows>\\System，不是 system32 =====\n");
    CheckExpand(L"$(runtime.system)\\COMMDLG.DLL", L"P:\\Windows\\System\\COMMDLG.DLL", image, "NTVDM 的 COMMDLG.DLL 落在 System");
    CheckExpand(L"$(runtime.system)\\",            L"P:\\Windows\\System\\",            image, "runtime.system 目录本身");
    Check(ExpandRuntimeMacros(L"$(runtime.system)\\", image) != L"P:\\Windows\\system32\\",
          "runtime.system 与 runtime.system32 必须不同");

    printf("\n===== 4. 大小写不敏感（语料三种写法并存）=====\n");
    const wchar_t* variants[] = {
        L"$(Runtime.System32)\\a", L"$(runtime.System32)\\a", L"$(RUNTIME.SYSTEM32)\\a",
        L"$(runtime.system32)\\a"
    };
    for (const wchar_t* v : variants)
        CheckStr(ExpandRuntimeMacros(v, image), L"P:\\Windows\\system32\\a",
                 ToUtf8(std::wstring(L"大小写变体: ") + v).c_str());
    CheckStr(ExpandRuntimeMacros(L"$(runtime.programfiles)\\x", image),
             L"P:\\Program Files\\x", "programfiles 小写");
    CheckStr(ExpandRuntimeMacros(L"$(runtime.ProgramFiles)\\x", image),
             L"P:\\Program Files\\x", "ProgramFiles 驼峰");
    CheckStr(ExpandRuntimeMacros(L"$(runtime.bootDrive)\\", image), L"P:\\", "bootDrive 驼峰");
    CheckStr(ExpandRuntimeMacros(L"$(runtime.bootdrive)\\", image), L"P:\\", "bootdrive 全小写");

    printf("\n===== 5. runtime.host.* （winsockai.dll 权威表里的注册表根）=====\n");
    CheckExpand(L"$(runtime.host.hklm.system.ccs)\\Services",
                L"HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services", image, "host.hklm.system.ccs");
    CheckExpand(L"$(runtime.host.hklm.software)\\Microsoft",
                L"HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft",                 image, "host.hklm.software");
    CheckExpand(L"$(runtime.host.hklm.system)\\Select",
                L"HKEY_LOCAL_MACHINE\\SYSTEM\\Select",                      image, "host.hklm.system");
    CheckExpand(L"$(runtime.host.hkcr)\\Unknown\\shell",
                L"HKEY_CLASSES_ROOT\\Unknown\\shell",                       image, "host.hkcr");
    CheckExpand(L"$(runtime.host.hklm.system)\\CURRENTCONTROLSET\\HARDWARE\\PROFILES\\CURRENT",
                L"HKEY_LOCAL_MACHINE\\SYSTEM\\CURRENTCONTROLSET\\HARDWARE\\PROFILES\\CURRENT",
                image, "host.hklm.system + 字面后缀（winsockai 第 12 项）");

    printf("\n===== 6. 一个串里多个宏 / 无宏 =====\n");
    CheckExpand(L"$(runtime.system32)\\$(runtime.programFiles)\\x",
                L"P:\\Windows\\system32\\P:\\Program Files\\x", image, "同串两个宏各自展开");
    CheckExpand(L"just text", L"just text", image, "无宏原样返回");
    CheckExpand(L"$ not a macro", L"$ not a macro", image, "孤立的 $ 不动");
    CheckExpand(L"$(runtime.system32", L"$(runtime.system32", image, "没有右括号 => 原样");
    CheckExpand(L"$()", L"$()", image, "空宏名 => 原样");
    CheckExpand(L"$(   )", L"$(   )", image, "空白名字（含非法字符）=> 原样");

    printf("\n===== 7. 不认识的宏：原样保留 + 上报 =====\n");
    {
        const auto r = ExpandMacros(
            L"$(runtime.system32)\\a;$(build.nttree)\\b;$(string.Foo);$(resourceString.displayName);"
            L"$(runtime.telnetClientsGroupSID);$(Arg0);$(dependentAssembly.SharedLibraries.path)",
            image);

        CheckStr(r.text,
                 L"P:\\Windows\\system32\\a;$(build.nttree)\\b;$(string.Foo);"
                 L"$(resourceString.displayName);$(runtime.telnetClientsGroupSID);$(Arg0);"
                 L"$(dependentAssembly.SharedLibraries.path)",
                 "已知宏展开、其余原样");
        Check(r.unresolved.size() == 6, "上报 6 个未展开宏");
        printf("         未展开 = ");
        for (const auto& u : r.unresolved) printf("%s ", ToUtf8(u).c_str());
        printf("\n");
        Check(r.unresolved[0] == L"$(build.nttree)", "第 1 个未展开 = $(build.nttree)（按出现顺序）");
        Check(!r.ok(), "有残留时 ok() == false");
    }
    {
        const auto r = ExpandMacros(L"$(runtime.system32)\\x;$(runtime.system32)\\y", image);
        Check(r.ok(), "全部认出来时 ok() == true");
        CheckStr(r.text, L"P:\\Windows\\system32\\x;P:\\Windows\\system32\\y", "两个同样的宏都展开");
    }

    printf("\n===== 8. ContainsMacro / ExtractMacroNames =====\n");
    Check(!ContainsMacro(L"no macros here"), "无宏 => false");
    Check(ContainsMacro(L"$(string.X)"), "有 $( => true");
    {
        const auto n = ExtractMacroNames(L"$(a.b)\\x\\$(c.d)\\y\\$(a.b)");
        Check(n.size() == 2, "重复名字去重（a.b 只出现一次）");
        Check(n[0] == L"$(a.b)" && n[1] == L"$(c.d)", "保持出现顺序");
    }
    {
        const auto n = ExtractMacroNames(L"plain");
        Check(n.empty(), "无宏 => 空列表");
    }

    printf("\n===== 9. 镜像自带目录覆盖（x: 占位符那件事）=====\n");
    {
        RuntimePaths p = RuntimePaths::FromWindowsDir(L"P:\\Windows");
        // 目标镜像 SOFTWARE hive 实测：ProgramFilesDir = x:\Program Files
        p.ApplyImageOverrides(L"x:\\Program Files", L"x:\\Program Files (x86)",
                              L"x:\\Program Files\\Common Files",
                              L"x:\\ProgramData", L"x:\\Users");
        CheckStr(p.programFiles, L"x:\\Program Files", "覆盖成镜像声明的值");
        CheckStr(p.commonFiles,  L"x:\\Program Files\\Common Files", "commonFiles 也跟着覆盖");
        CheckStr(p.windows,      L"P:\\Windows", "未覆盖的仍用挂载盘（windows）");
        CheckStr(p.startMenu,    L"x:\\ProgramData\\Microsoft\\Windows\\Start Menu",
                 "programData 换盘符后 startMenu 被重算（不留旧盘符）");
        CheckStr(p.publicDir,    L"x:\\Users\\Public", "users 换盘符后 publicDir 被重算");
        CheckExpand(L"$(runtime.programFiles)\\x", L"x:\\Program Files\\x", p, "覆盖后宏按新值展开");
    }

    printf("\n===== 10. 已知宏清单 =====\n");
    {
        std::size_t n = 0;
        const wchar_t* const* names = KnownRuntimeMacroNames(&n);
        Check(n == 23, "23 个已知宏 = 19 个路径 + 4 个注册表根");
        Check(names != nullptr, "名字表非空");
        for (std::size_t i = 0; i < n; ++i)
            Check(wcsncmp(names[i], L"runtime.", 8) == 0, ToUtf8(std::wstring(L"名字: ") + names[i]).c_str());
    }

    printf("\n通过 %d / 失败 %d\n", g_pass, g_fail);
    return g_fail == 0 ? 0 : 1;
}
