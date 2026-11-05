trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\AudioRig.ps1"
# Every booster start "self-heals" sliders left at 4% by a booster that died. A dead line
# for an app that a LIVE booster is holding right now must not be healed: that un-ducked
# the running boost, so the app played at full volume under the 25x copy. Three ways it
# used to happen: a dead line beside the live one; a copy renamed to Booster.exe, whose
# own lines counted as dead (a live booster is now recognised by the event it holds, not
# by its exe name); and two boosted apps of the same name, where one closing turned the
# line they share into an orphan.
# Then the list of apps the booster has ever held at 4% (ducked-before.txt): an app saved
# at 4% because a restore came too late - the app closing in the same moment, a shutdown, a
# kill - has no note left to say so, and used to start at 4% every time ("everything is half
# as loud until I open the booster"). Such an app, found at exactly 4% with no note, is put
# back on the second pass that sees it; one at any other level, or one the booster never
# held, is left alone. And a boost of an app found at 4% ends at the level it really had.
Build-Rig
$state = $script:NoteFile
$history = $script:HistoryFile
$renamedDir = Join-Path $Rig.Dir 'renamed'
New-Item -ItemType Directory -Force -Path $renamedDir | Out-Null
Copy-Item -LiteralPath (Join-Path $Rig.Dir 'AppVolumeBooster.exe') -Destination (Join-Path $renamedDir 'Booster.exe')
$player = Start-Player 90
$orig = Probe get $player.Id
Write-Host ('  private player pid {0}; original state {1} (restored at the end)' -f $player.Id, $orig)
$second = $null
try {
    foreach ($case in @(@('AppVolumeBooster.exe', (Join-Path $Rig.Dir 'AppVolumeBooster.exe')), @('Booster.exe (renamed copy)', (Join-Path $renamedDir 'Booster.exe')))) {
        Write-Host ('  -- {0} --' -f $case[0])
        $a = Start-Booster -Exe $case[1] -BoosterArgs @('--pid', $player.Id, '--boost', '100', '--seconds', '8') -Log (Join-Path $Rig.Dir ('a' + [guid]::NewGuid().ToString('N') + '.log'))
        Start-Sleep -Milliseconds 1500
        Write-Host ('     booster A holds it : ' + (Probe get $player.Id))
        # a dead (orphan) line for the same app, as a crashed earlier run would leave
        Add-Content -LiteralPath $state -Value '0|powershell|1.0000'
        Write-Host ('     state file         : ' + ((Get-Content -LiteralPath $state) -join ' / '))
        # another instance starts; its start-up self-heal sees that dead line
        $w = Start-Process (Join-Path $Rig.Dir 'probe.exe') -ArgumentList 'watch', $player.Id, 2500, (Q (Join-Path $Rig.Dir 'w.txt')) -PassThru -WindowStyle Hidden
        Start-Sleep -Milliseconds 300
        Invoke-Booster @('--pid', 999999) -Exe $case[1] | Out-Null
        $w.WaitForExit()
        $trace = (Get-Content -LiteralPath (Join-Path $Rig.Dir 'w.txt') -Raw).Trim()
        Write-Host ('     meanwhile          : ' + ($trace -split "`n")[0].Trim())
        Assert ($trace -match 'max=0\.040') ('{0}: the slider A holds was never un-ducked by the other instance''s self-heal' -f $case[0])
        $a.WaitForExit()
        Assert (Test-Restored $player $orig) ('{0}: A restored it normally when it stopped' -f $case[0])
        if (Test-Path -LiteralPath $state) { Remove-Item -LiteralPath $state -Force }
    }

    Write-Host '  -- two boosted apps of the same name, and one of them closes --'
    $second = Start-Player 60
    $a = Start-Booster -BoosterArgs @('--pid', $player.Id, '--pid', $second.Id, '--boost', '100', '--seconds', '10') -Log (Join-Path $Rig.Dir 'two.log')
    Start-Sleep -Milliseconds 2000
    Write-Host ('     both held          : {0} and {1}' -f (Probe get $player.Id), (Probe get $second.Id))
    Stop-Process -Id $second.Id          # "the other one closes"; the booster restores its slider
    Start-Sleep -Milliseconds 800
    $note = if (Test-Path -LiteralPath $state) { (Get-Content -LiteralPath $state) -join ' / ' } else { '(none)' }
    Write-Host ('     state file         : ' + $note)
    Assert ($note -match ('(^| / )' + $a.Id + '\|powershell\|')) 'the line both shared still belongs to the live booster (it used to be turned into an orphan)'
    $w = Start-Process (Join-Path $Rig.Dir 'probe.exe') -ArgumentList 'watch', $player.Id, 2500, (Q (Join-Path $Rig.Dir 'w2.txt')) -PassThru -WindowStyle Hidden
    Start-Sleep -Milliseconds 300
    Invoke-Booster @('--pid', 999999) | Out-Null
    $w.WaitForExit()
    $trace = (Get-Content -LiteralPath (Join-Path $Rig.Dir 'w2.txt') -Raw).Trim()
    Write-Host ('     meanwhile          : ' + ($trace -split "`n")[0].Trim())
    Assert ($trace -match 'max=0\.040') 'the app still boosted was never un-ducked by a self-heal pass'
    $a.WaitForExit()
    Assert (Test-Restored $player $orig) 'and it was restored normally when the boost ended'
    Assert (-not (Test-Path -LiteralPath $state) -or (Get-Content -LiteralPath $state -Raw) -notmatch 'powershell') 'no line is left in the note afterwards - not even for the app that closed mid-boost'

    Write-Host '  -- the list of apps ever held at 4%: an app saved at 4% with no note --'
    $was = [regex]::Match($orig, 'vol=([0-9.]+)').Groups[1].Value
    Assert ((Test-Path -LiteralPath $history) -and (Get-Content -LiteralPath $history -Raw) -match ('(?m)^powershell\|' + [regex]::Escape($was))) ('the boosts above put the player''s app on the list, with its real level ({0})' -f $(if (Test-Path -LiteralPath $history) { ((Get-Content -LiteralPath $history) -join ' / ') } else { '(no file)' }))
    if (Test-Path -LiteralPath $state) { Remove-Item -LiteralPath $state -Force }
    Probe set $player.Id 0.04 0 | Out-Null
    Start-Sleep -Milliseconds 300
    $passes = Probe heal 3
    Write-Host ('     three repair passes: ' + (($passes -split "`r?`n") -join ' / '))
    Assert (($passes -split "`r?`n")[0] -eq '-' -and ($passes -split "`r?`n")[1] -match 'powershell') 'the first pass only takes note, the second puts it back (a boost just starting may not have written its note yet)'
    Assert (Test-Restored $player $orig) 'the player is back at its real level, although no note said so'
    Probe set $player.Id 0.3 0 | Out-Null
    Start-Sleep -Milliseconds 300
    Probe heal 3 | Out-Null
    Assert ((Probe get $player.Id) -match 'vol=0\.300') 'at any other level (30%) it is left alone - that is the user''s own setting'
    $kept = [IO.File]::ReadAllText($history)
    Remove-Item -LiteralPath $history -Force
    Probe set $player.Id 0.04 0 | Out-Null
    Start-Sleep -Milliseconds 300
    Probe heal 3 | Out-Null
    Assert ((Probe get $player.Id) -match 'vol=0\.040') 'an app the booster never held is left alone even at 4%'
    [IO.File]::WriteAllText($history, $kept)

    Write-Host '  -- a boost of an app found at 4% ends at the level the app really had --'
    $r = Invoke-Booster @('--pid', $player.Id, '--boost', '100', '--seconds', '2')
    Write-Host ('     log: ' + $r.Log)
    Assert (Test-Restored $player $orig) ('after the boost the player is at {0}, not at the 4% it was found at' -f (Probe get $player.Id))
}
finally {
    Stop-Player $player $orig
    if ($second -and -not $second.HasExited) { Stop-Player $second $orig }
}
Complete-Test
