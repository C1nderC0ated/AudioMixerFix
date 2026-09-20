@echo off
setlocal EnableExtensions
title Rebuild AppVolumeBooster.exe

rem Rebuilds AppVolumeBooster.exe from AppVolumeBooster.cs using the C# compiler
rem that ships INSIDE Windows (.NET Framework 4.x) - no SDK, no internet, no admin.
rem Works on any Windows 10/11 machine, including right after a reinstall.
rem The booster can target several apps at once, system sounds, or all audio.
rem
rem Keep this file CRLF/ASCII like the rest of the kit. cmd needs CRLF to find a
rem label, so an editor that rewrites the endings to LF breaks every goto below.
rem
rem Builds to a .new temp and only then replaces the real exe: a failed rebuild must
rem leave the working binary you already had, and must not report success.

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [FAIL] In-box C# compiler not found under %WINDIR%\Microsoft.NET.
    pause
    exit /b 1
)

set "OUT=%~dp0AppVolumeBooster.exe"
set "NEW=%~dp0AppVolumeBooster.exe.new"
if exist "%NEW%" del "%NEW%"

"%CSC%" /nologo /t:winexe /out:"%NEW%" ^
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll ^
    "%~dp0AppVolumeBooster.cs"
set "RC=%errorlevel%"

if not "%RC%"=="0" goto :failed
if not exist "%NEW%" goto :missing

move /y "%NEW%" "%OUT%" >nul
if errorlevel 1 goto :locked
echo [OK]   Built: %OUT%
pause
exit /b 0

:failed
echo [FAIL] Compilation failed (csc exit %RC%) - see the errors above.
echo        Your previous exe, if any, was left untouched.
if exist "%NEW%" del "%NEW%"
pause
exit /b %RC%

:missing
echo [FAIL] csc reported success but produced no exe at:
echo        %NEW%
pause
exit /b 1

:locked
echo [FAIL] Built OK but could not replace the exe - it is probably still running.
echo        Close AppVolumeBooster and run this again. New build kept at:
echo        %NEW%
pause
exit /b 1
