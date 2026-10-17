// OCID - 原生服务栈 DLL 的 P/Invoke 门面（OcidServicing.dll）
//
// ── 为什么要有这一层 ──────────────────────────────────────────────────────
//
// 服务规则（组件名推导、清单解析、WCM 模式、注册表值编码/合并、CMI 落地决策）都实现在
// C++ 侧（`Source/Native/OcidServicing/`）。C# 这边**只能搬运，不能自己解释**：
// 一旦两边各写一份"值文本 → hive 字节"的转换，REG_QWORD / REG_MULTI_SZ 这类
// 内存形态特殊的值就会在不同版本间静默写坏，而且极难发现（值看着"差不多"）。
//
// 所以本文件只做三件事：定位 DLL、封送参数、把返回码翻译成异常或结果对象。
// 任何一行"解释规则"的代码出现在这里都是设计错误。
//
// 导出的 C ABI 见 `Source/Native/OcidServicing/src/OcidServicingApi.cpp`，名字用
// `src/OcidServicing.def` 去掉 x86 的 `_Name@N` 修饰，因此这里按 `Cdecl` 声明。

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace OneCore_Image_Designer.Services
{
    /// <summary>写入动作，取值与 C++ 侧 <c>ocid::WriteAction</c> 一一对应。</summary>
    internal enum OcidWriteAction
    {
        /// <summary>写：新增，或按 hint 覆盖/合并后写。</summary>
        Write = 0,
        /// <summary>值已存在 + hint=replace + owner=true ⇒ **不写**（正常路径，不是错误）。</summary>
        SkipOwned = 1,
        /// <summary>合并失败（类型不同 / 长度非法）⇒ 不写。</summary>
        SkipMergeFailed = 2,
        /// <summary>operationHint 非法（对应 E_INVALIDARG 0x80070057）⇒ 不写。</summary>
        InvalidHint = 3,
    }

    /// <summary>一次落地决策的结果。</summary>
    internal sealed class OcidDecision
    {
        public OcidWriteAction Action { get; init; }
        public bool WillWrite { get; init; }
        public uint TypeCode { get; init; }
        public byte[] Data { get; init; } = Array.Empty<byte>();
        /// <summary>为什么这么决定（排障）。</summary>
        public string Note { get; init; } = "";
    }

    /// <summary>
    /// <c>OcidServicing.dll</c> 的调用门面。DLL 不存在时 <see cref="Available"/> 为 false，
    /// 调用方应退回"不写并告警"，**绝不能**退回一份 C# 自己实现的解释逻辑。
    /// </summary>
    internal static class OcidServicingNative
    {
        internal const string DllName = "OcidServicing.dll";

        /// <summary>返回码，与 C++ 侧匿名枚举一致。</summary>
        internal enum Rc
        {
            Ok = 0,
            Argument = 1,
            Buffer = 2,
            UnknownType = 3,
            BadValue = 4,
            Merge = 5,
            Internal = 6,
        }

        /// <summary>找到的 DLL 全路径；找不到为 null。</summary>
        public static string? DllPath { get; private set; }

        public static bool Available => DllPath != null;

        static OcidServicingNative()
        {
            DllPath = FindDll();
            if (DllPath != null)
            {
                // 用解析器把 "OcidServicing.dll" 指到我们找到的那个文件。
                // 不这么做的话，DllImport 只按默认搜索顺序找，开发期（DLL 在
                // Source\Native\Output\x86）必然找不到，且报错只说"找不到模块"。
                NativeLibrary.SetDllImportResolver(typeof(OcidServicingNative).Assembly, (name, asm, path) =>
                {
                    if (string.Equals(name, DllName, StringComparison.OrdinalIgnoreCase))
                        return DllPath == null ? IntPtr.Zero : NativeLibrary.Load(DllPath);
                    return IntPtr.Zero;
                });
            }
        }

        private static string? FindDll()
        {
            var candidates = new List<string>();
            void Add(string p)
            {
                if (!string.IsNullOrEmpty(p)) candidates.Add(p);
            }

            string baseDir = AppContext.BaseDirectory;
            // 32 位 DLL 没法被 64 位进程加载（反之亦然），所以先按本进程位数找对应架构，
            // 找不到再退回另一个架构 —— 这样至少错误信息能指出是"架构不匹配"，
            // 而不是笼统的"找不到模块"。
            bool is64 = Environment.Is64BitProcess;
            string arch = is64 ? "x64" : "x86";
            string other = is64 ? "x86" : "x64";

            foreach (var a in new[] { arch, other })
            {
                Add(Path.Combine(baseDir, a, DllName));
                Add(Path.Combine(baseDir, "CBS", a, DllName));
            }
            Add(Path.Combine(baseDir, DllName));
            Add(Path.Combine(baseDir, "CBS", DllName));

            // 仓库里原生构建的输出目录（开发期）。
            // ⚠ 不能用固定层数往上跳：主程序在 `bin\Release\net8.0-windows\`（3 层到仓库根），
            //   测试在 `Tests\bin\Release\net8.0-windows\`（4 层）。写成固定值必然有一边找不到，
            //   而且失败表现只是"模块找不到"，很难联想到是路径层数。所以逐层往上找。
            var dir = new DirectoryInfo(baseDir);
            for (int up = 0; up < 6 && dir != null; up++, dir = dir.Parent)
            {
                foreach (var a in new[] { arch, other })
                    Add(Path.Combine(dir.FullName, "Source", "Native", "Output", a, DllName));
            }

            // 必须**先**按偏好架构命中：上面的 Add 顺序已保证这一点，这里不再排序。
            foreach (var c in candidates)
            {
                try { if (File.Exists(c)) return Path.GetFullPath(c); } catch { /* 路径非法就跳过 */ }
            }
            return null;
        }

        /// <summary>自检：DLL 存在且 ABI 版本对得上。</summary>
        public static bool Probe(out string message)
        {
            if (DllPath == null) { message = $"未找到 {DllName}"; return false; }
            try
            {
                int v = OcidServicingVersion();
                message = $"OcidServicing v{v} @ {DllPath}";
                return v == 1;
            }
            catch (BadImageFormatException)
            {
                // 最容易被误读成"dll 坏了"的情况：架构不匹配。32 位进程载不了 x64 的 DLL。
                message = $"架构不匹配：本进程 {(Environment.Is64BitProcess ? "64" : "32")} 位，" +
                          $"但加载的是 {DllPath}。请确认 Output\\x64 与 Output\\x86 都已构建。";
                return false;
            }
            catch (Exception ex)
            {
                message = $"加载 {DllPath} 失败：{ex.Message}";
                return false;
            }
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int OcidServicingVersion();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern uint OcidRegValueTypeCode(string valueType);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int OcidRegValueEncode(
            string valueType, string text,
            [Out] byte[]? outBytes, uint cap, out uint outLen, out int outHasMacro,
            [Out] char[]? errBuf, uint errCap);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int OcidRegValueMerge(
            uint typeCode,
            byte[]? existing, uint existingLen,
            byte[]? incoming, uint incomingLen,
            int prepend,
            [Out] byte[]? outBytes, uint cap, out uint outLen);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int OcidCmiResolve(
            string mechanism, string operationHint, int owner,
            string valueType, string valueText,
            int valueExists, uint existingTypeCode,
            byte[]? existing, uint existingLen,
            [Out] byte[]? outBytes, uint outCap, out uint outLen, out uint outTypeCode,
            out int outAction, out int outWillWrite,
            [Out] char[]? noteBuf, uint noteCap,
            [Out] char[]? errBuf, uint errCap);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr OcidWriteActionName(int action);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int OcidExpandMacros(
            string windowsDir, string text,
            [Out] char[]? outBuf, uint outCap, out uint outLen,
            [Out] char[]? leftoverBuf, uint leftoverCap, out uint leftoverLen);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int OcidExpandMacrosEx(
            string windowsDir,
            string programFilesDir, string programFilesX86Dir,
            string commonFilesDir, string programDataDir, string usersDir,
            string text,
            [Out] char[]? outBuf, uint outCap, out uint outLen,
            [Out] char[]? leftoverBuf, uint leftoverCap, out uint leftoverLen);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int OcidKnownMacroNames(
            [Out] char[]? outBuf, uint outCap, out uint outLen);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int OcidApplyKeySddl(
            IntPtr keyHandle, string sddl, out int outApplied,
            [Out] char[]? errBuf, uint errCap);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int OcidReadKeySddl(
            IntPtr keyHandle, uint informationBits,
            [Out] char[]? outBuf, uint outCap, out uint outLen,
            [Out] char[]? errBuf, uint errCap);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int OcidApplyPathSddl(
            string path, string sddl, out int outApplied,
            [Out] char[]? errBuf, uint errCap);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int OcidReadPathSddl(
            string path, uint informationBits,
            [Out] char[]? outBuf, uint outCap, out uint outLen,
            [Out] char[]? errBuf, uint errCap);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int OcidPathExists(string path);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int OcidDescribeSddl(
            string sddl,
            out int hasOwner, out int hasGroup, out int hasDacl, out int hasSacl,
            out int daclProtected, out int daclNoAccessControl,
            out int daclAceCount, out int saclAceCount,
            out uint securityInformation,
            [Out] char[]? daclSids, uint daclSidsCap, out uint daclSidsLen, out int daclSidsAvailable,
            [Out] char[]? errBuf, uint errCap);

        /// <summary>
        /// 拿下键的所有权并把夺权前的 SDDL 交回来（用完由调用方还原）。
        /// 离线镜像里的 <c>DriverDatabase</c> 属于 SYSTEM/TrustedInstaller，管理员连枚举都被拒，
        /// 这条路上唯一能进门的钥匙是 SeTakeOwnershipPrivilege。
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int OcidTakeKeyOwnership(
            IntPtr rootKey, string subKey,
            [Out] char[]? outPriorSddl, uint outCap, out uint outLen,
            [Out] char[]? errBuf, uint errCap);

        /// <summary>当前进程用户 SID 文本（诊断用）。</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int OcidCurrentUserSid(
            [Out] char[]? outBuf, uint outCap, out uint outLen,
            [Out] char[]? errBuf, uint errCap);

        /// <summary>逐键夺权整棵树（子键各有受保护 DACL，只拿根键不够 —— 实测）。</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int OcidTakeKeyTreeOwnership(
            IntPtr rootKey, string subKey,
            [Out] char[]? outText, uint outCap, out uint outLen,
            [Out] char[]? errBuf, uint errCap);

        /// <summary>按 `相对路径\tSDDL` 文本逆序还原整棵树的 SDDL。</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int OcidRestoreKeyTreeSddl(
            IntPtr rootKey, string text, [Out] char[]? errBuf, uint errCap);
        // ── DMI：离线导入驱动包（见原生 include/ocid/DriverPackage.h 的证据链） ──

        /// <summary>驱动包目录名：lower("&lt;INF 名&gt;_&lt;架构&gt;_&lt;16 位十六进制&gt;")，哈希按 INF 文件字节算。</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int OcidDmiStoreId(
            string infPath, uint arch,
            [Out] char[]? outBuf, uint outCap, out uint outLen,
            [Out] char[]? errBuf, uint errCap);

        /// <summary>架构值 → StoreId 里的字符串；本版本只认 0/5/6/9，其他回 IntPtr.Zero。</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern IntPtr OcidDmiArchString(uint arch);

        /// <summary>属性 id → DriverDatabase 的值名（8061 是 "%04x" 的属性 id）。</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int OcidDmiDbValueName(
            uint propertyId,
            [Out] char[]? outBuf, uint outCap, out uint outLen,
            [Out] char[]? errBuf, uint errCap);

        /// <summary>oemN.inf 名分配：位图里的第一个 0 位（每字节从高位到低位），位图按值进出。</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int OcidDmiAcquireOemInfName(
            [In, Out] byte[]? bitmap, uint bitmapCap, ref uint bitmapLen,
            [Out] char[]? nameBuf, uint nameCap,
            out uint outIndex, out int outGrew,
            [Out] char[]? errBuf, uint errCap);

        /// <summary>释放 oemN.inf 名（清位），位图就地修改。</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int OcidDmiReleaseOemInfName(
            [In, Out] byte[]? bitmap, uint bitmapLen, string infName,
            [Out] char[]? errBuf, uint errCap);

        /// <summary>该导入标志组合是否让包保留原 INF 文件名（0x200 收件箱 / 0x100 OS 引导）。</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int OcidDmiKeepsOriginalInfName(uint importFlags);

        private const int ErrCap = 1024;
        private const int NoteCap = 1024;
        private const int LeftoverCap = 2048;

        /// <summary>清单里的 <c>valueType</c> 文本 → 真实 Win32 REG_* 码；0 = 未知。</summary>
        public static uint RegValueTypeCode(string valueType) => OcidRegValueTypeCode(valueType ?? "");

        /// <summary>
        /// 值文本 → 精确要写进 hive 的字节。
        /// </summary>
        /// <returns>成功返回 true；失败时 <paramref name="error"/> 有原因，<paramref name="bytes"/> 为空。</returns>
        public static bool TryEncode(string valueType, string text, out byte[] bytes,
                                     out bool hasMacro, out string error)
        {
            bytes = Array.Empty<byte>();
            hasMacro = false;
            error = "";
            var err = new char[ErrCap];

            int rc = OcidRegValueEncode(valueType ?? "", text ?? "",
                                        Array.Empty<byte>(), 0, out uint len, out int macro,
                                        err, ErrCap);
            hasMacro = macro != 0;
            if (rc == (int)Rc.Buffer && len > 0)
            {
                var buf = new byte[len];
                rc = OcidRegValueEncode(valueType ?? "", text ?? "",
                                        buf, (uint)buf.Length, out len, out macro,
                                        err, ErrCap);
                hasMacro = macro != 0;
                if (rc == (int)Rc.Ok) { bytes = buf; return true; }
            }
            else if (rc == (int)Rc.Ok)
            {
                return true;   // 合法的空值（例如 REG_NONE）
            }

            error = RcText(rc, err);
            return false;
        }

        /// <summary>
        /// CMI 落地决策：把「清单声明 + hive 现状」交给 C++ 侧算该不该写、写什么。
        /// </summary>
        /// <returns>成功算出决策返回 true（**哪怕结论是不写**）；参数/编码出错返回 false。</returns>
        public static bool TryResolveWrite(
            string mechanism, string operationHint, bool owner,
            string valueType, string valueText,
            bool valueExists, uint existingTypeCode, byte[] existing,
            out OcidDecision decision, out string error)
        {
            decision = new OcidDecision();
            error = "";

            var err = new char[ErrCap];
            var note = new char[NoteCap];
            byte[]? ex = existing.Length > 0 ? existing : null;

            int rc = OcidCmiResolve(mechanism ?? "", operationHint ?? "", owner ? 1 : 0,
                                    valueType ?? "", valueText ?? "",
                                    valueExists ? 1 : 0, existingTypeCode,
                                    ex, (uint)existing.Length,
                                    Array.Empty<byte>(), 0, out uint len, out uint typeCode,
                                    out int action, out int willWrite,
                                    note, NoteCap, err, ErrCap);

            byte[] data = Array.Empty<byte>();
            if (rc == (int)Rc.Buffer && len > 0)
            {
                var buf = new byte[len];
                rc = OcidCmiResolve(mechanism ?? "", operationHint ?? "", owner ? 1 : 0,
                                    valueType ?? "", valueText ?? "",
                                    valueExists ? 1 : 0, existingTypeCode,
                                    ex, (uint)existing.Length,
                                    buf, (uint)buf.Length, out len, out typeCode,
                                    out action, out willWrite,
                                    note, NoteCap, err, ErrCap);
                if (rc == (int)Rc.Ok) data = buf;
            }

            if (rc != (int)Rc.Ok)
            {
                error = RcText(rc, err);
                return false;
            }

            decision = new OcidDecision
            {
                Action = (OcidWriteAction)action,
                WillWrite = willWrite != 0,
                TypeCode = typeCode,
                Data = data,
                Note = CStr(note),
            };
            return true;
        }

        /// <summary>决策名（日志用），直接问 C++ 侧要，避免两边枚举名漂移。</summary>
        public static string ActionName(OcidWriteAction action)
        {
            try
            {
                IntPtr p = OcidWriteActionName((int)action);
                return p == IntPtr.Zero ? action.ToString() : (Marshal.PtrToStringUni(p) ?? action.ToString());
            }
            catch { return action.ToString(); }
        }

        /// <summary>
        /// 展开清单值里的 <c>$(runtime.*)</c> 宏。基础路径按 <paramref name="windowsDir"/>
        /// （离线镜像形如 <c>P:\Windows</c>）推导，**离线 hive 给不出这些路径**，详见
        /// <c>Source\Native\OcidServicing\include\ocid\MacroExpander.h</c>。
        /// </summary>
        /// <param name="leftover">
        /// 没展开、原样保留的宏（<c>;</c> 分隔）。**不是错误** —— <c>$(string.*)</c>、
        /// <c>$(build.nttree)</c> 这些本来就不是路径宏，调用方自己决定要不要告警。
        /// </param>
        public static bool TryExpandMacros(string windowsDir, string text,
                                           out string expanded, out string leftover, out string error)
            => ExpandCore(null, windowsDir, text, out expanded, out leftover, out error);

        /// <summary>同上，但用镜像自己声明的目录覆盖（<c>x:\Program Files</c> 那种占位盘符）。</summary>
        public static bool TryExpandMacrosEx(string windowsDir,
                                             string programFilesDir, string programFilesX86Dir,
                                             string commonFilesDir, string programDataDir,
                                             string usersDir, string text,
                                             out string expanded, out string leftover, out string error)
            => ExpandCore(ImageDirs.Of(programFilesDir, programFilesX86Dir, commonFilesDir,
                                       programDataDir, usersDir),
                          windowsDir, text, out expanded, out leftover, out error);

        /// <summary>镜像自己声明的目录；全空表示不覆盖。</summary>
        private readonly struct ImageDirs
        {
            public readonly string Pf, PfX86, Cf, Pd, Users;
            private ImageDirs(string pf, string pfX86, string cf, string pd, string users)
            { Pf = pf; PfX86 = pfX86; Cf = cf; Pd = pd; Users = users; }

            public static ImageDirs Of(string pf, string pfX86, string cf, string pd, string users)
                => new(pf ?? "", pfX86 ?? "", cf ?? "", pd ?? "", users ?? "");
        }

        private static bool ExpandCore(ImageDirs? dirs, string windowsDir, string text,
                                       out string expanded, out string leftover, out string error)
        {
            expanded = text ?? "";
            leftover = "";
            error = "";

            try
            {
                var lf = new char[LeftoverCap];

                int Call(char[]? buf, uint cap, out uint len, out uint lfLen)
                {
                    if (dirs is { } d)
                        return OcidExpandMacrosEx(windowsDir ?? "", d.Pf, d.PfX86, d.Cf, d.Pd, d.Users,
                                                  text ?? "", buf, cap, out len, lf, LeftoverCap, out lfLen);
                    return OcidExpandMacros(windowsDir ?? "", text ?? "",
                                            buf, cap, out len, lf, LeftoverCap, out lfLen);
                }

                int rc = Call(Array.Empty<char>(), 0, out uint need, out _);
                if (rc == (int)Rc.Buffer)
                {
                    // `need` 已是"不含结尾 NUL 的字符数"（C++ 侧 CopyOutStr 的约定）。
                    var buf = new char[need + 1];
                    rc = Call(buf, (uint)buf.Length, out need, out _);
                    if (rc == (int)Rc.Ok) expanded = new string(buf, 0, (int)need);
                }
                else if (rc == (int)Rc.Ok)
                {
                    expanded = "";
                }

                if (rc != (int)Rc.Ok)
                {
                    error = RcText(rc, new char[ErrCap]);
                    return false;
                }

                leftover = CStr(lf);
                return true;
            }
            catch (DllNotFoundException ex) { error = $"未找到原生库：{ex.Message}"; return false; }
            catch (EntryPointNotFoundException ex) { error = $"原生库缺少宏展开导出（版本不同步）：{ex.Message}"; return false; }
            catch (BadImageFormatException) { error = "架构不匹配：OcidServicing.dll 位数与本进程不同"; return false; }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        /// <summary>已知的 <c>$(runtime.*)</c> 宏名（<c>;</c> 分隔），工具/诊断用。</summary>
        public static string KnownMacroNames()
        {
            try
            {
                var buf = new char[1024];
                int rc = OcidKnownMacroNames(buf, (uint)buf.Length, out uint len);
                return rc == (int)Rc.Ok ? new string(buf, 0, (int)len) : "";
            }
            catch { return ""; }
        }

        // ─────────────────────────────────────────────────────────────────────
        // 安全描述符（SDDL）→ 离线 hive 里的键
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>`SECURITY_INFORMATION` 位（winnt.h）。</summary>
        internal static class SecInfo
        {
            public const uint Owner = 0x00000001;
            public const uint Group = 0x00000002;
            public const uint Dacl = 0x00000004;
            public const uint Sacl = 0x00000010;

            /// <summary>
            /// 回读时的默认位：OWNER|GROUP|DACL，**不读 SACL**。
            /// 读 SACL 需要 `SeSecurityPrivilege`，拿不到是常态；硬要读会让"回读校验"整体失败，
            /// 反而把真正关心的 DACL 是否正确给掩盖掉。
            /// </summary>
            public const uint DefaultRead = Owner | Group | Dacl;
        }

        /// <summary>SDDL 的结构事实（纯文本解读，不碰注册表）。</summary>
        internal readonly struct SddlFacts
        {
            public bool HasOwner { get; init; }
            public bool HasGroup { get; init; }
            public bool HasDacl { get; init; }
            public bool HasSacl { get; init; }
            public bool DaclProtected { get; init; }
            public bool DaclNoAccessControl { get; init; }
            public int DaclAceCount { get; init; }
            public int SaclAceCount { get; init; }
            /// <summary>这段 SDDL 实际需要设置的 SECURITY_INFORMATION 位。</summary>
            public uint SecurityInformation { get; init; }

            /// <summary>
            /// DACL 里每条 ACE 的**规范 SID 文本**（`S-1-5-…`），顺序与 ACE 一致。
            /// 回读校验比的是它而不是 ACE 条数 —— Windows 应用 DACL 时会把"仅继承"ACE
            /// 拆成两条，条数会变、SID 集合不变。
            /// </summary>
            public IReadOnlyList<string> DaclSids { get; init; }
            /// <summary>DaclSids 是否可信；false 时只能退回"条数不少于"的弱比对。</summary>
            public bool DaclSidsAvailable { get; init; }
        }

        /// <summary>应用 SDDL 的结果。**跳过（(inherit)）是合法结果，不是失败。**</summary>
        internal enum SddlApplyOutcome
        {
            Applied,
            SkippedInherit,
            Failed,
        }

        /// <summary>
        /// 回读的 DACL 与写入意图是否**同一个策略**（比 SID 集合，不比条数、不比顺序）。
        ///
        /// 【为什么不能比条数】Windows **应用** DACL 时会把"仅继承"（`IO`）ACE 拆成
        /// "对本对象生效"+"仅继承"两条：实测把 8 条
        /// <c>D:P(A;CI;GA;;;TI)(A;;0x1301bf;;;SY)…(A;CIOI;GRGX;;;BU)…</c> 落到目录上，
        /// 回读是 **11 条**（SID 集合一模一样、顺序变成规范序）。所以条数只要求"不少于"。
        ///
        /// **SID 集合**才是稳定事实：声明里有的 SID 回读必须都有；声明是**受保护** DACL 时
        /// 还必须一个不多 —— `P` 的意义就是挡住父对象的继承 ACE 掺进来。
        /// 两侧都取自原生 `ConvertSidToStringSidW` 的规范化结果，别名（`BA`）与
        /// SID（`S-1-5-32-544`）因此不会造成假失败。
        /// </summary>
        /// <returns>true = 集合关系成立；<paramref name="note"/> 给出"为什么没比成"或缺失项。</returns>
        internal static bool SidsCompatible(SddlFacts want, SddlFacts got, out string note)
        {
            note = "";
            if (!want.DaclSidsAvailable || !got.DaclSidsAvailable)
            {
                note = "（一侧的 SID 集合未能规范化，只比了条数）";
                return true;
            }

            var w = new HashSet<string>(want.DaclSids, StringComparer.OrdinalIgnoreCase);
            var g = new HashSet<string>(got.DaclSids, StringComparer.OrdinalIgnoreCase);
            if (!w.IsSubsetOf(g))
            {
                note = "声明里的 SID 回读不见了：" +
                       string.Join(",", w.Except(g).OrderBy(x => x, StringComparer.Ordinal));
                return false;
            }
            if (want.DaclProtected && !g.SetEquals(w))
            {
                note = "声明是受保护 DACL，回读却多出 SID：" +
                       string.Join(",", g.Except(w).OrderBy(x => x, StringComparer.Ordinal));
                return false;
            }
            return true;
        }

        /// <summary>SDDL 的文本级解读，用于落盘前决策与告警。</summary>
        public static bool TryDescribeSddl(string sddl, out SddlFacts facts, out string error)
        {
            facts = default;
            error = "";
            var err = new char[ErrCap];
            try
            {
                // SID 列表用"先问长度、再取内容"的两趟调用，避免猜一个够不够大的缓冲。
                int rc = OcidDescribeSddl(sddl ?? "",
                                          out int ho, out int hg, out int hd, out int hs,
                                          out int prot, out int noAc,
                                          out int dAce, out int sAce, out uint info,
                                          null, 0, out uint sidsLen, out int sidsAvail,
                                          err, ErrCap);
                if (rc != (int)Rc.Ok && rc != (int)Rc.Buffer) { error = ErrText(err, rc); return false; }

                var sids = new List<string>();
                bool avail = sidsAvail != 0;
                if (sidsLen > 0)
                {
                    var buf = new char[sidsLen + 1];
                    rc = OcidDescribeSddl(sddl ?? "",
                                          out ho, out hg, out hd, out hs,
                                          out prot, out noAc,
                                          out dAce, out sAce, out info,
                                          buf, (uint)buf.Length, out uint got, out sidsAvail,
                                          err, ErrCap);
                    if (rc != (int)Rc.Ok) { error = ErrText(err, rc); return false; }
                    avail = sidsAvail != 0;
                    string joined = new string(buf, 0, (int)Math.Min(got, (uint)buf.Length - 1));
                    if (joined.Length > 0)
                        sids.AddRange(joined.Split(';', StringSplitOptions.RemoveEmptyEntries));
                }

                facts = new SddlFacts
                {
                    HasOwner = ho != 0,
                    HasGroup = hg != 0,
                    HasDacl = hd != 0,
                    HasSacl = hs != 0,
                    DaclProtected = prot != 0,
                    DaclNoAccessControl = noAc != 0,
                    DaclAceCount = dAce,
                    SaclAceCount = sAce,
                    SecurityInformation = info,
                    DaclSids = sids,
                    DaclSidsAvailable = avail,
                };
                return true;
            }
            catch (Exception ex)
            {
                error = $"调用原生库失败：{ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// 把 SDDL 写到**已打开的键**上（<paramref name="keyHandle"/> =
        /// <c>RegistryKey.Handle.DangerousGetHandle()</c>）。
        /// </summary>
        /// <remarks>
        /// 写 owner/group 需要 `SeRestorePrivilege`、写 SACL 需要 `SeSecurityPrivilege`；
        /// 只写 DACL 不需要额外特权。调用方应在此之前启用它们
        /// （<see cref="PrivilegeHelper.EnableServicingPrivileges"/> 已包含）。
        /// </remarks>
        public static SddlApplyOutcome ApplyKeySddl(IntPtr keyHandle, string sddl, out string error)
        {
            error = "";
            var err = new char[ErrCap];
            try
            {
                int rc = OcidApplyKeySddl(keyHandle, sddl ?? "", out int applied, err, ErrCap);
                if (rc == (int)Rc.Ok)
                    return applied != 0 ? SddlApplyOutcome.Applied : SddlApplyOutcome.SkippedInherit;
                error = ErrText(err, rc);
                return SddlApplyOutcome.Failed;
            }
            catch (Exception ex)
            {
                error = $"调用原生库失败：{ex.Message}";
                return SddlApplyOutcome.Failed;
            }
        }

        /// <summary>
        /// 回读键的安全描述符 → SDDL 文本。
        /// </summary>
        /// <param name="informationBits">
        /// 0 ⇒ <see cref="SecInfo.DefaultRead"/>（OWNER|GROUP|DACL）。
        /// 只想比对 DACL 时传 <see cref="SecInfo.Dacl"/>：读 OWNER|GROUP 时回读结果**一定**带
        /// <c>O:</c>/<c>G:</c> 段，拿它跟"只写了 D: 的目标 SDDL"比会假失败。
        /// </param>
        public static bool TryReadKeySddl(IntPtr keyHandle, uint informationBits,
                                          out string sddl, out string error)
        {
            sddl = "";
            error = "";
            var err = new char[ErrCap];
            try
            {
                var buf = new char[4 * 1024];
                int rc = OcidReadKeySddl(keyHandle, informationBits, buf, (uint)buf.Length,
                                         out uint len, err, ErrCap);
                if (rc == (int)Rc.Buffer && len > 0)
                {
                    buf = new char[len + 1];
                    rc = OcidReadKeySddl(keyHandle, informationBits, buf, (uint)buf.Length,
                                         out len, err, ErrCap);
                }
                if (rc != (int)Rc.Ok) { error = ErrText(err, rc); return false; }

                sddl = new string(buf, 0, (int)len);
                return true;
            }
            catch (Exception ex)
            {
                error = $"调用原生库失败：{ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// 拿下键的所有权（管理员 + SeTakeOwnershipPrivilege），返回**夺权前**的 SDDL。
        /// </summary>
        /// <remarks>
        /// 夺权后键上是一条「当前用户 KEY_ALL_ACCESS」的受保护临时 ACL —— 写完之后**必须**
        /// 用返回的 SDDL 调 <see cref="ApplyKeySddl"/> 还原，否则就是把镜像里的安全边界改掉了。
        /// 返回的 SDDL 为空串表示夺权前读不到（无法还原），调用方应当把它当失败处理。
        /// </remarks>
        public static bool TryTakeKeyOwnership(IntPtr rootKey, string subKey,
                                               out string priorSddl, out string error)
        {
            priorSddl = "";
            error = "";
            var err = new char[ErrCap];
            try
            {
                var buf = new char[8 * 1024];
                int rc = OcidTakeKeyOwnership(rootKey, subKey, buf, (uint)buf.Length,
                                              out uint len, err, ErrCap);
                if (rc == (int)Rc.Buffer && len > 0)
                {
                    buf = new char[len + 1];
                    rc = OcidTakeKeyOwnership(rootKey, subKey, buf, (uint)buf.Length, out len, err, ErrCap);
                }
                if (rc != (int)Rc.Ok) { error = ErrText(err, rc); return false; }
                priorSddl = new string(buf, 0, (int)len);
                return true;
            }
            catch (Exception ex)
            {
                error = $"调用原生库失败：{ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// 逐键夺权整棵树（含 <paramref name="subKey"/> 自身），返回每层夺权前的
        /// `相对路径&lt;TAB&gt;SDDL` 文本（`\r\n` 分隔）。
        /// </summary>
        /// <remarks>
        /// 为什么要树：<c>DriverDatabase</c> 的子键（DriverPackages/…）各有**受保护** DACL，
        /// 继承 ACE 的传播只发生在创建时，不会补写到已存在的子键（实测把父键 ACE 改成可继承
        /// 再重设 DACL 也无效）⇒ 只能逐键夺权。用完必须调
        /// <see cref="TryRestoreKeyTreeSddl"/> 逆序还原。
        /// </remarks>
        public static bool TryTakeKeyTreeOwnership(IntPtr rootKey, string subKey,
                                                  out string priorText, out string error)
        {
            priorText = "";
            error = "";
            var err = new char[ErrCap];
            try
            {
                // 缓冲区要**循环**扩容：第二次夺权读到的"原 SDDL"其实是上一次留下的临时 ACL，
                // 里面的 owner 从 SYSTEM 换成了当前用户（SID 文本更长），所以重试一次反而可能更长。
                var buf = new char[64 * 1024];
                int rc = (int)Rc.Buffer;
                uint len = 0;
                for (int attempt = 0; attempt < 6 && rc == (int)Rc.Buffer; attempt++)
                {
                    rc = OcidTakeKeyTreeOwnership(rootKey, subKey, buf, (uint)buf.Length, out len, err, ErrCap);
                    if (rc == (int)Rc.Buffer)
                    {
                        buf = new char[Math.Max(len * 2 + 64, 64 * 1024)];
                    }
                }
                if (rc != (int)Rc.Ok) { error = ErrText(err, rc); return false; }
                priorText = new string(buf, 0, (int)len);
                return true;
            }
            catch (Exception ex)
            {
                error = $"调用原生库失败：{ex.Message}";
                return false;
            }
        }

        /// <summary>按 <see cref="TryTakeKeyTreeOwnership"/> 交回的文本逆序还原 SDDL。</summary>
        public static bool TryRestoreKeyTreeSddl(IntPtr rootKey, string priorText, out string error)
        {
            error = "";
            var err = new char[ErrCap];
            try
            {
                int rc = OcidRestoreKeyTreeSddl(rootKey, priorText ?? "", err, ErrCap);
                if (rc != (int)Rc.Ok) { error = ErrText(err, rc); return false; }
                return true;
            }
            catch (Exception ex)
            {
                error = $"调用原生库失败：{ex.Message}";
                return false;
            }
        }
        /// <summary>当前进程用户 SID 文本（diagnostics）。</summary>
        public static bool TryGetCurrentUserSid(out string sid, out string error)
        {
            sid = "";
            error = "";
            var err = new char[ErrCap];
            try
            {
                var buf = new char[512];
                int rc = OcidCurrentUserSid(buf, (uint)buf.Length, out uint len, err, ErrCap);
                if (rc != (int)Rc.Ok) { error = ErrText(err, rc); return false; }
                sid = new string(buf, 0, (int)len);
                return true;
            }
            catch (Exception ex)
            {
                error = $"调用原生库失败：{ex.Message}";
                return false;
            }
        }
        // ─────────────────────────────────────────────────────────────────────
        // 安全描述符（SDDL）→ 磁盘上的文件/目录（CSI 落位）
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 把 SDDL 写到**文件或目录路径**上（<c>SetNamedSecurityInfoW</c>）。
        /// </summary>
        /// <remarks>
        /// 与 <see cref="ApplyKeySddl"/> 的区别不只是 API：文件这条路的
        /// "DACL 是否受保护"必须由 SECURITY_INFORMATION 标志说明
        /// （<c>SetNamedSecurityInfoW</c> **不看**描述符里的 `SE_DACL_PROTECTED`），
        /// 这个换算做在原生 <c>ApplySddlToPath</c> 里，C# 侧**不要**再算一遍。
        /// 写 owner/group 要 `SeRestorePrivilege`、写 SACL 要 `SeSecurityPrivilege`。
        /// </remarks>
        public static SddlApplyOutcome ApplyPathSddl(string path, string sddl, out string error)
        {
            error = "";
            var err = new char[ErrCap];
            try
            {
                int rc = OcidApplyPathSddl(path ?? "", sddl ?? "", out int applied, err, ErrCap);
                if (rc == (int)Rc.Ok)
                    return applied != 0 ? SddlApplyOutcome.Applied : SddlApplyOutcome.SkippedInherit;
                error = ErrText(err, rc);
                return SddlApplyOutcome.Failed;
            }
            catch (Exception ex)
            {
                error = $"调用原生库失败：{ex.Message}";
                return SddlApplyOutcome.Failed;
            }
        }

        /// <summary>回读文件/目录的安全描述符 → SDDL 文本（0 ⇒ OWNER|GROUP|DACL）。</summary>
        public static bool TryReadPathSddl(string path, uint informationBits,
                                           out string sddl, out string error)
        {
            sddl = "";
            error = "";
            var err = new char[ErrCap];
            try
            {
                var buf = new char[4 * 1024];
                int rc = OcidReadPathSddl(path ?? "", informationBits, buf, (uint)buf.Length,
                                          out uint len, err, ErrCap);
                if (rc == (int)Rc.Buffer && len > 0)
                {
                    buf = new char[len + 1];
                    rc = OcidReadPathSddl(path ?? "", informationBits, buf, (uint)buf.Length,
                                          out len, err, ErrCap);
                }
                if (rc != (int)Rc.Ok) { error = ErrText(err, rc); return false; }

                sddl = new string(buf, 0, (int)len);
                return true;
            }
            catch (Exception ex)
            {
                error = $"调用原生库失败：{ex.Message}";
                return false;
            }
        }

        /// <summary>路径是否存在（走原生侧，与落位时的判断同源）。</summary>
        public static bool PathExists(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try { return OcidPathExists(path) != 0; }
            catch { return File.Exists(path) || Directory.Exists(path); }
        }

        /// <summary>
        /// 安全描述符相关的失败文本：优先用原生侧给的具体原因（它知道是 Win32 错误几号、
        /// 需要哪个特权），退化成返回码名。
        /// </summary>
        private static string ErrText(char[] err, int rc)
        {
            string e = CStr(err);
            if (e.Length > 0) return e;
            return ((Rc)rc) switch
            {
                Rc.Argument => "参数错误",
                Rc.BadValue => "安全描述符非法",
                Rc.Buffer => "缓冲不足",
                Rc.Internal => "原生侧未预期异常",
                _ => $"rc={rc}",
            };
        }

        private static string CStr(char[] buf)
        {
            int n = Array.IndexOf(buf, '\0');
            if (n < 0) n = buf.Length;
            return new string(buf, 0, n);
        }

        private static string RcText(int rc, char[] err)
        {
            string e = CStr(err);
            string what = ((Rc)rc) switch
            {
                Rc.Argument => "参数错误",
                Rc.Buffer => "缓冲不足",
                Rc.UnknownType => "未知的 valueType",
                Rc.BadValue => "值文本非法",
                Rc.Merge => "合并失败（类型不同或长度非法）",
                Rc.Internal => "原生侧未预期异常",
                _ => $"rc={rc}",
            };
            return e.Length > 0 ? $"{what}：{e}" : what;
        }

        // ────────────────────────────────────────────────────────────────────
        // DMI（离线导入驱动包）—— 原生实现见 include/ocid/DriverPackage.h。
        // 这里只做搬运：判断规则一条都不在 C# 侧重写。
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 驱动包目录名 <c>FileRepository\&lt;StoreId&gt;</c> 的那一层：
        /// <c>lower("&lt;INF 文件名&gt;_&lt;架构串&gt;_&lt;16 位小写十六进制&gt;")</c>。
        /// 哈希按 INF 的**文件字节**算 —— 必须对"将要写进镜像的那一份"调用。
        /// </summary>
        public static bool TryDmiStoreId(string infPath, uint arch, out string storeId, out string error)
        {
            storeId = string.Empty;
            var err = new char[ErrCap];
            int cap = 512;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var buf = new char[cap];
                int rc = OcidDmiStoreId(infPath ?? string.Empty, arch, buf, (uint)buf.Length,
                                        out uint len, err, (uint)err.Length);
                if (rc == (int)Rc.Buffer) { cap = (int)len + 1; continue; }
                if (rc != 0) { error = RcText(rc, err); return false; }
                storeId = CStr(buf);
                error = string.Empty;
                return true;
            }
            error = "StoreId 长度反复不足";
            return false;
        }

        /// <summary>架构值 → StoreId 里用的字符串；本版本只认 0=x86 / 5=arm / 6=ia64 / 9=amd64。</summary>
        public static string? DmiArchString(uint arch)
        {
            var p = OcidDmiArchString(arch);
            return p == IntPtr.Zero ? null : Marshal.PtrToStringUni(p);
        }

        /// <summary>属性 id → DriverDatabase 里的值名（8061 是 "%04x" 的属性 id）。</summary>
        public static string DmiDbValueName(uint propertyId)
        {
            var err = new char[ErrCap];
            int cap = 16;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var buf = new char[cap];
                int rc = OcidDmiDbValueName(propertyId, buf, (uint)buf.Length, out uint len, err, (uint)err.Length);
                if (rc == (int)Rc.Buffer) { cap = (int)len + 1; continue; }
                if (rc != 0) throw new InvalidOperationException(RcText(rc, err));
                return CStr(buf);
            }
            throw new InvalidOperationException("DriverDatabase 值名长度反复不足");
        }

        /// <summary>
        /// 分配一个 <c>oemN.inf</c> 名：位图里**第一个 0 位**（每字节从高位到低位扫），
        /// 位图满则追加一个 0x80 字节。返回的位图必须写回 DriverDatabase 的 "0004"。
        /// </summary>
        public static bool TryDmiAcquireOemInfName(byte[]? bitmap, out byte[] newBitmap, out string name,
                                                   out uint index, out bool grew, out string error)
        {
            newBitmap = bitmap ?? Array.Empty<byte>();
            name = string.Empty;
            index = 0;
            grew = false;

            var err = new char[ErrCap];
            var nameBuf = new char[300];
            uint len = (uint)newBitmap.Length;
            int cap = Math.Max(newBitmap.Length + 1, 16);

            for (int attempt = 0; attempt < 3; attempt++)
            {
                var buf = new byte[cap];
                Array.Copy(newBitmap, buf, newBitmap.Length);
                uint n = len;
                int rc = OcidDmiAcquireOemInfName(buf, (uint)buf.Length, ref n,
                                                  nameBuf, (uint)nameBuf.Length,
                                                  out index, out int g, err, (uint)err.Length);
                if (rc == (int)Rc.Buffer) { cap = (int)n + 1; continue; }
                if (rc != 0) { error = RcText(rc, err); return false; }

                newBitmap = new byte[n];
                Array.Copy(buf, newBitmap, (int)n);
                name = CStr(nameBuf);
                grew = g != 0;
                error = string.Empty;
                return true;
            }
            error = "oemN.inf 位图容量反复不足";
            return false;
        }

        /// <summary>释放一个 oemN.inf 名（清位）。位图**就地**修改。</summary>
        public static bool TryDmiReleaseOemInfName(byte[] bitmap, string infName, out string error)
        {
            var err = new char[ErrCap];
            int rc = OcidDmiReleaseOemInfName(bitmap, (uint)bitmap.Length, infName ?? string.Empty,
                                              err, (uint)err.Length);
            if (rc != 0) { error = RcText(rc, err); return false; }
            error = string.Empty;
            return true;
        }

        /// <summary>这个导入标志组合是否让包保留原 INF 文件名（0x200 收件箱 / 0x100 OS 引导）。</summary>
        public static bool DmiKeepsOriginalInfName(uint importFlags) =>
            OcidDmiKeepsOriginalInfName(importFlags) != 0;
    }
}
