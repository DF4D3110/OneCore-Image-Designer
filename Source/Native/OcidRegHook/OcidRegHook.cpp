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

// 返回改写后的路径；未命中映射则原样返回
static std::wstring RewritePath(const std::wstring& in)
{
    for (size_t i = 0; i < g_mappings.size(); ++i)
    {
        if (PathStartsWith(in, g_mappings[i].from))
        {
            std::wstring out = g_mappings[i].to + in.substr(g_mappings[i].from.size());
            InterlockedIncrement(&g_rewriteCount);
            return out;
        }
    }
    return in;
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

static bool ShouldLog(const std::wstring& mod, bool isWrite)
{
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
    if (g_enableRewrite) use = RewritePath(want);
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
    std::wstring use  = g_enableRewrite ? RewritePath(want) : want;
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
    std::wstring use  = g_enableRewrite ? RewritePath(want) : want;
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
    std::wstring use  = g_enableRewrite ? RewritePath(want) : want;
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
        if (!IsTargetModule(me.szModule)) continue;                     // 只打服务栈家族

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
        }
        LogW(L"[install] reals: NtQueryKey=%p NtOpenKey=%p NtSetValueKey=%p NtCreateKey=%p",
             Real_NtQueryKey, Real_NtOpenKey, Real_NtSetValueKey, Real_NtCreateKey);
    }

    static const char* funcs[] = {
        "NtCreateKey", "NtCreateKeyTransacted", "NtOpenKey", "NtOpenKeyEx",
        "NtSetValueKey", "NtDeleteKey", "NtDeleteValueKey",
    };
    static void* hooks[] = {
        (void*)Hook_NtCreateKey, (void*)Hook_NtCreateKeyTransacted, (void*)Hook_NtOpenKey,
        (void*)Hook_NtOpenKeyEx, (void*)Hook_NtSetValueKey, (void*)Hook_NtDeleteKey,
        (void*)Hook_NtDeleteValueKey,
    };
    static void** reals[] = {
        (void**)&Real_NtCreateKey, (void**)&Real_NtCreateKeyTransacted, (void**)&Real_NtOpenKey,
        (void**)&Real_NtOpenKeyEx, (void**)&Real_NtSetValueKey, (void**)&Real_NtDeleteKey,
        (void**)&Real_NtDeleteValueKey,
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
    return patched;
}

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
