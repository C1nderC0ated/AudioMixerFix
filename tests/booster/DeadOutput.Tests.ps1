trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\AudioRig.ps1"
. "$PSScriptRoot\..\lib\SourceVariants.ps1"
# When the output stops responding (device removed, taken over in exclusive mode), the
# boost stops itself within a few seconds and says why - it used to sit there "boosting"
# with the target ducked and silent until its timer ran out. A test-only build makes the
# output "die" about 1 s in; the "before" build also loses the watcher's check.
Build-Rig
New-Fault10Source $Rig.Src (Join-Path $Rig.Dir 'fault10.cs') (Join-Path $Rig.Dir 'nofix10.cs')
New-Build (Join-Path $Rig.Dir 'fault10.cs') (Join-Path $Rig.Dir 'fault10\AppVolumeBooster.exe') | Out-Null
New-Build (Join-Path $Rig.Dir 'nofix10.cs') (Join-Path $Rig.Dir 'nofix10\AppVolumeBooster.exe') | Out-Null
$player = Start-Player 60
$orig = Probe get $player.Id
Write-Host ('  private player pid {0}; original state {1} (restored at the end)' -f $player.Id, $orig)
try {
    foreach ($v in 'fault10', 'nofix10') {
        Write-Host ('  -- the output dies about 1 s in ({0}) --' -f $v)
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $r = Invoke-Booster @('--pid', $player.Id, '--boost', '100', '--seconds', '10') -Exe (Join-Path $Rig.Dir "$v\AppVolumeBooster.exe")
        $sw.Stop()
        Write-Host ('     ran {0:N1} s: {1}' -f $sw.Elapsed.TotalSeconds, $r.Log)
        if ($v -eq 'fault10') {
            Assert ($r.Log -match 'stopReason=the audio output stopped responding \(0x88890004\)') 'stopped itself, naming the failure'
            Assert ($sw.Elapsed.TotalSeconds -lt 6) 'within a few seconds, not at the 10 s timer'
            Assert (Test-Restored $player $orig) 'and the player was restored'
        } else {
            Assert ($r.Log -match 'stopReason=timer') 'without the check it sat there until the timer - so the checks above can fail'
        }
    }
}
finally { Stop-Player $player $orig }
Complete-Test
