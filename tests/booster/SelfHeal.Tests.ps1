trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\AudioRig.ps1"
# Every booster start "self-heals" sliders left at 4% by a booster that died. A dead line
# for an app that a LIVE booster is holding right now must not be healed: that un-ducked
# the running boost, so the app played at full volume under the 25x copy.
Build-Rig
$state = Join-Path $Rig.Dir 'booster-state.txt'
$player = Start-Player 40
$orig = Probe get $player.Id
Write-Host ('  private player pid {0}; original state {1} (restored at the end)' -f $player.Id, $orig)
try {
    $a = Start-Booster -BoosterArgs @('--pid', $player.Id, '--boost', '100', '--seconds', '8') -Log (Join-Path $Rig.Dir 'a.log')
    Start-Sleep -Milliseconds 1500
    Write-Host ('  booster A holds it : ' + (Probe get $player.Id))
    # a dead (orphan) line for the same app, as a crashed earlier run would leave
    Add-Content -LiteralPath $state -Value '0|powershell|1.0000'
    Write-Host ('  state file         : ' + ((Get-Content -LiteralPath $state) -join ' / '))
    # another booster instance starts; its start-up self-heal sees that dead line
    $w = Start-Process (Join-Path $Rig.Dir 'probe.exe') -ArgumentList 'watch', $player.Id, 2500, (Q (Join-Path $Rig.Dir 'w.txt')) -PassThru -WindowStyle Hidden
    Start-Sleep -Milliseconds 300
    Invoke-Booster @('--pid', 999999) | Out-Null
    $w.WaitForExit()
    $trace = (Get-Content -LiteralPath (Join-Path $Rig.Dir 'w.txt') -Raw).Trim()
    Write-Host ('  meanwhile          : ' + ($trace -split "`n")[0].Trim())
    Assert ($trace -match 'max=0\.040') 'the slider A holds was never un-ducked by the other instance''s self-heal'
    $a.WaitForExit()
    Assert (Test-Restored $player $orig) 'A restored it normally when it stopped'
}
finally { Stop-Player $player $orig }
Complete-Test
