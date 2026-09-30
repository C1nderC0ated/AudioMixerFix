trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\AudioRig.ps1"
. "$PSScriptRoot\..\lib\SourceVariants.ps1"
# When the CAPTURE stream stops responding while the output stays healthy, the boost stops
# itself within a few seconds and says why. Only the output side was watched: a dead
# capture client left the target ducked at 4% and silent while the window said "Boosting".
# A test-only build makes the capture client fail about 1 s in.
Build-Rig
New-FaultCaptureSource $Rig.Src (Join-Path $Rig.Dir 'capfault.cs')
New-Build (Join-Path $Rig.Dir 'capfault.cs') (Join-Path $Rig.Dir 'capfault\AppVolumeBooster.exe') | Out-Null
$player = Start-Player 40
$orig = Probe get $player.Id
Write-Host ('  private player pid {0}; original state {1} (restored at the end)' -f $player.Id, $orig)
try {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $r = Invoke-Booster @('--pid', $player.Id, '--boost', '100', '--seconds', '10') -Exe (Join-Path $Rig.Dir 'capfault\AppVolumeBooster.exe')
    $sw.Stop()
    Write-Host ('  ran {0:N1} s: {1}' -f $sw.Elapsed.TotalSeconds, $r.Log)
    Assert ($r.Log -match 'stopReason=the captured app''s audio stream stopped responding \(0x88890004\)') 'stopped itself, naming the failure'
    Assert ($sw.Elapsed.TotalSeconds -lt 6) 'within a few seconds, not at the 10 s timer'
    Assert (Test-Restored $player $orig) 'and the player was restored'
}
finally { Stop-Player $player $orig }
Complete-Test
