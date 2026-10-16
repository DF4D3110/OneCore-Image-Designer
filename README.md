# OneCore Image Designer

![OneCore Image Designer](Assets/banner.png)

OneCore Image Designer (OCID) is an independent Windows workbench for preparing and servicing
Windows Core OS / OneCore / IoT Core images. It covers project-based OEMInput authoring, device
layout authoring, image build, reconstruction of a system package set from an image, FFU browsing,
Microsoft UUP query/download, an addonkit front end, and a set of offline registry, driver,
firmware and component-store tools.

It is **not** a Microsoft product and is not affiliated with Microsoft. It does not redistribute
the Windows servicing stack it is replacing (`CbsCore.dll`, `wcp.dll`, CMI/CSI/DMI, KTM), nor any
Microsoft package sample. A small number of Microsoft utility binaries are retained as runtime
inputs — the `Tools/wcpex` pair used to decompress DCM-compressed manifests, the `makeappx.exe`
fixture used by the package-generation sources, and the tools shipped inside the vendored
`addonkit/` — see [Third-party code and licensing](#third-party-code-and-licensing). Workflows that
need Microsoft tooling otherwise use the ADK, DISM or Windows 10 IoT Core Kits installation already
present on the machine; the parts of the servicing stack this project needs are being
re-implemented as project-owned C++ (see
[Self-contained servicing stack](#self-contained-servicing-stack-in-progress)).

The application UI is in Chinese.

## Requirements

- Windows 10 / 11, x64.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) — builds the WPF application, the
  `UupBridge` helper, and the offline test harness.
- Optional, depending on the workflow used:
  - Administrator rights for anything that mounts, services or writes an image.
  - Windows ADK (Deployment Tools) for the image-build and package-generation workflows; the ADK
    build to use is selectable in-app.
  - Windows 10 IoT Core Kits 1809 (10.0.17763.x) for the bundled addonkit shell, per the upstream
    addonkit README.
  - An image source: FFU, VHD/VHDX, WIM, or a mounted directory.
  - Visual Studio 2022 with the C++ workload to build the native servicing stack.
- The optional UUP-to-ISO workflow needs a separately built `UUPMediaConverter.exe` **and** a
  .NET 10 runtime. Neither is shipped here (see [Third-party code and licensing](#third-party-code-and-licensing)).

UUP queries and downloads run only when the user asks for them; nothing is queried or downloaded at
startup.

## Build and run

```powershell
git clone https://github.com/DF4D3110/OneCore-Image-Designer.git
cd OneCore-Image-Designer
```

Build and launch the application from the repository root. This also builds the repository-local
`UupBridge` and copies its runtime files into the application output.

```powershell
dotnet build ".\OneCore Image Designer.csproj" --configuration Release
bin\Release\net8.0-windows\OneCore Image Designer.exe
```

The native servicing stack is separate and needs the Visual Studio C++ build environment. It
cross-builds both architectures on purpose: the application is x64, while the servicing binaries it
replaces are frequently x86.

```powershell
.\Source\Native\build-servicing.ps1
```

That writes `Source/Native/Output/x86/` and `Source/Native/Output/x64/` — the `OcidServicing`
library, the `ocid_manifest.exe` console tool, and the native unit tests. These are build outputs
and are not checked in: run the script once after cloning before using the servicing pages.
`Services/OcidServicingNative.cs` locates the library relative to the application output, so no
copy step is involved.

Assemble the standalone CBS console bundle (the CLI plus its elevation helper):

```powershell
.\Build-CbsBundle.ps1          # add -NoBuild to reuse an existing CLI build
# -> bin\Release\net8.0-windows\CBS\
```

Run the offline regression harness (no network, no UUP download):

```powershell
dotnet run --project ".\Tests\OneCore.Image.Designer.OfflineTests.csproj" --configuration Release
```

The main build also copies the bundled `addonkit/` tree into the output directory. That copy is
additive — anything the in-app downloader has already placed under `addonkit\adk\` is left in place.

## Application overview

| Area | View | What it does |
| --- | --- | --- |
| Start | `Views/StartPage.xaml` | Entry point; a tile per workflow, plus recent-artifact shortcuts. |
| OEMInput | `Views/OEMInput/` | Discover FM and DeviceLayout inputs, edit V1/V2 OEMInput documents, validate them against the bundled schemas, and save `.cwproj` projects. |
| Device layout | `Views/DeviceLayout/` | Inspect and author device layouts, generate the companion BSP components, and build PkgGen driver packages from a directory of INFs. |
| Image build | `Views/ImageBuild/` | Drive the selected ADK build's `imageapp.exe` / `imggen.cmd`, discovering the tool set that matches the OS package build number. |
| Reconstruction | `Views/SystemReconstruct/` | Rebuild a system package set from an image, with per-node XML preview. |
| CBS | `Views/Cbs/` | CBS servicing against a mounted image. |
| FFU browser | `Views/FFUBrowser/` | Browse FFU partitions, with a hex viewer. |
| updateapp | `Views/UpdateAppView.xaml` | Dispatch the version-specific `updateapp.exe` commands, rendered per build from that build's official usage text, with per-command argument template, category and risk level. |
| UUP | `Views/UupView.xaml` | Query Microsoft UUP by SKU/architecture/ring/branch, pick an update, save and reload snapshots, download it, and optionally convert to ISO through an external converter. |
| SxS | `Views/SxsView.xaml` | Non-executing preflight/preview of a selected image and output directory. It runs no scripts and creates no output. |
| addonkit | `Views/AddonKitView.xaml` | Microsoft `iot-adk-addonkit` front end: module/ADK status, sample workspace discovery, preflight gating, and elevated one-click build commands with live transcript logging. |
| Tools | `Views/Tools/` | Offline registry editor, driver extractor, firmware parser, WinSxS hash calculator, dependency viewer, component-store verifier, and the ADK tool downloader. |

Cross-cutting behaviour:

- **Long-running work never blocks the UI.** Task output streams into a shared log window
  (`Views/TaskLogWindow.xaml`) through a worker-thread queue drained on a batch timer instead of a
  dispatcher call per line; logs are also persisted to temp files with copy/open controls.
- **Elevation is explicit.** Operations that need it are launched through the bundled NSudo helper
  (`Services/ElevationHelper.cs`) rather than requiring the whole application to run elevated.
- **Destructive commands require confirmation** and are labelled by risk in the UI.

## How it is put together

- `OneCore Image Designer.csproj` — the WPF application (`net8.0-windows`, x64): MVVM with
  `ViewModels/` + `Views/`, domain models in `Models/`, and the workflow logic in `Services/`.
- `Services/` — ~75 single-purpose services (image build, CBS and SxS servicing, drivers, firmware,
  component store, offline registry, UUP, addonkit, validation, logging). Most user-visible
  behaviour lives here; the views stay thin.
- `UupShared/` — offline UUP query parsing and payload contracts, shared by the app and the bridge.
- `UupBridge/` — a separate `net8.0` adapter executable over the vendored UUP core; the application
  builds it and copies it into the output rather than referencing it directly.
- `MobilePackageGen/` — the package-generation toolchain (CLI, WinForms GUI, FFU/VHDX/WIM/raw-FS/
  storage-pool adapters, CAB and DCM compression, cat/signature rebuilding, the `CbsxCli` and
  `SxSFounderCli` command-line tools). It has its own README at `MobilePackageGen/readme.md`.
- `Source/Native/` — the project-owned C++ servicing stack and the diagnostic registry hook.
- `ThirdParty/` — the vendored UUP source closure and the consolidated third-party notice.
- `Schemas/`, `Assets/` — the OEMInput XML schemas and the UI assets, project templates and
  device-layout validation rule sets.

### Era-aware OEMInput validation

OEMInput documents are validated with the schema family that matches the target OS build: 11
schemas are bundled (`Schemas/OEMInput.V1.*.xsd`, `V2.*.xsd`) and the optional target build —
auto-detected from the selected package root when possible — selects one of them. Field-level era
gating adds warnings for fields that do not exist in every generation (AppX optional packages,
MachineInfoFile, TestContent/DevicePlatformIDs, V2-only and V2-obsolete fields). The device-layout
rule sets under `Assets/ValidationRules/` are likewise stored per ADK build family, so the rule set
matches whichever ADK the user has installed.

## Self-contained servicing stack (in progress)

The long-term goal is to assemble a Windows image from a package set **without depending on the
operating system's own servicing components**. Upstream that work is done by `CbsCore.dll` (CBS),
`wcp.dll` (WCP/SMI), the CMI engine, the CSI/DMI providers and KTM. Those components are
architecture-specific, version-locked, and partly encumbered by independent licenses, so OCID
re-implements the behaviour it needs as project-owned C++ under `Source/Native/OcidServicing/`, and
exposes it to the application through a small C ABI.

| Module | Responsibility |
| --- | --- |
| `Identity` | Assembly identity: the canonical `name~version~arch~token~language` string, `versionScope` handling, the forced `0.0.0.0` version of non-SxS packages, and component-name derivation including Windows' long-name abbreviation rule. |
| `Manifest` | Self-contained pull parser for component manifests: identity, both registry declaration mechanisms (`registryKeys` and `configurationSchema`), security descriptor definitions, trust info and directories. |
| `WcmSchema` | WCM (`smiengine`) schema declarations — `wcm:handler`, `wcm:legacyName`, `wcm:legacyType`, `default` — and the key-level attributes (`securityDescriptor`, `perUserVirtualization`, `mapDesiredAccessToMaxAllowedAccess`). |
| `RegistryValue` | Registry value encoding (declaration text to raw bytes, per value type) and type-correct merging. |
| `CmiInstaller` | CMI write decisions: `owner`, `operationHint` (replace / append / prepend), `InternalInstall`, and the per-type merge rules. |
| `MacroExpander` | Expansion of `$(runtime.*)` path macros and `$(runtime.host.*)` registry-root macros, including WOW64 variants and unresolved-macro reporting. |
| `SecurityDescriptor` | SDDL resolution and description, `(inherit)` pass-through, and SDDL ↔ self-relative binary conversion. |

The same code backs both the console tool and the application. The managed side binds through
`Services/OcidServicingNative.cs` and applies the decisions in `Services/OcidSmiService.cs`. The
application deliberately never falls back to a managed re-implementation of value encoding: when the
native library is unavailable it skips the operation and reports it, because a silently wrong byte
image is worse than a reported failure.

`Source/Native/OcidServicing/tools/ocid_manifest.cpp` builds `ocid_manifest.exe`, a console entry
point for inspecting and exporting the same decisions (`info`, `ops`, `scan`, `export-ops`,
`verify-names`, `expand`, `macros`, `dump-xsd`), used for corpus-wide verification.

`Source/Native/OcidRegHook/` is a separate x86 DLL that patches `wcp.dll`'s import table so the
registry calls the upstream servicing stack makes can be observed — and optionally redirected to an
offline hive — instead of hitting the host registry. It is loaded only by the legacy
`wcp.dll`-driven CBS path, is off-limits to the target design, and defaults to log-only
(`OCID_REGHOOK`, `OCID_REGHOOK_LOG`, `OCID_REGHOOK_MAP`, `OCID_REGHOOK_REWRITE`). It is an interim
research instrument, not part of the self-contained servicing stack.

## Repository layout

```text
Assets/                   UI assets, templates, and per-ADK-build device-layout validation rules
Models/                   Domain and project models
Services/                 Workflow services (image, CBS, SxS, drivers, firmware, offline registry, UUP, validation)
ViewModels/               WPF view models
Views/                    WPF pages, steps and dialogs
Schemas/                  Bundled OEMInput V1/V2 schemas
MobilePackageGen/         Package-generation toolchain, CLIs and GUI (own README)
UupBridge/                Separate .NET 8 adapter over the vendored UUP core
UupShared/                Offline UUP query parsing and payload contracts shared by app and bridge
Tests/                    Offline regression harness
Source/Native/OcidServicing/  Project-owned C++ servicing stack: library, console tool and native tests
Source/Native/OcidRegHook/    Diagnostic registry interception DLL for the legacy CBS path
Tools/wcpex/              Vendored wcpex.exe + wcp.dll, used to decompress DCM-compressed manifests
ThirdParty/               Vendored UUP source closure and the third-party notice inventory
addonkit/                 Bundled Microsoft iot-adk-addonkit 17763-v7 (PowerShell toolchain + sample workspace)
Build-CbsBundle.ps1       Assembles the standalone CBS console bundle
```

Generated `bin`, `obj`, `build`, `intermediate` and `Source/Native/Output` trees, debug symbols and
generated Appx files are not tracked; release packages belong on GitHub Releases. The checked-in
`7z.dll` architecture variants, the `makeappx.exe` fixture and its support DLLs, and `Tools/wcpex`
are retained **runtime** inputs (consumed by the package-generation sources or embedded into the
application), not build outputs.

The per-build ADK tool zips consumed by the in-app downloader (`addonkit\adk\<build>\`) are hosted
as assets on this repository's GitHub Release tagged `adktool`.

## Tests

`Tests/` is an offline regression harness (`Tests/Program.cs`, no network, no image required). It
groups checks for UUP query parsing and snapshot round-trip, payload lookup and path-traversal
rejection, bridge cancellation and error propagation, conversion input/output/tool validation,
overwrite protection, same-drive free-space accounting, safe CLI argument construction, artifact
preflight, the SxS no-execution gate, XML capability detection, and the managed-to-native servicing
bindings.

The native modules have their own assertion suites, built and run by `build-servicing.ps1`:
`identity`, `manifest`, `wcm`, `registryvalue`, `cmiinstaller`, `macroexpander`,
`securitydescriptor`.

## Third-party code and licensing

The repository root `LICENSE` (MIT) applies to this project's own code only; it does not relicense
vendored software. Preserve the per-component notices when redistributing.

- **[UUPMediaCreator](https://github.com/OSTooling/UUPMediaCreator)** — MIT. Its original license and
  upstream README are retained under `ThirdParty/UUPMediaCreator/`. Only the source projects needed
  by `UupBridge` are included; no converter or `cdimage.exe` binary is redistributed.
- **Cabinet LZX decoder** — dual-licensed LGPL-2.1 or MS-PL; the source header and license are
  retained. **SevenZipExtractor / 7-Zip** — LGPL-2.1. **Microsoft.Deployment.Compression** (WiX) —
  MS-RL.
- **`addonkit/`** — Microsoft's open-source `iot-adk-addonkit` (release 17763-v7), MIT; its
  `LICENSE` is retained at `addonkit/LICENSE`. The sample certificates and `.pfx` files inside it
  are Microsoft's publicly published sample keys shipped with the official toolkit, not private
  credentials.
- **`MobilePackageGen/`** — a fork/rework of WSK Tools v1.0.4 and upstream MobilePackageGen. Its
  component inventory, including DiscUtils (MIT), NSudo (MIT), Img2Ffu (MIT), StorageSpace (MIT)
  and DotnetPackaging.Msix (MIT), is documented in `MobilePackageGen/readme.md` and
  `MobilePackageGen/license.md`.
- **Tooling binaries** — `Tools/wcpex/` and the package-generation support DLLs are third-party
  binaries retained as runtime inputs; see `ThirdParty/THIRD-PARTY-NOTICES.md`.

Not distributed, and therefore not usable from a clean clone:

- `CbsExploder` and `SxSFounder` executables — their independent redistribution terms have not been
  established. Microsoft package samples are not included either.
- The GPL-3.0 `pureSxS` / `explodeSxS` script trees that were previously vendored under
  `ThirdParty/SxS/` have been **removed** from the repository. The behaviours they documented are
  re-implemented in project code (`Services/PureSxsService.cs`,
  `Services/ExplodeSxSNativeService.cs`, both of which record their provenance in their file
  headers). The SxS page remains a read-only preflight and executes nothing.
- A UUP-to-ISO converter and its .NET 10 runtime — the user must supply a separately built one.

`ThirdParty/THIRD-PARTY-NOTICES.md` is an inventory of provenance, not a legal opinion on
distribution of a combined work.

## Status

This is an active work in progress. Two things are worth stating plainly:

1. **The servicing stack rewrite is incomplete.** The modules listed above are implemented and
   tested, but the application still drives upstream servicing binaries for CBS/CSI work. The
   direction is to replace them module by module rather than to redistribute them, so that
   assembling an image from a package set no longer requires the operating system's own servicing
   components.
2. **Some workflows depend on software the user must provide** — the ADK, the IoT Core Kits, a
   separately built UUP converter, and administrator rights. Where a workflow cannot run it says so
   instead of silently degrading.

## About

The Help → About entry opens the Windows-provided Shell About dialog through
`rundll32 shell32.dll,ShellAbout`; the application does not display a separately authored About
description.
