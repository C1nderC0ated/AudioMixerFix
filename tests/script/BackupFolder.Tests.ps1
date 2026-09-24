trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
# Backups go next to the script, or to %LOCALAPPDATA%\AudioMixerFix\backups when that
# folder cannot be written; and an error inside one step is a [FAIL] line for that step,
# not the end of the whole run. The REAL script runs here, top to bottom, as its own
# powershell.exe from scratch "kit" folders - only the five step functions are replaced
# by stubs (inserted just before main, so they override the real ones), and the stubs
# make their backups through the real Ensure-BackupDir and $BackupDir. Nothing on the
# machine is touched; %LOCALAPPDATA% points at a scratch folder for each run.
$src  = [IO.File]::ReadAllText((Get-KitFile 'Fix-AudioMixer.ps1'))
$mark = '# ---- main ----'
$at   = $src.IndexOf($mark)
Assert ($at -gt 0) 'found where main starts in the script under test'
$stubs = @'
function Fix-BleachBit { Section 'stub bleachbit'; Ensure-BackupDir; Set-Content -LiteralPath (Join-Path $BackupDir 'bleachbit.ini.bak') -Value 'x'; Report OK ('STUB bleachbit, backup in ' + $BackupDir) }
function Fix-Store     { Section 'stub store'; Ensure-BackupDir; Set-Content -LiteralPath (Join-Path $BackupDir 'store.reg') -Value 'x'; Report OK 'STUB store' }
function Fix-Services  { Section 'stub services'; if ($env:AMF_STUB_THROW) { throw 'stub boom' }; Report OK 'STUB services' }
function Fix-Browsers  { Section 'stub browsers'; Report OK 'STUB browsers' }
function Show-Status([bool]$Full) { Section 'stub status'; Report OK 'STUB status' }

'@
$child = $src.Substring(0, $at) + $stubs + $src.Substring($at)
$root  = New-TestScratch 'kits'

function New-Kit([string]$name) {
    $k = Join-Path $root $name
    New-Item -ItemType Directory -Force -Path $k | Out-Null
    [IO.File]::WriteAllText((Join-Path $k 'Fix-AudioMixer.ps1'), $child, (New-Object Text.ASCIIEncoding))
    $k
}
function New-LocalAppData([string]$name) { $l = Join-Path $root $name; New-Item -ItemType Directory -Force -Path $l | Out-Null; $l }
function Invoke-Kit([string]$kit, [string]$lad, [bool]$throw = $false) {
    $oldLad = $env:LOCALAPPDATA
    try {
        $env:LOCALAPPDATA = $lad
        $env:AMF_STUB_THROW = $(if ($throw) { '1' } else { '' })
        $o = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $kit 'Fix-AudioMixer.ps1') 2>&1 | Out-String
        $rc = $LASTEXITCODE
    } finally { $env:LOCALAPPDATA = $oldLad; $env:AMF_STUB_THROW = '' }
    [pscustomobject]@{ Out = $o; Rc = $rc }
}
function Has([string]$out, [string]$s) { $out.IndexOf($s, [StringComparison]::Ordinal) -ge 0 }
function All-Steps([string]$out) { (Has $out 'STUB bleachbit') -and (Has $out 'STUB store') -and (Has $out 'STUB services') -and (Has $out 'STUB browsers') -and (Has $out 'STUB status') }

Write-Host '  -- an ordinary writable kit folder --'
$k1 = New-Kit 'writable'; $l1 = New-LocalAppData 'lad1'
$r = Invoke-Kit $k1 $l1
Assert ($r.Rc -eq 0) ('exit code 0 (got {0})' -f $r.Rc)
Assert (Has $r.Out '== Summary ==') 'Summary printed'
Assert (All-Steps $r.Out) 'all five steps ran'
Assert (Test-Path -LiteralPath (Join-Path $k1 'backups\bleachbit.ini.bak')) 'the backup landed next to the script'
Assert (-not (Has $r.Out 'backups go to')) 'no fallback announced'
Assert (@(Get-ChildItem -LiteralPath (Join-Path $k1 'backups') -Force -Filter '.write-test.*').Count -eq 0) 'no test-write file left behind'

Write-Host '  -- the kit folder cannot be written (a "backups" FILE stands in for write-protected media) --'
$k2 = New-Kit 'ro'; Set-Content -LiteralPath (Join-Path $k2 'backups') -Value 'not a folder'
$l2 = New-LocalAppData 'lad2'
$r = Invoke-Kit $k2 $l2
$fb = Join-Path $l2 'AudioMixerFix\backups'
Assert ($r.Rc -eq 0) ('exit code 0 (got {0})' -f $r.Rc)
Assert (Has $r.Out '== Summary ==') 'Summary printed - the run was not aborted (it used to end at the first backup)'
Assert (All-Steps $r.Out) 'every step ran'
Assert ((Has $r.Out 'backups go to') -and (Has $r.Out $fb)) 'the fallback folder is announced by path'
Assert ((Test-Path -LiteralPath (Join-Path $fb 'bleachbit.ini.bak')) -and (Test-Path -LiteralPath (Join-Path $fb 'store.reg'))) 'both backups landed in the fallback folder'

Write-Host '  -- no folder can be written at all --'
$k3 = New-Kit 'ro2'; Set-Content -LiteralPath (Join-Path $k3 'backups') -Value 'not a folder'
$l3 = New-LocalAppData 'lad3'; Set-Content -LiteralPath (Join-Path $l3 'AudioMixerFix') -Value 'not a folder'
$r = Invoke-Kit $k3 $l3
Assert ($r.Rc -eq 1) ('exit code 1 (got {0})' -f $r.Rc)
Assert (Has $r.Out '== Summary ==') 'Summary printed'
Assert ((Has $r.Out '[FAIL] BleachBit rule did not finish') -and (Has $r.Out '[FAIL] Volume store did not finish')) 'the two steps that need a backup are [FAIL]'
Assert ((Has $r.Out 'so nothing was changed here') -and -not (Has $r.Out 'STUB bleachbit') -and -not (Has $r.Out 'STUB store')) '... and stopped before changing anything'
Assert ((Has $r.Out 'STUB services') -and (Has $r.Out 'STUB browsers') -and (Has $r.Out 'STUB status')) 'the other steps still ran'
Assert (Has $r.Out '2 failure(s)') 'the Summary counts both failures'

Write-Host '  -- a step fails in a way nobody anticipated --'
$k4 = New-Kit 'boom'; $l4 = New-LocalAppData 'lad4'
$r = Invoke-Kit $k4 $l4 $true
Assert ($r.Rc -eq 1) ('exit code 1 (got {0})' -f $r.Rc)
Assert (Has $r.Out '== Summary ==') 'Summary printed'
Assert ($r.Out -match '\[FAIL\] Audio services did not finish \(line \d+\): stub boom') 'the [FAIL] names the step, the line and the error'
Assert ((Has $r.Out 'STUB browsers') -and (Has $r.Out 'STUB status')) 'the steps after it still ran'

Write-Host '  -- a real read-only folder (Everyone denied adding files and folders) --'
$k5 = New-Kit 'acl'; $l5 = New-LocalAppData 'lad5'
& icacls $k5 /deny '*S-1-1-0:(WD,AD)' | Out-Null
try {
    $enforced = $true; try { [IO.File]::WriteAllText((Join-Path $k5 'x.txt'), 'x'); $enforced = $false } catch { }
    if ($enforced) {
        $r = Invoke-Kit $k5 $l5
        Assert (($r.Rc -eq 0) -and (Has $r.Out 'backups go to') -and (Has $r.Out '== Summary ==')) 'falls back and completes'
    } else { Write-Host '     (the deny entry is not enforced for this account - skipped)' }
} finally { & icacls $k5 /remove:d '*S-1-1-0' | Out-Null }

Write-Host '  -- a "backups" folder that exists but cannot be written --'
$k6 = New-Kit 'existing-ro'; $b6 = Join-Path $k6 'backups'; New-Item -ItemType Directory -Force -Path $b6 | Out-Null
$l6 = New-LocalAppData 'lad6'
& icacls $b6 /deny '*S-1-1-0:(WD,AD)' | Out-Null
try {
    $enforced = $true; try { [IO.File]::WriteAllText((Join-Path $b6 'x.txt'), 'x'); $enforced = $false } catch { }
    if ($enforced) {
        $r = Invoke-Kit $k6 $l6
        Assert (($r.Rc -eq 0) -and (Has $r.Out 'backups go to') -and (Test-Path -LiteralPath (Join-Path $l6 'AudioMixerFix\backups\bleachbit.ini.bak'))) 'existing is not the same as writable: the fallback is used'
    } else { Write-Host '     (the deny entry is not enforced for this account - skipped)' }
} finally { & icacls $b6 /remove:d '*S-1-1-0' | Out-Null }

Complete-Test
