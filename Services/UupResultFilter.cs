using System;
using System.Collections.Generic;
using System.Linq;
using OneCore_Image_Designer.Models;

namespace OneCore_Image_Designer.Services
{
    public static class UupResultFilter
    {
        public static List<UupScanEntry> Apply(
            IEnumerable<UupScanEntry> entries,
            string? targetBuild,
            string? titleKeyword)
        {
            var buildQuery = targetBuild?.Trim() ?? string.Empty;
            var titleQuery = titleKeyword?.Trim() ?? string.Empty;

            return entries.Where(entry =>
                    (buildQuery.Length == 0 ||
                     ((entry.Build ?? string.Empty).Contains(buildQuery, StringComparison.OrdinalIgnoreCase) ||
                      (entry.ReleaseVersion ?? string.Empty).Contains(buildQuery, StringComparison.OrdinalIgnoreCase))) &&
                    (titleQuery.Length == 0 ||
                     (entry.Title ?? string.Empty).Contains(titleQuery, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
    }
}
