using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.Win32;
using OneCore_Image_Designer.Models;
using OneCore_Image_Designer.Services;

var tests = new (string Name, Func<Task> Run)[]
{
    ("UUP query parsing", () => { QueryParsing(); return Task.CompletedTask; }),
    ("UUP snapshot round-trip", () => { SnapshotRoundTrip(); return Task.CompletedTask; }),
    ("UUP payload lookup and safe path", () => { PayloadLookup(); return Task.CompletedTask; }),
    ("UUP bridge cancellation and errors", BridgeCancellationAndErrors),
    ("UUP media conversion preflight blocks unsafe targets", () => { MediaConversionPreflight(); return Task.CompletedTask; }),
    ("artifact registry and preflight", () => { ArtifactPreflight(); return Task.CompletedTask; }),
    ("SxS preview is non-executing and blocks unsafe scripts", () => { SxsPreview(); return Task.CompletedTask; }),
    ("OS generation XML regressions", () => { GenerationRegression(); return Task.CompletedTask; }),
    ("Validation rule families wire-up and hard-intercept templates", () => { RuleFamiliesRegression(); return Task.CompletedTask; }),
    ("Ocid servicing native library (ABI, value encoding, CMI decisions)", () => { OcidServicingNativeAbi(); return Task.CompletedTask; }),
    ("Ocid $(runtime.*) macro expansion through the native library", () => { OcidMacroExpansion(); return Task.CompletedTask; }),
    ("Ocid registryKeys wire format (key-only keys and key SDDL)", () => { OcidRegistryOpShape(); return Task.CompletedTask; }),
    ("Ocid key SDDL is applied and verified by read-back", () => { OcidSddlApplyAndReadback(); return Task.CompletedTask; }),
    ("Ocid key SDDL survives an offline hive load/unload cycle", () => { OcidSddlPersistsInOfflineHive(); return Task.CompletedTask; }),
    ("Ocid CSI copies payload to the store, hardlinks destinations and applies file ACL", () => { OcidCsiFilePlacement(); return Task.CompletedTask; }),
    ("Ocid protected directory write takes ownership then restores the lock", () => { OcidTrustedInstallerAccess(); return Task.CompletedTask; }),
    ("Ocid driver package store id, oemN.inf names and DriverDatabase property names", () => { OcidDriverPackage(); return Task.CompletedTask; })
    ,("Ocid DriverDatabase ledger writes a hived copy and reads back value-for-value", () => { OcidDriverDatabaseLedger(); return Task.CompletedTask; })
    ,("Ocid driver store lands package files and publishes the INF", () => { OcidDriverStoreLanding(); return Task.CompletedTask; })
    ,("Ocid DMI import lands files, publishes oemN.inf and writes the database in one pass", () => { OcidDmiImport(); return Task.CompletedTask; })
    ,("Cabinet decompressor round-trips MSZIP and LZX fixtures made by makecab", () => { CabinetDecompression(); return Task.CompletedTask; })
    ,("CabExtraction matches expand.exe and refuses container cabinets", () => { CabExtractionAgainstExpand(); return Task.CompletedTask; })
};

var failures = new List<string>();
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures.Add($"{name}: {ex}"); Console.Error.WriteLine($"FAIL {name}: {ex.Message}"); }
}
return failures.Count == 0 ? 0 : 1;

static void QueryParsing()
{
    Equal("10.0.26100.1", UupQueryParser.ParseVersion(" 10.0.26100.1 "));
    Throws<ArgumentException>(() => UupQueryParser.ParseVersion("10.0.26100"));
    Equal("arm64", UupQueryParser.ParseArchitecture("ARM64"));
    Throws<ArgumentException>(() => UupQueryParser.ParseArchitecture("mips"));
    SequenceEqual(new[] { "ProductRelease", "OSFlight" },
        UupQueryParser.ParseFilters(new[] { "productrelease", "OSFlight", "PRODUCTRELEASE" }));
    Throws<ArgumentException>(() => UupQueryParser.ParseFilters(Array.Empty<string>()));
    Throws<ArgumentException>(() => UupQueryParser.ParseFilters(new[] { "unexpected" }));
}

static void SnapshotRoundTrip()
{
    var root = NewTempDirectory();
    try
    {
        var path = Path.Combine(root, "scan.json");
        var snapshot = new UupScanSnapshot
        {
            TotalRequests = 2,
            ReportingSku = "Professional",
            Architecture = "amd64",
            FlightRing = "Retail",
            CurrentBranch = "ge_release",
            PayloadFile = "scan.data.json.gz",
            Entries = new List<UupScanEntry>
            {
                new() { Title = "Build 26100", UpdateId = "abc", Build = "10.0.26100.1", Filter = "OSFlight" }
            }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot));
        var loaded = new UupIntegrationService().LoadSnapshot(path);
        Equal(2, loaded.TotalRequests);
        Equal("amd64", loaded.Architecture);
        Equal("abc", loaded.Entries.Single().UpdateId);
        Equal("10.0.26100.1", loaded.Entries.Single().Build);
        Equal("scan.data.json.gz", loaded.PayloadFile);
    }
    finally { Directory.Delete(root, true); }
}

static void PayloadLookup()
{
    var payloads = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["update-1"] = "payload" };
    Equal("payload", UupPayloadLocator.FindUpdate(payloads, "UPDATE-1"));
    Throws<KeyNotFoundException>(() => UupPayloadLocator.FindUpdate(payloads, "missing"));
    var root = NewTempDirectory();
    try
    {
        var snapshot = Path.Combine(root, "scan.json");
        Equal(Path.Combine(root, "scan.data.json.gz"),
            UupPayloadLocator.ResolvePayloadPath(snapshot, null));
        Throws<InvalidDataException>(() => UupPayloadLocator.ResolvePayloadPath(snapshot, "..\\outside.gz"));
    }
    finally { Directory.Delete(root, true); }
}

static async Task BridgeCancellationAndErrors()
{
    var root = NewTempDirectory();
    try
    {
        var executable = Path.Combine(root, "bridge.exe");
        var payload = Path.Combine(root, "payload.gz");
        File.WriteAllText(executable, string.Empty);
        File.WriteAllText(payload, "offline-test-payload");
        var snapshot = new UupScanSnapshot { SnapshotPath = Path.Combine(root, "scan.json"), PayloadFile = "payload.gz", Architecture = "amd64" };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelling = new UupBridgeService(executable,
            (_, _, token) => Task.FromCanceled(token));
        await ThrowsAsync<OperationCanceledException>(() =>
            cancelling.DownloadAsync(snapshot, new UupScanEntry { UpdateId = "id" }, root, null, cancellation.Token));

        var failing = new UupBridgeService(executable,
            (_, _, _) => Task.FromException(new InvalidOperationException("bridge exit code 7")));
        var error = await ThrowsAsync<InvalidOperationException>(() =>
            failing.DownloadAsync(snapshot, new UupScanEntry { UpdateId = "id" }, root, null, CancellationToken.None));
        Equal("bridge exit code 7", error.Message);
    }
    finally { Directory.Delete(root, true); }
}

static void ArtifactPreflight()
{
    var root = NewTempDirectory();
    try
    {
        var path = Path.Combine(root, "snapshot.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new UupScanSnapshot
        {
            PayloadFile = "missing.gz",
            Entries = new List<UupScanEntry> { new() { UpdateId = "u1" } }
        }));
        var registry = new IntegrationArtifactRegistry();
        var artifact = registry.Register(new IntegrationArtifact(IntegrationArtifactKind.UupSnapshot, path, "test"));
        True(!registry.Preflight(artifact).IsReady, "Missing snapshot payload should block preflight.");
        Equal(1, registry.Artifacts.Count);
        Throws<ArgumentException>(() => registry.Register(new IntegrationArtifact(IntegrationArtifactKind.UupSnapshot, " ", "invalid")));
    }
    finally { Directory.Delete(root, true); }
}

static void MediaConversionPreflight()
{
    var root = NewTempDirectory();
    try
    {
        var input = Path.Combine(root, "input");
        var temp = Path.Combine(root, "temp");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(temp);
        File.WriteAllText(Path.Combine(input, "payload.cab"), "local fixture");
        var converter = Path.Combine(root, "UUPMediaConverter.exe");
        File.WriteAllText(converter, "not executable; preflight only");
        var iso = Path.Combine(root, "exists.iso");
        File.WriteAllText(iso, "preserve existing output");
        var request = new UupMediaConversionRequest(converter, input, iso, temp, "en-US", "");
        var report = UupMediaConversionService.Preflight(request, _ => long.MaxValue);
        True(!report.IsReady, "Preflight must block overwriting an existing ISO.");
        True(report.Errors.Any(error => error.Contains("already exists", StringComparison.OrdinalIgnoreCase)),
            "Existing ISO overwrite hazard was not surfaced.");
        True(report.EstimatedRequiredBytes >= 20L * 1024 * 1024 * 1024,
            "Preflight should expose its conservative working-space estimate.");
        File.Delete(iso);
        File.WriteAllText(Path.Combine(temp, "occupied.tmp"), "keep");
        report = UupMediaConversionService.Preflight(request with { IsoPath = iso }, _ => long.MaxValue);
        True(report.Errors.Any(error => error.Contains("empty temporary", StringComparison.OrdinalIgnoreCase)),
            "Non-empty temporary directory must block conversion.");

        File.Delete(Path.Combine(temp, "occupied.tmp"));
        report = UupMediaConversionService.Preflight(request with { IsoPath = iso },
            _ => report.EstimatedRequiredBytes + 1024 * 1024 * 1024);
        True(report.Errors.Any(error => error.Contains("combined estimated requirement", StringComparison.OrdinalIgnoreCase)),
            "Same-volume temp and output space demands must be combined.");

        var invalid = UupMediaConversionService.Preflight(new UupMediaConversionRequest(
            Path.Combine(root, "missing.exe"), Path.Combine(root, "missing-input"),
            Path.Combine(root, "bad-output.bin"), Path.Combine(root, "missing-temp"), " ", ""),
            _ => 0);
        True(invalid.Errors.Any(error => error.Contains("converter", StringComparison.OrdinalIgnoreCase)), "Missing converter was not rejected.");
        True(invalid.Errors.Any(error => error.Contains("input directory", StringComparison.OrdinalIgnoreCase)), "Missing UUP input was not rejected.");
        True(invalid.Errors.Any(error => error.Contains("language", StringComparison.OrdinalIgnoreCase)), "Invalid language was not rejected.");
        True(invalid.Errors.Any(error => error.Contains(".iso", StringComparison.OrdinalIgnoreCase)), "Invalid ISO extension was not rejected.");
        var malformed = UupMediaConversionService.Preflight(new UupMediaConversionRequest(
            converter, input, "\0bad.iso", temp, "en-US", ""), _ => long.MaxValue);
        True(!malformed.IsReady && malformed.Errors.Any(error => error.Contains("Invalid conversion path", StringComparison.OrdinalIgnoreCase)),
            "Malformed output paths should be returned as explicit preflight errors.");

        var command = UupMediaConversionService.CreateStartInfo(request with { IsoPath = iso, Edition = "Professional" });
        True(!command.UseShellExecute, "Converter must run without shell invocation.");
        True(command.ArgumentList.Contains("--no-key-prompt"), "Redirected conversion must disable the upstream keypress prompt.");
        True(command.ArgumentList.Contains("--uup-path") && command.ArgumentList.Contains(input), "CLI must pass UUP input as a distinct argument.");
        True(command.ArgumentList.Contains("--iso-path") && command.ArgumentList.Contains(iso), "CLI must pass output ISO as a distinct argument.");
        True(command.ArgumentList.Contains("--edition") && command.ArgumentList.Contains("Professional"), "Optional edition must be passed.");

    }
    finally { Directory.Delete(root, true); }
}

static void SxsPreview()
{
    var root = NewTempDirectory();
    try
    {
        var sxs = Path.Combine(root, "SxS");
        Directory.CreateDirectory(sxs);
        var image = Path.Combine(root, "source.wim");
        File.WriteAllText(image, "not an actual image");

        var preview = new SxsWorkflowPreflightService().Preview(image, root, sxs);

        // 语义已变：外部 cbsx.exe / sxsfounder.exe / prepare.cmd 全部移除，
        // 能力由本程序自带实现完成（SxSFounderService / CbsxService /
        // DismServicingService / ExplodeSxSNativeService / PureSxsService）。
        // 所以预检**不再**因"缺少外部工具"而阻断，而应当明确说明不需要外部工具。
        True(preview.Checks.Any(c => c.Contains("不需要", StringComparison.OrdinalIgnoreCase)
                                  || c.Contains("自带", StringComparison.OrdinalIgnoreCase)),
             "Preview should state that no external tools or scripts are required.");
        True(preview.Inputs.Contains(image), "Preview should include the input image.");
        True(preview.PlannedOutputs.Contains(root), "Preview should include the output directory.");
    }
    finally { Directory.Delete(root, true); }
}

static void GenerationRegression()
{
    var root = NewTempDirectory();
    try
    {
        var v1 = Path.Combine(root, "iot.xml");
        File.WriteAllText(v1, "<OEMInput xmlns=\"http://schemas.microsoft.com/embedded/2004/10/ImageUpdate\"><AdditionalFM>FM.xml</AdditionalFM></OEMInput>");
        var v2 = Path.Combine(root, "core.xml");
        File.WriteAllText(v2, "<FeatureManifest xmlns=\"http://schemas.microsoft.com/embedded/2019/06/ImageUpdate\"><FeatureGroups/><Condition/><DeviceTargetInfo/></FeatureManifest>");
        var iotResult = VersionCapabilityService.ScanFile(v1);
        var coreResult = VersionCapabilityService.ScanFile(v2);
        Equal(PlatformGeneration.IoTCore17763, iotResult.Generation);
        Equal(ArtifactKind.OEMInput, iotResult.Kind);
        Equal(PlatformGeneration.CoreOs20348, coreResult.Generation);
        True(coreResult.Features.Contains("FeatureGroups"), "Core OS feature group was not detected.");
        True(coreResult.Features.Contains("DeviceTargetInfo"), "Core OS device targeting was not detected.");
    }
    finally { Directory.Delete(root, true); }
}

static void RuleFamiliesRegression()
{
    // 校验规则目录已按「41 版 ADK DeviceLayoutValidation.dll 内嵌 XML 哈希归族」重建：
    // 目录名=ADK build 族代表版本（14349/14986/16288/17040/17095/17666/17704/17733/
    // 18272/18298/18327/19023），另含 10527（DLL 原生族）与 phone（早期 cab/手机样本独有）。
    // 本测试守护：① 所有模板绑定的规则文件在新树中真实存在；② checker 能完整跑通；
    // ③ 标注「可硬拦截」的模板对绑定规则保持 0 错误（官方模板 × 官方规则零错是硬契约）。
    var bound = DeviceLayoutTemplates.ListAll()
        .Where(t => !string.IsNullOrEmpty(t.DefaultRule)).ToList();
    True(bound.Count >= 6, $"expected >=6 bound templates, got {bound.Count}");

    // 规则树本身非空且目录名符合 ADK build/phone 约定
    var ruleDirs = Directory.EnumerateDirectories(DeviceLayoutRuleChecker.RulesRoot)
        .Select(Path.GetFileName).ToList();
    True(ruleDirs.Count >= 14, $"expected >=14 rule dirs, got {ruleDirs.Count}");
    True(ruleDirs.Contains("phone"), "phone dir missing");
    True(ruleDirs.Contains("19023"), "19023 (latest family) dir missing");

    foreach (var t in bound)
    {
        var rule = t.ResolveRulePath();
        True(rule != null, $"template {t.Key}: rule file missing for {t.DefaultRule}");

        var doc = DeviceLayoutService.ParseFromXml(t.LayoutXml);
        True(doc != null, $"template {t.Key}: layout parse failed: {DeviceLayoutService.LastError}");

        var res = DeviceLayoutRuleChecker.Check(doc!, rule!);
        int errs = res.Issues.Count(i => i.IsError);

        // Notes 声明「0 错，可硬拦截」的模板必须保持零错
        bool hardIntercept = t.Notes.Contains("0 错，可硬拦截");
        if (hardIntercept)
            True(errs == 0, $"template {t.Key} vs {t.DefaultRule}: expected 0 errors, got {errs}"
                + string.Join("; ", res.Issues.Where(i => i.IsError).Select(i => i.Where + ":" + i.Message).Take(3)));
    }
}

// ── OCID 原生服务栈库（OcidServicing.dll）──────────────────────────────────
//
// 这组断言守的是**跨语言边界**，不是业务规则本身（规则由 C++ 侧的测试守）：
//   · 导出名去修饰、调用约定、参数封送是否与 C ABI 对得上；
//   · 架构（x86/x64）能不能在本进程里加载；
//   · "不写"的几种结论能不能正确地穿过边界传回来。
// 这几件事一旦错了，表现是进程崩溃或静默写错字节，而不是编译错误 —— 所以必须实测。
static void OcidServicingNativeAbi()
{
    True(OcidServicingNative.Available,
        "OcidServicing.dll 未找到（先跑 Source\\Native\\build-servicing.ps1）");
    True(OcidServicingNative.Probe(out string probeMsg), "原生库自检失败：" + probeMsg);

    // 类型码必须是**真实 Win32** 码，不是 wcp/smiengine 的内部枚举。
    Equal<uint>(1u, OcidServicingNative.RegValueTypeCode("REG_SZ"));
    Equal<uint>(2u, OcidServicingNative.RegValueTypeCode("REG_EXPAND_SZ"));
    Equal<uint>(3u, OcidServicingNative.RegValueTypeCode("REG_BINARY"));
    Equal<uint>(4u, OcidServicingNative.RegValueTypeCode("REG_DWORD"));
    Equal<uint>(7u, OcidServicingNative.RegValueTypeCode("REG_MULTI_SZ"));
    Equal<uint>(11u, OcidServicingNative.RegValueTypeCode("REG_QWORD"));
    Equal<uint>(0u, OcidServicingNative.RegValueTypeCode("REG_NOT_A_TYPE"));

    // REG_DWORD：4 字节小端。
    True(OcidServicingNative.TryEncode("REG_DWORD", "0x3F", out byte[] dw, out _, out string e1),
        "REG_DWORD 编码失败：" + e1);
    Equal("3F000000", Hex(dw));

    // REG_QWORD：文本就是**那 8 个字节本身**，按低地址→高地址书写，不做数值解释。
    // 所以文本与写进注册表的字节是**逐字符镜射**（identity），不是"大端数字"。
    // 依据：清单 `0001000000000000` ↔ 活机该值读出 `0x100`（23/23 命中、0 反例）。
    // ⚠ 文本必须是 **16 位 hex**（实测 501/501 全语料如此，没有 `0x` 前缀形态）。
    True(OcidServicingNative.TryEncode("REG_QWORD", "0001000000000000", out byte[] qw, out _, out string e2),
        "REG_QWORD 编码失败：" + e2);
    Equal("0001000000000000", Hex(qw));
    True(!OcidServicingNative.TryEncode("REG_QWORD", "0x100", out _, out _, out _),
        "REG_QWORD 带 0x 前缀应失败（真实数据里没有这种形态）");

    // REG_SZ / REG_MULTI_SZ 的宽字符镜像与结尾 NUL。
    True(OcidServicingNative.TryEncode("REG_SZ", "abc", out byte[] sz, out _, out string e3),
        "REG_SZ 编码失败：" + e3);
    Equal("6100620063000000", Hex(sz));

    True(OcidServicingNative.TryEncode("REG_MULTI_SZ", "\"a\",\"b\"", out byte[] ms, out _, out string e4),
        "REG_MULTI_SZ 编码失败：" + e4);
    // "a\0b\0\0" 的 UTF-16LE：每个项各自以 NUL 结束（6100 0000 6200 0000），末尾再加一个 NUL（0000）。
    // 容易写成 "ab\0"（漏掉项间分隔），那是错的。
    Equal("61000000620000000000", Hex(ms));

    // 失败必须是**显式失败**，不许拿 0 顶替。
    True(!OcidServicingNative.TryEncode("REG_NOT_A_TYPE", "x", out _, out _, out _), "未知类型应失败");
    True(!OcidServicingNative.TryEncode("REG_DWORD", "zzz", out _, out _, out _), "REG_DWORD 文本非法应失败");

    // ── CMI 落地决策 ─────────────────────────────────────────────────────
    // ① owner 组件 + 值已存在 + replace ⇒ 不覆盖（这是最容易漏、也最容易写坏数据的一条）
    True(OcidServicingNative.TryResolveWrite("registryKeys", "replace", owner: true,
            "REG_SZ", "new", valueExists: true, existingTypeCode: 1, existing: Bytes("old"),
            out var d1, out string r1), "决策①失败：" + r1);
    Equal(OcidWriteAction.SkipOwned, d1.Action);
    True(!d1.WillWrite, "owner=true + 值已存在 ⇒ 必须不写");

    // ② 值不存在 ⇒ 写，owner 不参与
    True(OcidServicingNative.TryResolveWrite("registryKeys", "replace", owner: true,
            "REG_SZ", "new", valueExists: false, existingTypeCode: 0, existing: Array.Empty<byte>(),
            out var d2, out string r2), "决策②失败：" + r2);
    True(d2.WillWrite, "值不存在 ⇒ 必须写");
    Equal<uint>(1u, d2.TypeCode);

    // ③ owner 组件但 hint 不是 replace（append）⇒ 合并而不是跳过
    True(OcidServicingNative.TryResolveWrite("registryKeys", "append", owner: true,
            "REG_SZ", "B", valueExists: true, existingTypeCode: 1, existing: Bytes("A"),
            out var d3, out string r3), "决策③失败：" + r3);
    True(d3.WillWrite, "append ⇒ 必须写（合并结果）");
    Equal("410042000000", Hex(d3.Data));

    // ④ WCM/SMI 那套**没有** owner 语义：值存在就覆盖
    True(OcidServicingNative.TryResolveWrite("wcm", "", owner: true,
            "REG_SZ", "new", valueExists: true, existingTypeCode: 1, existing: Bytes("old"),
            out var d4, out string r4), "决策④失败：" + r4);
    True(d4.WillWrite, "WCM 不受 owner 影响 ⇒ 必须写");

    // ⑤ 非法 operationHint ⇒ InvalidHint（对应 E_INVALIDARG），不写、也不抛
    True(OcidServicingNative.TryResolveWrite("registryKeys", "bogus", owner: false,
            "REG_SZ", "new", valueExists: true, existingTypeCode: 1, existing: Bytes("old"),
            out var d5, out string r5), "决策⑤失败：" + r5);
    Equal(OcidWriteAction.InvalidHint, d5.Action);
    True(!d5.WillWrite, "非法 hint ⇒ 不写");

    // ⑥ 路径层数回归：原来写死 4 层往上，主程序布局（3 层）永远找不到工具。
    True(OcidSmiService.FindManifestTool() != null,
        "FindManifestTool() 没找到 ocid_manifest.exe（路径层数逻辑坏了）");
}

/// <summary>
/// 安全描述符（SDDL）真的落到**键**上，并且写完立刻回读比对。
/// </summary>
/// <remarks>
/// 为什么在 HKCU 下测、而不是造一个 hive 文件：这里要验的是 C# ↔ 原生的**接线**
/// （导出名、句柄传递、三态结果、回读路径、错误文本）。注册表语义本身已在
/// `Source\Native\OcidServicing\tests\securitydescriptor_tests.cpp` 里对真实键验过；
/// 而 "离线 hive" 与 "活注册表" 走的是同一套内核代码路径（差别只在 hive 从哪来）。
/// HKCU 可写可删，不会碰到别人的键。
/// </remarks>
static void OcidSddlApplyAndReadback()
{
    True(OcidServicingNative.Available, "OcidServicing.dll 未找到（先跑 Source\\Native\\build-servicing.ps1）");

    // ① 文本解读（不碰注册表）
    const string sddl = "D:P(A;;GA;;;SY)(A;;GR;;;BA)(A;;GR;;;BU)";
    True(OcidServicingNative.TryDescribeSddl(sddl, out var facts, out string e1), "解读 SDDL 失败：" + e1);
    True(facts.HasDacl, "应识别出 D: 段");
    Equal(false, facts.HasOwner);
    Equal(false, facts.HasGroup);
    Equal(true, facts.DaclProtected);
    Equal(3, facts.DaclAceCount);
    Equal(0, facts.SaclAceCount);

    string sub = "Software\\OCID_SelfTest\\Sddl_" + Guid.NewGuid().ToString("N");
    var hkcu = Microsoft.Win32.Registry.CurrentUser;
    try
    {
        using (var key = hkcu.CreateSubKey(sub, writable: true))
        {
            True(key != null, "建测试键失败：" + sub);
            IntPtr raw = key!.Handle.DangerousGetHandle();

            // ② 先记下 owner/group，待会儿要证明"只设 D: 没碰它们"
            True(OcidServicingNative.TryReadKeySddl(raw,
                     OcidServicingNative.SecInfo.Owner | OcidServicingNative.SecInfo.Group,
                     out string ownBefore, out string e0), "回读 owner/group 失败：" + e0);

            // ③ 走**产品路径**：ApplyAndVerifyKeySddl 自己以 WRITE_DAC 重开句柄，
            //    写完立刻回读比对。写值用的 CreateSubKey 句柄**没有** WRITE_DAC，
            //    这正是最早让 C# 侧全挂（ERROR_ACCESS_DENIED 5）的那个坑。
            var r = OcidSmiService.ApplyAndVerifyKeySddl(hkcu, sub, sddl);
            True(r.Outcome == OcidServicingNative.SddlApplyOutcome.Applied,
                 $"应写入成功：outcome={r.Outcome} err={r.Error}");
            True(r.Verified == true,
                 $"回读比对应一致：verified={r.Verified} back={r.Got} err={r.Error}");
            True(r.Got.Contains("D:", StringComparison.Ordinal), "回读应含 D: 段：" + r.Got);

            // ④ 只设 D: ⇒ owner/group 一字未动
            True(OcidServicingNative.TryReadKeySddl(raw,
                     OcidServicingNative.SecInfo.Owner | OcidServicingNative.SecInfo.Group,
                     out string ownAfter, out string e4), "回读 owner/group 失败：" + e4);
            Equal(ownBefore, ownAfter);

            // ⑤ `(inherit)` ⇒ 跳过（合法结果），不是失败
            Equal(OcidServicingNative.SddlApplyOutcome.SkippedInherit,
                  OcidSmiService.ApplyAndVerifyKeySddl(hkcu, sub, "(inherit)").Outcome);

            // ⑥ 畸形 SDDL ⇒ 失败**且必须给出原因**（"失败了但不知道为什么"等于没报）
            var bad = OcidSmiService.ApplyAndVerifyKeySddl(hkcu, sub, "D:(A;;GA;;;SY");
            Equal(OcidServicingNative.SddlApplyOutcome.Failed, bad.Outcome);
            True(bad.Error.Length > 0, "失败必须带原因");
        }
    }
    finally
    {
        // 绝不能在用户注册表里留测试键。注意：上面写进去的 `D:P(...)` 把当前用户
        // 挡在门外了（BA/BU 只有读权）—— 这恰好**证明** DACL 真写进去了。
        // 但 owner 恒有 WRITE_DAC，先给自己补一条再删，才删得掉。
        try
        {
            OcidSmiService.ApplyAndVerifyKeySddl(hkcu, sub, "D:(A;;KA;;;WD)");
            hkcu.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);

            // 容器键（Software\OCID_SelfTest）是 CreateSubKey 顺手建的，也要收掉 ——
            // 用户注册表里不该留下任何 OCID 痕迹。
            using (var parent = hkcu.OpenSubKey("Software\\OCID_SelfTest"))
            {
                if (parent != null && parent.SubKeyCount == 0 && parent.ValueCount == 0)
                    hkcu.DeleteSubKey("Software\\OCID_SelfTest", throwOnMissingSubKey: false);
            }
        }
        catch { /* 清理本身失败不影响上面的结论 */ }
    }

    True(hkcu.OpenSubKey(sub) == null, "测试键没清理掉：" + sub);
}

static bool Reg(params string[] args)
{
    var psi = new System.Diagnostics.ProcessStartInfo("reg.exe")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    foreach (string a in args) psi.ArgumentList.Add(a);
    using var p = System.Diagnostics.Process.Start(psi);
    if (p == null) return false;
    p.StandardOutput.ReadToEnd();
    p.StandardError.ReadToEnd();
    p.WaitForExit();
    return p.ExitCode == 0;
}

/// <summary>
/// DACL 是不是真的**落进 hive 文件**了（而不是只活在内存里）。
/// </summary>
/// <remarks>
/// 做法：拿一份最小真实 hive（`reg save HKLM\SAM` 出来的副本）当离线 hive：
/// 载入 → 建键 → 走产品路径写 SDDL（写完回读）→ **卸载** → **重新载入同一个文件** → 再回读。
/// 只有跨载入周期仍在，才算落盘。
///
/// 两个坑：① hive 文件必须本身已经是合法 hive（`reg save` 一份现成的最省事，
/// 空文件 load 不了）；② **卸载前必须没有任何键句柄**，否则 `reg unload` 报
/// "拒绝访问"（实测踩到，GC 之后立刻就能卸）。
/// 载入/卸载需要管理员；拿不到就明确 skip，不假装通过。
/// </remarks>
static void OcidSddlPersistsInOfflineHive()
{
    True(OcidServicingNative.Available, "OcidServicing.dll 未找到（先跑 Source\\Native\\build-servicing.ps1）");

    string tmp = Path.Combine(Path.GetTempPath(),
                              "ocid_acl_probe_" + Guid.NewGuid().ToString("N") + ".hive");
    const string Mount = "OCID_ACL_PROBE";
    const string Sub = "Probe\\SddlPersisted";
    const string Sddl = "D:P(A;;GA;;;SY)(A;;GR;;;BA)";

    if (!Reg("save", @"HKLM\SAM", tmp, "/y"))
    {
        Console.WriteLine("  [skip] reg save 不出探针 hive（需要管理员）—— 跳过落盘持久化用例");
        return;
    }

    bool loaded = false;
    try
    {
        if (!Reg("load", @"HKLM\" + Mount, tmp))
        {
            Console.WriteLine("  [skip] 载入探针 hive 失败（需要管理员）—— 跳过");
            return;
        }
        loaded = true;

        string rel = Mount + "\\" + Sub;

        // ① 在**离线 hive** 里建键（SMI 的服务路径也是这么建的）
        using (var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(rel, writable: true))
            True(k != null, "在载入的离线 hive 里建键失败：" + rel);

        // ② 产品路径：写 SDDL + 立刻回读比对
        var r = OcidSmiService.ApplyAndVerifyKeySddl(
                    Microsoft.Win32.Registry.LocalMachine, rel, Sddl);
        True(r.Outcome == OcidServicingNative.SddlApplyOutcome.Applied,
             $"离线 hive 里写 DACL 应成功：outcome={r.Outcome} err={r.Error}");
        True(r.Verified == true,
             $"离线 hive 里写完回读应一致：back={r.Got} err={r.Error}");

        // 卸载前把句柄放干净
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        True(Reg("unload", @"HKLM\" + Mount),
             "卸载离线 hive（句柄没关干净时这一步会失败）");
        loaded = false;

        // ③ 重新载入**同一个文件**：DACL 还在，才叫落进 hive
        True(Reg("load", @"HKLM\" + Mount, tmp), "重新载入同一份 hive 文件");
        loaded = true;

        using (var k2 = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(rel))
        {
            True(k2 != null, "重载后键还在（顺便证明这次写真的进了文件）");
            IntPtr raw = k2!.Handle.DangerousGetHandle();

            True(OcidServicingNative.TryReadKeySddl(raw, OcidServicingNative.SecInfo.Dacl,
                     out string back, out string e3), "重载后回读 DACL 失败：" + e3);
            True(OcidServicingNative.TryDescribeSddl(back, out var got, out string e4),
                 "重载后回读的 SDDL 无法解读：" + e4);
            Equal(true, got.DaclProtected);
            Equal(2, got.DaclAceCount);
        }
    }
    finally
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        if (loaded) Reg("unload", @"HKLM\" + Mount);
        try { File.Delete(tmp); } catch { /* 临时 hive 删不掉不影响结论 */ }
    }
}

/// <summary>
/// CSI 落位：**硬链接**（不是复制）+ 目录属性 + 清单声明的文件/目录 ACL。
/// </summary>
/// <remarks>
/// 夹具全在临时目录里（不碰真镜像），**照真实镜像的形状**摆：
///   <c>&lt;tmp&gt;\Windows\System32\</c>                     ← 落点父目录
///   <c>&lt;tmp&gt;\packages\&lt;组件目录&gt;.manifest</c>        ← 清单（**平铺**在 packages 下）
///   <c>&lt;tmp&gt;\packages\&lt;组件目录&gt;\basesrv.dll</c>       ← 载荷（在**同名子目录**里）
/// "是硬链接"用**写一个、从另一个读**来证：复制品不会同步。
/// **平铺**这一点是回归的重点：按"清单所在目录名"推组件目录会得到 "packages"，
/// 载荷路径就变成 <c>packages\packages\basesrv.dll</c> —— 真实镜像上实测踩到过。
/// </remarks>
static void OcidCsiFilePlacement()
{
    True(OcidServicingNative.Available, "OcidServicing.dll 未找到（先跑 Source\\Native\\build-servicing.ps1）");

    string root = NewTempDirectory();
    try
    {
        string windows = Path.Combine(root, "Windows");
        string packages = Path.Combine(root, "packages");
        const string CompDir = "arm_microsoft-windows-basesrv_31bf3856ad364e35_6.2.8061.0_none_0123456789abcdef";
        string compPath = Path.Combine(packages, CompDir);
        string system32 = Path.Combine(windows, "System32");
        Directory.CreateDirectory(system32);
        Directory.CreateDirectory(compPath);

        // 载荷：真镜像里 packges\<组件目录>\<文件> 是**独立**的一份（与 WinSxS 正本不同文件）
        string payload = Path.Combine(compPath, "basesrv.dll");
        var bytes = new byte[4096];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = 0xAB;
        File.WriteAllBytes(payload, bytes);

        // writeableType="systemData" 的那类（真镜像里的 win.ini / hosts）载荷
        string writeablePayload = Path.Combine(compPath, "win.ini");
        File.WriteAllText(writeablePayload, "[fonts]\r\n");

        File.WriteAllText(Path.Combine(packages, CompDir + ".manifest"), """
<?xml version="1.0" encoding="utf-8"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v3" manifestVersion="1.0">
  <assemblyIdentity name="Microsoft-Windows-Basesrv" version="6.2.8061.0" processorArchitecture="arm" language="neutral" publicKeyToken="31bf3856ad364e35" buildType="release" />
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
    <security>
      <accessControl>
        <securityDescriptorDefinitions>
          <securityDescriptorDefinition name="WRP_FILE_DEFAULT_SDDL" sddl="D:P(A;;GA;;;SY)(A;;GR;;;BA)(A;;GA;;;BU)" />
          <securityDescriptorDefinition name="WRP_DIR_DEFAULT_SDDL" sddl="D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;BU)" />
        </securityDescriptorDefinitions>
      </accessControl>
    </security>
  </trustInfo>
  <file name="basesrv.dll" destinationPath="$(runtime.system32)\" sourceName="basesrv.dll" sourcePath=".\">
    <securityDescriptor name="WRP_FILE_DEFAULT_SDDL" />
  </file>
  <file name="orphan.dll" destinationPath="$(build.nttree)\" sourceName="orphan.dll" />
  <file name="win.ini" destinationPath="$(runtime.systemRoot)\" sourceName="win.ini" writeableType="systemData" />
  <directories>
    <directory destinationPath="$(runtime.system32)\OcidCsiTest" owner="true" attributes="hidden">
      <securityDescriptor name="WRP_DIR_DEFAULT_SDDL" />
    </directory>
  </directories>
</assembly>
""", new System.Text.UTF8Encoding(false));

        var ops = CsiFileService.BuildPlan(packages, windows, packages);
        Equal(4, ops.Count);

        // 载荷路径必须落在**同名子目录**里（按目录名推的话这里会变成 packages\packages\…）
        Equal(payload, ops.Single(o => o.Name == "basesrv.dll").PayloadPath);

        // 展开不了的宏**必须带着 unresolved 出来**（不是悄悄丢，也不是写个错路径）
        var orphan = ops.Single(o => o.Name == "orphan.dll");
        True(orphan.Unresolved.Length > 0, "未展开宏的条目必须带 unresolved：" + orphan.Unresolved);
        True(orphan.DestinationPath.Length > 0, "destinationPath 应保留原文便于定位");

        var dirOp = ops.Single(o => o.IsDirectory);
        Equal("hidden", dirOp.Attributes);

        var r = CsiFileService.Place(ops);
        foreach (var n in r.Notes) Console.WriteLine("  " + n);
        True(r.Ok, "落位不应有错误：" + r.Summary());
        Equal(4, r.Planned);
        Equal(2, r.StoreCopied);      // 载荷 → WinSxS 正本是**复制**（basesrv + win.ini）
        Equal(1, r.DestLinked);       // 正本 → System32 落点（只有 basesrv）
        Equal(1, r.WriteableCopies);  // win.ini：writeableType ⇒ 独立副本
        Equal(0, r.Copied);           // **不许**退化成复制
        Equal(1, r.Dirs);
        Equal(1, r.Skipped);          // orphan.dll（宏没展开）
        Equal(0, r.MissingPayload);
        Equal(2, r.AclApplied);       // 文件 1 + 目录 1
        Equal(0, r.AclSkipped);
        Equal(0, r.AclMismatched);

        string store = Path.Combine(windows, "WinSxS", CompDir, "basesrv.dll");
        string dest = Path.Combine(system32, "basesrv.dll");
        True(File.Exists(store), "正本没建出来：" + store);
        True(File.Exists(dest), "落点没建出来：" + dest);

        // 硬链接的判据：写一个，另一个跟着变（复制品不会）
        File.WriteAllBytes(dest, new byte[] { 0x11, 0x22, 0x33 });
        True(File.ReadAllBytes(store).SequenceEqual(new byte[] { 0x11, 0x22, 0x33 }),
             "落点与正本不是同一个文件（硬链接没生效）");

        // 正本与**载荷**必须是两份：往正本里写，packages 里的载荷不能跟着变
        File.WriteAllBytes(store, new byte[] { 0x99 });
        True(File.ReadAllBytes(payload).SequenceEqual(bytes),
             "正本是复制品（载荷与正本硬链接在一起了，packages 被覆盖时正本会跟着坏）");

        // writeableType 那条：**必须**是独立副本 —— 往落点里写，正本不能跟着变
        // （正本是所有组件共享的底本；跟着变就等于一次运行时写入污染了镜像）
        string wStore = Path.Combine(windows, "WinSxS", CompDir, "win.ini");
        string wDest = Path.Combine(windows, "win.ini");
        True(File.Exists(wStore), "writeable 文件的正本没建出来：" + wStore);
        True(File.Exists(wDest), "writeable 文件的落点没建出来：" + wDest);
        Equal("[fonts]\r\n", File.ReadAllText(wDest));
        File.WriteAllText(wDest, "[fonts]\r\n[ocid-test]\r\n");
        Equal("[fonts]\r\n", File.ReadAllText(wStore));   // 正本一字未动
        True(!File.ReadAllBytes(wDest).SequenceEqual(File.ReadAllBytes(wStore)),
             "writeable 落点应是独立副本（链到正本就会一起变）");

        // ACL 独立复核（不走产品路径的判断）
        True(OcidServicingNative.TryReadPathSddl(dest, OcidServicingNative.SecInfo.Dacl,
                 out string got, out string aerr), "回读文件 DACL 失败：" + aerr);
        True(OcidServicingNative.TryDescribeSddl(got, out var acl, out _), "回读的 SDDL 解读失败：" + got);
        Equal(true, acl.DaclProtected);
        Equal(3, acl.DaclAceCount);

        // 目录：建出来了 + 属性按清单置位
        string testDir = Path.Combine(system32, "OcidCsiTest");
        True(Directory.Exists(testDir), "目录没建出来：" + testDir);
        True((File.GetAttributes(testDir) & FileAttributes.Hidden) != 0, "目录应带 hidden 属性");

        // 未展开宏的条目**一个文件都不该产生**
        Equal(0, Directory.GetFiles(root, "orphan.dll", SearchOption.AllDirectories).Length);

        // 路径 ACL 的两种边界：合法跳过 vs 真失败
        Equal(OcidServicingNative.SddlApplyOutcome.SkippedInherit,
              CsiFileService.ApplyAndVerifyPathSddl(dest, "(inherit)").Outcome);
        var bad = CsiFileService.ApplyAndVerifyPathSddl(dest, "D:(A;;GA;;;SY");
        Equal(OcidServicingNative.SddlApplyOutcome.Failed, bad.Outcome);
        True(bad.Error.Length > 0, "失败必须带原因");
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); } catch { /* 临时树删不掉不影响结论 */ }
    }
}

/// <summary>
/// TrustedInstaller 那条权限链的自证（真机上独一无二的那条路）：
/// 受保护目录（owner=TI、DACL 只给 TI）**直接写会被拒**；
/// <c>TryWriteInto</c>/<c>ForWrite</c> 取一次所有权、把活干完、再**原样还原**快照；
/// <c>ApplyDeclaredSddl</c> 落的 SDDL 就是目标状态，所以取完所有权**不还原**，但清单没声明
/// 属主时要把属主还回原主。CSI 能不能重装/落位已有组件全靠这条链，必须在回归里锁住。
/// </summary>
static void OcidTrustedInstallerAccess()
{
    True(OcidServicingNative.Available, "OcidServicing.dll 未找到（先跑 Source\\Native\\build-servicing.ps1）");
    if (!TrustedInstallerAccess.IsAdministrator())
        throw new InvalidOperationException("该用例需要管理员身份（SeTakeOwnershipPrivilege 只在管理员令牌里）");

    // 这条链的每一步都要特权：改属主到 TI、写、再还原
    PrivilegeHelper.EnableServicingPrivileges(msg => Console.WriteLine("  " + msg));

    string root = NewTempDirectory();
    string locked = Path.Combine(root, "Locked");
    Directory.CreateDirectory(locked);

    string ti = TrustedInstallerAccess.TrustedInstallerSid;
    string hardened = "O:" + ti + "D:PAI(A;OICI;FA;;;" + ti + ")";
    try
    {
        // 造出"WinSxS 那样"的受保护目录：owner=TI，DACL 只给 TI 全权
        Equal(OcidServicingNative.SddlApplyOutcome.Applied,
              OcidServicingNative.ApplyPathSddl(locked, hardened, out string herr));

        // 前提自证：这个状态**确实**写不进去（否则下面的用例全是假通过）
        bool denied;
        try
        {
            File.WriteAllText(Path.Combine(locked, "baseline.txt"), "x");
            denied = false;
        }
        catch (Exception ex) when (TrustedInstallerAccess.IsAccessDenied(ex)) { denied = true; }
        True(denied, "TI-only 的目录竟然能直接写 —— 这个用例失去意义（有别的权限漏进来了？）");

        // ① TryWriteInto：窗口期内写完**立刻能读**（CSI 建落点前拷正本走的就是这一步）
        string inside = "";
        bool ok = TrustedInstallerAccess.TryWriteInto(
            locked,
            () =>
            {
                File.WriteAllText(Path.Combine(locked, "written.txt"), "ocid");
                inside = File.ReadAllText(Path.Combine(locked, "written.txt"));
            },
            out bool took, out string werr, msg => Console.WriteLine("  " + msg));
        True(ok, "TryWriteInto 失败：" + werr);
        True(took, "走了取所有权那条路就必须记账（OwnershipTaken）");
        Equal("ocid", inside);

        // ★ 还原动作会**把窗口期内新建的子对象重新继承一遍** —— 这是对**容器**设置 DACL 的
        //   固有语义（实测：`icacls /inheritance:r /grant:r` 与 SetNamedSecurityInfo 结果一致），
        //   快照里的 `AI` 剥不剥都一样（曾为此写过 StripAutoInherit，实测无效、已删）。
        //   子对象原有的**继承** ACE 被换成父目录的策略，**显式** ACE 原样保留。
        //   于是窗口不是"一次写动作"而是"一段连续工作"：窗口期内要接着用的东西必须在窗口里
        //   用完（见下面 ④ 的 ForWrite —— CSI"复制正本 + 立刻建落点"就是靠它）。
        string child = Path.Combine(locked, "written.txt");
        True(OcidServicingNative.TryReadPathSddl(child, OcidServicingNative.SecInfo.DefaultRead,
                                                 out string childSddl, out string cerr),
             "回读子文件 ACL 失败：" + cerr);
        Console.WriteLine("  (实测) 窗口关闭后子文件的 ACL：" + childSddl);

        True(OcidServicingNative.TryReadPathSddl(locked, OcidServicingNative.SecInfo.Dacl,
                                                 out string back, out string berr),
             "还原后回读 DACL 失败：" + berr);
        True(OcidServicingNative.TryDescribeSddl(back, out var rb, out _), "回读的 DACL 解读失败：" + back);
        Equal(true, rb.DaclProtected);
        Equal(1, rb.DaclAceCount);                    // 还原 ⇒ 又只剩 TI 一条
        True(OcidServicingNative.TryReadPathSddl(locked, OcidServicingNative.SecInfo.Owner,
                                                 out string ownerSddl, out string oerr),
             "还原后回读 owner 失败：" + oerr);
        True(ownerSddl.Contains("S-1-5-80-956008885"),
             "还原后属主应回到 TrustedInstaller，实际：" + ownerSddl);

        // ② ApplyDeclaredSddl：落的**就是**清单声明的状态 ⇒ 取所有权后不还原
        string declared = "D:PAI(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)";
        Equal(OcidServicingNative.SddlApplyOutcome.Applied,
              TrustedInstallerAccess.ApplyDeclaredSddl(locked, declared, out bool took2, out string derr,
                                                       msg => Console.WriteLine("  " + msg)));
        // 声明 ACL 能不能**直接**落地取决于当前令牌的特权：本机实测 SeRestorePrivilege
        // 启用时，对"BA 连读都没有"的目录也能直接写 DACL，于是压根不会走取所有权那条路
        // （那条路留给特权不生效的环境/对象）。所以这里只记录、不锁死；要钉住的是
        // "声明真的落上了"和"属主没被我们顺手改掉"。
        Console.WriteLine("  (实测) 落声明 ACL 是否走了取所有权：" + (took2 ? "是" : "否（直接可写）"));
        // 声明里没写属主 ⇒ 取所有权只是**手段**：落入声明 DACL 后，属主必须还回原主（TI），
        // 否则镜像里每个被服务的对象属主都会变成当前管理员。
        True(OcidServicingNative.TryReadPathSddl(locked, OcidServicingNative.SecInfo.Owner,
                                                 out string ownerAfter, out string oerr2),
             "回读属主失败：" + oerr2);
        True(ownerAfter.Contains("S-1-5-80-956008885"),
             "声明未写属主时属主应还回 TrustedInstaller，实际：" + ownerAfter);
        // 声明 DACL 给了 BA 全权，之后正常写/读都该通（尽管属主是 TI）
        File.WriteAllText(Path.Combine(locked, "after.txt"), "ok");
        True(OcidServicingNative.TryReadPathSddl(locked, OcidServicingNative.SecInfo.Dacl,
                                                 out string d2, out _), "回读失败");
        True(OcidServicingNative.TryDescribeSddl(d2, out var d2f, out _), "回读的 DACL 解读失败：" + d2);
        Equal(2, d2f.DaclAceCount);

        // ③ 快照往返：拍下 → 改成别的 → 还原回原样
        True(TrustedInstallerAccess.TrySnapshot(locked, out var shot, out string serr), "拍快照失败：" + serr);
        Equal(OcidServicingNative.SddlApplyOutcome.Applied,
              OcidServicingNative.ApplyPathSddl(locked, hardened, out _));
        True(TrustedInstallerAccess.TryRestore(locked, shot!, out string rerr, null), "还原失败：" + rerr);
        True(OcidServicingNative.TryReadPathSddl(locked, OcidServicingNative.SecInfo.Dacl,
                                                 out string restored, out _), "还原后回读失败");
        True(OcidServicingNative.TryDescribeSddl(restored, out var rst, out _), "还原后的 DACL 解读失败");
        Equal(2, rst.DaclAceCount);

        // ④ ForWrite：窗口期内"写进去 + 拿刚写的文件当源"必须都成功 —— 这就是 CSI 的
        //    "复制正本 → 立刻建落点"。实测窗口一关，源会被父目录的还原重算成 TI-only，
        //    读它就是 Access denied（产品里表现为 CreateHardLinkW 拒绝访问）。
        Equal(OcidServicingNative.SddlApplyOutcome.Applied,
              OcidServicingNative.ApplyPathSddl(locked, hardened, out _));   // 重新锁死
        string store = Path.Combine(locked, "store.bin");
        string copied = Path.Combine(locked, "copied.bin");
        bool did = TrustedInstallerAccess.ForWrite(
            locked,
            () =>
            {
                File.WriteAllText(store, "payload");
                File.Copy(store, copied);       // ← 窗口若已关：源不可读 ⇒ 拒绝访问
            },
            out bool took4, out string ferr, msg => Console.WriteLine("  " + msg));
        True(did, "ForWrite 失败：" + ferr);
        True(took4, "受保护目录上必须走取所有权那条路");

        // 反向自证：窗口关掉之后同一条源文件就该"用不了"了（目标放在目录外，排除"目标目录
        // 不可写"这个混在一起的变量）。只记录实测现象、不锁死断言 —— 它取决于系统是否在
        // 还原父目录 DACL 时重算子对象的继承。
        bool sourceDenied;
        try { File.Copy(store, Path.Combine(root, "copied2.bin")); sourceDenied = false; }
        catch (Exception ex) when (TrustedInstallerAccess.IsAccessDenied(ex)) { sourceDenied = true; }
        Console.WriteLine("  (实测) 窗口关闭后再拿同一个源复制：" +
                          (sourceDenied ? "拒绝访问（正是 ④ 存在的理由）" : "仍然成功"));

        // ⑤ 别把什么都当权限不足：普通 IO 错误不许触发"取所有权"这条重路
        True(TrustedInstallerAccess.IsAccessDenied(new UnauthorizedAccessException()), "权限异常必须识别");
        True(!TrustedInstallerAccess.IsAccessDenied(new IOException("磁盘满了")), "IO 错误不能被当成权限问题");

        // ⑥ 诊断路径不许抛（TI 令牌在现代 Windows 上是 PPL，拿不到是**预期**结果）
        True(TrustedInstallerAccess.Diagnose().Length > 0, "诊断文本不能为空");
        TrustedInstallerAccess.RevertImpersonation();
    }
    finally
    {
        // 收尾：先解开锁，否则临时树删不掉 —— 这正是产品里"重装被锁死的组件"的同一情形
        try { OcidServicingNative.ApplyPathSddl(locked, TrustedInstallerAccess.TemporaryWriteSddl, out _); }
        catch { /* 收尾失败不影响结论 */ }
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}

static void OcidMacroExpansion()
{
    True(OcidServicingNative.Available, "OcidServicing.dll 未找到（先跑 Source\\Native\\build-servicing.ps1）");

    // $(runtime.*) 的基础路径来自**镜像自身** —— 离线 hive 里没有这些路径，所以 windowsDir
    // 参数就是唯一来源。这里模拟离线镜像 P:\Windows（挂载出来的 8061 ARM 盘）。
    const string Win = @"P:\Windows";

    // ① 最常用的一个：Windows 目录本身。
    True(OcidServicingNative.TryExpandMacros(Win, "$(runtime.windows)", out string x1, out string l1, out string e1),
        "①失败：" + e1);
    Equal(@"P:\Windows", x1);
    Equal("", l1);

    // ② 宏名**大小写不敏感**：清单里 $(runtime.Windows) 与 $(runtime.windows) 并存。
    True(OcidServicingNative.TryExpandMacros(Win, "$(runtime.Windows)", out string x2, out _, out string e2),
        "②失败：" + e2);
    Equal(@"P:\Windows", x2);

    // ③ system32 是**小写**拼上去的（不是 Win32 常见的 System32 大小写）——
    //    这是照 smiengine!ExpandDollarVar / wcp 的权威表抄的，不是我们随手拼的。
    True(OcidServicingNative.TryExpandMacros(Win, "$(runtime.system32)", out string x3, out _, out string e3),
        "③失败：" + e3);
    Equal(@"P:\Windows\system32", x3);

    // ④ $(runtime.system) 是 **NTVDM 的 System** 目录，不是 system32 的别名。
    //    实测依据：NTVDM 相关注册表里 COMMDLG.DLL / MMSYSTEM.DLL / keyboard.drv 等都在 <Windows>\System。
    True(OcidServicingNative.TryExpandMacros(Win, "$(runtime.system)", out string x4, out _, out string e4),
        "④失败：" + e4);
    Equal(@"P:\Windows\System", x4);

    // ⑤ 一个值里可以混多个宏与普通文本，非宏部分必须原样保留。
    True(OcidServicingNative.TryExpandMacros(Win, @"pre $(runtime.windows)\Sysnative post",
            out string x5, out _, out string e5), "⑤失败：" + e5);
    Equal(@"pre P:\Windows\Sysnative post", x5);

    // ⑥ 调用方没有覆盖时，Program Files 由 windowsDir 的盘符推导（P:\Program Files）。
    True(OcidServicingNative.TryExpandMacros(Win, @"$(runtime.programFiles)\Sub",
            out string x6, out _, out string e6), "⑥失败：" + e6);
    Equal(@"P:\Program Files\Sub", x6);

    // ⑦ 镜像自己声明了目录（清单里 x:\Program Files 那种占位盘符）⇒ 必须用声明值覆盖。
    True(OcidServicingNative.TryExpandMacrosEx(Win, @"X:\Program Files", "", "", "", "",
            @"$(runtime.programFiles)\App", out string x7, out _, out string e7), "⑦失败：" + e7);
    Equal(@"X:\Program Files\App", x7);

    // ⑧ 非路径宏（$(string.*)、$(build.nttree) 等）**不是错误**：原样保留，
    //    并通过 leftover 报给调用方，由产品侧决定要不要告警。
    True(OcidServicingNative.TryExpandMacros(Win, "$(string.foo)", out string x8, out string l8, out string e8),
        "⑧失败（未展开的宏不该算错误）：" + e8);
    Equal("$(string.foo)", x8);
    True(l8.Contains("$(string.foo)"), "⑧ leftover 应报出未展开的宏，实际：" + l8);

    // ⑨ 宏名清单与产品侧诊断共用同一份原生表，数量固定 23（`;` 分隔）。
    string names = OcidServicingNative.KnownMacroNames();
    True(names.Contains("runtime.system32"), "⑨ 缺少 runtime.system32：" + names);
    True(names.Contains("runtime.public"), "⑨ 缺少 runtime.public：" + names);
    Equal(23, names.Split(';', StringSplitOptions.RemoveEmptyEntries).Length);
}

// registryKeys 那一套（原生 `ocid_manifest.exe export-ops` 吐的 JSONL）是 C++ → C# 的
// **线格式契约**。下面这几行是 4000 份真实清单跑出来的原样文本（不是手写的），
// 字段名或语义一改，这里就该红 —— 而不是等到把错误的字节写进镜像才发现。
static void OcidRegistryOpShape()
{
    // ① 普通的"有值"设置：必须能认出类型码与编码好的字节。
    const string WithValue = """
    {"component":"x","identity":"i","mechanism":"registryKeys","root":"HKEY_LOCAL_MACHINE",
    "subKey":"SOFTWARE\\Foo","valueName":"Bar","valueType":"REG_SZ","data":"baz",
    "scope":"allUsers","owner":false,"operationHint":"replace","sourceItem":"s",
    "keyOnly":false,"keySddl":"","keySdName":"","keySdBuildFilter":"","perUserVirtualization":"",
    "encoded":true,"typeCode":1,"dataHex":"620061007A000000","hasMacro":false,"encodeError":""}
    """;
    var a = OcidSmiService.ParseOpJson(WithValue);
    True(a != null, "①解析失败");
    Equal("HKEY_LOCAL_MACHINE", a!.Root);
    Equal(@"SOFTWARE\Foo", a.SubKey);
    Equal("Bar", a.ValueName);
    Equal("REG_SZ", a.ValueType);
    True(a.Encoded, "①encoded 应为 true");
    Equal(1u, a.TypeCode);
    True(!a.KeyOnly, "①不该被判成 key-only");
    Equal("", a.KeySddl);

    // ② ★ 只建键、不写值（4000 份实测 41 条）。认不出来就会去写 `(Default)=""`，
    //    镜像里凭空多一条值、而且看起来像成功。
    //    同时它带 SDDL —— 键级 ACL 与"没有值"是两件独立的事，必须都能带出来。
    const string KeyOnlyWithSd = """
    {"component":"msil_ipamauc_31bf3856ad364e35_6.2.8400.0_none","identity":"IpamAuc~6.2.8400.0~msil~31bf3856ad364e35~neutral","mechanism":"registryKeys","root":"HKEY_CLASSES_ROOT","subKey":"CLSID\\{A9A1821A-65B2-4F99-80A6-337C4549815C}\\Implemented Categories\\{62C8FE65-4EBB-45E7-B440-6E39B2CDBF29}","valueName":"","valueType":"","data":"","scope":"","owner":false,"operationHint":"","sourceItem":"HKEY_CLASSES_ROOT\\CLSID\\{A9A1821A-65B2-4F99-80A6-337C4549815C}\\Implemented Categories\\{62C8FE65-4EBB-45E7-B440-6E39B2CDBF29}","keyOnly":true,"keySddl":"O:S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464G:S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464D:P(A;CI;GA;;;S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464)(A;CI;GR;;;SY)(A;CI;GR;;;BA)(A;CI;GR;;;BU)(A;CI;GR;;;S-1-15-2-1)","keySdName":"WRP_REGKEY_DEFAULT_SDDL","keySdBuildFilter":"","perUserVirtualization":"","encoded":true,"typeCode":0,"dataHex":"","hasMacro":false,"encodeError":""}
    """;
    var b = OcidSmiService.ParseOpJson(KeyOnlyWithSd);
    True(b != null, "②解析失败");
    True(b!.KeyOnly, "②没认出 keyOnly —— 会写出一条 (Default)=\"\"");
    Equal("", b.ValueName);
    Equal("", b.ValueType);
    Equal("", b.Data);
    Equal(0u, b.TypeCode);
    Equal(@"HKEY_CLASSES_ROOT", b.Root);
    Equal(@"CLSID\{A9A1821A-65B2-4F99-80A6-337C4549815C}\Implemented Categories\{62C8FE65-4EBB-45E7-B440-6E39B2CDBF29}", b.SubKey);
    Equal("WRP_REGKEY_DEFAULT_SDDL", b.KeySdName);
    True(b.KeySddl.StartsWith("O:S-1-5-80-956008885", StringComparison.Ordinal), "②SDDL 没带出来：" + b.KeySddl);

    // ③ "有值 + 带 SD" 与 "只有键" 是两种；别把前者也吞掉。
    const string ValueWithSd = """
    {"root":"HKEY_LOCAL_MACHINE","subKey":"SOFTWARE\\Foo","valueName":"Bar",
    "valueType":"REG_DWORD","operationHint":"replace","keyOnly":false,
    "keySddl":"D:P(A;;GA;;;SY)","keySdName":"N","perUserVirtualization":"Disable",
    "encoded":true,"typeCode":5,"dataHex":"01000000","hasMacro":false,"encodeError":""}
    """;
    var c = OcidSmiService.ParseOpJson(ValueWithSd);
    True(c != null, "③解析失败");
    True(!c!.KeyOnly, "③有值的设置被误判成 key-only");
    Equal("Bar", c.ValueName);
    Equal("Disable", c.PerUserVirtualization);
    Equal("D:P(A;;GA;;;SY)", c.KeySddl);

    // ④ 编码失败必须传上来（Encoded=false ⇒ 上层不许写）。
    const string Bad = """
    {"root":"HKEY_LOCAL_MACHINE","subKey":"SOFTWARE\\Foo","valueName":"Bar",
    "valueType":"REG_SZ","encoded":false,"typeCode":0,"dataHex":"",
    "hasMacro":true,"encodeError":"boom"}
    """;
    var d = OcidSmiService.ParseOpJson(Bad);
    True(d != null, "④解析失败");
    True(!d!.Encoded, "④encoded=false 没传出来");
    True(d.HasMacro, "④hasMacro 没传出来");
    Equal("boom", d.EncodeError);
}

static void OcidDriverPackage()
{
    True(OcidServicingNative.Available, "OcidServicing.dll 未找到（先跑 Source\\Native\\build-servicing.ps1）");

    string root = NewTempDirectory();
    try
    {
        // ① StoreId：测试里把公式**独立**再实现一遍（h = h*39 + byte，哈希按小端写 16 位十六进制）
        string inf = Path.Combine(root, "ax88772.inf");
        var bytes = new byte[16];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(0xF0 + i);
        File.WriteAllBytes(inf, bytes);

        ulong h = 0;
        foreach (byte b in bytes) h = h * 39 + b;
        var le = new byte[8];
        for (int i = 0; i < 8; i++) le[i] = (byte)(h >> (8 * i));
        string expect = "ax88772.inf_arm_" + Convert.ToHexString(le).ToLowerInvariant();

        True(OcidServicingNative.TryDmiStoreId(inf, 5, out var storeId, out var err), "StoreId 算不出来：" + err);
        Equal(expect, storeId);

        // 哈希只看文件字节：内容不变、换个架构串只是前缀换掉
        True(OcidServicingNative.TryDmiStoreId(inf, 0, out var storeIdX86, out _), "x86 StoreId 算不出来");
        Equal("ax88772.inf_x86_" + Convert.ToHexString(le).ToLowerInvariant(), storeIdX86);

        // 内容变一个字节，哈希必须变（防止"算了个常量"这种假通过）
        File.WriteAllBytes(inf, bytes.Concat(new byte[] { 0x01 }).ToArray());
        True(OcidServicingNative.TryDmiStoreId(inf, 5, out var storeId2, out _), "改内容后 StoreId 算不出来");
        True(storeId2 != storeId, "INF 内容变了 StoreId 却不变");

        // ② 架构串只认 8061 认的那四个，别的必须回 null（不许瞎编一个）
        Equal("x86", OcidServicingNative.DmiArchString(0));
        Equal("arm", OcidServicingNative.DmiArchString(5));
        Equal("ia64", OcidServicingNative.DmiArchString(6));
        Equal("amd64", OcidServicingNative.DmiArchString(9));
        True(OcidServicingNative.DmiArchString(7) == null, "未知架构必须回 null");

        // ③ DriverDatabase 的值名 = "%04x" 的属性 id
        Equal("0003", OcidServicingNative.DmiDbValueName(3));
        Equal("1a2b", OcidServicingNative.DmiDbValueName(0x1A2B));
        Equal("ffff", OcidServicingNative.DmiDbValueName(0xFFFF));

        // ④ oemN.inf 名的分配：位图里第一个 0 位（每字节高位在前），满了才长一个字节
        True(OcidServicingNative.TryDmiAcquireOemInfName(Array.Empty<byte>(), out var bm1, out var n1, out var i1, out var grew1, out var e1), e1);
        Equal("oem0.inf", n1);
        Equal(0u, i1);
        True(grew1, "空位图必须先长出一个字节");
        Equal(1, bm1.Length);
        Equal((byte)0x80, bm1[0]);   // 0 号位被占

        True(OcidServicingNative.TryDmiAcquireOemInfName(bm1, out var bm2, out var n2, out _, out var grew2, out var e2), e2);
        Equal("oem1.inf", n2);
        True(!grew2, "还有空位就不该长位图");
        Equal((byte)0xC0, bm2[0]);

        // 释放 0 号位后它必须被重新分配到（不然早晚把 oem 号用光）
        True(OcidServicingNative.TryDmiReleaseOemInfName(bm2, "oem0.inf", out var e3), e3);
        Equal((byte)0x40, bm2[0]);
        True(OcidServicingNative.TryDmiAcquireOemInfName(bm2, out _, out var n4, out _, out _, out var e4), e4);
        Equal("oem0.inf", n4);

        // ⑤ 保留原 INF 名的标志位
        True(OcidServicingNative.DmiKeepsOriginalInfName(0x200), "收件箱包应保留原 INF 名");
        True(OcidServicingNative.DmiKeepsOriginalInfName(0x100), "OS 引导包应保留原 INF 名");
        True(!OcidServicingNative.DmiKeepsOriginalInfName(0), "默认导入要改名成 oemN.inf");
        True(!OcidServicingNative.DmiKeepsOriginalInfName(0x400), "别的标志不该误判");
    }
    finally { Directory.Delete(root, true); }

    // ⑥ 真机对照：8061 镜像里唯一那个驱动包（有镜像才算，没镜像就跳过）
    const string RealInf = @"P:\Windows\INF\ax88772.inf";
    const string RealStore = @"P:\Windows\System32\DriverStore\FileRepository";
    if (File.Exists(RealInf) && Directory.Exists(RealStore))
    {
        var dirs = Directory.GetDirectories(RealStore);
        True(dirs.Length >= 1, "真机 DriverStore 至少该有一个包目录");

        True(OcidServicingNative.TryDmiStoreId(RealInf, 5, out var realId, out var realErr), "真机 StoreId 算不出来：" + realErr);
        foreach (var d in dirs)
            True(Path.GetFileName(d) == realId, $"目录名对不上：{Path.GetFileName(d)} ≠ {realId}");

        // 目录里那份 INF 必须与 Windows\INF 里那份逐字节相同（同一个文件的两条路径）
        string stored = Path.Combine(dirs[0], Path.GetFileName(RealInf));
        True(File.Exists(stored), "包目录里应该有同名 INF：" + stored);
        True(File.ReadAllBytes(stored).SequenceEqual(File.ReadAllBytes(RealInf)),
             "包目录里的 INF 与 Windows\\INF 里的不是同一份内容");
    }
}

static byte[] Bytes(string s)
{
    // 测试里构造"已有值"：REG_SZ 的内存镜像 = UTF-16 + 结尾 NUL。
    var chars = s.ToCharArray();
    var b = new byte[(chars.Length + 1) * 2];
    for (int i = 0; i < chars.Length; i++)
    {
        b[i * 2] = (byte)(chars[i] & 0xFF);
        b[i * 2 + 1] = (byte)(chars[i] >> 8);
    }
    return b;
}


// ───────────────────────── DMI：DriverDatabase 落盘（真机回读） ─────────────────────────
//
// 验收方式：把真机 hive（Q: 上的 8061 x86 client 的 SYSTEM，之前会话已经挂在 HKLM\OCIDDB）
// 用 reg save 存一份、用 reg load 挂成 HKLM\OCIDTEST，**先清空**三个驱动库子树，
// 再用我们算出来的计划文件重新写一遍，最后和真机逐值比对 kind + 数据。
//
// 为什么先清空：不清空的话"写"这一步可能是空转，比对仍然全过 —— 那就什么都没证明。
// 计划文件由原生工具产生（ocid_manifest dmi-ledger），C# 只负责照单写。
// 驱动包**文件**落盘：拿真机商店目录当载荷来源，落到临时镜像根里再逐个字节回读。
// 为什么用真机目录当来源：它同时给了"该有哪些文件"和"内容该是什么"两份金标准。
static void OcidDriverStoreLanding()
{
    const string imageRoot = @"Q:\";
    string repository = Path.Combine(imageRoot, @"Windows\System32\DriverStore\FileRepository");
    if (!Directory.Exists(repository)) { Console.WriteLine("  [skip] 没有真机镜像（Q:）"); return; }

    string root = Path.Combine(Path.GetTempPath(), "ocid-dmi-" + Guid.NewGuid().ToString("N"));
    string image = Path.Combine(root, "image");
    Directory.CreateDirectory(image);
    try
    {
        // 挑三个小包（1 个 INF + 少量载荷），避免把测试拖成几分钟。
        var candidates = Directory.GetDirectories(repository)
            .Select(d => (Dir: d, Files: Directory.GetFiles(d)))
            .Where(x => x.Files.Length is >= 2 and <= 6)
            .Where(x => x.Files.Count(f => f.EndsWith(".inf", StringComparison.OrdinalIgnoreCase)) == 1)
            .OrderBy(x => x.Files.Length)
            .Take(3)
            .ToList();
        True(candidates.Count == 3, $"找不到 3 个可以当样本的商店目录（{candidates.Count}）");

        foreach (var (dir, files) in candidates)
        {
            string storeId = Path.GetFileName(dir);
            string inf = files.First(f => f.EndsWith(".inf", StringComparison.OrdinalIgnoreCase));
            string infName = Path.GetFileName(inf);

            var landed = DriverStoreService.LandPackage(image, dir, storeId, infName,
                files.Select(Path.GetFileName), publishedInfName: infName,
                log: m => Console.WriteLine("  " + m));

            True(landed.Files.Count == files.Length,
                $"落盘文件数不符：{landed.Files.Count} vs {files.Length}（{storeId}）");
            True(landed.PublishedUnderOriginalName, "收件箱驱动应当以原名发布");
            True(File.Exists(landed.PublishedInfPath), $"没有发布 INF：{landed.PublishedInfPath}");

            var (checkedFiles, problems) = DriverStoreService.VerifyPackage(dir, landed.StoreDirectory,
                infName, landed.PublishedInfPath);
            True(checkedFiles == files.Length, $"回读文件数不符：{checkedFiles} vs {files.Length}");
            True(problems.Count == 0, $"回读不一致（{storeId}）：{string.Join("；", problems.Take(3))}");
            Console.WriteLine($"  {storeId}：{checkedFiles} 个文件逐个字节一致，INF 已按原名发布");
        }

        // 商店目录的路径规则：<image>\Windows\System32\DriverStore\FileRepository\<StoreId>（无中间层）。
        string probe = DriverStoreService.StoreDirectoryOf(image, "x.inf_x86_0123456789abcdef");
        True(probe.EndsWith(@"Windows\System32\DriverStore\FileRepository\x.inf_x86_0123456789abcdef",
             StringComparison.OrdinalIgnoreCase), $"商店目录规则不对：{probe}");
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); } catch { /* 临时目录清不掉不影响结论 */ }
    }
}
static void OcidDriverDatabaseLedger()
{
    const string sourceMount = "OCIDDB";      // 真机 hive（只读用）
    const string testMount = "OCIDTEST";      // 我们写的副本
    string ledger = Environment.GetEnvironmentVariable("OCID_LEDGER_JSONL") ?? @"E:\ocid_verify\ledger2.jsonl";
    string imageSystem = Environment.GetEnvironmentVariable("OCID_IMAGE_SYSTEM")
        ?? @"Q:\Windows\System32\config\SYSTEM";

    if (!File.Exists(ledger)) { Console.WriteLine($"  [skip] 没有计划文件 {ledger}"); return; }
    if (!File.Exists(imageSystem)) { Console.WriteLine($"  [skip] 没挂载真机镜像 {imageSystem}"); return; }

    var root = NewTempDirectory();
    string hived = Path.Combine(root, "system.hiv");
    try
    {
        // 【用哪份 hive 写】镜像里的 `config\DRIVERS` 就是**真正的驱动数据库 hive**：
        // 它的 `DriverDatabase` 属于 SYSTEM/TrustedInstaller，管理员直接枚举都是 Access denied
        // （实测）。这正是离线落盘要面对的真实条件 —— 所以测试也走同一条路：
        // 拷一份 DRIVERS → reg load → 夺所有权 → 写 → 逐值比对 → **还原原 SDDL** → unload。
        string driversHive = Path.Combine(Path.GetDirectoryName(imageSystem)!, "DRIVERS");
        True(File.Exists(driversHive), "镜像里没有 config\\DRIVERS");
        File.Copy(driversHive, hived, overwrite: true);

        RunReg("load", $@"HKLM\{testMount}", hived);
        try
        {
            PrivilegeHelper.EnableServicingPrivileges(m => Console.WriteLine("  " + m));

            using (var testRoot = Registry.LocalMachine.OpenSubKey(testMount, writable: true)
                       ?? throw new InvalidOperationException($"{testMount} 打不开"))
            {
                True(OcidServicingNative.TryGetCurrentUserSid(out string sid, out string sidErr),
                     $"拿不到当前用户 SID：{sidErr}");

                True(OcidServicingNative.TryTakeKeyTreeOwnership(testRoot.Handle.DangerousGetHandle(), "DriverDatabase",
                        out string priorText, out string ownErr),
                    $"逐键夺权失败：{ownErr}（进程需要管理员 + SeTakeOwnershipPrivilege）");
                int takenKeys = priorText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                True(takenKeys > 1, "应当拿下整棵树，而不是只有根键");
                Console.WriteLine($"  逐键夺权成功：{takenKeys} 个键，当前用户 {sid}");

                var (rows, keys, values) = DriverDatabaseService.ApplyLedgerJsonl(testRoot, ledger, "DriverDatabase",
                    m => Console.WriteLine("  " + m));
                True(rows > 0 && values > 0, "计划文件是空的");
                Console.WriteLine($"  写入：{rows} 行 / {keys} 键 / {values} 值");

                // 还原原 SDDL（夺权只是**写入窗口**，不是永久改镜像）。
                True(OcidServicingNative.TryRestoreKeyTreeSddl(testRoot.Handle.DangerousGetHandle(), priorText,
                        out string restoreErr),
                    $"还原 SDDL 失败：{restoreErr} —— 镜像的安全边界会被留下改动");
                Console.WriteLine($"  原 SDDL 已还原（{takenKeys} 个键）");
            }
            using var expected = Registry.LocalMachine.OpenSubKey(sourceMount)
                ?? throw new InvalidOperationException($"{sourceMount} 打不开");
            using var actual = Registry.LocalMachine.OpenSubKey(testMount)
                ?? throw new InvalidOperationException($"{testMount} 打不开");
            // 真机镜像里 575 个商店目录只有 158 个还有台账记录（其余是清理后留下的文件），
            // 所以拿"真机有记录的"当金标准；`0007`（签名者）来自目录签名验证，不在比对范围。
            var (compared, mismatch, skipped) = DriverDatabaseService.CompareLedger(expected, actual, ledger,
                "DriverDatabase", m => Console.Error.WriteLine("  " + m),
                skipMissingInExpected: true, skipValueNames: new HashSet<string> { "0007" });
            Console.WriteLine($"  回读比对：{compared} 个值，不一致 {mismatch}，真机无记录跳过 {skipped}");
            True(compared > 1200, $"比对的值太少（{compared}）—— 计划文件或挂载不对");
            True(mismatch == 0, $"{mismatch} 个值与真机不一致");
        }
        finally
        {
            RunReg("unload", $@"HKLM\{testMount}");
        }
    }
    finally
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
    }
}

// 端到端：一次 Import 把"分 oemN.inf 名、铺文件、发布 INF、写库、位图"全做完，
// 然后逐样回读。第三方包（inbox=false）走 oemN.inf 分配，这是收件箱路径没覆盖的分支。
static void OcidDmiImport()
{
    string repository = @"Q:\Windows\System32\DriverStore\FileRepository";
    string driversHive = @"Q:\Windows\System32\config\DRIVERS";
    if (!Directory.Exists(repository) || !File.Exists(driversHive))
    {
        Console.WriteLine("  [skip] 没有真机镜像（Q:）");
        return;
    }

    string root = NewTempDirectory();
    string hive = Path.Combine(root, "drivers.hiv");
    try
    {
        string image = Path.Combine(root, "image");
        Directory.CreateDirectory(image);
        Directory.CreateDirectory(Path.Combine(image, "Windows", "INF"));

        var candidate = Directory.GetDirectories(repository)
            .Select(d => (Dir: d, Files: Directory.GetFiles(d)))
            .Where(x => x.Files.Length is >= 2 and <= 4)
            .Where(x => x.Files.Count(f => f.EndsWith(".inf", StringComparison.OrdinalIgnoreCase)) == 1)
            .OrderBy(x => x.Files.Length)
            .First();
        string storeId = Path.GetFileName(candidate.Dir);
        string infName = Path.GetFileName(candidate.Files.First(f => f.EndsWith(".inf", StringComparison.OrdinalIgnoreCase)));
        var fileNames = candidate.Files.Select(Path.GetFileName).ToList();

        // 计划文件：包记录 + 真机记录里那 3 个必须的 INF 值，够验证"写库确实发生了"。
        string ledger = Path.Combine(root, "one.jsonl");
        File.WriteAllLines(ledger, new[]
        {
            "{\"keys\":[{\"path\":\"DriverPackages\\\\" + storeId.Replace("\\", "\\\\") +
            "\",\"values\":[{\"name\":\"0002\",\"type\":\"sz\",\"text\":\"" + infName + "\"}," +
            "{\"name\":\"0005\",\"type\":\"dword\",\"dword\":1}," +
            "{\"name\":\"000a\",\"type\":\"dword\",\"dword\":0}]}]}"
        });

        File.Copy(driversHive, hive, overwrite: true);
        RunReg("load", @"HKLM\OCIDIMP", hive);
        try
        {
            PrivilegeHelper.EnableServicingPrivileges(m => Console.WriteLine("  " + m));
            using var hiveRoot = Registry.LocalMachine.OpenSubKey("OCIDIMP", writable: true)
                ?? throw new InvalidOperationException("OCIDIMP 打不开");

            // DriverDatabase 属于 SYSTEM/TrustedInstaller：先夺所有权再写（真机同样条件）。
            using (var probe = Registry.LocalMachine.OpenSubKey("OCIDIMP", writable: true)!)
            {
                True(OcidServicingNative.TryTakeKeyTreeOwnership(probe.Handle.DangerousGetHandle(),
                        "DriverDatabase", out _, out string ownErr), $"夺所有权失败：{ownErr}");
            }

            var result = DmiImportService.Import(image, hiveRoot, storeId, infName, candidate.Dir, fileNames,
                inbox: false, architecture: 0, ledger, m => Console.WriteLine("  " + m));

            True(result.PublishedInfName.StartsWith("oem", StringComparison.OrdinalIgnoreCase) &&
                 result.PublishedInfName.EndsWith(".inf", StringComparison.OrdinalIgnoreCase),
                $"第三方包应当发布成 oemN.inf，实际 {result.PublishedInfName}");
            True(result.BitmapChanged, "分配 oemN.inf 应当改动位图");
            True(result.Landed.Count == fileNames.Count, $"落盘文件数不符：{result.Landed.Count} vs {fileNames.Count}");
            True(File.Exists(Path.Combine(image, "Windows", "INF", result.PublishedInfName)),
                "发布后的 oemN.inf 不在 Windows\\INF 下");

            using var storeKey = DriverDatabaseService.EnsureKey(hiveRoot, "DriverDatabase");
            var bitmap = storeKey.GetValue("0004") as byte[];
            True(bitmap is { Length: DmiImportService.OemBitmapSize }, "根键 0004 位图没写成 4099 字节");
            True(storeKey.GetValue("0003") is int, "根键 0003 架构值没写");
            using var package = storeKey.OpenSubKey("DriverPackages\\" + storeId)
                ?? throw new InvalidOperationException("包记录没写进库");
            Equal(infName, package.GetValue("0002") as string);
            Console.WriteLine($"  {storeId} → {result.PublishedInfName}，{result.Landed.Count} 个文件，{result.Values} 个值");
        }
        finally
        {
            RunReg("unload", @"HKLM\OCIDIMP");
        }
    }
    finally
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
    }
}

// 把 SDDL 应用到已打开的键（还原用）。失败打印原因并返回 false。
static bool OcIdApply(RegistryKey key, string sddl)
{
    var outcome = OcIdServicingNativeApply(key, sddl, out string err);
    if (!outcome) Console.Error.WriteLine($"  [sddl] {err}");
    return outcome;
}

static bool OcIdServicingNativeApply(RegistryKey key, string sddl, out string error)
{
    var outcome = OneCore_Image_Designer.Services.OcidServicingNative.ApplyKeySddl(
        key.Handle.DangerousGetHandle(), sddl, out error);
    return outcome == OneCore_Image_Designer.Services.OcidServicingNative.SddlApplyOutcome.Applied
           || outcome == OneCore_Image_Designer.Services.OcidServicingNative.SddlApplyOutcome.SkippedInherit;
}
static void RunReg(params string[] args)
{
    var psi = new System.Diagnostics.ProcessStartInfo("reg.exe")
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var proc = System.Diagnostics.Process.Start(psi)
        ?? throw new InvalidOperationException("reg.exe 起不来");
    string stdout = proc.StandardOutput.ReadToEnd();
    string stderr = proc.StandardError.ReadToEnd();
    proc.WaitForExit();
    if (proc.ExitCode != 0)
        throw new InvalidOperationException($"reg {string.Join(' ', args)} 失败 ({proc.ExitCode}): {stderr}{stdout}");
}
static string Hex(byte[] b) => Convert.ToHexString(b);

// Cabinet decompression is now first-party (Source/Managed/OcidCabinet): raw DEFLATE for MSZIP
// folders, LZX for every window size Windows images use. makecab.exe is the closest thing to a
// reference encoder on a stock machine, so the fixture is generated with it rather than checked
// into the repo: the blob spans many CFDATA blocks, which is exactly the case where MSZIP must
// carry its 32 KiB history across block boundaries (a decoder that resets per block fails here).
static void CabinetDecompression()
{
    string root = NewTempDirectory();
    try
    {
        string srcDir = Path.Combine(root, "src");
        string cabDir = Path.Combine(root, "cab");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(cabDir);

        var random = new Random(20261019);
        byte[] blob = new byte[3 * 1024 * 1024 + 517];
        for (int i = 0; i < blob.Length; i++)
        {
            blob[i] = i % 97 < 90 ? (byte)(i % 251) : (byte)random.Next(256);
        }

        string text = string.Concat(Enumerable.Repeat(
            "cabinet fixture: MSZIP keeps its 32 KiB window across CFDATA blocks.\n", 4000));
        var files = new Dictionary<string, byte[]>
        {
            ["empty.bin"] = Array.Empty<byte>(),
            ["text.bin"] = System.Text.Encoding.UTF8.GetBytes(text),
            ["blob.bin"] = blob,
        };
        foreach (var (name, data) in files)
        {
            File.WriteAllBytes(Path.Combine(srcDir, name), data);
        }

        string listings = string.Join("\r\n", files.Keys.Select(name => $"\"{Path.Combine(srcDir, name)}\""));
        foreach (var (type, memory) in new[] { ("MSZIP", 21), ("LZX", 15), ("LZX", 17), ("LZX", 20), ("LZX", 21) })
        {
            string stem = $"{type.ToLowerInvariant()}{memory}";
            string ddf = Path.Combine(root, stem + ".ddf");
            File.WriteAllText(ddf,
                $".Set CabinetNameTemplate={stem}.cab\r\n" +
                ".Set Cabinet=on\r\n" +
                ".Set Compress=on\r\n" +
                $".Set DiskDirectoryTemplate={cabDir}\r\n" +
                $".Set CompressionType={type}\r\n" +
                $".Set CompressionMemory={memory}\r\n" +
                listings + "\r\n");

            RunTool("makecab.exe", $"/F \"{ddf}\"", root);

            string cabPath = Path.Combine(cabDir, stem + ".cab");
            True(File.Exists(cabPath), $"makecab did not produce {cabPath}");

            string outDir = Path.Combine(root, "out-" + stem);
            Directory.CreateDirectory(outDir);
            Cabinet.CabinetExtractor.ExtractCabinet(cabPath, outDir);

            foreach (var (name, data) in files)
            {
                byte[] actual = File.ReadAllBytes(Path.Combine(outDir, name));
                True(actual.AsSpan().SequenceEqual(data),
                    $"{stem}: {name} does not match the source ({actual.Length} vs {data.Length} bytes)");
            }

            Equal(files.Count, Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Length);
        }
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); } catch { /* temp cleanup is best effort */ }
    }
}

// The desktop services extract cabinets in-process now (Services/CabExtraction.cs). This guard
// compares that path with the Windows cabinet API via expand.exe on real packages; the corpora
// live outside the repository, so the test is opt-in through environment variables and prints a
// skip line otherwise. OCID_UNSUPPORTED_CAB points at a package the in-process reader must refuse
// (a WU container cabinet, or a sparse one) so callers fall back to expand.exe instead of getting
// silently renamed/truncated payloads.
static void CabExtractionAgainstExpand()
{
    string? cabDir = Environment.GetEnvironmentVariable("OCID_CAB_TEST_DIR");
    string? unsupportedCab = Environment.GetEnvironmentVariable("OCID_UNSUPPORTED_CAB");

    if (string.IsNullOrEmpty(cabDir) && string.IsNullOrEmpty(unsupportedCab))
    {
        Console.WriteLine("  (skipped: set OCID_CAB_TEST_DIR / OCID_UNSUPPORTED_CAB to run against real packages)");
        return;
    }

    if (!string.IsNullOrEmpty(unsupportedCab) && File.Exists(unsupportedCab))
    {
        True(!CabExtraction.IsSupported(unsupportedCab),
            $"package should be reported as unsupported: {unsupportedCab}");
        Throws<NotSupportedException>(() => CabExtraction.List(unsupportedCab));
        Throws<NotSupportedException>(() => CabExtraction.ReadEntry(unsupportedCab, "0"));
    }

    if (string.IsNullOrEmpty(cabDir) || !Directory.Exists(cabDir)) return;

    int compared = 0;
    foreach (string cab in Directory.EnumerateFiles(cabDir, "*.cab")
                                 .OrderBy(f => new FileInfo(f).Length))
    {
        // Container cabs (WU payloads with a cix manifest) are handled by expand.exe only.
        if (!CabExtraction.IsSupported(cab)) continue;

        string root = NewTempDirectory();
        try
        {
            string ours = Path.Combine(root, "ours");
            string theirs = Path.Combine(root, "expand");
            Directory.CreateDirectory(ours);
            Directory.CreateDirectory(theirs);

            CabExtraction.ExtractAll(cab, ours);
            RunTool("expand.exe", $"-F:* \"{cab}\" \"{theirs}\"", root);

            var expected = HashTree(theirs);
            var actual = HashTree(ours);
            True(actual.Count > 0, $"no files extracted from {cab}");
            Equal(expected.Count, actual.Count);
            foreach (var (path, hash) in expected)
            {
                True(actual.TryGetValue(path, out string? got), $"{Path.GetFileName(cab)}: missing {path}");
                Equal(hash, got);
            }

            compared++;
            if (compared == 3) break;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp cleanup is best effort */ }
        }
    }

    True(compared > 0, $"no cabinet under {cabDir} is handled by the in-process reader");
}

static Dictionary<string, string> HashTree(string root)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
    {
        string rel = Path.GetRelativePath(root, file);
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var fs = File.OpenRead(file);
        map[rel] = Convert.ToHexString(sha.ComputeHash(fs));
    }
    return map;
}

static void RunTool(string fileName, string arguments, string workingDirectory)
{
    var psi = new System.Diagnostics.ProcessStartInfo(fileName, arguments)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
        WorkingDirectory = workingDirectory,
    };
    using var proc = System.Diagnostics.Process.Start(psi)
        ?? throw new InvalidOperationException($"{fileName} 起不来");
    string stdout = proc.StandardOutput.ReadToEnd();
    string stderr = proc.StandardError.ReadToEnd();
    proc.WaitForExit();
    if (proc.ExitCode != 0)
        throw new InvalidOperationException($"{fileName} {arguments} 失败 ({proc.ExitCode}): {stderr}{stdout}");
}

static string NewTempDirectory()
{
    var path = Path.Combine(Path.GetTempPath(), "ocid-offline-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
}

static void True(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
{
    if (!expected.SequenceEqual(actual)) throw new InvalidOperationException("Sequences differ.");
}

static TException Throws<TException>(Action action) where TException : Exception
{
    try { action(); }
    catch (TException ex) { return ex; }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static async Task<TException> ThrowsAsync<TException>(Func<Task> action) where TException : Exception
{
    try { await action(); }
    catch (TException ex) { return ex; }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}
