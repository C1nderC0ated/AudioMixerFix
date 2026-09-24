trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\AudioRig.ps1"
# The booster's command line: a --seconds value it cannot honour is refused BEFORE the
# boost starts (beyond ~24.8 days the wait overflowed after the engine had started and the
# process exited with its target ducked; negative or NaN waited forever), and a --log it
# cannot write is exit code 3 up front instead of a crash in the middle of the run.
Build-Rig
$player = Start-Player 60
$orig = Probe get $player.Id
Write-Host ('  private player pid {0}; original state {1} (restored at the end)' -f $player.Id, $orig)
try {
    Write-Host '  -- --seconds values it cannot honour --'
    foreach ($s in '-1', 'NaN', '3000000') {
        $r = Invoke-Booster @('--pid', $player.Id, '--boost', '100', '--seconds', $s)
        Write-Host ('     --seconds {0,-8} exit {1}: {2}' -f $s, $r.Code, ($r.Log -replace '^(.{0,100}).*$', '$1'))
        Assert ($r.Code -eq 1 -and $r.Log -match '--seconds must be between') "--seconds $s is refused up front"
        Assert (Test-Restored $player $orig) "--seconds $s left nothing ducked"
    }
    Write-Host '  -- a --log it cannot write --'
    # a folder that does not exist, inside the scratch folder - so even a build that did
    # create it would leave nothing behind
    $bad = Join-Path $Rig.Dir ('no_such_dir_' + [guid]::NewGuid().ToString('N') + '\x.log')
    $p = Start-Process (Join-Path $Rig.Dir 'AppVolumeBooster.exe') -ArgumentList '--pid', $player.Id, '--seconds', '2', '--log', (Q $bad) -PassThru -Wait -WindowStyle Hidden
    Write-Host ('     exit {0}' -f $p.ExitCode)
    Assert ($p.ExitCode -eq 3) 'exit code 3 (log not writable), not a crash'
    Assert (-not (Get-Process WerFault -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt (Get-Date).AddSeconds(-20) })) 'no Windows Error Reporting dialog'
    Assert (Test-Restored $player $orig) 'nothing left ducked'
    Write-Host '  -- a normal run --'
    $r = Invoke-Booster @('--pid', $player.Id, '--boost', '100', '--seconds', '2')
    Assert ($r.Code -eq 0 -and $r.Log -match '^ok ') 'succeeds'
}
finally { Stop-Player $player $orig }
Complete-Test
