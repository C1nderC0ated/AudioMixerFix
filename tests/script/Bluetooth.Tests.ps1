trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\ScriptStubs.ps1"
# The Bluetooth switches say [OK] and "a reboot is required" only when the value really
# changed. They used to say it unconditionally, so a failed write sent the user to reboot
# for nothing. The key paths point at the test key (or at nothing); the real Bluetooth key
# is never read or written.
. ([scriptblock]::Create((Get-ShippedFunction 'Set-BtAbsoluteVolume')))
$ErrorActionPreference = 'Stop'      # as in the shipped script
$Elevated = $true; $CheckOnly = $false

Write-Host '  -- the write fails (an invalid root, so reg.exe writes nothing anywhere) --'
$BtCtKey = 'HKLM:\SOFTWARE\NoSuchKey_AudioMixerFixTest'
$BtCtReg = 'HKXX\NoSuchRoot\AudioMixerFixTest'
Reset-Log; Set-BtAbsoluteVolume $true
Write-Host ('     ' + ($global:Reports -join ' | '))
Assert ((Count-Reports '`[OK`]*') -eq 0) 'no [OK]'
Assert ((Count-Reports '`[FAIL`]*DisableAbsoluteVolume = 1*') -eq 1) 'one [FAIL] naming the value'
Assert ((Count-Reports '*REBOOT is required*') -eq 0) 'no "a reboot is required"'

Write-Host '  -- the write works (the test key stands in for the Bluetooth key) --'
$BtCtKey = "$TestKey\CT"
$BtCtReg = 'HKCU\Software\AudioMixerFixTest\CT'
Reset-Log; Set-BtAbsoluteVolume $true
Assert ((Count-Reports '`[OK`] DisableAbsoluteVolume = 1 written.') -eq 1) 'disable: [OK] written'
Assert ((Get-ItemProperty $BtCtKey).DisableAbsoluteVolume -eq 1) '... and the value really is 1'
Reset-Log; Set-BtAbsoluteVolume $false
Assert ((Count-Reports '`[OK`] DisableAbsoluteVolume = 0 written.') -eq 1) 'enable: [OK] written'
Reset-Log; Set-BtAbsoluteVolume $false
Assert ((Count-Reports '`[OK`] Already set*') -eq 1) 'running it again: "Already set"'

Write-Host '  -- reg.exe succeeds, but the value is not where the script reads it --'
# the two copies of the key path ($BtCtKey, $BtCtReg) drifting apart: exit 0 alone must
# not mean [OK]
$BtCtKey = "$TestKey\CT_read"
$BtCtReg = 'HKCU\Software\AudioMixerFixTest\CT_write'
Reset-Log; Set-BtAbsoluteVolume $true
Assert ((Count-Reports '`[OK`]*') -eq 0 -and (Count-Reports '`[FAIL`]*reg.exe exit 0*') -eq 1) '[FAIL], not [OK]'

Complete-Test
