trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\AudioRig.ps1"
# The booster enumerates every ACTIVE output through IMMDeviceCollection (it used to see
# only the default one), lists the private player in its app list, ducks it on every
# active output, restores it, and a clean run counts no glitch.
Build-Rig
$active = @(Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render' |
    Where-Object { (Get-ItemProperty $_.PSPath -Name DeviceState -ErrorAction SilentlyContinue).DeviceState -eq 1 })
$dev = Probe devices
Write-Host ('  registry: {0} active render endpoint(s); ActiveRenderDevices(): {1}' -f $active.Count, ($dev -split "`n")[0].Trim())
Assert ($dev -match "count=$($active.Count)\b") 'IMMDeviceCollection finds exactly the active outputs'

$player = Start-Player 40
$orig = Probe get $player.Id
Write-Host ('  private player pid {0}; original state {1} (restored at the end)' -f $player.Id, $orig)
try {
    $items = Probe list
    Assert ($items -match "powershell  \(pid $($player.Id)") 'the app list shows the player'
    $log = Join-Path $Rig.Dir 'b.log'
    $b = Start-Booster -BoosterArgs @('--pid', $player.Id, '--boost', '100', '--seconds', '3') -Log $log
    $trace = Probe watch $player.Id 4500
    $b.WaitForExit()
    $l = (Get-Content -LiteralPath $log -Raw).Trim()
    Write-Host ('  trace: ' + ($trace -split "`n")[0].Trim())
    Write-Host ('  log  : ' + $l)
    Assert ($trace -match 'min=0\.040') 'the player was ducked to 4% while boosted'
    Assert (Test-Restored $player $orig) '... and restored afterwards'
    Assert ($l -match 'glitches=0\b') 'a clean run counts no glitch (the stray one at stop was a miscount)'
}
finally { Stop-Player $player $orig }
Complete-Test
