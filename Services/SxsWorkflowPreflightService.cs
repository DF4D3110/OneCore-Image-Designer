using System.IO;
using OneCore_Image_Designer.Models;

namespace OneCore_Image_Designer.Services
{
    public sealed record SxsWorkflowPreview(string Summary, IReadOnlyList<string> Inputs,
        IReadOnlyList<string> PlannedOutputs, IReadOnlyList<string> Checks, bool Ready);

    public sealed class SxsWorkflowPreflightService
    {
        public SxsWorkflowPreview Preview(string? imagePath, string? outputDirectory, string sxsRoot)
        {
            var inputs = new List<string>();
            var outputs = new List<string>();
            var checks = new List<string>();
            var ready = true;

            if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
                inputs.Add(Path.GetFullPath(imagePath));
            else
            {
                checks.Add("Select an existing source image file.");
                ready = false;
            }

            if (!string.IsNullOrWhiteSpace(outputDirectory) && Directory.Exists(outputDirectory))
                outputs.Add(Path.GetFullPath(outputDirectory));
            else
            {
                checks.Add("Select an existing output directory.");
                ready = false;
            }

            var tools = new IntegrationToolService().DiscoverSxs(sxsRoot);
            var missing = tools.Where(tool => !tool.IsAvailable).Select(tool => tool.Name).ToArray();
            if (missing.Length > 0)
            {
                checks.Add("Missing source/tool files: " + string.Join(", ", missing));
                ready = false;
            }

            // 原先在这里要求存在外部 cbsx.exe / sxsfounder.exe 与 prepare.cmd。
            // 这些外部工具已全部删除 —— 铺底、CBS 装包、DISM 收尾、纯 SxS 导出
            // 现在都由本程序自己的 C# 实现完成（SxSFounderService / CbsxService /
            // DismServicingService / ExplodeSxSNativeService / PureSxsService）。
            checks.Add("外部工具：不需要（全部由本程序自带实现完成）");

            var prepareScript = Path.Combine(sxsRoot, "explodeSxS", "prepare.cmd");
            if (File.Exists(prepareScript))
            {
                var script = File.ReadAllText(prepareScript);
                if (script.Contains("format X:", StringComparison.OrdinalIgnoreCase))
                {
                    checks.Add("prepare.cmd contains a volume-format operation targeting X:. It will not be launched by this application.");
                    ready = false;
                }
            }
            checks.Add("No upstream SxS script or image operation is executed by this preview.");
            checks.Add("CbsExploder/SxSFounder licensing and redistribution provenance remain unverified.");
            return new SxsWorkflowPreview(
                ready ? "Preflight passed. Review the inputs and outputs before proceeding." :
                    "Preview only; image operations are unavailable until all required tools and licenses are verified.",
                inputs, outputs, checks, ready);
        }
    }
}
