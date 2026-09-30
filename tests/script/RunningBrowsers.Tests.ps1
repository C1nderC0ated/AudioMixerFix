trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\ScriptStubs.ps1"
# The launch flags only work for a browser that was STARTED with them. A browser update
# recreates its Start Menu shortcut without them, and a browser started by another program
# never reads a shortcut at all - so the browser could be running without the fix while
# every shortcut check said [OK]. -Status (and the status at the end of the default run)
# now asks the running browser itself - and looks at the outcome as well: a browser that has
# the flags on its command line and a separate audio process anyway is not fixed.
# Get-CimInstance is a stub here: no real process is looked at.
$BrowserFeatures = @('AudioServiceSandbox', 'AudioServiceOutOfProcess')
$BrowserExes = @('thorium.exe', 'chrome.exe', 'msedge.exe')
. ([scriptblock]::Create((Get-ShippedFunction 'Get-DisableFeatureSwitches')))
. ([scriptblock]::Create((Get-ShippedFunction 'Test-BrowserFlags')))
. ([scriptblock]::Create((Get-ShippedFunction 'Show-RunningBrowsers')))

Write-Host '  -- which command lines count as "with the flags" --'
$both = '--disable-features=AudioServiceSandbox,AudioServiceOutOfProcess'
$cases = @(
    @("`"C:\x\thorium.exe`" $both", $true, 'both features'),
    @("`"C:\x\thorium.exe`" --disable-features=Translate,AudioServiceOutOfProcess,AudioServiceSandbox --foo", $true, 'merged into the user''s own list, any order'),
    @("`"C:\x\thorium.exe`" --disable-features=`"AudioServiceSandbox,AudioServiceOutOfProcess`"", $true, 'quoted list'),
    @("`"C:\x\thorium.exe`" `"--disable-features=AudioServiceSandbox, AudioServiceOutOfProcess`"", $true, 'the whole switch quoted, with a space in the list'),
    @('"C:\x\thorium.exe"', $false, 'no switch at all'),
    @('"C:\x\thorium.exe" --disable-features=AudioServiceSandbox', $false, 'only one of the two'),
    @("`"C:\x\thorium.exe`" $both --disable-features=Translate", $false, 'a later switch wins in Chromium, and it lacks them'),
    @("`"C:\x\thorium.exe`" --xdisable-features=AudioServiceSandbox,AudioServiceOutOfProcess", $false, 'a different switch that merely ends the same way')
)
foreach ($c in $cases) { Assert ((Test-BrowserFlags $c[0]) -eq $c[1]) ('{0} -> {1}' -f $c[2], $c[1]) }

Write-Host '  -- the running browsers --'
function New-Proc([string]$name, [int]$id, [string]$cl, [int]$parent = 4) { [pscustomobject]@{ Name = $name; ProcessId = $id; CommandLine = $cl; ParentProcessId = $parent } }
$global:FakeProcs = @(
    (New-Proc 'thorium.exe' 100 "`"C:\x\thorium.exe`" $both"),
    (New-Proc 'thorium.exe' 101 "`"C:\x\thorium.exe`" --type=renderer $both" 100),
    (New-Proc 'thorium.exe' 102 "`"C:\x\thorium.exe`" --type=utility --utility-sub-type=network.mojom.NetworkService $both" 100),
    (New-Proc 'chrome.exe' 200 '"C:\y\chrome.exe"'),
    (New-Proc 'chrome.exe' 201 '"C:\y\chrome.exe" --type=utility --utility-sub-type=audio.mojom.AudioService' 200),
    (New-Proc 'msedge.exe' 300 $null),
    (New-Proc 'notepad.exe' 400 'notepad.exe'),
    (New-Proc 'msedge.exe' 500 "`"C:\z\msedge.exe`" $both"),
    (New-Proc 'msedge.exe' 501 "`"C:\z\msedge.exe`" --type=utility --utility-sub-type=audio.mojom.AudioService $both" 500)
)
function Get-CimInstance { param($ClassName, $ErrorAction) if ($global:CimFails) { throw 'stub: WMI unavailable' }; $global:FakeProcs }
Reset-Log; $global:CimFails = $false
Show-RunningBrowsers
$global:Reports | ForEach-Object { Write-Host ('     ' + $_) }
Assert ((Count-Reports '`[OK`] thorium.exe (pid 100)*with the audio flags*') -eq 1) 'a browser started with the flags is [OK]'
Assert ((Count-Reports '`[WARN`] chrome.exe (pid 200)*WITHOUT the audio flags*') -eq 1) 'a browser started without them is a [WARN] that names it'
Assert ((Count-Reports '*(pid 101)*') -eq 0 -and (Count-Reports '*(pid 102)*') -eq 0 -and (Count-Reports '*(pid 201)*') -eq 0) 'helper processes (--type=...) get no line of their own, only the browser itself'
Assert ((Count-Reports '*(pid 300)*') -eq 0 -and (Count-Reports '*notepad*') -eq 0) 'a process whose command line cannot be read, or that is no browser, is left out'
Assert ((Count-Reports '`[WARN`] msedge.exe (pid 500) was started with the audio flags, but still plays through a separate audio process (pid 501)*') -eq 1 -and (Count-Reports '`[OK`] msedge.exe*') -eq 0) 'flags on the command line but a separate audio process anyway: a [WARN], not an [OK]'

Write-Host '  -- nothing running, and WMI failing --'
Reset-Log; $global:FakeProcs = @((New-Proc 'notepad.exe' 400 'notepad.exe'))
Show-RunningBrowsers
Assert ((Count-Reports '`[INFO`] None of the known Chromium browsers is running*') -eq 1 -and (Count-Reports '`[WARN`]*') -eq 0) 'no browser running: one [INFO], no warning'
Reset-Log; $global:CimFails = $true
Show-RunningBrowsers
Assert ((Count-Reports '`[INFO`] Could not list running processes: stub: WMI unavailable') -eq 1) 'a failing process query is reported, and ends nothing'

Write-Host '  -- it is part of the status --'
$status = Get-ShippedFunction 'Show-Status'
$code = @(($status -split "`r?`n") | Where-Object { $_.Trim() -notmatch '^#' }) -join "`n"
Assert ($code -match '(?m)^\s*Show-RunningBrowsers\s*$') 'Show-Status calls it'

Complete-Test
