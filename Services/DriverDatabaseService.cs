using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;

namespace OneCore_Image_Designer.Services;

/// <summary>
/// 把 <c>ocid_manifest dmi-ledger</c> 算出来的 DriverDatabase 记录写进一个**已挂载的**
/// 脱机 hive（<c>HKLM\&lt;mount&gt;</c> 下的 <c>DriverDatabase</c> 子树）。
///
/// 【为什么写的是一个"计划文件"而不是直接读 INF】
/// 值的类型和字节必须和真机一模一样（000d 是 16 字节原始 GUID、000e 是 FILETIME 二进制、
/// 000f 是 [build&lt;&lt;16|rev][major&lt;&lt;16|minor] 的拼装 DWORD），这些判断已经在原生
/// DriverInf 里做完，并用真机 2054 个值验收过。C# 这边只负责"照单写"，
/// 不再重复解析 INF —— 两份实现必然漂移，而值类型写错光看注册表看不出来。
///
/// 【不写什么】
/// 0007 签名者 / 0008 签名分数来自**目录签名验证**（crypt32 / CatRoot），不是 INF 里的文本，
/// 由调用方（未来的签名校验步骤）另行提供；这里照单写，计划文件里没有就不写。
/// </summary>
public static class DriverDatabaseService
{
    /// <summary>计划文件里的一条值。</summary>
    public sealed class LedgerValue
    {
        public string Name = "";
        /// <summary>sz | dword | binary | multi</summary>
        public string Type = "";
        public string? Text;
        public uint Dword;
        public byte[]? Bytes;
        public string[] Strings = Array.Empty<string>();
    }

    /// <summary>计划文件里的一条"键 → 值"记录。路径相对 DriverDatabase，用反斜杠分隔。</summary>
    public sealed record LedgerEntry(string Path, LedgerValue Value);

    /// <summary>读一行 JSONL，展开成"键路径 + 值"的扁平列表。类型不认识就抛——静默跳过会写出半个库。</summary>
    public static List<LedgerEntry> ParseRow(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (!root.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("行里没有 keys 数组");

        var result = new List<LedgerEntry>();
        foreach (var keyEl in keys.EnumerateArray())
        {
            string path = keyEl.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
            if (path.Length == 0) continue;
            if (!keyEl.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var v in values.EnumerateArray())
                result.Add(new LedgerEntry(path, ParseValue(v)));
        }
        return result;
    }

    private static LedgerValue ParseValue(JsonElement v)
    {
        var lv = new LedgerValue
        {
            Name = v.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            Type = v.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
        };
        switch (lv.Type)
        {
            case "sz":
                lv.Text = v.TryGetProperty("text", out var txt) ? txt.GetString() ?? "" : "";
                break;
            case "dword":
                lv.Dword = v.TryGetProperty("dword", out var dw) && dw.TryGetUInt32(out var d) ? d : 0u;
                break;
            case "binary":
                lv.Bytes = v.TryGetProperty("hex", out var hex) && hex.GetString() is { Length: > 0 } h
                    ? Convert.FromHexString(h)
                    : Array.Empty<byte>();
                break;
            case "multi":
                if (v.TryGetProperty("strings", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (var s in arr.EnumerateArray()) list.Add(s.GetString() ?? "");
                    lv.Strings = list.ToArray();
                }
                break;
            default:
                throw new InvalidOperationException($"未知的值类型 '{lv.Type}'（值名 {lv.Name}）");
        }
        return lv;
    }

    /// <summary>
    /// 把计划文件写进 <paramref name="hiveRoot"/><paramref name="storeSubKey"/>（默认
    /// <c>DriverDatabase</c>）。返回 (行数, 键数, 值数)。
    /// </summary>
    public static (int Rows, int Keys, int Values) ApplyLedgerJsonl(RegistryKey hiveRoot, string jsonlPath,
        string storeSubKey = "DriverDatabase", Action<string>? log = null)
    {
        int rows = 0, keyCount = 0, valueCount = 0;
        using var store = EnsureKey(hiveRoot, storeSubKey);

        foreach (var line in File.ReadLines(jsonlPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var entries = ParseRow(line);
            rows++;

            // 同一行里同一个键的值归并一次，避免反复开关同一个键。
            foreach (var group in entries.GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
            {
                using var key = EnsureKey(store, group.Key);
                foreach (var entry in group) { WriteValue(key, entry.Value); valueCount++; }
                keyCount++;
            }
            if (rows % 50 == 0) log?.Invoke($"[dmi-db] {rows} 行 / {valueCount} 个值");
        }
        log?.Invoke($"[dmi-db] 完成：{rows} 行、{keyCount} 个键、{valueCount} 个值");
        return (rows, keyCount, valueCount);
    }

    /// <summary>按反斜杠逐级建键（不存在就建）。</summary>
    public static RegistryKey EnsureKey(RegistryKey root, string relativePath)
    {
        var key = root;
        bool owned = false;
        foreach (var part in relativePath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = key.CreateSubKey(part);
            if (owned) key.Dispose();
            key = next;
            owned = true;
        }
        return key;
    }

    /// <summary>照类型写值。类型不匹配就抛——写错类型比写错值更难查。</summary>
    public static void WriteValue(RegistryKey key, LedgerValue v)
    {
        switch (v.Type)
        {
            case "sz":
                key.SetValue(v.Name, v.Text ?? "", RegistryValueKind.String);
                break;
            case "dword":
                key.SetValue(v.Name, unchecked((int)v.Dword), RegistryValueKind.DWord);
                break;
            case "binary":
                key.SetValue(v.Name, v.Bytes ?? Array.Empty<byte>(), RegistryValueKind.Binary);
                break;
            case "multi":
                key.SetValue(v.Name, v.Strings.Length == 0 ? new[] { "" } : v.Strings, RegistryValueKind.MultiString);
                break;
            default:
                throw new InvalidOperationException($"未知的值类型 '{v.Type}'（值名 {v.Name}）");
        }
    }

    /// <summary>
    /// 逐值回读比对：<paramref name="expectedRoot"/>（真机 hive）与 <paramref name="actualRoot"/>
    /// （我们写的 hive）都按计划文件覆盖到的键/值比 kind + 数据。
    /// </summary>
    /// <param name="skipMissingInExpected">
    /// 真机 hive 里没有这条记录时**跳过**而不是算不一致。真机镜像里 575 个商店目录只有 158 个
    /// 还有台账记录（其余文件被清理过），拿"有记录的"当金标准才是对的。
    /// </param>
    /// <param name="skipValueNames">
    /// 不参与比对的值名。`0007`（签名者）来自**目录签名验证**，不是 INF 能算出来的
    /// （真机里有 5 个包是空串），归未来的 crypt32 工作。
    /// </param>
    public static (int Compared, int Mismatch, int Skipped) CompareLedger(
        RegistryKey expectedRoot, RegistryKey actualRoot,
        string jsonlPath, string storeSubKey = "DriverDatabase", Action<string>? report = null,
        bool skipMissingInExpected = false, ISet<string>? skipValueNames = null)
    {
        int compared = 0, mismatch = 0, skipped = 0;
        using var expectedStore = expectedRoot.OpenSubKey(storeSubKey)
            ?? throw new InvalidOperationException($"真机 hive 里没有 {storeSubKey}");
        using var actualStore = actualRoot.OpenSubKey(storeSubKey)
            ?? throw new InvalidOperationException($"写入的 hive 里没有 {storeSubKey}");

        foreach (var line in File.ReadLines(jsonlPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            foreach (var group in ParseRow(line).GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
            {
                using var exp = OpenKey(expectedStore, group.Key);
                using var act = OpenKey(actualStore, group.Key);
                if (exp == null || act == null)
                {
                    if (skipMissingInExpected && exp == null && act != null) { skipped += group.Count(); continue; }
                    compared += group.Count();
                    mismatch += group.Count();
                    report?.Invoke($"缺键 {group.Key}");
                    continue;
                }
                foreach (var entry in group)
                {
                    var v = entry.Value;
                    if (skipValueNames != null && skipValueNames.Contains(v.Name)) { skipped++; continue; }
                    compared++;
                    var wantKind = exp.GetValueKind(v.Name);
                    var gotKind = act.GetValueKind(v.Name);
                    var want = exp.GetValue(v.Name);
                    var got = act.GetValue(v.Name);
                    if (wantKind != gotKind || !ValueEquals(want, got))
                    {
                        mismatch++;
                        report?.Invoke($"不一致 {group.Key}\\{v.Name}：kind {wantKind}→{gotKind}，{Show(want)}→{Show(got)}");
                    }
                }
            }
        }
        return (compared, mismatch, skipped);
    }

    private static string Show(object? value) => value switch
    {
        null => "<null>",
        byte[] b => Convert.ToHexString(b),
        string[] s => string.Join("|", s),
        _ => value.ToString() ?? "<null>",
    };

    private static RegistryKey? OpenKey(RegistryKey root, string path)
    {
        var key = root;
        bool owned = false;
        foreach (var part in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = key.OpenSubKey(part);
            if (owned) key.Dispose();
            if (next == null) return null;
            key = next;
            owned = true;
        }
        return owned ? key : null;
    }

    private static bool ValueEquals(object? a, object? b)
    {
        if (a == null && b == null) return true;
        if (a is byte[] ba && b is byte[] bb) return ba.AsSpan().SequenceEqual(bb);
        if (a is string[] sa && b is string[] sb) return sa.SequenceEqual(sb, StringComparer.Ordinal);
        return Equals(a, b);
    }

    /// <summary>递归删掉一个子键（不存在就算了）。</summary>
    public static void DeleteSubKeyTreeIfExists(RegistryKey root, string name)
    {
        try { root.DeleteSubKeyTree(name, throwOnMissingSubKey: false); } catch (Exception) { }
    }
}
