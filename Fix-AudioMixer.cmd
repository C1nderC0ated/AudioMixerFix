@echo off
setlocal EnableExtensions
title Fix Audio Mixer (per-app volume memory)

rem Self-elevating launcher for Fix-AudioMixer.ps1 (double-click friendly).
rem It re-launches itself as admin, then runs the PowerShell worker beside it.

net session >nul 2>&1
if errorlevel 1 (
    echo Requesting administrator privileges...
    powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath 'powershell.exe' -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','\"%~dp0Fix-AudioMixer.ps1\"'"
    exit /b 0
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Fix-AudioMixer.ps1"
echo.
echo Done. Review the [OK]/[WARN]/[FAIL] lines above.
pause
exit /b 0
