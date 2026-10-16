# Third-party source and license notices

The project root `LICENSE` (MIT) applies only to this project's own code. It does not replace the
license of vendored components. Preserve the per-component notices when redistributing.

| Component | Included content | License / notice |
| --- | --- | --- |
| [UUPMediaCreator](https://github.com/OSTooling/UUPMediaCreator) | Windows Update, Downloads, CompDB and Cabinet source projects used by `UupBridge`; project files locally target net8.0 | MIT in `UUPMediaCreator/LICENSE`; upstream README at `UUPMediaCreator/UPSTREAM-README.md` |
| Cabinet LZX decoder | `UUPMediaCreator/src/Cabinet/Xna/LzxDecoder.cs` | Dual-licensed LGPL-2.1 or MS-PL; retain the source header and `UUPMediaCreator/src/Cabinet/LICENSE` |
| SharpZipLib 1.4.2 | NuGet dependency of the vendored Cabinet project | MIT (package metadata) |
| System.Text.Json 9.0.4 | NuGet dependency of the vendored Windows Update projects | MIT |
| [iot-adk-addonkit](https://github.com/microsoft/iot-adk-addonkit) release 17763-v7 | The bundled PowerShell IoT Core imaging toolchain and its sample workspace, under `addonkit/` | MIT in `addonkit/LICENSE`; Microsoft's bundled third-party notice list at `addonkit/TPN` |
| `MobilePackageGen/` subtree | Package-generation libraries, adapters and the CLI/GUI tooling | Subtree license in `MobilePackageGen/license.md`; component-by-component inventory in `MobilePackageGen/readme.md` (DiscUtils MIT, NSudo MIT, Img2Ffu MIT, StorageSpace MIT, DotnetPackaging.Msix MIT, WiX compression MS-RL, SevenZipExtractor / 7-Zip LGPL-2.1; `msdelta.dll` is called at runtime and is not redistributed) |
| Tooling binaries | `Tools/wcpex/wcpex.exe` and `Tools/wcpex/wcp.dll`; the package-generation support DLLs (`7z.dll` architecture variants, the `makeappx.exe` fixture and its support DLLs) | Retained **runtime inputs** redistributed as-is by their upstream projects, not build outputs of this repository. Their independent binary provenance has not been reviewed here. |

The sample certificates and `.pfx` files bundled under `addonkit/` are Microsoft's publicly
published sample keys shipped with the official toolkit; they are not private credentials.

## Referenced but not distributed

- `CbsExploder` and `SxSFounder` executables — their independent redistribution terms have not been
  established. Microsoft package samples from the original input SxS directory are not included.
- The GPL-3.0 `pureSxS` and `explodeSxS` script trees previously vendored under `SxS/` have been
  **removed from this repository**. The behaviours they documented are re-implemented in project
  code (`Services/PureSxsService.cs`, `Services/ExplodeSxSNativeService.cs`), which record their
  provenance in their file headers. No pureSxS/explodeSxS source is redistributed, and no upstream
  script is executed by the application.
- The optional UUP-to-ISO converter and the `cdimage.exe` it uses — the workflow requires a
  separately built `UUPMediaConverter.exe` and a .NET 10 runtime, neither of which is shipped here.

This file is an inventory of provenance, not a relicensing statement or a legal determination about
distribution of a combined application.
