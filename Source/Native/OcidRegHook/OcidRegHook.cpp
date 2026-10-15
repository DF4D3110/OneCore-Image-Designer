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
    LogW(L"[NtCreateKey] want=%s%s%s", want.c_str(),
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
    LogW(L"[NtCreateKeyTransacted] want=%s%s%s", want.c_str(),
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
static NTSTATUS NTAPI Hook_NtSetValueKey(HANDLE h, PUNICODE_STRING name,
                                         ULONG titleIdx, ULONG type, PVOID data, ULONG size)
{
    InterlockedIncrement(&g_callCount);
    std::wstring kp = HandleToPath(h);
    LogW(L"[NtSetValueKey] key=%s  value=%s  type=%lu  size=%lu",
         kp.c_str(), UsToStr(name).c_str(), type, size);
    return Real_NtSetValueKey(h, name, titleIdx, type, data, size);
}

static NTSTATUS NTAPI Hook_NtDeleteKey(HANDLE h)
{
    InterlockedIncrement(&g_callCount);
    LogW(L"[NtDeleteKey] key=%s", HandleToPath(h).c_str());
    return Real_NtDeleteKey(h);
}

static NTSTATUS NTAPI Hook_NtDeleteValueKey(HANDLE h, PUNICODE_STRING name)
{
    InterlockedIncrement(&g_callCount);
    LogW(L"[NtDeleteValueKey] key=%s  value=%s", HandleToPath(h).c_str(), UsToStr(name).c_str());
    return Real_NtDeleteValueKey(h, name);
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
// 遍历【本进程所有已加载模块】逐个打补丁。
//
// 【为什么不是只打 wcp.dll】实测：只打 wcp.dll 时截获了 231,006 次注册表调用，
// **全部**带离线前缀（`{bf1a281b-…}<drive>:/…`），一次活路径都没有 —— 也就是说
// wcp.dll 这条路的路径解析本来就是对的。而那笔真正失败的写
// （`SOFTWARE\Classes\Unknown\shell` 默认值）**根本没出现在日志里**；
// 失败栈是 SIL 的 `DirectRegistryProvider::SysSetValueKey`
// ⇒ 那次调用是从**别的模块**发出的（cbscore.dll / 其它 SIL 组件 / 或 NtCreateKeyTransacted）。
// ⇒ 所以必须遍历所有模块。
//
// 注意：跳过我们自己（我们最终调的是保存下来的真身指针，正常情况下不会绕回来，
// 但跳过能彻底避免递归的隐患）。
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

    // 先拿到 NtQueryKey 的真身（句柄反查路径要用）
    if (!Real_NtQueryKey)
    {
        HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
        Real_NtQueryKey = (PFN_NtQueryKey)GetProcAddress(ntdll, "NtQueryKey");
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
