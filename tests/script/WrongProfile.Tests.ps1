trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\ScriptStubs.ps1"
# When UAC elevation runs the script as a DIFFERENT account (a standard user typing an
# administrator's password), HKCU, %APPDATA% and the Desktop belong to that other account,
# and every per-user fix used to land on the wrong profile while printing [OK]. The desktop
# owner (explorer.exe) is the real user; a step must refuse, by name, when they differ.
foreach ($f in 'Get-DesktopOwnerSid', 'Test-WrongProfile', 'Test-PerUserAllowed', 'Fix-BleachBit') {
    . ([scriptblock]::Create((Get-ShippedFunction $f)))
}
$MyIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$CheckOnly = $false
$BackupDir = New-TestScratch 'bk'
function Ensure-BackupDir { }

Write-Host '  -- the real desktop owner of this session (must NOT fire) --'
$real = Get-DesktopOwnerSid
Write-Host ('     desktop owner SID : ' + $real)
Write-Host ('     this process SID  : ' + $MyIdentity.User.Value)
$script:WrongProfileCache = $null
if ($real) { Assert ($real -eq $MyIdentity.User.Value) 'the desktop owner is found and is this user (run the tests as the signed-in user)' }
else { Write-Host '     (no explorer.exe in this session - the owner check is skipped, as the script skips it)' }
Assert (-not (Test-WrongProfile)) 'Test-WrongProfile is false in the normal case'

Write-Host '  -- a simulated over-the-shoulder elevation: the desktop belongs to someone else --'
$fakeAppData = New-TestScratch 'appdata'
New-Item -ItemType Directory -Force -Path "$fakeAppData\BleachBit" | Out-Null
$ini = "$fakeAppData\BleachBit\bleachbit.ini"
Set-Content -LiteralPath $ini -Value @('[tree]', 'winapp2_windows.windows_volume_mixer = True') -Encoding UTF8
$env:APPDATA = $fakeAppData
function Get-DesktopOwnerSid { 'S-1-5-21-1111111111-2222222222-3333333333-1001' }
$script:WrongProfileCache = $null
Reset-Log
Fix-BleachBit
Assert ((Count-Reports '`[FAIL`] Skipped the BleachBit rule*') -eq 1) 'refuses with a FAIL that names the step'
Assert ((Get-Content -LiteralPath $ini -Raw) -match 'windows_volume_mixer = True') 'the wrong profile''s file was NOT edited'

Write-Host '  -- the same account, desktop owner matching again --'
function Get-DesktopOwnerSid { $MyIdentity.User.Value }
$script:WrongProfileCache = $null
Reset-Log
Fix-BleachBit
Assert ((Get-Content -LiteralPath $ini -Raw) -match 'windows_volume_mixer = False') 'with the right owner the rule IS disabled'

Complete-Test
