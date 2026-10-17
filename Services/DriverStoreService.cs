using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace OneCore_Image_Designer.Services;

/// <summary>
/// 驱动包**文件**落盘：把 INF 与载荷铺进离线镜像的 `FileRepository` 与 `Windows\INF`。
/// </summary>
/// <remarks>
/// <para>语义来自 8061 真机（`E:\cbs_8061x86c.vhd`）的实测，不是文档推断：</para>
/// <list type="bullet">
///   <item>商店目录是 `<image>\Windows\System32\DriverStore\FileRepository\&lt;StoreId&gt;`，
///         **没有中间的架构/语言层**；目录里 INF 与载荷平铺并列
///         （`1394.inf` + `1394ohci.sys`）。</item>
///   <item>收件箱驱动的 INF 以**原名**发布到 `Windows\INF\&lt;inf 名&gt;`
///         （真机 588 个 `.inf` 全是原名，只有 1 个 `oem0.inf`），且该副本与商店目录里的
///         那份**不是同一个硬链接**（Windows\INF 那份是独立副本，商店里那份带硬链接）。</item>
///   <item>第三方包才走 `oem%u.inf`（4099 字节位图取第一个 0 位，MSB-first）；
///         这条由原生 `AcquireOemInfName` 决定，本类只负责把结果落成文件。</item>
/// </list>
/// <para>
/// 为什么单独一个类：CSI（<see cref="CsiFileService"/>）落的是**组件**文件（正本在 WinSxS、
/// 落点硬链接过去）；驱动包的正本就在 `FileRepository` 自己身上，没有 WinSxS 那一层，
/// 权限归属也不同（WinSxS 是 TrustedInstaller，DriverStore 同样是受保护目录但走的是
/// 商店自己的 DACL）。两条落盘链路混在一起会把语义搅乱。
/// </para>
/// </remarks>
public static class DriverStoreService
{
    /// <summary>一条落盘结果。</summary>
    public sealed record LandedFile(string Name, string StorePath, long Length, string Sha256);

    /// <summary>一次落盘的汇总。</summary>
    public sealed record LandResult(
        string StoreDirectory,
        IReadOnlyList<LandedFile> Files,
        string PublishedInfPath,
        bool PublishedUnderOriginalName);

    /// <summary>商店目录的完整路径（`&lt;image&gt;\Windows\System32\DriverStore\FileRepository\&lt;StoreId&gt;`）。</summary>
    public static string StoreDirectoryOf(string imageRoot, string storeId)
        => Path.Combine(imageRoot, "Windows", "System32", "DriverStore", "FileRepository", storeId);

    /// <summary>
    /// 把一份驱动包落进离线镜像。
    /// </summary>
    /// <param name="imageRoot">离线镜像根（脱机挂载点或解包目录）。</param>
    /// <param name="sourceDirectory">载荷所在目录（OCID 包目录；测试里用真机商店目录）。</param>
    /// <param name="storeId">原生算出来的目录名（`inf名_架构_16hex`）。</param>
    /// <param name="infName">INF 文件名（如 `acpi.inf`）。</param>
    /// <param name="fileNames">要铺进商店目录的文件名（INF 必须在这里面）。</param>
    /// <param name="publishedInfName">
    /// 发布到 `Windows\INF` 下的文件名：收件箱=INF 原名；第三方=`oem%u.inf`
    /// （由原生 <c>AcquireOemInfName</c> 从位图分配）。
    /// </param>
    /// <param name="log">进度/告警。</param>
    public static LandResult LandPackage(
        string imageRoot,
        string sourceDirectory,
        string storeId,
        string infName,
        IEnumerable<string> fileNames,
        string publishedInfName,
        Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(imageRoot)) throw new ArgumentException("镜像根为空", nameof(imageRoot));
        if (string.IsNullOrWhiteSpace(storeId)) throw new ArgumentException("StoreId 为空", nameof(storeId));
        if (string.IsNullOrWhiteSpace(infName)) throw new ArgumentException("INF 名为空", nameof(infName));
        if (string.IsNullOrWhiteSpace(publishedInfName)) throw new ArgumentException("发布名为空", nameof(publishedInfName));

        var storeDirectory = StoreDirectoryOf(imageRoot, storeId);
        Directory.CreateDirectory(storeDirectory);

        var landed = new List<LandedFile>();
        bool sawInf = false;
        foreach (var raw in fileNames)
        {
            var name = Path.GetFileName(raw);
            if (string.IsNullOrWhiteSpace(name)) continue;

            var source = Path.Combine(sourceDirectory, name);
            if (!File.Exists(source))
            {
                // 缺载荷**不**致命：Windows 自己的 ImportDriverPackageWorker 对缺文件也只 warn。
                log?.Invoke($"警告：载荷缺失 {source}（按 Windows 语义继续）");
                continue;
            }

            var target = Path.Combine(storeDirectory, name);
            File.Copy(source, target, overwrite: true);
            var info = new FileInfo(target);
            landed.Add(new LandedFile(name, target, info.Length, Sha256Of(target)));
            if (string.Equals(name, infName, StringComparison.OrdinalIgnoreCase)) sawInf = true;
        }

        if (!sawInf)
            throw new InvalidOperationException($"载荷里没有 INF「{infName}」，不能算一次成功导入（{sourceDirectory}）");

        // 发布 INF 到 Windows\INF：**独立副本**（真机上它与商店里那份不是同一个硬链接）。
        var infDirectory = Path.Combine(imageRoot, "Windows", "INF");
        Directory.CreateDirectory(infDirectory);
        var published = Path.Combine(infDirectory, publishedInfName);
        File.Copy(Path.Combine(storeDirectory, infName), published, overwrite: true);
        log?.Invoke($"发布 {publishedInfName} → {published}");

        return new LandResult(storeDirectory, landed, published,
                              string.Equals(publishedInfName, infName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 回读校验：商店目录里的文件集与源目录**逐个字节相同**（大小 + SHA-256），
    /// 且发布的 INF 与商店里的那份一致。
    /// </summary>
    /// <returns>(核对文件数, 不一致清单)。</returns>
    public static (int Checked, IReadOnlyList<string> Problems) VerifyPackage(
        string sourceDirectory, string storeDirectory, string infName, string publishedInfPath)
    {
        var problems = new List<string>();
        int checkedFiles = 0;

        if (!Directory.Exists(storeDirectory))
            return (0, new[] { $"商店目录不存在：{storeDirectory}" });

        foreach (var source in Directory.GetFiles(sourceDirectory))
        {
            var name = Path.GetFileName(source);
            var target = Path.Combine(storeDirectory, name);
            if (!File.Exists(target)) { problems.Add($"缺文件：{name}"); continue; }
            checkedFiles++;
            if (new FileInfo(source).Length != new FileInfo(target).Length)
                problems.Add($"大小不符：{name}");
            else if (Sha256Of(source) != Sha256Of(target))
                problems.Add($"内容不符：{name}");
        }

        var storedInf = Path.Combine(storeDirectory, infName);
        if (!File.Exists(storedInf)) problems.Add($"商店目录里没有 INF：{infName}");
        else if (!File.Exists(publishedInfPath)) problems.Add($"没有发布 INF：{publishedInfPath}");
        else if (Sha256Of(storedInf) != Sha256Of(publishedInfPath))
            problems.Add("发布的 INF 与商店里那份内容不一致");

        return (checkedFiles, problems);
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
