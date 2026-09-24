@echo off
setlocal EnableExtensions
title Per-app volume memory check
echo ===============================================================
echo   Per-app volume memory check   (any machine, or user)
echo ===============================================================
echo   Windows saves an app's volume WHEN THE APP CLOSES, and keeps
echo   a separate value per output device. So: set a volume, close
echo   that app, then run this. Apps listed below = saving works.
rem NOTE: the $paths/$names arrays below mirror $Stores in Fix-AudioMixer.ps1, the help
rem block at the top of that file, and the table in README.md - change one, change all
rem four. The loop is driven by $paths.Count, so adding a variant here needs no other
rem edit in this file.
rem The verdict only claims what it saw. Entries existing does NOT prove the store works
rem for everyone: with a Medium label, ordinary apps save while Chromium and Store apps
rem fail silently - so it used to say WORKING in exactly the failure it exists to catch.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$paths=@('Registry::HKEY_CURRENT_USER\Software\Microsoft\Internet Explorer\LowRegistry\Audio\PolicyConfig\PropertyStore','Registry::HKEY_CURRENT_USER\Software\Microsoft\Multimedia\Audio\PolicyConfig\PropertyStore'); $names=@('canonical store (IE LowRegistry)','variant store (Multimedia-Audio)'); $any=$false; for($i=0;$i -lt $paths.Count;$i++){ Write-Host ''; if(-not (Test-Path $paths[$i])){ Write-Host ('  '+$names[$i]+': MISSING') -ForegroundColor Yellow; continue }; $k=Get-Item $paths[$i]; $n=$k.SubKeyCount; if($n -eq 0){ Write-Host ('  '+$names[$i]+': exists, no saved volumes yet') -ForegroundColor Gray; continue }; $any=$true; Write-Host ('  '+$names[$i]+': '+$n+' saved entries') -ForegroundColor Green; foreach($sk in $k.GetSubKeyNames()){ $d=(Get-ItemProperty ($paths[$i]+'\'+$sk) -ErrorAction SilentlyContinue).'(default)'; $app='(system sounds)'; if($d -and $d -notmatch '\|#'){ $t=$d.Substring($d.LastIndexOf('\')+1); $c=$t.IndexOf('%%'); if($c -ge 0){$t=$t.Substring(0,$c)}; if($t){$app=$t} }; Write-Host ('     - '+$app) } }; Write-Host ''; if($any){ Write-Host ' VERDICT: Windows is saving volumes for the apps listed above.' -ForegroundColor Green; Write-Host ' A browser (Chrome, Edge, Thorium...) or Microsoft Store app MISSING here after you set its volume' -ForegroundColor Yellow; Write-Host ' and closed it means the store''s security label is wrong - ordinary apps still save fine then.' -ForegroundColor Yellow; Write-Host ' Run Fix-AudioMixer.cmd; Fix-AudioMixer.ps1 -Status shows the label.' -ForegroundColor Yellow } else { Write-Host ' VERDICT: nothing saved yet. Set an app volume, CLOSE that app, run this again. Still empty after that = run Fix-AudioMixer.cmd.' -ForegroundColor Yellow }"
echo.
pause
exit /b 0

