# For tests of Fix-AudioMixer.ps1. Dot-source after TestLib.ps1. Replaces every command
# the script's steps use to change the machine with a stub that only records the call:
#
#   Report / Section         recorded in $global:Reports ("[OK] ...", "[FAIL] ...")
#   Stop-Service / Start-Service
#                            recorded in $global:Calls; a name in $global:FailStop or
#                            $global:FailStart makes that call throw, as a stuck service would
#   Stamp                    a fixed time stamp, so backup names are predictable
#   Start-Sleep              returns at once
#   Reset-Log                clears both logs between cases
#   Count-Reports <like>     how many report lines match a -like pattern
#
# A test then loads the step it checks with
#   . ([scriptblock]::Create((Get-ShippedFunction 'Step-Name')))
# at script level (inside a function the definitions would vanish when it returns) and
# stubs whatever else that step reaches for - reg.exe usually, as a function named reg.exe.

$global:Calls   = New-Object System.Collections.Generic.List[string]
$global:Reports = New-Object System.Collections.Generic.List[string]
$global:FailStop = @(); $global:FailStart = @()
function Reset-Log { $global:Calls.Clear(); $global:Reports.Clear() }
function Count-Reports([string]$like) { @($global:Reports | Where-Object { $_ -like $like }).Count }

function Report([string]$Tag, [string]$Msg) { $global:Reports.Add("[$Tag] $Msg") }
function Section([string]$t) { }
function Stamp { '20260101-000000' }
function Start-Sleep { param($Seconds, $Milliseconds) }
function Stop-Service  { param([string]$Name, [switch]$Force, $ErrorAction) $global:Calls.Add("Stop-Service $Name");  if ($global:FailStop  -contains $Name) { throw "stub: cannot stop $Name" } }
function Start-Service { param([string]$Name, $ErrorAction) $global:Calls.Add("Start-Service $Name"); if ($global:FailStart -contains $Name) { throw "stub: cannot start $Name" } }
