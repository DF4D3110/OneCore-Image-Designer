// OCID - 清单里的 $(runtime.*) 宏展开
//
// ── 这一层回答什么 ────────────────────────────────────────────────────────
//
// 清单里到处写着 `destinationPath="$(runtime.system32)\de\"`、
// `value="$(runtime.programFiles)\Internet Explorer\IEXPLORE.EXE"`。
// 这些点号名字**不是**环境变量名，是 WCM/CSI 自己的"运行时已知文件夹"宏；
// 落到镜像里必须先展开成真实路径，否则：
//   · 文件会被搬到字面目录 `$(runtime.system32)`（错）
//   · 注册表值会被写成字面 `$(runtime.…)`（错，而且很难发现）
//
// 之前 RegistryValue.h 里把这件事标成"还没解决"：检测到宏就只置
// `hasUnresolvedMacro` 让上层告警 —— 因为当时展开表没挖出来，**不猜**。
// 这个文件把展开表补上。
//
// ── 证据 ──────────────────────────────────────────────────────────────────
//
// [语料] `E:\sxs_lib` 全部 70219 份真实清单里 `$(…)` 形态统计：
//   34353 个不同的"宏"，但绝大多数根本不是路径：
//     $(string.*) 39771 · $(resourceString.*) 43990   ← 本地化字符串表引用
//     $(build.nttree) 77097                           ← 构建期源路径（importPath 用）
//     $(dependentAssembly.*) 170 · $(mc.*) 421 · $(ArgN) 32
//   真正的路径宏只有 `runtime.*` 一族，**20 个不同的项**（大小写归一后）：
//
//     runtime.system32          51479
//     runtime.windows           16288
//     runtime.programfiles       6533
//     runtime.wbem               3538
//     runtime.drivers            1968
//     runtime.help               1625
//     runtime.bootdrive          1496
//     runtime.inf                1356
//     runtime.commonfiles         983
//     runtime.startmenu           464
//     runtime.fonts               455
//     runtime.programdata         388
//     runtime.systemroot          204
//     runtime.documentssettings   165
//     runtime.public               27
//     runtime.windir               25
//     runtime.system               23
//     runtime.programfilesx86       2
//     runtime.apppatch              1
//     runtime.telnetclientsgroupsid 1   ← 这个是 SID，不是路径
//
//   ⚠ **大小写不敏感**：语料里同时存在 `$(runtime.system32)` / `$(Runtime.System32)`
//     / `$(runtime.System32)`、`$(runtime.programFiles)` / `$(runtime.programfiles)`
//     / `$(runtime.ProgramFiles)`、`$(runtime.bootDrive)` / `$(runtime.bootdrive)`
//     ⇒ 查表必须归一小写，否则会漏掉几千处。
//
// [表] `winsockai.dll` 里有一张 12 项的"宏 → 替换串"权威表（反编译：
//   `E:\ocid_decomp\sym8061x86_winsockai.c` 约 L4182-L4231，`v13[3i+2]` 是宏字面量、
//   `v47[i]` 是替换串 LUNICODE_STRING）。它直接印证了前 9 个：
//     $(runtime.system32)                  → %SystemRoot%\system32
//     $(runtime.windows)                   → %SystemRoot%
//     $(runtime.bootDrive)                 → %SystemDrive%
//     $(runtime.programFiles)              → %ProgramFiles%
//     $(runtime.commonFiles)               → %CommonProgramFiles%
//     $(runtime.host.hklm.system.ccs)      → HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet
//     $(runtime.host.hklm.software)        → HKEY_LOCAL_MACHINE\SOFTWARE
//     $(runtime.host.hklm.system)          → HKEY_LOCAL_MACHINE\SYSTEM
//     $(runtime.host.hkcr)                 → HKEY_CLASSES_ROOT
//   （同一文件 L4243-L4251 还显示：匹配用的是**前缀**语义，`RtlEqualLUnicodeStringPrefix`
//     命中后把替换串长度从源串里减掉并前移 Buffer ⇒ 展开就是"换掉开头那一段"。）
//
// [表] `smiengine.dll` 的 `?ExpandDollarVar@@YGJPBGPAPAG@Z` @0x10061168（反编译
//   L77325-L77419）是**另一条**通路，只认 4 个，且按"是否 ILal 实例"分叉：
//     有 ILal：systemRoot/windir/windows → `%windir%`；system32 → `%windir%` + `\system32`
//     无 ILal：三者 → `\SystemRoot`；system32 → `\SystemRoot` + `\system32`
//   `\SystemRoot` 是内核侧前缀，开机才解析 —— 说明服务栈在"离线但要用宿主 API"时
//   会退化成这种可延后解析的形式。
//
// [实测] 目标镜像（`E:\ocid_master.vhd`，8061 ARM 构建产物）挂成 `P:` 后逐个探目录：
//     P:\Windows                     ✓  ← runtime.windows
//     P:\Windows\System32            ✓  ← runtime.system32
//     P:\Windows\System32\drivers    ✓
//     P:\Windows\System32\wbem       ✓
//     P:\Windows\inf                 ✓
//     P:\Windows\Fonts               ✓
//     P:\Program Files               ✓
//     P:\Program Files\Common Files  ✓
//     P:\ProgramData                 ✓
//     P:\Users                       ✓  ← runtime.documentsSettings
//     P:\Users\Public                ✓
//     P:\                            ✓  ← runtime.bootDrive
//   `runtime.help` / `runtime.apppatch` / `runtime.startMenu` /
//   `runtime.programFilesX86` 在该镜像里**目录不存在** —— 因为对应组件还没装进去，
//   不是映射错。（这张镜像顶层只有 packages / Program Files / ProgramData /
//   System Volume Information / Users / Windows。）
//
// [实测] `runtime.system` = **`<Windows>\System`**（Win16/NTVDM 目录）。语料里用它的
//   两份清单落点全是 Win16 组件：`COMMDLG.DLL` `keyboard.drv` `lzexpand.dll`
//   `MMSYSTEM.DLL` `mmtask.tsk` `mouse.drv` `olecli.dll` `OLESVR.DLL`
//   （`x86_microsoft-windows-ntvdm-system_…manifest`），另一份则是
//   `<directory destinationPath="$(runtime.system)\">` 与 `$(runtime.programfiles)\windows nt\tabletextservice\`
//   并列出现。`System` 正是 NTVDM 的 Win16 系统目录，吻合。
//
// [实测] `runtime.documentsSettings` = **`<盘>:\Users`**：语料里它的典型用法是
//   `$(runtime.documentsSettings)\Default\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\…`
//   = `C:\Users\Default\…`。"documentsSettings" 是 XP 时代 "Documents and Settings"
//   的遗留名字，在 Vista 之后就是 `Users`。
//
// ── 基础路径从哪来（这是本题最容易搞错的地方）─────────────────────────────
//
// 离线镜像**给不出**这些路径。实测目标镜像的 `SYSTEM` hive：
//     ControlSet001\Control\Session Manager\Environment 只有
//       windir = %SystemRoot%        ← 循环，自己还要别人来解
//       Path / ComSpec / TEMP / TMP / OS / PATHEXT / USERNAME / PROCESSOR_ARCHITECTURE
//     **没有** SystemRoot、SystemDrive、ProgramFiles、ProgramData、PUBLIC
//   而且 `CurrentControlSet` 在离线 hive 里**根本不存在**（开机才生成）。
//   同样的键在 `SOFTWARE` hive 里也没有。
//
// ⇒ 基础路径只能由服务栈按"盘符 + 约定目录名"自己拼。本文件提供两种来源：
//     1. `RuntimePaths::FromWindowsDir()`   —— 从 Windows 目录推（离线服务时用这个）
//     2. `RuntimePaths::ApplyImageOverrides()` —— 用镜像自己声明的目录名覆盖
//
//   ⚠ 为什么还要留第 2 种：目标镜像的
//       `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\ProgramFilesDir = x:\Program Files`
//      —— **`x:` 是构建期占位盘符**（同一键下 CommonFilesDir 也是 `x:`）。
//     真机上是 `C:\Program Files`。所以"照抄镜像里的值"和"按挂载盘拼"会得到不同结果，
//     而目前**没有一次成功的 CBS 运行**能告诉我们真实服务栈选哪个。
//     ⇒ 两种都提供、由调用方选，绝不偷偷二选一。
//
// ── 宏之外的残留 ──────────────────────────────────────────────────────────
//
//   `$(build.nttree)`、`$(string.*)`、`$(resourceString.*)`、`$(ArgN)`、
//   `$(dependentAssembly.*)`、`$(runtime.telnetClientsGroupSID)` **不是**路径宏，
//   本文件不动它们，但会把名字收集到 `unresolved` 里让上层看得见 ——
//   比静默替换成空串安全得多。

#pragma once

#include <cstddef>
#include <string>
#include <vector>

namespace ocid
{
    /// 目标镜像的基础路径集合。全部**不带尾随反斜杠**（`bootDrive` 形如 `P:`）。
    struct RuntimePaths
    {
        std::wstring windows;          ///< `$(runtime.windows/systemRoot/windir)`
        std::wstring bootDrive;        ///< `$(runtime.bootDrive)`，形如 `P:`
        std::wstring programFiles;     ///< `$(runtime.programFiles)`
        std::wstring programFilesX86;  ///< `$(runtime.programFilesX86)`
        std::wstring commonFiles;      ///< `$(runtime.commonFiles)`
        std::wstring programData;      ///< `$(runtime.programData)`
        std::wstring users;            ///< `$(runtime.documentsSettings)`
        std::wstring publicDir;        ///< `$(runtime.public)`
        std::wstring startMenu;        ///< `$(runtime.startMenu)`

        /// 由 Windows 目录（例如 `P:\Windows`）按约定推出全部路径。
        static RuntimePaths FromWindowsDir( const std::wstring& windowsDir );

        /// 用镜像自己声明的目录覆盖（传空串表示不覆盖）。
        /// 目标：`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion` 的
        /// `ProgramFilesDir` / `CommonFilesDir` / `ProgramFilesDir (x86)`。
        void ApplyImageOverrides( const std::wstring& pf,
                                  const std::wstring& pfX86,
                                  const std::wstring& cf,
                                  const std::wstring& pd,
                                  const std::wstring& usersDir );
    };

    /// 展开结果。
    struct MacroExpansion
    {
        std::wstring              text;        ///< 展开后的文本
        std::vector<std::wstring> unresolved;  ///< 原样保留的宏名（含 `$(` `)`），去重并按出现顺序
        bool ok() const { return unresolved.empty(); }
    };

    /// 文本里是否含任意 `$(…)` 形态（不判断是不是路径宏）。
    bool ContainsMacro( const std::wstring& text );

    /// 抽出全部 `$(…)` 名字（含括号），去重、按出现顺序。不区分是否已知。
    std::vector<std::wstring> ExtractMacroNames( const std::wstring& text );

    /// 只展开已知的 `runtime.*` 路径宏；其余一律原样保留并记进 `unresolved`。
    /// 大小写不敏感（语料实测三种大小写混用）。
    MacroExpansion ExpandMacros( const std::wstring& text, const RuntimePaths& paths );

    /// 只展开 `runtime.*` 路径宏，不关心残留（工具/测试里的简写）。
    std::wstring ExpandRuntimeMacros( const std::wstring& text, const RuntimePaths& paths );

    /// 已知的 `runtime.*` 路径宏名表（小写、不含 `$(` `)`），供工具列出与测试遍历。
    const wchar_t* const* KnownRuntimeMacroNames( std::size_t* count );
}
