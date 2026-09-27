using System;
using System.Collections.Generic;
using System.IO;
using OneCore_Image_Designer.Models;

namespace OneCore_Image_Designer.Services
{
    public sealed class IntegrationToolService
    {
        public IReadOnlyList<ToolProbeResult> DiscoverSxs(string? sxsRoot)
        {
            var results = new List<ToolProbeResult>();
            Add(results, "pureSxS script", sxsRoot, "pureSxS\\puresxs.py");
            Add(results, "pureSxS inventory script", sxsRoot, "pureSxS\\havesxs.py");
            Add(results, "explodeSxS staging script", sxsRoot, "explodeSxS\\prepare.cmd");
            Add(results, "explodeSxS CBS workflow", sxsRoot, "explodeSxS\\cbss.cmd");
            Add(results, "explodeSxS capture script", sxsRoot, "explodeSxS\\captureimg.cmd");
            Add(results, "explodeSxS finish script", sxsRoot, "explodeSxS\\finishimg.cmd");
            return results;
        }

        private static void Add(List<ToolProbeResult> results, string name, string? root, string relativePath)
        {
            string? path = null;
            string? working = null;
            if (!string.IsNullOrWhiteSpace(root))
            {
                working = Path.GetFullPath(root);
                var candidate = Path.Combine(working, relativePath);
                if (File.Exists(candidate)) path = candidate;
            }
            results.Add(new ToolProbeResult { Name = name, ExecutablePath = path, WorkingDirectory = working });
        }

    }
}
