// OCID - CSI 落位计划 + 文件 ACL 自检
//
// 语料来自**真实清单的形状**（8061 ARM 构建产物镜像里实测到的写法），不是编的：
//   · `destinationPath="$(runtime.system32)\"`（132 处，最多）
//   · `destinationPath="$(Runtime.System32)\Dism\"`（32 处，**大写 R**，必须大小写不敏感）
//   · `destinationPath="$(build.nttree)\"`（**不是**路径宏，展开不了 ⇒ 这条不能执行）
//   · `<file name="ax88772.INF" sourceName="ax88772.INF" importPath="$(build.nttree)\driver_infs\">`
//     （**没有** destinationPath —— 驱动类，落位规则不同）
//   · `<secureityDescriptor name=…>` 嵌在 `<file>` / `<directory>` 里，SDDL 原文在
//     `<trustInfo><security><accessControl><securityDescriptorDefinition name= sddl=>`
//
// 落盘那半（宏展开 + 硬链接 + SDDL）见 `ocid/FilePlacement.h` 顶部的实测证据。

#include <windows.h>

#include "ocid/ConsoleUtf8.h"
#include "ocid/FilePlacement.h"
#include "ocid/Manifest.h"
#include "ocid/SecurityDescriptor.h"

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

static void CheckInt(long long got, long long want, const char* what)
{
    if (got == want) { ++g_pass; printf("  [ok]   %s\n", what); }
    else { ++g_fail; printf("  [FAIL] %s (got %lld, want %lld)\n", what, got, want); }
}

static void CheckStr(const std::wstring& got, const std::wstring& want, const char* what)
{
    if (got == want) { ++g_pass; printf("  [ok]   %s\n", what); }
    else
    {
        ++g_fail;
        printf("  [FAIL] %s\n         got  = %s\n         want = %s\n",
               what, ToUtf8(got).c_str(), ToUtf8(want).c_str());
    }
}

static bool Contains(const std::wstring& hay, const wchar_t* needle)
{
    return hay.find(needle) != std::wstring::npos;
}

/// 路径比较**不区分大小写**：宏表给的是 `$(runtime.system32)` → `<windows>\system32`
/// （小写，与语料里的宏定义一致），而真实镜像里那个目录的拼写是 `System32`
/// （setup 建的）。NTFS 不区分大小写，两者是同一个位置 —— 逐字符比大小写会假失败。
static void CheckPath(const std::wstring& got, const std::wstring& want, const char* what)
{
    if (_wcsicmp(got.c_str(), want.c_str()) == 0) { ++g_pass; printf("  [ok]   %s\n", what); }
    else
    {
        ++g_fail;
        printf("  [FAIL] %s\n         got  = %s\n         want = %s\n",
               what, ToUtf8(got).c_str(), ToUtf8(want).c_str());
    }
}

// ── 夹具：一份形状真实的组件清单 ───────────────────────────────────────────
// 注意：夹具里含 `…BA)"` 这种序列，普通 `R"(…)"` 会在那里**提前终止** ——
// 必须自定义定界符。
static const wchar_t* MANIFEST_XML = LR"OCIDXML(<?xml version="1.0" encoding="utf-8"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v3" manifestVersion="1.0">
  <assemblyIdentity name="Microsoft-Windows-Basesrv" version="6.2.8061.0"
                    processorArchitecture="arm" language="neutral"
                    publicKeyToken="31bf3856ad364e35" buildType="release" />
  <trustInfo>
    <security>
      <accessControl>
        <securityDescriptorDefinitions>
          <securityDescriptorDefinition name="WRP_FILE_DEFAULT_SDDL"
              sddl="O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)" />
          <securityDescriptorDefinition name="PUBLIC_DESKTOP_SDDL"
              sddl="D:P(A;;GA;;;SY)(A;;GR;;;BA)(A;;GR;;;BU)" />
        </securityDescriptorDefinitions>
      </accessControl>
    </security>
  </trustInfo>
  <file name="basesrv.dll" destinationPath="$(runtime.system32)\"
        sourceName="basesrv.dll" sourcePath=".\">
    <securityDescriptor name="WRP_FILE_DEFAULT_SDDL" />
  </file>
  <file name="DismCore.dll" destinationPath="$(Runtime.System32)\Dism\"
        sourceName="DismCore.dll" />
  <file name="kdcom.dll" destinationPath="$(build.nttree)\" sourceName="kdcom.dll" />
  <file name="ax88772.INF" sourceName="ax88772.INF" importPath="$(build.nttree)\driver_infs\" />
  <file name="win.ini" destinationPath="$(runtime.systemRoot)\"
        sourceName="win.ini" writeableType="systemData" />
  <directories>
    <directory destinationPath="$(runtime.public)\" owner="true" attributes="readOnly" />
    <directory destinationPath="$(runtime.public)\Desktop\" owner="true" attributes="readOnly hidden">
      <securityDescriptor name="PUBLIC_DESKTOP_SDDL" />
    </directory>
    <directory destinationPath="$(runtime.system32)\NoSuchSd\">
      <securityDescriptor name="THIS_NAME_IS_NOT_DEFINED" />
    </directory>
  </directories>
</assembly>
)OCIDXML";

static const wchar_t* COMP_DIR =
    L"arm_microsoft-windows-basesrv_31bf3856ad364e35_6.2.8061.0_none_93d3f64c57474751";

static std::wstring TempDir(const wchar_t* leaf)
{
    wchar_t buf[MAX_PATH] = {};
    ::GetTempPathW(MAX_PATH, buf);
    std::wstring p = buf;
    if (!p.empty() && p.back() == L'\\') p.pop_back();
    p += L"\\ocid_place_test_";
    p += leaf;
    return p;
}

static void RmTree(const std::wstring& dir)
{
    // 只用一次一层的删除（测试自己造的结构是扁平的）
    WIN32_FIND_DATAW fd;
    std::wstring pat = dir + L"\\*";
    HANDLE h = ::FindFirstFileW(pat.c_str(), &fd);
    if (h != INVALID_HANDLE_VALUE)
    {
        do {
            if (wcscmp(fd.cFileName, L".") == 0 || wcscmp(fd.cFileName, L"..") == 0) continue;
            std::wstring p = dir + L"\\" + fd.cFileName;
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
                RmTree(p);
                ::RemoveDirectoryW(p.c_str());
            } else {
                ::SetFileAttributesW(p.c_str(), FILE_ATTRIBUTE_NORMAL);
                ::DeleteFileW(p.c_str());
            }
        } while (::FindNextFileW(h, &fd));
        ::FindClose(h);
    }
    ::RemoveDirectoryW(dir.c_str());
}

// ── ① 落位计划 ─────────────────────────────────────────────────────────────
static void TestPlan()
{
    printf("\n== ① 落位计划（真实清单形状）==\n");

    ComponentManifest m;
    auto r = ParseComponentManifest(MANIFEST_XML, m);
    Check(r.ok, "夹具清单解析成功");
    if (!r.ok) { printf("       err=%s\n", ToUtf8(r.error).c_str()); return; }

    CheckInt((long long)m.files.size(), 5, "解析到 5 个 <file>");
    CheckInt((long long)m.securityDescriptorDefinitions.size(), 2, "解析到 2 条 SD 定义");

    const std::wstring pkgRoot = TempDir(L"pkg");
    const PlacementPlan plan = BuildPlacementPlan(m, COMP_DIR, pkgRoot, std::wstring(L"P:\\Windows"));

    CheckStr(plan.componentDir, COMP_DIR, "组件目录名带出来");
    CheckStr(plan.storeDir, std::wstring(L"P:\\Windows\\WinSxS\\") + COMP_DIR,
             "正本目录 = Windows\\WinSxS\\<组件目录>");

    // 只有 4 条有 destinationPath（Inf 那条没有）
    CheckInt((long long)plan.files.size(), 4, "计划里 4 条 <file>（无 destinationPath 的不进来）");
    CheckInt((long long)plan.filesWithoutDestination, 1, "1 条无 destinationPath（驱动类）");
    CheckInt((long long)plan.sddlUnresolved, 1, "1 条 SD 引用查不到（必须显式记下来）");
    CheckInt((long long)plan.unresolvedCount, 1, "1 条含展开不了的宏（$(build.nttree)）");
    Check(!plan.ok(), "有未展开宏 ⇒ ok()==false（不能当成功）");
    CheckInt((long long)plan.executableFiles(), 3, "可执行的三条（宏全展开且有落点）");

    const FilePlacementOp& f0 = plan.files[0];
    CheckStr(f0.name, L"basesrv.dll", "① name");
    CheckPath(f0.destinationPath, L"P:\\Windows\\System32\\basesrv.dll",
              "① 落点 = $(runtime.system32)\\ + name");
    CheckStr(f0.storePath, plan.storeDir + L"\\basesrv.dll", "① 正本 = <storeDir>\\name");
    CheckStr(f0.payloadPath, pkgRoot + L"\\" + COMP_DIR + L"\\basesrv.dll",
             "① 载荷 = <packages>\\<组件目录>\\<sourceName>");
    CheckStr(f0.sddl, L"O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)", "① 内嵌 SD 引用解析成 SDDL 原文");
    CheckStr(f0.sdName, L"WRP_FILE_DEFAULT_SDDL", "① 引用名带出来");
    CheckStr(f0.unresolved, L"", "① 宏全展开");
    Check(!f0.writeable, "① 没写 writeableType ⇒ 是硬链接");
    CheckStr(f0.writeableType, L"", "① writeableType 原样带出（空）");
    CheckInt((long long)plan.missingPayload, 4, "载荷都不存在（还没造包展开目录）");

    const FilePlacementOp& f1 = plan.files[1];
    CheckPath(f1.destinationPath, L"P:\\Windows\\System32\\Dism\\DismCore.dll",
              "② $(Runtime.System32) 大写写法也要展开对");

    const FilePlacementOp& f2 = plan.files[2];
    CheckStr(f2.unresolved, L"$(build.nttree)", "③ 未展开的宏被记下来（不是静默替换成空串）");
    Check(Contains(f2.destinationPath, L"$(build.nttree)"), "③ 未展开的宏**原样保留**在落点里");

    // 目录
    CheckInt((long long)plan.directories.size(), 3, "3 条 <directory>");
    const DirectoryPlacementOp& d0 = plan.directories[0];
    CheckStr(d0.path, L"P:\\Users\\Public", "目录路径去掉了尾随反斜杠");
    Check(d0.owner, "目录 owner=\"true\" 带出来");
    CheckStr(d0.attributes, L"readOnly", "目录 attributes 原样带出");

    const DirectoryPlacementOp& d1 = plan.directories[1];
    CheckStr(d1.path, L"P:\\Users\\Public\\Desktop", "带 SD 的目录路径");
    CheckStr(d1.sddl, L"D:P(A;;GA;;;SY)(A;;GR;;;BA)(A;;GR;;;BU)", "目录内嵌 SD 解析成原文");

    const DirectoryPlacementOp& d2 = plan.directories[2];
    CheckStr(d2.sddl, L"", "查不到的 SD 引用 ⇒ sddl 为空（不许瞎编）");
    CheckStr(d2.sdName, L"THIS_NAME_IS_NOT_DEFINED", "但引用名要留着，便于定位");

    // notes 必须能看到"为什么这几条没落位"
    bool sawNoDest = false, sawBadSd = false;
    for (const auto& n : plan.notes) {
        if (Contains(n, L"无 destinationPath")) sawNoDest = true;
        if (Contains(n, L"安全描述符引用解析失败")) sawBadSd = true;
    }
    Check(sawNoDest, "notes 说明为什么那条驱动文件不落位");
    Check(sawBadSd, "notes 说明哪条 SD 引用没查到");

    // ④ writeableType="systemData" ⇒ 落点是**拷贝**，不是硬链接
    //    （实测：这 6 个文件 `fsutil hardlink list` 只有 1 条链接）
    const FilePlacementOp& f3 = plan.files[3];
    CheckStr(f3.name, L"win.ini", "④ writeableType 那条是 win.ini");
    Check(f3.writeable, "④ writeableType=systemData ⇒ writeable=true（落位时要拷贝）");
    CheckStr(f3.writeableType, L"systemData", "④ writeableType 原值带出来");
    CheckPath(f3.destinationPath, L"P:\\Windows\\win.ini", "④ 落点 = $(runtime.systemRoot)\\ + name");
    CheckInt((long long)plan.writeableFiles, 1, "④ 计划里 1 条需要拷贝");
}

// ── ③b 架构红移（System32 ⇒ SysWOW64）───────────────────────────────────────
// 实测依据：8061 ARM 镜像（内核 PE machine=0x01C4=ARMNT）里，
//   arm_ 组件的 $(Runtime.System32)\Dism\ ⇒ Windows\System32\Dism\
//   x86_ 组件**同一句声明**          ⇒ Windows\SysWOW64\Dism\
// 已落位的 206 条里 27 条"落点≠正本"，由"红移 21 条 + writeableType 6 条"解释干净。
static void TestArchRedirection()
{
    printf("\n== ③b 架构红移 System32 ⇒ SysWOW64 ==\n");

    ComponentManifest m;
    auto r = ParseComponentManifest(MANIFEST_XML, m);
    Check(r.ok, "夹具清单解析成功");
    if (!r.ok) return;

    const std::wstring pkgRoot = TempDir(L"pkg_arch");
    const std::wstring win     = L"P:\\Windows";
    const std::wstring X86_DIR =
        L"x86_microsoft-windows-basesrv_31bf3856ad364e35_6.2.8061.0_none_93d3f64c57474751";

    // 组件目录名的架构前缀
    CheckStr(ComponentArchOf(COMP_DIR), L"arm", "ComponentArchOf(arm_…) = arm");
    CheckStr(ComponentArchOf(X86_DIR), L"x86", "ComponentArchOf(x86_…) = x86");
    CheckStr(ComponentArchOf(L"neutral"), L"", "没有下划线 ⇒ 空（不猜）");
    CheckStr(ComponentArchOf(L"_leading"), L"", "下划线在开头 ⇒ 空（不猜）");

    // 只列**实测确认**的组合；没量过的必须为 false（宁可不红移，也不把猜测当事实）
    Check(IsWow64Redirected(L"x86", L"arm"), "实测组合：arm 镜像上的 x86 组件 ⇒ 红移");
    Check(!IsWow64Redirected(L"arm", L"arm"), "arm 组件在 arm 镜像上 ⇒ 不红移");
    Check(!IsWow64Redirected(L"x86", L"x86"), "x86 组件在 x86 镜像上 ⇒ 不红移");
    Check(!IsWow64Redirected(L"arm", L"arm64"), "arm 组件在 arm64 镜像上：**未测量**，按不红移");
    Check(!IsWow64Redirected(L"", L"arm"), "架构未知 ⇒ 不红移");
    Check(!IsWow64Redirected(L"x86", L""), "镜像架构未知 ⇒ 不红移");

    // ① 原生架构：原样落 System32
    {
        const PlacementPlan p = BuildPlacementPlan(m, COMP_DIR, pkgRoot, win, L"arm");
        CheckInt((long long)p.redirectedFiles, 0, "arm 组件 ⇒ 0 条红移");
        Check(!p.files[0].redirected, "arm 组件的 basesrv.dll 没被改写");
        CheckPath(p.files[0].destinationPath, L"P:\\Windows\\System32\\basesrv.dll",
                  "arm 组件落 System32");
        CheckStr(p.componentArch, L"arm", "计划里带出组件架构");
        CheckStr(p.imageArch, L"arm", "计划里带出镜像架构");
    }

    // ② 非原生架构（实测那一格）：System32 ⇒ SysWOW64
    {
        const PlacementPlan p = BuildPlacementPlan(m, X86_DIR, pkgRoot, win, L"arm");
        CheckInt((long long)p.redirectedFiles, 2, "x86 组件在 arm 镜像上 ⇒ 2 条红移（basesrv/DismCore）");
        Check(p.files[0].redirected, "basesrv.dll 被标记红移");
        CheckPath(p.files[0].destinationPath, L"P:\\Windows\\SysWOW64\\basesrv.dll",
                  "System32 ⇒ SysWOW64（实测 DismCore 就是这个走向）");
        CheckPath(p.files[1].destinationPath, L"P:\\Windows\\SysWOW64\\Dism\\DismCore.dll",
                  "System32 下的子目录一起红移");
        // 没展开的宏（$(build.nttree)）不以 System32 开头 ⇒ 不该被改写
        Check(!p.files[2].redirected, "不是 System32 开头的落点不动");
        // win.ini 在 Windows\ 根（$(runtime.systemRoot)）⇒ 不红移（实测它在 System32 外）
        Check(!p.files[3].redirected, "$(runtime.systemRoot) 落点不红移");
        CheckPath(p.files[3].destinationPath, L"P:\\Windows\\win.ini",
                  "SystemRoot 落点保持原样");
        // 正本目录**不**红移（WinSxS 是各架构并列的，实测如此）
        CheckStr(p.files[0].storePath, p.storeDir + L"\\basesrv.dll", "正本不受红移影响");
    }

    // ③ 未测量组合：不红移（并把"为什么"留在注释里）
    {
        const PlacementPlan p = BuildPlacementPlan(m, X86_DIR, pkgRoot, win, L"x86");
        CheckInt((long long)p.redirectedFiles, 0, "x86 组件在 x86 镜像上 ⇒ 0 条红移");
    }

    // ④ 组件目录名没有架构前缀时要**说出来**（否则就是悄悄按 System32 落位）
    {
        const PlacementPlan p = BuildPlacementPlan(m, L"neutral", pkgRoot, win, L"arm");
        bool noted = false;
        for (const auto& n : p.notes) if (Contains(n, L"没有架构前缀")) noted = true;
        Check(noted, "架构推不出来 ⇒ notes 里有一条说明");
        CheckInt((long long)p.redirectedFiles, 0, "推不出来 ⇒ 不红移");
    }
}

// ── ② 载荷存在性 ───────────────────────────────────────────────────────────
static void TestPayloadProbe()
{
    printf("\n== ② 载荷存在性探测 ==\n");

    ComponentManifest m;
    ParseComponentManifest(MANIFEST_XML, m);

    const std::wstring pkgRoot = TempDir(L"pkg2");
    const std::wstring compDir = pkgRoot + L"\\" + COMP_DIR;
    ::CreateDirectoryW(pkgRoot.c_str(), nullptr);
    ::CreateDirectoryW(compDir.c_str(), nullptr);
    {
        std::wstring payload = compDir + L"\\basesrv.dll";
        HANDLE h = ::CreateFileW(payload.c_str(), GENERIC_WRITE, 0, nullptr,
                                 CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h != INVALID_HANDLE_VALUE) ::CloseHandle(h);
    }

    const PlacementPlan plan = BuildPlacementPlan(m, COMP_DIR, pkgRoot, std::wstring(L"P:\\Windows"));
    const FilePlacementOp& f0 = plan.files[0];
    Check(::GetFileAttributesW(f0.payloadPath.c_str()) != INVALID_FILE_ATTRIBUTES,
          "造出来的载荷文件确实在计划指的位置");
    // 四条里有一条存在 ⇒ 另三条仍算缺
    CheckInt((long long)plan.missingPayload, 3, "缺载荷计数只数真的缺的");

    RmTree(pkgRoot);
}

// ── ③ 文件/目录 ACL 落盘 + 回读 ────────────────────────────────────────────
static void TestPathSddl()
{
    printf("\n== ③ 文件/目录 SDDL 落盘与回读 ==\n");

    const std::wstring base = TempDir(L"acl");
    ::CreateDirectoryW(base.c_str(), nullptr);
    const std::wstring file = base + L"\\probe.bin";
    {
        HANDLE h = ::CreateFileW(file.c_str(), GENERIC_WRITE, 0, nullptr,
                                 CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h != INVALID_HANDLE_VALUE) ::CloseHandle(h);
    }
    Check(PathExists(file), "PathExists 认得刚建的文件");
    Check(!PathExists(base + L"\\nope.bin"), "PathExists 对不存在的路径为假");

    const std::wstring sddl = L"D:P(A;;GA;;;SY)(A;;GR;;;BA)";
    std::wstring e;
    Check(ApplySddlToPath(file, sddl, e) == SddlApplyResult::Applied,
          "文件上应用 DACL 成功");
    Check(e.empty(), "成功时不产生错误文本");

    std::wstring back;
    Check(ReadPathSddl(file, DACL_SECURITY_INFORMATION, back, e), "回读文件的 DACL");
    SddlInfo info;
    std::wstring perr;
    Check(DescribeSddl(back, info, perr), "回读结果能解析");
    Check(info.daclProtected, "回读的 DACL 带 P 保护标志（正是写入的意图）");
    CheckInt((long long)info.daclAceCount, 2, "回读 2 条 ACE");
    Check(Contains(back, L"D:"), "回读含 D: 段");

    // (inherit) 是"跳过"，不是失败
    std::wstring e2;
    Check(ApplySddlToPath(file, L"(inherit)", e2) == SddlApplyResult::SkippedInherit,
          "(inherit) 判为跳过");
    Check(e2.empty(), "(inherit) 跳过时不产生错误");

    // 畸形 SDDL、空路径、不存在的路径都要**带着原因**失败
    std::wstring e3;
    Check(ApplySddlToPath(file, L"这不是 SDDL", e3) == SddlApplyResult::Failed, "畸形 SDDL 被拒");
    Check(!e3.empty(), "畸形 SDDL 失败时给出原因");

    std::wstring e4;
    Check(ApplySddlToPath(L"", L"D:P(A;;GA;;;SY)", e4) == SddlApplyResult::Failed, "空路径被拒");

    std::wstring e5;
    const std::wstring gone = base + L"\\no_such_file.bin";
    Check(ApplySddlToPath(gone, L"D:P(A;;GA;;;SY)", e5) == SddlApplyResult::Failed,
          "不存在的路径被拒");
    Check(Contains(e5, L"路径不存在"), "该失败的原因指向“路径不存在”");

    // 目录同样适用
    const std::wstring dir = base + L"\\sub";
    ::CreateDirectoryW(dir.c_str(), nullptr);
    std::wstring e6;
    Check(ApplySddlToPath(dir, L"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GR;;;BU)", e6)
              == SddlApplyResult::Applied,
          "目录上应用 DACL 成功");
    std::wstring dback;
    Check(ReadPathSddl(dir, DACL_SECURITY_INFORMATION, dback, e6), "回读目录的 DACL");
    SddlInfo dinfo;
    Check(DescribeSddl(dback, dinfo, perr) && dinfo.daclAceCount == 3, "目录回读 3 条 ACE");

    RmTree(base);
    Check(!PathExists(base), "测试目录已清理");
}

int main()
{
    SetupConsoleUtf8();

    TestPlan();
    TestPayloadProbe();
    TestArchRedirection();
    TestPathSddl();

    printf("\n通过 %d / 失败 %d\n", g_pass, g_fail);
    return g_fail == 0 ? 0 : 1;
}
