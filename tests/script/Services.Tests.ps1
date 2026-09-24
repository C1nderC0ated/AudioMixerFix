trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\ScriptStubs.ps1"
# Every failure in the services step ends in one catch - a missing service, WMI refusing,
# a denied Set-Service - so its message must name the real error and assume nothing. It
# used to call all of them "not found". Get-Service, Get-CimInstance and Set-Service are
# stubs here; no real service is queried or changed.
. ([scriptblock]::Create((Get-ShippedFunction 'Fix-Services')))
$Elevated = $true; $CheckOnly = $false
function Get-Service { param($Name, $ErrorAction) if ($global:NoSuchService -contains $Name) { throw "Cannot find any service with service name '$Name'." }; [pscustomobject]@{ Status = 'Running' } }
function Get-CimInstance { param($ClassName, $Filter) [pscustomobject]@{ StartMode = 'Manual' } }
function Set-Service { param($Name, $StartupType) throw ("Service '{0}' cannot be configured due to the following error: Access is denied" -f $Name) }

Write-Host '  -- Set-Service is denied --'
$global:NoSuchService = @()
Reset-Log; Fix-Services
Write-Host ('     ' + ($global:Reports -join ' | '))
Assert ((Count-Reports '`[FAIL`]*Access is denied*') -eq 2) 'reported for both services, with the real error'
Assert ((Count-Reports '*not found*') -eq 0) '... and not called "not found"'

Write-Host '  -- a service that really is missing --'
$global:NoSuchService = @('Audiosrv')
Reset-Log; Fix-Services
Assert ((Count-Reports '`[FAIL`] Audiosrv*Cannot find any service*') -eq 1) 'still says so'

Complete-Test
