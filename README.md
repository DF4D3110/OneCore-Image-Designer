# OneCore Image Designer

OneCore Image Designer is a Windows WPF workbench for preparing Windows Core OS images and related servicing workflows. It provides project-based OEMInput authoring, device-layout work, image build/reconstruction tools, FFU browsing, and independent UUP and SxS pages.

The UI follows the compact, light ICD workspace style. This is an independent implementation; it does not redistribute ICD binaries or claim to be an official Microsoft product.

## Features

- **OEMInput designer** — discover FM and DeviceLayout inputs, edit V1/V2 documents, validate against the bundled schemas, and save `.cwproj` projects. Validation is era-aware: an optional target build (auto-detected from the selected MSPackageRoot path) selects the matching official OEMInput XSD family among 11 vendored schemas (V1 six generations 10527→18963→frozen, V2 four: 18963 transitional / 18985–19041 / 20348–22000 / 22621), grouped from hashing featureapi.dll across all 41 ADK builds; plus field-era gating warnings (AppXOptionalPackages 17047+, MachineInfoFile 17666+, TestContent/DevicePlatformIDs/V2 18963+, V2 Obsolete fields) from decompiled OEMInput.cs of 12 representative builds.
- **Device layout** — inspect and author layouts, including period-specific package/manifest generation rules. Validation rule sets were re-extracted from all 41 ADK `DeviceLayoutValidation.dll` assemblies (IL-decompiled embedded XML, deduplicated by content hash): `Assets\ValidationRules\{ADK build}\` directories named by family-representative build (14349/14986/16288/17040/17095/17666/17704/17733/18272/18298/18327/19023), plus `10527` (native DLL family) and `phone` (early phone-only rules from the ADK validation-manifest cab and phone samples). The dropdown therefore matches the ADK build you download. The layout-creation wizard additionally builds BSP companion components (individually or bundled with the layout, `Services/BspComponentBuilderService.cs`): OEMDevicePlatform package (modern standalone package + `OEMDEVICEPLATFORM_*` FIP, or Phone-era `$(runtime.windows)\ImageUpdate` placement), DeviceInfo registry package (SiliconInformation/ManufacturerInformation + `DEVICE_*` FIP), `machineinfo.xml` (gated with a 17666+ era hint), and IoT Custom.SMBIOS source package (wm.xml + CFG for PkgGen); a unified FM merges layout + component sections with the `Devices` binding. All manifest/mum/FIP templates are field-for-field from real samples (QEMU ARM64 20279 / 15254 QC8960 / QCDB410C), verified by a headless smoke test reproducing sample keyform filenames, update.mum structure and update-name hashes. Driver handling (`Services/DriverPackageBuilder.cs`): recursively scan a directory for `*.inf`, group by containing directory, and emit one PkgGen source package per group — `identity`/`drivers`/`inf` wm.xml per the addonkit IoTWMWriter and real NXP imx-iotcore BSP samples, with configurable package namespace (default `Drivers`, vendor style e.g. `iMX`) and FeatureID policy (per-package `DRIVERS_<INF>` addonkit style, or a shared group like `IMX_DRIVERS` NXP style); optionally invoke the ADK's `PkgGen.exe` (auto-discovered from installed toolsets, with `makecat.exe` PATH injection) to build real cabs directly — where signing fails without test certificates the wizard reports an actionable hint and the wm.xml source packages remain addonkit-buildable. Successful packs clean their staging directories and `.cdf` intermediates so the output holds only final cabs plus FM/machineinfo/SMBIOS sources.
- **Image workflows** — invoke configured image-app/ADK tools to build images, reconstruct system packages, and browse FFU contents. Long-running work (component extraction, UUP downloads, external driver packaging) streams its live log into a shared task-log window (`Views/TaskLogWindow` + `Services/TaskLogSink`): a worker-thread queue drained on a 400 ms batch timer, so the main UI never blocks on per-line dispatcher calls; logs also persist to temp files with copy/open controls. In the selection-based reconstruction view, package-list FM/OEMInput entries and any file-tree node support double-click XML preview (`Views/XmlPreviewWindow`, indented + syntax-colored, binary files summarized, 2 MB truncation guard, read directly from the mounted image partitions). Driver scanning marks third-party driver packages (INF base names not starting with `microsoft`, the inbox-vs-OEM naming rule verified against 22621 driver packages) with a dedicated "第三方驱动" filter, and an "外部驱动打包" action scans an arbitrary folder of INF directories, generating PkgGen source packages (wm.xml) or full cabs via the installed ADK's PkgGen.
- **UUP update query and download** — query Microsoft UUP by SKU, architecture, ring, branch, and release filter; choose a returned update, save/load snapshots, and download the selected update using the vendored UUPMediaCreator core.
- **SxS source tools** — browse GPL-isolated pureSxS/explodeSxS source and run a non-executing preflight/preview of a selected image and output directory. Missing CBS binaries and the upstream `prepare.cmd` X: format command block operations; this app never runs the upstream scripts.
- **Offline utilities** — registry, driver, firmware, WinSxS hash, dependency, and component-store tools.
- **Bundled addonkit** — Microsoft's `iot-adk-addonkit` (17763-v7, PowerShell IoT Core imaging toolchain) ships in-repo under `addonkit/` and is copied to the output directory on build. Feature → "addonkit 工作区" opens a dedicated page: module/ADK status, sample workspace discovery of products/BSPs, a preflight check (bundled module, workspace XML, IoT Core Kits registry for 17763, MSPkgRoot/KITSROOT reachability, installed ADK build, Retail signing parameters) that gates commands unless explicitly overridden, and elevated one-click commands (build packages, build FFU, build recovery, import sample packages) with live transcript logging. Tools → "ADK 工具下载 / addonkit…" remains for downloading tool zips and launching the interactive shell.
- **ADK tool download** — per-build ADK tool zips (imageapp.exe, imggen.cmd, etc.) are hosted on this repository's GitHub Release (tag `adktool`); the in-app manager lists them, downloads the selected build, verifies the SHA256 digest provided by the GitHub asset API, and extracts to `addonkit\adk\<build>\`. Downloads use a hardened HTTP client (explicit TLS 1.2/1.3, direct connection bypassing system proxy, 20 s connect timeout) with up to 3 attempts and exponential backoff on transient network/5xx failures, and translate socket/TLS exceptions into actionable Chinese diagnostics in the download log. The Image Build page auto-discovers the tool matching the OS package build number from this directory.
- **updateapp update orchestration** — a start-page tile and Feature → "updateapp 更新调度" open a page that dispatches the version-specific `updateapp.exe` from the downloaded ADK build. The command list is rendered dynamically per build from the official usage text embedded in each `updateapp.exe` (extracted once from IDA decompilations of 40 builds, 10527–22621, and vendored as `Services/UpdateAppUsageCatalog.cs`): old builds expose `installfromsd`/`stagetosd`/`stageinproc`/`converttocbs`, newer builds expose `finalizecommit`/`rollback`/`getfeatures`/`creatediffhives`, and 22000+ add DISM-style `/image:` switches; each command shows its official argument template, category, and risk level. Execution targets are either an offline image (VHD/VHDX/FFU for `mountandinstall`, package/actionlist directories for install/stage families) or the live running system (explicitly warned). Commands run elevated through the same PowerShell transcript-capture runner as addonkit, with live log streaming, cancellation, and destructive-command confirmation.

UUP-to-installation-media conversion is a separate optional workflow on the UUP page. It launches a user-selected upstream `UUPMediaConverter.exe`, displays its stdout/stderr progress, supports cancellation, and checks that a .NET 10 runtime is available, the local UUP set is non-empty, the requested ISO does not already exist, the output parent exists, the temp folder exists and is empty, and destination/temp volumes have conservative free space (max of 20 GiB or twice the input size per destination; requirements are summed when both destinations share a drive). Preflight size enumeration/runtime detection run asynchronously from the UI, with a bounded runtime-probe timeout. The converter requires an explicit start confirmation, uses upstream `--no-key-prompt`, and is not bundled or launched during tests. The upstream [`OSTooling/UUPMediaCreator`](https://github.com/OSTooling/UUPMediaCreator) source is MIT-licensed. Its UUPMediaConverter targets .NET 10 and references Media.Creator, Imaging, Common.Messaging, Microsoft.Dism, Microsoft.Wim, Privileges, Media.Creator.Planning, TempManager, VirtualHardDiskLib, CompDB and Cabinet projects, plus DiscUtils, ManagedWimLib, ini-parser, and other NuGet dependencies. A no-restore Release build of the supplied converter checkout failed with 18 unresolved DiscUtils/ManagedWimLib errors; this confirms the dependency assets are not presently resolved, not that the source is absent. The closure also contains `cdimage.exe`; its independent binary provenance/redistribution terms have not been reviewed. Consequently no upstream source closure or converter/CDImage binary is redistributed here; users must provide a separately built converter and its required .NET 10 runtime. Upstream documents that update packages are not integrated into the resulting ISO.

UUP queries, snapshots, payload association, downloaded folders, converted ISOs, and generated image/CBS output paths are registered as artifacts; the registry is in-memory for the current application session. Registered artifacts receive basic existence/snapshot-payload preflight.

SxS preflight previews selected input/output paths and required script availability without creating output directories, mounting images, or executing scripts. It reports the `prepare.cmd` `format X:` operation and remains blocked because `SxSFounder`/`CbsExploder` binaries are absent and their independent licensing/build provenance is unresolved. No automatic formatting, even with confirmation, is implemented.

## Self-contained servicing stack (in progress)

The long-term goal is to assemble a Windows image from a package set **without depending on the operating system's own servicing components**. Upstream that work is performed by `CbsCore.dll` (CBS), `wcp.dll` (WCP/SMI), the CMI engine, the CSI/DMI providers and KTM. Those components are architecture-specific, version-locked, and partly encumbered by independent licenses, so OCID is re-implementing the behaviour it needs as project-owned C++.

Implemented so far under `Source/Native/OcidServicing/`:

| Module | Responsibility |
| --- | --- |
| `Identity` | Assembly identity: the canonical `name~version~arch~token~language` string, `versionScope` handling, the forced `0.0.0.0` version of non-SxS packages, and component-name derivation including Windows' long-name abbreviation |
| `Manifest` | Self-contained pull parser for component manifests, reading both registry declaration mechanisms (`registryKeys` and `configurationSchema`) |
| `WcmSchema` | WCM (`smiengine`) schema declarations: `wcm:handler` / `wcm:legacyName` / `wcm:legacyType` / `default` |
| `RegistryValue` | Registry value encoding (text to raw bytes) and type-correct merging |
| `CmiInstaller` | CMI write decisions: `owner`, `operationHint`, `InternalInstall`, and the per-type merge rules |

Build the native library, the console entry point, and the native tests:

```powershell
.\Source\Native\build-servicing.ps1
```

The same code backs the console tool and the application. The managed side binds through `Services/OcidServicingNative.cs` and applies the decisions in `Services/OcidSmiService.cs`. The application never falls back to a managed re-implementation of the value encoding: when the native library is unavailable it skips the operation and reports it, because a silently wrong byte image is worse than a reported failure.

`Source/Native/OcidRegHook/` is a diagnostic IAT-hooking DLL used while researching how the servicing stack writes to offline hives. It is a research aid, not part of the shipping path.

## Requirements

- Windows 10/11.
- .NET 8 SDK to build the base WPF application/UUP bridge. The external UUPMediaConverter workflow requires a .NET 10 runtime; the base app itself does not require .NET 10.
- Python is required to run the included pureSxS scripts manually.
- Some image and CBS operations require ADK/DISM, administrator permissions, an image source, or additional licensed tools. The required tools depend on the selected workflow.

UUP scan and download require internet access and are started only when the user requests them. The application does not initiate a scan or download on launch.

## Build

From the repository root:

```powershell
dotnet build ".\OneCore Image Designer.csproj" --configuration Release
```

The main build also builds the repository-local `UupBridge` and copies its runtime files to the application output.

The self-contained servicing stack is native code and is built separately. It requires Visual Studio 2022 (C++ workload) with a C++ build environment script; the build script cross-compiles both architectures because the application is x64 while the servicing stack it replaces may be x86:

```powershell
.\Source\Native\build-servicing.ps1
```

That produces `Source/Native/Output/x86/` and `Source/Native/Output/x64/` (the `OcidServicing` library, `ocid_manifest.exe`, and the native unit tests). These are build outputs and are excluded from the repository; run the script once after cloning before using the servicing pages. `Services/OcidServicingNative.cs` locates the library relative to the application output, so no copy step is required.

`.\Build-CbsBundle.ps1` assembles the standalone console bundle at `bin\Release\net8.0-windows\CBS\` (the CLI plus its elevation helper). Pass `-NoBuild` to skip rebuilding the CLI.

 No external UUPMediaCreator checkout or .NET 10 runtime is required for the base app. NuGet packages are restored by the SDK in the normal way.

The build also copies the bundled `addonkit/` tree (Microsoft iot-adk-addonkit 17763-v7) into the output directory. The copy target is additive: files previously installed under `addonkit\adk\` by the in-app downloader are left in place. The addonkit shell additionally requires the Windows 10 IoT Core Kits (1809 / 10.0.17763.x) and the ADK to be installed on the machine, per the upstream README.

Generated `bin`, `obj`, `build`, `intermediate`, and `Source/Native/Output` trees, debug symbols, and generated Appx files are excluded from the repository; release packages should be attached to GitHub Releases instead. The checked-in `7z.dll` architecture variants, `makeappx.exe` fixture and its support DLLs, and `Tools/wcpex` binaries are retained runtime inputs used by the package-generation sources or embedded by the application, not build outputs.

Run the generated application from:

```text
bin\Release\net8.0-windows\OneCore Image Designer.exe
```

Run the offline regression harness (Windows, .NET 8 SDK; no network/UUP download required):

```powershell
dotnet run --project ".\Tests\OneCore.Image.Designer.OfflineTests.csproj" --configuration Release
```

## Repository layout

```text
Assets/                 UI assets, templates, and validation rules
Models/                 Project and domain models
Services/               Image, project, UUP, SxS, and validation services
ViewModels/              WPF view models
Views/                   WPF pages and dialogs
Schemas/                 OEMInput schemas
MobilePackageGen/         Package-generation libraries
UupBridge/               .NET 8 adapter over the vendored UUP core
UupShared/               Offline UUP query parsing and payload contracts shared by app and bridge
Tests/                   Offline regression harness for UUP, SxS safety, artifacts, XML generations, and the native servicing library
Source/Native/            Project-owned C++ servicing stack (OcidServicing), its console tool and tests, plus the diagnostic registry hook
Reference/               Reverse-engineering scripts used to derive servicing behaviour from the upstream binaries
Tools/                    Vendored helper binaries (wcpex and its wcp.dll), used to decompress DCM-compressed manifests
ThirdParty/UUPMediaCreator/  Vendored upstream UUP source closure
ThirdParty/SxS/           Isolated GPL-3 pureSxS and explodeSxS sources
addonkit/                 Bundled Microsoft iot-adk-addonkit 17763-v7 (PowerShell toolchain + sample workspace); downloaded ADK tool sets land under addonkit/adk/<build>/ at runtime
```

## Third-party code and licensing

The repository root `LICENSE` remains the project-level MIT license; it does not relicense vendored software.

- UUPMediaCreator is MIT-licensed. Its original license and README are retained under `ThirdParty/UUPMediaCreator/`.
- The vendored Cabinet LZX decoder is dual-licensed under LGPL-2.1 or MS-PL; its original header and license notice are retained.
- pureSxS and explodeSxS are separate GPL-3.0 components, each retaining its own `LICENSE` and upstream README. Their source files are not represented as MIT-licensed project code.
- `addonkit/` is an unmodified copy of Microsoft's open-source `iot-adk-addonkit` (release 17763-v7), MIT-licensed; its original `LICENSE` is retained at `addonkit/LICENSE`. The sample test certificates and `.pfx` files inside it are Microsoft's publicly published sample keys shipped with the official toolkit, not private credentials.
- `CbsExploder` and `SxSFounder` binaries are excluded because their independent redistribution terms have not been established. Microsoft package samples from the input SxS directory are not included.

See [`ThirdParty/THIRD-PARTY-NOTICES.md`](ThirdParty/THIRD-PARTY-NOTICES.md) for the component inventory and provenance details. Preserve the per-component notices when redistributing the repository. This inventory records source boundaries; it is not a legal opinion on distribution of a combined work.

## Roadmap implementation status

1. **Offline tests — implemented.** `Tests/` covers UUP query parsing, snapshot round-trip, payload lookup/path traversal rejection, bridge cancellation/error propagation, conversion input/output/tool validation, overwrite protection, combined same-drive space checks, safe CLI argument construction (including disabling the upstream keypress prompt), artifact preflight, the SxS no-execution safety gate, and XML capability detection for IoT Core 17763/Core OS 20348 shapes. No UUP content is downloaded or converted by tests.
2. **UUP media conversion — optional external-tool workflow implemented.** The app provides an observable, cancellable launcher and input/output/tool/free-space checks. The converter source exists locally but is not vendored because its .NET 10 dependency closure is unresolved in the supplied checkout (`DiscUtils` and `ManagedWimLib` missing during no-restore build) and the `cdimage.exe` redistribution provenance has not been reviewed. The user must provide a separately built converter/runtime. No converter executable is shipped or conversion performed during validation.
3. **Safe SxS workflow — preview implemented; image operation blocked.** The SxS page presents the chosen image/output paths and a read-only preflight. It does not execute scripts or create outputs. Upstream `prepare.cmd` formats `X:`; no automated volume format path exists. `CbsExploder`/`SxSFounder` binaries and license evidence are absent.
4. **CBS licensing/provenance — unresolved and not distributed.** Local notices identify GPL-3.0 pureSxS/explodeSxS source, but do not establish independent terms or build provenance for the referenced CBS executables; no external sources were consulted. Resolve this with authoritative source/license/build records before adding either binaries or source.
5. **Artifacts and generation regression — partial.** UUP snapshots/download folders and generated media/CBS output paths are registered in a shared in-process registry; a unified basic existence/snapshot-payload preflight is available. Tests exercise representative XML shapes, not generation builds against real OS/ADK packages. Genuine licensed representative OS/ADK samples and generation-specific build tools are not available in this repository, so end-to-end generation regression remains blocked.
6. **Self-contained servicing stack — in progress.** `Source/Native/OcidServicing/` re-implements the servicing behaviour OCID needs — assembly identity, manifest parsing, WCM schema handling, registry value encoding, and CMI write decisions — as project-owned C++, exposed to the application through a small C ABI. The upstream dependencies named in items 3 and 4 are being removed module by module rather than redistributed: the goal is that assembling an image from a package set no longer requires the operating system's own servicing binaries.

## About

The Help → About entry opens the Windows-provided Shell About dialog through `rundll32 shell32.dll,ShellAbout`; the application does not display a separately authored About description.
