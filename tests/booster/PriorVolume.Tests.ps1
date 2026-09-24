trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\AudioRig.ps1"
# When the boost ends, the slider goes back to what it was - a level the user set very low
# included (a 3% app used to come back at 100%), and also when a second booster joins while
# the first one holds the slider at 4%: it takes the real prior from the first one's line
# in the state file instead of recording 4%.
Build-Rig
$player = Start-Player 40
$orig = Probe get $player.Id
Write-Host ('  private player pid {0}; original state {1} (restored at the end)' -f $player.Id, $orig)
try {
    Write-Host '  -- the user has set this app to 3% (no booster involved) --'
    Probe set $player.Id 0.03 - | Out-Null
    Invoke-Booster @('--pid', $player.Id, '--boost', '100', '--seconds', '2') | Out-Null
    $after = Probe get $player.Id
    Write-Host ('     after the boost: ' + $after)
    Assert ($after -match 'vol=0\.030') 'back at the user''s own 3%, not 100%'

    Write-Host '  -- the slider is at 4% because another booster holds it --'
    Probe set $player.Id 0.44 - | Out-Null
    # booster 1 ducks it (recording 0.44); booster 2 joins while it sits at 4% and outlives
    # booster 1, so the value booster 2 restores last is what remains
    $b1 = Start-Booster -BoosterArgs @('--pid', $player.Id, '--boost', '100', '--seconds', '3') -Log (Join-Path $Rig.Dir 'b1.log')
    Start-Sleep -Milliseconds 1200
    Write-Host ('     under booster 1: ' + (Probe get $player.Id))
    $b2 = Start-Booster -BoosterArgs @('--pid', $player.Id, '--boost', '100', '--seconds', '5') -Log (Join-Path $Rig.Dir 'b2.log')
    $b1.WaitForExit(); $b2.WaitForExit()
    $after2 = Probe get $player.Id
    Write-Host ('     after both     : ' + $after2)
    Assert ($after2 -match 'vol=0\.440') 'booster 2 restored the real prior (0.44) it took from booster 1''s line, not 100%'
}
finally { Stop-Player $player $orig }
Complete-Test
