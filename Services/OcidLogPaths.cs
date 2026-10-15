using System;
using System.IO;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// OCID 的**日志落点统一入口** —— 所有日志都进 <c>OCID_Logs</c>，不再散落在 Temp 根、
    /// <c>crash_logs</c>、<c>OneCoreImageDesigner_Logs</c> 等各处。
    ///
    /// 【为什么要统一】此前日志散在四处，排障时要到处找，而且"程序到底把东西写哪了"这个问题
    /// 反复消耗时间（连带出过"以为没有残留、其实写在别处"的误判）：
    ///   · <c>&lt;BaseDirectory&gt;\crash_logs\</c>              —— FreezeWatchdog、DeviceLayoutService
    ///   · <c>%TEMP%\OneCoreImageDesigner_Logs\</c>   —— DriverExtractor、ComponentExtractor
    ///   · <c>%TEMP%\</c> 根下零散文件                —— cbsxcli_*.txt、ocid_postinstall_*.log、ocid_reghook*
    ///
    /// 【新的目录结构】
    /// <code>
    /// OCID_Logs\                       ← 统一根目录
    ///   ocid.log                       ← 主日志（如需要）
    ///   cbs\                           ← CBS / 装包相关
    ///   reghook\                       ← 注册表截获（OcidRegHook）
    ///   crash\                         ← 原来 crash_logs 的内容（trace_last.log 等）
    ///   misc\                          ← 其它零散产物
    /// </code>
    ///
    /// 【放哪】优先程序目录（<see cref="AppDomain.BaseDirectory"/>）；**若那里不可写**
    /// （例：程序装在 Program Files 且非管理员），自动回退到
    /// <c>%LOCALAPPDATA%\OneCoreImageDesigner\OCID_Logs</c>，**绝不因为写不了日志而让功能失败**。
    ///
    /// 【纪律】新增日志一律走这里，不要再自己拼 <c>Path.GetTempPath()</c> 或 <c>crash_logs</c>。
    /// </summary>
    public static class OcidLogPaths
    {
        const string RootName = "OCID_Logs";

        static readonly object _lock = new();
        static string? _root;           // 已解析好的根目录（缓存）
        static bool _probed;            // 是否已经探测过可写性

        /// <summary>统一日志根目录（首次访问时探测可写性并创建子目录）。</summary>
        public static string Root
        {
            get
            {
                lock (_lock)
                {
                    if (_root != null && _probed) return _root!;

                    string primary = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, RootName);
                    string fallback = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "OneCoreImageDesigner", RootName);

                    _root = TryPrepare(primary) ? primary
                          : TryPrepare(fallback) ? fallback
                          : Path.Combine(Path.GetTempPath(), RootName);   // 最后兜底：至少能写
                    _probed = true;
                    EnsureSubDirs();
                    return _root!;
                }
            }
        }

        /// <summary>原 <c>crash_logs</c> 的内容（崩溃/冻结轨迹）都放这里。</summary>
        public static string CrashDir => Sub("crash");
        /// <summary>CBS / 装包相关日志。</summary>
        public static string CbsDir => Sub("cbs");
        /// <summary>注册表截获（OcidRegHook）相关。</summary>
        public static string RegHookDir => Sub("reghook");
        /// <summary>其它零散产物。</summary>
        public static string MiscDir => Sub("misc");

        /// <summary>拼一个不冲突的文件名（带时间戳），避免多次运行互相覆盖。</summary>
        public static string Stamped(string dir, string baseName, string ext = ".log")
        {
            EnsureDir(dir);
            return Path.Combine(dir, $"{baseName}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
        }

        public static string Auto(string dir, string baseName, string ext = ".log")
            => Path.Combine(dir, baseName + ext);

        /// <summary>确保某个目录存在（失败不抛，只返回路径）。</summary>
        public static string EnsureDir(string dir)
        {
            try { if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir); }
            catch { }
            return dir;
        }

        /// <summary>供界面/日志展示的一句话说明，讲清"日志到底写到哪了"。</summary>
        public static string Describe()
            => $"OCID_Logs = {Root}（crash = {CrashDir}）";

        // ────────────────────────────────────────────────────────────────

        static string Sub(string name) => Path.Combine(Root, name);

        static void EnsureSubDirs()
        {
            foreach (var d in new[] { CbsDir, RegHookDir, CrashDir, MiscDir }) EnsureDir(d);
        }

        /// <summary>探测目录能不能写：能建目录且能落一个探针文件就认为可写。</summary>
        static bool TryPrepare(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".write_probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }
    }
}
