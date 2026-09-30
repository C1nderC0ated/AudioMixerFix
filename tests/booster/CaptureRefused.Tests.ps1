trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\AudioRig.ps1"
. "$PSScriptRoot\..\lib\SourceVariants.ps1"
# When one of several chosen apps cannot be captured (its capture is refused, or times
# out), the others are boosted and the window says which one was left out. That app used
# to stay in the target list, so it was ducked to 4% anyway - with no boosted copy to make
# up for it, it played quieter than before the boost. A test-only build refuses the first
# capture; two private players stand in for the two apps.
Build-Rig
New-RefuseFirstCaptureSource $Rig.Src (Join-Path $Rig.Dir 'refuse.cs')
New-Build (Join-Path $Rig.Dir 'refuse.cs') (Join-Path $Rig.Dir 'refuse\AppVolumeBooster.exe') | Out-Null
$first = Start-Player 60
$orig = Probe get $first.Id
$other = Start-Player 60
Write-Host ('  private players pid {0} (its capture will be refused) and {1}; original state {2}' -f $first.Id, $other.Id, $orig)
try {
    $log = Join-Path $Rig.Dir 'r.log'
    $b = Start-Booster -Exe (Join-Path $Rig.Dir 'refuse\AppVolumeBooster.exe') -BoosterArgs @('--pid', $first.Id, '--pid', $other.Id, '--boost', '100', '--seconds', '5') -Log $log
    $w = Start-Process (Join-Path $Rig.Dir 'probe.exe') -ArgumentList 'watch', $first.Id, 3500, (Q (Join-Path $Rig.Dir 'w1.txt')) -PassThru -WindowStyle Hidden
    $t2 = Probe watch $other.Id 3500
    $w.WaitForExit()
    $t1 = (Get-Content -LiteralPath (Join-Path $Rig.Dir 'w1.txt') -Raw).Trim()
    $b.WaitForExit()
    $l = (Get-Content -LiteralPath $log -Raw).Trim()
    Write-Host ('  refused app : ' + ($t1 -split "`n")[0].Trim())
    Write-Host ('  other app   : ' + ($t2 -split "`n")[0].Trim())
    Write-Host ('  log         : ' + $l)
    Assert ($t1 -match 'min=0\.440') 'the app whose capture was refused was never ducked'
    Assert ($t2 -match 'min=0\.040') 'the other app was ducked and boosted as usual'
    Assert ($l -match 'warning=Some apps could not be captured: powershell \(test: capture refused\)') 'and the run says which app was left out'
    Assert ((Test-Restored $first $orig) -and (Test-Restored $other $orig)) 'both are at their own level afterwards'
}
finally { Stop-Player $first $orig; Stop-Player $other $orig }
Complete-Test
