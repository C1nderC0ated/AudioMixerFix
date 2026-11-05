trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
# Check-Store.bat, run as a copy whose two store paths point at a key that does not exist
# and whose pause is stubbed: the real registry is only read, and nothing is written
# anywhere but the scratch folder. Written for two things a reader who downloaded the file
# on its own from the release page saw: the variant store, which exists only on some
# builds, printed as a yellow MISSING like a real problem; and a verdict telling them to
# run Fix-AudioMixer.cmd, a file they had not downloaded, without saying where to get it.
$src = [IO.File]::ReadAllText((Get-KitFile 'Check-Store.bat'))
$paths = @([regex]::Matches($src, "'Registry::HKEY_CURRENT_USER\\([^']+)'") | ForEach-Object { $_.Groups[1].Value })
Assert ($paths.Count -eq 2) ('found the two store paths in the file ({0})' -f $paths.Count)
$scratch = New-TestScratch 'checkstore'
$ascii = New-Object Text.ASCIIEncoding
$nowhere = 'Software\AudioMixerFixTest\NoSuchStore'
Assert (-not (Test-Path -LiteralPath ('Registry::HKEY_CURRENT_USER\' + $nowhere))) 'the stand-in store key does not exist'

function Invoke-CheckStore([string]$folder) {
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    $t = $src
    foreach ($p in $paths) { $t = $t.Replace("'Registry::HKEY_CURRENT_USER\$p'", "'Registry::HKEY_CURRENT_USER\$nowhere'") }
    $t = $t.Replace("`r`npause`r`n", "`r`necho [PAUSE-REACHED]`r`n")
    # every substitution must apply, or the real store would be read and the window would wait
    if ($t.Contains('PropertyStore') -or -not $t.Contains('[PAUSE-REACHED]')) { throw 'stub substitution did not apply' }
    $bat = Join-Path $folder 'Check-Store.bat'
    [IO.File]::WriteAllText($bat, $t, $ascii)
    $o = & $env:ComSpec /d /c (Q $bat) 2>&1 | Out-String
    [pscustomobject]@{ Code = $LASTEXITCODE; Out = $o }
}

Write-Host '  -- downloaded on its own: no Fix-AudioMixer.cmd next to it --'
$r = Invoke-CheckStore (Join-Path $scratch 'alone')
Assert (($r.Code -eq 0) -and ($r.Out -match '\[PAUSE-REACHED\]')) ('runs to its pause and exits 0 (exit {0})' -f $r.Code)
Assert ($r.Out -match '(?m)^\s*canonical store \(IE LowRegistry\): MISSING') 'a missing canonical store is MISSING'
Assert ($r.Out -match '(?m)^\s*variant store \(Multimedia-Audio\): not present') 'a missing variant store is "not present", not an alarm (it was a yellow MISSING like the canonical one)'
Assert ($r.Out -match 'VERDICT: nothing saved yet') 'with no store, the "nothing saved yet" verdict'
Assert ($r.Out -match 'download it and Fix-AudioMixer\.ps1 from the page this file came from') 'and it says where Fix-AudioMixer.cmd and the script come from (it only said "run Fix-AudioMixer.cmd")'

Write-Host '  -- next to the kit --'
$kit = Join-Path $scratch 'kit'
New-Item -ItemType Directory -Force -Path $kit | Out-Null
Set-Content -LiteralPath (Join-Path $kit 'Fix-AudioMixer.cmd') -Value 'rem stand-in: never run'
$r = Invoke-CheckStore $kit
Assert (($r.Code -eq 0) -and ($r.Out -match 'VERDICT: nothing saved yet')) 'runs the same'
Assert ($r.Out -notmatch 'from the page this file came from') 'no download hint when Fix-AudioMixer.cmd is next to it'

Complete-Test
