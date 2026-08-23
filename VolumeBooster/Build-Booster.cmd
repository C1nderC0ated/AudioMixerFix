@echo off
setlocal EnableExtensions
title Rebuild AppVolumeBooster.exe

rem Rebuilds AppVolumeBooster.exe from AppVolumeBooster.cs using the C# compiler
rem that ships INSIDE Windows (.NET Framework 4.x) - no SDK, no internet, no admin.
rem Works on any Windows 10/11 machine, including right after a reinstall.

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [FAIL] In-box C# compiler not found under %WINDIR%\Microsoft.NET.
    pause
    exit /b 1
)

"%CSC%" /nologo /t:winexe /out:"%~dp0AppVolumeBooster.exe" ^
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll ^
    "%~dp0AppVolumeBooster.cs"

if errorlevel 1 (
    echo [FAIL] Compilation failed - see errors above.
) else (
    echo [OK]   Built: %~dp0AppVolumeBooster.exe
)
pause
exit /b 0
