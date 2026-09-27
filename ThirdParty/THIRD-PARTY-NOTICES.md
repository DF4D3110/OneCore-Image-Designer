# Third-party source and license notices

The project root `LICENSE` applies only to this project's own code. It does not replace the license of vendored components.

| Component | Included content | License / notice |
| --- | --- | --- |
| [UUPMediaCreator](https://github.com/OSTooling/UUPMediaCreator) | Windows Update, Downloads, CompDB, and Cabinet source projects used by `UupBridge`; project files locally target net8.0 | MIT in `UUPMediaCreator/LICENSE`; upstream README at `UUPMediaCreator/UPSTREAM-README.md` |
| Cabinet LZX decoder | `UUPMediaCreator/src/Cabinet/Xna/LzxDecoder.cs` | Dual-licensed LGPL-2.1 or MS-PL; retain the source header and `UUPMediaCreator/src/Cabinet/LICENSE` |
| SharpZipLib 1.4.2 | NuGet dependency of the vendored Cabinet project | MIT (package metadata) |
| System.Text.Json 9.0.4 | NuGet dependency of the vendored Windows Update projects | MIT |
| [pureSxS](https://github.com/asdcorp/pureSxS) | Python scripts and manifest helper under `SxS/pureSxS` | GPL-3.0; see `SxS/pureSxS/LICENSE` and upstream README |
| [explodeSxS](https://github.com/asdcorp/explodeSxS) | Workflow scripts and configuration under `SxS/explodeSxS` | GPL-3.0; see `SxS/explodeSxS/LICENSE` and upstream README |

The two SxS snapshots were supplied as `principalis` source directories without Git metadata; no upstream commit identifier was available to record. Their referenced `CbsExploder` and `SxSFounder` executables are not included because their independent redistribution terms have not been verified. Microsoft package samples from the original `sxs` directory were also omitted.

SxS source files remain isolated in their own directories and retain their upstream notices. This file is an inventory, not a relicensing statement or legal determination about distribution of a combined application.
