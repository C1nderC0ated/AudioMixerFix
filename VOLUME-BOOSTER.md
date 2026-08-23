# Optional Add-on: Per-App Volume Booster (100% - 200%)

A small optional tool that makes ONE chosen application louder than the Windows
maximum - up to 200% - while every other app stays untouched. It lives in the
`VolumeBooster\` subfolder and changes nothing on the system: no drivers, no
services, no admin rights, no installation. If you never run it, it does nothing.

| File | Purpose |
|---|---|
| `VolumeBooster\AppVolumeBooster.exe` | The tool (double-click). ~32 KB, needs nothing installed. |
| `VolumeBooster\AppVolumeBooster.cs` | Complete source code, one file. |
| `VolumeBooster\Build-Booster.cmd` | Rebuilds the exe using the C# compiler that ships inside Windows - works even on a freshly reinstalled machine with no tools. |
| `VolumeBooster\booster-state.txt` | Appears only while a boost is active (crash-recovery data, see below). Deleted automatically. |

## How to use it

1. Start playback in the app you want louder (it must be making sound, or at least have opened audio once).
2. Double-click `AppVolumeBooster.exe`.
3. Pick the app in the dropdown (press **Refresh** if it is missing), set the slider (100-200%), press **Start boost**.
4. Keep the booster window open while you need the boost. Press **Stop boost** or close the window to return everything to normal.

To boost two apps at once, simply run a second copy of the exe.

## How it works (and why the mixer slider shows 4%)

Windows itself caps every per-app volume at 100% - there is no API to go higher.
The booster uses the same OS feature OBS uses for per-application audio capture
(*process loopback capture*, Windows 10 2004+): it captures ONLY the chosen
app's audio stream, multiplies the samples (float math, lossless), and plays the
louder copy to your default output.

One quirk discovered while building this (verified on this machine, build
26100.9168): the captured stream already has the app's mixer volume applied, so
simply muting the original would also silence the capture. Instead the booster
holds the app's mixer slider at **4%** (a whisper, ~30 dB below the boosted
copy) and multiplies the captured signal by `boost / 0.04`. That is why, while
a boost is active:

- the app's slider in the Volume Mixer sits at 4% - **by design, do not "fix" it**;
- a new entry `AppVolumeBooster` appears in the mixer - that is the boosted copy (leave it at 100%);
- audio arrives with ~60 ms extra latency (fine for music/video/games; noticeable in rhythm games). If the machine is too busy to keep such a small buffer fed - e.g. a demanding game - the booster grows the buffer automatically, about 20 ms per audible dropout up to ~160 ms, instead of continuing to glitch. The status line shows the current value; stopping and restarting the boost resets it to ~60 ms;
- a soft limiter rounds off peaks above 95% full scale, so 200% on already-loud material compresses rather than crackles. If the result sounds squashed, the source is already near maximum - there is nothing left to boost cleanly.

When the boost stops - for any reason - the slider is restored to its previous value.

## Interplay with the volume memory this kit protects

Windows saves an app's mixer volume when the app closes. While boosting, the
app's live volume is 4%, so the dangerous case is "app closes while boosted".
All of these were tested end-to-end on this machine:

| Scenario | What happens |
|---|---|
| You press Stop / close the booster | Slider restored instantly. Verified. |
| The boosted app exits or crashes while boosting | The booster notices within a second, stops, and restores the saved volume through its still-held session handle. Verified: the app came back at its old volume on relaunch. |
| The booster itself is killed hard (or PC loses power) mid-boost | Windows may persist the 4%. The booster leaves a note in `booster-state.txt`; the next time ANY copy of the booster runs (and every few seconds while one is open), it finds the app and repairs its slider to the saved value. Verified. |
| Nothing else available | Just drag the app's slider back up in the Volume Mixer - it is an ordinary volume value, nothing is locked. |

The booster never touches the registry, the PropertyStore, or anything else
this kit manages.

## Honest limitations

- **Boost is per process (tree), not per tab/stream.** Boosting a browser boosts all of its tabs; Windows groups them into one audio session set.
- **Default output device only.** The boosted copy plays to the default device. If you switch output devices mid-boost, the booster stops safely - press Start again.
- **Protected (DRM) audio paths** may deliver silence to the capture API; if an app produces silence when boosted, that is why.
- **Anti-cheat safe by construction**: nothing is injected into any process - the audio is read through a public OS API, same as OBS. Games cannot tell the difference.
- **Unsigned exe**: SmartScreen or an antivirus may warn on first run - expected for any home-built exe. The full source sits next to it, and `Build-Booster.cmd` reproduces the exe from that source using only Windows' own compiler.
- **Hardware care**: 200% is 6 dB over what the system normally allows. On laptop speakers at full device volume this can sound bad or, on cheap speakers, damage them over time. Prefer boosting quiet content rather than everything.
- Windows 10 version 2004 (build 19041) or newer required. System sounds (pid 0) cannot be boosted.

## Command line (for scripts; the window appears when run with no arguments)

```
AppVolumeBooster.exe --pid <N> | --name <exe>  [--boost 100..200]  [--seconds <S>]  [--log <file>]
```

Runs headless: boosts the given process until the app closes, `--seconds`
elapse, or the booster process is terminated; restores volumes on the way out
and writes a one-line result to `--log`.

## How this was verified (2026-08-23, this machine)

A test harness (WASAPI tone generator at a known amplitude + endpoint loopback
RMS meter) measured the actual speaker feed: baseline tone RMS 0.1768, boosted
at 200% -> 0.3533 (theoretical 0.3536, i.e. exact 2.00x within 0.1%), with the
target's slider confirmed at 4% during boost and restored after. The
close-while-boosted, kill-while-boosted, and self-heal paths were each
exercised with real process kills; per-app volume memory (the main subject of
this kit) survived every scenario with the correct value.

## Why this approach (alternatives considered)

- **Letasoft Sound Booster** (the commercial reference): code injection into every sound-playing process plus a system-wide APO - powerful (up to 500%, global) but invasive, and injection can upset anti-cheat.
- **Microsoft Store "volume booster" apps**: the one reviewed for this project turned out to be a 151 MB wrapper that requires installing the VB-CABLE virtual audio driver and re-routing the DEFAULT audio device through itself - global boost only, and when it breaks, all system audio goes silent.
- **Equalizer APO preamp**: solid, but system/endpoint-wide, needs an APO install per device and offers no per-app control.
- **This tool**: per-app, 32 KB, user-mode only, nothing installed, nothing global touched - at the cost of ~50 ms latency and the 4% slider quirk. For boosting one quiet app, that is the better trade.

## Maintenance

Same policy as the main kit: not actively maintained. The source is small and
commented; `Build-Booster.cmd` rebuilds it on bare Windows.
