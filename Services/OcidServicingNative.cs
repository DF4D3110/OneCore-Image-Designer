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
        private static extern int OcidDescribeSddl(
            string sddl,
            out int hasOwner, out int hasGroup, out int hasDacl, out int hasSacl,
            out int daclProtected, out int daclNoAccessControl,
            out int daclAceCount, out int saclAceCount,
            out uint securityInformation,
            [Out] char[]? errBuf, uint errCap);

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
        }

        /// <summary>应用 SDDL 的结果。**跳过（(inherit)）是合法结果，不是失败。**</summary>
        internal enum SddlApplyOutcome
        {
            Applied,
            SkippedInherit,
            Failed,
        }

        /// <summary>SDDL 的文本级解读，用于落盘前决策与告警。</summary>
        public static bool TryDescribeSddl(string sddl, out SddlFacts facts, out string error)
        {
            facts = default;
            error = "";
            var err = new char[ErrCap];
            try
            {
                int rc = OcidDescribeSddl(sddl ?? "",
                                          out int ho, out int hg, out int hd, out int hs,
                                          out int prot, out int noAc,
                                          out int dAce, out int sAce, out uint info,
                                          err, ErrCap);
                if (rc != (int)Rc.Ok) { error = ErrText(err, rc); return false; }

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
    }
}
