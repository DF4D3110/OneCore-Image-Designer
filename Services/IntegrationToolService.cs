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
