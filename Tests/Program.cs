using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
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
    ("Ocid CSI copies payload to the store, hardlinks destinations and applies file ACL", () => { OcidCsiFilePlacement(); return Task.CompletedTask; })
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

static string Hex(byte[] b) => Convert.ToHexString(b);

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
