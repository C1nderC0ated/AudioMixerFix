trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\AudioRig.ps1"
# The booster has to rebuild on a bare Windows: one .cs file, the C# compiler that ships
# inside .NET Framework 4.x, three references - and warning-clean at level 4, as a 64-bit
# and as a 32-bit build. Everything is built into a scratch folder: the kit's own exe is
# never replaced. Nothing is played.
# The exe that ships has to BE that source, built: every other test compiles the source
# into a scratch folder, so a kit whose exe was never rebuilt after a fix passed them all
# while the file people actually double-click still had the bug (on 2026-09-30 it was a
# week behind its source). Two builds of one source differ only in the time stamp and the
# module id the compiler gives each build; the shipped exe may differ from a fresh build
# only there.
$d = New-TestScratch 'build'
Write-Host ('     compiler: ' + $script:Csc)
$w = New-Build $Rig.Src (Join-Path $d 'AppVolumeBooster.exe') -Warn4
Assert ($w.Count -eq 0) ('AnyCPU build at warning level 4: {0} warning(s)' -f $w.Count)
$w = New-Build $Rig.Src (Join-Path $d 'x86\AppVolumeBooster.exe') -Platform x86 -Warn4
Assert ($w.Count -eq 0) ('32-bit build at warning level 4: {0} warning(s)' -f $w.Count)
$bb = [IO.File]::ReadAllText((Join-Path $Kit 'VolumeBooster\Build-Booster.cmd'))
Assert ($bb -match [regex]::Escape(($script:CscRefs -join ' '))) 'Build-Booster.cmd uses exactly the same three references'

Write-Host '  -- the exe that ships is this source, built --'
$shipSrc = Join-Path $Kit 'VolumeBooster\AppVolumeBooster.cs'
if ($Rig.Src -ne $shipSrc) { Write-Host '     (another source is under test - skipped)' }
else {
    function Get-Diff([byte[]]$x, [byte[]]$y) {
        $r = New-Object System.Collections.Generic.List[int]
        $n = [Math]::Min($x.Length, $y.Length)
        for ($i = 0; $i -lt $n; $i++) { if ($x[$i] -ne $y[$i]) { $r.Add($i) } }
        , $r.ToArray()
    }
    # built the way Build-Booster.cmd builds it: to "AppVolumeBooster.exe.new" (the name goes
    # into the file), with no other switch
    Assert ($bb -match '/out:"%NEW%"' -and $bb -match 'set "NEW=%~dp0AppVolumeBooster\.exe\.new"') 'Build-Booster.cmd still builds to AppVolumeBooster.exe.new (this check builds the same way)'
    New-Build $Rig.Src (Join-Path $d 'one\AppVolumeBooster.exe.new') | Out-Null
    Start-Sleep -Milliseconds 1100          # a different second, so the time stamps differ
    New-Build $Rig.Src (Join-Path $d 'two\AppVolumeBooster.exe.new') | Out-Null
    $a = [IO.File]::ReadAllBytes((Join-Path $d 'one\AppVolumeBooster.exe.new'))
    $b = [IO.File]::ReadAllBytes((Join-Path $d 'two\AppVolumeBooster.exe.new'))
    $shipped = Join-Path $Kit 'VolumeBooster\AppVolumeBooster.exe'
    $s = if (Test-Path -LiteralPath $shipped) { [IO.File]::ReadAllBytes($shipped) } else { New-Object byte[] 0 }
    $pe = [BitConverter]::ToInt32($a, 0x3C)
    $stamp = ($pe + 8)..($pe + 11)                       # the PE header's time stamp
    $own = @((Get-Diff $a $b) | Where-Object { $stamp -notcontains $_ })
    Assert ($a.Length -eq $b.Length -and $own.Count -ge 1 -and ($own[-1] - $own[0]) -lt 16) ('two builds of the same source differ only in the time stamp and one 16-byte id ({0} other bytes, {1} apart)' -f $own.Count, $(if ($own.Count) { $own[-1] - $own[0] } else { 0 }))
    # any 16-byte id that holds every byte that differed lies inside this window
    $lo = $own[-1] - 15; $hi = $own[0] + 15
    $bad = @((Get-Diff $a $s) | Where-Object { $stamp -notcontains $_ -and ($_ -lt $lo -or $_ -gt $hi) })
    $why = if ($s.Length -eq 0) { 'it is missing' } elseif ($s.Length -ne $a.Length) { '{0} bytes, a fresh build has {1}' -f $s.Length, $a.Length } elseif ($bad.Count) { '{0} bytes differ' -f $bad.Count } else { '' }
    Assert ($why -eq '') ('VolumeBooster\AppVolumeBooster.exe is a build of AppVolumeBooster.cs as it is now{0}' -f $(if ($why) { " - it is NOT ($why): run Build-Booster.cmd" } else { '' }))
}
Complete-Test
