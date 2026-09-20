# Optional Add-on: Per-App Volume Booster (100% - 500%)

A small optional tool that makes chosen applications louder than the Windows
maximum - up to 500% - while every other app stays untouched (unless you opt
into boosting all audio). It lives in the `VolumeBooster\` subfolder and
changes nothing on the system: no drivers, no services, no admin rights, no
installation. If you never run it, it does nothing.

| File | Purpose |
|---|---|
| `VolumeBooster\AppVolumeBooster.exe` | The tool (double-click). Needs nothing installed. |
| `VolumeBooster\AppVolumeBooster.cs` | Complete source code, one file. |
| `VolumeBooster\Build-Booster.cmd` | Rebuilds the exe using the C# compiler that ships inside Windows - works even on a freshly reinstalled machine with no tools. It compiles to a temporary file and only then replaces the exe, so a failed rebuild leaves the working binary you already had and exits non-zero instead of reporting success. |
| `VolumeBooster\booster-state.txt` | Appears only while a boost is active (crash-recovery data, see below). Deleted automatically. Written through a temporary file and an atomic replace, so killing the booster during the write cannot leave it truncated. |

## How to use it

1. Start playback in the app(s) you want louder (an app must be making sound, or at least have opened audio once, to show up in the list).
2. Double-click `AppVolumeBooster.exe`.
3. Check one or more apps (press **Refresh** if one is missing), set the slider (100-500%), press **Start boost**.
4. Optional:
   - **Windows system sounds** - also boost beeps and notifications, without touching other apps. Windows does not give this mixer entry a real process; if capture fails, the status line says so and the reliable alternative is the next box.
   - **Boost all audio on this device** - boost everything on the default output, including Windows system sounds. The app list is ignored while this is checked.
5. Keep the booster window open while you need the boost. Press **Stop boost** or close the window to return everything to normal.

One instance can boost several apps at once. If one of those apps closes, the others keep boosting; the booster stops when the last selected app is gone (unless you are boosting all audio or system sounds alone).

## How it works (and why the mixer slider shows 4%)

Windows itself caps every per-app volume at 100% - there is no API to go higher.
The booster uses the same OS feature OBS uses for per-application audio capture
(*process loopback capture*, Windows 10 2004+): it captures the chosen app's
audio stream (or, for "all audio", everything except the booster itself),
multiplies the samples (float math, lossless), mixes multiple captures if needed,
and plays the louder copy to your default output.

One quirk discovered while building this (verified on this machine, build
26100.9168): the captured stream already has the app's mixer volume applied, so
simply muting the original would also silence the capture. Instead the booster
holds each target's mixer slider at **4%** (a whisper, ~30 dB below the boosted
copy) and multiplies the captured signal by `boost / 0.04`. That is why, while
a boost is active:

- each boosted app's slider in the Volume Mixer sits at 4% - **by design, do not "fix" it**. That 4% is also the quickest health check there is: if a boosted app's slider does *not* drop to 4% while the boost is running, the boost is broken, and what you are hearing is the app at full volume plus a copy overdriven by 25x. Stop the boost rather than reaching for the volume knob;
- with **Boost all audio**, every other slider (including System sounds) sits at 4% the same way;
- a new entry `AppVolumeBooster` appears in the mixer - that is the boosted copy (leave it at 100%);
- audio arrives with ~60 ms extra latency (fine for music/video/games; noticeable in rhythm games). If the machine is too busy to keep such a small buffer fed - e.g. a demanding game - the booster grows the buffer automatically, about 20 ms per audible dropout up to ~160 ms, instead of continuing to glitch. The status line shows the current value; stopping and restarting the boost resets it to ~60 ms;
- a soft limiter rounds off peaks above 95% full scale, so high boost on already-loud material compresses rather than crackles. At 500% that kicks in earlier. Mixing two loud apps can hit the limiter sooner than one app would. If the result sounds squashed, the source is already near maximum - there is nothing left to boost cleanly.

When the boost stops - for any reason - the slider(s) are restored to their previous values.

## Interplay with the volume memory this kit protects

Windows saves an app's mixer volume when the app closes. While boosting, the
app's live volume is 4%, so the dangerous case is "app closes while boosted".
All of these were tested end-to-end on this machine (originally single-app;
multi-app uses the same restore path per target):

| Scenario | What happens |
|---|---|
| You press Stop / close the booster | Sliders restored instantly. Verified. |
| A boosted app exits or crashes while boosting | That app's saved volume is restored. If other selected apps remain, boosting continues; if it was the last one, the booster stops. Verified for the single-app case: the app came back at its old volume on relaunch. |
| The booster itself is killed hard (or PC loses power) mid-boost | Windows may persist the 4%. The booster leaves a note in `booster-state.txt`; the next time ANY copy of the booster runs (and every few seconds while one is open), it finds each ducked app (and system sounds, if that was the target) and repairs the slider to the saved value. The note itself is written atomically, so a kill landing *during* that write leaves the previous contents intact rather than a half-written file. Verified, including kills timed into the write. |
| Nothing else available | Just drag the slider(s) back up in the Volume Mixer - they are ordinary volume values, nothing is locked. |

The booster never touches the registry, the PropertyStore, or anything else
this kit manages.

## Honest limitations

- **Boost is per process (tree), not per tab/stream.** Boosting a browser boosts all of its tabs; Windows groups them into one audio session set.
- **Default output device only.** The boosted copy plays to the default device. If you switch output devices mid-boost, the booster stops safely - press Start again.
- **System sounds as their own target are best-effort.** The Volume Mixer entry has process id 0; there is no supported API to capture that session alone. The checkbox tries anyway. If Windows refuses, use **Boost all audio on this device**, which includes system sounds because it captures everything except the booster.
- **Boost all audio ducks every session** on the default device, not only the apps you had checked. New apps that start making sound are ducked too, until you stop.
- **The booster will not boost an app it is running inside.** Per-app capture takes the target's whole process tree, so if you launch the booster *from* a program - a shell, a script, a terminal - and then boost that same program, it would capture its own boosted output, amplify it again, and run away into feedback at whatever the slider says. Double-clicking the exe is never affected, because its parent is Explorer. The case is detected before any audio starts and refused with an explanation; "Boost all audio" is immune either way, since it captures everything *except* the booster.
- **Protected (DRM) audio paths** may deliver silence to the capture API; if an app produces silence when boosted, that is why.
- **Anti-cheat safe by construction**: nothing is injected into any process - the audio is read through a public OS API, same as OBS. Games cannot tell the difference.
- **Unsigned exe**: SmartScreen or an antivirus may warn on first run - expected for any home-built exe. The full source sits next to it, and `Build-Booster.cmd` reproduces the exe from that source using only Windows' own compiler.
- **Hardware care**: 500% is about 14 dB over what the system normally allows. On laptop speakers at full device volume this can sound bad or, on cheap speakers, damage them over time. Prefer boosting quiet content rather than everything. Start low and only go higher if the source is actually quiet. Boosting all audio at a high slider is the most aggressive setting this tool has.
- Windows 10 version 2004 (build 19041) or newer required.

## Command line (for scripts; the window appears when run with no arguments)

```
AppVolumeBooster.exe --pid <N> [--pid <N> ...] | --name <exe> [--name <exe> ...] | --all
    [--system-sounds] [--boost 100..500] [--seconds <S>] [--padms <10..150>] [--log <file>]
```

Runs headless: boosts the given process(es) until they close, `--seconds`
elapse, or the booster process is terminated; restores volumes on the way out
and writes a one-line result to `--log`.

- Repeat `--pid` / `--name` to boost several programs at once (`--name` matches every running process of that exe).
- `--system-sounds` includes the Windows System sounds session when the OS allows that capture.
- `--all` boosts every sound on the default device, including system sounds (the app list is ignored).
- `--system` is accepted as a synonym for `--system-sounds`.
- `--padms` sets the starting render-buffer size in milliseconds (10-150, default ~50). It exists for latency testing; leave it alone otherwise.
- An unrecognised or misspelled argument is an **error**: the run stops with exit code 1 rather than quietly continuing on the defaults, which is what `--bost 300` used to do. A flag with no value - at the end of the line, or followed by another flag - is reported the same way.
- `--boost` is clamped to 100-500, and the `--log` line reports the value actually applied rather than the one requested, so `--boost 5000` logs `boost=500`.
- This is a windowless exe with no console, so `--log` is the only place any error can appear. It is resolved before the rest of the command line is parsed, so even an argument error still reaches the log file.

## How this was verified (2026-08-23, this machine)

A test harness (WASAPI tone generator at a known amplitude + endpoint loopback
RMS meter) measured the actual speaker feed: baseline tone RMS 0.1768, boosted
at 200% -> 0.3533 (theoretical 0.3536, i.e. exact 2.00x within 0.1%), with the
target's slider confirmed at 4% during boost and restored after. The
close-while-boosted, kill-while-boosted, and self-heal paths were each
exercised with real process kills; per-app volume memory (the main subject of
this kit) survived every scenario with the correct value. Multi-app mixing
uses that same per-stream gain on each capture, then sums and soft-clips.

Re-verified 2026-09-20, after the multi-app rewrite and a review pass over the source: the code compiles warning-clean at `/warn:4`; the state file survived nine process kills timed across the write window, where a plain non-atomic write corrupted it in two of those nine; and a state-file update that cannot take the cross-process lock is now skipped rather than racing another booster instance.

Fixed 2026-09-20: **per-app boost was not ducking anything at all.** The COM interop declared
`IsSystemSoundsSession()` without `[PreserveSig]`, so .NET marshalled it as
`HRESULT IsSystemSoundsSession([out,retval] int*)`. The real method takes no such pointer and
never writes it, so the value read back was always 0 - "yes, this is the system sounds session"
for *every* session on the device. `WantSession` therefore rejected every ordinary app, the
target kept its slider at 100% while still being captured, and the captured signal was still
multiplied by `boost / 0.04`. The result was constant heavy distortion at any slider position,
including 100%. **Boost all audio was never affected**, because it returns true before reaching
the system-sounds test - which is exactly why one mode sounded fine and the other did not.

Confirmed by sampling the live mixer volumes at 50 ms for 10 s during a real browser boost: the
browser session read 1.000 in all 164 samples, with zero transitions. With `[PreserveSig]` added,
the same probe reports the browser and Discord as *not* system sounds and the real pid-0 session
as system sounds, ducking resumes, and the boost sounds clean again.

The same flaw applied to every other COM method in the file, so all 65 of them now carry
`[PreserveSig]` and their `int` returns are finally real HRESULTs rather than a constant 0.
That brings one rule with it: **an HRESULT is a failure only when it is negative.** Several
calls made here legitimately return a non-zero *success* - `S_FALSE` from `Stop()` on an
already-stopped client, `AUDCLNT_S_BUFFER_EMPTY` from `GetBuffer` - so `Native.Check` tests
`hr < 0`, not `hr != 0`, which would have started throwing on perfectly good returns. Any new
code that inspects one of these results has to do the same. The capture loop's
`if (GetBuffer(...) != 0) break;` is deliberately left as-is and commented: there the only
non-zero success means "no data", and breaking out is exactly the right response anyway.

Verified after that pass by boosting a silent session and tracing its mixer volume: ducked
1.000 -> 0.040 within 88 ms, held, restored to 1.000 the moment the timer expired, with
`glitches=0` and precisely 6.01 s of audio captured. A session created *during* a boost was
ducked 58 ms after it appeared, which exercises the session-notification callback - one of the
two interfaces the program implements rather than calls, and the ones most at risk from a
marshalling change.

Added 2026-09-20: a guard against boosting a process tree that contains the booster itself.
This was found the hard way, by doing it - the feedback is immediate and loud. The predicate
was checked directly against the real method: targeting the parent shell reports true, while
Discord and the browser report false, so ordinary targets are unaffected. End to end, the
previously-catastrophic case now exits in about a second with "no capture stream started:
... would feed back on itself" and never opens an audio stream, while an unrelated target
still ducks to 0.040 and restores with glitches=0.

## Why this approach (alternatives considered)

- **Letasoft Sound Booster** (the commercial reference): code injection into every sound-playing process plus a system-wide APO - powerful (up to 500%, global) but invasive, and injection can upset anti-cheat.
- **Microsoft Store "volume booster" apps**: the one reviewed for this project turned out to be a 151 MB wrapper that requires installing the VB-CABLE virtual audio driver and re-routing the DEFAULT audio device through itself - global boost only, and when it breaks, all system audio goes silent.
- **Equalizer APO preamp**: solid, but system/endpoint-wide, needs an APO install per device and offers no per-app control.
- **This tool**: per-app (one or many), optional all-audio mode, user-mode only, nothing installed - at the cost of ~50 ms latency and the 4% slider quirk. For boosting quiet apps, that is the better trade.

## Maintenance

Same policy as the main kit: not actively maintained. The source is small and
commented; `Build-Booster.cmd` rebuilds it on bare Windows.
