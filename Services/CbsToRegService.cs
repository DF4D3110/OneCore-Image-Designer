using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml.Serialization;
using CbsToReg;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// mum → 注册表 转换服务（移植自独立版 MobilePackageGen 的 Applications\CbsToReg，
    /// 类文件已置于 MobilePackageGen\MobilePackageGen.Common\CbsToReg\，此处只做 OCID 侧封装）。
    ///
    /// 作用：把 CBS 包 .mum 里的 &lt;registryKeys&gt;&lt;registryKey keyName=…&gt;&lt;registryValue …&gt;
    /// 转成 .reg 文本，供离线 hive 导入使用。
    ///
    /// 离线导入的关键：mum 里写的是 HKEY_LOCAL_MACHINE\SOFTWARE / SYSTEM，
    /// 而离线 hive 被临时挂载为 HKLM\RTSOFTWARE / RTSYSTEM，
    /// 因此 <see cref="CbsToReg.CbsToReg.Build"/> 会把这两个前缀重命名（见 KeyNameReplace）。
    ///
    /// 与独立版 Program.cs 的差异（改进）：
    ///   · DCM 解压统一走 <see cref="ManifestDecompressor"/>（wcpex 官方通道，已实证 3000/3000），
    ///     而不是 LibSxS.Delta.DeltaAPI.LoadManifest —— 与 OCID 其余部分一致；
    ///   · null 字段归一化为空串，避免 Build() 里 registryValue.Name == "" 判断对 null 抛异常。
    /// </summary>
    public static class CbsToRegService
    {
        /// <summary>离线 hive 的临时挂载名（导入 .reg 时用）。</summary>
        public const string DefaultSoftwareHive = "RTSOFTWARE";
        public const string DefaultSystemHive = "RTSYSTEM";

        public sealed class Result
        {
            public bool Success;
            public string Error = "";
            public string RegText = "";
            public int Keys;
            public int Values;
            public string? PackageName;
        }

        /// <summary>读 mum → 生成 .reg 文本。</summary>
        public static Result Convert(string mumPath,
            string softwareHive = DefaultSoftwareHive,
            string systemHive = DefaultSystemHive)
        {
            var res = new Result();
            try
            {
                if (!File.Exists(mumPath)) { res.Error = "mum 不存在: " + mumPath; return res; }

                // 1) 取明文 XML（自动处理 DCM）
                string? xml = ManifestDecompressor.ReadXmlText(mumPath, out string? why);
                if (xml == null) { res.Error = "mum 无法解析: " + (why ?? "未知原因"); return res; }

                // 2) 反序列化到 CbsToReg 的 mum 模型
                CbsToReg.Mum.Assembly? asm;
                try
                {
                    var serializer = new XmlSerializer(typeof(CbsToReg.Mum.Assembly));
                    using var reader = new StringReader(xml);
                    asm = (CbsToReg.Mum.Assembly?)serializer.Deserialize(reader);
                }
                catch (Exception ex) { res.Error = "mum 反序列化失败: " + ex.Message; return res; }

                if (asm == null) { res.Error = "mum 反序列化为空"; return res; }
                res.PackageName = asm.AssemblyIdentity?.Name;

                // 3) 收集 registryKeys
                var exporter = new CbsToReg.CbsToReg();
                foreach (var regKey in asm.RegistryKeys ?? new List<CbsToReg.Mum.RegistryKey>())
                {
                    var values = new List<CbsToReg.RegistryValue>(regKey.RegistryValues?.Count ?? 0);
                    foreach (var kv in regKey.RegistryValues ?? new List<CbsToReg.Mum.RegistryValue>())
                    {
                        values.Add(new CbsToReg.RegistryValue(
                            kv.Name ?? "",
                            kv.Value ?? "",
                            kv.ValueType ?? "",
                            kv.Mutable ?? "",
                            kv.OperationHint ?? ""));
                    }
                    exporter.Add(new RegistryCollection(regKey.KeyName ?? "", values));
                    res.Values += values.Count;
                }
                res.Keys = (asm.RegistryKeys?.Count ?? 0);

                // 4) 生成 .reg 文本
                res.RegText = exporter.Build(softwareHive, systemHive);
                res.Success = true;
                return res;
            }
            catch (Exception ex) { res.Error = ex.GetType().Name + ": " + ex.Message; return res; }
        }

        /// <summary>读 mum → 写 .reg 文件（UTF-8）。</summary>
        public static Result ConvertAndSave(string mumPath, string outputRegPath,
            string softwareHive = DefaultSoftwareHive,
            string systemHive = DefaultSystemHive)
        {
            var res = Convert(mumPath, softwareHive, systemHive);
            if (!res.Success) return res;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputRegPath))!);
                File.WriteAllText(outputRegPath, res.RegText, new UTF8Encoding(true));
            }
            catch (Exception ex) { res.Success = false; res.Error = "写 .reg 失败: " + ex.Message; }
            return res;
        }
    }
}
