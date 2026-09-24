trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
# Facts written down in more than one place drift apart (pitfall 28 of the windows-shell-dev
# skill: "an accepted-value set stated in more than one place drifts"). Every check below
# pulls the set or the number out of the CODE and compares each copy with it, so a change
# that leaves a copy behind fails here instead of misleading a reader.
$ps1Path = Get-KitFile 'Fix-AudioMixer.ps1'
$ps1    = [IO.File]::ReadAllText($ps1Path)
$bat    = [IO.File]::ReadAllText((Get-KitFile 'Check-Store.bat'))
$readme = [IO.File]::ReadAllText((Get-KitFile 'README.md'))
$vbdoc  = [IO.File]::ReadAllText((Get-KitFile 'VOLUME-BOOSTER.md'))
$cs     = [IO.File]::ReadAllText((Get-KitFile 'VolumeBooster\AppVolumeBooster.cs'))
$t = $null; $e = $null
$ast  = [Management.Automation.Language.Parser]::ParseFile($ps1Path, [ref]$t, [ref]$e)
$help = $ps1.Substring(0, $ps1.IndexOf('[CmdletBinding()]'))
# code without its comments, for the checks on what the code does
$code = @(($ps1 -split "`r?`n") | Where-Object { $_.Trim() -notmatch '^#' } | ForEach-Object { $_ -replace '\s+#\s.*$', '' }) -join "`n"
function Same([string[]]$a, [string[]]$b) { (@($a | Sort-Object -Unique) -join '|') -eq (@($b | Sort-Object -Unique) -join '|') }
function Show([string]$label, [string[]]$set) { Write-Host ('     {0,-20} {1}' -f $label, ($set -join ', ')) }

Write-Host '  -- the two store paths: four copies --'
$storesAst = $ast.Find({ param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -eq '$Stores' }, $true)
$fromArray  = @([regex]::Matches($storesAst.Extent.Text, "Reg\s*=\s*'([^']+)'") | ForEach-Object { $_.Groups[1].Value })
$fromHelp   = @([regex]::Matches($help, 'HKCU\\Software\\[^\r\n]*?\\PropertyStore') | ForEach-Object { $_.Value })
$fromBat    = @([regex]::Matches($bat, "'Registry::HKEY_CURRENT_USER\\([^']+)'") | ForEach-Object { 'HKCU\' + $_.Groups[1].Value })
$fromReadme = @([regex]::Matches($readme, '(?m)^\| `(HKCU\\[^`]+)`') | ForEach-Object { $_.Groups[1].Value })
Show '$Stores' $fromArray
Assert ($fromArray.Count -eq 2) ('the script''s $Stores has both variants ({0})' -f $fromArray.Count)
Assert (Same $fromArray $fromHelp)   'the help block at the top of the script lists the same'
Assert (Same $fromArray $fromBat)    'Check-Store.bat lists the same'
Assert (Same $fromArray $fromReadme) 'the README table lists the same'
Assert (($ps1 -match 'this list exists in FOUR places') -and ($bat -match 'change all\s+rem four')) 'both copy notes count four copies'

Write-Host '  -- the switches --'
$declared  = @($ast.ParamBlock.Parameters | Where-Object { $_.StaticType -eq [switch] } | ForEach-Object { $_.Name.VariablePath.UserPath })
$helpUsage = @([regex]::Matches($help, '\.\\Fix-AudioMixer\.ps1 -(\w+)') | ForEach-Object { $_.Groups[1].Value })
$table     = @([regex]::Matches($readme, '(?m)^\| `\.\\Fix-AudioMixer\.ps1 -(\w+)`(?: / `-(\w+)`)?') | ForEach-Object { $_.Groups[1].Value; if ($_.Groups[2].Success) { $_.Groups[2].Value } })
$exclAst   = $ast.Find({ param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -eq '$exclusive' }, $true)
$actions   = @([regex]::Matches($exclAst.Extent.Text, "N = '(\w+)'") | ForEach-Object { $_.Groups[1].Value })
$dispatch  = @([regex]::Matches($code, '(?m)^(?:if|elseif)\s+\(\$(\w+)\)\s+\{ Invoke-Step') | ForEach-Object { $_.Groups[1].Value })
Show 'param()' $declared
Assert ($declared.Count -ge 8) ('read the declared switches ({0})' -f $declared.Count)
Assert (Same $declared $helpUsage) 'the USAGE lines at the top of the script name the same switches'
Assert (Same $declared $table) 'the README command table names the same switches'
Assert (Same $declared (@($actions) + 'CheckOnly')) 'the one-action-at-a-time check knows every action switch'
Assert (Same $actions $dispatch) 'and every action switch has its branch in the dispatch'

Write-Host '  -- the browsers the script patches, and the README --'
$exeLine = @($ps1 -split "`r?`n" | Where-Object { $_ -match '^\$BrowserExes\s*=' })
$exes = @(if ($exeLine.Count -eq 1) { [regex]::Matches($exeLine[0], "'([^']+\.exe)'") | ForEach-Object { $_.Groups[1].Value } })
$display = @{ 'thorium.exe' = 'Thorium'; 'chrome.exe' = 'Chrome'; 'msedge.exe' = 'Edge'; 'brave.exe' = 'Brave'
              'vivaldi.exe' = 'Vivaldi'; 'opera.exe' = 'Opera'; 'opera_gx.exe' = 'Opera GX'; 'browser.exe' = 'Yandex Browser' }
$row = @($readme -split "`n" | Where-Object { $_ -match "Chromium's audio sandbox" })
Assert ($exes.Count -ge 1 -and $row.Count -eq 1) ('found the $BrowserExes line ({0} browsers) and the README symptom row' -f $exes.Count)
foreach ($x in $exes) {
    # a new exe without a display name here fails on purpose: add it to $display AND the README
    Assert ($display.ContainsKey($x) -and $row[0].Contains($display[$x])) ('the README names the browser behind {0}' -f $x)
}

Write-Host '  -- where backups go --'
$fallback = [regex]::Match($ps1, "Combine\(\`$env:LOCALAPPDATA, '([^']+)'\)").Groups[1].Value
Assert ($fallback -ne '') ('read the fallback folder from Ensure-BackupDir (%LOCALAPPDATA%\{0})' -f $fallback)
Assert ($help.Contains('%LOCALAPPDATA%\' + $fallback)) 'the help block names it'
Assert ($readme.Contains('%LOCALAPPDATA%\' + $fallback)) 'the README names it'

Write-Host '  -- file paths are literal ([ and ] are wildcards to -Path) --'
Assert (([regex]::Matches($code, 'Test-Path -LiteralPath \$bk\b')).Count -eq 3) 'the three backup checks use Test-Path -LiteralPath'
Assert (([regex]::Matches($code, 'Get-Item -LiteralPath \$bk\b')).Count -eq 3) '... and Get-Item -LiteralPath'
$plain = @([regex]::Matches($code, '(Test-Path|Get-Item|Get-ChildItem)\s+(-Path\s+)?\$(bk|ini|dll|dirs|_)(?![.\w])') | ForEach-Object { $_.Value })
Assert ($plain.Count -eq 0) ('no file path is read as a wildcard ({0})' -f ($plain -join '; '))

Write-Host '  -- numbers in VOLUME-BOOSTER.md, recounted from the source --'
$csCode = [regex]::Replace($cs, '//[^\r\n]*', '')
$methods = 0; $covered = 0; $ifaces = 0
foreach ($m in [regex]::Matches($csCode, '\binterface\s+(\w+)\s*\{')) {
    $start = $m.Index + $m.Length; $i = $start; $depth = 1
    while ($depth -gt 0) { $c = $csCode[$i]; if ($c -eq '{') { $depth++ } elseif ($c -eq '}') { $depth-- }; $i++ }
    $decls = @($csCode.Substring($start, $i - 1 - $start) -split ';' | Where-Object { $_ -match '\(' })
    $methods += $decls.Count; $covered += @($decls | Where-Object { $_ -match '\[PreserveSig\]' }).Count; $ifaces++
}
Write-Host ('     {0} COM interfaces, {1} methods, {2} with [PreserveSig]' -f $ifaces, $methods, $covered)
Assert ($methods -gt 0 -and $covered -eq $methods) 'every COM interface method carries [PreserveSig]'
Assert ($vbdoc -match ('\b{0} today\b' -f $methods)) ('the doc gives today''s count ({0})' -f $methods)
function Num([string]$pattern) { $g = [regex]::Match($cs, $pattern).Groups[1]; if ($g.Success) { [double]$g.Value } else { [double]::NaN } }
$pad  = Num 'volatile int targetPadFrames = (\d+);'
$step = Num 'int np = targetPadFrames \+ (\d+);'
$cap  = Num 'if \(np > (\d+)\) np = '
$duck = Num 'public const float DUCK = ([0-9.]+)f;'
$maxb = Num 'public const float MAXBOOST = ([0-9.]+)f;'
$pmin = Num 'if \(ms < (\d+)\) ms = '
$pmax = Num 'if \(ms > (\d+)\) ms = '
Assert ($cs.Contains('return targetPadFrames / 48 + 10;')) 'latency is still reported as buffer + 10 ms'
$claims = @(
    ('a real {0} ms buffer' -f ($pad / 48)),
    ('~{0} ms extra latency' -f ($pad / 48 + 10)),
    ('by {0} ms per audible dropout, up to ~{1} ms' -f ($step / 48), ($cap / 48 + 10)),
    ('**{0}%**' -f [math]::Round($duck * 100)),
    ('(100-{0}%)' -f [math]::Round($maxb * 100)),
    ('({0}-{1}, default ~{2})' -f $pmin, $pmax, ($pad / 48)),
    ('0-{0}' -f [math]::Floor([int]::MaxValue / 1000))
)
foreach ($c in $claims) { Assert ($vbdoc.Contains($c)) ('the doc says "{0}"' -f $c) }

Write-Host '  -- the bytes of every shipped file --'
# cmd needs CRLF to find a label, Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI, and
# a BOM corrupts a batch file's first line: scripts and sources stay ASCII + CRLF, no BOM.
# The Markdown files are LF, as they have always been.
$scripts = @(Get-ChildItem -LiteralPath $Kit -Recurse -File | Where-Object { $_.Extension -in '.cmd', '.bat', '.ps1', '.cs' })
$wrong = @()
foreach ($f in $scripts) {
    $b = [IO.File]::ReadAllBytes($f.FullName)
    $s = [Text.Encoding]::ASCII.GetString($b)
    $crlf = ([regex]::Matches($s, "`r`n")).Count
    $why = @()
    if ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF) { $why += 'BOM' }
    if (@($b | Where-Object { $_ -gt 127 }).Count) { $why += 'non-ASCII' }
    if (([regex]::Matches($s, "`n")).Count -ne $crlf -or ([regex]::Matches($s, "`r")).Count -ne $crlf) { $why += 'not CRLF-only' }
    if (-not $s.EndsWith("`r`n")) { $why += 'no final CRLF' }
    if ($why) { $wrong += ('{0} ({1})' -f $f.FullName.Substring($Kit.Length + 1), ($why -join ', ')) }
}
$names = @($scripts | ForEach-Object { $_.FullName.Substring($Kit.Length + 1) })
$core = 'Fix-AudioMixer.cmd', 'Check-Store.bat', 'Fix-AudioMixer.ps1', 'VolumeBooster\Build-Booster.cmd', 'VolumeBooster\AppVolumeBooster.cs'
Assert (@($core | Where-Object { $names -notcontains $_ }).Count -eq 0) ('checked {0} scripts and sources, the five shipped ones included' -f $scripts.Count)
Assert ($wrong.Count -eq 0) ('all ASCII, CRLF, no BOM{0}' -f $(if ($wrong) { ': ' + ($wrong -join '; ') } else { '' }))
$docs = @(Get-ChildItem -LiteralPath $Kit -Recurse -File -Filter *.md)
$badDocs = @($docs | Where-Object { $b = [IO.File]::ReadAllBytes($_.FullName); ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB) -or (@($b | Where-Object { $_ -eq 13 }).Count -gt 0) } | ForEach-Object { $_.Name })
Assert ($docs.Count -ge 3 -and $badDocs.Count -eq 0) ('{0} Markdown files, LF and no BOM{1}' -f $docs.Count, $(if ($badDocs) { ': ' + ($badDocs -join ', ') } else { '' }))

Write-Host '  -- tests\README.md lists every test file --'
$testsDir = Join-Path $Kit 'tests'
$onDisk = @(Get-ChildItem -LiteralPath $testsDir -Recurse -File -Filter '*.Tests.ps1' | ForEach-Object { $_.Directory.Name + '\' + $_.Name })
$testDoc = [IO.File]::ReadAllText((Join-Path $testsDir 'README.md'))
$inTable = @([regex]::Matches($testDoc, '(?m)^\| `(\w+\\\w+\.Tests\.ps1)`') | ForEach-Object { $_.Groups[1].Value })
$missing = @($onDisk | Where-Object { $inTable -notcontains $_ })
$stale = @($inTable | Where-Object { $onDisk -notcontains $_ })
Assert ($onDisk.Count -ge 20) ('found the test files ({0})' -f $onDisk.Count)
Assert ($missing.Count -eq 0) ('every one has a row in the table{0}' -f $(if ($missing) { ' - missing: ' + ($missing -join ', ') } else { '' }))
Assert ($stale.Count -eq 0 -and $inTable.Count -eq $onDisk.Count) ('and every row names a file that exists, once{0}' -f $(if ($stale) { ' - no such file: ' + ($stale -join ', ') } else { '' }))

Complete-Test
