using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using OneCore_Image_Designer.Models;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// OEMInput schema 家族选择器。
    ///
    /// 依据（2026-09 实证，41 版 featureapi.dll 内嵌 XSD 哈希归组）：
    /// OEMInput XSD 并非一成不变，V1 内部有 6 个家族、V2 有 4 个家族，
    /// 字段必填性/枚举/子树随 build 演进（如 18272+ 收紧 AppXOptionalPackages 非空、
    /// 18963 分裂 V1/V2、18985 完成 SOC→DeviceLayoutType 改名、22621 V2 再扩）。
    /// 用 20348 版 schema 校验早期 build 的 OEMInput 会误判，故按目标 build 选家族。
    ///
    /// 家族边界表（build → 内嵌资源后缀）：
    ///   V1: ≤14959 → .10527-14959 | 14986–17040 → .14986-17040 | 17047–17125 → .17047-17125
    ///       17666–18362 → .17666-18362 | 18890–18941 → .18890-18941 | 18963 → .18963
    ///       ≥18985 → （无后缀，20348/22000 冻结版）
    ///   V2: 18963 → .18963 | 18985–19041 → .18985-19041 | 20348–22000 → （无后缀）
    ///       ≥22621 → .22621
    /// build=0（未指定）时回退无后缀基线版，行为与旧版一致。
    /// </summary>
    public static class OemInputSchemaFamilyService
    {
        /// <summary>返回该 build+代际应使用的内嵌资源后缀（"" = 基线版）。</summary>
        public static string FamilySuffix(int build, OemInputSchemaVersion version)
        {
            if (build <= 0) return string.Empty;

            if (version == OemInputSchemaVersion.V1_2004_10)
            {
                if (build <= 14959) return ".10527-14959";
                if (build <= 17040) return ".14986-17040";
                if (build <= 17125) return ".17047-17125";
                if (build <= 18362) return ".17666-18362";
                if (build <= 18941) return ".18890-18941";
                if (build == 18963) return ".18963";
                return string.Empty;   // ≥18985：V1 冻结版（18494 字节，20348/22000 同）
            }

            // V2（18963 才存在）
            if (build < 18963) return string.Empty;   // 早期无 V2：交给基线（校验会因 ns 不符报错，语义正确）
            if (build == 18963) return ".18963";
            if (build <= 19041) return ".18985-19041";
            if (build < 22621) return string.Empty;   // 20348/22000 基线
            return ".22621";
        }

        /// <summary>人类可读的家族标签（UI 展示用）。</summary>
        public static string FamilyLabel(int build, OemInputSchemaVersion version)
        {
            string suffix = FamilySuffix(build, build > 0 ? version : OemInputSchemaVersion.V1_2004_10);
            string baseName = version == OemInputSchemaVersion.V2_2019_06 ? "OEMInput.V2" : "OEMInput.V1";
            string ver = version == OemInputSchemaVersion.V2_2019_06
                ? (build <= 0 ? "20348（基线）"
                    : build == 18963 ? "18963 transitional"
                    : build <= 19041 && build >= 18985 ? "18985–19041"
                    : build >= 22621 ? "22621+"
                    : "20348/22000")
                : (build <= 0 ? "20348（基线）"
                    : build <= 14959 ? "10527–14959"
                    : build <= 17040 ? "14986–17040"
                    : build <= 17125 ? "17047–17125"
                    : build <= 18362 ? "17666–18362"
                    : build <= 18941 ? "18890–18941"
                    : build == 18963 ? "18963"
                    : "≥18985 冻结版");
            return $"{baseName}{suffix}.xsd（{ver}）";
        }

        /// <summary>
        /// 按 build+代际打开对应内嵌 XSD 流；家族文件缺失时回退基线版。
        /// 找不到任何资源返回 null。
        /// </summary>
        public static Stream? OpenSchemaStream(int build, OemInputSchemaVersion version)
        {
            Assembly asm = typeof(OemInputSchemaFamilyService).Assembly;
            string prefix = version == OemInputSchemaVersion.V2_2019_06 ? "OEMInput.V2" : "OEMInput.V1";
            string suffix = FamilySuffix(build, version);

            var names = asm.GetManifestResourceNames();
            string wanted = $"{prefix}{suffix}.xsd";
            string? hit = names.FirstOrDefault(n =>
                n.EndsWith(wanted, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return asm.GetManifestResourceStream(hit);

            // 家族缺失 → 基线
            string baseline = $"{prefix}.xsd";
            hit = names.FirstOrDefault(n => n.EndsWith(baseline, StringComparison.OrdinalIgnoreCase));
            return hit is null ? null : asm.GetManifestResourceStream(hit);
        }
    }
}
