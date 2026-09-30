trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\AudioRig.ps1"
# An app that closes its audio session and opens it again while it is boosted - a browser
# does it 5 seconds after a pause, a game when it re-opens its audio - gets the new session
# under the SAME instance id. The re-opened session has to be held at 4% like the first,
# and the LIVE one has to be back at its own level when the boost ends - an app left at 4%
# is saved at 4% by Windows. SessionMaker.cs is such an app: a silent stream, closed and
# opened again. Nothing is played in this test.
# (A behaviour check, not a regression test: the booster passed it before 2026-09-30 too.)
Build-Rig
New-Build $Rig.Src (Join-Path $Rig.Dir 'sessionmaker.exe') -Main 'AppVolumeBoosterNs.SessionMaker' -Console -Extra @(Join-Path $Rig.Lib 'SessionMaker.cs') | Out-Null
$report = Join-Path $Rig.Dir 'maker.txt'
# round 1 for 5 s, 1.5 s with no session at all, round 2 for 14 s
$mk = Start-Process (Join-Path $Rig.Dir 'sessionmaker.exe') -ArgumentList (Q $report), 5000, 1500, 14000 -PassThru -WindowStyle Hidden
try {
    $dl = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $dl -and -not (Test-Path -LiteralPath $report)) { Start-Sleep -Milliseconds 100 }
    Assert (Test-Path -LiteralPath $report) 'the test app has its first session'
    # the boost starts in round 1 and ends about 5 s into round 2
    $r = Invoke-Booster @('--pid', $mk.Id, '--boost', '100', '--seconds', '10')
    Write-Host ('  log: ' + $r.Log)
    $mk.WaitForExit()
    $lines = @(Get-Content -LiteralPath $report)
    $r1 = @($lines | Where-Object { $_ -match 'round=1 ' }); $r2 = @($lines | Where-Object { $_ -match 'round=2 ' })
    Write-Host ('  round 1: {0} samples, {1} at 4%; round 2: {2} samples, {3} at 4%; last three: {4}' -f $r1.Count, @($r1 | Where-Object { $_ -match 'vol=0\.040' }).Count, $r2.Count, @($r2 | Where-Object { $_ -match 'vol=0\.040' }).Count, (($r2 | Select-Object -Last 3 | ForEach-Object { ($_ -split ' ')[2] }) -join ' '))
    Assert (@($r1 | Where-Object { $_ -match 'vol=0\.040' }).Count -ge 3) 'the first session was ducked'
    Assert (@($r2 | Where-Object { $_ -match 'vol=0\.040' }).Count -ge 3) 'the session that replaced it was ducked too'
    Assert ($r2.Count -ge 5 -and @($r2 | Select-Object -Last 5 | Where-Object { $_ -notmatch 'vol=1\.000' }).Count -eq 0) 'when the boost ended, the LIVE session went back to 100%'
}
finally { if (-not $mk.HasExited) { Stop-Process -Id $mk.Id -ErrorAction SilentlyContinue } }
Complete-Test
