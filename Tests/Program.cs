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
    ("Validation rule families wire-up and hard-intercept templates", () => { RuleFamiliesRegression(); return Task.CompletedTask; })
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
        Directory.CreateDirectory(Path.Combine(sxs, "explodeSxS"));
        var prepare = Path.Combine(sxs, "explodeSxS", "prepare.cmd");
        File.WriteAllText(prepare, "format X: /q /y");
        var image = Path.Combine(root, "source.wim");
        File.WriteAllText(image, "not an actual image");
        var preview = new SxsWorkflowPreflightService().Preview(image, root, sxs);
        True(!preview.Ready, "Unlicensed/missing tools and format script must block SxS operations.");
        True(preview.Inputs.Contains(image), "Preview should include the input image.");
        True(preview.PlannedOutputs.Contains(root), "Preview should include the output directory.");
        True(preview.Checks.Any(check => check.Contains("formatting operation", StringComparison.OrdinalIgnoreCase)
            || check.Contains("formatting", StringComparison.OrdinalIgnoreCase)
            || check.Contains("volume-format", StringComparison.OrdinalIgnoreCase)), "X: format hazard should be reported.");
        True(preview.Checks.Any(check => check.Contains("not be launched", StringComparison.OrdinalIgnoreCase)), "Preview must explicitly state scripts are not launched.");
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
