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

Complete-Test
