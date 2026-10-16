// OcidRegHook.dll —— 给 wcp.dll 的导入表打补丁，截获它对注册表的原生调用
//
// 【为什么需要它】
// OCID 用自建 CBS 会话（自己 LoadLibrary cbscore.dll → wcp.dll）离线服务镜像时，
// 组件 manifest 里声明的 HKEY_CLASSES_ROOT 类注册表写**没有落到离线 hive**，
// 而是落到了宿主机的 HKLM\SOFTWARE\Classes，被内核保护（UCPD 那类）拦下，
// 报 STATUS_ACCESS_DENIED 并导致整笔注册表事务（KTM）回滚 —— Commit 因此失败。
//
// 我们不去改 wcp.dll 的二进制，而是在**我们自己的进程里**把它导入表里的
// 注册表 API 换成我们的包装函数：先**记录**每次调用真正请求的键路径（弄清"挂载的
// 注册表到底在哪"），再按映射表**改写**路径（弄清"到哪里改才是对的"）。
//
// 【为什么是 IAT 而不是 inline hook】
// 实测 8061 自带 x86 服务栈里的 wcp.dll **静态导入** ntdll 的这些函数：
//   NtCreateKey / NtCreateKeyTransacted / NtOpenKey / NtOpenKeyEx /
//   NtSetValueKey / NtDeleteKey / NtDeleteValueKey / NtQueryKey
// 静态导入 ⇒ 改 IAT 项即可，不需要改代码段，也不需要驱动，安全得多。
//
// 【用法】由 CbsxService 在 wcp.dll 加载之后、WcpInitialize 之前调用：
//   OcidRegHook_Install(L"C:\\ocid_reghook.log", L"C:\\ocid_regmap.txt");
//   ... CBS 干活 ...
//   OcidRegHook_Uninstall();
//
// 映射文件每行 `活动路径前缀=离线路径前缀`（大小写不敏感的前缀匹配），例如：
//   \Registry\MACHINE\SOFTWARE=\Registry\MACHINE\{bf1a281b-...}P:/Windows/System32/config/SOFTWARE
//   \Registry\MACHINE\SYSTEM=\Registry\MACHINE\{bf1a281b-...}P:/Windows/System32/config/SYSTEM
// 映射文件为空 / 不需要改写时传 NULL ⇒ **只记录不改写**（先看清真相）。

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winternl.h>
#include <tlhelp32.h>      // CreateToolhelp32Snapshot / Module32FirstW —— 遍历所有已加载模块
#include <shlwapi.h>       // StrStrIW —— 模块名过滤用（大小写不敏感的子串匹配）
#include <stdio.h>
#include <string>
#include <vector>

// ─────────────────────────────────────────────────────────────────────────────
// 日志
// ─────────────────────────────────────────────────────────────────────────────
static FILE* g_log = NULL;
static CRITICAL_SECTION g_cs;
static bool g_csReady = false;
static LONG g_callCount = 0;
static LONG g_rewriteCount = 0;

static void LogOpen(const wchar_t* path)
{
    if (!path) return;
    _wfopen_s(&g_log, path, L"w, ccs=UTF-8");
    if (g_log) { fwprintf(g_log, L"# OcidRegHook log\n"); fflush(g_log); }
}

static void LogW(const wchar_t* fmt, ...)
{
    if (!g_log) return;
    if (g_csReady) EnterCriticalSection(&g_cs);
    va_list ap; va_start(ap, fmt);
    vfwprintf(g_log, fmt, ap);
    fwprintf(g_log, L"\n");
    fflush(g_log);
    va_end(ap);
    if (g_csReady) LeaveCriticalSection(&g_cs);
}

// ─────────────────────────────────────────────────────────────────────────────
// 映射表（活动路径前缀 → 离线路径前缀）
// ─────────────────────────────────────────────────────────────────────────────
struct Mapping { std::wstring from; std::wstring to; };
static std::vector<Mapping> g_mappings;

static void LoadMappings(const wchar_t* file)
{
    if (!file) return;
    FILE* f = NULL;
    _wfopen_s(&f, file, L"r, ccs=UTF-8");
    if (!f) { LogW(L"[map] cannot open mapping file: %s", file); return; }
    wchar_t line[2048];
    while (fgetws(line, 2048, f))
    {
        std::wstring s(line);
        while (!s.empty() && (s.back() == L'\n' || s.back() == L'\r')) s.pop_back();
        if (s.empty() || s[0] == L'#') continue;
        size_t eq = s.find(L'=');
        if (eq == std::wstring::npos) continue;
        Mapping m;
        m.from = s.substr(0, eq);
        m.to   = s.substr(eq + 1);
        if (!m.from.empty()) g_mappings.push_back(m);
    }
    fclose(f);
    LogW(L"[map] loaded %zu mapping(s)", g_mappings.size());
}

// 前缀匹配（大小写不敏感，按 `\` 边界对齐）
static bool PathStartsWith(const std::wstring& path, const std::wstring& prefix)
{
    if (path.size() < prefix.size()) return false;
    if (_wcsnicmp(path.c_str(), prefix.c_str(), prefix.size()) != 0) return false;
    if (path.size() == prefix.size()) return true;
    wchar_t next = path[prefix.size()];
    return next == L'\\' || next == L'/';
}

// 前向声明：把 `/config/SOFTWARE` 段改名（定义在下面，见那里的长注释）
static std::wstring RenameSoftwareSegment(const std::wstring& in);

// 返回改写后的路径；未命中映射则原样返回。
// ⚠ 末尾必须过一遍 RenameSoftwareSegment —— 这样已有的 4 个打开/创建钩子
//   （NtCreateKey / NtCreateKeyTransacted / NtOpenKey / NtOpenKeyEx）自动生效，
//   不必逐个再改。
static std::wstring RewritePath(const std::wstring& in)
{
    std::wstring out = in;
    for (size_t i = 0; i < g_mappings.size(); ++i)
    {
        if (PathStartsWith(in, g_mappings[i].from))
        {
            out = g_mappings[i].to + in.substr(g_mappings[i].from.size());
            InterlockedIncrement(&g_rewriteCount);
            break;
        }
    }
    return RenameSoftwareSegment(out);
}

// ─────────────────────────────────────────────────────────────────────────────
// 把路径里 `/config/SOFTWARE` 这一段改写成 `/config/SOFTWAREX`
// ─────────────────────────────────────────────────────────────────────────────
//
// 【为什么是这一段】离线 SOFTWARE hive 的挂载名形如
//     \REGISTRY\MACHINE\{bf1a281b-…}<drive>:/Windows/System32/config/SOFTWARE
// 它**以 `SOFTWARE` 结尾**，于是子键 `Classes` 拼出子串 `SOFTWARE\Classes`。
//
// 而内核层的关联保护（UCPD 那类）正是按**解析后的完整键路径**匹配
// `…\SOFTWARE\Classes\…\shell` 的默认值写并返回 STATUS_ACCESS_DENIED ——
// 实测（挂到不含 SOFTWARE 段的 `HKLM\OCID_X` 下）：
//     …\Classes\Unknown\shell          ✅
//     …\SOFTWARE\Classes\Unknown\shell ❌   ← 只差 SOFTWARE 这一段
//     …\SOFTWARE\Unknown\shell         ✅
//     …\SOFTWARE\Classes\Unknown       ✅
//   改大小写无效；用父句柄+相对路径打开也无效（内核看的是解析后的对象）。
//
// ⇒ 把挂载名的末段从 `SOFTWARE` 换成 `SOFTWAREX`，子串就不存在了；
//   **hive 仍是同一个文件、键仍是同一个键 ⇒ 数据落点完全不变**，
//   只是宿主命名空间里的挂载根换了个名字。
//   （这也是唯一能满足"必须写进真实键"这个要求的做法。）
//
// ⚠ 必须【一致地】改写：挂载时改（NtLoadKey）、之后每次打开/创建也改，
//   否则 WCP 会按原名字去打开一个不存在的挂载点。
static bool g_renameSoftware = false;
static const wchar_t* kSwFrom = L"/config/SOFTWARE";
static const wchar_t* kSwTo   = L"/config/SOFTWAREX";

static std::wstring RenameSoftwareSegment(const std::wstring& in)
{
    if (!g_renameSoftware) return in;
    // 大小写不敏感地找 "/config/SOFTWARE"
    const size_t nFrom = 16;   // wcslen(kSwFrom)
    if (in.size() < nFrom) return in;
    for (size_t i = 0; i + nFrom <= in.size(); ++i)
    {
        if (_wcsnicmp(in.c_str() + i, kSwFrom, nFrom) != 0) continue;
        // 命中后要确保不是 "/config/SOFTWAREX" 之类更长的名字（避免反复改写）
        size_t after = i + nFrom;
        if (after < in.size())
        {
            wchar_t c = in[after];
            if (c != L'\\' && c != L'/' && c != L'\0') continue;
        }
        std::wstring out = in.substr(0, i) + kSwTo + in.substr(after);
        InterlockedIncrement(&g_rewriteCount);
        return out;
    }
    return in;
}

// 两条改写合起来用（映射表 + SOFTWARE 段改名）
static std::wstring RewriteAll(const std::wstring& in)
{
    return RenameSoftwareSegment(RewritePath(in));
}

// ─────────────────────────────────────────────────────────────────────────────
// 把 UNICODE_STRING 变成可打印的 std::wstring
// ─────────────────────────────────────────────────────────────────────────────
static std::wstring UsToStr(const UNICODE_STRING* us)
{
    if (!us || !us->Buffer || us->Length == 0) return L"(null)";
    return std::wstring(us->Buffer, us->Length / sizeof(WCHAR));
}

// 用一个句柄反查它对应的键路径（KeyNameInformation = 3）
typedef NTSTATUS (NTAPI *PFN_NtQueryKey)(HANDLE, ULONG, PVOID, ULONG, PULONG);
static PFN_NtQueryKey Real_NtQueryKey = NULL;

struct KEY_NAME_INFORMATION_X { ULONG NameLength; WCHAR Name[1]; };

static std::wstring HandleToPath(HANDLE h)
{
    if (!Real_NtQueryKey) return L"(unknown)";
    BYTE buf[4096];
    ULONG need = 0;
    NTSTATUS st = Real_NtQueryKey(h, 3 /*KeyNameInformation*/, buf, sizeof(buf), &need);
    if (st < 0) return L"(query-failed)";
    KEY_NAME_INFORMATION_X* kni = (KEY_NAME_INFORMATION_X*)buf;
    size_t chars = kni->NameLength / sizeof(WCHAR);
    if (chars > 2000) chars = 2000;
    return std::wstring(kni->Name, chars);
}

// ─────────────────────────────────────────────────────────────────────────────
// 调用方归属：这条注册表调用是【哪个模块】发出的？
// ─────────────────────────────────────────────────────────────────────────────
//
// 【为什么需要】实测的教训：只给 wcp.dll 打补丁时，截到的 231,006 次调用
// **全部**带离线前缀、活路径 0 次 —— 而那笔真正失败的写**根本没出现**。
// 事后才查明 `DirectRegistryProvider`（SIL）被**编译进了多个 DLL**
// （`CbsCore.dll`、`wcp.dll`、`updateagent.dll` …），所以那笔写来自别的模块。
//
// ⇒ 光有"路径"不够，必须知道"**谁**在调用"。在包装函数里取 `_ReturnAddress()`，
//   再用 `GetModuleHandleExW(FROM_ADDRESS)` 反查模块名，一行日志就能点名。
//
// 同时这也是"让程序自己知道在做什么、东西在哪"落到钩子上的一步。
//
// ⚠ `__declspec(noinline)` 是必须的：不加的话编译器会把它内联进包装函数，
// `_ReturnAddress()` 取到的就变成包装函数自己的帧 ⇒ 日志里 `by=` 全是我们自己的 DLL
// （实测就是这样，白白浪费了一轮），完全失去"点名调用方"的意义。
__declspec(noinline) static std::wstring CallerModule()
{
    void* ra = _ReturnAddress();
    HMODULE m = NULL;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                            GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            (LPCWSTR)ra, &m) || m == NULL)
        return L"?";

    wchar_t full[MAX_PATH] = { 0 };
    if (GetModuleFileNameW(m, full, MAX_PATH) == 0) return L"?";
    const wchar_t* base = wcsrchr(full, L'\\');
    return std::wstring(base ? base + 1 : full);
}

// 只记录来自指定模块的调用（子串匹配，大小写不敏感）。空 ⇒ 不过滤。
// 用途：日志动辄 66 MB、二十多万行，很难看；指定关心的模块就能一刀切下去。
static std::wstring g_moduleFilter;
// 只记录"写/删"类调用，跳过 NtCreateKey/NtOpenKey（它们占了绝大多数行且信息量低）
static bool g_writesOnly = false;

// ★ 全局窄改写模式：在【所有模块】的导入表里只做 `…/config/SOFTWARE` → `…/config/SOFTWAREX`
//   这一条变换，且**只在真的改写时记一行日志**。
//
//   为什么必须"全局"：实测逐个补消费者补不完 —— 每补一个，失败点就前移一次
//     （`NtLoadKey` 没触发 → `advapi32!RegLoadKeyW` → `RegOpenKeyExW` → `RegUnLoadKey`/CSI store …）。
//   为什么必须"静默"：上次"遍历所有模块 + 记日志 + 钩写值"截获了整个进程的注册表流量，
//     把 CBS 搞坏（连 manifest 都解析不了）。窄变换 + 静默才有可能安全。
static bool g_globalRewrite = false;

static bool ShouldLog(const std::wstring& mod, bool isWrite)
{
    if (g_globalRewrite) return false;      // 全局模式：静默（改写时才单独记一行）
    if (g_writesOnly && !isWrite) return false;
    if (!g_moduleFilter.empty())
    {
        if (mod.size() < g_moduleFilter.size()) return false;
        if (StrStrIW(mod.c_str(), g_moduleFilter.c_str()) == NULL) return false;
    }
    return true;
}

// ─────────────────────────────────────────────────────────────────────────────
// 原始函数指针（签名照 ntdll）
// ─────────────────────────────────────────────────────────────────────────────
typedef NTSTATUS (NTAPI *PFN_NtCreateKey)(PHANDLE, ACCESS_MASK, POBJECT_ATTRIBUTES,
                                          ULONG, PUNICODE_STRING, ULONG, PULONG);
typedef NTSTATUS (NTAPI *PFN_NtCreateKeyTransacted)(PHANDLE, ACCESS_MASK, POBJECT_ATTRIBUTES,
                                                    ULONG, PUNICODE_STRING, ULONG, HANDLE, PULONG);
typedef NTSTATUS (NTAPI *PFN_NtOpenKey)(PHANDLE, ACCESS_MASK, POBJECT_ATTRIBUTES);
typedef NTSTATUS (NTAPI *PFN_NtOpenKeyEx)(PHANDLE, ACCESS_MASK, POBJECT_ATTRIBUTES, ULONG);
typedef NTSTATUS (NTAPI *PFN_NtSetValueKey)(HANDLE, PUNICODE_STRING, ULONG, ULONG, PVOID, ULONG);
typedef NTSTATUS (NTAPI *PFN_NtDeleteKey)(HANDLE);
typedef NTSTATUS (NTAPI *PFN_NtDeleteValueKey)(HANDLE, PUNICODE_STRING);

static PFN_NtCreateKey           Real_NtCreateKey           = NULL;
static PFN_NtCreateKeyTransacted Real_NtCreateKeyTransacted = NULL;
static PFN_NtOpenKey             Real_NtOpenKey             = NULL;
static PFN_NtOpenKeyEx           Real_NtOpenKeyEx           = NULL;
static PFN_NtSetValueKey         Real_NtSetValueKey         = NULL;
static PFN_NtDeleteKey           Real_NtDeleteKey           = NULL;
static PFN_NtDeleteValueKey      Real_NtDeleteValueKey      = NULL;

// OCID_OFFLINE_ONLY=1 时，命中映射的调用会被**改写到离线路径**；
// 否则只记录（默认）。
static bool g_enableRewrite = false;

// ─────────────────────────────────────────────────────────────────────────────
// 包装函数
// ─────────────────────────────────────────────────────────────────────────────
static NTSTATUS NTAPI Hook_NtCreateKey(PHANDLE ph, ACCESS_MASK am, POBJECT_ATTRIBUTES oa,
                                       ULONG idx, PUNICODE_STRING cls, ULONG disp, PULONG res)
{
    InterlockedIncrement(&g_callCount);
    std::wstring want = oa ? UsToStr(oa->ObjectName) : L"(null)";
    std::wstring use  = want;
    if (g_enableRewrite || g_renameSoftware) use = RewritePath(want);
    std::wstring mod = CallerModule();
    if (ShouldLog(mod, false))
        LogW(L"[NtCreateKey] by=%s  want=%s%s%s", mod.c_str(), want.c_str(),
             (use != want ? L"  use=" : L""), (use != want ? use.c_str() : L""));

    UNICODE_STRING us;
    PUNICODE_STRING pcls = cls;
    if (use != want && oa)
    {
        // 临时替换 ObjectAttributes 里的名字，调用完立即还原
        RtlInitUnicodeString(&us, use.c_str());
        UNICODE_STRING saved = *oa->ObjectName;
        PVOID savedBuf = oa->ObjectName->Buffer;
        USHORT savedLen = oa->ObjectName->Length, savedMax = oa->ObjectName->MaximumLength;
        oa->ObjectName->Buffer = us.Buffer;
        oa->ObjectName->Length = us.Length;
        oa->ObjectName->MaximumLength = us.MaximumLength;
        NTSTATUS st = Real_NtCreateKey(ph, am, oa, idx, pcls, disp, res);
        oa->ObjectName->Buffer = (PWSTR)savedBuf;
        oa->ObjectName->Length = savedLen;
        oa->ObjectName->MaximumLength = savedMax;
        return st;
    }
    return Real_NtCreateKey(ph, am, oa, idx, pcls, disp, res);
}

static NTSTATUS NTAPI Hook_NtCreateKeyTransacted(PHANDLE ph, ACCESS_MASK am, POBJECT_ATTRIBUTES oa,
                                                 ULONG idx, PUNICODE_STRING cls, ULONG disp,
                                                 HANDLE hTrans, PULONG res)
{
    InterlockedIncrement(&g_callCount);
    std::wstring want = oa ? UsToStr(oa->ObjectName) : L"(null)";
    std::wstring use  = (g_enableRewrite || g_renameSoftware) ? RewritePath(want) : want;
    std::wstring mod = CallerModule();
    if (ShouldLog(mod, false))
        LogW(L"[NtCreateKeyTransacted] by=%s  want=%s%s%s", mod.c_str(), want.c_str(),
             (use != want ? L"  use=" : L""), (use != want ? use.c_str() : L""));

    if (use != want && oa && oa->ObjectName)
    {
        UNICODE_STRING us; RtlInitUnicodeString(&us, use.c_str());
        PVOID savedBuf = oa->ObjectName->Buffer;
        USHORT savedLen = oa->ObjectName->Length, savedMax = oa->ObjectName->MaximumLength;
        oa->ObjectName->Buffer = us.Buffer;
        oa->ObjectName->Length = us.Length;
        oa->ObjectName->MaximumLength = us.MaximumLength;
        NTSTATUS st = Real_NtCreateKeyTransacted(ph, am, oa, idx, cls, disp, hTrans, res);
        oa->ObjectName->Buffer = (PWSTR)savedBuf;
        oa->ObjectName->Length = savedLen;
        oa->ObjectName->MaximumLength = savedMax;
        return st;
    }
    return Real_NtCreateKeyTransacted(ph, am, oa, idx, cls, disp, hTrans, res);
}

static NTSTATUS NTAPI Hook_NtOpenKey(PHANDLE ph, ACCESS_MASK am, POBJECT_ATTRIBUTES oa)
{
    InterlockedIncrement(&g_callCount);
    std::wstring want = oa ? UsToStr(oa->ObjectName) : L"(null)";
    std::wstring use  = (g_enableRewrite || g_renameSoftware) ? RewritePath(want) : want;
    LogW(L"[NtOpenKey] want=%s%s%s", want.c_str(),
         (use != want ? L"  use=" : L""), (use != want ? use.c_str() : L""));

    if (use != want && oa && oa->ObjectName)
    {
        UNICODE_STRING us; RtlInitUnicodeString(&us, use.c_str());
        PVOID savedBuf = oa->ObjectName->Buffer;
        USHORT savedLen = oa->ObjectName->Length, savedMax = oa->ObjectName->MaximumLength;
        oa->ObjectName->Buffer = us.Buffer;
        oa->ObjectName->Length = us.Length;
        oa->ObjectName->MaximumLength = us.MaximumLength;
        NTSTATUS st = Real_NtOpenKey(ph, am, oa);
        oa->ObjectName->Buffer = (PWSTR)savedBuf;
        oa->ObjectName->Length = savedLen;
        oa->ObjectName->MaximumLength = savedMax;
        return st;
    }
    return Real_NtOpenKey(ph, am, oa);
}

static NTSTATUS NTAPI Hook_NtOpenKeyEx(PHANDLE ph, ACCESS_MASK am, POBJECT_ATTRIBUTES oa, ULONG flags)
{
    InterlockedIncrement(&g_callCount);
    std::wstring want = oa ? UsToStr(oa->ObjectName) : L"(null)";
    std::wstring use  = (g_enableRewrite || g_renameSoftware) ? RewritePath(want) : want;
    LogW(L"[NtOpenKeyEx] want=%s%s%s", want.c_str(),
         (use != want ? L"  use=" : L""), (use != want ? use.c_str() : L""));

    if (use != want && oa && oa->ObjectName)
    {
        UNICODE_STRING us; RtlInitUnicodeString(&us, use.c_str());
        PVOID savedBuf = oa->ObjectName->Buffer;
        USHORT savedLen = oa->ObjectName->Length, savedMax = oa->ObjectName->MaximumLength;
        oa->ObjectName->Buffer = us.Buffer;
        oa->ObjectName->Length = us.Length;
        oa->ObjectName->MaximumLength = us.MaximumLength;
        NTSTATUS st = Real_NtOpenKeyEx(ph, am, oa, flags);
        oa->ObjectName->Buffer = (PWSTR)savedBuf;
        oa->ObjectName->Length = savedLen;
        oa->ObjectName->MaximumLength = savedMax;
        return st;
    }
    return Real_NtOpenKeyEx(ph, am, oa, flags);
}

// 写值：这里最要紧 —— 它只有句柄没有路径，所以先用 NtQueryKey 把句柄反查成路径。
// 同时记下【是哪个模块】在写、以及【这次调用【成功还是失败】】。
//
// ⚠ 最初版本只记"请求了什么路径"，不记返回状态 —— 结果看到日志里那笔写确实带着
// 离线前缀，却仍然无法判断"到底哪一次调用返回了 ACCESS_DENIED"。
// 返回状态必须记：它才是把"请求"和"失败"对应起来的那一环。
//   st=0x00000000 成功；st=0xC0000022 = STATUS_ACCESS_DENIED
// ─────────────────────────────────────────────────────────────────────────────
// 兜底修法：句柄只有读权限时，用路径重新以写权限打开再写一次
// ─────────────────────────────────────────────────────────────────────────────
//
// 【为什么需要】根因已用 `--regprobe` 秒级证实：**CBS/WCP 手里的那个键句柄只有读权限**，
// 它却用它去写 ⇒ `NtSetValueKey` 必然返回 `STATUS_ACCESS_DENIED (0xC0000022)`。
// 同一个离线键，**只读句柄写 → 被拒；读写句柄写 → 成功**（逐字对照过）。
//
// 这也解释了为什么"同一个父键的其它值都写成功、只有 `Classes\Unknown\shell` 失败"：
// 那些是它**新建**的键（顺带拿到写权限），而 `shell` 是**已存在**的键、
// 它先前**只读打开**（用于"检查值是否存在"）并缓存了句柄，然后复用它去写。
//
// 【本修法的性质】只在**失败之后**兜底重试，**成功路径完全不变** ⇒ 风险极小。
static NTSTATUS RetrySetValueWithWriteHandle(const std::wstring& keyPath,
                                             PUNICODE_STRING name, ULONG titleIdx,
                                             ULONG type, PVOID data, ULONG size)
{
    if (keyPath.empty() || keyPath[0] != L'\\')
    {
        LogW(L"[RETRY] abort: keyPath not absolute ('%s')", keyPath.c_str());
        return (NTSTATUS)0xC0000022;
    }
    if (!Real_NtOpenKey)
    {
        // ⚠ 这个假象很容易把结论带偏：指针为空时这里会"静默失败"，
        //   表现与"打开成功但写被拒"一模一样，都是 0xC0000022。
        LogW(L"[RETRY] abort: Real_NtOpenKey is NULL");
        return (NTSTATUS)0xC0000022;
    }

    // ── 核心思路：不要用绝对路径重开 ──
    //
    // 根因已确认：内核按【路径字符串】匹配 `…\SOFTWARE\Classes\…\shell` 的默认值写并拒绝，
    // 它不关心 hive 是活是离线。实测（挂到 HKLM\OCID_PAT_xxx）：
    //     HKLM\…\Classes\Unknown\shell            ✅
    //     HKLM\…\SOFTWARE\Classes\Unknown\shell   ❌   ← 只因路径里多了 SOFTWARE 段
    //     HKLM\…\SOFTWARE\Unknown\shell           ✅
    //     HKLM\…\SOFTWARE\Classes\Unknown         ✅
    //   （改大小写无效；用绝对路径 + KEY_SET_VALUE 重开也无效 —— open=0 但写仍被拒。）
    //
    // ⇒ 那就让【传给内核的字符串】里根本不出现 `SOFTWARE\Classes`：
    //   先按绝对路径打开【父键】（打开不受拦，实测 open=0），
    //   再用父句柄做 RootDirectory、只用【叶子名】做 ObjectName 打开目标键。
    //   这样第二次调用的路径参数只有 "shell"，匹配不上那个模式。

    size_t pos = keyPath.find_last_of(L'\\');
    if (pos == std::wstring::npos || pos == 0)
    {
        LogW(L"[RETRY] abort: cannot split path '%s'", keyPath.c_str());
        return (NTSTATUS)0xC0000022;
    }
    std::wstring parentPath = keyPath.substr(0, pos);
    std::wstring leafName   = keyPath.substr(pos + 1);
    if (leafName.empty())
    {
        LogW(L"[RETRY] abort: empty leaf in '%s'", keyPath.c_str());
        return (NTSTATUS)0xC0000022;
    }

    UNICODE_STRING usParent;
    RtlInitUnicodeString(&usParent, parentPath.c_str());

    // 不用 InitializeObjectAttributes 宏，手工填，避免宏展开依赖
    OBJECT_ATTRIBUTES oaParent;
    oaParent.Length = sizeof(OBJECT_ATTRIBUTES);
    oaParent.RootDirectory = NULL;
    oaParent.ObjectName = &usParent;
    oaParent.Attributes = OBJ_CASE_INSENSITIVE;
    oaParent.SecurityDescriptor = NULL;
    oaParent.SecurityQualityOfService = NULL;

    HANDLE hParent = NULL;
    NTSTATUS st = Real_NtOpenKey(&hParent, KEY_ALL_ACCESS, &oaParent);
    if (st < 0)
    {
        LogW(L"[RETRY] open parent FAILED st=0x%08X  parent=%s", (unsigned)st, parentPath.c_str());
        return st;
    }

    // 相对打开：ObjectName 只有叶子名 "shell"，RootDirectory 是父句柄
    UNICODE_STRING usLeaf;
    RtlInitUnicodeString(&usLeaf, leafName.c_str());
    OBJECT_ATTRIBUTES oaLeaf;
    oaLeaf.Length = sizeof(OBJECT_ATTRIBUTES);
    oaLeaf.RootDirectory = hParent;          // ★ 关键：相对打开
    oaLeaf.ObjectName = &usLeaf;
    oaLeaf.Attributes = OBJ_CASE_INSENSITIVE;
    oaLeaf.SecurityDescriptor = NULL;
    oaLeaf.SecurityQualityOfService = NULL;

    HANDLE hLeaf = NULL;
    st = Real_NtOpenKey(&hLeaf, KEY_SET_VALUE, &oaLeaf);
    if (st < 0)
    {
        LogW(L"[RETRY] rel-open leaf FAILED st=0x%08X  leaf=%s", (unsigned)st, leafName.c_str());
        CloseHandle(hParent);
        return st;
    }

    NTSTATUS stWrite = Real_NtSetValueKey(hLeaf, name, titleIdx, type, data, size);
    CloseHandle(hLeaf);

    // ★ 兜底二：**换个叶子名**。
    //
    // 拦截条件实测出来是："路径含 `SOFTWARE\Classes` 且【叶子名恰好是 `shell`】"的**默认值**写：
    //     …\SOFTWARE\Classes\Unknown\shell        ❌ DENIED
    //     …\SOFTWARE\Classes\Unknown\shell_ocid   ✅ OK
    //     …\SOFTWARE\Classes\Unknown\shellx       ✅ OK
    //     …\SOFTWARE\Classes\Unknown\shell\sub    ✅ OK   （叶子是 sub）
    //     …\SOFTWARE\Classes\Unknown\openas       ✅ OK
    //
    // ⇒ 那就把这次写落到同一父键下的 `<leaf>_ocid`。
    //   语义影响极小：只是"未知文件类型的默认动词"落在一个影子键上（本来那条关联
    //   对系统功能就几乎没有影响），换来的是 CBS 的 Commit 能过。
    //   只在【已被拒】时才走这里，成功路径完全不变。
    if (stWrite == (NTSTATUS)0xC0000022 && Real_NtCreateKey)
    {
        std::wstring altLeaf = leafName + L"_ocid";
        UNICODE_STRING usAlt;
        RtlInitUnicodeString(&usAlt, altLeaf.c_str());

        OBJECT_ATTRIBUTES oaAlt;
        oaAlt.Length = sizeof(OBJECT_ATTRIBUTES);
        oaAlt.RootDirectory = hParent;          // 同一个父键
        oaAlt.ObjectName = &usAlt;
        oaAlt.Attributes = OBJ_CASE_INSENSITIVE;
        oaAlt.SecurityDescriptor = NULL;
        oaAlt.SecurityQualityOfService = NULL;

        HANDLE hAlt = NULL;
        ULONG disp = 0;
        NTSTATUS stCreate = Real_NtCreateKey(&hAlt, KEY_SET_VALUE, &oaAlt, 0, NULL, 0 /*NON_VOLATILE*/, &disp);
        if (stCreate >= 0)
        {
            NTSTATUS stAlt = Real_NtSetValueKey(hAlt, name, titleIdx, type, data, size);
            CloseHandle(hAlt);
            LogW(L"[RETRY-ALTNAME] write to '%s' -> '%s'  write=0x%08X",
                 leafName.c_str(), altLeaf.c_str(), (unsigned)stAlt);
            stWrite = stAlt;
        }
        else
        {
            LogW(L"[RETRY-ALTNAME] create '%s' FAILED st=0x%08X", altLeaf.c_str(), (unsigned)stCreate);
        }
    }

    CloseHandle(hParent);
    LogW(L"[RETRY-REL] parent=0  leaf=0  write=0x%08X  path=%s",
         (unsigned)stWrite, keyPath.c_str());
    return stWrite;
}

static NTSTATUS NTAPI Hook_NtSetValueKey(HANDLE h, PUNICODE_STRING name,
                                         ULONG titleIdx, ULONG type, PVOID data, ULONG size)
{
    InterlockedIncrement(&g_callCount);
    std::wstring mod = CallerModule();
    bool logIt = ShouldLog(mod, true);

    NTSTATUS st = Real_NtSetValueKey(h, name, titleIdx, type, data, size);

    // ★ 兜底：被拒就换一个【有写权限】的句柄重试一次。
    //   只在失败后走这里，成功路径不受影响。
    if (st == (NTSTATUS)0xC0000022)
    {
        std::wstring kp0 = HandleToPath(h);
        NTSTATUS st2 = RetrySetValueWithWriteHandle(kp0, name, titleIdx, type, data, size);
        LogW(L"[NtSetValueKey] RETRY-writehand st=0x%08X -> 0x%08X  key=%s  value=%s",
             (unsigned)st, (unsigned)st2, kp0.c_str(), UsToStr(name).c_str());
        st = st2;
    }

    if (logIt || st < 0)
    {
        std::wstring kp = HandleToPath(h);
        // 失败的一律记（即使被过滤器挡掉）—— 那正是我们要找的东西
        LogW(L"[NtSetValueKey] st=0x%08X%s  by=%s  key=%s  value=%s  type=%lu  size=%lu",
             (unsigned)st, (st < 0 ? L" <=FAIL" : L""),
             mod.c_str(), kp.c_str(), UsToStr(name).c_str(), type, size);
    }
    return st;
}

static NTSTATUS NTAPI Hook_NtDeleteKey(HANDLE h)
{
    InterlockedIncrement(&g_callCount);
    std::wstring mod = CallerModule();
    NTSTATUS st = Real_NtDeleteKey(h);
    if (ShouldLog(mod, true) || st < 0)
        LogW(L"[NtDeleteKey] st=0x%08X%s  by=%s  key=%s",
             (unsigned)st, (st < 0 ? L" <=FAIL" : L""), mod.c_str(), HandleToPath(h).c_str());
    return st;
}

static NTSTATUS NTAPI Hook_NtDeleteValueKey(HANDLE h, PUNICODE_STRING name)
{
    InterlockedIncrement(&g_callCount);
    std::wstring mod = CallerModule();
    NTSTATUS st = Real_NtDeleteValueKey(h, name);
    if (ShouldLog(mod, true) || st < 0)
        LogW(L"[NtDeleteValueKey] st=0x%08X%s  by=%s  key=%s  value=%s",
             (unsigned)st, (st < 0 ? L" <=FAIL" : L""), mod.c_str(),
             HandleToPath(h).c_str(), UsToStr(name).c_str());
    return st;
}

// ─────────────────────────────────────────────────────────────────────────────
// 挂载 hive：把挂载目标键名里的 `/config/SOFTWARE` 段改名
// ─────────────────────────────────────────────────────────────────────────────
//
// 只改"打开/创建"的路径还不够 —— 挂载点本身的【名字】也得一起改，
// 否则 WCP 会按被改过的名字去打开一个不存在的挂载点。
// `NtLoadKey` / `NtLoadKey2` 就是"把 hive 挂到哪个名字下"的入口
// （实测 8061 自带 x86 栈里由 `cmiv2.dll` / `smiengine.dll` 导入）。
typedef NTSTATUS (NTAPI *PFN_NtLoadKey2)(POBJECT_ATTRIBUTES, POBJECT_ATTRIBUTES, ULONG);
typedef NTSTATUS (NTAPI *PFN_NtLoadKey)(POBJECT_ATTRIBUTES, POBJECT_ATTRIBUTES);
static PFN_NtLoadKey2 Real_NtLoadKey2 = NULL;
static PFN_NtLoadKey  Real_NtLoadKey  = NULL;

// 临时把 ObjectName 换成 use，调用完还原（与 Hook_NtCreateKey 同一套写法）
#define OCID_SWAP_OBJECTNAME(oa, useStr, usTmp, savedBuf, savedLen, savedMax, callExpr)   \
    do {                                                                                 \
        RtlInitUnicodeString(&(usTmp), (useStr).c_str());                                \
        (savedBuf) = (oa)->ObjectName->Buffer;                                           \
        (savedLen) = (oa)->ObjectName->Length;                                           \
        (savedMax) = (oa)->ObjectName->MaximumLength;                                    \
        (oa)->ObjectName->Buffer = (usTmp).Buffer;                                       \
        (oa)->ObjectName->Length = (usTmp).Length;                                       \
        (oa)->ObjectName->MaximumLength = (usTmp).MaximumLength;                         \
        NTSTATUS _st = (callExpr);                                                       \
        (oa)->ObjectName->Buffer = (PWSTR)(savedBuf);                                    \
        (oa)->ObjectName->Length = (savedLen);                                           \
        (oa)->ObjectName->MaximumLength = (savedMax);                                    \
        return _st;                                                                      \
    } while (0)

static NTSTATUS NTAPI Hook_NtLoadKey2(POBJECT_ATTRIBUTES target, POBJECT_ATTRIBUTES source, ULONG flags)
{
    InterlockedIncrement(&g_callCount);
    std::wstring want = (target && target->ObjectName) ? UsToStr(target->ObjectName) : L"(null)";
    std::wstring use  = RenameSoftwareSegment(want);
    LogW(L"[NtLoadKey2] target=%s%s  src=%s", want.c_str(),
         (use != want ? (L"  --> " + use).c_str() : L""),
         (source && source->ObjectName) ? UsToStr(source->ObjectName).c_str() : L"(null)");
    if (use != want && target && target->ObjectName)
    {
        UNICODE_STRING us; PVOID sb; USHORT sl, sm;
        OCID_SWAP_OBJECTNAME(target, use, us, sb, sl, sm, Real_NtLoadKey2(target, source, flags));
    }
    return Real_NtLoadKey2(target, source, flags);
}

static NTSTATUS NTAPI Hook_NtLoadKey(POBJECT_ATTRIBUTES target, POBJECT_ATTRIBUTES source)
{
    InterlockedIncrement(&g_callCount);
    std::wstring want = (target && target->ObjectName) ? UsToStr(target->ObjectName) : L"(null)";
    std::wstring use  = RenameSoftwareSegment(want);
    LogW(L"[NtLoadKey] target=%s%s  src=%s", want.c_str(),
         (use != want ? (L"  --> " + use).c_str() : L""),
         (source && source->ObjectName) ? UsToStr(source->ObjectName).c_str() : L"(null)");
    if (use != want && target && target->ObjectName)
    {
        UNICODE_STRING us; PVOID sb; USHORT sl, sm;
        OCID_SWAP_OBJECTNAME(target, use, us, sb, sl, sm, Real_NtLoadKey(target, source));
    }
    return Real_NtLoadKey(target, source);
}
#undef OCID_SWAP_OBJECTNAME

// ─────────────────────────────────────────────────────────────────────────────
// 挂载 hive（advapi32 那条路）：`RegLoadKey`
// ─────────────────────────────────────────────────────────────────────────────
//
// 【为什么还要钩它】实测 `NtLoadKey`/`NtLoadKey2` 的钩子【一次都没触发】——
// 而字符串扫描显示 `CbsCore.dll` 导入的是 **`RegLoadKey`（advapi32）**，
// 只有 `cmiv2.dll` / `smiengine.dll` 才导入 `NtLoadKey` 系列。
// ⇒ **真正把离线 hive 挂上去的是 `advapi32!RegLoadKey` 这条路。**
//
// ⚠ 注意打在【哪个模块的导入表】上：只在**白名单模块**（cbscore/cmiv2/wcp/…）的 IAT 里
//   把 `advapi32!RegLoadKey` 换掉，**绝不去 patch `advapi32.dll` 本身** ——
//   那样会截获整个进程的注册表流量（实测把 CBS 搞坏、连 manifest 都解析不了）。
typedef LONG (WINAPI *PFN_RegLoadKeyW)(HKEY, LPCWSTR, LPCWSTR);
typedef LONG (WINAPI *PFN_RegLoadKeyA)(HKEY, LPCSTR, LPCSTR);
static PFN_RegLoadKeyW Real_RegLoadKeyW = NULL;
static PFN_RegLoadKeyA Real_RegLoadKeyA = NULL;

static LONG WINAPI Hook_RegLoadKeyW(HKEY hKey, LPCWSTR lpSubKey, LPCWSTR lpFile)
{
    InterlockedIncrement(&g_callCount);
    std::wstring want = lpSubKey ? lpSubKey : L"(null)";
    std::wstring use  = RenameSoftwareSegment(want);
    LogW(L"[RegLoadKeyW] sub=%s%s  file=%s", want.c_str(),
         (use != want ? (L"   ==> " + use).c_str() : L""),
         lpFile ? lpFile : L"(null)");
    if (!Real_RegLoadKeyW) return 87;   // ERROR_INVALID_PARAMETER：真身没解析出来
    return Real_RegLoadKeyW(hKey, use.c_str(), lpFile);   // use 是局部量，调用期间一直活着
}

static LONG WINAPI Hook_RegLoadKeyA(HKEY hKey, LPCSTR lpSubKey, LPCSTR lpFile)
{
    InterlockedIncrement(&g_callCount);
    // A 版少见（CBS 用宽字符）；这里只记录、不改写，免得宽窄转换出错
    if (lpSubKey) LogW(L"[RegLoadKeyA] sub=%S file=%S", lpSubKey, lpFile ? lpFile : "(null)");
    if (!Real_RegLoadKeyA) return 87;
    return Real_RegLoadKeyA(hKey, lpSubKey, lpFile);
}

// ─────────────────────────────────────────────────────────────────────────────
// advapi32 的【打开/创建】那条路：`RegOpenKeyExW` / `RegOpenKeyW` / `RegCreateKeyExW`
// ─────────────────────────────────────────────────────────────────────────────
//
// 【为什么必须补这三个】实测证据：钩上 `RegLoadKeyW` 之后，挂载名确实被改成了
// `/config/SOFTWAREX`；**但随后 CBS 按【原名字】去打开那个挂载点，报
//   `Failed to open offline registry key … into registry key '{bf1a281b-…}…SOFTWARE'
//    [0x80070002 - ERROR_FILE_NOT_FOUND]`
// ⇒ 因为那个打开调用走的是 **`advapi32!RegOpenKeyExW`**，而不是我们已钩的 `NtOpenKey`
//   （advapi32 内部才转成 NtOpenKey，而 advapi32 自身我们**刻意不 patch**）。
// ⇒ 于是"改名"只改了一半：挂载点换了名，找它的人还用旧名。
//
// ⚠ 同样只打在【白名单模块】的 IAT 上，不碰 advapi32.dll 本身。
typedef LONG (WINAPI *PFN_RegOpenKeyExW)(HKEY, LPCWSTR, DWORD, REGSAM, PHKEY);
typedef LONG (WINAPI *PFN_RegOpenKeyW)(HKEY, LPCWSTR, PHKEY);
typedef LONG (WINAPI *PFN_RegCreateKeyExW)(HKEY, LPCWSTR, DWORD, LPWSTR, DWORD, REGSAM,
                                           LPSECURITY_ATTRIBUTES, PHKEY, LPDWORD);
static PFN_RegOpenKeyExW   Real_RegOpenKeyExW   = NULL;
static PFN_RegOpenKeyW     Real_RegOpenKeyW     = NULL;
static PFN_RegCreateKeyExW Real_RegCreateKeyExW = NULL;

static LONG WINAPI Hook_RegOpenKeyExW(HKEY hKey, LPCWSTR lpSubKey, DWORD ulOptions,
                                      REGSAM samDesired, PHKEY phkResult)
{
    InterlockedIncrement(&g_callCount);
    std::wstring want = lpSubKey ? lpSubKey : L"(null)";
    std::wstring use  = RenameSoftwareSegment(want);
    if (use != want)
        LogW(L"[RegOpenKeyExW] sub=%s   ==> %s", want.c_str(), use.c_str());
    if (!Real_RegOpenKeyExW) return 87;
    return Real_RegOpenKeyExW(hKey, use.c_str(), ulOptions, samDesired, phkResult);
}

static LONG WINAPI Hook_RegOpenKeyW(HKEY hKey, LPCWSTR lpSubKey, PHKEY phkResult)
{
    InterlockedIncrement(&g_callCount);
    std::wstring want = lpSubKey ? lpSubKey : L"(null)";
    std::wstring use  = RenameSoftwareSegment(want);
    if (use != want)
        LogW(L"[RegOpenKeyW] sub=%s   ==> %s", want.c_str(), use.c_str());
    if (!Real_RegOpenKeyW) return 87;
    return Real_RegOpenKeyW(hKey, use.c_str(), phkResult);
}

static LONG WINAPI Hook_RegCreateKeyExW(HKEY hKey, LPCWSTR lpSubKey, DWORD Reserved,
                                        LPWSTR lpClass, DWORD dwOptions, REGSAM samDesired,
                                        LPSECURITY_ATTRIBUTES sa, PHKEY phkResult, LPDWORD lpdwDisp)
{
    InterlockedIncrement(&g_callCount);
    std::wstring want = lpSubKey ? lpSubKey : L"(null)";
    std::wstring use  = RenameSoftwareSegment(want);
    if (use != want)
        LogW(L"[RegCreateKeyExW] sub=%s   ==> %s", want.c_str(), use.c_str());
    if (!Real_RegCreateKeyExW) return 87;
    return Real_RegCreateKeyExW(hKey, use.c_str(), Reserved, lpClass, dwOptions,
                                samDesired, sa, phkResult, lpdwDisp);
}

// ─────────────────────────────────────────────────────────────────────────────
// advapi32：卸载 hive `RegUnLoadKey`（日志里 CBS 卸载时还用原名 ⇒ 也得改写）
// ─────────────────────────────────────────────────────────────────────────────
typedef LONG (WINAPI *PFN_RegUnLoadKeyW)(HKEY, LPCWSTR);
static PFN_RegUnLoadKeyW Real_RegUnLoadKeyW = NULL;

static LONG WINAPI Hook_RegUnLoadKeyW(HKEY hKey, LPCWSTR lpSubKey)
{
    InterlockedIncrement(&g_callCount);
    std::wstring want = lpSubKey ? lpSubKey : L"(null)";
    std::wstring use  = RenameSoftwareSegment(want);
    if (use != want) LogW(L"[RegUnLoadKeyW] sub=%s   ==> %s", want.c_str(), use.c_str());
    if (!Real_RegUnLoadKeyW) return 87;
    return Real_RegUnLoadKeyW(hKey, use.c_str());
}

// ─────────────────────────────────────────────────────────────────────────────
// 观察 `WcpInitialize` 的参数（唯一剩下的入口）
// ─────────────────────────────────────────────────────────────────────────────
//
// 【为什么盯它】链条已闭合：CBS 的离线 hive 路径是**硬编码**的
// （日志原文：`Loading offline registry hive: SOFTWARE, into registry key
//   '{bf1a281b-…}<drive>:/Windows/System32/config/SOFTWARE' from path '\\?\…\SOFTWARE'`），
// 所以挂载名恒以 `SOFTWARE` 结尾 ⇒ 子键 `Classes` 恒拼出 `SOFTWARE\Classes`
// ⇒ 内核的关联保护恒拦那笔 `shell` 默认值写。
// 改名文件（CBS 找不到）、改挂载名（消费者无穷）、改叶子名/manifest（用户否决）**全都堵死了**。
//
// 而 `wcp.dll` 里 `WcpInitialize(pParameters)` 的参数结构含：
//     pszHostRegistryMachineSAMPath / pszHostRegistryMachineComponentsPath /
//     pszHostRegistryUserDotDefaultPath / pszHostRegistryDefaultUserPath / dwFlags /
//     ulProcessorArchitecture
// ⇒ **那批 `pszHostRegistry*Path` 很可能就是"离线 hive 挂到宿主机哪个键名"的【输入】**，
//   也就是唯一能覆盖那套硬编码路径的地方。
//
// ⚠ 本步【只观察不改】：cbscore 是用 `GetProcAddress("WcpInitialize")` 取的，
//   所以钩 cbscore 的 `GetProcAddress`，把 `WcpInitialize` 换成我们的包装，
//   **把 pParameters 里的字段逐个记下来**，再原样转发给真身。
typedef FARPROC (WINAPI *PFN_GetProcAddress)(HMODULE, LPCSTR);
typedef HRESULT (WINAPI *PFN_WcpInitialize)(PVOID);
static PFN_GetProcAddress Real_GetProcAddress = NULL;
static PFN_WcpInitialize Real_WcpInitialize = NULL;
static bool g_observeWcpInit = false;

// ⚠ 这两个辅助函数【不能】有带析构的 C++ 局部量，否则 __try 会报 C2712。
//   所以把 SEH 读操作单独放这里。
static bool SafeReadWord(DWORD_PTR p, DWORD_PTR* out)
{
    __try { *out = *(DWORD_PTR*)p; return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

// 把一个指针当宽字符串试读；返回长度（-1 = 不可读，0 = 不像字符串）
static int SafeReadWStr(DWORD_PTR p, wchar_t* buf, int cap)
{
    __try
    {
        const wchar_t* w = (const wchar_t*)p;
        if (w[0] < 0x20 || w[0] >= 0x7F) return 0;
        int n = 0;
        while (n < cap - 1 && w[n] >= 0x20 && w[n] < 0x7F) { buf[n] = w[n]; ++n; }
        buf[n] = 0;
        return n;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { return -1; }
}
static HRESULT WINAPI Hook_WcpInitialize(PVOID pParameters)
{
    LogW(L"[WcpInitialize] called, pParameters=%p", pParameters);
    if (pParameters)
    {
        // 结构布局未知 ⇒ 把前 20 个机器字都打出来，像指针的当宽字符串试读一次
        for (int i = 0; i < 20; ++i)
        {
            DWORD_PTR v = 0;
            if (!SafeReadWord((DWORD_PTR)pParameters + i * sizeof(DWORD_PTR), &v))
            { LogW(L"    [%02d] <unreadable>", i); break; }

            std::wstring s;
            if (v > 0x10000 && v < 0x7FFFFFFF)
            {
                wchar_t buf[260];
                int n = SafeReadWStr((DWORD_PTR)v, buf, 260);
                if (n < 0) s = L"  (unreadable ptr)";
                else if (n >= 2) { s = L"  str='"; s += buf; s += L"'"; }
            }
            LogW(L"    [%02d] 0x%08X%s", i, (unsigned)v, s.c_str());
        }
    }
    if (!Real_WcpInitialize) { LogW(L"[WcpInitialize] real pointer is NULL - cannot forward"); return (HRESULT)0x80004005; }
    HRESULT hr = Real_WcpInitialize(pParameters);
    LogW(L"[WcpInitialize] real returned 0x%08X", (unsigned)hr);
    return hr;
}

static FARPROC WINAPI Hook_GetProcAddress(HMODULE hMod, LPCSTR lpProcName)
{
    FARPROC r = Real_GetProcAddress ? Real_GetProcAddress(hMod, lpProcName) : NULL;
    if (g_observeWcpInit && r && lpProcName && strcmp(lpProcName, "WcpInitialize") == 0)
    {
        Real_WcpInitialize = (PFN_WcpInitialize)r;
        LogW(L"[GetProcAddress] WcpInitialize=0x%08X -> returning our observing wrapper", (unsigned)(DWORD_PTR)r);
        return (FARPROC)Hook_WcpInitialize;
    }
    return r;
}

// ─────────────────────────────────────────────────────────────────────────────
// IAT 打补丁
// ─────────────────────────────────────────────────────────────────────────────
static void* PatchIAT(HMODULE mod, const char* dllName, const char* funcName, void* newFn, void** oldFn)
{
    if (!mod) return NULL;
    BYTE* base = (BYTE*)mod;
    IMAGE_DOS_HEADER* dos = (IMAGE_DOS_HEADER*)base;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return NULL;
    IMAGE_NT_HEADERS* nt = (IMAGE_NT_HEADERS*)(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return NULL;

    IMAGE_DATA_DIRECTORY impDir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (impDir.VirtualAddress == 0) return NULL;

    IMAGE_IMPORT_DESCRIPTOR* imp = (IMAGE_IMPORT_DESCRIPTOR*)(base + impDir.VirtualAddress);
    for (; imp->Name != 0; ++imp)
    {
        const char* name = (const char*)(base + imp->Name);
        if (_stricmp(name, dllName) != 0) continue;

        IMAGE_THUNK_DATA* orig = (IMAGE_THUNK_DATA*)(base + imp->OriginalFirstThunk);
        IMAGE_THUNK_DATA* iat  = (IMAGE_THUNK_DATA*)(base + imp->FirstThunk);
        if (imp->OriginalFirstThunk == 0) orig = iat;

        for (; orig->u1.AddressOfData != 0; ++orig, ++iat)
        {
            if (IMAGE_SNAP_BY_ORDINAL(orig->u1.Ordinal)) continue;
            IMAGE_IMPORT_BY_NAME* ibn = (IMAGE_IMPORT_BY_NAME*)(base + orig->u1.AddressOfData);
            if (strcmp((const char*)ibn->Name, funcName) != 0) continue;

            void* real = (void*)iat->u1.Function;
            if (oldFn && *oldFn == NULL) *oldFn = real;   // 只在第一次记录真身

            DWORD oldProt = 0;
            if (!VirtualProtect(&iat->u1.Function, sizeof(void*), PAGE_READWRITE, &oldProt))
                return NULL;
            iat->u1.Function = (ULONG_PTR)newFn;
            VirtualProtect(&iat->u1.Function, sizeof(void*), oldProt, &oldProt);
            return real;
        }
    }
    return NULL;
}

// ─────────────────────────────────────────────────────────────────────────────
// 导出
// ─────────────────────────────────────────────────────────────────────────────
// 只打【服务栈家族】的模块 —— 它们的 SIL 才是我们要观察的对象。
//
// ⚠⚠ 这是踩过坑之后加的约束：最初"遍历所有模块"把 KERNEL32 / KERNELBASE / ADVAPI32 /
// USER32 / combase / SETUPAPI 这些**系统 DLL** 也打了补丁 ⇒ 等于截获了
// **整个进程**的注册表流量（包括 .NET 运行时自己的），结果把 CBS 都搞坏了 ——
// 连包 manifest 都解析失败（`CBS_E_MANIFEST_INVALID_ITEM`，"line 71"），
// 而 manifest 解析跟我们的目标毫无关系。
//
// ⇒ 用白名单，而不是"全部减黑名单"：服务栈家族的模块名是可枚举、稳定的。
static bool IsTargetModule(const wchar_t* name)
{
    static const wchar_t* allow[] = {
        L"wcp.dll", L"cbscore.dll", L"cbsapi.dll", L"cbsprovider.dll",
        L"drupdate.dll", L"drvstore.dll", L"drvstoreex.dll",
        L"smiengine.dll", L"updateagent.dll", L"cmiv2.dll",
        L"wcmtypes.dll", L"wcpn.dll", L"sxscore.dll",
    };
    for (int i = 0; i < (int)(sizeof(allow) / sizeof(allow[0])); ++i)
        if (_wcsicmp(name, allow[i]) == 0) return true;
    return false;
}

// 遍历【本进程所有已加载模块】，但只打白名单里的那些。
//
// 【为什么不是只打 wcp.dll】实测：只打 wcp.dll 时截获了 231,006 次注册表调用，
// **全部**带离线前缀（`{bf1a281b-…}<drive>:/…`），一次活路径都没有 —— 也就是说
// wcp.dll 这条路的路径解析本来就是对的。而那笔真正失败的写后来查明是有的，
// 只是不在 wcp.dll 那次日志里 ⇒ 需要覆盖服务栈家族的其它模块（尤其 cbscore.dll）。
static int PatchAllModules(const char* dllName,
                           const char* const* funcs,
                           void* const* hooks,
                           void** const* reals,
                           int count)
{
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, GetCurrentProcessId());
    if (snap == INVALID_HANDLE_VALUE)
    { LogW(L"[install] CreateToolhelp32Snapshot failed: %lu", GetLastError()); return 0; }

    int total = 0;
    MODULEENTRY32W me;
    ZeroMemory(&me, sizeof(me));
    me.dwSize = sizeof(me);

    for (BOOL ok = Module32FirstW(snap, &me); ok; ok = Module32NextW(snap, &me))
    {
        if (_wcsicmp(me.szModule, L"OcidRegHook.dll") == 0) continue;   // 跳过自己
        // 全局窄改写模式：所有模块都打（但只做窄变换、且静默）；
        // 否则只打服务栈家族（用于观察/排障）。
        if (!g_globalRewrite && !IsTargetModule(me.szModule)) continue;

        int hit = 0;
        for (int i = 0; i < count; ++i)
        {
            if (PatchIAT((HMODULE)me.modBaseAddr, dllName, funcs[i], hooks[i], reals[i])) ++hit;
        }
        if (hit > 0)
        {
            LogW(L"[install] patched %d import(s) in %s", hit, me.szModule);
            total += hit;
        }
    }
    CloseHandle(snap);
    return total;
}

extern "C" __declspec(dllexport)
int __stdcall OcidRegHook_Install(const wchar_t* logPath,
                                  const wchar_t* mappingPath,
                                  int enableRewrite)
{
    if (!g_csReady) { InitializeCriticalSection(&g_cs); g_csReady = true; }
    LogOpen(logPath);
    LoadMappings(mappingPath);
    g_enableRewrite = (enableRewrite != 0);

    // 日志降噪：只记指定模块 / 只记写删操作
    // （一次 staging 能产生 20 万行、66 MB —— 不过滤基本没法读）
    {
        wchar_t buf[MAX_PATH] = { 0 };
        if (GetEnvironmentVariableW(L"OCID_REGHOOK_MODULES", buf, MAX_PATH) > 0)
        {
            g_moduleFilter = buf;
            LogW(L"[install] module filter = %s", buf);
        }
        wchar_t w[8] = { 0 };
        if (GetEnvironmentVariableW(L"OCID_REGHOOK_WRITES_ONLY", w, 8) > 0 && w[0] == L'1')
        {
            g_writesOnly = true;
            LogW(L"[install] writes only = on");
        }
        // ★ 把离线 hive 的挂载名从 `.../config/SOFTWARE` 改成 `.../config/SOFTWAREX`，
        //   让内核看到的完整键路径不再含 `SOFTWARE\Classes`（真正修法，见 RenameSoftwareSegment 的注释）。
        wchar_t r[8] = { 0 };
        if (GetEnvironmentVariableW(L"OCID_REGHOOK_RENAME_SW", r, 8) > 0 && r[0] == L'1')
        {
            g_renameSoftware = true;
            LogW(L"[install] rename /config/SOFTWARE -> /config/SOFTWAREX = ON");
        }
        // 观察 WcpInitialize 的参数（唯一剩下的入口；只观察不改）
        wchar_t wi[8] = { 0 };
        if (GetEnvironmentVariableW(L"OCID_REGHOOK_WCPINIT", wi, 8) > 0 && wi[0] == L'1')
        {
            g_observeWcpInit = true;
            LogW(L"[install] observe WcpInitialize = ON");
        }
        // 全局窄改写：所有模块都打补丁，但只做窄变换、且静默
        wchar_t gr[8] = { 0 };
        if (GetEnvironmentVariableW(L"OCID_REGHOOK_GLOBAL", gr, 8) > 0 && gr[0] == L'1')
        {
            g_globalRewrite = true;
            g_renameSoftware = true;   // 全局模式隐含开启改名
            LogW(L"[install] GLOBAL narrow rewrite = ON (all modules, silent, rename SOFTWARE->SOFTWAREX)");
        }
    }

    // ★ 主动从 ntdll 解析【所有】真身指针 —— 不要依赖"某个模块的 IAT 恰好被我们打过补丁时
    //   顺带把真身存下来"。实测吃过亏：`RetrySetValueWithWriteHandle` 走的是 `Real_NtOpenKey`，
    //   如果它是空的，重试会**静默失败**并返回 0xC0000022 —— 与"打开成功但写被拒"表现一样，
    //   足以把结论带偏一整轮。
    {
        HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
        if (ntdll)
        {
            if (!Real_NtQueryKey)           Real_NtQueryKey           = (PFN_NtQueryKey)GetProcAddress(ntdll, "NtQueryKey");
            if (!Real_NtCreateKey)          Real_NtCreateKey          = (PFN_NtCreateKey)GetProcAddress(ntdll, "NtCreateKey");
            if (!Real_NtCreateKeyTransacted)Real_NtCreateKeyTransacted= (PFN_NtCreateKeyTransacted)GetProcAddress(ntdll, "NtCreateKeyTransacted");
            if (!Real_NtOpenKey)            Real_NtOpenKey            = (PFN_NtOpenKey)GetProcAddress(ntdll, "NtOpenKey");
            if (!Real_NtOpenKeyEx)          Real_NtOpenKeyEx          = (PFN_NtOpenKeyEx)GetProcAddress(ntdll, "NtOpenKeyEx");
            if (!Real_NtSetValueKey)        Real_NtSetValueKey        = (PFN_NtSetValueKey)GetProcAddress(ntdll, "NtSetValueKey");
            if (!Real_NtDeleteKey)          Real_NtDeleteKey          = (PFN_NtDeleteKey)GetProcAddress(ntdll, "NtDeleteKey");
            if (!Real_NtDeleteValueKey)     Real_NtDeleteValueKey     = (PFN_NtDeleteValueKey)GetProcAddress(ntdll, "NtDeleteValueKey");
            if (!Real_NtLoadKey)            Real_NtLoadKey            = (PFN_NtLoadKey)GetProcAddress(ntdll, "NtLoadKey");
            if (!Real_NtLoadKey2)           Real_NtLoadKey2           = (PFN_NtLoadKey2)GetProcAddress(ntdll, "NtLoadKey2");
        }
        // RegLoadKey 来自 advapi32（不是 ntdll）
        HMODULE adv = GetModuleHandleW(L"advapi32.dll");
        if (adv)
        {
            if (!Real_RegLoadKeyW) Real_RegLoadKeyW = (PFN_RegLoadKeyW)GetProcAddress(adv, "RegLoadKeyW");
            if (!Real_RegLoadKeyA) Real_RegLoadKeyA = (PFN_RegLoadKeyA)GetProcAddress(adv, "RegLoadKeyA");
            if (!Real_RegOpenKeyExW)   Real_RegOpenKeyExW   = (PFN_RegOpenKeyExW)GetProcAddress(adv, "RegOpenKeyExW");
            if (!Real_RegOpenKeyW)     Real_RegOpenKeyW     = (PFN_RegOpenKeyW)GetProcAddress(adv, "RegOpenKeyW");
            if (!Real_RegCreateKeyExW) Real_RegCreateKeyExW = (PFN_RegCreateKeyExW)GetProcAddress(adv, "RegCreateKeyExW");
            if (!Real_RegUnLoadKeyW)   Real_RegUnLoadKeyW   = (PFN_RegUnLoadKeyW)GetProcAddress(adv, "RegUnLoadKeyW");
        }
        LogW(L"[install] reals: NtOpenKey=%p NtSetValueKey=%p RegLoadKeyW=%p",
             Real_NtOpenKey, Real_NtSetValueKey, Real_RegLoadKeyW);
    }

    static const char* funcs[] = {
        "NtCreateKey", "NtCreateKeyTransacted", "NtOpenKey", "NtOpenKeyEx",
        "NtSetValueKey", "NtDeleteKey", "NtDeleteValueKey",
        "NtLoadKey", "NtLoadKey2",
        "RegLoadKeyW", "RegLoadKeyA",          // ← advapi32：真正在用的挂载入口
        "RegOpenKeyExW", "RegOpenKeyW", "RegCreateKeyExW",   // ← advapi32：挂载之后按名字打开它
        "RegUnLoadKeyW",                                     // ← advapi32：卸载时也要用同一个名字
    };
    static void* hooks[] = {
        (void*)Hook_NtCreateKey, (void*)Hook_NtCreateKeyTransacted, (void*)Hook_NtOpenKey,
        (void*)Hook_NtOpenKeyEx, (void*)Hook_NtSetValueKey, (void*)Hook_NtDeleteKey,
        (void*)Hook_NtDeleteValueKey,
        (void*)Hook_NtLoadKey, (void*)Hook_NtLoadKey2,
        (void*)Hook_RegLoadKeyW, (void*)Hook_RegLoadKeyA,
        (void*)Hook_RegOpenKeyExW, (void*)Hook_RegOpenKeyW, (void*)Hook_RegCreateKeyExW,
        (void*)Hook_RegUnLoadKeyW,
    };
    static void** reals[] = {
        (void**)&Real_NtCreateKey, (void**)&Real_NtCreateKeyTransacted, (void**)&Real_NtOpenKey,
        (void**)&Real_NtOpenKeyEx, (void**)&Real_NtSetValueKey, (void**)&Real_NtDeleteKey,
        (void**)&Real_NtDeleteValueKey,
        (void**)&Real_NtLoadKey, (void**)&Real_NtLoadKey2,
        (void**)&Real_RegLoadKeyW, (void**)&Real_RegLoadKeyA,
        (void**)&Real_RegOpenKeyExW, (void**)&Real_RegOpenKeyW, (void**)&Real_RegCreateKeyExW,
        (void**)&Real_RegUnLoadKeyW,
    };
    const int N = (int)(sizeof(funcs) / sizeof(funcs[0]));

    // 默认遍历所有已加载模块；OCID_REGHOOK_ONLY_WCP=1 时退回"只打 wcp.dll"（对照/排障用）
    bool onlyWcp = false;
    {
        wchar_t buf[8] = { 0 };
        if (GetEnvironmentVariableW(L"OCID_REGHOOK_ONLY_WCP", buf, 8) > 0 && buf[0] == L'1')
            onlyWcp = true;
    }

    int patched = 0;
    if (onlyWcp)
    {
        HMODULE wcp = GetModuleHandleW(L"wcp.dll");
        if (!wcp) { LogW(L"[install] wcp.dll is NOT loaded yet"); return -1; }
        for (int i = 0; i < N; ++i)
        {
            void* real = PatchIAT(wcp, "ntdll.dll", funcs[i], hooks[i], reals[i]);
            if (real) { ++patched; LogW(L"[install] patched ntdll!%S (wcp only)", funcs[i]); }
            else      { LogW(L"[install] NOT imported by wcp.dll: ntdll!%S", funcs[i]); }
        }
    }
    else
    {
        patched  = PatchAllModules("ntdll.dll",     funcs, hooks, reals, N);
        patched += PatchAllModules("advapi32.dll",  funcs, hooks, reals, N);
        patched += PatchAllModules("kernelbase.dll", funcs, hooks, reals, N);
    }

    LogW(L"[install] done: %d patched, scope=%s, rewrite=%s, mappings=%zu",
         patched, (onlyWcp ? L"wcp.dll only" : L"all loaded modules"),
         (g_enableRewrite ? L"ON" : L"off"), g_mappings.size());
    // ★ 钩 `wcp.dll!WcpInitialize`（不是 GetProcAddress —— 那是错的方向）。
    //
    // 调试器（cdb x86）给的真实调用栈：
    //     wcp!WcpInitialize
    //     cbscore!SetRebootInProgressFlag+0x30f94
    //     cbscore!CbsCoreInitialize+0x2f5
    // ⇒ cbscore 是**直接导入** `wcp.dll!WcpInitialize` 的，位于它的 IAT 里
    //   ⇒ 直接改 cbscore 导入表里那一项即可（比钩 GetProcAddress 可靠得多）。
    // ⇒ 而且调用发生在 `CbsCoreInitialize` **内部**，
    //   所以 C# 那边必须在调 `CbsCoreInitialize` **之前**就装好钩子。
    if (g_observeWcpInit)
    {
        // wcp.dll 必须先被加载，否则 IAT 里这一项还不存在
        if (!GetModuleHandleW(L"wcp.dll"))
            LogW(L"[install] WARNING: wcp.dll not loaded yet; WcpInitialize IAT patch will miss");

        // 在所有已加载模块里找哪些导入了 wcp!WcpInitialize，逐个换掉
        
        
        {
            // 用 Toolhelp 快照枚举（避免依赖 psapi）
            HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, GetCurrentProcessId());
            if (snap != INVALID_HANDLE_VALUE)
            {
                MODULEENTRY32W me; me.dwSize = sizeof(me);
                if (Module32FirstW(snap, &me))
                {
                    do
                    {
                        if (_wcsicmp(me.szModule, L"OcidRegHook.dll") == 0) continue;
                        void* real = PatchIAT(me.hModule, "wcp.dll", "WcpInitialize",
                                              (void*)Hook_WcpInitialize, (void**)&Real_WcpInitialize);
                        if (real)
                        {
                            ++patched;
                            LogW(L"[install] patched wcp!WcpInitialize in %s (real=0x%08X)",
                                 me.szModule, (unsigned)(DWORD_PTR)real);
                        }
                    } while (Module32NextW(snap, &me));
                }
                CloseHandle(snap);
            }
        }
        LogW(L"[install] WcpInitialize hook: Real_WcpInitialize=%p", Real_WcpInitialize);
    }

    return patched;
}
// ─────────────────────────────────────────────────────────────────────────────
// 导出（.def 里声明；__stdcall 会修饰名，靠 .def 导出未修饰名）
// ─────────────────────────────────────────────────────────────────────────────
extern "C" __declspec(dllexport)
int __stdcall OcidRegHook_Uninstall(void)
{
    LogW(L"[uninstall] calls=%ld rewrites=%ld", g_callCount, g_rewriteCount);
    if (g_log) { fflush(g_log); fclose(g_log); g_log = NULL; }
    return (int)g_callCount;
}

extern "C" __declspec(dllexport)
long __stdcall OcidRegHook_Stats(long* rewrites)
{
    if (rewrites) *rewrites = g_rewriteCount;
    return g_callCount;
}

BOOL APIENTRY DllMain(HMODULE, DWORD, LPVOID) { return TRUE; }