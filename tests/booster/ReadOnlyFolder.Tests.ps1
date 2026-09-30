trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\AudioRig.ps1"
# The booster keeps a crash-recovery note (booster-state.txt) in %LOCALAPPDATA%\AppVolumeBooster
# - one per user, shared by every copy of the exe - and removes it again after a clean stop.
# It used to sit next to the exe: a copy in a read-only folder (a write-protected stick,
# Program Files) then lost the note in an empty catch, and two copies in different folders
# kept separate notes, so the second could take a slider the first held at 4% for the real
# level. The read-only folder here is a scratch copy with write denied to the current account.
Build-Rig
$noteDir = Join-Path $env:LOCALAPPDATA 'AppVolumeBooster'
$note = Join-Path $noteDir 'booster-state.txt'
$hadNoteDir = Test-Path -LiteralPath $noteDir
$noteBefore = if (Test-Path -LiteralPath $note) { [IO.File]::ReadAllText($note) } else { $null }
$ro = Join-Path $Rig.Dir 'readonly'
New-Item -ItemType Directory -Force -Path $ro | Out-Null
Copy-Item -LiteralPath (Join-Path $Rig.Dir 'AppVolumeBooster.exe') -Destination $ro
$me = [Security.Principal.WindowsIdentity]::GetCurrent().Name
# deny adding files and folders (WD,AD) - not (W), which also denies SYNCHRONIZE and then
# Windows refuses to even start the exe from that folder ("Access is denied")
& icacls $ro /deny "${me}:(WD,AD)" | Out-Null
$player = $null
try {
    $writable = $true; try { Set-Content -LiteralPath (Join-Path $ro 'x.txt') -Value 'x' -ErrorAction Stop } catch { $writable = $false }
    Assert (-not $writable) ('the copy''s folder really is read-only for {0}' -f $me)
    $player = Start-Player 40
    $orig = Probe get $player.Id
    Write-Host ('  private player pid {0}; original state {1} (restored at the end)' -f $player.Id, $orig)
    function Watch-Boost([string]$exe) {
        $b = Start-Booster -Exe $exe -BoosterArgs @('--pid', $player.Id, '--boost', '100', '--seconds', '3') -Log (Join-Path $Rig.Dir ('w' + [guid]::NewGuid().ToString('N') + '.log'))
        $seen = @{ ExeDir = $false; Note = $false; NoteText = '' }
        $deadline = (Get-Date).AddSeconds(8)
        while (-not $b.HasExited -and (Get-Date) -lt $deadline) {
            if (Test-Path -LiteralPath (Join-Path (Split-Path $exe) 'booster-state.txt')) { $seen.ExeDir = $true }
            if (Test-Path -LiteralPath $note) { $seen.Note = $true; try { $seen.NoteText = [IO.File]::ReadAllText($note) } catch { } }
            Start-Sleep -Milliseconds 100
        }
        $b.WaitForExit()
        $seen
    }
    Write-Host '  -- a writable folder --'
    $s = Watch-Boost (Join-Path $Rig.Dir 'AppVolumeBooster.exe')
    Assert ($s.Note -and -not $s.ExeDir) 'the note is written to %LOCALAPPDATA%\AppVolumeBooster, not next to the exe'
    $prior = if ($s.NoteText -match '(?m)^\d+\|powershell\|([0-9.]+)') { [double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture) } else { -1 }
    $was = if ($orig -match 'vol=([0-9.]+)') { [double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture) } else { -2 }
    Assert ([math]::Abs($prior - $was) -lt 0.001) ('... naming the target and its real level ({0})' -f ($s.NoteText -replace '\s+', ' ').Trim())
    Assert (-not (Test-Path -LiteralPath $note) -or [IO.File]::ReadAllText($note) -eq [string]$noteBefore) '... and it is gone again after a clean stop'
    Write-Host '  -- a read-only folder --'
    $s = Watch-Boost (Join-Path $ro 'AppVolumeBooster.exe')
    Assert (-not $s.ExeDir) 'nothing is written into the read-only folder (and nothing fails)'
    Assert ($s.Note) 'the note went to %LOCALAPPDATA%\AppVolumeBooster as before'
    Assert (Test-Restored $player $orig) 'the boost itself worked and restored the player'
}
finally {
    if ($player) { Stop-Player $player $orig }
    & icacls $ro /remove:d $me | Out-Null
    # leave the real note file exactly as it was
    if ($null -ne $noteBefore) { [IO.File]::WriteAllText($note, $noteBefore) }
    elseif (Test-Path -LiteralPath $note) { Remove-Item -LiteralPath $note -Force }
    if (-not $hadNoteDir -and (Test-Path -LiteralPath $noteDir) -and -not (Get-ChildItem -LiteralPath $noteDir)) { Remove-Item -LiteralPath $noteDir }
}
Complete-Test
