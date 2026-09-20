@echo off
setlocal EnableExtensions
title Fix Audio Mixer (per-app volume memory)

rem Self-elevating launcher for Fix-AudioMixer.ps1 (double-click friendly).
rem
rem Elevation re-launches THIS .cmd - not the .ps1 - so the elevated console reaches
rem the pause below. Launching the .ps1 directly gives it a console that closes the
rem instant the script ends, so every [OK]/[WARN]/[FAIL] line flashes past unread.
rem
rem Because that relaunch re-enters this file, the elevation test has to be correct or
rem it would prompt in a loop. "net session" is NOT a valid test: it returns 2 whenever
rem the Server service is stopped - true on this machine, elevated or not - so it reads
rem as "not admin" forever. fltmc requires real admin rights and depends on no service
rem (verified here: elevated 0, Basic User 1; net session gave 2 both ways). The
rem --elevated marker is the backstop: even where a probe is wrong, this file elevates
rem at most once.
rem
rem The script path travels through an env var rather than a PowerShell string literal,
rem so a folder name containing an apostrophe cannot break the quoting.

if /i "%~1"=="--elevated" goto :elevated

fltmc >nul 2>&1
if not errorlevel 1 goto :elevated

echo Requesting administrator privileges...
set "SELFCMD=%~f0"
powershell -NoProfile -Command "try { Start-Process -Verb RunAs -FilePath $env:ComSpec -ArgumentList '/c', ('\"' + $env:SELFCMD + '\"'), '--elevated' -ErrorAction Stop } catch { exit 1 }"
if errorlevel 1 goto :declined
exit /b 0

:declined
echo.
echo [FAIL] Elevation was declined or failed - nothing was changed.
echo        Approve the UAC prompt, or run Fix-AudioMixer.ps1 from an elevated
echo        PowerShell window.
pause
exit /b 1

:elevated
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Fix-AudioMixer.ps1"
set "RC=%errorlevel%"
echo.
if "%RC%"=="0" echo Done - no failures reported. Review the [OK] / [WARN] lines above.
if not "%RC%"=="0" echo Done - the script reported failures. Review the [FAIL] lines above.
pause
exit /b %RC%
