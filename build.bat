@echo off
setlocal
cd /d "%~dp0"

echo ============================================
echo  PdfCompressor - self-contained single-file build
echo ============================================
echo.

set "DOTNET_CMD=dotnet"
if exist "C:\Program Files\dotnet\dotnet.exe" set "DOTNET_CMD=C:\Program Files\dotnet\dotnet.exe"

"%DOTNET_CMD%" --version >nul 2>nul
if errorlevel 1 (
    echo .NET SDK not found. Install it first:
    echo   winget install Microsoft.DotNet.SDK.8
    echo then run this script again.
    pause
    exit /b 1
)

"%DOTNET_CMD%" publish "PdfCompressor\PdfCompressor.csproj" -c Release -o "Standalone" --nologo

echo.
if exist "Standalone\PdfCompressor.exe" (
    echo SUCCESS! Your single-file exe is here:
    echo   %~dp0Standalone\PdfCompressor.exe
) else (
    echo BUILD FAILED - see errors above.
)
pause
