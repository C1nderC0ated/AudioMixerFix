trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\ScriptStubs.ps1"
# What -Status says about audio effects (APOs) on an output, and where the enhancements
# switch lives. Both are in the endpoint's FxProperties key. Drivers list their effects as
# REG_MULTI_SZ, which the old filter skipped - it showed one third-party APO on the machine
# this was written on and missed six on the same output. The "endpoint" here is a key under
# the test key.
foreach ($f in 'Get-EffectClsids', 'Resolve-ApoVendor') { . ([scriptblock]::Create((Get-ShippedFunction $f))) }
$fxKey = "$TestKey\ep\FxProperties"
New-Item -Path $fxKey -Force | Out-Null
$A = '{11111111-AAAA-4AAA-8AAA-111111111111}'; $B = '{22222222-BBBB-4BBB-8BBB-222222222222}'; $C = '{33333333-cccc-4ccc-8ccc-333333333333}'
$MODE = '{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}'; $PAGE = '{44444444-DDDD-4DDD-8DDD-444444444444}'
New-ItemProperty -LiteralPath $fxKey -Name '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},0' -PropertyType String -Value '{00000000-0000-0000-0000-000000000000}' | Out-Null
New-ItemProperty -LiteralPath $fxKey -Name '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},13' -PropertyType MultiString -Value @($A) | Out-Null
New-ItemProperty -LiteralPath $fxKey -Name '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},14' -PropertyType MultiString -Value @($B, $C) | Out-Null
New-ItemProperty -LiteralPath $fxKey -Name '{1f7d339a-5380-41a5-ad81-e7b6a9a0a802},7' -PropertyType String -Value $C.ToUpper() | Out-Null
New-ItemProperty -LiteralPath $fxKey -Name '{d3993a3f-99c2-4402-b5ec-a92a0367664b},5' -PropertyType MultiString -Value @($MODE) | Out-Null
New-ItemProperty -LiteralPath $fxKey -Name '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},3' -PropertyType String -Value $PAGE | Out-Null
New-ItemProperty -LiteralPath $fxKey -Name '{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5' -PropertyType DWord -Value 1 | Out-Null
New-ItemProperty -LiteralPath $fxKey -Name 'SomeText' -PropertyType String -Value 'not a class id' | Out-Null

Write-Host '  -- the effect class ids of an output --'
$fx = Get-ItemProperty -LiteralPath $fxKey
$ids = @(Get-EffectClsids $fx)
Write-Host ('     ' + ($ids -join ', '))
Assert ($ids -contains $A -and $ids -contains $B -and $ids -contains $C.ToUpper()) 'class ids in REG_MULTI_SZ values are found (they used to be skipped: only plain strings counted)'
Assert ($ids.Count -eq 3) 'one that is listed twice is reported once'
Assert ($ids -notcontains $MODE) 'a signal-processing MODE id is not taken for an effect'
Assert ($ids -notcontains $PAGE -and $ids -notcontains '{00000000-0000-0000-0000-000000000000}') 'nor the settings page, nor the empty slot'

Write-Host '  -- a class id with no registration --'
$r = Resolve-ApoVendor $MODE      # a mode id is never a registered class
Assert ($r.Dll -eq '' -and $r.Name -eq '(unregistered)') 'Resolve-ApoVendor returns no DLL for it'
$status = Get-ShippedFunction 'Show-Status'
$code = @(($status -split "`r?`n") | Where-Object { $_.Trim() -notmatch '^#' }) -join "`n"
Assert ($code -match 'Where-Object \{ \$_\.Dll \} \| Group-Object Dll' -and $code -match 'Where-Object \{ -not \$_\.Dll \}') 'Show-Status names a DLL only for the ones that have one (Split-Path on the empty string used to end the status with a [FAIL])'
Assert ($code -match 'Get-EffectClsids \$fx') 'and takes its list from Get-EffectClsids'

Write-Host '  -- where the enhancements switch is read --'
Assert ($code -match '\$fx\.\$SysFxValue' -and $code -notmatch '\$props\.\$SysFxValue') 'Show-Status reads the switch from FxProperties, not from Properties'

Complete-Test
