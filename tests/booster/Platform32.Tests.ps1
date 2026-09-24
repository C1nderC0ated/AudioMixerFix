trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\AudioRig.ps1"
# Process-loopback activation passes a PROPVARIANT whose BLOB pointer sits at 8 + the
# pointer size. Hard-coded at 16, it was NULL in a 32-bit process and activation failed
# there. Both builds must activate and actually capture audio.
Build-Rig
New-Build $Rig.Src (Join-Path $Rig.Dir 'x86\AppVolumeBooster.exe') -Platform x86 | Out-Null
$player = Start-Player 40
$orig = Probe get $player.Id
Write-Host ('  private player pid {0}; original state {1} (restored at the end)' -f $player.Id, $orig)
try {
    foreach ($v in @(@('AnyCPU (64-bit here)', (Join-Path $Rig.Dir 'AppVolumeBooster.exe')), @('x86 (a 32-bit process)', (Join-Path $Rig.Dir 'x86\AppVolumeBooster.exe')))) {
        $r = Invoke-Booster @('--pid', $player.Id, '--boost', '100', '--seconds', '2') -Exe $v[1]
        Write-Host ('  {0,-22}: {1}' -f $v[0], ($r.Log -replace '^(.{0,150}).*$', '$1'))
        Assert (($r.Log -match '^ok ') -and ((Get-LogNumber $r.Log 'capSamples') -gt 0)) ('{0}: activates process loopback and captures audio' -f $v[0])
    }
    Assert (Test-Restored $player $orig) 'the player was restored'
}
finally { Stop-Player $player $orig }
Complete-Test
