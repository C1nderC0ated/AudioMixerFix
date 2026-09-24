trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\AudioRig.ps1"
# The window's Start/Stop button and a boost that stops itself (its target closed) raced:
# a click on a button still reading "Stop boost" could START a new boost, and a stale
# "stopped" notification from the old engine orphaned the new one - left running with no
# way to stop it from the window. UiTest.cs drives the real MainForm through that
# sequence with real engines, on the private player only.
Build-Rig
New-Build $Rig.Src (Join-Path $Rig.Dir 'uitest.exe') -Main 'AppVolumeBoosterNs.UiTest' -Extra @(Join-Path $Rig.Lib 'UiTest.cs') | Out-Null
$player = Start-Player 60
$orig = Probe get $player.Id
Write-Host ('  private player pid {0}; original state {1} (restored at the end)' -f $player.Id, $orig)
try {
    $out = Join-Path $Rig.Dir 'ui.txt'
    $p = Start-Process (Join-Path $Rig.Dir 'uitest.exe') -ArgumentList (Q $out), $player.Id -PassThru -Wait -WindowStyle Hidden
    if (Test-Path -LiteralPath $out) { Get-Content -LiteralPath $out | ForEach-Object { Write-Host $_ } } else { Write-Host '  (no report)' }
    Assert ($p.ExitCode -eq 0) ('every step of the race held (uitest exit {0})' -f $p.ExitCode)
    Assert (Test-Restored $player $orig) 'nothing is left ducking the player'
}
finally { Stop-Player $player $orig }
Complete-Test
