trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\AudioRig.ps1"
# The booster keeps a crash-recovery note (booster-state.txt) next to its exe. From a
# read-only folder that write used to fail silently, so a crash mid-boost left nothing to
# repair the slider with. It now falls back to %LOCALAPPDATA%\AppVolumeBooster, and removes
# the note again after a clean stop. The read-only folder is a scratch copy with write
# denied to the current account.
Build-Rig
$fallbackDir = Join-Path $env:LOCALAPPDATA 'AppVolumeBooster'
$fallback = Join-Path $fallbackDir 'booster-state.txt'
$hadFallbackDir = Test-Path -LiteralPath $fallbackDir
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
        $seen = @{ ExeDir = $false; Fallback = $false }
        $deadline = (Get-Date).AddSeconds(8)
        while (-not $b.HasExited -and (Get-Date) -lt $deadline) {
            if (Test-Path -LiteralPath (Join-Path (Split-Path $exe) 'booster-state.txt')) { $seen.ExeDir = $true }
            if (Test-Path -LiteralPath $fallback) { $seen.Fallback = $true }
            Start-Sleep -Milliseconds 100
        }
        $b.WaitForExit()
        $seen
    }
    Write-Host '  -- a writable folder: the note sits next to the exe, as documented --'
    $s = Watch-Boost (Join-Path $Rig.Dir 'AppVolumeBooster.exe')
    Assert ($s.ExeDir -and -not $s.Fallback) 'written next to the exe, not to the fallback'
    Write-Host '  -- a read-only folder --'
    $s = Watch-Boost (Join-Path $ro 'AppVolumeBooster.exe')
    Assert (-not $s.ExeDir) 'nothing written into the read-only folder'
    Assert ($s.Fallback) 'the note went to %LOCALAPPDATA%\AppVolumeBooster instead'
    Assert (-not (Test-Path -LiteralPath $fallback)) '... and is removed again after a clean stop'
    Assert (Test-Restored $player $orig) 'the boost itself worked and restored the player'
}
finally {
    if ($player) { Stop-Player $player $orig }
    & icacls $ro /remove:d $me | Out-Null
    if (-not $hadFallbackDir -and (Test-Path -LiteralPath $fallbackDir) -and -not (Get-ChildItem -LiteralPath $fallbackDir)) { Remove-Item -LiteralPath $fallbackDir }
}
Complete-Test
