using System;
using System.Collections.Generic;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// updateapp 单条命令（从该 build 的官方 usage 文本解析）。
    /// </summary>
    public class UpdateAppCommandInfo
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>官方参数模板（如 "&lt;path to image&gt; &lt;path to package...&gt;"）。</summary>
        public string ArgsTemplate { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public string Category { get; set; } = "其他";

        /// <summary>风险分级：Safe=只读或文件级；Mutating=修改镜像；Live=可能重启/改运行中系统；Destructive=清空数据。</summary>
        public string Risk
        {
            get
            {
                switch (Name.ToLowerInvariant())
                {
                    case "cleanup":
                    case "reset":
                    case "remove":
                    case "recall":
                        return "Destructive";
                    case "install":
                    case "installfromsd":
                    case "installinprocnoreboot":
                    case "installfromsdinprocnoreboot":
                    case "mountandinstall":
                    case "stage":
                    case "stageinproc":
                    case "stagetosd":
                    case "stagetosdinproc":
                    case "staged":
                    case "commit":
                    case "postrebootcommit":
                    case "finalizecommit":
                    case "rollback":
                    case "installfeatures":
                    case "uninstallfeatures":
                    case "createclone":
                    case "commitclone":
                        return "Mutating";
                    default:
                        return "Safe";
                }
            }
        }

        /// <summary>模板是否要求提供离线路径（image）。</summary>
        public bool NeedsImage => ArgsTemplate.Contains("<path to image>");

        /// <summary>模板是否接受包/动作清单路径。</summary>
        public bool NeedsPackages =>
            ArgsTemplate.Contains("<path to package") || ArgsTemplate.Contains("<path to directory");

        public string DisplayText =>
            string.IsNullOrEmpty(ArgsTemplate) ? Name : $"{Name}  {ArgsTemplate}";

        /// <summary>可能重启设备或修改运行中系统的命令（UI 需红字提醒）。</summary>
        public bool MayRebootOrMutateLive =>
            Name is "install" or "installfromsd" or "commit" or "postrebootcommit"
                or "installfeatures" or "uninstallfeatures" or "reset" or "rollback"
                or "createclone" or "commitclone";
    }

    /// <summary>
    /// 把某 build 内嵌的官方 usage 文本解析为命令清单，供界面动态渲染。
    /// </summary>
    public static class UpdateAppUsageService
    {
        private static readonly HashSet<string> UpdateCategory = new(StringComparer.OrdinalIgnoreCase)
        {
            "install", "installfromsd", "installinprocnoreboot", "installfromsdinprocnoreboot",
            "mountandinstall", "stage", "stageinproc", "stagetosd", "stagetosdinproc",
            "commit", "postrebootcommit", "finalizecommit", "rollback", "staged",
        };

        private static readonly HashSet<string> QueryCategory = new(StringComparer.OrdinalIgnoreCase)
        {
            "getinstalledpackages", "getupdateresults", "getfullupdateresults", "getfeatures",
            "getinstalledfeatures", "getdeferredupdates", "getservicingstatus", "getupdateflags",
            "getdeviceinventory", "updatereserve", "logstorage", "getlatestlogs", "getsprayedlogs",
        };

        private static readonly HashSet<string> ToolCategory = new(StringComparer.OrdinalIgnoreCase)
        {
            "renamepackages", "dumpfilesystem", "captureuvmstate", "generateuvmreport", "loadlibrary",
            "compress", "decompress", "decompressmanifests", "getlatestpayload", "creatediffhives",
            "mergediffhives", "applycontainerchanges", "copy", "converttocbs", "iutocbs",
            "iutocbsfromupdateoutput", "shrinkcabsinfolder", "attachspaces",
        };

        public static IReadOnlyList<int> CatalogBuilds
        {
            get
            {
                var list = new List<int>(UpdateAppUsageCatalog.UsageByBuild.Keys);
                list.Sort();
                return list;
            }
        }

        /// <summary>解析指定 build 的命令清单；无目录数据时返回空。</summary>
        public static List<UpdateAppCommandInfo> Parse(int build)
        {
            var result = new List<UpdateAppCommandInfo>();
            if (!UpdateAppUsageCatalog.UsageByBuild.TryGetValue(build, out var usage)) return result;

            UpdateAppCommandInfo? current = null;
            foreach (var rawLine in usage.Replace("\r\n", "\n").Split('\n'))
            {
                var line = rawLine.TrimEnd();
                if (string.IsNullOrWhiteSpace(line)) continue;

                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("UpdateApp.exe ", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("UpdateApp.exe\t", StringComparison.OrdinalIgnoreCase))
                {
                    var body = trimmed.Substring("UpdateApp.exe".Length).Trim();
                    var sp = body.IndexOf(' ');
                    var name = sp < 0 ? body : body.Substring(0, sp);
                    var args = sp < 0 ? string.Empty : body.Substring(sp + 1).Trim();
                    current = new UpdateAppCommandInfo
                    {
                        Name = name,
                        ArgsTemplate = args,
                        Category = Classify(name),
                    };
                    result.Add(current);
                    continue;
                }

                if (trimmed.StartsWith("Example:", StringComparison.OrdinalIgnoreCase))
                {
                    current = null;
                    continue;
                }

                if (current != null && current.Description.Length < 500)
                {
                    current.Description = (current.Description + " " + trimmed).Trim();
                }
            }
            return result;
        }

        private static string Classify(string name)
        {
            if (UpdateCategory.Contains(name)) return "更新调度";
            if (QueryCategory.Contains(name)) return "查询";
            if (ToolCategory.Contains(name)) return "工具";
            return "其他";
        }
    }
}
