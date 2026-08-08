@echo off
title FaceFlow - publish
cd /d "%~dp0"

echo Publishing a self-contained x64 build to .\dist ...
dotnet publish src\FaceFlow.App\FaceFlow.App.csproj -c Release -r win-x64 ^
    --self-contained true -p:PublishSingleFile=false -o dist
if errorlevel 1 (
    echo Publish failed.
    pause
    exit /b 1
)

if not exist "dist\models" mkdir "dist\models"
copy /y "models\*.onnx" "dist\models\" >nul 2>nul

echo.
echo Published to .\dist  -  run dist\FaceFlow.exe
pause
