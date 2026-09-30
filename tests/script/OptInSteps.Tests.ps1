trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\ScriptStubs.ps1"
# Three steps that change something only after a check: -DisableEnhancements needs a
# verified backup - and writes the switch where Windows reads it, the endpoint's
# FxProperties key (it used to write it under Properties, print [OK], and change nothing) -
# the BleachBit rule is not edited under a running BleachBit, and the store's integrity
# label is re-applied unless it is exactly the one Windows uses. The "registry" here is the
# test key; reg.exe is a stub.
$Elevated = $true; $CheckOnly = $false
$BackupDir = New-TestScratch 'bk'
function Ensure-BackupDir { }
function Test-PerUserAllowed { $true }
$root = "$TestKey\optin"

Write-Host '  -- -DisableEnhancements with a failing backup --'
. ([scriptblock]::Create((Get-ShippedFunction 'Disable-Enhancements')))
New-Item -Path "$root\ep\Properties" -Force | Out-Null
New-Item -Path "$root\ep\FxProperties" -Force | Out-Null
$SysFxValue = '{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5'
function Get-ActiveRenderEndpoints { [pscustomobject]@{ Guid = '{fake}'; State = 1; Name = 'Speakers (fake)'; Path = "$root\ep" } }
function reg.exe { $global:Calls.Add('reg.exe ' + ($args -join ' ')); if ($global:ExportFails) { $global:LASTEXITCODE = 1; return }; Set-Content -LiteralPath $args[2] -Value 'x'; $global:LASTEXITCODE = 0 }
Reset-Log; $global:ExportFails = $true
Disable-Enhancements
$v = (Get-ItemProperty "$root\ep\FxProperties" -ErrorAction SilentlyContinue).$SysFxValue
Assert ((Count-Reports '`[FAIL`]*backup of the endpoint key FAILED*') -eq 1) 'the failed backup is a FAIL'
Assert ($null -eq $v) 'and the endpoint was left unchanged'
Reset-Log; $global:ExportFails = $false
Disable-Enhancements
$v = (Get-ItemProperty "$root\ep\FxProperties" -ErrorAction SilentlyContinue).$SysFxValue
Assert ($v -eq 1 -and (Count-Reports '`[OK`]*backup of endpoint key saved*') -eq 1) 'with a good backup it is changed, and the [OK] is true'
Assert ($null -eq (Get-ItemProperty "$root\ep\Properties" -ErrorAction SilentlyContinue).$SysFxValue) 'the switch went into FxProperties, where Windows keeps it - nothing was written under Properties'
Reset-Log
Disable-Enhancements
Assert ((Count-Reports '`[OK`]*enhancements already OFF*') -eq 1) 'a second run finds it already off (it never did, reading the wrong key)'
Write-Host '  -- an output with no effects at all, and the preview without admin rights --'
Remove-Item -LiteralPath "$root\ep\FxProperties" -Recurse -Force
Reset-Log
Disable-Enhancements
Assert ((Count-Reports '`[OK`]*no effects are registered for this output*') -eq 1 -and (Count-Reports '`[FAIL`]*') -eq 0) 'no FxProperties key: nothing to turn off, and nothing is created'
New-Item -Path "$root\ep\FxProperties" -Force | Out-Null
$Elevated = $false; $CheckOnly = $true
Reset-Log
Disable-Enhancements
Assert ((Count-Reports '`[INFO`] WOULD set enhancements OFF for: Speakers (fake)') -eq 1 -and (Count-Reports '`[FAIL`]*') -eq 0) '-CheckOnly previews in an ordinary window (it used to be refused with [FAIL])'
$CheckOnly = $false
Reset-Log
Disable-Enhancements
Assert ((Count-Reports '`[FAIL`] Requires admin*') -eq 1 -and $null -eq (Get-ItemProperty "$root\ep\FxProperties" -ErrorAction SilentlyContinue).$SysFxValue) 'the real run still refuses without elevation, and writes nothing'
$Elevated = $true

Write-Host '  -- the BleachBit rule while BleachBit is open --'
. ([scriptblock]::Create((Get-ShippedFunction 'Fix-BleachBit')))
$fake = New-TestScratch 'appdata'
New-Item -ItemType Directory -Force -Path "$fake\BleachBit" | Out-Null
$ini = "$fake\BleachBit\bleachbit.ini"; $env:APPDATA = $fake
Set-Content -LiteralPath $ini -Value @('[tree]', 'winapp2_windows.windows_volume_mixer = True') -Encoding UTF8
function Get-Process { param($Name, $ErrorAction) if ($global:BleachOpen -and $Name -like 'bleachbit*') { [pscustomobject]@{ Name = 'bleachbit' } } }
Reset-Log; $global:BleachOpen = $true
Fix-BleachBit
Assert ((Count-Reports '`[WARN`] BleachBit is open*') -eq 1) 'says BleachBit must be closed first'
Assert ((Get-Content -LiteralPath $ini -Raw) -match 'mixer = True') 'and did not edit the file under it'
Reset-Log; $global:BleachOpen = $false
Fix-BleachBit
Assert ((Get-Content -LiteralPath $ini -Raw) -match 'mixer = False') 'with BleachBit closed the rule is disabled'

Write-Host '  -- the ini keeps its bytes: UTF-8, with or without a BOM, LF or CRLF --'
# Get-Content took a BOM-less file for ANSI, so a non-ASCII path in it came back as mojibake;
# Set-Content -Encoding UTF8 then added a BOM and turned LF into CRLF
$cyr = -join [char[]](0x041C, 0x0430, 0x0440, 0x0438, 0x044F)
foreach ($c in @(@($false, "`n", 'no BOM, LF'), @($true, "`r`n", 'BOM, CRLF'))) {
    $body = (@('[whitelist/paths]', "0_path = C:\Users\$cyr\Music", '', '[tree]', 'winapp2_windows.windows_volume_mixer = True', '') -join $c[1])
    [IO.File]::WriteAllText($ini, $body, (New-Object Text.UTF8Encoding($c[0])))
    Reset-Log
    Fix-BleachBit
    $b = [IO.File]::ReadAllBytes($ini)
    $hasBom = ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)
    $skip = if ($hasBom) { 3 } else { 0 }
    $txt = (New-Object Text.UTF8Encoding($false)).GetString($b, $skip, $b.Length - $skip)
    Assert ($txt -ceq $body.Replace('mixer = True', 'mixer = False')) ('{0}: only the rule line changed - the non-ASCII path and the line endings are intact' -f $c[2])
    Assert ($hasBom -eq $c[0]) ('{0}: the BOM is {1}' -f $c[2], $(if ($c[0]) { 'kept' } else { 'not added' }))
}

Write-Host '  -- which store labels are accepted --'
foreach ($f in 'Test-LowLabel', 'Repair-ChildLabels', 'Fix-Store') { . ([scriptblock]::Create((Get-ShippedFunction $f))) }
function Initialize-RegLabel { }
Add-Type -TypeDefinition @'
public static class RegLabel {
    public static string Current = "";
    public static int SetLowCalls = 0;
    public static string Get(string sub) { return Current; }
    public static string SetLow(string sub) { SetLowCalls++; Current = "S:(ML;OICI;NW;;;LW)"; return "OK"; }
}
'@
New-Item -Path "$root\store" -Force | Out-Null
$Stores = @(@{ Name = 'canonical'; Reg = 'HKCU\FAKE'; PS = "$root\store"; Sub = 'FAKE' })
function Get-ExistingStores { @($Stores) }
foreach ($c in @(@('S:(ML;;NW;;;LW)', 1, 'Low but NOT inherited -> re-applied'),
                 @('S:(ML;OICI;NWNR;;;LW)', 1, 'Low but also no-read-up -> re-applied'),
                 @('S:(ML;OICI;NW;;;LW)', 0, 'exactly right -> left alone'))) {
    [RegLabel]::Current = $c[0]; [RegLabel]::SetLowCalls = 0
    Reset-Log
    Fix-Store
    Assert ([RegLabel]::SetLowCalls -eq $c[1]) ('{0,-24} {1}' -f $c[0], $c[2])
}

Complete-Test
