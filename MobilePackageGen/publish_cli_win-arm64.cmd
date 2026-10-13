@echo off
REM Publish the two C# CLIs (replacements for cbsx.exe and sxsfounder.exe).
REM Kept separate from publish_win_*.cmd so the existing GUI publish flow is untouched.

setlocal
set OUT=%CD%\publish\cli\win-arm64
echo Output directory: %OUT%
echo.

echo [1/2] CbsxCli  (win-arm64)
dotnet publish MobilePackageGen\CbsxCli\CbsxCli.csproj -c Release -r win-arm64 --self-contained true ^
    -p:Platform=ARM64 -p:PublishTrimmed=false -o "%OUT%\cbsx" || exit /b 1

echo [2/2] SxSFounderCli  (win-arm64)
dotnet publish MobilePackageGen\SxSFounderCli\SxSFounderCli.csproj -c Release -r win-arm64 --self-contained true ^
    -p:Platform=ARM64 -p:PublishTrimmed=false -o "%OUT%\sxsfounder" || exit /b 1

echo.
echo Done. Usage:
echo   "%OUT%\cbsx\CbsxCli.exe" /?
echo   "%OUT%\sxsfounder\SxSFounderCli.exe" /?
endlocal
