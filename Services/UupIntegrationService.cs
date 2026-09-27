using System;
using System.IO;
using System.Text.Json;
using OneCore_Image_Designer.Models;

namespace OneCore_Image_Designer.Services
{
    public sealed class UupIntegrationService
    {
        public UupScanSnapshot LoadSnapshot(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("UUP 快照不存在。", path);
            return JsonSerializer.Deserialize<UupScanSnapshot>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("UUP 快照为空或格式无效。");
        }
    }
}
