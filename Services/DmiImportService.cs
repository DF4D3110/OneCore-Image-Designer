using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace OneCore_Image_Designer.Services;

/// <summary>
/// 一次完整的驱动包导入：文件落盘 + INF 发布 + DriverDatabase 写库。
///
/// <para>三段各自的语义已经单独对过真机（见 <see cref="DriverStoreService"/>、
/// <see cref="DriverDatabaseService"/>、<see cref="OcidServicingNative.TryDmiAcquireOemInfName"/>）；
/// 这里只负责把它们按 Windows 的顺序串起来：先分 oemN.inf 名，再落文件、发布 INF，
/// 最后写库（根键的 0004 位图带新位）。</para>
///
/// <para><b>收件箱驱动</b>（<c>Inbox</c>）保持原名发布、不占位图；第三方包才分配
/// <c>oemN.inf</c>。</para>
/// </summary>
public static class DmiImportService
{
    /// <summary>8061 位图大小（<c>kOemDriverInfFileMapSize</c>）。</summary>
    public const int OemBitmapSize = 4099;

    public sealed record Result(
        string StoreId,
        IReadOnlyList<string> Landed,
        string PublishedInfName,
        bool Inbox,
        int Rows,
        int Keys,
        int Values,
        bool BitmapChanged);

    /// <summary>
    /// 导入一个驱动包。<paramref name="ledgerJsonlPath"/> 是 <c>ocid_manifest dmi-ledger</c>
    /// 写给这个包的记录（一行）。
    /// </summary>
    /// <param name="imageRoot">映像根（<c>FileRepository</c> / <c>Windows\INF</c> 的父）。</param>
    /// <param name="hiveRoot">离线挂载的 hive 根（<c>DriverDatabase</c> 的父键）。</param>
    /// <param name="storeId">商店目录名。</param>
    /// <param name="infName">包里 INF 的文件名（原名）。</param>
    /// <param name="packageDirectory">包展开目录（载荷来源）。</param>
    /// <param name="fileNames">要铺进商店目录的文件名（INF 在首位）。</param>
    /// <param name="inbox">收件箱驱动（true 时保持原名发布、不分配 oemN.inf）。</param>
    /// <param name="architecture">根键 0003 的架构值（0=x86/5=arm/6=ia64/9=amd64）。</param>
    public static Result Import(string imageRoot, RegistryKey hiveRoot, string storeId, string infName,
        string packageDirectory, IReadOnlyList<string> fileNames, bool inbox, uint architecture,
        string ledgerJsonlPath, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(imageRoot)) throw new ArgumentException("imageRoot 为空", nameof(imageRoot));
        if (hiveRoot is null) throw new ArgumentNullException(nameof(hiveRoot));
        if (string.IsNullOrWhiteSpace(storeId)) throw new ArgumentException("storeId 为空", nameof(storeId));
        if (string.IsNullOrWhiteSpace(infName)) throw new ArgumentException("infName 为空", nameof(infName));
        if (fileNames is null || fileNames.Count == 0) throw new ArgumentException("文件集为空", nameof(fileNames));

        using var store = DriverDatabaseService.EnsureKey(hiveRoot, "DriverDatabase");

        // 1) 先定发布名：第三方包要先把位图里的那一位占住，再落文件；否则两个包并发
        //    导入会拿到同一个 oemN.inf。
        byte[] bitmap = ReadBitmap(store);
        string published;
        bool bitmapChanged = false;
        if (inbox)
        {
            published = infName;
            log?.Invoke($"[dmi-import] 收件箱驱动，保持原名发布 {infName}");
        }
        else
        {
            if (!OcidServicingNative.TryDmiAcquireOemInfName(bitmap, out var updated, out var name,
                    out _, out _, out var error))
            {
                throw new InvalidOperationException($"分配 oemN.inf 失败：{error}");
            }
            bitmap = updated;
            published = name;
            bitmapChanged = true;
            log?.Invoke($"[dmi-import] 分配发布名 {published}");
        }

        // 2) 文件：商店目录铺整套文件集，INF 以独立副本发布到 Windows\INF。
        var land = DriverStoreService.LandPackage(imageRoot, packageDirectory, storeId, infName, fileNames,
            published, log);

        // 3) 库：记录写进 hive，根键的 0003/0004 用本次的架构与位图。
        int rows = 0, keys = 0, values = 0;
        if (!string.IsNullOrWhiteSpace(ledgerJsonlPath))
        {
            (rows, keys, values) = DriverDatabaseService.ApplyLedgerJsonl(hiveRoot, ledgerJsonlPath,
                "DriverDatabase", log);
        }

        WriteRoot(store, architecture, bitmap);

        // 4) 回读：发布名、位图、商店目录三样都在盘上/库里才算成。
        Verify(imageRoot, store, storeId, published, infName);

        return new Result(storeId, land.Files.Select(f => f.Name).ToList(), published, inbox, rows, keys,
            values, bitmapChanged);
    }

    /// <summary>读根键的 0004 位图；没有就返回全 0（等于"一个 oemN.inf 都没用"）。</summary>
    public static byte[] ReadBitmap(RegistryKey store)
    {
        var raw = store.GetValue("0004") as byte[];
        var bitmap = new byte[OemBitmapSize];
        if (raw is null || raw.Length == 0) return bitmap;
        if (raw.Length == OemBitmapSize)
        {
            Array.Copy(raw, bitmap, OemBitmapSize);
            return bitmap;
        }

        // 真机上见过"只存前缀"的位图（8061 images 里就是 1 个字节 0x80）：位图尾部没有
        // 记录到的位就是还没用过的名字，补 0 是唯一安全的解释（补 1 会永久浪费名字）。
        if (raw.Length > OemBitmapSize)
            throw new InvalidOperationException($"DriverDatabase 根键的 0004 位图长度是 {raw.Length}，超出 {OemBitmapSize}");
        Array.Copy(raw, bitmap, raw.Length);
        return bitmap;
    }

    /// <summary>写根键的 0003（架构）与 0004（位图）。</summary>
    public static void WriteRoot(RegistryKey store, uint architecture, byte[] bitmap)
    {
        if (bitmap is null || bitmap.Length != OemBitmapSize)
            throw new ArgumentException($"位图长度必须是 {OemBitmapSize}", nameof(bitmap));
        store.SetValue("0003", unchecked((int)architecture), RegistryValueKind.DWord);
        store.SetValue("0004", bitmap, RegistryValueKind.Binary);
    }

    private static void Verify(string imageRoot, RegistryKey store, string storeId, string published,
        string infName)
    {
        var infDirectory = Path.Combine(imageRoot, "Windows", "INF");
        var publishedPath = Path.Combine(infDirectory, published);
        if (!File.Exists(publishedPath))
            throw new InvalidOperationException($"发布后找不到 {publishedPath}");

        var storeDirectory = Path.Combine(imageRoot, "Windows", "System32", "DriverStore",
            "FileRepository", storeId);
        if (!Directory.Exists(storeDirectory))
            throw new InvalidOperationException($"发布后找不到商店目录 {storeDirectory}");

        var stored = Path.Combine(storeDirectory, infName);
        if (!File.Exists(stored))
            throw new InvalidOperationException($"商店目录里没有 INF：{stored}");

        var bitmap = store.GetValue("0004") as byte[];
        if (bitmap is null || bitmap.Length != OemBitmapSize)
            throw new InvalidOperationException("回读：根键 0004 位图缺失或长度不对");
    }
}
