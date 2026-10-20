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
# Last, the tray: minimised, the window goes there; what Start in the tray takes when nothing
# is checked (on made-up rows); and Start, Stop and the slider in its panel, on the player.
# The tray icon shows for a moment while that runs; the window itself stays off the screen.
# And starting the exe again while the window is in the tray brings that window back instead
# of opening a second one: in the window test, and here between two processes (the probe,
# under a test name - no window, and the user's own booster is never asked anything).
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

    Write-Host '  -- a second start, from another process --'
    $name = 'AppVolumeBooster.Test.' + $PID
    $outA = Join-Path $Rig.Dir 'claimA.txt'; $outB = Join-Path $Rig.Dir 'claimB.txt'
    $pa = Start-Process (Join-Path $Rig.Dir 'probe.exe') -ArgumentList 'claim', $name, 6000, (Q $outA) -PassThru -WindowStyle Hidden
    $dl = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $dl -and -not ((Test-Path -LiteralPath $outA) -and (Get-Content -LiteralPath $outA -Raw) -match 'claimed')) { Start-Sleep -Milliseconds 100 }
    Start-Process (Join-Path $Rig.Dir 'probe.exe') -ArgumentList 'claim', $name, 0, (Q $outB) -Wait -WindowStyle Hidden
    $pa.WaitForExit()
    $a = if (Test-Path -LiteralPath $outA) { (Get-Content -LiteralPath $outA -Raw).Trim() } else { '(none)' }
    $b = if (Test-Path -LiteralPath $outB) { (Get-Content -LiteralPath $outB -Raw).Trim() } else { '(none)' }
    Write-Host ('     first: {0}; second: {1}' -f $a, $b)
    Assert ($b -eq 'second' -and $a -eq 'first shown=1') 'the second start finds the first, which is asked once to show itself'
}
finally { Stop-Player $player $orig; Stop-Player $player2 $orig }
Complete-Test
