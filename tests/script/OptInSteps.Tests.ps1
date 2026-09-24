trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\ScriptStubs.ps1"
# Three steps that change something only after a check: -DisableEnhancements needs a
# verified backup, the BleachBit rule is not edited under a running BleachBit, and the
# store's integrity label is re-applied unless it is exactly the one Windows uses. The
# "registry" here is the test key; reg.exe is a stub.
$Elevated = $true; $CheckOnly = $false
$BackupDir = New-TestScratch 'bk'
function Ensure-BackupDir { }
function Test-PerUserAllowed { $true }
$root = "$TestKey\optin"

Write-Host '  -- -DisableEnhancements with a failing backup --'
. ([scriptblock]::Create((Get-ShippedFunction 'Disable-Enhancements')))
New-Item -Path "$root\ep\Properties" -Force | Out-Null
$SysFxValue = '{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5'
function Get-ActiveRenderEndpoints { [pscustomobject]@{ Guid = '{fake}'; State = 1; Name = 'Speakers (fake)'; Path = "$root\ep" } }
function reg.exe { $global:Calls.Add('reg.exe ' + ($args -join ' ')); if ($global:ExportFails) { $global:LASTEXITCODE = 1; return }; Set-Content -LiteralPath $args[2] -Value 'x'; $global:LASTEXITCODE = 0 }
Reset-Log; $global:ExportFails = $true
Disable-Enhancements
$v = (Get-ItemProperty "$root\ep\Properties" -ErrorAction SilentlyContinue).$SysFxValue
Assert ((Count-Reports '`[FAIL`]*backup of the endpoint key FAILED*') -eq 1) 'the failed backup is a FAIL'
Assert ($null -eq $v) 'and the endpoint was left unchanged'
Reset-Log; $global:ExportFails = $false
Disable-Enhancements
$v = (Get-ItemProperty "$root\ep\Properties" -ErrorAction SilentlyContinue).$SysFxValue
Assert ($v -eq 1 -and (Count-Reports '`[OK`]*backup of endpoint key saved*') -eq 1) 'with a good backup it is changed, and the [OK] is true'

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

Write-Host '  -- which store labels are accepted --'
. ([scriptblock]::Create((Get-ShippedFunction 'Fix-Store')))
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
