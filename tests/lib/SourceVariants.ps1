# TEST-ONLY variants of the booster source - the shipped file is never touched. Each
# function reads the source under test, applies exact edits and writes the variant for
# New-Build. Every anchor must match exactly once or the build throws: an edit that
# silently did not apply would leave a test proving nothing.
#
#   New-MeterSource <src> <out>     measurements appended to the CLI log line:
#       padAvgMs/padMinMs   queue level at each render wake-up once real audio flows
#       dryPasses           wake-ups that found the queue empty
#       dryWithData         ... with audio waiting in the ring (a render-side stall)
#       firstGlitchMs       ms from the first real audio to the first counted glitch
#       capMaxGapMs/capGaps30, silentPkts/pkts   capture packet timing
#   New-StressSource <src> <out> <kind> <ms> [<everyMs>]
#       render   the render thread oversleeps <ms> every <everyMs> once audio flows
#       capture  the same in the capture thread
#       render1 / capture1   ONE such stall of <ms>, about 1 s in
#   New-Fault09Source <src> <faultOut> <nofixOut> [<stallMs>]
#       one render-thread stall (default 300 ms) plus backlogMs in the log; the nofix
#       variant also loses both backlog guards - the original bug
#   New-Fault10Source <src> <faultOut> <nofixOut>
#       the output "dies" about 1 s in (AUDCLNT_E_DEVICE_INVALIDATED from GetCurrentPadding);
#       the nofix variant also loses the watcher's check - the original bug
#
# Edits are a FLAT list: old1, new1, old2, new2, ...  (nested arrays built with the unary
# comma are easy to get one level wrong in PowerShell, and a wrong level applies nothing).

function Edit-Source([string]$Source, [string]$Out, [string[]]$Pairs) {
    if ($Pairs.Count % 2) { throw 'Edit-Source: the edit list must be old/new pairs' }
    $d = [IO.File]::ReadAllText($Source) -replace "`r`n", "`n"
    for ($i = 0; $i -lt $Pairs.Count; $i += 2) {
        $old = $Pairs[$i] -replace "`r`n", "`n"; $new = $Pairs[$i + 1] -replace "`r`n", "`n"
        $n = ([regex]::Matches($d, [regex]::Escape($old))).Count
        if ($n -ne 1) { throw ('test-only source edit: anchor matched {0} times: {1}' -f $n, $old.Substring(0, [Math]::Min(80, $old.Length))) }
        $d = $d.Replace($old, $new)
    }
    [IO.File]::WriteAllText($Out, ($d -replace "`n", "`r`n"), (New-Object Text.ASCIIEncoding))
}

function New-MeterSource([string]$Source, [string]$Out) {
    Edit-Source $Source $Out @(
        '        public long CapSamples, RenFrames, Glitches, TrimmedSamples;',
        @'
        public long CapSamples, RenFrames, Glitches, TrimmedSamples;
        public long MPadSum, MPadN, MDryPasses, MDryWithData, MCapMaxGap, MCapGaps30, MSilentPkts, MPkts;
        public long MPadMin = long.MaxValue, MFirstGlitchMs = -1, MFirstRealMs = -1, MLastCap = -1;
        public readonly Stopwatch MClock = Stopwatch.StartNew();
        public string MetricsForTest
        {
            get
            {
                return string.Format(CultureInfo.InvariantCulture,
                    " padAvgMs={0:0.0} padMinMs={1:0.0} dryPasses={2} dryWithData={3} firstGlitchMs={4} capMaxGapMs={5} capGaps30={6} silentPkts={7} pkts={8}",
                    MPadN == 0 ? -1.0 : MPadSum / (double)MPadN / 48.0, MPadMin == long.MaxValue ? -1.0 : MPadMin / 48.0,
                    MDryPasses, MDryWithData, MFirstGlitchMs, MCapMaxGap, MCapGaps30, MSilentPkts, MPkts);
            }
        }
'@,
        @'
                NoteRenderResult(hrPad);
                if (hrPad != 0) continue;
'@,
        @'
                NoteRenderResult(hrPad);
                if (hrPad != 0) continue;
                bool mReal = Interlocked.Read(ref RenFrames) > 0;
                if (mReal) { MPadSum += pad; MPadN++; if (pad < MPadMin) MPadMin = pad; if (pad == 0) MDryPasses++; }
'@,
        '                uint gotFrames = (uint)(maxGot / 2);',
        @'
                uint gotFrames = (uint)(maxGot / 2);
                if (mReal && pad == 0 && gotFrames > 0) MDryWithData++;
'@,
        'Interlocked.Increment(ref Glitches);',
        'Interlocked.Increment(ref Glitches); if (MFirstGlitchMs < 0) MFirstGlitchMs = MClock.ElapsedMilliseconds - MFirstRealMs;',
        '                Interlocked.Add(ref RenFrames, gotFrames);',
        @'
                if (MFirstRealMs < 0) MFirstRealMs = MClock.ElapsedMilliseconds;
                Interlocked.Add(ref RenFrames, gotFrames);
'@,
        @'
                    Interlocked.Add(ref CapSamples, samples);
                    ring.Push(tmp, samples);
'@,
        @'
                    Interlocked.Add(ref CapSamples, samples);
                    { long mNow = MClock.ElapsedMilliseconds; if (MLastCap >= 0) { long mg = mNow - MLastCap; if (mg > MCapMaxGap) MCapMaxGap = mg; if (mg > 30) MCapGaps30++; } MLastCap = mNow; }
                    MPkts++; if ((fl & K.BUF_SILENT) != 0) MSilentPkts++;
                    ring.Push(tmp, samples);
'@,
        @'
                TryLog(log, line + "\r\n");
                return 0;
'@,
        @'
                line += eng.MetricsForTest;
                TryLog(log, line + "\r\n");
                return 0;
'@
    )
}

# the first lines of the render and capture loops, which the stall builders hook into
$script:RenderTop  = "            while (running)`n            {`n                renEv.WaitOne(100);`n"
$script:CaptureTop = "            while (running)`n            {`n                ev.WaitOne(100);`n                uint pkt;`n"

function New-StressSource([string]$Source, [string]$Out, [string]$Kind, [int]$Ms, [int]$EveryMs = 0) {
    switch ($Kind) {
        'render'   { $top = $script:RenderTop;  $decl = '            System.Diagnostics.Stopwatch sHic = System.Diagnostics.Stopwatch.StartNew(); long sNext = 1000;'
                     $line = "if (Interlocked.Read(ref RenFrames) > 0 && sHic.ElapsedMilliseconds >= sNext) { Thread.Sleep($Ms); sNext = sHic.ElapsedMilliseconds + $EveryMs; }" }
        'capture'  { $top = $script:CaptureTop; $decl = '            System.Diagnostics.Stopwatch sHic = System.Diagnostics.Stopwatch.StartNew(); long sNext = 1000;'
                     $line = "if (sHic.ElapsedMilliseconds >= sNext) { Thread.Sleep($Ms); sNext = sHic.ElapsedMilliseconds + $EveryMs; }" }
        'render1'  { $top = $script:RenderTop;  $decl = '            bool sDone = false;'
                     $line = "if (!sDone && Interlocked.Read(ref RenFrames) > 48000) { sDone = true; Thread.Sleep($Ms); }" }
        'capture1' { $top = $script:CaptureTop; $decl = '            bool sDone = false;'
                     $line = "if (!sDone && Interlocked.Read(ref CapSamples) > 96000) { sDone = true; Thread.Sleep($Ms); }" }
        default    { throw "unknown stress kind: $Kind" }
    }
    Edit-Source $Source $Out @($top, ($decl + "`n" + $top.Replace("{`n", "{`n                $line`n")))
}

function New-Fault09Source([string]$Source, [string]$FaultOut, [string]$NoFixOut, [int]$StallMs = 300) {
    $ren = $script:RenderTop.TrimEnd("`n")
    $common = @(
        $ren,
        ("            bool stalled = false;`n" + $ren.Replace("{`n", "{`n                if (!stalled && Interlocked.Read(ref RenFrames) > 48000) { stalled = true; Thread.Sleep($StallMs); }`n")),
        '        public int TrimTo(int keep)',
        "        public int CountForTest { get { lock (gate) { return rCount; } } }`n`n        public int TrimTo(int keep)",
        '        public int LatencyMs { get { return targetPadFrames / 48 + 10; } }',
        "        public int LatencyMs { get { return targetPadFrames / 48 + 10; } }`n        public int BacklogPeakMs;`n        public int BacklogMsForTest { get { int m = 0; foreach (SampleRing r in mixRings) m = Math.Max(m, r.CountForTest); return m / 96; } }",
        '                watchTick++;',
        "                watchTick++;`n                if (watchTick > 4) BacklogPeakMs = BacklogMsForTest;",
        '                TryLog(log, line + "\r\n");',
        ('                line += " backlogMs=" + eng.BacklogPeakMs;' + "`n" + '                TryLog(log, line + "\r\n");')
    )
    Edit-Source $Source $FaultOut $common
    Edit-Source $Source $NoFixOut ($common + @(
        ("                    if (!flowing)`n                    {`n                        int late = rings[r].TrimTo(samples);`n" +
         "                        if (late > 0) Interlocked.Add(ref TrimmedSamples, late);`n                    }`n"), '',
        ("                for (int r = 0; r < rings.Length; r++)`n                {`n                    int dropped = rings[r].TrimTo(MaxBacklogSamples);`n" +
         "                    if (dropped > 0) Interlocked.Add(ref TrimmedSamples, dropped);`n                }"), ''
    ))
}

function New-Fault10Source([string]$Source, [string]$FaultOut, [string]$NoFixOut) {
    $fault = @(
        '                uint pad; int hrPad = renClient.GetCurrentPadding(out pad);',
        '                uint pad = 0; int hrPad = (Interlocked.Read(ref RenFrames) > 48000) ? unchecked((int)0x88890004) : renClient.GetCurrentPadding(out pad);'
    )
    Edit-Source $Source $FaultOut $fault
    Edit-Source $Source $NoFixOut ($fault + @(
        "                string fatal = fatalReason;`n                if (fatal != null) { StopBecause(fatal); return; }`n", ''
    ))
}
