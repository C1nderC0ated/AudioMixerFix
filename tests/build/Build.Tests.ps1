trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\AudioRig.ps1"
# The booster has to rebuild on a bare Windows: one .cs file, the C# compiler that ships
# inside .NET Framework 4.x, three references - and warning-clean at level 4, as a 64-bit
# and as a 32-bit build. Everything is built into a scratch folder: the kit's own exe is
# never replaced. Nothing is played.
$d = New-TestScratch 'build'
Write-Host ('     compiler: ' + $script:Csc)
$w = New-Build $Rig.Src (Join-Path $d 'AppVolumeBooster.exe') -Warn4
Assert ($w.Count -eq 0) ('AnyCPU build at warning level 4: {0} warning(s)' -f $w.Count)
$w = New-Build $Rig.Src (Join-Path $d 'x86\AppVolumeBooster.exe') -Platform x86 -Warn4
Assert ($w.Count -eq 0) ('32-bit build at warning level 4: {0} warning(s)' -f $w.Count)
$bb = [IO.File]::ReadAllText((Join-Path $Kit 'VolumeBooster\Build-Booster.cmd'))
Assert ($bb -match [regex]::Escape(($script:CscRefs -join ' '))) 'Build-Booster.cmd uses exactly the same three references'
Complete-Test
