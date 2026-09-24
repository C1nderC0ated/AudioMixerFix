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
rem Exit codes are compared with 0 rather than tested with "if errorlevel 1", which is
rem "1 or higher" - so a negative code (the form a failing tool's HRESULT takes) read as
rem success: here as "already elevated", below as "the elevated copy was started".
rem
rem The script path travels through an env var rather than a PowerShell string literal,
rem so a folder name containing an apostrophe cannot break the quoting. The elevated
rem window gets it as  cmd /s /c ""PATH" --elevated"  - with a plain /c, cmd dropped
rem the quotes around a path containing "&", "^" or "@" (measured with "R&D", "a ^ b"
rem and "a@b"), so the elevated window ran nothing while this one had already closed.
rem That window's cmd still expands a %%NAME%% pair in the path when NAME is a variable
rem it knows - measured: a folder "a%%OS%%b" became "aWindows_NTb" and nothing ran. So a
rem path with such a pair is first put to cmd itself (the same expansion, compared with the
rem raw path read back through delayed expansion) and refused with an explanation if it
rem would change. A path without a pair skips the check; a lone percent sign is harmless.

if /i "%~1"=="--elevated" goto :elevated

fltmc >nul 2>&1
if "%errorlevel%"=="0" goto :elevated

echo Requesting administrator privileges...
rem An elevated process cannot see drive letters mapped in this (non-elevated) session,
rem so launched from a mapped network drive the elevated window could not find this file
rem and closed at once, while this side reported nothing. Checked in the same PowerShell
rem call (no extra start-up): exit 2 = network drive. A UNC path (\\server\share) has no
rem drive letter, makes DriveInfo throw, and is allowed through - elevation can reach it.
set "SELFCMD=%~f0"
set "SELFDRIVE=%~d0"
powershell -NoProfile -Command "try { if ([IO.DriveInfo]::new($env:SELFDRIVE).DriveType -eq 'Network') { exit 2 } } catch {}; if ($env:SELFCMD -match '%%[^%%]+%%') { try { $q = $env:SELFCMD.Replace('^', '^^').Replace('!', '^!'); $p = Start-Process -FilePath $env:ComSpec -ArgumentList '/d', '/v:on', '/s', '/c', ('\"if not \"' + $q + '\"==\"!SELFCMD!\" exit 3\"') -Wait -PassThru -WindowStyle Hidden; if ($p.ExitCode -ne 0) { exit 3 } } catch { exit 3 } }; try { Start-Process -Verb RunAs -FilePath $env:ComSpec -ArgumentList '/s', '/c', ('\"\"' + $env:SELFCMD + '\" --elevated\"') -ErrorAction Stop } catch { exit 1 }"
set "PSRC=%errorlevel%"
if "%PSRC%"=="2" goto :netdrive
if "%PSRC%"=="3" goto :pctpath
if not "%PSRC%"=="0" goto :declined
exit /b 0

:netdrive
echo.
echo [FAIL] This copy runs from %SELFDRIVE%, a mapped network drive. The elevated window
echo        cannot see drive letters mapped in this session, so it would fail silently.
echo        Copy the AudioMixerFix folder to a local drive and run it from there.
pause
exit /b 1

:pctpath
echo.
echo [FAIL] This copy's path contains a %%NAME%% pair that the elevated window would expand
echo        as a variable, so it could not find this file:
echo        "%SELFCMD%"
echo        Rename that folder, or copy the AudioMixerFix folder somewhere without such a
echo        pair in its path, and run it from there.
pause
exit /b 1

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

