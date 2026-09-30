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
Build-Rig
$state = $script:NoteFile
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
}
finally {
    Stop-Player $player $orig
    if ($second -and -not $second.HasExited) { Stop-Player $second $orig }
}
Complete-Test
