trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\AudioRig.ps1"
. "$PSScriptRoot\..\lib\SourceVariants.ps1"
# How loud the boosted copy is - the one thing the booster is for, and until 2026-09-30 the
# one thing no test looked at: every other check reads sliders, gaps, counters and logs, so
# a wrong gain (half as loud, or 25 times too loud) would have left them all green.
# The probe records the booster's own output, as Dropouts does, and reports its level.
#   * at 100% the copy is the app at a full slider: the player's tone at the level it has
#     in its file (its amplitude is 40 of 32768, which is an rms of 0.00086)
#   * at 200% it is twice that
#   * a build with the gain halved is measured too, to show that the check can fail
# What is heard stays a faint tone: 200% of the player is about -52 dBFS.
Build-Rig
New-GainSource $Rig.Src (Join-Path $Rig.Dir 'half.cs') 'boost / DUCK * 0.5f'
New-Build (Join-Path $Rig.Dir 'half.cs') (Join-Path $Rig.Dir 'half\AppVolumeBooster.exe') | Out-Null
$expected = 40 / 32768 / [math]::Sqrt(2)
$player = Start-Player 60
$orig = Probe get $player.Id
Write-Host ('  private player pid {0}; original state {1} (restored at the end)' -f $player.Id, $orig)
function Measure-Boost([int]$percent, [string]$exe = (Join-Path $Rig.Dir 'AppVolumeBooster.exe')) {
    $b = Start-Booster -Exe $exe -BoosterArgs @('--pid', $player.Id, '--boost', $percent, '--seconds', '7') -Log (Join-Path $Rig.Dir ('g' + [guid]::NewGuid().ToString('N') + '.log'))
    Start-Sleep -Milliseconds 1500          # the boost is up and its buffer is filled
    $g = Probe gaps $b.Id 3500
    $b.WaitForExit()
    Write-Host ('     {0,3}%: {1}' -f $percent, ($g -replace ' at:.*$', ''))
    [pscustomobject]@{ Rms = (Get-LogNumber $g 'rms'); Sound = (Get-LogNumber $g 'soundMs') }
}
try {
    $m100 = Measure-Boost 100
    $m200 = Measure-Boost 200
    $half = Measure-Boost 100 (Join-Path $Rig.Dir 'half\AppVolumeBooster.exe')
    Assert ($m100.Sound -ge 2500 -and $m200.Sound -ge 2500) 'the boosted copy was recorded both times'
    Assert ([math]::Abs($m100.Rms / $expected - 1) -le 0.08) ('at 100% the copy has the level of the tone itself: rms {0:N6} against {1:N6} ({2:N1}% off)' -f $m100.Rms, $expected, (($m100.Rms / $expected - 1) * 100))
    Assert ($m100.Rms -gt 0 -and [math]::Abs($m200.Rms / $m100.Rms - 2) -le 0.06) ('at 200% it is twice as loud: x{0:N3}' -f $(if ($m100.Rms -gt 0) { $m200.Rms / $m100.Rms } else { 0 }))
    Assert ($half.Rms -gt 0 -and [math]::Abs($half.Rms / $expected - 1) -gt 0.08) ('a build with the gain halved does NOT pass the 100% check (x{0:N3} of the tone) - so that check can fail' -f ($half.Rms / $expected))
    Assert (Test-Restored $player $orig) 'and the player is back at its own level'
}
finally { Stop-Player $player $orig }
Complete-Test
