trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\AudioRig.ps1"
. "$PSScriptRoot\..\lib\SourceVariants.ps1"
# An app that runs as administrator cannot be waited on by a booster that does not: asking
# to be told when it exits is refused with "Access is denied". The booster took that refusal
# for the app having closed, so the boost of such an app ended the moment it started, with
# "target closed". Not being allowed to watch is not the same as gone: the boost now runs,
# and the watcher looks at that target itself on every pass.
# The refusal is simulated by a test-only build, so the test does not depend on how the
# machine it runs on is set up. The target is the private player.
Build-Rig
New-HookDeniedSource $Rig.Src (Join-Path $Rig.Dir 'denied.cs')
$exe = Join-Path $Rig.Dir 'denied\AppVolumeBooster.exe'
New-Build (Join-Path $Rig.Dir 'denied.cs') $exe | Out-Null
$player = Start-Player 60
$orig = Probe get $player.Id
Write-Host ('  private player pid {0}; original state {1} (restored at the end)' -f $player.Id, $orig)
$b = $null
try {
    Write-Host '  -- the boost runs --'
    $r = Invoke-Booster @('--pid', $player.Id, '--boost', '100', '--seconds', '3') -Exe $exe
    Write-Host ('  log: ' + $r.Log)
    Assert ($r.Log -match 'stopReason=timer' -and (Get-LogNumber $r.Log 'capSamples') -gt 96000) 'it runs for its 3 seconds (it used to end at once with "target closed")'
    Assert (Test-Restored $player $orig) 'and restores the player'

    Write-Host '  -- and the app closing is still noticed --'
    $log = Join-Path $Rig.Dir 'denied2.log'
    $b = Start-Booster -Exe $exe -BoosterArgs @('--pid', $player.Id, '--boost', '100', '--seconds', '60') -Log $log
    $dl = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $dl -and (Probe get $player.Id) -notmatch 'vol=0\.040') { Start-Sleep -Milliseconds 200 }
    Assert ((Probe get $player.Id) -match 'vol=0\.040') 'the player is being boosted'
    Stop-Process -Id $player.Id -Force
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $dl = (Get-Date).AddSeconds(10)
    while (-not $b.HasExited -and (Get-Date) -lt $dl) { Start-Sleep -Milliseconds 100 }
    Assert ($b.HasExited) ('the boost ended {0:N1} s after the player was closed' -f $sw.Elapsed.TotalSeconds)
    $l = if (Test-Path -LiteralPath $log) { (Get-Content -LiteralPath $log -Raw).Trim() } else { '(no log)' }
    Write-Host ('  log: ' + $l)
    Assert ($l -match 'stopReason=target closed') '... with "target closed"'
}
finally {
    if ($b -and -not $b.HasExited) { Stop-Process -Id $b.Id -ErrorAction SilentlyContinue }
    if ($player -and -not $player.HasExited) { Stop-Player $player $orig }
    Start-Sleep -Seconds 1
    # the player was closed while it was held at 4%: the booster puts the closed session back,
    # and Windows keeps that as the saved level - checked on a fresh session, repaired if not
    $chk = Start-Player 20
    $state = Probe get $chk.Id
    Assert ($state -eq $orig) ('a fresh PowerShell session starts at the level from before the test ({0})' -f $state)
    Stop-Player $chk $orig
}
Complete-Test
