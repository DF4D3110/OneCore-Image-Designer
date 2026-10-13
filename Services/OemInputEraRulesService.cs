using System;
using System.Collections.Generic;
using OneCore_Image_Designer.Models;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// OEMInput 时代语义规则（featureapi 反编译实证，2026-09 全 41 版扫描归组）。
    ///
    /// 字段首现时代（public 字段声明矩阵，12 个代表版本逐一核对）：
    ///   &lt;17047  无 AppXOptionalPackages（14349–16299 时代类里没有该字段）
    ///    17047+ AppXOptionalPackages 出现
    ///    17666+ MachineInfoFile 出现
    ///    18963+ TestContent / DevicePlatformIDs 出现；V1/V2 schema 分裂
    ///   V2 全系（18963+）：AdditionalSKUs / UserStoreMapData / FormatDPP /
    ///            PackageFiles / Resolutions 为 [Obsolete(..., true)]，写入即官方异常。
    ///
    /// 本服务只做「目标 build 早于字段出现时代仍填写了该字段」的门控提示，
    /// 结构合法性由 OemInputSchemaFamilyService 选出的对应时代官方 XSD 兜底。
    /// </summary>
    public static class OemInputEraRulesService
    {
        public const int FirstAppXOptionalPackages = 17047;
        public const int FirstMachineInfoFile = 17666;
        public const int FirstTestContent = 18963;
        public const int FirstDevicePlatformIDs = 18963;
        public const int FirstV2Schema = 18963;

        /// <summary>按目标 build 生成时代违规警告（不阻断，仅提示）。</summary>
        public static List<string> CheckEra(OEMInputDoc doc)
        {
            var warns = new List<string>();
            int b = doc.TargetBuild;
            if (b <= 0) return warns;   // 未指定 build：不做时代判断

            if (b < FirstAppXOptionalPackages && doc.AppXOptionalPackages.Count > 0)
                warns.Add($"AppXOptionalPackages 是 {FirstAppXOptionalPackages}+ 才有的字段（目标 build {b} 的 imageapp 不认识，XSD 也会拒）");

            if (b < FirstMachineInfoFile && !string.IsNullOrWhiteSpace(doc.MachineInfoFile))
                warns.Add($"MachineInfoFile 是 {FirstMachineInfoFile}+ 才有的字段（目标 build {b} 不支持）");

            if (b < FirstTestContent && !string.IsNullOrWhiteSpace(doc.TestContent))
                warns.Add($"TestContent 字段 {FirstTestContent}+ 才进入 OEMInput 类（目标 build {b} 可能忽略或拒绝）");

            if (b < FirstDevicePlatformIDs && doc.DevicePlatformIDs.Count > 0)
                warns.Add($"DevicePlatformIDs 是 {FirstDevicePlatformIDs}+ 才有的字段（目标 build {b} 不支持）");

            if (doc.IsSchemaV2 && b < FirstV2Schema)
                warns.Add($"V2 schema（2019/06）{FirstV2Schema} 才存在，目标 build {b} 只认 V1");

            if (doc.IsSchemaV2 && doc.AdditionalSKUs.Count > 0)
                warns.Add("V2 中 AdditionalSKUs 为官方 Obsolete（序列化即抛异常），请清空");

            return warns;
        }
    }
}
