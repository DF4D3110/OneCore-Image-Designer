using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// CBS（Component Based Servicing）COM 互操作客户端 —— `cbsx.exe` 的 C# 等价实现。
    ///
    /// 【规范来源】WitherOrNot/cbsexploder（GPLv3）的 CbsApi.h / cbsexploder.cpp。
    /// 本文件只复现**行为规范**（选项语义、调用序列、接口 GUID 与 vtable 顺序），
    /// 不含其源代码。CBS 接口本身是 Windows 系统 DLL（cbscore.dll）的公开导出。
    ///
    /// 【关键认识】cbsx.exe 不是 CBS 引擎，而是 CBS 官方 COM API 的薄封装：
    ///   CbsCoreInitialize → IClassFactory → ICbsSession::CreatePackage
    ///   → ICbsPackage::EvaluateApplicability → InitiateChanges → ICbsSession::FinalizeEx
    /// 组件存储登记、WinSxS 硬链接、事务与回滚全部由 cbscore.dll 内部完成。
    ///
    /// 【实测规范】逐行提取自 CbsApi.h（见 out/CbsApi_spec.md）：
    ///   ICbsSession   IID = 75207391-23F2-4396-85F0-8FDB879ED0ED   27 个方法
    ///   ICbsUIHandler IID = 75207392-23F2-4396-85F0-8FDB879ED0ED   10 个方法
    ///   其余接口（ICbsPackage / ICbsIdentity / IEnumCbsIdentity）在头文件里【没有】IID。
    ///
    /// 【为什么只有 ICbsSession 用 ComImport】
    ///   头文件里只有 ICbsSession 与 ICbsUIHandler 带 MIDL_INTERFACE(GUID)。C++ 侧拿到
    ///   CreatePackage 返回的裸指针后直接按 vtable 调用，无需 IID；而 C# 里
    ///   `[ComImport]` 接口【必须】带 `[Guid]`，且把 RCW 转成接口会触发 QueryInterface。
    ///   所以对没有 IID 的三个接口，本文件改用**裸 vtable 分派**（见 CallVtbl），
    ///   既不需要 IID，也不会发生 QI 失败。
    ///
    /// 【vtable 索引】0=QueryInterface 1=AddRef 2=Release，故「第 N 个方法」的索引是 N-1。
    /// </summary>
    public static class CbsxService
    {
        // ────────────────────────────────────────────────────────────────
        // 枚举（数值取自 CbsApi.h，必须精确）
        // ────────────────────────────────────────────────────────────────

        public enum CbsInstallState
        {
            PartiallyInstalled = -19,
            Cancel = -18,
            Superseded = -17,
            Default = -16,
            InvalidPermanent = -8,
            InvalidInstalled = -7,
            InvalidStaged = -4,
            InvalidResolved = -2,
            Unknown = -1,
            Absent = 0,
            Resolving = 1,
            Resolved = 2,
            Staging = 3,
            Staged = 4,
            UninstallRequested = 5,
            InstallRequested = 6,
            Installed = 7,
            Permanent = 8,
            Invalid = 0x7fffffff
        }

        public enum CbsPackageType
        {
            Unknown = -2,
            Existing = -1,
            Cabinet = 0,
            Expanded = 1,
            Manifest = 2,
            XmlString = 3,
            ExpandedWithMum = 4
        }

        public enum CbsPackageProperty
        {
            IdentityString = 1,
            DisplayName = 2,
            Description = 3,
            ReleaseType = 8,
            InstallPackageName = 18,
            InstallLocation = 19
        }

        [Flags]
        public enum CbsSessionOption : uint
        {
            None = 0,
            LoadPersisted = 0x80,
            DoScavenge = 0x400,
            CancelAllPendedTransactions = 0x800,
            EnableCompression = 0x2000,
            DisableCompression = 0x4000,
            DetectAndRepairStoreCorruption = 0xC000,
            ReportStackInfo = 0x100000,
            DoSynchronousCleanup = 0x400000,
            AnalyzeComponentStore = 0x8000000,
            CancelOnlySmartPendedTransactions = 0x80000000
        }

        public enum CbsRequiredAction : uint
        {
            None = 0,
            Reboot = 1
        }

        // ────────────────────────────────────────────────────────────────
        // ComImport 接口（有 IID 的两个）
        // 未用到的方法保留占位，保证 vtable 不错位。
        // ────────────────────────────────────────────────────────────────

        [ComImport, Guid("75207391-23F2-4396-85F0-8FDB879ED0ED"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ICbsSession
        {
            [PreserveSig] int Initialize(CbsSessionOption sessionOptions,
                                         [MarshalAs(UnmanagedType.LPWStr)] string clientID,
                                         [MarshalAs(UnmanagedType.LPWStr)] string? bootDrive,
                                         [MarshalAs(UnmanagedType.LPWStr)] string? winDir);
            [PreserveSig] int Finalize(out CbsRequiredAction pRequiredAction);
            [PreserveSig] int CreatePackage(uint options, CbsPackageType packageType,
                                            [MarshalAs(UnmanagedType.LPWStr)] string szPkgPath,
                                            [MarshalAs(UnmanagedType.LPWStr)] string? szSandboxPath,
                                            out IntPtr pPackage);                     // 返回裸指针
            [PreserveSig] int OpenPackage(uint options, IntPtr pPackageIdentity, IntPtr unkArgAboutLog, out IntPtr pPackage);
            [PreserveSig] int EnumeratePackages(uint options, out IntPtr pPackageList);   // 返回裸指针
            [PreserveSig] int CreateCbsIdentity(out IntPtr ppIdentity);
            [PreserveSig] int GetStatus(out uint pCurrentPhase, out int pLastSuccessfulSessionState,
                                        out int pbCompleted, out int phrStatus);
            [PreserveSig] int Resume(IntPtr pUIHandler);
            [PreserveSig] int GetSessionId(out IntPtr pszId);
            [PreserveSig] int GetProperty(uint prop, out IntPtr pValue);
            [PreserveSig] int AddPhaseBreak();
            [PreserveSig] int FinalizeEx(uint reserved, out CbsRequiredAction pRequiredAction);
            [PreserveSig] int AddSource(uint options, [MarshalAs(UnmanagedType.LPWStr)] string basePath);
            [PreserveSig] int RegisterCbsUIHandler(IntPtr pUIHandler);
            [PreserveSig] int CreateWindowsUpdatePackage(uint a, [MarshalAs(UnmanagedType.LPWStr)] string b,
                                                         Guid c, uint d, CbsPackageType e,
                                                         [MarshalAs(UnmanagedType.LPWStr)] string f,
                                                         [MarshalAs(UnmanagedType.LPWStr)] string g,
                                                         uint h, IntPtr i, int j, out IntPtr k);
            [PreserveSig] int EnumerateCapabilities(uint sourceFilter,
                                                    [MarshalAs(UnmanagedType.LPWStr)] string szNamespace,
                                                    [MarshalAs(UnmanagedType.LPWStr)] string szLang,
                                                    [MarshalAs(UnmanagedType.LPWStr)] string szArch,
                                                    uint dwMajor, uint dwMinor, out IntPtr ppList);
            [PreserveSig] int InitializeEx(uint sessionOptions,
                                           [MarshalAs(UnmanagedType.LPWStr)] string sourceName,
                                           [MarshalAs(UnmanagedType.LPWStr)] string bootDrive,
                                           [MarshalAs(UnmanagedType.LPWStr)] string winDir,
                                           [MarshalAs(UnmanagedType.LPWStr)] string externalDir);
            [PreserveSig] int CreateExternalTransformerExecutor(out IntPtr ppCsiExecutor);
            [PreserveSig] int ObserveSessions(uint options, IntPtr pListener, out IntPtr ppEnum);
            [PreserveSig] int GetActivities(long options, out IntPtr ppActivities);
            [PreserveSig] int SetEnhancedOptions(uint enhancedOptions);
            [PreserveSig] int SetProperty(uint prop, [MarshalAs(UnmanagedType.LPWStr)] string value);
            [PreserveSig] int PerformOperation(uint reserved, int type);
            [PreserveSig] int SetClientToken(long token);
        }

        [ComImport, Guid("00000001-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IClassFactory
        {
            [PreserveSig] int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject);
            [PreserveSig] int LockServer(int fLock);
        }

        // ────────────────────────────────────────────────────────────────
        // 裸 vtable 分派（用于没有 IID 的接口）
        // vtable 索引：0=QueryInterface 1=AddRef 2=Release，第 N 个方法 => 索引 N-1
        // ────────────────────────────────────────────────────────────────

        static T Fn<T>(IntPtr pUnk, int vtblIndex) where T : Delegate
        {
            IntPtr vtbl = Marshal.ReadIntPtr(pUnk, 0);
            IntPtr fn = Marshal.ReadIntPtr(vtbl, vtblIndex * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<T>(fn);
        }

        const int VT_Release = 2;

        // ICbsIdentity : 4=Clear 5=IsNull 6=IsEqual 7=LoadFromAttributes
        //                8=LoadFromStringId 9=GetStringId 10=SaveAsStringId ...
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int GetStringIdFn(IntPtr self, out IntPtr pszID);

        // IEnumCbsIdentity : 4=Next...
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int EnumNextFn(IntPtr self, uint celt, out IntPtr rgpCol, out uint pbFetched);

        // ICbsPackage : 4=GetIdentity 5=GetProperty ... 11=EvaluateApplicability 12=InitiateChanges
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int PkgGetPropertyFn(IntPtr self, CbsPackageProperty prop, out IntPtr pValue);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int PkgEvaluateFn(IntPtr self, uint option, out CbsInstallState app, out CbsInstallState cur);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int PkgInitiateFn(IntPtr self, uint installOptions, CbsInstallState target, IntPtr uiHandler);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int ReleaseFn(IntPtr self);

        static void Release(IntPtr pUnk)
        {
            if (pUnk == IntPtr.Zero) return;
            try { Fn<ReleaseFn>(pUnk, VT_Release)(pUnk); } catch { }
        }

        /// <summary>
        /// 释放 ICbsSession 的 RCW，确保 COM 引用计数归零（CBS 才会真正卸载离线 hive）。
        /// 【干净收尾的关键】不释放的话，即使 FinalizeEx 成功，hive 也可能仍被持有。
        /// </summary>
        static void SafeReleaseSession(ICbsSession? session)
        {
            if (session == null) return;
            try
            {
                if (Marshal.IsComObject(session)) Marshal.FinalReleaseComObject(session);
            }
            catch { }
            // 给 CBS 一点时间完成 hive 卸载
            try { System.Threading.Thread.Sleep(300); } catch { }
        }

        static string TakeCoTaskMemString(IntPtr p)
        {
            if (p == IntPtr.Zero) return "";
            try { return Marshal.PtrToStringUni(p) ?? ""; }
            finally { Marshal.FreeCoTaskMem(p); }
        }

        // ────────────────────────────────────────────────────────────────
        // 原生互操作
        // ────────────────────────────────────────────────────────────────

        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
        static extern IntPtr LoadLibraryA(string lpFileName);

        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
        static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("ole32.dll")]
        static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        [DllImport("ole32.dll")]
        static extern int CoGetMalloc(uint dwMemContext, out IntPtr ppMalloc);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int CbsCoreInitRet1(int x);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void CbsCoreInitNop();

        // HRESULT WINAPI CbsCoreInitialize(IMalloc*, int(WINAPI*)(int), void(*)(), ×5, IClassFactory**)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int CbsCoreInitializeDelegate(
            IntPtr malloc,
            CbsCoreInitRet1 ret1,
            CbsCoreInitNop nop1, CbsCoreInitNop nop2, CbsCoreInitNop nop3,
            CbsCoreInitNop nop4, CbsCoreInitNop nop5,
            out IClassFactory factory);

        // 必须保活：被 GC 回收后原生侧回调会崩
        static CbsCoreInitRet1? _ret1;
        static CbsCoreInitNop? _nop;

        // ────────────────────────────────────────────────────────────────
        // 结果模型
        // ────────────────────────────────────────────────────────────────

        public sealed class PackageOperation
        {
            public string ManifestPath = "";
            public SkuAssemblyService.PackageIntent Intent = SkuAssemblyService.PackageIntent.Unknown;
            public CbsInstallState TargetState;
            public CbsInstallState CurrentState = CbsInstallState.Unknown;

            /// <summary>已是目标状态而跳过（幂等，不算失败）。</summary>
            public bool Skipped;
            public CbsInstallState Applicability = CbsInstallState.Unknown;
            public string Identity = "";
            public bool Ok;
            public string? Error;
        }

        public sealed class Result
        {
            public bool Success;
            public string Error = "";
            public bool RebootRequired;
                /// <summary>因已是目标状态而跳过的包数（幂等跳过，不算失败）。</summary>
                public int Skipped;
            public readonly List<string> EnumeratedIdentities = new();
            public readonly List<PackageOperation> Operations = new();
            public readonly List<string> Log = new();
        }

        // ────────────────────────────────────────────────────────────────
        // 工具
        // ────────────────────────────────────────────────────────────────

        /// <summary>照搬 cbsexploder：取 winDir 中 "Windows" 之前的盘根。</summary>
        public static string DeriveBootDrive(string winDir)
        {
            int i = winDir.IndexOf("Windows", StringComparison.OrdinalIgnoreCase);
            if (i <= 0) throw new ArgumentException("winDir 里找不到 \"Windows\": " + winDir);
            return winDir[..(i - 1)];   // "X:\image\Windows" -> "X:\image"
        }

        public static CbsInstallState IntentToState(SkuAssemblyService.PackageIntent intent) => intent switch
        {
            SkuAssemblyService.PackageIntent.Installed => CbsInstallState.Installed,
            SkuAssemblyService.PackageIntent.Staged => CbsInstallState.Staged,
            SkuAssemblyService.PackageIntent.Absent => CbsInstallState.Absent,
            _ => CbsInstallState.Unknown
        };

        // 记录实际加载的 cbscore.dll 路径 —— InitializeEx 的 externalDir 需要栈所在目录
        static string? _lastStackDll;

        /// <summary>
        /// 确保 cbscore.dll 可被 LoadLibraryA 找到。
        ///
        /// CBS 规则：所用 servicing stack 必须**不低于**目标镜像的版本，否则
        /// Initialize 返回 0x800F0823（CBS_E_NEW_SERVICING_STACK_REQUIRED）。
        /// 因此搜索顺序是：
        ///   1) 默认搜索路径（在线镜像时 servicing stack 已在其中）
        ///   2) **目标镜像自己的 WinSxS\*servicingstack***  ← 版本恰好匹配，离线服务的首选
        ///   3) 宿主 %SystemRoot%\WinSxS\*servicingstack*    ← 镜像栈不完整/过旧时的后备
        ///
        /// 注意：WCOS 类镜像的 servicing stack 常常是精简版（没有 cbscore.dll），
        /// 这时会自然落到宿主栈；而宿主栈若比镜像新太多，就会触发 0x800F0823。
        /// </summary>
        public static bool TryAddServicingStackToSearchPath(string? imageRootOrHint, out string detail)
        {
            detail = "";

            // 1) 默认路径
            if (LoadLibraryA("cbscore.dll") != IntPtr.Zero) { detail = "cbscore.dll 已在默认搜索路径"; return true; }

            var roots = new List<string>();

            // 2) 目标镜像自己的栈（版本匹配，首选）
            if (!string.IsNullOrWhiteSpace(imageRootOrHint))
            {
                roots.Add(Path.Combine(imageRootOrHint, "Windows", "WinSxS"));
                roots.Add(Path.Combine(imageRootOrHint, "WinSxS"));
                roots.Add(imageRootOrHint);
            }

            // 3) 宿主栈（后备）
            string sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(sysRoot)) roots.Add(Path.Combine(sysRoot, "WinSxS"));

            var tried = new List<string>();
            foreach (var root in roots)
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
                IEnumerable<string> dirs;
                try { dirs = Directory.EnumerateDirectories(root, "*servicingstack*"); } catch { continue; }
                foreach (var dir in dirs)
                {
                    string dll = Path.Combine(dir, "cbscore.dll");
                    if (!File.Exists(dll)) continue;
                    tried.Add(dir);
                    SetDllDirectory(dir);
                    if (LoadLibraryA("cbscore.dll") != IntPtr.Zero)
                    {
                        _lastStackDll = dll;
                        detail = "已从 " + dir + " 加载 cbscore.dll";
                        return true;
                    }
                }
            }

            detail = tried.Count == 0
                ? "未找到任何含 cbscore.dll 的 servicing stack 目录"
                : "找到 " + tried.Count + " 个 cbscore.dll 但均无法加载";
            return false;
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetDllDirectory(string lpPathName);

        // ────────────────────────────────────────────────────────────────
        // 会话
        // ────────────────────────────────────────────────────────────────

        static ICbsSession? OpenSession(string winDir, string clientId, Action<string>? log, out string? error)
        {
            error = null;
            try
            {
                // 先确保 cbscore.dll 可见：优先目标镜像自带的 servicing stack（版本恰好匹配），
                // 回退宿主栈。放在这里而不是交给调用方，避免每个入口都要记得做一次。
                if (!TryAddServicingStackToSearchPath(
                        Path.GetDirectoryName(Path.GetFullPath(winDir).TrimEnd('\\')) is string parent
                            && Path.GetFileName(Path.GetFullPath(winDir).TrimEnd('\\'))
                                 .Equals("Windows", StringComparison.OrdinalIgnoreCase)
                            ? parent : winDir,
                        out string stackDetail))
                {
                    error = "servicing stack 不可用: " + stackDetail;
                    return null;
                }
                log?.Invoke("servicing stack: " + stackDetail);

                // ── 架构判定（必须最先，因为架构错了后面全白费）──
                // CBS 的 servicing stack 是**按架构**的：x64 进程加载不了 x86 的 cbscore.dll，
                // 它会**静默回退到宿主栈**，然后拿宿主版本去服务目标镜像 →
                // 0x8007051A ERROR_REVISION_MISMATCH（Windows::COM::VerifyStoreVersionNumber）。
                // 这里提前把结论说清楚，并给出转接指引，而不是让调用方去猜那个错误码。
                var (imgArch, archEvidence) = ArchRelay.DetectImageArchitecture(winDir);
                if (ArchRelay.NeedsRelay(imgArch, out string relayReason))
                {
                    string? helper = ArchRelay.FindHelper(imgArch);
                    log?.Invoke($"⚠ 架构不匹配：{relayReason}");
                    log?.Invoke($"   镜像架构判定依据: {archEvidence}");
                    log?.Invoke("   → 请改走 DISM 通道（dism /image 有自己的跨架构离线调度）："
                              + "ServicingRouter 判定架构不符时会自动选 Channel.Dism，"
                              + "或直接用 DismServicingService。");
                    if (helper != null)
                        log?.Invoke($"   若坚持用原生通道，可改用 {ArchRelay.ToToken(imgArch)} 子程序: {helper}");
                    error = relayReason + " → 请改走 DISM 通道（dism /image）";
                    return null;
                }
                log?.Invoke($"架构判定: 镜像={ArchRelay.ToToken(imgArch)} 进程={ArchRelay.ToToken(ArchRelay.CurrentProcessArch())}，匹配");

                // CBS 离线服务要读写目标镜像的 hive。dism /Apply-Image 铺出的镜像会还原
                // WIM 原始 ACL（hive 归属 TrustedInstaller），此时必须显式启用备份/还原特权，
                // 否则 ICbsSession::Initialize 会以 0x8007051A (ERROR_NO_SUCH_PRIVILEGE) 失败。
                PrivilegeHelper.EnableServicingPrivileges(log);

                IntPtr cbscore = LoadLibraryA("cbscore.dll");
                if (cbscore == IntPtr.Zero)
                {
                    error = "无法加载 cbscore.dll（servicing stack 未在搜索路径中）";
                    return null;
                }

                IntPtr proc = GetProcAddress(cbscore, "CbsCoreInitialize");
                if (proc == IntPtr.Zero) { error = "cbscore.dll 里找不到 CbsCoreInitialize"; return null; }
                var init = Marshal.GetDelegateForFunctionPointer<CbsCoreInitializeDelegate>(proc);

                int hr = CoInitializeEx(IntPtr.Zero, 0 /* COINIT_MULTITHREADED */);
                log?.Invoke($"CoInitializeEx -> 0x{hr:X8}");

                hr = CoGetMalloc(1 /* MEMCTX_TASK */, out IntPtr malloc);
                if (hr < 0) { error = $"CoGetMalloc 失败 0x{hr:X8}"; return null; }

                _ret1 = _ => 1;
                _nop = () => { };

                hr = init(malloc, _ret1, _nop, _nop, _nop, _nop, _nop, out IClassFactory factory);
                if (hr < 0 || factory == null) { error = $"CbsCoreInitialize 失败 0x{hr:X8}"; return null; }
                log?.Invoke("CbsCoreInitialize OK");

                Guid iidSession = new("75207391-23F2-4396-85F0-8FDB879ED0ED");
                hr = factory.CreateInstance(IntPtr.Zero, ref iidSession, out IntPtr pSession);
                if (hr < 0 || pSession == IntPtr.Zero) { error = $"CreateInstance(ICbsSession) 失败 0x{hr:X8}"; return null; }

                var session = (ICbsSession)Marshal.GetObjectForIUnknown(pSession);
                Release(pSession);
                log?.Invoke("ICbsSession 已取得");

                string bootDrive = DeriveBootDrive(winDir);
                hr = session.Initialize(CbsSessionOption.None, clientId, bootDrive, winDir);

                if (hr < 0 && hr == unchecked((int)0x800F0823))
                {
                    // CBS_E_NEW_SERVICING_STACK_REQUIRED：
                    // CBS 把「已加载栈自称的版本」与「镜像声明的栈版本」比较，
                    // 加载的栈一律自称 6.2.<build>，而较新的镜像声明 10.0.<build>，
                    // 于是判"栈太低"。
                    //
                    // DISM 能服务这类镜像，说明它走了另一条入口：InitializeEx 的
                    // externalDir（外部服务目录）。这里先原样重试一次，把 externalDir
                    // 指向已加载栈所在目录，看 CBS 是否改用它声明的版本。
                    string? stackDir = Path.GetDirectoryName(_lastStackDll!);
                    if (!string.IsNullOrEmpty(stackDir))
                    {
                        log?.Invoke($"Initialize 返回 0x800F0823，改用 InitializeEx 并把 externalDir 指向 {stackDir}");
                        hr = session.InitializeEx((uint)CbsSessionOption.None, clientId, bootDrive, winDir, stackDir);
                        if (hr >= 0)
                        {
                            log?.Invoke($"InitializeEx（externalDir）成功 (bootDrive=\"{bootDrive}\", winDir=\"{winDir}\")");
                            return session;
                        }
                        log?.Invoke($"InitializeEx 也失败 0x{hr:X8}");
                    }
                    else
                    {
                        log?.Invoke("Initialize 返回 0x800F0823，但拿不到栈目录，无法尝试 InitializeEx");
                    }
                }

                if (hr < 0)
                {
                    string? privHint = PrivilegeHelper.Explain(hr);
                    if (privHint != null) log?.Invoke("  " + privHint);
                    error = $"ICbsSession::Initialize 失败 0x{hr:X8}（bootDrive=\"{bootDrive}\", winDir=\"{winDir}\"）"
                          + (privHint != null ? " —— " + privHint : "")
                          + (hr == unchecked((int)0x800F0823)
                             ? " —— CBS_E_NEW_SERVICING_STACK_REQUIRED：镜像声明的 servicing stack 版本高于已加载栈自称的版本。"
                             + "跨代场景（镜像 10.0.x + 可加载栈 6.2.y）会触发它；可改用 DismServicingService 走 DISM 兜底。"
                             : "");
                    return null;
                }
                log?.Invoke($"会话已初始化 (bootDrive=\"{bootDrive}\", winDir=\"{winDir}\")");
                return session;
            }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; return null; }
        }

        // ────────────────────────────────────────────────────────────────
        // 公开 API
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// `/ep` —— 枚举离线镜像里的包（**只读**，不改动镜像）。
        /// 最小可验证路径：结果可与 `dism /image:... /get-packages` 对照。
        /// </summary>
        public static Result EnumeratePackages(string winDir, Action<string>? log = null)
        {
            var res = new Result();
            try
            {
                // ── 强制门禁：CBS 只能在挂载的 VHD/VHDX 上操作（保护宿主系统）──
                var gate = CbsSafety.CheckTarget(winDir);
                log?.Invoke("门禁: " + gate.Reason);
                if (!gate.Allowed) { res.Error = gate.Reason; return res; }
                if (CbsSafety.TryDetectForeignSession(winDir, out string foreign))
                { res.Error = foreign; return res; }

                var session = OpenSession(winDir, "OneCoreImageDesigner", log, out string? err);
                if (session == null) { res.Error = err ?? "会话创建失败"; return res; }

                int hr = session.EnumeratePackages(0x50, out IntPtr pEnum);
                if (hr < 0 || pEnum == IntPtr.Zero) { res.Error = $"EnumeratePackages 失败 0x{hr:X8}"; return res; }

                try
                {
                    var next = Fn<EnumNextFn>(pEnum, 3);   // IEnumCbsIdentity: 4=Next => 索引 3
                    while (true)
                    {
                        hr = next(pEnum, 1, out IntPtr pIdent, out uint fetched);
                        if (hr < 0 || fetched != 1 || pIdent == IntPtr.Zero) break;
                        try
                        {
                            var getStringId = Fn<GetStringIdFn>(pIdent, 8);   // ICbsIdentity: 9=GetStringId => 索引 8
                            if (getStringId(pIdent, out IntPtr pStr) >= 0)
                            {
                                string id = TakeCoTaskMemString(pStr);
                                if (!string.IsNullOrEmpty(id)) res.EnumeratedIdentities.Add(id);
                            }
                        }
                        catch (Exception ex) { log?.Invoke("  读取 identity 失败: " + ex.Message); }
                        finally { Release(pIdent); }
                    }
                }
                finally { Release(pEnum); }

                // 【干净收尾】只读操作也必须关会话：
                // 不调用 FinalizeEx 会让 CBS 挂载的离线 hive（SOFTWARE/SYSTEM/.../ntuser.dat）
                // 一直留在挂载状态，下一次操作就被"残留挂载"挡住。
                try
                {
                    int hrf = session.FinalizeEx(0, out CbsRequiredAction act);
                    if (hrf < 0) log?.Invoke($"  FinalizeEx 返回 0x{hrf:X8}（只读会话，通常可忽略）");
                    else log?.Invoke($"  会话已收尾（RebootRequired={act == CbsRequiredAction.Reboot}）");
                }
                catch (Exception ex) { log?.Invoke("  FinalizeEx 异常: " + ex.Message); }
                finally { SafeReleaseSession(session); }

                log?.Invoke($"枚举到 {res.EnumeratedIdentities.Count} 个包");
                res.Success = true;
                return res;
            }
            catch (Exception ex) { res.Error = ex.GetType().Name + ": " + ex.Message; return res; }
        }

        /// <summary>
        /// 按 <see cref="SkuAssemblyService.Plan"/> 逐个包执行 `/ip` `/sp` `/up`。
        /// 每包都先 EvaluateApplicability 并做与 cbsexploder 相同的两项断言，
        /// 全部成功后才 FinalizeEx（CBS 的事务语义）。
        /// </summary>
        /// <param name="dryRun">
        /// true 时执行到 EvaluateApplicability 为止就停下 —— 打开包、读身份、判适用性，
        /// **不调用 InitiateChanges**，因此镜像完全不变。用于上线前的可执行性预检。
        /// </param>
        public static Result ApplyPlan(string winDir, SkuAssemblyService.Plan plan,
            Action<string>? log = null, bool dryRun = false)
        {
            var res = new Result();
            try
            {
                if (!plan.Success) { res.Error = "计划无效: " + plan.Error; return res; }
                if (plan.Operations.Count == 0) { res.Error = "计划为空"; return res; }
                if (dryRun) log?.Invoke("== 试运行（dry-run）：只做适用性判定，不修改镜像 ==");

                // ── 强制门禁：CBS 只能在挂载的 VHD/VHDX 上操作（保护宿主系统）──
                var gate = CbsSafety.CheckTarget(winDir);
                log?.Invoke("门禁: " + gate.Reason);
                if (!gate.Allowed) { res.Error = gate.Reason; return res; }
                if (CbsSafety.TryDetectForeignSession(winDir, out string foreign))
                { res.Error = foreign; return res; }

                var session = OpenSession(winDir, "OneCoreImageDesigner", log, out string? err);
                if (session == null) { res.Error = err ?? "会话创建失败"; return res; }

                foreach (var op in plan.Operations)
                {
                    var rec = new PackageOperation
                    {
                        ManifestPath = op.ResolvedPath ?? op.RawManifestPath,
                        Intent = op.Intent,
                        TargetState = IntentToState(op.Intent)
                    };
                    res.Operations.Add(rec);

                    if (!op.FileExists || op.ResolvedPath == null)
                    {
                        rec.Error = "mum 不存在";
                        res.Error = "有包未解析，已中止（未做任何改动）";
                        return res;
                    }

                    int hr = session.CreatePackage(0, CbsPackageType.ExpandedWithMum,
                                                   op.ResolvedPath, null, out IntPtr pPkg);
                    if (hr < 0 || pPkg == IntPtr.Zero) { rec.Error = $"CreatePackage 失败 0x{hr:X8}"; res.Error = rec.Error; return res; }

                    try
                    {
                        var getProp = Fn<PkgGetPropertyFn>(pPkg, 4);      // ICbsPackage: 5=GetProperty => 索引 4
                        if (getProp(pPkg, CbsPackageProperty.IdentityString, out IntPtr pIdent) >= 0)
                            rec.Identity = TakeCoTaskMemString(pIdent);
                        log?.Invoke($"包 {rec.Identity}");

                        var evalFn = Fn<PkgEvaluateFn>(pPkg, 10);          // 11=EvaluateApplicability => 索引 10
                        hr = evalFn(pPkg, 0, out CbsInstallState app, out CbsInstallState cur);
                        if (hr < 0) { rec.Error = $"EvaluateApplicability 失败 0x{hr:X8}"; res.Error = rec.Error; return res; }
                        rec.Applicability = app;
                        rec.CurrentState = cur;
                        log?.Invoke($"  当前状态={cur}  适用性={app}  目标={rec.TargetState}");

                        // 与 cbsexploder 相同的两项断言，但**中止条件收窄**：
                        //   · “已经是目标状态” 是**正常情况**（幂等），应当**跳过并继续**，
                        //     而不是中止整个计划。原实现把它当错误，导致计划里只要有一个
                        //     包已是目标状态，后面所有包就都装不上了（实测：
                        //     Enterprise 已 Installed → Core/CoreSingleLanguage 明明是
                        //     Absent→Installed 也被一起放弃）。
                        //   · 适用性不足才确实应当拦下。
                        if (cur == rec.TargetState)
                        {
                            rec.Skipped = true;
                            log?.Invoke($"  ⏭ 已是目标状态 {cur}，跳过该包");
                            res.Skipped++;
                            continue;
                        }
                        if (app != CbsInstallState.Installed)
                        {
                            rec.Error = $"包不适用于当前镜像（applicability={app}）";
                            res.Error = rec.Error;
                            return res;
                        }

                        if (dryRun)
                        {
                            log?.Invoke("  [dry-run] 跳过 InitiateChanges");
                            rec.Ok = true;
                            continue;
                        }

                        log?.Invoke($"  设置 {rec.Identity}: {cur} -> {rec.TargetState}");
                        var initiate = Fn<PkgInitiateFn>(pPkg, 11);        // 12=InitiateChanges => 索引 11
                        hr = initiate(pPkg, 0, rec.TargetState, IntPtr.Zero);
                        if (hr < 0) { rec.Error = $"InitiateChanges 失败 0x{hr:X8}"; res.Error = rec.Error; return res; }

                        rec.Ok = true;
                    }
                    finally { Release(pPkg); }
                }

                int hrf = session.FinalizeEx(0, out CbsRequiredAction act);
                if (hrf < 0) { res.Error = $"FinalizeEx 失败 0x{hrf:X8}"; return res; }

                res.RebootRequired = act == CbsRequiredAction.Reboot;
                log?.Invoke($"FinalizeEx OK，RebootRequired={res.RebootRequired}");
                res.Success = true;
                return res;
            }
            catch (Exception ex) { res.Error = ex.GetType().Name + ": " + ex.Message; return res; }
        }

        public static string RenderEnumeration(Result r)
        {
            var sb = new StringBuilder();
            sb.AppendLine("CBS 包枚举（/ep）");
            sb.AppendLine("  Success : " + r.Success);
            sb.AppendLine("  Error   : " + (r.Error.Length == 0 ? "(none)" : r.Error));
            sb.AppendLine("  包数量  : " + r.EnumeratedIdentities.Count);
            sb.AppendLine();
            foreach (var id in r.EnumeratedIdentities) sb.AppendLine("    " + id);
            return sb.ToString();
        }
    }
}
