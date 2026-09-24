<#
Runs the kit's tests. Windows PowerShell 5.1, nothing to install. From the kit folder:

  powershell -NoProfile -ExecutionPolicy Bypass -File tests\Run-Tests.ps1
      the fast suites - script, launcher and build. About two and a half minutes; nothing on the
      machine is changed (see tests\README.md for exactly what they touch).

  powershell -NoProfile -ExecutionPolicy Bypass -File tests\Run-Tests.ps1 -Audio
      the booster's audio tests as well. About five more minutes, during which a private
      test process plays a faint tone and gets boosted. Nothing else is boosted.

  ... -Only Launcher,Consistency
      only the test files whose name contains one of these words. A word that matches
      nothing stops the run (a typo would otherwise look like a pass), and an audio
      test's name needs -Audio as well.

Every test file runs in its own powershell.exe. Exit code 0 when every check passed,
1 otherwise.
#>
param([switch]$Audio, [string[]]$Only = @())

$here = $PSScriptRoot
# powershell -File passes "-Only a,b" as the ONE string "a,b" - split it here, or the
# documented form above matches nothing
$Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$suites = @('script', 'launcher', 'build')
if ($Audio) { $suites += 'booster' }
$files = @(foreach ($s in $suites) { Get-ChildItem -LiteralPath (Join-Path $here $s) -Filter '*.Tests.ps1' | Sort-Object Name })
if ($Only.Count -gt 0) {
    $audioFiles = @(Get-ChildItem -LiteralPath (Join-Path $here 'booster') -Filter '*.Tests.ps1')
    foreach ($w in $Only) {
        if (@($files | Where-Object { $_.BaseName -like "*$w*" }).Count -gt 0) { continue }
        if (-not $Audio -and @($audioFiles | Where-Object { $_.BaseName -like "*$w*" }).Count -gt 0) {
            Write-Host ("'{0}' names an audio test - add -Audio to run it." -f $w) -ForegroundColor Yellow
        } else {
            Write-Host ("No test file name contains '{0}'." -f $w) -ForegroundColor Yellow
        }
        exit 1
    }
    $files = @($files | Where-Object { $n = $_.BaseName; @($Only | Where-Object { $n -like "*$_*" }).Count -gt 0 })
}

Write-Host ('Running {0} test file(s) from {1}' -f $files.Count, ($suites -join ', ')) -ForegroundColor White
if ($Audio) {
    Write-Host 'The booster tests take about five minutes. A private PowerShell process plays a faint tone' -ForegroundColor Yellow
    Write-Host '(about -58 dBFS) and only that process is boosted; its mixer volume is put back each time.' -ForegroundColor Yellow
}
$all = [Diagnostics.Stopwatch]::StartNew()
$rows = @()
foreach ($f in $files) {
    $suite = Split-Path (Split-Path $f.FullName) -Leaf
    Write-Host ''
    Write-Host ('== {0}\{1}' -f $suite, $f.Name) -ForegroundColor Cyan
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $out = New-Object System.Collections.Generic.List[string]
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $f.FullName 2>&1 | ForEach-Object {
        $line = "$_"; $out.Add($line)
        if ($line -match '^\s+FAIL ') { Write-Host $line -ForegroundColor Red } else { Write-Host $line }
    }
    $rc = $LASTEXITCODE
    $pass = @($out | Where-Object { $_ -match '^\s+PASS ' }).Count
    $fail = @($out | Where-Object { $_ -match '^\s+FAIL ' }).Count
    # a file that fails without a FAIL line (it crashed, or asserted nothing) still fails
    $ok = ($rc -eq 0 -and $fail -eq 0 -and $pass -gt 0)
    $rows += [pscustomobject]@{ Ok = $ok; Name = "$suite\$($f.Name)"; Pass = $pass; Fail = $fail; Rc = $rc; Sec = [int]$sw.Elapsed.TotalSeconds }
}

Write-Host ''
Write-Host '== summary' -ForegroundColor Cyan
foreach ($r in $rows) {
    $what = if ($r.Ok) { '{0} checks' -f $r.Pass } else { '{0} of {1} failed, exit {2}' -f $r.Fail, ($r.Pass + $r.Fail), $r.Rc }
    Write-Host ('   {0,-5} {1,-40} {2,-28} {3,4} s' -f $(if ($r.Ok) { 'PASS' } else { 'FAIL' }), $r.Name, $what, $r.Sec) -ForegroundColor $(if ($r.Ok) { 'Green' } else { 'Red' })
}
$checks = ($rows | Measure-Object Pass -Sum).Sum + ($rows | Measure-Object Fail -Sum).Sum
$failing = ($rows | Measure-Object Fail -Sum).Sum
$badFiles = @($rows | Where-Object { -not $_.Ok }).Count
Write-Host ('{0} checks in {1} file(s), {2} failing, {3} file(s) failing - {4:N0} s' -f $checks, $rows.Count, $failing, $badFiles, $all.Elapsed.TotalSeconds) -ForegroundColor $(if ($badFiles) { 'Red' } else { 'Green' })
if ($badFiles) { exit 1 } else { exit 0 }
