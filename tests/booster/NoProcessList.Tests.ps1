trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\AudioRig.ps1"
. "$PSScriptRoot\..\lib\SourceVariants.ps1"
# A per-app boost needs the list of running processes: it is how the booster knows which
# helper processes belong to the chosen app, and how it rules out that it is running INSIDE
# that app - the case that feeds its own output back into itself, loudly. When the list
# could not be read, the booster used to carry on with an empty one: every check on it then
# passed whatever the truth. It refuses to start now, before anything is touched.
# The empty list comes from a test-only build; the target is the private player.
Build-Rig
New-NoProcessListSource $Rig.Src (Join-Path $Rig.Dir 'nolist.cs')
$exe = Join-Path $Rig.Dir 'nolist\AppVolumeBooster.exe'
New-Build (Join-Path $Rig.Dir 'nolist.cs') $exe | Out-Null
$player = Start-Player 30
$orig = Probe get $player.Id
Write-Host ('  private player pid {0}; original state {1}' -f $player.Id, $orig)
try {
    $watch = Join-Path $Rig.Dir 'watch.txt'
    $w = Start-Process (Join-Path $Rig.Dir 'probe.exe') -ArgumentList 'watch', $player.Id, 4000, (Q $watch) -PassThru -WindowStyle Hidden
    Start-Sleep -Milliseconds 500
    $r = Invoke-Booster @('--pid', $player.Id, '--boost', '100', '--seconds', '2') -Exe $exe
    $w.WaitForExit()
    Write-Host ('  log: ' + $r.Log)
    Assert ($r.Code -eq 1 -and $r.Log -match 'the list of running processes could not be read') 'the boost is refused, and the log says why'
    $seen = if (Test-Path -LiteralPath $watch) { (Get-Content -LiteralPath $watch -Raw).Trim() } else { '' }
    Write-Host ('  player during that: ' + (($seen -split "`r?`n")[0]))
    Assert ($seen -match 'samples=\d{2,}' -and $seen -notmatch 'vol=0\.040') 'the player''s slider was never lowered'
    Assert (Test-Restored $player $orig) 'and it is at its own level afterwards'
}
finally { Stop-Player $player $orig }
Complete-Test
