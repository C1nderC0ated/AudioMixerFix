trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\AudioRig.ps1"
# How the booster learns a process's name and its parent. Both used to be expensive - a
# snapshot of every process and thread on the machine for ONE name (2-3 ms, several times
# per audio session on every pass of the watcher), and a WMI query for the parents (half a
# second for the first one, which was most of the wait after pressing Start). They are
# direct calls now, and have to give exactly what the old ways gave:
#   * the name is the exe name without ".exe" - and only ".exe" - for every running process
#   * no name for a process that has exited, even while something still holds a handle to
#     it (the booster itself does, for its targets) - "is it still running" depends on that
#   * the same parent for every process as WMI reports
#   * the name a program was started under, also after its exe was renamed while it runs.
#     An updater does that to make room for the new version, and the name was read from the
#     file as it is called now: found on 2026-11-12, when one process on the machine was in
#     that state and the "same name as Get-Process" check below failed on it.
# Nothing is played in this test.
Build-Rig
$probe = Join-Path $Rig.Dir 'probe.exe'
$dotted = Join-Path $Rig.Dir 'dotted.name v2.exe'
Copy-Item -LiteralPath $probe -Destination $dotted
$d = Start-Process $dotted -ArgumentList 'sleep', '30000' -PassThru -WindowStyle Hidden
$z = Start-Process $probe -ArgumentList 'sleep', '60000' -PassThru -WindowStyle Hidden
$ren = Join-Path $Rig.Dir 'renamed.exe'
Copy-Item -LiteralPath $probe -Destination $ren
$r = Start-Process $ren -ArgumentList 'sleep', '30000' -PassThru -WindowStyle Hidden
try {
    Start-Sleep -Milliseconds 300
    Rename-Item -LiteralPath $ren -NewName 'renamed.exe.old-20260101-000000'     # Windows lets a running exe be renamed
    $null = $z.Handle            # an open handle: the process object outlives the process
    Stop-Process -Id $z.Id -Force; $z.WaitForExit()
    $gone = 3999999996           # no such process
    $before = @{}; Get-Process | ForEach-Object { $before[[uint32]$_.Id] = $_.ProcessName }
    $wmi = @{}; Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId | ForEach-Object { $wmi[[uint32]$_.ProcessId] = [uint32]$_.ParentProcessId }
    $out = Probe procs ('{0},{1},{2},{3},{4}' -f $PID, $d.Id, $z.Id, $gone, $r.Id)
    $after = @{}; Get-Process | ForEach-Object { $after[[uint32]$_.Id] = $_.ProcessName }

    $ask = @{}; $name = @{}; $parent = @{}; $time = ''
    foreach ($l in ($out -split "`r?`n")) {
        $p = $l -split '\|'
        if ($p[0] -eq 'ask') { $ask[[uint32]$p[1]] = $p[2] }
        elseif ($p[0] -eq 'time') { $time = $l }
        elseif ($p.Count -eq 3) { $name[[uint32]$p[0]] = $p[2]; $parent[[uint32]$p[0]] = [uint32]$p[1] }
    }
    Assert ($ask[[uint32]$PID] -ceq 'powershell') ('this PowerShell is "{0}"' -f $ask[[uint32]$PID])
    Assert ($ask[[uint32]$d.Id] -ceq 'dotted.name v2') ('only ".exe" is taken off: "dotted.name v2.exe" is "{0}"' -f $ask[[uint32]$d.Id])
    Assert ($ask.ContainsKey([uint32]$z.Id) -and $ask[[uint32]$z.Id] -eq '') 'a process that has exited has no name, although a handle to it is still open'
    Assert ($ask.ContainsKey([uint32]$gone) -and $ask[[uint32]$gone] -eq '') 'and neither has a pid that does not exist'
    Assert ((Test-Path -LiteralPath (Join-Path $Rig.Dir 'renamed.exe.old-20260101-000000')) -and -not (Test-Path -LiteralPath $ren) -and -not $r.HasExited) 'a running program''s exe was renamed under it'
    Assert ($ask[[uint32]$r.Id] -ceq 'renamed') ('it keeps the name it was started under: "{0}" (it was read from the file as it is called now: "renamed.exe.old-20260101-000000")' -f $ask[[uint32]$r.Id])
    Assert ($before[[uint32]$r.Id] -ceq 'renamed') ('which is the name Get-Process gives it ("{0}")' -f $before[[uint32]$r.Id])

    # only processes that were there, under the same name, before AND after the probe ran
    $stable = @($name.Keys | Where-Object { $_ -ne 0 -and $before.ContainsKey($_) -and $after.ContainsKey($_) -and $before[$_] -ceq $after[$_] })
    $badName = @($stable | Where-Object { $name[$_] -cne $before[$_] })
    $badName | Select-Object -First 5 | ForEach-Object { Write-Host ('     pid {0}: booster "{1}", Get-Process "{2}"' -f $_, $name[$_], $before[$_]) }
    Assert ($stable.Count -ge 20 -and $badName.Count -eq 0) ('the same name as Get-Process for all {0} running processes' -f $stable.Count)
    $both = @($stable | Where-Object { $wmi.ContainsKey($_) })
    $badParent = @($both | Where-Object { $parent[$_] -ne $wmi[$_] })
    $badParent | Select-Object -First 5 | ForEach-Object { Write-Host ('     pid {0}: booster parent {1}, WMI {2}' -f $_, $parent[$_], $wmi[$_]) }
    Assert ($both.Count -ge 20 -and $badParent.Count -eq 0) ('the same parent as WMI for all {0}' -f $both.Count)
    Assert ($parent[[uint32]$d.Id] -eq [uint32]$PID) 'the process this test started has this PowerShell as its parent'

    Write-Host ('  ' + $time)
    $nameMs = if ($time -match 'nameMs=([0-9.]+)') { [double]$Matches[1] } else { 999 }
    $mapMs = if ($time -match 'mapMs=([0-9.]+)') { [double]$Matches[1] } else { 999 }
    Assert ($nameMs -lt 0.5) ('one name takes {0:N3} ms (median of 200; the snapshot way took 2-3 ms)' -f $nameMs)
    Assert ($mapMs -lt 25) ('the parents of all processes take {0:N1} ms (median of 21; the WMI query took 50-120 ms)' -f $mapMs)
}
finally {
    foreach ($p in $d, $z, $r) { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
}
Complete-Test
