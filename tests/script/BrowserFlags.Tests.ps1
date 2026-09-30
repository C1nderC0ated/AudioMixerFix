trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\ScriptStubs.ps1"
# Chromium honours only the LAST --disable-features on a command line, so a second switch
# silently re-enabled every feature the user had disabled in their own. The two features
# are merged into the existing switch instead - however that switch is quoted - and -Revert
# removes only those two. A shortcut that cannot hold the result is refused, not reported
# as done. Tested on the merge function directly and on throwaway shortcuts - never a real
# one.
foreach ($f in 'Get-DisableFeatureSwitches', 'Merge-DisableFeatures', 'Test-BrowserTarget') { . ([scriptblock]::Create((Get-ShippedFunction $f))) }
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
    @('--app="My  App" --disable-features=A', '--app="My  App" --disable-features=A,AudioServiceSandbox,AudioServiceOutOfProcess'),
    # a quoted list with a space in it used to be cut at the quote and glued onto our switch
    @('--disable-features="Translate, OptimizationHints" --foo', '--disable-features=Translate,OptimizationHints,AudioServiceSandbox,AudioServiceOutOfProcess --foo'),
    # a switch quoted as a whole was not seen, so a second one was appended - and won
    @('"--disable-features=A,B" --foo', '--disable-features=A,B,AudioServiceSandbox,AudioServiceOutOfProcess --foo'),
    # Chromium lower-cases switch names: this is the same switch
    @('--Disable-Features=A', '--disable-features=A,AudioServiceSandbox,AudioServiceOutOfProcess')
)
foreach ($c in $cases) {
    $got = Merge-DisableFeatures $c[0] $F @()
    Assert ($got -ceq $c[1]) ('[{0}] -> [{1}]' -f $c[0], $got)
    Assert ((Merge-DisableFeatures $got $F @()) -ceq $got -and @(Get-DisableFeatureSwitches $got).Count -eq 1) '   ... one switch, and a second run changes nothing'
}
Write-Host '  -- taking out again (-Revert) --'
$rev = @(
    @('--disable-features=Translate,AudioServiceSandbox,AudioServiceOutOfProcess', '--disable-features=Translate'),
    @("--incognito $ours", '--incognito'),
    @("--x $ours --y", '--x --y'),
    @('--disable-features="A, AudioServiceSandbox,AudioServiceOutOfProcess"', '--disable-features=A')
)
foreach ($c in $rev) {
    $got = Merge-DisableFeatures $c[0] @() $F
    Assert ($got -ceq $c[1]) ('[{0}] -> [{1}]' -f $c[0], $got)
}

Write-Host '  -- which shortcut targets are browsers --'
$BrowserExes = @('thorium.exe', 'opera.exe', 'chrome_proxy.exe')
foreach ($c in @(
        @('C:\x\Thorium\Application\thorium.exe', $true, 'a browser exe'),
        @('C:\x\Thorium\Application\chrome_proxy.exe', $true, 'the proxy an installed web app starts through'),
        @('C:\Users\u\AppData\Local\Programs\Opera GX\launcher.exe', $true, 'Opera GX: its shortcuts point at launcher.exe'),
        @('C:\Users\u\AppData\Local\Programs\Opera\launcher.exe', $true, 'Opera, the same'),
        @('C:\Games\SomeGame\launcher.exe', $false, 'any other launcher.exe'),
        @('C:\Windows\notepad.exe', $false, 'another program'),
        @('', $false, 'no target'))) {
    Assert ((Test-BrowserTarget $c[0]) -eq $c[1]) ('{0} -> {1}' -f $c[2], $c[1])
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
# ($tmp is the 8.3 form of the folder; the script reports the long one)
Assert ((Count-Reports ('`[OK`] Added flags: ?:\*\' + (Split-Path $tmp -Leaf) + '\Thorium.lnk (thorium.exe)')) -eq 1) 'the [OK] names the shortcut by its full path (three shortcuts are all called Thorium.lnk)'
Reset-Log
Fix-Browsers
Assert ((Count-Reports '*already present*') -eq 1) 'a second run sees it as done'
$Revert = $true
Fix-Browsers
$after = $w.CreateShortcut("$tmp\Thorium.lnk").Arguments
Write-Host ('     after -Revert: ' + $after)
Assert ($after -ceq '--disable-features=Translate') '-Revert removes only our two features'
$Revert = $false

Write-Host '  -- a shortcut whose arguments leave no room for the switch --'
# a shortcut holds 1023 characters of arguments; Save() cuts a longer list without an error
$long = '--proxy-bypass-list=' + ('a' * 980)
$s = $w.CreateShortcut("$tmp\Thorium.lnk"); $s.Arguments = $long; $s.Save()
Reset-Log
Fix-Browsers
$after = $w.CreateShortcut("$tmp\Thorium.lnk").Arguments
Write-Host ('     ' + ($global:Reports | Where-Object { $_ -match 'FAIL|OK' } | Select-Object -First 1))
Assert ((Count-Reports '`[FAIL`]*a shortcut holds 1023*') -eq 1 -and (Count-Reports '`[OK`] Added flags*') -eq 0) 'a [FAIL] that says why - it used to print "[OK] Added flags" on every run with the switch cut off'
Assert ($after -ceq $long) 'and the shortcut is left exactly as it was'

Write-Host '  -- where shortcuts are looked for --'
# The step above ran on a stub list. This is the real Get-Shortcuts, with %APPDATA% and
# %ProgramData% pointed at a scratch "profile" (the two Desktop folders cannot be pointed
# elsewhere; they are only read).
. ([scriptblock]::Create((Get-ShippedFunction 'Get-Shortcuts')))
$fake = New-TestScratch 'profile'
$spots = [ordered]@{
    'the user''s Start Menu'   = 'Roaming\Microsoft\Windows\Start Menu\Programs\Thorium.lnk'
    'a Start Menu subfolder'   = 'Roaming\Microsoft\Windows\Start Menu\Programs\Chromium Apps\Some App.lnk'
    'the all-users Start Menu' = 'ProgramData\Microsoft\Windows\Start Menu\Programs\Thorium.lnk'
    'the taskbar pins'         = 'Roaming\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\Thorium.lnk'
}
foreach ($rel in $spots.Values) {
    New-Item -ItemType Directory -Force -Path (Split-Path (Join-Path $fake $rel)) | Out-Null
    Copy-Item -LiteralPath "$tmp\Thorium.lnk" -Destination (Join-Path $fake $rel)
}
$savedA = $env:APPDATA; $savedP = $env:ProgramData
try {
    $env:APPDATA = Join-Path $fake 'Roaming'; $env:ProgramData = Join-Path $fake 'ProgramData'
    $found = @(Get-Shortcuts | ForEach-Object { $_.FullName })
} finally { $env:APPDATA = $savedA; $env:ProgramData = $savedP }
foreach ($k in $spots.Keys) {
    Assert (@($found | Where-Object { $_ -like ('*\' + (Split-Path $fake -Leaf) + '\' + $spots[$k]) }).Count -eq 1) ('a shortcut in {0} is found' -f $k)
}

Write-Host '  -- the backup of a shortcut --'
. ([scriptblock]::Create((Get-ShippedFunction 'Backup-Shortcut')))
$BackupDir = New-TestScratch 'bk2'
# a name no single ANSI code page can hold: Cyrillic next to an accented Latin letter
$odd = Join-Path $tmp ('Thorium ' + [char]0x0416 + [char]0x00E9 + '.lnk')
Copy-Item -LiteralPath "$tmp\Thorium.lnk" -Destination $odd
$b1 = Backup-Shortcut (Get-Item -LiteralPath $odd)
$b2 = Backup-Shortcut (Get-Item -LiteralPath "$tmp\Thorium.lnk")
$b3 = Backup-Shortcut (Get-Item -LiteralPath (Join-Path $fake $spots['the taskbar pins']))
Assert ((Test-Path -LiteralPath $b1) -and (Split-Path $b1 -Leaf) -match '\.lnk\.[0-9A-F]{8}\.20260101-000000\.bak$') ('the copy is named <name>.lnk.<hash>.<stamp>.bak ({0})' -f ((Split-Path $b1 -Leaf) -replace '[^\x20-\x7E]', '?'))
Assert ($b2 -ne $b3 -and (Test-Path -LiteralPath $b2) -and (Test-Path -LiteralPath $b3)) 'two shortcuts with the same name, from different folders, get two backups'
$map = [IO.File]::ReadAllText((Join-Path $BackupDir 'shortcut-sources.txt'), [Text.Encoding]::UTF8)
Assert ($map.Contains((Split-Path $b1 -Leaf) + "`t" + (Get-Item -LiteralPath $odd).FullName)) 'shortcut-sources.txt names the exact path each backup came from - readable whatever the system code page (it was written as ANSI, with "?" for such a name)'
Assert (@($map -split "`r?`n" | Where-Object { $_ }).Count -eq 3) 'one line per backup'

Complete-Test
