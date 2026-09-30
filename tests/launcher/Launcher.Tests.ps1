trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
# Fix-AudioMixer.cmd, driven without ever elevating. The branch tests run copies of the
# launcher with fltmc and PowerShell replaced by stubs that return a chosen exit code; the
# relaunch test runs the launcher's own PowerShell line with only "-Verb RunAs" swapped for
# "-Wait" (the same cmd.exe argument handling, no UAC prompt), aimed at stub scripts in
# scratch folders with awkward names. The real worker script never runs.
$src = [IO.File]::ReadAllText((Get-KitFile 'Fix-AudioMixer.cmd'))
$psLine = @($src -split "`r`n" | Where-Object { $_.StartsWith('powershell -NoProfile -Command') })
Assert ($psLine.Count -eq 1) 'found the launcher''s elevation line'
$psLine = $psLine[0]
$work = 'powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Fix-AudioMixer.ps1"'
$scratch = New-TestScratch 'launcher'
$ascii = New-Object Text.ASCIIEncoding

function Invoke-Batch([string]$path, [hashtable]$vars = @{}) {
    $saved = @{}
    foreach ($k in $vars.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $vars[$k]) }
    try {
        $o = & $env:ComSpec /d /c (Q $path) 2>&1 | Out-String
        [pscustomobject]@{ Code = $LASTEXITCODE; Out = $o }
    } finally { foreach ($k in $vars.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) } }
}

Write-Host '  -- the network-drive check, taken verbatim from the launcher --'
$m = [regex]::Match($psLine, '-Command "(try \{ if \(\[IO\.DriveInfo\].*?catch \{\};)')
Assert $m.Success 'found the drive check'
if ($m.Success) {
    foreach ($d in @(@('C:', 'a local disk'), @('\\', 'a UNC path passes through'))) {
        $saved = $env:SELFDRIVE; $env:SELFDRIVE = $d[0]
        & powershell.exe -NoProfile -Command ($m.Groups[1].Value + ' exit 0') | Out-Null
        $rc = $LASTEXITCODE; $env:SELFDRIVE = $saved
        Assert ($rc -eq 0) ('SELFDRIVE={0} -> exit {1} ({2})' -f $d[0], $rc, $d[1])
    }
}
Assert ([IO.DriveType]::Network.ToString() -eq 'Network') 'the enum name it compares against really is "Network"'

Write-Host '  -- every exit-code branch, fltmc and PowerShell stubbed --'
# the launcher first looks for the script next to itself; the copies below sit in $scratch
Set-Content -LiteralPath (Join-Path $scratch 'Fix-AudioMixer.ps1') -Value '# stand-in: never run'
function Invoke-Launcher([string]$name, [int]$fltmc, [int]$ps) {
    $t = $src.Replace('fltmc >nul 2>&1', "cmd /c exit $fltmc")
    $t = $t.Replace($psLine, "echo [WOULD-ELEVATE] & cmd /c exit $ps")
    $t = $t.Replace($work, 'echo [WORKER-RAN] & cmd /c exit 0').Replace('pause', 'echo [PAUSE-REACHED]')
    # every substitution must apply, or the case would quietly test something else
    if ($t.Contains('fltmc >nul') -or ([regex]::Matches($t, '\[WOULD-ELEVATE\]')).Count -ne 1 -or ([regex]::Matches($t, '\[WORKER-RAN\]')).Count -ne 1) { throw "stub substitution did not apply ($name)" }
    $p = Join-Path $scratch "launcher_$name.cmd"
    [IO.File]::WriteAllText($p, $t, $ascii)
    Invoke-Batch $p
}
$r = Invoke-Launcher 'elevated' 0 0
Assert (($r.Out -match '\[WORKER-RAN\]') -and ($r.Out -notmatch '\[WOULD-ELEVATE\]')) 'already elevated -> runs the worker, no elevation call'
$r = Invoke-Launcher 'fneg' -1 0
Assert (($r.Out -notmatch '\[WORKER-RAN\]') -and ($r.Out -match '\[WOULD-ELEVATE\]')) 'fltmc exit -1 is NOT taken as "already elevated" - it asks for elevation'
$r = Invoke-Launcher 'accepted' 1 0
Assert (($r.Code -eq 0) -and ($r.Out -notmatch '\[FAIL\]') -and ($r.Out -notmatch '\[PAUSE-REACHED\]')) 'elevation started -> quiet exit 0'
$r = Invoke-Launcher 'declined' 1 1
Assert (($r.Code -eq 1) -and ($r.Out -match 'declined or failed') -and ($r.Out -notmatch 'network drive')) 'UAC declined -> its own message, exit 1'
$r = Invoke-Launcher 'netdrive' 1 2
Assert (($r.Code -eq 1) -and ($r.Out -match 'mapped network drive') -and ($r.Out -match '\[PAUSE-REACHED\]')) 'exit 2 -> the network-drive message, paused, exit 1'
$r = Invoke-Launcher 'pctpath' 1 3
Assert (($r.Code -eq 1) -and ($r.Out -match 'contains a %NAME% pair') -and ($r.Out -match '\[PAUSE-REACHED\]') -and ($r.Out -notmatch 'declined')) 'exit 3 -> the %NAME% message, paused, exit 1'
$r = Invoke-Launcher 'ps4' 1 4
Assert (($r.Code -eq 1) -and ($r.Out -match 'declined or failed') -and ($r.Out -notmatch 'network drive|NAME% pair')) 'any other code -> the general failure'
$r = Invoke-Launcher 'psneg' 1 -1
Assert (($r.Code -eq 1) -and ($r.Out -match 'declined or failed')) 'a negative code -> a failure too, not a silent exit 0'

Write-Host '  -- the launcher on its own, as Explorer runs it from inside a ZIP --'
$lone = Join-Path $scratch 'lone'
New-Item -ItemType Directory -Force -Path $lone | Out-Null
$t = $src.Replace('fltmc >nul 2>&1', 'cmd /c exit 1').Replace($psLine, 'echo [WOULD-ELEVATE] & cmd /c exit 0')
$t = $t.Replace($work, 'echo [WORKER-RAN] & cmd /c exit 0').Replace('pause', 'echo [PAUSE-REACHED]')
[IO.File]::WriteAllText((Join-Path $lone 'Fix-AudioMixer.cmd'), $t, $ascii)
$r = Invoke-Batch (Join-Path $lone 'Fix-AudioMixer.cmd')
Assert (($r.Code -eq 1) -and ($r.Out -match 'Fix-AudioMixer\.ps1 is not in the folder') -and ($r.Out -match '\[PAUSE-REACHED\]')) 'without Fix-AudioMixer.ps1 next to it -> says so and what to do, paused, exit 1'
Assert ($r.Out -notmatch '\[WOULD-ELEVATE\]|\[WORKER-RAN\]') '... before asking for elevation (it used to go through the prompt and end with PowerShell''s own error)'

Write-Host '  -- the elevated relaunch, from folders with awkward names --'
$psRun = $psLine.Replace('-Verb RunAs', '-Wait -WindowStyle Hidden')
Assert ($psRun -ne $psLine) 'swapped -Verb RunAs for -Wait'
$root = Join-Path $scratch 'relaunch probe'          # a space, like most real paths
New-Item -ItemType Directory -Force -Path $root | Out-Null
$drv = Join-Path $root 'drv.cmd'
[IO.File]::WriteAllText($drv, "@echo off`r`n$psRun`r`n", $ascii)
$cyrillic = -join [char[]](0x041C, 0x0430, 0x0440, 0x0438, 0x044F)
$cases = @(
    @{ N = 'plain' }, @{ N = 'a (1)' }, @{ N = 'R&D' }, @{ N = 'a & b' }, @{ N = 'a ^ b' }, @{ N = 'a@b' },
    @{ N = "it's" }, @{ N = 'a, b' }, @{ N = '100% b' }, @{ N = 'Bo%b & Co' }, @{ N = 'a%NoSuchVar%b' },
    @{ N = 'a^b!c%NoSuchVar%d' }, @{ N = "$cyrillic %NoSuchVar%"; L = '(Cyrillic) %NoSuchVar%' },
    # a caret next to a harmless pair, with no "!" in the path: these were refused
    @{ N = 'a^b%NoSuchVar%c' }, @{ N = 'a%NoSuchVar%^c' },
    @{ N = 'a%OS%b'; Refuse = $true }, @{ N = '%USERNAME% kit'; Refuse = $true }, @{ N = 'a%CD%b'; Refuse = $true },
    @{ N = 'a^%OS%b'; Refuse = $true }, @{ N = 'a!%OS%b'; Refuse = $true }
)
foreach ($c in $cases) {
    $label = if ($c.L) { $c.L } else { $c.N }
    $d = Join-Path $root $c.N
    New-Item -ItemType Directory -Force -Path $d | Out-Null
    $stub = Join-Path $d 'Fix-AudioMixer.cmd'
    $mark = Join-Path $scratch ('ran_' + [guid]::NewGuid().ToString('N') + '.txt')
    [IO.File]::WriteAllText($stub, "@echo off`r`n>`"$mark`" echo ran with: %*`r`n", $ascii)
    $r = Invoke-Batch $drv @{ SELFCMD = $stub; SELFDRIVE = 'C:' }
    $got = if (Test-Path -LiteralPath $mark) { (Get-Content -LiteralPath $mark -Raw).Trim() } else { 'not run' }
    if ($c.Refuse) { Assert (($got -eq 'not run') -and ($r.Code -eq 3)) ('"{0}" -> refused with exit 3, not an elevated window that runs nothing (exit {1}, {2})' -f $label, $r.Code, $got) }
    else { Assert ($got -eq 'ran with: --elevated') ('"{0}" -> relaunched ({1})' -f $label, $got) }
}

Complete-Test
