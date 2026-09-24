# Booster audio rig, for tests\booster\*. Dot-source after TestLib.ps1.
#
# SAFETY - each rule was learned from a real incident on the machine these were written on:
#   * Only a PRIVATE player is ever boosted: a hidden PowerShell process looping a tone at
#     about -58 dBFS. Never a real app, and never "Boost all audio" (the exclusivity test
#     only simulates that boost's mutex).
#   * The booster never runs inside the target's process tree - that captured its own
#     output and fed back, loudly. Player and booster are siblings here.
#   * The player's mixer volume and mute are read at start and put back BEFORE it exits:
#     Windows saves an app's mixer state when it closes, so a player left at 4% or muted
#     would change what every PowerShell window starts at.
#
#   Build-Rig [<source>]      compile the booster (warning level 4) and the probe from the
#                             source under test into a fresh scratch folder, $Rig.Dir
#   New-Build <source> <exe> [-Main <class>] [-Console] [-Platform x86] [-Extra <cs>] [-Warn4]
#   Start-Player [<seconds>]  the private player; returns once its audio session exists
#   Stop-Player <proc> <original>
#   Probe <mode> <args...>    get / set / watch / list / devices / gaps  (see Probes.cs)
#   Test-Restored <proc> <original>   the player is back at its original volume and mute
#   Start-Booster <args...>   start the booster CLI with a --log; returns the process
#   Invoke-Booster <args...>  run it to the end; returns @{ Code; Log }

$Rig = @{ Src = (Get-KitFile 'VolumeBooster\AppVolumeBooster.cs'); Dir = $null; Lib = $PSScriptRoot; Wav = $null }
$script:Csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path -LiteralPath $script:Csc)) { $script:Csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
$script:CscRefs = @('/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Management.dll')

function New-Build([string]$Source, [string]$Out, [string]$Main = '', [switch]$Console, [string]$Platform = '', [string[]]$Extra = @(), [switch]$Warn4) {
    New-Item -ItemType Directory -Force -Path (Split-Path $Out) | Out-Null
    $a = @('/nologo', $(if ($Console) { '/t:exe' } else { '/t:winexe' }), "/out:$Out") + $script:CscRefs
    if ($Main) { $a += "/main:$Main" }
    if ($Platform) { $a += "/platform:$Platform" }
    if ($Warn4) { $a += '/warn:4' }
    $o = & $script:Csc @a $Source @Extra 2>&1
    if ($LASTEXITCODE -ne 0) { $o | ForEach-Object { Write-Host ('     ' + $_) }; throw "build failed: $Out" }
    , @($o | Where-Object { "$_" -match 'warning' })
}

function Build-Rig([string]$Source = $Rig.Src) {
    $d = New-TestScratch 'rig'
    $w = New-Build $Source (Join-Path $d 'AppVolumeBooster.exe') -Warn4
    New-Build $Source (Join-Path $d 'probe.exe') -Main 'AppVolumeBoosterNs.Probes' -Console -Extra @(Join-Path $Rig.Lib 'Probes.cs') | Out-Null
    $Rig.Dir = $d
    $Rig.Wav = $null
    Write-Host ('  rig built from {0}, warning level 4: {1} warning(s)' -f (Split-Path $Source -Leaf), $w.Count)
}

function Probe {
    $o = Join-Path $Rig.Dir ('p' + [guid]::NewGuid().ToString('N') + '.txt')
    $argList = @($args | ForEach-Object { "$_" })
    if ($argList[0] -ne 'set') { $argList += (Q $o) }
    Start-Process (Join-Path $Rig.Dir 'probe.exe') -ArgumentList $argList -Wait -WindowStyle Hidden
    if (Test-Path -LiteralPath $o) { (Get-Content -LiteralPath $o -Raw).Trim() } else { '' }
}

function Start-Player([int]$seconds = 30) {
    if (-not $Rig.Wav) {
        $Rig.Wav = Join-Path $Rig.Dir 'quiet.wav'
        $sr = 44100; $n = $sr * 2
        $ms = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter($ms)
        $bw.Write([char[]]'RIFF'); $bw.Write([int](36 + $n * 2)); $bw.Write([char[]]'WAVE')
        $bw.Write([char[]]'fmt '); $bw.Write([int]16); $bw.Write([int16]1); $bw.Write([int16]1)
        $bw.Write([int]$sr); $bw.Write([int]($sr * 2)); $bw.Write([int16]2); $bw.Write([int16]16)
        $bw.Write([char[]]'data'); $bw.Write([int]($n * 2))
        for ($i = 0; $i -lt $n; $i++) { $bw.Write([int16]([math]::Sin($i * 0.05) * 40)) }   # about -58 dBFS
        $bw.Flush(); [IO.File]::WriteAllBytes($Rig.Wav, $ms.ToArray()); $bw.Close()
    }
    $cmd = "`$p = New-Object Media.SoundPlayer '$($Rig.Wav)'; `$p.PlayLooping(); Start-Sleep -Seconds $seconds; `$p.Stop()"
    $proc = Start-Process powershell -ArgumentList '-NoProfile', '-Command', $cmd -PassThru -WindowStyle Hidden
    # a child PowerShell needs a couple of seconds just to start: poll for its session
    $deadline = (Get-Date).AddSeconds(12)
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) { throw "player exited early (code $($proc.ExitCode))" }
        if ((Probe get $proc.Id) -match 'vol=') { return $proc }
        Start-Sleep -Milliseconds 300
    }
    Stop-Process -Id $proc.Id -ErrorAction SilentlyContinue
    throw 'the player never produced an audio session (is an output device active?)'
}

function Stop-Player($proc, [string]$original) {
    if ($proc -and -not $proc.HasExited -and $original -match 'vol=([0-9.]+) mute=([01])') {
        Probe set $proc.Id $Matches[1] $Matches[2] | Out-Null
        Start-Sleep -Milliseconds 300
    }
    if ($proc) { Stop-Process -Id $proc.Id -ErrorAction SilentlyContinue }
}

function Test-Restored($proc, [string]$original) { (Probe get $proc.Id) -eq $original }

function Start-Booster([string]$Exe = (Join-Path $Rig.Dir 'AppVolumeBooster.exe'), [string[]]$BoosterArgs, [string]$Log) {
    Start-Process $Exe -ArgumentList ($BoosterArgs + @('--log', (Q $Log))) -PassThru -WindowStyle Hidden
}

function Invoke-Booster([string[]]$BoosterArgs, [string]$Exe = (Join-Path $Rig.Dir 'AppVolumeBooster.exe')) {
    $log = Join-Path $Rig.Dir ('b' + [guid]::NewGuid().ToString('N') + '.log')
    $p = Start-Process $Exe -ArgumentList ($BoosterArgs + @('--log', (Q $log))) -PassThru -Wait -WindowStyle Hidden
    $l = if (Test-Path -LiteralPath $log) { (Get-Content -LiteralPath $log -Raw).Trim() } else { '(no log)' }
    [pscustomobject]@{ Code = $p.ExitCode; Log = $l }
}

function Get-LogNumber([string]$log, [string]$key) { if ($log -match ('\b' + $key + '=(-?[0-9.]+)')) { [double]$Matches[1] } else { [double]::NaN } }
