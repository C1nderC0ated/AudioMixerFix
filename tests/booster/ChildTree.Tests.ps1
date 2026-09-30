trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\AudioRig.ps1"
. "$PSScriptRoot\..\lib\SourceVariants.ps1"
# A browser started without the kit's flags (and Electron apps such as Discord) plays its
# audio from a helper process that the browser recycles. The booster used to tie the boost
# to whatever pid owned the session: when that helper went away the boost ended with
# "target closed" while the browser played on, a replacement helper was neither ducked nor
# captured, and any process a target spawned mid-boost waited up to 10 s for the next tree
# rebuild before it was ducked - while its audio was already being relayed at full volume.
# The "browser" here is a hidden PowerShell that spawns audio children (the private
# faint-tone players) one after another; nothing else is boosted.
Build-Rig
$sim = Join-Path $Rig.Dir 'tree'
New-Item -ItemType Directory -Force -Path $sim | Out-Null
$ascii = New-Object Text.ASCIIEncoding
[IO.File]::WriteAllText((Join-Path $sim 'child.ps1'), @'
param([string]$Wav, [int]$Seconds)
$p = New-Object Media.SoundPlayer $Wav
$p.PlayLooping()
Start-Sleep -Seconds $Seconds
$p.Stop()
'@, $ascii)
[IO.File]::WriteAllText((Join-Path $sim 'parent.ps1'), @'
param([string]$Wav, [string]$Child, [string]$PidFile, [int]$FirstDelay, [int]$ChildSeconds, [int]$Gap, [int]$Count)
Start-Sleep -Seconds $FirstDelay
for ($n = 1; $n -le $Count; $n++) {
    $c = Start-Process powershell -ArgumentList '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $Child + '"'), '-Wav', ('"' + $Wav + '"'), '-Seconds', $ChildSeconds -PassThru -WindowStyle Hidden
    Add-Content -LiteralPath $PidFile -Value ('c' + $n + '=' + $c.Id)
    $c.WaitForExit()
    Start-Sleep -Seconds $Gap
}
'@, $ascii)
# the wav is made by the first Start-Player; that player is put back at once. Its level
# is what "Windows PowerShell" is saved at on this machine - the end of the test checks it.
$w = Start-Player 3; $saved = Probe get $w.Id; Stop-Player $w $saved
$t0 = [Diagnostics.Stopwatch]::StartNew()
function Say([string]$m) { Write-Host ('     [{0,5:N1}s] {1}' -f $t0.Elapsed.TotalSeconds, $m) }
function Start-Parent([string]$pidFile, [int]$firstDelay, [int]$childSeconds, [int]$gap, [int]$count) {
    if (Test-Path -LiteralPath $pidFile) { Remove-Item -LiteralPath $pidFile }
    Start-Process powershell -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Q (Join-Path $sim 'parent.ps1')), '-Wav', (Q $Rig.Wav), '-Child', (Q (Join-Path $sim 'child.ps1')), '-PidFile', (Q $pidFile), '-FirstDelay', $firstDelay, '-ChildSeconds', $childSeconds, '-Gap', $gap, '-Count', $count -PassThru -WindowStyle Hidden
}
function Wait-ChildPid([string]$pidFile, [string]$key, [int]$timeoutS) {
    $dl = (Get-Date).AddSeconds($timeoutS)
    while ((Get-Date) -lt $dl) {
        if (Test-Path -LiteralPath $pidFile) {
            $txt = ''; try { $txt = [IO.File]::ReadAllText($pidFile) } catch { }
            $m = [regex]::Match($txt, "(?m)^$key=(\d+)")
            if ($m.Success) { return [int]$m.Groups[1].Value }
        }
        Start-Sleep -Milliseconds 100
    }
    throw "child $key never appeared"
}
# follows one child's session until the child exits: when it was first seen, when it first
# read 4%, its last state
function Trace-Session([int]$id, [int]$maxS, [string]$label) {
    $r = @{ Seen = $null; First = ''; Ducked = $null; Last = ''; Gone = $null }
    $dl = (Get-Date).AddSeconds($maxS)
    while ((Get-Date) -lt $dl) {
        if (-not (Get-Process -Id $id -ErrorAction SilentlyContinue)) { $r.Gone = $t0.Elapsed.TotalSeconds; break }
        $s = Probe get $id
        if ($s -match 'vol=') {
            if ($null -eq $r.Seen) { $r.Seen = $t0.Elapsed.TotalSeconds; $r.First = $s; Say "$label session first seen: $s" }
            if ($null -eq $r.Ducked -and $s -match 'vol=0\.040') { $r.Ducked = $t0.Elapsed.TotalSeconds }
            $r.Last = $s
        }
        Start-Sleep -Milliseconds 150
    }
    $r.Delay = if ($r.Ducked) { $r.Ducked - $r.Seen } else { -1 }
    Say ('{0}: {1}' -f $label, $(if ($r.Ducked) { 'ducked {0:N1} s after its session appeared' -f $r.Delay } else { 'NEVER ducked' }))
    $r
}
$parents = @(); $boosters = @()
try {
    Write-Host '  -- 1. only the session-created callback can duck (the watcher''s duck pass is removed): a child spawned mid-boost --'
    New-NoWatcherDuckSource $Rig.Src (Join-Path $Rig.Dir 'nowatch.cs')
    New-Build (Join-Path $Rig.Dir 'nowatch.cs') (Join-Path $Rig.Dir 'nowatch\AppVolumeBooster.exe') | Out-Null
    $pids1 = Join-Path $sim 'p1.txt'
    $pa = Start-Parent $pids1 3 10 1 1; $parents += $pa
    $b1 = Start-Booster -Exe (Join-Path $Rig.Dir 'nowatch\AppVolumeBooster.exe') -BoosterArgs @('--pid', $pa.Id, '--boost', '100', '--seconds', '60') -Log (Join-Path $sim 'b1.log'); $boosters += $b1
    Say ('booster {0} on the parent {1}, which has no audio of its own' -f $b1.Id, $pa.Id)
    $c1 = Wait-ChildPid $pids1 'c1' 30
    $r1 = Trace-Session $c1 40 'c1'
    Assert ($r1.Ducked -and $r1.Delay -le 2.5) ('a session a target spawns mid-boost is ducked at once by the callback (it used to wait for the 10 s tree rebuild, and the callback never fired at all): {0}' -f $(if ($r1.Ducked) { '{0:N1} s' -f $r1.Delay } else { 'never' }))
    $pa.WaitForExit()
    $dl = (Get-Date).AddSeconds(10); while (-not $b1.HasExited -and (Get-Date) -lt $dl) { Start-Sleep -Milliseconds 100 }
    if (-not $b1.HasExited) { Stop-Process -Id $b1.Id -ErrorAction SilentlyContinue }
    Write-Host ('     log: ' + (Get-Content -LiteralPath (Join-Path $sim 'b1.log') -Raw).Trim())

    Write-Host '  -- 2. the checked pid is the audio helper itself, and it gets recycled --'
    $pids2 = Join-Path $sim 'p2.txt'
    $pb = Start-Parent $pids2 1 8 1 2; $parents += $pb
    $h1 = Wait-ChildPid $pids2 'c1' 30
    $dl = (Get-Date).AddSeconds(30); while ((Get-Date) -lt $dl -and -not ((Probe get $h1) -match 'vol=')) { Start-Sleep -Milliseconds 200 }
    $b2 = Start-Booster -BoosterArgs @('--pid', $h1, '--boost', '100', '--seconds', '90') -Log (Join-Path $sim 'b2.log'); $boosters += $b2
    Say ('booster {0} started with --pid {1} (the helper, not its parent {2})' -f $b2.Id, $h1, $pb.Id)
    $q1 = Trace-Session $h1 30 'helper 1'
    Assert ($null -ne $q1.Ducked) 'the helper was ducked'
    Start-Sleep -Milliseconds 1500
    Assert (-not $b2.HasExited) 'the boost survives the helper going away (it used to end with "target closed")'
    $h2 = Wait-ChildPid $pids2 'c2' 30
    # record the booster's own output while helper 2 plays: its tone must be in the copy
    $gOut = Join-Path $sim 'gaps2.txt'
    $gp = Start-Process (Join-Path $Rig.Dir 'probe.exe') -ArgumentList 'gaps', $b2.Id, 9000, (Q $gOut) -PassThru -WindowStyle Hidden
    $q2 = Trace-Session $h2 30 'helper 2'
    Assert ($q2.Ducked -and $q2.Delay -le 2.5) ('the replacement helper is ducked at once ({0})' -f $(if ($q2.Ducked) { '{0:N1} s' -f $q2.Delay } else { 'never' }))
    $gp.WaitForExit()
    $g = if (Test-Path -LiteralPath $gOut) { (Get-Content -LiteralPath $gOut -Raw).Trim() } else { '' }
    Assert ((Get-LogNumber $g 'soundMs') -ge 500) ('and its sound is in the boosted copy - the capture follows the app''s live process tree ({0} ms of sound recorded)' -f (Get-LogNumber $g 'soundMs'))
    $pb.WaitForExit()
    $dl = (Get-Date).AddSeconds(8); while (-not $b2.HasExited -and (Get-Date) -lt $dl) { Start-Sleep -Milliseconds 100 }
    Assert ($b2.HasExited) 'the boost ends when the parent itself exits'
    if (-not $b2.HasExited) { Stop-Process -Id $b2.Id -ErrorAction SilentlyContinue }
    $log2 = (Get-Content -LiteralPath (Join-Path $sim 'b2.log') -Raw).Trim()
    Write-Host ('     log: ' + $log2)
    Assert ($log2 -match 'stopReason=target closed') '... with "target closed"'
}
finally {
    foreach ($p in $parents) { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -ErrorAction SilentlyContinue } }
    Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" | Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -match [regex]::Escape($sim) } | ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
    foreach ($b in $boosters) { if ($b -and -not $b.HasExited) { Stop-Process -Id $b.Id -ErrorAction SilentlyContinue } }
    Start-Sleep -Seconds 1
    # the children exit while ducked; the booster restores their expired sessions, and
    # Windows keeps that as the saved level - checked on a fresh session, repaired if not
    $chk = Start-Player 20
    $state = Probe get $chk.Id
    Assert ($state -eq $saved) ('a fresh PowerShell session starts at the level from before the test ({0})' -f $state)
    Stop-Player $chk $saved       # puts that level back if it is not
}
Complete-Test
