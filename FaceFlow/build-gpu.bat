@echo off
title FaceFlow - build (NVIDIA GPU)
cd /d "%~dp0"

echo.
echo Building FaceFlow with the CUDA build of ONNX Runtime.
echo Requires an NVIDIA GPU plus a matching CUDA + cuDNN runtime installed.
echo If CUDA is missing at run time FaceFlow falls back to CPU automatically.
echo.

dotnet build FaceFlow.sln -c Release -p:FaceFlowGpu=true
if errorlevel 1 (
    echo.
    echo Build failed. See the messages above.
    pause
    exit /b 1
)

echo.
echo GPU build succeeded.
pause
