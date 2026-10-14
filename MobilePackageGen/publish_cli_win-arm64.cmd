@echo off
REM Publish the C# CLIs for amd64, x86 and arm64.
REM
REM Layout:
REM   publish\cli\amd64\CbsxCli.exe              <- x64 host
REM   publish\cli\amd64\x86\CbsxCli.exe          <- ArchRelay dispatches x86 images here
REM   publish\cli\arm64\CbsxCli.exe              <- arm64 host (run ON arm64, cannot be relayed from x64)
REM
REM Why x86 lives under amd64: CBS servicing stacks are per-architecture, and an x64
REM process cannot load an x86 cbscore.dll -- it silently falls back to the host stack and
REM CBS then fails with 0x8007051A ERROR_REVISION_MISMATCH. ArchRelay picks the matching
REM helper rather than restarting the main process.
REM
REM Why arm64 is separate and not a relay target: Windows x64 has no ARM64 emulation
REM (Prism is the opposite direction). An arm64 helper is only useful when the whole
REM process runs on an arm64 host.

setlocal
set ROOT=%CD%\publish\cli

echo [1/6] CbsxCli amd64
dotnet publish MobilePackageGen\CbsxCli\CbsxCli.csproj -c Release -r win-x64 --self-contained true ^
    -p:Platform=x64 -p:PublishTrimmed=false -o "%ROOT%\amd64" || exit /b 1

echo [2/6] CbsxCli x86 (relay helper for x86 images)
dotnet publish MobilePackageGen\CbsxCli\CbsxCli.csproj -c Release -r win-x86 --self-contained true ^
    -p:Platform=x86 -p:PublishTrimmed=false -o "%ROOT%\amd64\x86" || exit /b 1

echo [3/6] CbsxCli arm64 (for an arm64 host)
dotnet publish MobilePackageGen\CbsxCli\CbsxCli.csproj -c Release -r win-arm64 --self-contained true ^
    -p:Platform=ARM64 -p:PublishTrimmed=false -o "%ROOT%\arm64" || exit /b 1

echo [4/6] SxSFounderCli amd64
dotnet publish MobilePackageGen\SxSFounderCli\SxSFounderCli.csproj -c Release -r win-x64 --self-contained true ^
    -p:Platform=x64 -p:PublishTrimmed=false -o "%ROOT%\amd64" || exit /b 1

echo [5/6] SxSFounderCli x86
dotnet publish MobilePackageGen\SxSFounderCli\SxSFounderCli.csproj -c Release -r win-x86 --self-contained true ^
    -p:Platform=x86 -p:PublishTrimmed=false -o "%ROOT%\amd64\x86" || exit /b 1

echo [6/6] SxSFounderCli arm64
dotnet publish MobilePackageGen\SxSFounderCli\SxSFounderCli.csproj -c Release -r win-arm64 --self-contained true ^
    -p:Platform=ARM64 -p:PublishTrimmed=false -o "%ROOT%\arm64" || exit /b 1

echo.
echo Done.
endlocal
