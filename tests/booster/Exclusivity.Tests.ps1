trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\AudioRig.ps1"
# "Boost all audio" captures everything except itself - including another booster's
# already boosted output, which it would boost a second time. So it refuses to start
# while another booster plays, and while it runs every other boost refuses. An all-audio
# boost is NEVER started here: ExclTest.cs only calls the claim on an engine that is never
# started, and a running all-audio boost is simulated by a process holding its mutex.
Build-Rig
New-Build $Rig.Src (Join-Path $Rig.Dir 'excl.exe') -Main 'AppVolumeBoosterNs.ExclTest' -Console -Extra @(Join-Path $Rig.Lib 'ExclTest.cs') | Out-Null
function Invoke-Claim { $f = Join-Path $Rig.Dir ('e' + [guid]::NewGuid().ToString('N') + '.txt'); Start-Process (Join-Path $Rig.Dir 'excl.exe') -ArgumentList (Q $f) -Wait -WindowStyle Hidden; (Get-Content -LiteralPath $f -Raw).Trim() }
$exe = Join-Path $Rig.Dir 'AppVolumeBooster.exe'
$real = @(Get-Process AppVolumeBooster -ErrorAction SilentlyContinue | Where-Object { $_.Path -notlike "$($Rig.Dir)*" })
Write-Host ('  your own boosters running right now: {0}' -f $real.Count)
$player = Start-Player 60
$orig = Probe get $player.Id
Write-Host ('  private player pid {0}; original state {1} (restored at the end)' -f $player.Id, $orig)
$holder = $null
try {
    Write-Host '  -- nothing else boosting: the all-audio claim succeeds --'
    $r = Invoke-Claim; Write-Host ('     ' + $r)
    if ($real.Count -eq 0) { Assert ($r -eq 'OK') 'no false refusal' } else { Write-Host '     (skipped: one of your own boosters is running)' }

    Write-Host '  -- a per-app booster is playing: the all-audio claim refuses --'
    $a = Start-Booster -Exe $exe -BoosterArgs @('--pid', $player.Id, '--boost', '100', '--seconds', '6') -Log (Join-Path $Rig.Dir 'a.log')
    Start-Sleep -Milliseconds 1500
    $r = Invoke-Claim; Write-Host ('     ' + $r)
    Assert ($r -match '^REFUSED: another booster is running \(AppVolumeBooster pid') 'refuses while another booster plays'
    $a.WaitForExit()

    Write-Host '  -- an all-audio boost holds the mutex (simulated by a helper process) --'
    $holder = Start-Process powershell -ArgumentList '-NoProfile', '-Command', "`$m = New-Object Threading.Mutex(`$false, 'AppVolumeBooster.AllAudio'); Start-Sleep -Seconds 12" -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 3
    $r = Invoke-Claim; Write-Host ('     a second all-audio: ' + $r)
    Assert ($r -match '^REFUSED: another booster is already boosting all audio') 'a second all-audio boost refuses'
    $r = Invoke-Booster @('--pid', $player.Id, '--boost', '100', '--seconds', '3')
    Write-Host ('     per-app meanwhile: ' + ($r.Log -split "`n")[0].Trim())
    Assert ($r.Log -match 'another booster is boosting all audio') 'a per-app boost refuses too'
    Assert (Test-Restored $player $orig) '... and ducked nothing'
    Stop-Process -Id $holder.Id -ErrorAction SilentlyContinue; $holder = $null
    Start-Sleep -Milliseconds 500

    Write-Host '  -- the mutex is gone again --'
    $r = Invoke-Booster @('--pid', $player.Id, '--boost', '100', '--seconds', '2')
    Assert ($r.Log -match '^ok ') 'a per-app boost runs normally'
}
finally {
    if ($holder) { Stop-Process -Id $holder.Id -ErrorAction SilentlyContinue }
    Stop-Player $player $orig
}
Complete-Test
