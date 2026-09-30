trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\ScriptStubs.ps1"
# The default fix's store step, and what -Status shows of the store. The "stores" are keys
# under the test key, reg.exe and the label calls are stubs: the real volume store is never
# read or written here.
#   * The canonical key is created whenever IT is missing. It used to be created only when
#     no variant existed at all, so a machine with just the empty Multimedia placeholder -
#     the state the README describes after a reinstall - got the placeholder labelled, an
#     [OK], and still no store.
#   * -CheckOnly gives a verdict on each label and says what the fix would do.
#   * A label that cannot be set is a [FAIL] (it was a [WARN], exit code 0).
#   * Saved entries that lack the label (restored from a .reg backup) get it too.
#   * -Status lists the saved apps by name with their saved level, and adds a note for an
#     app saved at exactly 4%, the level the volume booster holds an app at - as [INFO],
#     because 4% can also be the user's own choice.
#   * -Status warns about a label that is not the Low one - except on an empty Multimedia
#     placeholder next to the canonical store, which Windows does not use.
$Elevated = $true; $CheckOnly = $false
$BackupDir = New-TestScratch 'bk'
function Ensure-BackupDir { }
function Initialize-RegLabel { }
function Test-PerUserAllowed { $true }
foreach ($f in 'Test-LowLabel', 'Get-LabelText', 'Show-LabelVerdict', 'Repair-ChildLabels', 'Fix-Store', 'Get-StoreEntries', 'Show-StoreEntries') { . ([scriptblock]::Create((Get-ShippedFunction $f))) }
$LOW = 'S:(ML;OICI;NW;;;LW)'
$canonSub = 'Software\AudioMixerFixTest\S\Canon'; $varSub = 'Software\AudioMixerFixTest\S\Variant'
$Stores = @(
    @{ Name = 'canonical (IE LowRegistry)'; Reg = "HKCU\$canonSub"; PS = "HKCU:\$canonSub"; Sub = $canonSub },
    @{ Name = 'variant (Multimedia\Audio)'; Reg = "HKCU\$varSub";   PS = "HKCU:\$varSub";   Sub = $varSub }
)
function Get-ExistingStores { @($Stores | Where-Object { Test-Path $_.PS }) }
Add-Type -TypeDefinition @'
using System.Collections.Generic;
public static class RegLabel {
    public static Dictionary<string, string> L = new Dictionary<string, string>();
    public static List<string> SetCalls = new List<string>();
    public static string FailOn = "";
    public static string Get(string sub) { return L.ContainsKey(sub) ? L[sub] : "(medium/none)"; }
    public static string SetLow(string sub) { SetCalls.Add(sub); if (FailOn != "" && sub == FailOn) return "set-err:5"; L[sub] = "S:(ML;OICI;NW;;;LW)"; return "OK"; }
}
'@
function reg.exe {
    $global:Calls.Add('reg.exe ' + ($args -join ' '))
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'add') {
        if ($global:AddFails) { $global:LASTEXITCODE = 1; return }
        New-Item -Path ('HKCU:\' + ($args[1] -replace '^HKCU\\', '')) -Force | Out-Null
    } elseif ($args[0] -eq 'export') { Set-Content -LiteralPath $args[2] -Value 'x' }
}
function Set-Case([bool]$Canon, [bool]$Variant) {
    Reset-Log
    Remove-Item -LiteralPath "$TestKey\S" -Recurse -Force -ErrorAction SilentlyContinue
    if ($Canon) { New-Item -Path $Stores[0].PS -Force | Out-Null }
    if ($Variant) { New-Item -Path $Stores[1].PS -Force | Out-Null }
    [RegLabel]::L.Clear(); [RegLabel]::SetCalls.Clear(); [RegLabel]::FailOn = ''
    $global:AddFails = $false
}
function Count-Calls([string]$like) { @($global:Calls | Where-Object { $_ -like $like }).Count }

Write-Host '  -- only the empty Multimedia placeholder exists --'
Set-Case $false $true
Fix-Store
Assert ((Count-Calls "reg.exe add HKCU\$canonSub /f") -eq 1 -and (Test-Path $Stores[0].PS)) 'the canonical key is created'
Assert ([RegLabel]::Get($canonSub) -eq $LOW -and [RegLabel]::Get($varSub) -eq $LOW) 'and both keys carry the Low label'
Assert ((Count-Reports '`[OK`] canonical (IE LowRegistry): applied Low label*') -eq 1) 'with an [OK] for the canonical one'

Write-Host '  -- the same machine, with -CheckOnly --'
Set-Case $false $true
$CheckOnly = $true
Fix-Store
$CheckOnly = $false
Assert ((Count-Reports '`[WARN`] The canonical store key is missing - WOULD create it*') -eq 1) 'says the canonical key is missing and would be created'
Assert ((Count-Reports '`[WARN`] variant (Multimedia\Audio): exists, but its label is (none = Medium) - WOULD apply the Low label*') -eq 1) 'gives a verdict on the label it found, not just the raw text'
Assert ((Count-Calls 'reg.exe*') -eq 0 -and [RegLabel]::SetCalls.Count -eq 0 -and -not (Test-Path $Stores[0].PS)) 'and changes nothing'
Set-Case $true $false
[RegLabel]::L[$canonSub] = $LOW
$CheckOnly = $true; Fix-Store; $CheckOnly = $false
Assert ((Count-Reports '`[WARN`]*') -eq 0 -and (Count-Reports '`[INFO`] canonical (IE LowRegistry): exists, label OK*') -eq 1) 'a healthy store gets no warning'

Write-Host '  -- the key cannot be created --'
Set-Case $false $false
$global:AddFails = $true
Fix-Store
Assert ((Count-Reports '`[FAIL`] Could not create the store key (reg.exe exit 1)*') -eq 1 -and (Count-Reports '`[OK`]*') -eq 0) 'a [FAIL], and no [OK]'

Write-Host '  -- the label cannot be set --'
Set-Case $true $false
[RegLabel]::FailOn = $canonSub
Fix-Store
Assert ((Count-Reports '`[FAIL`] canonical (IE LowRegistry): could not set Low label (set-err:5)*') -eq 1 -and (Count-Reports '`[OK`]*') -eq 0) 'a [FAIL] (it used to be a [WARN], and the run ended with exit code 0)'

Write-Host '  -- saved entries that came back from a .reg file without the label --'
Set-Case $true $false
[RegLabel]::L[$canonSub] = $LOW
foreach ($e in 'aaaa_0', 'bbbb_0', 'cccc_0') { New-Item -Path ("HKCU:\$canonSub\$e\{219ED5A0-9CBF-4F3A-B927-37C9E5C5F14F}") -Force | Out-Null }
[RegLabel]::L["$canonSub\aaaa_0"] = $LOW; [RegLabel]::L["$canonSub\aaaa_0\{219ED5A0-9CBF-4F3A-B927-37C9E5C5F14F}"] = 'S:AI(ML;OICIID;NW;;;LW)'   # as Windows reports an inherited one
Fix-Store
Assert ([RegLabel]::SetCalls.Count -eq 4 -and @([RegLabel]::SetCalls | Where-Object { $_ -like '*aaaa_0*' }).Count -eq 0) 'the two entries without it (and their subkeys) are labelled; the one that has it is left alone'
Assert ((Count-Reports '`[OK`] canonical (IE LowRegistry): applied the Low label to 4 saved entries that lacked it.') -eq 1) 'and the run says how many'
Reset-Log; [RegLabel]::SetCalls.Clear()
Fix-Store
Assert ([RegLabel]::SetCalls.Count -eq 0 -and (Count-Reports '*saved entr*') -eq 0) 'a second run finds nothing left to do'

Write-Host '  -- what -Status says about a label --'
$medium = '(none = Medium)'
Set-Case $true $true
Show-LabelVerdict $Stores[0] 12 $LOW
Assert ($global:Reports.Count -eq 0) 'the Low label: nothing to say'
Show-LabelVerdict $Stores[1] 0 $medium
Assert ((Count-Reports '`[INFO`] variant (Multimedia\Audio): an empty placeholder without the Low label*') -eq 1 -and (Count-Reports '`[WARN`]*') -eq 0) 'an empty placeholder next to the canonical store: information, no warning (it used to say "Run the default fix")'
Reset-Log
Show-LabelVerdict $Stores[1] 3 $medium
Assert ((Count-Reports '`[WARN`] variant (Multimedia\Audio): that is not the Low label*') -eq 1) 'the same key with saved entries in it: a warning'
Reset-Log
Show-LabelVerdict $Stores[0] 0 $medium
Assert ((Count-Reports '`[WARN`] canonical (IE LowRegistry): that is not the Low label*') -eq 1) 'the canonical store: a warning, empty or not'
Set-Case $false $true
Show-LabelVerdict $Stores[1] 0 $medium
Assert ((Count-Reports '`[WARN`] variant (Multimedia\Audio): that is not the Low label*') -eq 1) 'the placeholder with no canonical store beside it: a warning'
$statusCode = @((Get-ShippedFunction 'Show-Status') -split "`r?`n" | Where-Object { $_.Trim() -notmatch '^#' }) -join "`n"
Assert ($statusCode -match '(?m)^\s*Show-LabelVerdict \$s \$k\.SubKeyCount \$lbl\s*$') 'and Show-Status uses it for every store it finds'

Write-Host '  -- what -Status shows of the saved entries --'
Set-Case $true $false
function New-Entry([string]$key, [string]$id, $vol, [bool]$mute) {
    $k = "HKCU:\$canonSub\$key"
    New-Item -Path "$k\{219ED5A0-9CBF-4F3A-B927-37C9E5C5F14F}" -Force | Out-Null
    Set-ItemProperty -LiteralPath $k -Name '(default)' -Value $id
    if ($null -ne $vol) {
        New-ItemProperty -LiteralPath "$k\{219ED5A0-9CBF-4F3A-B927-37C9E5C5F14F}" -Name '3' -PropertyType Binary -Value ([byte[]](4, 0, 0, 0, 0, 0, 0, 0) + [BitConverter]::GetBytes([single]$vol)) | Out-Null
        New-ItemProperty -LiteralPath "$k\{219ED5A0-9CBF-4F3A-B927-37C9E5C5F14F}" -Name '5' -PropertyType Binary -Value ([byte[]](11, 0, 0, 0, 0, 0, 0, 0) + [BitConverter]::GetBytes([int16]$(if ($mute) { -1 } else { 0 })) + [byte[]](0, 0, 0, 0, 0, 0)) | Out-Null
    }
}
$dev = '{0.0.0.00000000}.{11111111-2222-3333-4444-555555555555}'
New-Entry 'k1_0' "$dev|\Device\HarddiskVolume3\Games\StarGame.exe%b{00000000-0000-0000-0000-000000000000}" 0.04 $false
New-Entry 'k2_0' "$dev|\Device\HarddiskVolume3\Program Files\AIMP\AIMP.exe%b{00000000-0000-0000-0000-000000000000}" 0.5 $true
New-Entry 'k3_0' "{0.0.0.00000000}.{99999999-2222-3333-4444-555555555555}|\Device\HarddiskVolume3\Program Files\AIMP\AIMP.exe%b{00000000-0000-0000-0000-000000000000}" 1.0 $false
New-Entry 'k4_0' "$dev|#%b{A9EF3FD9-4240-455E-A4D5-F2B3301887B2}" 0.4 $false
New-Entry 'k5_0' "$dev|\Device\HarddiskVolume3\Apps\NoLevel.exe%b{00000000-0000-0000-0000-000000000000}" $null $false
$e = @(Get-StoreEntries $Stores[0])
Assert ($e.Count -eq 5 -and [math]::Abs((@($e | Where-Object { $_.Name -eq 'StarGame.exe' })[0].Vol) - 0.04) -lt 0.0001) 'the saved level is read from the entry (StarGame.exe: 0.04)'
Assert (@($e | Where-Object { $_.Name -eq 'AIMP.exe' -and $_.Mute }).Count -eq 1) 'and the mute flag'
Reset-Log
$out = (Show-StoreEntries $Stores[0] 6>&1 | Out-String)
$out -split "`r?`n" | Where-Object { $_.Trim() } | ForEach-Object { Write-Host ('   ' + $_) }
Assert ($out -match '(?m)^\s+- AIMP\.exe  50% muted, 100%  \(2 entries\)\s*$') 'one line per app, with every level saved for it'
Assert ($out -match '(?m)^\s+- \(system sounds\)  40%\s*$' -and $out -match '(?m)^\s+- NoLevel\.exe  no level saved\s*$') 'system sounds and an entry without a level are shown as such'
Assert ((Count-Reports '`[INFO`] StarGame.exe is saved at 4%. If you did not set that yourself*volume booster*') -eq 1 -and (Count-Reports '*is saved at 4%*') -eq 1) 'an app saved at exactly 4% gets a note - and only that one'
Assert ((Count-Reports '`[WARN`]*') -eq 0) 'as information, not as a warning: 4% can be the user''s own level'

Complete-Test
