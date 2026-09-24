trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\AudioRig.ps1"
# Stop() is reached from the window, the target-exit handler and the watcher, whose ticks
# can overlap; two shutdowns at once raced over the process list and could throw on a
# thread-pool thread, which ends the process. StopTest.cs stops one running engine from 8
# threads at the same instant, 20 times over, on the private player only.
Build-Rig
New-Build $Rig.Src (Join-Path $Rig.Dir 'stoptest.exe') -Main 'AppVolumeBoosterNs.StopTest' -Console -Extra @(Join-Path $Rig.Lib 'StopTest.cs') | Out-Null
$player = Start-Player 120
$orig = Probe get $player.Id
Write-Host ('  private player pid {0}; original state {1} (restored at the end)' -f $player.Id, $orig)
try {
    $out = Join-Path $Rig.Dir 'st.txt'
    $p = Start-Process (Join-Path $Rig.Dir 'stoptest.exe') -ArgumentList (Q $out), $player.Id, 20 -PassThru -Wait -WindowStyle Hidden
    Write-Host ('  stoptest exit code {0} (a crash would show as a large or negative code)' -f $p.ExitCode)
    $txt = if (Test-Path -LiteralPath $out) { Get-Content -LiteralPath $out -Raw } else { '' }
    if ($txt) { ($txt -split "`r?`n") | Select-Object -First 6 | ForEach-Object { Write-Host ('  ' + $_) } } else { Write-Host '  (no report - the process died before writing it)' }
    Assert ($txt -match 'exceptions=0\b') 'no exception from 8 simultaneous stops, 20 times over'
    Assert ($txt -match 'doubleStoppedEvents=0\b') 'StoppedEvent fired at most once per engine'
    Assert ($txt -match 'stopReturnedBeforeRestore=0\b') 'Stop() never returned before the sliders were restored'
    Assert (Test-Restored $player $orig) 'the player was restored at the end'
}
finally { Stop-Player $player $orig }
Complete-Test
