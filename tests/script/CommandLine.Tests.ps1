trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
# A wrong command line exits 2 and says what was wrong (README: "2 the command line itself
# was wrong"). A misspelled switch used to be rejected by PowerShell itself, before the
# script ran, with exit 1 - the code that means "a fix failed". Every case here stops
# before the first step, so these runs of the real script change nothing.
$ps1 = Get-KitFile 'Fix-AudioMixer.ps1'
$t = $null; $e = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($ps1, [ref]$t, [ref]$e)
$declared = @($ast.ParamBlock.Parameters | Where-Object { $_.StaticType -eq [switch] } | ForEach-Object { '-' + $_.Name.VariablePath.UserPath })
Write-Host ('     switches in param(): ' + ($declared -join ' '))
Assert ($declared.Count -ge 8) ('read the declared switches ({0})' -f $declared.Count)
function Invoke-Script([string[]]$a) {
    $o = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ps1 @a 2>&1 | Out-String
    [pscustomobject]@{ Code = $LASTEXITCODE; Out = $o }
}

Write-Host '  -- a misspelled switch --'
$r = Invoke-Script @('-Statuss')
Assert ($r.Code -eq 2) ('exit code 2 (got {0})' -f $r.Code)
Assert ($r.Out -match '\[FAIL\] Unknown argument\(s\): -Statuss\.') 'the [FAIL] names it'
$listed = if ($r.Out -match 'Valid switches: ([^\r\n]+)\.') { @($Matches[1] -split ' ') } else { @() }
Assert (($listed.Count -eq $declared.Count) -and (@($declared | Where-Object { $listed -notcontains $_ }).Count -eq 0)) 'and lists exactly the switches the script declares'

Write-Host '  -- a stray extra word --'
$r = Invoke-Script @('-Status', 'extra')
Assert ($r.Code -eq 2 -and $r.Out -match 'Unknown argument\(s\): extra\.') ('exit code 2, naming it (got {0})' -f $r.Code)

Write-Host '  -- two action switches at once --'
$r = Invoke-Script @('-Status', '-Revert')
Assert ($r.Code -eq 2 -and $r.Out -match 'Mutually exclusive switches: -Status, -Revert') ('exit code 2, naming both (got {0})' -f $r.Code)

Write-Host '  -- started by a 32-bit PowerShell --'
# A 32-bit PowerShell sees no audio endpoints (a different view of HKLM\SOFTWARE), so the
# script hands the run to the 64-bit one - with its arguments, and passing on its exit code.
$ps32 = Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
if ([Environment]::Is64BitOperatingSystem -and (Test-Path -LiteralPath $ps32)) {
    $o = & $ps32 -NoProfile -ExecutionPolicy Bypass -File $ps1 -Status -Revert 2>&1 | Out-String
    $rc = $LASTEXITCODE
    Assert ($o -match 'continuing in the 64-bit one') 'it says that it hands over'
    Assert ($rc -eq 2 -and $o -match 'Mutually exclusive switches: -Status, -Revert') ('the 64-bit run got the same arguments, and its exit code comes back (got {0})' -f $rc)
    $o = & $ps32 -NoProfile -ExecutionPolicy Bypass -File $ps1 -Statuss 2>&1 | Out-String
    Assert ($LASTEXITCODE -eq 2 -and $o -match 'Unknown argument\(s\): -Statuss\.') 'an unknown argument is passed on too'
    $o = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ps1 -Statuss 2>&1 | Out-String
    Assert ($o -notmatch 'continuing in the 64-bit one') 'a 64-bit PowerShell just runs it'
} else { Write-Host '     (no 32-bit PowerShell on this machine - skipped)' }

Complete-Test
