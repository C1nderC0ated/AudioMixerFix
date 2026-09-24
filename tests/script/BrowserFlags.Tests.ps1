trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\ScriptStubs.ps1"
# Chromium honours only the LAST --disable-features on a command line, so a second switch
# silently re-enabled every feature the user had disabled in their own. The two features
# are merged into the existing switch instead, and -Revert removes only those two. Tested
# on the merge function directly and on a throwaway shortcut - never a real one.
. ([scriptblock]::Create((Get-ShippedFunction 'Merge-DisableFeatures')))
$F = @('AudioServiceSandbox', 'AudioServiceOutOfProcess')
$ours = '--disable-features=AudioServiceSandbox,AudioServiceOutOfProcess'

Write-Host '  -- merging in --'
$cases = @(
    @('', $ours),
    @('--incognito', "--incognito $ours"),
    @('--disable-features=Translate', '--disable-features=Translate,AudioServiceSandbox,AudioServiceOutOfProcess'),
    @("--disable-features=Translate $ours", '--disable-features=Translate,AudioServiceSandbox,AudioServiceOutOfProcess'),
    @('--disable-features=AudioServiceSandbox', $ours),
    @($ours, $ours),
    @('--disable-features="Translate,X" --foo', '--disable-features=Translate,X,AudioServiceSandbox,AudioServiceOutOfProcess --foo'),
    @('--app="My  App" --disable-features=A', '--app="My  App" --disable-features=A,AudioServiceSandbox,AudioServiceOutOfProcess')
)
foreach ($c in $cases) {
    $got = Merge-DisableFeatures $c[0] $F @()
    Assert ($got -ceq $c[1]) ('[{0}] -> [{1}]' -f $c[0], $got)
}
Write-Host '  -- taking out again (-Revert) --'
$rev = @(
    @('--disable-features=Translate,AudioServiceSandbox,AudioServiceOutOfProcess', '--disable-features=Translate'),
    @("--incognito $ours", '--incognito'),
    @("--x $ours --y", '--x --y')
)
foreach ($c in $rev) {
    $got = Merge-DisableFeatures $c[0] @() $F
    Assert ($got -ceq $c[1]) ('[{0}] -> [{1}]' -f $c[0], $got)
}

Write-Host '  -- the real Fix-Browsers step on a throwaway shortcut --'
. ([scriptblock]::Create((Get-ShippedFunction 'Fix-Browsers')))
$CheckOnly = $false; $Revert = $false
$BrowserExes = @('thorium.exe'); $BrowserFeatures = $F
$BackupDir = New-TestScratch 'bk'
function Ensure-BackupDir { }
function Backup-Shortcut($l) { }
function Test-PerUserAllowed { $true }
$tmp = New-TestScratch 'lnk'
Set-Content -LiteralPath "$tmp\thorium.exe" -Value 'x'
$w = New-Object -ComObject WScript.Shell
$s = $w.CreateShortcut("$tmp\Thorium.lnk"); $s.TargetPath = "$tmp\thorium.exe"; $s.Arguments = '--disable-features=Translate'; $s.Save()
function Get-Shortcuts { Get-ChildItem -LiteralPath $tmp -Filter *.lnk }
Reset-Log
Fix-Browsers
$after = $w.CreateShortcut("$tmp\Thorium.lnk").Arguments
Write-Host ('     shortcut now: ' + $after)
Assert ($after -ceq '--disable-features=Translate,AudioServiceSandbox,AudioServiceOutOfProcess') 'the user''s Translate stays disabled - no second switch'
Reset-Log
Fix-Browsers
Assert ((Count-Reports '*already present*') -eq 1) 'a second run sees it as done'
$Revert = $true
Fix-Browsers
$after = $w.CreateShortcut("$tmp\Thorium.lnk").Arguments
Write-Host ('     after -Revert: ' + $after)
Assert ($after -ceq '--disable-features=Translate') '-Revert removes only our two features'

Complete-Test
