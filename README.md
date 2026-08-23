# Windows 11: Fix for Per-App Volume (Volume Mixer) Memory

Store this folder, and also keep a backup on external or cloud storage (since a system reset will delete everything on C:\). 
After reinstalling Windows, copy it back and then run just one file.

## Quick Guide

1. Double-click the file **`Fix-AudioMixer.cmd`** (and approve the administrator prompt).
2. Set the volume for each application in the Volume Mixer. Then, **close and reopen that application one time** (Windows saves an app's volume settings only when the app closes).
3. First, test with an app that is not a web browser (like a media player or a game). When that works, you are finished.

If an issue still occurs, open PowerShell and run `.\Fix-AudioMixer.ps1 -Status` for a complete diagnosis. 
This will check the store variant, Bluetooth settings, audio effects (APOs), and devices. 
Afterward, check the table of symptoms below.

---

## Symptom, cause, and solution

| Symptom | Probable cause | Action to take |
|---|---|---|
| All applications reset to 100% volume after a restart or reopening | The store key was deleted (by a cleaner) or has an incorrect integrity label | Run the script with its default settings; for the worst case, use `-RebuildStore` |
| Only your **browser** does not remember its volume setting | Chromium's audio sandbox (affects Chrome, Edge, Thorium, Brave, Vivaldi, Opera; Firefox is not affected) | The default script adds launch flags to browser shortcuts |
| Volume behaves erratically with **Bluetooth** (slider jumps back, large volume changes, buttons conflict with the slider) | Bluetooth absolute volume is active | To confirm, check if the main volume slider misbehaves on the Bluetooth device. Then, run `-DisableBtAbsoluteVolume` and restart your computer. |
| Volumes reset randomly when an audio device restarts; an OEM effect app is installed (such as Nahimic, Dolby, DTS, Waves, or Realtek Console) | Audio enhancements or third-party APOs are rebuilding the endpoint | Use `-DisableEnhancements` and manually stop the OEM service (see instructions below) |
| Volumes "reset" after changing the output device | This is by design: volumes are stored for each specific combination of device and application | Set volumes once for every device you use |
| Volumes disappear after reinstalling an audio driver | This is also expected; a new driver creates new endpoint IDs, making old entries unusable | Reset volumes again after any driver reinstallation |
| Applications forget their **assigned output device** after a full shutdown (for virtual cables or multi-device setups) | Device enumeration during cold boot, combined with Fast Startup | Disable Fast Startup (see manual instructions below) |

---

## What actually caused the problem on my computer (three reasons)

**1. A cleaning program was deleting the storage location.** Windows stores each app's saved volume settings in a registry location. 
BleachBit's winapp2 rule called **"Windows Volume Mixer"** removes this data every time it cleans. 
The script disables that rule (`winapp2_windows.windows_volume_mixer = False`), and you must never re-enable it.

**2. The store needs a specific "low integrity" label.** This storage location resides in a *low-integrity* registry area. 
This allows sandboxed, low-integrity audio writers (like Chromium's audio process and UWP apps) to write to it. 
If a key is recreated manually (using `reg add`, `.reg` import, or `New-Item`), it will have **Medium** integrity. 
In this case, medium-integrity apps (such as Firefox) save their settings correctly, but Chromium or UWP apps silently fail to do so. 
This specific distinction is documented in winutil issue #3294. The correct label is `S:(ML;OICI;NW;;;LW)`. 
The script applies this label using the Windows security API, or you can use `-RebuildStore` to have the audio service recreate the key with the correct settings.

**3. Chromium browsers cannot save volume settings by default.** Their sandboxed audio process is prevented from saving this information. 
The only reliable solution is to launch them with these flags:

```
--disable-features=AudioServiceSandbox,AudioServiceOutOfProcess
```

This involves a trade-off: audio-process sandboxing becomes slightly weaker. 
An alternative is to use an in-browser volume extension and skip using these flags.

## Where the store lives (it varies by build)

| Location | Status |
|---|---|
| `HKCU\Software\Microsoft\Internet Explorer\LowRegistry\Audio\PolicyConfig\PropertyStore` | This is the standard location, used in official versions 23H2, 24H2, and 25H2. |
| `HKCU\Software\Microsoft\Multimedia\Audio\PolicyConfig\PropertyStore` | This version appeared in some 24H2 pre-release builds. It might be an empty placeholder next to the standard one. |
| `%LocalAppData%\Microsoft\Windows\Audio\AppVolume` folder | **This is false.** Many AI-generated "reset audio" articles mention it, but no reliable source confirms it stores volume data. The script ignores this folder, so do not delete folders based on that advice. |

The script checks both registry variations. It then labels the one that exists and uses `-Status` to show you which one contains entries. 
Volumes are stored per device because entries are identified by a combination of the output device's endpoint GUID and the application's path. 
This also explains why reinstalling a driver, which creates new GUIDs, resets volume settings.

**An observation from a specific computer (build 26100.9168):** The IE-LowRegistry path was not present at first. 
The audio service never created it on its own, nor did it use the default Multimedia placeholder when the standard path was missing. 
I had to manually create the standard key and apply the Low label to make persistence work, which I confirmed from beginning to end. 
Therefore, do not rely on advice like "Windows will just rebuild it." The script explicitly creates and labels the key. 
If the service fails to recreate the key, `-RebuildStore` uses this method as a backup.

The standard path is found within the Internet Explorer section. Community reports connect removing the IE-mode component (a Feature on Demand) with a broken store. 
The `-Status` command verifies if this component is installed.

---

## The script

You can run this script multiple times without issues. It saves everything it changes to a `backups\` folder located next to the script (wherever you place the kit). 
It works correctly regardless of your system's language settings (for example, on Cyrillic Windows) because it uses SIDs and service names and never interprets localized output.

| Command | What it does |
|---|---|
| `Fix-AudioMixer.cmd` (double-click) | This file automatically elevates its permissions and applies the main fixes. |
| `Check-Store.bat` (double-click) | This provides a quick visual check. It lists applications with saved volumes in both store variants and gives a clear result. No administrator rights are required. |
| `.\Fix-AudioMixer.ps1` | This performs the main fixes: it disables the cleaner rule, manages the store and its label, adjusts services, and sets browser flags. |
| `.\Fix-AudioMixer.ps1 -CheckOnly` | This reports what any mode *would* do without making any changes. |
| `.\Fix-AudioMixer.ps1 -Status` | This offers a complete diagnosis, including store variations, entries, labels, Bluetooth status, APOs for each output, device and driver information, Fast Startup status, and the IE-mode component. |
| `.\Fix-AudioMixer.ps1 -Revert` | This command removes any browser flags that the script added. |
| `.\Fix-AudioMixer.ps1 -RebuildStore` | **This is an optional action.** It backs up and deletes the store, then restarts the audio service so Windows can rebuild it naturally. If Windows does not rebuild it (as was the case on my machine's build), the script explicitly recreates and labels it. **Warning: Any running applications will have their volume reset to 100%, and all saved entries will be cleared (starting fresh).** |
| `.\Fix-AudioMixer.ps1 -CleanGhostEndpoints` | **This is an optional action.** It removes `NOTPRESENT` "ghost" render and capture endpoints left by old drivers, HDMI/NVIDIA outputs, and virtual devices. It takes ownership of these keys (which are owned by TrustedInstaller), first backs up `MMDevices\Audio`, and then restarts audio. This is purely for appearance; entries for actual hardware will reappear when reconnected. |
| `.\Fix-AudioMixer.ps1 -DisableEnhancements` | **This is an optional action.** It turns off "Audio enhancements" for every active output. This sets the documented `Disable_SysFx` value to 1, and the endpoint key is backed up beforehand. A reboot is needed afterwards. |
| `.\Fix-AudioMixer.ps1 -DisableBtAbsoluteVolume` / `-EnableBtAbsoluteVolume` | **This is an optional action.** Use this to change the Bluetooth absolute volume setting (`HKLM\SYSTEM\CurrentControlSet\Control\Bluetooth\Audio\AVRCP\CT\DisableAbsoluteVolume`). **A reboot is required.** This change affects all Bluetooth audio devices globally. |

The `-CheckOnly` command can be combined with the optional switches. For example, you can use `.\Fix-AudioMixer.ps1 -DisableEnhancements -CheckOnly`.

---

## Manual Tasks

**Turn Off Fast Startup** (Apps sometimes lose their assigned *device* after a complete shutdown, especially with virtual audio cables or multiple audio setups):
Go to Control Panel → Hardware and Sound → Power Options → Select "Choose what the power buttons do" → Then "Change settings that are currently unavailable" → Uncheck the box for **Turn on fast startup** → Click Save.

**Prevent OEM Effect Services from Re-enabling Enhancements** (Only do this if you do not use these effects):
Open `services.msc` → Locate *NahimicService*, *DTS APO*, *Dolby*, *Waves MaxxAudio*, or *SmartByte* → Stop the service and change its Startup type to **Disabled**; alternatively, uninstall the application from Settings → Apps. 
(If your computer uses **Conexant AudioSmart** via the Realtek driver for its audio effects; leave it active unless problems arise. You can use `-DisableEnhancements` to bypass it cleanly.)

**Audio Enhancements via the User Interface** (as an alternative to using a switch):
Go to Settings → System → Sound → Click *All sound devices* → For each output device, set **Audio enhancements: Off**. 
For the older method: Press `Win+R` → Type `mmsys.cpl` → Select the device → Click Properties → Go to the Enhancements tab → Check "Disable all enhancements." 
(Note: this tab might not appear with the Realtek UAD driver; use the Settings app instead.)

**Clean Up Old or Ghost Output Devices** (This is cosmetic and reduces clutter in your device list).
The simplest way is to run **`-CleanGhostEndpoints`**. This tool backs up `MMDevices\Audio`, takes ownership of the `NOTPRESENT` registry keys, and then deletes them. 
These ghost devices are *only found in the registry*; they usually do not show up as removable Plug and Play devices. 
Because of this, `pnputil /enum-devices /disconnected` often shows nothing, and Device Manager's "show hidden devices" will not reveal them. The script removes them by clearing their registry keys. 
To also stop Windows from *using* a device you physically own (like the audio output from an HDMI monitor), disable it in the UI: 
Press `Win+R` → Type `mmsys.cpl` → Go to the Playback tab → Right-click → Select "Show Disabled Devices" and "Show Disconnected Devices" → **Disable** any devices you never use → Set your preferred device as the **Default Device** and **Default Communication Device**.

**Reinstall or Roll Back an Audio Driver** (If the audio output itself is faulty, such as crackling sounds, missing jacks, or phantom devices that will not disappear):
- *For Realtek:* Open Device Manager → Expand "Sound, video and game controllers" → Right-click the Realtek device → Select "Uninstall device" → Tick the box for **"Attempt to remove the driver"** → Restart your computer (Windows will reinstall a clean driver; or you can install an OEM package). 
Alternatively, go to Properties → Driver → Select **Roll Back Driver** if a recent update caused the issue.
- *For NVIDIA HDMI/Virtual Audio:* These are part of the GPU driver. Clean them using **DDU in Safe Mode**, then reinstall the NVIDIA driver with a *Custom install* and uncheck any audio components you do not need.
- In either case, be aware that any saved per-application volumes for that device will reset (a new driver means new endpoint IDs). Adjust your volumes once this is done.

**Bluetooth Absolute Volume (Confirm Before Changing):**
With your Bluetooth device active, gently move the master volume slider. If it springs back or moves in large steps, absolute volume is likely active. In this case, use `-DisableBtAbsoluteVolume` and reboot. 
If the master volume works correctly but only one app has issues, the problem is with that specific app, not Bluetooth. 
After disabling, set the headset's own hardware volume close to maximum once; its buttons will then no longer control the Windows slider (you will have two separate volume controls).

## How to Check if It's Working

- **`Check-Store.bat`** provides a quick visual check. Double-click it. If an app appears with a green "verdict," Windows is storing its volume settings. (An app only shows up after you adjust its volume and then close it.)
- For a more detailed look, run `.\Fix-AudioMixer.ps1 -Status`. This shows the same list of apps, along with the integrity label `S:(ML;OICI;NW;;;LW)`, Bluetooth settings, Audio Processing Objects (APOs), and the status of various devices.
- To test it, set a media player's volume to around 30%. Close the player completely, then open it again. The volume should still be at 30%.
- Volume settings save when you close an application, and this applies to each output device separately. Browsers require specific flags to save their settings, while Discord handles some of its volume internally, making it an unreliable test app.

## What Won't Help (for this issue)

- The **Reset** button in Settings → Sound → Volume mixer will only restore default values; it will not fix the volume saving problem.
- Tools like the audio **troubleshooter**, `sfc /scannow`, or DISM are generally useful for fixing audio issues, but they do not rebuild or relabel the volume store, so they are unlikely to help here.
- As of August 2026, Microsoft has **no official knowledge base article** addressing this issue; it is a problem identified by the user community.

## After Reinstalling or Resetting

A standard, new Windows installation usually retains volume memory. However, systems using debloated images or IoT/LTSC installs might completely lack the store key from the start (as was the case with my machine). Therefore:

1. Keep a copy of this `AudioMixerFix` folder on external or cloud storage, as a system reset will wipe `C:\`.
2. After reinstalling Windows, copy the folder back. Install your applications and browser. Then, run `Fix-AudioMixer.cmd` once. 
This script will detect and create any missing components, such as the store, label, cleaner guard, or browser flags. You can confirm its success by running `-Status`.
3. Set your application volumes once. Each app will save its setting when you close it.

## How to Revert Changes

- To revert browser flags, use `-Revert`. For Bluetooth, use `-EnableBtAbsoluteVolume` and then restart your computer.
- For enhancements, either reset the endpoint's `{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5` value to 0 (or delete it), or use the toggle in Settings. Timestamped `.reg` backups of all affected endpoints are in the `backups\` folder.
- All other changes have timestamped copies in `backups\`, including `bleachbit.ini.*`, `PropertyStore.*.reg`, `*.lnk.*.bak`, and `endpoint.*.reg`.

## Primary Information Sources

Information came from winutil issues #3294/#2776 (store path and Chromium/integrity split), NirSoft AppAudioConfig (store location), and Microsoft Learn resources: PKEY_AudioEndpoint_Disable_SysFx, PKEY_FX_* APO slots, and Bluetooth AVRCP absolute-volume guidelines. 
Other sources include elevenforum (for BT absolute volume, enhancements, endpoint pruning), Level1Techs (Multimedia store variant, DefaultEndpoint), dechamps/APO README (FxProperties, endpoint GUIDs changing after driver reinstallation), and Microsoft Q&A threads discussing 24H2 mixer resets.

## Maintenance

This utility **will not** be actively maintained, unless it becomes popular for some reason and i'd get requests.

## Optional Add-on: Per-App Volume Booster

`VolumeBooster\AppVolumeBooster.exe` (optional, added 2026-08-23) boosts one chosen application past 100% - up to 200% - with no drivers, no admin rights, and nothing installed; if you never run it, it changes nothing. 
Full documentation, including how it interacts with the volume memory this kit protects, is in **VOLUME-BOOSTER.md**.
