@echo off
setlocal EnableExtensions
title Per-app volume memory check
echo ===============================================================
echo   Per-app volume memory check   (any machine, or user)
echo ===============================================================
echo   Windows saves an app's volume WHEN THE APP CLOSES, and keeps
echo   a separate value per output device. So: set a volume, close
echo   that app, then run this. Apps listed below = saving works.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$paths=@('Registry::HKEY_CURRENT_USER\Software\Microsoft\Internet Explorer\LowRegistry\Audio\PolicyConfig\PropertyStore','Registry::HKEY_CURRENT_USER\Software\Microsoft\Multimedia\Audio\PolicyConfig\PropertyStore'); $names=@('canonical store (IE LowRegistry)','variant store (Multimedia-Audio)'); $any=$false; for($i=0;$i -lt 2;$i++){ Write-Host ''; if(-not (Test-Path $paths[$i])){ Write-Host ('  '+$names[$i]+': MISSING') -ForegroundColor Yellow; continue }; $k=Get-Item $paths[$i]; $n=$k.SubKeyCount; if($n -eq 0){ Write-Host ('  '+$names[$i]+': exists, no saved volumes yet') -ForegroundColor Gray; continue }; $any=$true; Write-Host ('  '+$names[$i]+': '+$n+' saved entries') -ForegroundColor Green; foreach($sk in $k.GetSubKeyNames()){ $d=(Get-ItemProperty ($paths[$i]+'\'+$sk) -ErrorAction SilentlyContinue).'(default)'; $app='(system sounds)'; if($d -and $d -notmatch '\|#'){ $t=$d.Substring($d.LastIndexOf('\')+1); $c=$t.IndexOf('%%'); if($c -ge 0){$t=$t.Substring(0,$c)}; if($t){$app=$t} }; Write-Host ('     - '+$app) } }; Write-Host ''; if($any){ Write-Host ' VERDICT: volume memory is WORKING - Windows is saving app volumes.' -ForegroundColor Green } else { Write-Host ' VERDICT: nothing saved yet. Set an app volume, CLOSE that app, run this again. Still empty after that = run Fix-AudioMixer.cmd.' -ForegroundColor Yellow }"
echo.
pause
exit /b 0
