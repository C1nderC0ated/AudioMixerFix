@echo off
setlocal EnableExtensions
title Per-app volume memory check
echo ===============================================================
echo   Per-app volume memory check   (any machine, or user)
echo ===============================================================
echo   Windows saves an app's volume WHEN THE APP CLOSES, and keeps
echo   a separate value per output device. So: set a volume, close
echo   that app, then run this. Apps listed below have a saved volume.
rem NOTE: the $paths/$names arrays below mirror $Stores in Fix-AudioMixer.ps1, the help
rem block at the top of that file, and the table in README.md - change one, change all
rem four. The loop is driven by $paths.Count, so adding a variant here needs no other
rem edit in this file.
rem The verdict only claims what it saw. Entries existing does NOT prove the store works
rem for everyone: with a Medium label, ordinary apps save while Chromium and Store apps
rem fail silently - so it used to say WORKING in exactly the failure it exists to catch.
rem A browser missing from the list has two possible causes, and the label is the rarer
rem one: a Chromium browser that was started without the kit's launch flags does not save
rem its volume either (an update of the browser recreates its Start Menu shortcut without
rem them). The verdict names both. Each app is listed once, however many outputs it has a
rem saved volume for.
rem Downloaded on its own from the release page, this file has no kit next to it: the
rem verdict then says where Fix-AudioMixer.cmd and the script come from, instead of naming
rem a file the reader does not have. The variant store exists only on some builds (the
rem README: some 24H2 pre-release builds), so its absence is not a yellow MISSING.
set "KITHERE=1"
if not exist "%~dp0Fix-AudioMixer.cmd" set "KITHERE="
powershell -NoProfile -ExecutionPolicy Bypass -Command "$paths=@('Registry::HKEY_CURRENT_USER\Software\Microsoft\Internet Explorer\LowRegistry\Audio\PolicyConfig\PropertyStore','Registry::HKEY_CURRENT_USER\Software\Microsoft\Multimedia\Audio\PolicyConfig\PropertyStore'); $names=@('canonical store (IE LowRegistry)','variant store (Multimedia-Audio)'); $any=$false; for($i=0;$i -lt $paths.Count;$i++){ Write-Host ''; if(-not (Test-Path $paths[$i])){ if($i -eq 0){ Write-Host ('  '+$names[$i]+': MISSING') -ForegroundColor Yellow } else { Write-Host ('  '+$names[$i]+': not present (only some builds have this one)') -ForegroundColor Gray }; continue }; $k=Get-Item $paths[$i]; $n=$k.SubKeyCount; if($n -eq 0){ Write-Host ('  '+$names[$i]+': exists, no saved volumes yet') -ForegroundColor Gray; continue }; $any=$true; Write-Host ('  '+$names[$i]+': '+$n+' saved entries') -ForegroundColor Green; $apps=@(); foreach($sk in $k.GetSubKeyNames()){ $d=(Get-ItemProperty ($paths[$i]+'\'+$sk) -ErrorAction SilentlyContinue).'(default)'; $app='(system sounds)'; if($d -and $d -notmatch '\|#'){ $t=$d.Substring($d.LastIndexOf('\')+1); $c=$t.IndexOf('%%'); if($c -ge 0){$t=$t.Substring(0,$c)}; if($t){$app=$t} }; $apps+=$app }; $apps | Sort-Object -Unique | ForEach-Object { Write-Host ('     - '+$_) } }; Write-Host ''; if($any){ Write-Host ' VERDICT: Windows is saving volumes for the apps listed above.' -ForegroundColor Green; Write-Host ' A browser (Chrome, Edge, Thorium...) MISSING here after you set its volume and closed it:' -ForegroundColor Yellow; Write-Host ' it was started without the launch flags (a browser update resets its Start Menu shortcut),' -ForegroundColor Yellow; Write-Host ' or the store''s security label is wrong. A Microsoft Store app missing: the label.' -ForegroundColor Yellow; Write-Host ' Fix-AudioMixer.ps1 -Status shows both; Fix-AudioMixer.cmd repairs both.' -ForegroundColor Yellow } else { Write-Host ' VERDICT: nothing saved yet. Set an app volume, CLOSE that app, run this again. Still empty after that = run Fix-AudioMixer.cmd.' -ForegroundColor Yellow }; if(-not $env:KITHERE){ Write-Host ' Fix-AudioMixer.cmd is not next to this file: download it and Fix-AudioMixer.ps1 from the page this file came from, into one folder.' -ForegroundColor Yellow }"
echo.
pause
exit /b 0
