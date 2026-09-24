trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\ScriptStubs.ps1"
# -RebuildStore deletes every saved per-app volume and restarts the audio services, so it
# must refuse without a verified backup, never claim what did not happen, and always start
# the services again. reg.exe and the service cmdlets are stubs: nothing real is touched.
. ([scriptblock]::Create((Get-ShippedFunction 'Rebuild-Store')))
$Elevated = $true; $CheckOnly = $false
$BackupDir = New-TestScratch 'bk'
function Ensure-BackupDir { }
function Initialize-RegLabel { }
function Test-PerUserAllowed { $true }
$Stores = @(@{ Name = 'canonical (IE LowRegistry)'; Reg = 'HKCU\FAKE\Store'; PS = 'Registry::HKEY_CURRENT_USER\FAKE\Store'; Sub = 'FAKE\Store' })
function Get-ExistingStores { @($Stores) }       # the store is "present again" afterwards
function reg.exe {
    $global:Calls.Add('reg.exe ' + ($args -join ' '))
    if ($args[0] -eq 'export') {
        if ($global:ExportFails) { $global:LASTEXITCODE = 1; return }
        Set-Content -LiteralPath $args[2] -Value 'Windows Registry Editor Version 5.00'
        $global:LASTEXITCODE = 0; return
    }
    if ($args[0] -eq 'delete' -and $global:DeleteFails) { $global:LASTEXITCODE = 1; return }
    $global:LASTEXITCODE = 0
}
function Set-Case([bool]$ExportFails = $false, [bool]$DeleteFails = $false, [string[]]$FailStop = @(), [string[]]$FailStart = @()) {
    Reset-Log
    $global:ExportFails = $ExportFails; $global:DeleteFails = $DeleteFails
    $global:FailStop = $FailStop; $global:FailStart = $FailStart
}
function Count-Calls([string]$like) { @($global:Calls | Where-Object { $_ -like $like }).Count }
$okClaim = '`[OK`] Store deleted and audio service restarted*'

Write-Host '  -- the backup export FAILS --'
Set-Case -ExportFails $true
Rebuild-Store
Assert ((Count-Reports '`[FAIL`]*Backup of*refusing*') -eq 1) 'reports FAIL for the failed backup'
Assert ((Count-Calls 'Stop-Service*') -eq 0) 'no service was stopped'
Assert ((Count-Calls 'reg.exe delete*') -eq 0) 'the store was NOT deleted'

Write-Host '  -- everything works --'
Set-Case
Rebuild-Store
Assert ((Count-Reports '`[OK`]*Backed up*') -eq 1) 'reports the verified backup'
Assert ((Count-Calls 'Stop-Service*') -eq 2) 'went on to stop both services'
Assert ((Count-Calls 'reg.exe delete*') -eq 1) 'went on to delete the store'
Assert ((Count-Reports $okClaim) -eq 1) 'and says so with [OK]'

Write-Host '  -- the delete works, AudioEndpointBuilder will not start again --'
Set-Case -FailStart @('AudioEndpointBuilder')
Rebuild-Store
Assert ((Count-Reports $okClaim) -eq 0) 'no [OK] claiming the service restarted'
Assert ((Count-Reports '`[WARN`]*did not restart*') -eq 1) 'says the store is back but the service is not'

Write-Host '  -- the delete itself fails --'
Set-Case -DeleteFails $true
Rebuild-Store
Assert ((Count-Reports $okClaim) -eq 0) 'no [OK] claiming the store was deleted'
Assert ((Count-Reports '`[FAIL`] Could not delete*') -eq 1) 'the failed delete is a FAIL'
Assert ((Count-Reports '*Old entries are gone*') -eq 0) 'does not say old entries are gone'

Write-Host '  -- a stop that half works: Audiosrv stops, AudioEndpointBuilder refuses --'
Set-Case -FailStop @('AudioEndpointBuilder')
try { Rebuild-Store } catch { }
Assert ((Count-Calls 'Stop-Service Audiosrv') -eq 1) 'Audiosrv was stopped before the other stop failed'
Assert ((Count-Calls 'Start-Service Audiosrv') -eq 1) 'Audiosrv is started again instead of being left down (it used to return at once)'
Assert ((Count-Reports '`[FAIL`] Could not stop audio services*') -eq 1) 'and the failed stop is still reported'
Assert ((Count-Calls 'reg.exe delete*') -eq 0) 'nothing was deleted'

Write-Host '  -- the kit sits in a folder with [ ] in its name --'
$BackupDir = Join-Path (New-TestScratch 'bk') 'AudioMixerFix [v2]\backups'
New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
Set-Case
Rebuild-Store
Assert ([IO.File]::Exists((Join-Path $BackupDir 'PropertyStore.canonicalIELowRegistry.20260101-000000.reg'))) 'the backup was really written there'
Assert ((Count-Reports '`[FAIL`]*Backup of*FAILED*') -eq 0) 'it is not reported as FAILED (to -Path, [v2] is a wildcard)'
Assert ((Count-Reports '`[OK`]*Backed up*') -eq 1) 'it is recognised, and the step goes on'

Complete-Test
