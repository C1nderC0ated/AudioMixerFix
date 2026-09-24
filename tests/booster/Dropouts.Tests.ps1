param([string[]]$Only = @())
trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
. "$PSScriptRoot\..\lib\TestLib.ps1"
. "$PSScriptRoot\..\lib\AudioRig.ps1"
. "$PSScriptRoot\..\lib\SourceVariants.ps1"
# Dropouts, measured on what is actually HEARD: while the booster runs, the probe records
# the booster's own output through process loopback (nothing is played by the probe) and
# finds every run of exact digital silence. Test-only builds add measurements to the log
# and inject stalls. What this guards:
#   * the 50 ms buffer really exists - it used to hold 0-20 ms, often exactly 0, so a few
#     ms of hiccup was a gap while the status line claimed ~60 ms
#   * one stall counts as ONE glitch (a 300 ms capture stall read as 14-15 and pushed
#     latency to the 160 ms cap), and a stall of the output thread counts at all (it read 0)
#   * a queue found empty just in time loses nothing and counts nothing
# -Only steady,hiccup50,hiccup60,capstall,renstall runs a subset.
$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })   # -File passes "a,b" as one string
function Want([string]$k) { $Only.Count -eq 0 -or $Only -contains $k }
$work = New-TestScratch 'variants'
$script:player = $null
function Run-Scenario([string]$key, [string[]]$stress, [int]$runs, [int]$sec) {
    $s1 = Join-Path $work "$key.stress.cs"; $s2 = Join-Path $work "$key.cs"
    if ($stress.Count -gt 0) { New-StressSource $Rig.Src $s1 $stress[0] ([int]$stress[1]) $(if ($stress.Count -gt 2) { [int]$stress[2] } else { 0 }) }
    else { Copy-Item -LiteralPath $Rig.Src -Destination $s1 -Force }
    New-MeterSource $s1 $s2
    Build-Rig $s2
    if (-not $script:player) {
        $script:player = Start-Player 300
        $script:orig = Probe get $script:player.Id
        Write-Host ('  private player pid {0}; original state {1} (restored at the end)' -f $script:player.Id, $script:orig)
        $cal = Probe gaps $script:player.Id 2000
        Write-Host ('  calibration, the player itself: ' + $cal)
        Assert ((Get-LogNumber $cal 'gaps') -eq 0) 'the player''s own output has no gaps (the detector is sound)'
    }
    $res = @()
    for ($i = 1; $i -le $runs; $i++) {
        $log = Join-Path $Rig.Dir ('d' + [guid]::NewGuid().ToString('N') + '.log')
        $b = Start-Booster -BoosterArgs @('--pid', $script:player.Id, '--boost', '100', '--seconds', $sec) -Log $log
        $g = Probe gaps $b.Id ($sec * 1000 - 100)
        $b.WaitForExit()
        $l = if (Test-Path -LiteralPath $log) { (Get-Content -LiteralPath $log -Raw).Trim() } else { '(no log)' }
        Write-Host ('   [{0}] glitches={1} latencyMs={2} queueMs={3} | heard: {4}' -f $key, (Get-LogNumber $l 'glitches'), (Get-LogNumber $l 'latencyMs'), (Get-LogNumber $l 'padAvgMs'), $g)
        $res += [pscustomobject]@{ G = (Get-LogNumber $l 'glitches'); Lat = (Get-LogNumber $l 'latencyMs'); Q = (Get-LogNumber $l 'padAvgMs'); Gaps = (Get-LogNumber $g 'gaps') }
    }
    , $res
}
try {
    if (Want 'steady') {
        Write-Host '  -- clean playback: the buffer really holds its target, nothing heard, nothing counted --'
        $r = Run-Scenario 'steady' @() 4 3
        Assert (@($r | Where-Object { $_.G -ne 0 }).Count -eq 0) ('no glitch counted in {0} clean runs' -f $r.Count)
        Assert (@($r | Where-Object { $_.Gaps -ne 0 }).Count -eq 0) 'no gap heard'
        Assert (@($r | Where-Object { -not ($_.Q -ge 35) }).Count -eq 0) 'the queue at each wake-up is >= 35 ms of the 50 ms target (it was 0-20)'
    }
    if (Want 'hiccup50') {
        Write-Host '  -- 50 ms output-thread hiccups every 400 ms: absorbed --'
        $r = Run-Scenario 'hiccup50' @('render', '50', '400') 2 4
        Assert (@($r | Where-Object { $_.Gaps -ne 0 }).Count -eq 0) 'no gap heard (was one 30-40 ms gap per run)'
        Assert (@($r | Where-Object { $_.G -ne 0 }).Count -eq 0) 'no glitch counted: empty just in time, nothing lost'
    }
    if (Want 'hiccup60') {
        Write-Host '  -- 60 ms output-thread hiccups every 400 ms: one real dropout, then absorbed --'
        $r = Run-Scenario 'hiccup60' @('render', '60', '400') 2 4
        Assert (@($r | Where-Object { $_.Gaps -ne 1 -or $_.G -ne 1 }).Count -eq 0) 'exactly one gap heard and exactly one glitch counted (was 6 gaps, none counted)'
        Assert (@($r | Where-Object { $_.Lat -ne 80 }).Count -eq 0) 'the buffer grew once: latency 60 -> 80 ms'
    }
    if (Want 'capstall') {
        Write-Host '  -- one 300 ms capture stall --'
        $r = Run-Scenario 'capstall' @('capture1', '300') 1 4
        Assert (($r[0].G -eq 1) -and ($r[0].Lat -eq 80)) ('one stall = one glitch, latency 80 ms (got {0} glitch(es), {1} ms; it was 14-15 and 160)' -f $r[0].G, $r[0].Lat)
    }
    if (Want 'renstall') {
        Write-Host '  -- one 150 ms output-thread stall --'
        $r = Run-Scenario 'renstall' @('render1', '150') 1 4
        Assert (($r[0].Gaps -eq 1) -and ($r[0].G -eq 1)) ('the gap that is heard is counted (heard {0}, counted {1}; it was counted 0)' -f $r[0].Gaps, $r[0].G)
        Assert ($r[0].Lat -eq 80) 'and the buffer grew once'
    }
}
finally {
    if ($script:player) { Stop-Player $script:player $script:orig; Write-Host ('  player restored to ' + $script:orig) }
}
Complete-Test
