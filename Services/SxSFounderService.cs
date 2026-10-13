using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// `sxsfounder.exe` 的 C# 等价实现 —— 用 WCP API 在离线镜像上"铺底"（造出可服务的镜像骨架）。
    ///
    /// 【规范来源】WitherOrNot/sxsfounder（GPLv3）的 sxsfounder.cpp（149 行）+ wcp.h。
    /// 本文件只复现行为规范（调用序列、结构体布局、导出函数签名与 GUID），不含其源码。
    /// WCP 接口是 Windows 系统 DLL（wcp.dll）的公开导出。
    ///
    /// 【发现】sxsfounder 极薄 —— 只做 7 个 WCP 导出的组合调用：
    ///   LoadLibraryA("wcp.dll"); CoInitialize();
    ///   WcpInitialize(&amp;cookie)
    ///   CoGetMalloc(MEMCTX_TASK, &amp;alloc); SetIsolationIMalloc(alloc)
    ///   OFFLINE_STORE_CREATION_PARAMETERS p = { cbSize }; p.pszHostSystemDrivePath = 镜像路径;
    ///   p.ulProcessorArchitecture = arch;
    ///   CreateNewWindows(0, L"X:\\", &amp;p, &amp;regKeys, &amp;disposition)     // 建目录骨架 + 挂注册表
    ///   CreateNewOfflineStore(0, &amp;p, IID_ICSIStore, &amp;store, &amp;disposition) // 建离线组件存储
    ///   DismountRegistryHives(regKeys)
    ///
    /// 【架构编号】sxsfounder 用的 ulProcessorArchitecture 取值来自 wcp.h 的架构枚举：
    ///   x86=0, arm=5, amd64=9, arm64=12。注意它**不是** PE 的 IMAGE_FILE_MACHINE 值。
    ///
    /// 【安全】与 CbsxService 同样的约束：只在挂载的 VHD/VHDX 上操作（CbsSafety 门禁）。
    /// </summary>
    public static class SxSFounderService
    {
        // ────────────────────────────────────────────────────────────────
        // 架构编号（wcp.h 的枚举值，不是 IMAGE_FILE_MACHINE）
        // ────────────────────────────────────────────────────────────────
        public static uint ArchitectureValue(string arch) => arch.ToLowerInvariant() switch
        {
            "x86" => 0,
            "arm" => 5,
            "amd64" => 9,
            "arm64" => 12,
            _ => throw new ArgumentException($"不支持的架构: {arch}（可用 x86 / arm / amd64 / arm64）")
        };

        // ────────────────────────────────────────────────────────────────
        // 结构体：必须与 wcp.h 的 _OFFLINE_STORE_CREATION_PARAMETERS 完全一致
        // ────────────────────────────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct OFFLINE_STORE_CREATION_PARAMETERS
        {
            public IntPtr cbSize;                       // SIZE_T
            public uint dwFlags;
            [MarshalAs(UnmanagedType.LPWStr)] public string? pszHostSystemDrivePath;
            [MarshalAs(UnmanagedType.LPWStr)] public string? pszHostWindowsDirectoryPath;
            [MarshalAs(UnmanagedType.LPWStr)] public string? pszTargetWindowsDirectoryPath;
            [MarshalAs(UnmanagedType.LPWStr)] public string? pszHostRegistryMachineSoftwarePath;
            [MarshalAs(UnmanagedType.LPWStr)] public string? pszHostRegistryMachineSystemPath;
            [MarshalAs(UnmanagedType.LPWStr)] public string? pszHostRegistryMachineSecurityPath;
            [MarshalAs(UnmanagedType.LPWStr)] public string? pszHostRegistryMachineSAMPath;
            [MarshalAs(UnmanagedType.LPWStr)] public string? pszHostRegistryMachineComponentsPath;
            [MarshalAs(UnmanagedType.LPWStr)] public string? pszHostRegistryUserDotDefaultPath;
            [MarshalAs(UnmanagedType.LPWStr)] public string? pszHostRegistryDefaultUserPath;
            public uint ulProcessorArchitecture;
            [MarshalAs(UnmanagedType.LPWStr)] public string? pszHostRegistryMachineOfflineSchemaPath;
        }

        // OFFLINE_STORE_CREATION_PARAMETERS_FLAGS（wcp.h）
        public const uint FLAG_FAKE_ONLINE = 0x4;
        public const uint FLAG_APPLY_NO_KTM = 0x10;

        /// <summary>ICSIStore 的 IID（wcp.h：465F1EC1-7F1D-4A85-A30B-AE1090F212DB）。</summary>
        public static readonly Guid IID_ICSIStore = new("465F1EC1-7F1D-4A85-A30B-AE1090F212DB");

        [ComImport, Guid("465F1EC1-7F1D-4A85-A30B-AE1090F212DB"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ICSIStore
        {
            // 只需要持有这个指针；具体方法本轮不需要。
            // 注意：C# ComImport 要求接口至少有一个方法才能正确布置 vtable，
            // 这里用一个占位（实际调用前会先 QI，未实现的槽位不会被触碰）。
            [PreserveSig] int Placeholder();
        }

        // ────────────────────────────────────────────────────────────────
        // WCP 导出函数签名（wcp.h）
        // ────────────────────────────────────────────────────────────────
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int WcpInitializeFn(out IntPtr ppCookie);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int WcpShutdownFn(IntPtr pCookie);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int SetIsolationIMallocFn(IntPtr alloc);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int CreateNewPseudoWindowsFn(uint dwFlags,
            [MarshalAs(UnmanagedType.LPWStr)] string? pszDirectoryRoot,
            IntPtr pParameters, out IntPtr ppCookie, out uint pdwDisposition);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int CreateNewWindowsFn(uint dwFlags,
            [MarshalAs(UnmanagedType.LPWStr)] string pszSystemDrive,
            ref OFFLINE_STORE_CREATION_PARAMETERS pParameters,
            out IntPtr ppRegKeys, out uint pdwDisposition);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int CreateNewOfflineStoreFn(uint dwFlags,
            ref OFFLINE_STORE_CREATION_PARAMETERS pParameters,
            ref Guid riid, out IntPtr ppStore, out uint pdwDisposition);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int DismountRegistryHivesFn(IntPtr pCookie);

        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
        static extern IntPtr LoadLibraryA(string lpFileName);

        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
        static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("ole32.dll")]
        static extern int CoInitialize(IntPtr pvReserved);

        [DllImport("ole32.dll")]
        static extern int CoGetMalloc(uint dwMemContext, out IntPtr ppMalloc);

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetDllDirectory(string lpPathName);

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetFullPathNameW(string lpFileName, uint nBufferLength,
            System.Text.StringBuilder lpBuffer, IntPtr lpFilePart);

        // ────────────────────────────────────────────────────────────────
        // 结果
        // ────────────────────────────────────────────────────────────────
        public sealed class Result
        {
            public bool Success;
            public string Error = "";
            public bool WindowsCreated;
            public bool StoreCreated;
            public uint WindowsDisposition;
            public uint StoreDisposition;
            public readonly List<string> Log = new();
            public readonly List<string> Parameters = new();
        }

        /// <summary>
        /// 在 offlineImage 指定的目录上铺出可服务的镜像骨架。
        /// </summary>
        /// <param name="offlineImagePath">离线镜像根（例：X:\image）</param>
        /// <param name="arch">x86 / arm / amd64 / arm64</param>
        public static Result Create(string offlineImagePath, string arch, Action<string>? log = null)
        {
            var res = new Result();
            void L(string s) { res.Log.Add(s); log?.Invoke(s); }

            IntPtr regKeys = IntPtr.Zero;
            IntPtr cookie = IntPtr.Zero;
            IntPtr alloc = IntPtr.Zero;
            try
            {
                if (string.IsNullOrWhiteSpace(offlineImagePath)) { res.Error = "offlineImagePath 为空"; return res; }
                Directory.CreateDirectory(offlineImagePath);
                string full = Path.GetFullPath(offlineImagePath);
                uint archVal;
                try { archVal = ArchitectureValue(arch); }
                catch (Exception ex) { res.Error = ex.Message; return res; }

                // ── 门禁：与 CBS 同样只在挂载的 VHD/VHDX 上操作 ──
                var gate = CbsSafety.CheckTarget(full);
                L("门禁: " + gate.Reason);
                if (!gate.Allowed) { res.Error = gate.Reason; return res; }

                // ── 让 wcp.dll 可见：优先目标镜像带的服务栈目录，回退宿主 ──
                if (!TryResolveWcp(full, L)) { res.Error = "找不到 wcp.dll"; return res; }

                IntPtr wcp = LoadLibraryA("wcp.dll");
                if (wcp == IntPtr.Zero) { res.Error = "wcp.dll 加载失败"; return res; }
                L("wcp.dll 已加载");

                int hr = CoInitialize(IntPtr.Zero);
                L($"CoInitialize -> 0x{hr:X8}");

                var WcpInitialize = Bind<WcpInitializeFn>(wcp, "WcpInitialize");
                var SetIsolationIMalloc = Bind<SetIsolationIMallocFn>(wcp, "SetIsolationIMalloc");
                var CreateNewWindows = Bind<CreateNewWindowsFn>(wcp, "CreateNewWindows");
                var CreateNewOfflineStore = Bind<CreateNewOfflineStoreFn>(wcp, "CreateNewOfflineStore");
                var DismountRegistryHives = Bind<DismountRegistryHivesFn>(wcp, "DismountRegistryHives");

                if (WcpInitialize == null || SetIsolationIMalloc == null || CreateNewWindows == null
                    || CreateNewOfflineStore == null || DismountRegistryHives == null)
                { res.Error = "wcp.dll 缺少所需导出（WcpInitialize/SetIsolationIMalloc/CreateNewWindows/CreateNewOfflineStore/DismountRegistryHives）"; return res; }

                hr = WcpInitialize(out cookie);
                if (hr < 0) { res.Error = $"WcpInitialize 失败 0x{hr:X8}"; return res; }
                L("WcpInitialize OK");

                hr = CoGetMalloc(1 /* MEMCTX_TASK */, out alloc);
                if (hr < 0) { res.Error = $"CoGetMalloc 失败 0x{hr:X8}"; return res; }

                hr = SetIsolationIMalloc(alloc);
                if (hr < 0) { res.Error = $"SetIsolationIMalloc 失败 0x{hr:X8}"; return res; }
                L("SetIsolationIMalloc OK");

                var p = new OFFLINE_STORE_CREATION_PARAMETERS
                {
                    cbSize = (IntPtr)Marshal.SizeOf<OFFLINE_STORE_CREATION_PARAMETERS>(),
                    dwFlags = 0,
                    pszHostSystemDrivePath = full,
                    ulProcessorArchitecture = archVal
                };
                L($"cbSize = {p.cbSize}");

                // ── CreateNewWindows：建目录骨架 + 挂载离线注册表 hive ──
                hr = CreateNewWindows(0, "X:\\", ref p, out regKeys, out uint wDisp);
                res.WindowsDisposition = wDisp;
                if (hr < 0)
                {
                    res.Error = $"CreateNewWindows 失败 0x{hr:X8}";
                    return res;
                }
                res.WindowsCreated = true;
                L($"CreateNewWindows OK (disposition={wDisp})");

                // ── CreateNewOfflineStore：建离线组件存储 ──
                Guid iid = IID_ICSIStore;
                hr = CreateNewOfflineStore(0, ref p, ref iid, out IntPtr pStore, out uint sDisp);
                res.StoreDisposition = sDisp;
                if (hr < 0)
                {
                    res.Error = $"CreateNewOfflineStore 失败 0x{hr:X8}";
                    return res;
                }
                res.StoreCreated = true;
                L($"CreateNewOfflineStore OK (disposition={sDisp})");
                if (pStore != IntPtr.Zero)
                {
                    try { Marshal.Release(pStore); } catch { }
                }

                // ── WCP 回填的路径参数（与 sxsfounder 一样打印出来） ──
                res.Parameters.Add("HostSystemDrivePath              : " + p.pszHostSystemDrivePath);
                res.Parameters.Add("HostWindowsDirectoryPath         : " + p.pszHostWindowsDirectoryPath);
                res.Parameters.Add("TargetWindowsDirectoryPath       : " + p.pszTargetWindowsDirectoryPath);
                res.Parameters.Add("HostRegistryMachineSoftwarePath  : " + p.pszHostRegistryMachineSoftwarePath);
                res.Parameters.Add("HostRegistryMachineSystemPath    : " + p.pszHostRegistryMachineSystemPath);
                res.Parameters.Add("HostRegistryMachineSecurityPath  : " + p.pszHostRegistryMachineSecurityPath);
                res.Parameters.Add("HostRegistryMachineSAMPath       : " + p.pszHostRegistryMachineSAMPath);
                res.Parameters.Add("HostRegistryMachineComponentsPath: " + p.pszHostRegistryMachineComponentsPath);
                res.Parameters.Add("HostRegistryUserDotDefaultPath   : " + p.pszHostRegistryUserDotDefaultPath);
                res.Parameters.Add("HostRegistryDefaultUserPath      : " + p.pszHostRegistryDefaultUserPath);
                res.Parameters.Add("ProcessorArchitecture            : 0x" + p.ulProcessorArchitecture.ToString("X8"));
                res.Parameters.Add("HostRegistryMachineOfflineSchemaPath: " + p.pszHostRegistryMachineOfflineSchemaPath);
                foreach (var s in res.Parameters) L("  " + s);

                res.Success = true;
                return res;
            }
            catch (Exception ex) { res.Error = ex.GetType().Name + ": " + ex.Message; return res; }
            finally
            {
                // ── 干净收尾：必须卸载注册表 hive，否则 VHD 被占用 ──
                if (regKeys != IntPtr.Zero)
                {
                    try
                    {
                        IntPtr wcp = LoadLibraryA("wcp.dll");
                        var dm = Bind<DismountRegistryHivesFn>(wcp, "DismountRegistryHives");
                        if (dm != null)
                        {
                            int hr2 = dm(regKeys);
                            L($"DismountRegistryHives -> 0x{hr2:X8}");
                        }
                    }
                    catch (Exception ex) { L("DismountRegistryHives 异常: " + ex.Message); }
                }
                if (alloc != IntPtr.Zero) { try { Marshal.Release(alloc); } catch { } }
                // cookie 释放交给 WcpShutdown（本轮保留，避免破坏 WCP 内部状态）
                _ = cookie;
            }
        }

        static T? Bind<T>(IntPtr module, string name) where T : Delegate
        {
            if (module == IntPtr.Zero) return null;
            IntPtr p = GetProcAddress(module, name);
            return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p);
        }

        /// <summary>
        /// 让 wcp.dll 可被 LoadLibraryA 找到。
        /// wcp.dll 来自 servicing stack（<c>WinSxS\*\servicingstack*</c>）；
        /// 优先目标镜像自带的（版本匹配），回退宿主。
        /// </summary>
        static bool TryResolveWcp(string imageRoot, Action<string> log)
        {
            if (LoadLibraryA("wcp.dll") != IntPtr.Zero) { log("wcp.dll 已在默认搜索路径"); return true; }

            var roots = new List<string>
            {
                Path.Combine(imageRoot, "Windows", "WinSxS"),
                Path.Combine(imageRoot, "WinSxS"),
                imageRoot
            };
            string sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(sysRoot)) roots.Add(Path.Combine(sysRoot, "WinSxS"));

            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                IEnumerable<string> dirs;
                try { dirs = Directory.EnumerateDirectories(root, "*servicingstack*"); } catch { continue; }
                foreach (var dir in dirs)
                {
                    if (!File.Exists(Path.Combine(dir, "wcp.dll"))) continue;
                    SetDllDirectory(dir);
                    if (LoadLibraryA("wcp.dll") != IntPtr.Zero)
                    {
                        log("wcp.dll 已从 " + dir + " 加载");
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>把结果渲染成文本。</summary>
        public static string Render(Result r)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("sxsfounder 等价操作（WCP）");
            sb.AppendLine("  Success          : " + r.Success);
            sb.AppendLine("  Error            : " + (r.Error.Length == 0 ? "(none)" : r.Error));
            sb.AppendLine("  CreateNewWindows : " + r.WindowsCreated + $" (disposition={r.WindowsDisposition})");
            sb.AppendLine("  CreateOfflineStore: " + r.StoreCreated + $" (disposition={r.StoreDisposition})");
            sb.AppendLine();
            sb.AppendLine("--- log ---");
            foreach (var l in r.Log) sb.AppendLine("  " + l);
            return sb.ToString();
        }
    }
}
