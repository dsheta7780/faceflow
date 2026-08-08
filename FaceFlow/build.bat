@echo off
title FaceFlow - build (CPU)
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo.
    echo .NET SDK 8 was not found.
    echo Install it from https://dotnet.microsoft.com/download/dotnet/8.0 and run this again.
    echo.
    pause
    exit /b 1
)

if not exist "models\*.onnx" (
    echo.
    echo No models found. Running get-models.bat first...
    call powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\get-models.ps1"
)

echo.
echo Building FaceFlow (CPU build)...
dotnet build FaceFlow.sln -c Release
if errorlevel 1 (
    echo.
    echo Build failed. See the messages above.
    pause
    exit /b 1
)

echo.
echo Build succeeded.
pause
