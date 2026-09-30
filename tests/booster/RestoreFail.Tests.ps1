trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\AudioRig.ps1"
. "$PSScriptRoot\..\lib\SourceVariants.ps1"
# A slider restore that fails at Stop (the session object dead after an audio-service
# restart) used to count as success: the crash note was deleted and the window said
# "restored" while the app sat at 4%. Now the note is kept as an orphan line for the next
# run's self-heal, and the stop says so. A test-only build makes every restore fail; the
# player it leaves at 4% is put back by hand at the end.
Build-Rig
New-RestoreFailSource $Rig.Src (Join-Path $Rig.Dir 'nofix.cs')
New-Build (Join-Path $Rig.Dir 'nofix.cs') (Join-Path $Rig.Dir 'restorefail\AppVolumeBooster.exe') | Out-Null
$noteDir = Join-Path $env:LOCALAPPDATA 'AppVolumeBooster'
$note = Join-Path $noteDir 'booster-state.txt'
$hadNoteDir = Test-Path -LiteralPath $noteDir
$noteBefore = if (Test-Path -LiteralPath $note) { [IO.File]::ReadAllText($note) } else { $null }
$player = Start-Player 40
$orig = Probe get $player.Id
Write-Host ('  private player pid {0}; original state {1} (restored at the end)' -f $player.Id, $orig)
try {
    $r = Invoke-Booster @('--pid', $player.Id, '--boost', '100', '--seconds', '2') -Exe (Join-Path $Rig.Dir 'restorefail\AppVolumeBooster.exe')
    Write-Host ('  log: ' + $r.Log)
    $after = Probe get $player.Id
    $noteNow = if (Test-Path -LiteralPath $note) { [IO.File]::ReadAllText($note) } else { '' }
    Write-Host ('  player afterwards: {0}; note: {1}' -f $after, ($noteNow -replace '\s+', ' ').Trim())
    Assert ($after -match 'vol=0\.040') 'the (simulated) failed restore really left the player at 4%'
    Assert ($r.Log -match 'restore=1 mixer slider\(s\) could not be restored') 'the run reports the failed restore instead of claiming success'
    $prior = if ($noteNow -match '(?m)^0\|powershell\|([0-9.]+)') { [double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture) } else { -1 }
    $was = if ($orig -match 'vol=([0-9.]+)') { [double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture) } else { -2 }
    Assert ([math]::Abs($prior - $was) -lt 0.001) ('the note is kept as an orphan line with the real prior ({0}), for the next run''s self-heal' -f $prior)
    Write-Host '  -- the next run''s self-heal repairs it --'
    Invoke-Booster @('--pid', 999999) | Out-Null
    Assert (Test-Restored $player $orig) 'a later booster start restored the player from that line'
    $noteNow = if (Test-Path -LiteralPath $note) { [IO.File]::ReadAllText($note) } else { '' }
    Assert ($noteNow -notmatch '(?m)^0\|powershell\|') 'and resolved the line'
}
finally {
    Stop-Player $player $orig
    if ($null -ne $noteBefore) { [IO.File]::WriteAllText($note, $noteBefore) }
    elseif (Test-Path -LiteralPath $note) { Remove-Item -LiteralPath $note -Force }
    if (-not $hadNoteDir -and (Test-Path -LiteralPath $noteDir) -and -not (Get-ChildItem -LiteralPath $noteDir)) { Remove-Item -LiteralPath $noteDir }
}
Complete-Test
