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

This runs the fast suites (script, launcher and build) in about two and a half minutes.

```
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Run-Tests.ps1 -Audio
```

This adds the booster's audio tests, about five more minutes. They need a working output
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
when everything passed and 1 otherwise.

Run one runner at a time. Every test file clears the shared test registry key when it
finishes, so two runs side by side would pull it out from under each other.

## What they touch

**Files.** Tests write only to fresh folders under `%TEMP%\AudioMixerFix-tests`. Each test
file removes its own folders when it ends. Nothing in the kit folder is ever written: even
the booster is compiled into a scratch folder, and the kit's own `AppVolumeBooster.exe` is
never replaced.

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
lines, and every one of them stops at the argument check, before the first step.
BackupFolder runs it from scratch copies of the kit, with all five step functions replaced
by stubs and `%LOCALAPPDATA%` pointed at a scratch folder.

**Everything else** in the script is tested one function at a time. The function is taken
out of `Fix-AudioMixer.ps1` by PowerShell's own parser (`Get-ShippedFunction`), so what runs
is exactly what ships. It is never a copy that could drift. Shortcuts are throwaway `.lnk`
files in a scratch folder. BackupFolder and ReadOnlyFolder make a scratch folder read-only
by denying the right to add files and folders to it (`icacls /deny ...(WD,AD)`), and remove
that deny again.

**Audio, only with `-Audio`.** The rules at the top of `lib\AudioRig.ps1` apply, and each
one comes from something that went wrong while these tests were being written:

- Only a private player is boosted: a hidden `powershell.exe` that loops a tone at about
  -58 dBFS. Most tests boost it at 100%, which plays it at its own level. The window test
  uses the window's default of 150%. Either way, what you hear stays a faint tone. No real
  app is ever boosted, and neither is "Boost all audio".
- The player's volume and mute in the Volume Mixer are read first and put back before it
  exits. Windows saves an app's mixer settings when the app closes, and this app is
  `powershell.exe`, so a player left at 4% would make every PowerShell window start at 4%.
- The booster never runs inside the player's process tree. Player and booster are siblings,
  because a booster inside its target's tree captured its own output and fed it back,
  loudly.
- Exclusivity never starts an all-audio boost. It simulates one by holding the
  `AppVolumeBooster.AllAudio` mutex for a few seconds, so a real "Boost all audio" started
  during those seconds would refuse.
- ReadOnlyFolder checks the booster's real fallback folder, `%LOCALAPPDATA%\AppVolumeBooster`.
  The booster removes its note there after a clean stop, and the test also removes the
  folder if it created it and the folder is empty.

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
                        outputs, records the booster's own output to find gaps
    RingTest.cs, StopTest.cs, UiTest.cs, ExclTest.cs
                        entry points that drive one of the booster's own classes directly
  script\               Fix-AudioMixer.ps1, Check-Store.bat and the documents
  launcher\             Fix-AudioMixer.cmd
  build\                the booster builds on a bare Windows
  booster\              the booster, with real audio (-Audio only)
```

## The tests

Each file's opening comment tells the whole story. This table is the short version.

| File | What it checks | The bug it was written for |
|---|---|---|
| `script\RebuildStore.Tests.ps1` | `-RebuildStore` refuses without a verified backup. It reports only what really happened, and it always starts the audio services again. A backup in a folder whose name holds `[v2]` is still found. | It could print `[FAIL] Could not restart` and then `[OK] ... audio service restarted`, and it said "deleted" without checking the delete. A half-failed stop left the Windows Audio service down. In a folder named like `[v2]`, valid backups looked failed, because `-Path` reads `[` and `]` as wildcards. |
| `script\CleanGhostEndpoints.Tests.ps1` | `-CleanGhostEndpoints` always restarts both services, and its summary claims only what happened. A `reg.exe` that writes an error does not abort the step. A small compiled `reg.exe` stub goes first on PATH for that check. | It always said "audio services restarted", without checking. Worse, under `$ErrorActionPreference = 'Stop'`, Windows PowerShell 5.1 turns a redirected stderr line of a native command into a terminating error. So one key that `reg.exe` could not delete ended the step with both audio services stopped, and nothing said that the sound was gone. |
| `script\WrongProfile.Tests.ps1` | A run elevated as a different account refuses the per-user steps and names each one. | A standard user who typed an administrator's password got every per-user fix applied to the administrator's profile, with `[OK]`. |
| `script\BrowserFlags.Tests.ps1` | The two features are merged into a shortcut's existing `--disable-features`. A second run sees them as done, and `-Revert` removes only those two. | A second `--disable-features` switch silently re-enabled every feature the user had disabled. Chromium honours only the last one. |
| `script\OptInSteps.Tests.ps1` | `-DisableEnhancements` changes an endpoint only after a verified backup. The BleachBit rule is not edited while BleachBit is open. The store's label is re-applied unless it is exactly the one Windows uses. | `-DisableEnhancements` said "backup of endpoint key saved" without checking. BleachBit's settings were edited while it was open, and it can save its own copy over them later. The label check accepted any label containing `;LW`, even one that new subkeys do not inherit. |
| `script\BackupFolder.Tests.ps1` | Backups go next to the script, or to `%LOCALAPPDATA%\AudioMixerFix\backups`, and the run says which. With no backup anywhere, the steps that need one are `[FAIL]` and change nothing. An error inside one step is a `[FAIL]` for that step, and the run still reaches the Summary. | A kit on a read-only drive ended the whole run at its first backup. |
| `script\Bluetooth.Tests.ps1` | The Bluetooth switches say `[OK]` and "a reboot is required" only after reading back a changed value. | They said it unconditionally, so a failed write sent the user to reboot for nothing. The same went for a write that reported success but landed somewhere else. |
| `script\Services.Tests.ps1` | A failure in the services step is reported with the real error. | Every failure was called "not found", including "Access is denied". |
| `script\CommandLine.Tests.ps1` | A misspelled switch, a stray argument or two action switches exit with 2 and say what was wrong. The list of valid switches is exactly the declared set. | PowerShell rejected an unknown switch before the script ran, with exit 1. The README promised 2, and 1 means "a fix failed". |
| `script\Consistency.Tests.ps1` | Every fact the kit states in more than one place is recomputed from the code and compared with each copy. That covers the store paths (four copies), the switches (the parameters, the help, the README, the one-action check and the dispatch), the browsers, the backup fallback folder and literal file paths. It also recounts the COM methods and their `[PreserveSig]`, and the numbers `VOLUME-BOOSTER.md` gives for the buffer, the latency, the duck level and the limits. Every script is checked for ASCII, CRLF and no BOM, every document for LF and no BOM, and this table must list every test file. | The notes said "three copies" when there were four, and the booster doc said "all 65" methods when there were 67. |
| `launcher\Launcher.Tests.ps1` | Every exit-code branch of `Fix-AudioMixer.cmd` is covered, with `fltmc` and PowerShell stubbed. The network-drive check is taken verbatim from the launcher. The elevated relaunch is tried from awkward folder names, among them `&`, `^`, `@`, `!`, `%`, a Cyrillic name, and `%NAME%` pairs that Windows would expand. | From a folder with `&`, `^` or `@` in its name, the elevated window ran nothing, and the first window had already closed. A folder such as `a%OS%b` did the same; it is now refused with an explanation. A negative exit code from `fltmc` or PowerShell read as success. |
| `build\Build.Tests.ps1` | The booster compiles with the C# compiler inside .NET Framework 4.x, both as the usual AnyCPU build and as a 32-bit one, with no warning at level 4. `Build-Booster.cmd` uses the same three references. | It guards the "one .cs file, in-box compiler, nothing to install" promise. |
| `booster\Mute.Tests.ps1` | A muted app stays muted while it is boosted and afterwards, and the log says why the boost is silent. | The booster unmuted it. |
| `booster\PriorVolume.Tests.ps1` | When a boost ends, the slider goes back to where it was, even a very low 3%. That also holds when a second booster joins while the first holds the slider at 4%. | A 3% app came back at 100%, and a second booster that joined recorded 4% as the level to go back to. |
| `booster\UiRace.Tests.ps1` | This drives the real window: its Start/Stop button against a boost that stops itself because its target closed. | A click could start a new boost, and a stale "stopped" notice orphaned it, running with no way to stop it. |
| `booster\Devices.Tests.ps1` | Every active output is found, the player appears in the app list, and it is ducked on every output and restored. A clean run counts no glitch. | Only the default output was seen, so an app playing on another output was not ducked. A clean run also counted a stray glitch when it stopped. |
| `booster\Backlog.Tests.ps1` | After the output thread stalls, the stale audio is dropped and the latency comes back down. A "before" build proves this check can fail. | One 300 ms stall left 260 ms of permanent extra latency, while the status line still said 60. |
| `booster\DeadOutput.Tests.ps1` | When the output stops responding, the boost stops itself within seconds and says why. A "before" build proves this check can fail. | It sat there "boosting" until its timer ran out, with the target ducked and silent. |
| `booster\ReadOnlyFolder.Tests.ps1` | From a read-only folder, the crash-recovery note goes to `%LOCALAPPDATA%\AppVolumeBooster`, and it is removed after a clean stop. | The write failed silently, so a crash left nothing to repair the slider with. |
| `booster\Platform32.Tests.ps1` | Process-loopback capture works in a 32-bit booster as well as a 64-bit one. | The offset of a pointer was hard-coded at 16, which is right only in a 64-bit process, so capture failed in a 32-bit one. |
| `booster\StopRace.Tests.ps1` | Eight simultaneous stops, 20 times over, give no exception and one "stopped" event, and a stop never returns before the slider is restored. | Overlapping shutdowns could throw on a thread-pool thread, which ends the process. |
| `booster\SelfHeal.Tests.ps1` | The start-up repair of sliders left behind by a crashed booster leaves alone a slider that a live booster holds. | It un-ducked a running boost, so the app played at full volume under the 25x copy. |
| `booster\Exclusivity.Tests.ps1` | "Boost all audio" refuses while another booster plays, and while one runs, every other boost refuses. | An all-audio boost would capture another booster's already boosted output and boost it a second time. |
| `booster\BoosterCli.Tests.ps1` | A `--seconds` value the booster cannot honour is refused before the boost starts, and a `--log` it cannot write is exit code 3. | Beyond about 24.8 days the wait overflowed after the target was ducked. Negative or NaN values waited forever. An unwritable log crashed the run halfway. |
| `booster\Dropouts.Tests.ps1` | This is measured on what is actually heard: the probe records the booster's own output and finds every silent gap. The 50 ms buffer really exists. One stall counts as one glitch, a stall of the output thread counts at all, and a queue that empties just in time counts nothing. | The buffer held 0-20 ms. One 300 ms capture stall counted as 14-15 glitches and pushed latency to the 160 ms cap, and a stall of the output thread counted none. |

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
files always read the kit folder itself.

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
  same compiler and the same references.
- The audio tests measure real-time behaviour on this machine's audio stack. Heavy load
  during the run (a game, a big build) can cause a real dropout that fails Dropouts or
  Backlog. Rerun that file on an idle machine before treating the failure as a regression.
