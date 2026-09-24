trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\AudioRig.ps1"
# Mute is never touched: an app the user muted stays muted while boosted and afterwards
# (the booster used to unmute it), and the CLI log says why the boost is silent.
Build-Rig
$player = Start-Player 30
$orig = Probe get $player.Id
Write-Host ('  private player pid {0}; original state {1} (restored at the end)' -f $player.Id, $orig)
try {
    Probe set $player.Id - 1 | Out-Null
    $muted = Probe get $player.Id
    Write-Host ('  muted by "the user"   : ' + $muted)
    $log = Join-Path $Rig.Dir 'b.log'
    $b = Start-Booster -BoosterArgs @('--pid', $player.Id, '--boost', '100', '--seconds', '3') -Log $log
    $trace = Probe watch $player.Id 4500
    $b.WaitForExit()
    $after = Probe get $player.Id
    $logText = if (Test-Path -LiteralPath $log) { (Get-Content -LiteralPath $log -Raw).Trim() } else { '(no log)' }
    Write-Host ('  during the boost      : ' + ($trace -split "`n")[0].Trim())
    Write-Host ('  after it              : ' + $after)
    Assert ($trace -match 'samples=(\d+) muted=\1\b') 'stayed muted for EVERY sample of the boost'
    Assert ($trace -match 'min=0\.040') 'was ducked to 4% meanwhile (the boost really ran)'
    Assert ($after -eq $muted) 'afterwards: still muted, volume back where it was'
    Assert ($logText -match 'warning=Muted in the Volume Mixer') 'the CLI log explains why it is silent'
}
finally { Stop-Player $player $orig }
Complete-Test
