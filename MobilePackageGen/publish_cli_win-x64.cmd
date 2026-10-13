@echo off
REM Publish the C# CLIs for BOTH amd64 and x86, arranged as a relay layout:
REM
REM   publish\cli\amd64\CbsxCli.exe          <- main process runs here
REM   publish\cli\amd64\x86\CbsxCli.exe      <- ArchRelay dispatches x86 images here
REM   publish\cli\amd64\SxSFounderCli.exe
REM   publish\cli\amd64\x86\SxSFounderCli.exe
REM
REM Why both: CBS servicing stacks are per-architecture. An x64 process cannot load an
REM x86 cbscore.dll; it silently falls back to the host stack and CBS then fails with
REM 0x8007051A ERROR_REVISION_MISMATCH when the image build differs. ArchRelay picks the
REM matching helper instead, without restarting the main process.

setlocal
set ROOT=%CD%\publish\cli

echo [1/4] CbsxCli amd64
dotnet publish MobilePackageGen\CbsxCli\CbsxCli.csproj -c Release -r win-x64 --self-contained true ^
    -p:Platform=x64 -p:PublishTrimmed=false -o "%ROOT%\amd64" || exit /b 1

echo [2/4] CbsxCli x86
dotnet publish MobilePackageGen\CbsxCli\CbsxCli.csproj -c Release -r win-x86 --self-contained true ^
    -p:Platform=x86 -p:PublishTrimmed=false -o "%ROOT%\amd64\x86" || exit /b 1

echo [3/4] SxSFounderCli amd64
dotnet publish MobilePackageGen\SxSFounderCli\SxSFounderCli.csproj -c Release -r win-x64 --self-contained true ^
    -p:Platform=x64 -p:PublishTrimmed=false -o "%ROOT%\amd64" || exit /b 1

echo [4/4] SxSFounderCli x86
dotnet publish MobilePackageGen\SxSFounderCli\SxSFounderCli.csproj -c Release -r win-x86 --self-contained true ^
    -p:Platform=x86 -p:PublishTrimmed=false -o "%ROOT%\amd64\x86" || exit /b 1

echo.
echo Done. Relay layout:
echo   %ROOT%\amd64\CbsxCli.exe
echo   %ROOT%\amd64\x86\CbsxCli.exe
endlocal
