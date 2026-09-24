# Shared by every test. Dot-source it right after the test's own trap line - a trap only
# covers the file it is written in, so each test file carries its own:
#
#   trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
#   . "$PSScriptRoot\..\lib\TestLib.ps1"
#
#   $Kit                      the kit folder (two levels up from this file)
#   Get-KitFile <relative>    a kit file, or the copy an AMF_* variable points at (below)
#   New-TestScratch <tag>     a fresh folder under %TEMP%\AudioMixerFix-tests, removed by
#                             Complete-Test; returned in its 8.3 form (no spaces)
#   $TestKey                  HKCU:\Software\AudioMixerFixTest - the ONLY registry place a
#                             test may write to; removed by Complete-Test
#   Get-ShippedFunction <n>   one function's text out of Fix-AudioMixer.ps1, via the real
#                             parser, so what a test runs is exactly what ships
#   Assert <cond> <what>      one PASS / FAIL line
#   Q <path>                  the path in double quotes, for Start-Process -ArgumentList
#   Complete-Test             cleans up and exits 0 (all passed) or 1
#
# Any file under test can be swapped for another copy - an older version, or a mutated
# one - without touching the tests. The variables and the files they replace:
#   AMF_PS1    Fix-AudioMixer.ps1               AMF_CMD    Fix-AudioMixer.cmd
#   AMF_BAT    Check-Store.bat                  AMF_CS     VolumeBooster\AppVolumeBooster.cs
#   AMF_README README.md                        AMF_VBDOC  VOLUME-BOOSTER.md

$global:AnyFail = $false
$Kit = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$TestKey = 'HKCU:\Software\AudioMixerFixTest'
$script:AmfVars = @{
    'Fix-AudioMixer.ps1'                = 'AMF_PS1'
    'Fix-AudioMixer.cmd'                = 'AMF_CMD'
    'Check-Store.bat'                   = 'AMF_BAT'
    'VolumeBooster\AppVolumeBooster.cs' = 'AMF_CS'
    'README.md'                         = 'AMF_README'
    'VOLUME-BOOSTER.md'                 = 'AMF_VBDOC'
}
$script:AmfShown = @{}
$script:Scratches = New-Object System.Collections.Generic.List[string]

function Get-KitFile([string]$relative) {
    $var = $script:AmfVars[$relative]
    if ($var) {
        $other = [Environment]::GetEnvironmentVariable($var)
        if ($other) {
            if (-not (Test-Path -LiteralPath $other)) { throw "$var points at a missing file: $other" }
            if (-not $script:AmfShown[$var]) { Write-Host ('  (testing {0} = {1})' -f $var, $other); $script:AmfShown[$var] = $true }
            return $other
        }
    }
    Join-Path $Kit $relative
}

function New-TestScratch([string]$tag = 't') {
    $d = Join-Path (Join-Path $env:TEMP 'AudioMixerFix-tests') ($tag + '-' + [guid]::NewGuid().ToString('N').Substring(0, 10))
    New-Item -ItemType Directory -Force -Path $d | Out-Null
    $script:Scratches.Add($d)
    # PS 5.1's Start-Process does NOT quote -ArgumentList elements, so a path with a space
    # reaches a child as two arguments (it once made a probe write a stray file named after
    # the first half of a user name). The 8.3 form has no spaces; Q() covers the rest.
    try { (New-Object -ComObject Scripting.FileSystemObject).GetFolder($d).ShortPath } catch { $d }
}

function Q([string]$s) { '"' + $s + '"' }

function Get-ShippedFunction([string]$name) {
    $t = $null; $e = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile((Get-KitFile 'Fix-AudioMixer.ps1'), [ref]$t, [ref]$e)
    $f = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true) | Select-Object -First 1
    if (-not $f) { throw "function $name not found in the script under test" }
    $f.Extent.Text
}

function Assert([bool]$cond, [string]$what) {
    if ($cond) { Write-Host ('    PASS  ' + $what) } else { Write-Host ('    FAIL  ' + $what); $global:AnyFail = $true }
}

function Complete-Test {
    foreach ($d in $script:Scratches) { Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $TestKey) { Remove-Item -LiteralPath $TestKey -Recurse -Force -ErrorAction SilentlyContinue }
    if ($global:AnyFail) { exit 1 } else { exit 0 }
}
