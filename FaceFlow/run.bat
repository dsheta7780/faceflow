@echo off
title FaceFlow
cd /d "%~dp0"
dotnet run --project src\FaceFlow.App\FaceFlow.App.csproj -c Release
if errorlevel 1 pause
