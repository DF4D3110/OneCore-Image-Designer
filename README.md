# OneCore Image Designer

OneCore Image Designer is a Windows WPF workbench for preparing Windows Core OS images and related servicing workflows. It provides project-based OEMInput authoring, device-layout work, image build/reconstruction tools, FFU browsing, and independent UUP and SxS pages.

The UI follows the compact, light ICD workspace style. This is an independent implementation; it does not redistribute ICD binaries or claim to be an official Microsoft product.

## Features

- **OEMInput designer** — discover FM and DeviceLayout inputs, edit V1/V2 documents, validate against the bundled schemas, and save `.cwproj` projects.
- **Device layout** — inspect and author layouts, including period-specific package/manifest generation rules.
- **Image workflows** — invoke configured image-app/ADK tools to build images, reconstruct system packages, and browse FFU contents.
- **UUP update query and download** — query Microsoft UUP by SKU, architecture, ring, branch, and release filter; choose a returned update, save/load snapshots, and download the selected update using the vendored UUPMediaCreator core.
- **SxS source tools** — browse the included pureSxS and explodeSxS source and workflow scripts. This page checks the bundled script set; it does not currently execute the upstream workflows.
- **Offline utilities** — registry, driver, firmware, WinSxS hash, dependency, and component-store tools.

UUP media conversion is not yet wired into the application. SxS execution is also deliberately not automated: `prepare.cmd` formats `X:`, and the referenced CBS executables are not bundled while their redistribution terms remain unverified.

## Requirements

- Windows 10/11.
- .NET 8 SDK to build; the WPF application and UUP bridge target .NET 8.
- Python is required to run the included pureSxS scripts manually.
- Some image and CBS operations require ADK/DISM, administrator permissions, an image source, or additional licensed tools. The required tools depend on the selected workflow.

UUP scan and download require internet access and are started only when the user requests them. The application does not initiate a scan or download on launch.

## Build

From the repository root:

```powershell
dotnet build ".\OneCore Image Designer.csproj" --configuration Release
```

The main build also builds the repository-local `UupBridge` and copies its runtime files to the application output. No external UUPMediaCreator checkout or .NET 10 runtime is required. NuGet packages are restored by the SDK in the normal way.

Generated `bin`, `obj`, `build`, and `intermediate` trees, debug symbols, and generated Appx files are excluded from the repository; release packages should be attached to GitHub Releases instead. The checked-in `7z.dll` architecture variants, `makeappx.exe` fixture and its support DLLs, and `Tools/wcpex` binaries are retained runtime inputs used by the package-generation sources or embedded by the application, not build outputs.

Run the generated application from:

```text
bin\Release\net8.0-windows\OneCore Image Designer.exe
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
ThirdParty/UUPMediaCreator/  Vendored upstream UUP source closure
ThirdParty/SxS/           Isolated GPL-3 pureSxS and explodeSxS sources
```

## Third-party code and licensing

The repository root `LICENSE` remains the project-level MIT license; it does not relicense vendored software.

- UUPMediaCreator is MIT-licensed. Its original license and README are retained under `ThirdParty/UUPMediaCreator/`.
- The vendored Cabinet LZX decoder is dual-licensed under LGPL-2.1 or MS-PL; its original header and license notice are retained.
- pureSxS and explodeSxS are separate GPL-3.0 components, each retaining its own `LICENSE` and upstream README. Their source files are not represented as MIT-licensed project code.
- `CbsExploder` and `SxSFounder` binaries are excluded because their independent redistribution terms have not been established. Microsoft package samples from the input SxS directory are not included.

See [`ThirdParty/THIRD-PARTY-NOTICES.md`](ThirdParty/THIRD-PARTY-NOTICES.md) for the component inventory and provenance details. Preserve the per-component notices when redistributing the repository. This inventory records source boundaries; it is not a legal opinion on distribution of a combined work.

## Next development steps

1. Add local, offline tests for UUP query parsing, snapshot round-trips, payload lookup, and bridge cancellation/error handling.
2. Integrate UUP media conversion as a separate, observable workflow with explicit tool and disk-space checks.
3. Design a safe SxS workflow that performs preflight and previews all inputs/outputs before any image operation; do not automate volume formatting without explicit, repeated confirmation.
4. Resolve the source/licensing and build provenance of CbsExploder/SxSFounder before considering their binaries or source for distribution.
5. Add artifact registration and unified preflight checks, then regression-test representative Windows/ADK generations.

## About

The Help → About entry opens the Windows-provided Shell About dialog through `rundll32 shell32.dll,ShellAbout`; the application does not display a separately authored About description.
