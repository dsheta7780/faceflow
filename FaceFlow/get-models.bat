@echo off
title FaceFlow - download models
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\get-models.ps1" %*
pause
