# Tests

These tests exist so that a change to the kit cannot quietly bring back a bug it already had.
Almost every check below was written against one of those bugs, and each file's opening
comment says which one. They are plain Windows PowerShell 5.1 with no framework and nothing
to install, and they leave the machine as they found it. The next section says exactly what
they touch.

## Running them

From the kit folder, in an ordinary PowerShell window (no admin rights needed):

```
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Run-Tests.ps1
```

This runs the fast suites (script, launcher and build) in about three minutes.

```
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Run-Tests.ps1 -Audio
```

This adds the booster's audio tests, about ten more minutes. They need a working output
device (speakers or headphones switched on). During those minutes a hidden test process
plays a faint tone.

```
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Run-Tests.ps1 -Only Launcher,Consistency
```

This runs only the test files whose name contains one of the words. A word that matches no
file stops the run, because a typo would otherwise look like a pass. An audio test's name
also needs `-Audio`. Any test file also runs on its own, for example
`powershell -NoProfile -ExecutionPolicy Bypass -File tests\script\Consistency.Tests.ps1`.

Every test file runs in its own `powershell.exe`, and every check prints one `PASS` or
`FAIL` line. The runner ends with a summary per file. A file fails when it prints a
`FAIL`, when it exits non-zero, or when it checked nothing at all. The runner exits with 0
when everything passed and 1 otherwise - also when it found no test file to run.

Run one runner at a time. Every test file clears the shared test registry key when it
finishes, so two runs side by side would pull it out from under each other.

## What they touch

**Files.** Tests write only to fresh folders under `%TEMP%\AudioMixerFix-tests`. Each test
file removes its own folders when it ends. Nothing in the kit folder is ever written: even
the booster is compiled into a scratch folder, and the kit's own `AppVolumeBooster.exe` is
never replaced. The build test only reads it, to compare it with a fresh build.

**Registry.** Tests write only to `HKCU\Software\AudioMixerFixTest`, which is removed at the
end of every test file. A step under test gets its key paths pointed at that key, and
`reg.exe`, the service cmdlets, `Get-CimInstance` and the security-label calls are replaced
by stubs that record what they were asked to do. The real volume store, `MMDevices`, the
Bluetooth key and the audio services are never written or changed. A few tests read real
state without changing it: Devices counts the active outputs under `MMDevices`,
WrongProfile asks who owns `explorer.exe`, and Exclusivity looks for boosters of your own
that are running.

**The launcher.** Nothing is ever elevated and no UAC prompt appears. The launcher test
runs the launcher's own PowerShell line with `-Verb RunAs` swapped for
`-Wait -WindowStyle Hidden`, aimed at stub scripts. Its copies of the launcher run with
`fltmc` and PowerShell replaced by stubs. The real worker script never runs from there.

**The real script, as a whole,** runs in only two places. CommandLine gives it bad command
lines - from the 32-bit PowerShell too - and every one of them stops at the argument check,
before the first step. BackupFolder runs it from scratch copies of the kit, with all five
step functions replaced by stubs and `%LOCALAPPDATA%` pointed at a scratch folder.

**Everything else** in the script is tested one function at a time. The function is taken
out of `Fix-AudioMixer.ps1` by PowerShell's own parser (`Get-ShippedFunction`), so what runs
is exactly what ships. It is never a copy that could drift. Shortcuts are throwaway `.lnk`
files in a scratch folder. To test where shortcuts are looked for, BrowserFlags points
`%APPDATA%` and `%ProgramData%` at a scratch folder for its own process; the two real
Desktop folders cannot be pointed elsewhere and are listed, nothing more.
BackupFolder and ReadOnlyFolder make a scratch folder read-only
by denying the right to add files and folders to it (`icacls /deny ...(WD,AD)`), and remove
that deny again.

**Audio, only with `-Audio`.** The rules at the top of `lib\AudioRig.ps1` apply, and each
one comes from something that went wrong while these tests were being written:

- Only a private player is boosted: a hidden `powershell.exe` that loops a tone at about
  -58 dBFS. Most tests boost it at 100%, which plays it at its own level. The window test
  uses the window's default of 150%, and Gain goes to 200% for a few seconds. Either way,
  what you hear stays a faint tone. No real app is ever boosted, and neither is "Boost all
  audio".
- The player's volume and mute in the Volume Mixer are read first and put back before it
  exits. Windows saves an app's mixer settings when the app closes, and this app is
  `powershell.exe`, so a player left at 4% would make every PowerShell window start at 4%.
- The booster never runs inside the player's process tree. Player and booster are siblings,
  because a booster inside its target's tree captured its own output and fed it back,
  loudly.
- Exclusivity never starts an all-audio boost. It simulates one by holding the
  `AppVolumeBooster.AllAudio` mutex for a few seconds, so a real "Boost all audio" started
  during those seconds would refuse.
- The booster keeps its crash note in `%LOCALAPPDATA%\AppVolumeBooster\booster-state.txt`,
  your real one. The rig reads it before a test and puts it back exactly as it was
  afterwards, and removes the folder if it created it. Do not run the audio tests while
  you are boosting something yourself. `settings.txt` beside it is put back the same way:
  a boost started in the window writes the app it boosted there.
- UiRace shows the booster's tray icon for about a second, and its window far off the
  screen. Nothing else of the window or its tray panel appears on the screen. It registers
  Ctrl+Alt+B, Ctrl+Alt+Up and Ctrl+Alt+Down for a moment (and skips that part if another
  program holds them), and writes its Start-with-Windows shortcut into a scratch folder,
  never the real Startup folder.
- The boosted copy no longer leaves an entry in the per-app volume store. Until
  2026-09-30 every test run added one for its scratch copy of the booster (its own path
  each time), and those entries stay until they are removed: look for `AppVolumeBooster.exe`,
  `uitest.exe` and `stoptest.exe` with a path under `%TEMP%` in the store.
- ChildTree runs a hidden "browser": a PowerShell process that spawns the faint-tone
  players as its children, one after another. Only those children are boosted.

If a run is interrupted, wait a few seconds for any test booster's timer to run out. Then
check that "Windows PowerShell" in the Volume Mixer is at its usual level and not muted.
Delete `%TEMP%\AudioMixerFix-tests` and `HKCU\Software\AudioMixerFixTest` if they were left
behind.

## Layout

```
tests\
  Run-Tests.ps1         the runner
  README.md             this file
  lib\                  shared helpers, dot-sourced by the tests
    TestLib.ps1         $Kit, Get-KitFile, New-TestScratch, $TestKey, Get-ShippedFunction,
                        Assert, Complete-Test
    ScriptStubs.ps1     recording stubs for Report, Section, Start-Sleep, the service cmdlets
    AudioRig.ps1        builds the booster and the probe, the private player, Probe,
                        Start-Booster, Invoke-Booster
    SourceVariants.ps1  test-only variants of the booster source: meters, injected stalls,
                        and the "before" builds with a fix taken out again
    Probes.cs           reads and sets an app's mixer volume, watches it, lists apps and
                        outputs, records the booster's own output to find gaps and
                        measure its level, shows the booster's view of the processes,
                        saves a picture of the booster's window (`shot`, for a look at
                        the layout - no test uses it)
    RingTest.cs, StopTest.cs, UiTest.cs, ExclTest.cs
                        entry points that drive one of the booster's own classes directly
    SessionMaker.cs     a silent app that closes its audio session and opens it again
  script\               Fix-AudioMixer.ps1, Check-Store.bat and the documents
  launcher\             Fix-AudioMixer.cmd
  build\                the booster builds on a bare Windows
  booster\              the booster, with real audio (-Audio only)
```

## The tests

Each file's opening comment tells the whole story. This table is the short version.

| File | What it checks | The bug it was written for |
|---|---|---|
| `script\Store.Tests.ps1` | The default fix creates the canonical store key whenever that key is missing, labels it, and labels saved entries that lack the label. `-CheckOnly` gives a verdict on each label. A label that cannot be set is a `[FAIL]`. `-Status` lists the saved apps by name with their saved level, and notes an app saved at exactly 4%. It warns about a label that is not the Low one, except on an empty placeholder that Windows does not use. | With only the empty Multimedia placeholder present, the placeholder was labelled, `[OK]` was printed, and there was still no store. `-CheckOnly` printed a Medium label as `[INFO]` and ended with no warning. A failed label was a `[WARN]` with exit code 0. `-Status` showed the first 15 keys in hash order, names only. On a healthy machine it warned about the unused placeholder's label and said to run the default fix. |
| `script\StatusEffects.Tests.ps1` | `-Status` finds the effect (APO) class ids of an output in `REG_MULTI_SZ` values too, leaves out what is not an effect, survives a class id with no registration, and reads the enhancements switch from `FxProperties`. | Only plain string values counted, so one third-party APO was shown and six beside it were missed. An unregistered class id ended the status with a `[FAIL]`. The switch was read from a key where Windows does not keep it. |
| `script\RebuildStore.Tests.ps1` | `-RebuildStore` refuses without a verified backup. It reports only what really happened, always starts the audio services again, and then the services that depend on them. It ends with the canonical store present and labelled. `-CheckOnly` works without admin rights. A backup in a folder whose name holds `[v2]` is still found. | It could print `[FAIL] Could not restart` and then `[OK] ... audio service restarted`, and it said "deleted" without checking the delete. A half-failed stop left the Windows Audio service down. In a folder named like `[v2]`, valid backups looked failed, because `-Path` reads `[` and `]` as wildcards. |
| `script\CleanGhostEndpoints.Tests.ps1` | `-CleanGhostEndpoints` always restarts both services, and its summary claims only what happened. A `reg.exe` that writes an error does not abort the step. A small compiled `reg.exe` stub goes first on PATH for that check. | It always said "audio services restarted", without checking. Worse, under `$ErrorActionPreference = 'Stop'`, Windows PowerShell 5.1 turns a redirected stderr line of a native command into a terminating error. So one key that `reg.exe` could not delete ended the step with both audio services stopped, and nothing said that the sound was gone. |
| `script\WrongProfile.Tests.ps1` | A run elevated as a different account refuses the per-user steps and names each one. | A standard user who typed an administrator's password got every per-user fix applied to the administrator's profile, with `[OK]`. |
| `script\BrowserFlags.Tests.ps1` | The two features are merged into a shortcut's existing `--disable-features`, however that switch is quoted. A second run sees them as done, and `-Revert` removes only those two. A shortcut with no room left for the switch is refused. Shortcuts are found in both Start Menus (subfolders too) and the taskbar pins, and each backup is traced to the path it came from. | A second `--disable-features` switch silently re-enabled every feature the user had disabled. Chromium honours only the last one. A shortcut's 1023-character limit cut the switch off while the run said `[OK]`. The list of backups was written in the ANSI code page, so a path outside it became question marks. |
| `script\RunningBrowsers.Tests.ps1` | `-Status`, and the status at the end of the default run, asks each running Chromium browser whether it was started with the flags, and warns by name when it was not - or when it has the flags and a separate audio process anyway. Helper processes get no line of their own. | A browser update recreated the Start Menu shortcut without the flags, and a browser started by another program never reads a shortcut at all. The browser then ran without the fix while every check said `[OK]`. |
| `script\OptInSteps.Tests.ps1` | `-DisableEnhancements` changes an endpoint only after a verified backup. The BleachBit rule is not edited while BleachBit is open, and the edit changes that one line only: the ini keeps its UTF-8 text, its BOM or lack of one, and its line endings. The store's label is re-applied unless it is exactly the one Windows uses. | `-DisableEnhancements` said "backup of endpoint key saved" without checking. BleachBit's settings were edited while it was open, and it can save its own copy over them later. The label check accepted any label containing `;LW`, even one that new subkeys do not inherit. An ini without a BOM was read as ANSI, which turned a non-ASCII path in it into mojibake, and it was written back with a BOM and CRLF. |
| `script\BackupFolder.Tests.ps1` | Backups go next to the script, or to `%LOCALAPPDATA%\AudioMixerFix\backups`, and the run says which. With no backup anywhere, the steps that need one are `[FAIL]` and change nothing. An error inside one step is a `[FAIL]` for that step, and the run still reaches the Summary. | A kit on a read-only drive ended the whole run at its first backup. |
| `script\Bluetooth.Tests.ps1` | The Bluetooth switches say `[OK]` and "a reboot is required" only after reading back a changed value. | They said it unconditionally, so a failed write sent the user to reboot for nothing. The same went for a write that reported success but landed somewhere else. |
| `script\Services.Tests.ps1` | A failure in the services step is reported with the real error. | Every failure was called "not found", including "Access is denied". |
| `script\CommandLine.Tests.ps1` | A misspelled switch, a stray argument or two action switches exit with 2 and say what was wrong. The list of valid switches is exactly the declared set. Started by a 32-bit PowerShell, the script hands the run to the 64-bit one, arguments and exit code included. | PowerShell rejected an unknown switch before the script ran, with exit 1. The README promised 2, and 1 means "a fix failed". A 32-bit PowerShell - what a 32-bit file manager starts - sees no audio endpoints at all, so `-Status` showed "0 active" outputs and the endpoint steps did nothing, silently. |
| `script\Consistency.Tests.ps1` | Every fact the kit states in more than one place is recomputed from the code and compared with each copy. That covers the store paths (four copies), the switches (the parameters, the help, the README, the one-action check and the dispatch), the browsers, the backup fallback folder and literal file paths. It also recounts the COM methods and their `[PreserveSig]`, and the numbers `VOLUME-BOOSTER.md` gives for the buffer, the latency, the duck level and the limits. Every script is checked for ASCII, CRLF and no BOM, every document for LF and no BOM, and this table must list every test file. | The notes said "three copies" when there were four, and the booster doc said "all 65" methods when there were 67. |
| `launcher\Launcher.Tests.ps1` | Every exit-code branch of `Fix-AudioMixer.cmd` is covered, with `fltmc` and PowerShell stubbed. The network-drive check is taken verbatim from the launcher. A launcher without the script next to it says so before it asks for elevation. The elevated relaunch is tried from awkward folder names, among them `&`, `^`, `@`, `!`, `%`, a Cyrillic name, and `%NAME%` pairs that Windows would expand. | From a folder with `&`, `^` or `@` in its name, the elevated window ran nothing, and the first window had already closed. A folder such as `a%OS%b` did the same; it is now refused with an explanation - but a folder with a caret next to a pair that expands to nothing, such as `a^b%X%c`, was refused as well. A negative exit code from `fltmc` or PowerShell read as success. Double-clicked inside a ZIP, the launcher runs alone from a temporary folder: that went through the administrator prompt and ended with PowerShell's own error. |
| `build\Build.Tests.ps1` | The booster compiles with the C# compiler inside .NET Framework 4.x, both as the usual AnyCPU build and as a 32-bit one, with no warning at level 4. `Build-Booster.cmd` uses the same three references. The `AppVolumeBooster.exe` that ships is that source, built: it may differ from a fresh build only in the time stamp and the module id the compiler gives every build. | It guards the "one .cs file, in-box compiler, nothing to install" promise. On 2026-09-30 the shipped exe was a week behind its source: every test compiles the source into a scratch folder, so all of them passed while the file people double-click still had the bugs. |
| `booster\Mute.Tests.ps1` | A muted app stays muted while it is boosted and afterwards, and the log says why the boost is silent. | The booster unmuted it. |
| `booster\PriorVolume.Tests.ps1` | When a boost ends, the slider goes back to where it was, even a very low 3%. That also holds when a second booster joins while the first holds the slider at 4%. | A 3% app came back at 100%, and a second booster that joined recorded 4% as the level to go back to. |
| `booster\UiRace.Tests.ps1` | This drives the real window. Its app list: a row carries the name and icon Windows shows for the app, the window's own periodic refresh leaves an unchanged list alone and happens only while Auto-refresh is on, and a check stays with its app through a refresh, also after the app restarted. Then its Start/Stop button against a boost that stops itself because its target closed, the status line after a boosted app is killed, and a checked list entry whose process has gone. Last the tray: minimised, the window goes there and comes back; what a Start in the tray takes when nothing is checked (on made-up rows); Start, Stop and the slider in the tray panel, and the remembered last app. A second start of the exe brings back the window that is in the tray instead of opening another, checked in the window test and between two processes (under a test name). Then: checking an app sets the slider to the level it was last boosted at, and a boost saves it; an app set to boost automatically starts the boost when it plays, not again after a Stop by hand until it was quiet; Ctrl+Alt+B / Up / Down start, stop and change the boost; the X button hides to the tray with that option on; Start with Windows writes and removes its shortcut. | A click could start a new boost, and a stale "stopped" notice orphaned it, running with no way to stop it. After a self-stop the list refresh overwrote the reason with "No apps with audio sessions found". A stale entry started a boost on a dead pid, or on whatever process had taken that pid since. |
| `booster\Devices.Tests.ps1` | The booster finds as many active outputs as the registry lists, the player appears in the app list, and it is ducked and restored. A clean run counts no glitch. The player is always on the default output, so on a machine with one output this cannot show that a second one is covered (see the last section). | Only the default output was seen, so an app playing on another output was not ducked. A clean run also counted a stray glitch when it stopped. |
| `booster\Backlog.Tests.ps1` | After the output thread stalls, the stale audio is dropped and the latency comes back down. A "before" build proves this check can fail. | One 300 ms stall left 260 ms of permanent extra latency, while the status line still said 60. |
| `booster\DeadOutput.Tests.ps1` | When the output stops responding, the boost stops itself within seconds and says why. A "before" build proves this check can fail. | It sat there "boosting" until its timer ran out, with the target ducked and silent. |
| `booster\ReadOnlyFolder.Tests.ps1` | The crash-recovery note lives in `%LOCALAPPDATA%\AppVolumeBooster`, one per user, whether the exe's folder is writable or read-only, and it is removed after a clean stop. | The note sat next to the exe: from a read-only folder the write failed silently, so a crash left nothing to repair the slider with, and two copies in different folders kept separate notes. |
| `booster\Platform32.Tests.ps1` | Process-loopback capture works in a 32-bit booster as well as a 64-bit one. | The offset of a pointer was hard-coded at 16, which is right only in a 64-bit process, so capture failed in a 32-bit one. |
| `booster\StopRace.Tests.ps1` | Eight simultaneous stops, 20 times over, give no exception and one "stopped" event, and a stop never returns before the slider is restored. | Overlapping shutdowns could throw on a thread-pool thread, which ends the process. |
| `booster\SelfHeal.Tests.ps1` | The start-up repair of sliders left behind by a crashed booster leaves alone a slider that a live booster holds. That also holds when the booster's exe was renamed, and when one of two boosted apps of the same name closes. A target that closes mid-boost leaves no line in the note once its slider is back. | It un-ducked a running boost, so the app played at full volume under the 25x copy. A renamed copy counted as dead and un-ducked its own targets every 5 s. One of two same-named apps closing orphaned the line they shared. A line was kept after every target exit, and later "healed" an app the user had set below 5% on purpose. |
| `booster\Exclusivity.Tests.ps1` | "Boost all audio" refuses while another booster plays, and while one runs, every other boost refuses. | An all-audio boost would capture another booster's already boosted output and boost it a second time. |
| `booster\BoosterCli.Tests.ps1` | A `--seconds` value the booster cannot honour is refused before the boost starts, a `--log` it cannot write is exit code 3, and `--log` followed by another flag creates no file. | Beyond about 24.8 days the wait overflowed after the target was ducked. Negative or NaN values waited forever. An unwritable log crashed the run halfway. `--log --boost 200` wrote a file named `--boost`. |
| `booster\ChildTree.Tests.ps1` | A process the target spawns during the boost is ducked at once, by the session-created callback alone. When the checked entry is an app's audio helper process (a browser without the kit's flags, Discord), the boost is tied to the app itself: it survives the helper being recycled, ducks the replacement at once, and ends only when the app closes. | The callback never fired, because the registration was never armed. A new child waited up to 10 s for the next tree rebuild while its audio was already relayed at full volume. The boost ended with "target closed" when a browser recycled its audio process, and the replacement was never boosted. |
| `booster\CaptureRefused.Tests.ps1` | When one of several chosen apps cannot be captured, it is left alone: not ducked, and named in the warning. The others are boosted. | The app stayed in the target list and was ducked to 4% with no boosted copy - quieter than before the boost. |
| `booster\CaptureDeath.Tests.ps1` | When the captured app's audio stream stops responding, the boost stops itself within seconds and says why. | Only the output side was watched: a dead capture stream left the app ducked and silent while the window said "Boosting". |
| `booster\RestoreFail.Tests.ps1` | A slider restore that fails at Stop is reported, and the crash note is kept for the next run's self-heal, which then repairs it. | A failed restore counted as success: the note was deleted and the window said "restored" while the app sat at 4%. |
| `booster\Dropouts.Tests.ps1` | This is measured on what is actually heard: the probe records the booster's own output and finds every silent gap. The 50 ms buffer really exists. One stall counts as one glitch, a stall of the output thread counts at all, and a queue that empties just in time counts nothing. | The buffer held 0-20 ms. One 300 ms capture stall counted as 14-15 glitches and pushed latency to the 160 ms cap, and a stall of the output thread counted none. |

| `booster\Gain.Tests.ps1` | How loud the boosted copy is, measured on the booster's own output: at 100% it has the level of the app's sound at a full slider, at 200% twice that. A build with the gain halved proves the check can fail. | No test looked at the level at all. Every other check reads sliders, gaps, counters and logs, so a wrong gain would have left them all green. |
| `booster\ProcessInfo.Tests.ps1` | The booster's own look-ups of a process's name and parent give what the old ones gave: the same name as `Get-Process` and the same parent as WMI for every running process, and no name for a process that has exited. They are also fast. | One name cost a snapshot of every process and thread on the machine (2-3 ms, several per audio session on every pass of the watcher). The parents came from a WMI query: half a second for the first one, most of the wait after pressing Start. |
| `booster\HookDenied.Tests.ps1` | When the booster is refused the right to wait for its target's exit, the boost runs anyway, and still ends when the target closes. The refusal comes from a test-only build. | "Access is denied" - the answer an app running as administrator gives a booster that is not - was taken for the app having closed: the boost ended at once with "target closed". |
| `booster\NoProcessList.Tests.ps1` | A per-app boost refuses to start when the list of running processes cannot be read, before any slider is touched. | It carried on with an empty list, on which the check against boosting a process tree the booster itself sits in - the feedback loop - always passes. |
| `booster\RecreatedSession.Tests.ps1` | An app that closes its audio session and opens it again during a boost (a browser does, 5 s after a pause) has the new session held at 4% as well, and back at its own level when the boost ends. | None - the booster passed this before the test was written. It pins the behaviour. |

`Dropouts` takes `-Only steady,hiccup50,hiccup60,capstall,renstall` to run some of its
scenarios.

## Testing another version

Each file under test can be swapped for another copy without touching the tests, through
these environment variables:

| Variable | Replaces |
|---|---|
| `AMF_PS1` | `Fix-AudioMixer.ps1` |
| `AMF_CMD` | `Fix-AudioMixer.cmd` |
| `AMF_BAT` | `Check-Store.bat` |
| `AMF_CS` | `VolumeBooster\AppVolumeBooster.cs` |
| `AMF_README` | `README.md` |
| `AMF_VBDOC` | `VOLUME-BOOSTER.md` |

A test that uses one prints `(testing AMF_PS1 = ...)`, so a run against another copy
cannot be mistaken for a run against the kit. Point one at the version before a fix to see
the new check fail, which is the proof that it can. You can also point one at a
deliberately broken copy to see which checks notice. The byte check and the list of test
files always read the kit folder itself, and the check of the shipped exe is skipped while
`AMF_CS` points at another source.

## Writing a new test

- One file per area, named `<Area>.Tests.ps1`, in the folder of its suite. Add a row to the
  table above; Consistency fails until you do.
- Start with the two lines every test file has, the trap first. A trap covers only the file
  it is written in.

  ```
  trap { Write-Host ('    FAIL  harness error: ' + $_); $global:AnyFail = $true; continue }
  . "$PSScriptRoot\..\lib\TestLib.ps1"
  ```

  End with `Complete-Test`, which cleans up and sets the exit code.
- Write only to `New-TestScratch` folders and under `$TestKey`. Never touch a real key, a
  real shortcut, a real service or the kit's own files.
- Test what ships. Take functions with `Get-ShippedFunction` instead of copying them, and
  read sets and numbers out of the code instead of writing them into the test.
- Stub at the edge. A PowerShell function named `reg.exe` shadows the real one inside the
  test. For behaviour only a real process has, such as writing to native stderr, compile a
  stub exe, as CleanGhostEndpoints does.
- Check code, not comments. Strip comments before matching script text: the script talks
  about what it does, so a comment that names a cmdlet satisfies a `-match` even with the
  code deleted. Pair every "does not contain" check with proof that the text being searched
  is really there.
- Run a new check once against the broken version, with an `AMF_*` copy or a "before"
  build like Backlog's. A check that has never failed has not shown that it can.
- Don't name a helper after an existing alias (`cli` is `Clear-Item`): aliases win over
  functions, so the call would silently run the alias.
- Keep `.ps1` and `.cs` files ASCII with CRLF line endings, and Markdown with LF.
  Consistency checks both.

## What they do not cover

- The real effect on Windows is not tested. That covers the store being rebuilt, the label
  taking effect, a browser really saving its volume, and a reboot applying the Bluetooth
  value. Those were verified by hand on the one machine the kit was written on, Windows 11
  build 26100.
- `Check-Store.bat` is checked only for its store list and its bytes. It is never run.
- `Build-Booster.cmd` is not run either. The build test compiles the same source with the
  same compiler and the same references, to the same output name. Its check of the shipped
  exe holds for the compiler it was built with: another version of the in-box compiler may
  produce different bytes from the same source, and the check then fails until the exe is
  rebuilt there.
- Ducking on a second output device is not tested: the test player is always on the default
  output, and the machine these were written on has one.
- An app running as administrator, boosted by a booster that is not, is only simulated
  (HookDenied): the refusal comes from a test-only build, not from a real elevated app.
- The audio tests measure real-time behaviour on this machine's audio stack. Heavy load
  during the run (a game, a big build) can cause a real dropout that fails Dropouts or
  Backlog. Rerun that file on an idle machine before treating the failure as a regression.
