trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\ScriptStubs.ps1"
# -RebuildStore deletes every saved per-app volume and restarts the audio services, so it
# must refuse without a verified backup, never claim what did not happen, always start the
# services again - and the services that depend on them - and end with the CANONICAL store
# present and carrying the Low label. The store here is a key under the test key; reg.exe,
# the label calls and the service cmdlets are stubs: nothing real is touched.
foreach ($f in 'Test-LowLabel', 'Get-RunningAudioDependents', 'Start-AudioDependents', 'Rebuild-Store') { . ([scriptblock]::Create((Get-ShippedFunction $f))) }
$Elevated = $true; $CheckOnly = $false
$BackupDir = New-TestScratch 'bk'
function Ensure-BackupDir { }
function Initialize-RegLabel { }
function Test-PerUserAllowed { $true }
$storeKey = "$TestKey\Store"
$Stores = @(@{ Name = 'canonical (IE LowRegistry)'; Reg = 'HKCU\Software\AudioMixerFixTest\Store'; PS = $storeKey; Sub = 'Software\AudioMixerFixTest\Store' })
function Get-ExistingStores { @($Stores | Where-Object { Test-Path $_.PS }) }   # as shipped: one store comes back as a lone hashtable
Add-Type -TypeDefinition 'public static class RegLabel { public static string Current = ""; public static string SetResult = "OK"; public static int SetLowCalls; public static string Get(string sub) { return Current; } public static string SetLow(string sub) { SetLowCalls++; if (SetResult == "OK") Current = "S:(ML;OICI;NW;;;LW)"; return SetResult; } }'
function reg.exe {
    $global:Calls.Add('reg.exe ' + ($args -join ' '))
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'export') {
        if ($global:ExportFails) { $global:LASTEXITCODE = 1; return }
        Set-Content -LiteralPath $args[2] -Value 'Windows Registry Editor Version 5.00'
    } elseif ($args[0] -eq 'delete') {
        if ($global:DeleteFails) { $global:LASTEXITCODE = 1; return }
        Remove-Item -LiteralPath $storeKey -Recurse -Force -ErrorAction SilentlyContinue
    } elseif ($args[0] -eq 'add') {
        New-Item -Path $storeKey -Force | Out-Null
    }
}
# The services. Audiosrv "recreates" the store when it starts unless a case says otherwise,
# and one vendor service that depends on it was running before.
function Start-Service { param([string]$Name, $ErrorAction)
    $global:Calls.Add("Start-Service $Name")
    if ($global:FailStart -contains $Name) { throw "stub: cannot start $Name" }
    if ($Name -eq 'Audiosrv' -and $global:ServiceRecreates) { New-Item -Path $storeKey -Force | Out-Null }
}
function Get-Service { param([string]$Name, [switch]$DependentServices, $ErrorAction)
    if ($DependentServices) { return @([pscustomobject]@{ Name = 'FakeVendorAudio'; Status = 'Running' }, [pscustomobject]@{ Name = 'FakeIdle'; Status = 'Stopped' }, [pscustomobject]@{ Name = 'Audiosrv'; Status = 'Running' }) }
    [pscustomobject]@{ Name = $Name; Status = 'Running' }
}
function Set-Case([bool]$ExportFails = $false, [bool]$DeleteFails = $false, [string[]]$FailStop = @(), [string[]]$FailStart = @(), [bool]$ServiceRecreates = $true, [string]$LabelAfter = 'S:(ML;OICI;NW;;;LW)', [string]$SetResult = 'OK') {
    Reset-Log
    New-Item -Path $storeKey -Force | Out-Null
    $global:ExportFails = $ExportFails; $global:DeleteFails = $DeleteFails
    $global:FailStop = $FailStop; $global:FailStart = $FailStart
    $global:ServiceRecreates = $ServiceRecreates
    [RegLabel]::Current = $LabelAfter; [RegLabel]::SetResult = $SetResult; [RegLabel]::SetLowCalls = 0
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
Assert (($global:Calls.IndexOf('Start-Service FakeVendorAudio')) -gt ($global:Calls.IndexOf('Start-Service Audiosrv'))) 'the service that depends on Audiosrv and was running is started again afterwards (it used to stay down until a reboot)'
Assert ((Count-Calls 'Start-Service FakeIdle') -eq 0 -and (Count-Calls 'Start-Service Audiosrv') -eq 1) 'one that was not running is left alone, and Audiosrv is not started twice'
Assert ((Count-Reports '`[INFO`] FakeVendorAudio depends on the audio service*running again*') -eq 1) 'and the run says so'

Write-Host '  -- that dependent service will not start again --'
Set-Case -FailStart @('FakeVendorAudio')
Rebuild-Store
Assert ((Count-Reports '`[WARN`] FakeVendorAudio was running before*did not start again*') -eq 1) 'a [WARN] names it'

Write-Host '  -- the service does NOT recreate the store --'
Set-Case -ServiceRecreates $false -LabelAfter '(medium/none)'
Rebuild-Store
Assert ((Count-Calls 'reg.exe add*') -eq 1 -and [RegLabel]::SetLowCalls -eq 1) 'the canonical key is created and labelled explicitly'
Assert ((Count-Reports '`[OK`] Service did not recreate the store; created + Low-labeled it explicitly*') -eq 1) 'and the [OK] says exactly that'

Write-Host '  -- the store comes back WITHOUT the Low label --'
Set-Case -LabelAfter '(medium/none)'
Rebuild-Store
Assert ([RegLabel]::SetLowCalls -eq 1) 'the label is checked and applied ("present again" used to be enough)'
Assert ((Count-Reports $okClaim) -eq 1) 'then it is [OK]'
Set-Case -LabelAfter '(medium/none)' -SetResult 'set-err:5'
Rebuild-Store
Assert ((Count-Reports '`[FAIL`] The canonical store is missing or could not be given the Low label (set-err:5)*') -eq 1 -and (Count-Reports $okClaim) -eq 0) 'and when the label cannot be set it is a [FAIL], with no [OK]'

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

Write-Host '  -- the preview, in an ordinary (not elevated) window --'
$Elevated = $false; $CheckOnly = $true
Set-Case
Rebuild-Store
Write-Host ('     ' + ($global:Reports -join ' | '))
Assert ((Count-Reports '`[FAIL`]*') -eq 0 -and (Count-Reports '`[INFO`] WOULD: backup + delete 1 store(s)*') -eq 1) '-CheckOnly previews without admin rights, and counts ONE store (a lone hashtable used to be counted by its keys: 4)'
Assert ((Count-Calls 'Stop-Service*') -eq 0 -and (Count-Calls 'reg.exe*') -eq 0) 'and touches nothing'
$CheckOnly = $false
Set-Case
Rebuild-Store
Assert ((Count-Reports '`[FAIL`] Requires admin*elevated PowerShell window*') -eq 1 -and (Count-Calls 'reg.exe*') -eq 0) 'the real run still refuses without elevation'

Complete-Test
