trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\AudioRig.ps1"
. "$PSScriptRoot\..\lib\SourceVariants.ps1"
# A stall of the output thread lets the capture ring fill. Afterwards the device drains at
# its own rate, so nothing ever removed the excess: 260 ms of permanent extra latency after
# one 300 ms stall, while the status line still said 60. The booster now resumes from the
# newest audio and caps what is left. Test-only builds inject the stall (and, for the
# "before" run, also remove both guards).
Build-Rig
New-Build $Rig.Src (Join-Path $Rig.Dir 'ringtest.exe') -Main 'AppVolumeBoosterNs.RingTest' -Console -Extra @(Join-Path $Rig.Lib 'RingTest.cs') | Out-Null
New-Fault09Source $Rig.Src (Join-Path $Rig.Dir 'fault09.cs') (Join-Path $Rig.Dir 'nofix09.cs') 300
New-Build (Join-Path $Rig.Dir 'fault09.cs') (Join-Path $Rig.Dir 'f\AppVolumeBooster.exe') | Out-Null
New-Build (Join-Path $Rig.Dir 'nofix09.cs') (Join-Path $Rig.Dir 'n\AppVolumeBooster.exe') | Out-Null

Write-Host '  -- SampleRing.TrimTo on its own --'
$o = Join-Path $Rig.Dir 'ring.txt'
Start-Process (Join-Path $Rig.Dir 'ringtest.exe') -ArgumentList (Q $o) -Wait -WindowStyle Hidden
Get-Content -LiteralPath $o | ForEach-Object { Write-Host ('     ' + $_) }
Assert ((Get-Content -LiteralPath $o -Raw) -match 'RINGTEST PASS') 'TrimTo drops the oldest and keeps the newest'

$player = Start-Player 90
$orig = Probe get $player.Id
Write-Host ('  private player pid {0}; original state {1} (restored at the end)' -f $player.Id, $orig)
try {
    Write-Host '  -- normal playback, the shipped logic --'
    $r = Invoke-Booster @('--pid', $player.Id, '--boost', '100', '--seconds', '4')
    Write-Host ('     ' + $r.Log)
    Assert ((Get-LogNumber $r.Log 'trimmedMs') -eq 0) 'trimmedMs=0 - normal playback is never trimmed'

    Write-Host '  -- one 300 ms stall of the output thread --'
    $r = Invoke-Booster @('--pid', $player.Id, '--boost', '100', '--seconds', '4') -Exe (Join-Path $Rig.Dir 'f\AppVolumeBooster.exe')
    Write-Host ('     ' + $r.Log)
    $tr = Get-LogNumber $r.Log 'trimmedMs'; $bk = Get-LogNumber $r.Log 'backlogMs'
    Assert ($tr -gt 0) "the stale audio was dropped (trimmedMs=$tr)"
    Assert ($bk -ge 0 -and $bk -le 20) "the backlog is back to a packet or two afterwards (backlogMs=$bk)"

    Write-Host '  -- the same stall with both guards removed (the original bug) --'
    $r = Invoke-Booster @('--pid', $player.Id, '--boost', '100', '--seconds', '4') -Exe (Join-Path $Rig.Dir 'n\AppVolumeBooster.exe')
    Write-Host ('     ' + $r.Log)
    $bk2 = Get-LogNumber $r.Log 'backlogMs'
    Assert ($bk2 -ge 150) "without them the backlog stays (backlogMs=$bk2) - so the check above can fail"
}
finally { Stop-Player $player $orig }
Complete-Test
