trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\AudioRig.ps1"
# The window's Start/Stop button and a boost that stops itself (its target closed) raced:
# a click on a button still reading "Stop boost" could START a new boost, and a stale
# "stopped" notification from the old engine orphaned the new one - left running with no
# way to stop it from the window. UiTest.cs drives the real MainForm through that
# sequence with real engines, on private players only. Its last step kills a second
# player under a running boost: the status line must then say "Stopped: the app was closed" -
# the list refresh used to overwrite it with "No apps with audio sessions found".
# Before all that it looks at the app list itself: a row carries the name and icon Windows
# shows for the app, the window's own periodic refresh leaves an unchanged list alone, and a
# check stays with its app through a refresh - also when the app has restarted meanwhile.
Build-Rig
New-Build $Rig.Src (Join-Path $Rig.Dir 'uitest.exe') -Main 'AppVolumeBoosterNs.UiTest' -Extra @(Join-Path $Rig.Lib 'UiTest.cs') | Out-Null
$player = Start-Player 90
$orig = Probe get $player.Id
$player2 = Start-Player 90
Write-Host ('  private players pid {0} and {1}; original state {2} (restored at the end)' -f $player.Id, $player2.Id, $orig)
try {
    $out = Join-Path $Rig.Dir 'ui.txt'
    $p = Start-Process (Join-Path $Rig.Dir 'uitest.exe') -ArgumentList (Q $out), $player.Id, $player2.Id -PassThru -Wait -WindowStyle Hidden
    if (Test-Path -LiteralPath $out) { Get-Content -LiteralPath $out | ForEach-Object { Write-Host $_ } } else { Write-Host '  (no report)' }
    Assert ($p.ExitCode -eq 0) ('every step of the race held (uitest exit {0})' -f $p.ExitCode)
    Assert (Test-Restored $player $orig) 'nothing is left ducking the first player'
}
finally { Stop-Player $player $orig; Stop-Player $player2 $orig }
Complete-Test
