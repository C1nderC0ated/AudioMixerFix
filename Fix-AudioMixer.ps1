#Requires -Version 5.1
# FILE FORMAT: ASCII, CRLF, no BOM - keep it that way. Windows PowerShell 5.1 reads a
# BOM-less file as ANSI, so one non-ASCII character (a typographic dash in a message,
# or anything inside the C# blocks below) is silently mangled before it ever runs.
<#
Fix-AudioMixer.ps1 (v2)
This script restores and safeguards the memory for Windows 11's per-application volume settings (known as "Volume Mixer"). It also helps identify common issues that prevent these settings from working correctly.

WHY VOLUMES RESET (full detail + sources in README.md):
1. Cleaning utilities, like the BleachBit winapp2 rule "Windows Volume Mixer", delete the storage key.
2. The system requires this storage key to have a LOW integrity label. If you recreate a key using `reg add` or a `.reg` import, it will be Medium integrity, preventing programs with low integrity from saving to it.
3. Chromium-based browsers use a sandbox for their audio process. They will not save volume settings unless you enable the `--disable-features=AudioServiceSandbox,AudioServiceOutOfProcess` flags.
4. With Bluetooth "absolute volume" enabled, changes to volume levels may not seem to save. This applies only to Bluetooth devices.
5. Audio effects or Audio Processing Objects (APOs) from original equipment manufacturers (OEMs) can rebuild the audio endpoint, which then causes per-application volume levels to be lost.
6. Volume settings are saved for each individual output device, identified by its endpoint GUID. When you switch devices or reinstall an audio driver, the system intentionally sets new or existing devices to 100% volume.

The storage location varies depending on the Windows build. This script checks both known locations:
canonical: HKCU\Software\Microsoft\Internet Explorer\LowRegistry\Audio\PolicyConfig\PropertyStore
variant: HKCU\Software\Microsoft\Multimedia\Audio\PolicyConfig\PropertyStore
(The claim circulating online about the "%LocalAppData%\Microsoft\Windows\Audio\AppVolume" folder could not be confirmed by any official source, so the script ignores it.)

You can run this script multiple times without issue. It creates backups of any changes it makes, storing them in a "backups" folder next to the script itself - or, when that folder cannot be written, in %LOCALAPPDATA%\AudioMixerFix\backups (the run says so).
The script is compatible with different regional settings, as it only uses SIDs, service names, and registry values, and never processes localized command output.

USAGE (run in an elevated PowerShell window, or double-click Fix-AudioMixer.cmd):
`.\Fix-AudioMixer.ps1` Applies core fixes (this is the default behavior).
`.\Fix-AudioMixer.ps1 -CheckOnly` Reports problems without making any changes.
`.\Fix-AudioMixer.ps1 -Status` Provides a complete diagnostic report on the store, Bluetooth, APOs, and endpoints.
`.\Fix-AudioMixer.ps1 -Revert` Removes the two audio flags from the browser shortcuts (whoever put them there).
`.\Fix-AudioMixer.ps1 -RebuildStore` OPT-IN: Deletes the volume store and restarts the audio service, letting Windows rebuild it from scratch. (WARNING: Any running applications will have their volumes reset to 100%).
`.\Fix-AudioMixer.ps1 -CleanGhostEndpoints` OPT-IN: Removes "ghost" audio endpoints that are marked as NOTPRESENT. (This process takes ownership of the keys and creates a backup first).
`.\Fix-AudioMixer.ps1 -DisableEnhancements` OPT-IN: Turns off Audio Enhancements (sets Disable_SysFx=1) for every active output device.
`.\Fix-AudioMixer.ps1 -DisableBtAbsoluteVolume` OPT-IN: Disables Bluetooth absolute volume. (Requires a system reboot; affects only Bluetooth devices).
`.\Fix-AudioMixer.ps1 -EnableBtAbsoluteVolume` OPT-IN: Restores Bluetooth absolute volume.
#>
[CmdletBinding()]
param(
    [switch]$CheckOnly,
    [switch]$Status,
    [switch]$Revert,
    [switch]$RebuildStore,
    [switch]$CleanGhostEndpoints,
    [switch]$DisableEnhancements,
    [switch]$DisableBtAbsoluteVolume,
    [switch]$EnableBtAbsoluteVolume,
    # Everything else on the command line lands here, so a misspelled switch is reported
    # by name and exits 2 like the other command-line errors. Without it PowerShell itself
    # rejected the parameter before the script ran - with exit 1, the code that means "a
    # fix failed", while the README promised 2.
    [Parameter(ValueFromRemainingArguments = $true)] [string[]]$Unrecognized
)

$ErrorActionPreference = 'Stop'

# ---- a 32-bit PowerShell -----------------------------------------------------
# A 32-bit program (some file managers and launchers) starts the 32-bit PowerShell, and
# that one is shown a different HKLM\SOFTWARE. Measured here: it sees no audio endpoints at
# all, so -Status reported "0 active" outputs and the endpoint steps found nothing to do -
# without a word. The run is handed to the 64-bit PowerShell, and its exit code passed on.
if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess -and $PSCommandPath) {
    $native = Join-Path $env:WINDIR 'sysnative\WindowsPowerShell\v1.0\powershell.exe'
    if (Test-Path -LiteralPath $native) {
        Write-Host '[..]   Started by a 32-bit PowerShell - continuing in the 64-bit one.' -ForegroundColor Gray
        $pass = @($PSBoundParameters.Keys | Where-Object { $_ -ne 'Unrecognized' -and $PSBoundParameters[$_] } | ForEach-Object { '-' + $_ })
        if ($Unrecognized) { $pass += $Unrecognized }
        & $native -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath @pass
        exit $LASTEXITCODE
    }
}

# ---- honest status tracking (never an unconditional [OK]) -------------------
$script:nOK = 0; $script:nWARN = 0; $script:nFAIL = 0
function Report {
    param([ValidateSet('OK','WARN','FAIL','INFO')][string]$Tag, [string]$Msg)
    switch ($Tag) {
        'OK'   { $script:nOK++;   $c = 'Green';  $p = '[OK]  ' }
        'WARN' { $script:nWARN++; $c = 'Yellow'; $p = '[WARN]' }
        'FAIL' { $script:nFAIL++; $c = 'Red';    $p = '[FAIL]' }
        default {                 $c = 'Gray';   $p = '[..]  ' }
    }
    Write-Host "$p $Msg" -ForegroundColor $c
}
function Section([string]$t) { Write-Host ''; Write-Host "== $t ==" -ForegroundColor Cyan }

# ---- constants -------------------------------------------------------------
# Both are known store variants. 'Sub' is the HKCU-relative path for the label API.
# NOTE: this list exists in FOUR places - this array, the help block at the top of this
# file, Check-Store.bat's own $paths/$names arrays, and the table in README.md. Change one,
# change all four (tests\script\Consistency.Tests.ps1 compares them).
$Stores = @(
    @{ Name = 'canonical (IE LowRegistry)';
       Reg  = 'HKCU\Software\Microsoft\Internet Explorer\LowRegistry\Audio\PolicyConfig\PropertyStore';
       PS   = 'Registry::HKEY_CURRENT_USER\Software\Microsoft\Internet Explorer\LowRegistry\Audio\PolicyConfig\PropertyStore';
       Sub  = 'Software\Microsoft\Internet Explorer\LowRegistry\Audio\PolicyConfig\PropertyStore' },
    @{ Name = 'variant (Multimedia\Audio)';
       Reg  = 'HKCU\Software\Microsoft\Multimedia\Audio\PolicyConfig\PropertyStore';
       PS   = 'Registry::HKEY_CURRENT_USER\Software\Microsoft\Multimedia\Audio\PolicyConfig\PropertyStore';
       Sub  = 'Software\Microsoft\Multimedia\Audio\PolicyConfig\PropertyStore' }
)
$BrowserFlags = '--disable-features=AudioServiceSandbox,AudioServiceOutOfProcess'
$BrowserFeatures = @('AudioServiceSandbox', 'AudioServiceOutOfProcess')
# README.md names these browsers in its symptom table (browser.exe is Yandex Browser) -
# change one, change both. The *_proxy.exe names are what a browser's installed web apps
# start through. Opera and Opera GX shortcuts point at launcher.exe, which Test-BrowserTarget
# recognises by its folder ('opera_gx.exe', listed here before, is not a name Opera uses).
$BrowserExes  = @('thorium.exe','chrome.exe','msedge.exe','brave.exe','vivaldi.exe','opera.exe','browser.exe','chrome_proxy.exe','msedge_proxy.exe','vivaldi_proxy.exe')
$BtCtKey      = 'HKLM:\SYSTEM\CurrentControlSet\Control\Bluetooth\Audio\AVRCP\CT'
$BtCtReg      = 'HKLM\SYSTEM\CurrentControlSet\Control\Bluetooth\Audio\AVRCP\CT'
$MMRender     = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render'
$SysFxValue   = '{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5'   # PKEY_AudioEndpoint_Disable_SysFx: 1 = enhancements OFF
$FxSlotFmtid  = 'd04e05a6-594b-4fb6-a80d-01af5eed7d1d'       # PKEY_FX_* effect-chain slots
$ScriptDir    = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$BackupDir    = Join-Path $ScriptDir 'backups'
$Elevated     = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

# ---- whose profile is this? ------------------------------------------------
# HKCU, %APPDATA% and the Desktop belong to whoever this process RUNS AS. On a
# standard-user account, UAC elevation with an administrator's password runs the
# script as that administrator - so every per-user fix used to land on the admin's
# profile while printing [OK], and the real user was left unfixed. The owner of this
# session's desktop (explorer.exe) is the real user. Compared by SID, never by name,
# so it holds on any display language. If the owner cannot be determined (no
# explorer, WMI unavailable) nothing is blocked - the old behaviour is kept.
function Get-DesktopOwnerSid {
    try {
        $sess = (Get-Process -Id $PID).SessionId
        $ex = @(Get-CimInstance Win32_Process -Filter ("Name='explorer.exe' AND SessionId={0}" -f $sess) -ErrorAction Stop) | Select-Object -First 1
        if (-not $ex) { return $null }
        $r = Invoke-CimMethod -InputObject $ex -MethodName GetOwnerSid -ErrorAction Stop
        if ($r.ReturnValue -ne 0) { return $null }
        return $r.Sid
    } catch { return $null }
}
$MyIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$script:WrongProfileCache = $null
# Lazy (one WMI query, ~0.1-0.3 s): only the steps that touch per-user state pay for it.
function Test-WrongProfile {
    if ($null -eq $script:WrongProfileCache) {
        $d = Get-DesktopOwnerSid
        $script:WrongProfileCache = [bool]($d -and $d -ne $MyIdentity.User.Value)
    }
    $script:WrongProfileCache
}

function Test-PerUserAllowed([string]$what) {
    if (-not (Test-WrongProfile)) { return $true }
    Report FAIL ("Skipped {0}: this window runs as {1}, but the desktop belongs to a different account, so the change would land on the wrong profile. Run Fix-AudioMixer.ps1 again from an ordinary PowerShell window as yourself - this step needs no admin rights." -f $what, $MyIdentity.Name)
    return $false
}

# Backups go to the 'backups' folder next to this script. When that cannot be written - the
# kit on a write-protected stick or a read-only share - they go to
# %LOCALAPPDATA%\AudioMixerFix\backups instead, and the run says so. This used to create the
# folder next to the script unconditionally, and under $ErrorActionPreference = 'Stop' the
# first backup that could not be written ended the whole run with a raw PowerShell error.
# A folder that exists is no proof it can be written to, so each candidate gets a test write.
$script:BackupDirReady = $false
# Every check on a backup file uses -LiteralPath: to -Path, [ and ] are wildcards, so from a
# kit folder named like "AudioMixerFix [v2]" a backup that existed tested as missing, and
# -RebuildStore / -CleanGhostEndpoints / -DisableEnhancements refused with "Backup FAILED".
function Ensure-BackupDir {
    if ($script:BackupDirReady) { return }
    $cands = @($BackupDir)
    if ($env:LOCALAPPDATA) { $cands += [IO.Path]::Combine($env:LOCALAPPDATA, 'AudioMixerFix\backups') }
    foreach ($c in $cands) {
        try {
            if (-not (Test-Path -LiteralPath $c -PathType Container)) { New-Item -ItemType Directory -Force -Path $c -ErrorAction Stop | Out-Null }
            $probe = Join-Path $c ('.write-test.' + $PID)
            [IO.File]::WriteAllText($probe, '')
            Remove-Item -LiteralPath $probe -Force -ErrorAction Stop
        } catch { continue }
        if ($c -ne $cands[0]) { Report INFO ("The script's folder cannot be written to, so backups go to {0}" -f $c) }
        $script:BackupDir = $c
        $script:BackupDirReady = $true
        return
    }
    throw ("no folder for backups could be written ({0}), so nothing was changed here" -f ($cands -join ', '))
}
function Stamp { (Get-Date).ToString('yyyyMMdd-HHmmss') }

function Backup-Shortcut([System.IO.FileInfo]$lnk) {
    # Desktop, Start Menu and the taskbar pin folder routinely hold shortcuts sharing one
    # leaf name (three Thorium.lnk on this machine), so a name-only backup lets them
    # overwrite each other inside a single second - and even when the stamps differ you
    # cannot tell which copy came from where. Tag each file with a short hash of its
    # source path and append the mapping, so restoring is not guesswork.
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $h = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::Unicode.GetBytes($lnk.FullName.ToLower()))).Replace('-','').Substring(0,8) }
    finally { $sha.Dispose() }
    $dst = Join-Path $BackupDir ($lnk.Name + '.' + $h + '.' + (Stamp) + '.bak')
    Copy-Item -LiteralPath $lnk.FullName -Destination $dst -Force
    # UTF-8: the default (ANSI) turned a path outside the system code page into question
    # marks, and the backup could no longer be traced to its shortcut
    Add-Content -LiteralPath (Join-Path $BackupDir 'shortcut-sources.txt') -Value ((Split-Path $dst -Leaf) + "`t" + $lnk.FullName) -Encoding UTF8
    $dst
}

# ---- registry integrity-label helper (the part a .reg file cannot do) -------
# Compiled ON DEMAND. Add-Type shells out to the C# compiler - measured at ~0.8 s for
# this type and ~0.6 s for RegDel below. Sitting at the top of the script that cost was
# paid by every invocation, -Status and -CheckOnly included; now each path pays only for
# the type it actually uses.
function Initialize-RegLabel {
    if ('RegLabel' -as [type]) { return }
Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class RegLabel {
  [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
  static extern int RegOpenKeyEx(IntPtr h, string sub, int opt, int sam, out IntPtr res);
  [DllImport("advapi32.dll", SetLastError=true)] static extern int RegCloseKey(IntPtr h);
  [DllImport("advapi32.dll", SetLastError=true)] static extern int RegGetKeySecurity(IntPtr h, int si, byte[] sd, ref int len);
  [DllImport("advapi32.dll", SetLastError=true)] static extern int RegSetKeySecurity(IntPtr h, int si, byte[] sd);
  [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
  static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string s, int rev, out IntPtr psd, out int sz);
  [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
  static extern bool ConvertSecurityDescriptorToStringSecurityDescriptor(byte[] sd, int rev, int si, out IntPtr s, out int len);
  [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
  static readonly IntPtr HKCU = new IntPtr(unchecked((int)0x80000001));
  const int LABEL=0x10, WRITE_OWNER=0x00080000, READ_CONTROL=0x00020000;
  public static string Get(string sub){
    IntPtr h; int rc = RegOpenKeyEx(HKCU, sub, 0, READ_CONTROL, out h);
    if(rc!=0) return "open-err:"+rc;
    int len=0; RegGetKeySecurity(h, LABEL, null, ref len);
    if(len<=0){ RegCloseKey(h); return "(medium/none)"; }
    byte[] b=new byte[len]; int rc2=RegGetKeySecurity(h, LABEL, b, ref len); RegCloseKey(h);
    if(rc2!=0) return "get-err:"+rc2;
    IntPtr s; int sl;
    if(ConvertSecurityDescriptorToStringSecurityDescriptor(b,1,LABEL,out s,out sl)){ string r=Marshal.PtrToStringUni(s); LocalFree(s); return r; }
    return "(medium/none)";
  }
  public static string SetLow(string sub){
    IntPtr psd; int sz;
    if(!ConvertStringSecurityDescriptorToSecurityDescriptor("S:(ML;OICI;NW;;;LW)",1,out psd,out sz))
      return "sddl-err:"+Marshal.GetLastWin32Error();
    byte[] b=new byte[sz]; Marshal.Copy(psd,b,0,sz); LocalFree(psd);
    IntPtr h; int rc = RegOpenKeyEx(HKCU, sub, 0, WRITE_OWNER|READ_CONTROL, out h);
    if(rc!=0) return "open-err:"+rc;
    int sr = RegSetKeySecurity(h, LABEL, b); RegCloseKey(h);
    return sr==0 ? "OK" : "set-err:"+sr;
  }
}
'@
}

# ---- registry ownership+delete helper (MMDevices keys are TrustedInstaller-owned) --
# Compiled ON DEMAND (~0.6 s): only -CleanGhostEndpoints ever needs this one.
function Initialize-RegDel {
    if ('RegDel' -as [type]) { return }
Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Text;
public static class RegDel {
  [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
  [DllImport("advapi32.dll", SetLastError=true)] static extern bool OpenProcessToken(IntPtr h, int a, out IntPtr t);
  [DllImport("advapi32.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern bool LookupPrivilegeValue(string s, string n, out long l);
  [StructLayout(LayoutKind.Sequential, Pack=4)] struct TP { public int Count; public long Luid; public int Attr; }
  [DllImport("advapi32.dll", SetLastError=true)] static extern bool AdjustTokenPrivileges(IntPtr t, bool d, ref TP n, int l, IntPtr p, IntPtr r);
  [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern int RegOpenKeyEx(IntPtr h, string s, int o, int sam, out IntPtr r);
  [DllImport("advapi32.dll", SetLastError=true)] static extern int RegCloseKey(IntPtr h);
  [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern int RegEnumKeyEx(IntPtr h, int i, StringBuilder n, ref int cch, IntPtr r, StringBuilder c, IntPtr cc, IntPtr ft);
  [DllImport("advapi32.dll", SetLastError=true)] static extern int RegSetKeySecurity(IntPtr h, int si, byte[] sd);
  [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern int RegDeleteTree(IntPtr h, string s);
  [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string s, int rev, out IntPtr psd, out int sz);
  [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
  static readonly IntPtr HKLM = new IntPtr(unchecked((int)0x80000002));
  const int WRITE_OWNER=0x00080000, WRITE_DAC=0x00040000, READ_CONTROL=0x00020000, KEY_ENUM=0x0008;
  static byte[] _owner, _dacl;
  static byte[] Sd(string sddl){ IntPtr p; int sz; if(!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl,1,out p,out sz)) return null; byte[] b=new byte[sz]; Marshal.Copy(p,b,0,sz); LocalFree(p); return b; }
  static void EnablePriv(string n){ IntPtr t; if(!OpenProcessToken(GetCurrentProcess(),0x28,out t)) return; long l; if(!LookupPrivilegeValue(null,n,out l)) return; TP tp; tp.Count=1; tp.Luid=l; tp.Attr=2; AdjustTokenPrivileges(t,false,ref tp,0,IntPtr.Zero,IntPtr.Zero); }
  static void Grant(string sub){
    IntPtr h;
    if(RegOpenKeyEx(HKLM,sub,0,WRITE_OWNER,out h)==0){ RegSetKeySecurity(h,1,_owner); RegCloseKey(h); }
    if(RegOpenKeyEx(HKLM,sub,0,WRITE_DAC|READ_CONTROL,out h)==0){ RegSetKeySecurity(h,4,_dacl); RegCloseKey(h); }
    if(RegOpenKeyEx(HKLM,sub,0,KEY_ENUM|READ_CONTROL,out h)==0){
      List<string> kids=new List<string>(); int i=0;
      while(true){ StringBuilder nm=new StringBuilder(512); int cch=512; if(RegEnumKeyEx(h,i,nm,ref cch,IntPtr.Zero,null,IntPtr.Zero,IntPtr.Zero)!=0) break; kids.Add(nm.ToString()); i++; }
      RegCloseKey(h);
      foreach(string k in kids) Grant(sub+"\\"+k);
    }
  }
  public static string Delete(string sub){
    if(_owner==null){ EnablePriv("SeTakeOwnershipPrivilege"); EnablePriv("SeRestorePrivilege"); EnablePriv("SeBackupPrivilege"); _owner=Sd("O:BA"); _dacl=Sd("D:(A;OICI;KA;;;BA)(A;OICI;KA;;;SY)"); }
    if(_owner==null||_dacl==null) return "sddl-init-failed";
    Grant(sub);
    int rc=RegDeleteTree(HKLM,sub);
    return rc==0 ? "OK" : "delete-err:"+rc;
  }
}
'@
}

# ---- shared helpers --------------------------------------------------------
# Callers wrap this in @(): PowerShell unrolls a one-element array on return, and a lone
# hashtable's .Count is its KEY count ("WOULD delete 4 store(s)" with one store).
function Get-ExistingStores { @($Stores | Where-Object { Test-Path $_.PS }) }

# The label Windows itself uses: Low, no-write-up only, inherited by subkeys (OICI) - with
# "ID" added when Windows reports it as inherited on a subkey.
function Test-LowLabel([string]$label) { $label -match '\(ML;(OICI|CIOI)(ID)?;NW;;;LW\)' }

# One saved entry per (output device, app). The level is a VT_R4 in value 3 and the mute
# flag a VT_BOOL in value 5 of the entry's {219ED5A0-...} subkey, each after an 8-byte header.
function Get-StoreEntries($store) {
    foreach ($k in (Get-ChildItem -LiteralPath $store.PS -ErrorAction SilentlyContinue)) {
        $def = (Get-ItemProperty -LiteralPath $k.PSPath -ErrorAction SilentlyContinue).'(default)'
        $app = '(system sounds)'
        if ($def -and $def -notmatch '\|#') {
            $tail = $def.Substring($def.LastIndexOf('\') + 1)
            $cut = $tail.IndexOf('%'); if ($cut -ge 0) { $tail = $tail.Substring(0, $cut) }
            if ($tail) { $app = $tail }
        }
        $vol = $null; $mute = $false
        $p = Get-Item -LiteralPath (Join-Path $k.PSPath '{219ED5A0-9CBF-4F3A-B927-37C9E5C5F14F}') -ErrorAction SilentlyContinue
        if ($p) {
            $b = $p.GetValue('3'); if ($b -is [byte[]] -and $b.Length -ge 12 -and $b[0] -eq 4) { $vol = [BitConverter]::ToSingle($b, 8) }
            $f = $p.GetValue('5'); if ($f -is [byte[]] -and $f.Length -ge 10 -and $f[0] -eq 11) { $mute = ([BitConverter]::ToInt16($f, 8) -ne 0) }
        }
        [pscustomobject]@{ Name = $app; Vol = $vol; Mute = $mute }
    }
}

# The saved apps by name, each with the level(s) Windows will start it at. It used to print
# the first 15 KEYS in hash order, names only - ten of them the same app on the machine this
# was written on - so neither "is my browser saved?" nor "at what level?" could be answered.
# An entry at exactly 4% gets a note: that is the level the volume booster holds an app at
# while it boosts, so a boost that ended without putting it back leaves the app that quiet.
# It is an [INFO], not a warning - 4% can just as well be the level the user chose.
function Show-StoreEntries($store) {
    $apps = @(Get-StoreEntries $store | Group-Object Name | Sort-Object Name)
    foreach ($g in ($apps | Select-Object -First 40)) {
        $levels = @($g.Group | ForEach-Object { if ($null -eq $_.Vol) { 'no level saved' } else { ('{0:0}%' -f ($_.Vol * 100)) + $(if ($_.Mute) { ' muted' } else { '' }) } } | Select-Object -Unique)
        $n = if ($g.Count -gt 1) { '  ({0} entries)' -f $g.Count } else { '' }
        Write-Host ("      - {0}  {1}{2}" -f $g.Name, ($levels -join ', '), $n)
    }
    if ($apps.Count -gt 40) { Write-Host ("      ... and {0} more apps" -f ($apps.Count - 40)) }
    foreach ($g in $apps) {
        if (@($g.Group | Where-Object { $null -ne $_.Vol -and $_.Vol -ge 0.035 -and $_.Vol -le 0.045 }).Count -gt 0) {
            Report INFO ("{0} is saved at 4%. If you did not set that yourself: it is the level the volume booster holds an app at while it boosts, so a boost may have ended without putting it back - drag its slider back up in the Volume Mixer once it is playing." -f $g.Name)
        }
    }
}

# What -Status says about a label that is not the Low one. An empty Multimedia placeholder
# next to the canonical store is not what Windows writes to, so its label is information:
# the [WARN] it used to get sent people with a healthy store off to "run the default fix".
function Show-LabelVerdict($store, [int]$entries, [string]$label) {
    if (Test-LowLabel $label) { return }
    $placeholder = ($store.Sub -ne $Stores[0].Sub) -and ($entries -eq 0) -and (Test-Path -LiteralPath $Stores[0].PS)
    if ($placeholder) { Report INFO ("{0}: an empty placeholder without the Low label. Windows is not using it, so that is harmless; the default fix labels it as well." -f $store.Name) }
    else { Report WARN ("{0}: that is not the Low label, so Chromium browsers and Store apps cannot save their volume here. Run the default fix." -f $store.Name) }
}

function Get-LabelText([string]$sub) {
    Initialize-RegLabel
    $l = [RegLabel]::Get($sub)
    if ([string]::IsNullOrWhiteSpace($l) -or $l -eq 'S:' -or $l -eq '(medium/none)') { '(none = Medium)' } else { $l }
}

function Get-ActiveRenderEndpoints {
    $out = @()
    foreach ($e in (Get-ChildItem $MMRender -ErrorAction SilentlyContinue)) {
        $st = (Get-ItemProperty $e.PSPath -Name DeviceState -ErrorAction SilentlyContinue).DeviceState
        $pr = Get-ItemProperty (Join-Path $e.PSPath 'Properties') -ErrorAction SilentlyContinue
        $nm = $pr.'{a45c254e-df1c-4efd-8020-67d146a850e0},2'
        if (-not $nm) { $nm = $pr.'{b3f8fa53-0004-438e-9003-51a46e139bfc},6' }
        $out += [pscustomobject]@{ Guid = $e.PSChildName; State = [int]$st; Name = $nm; Path = $e.PSPath }
    }
    $out
}

function Resolve-ApoVendor([string]$clsid) {
    foreach ($root in 'HKLM:\SOFTWARE\Classes\CLSID', 'HKLM:\SOFTWARE\Classes\WOW6432Node\CLSID') {
        $ip = Get-ItemProperty "$root\$clsid\InprocServer32" -ErrorAction SilentlyContinue
        if ($ip) {
            $dll  = $ip.'(default)'
            $name = (Get-ItemProperty "$root\$clsid" -ErrorAction SilentlyContinue).'(default)'
            $co   = ''
            if ($dll -and (Test-Path -LiteralPath $dll)) { $co = (Get-Item -LiteralPath $dll).VersionInfo.CompanyName }
            return [pscustomobject]@{ Clsid = $clsid; Name = $name; Dll = $dll; Company = $co }
        }
    }
    [pscustomobject]@{ Clsid = $clsid; Name = '(unregistered)'; Dll = ''; Company = '' }
}

# Effect (APO) class ids registered in an endpoint's FxProperties. Drivers written since
# Windows 10 1803 list theirs as REG_MULTI_SZ ("composite" effects), which PowerShell hands
# back as string[]. The old filter kept plain strings only: on the machine this was written
# on it showed one Conexant APO and missed six DTS and Realtek ones on the same output.
# Left out: the property set d3993a3f-... (signal-processing MODE ids - GUIDs, but not
# classes) and d04e05a6-...,3 (the effect's settings page, not an effect).
function Get-EffectClsids($fx) {
    $ids = foreach ($p in $fx.PSObject.Properties) {
        if ($p.Name -like 'PS*' -or $p.Name -like '{d3993a3f-99c2-4402-b5ec-a92a0367664b}*' -or $p.Name -eq '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},3') { continue }
        foreach ($v in @($p.Value)) {
            if ($v -is [string] -and $v -match '^\{[0-9A-Fa-f-]{36}\}$' -and $v -ne '{00000000-0000-0000-0000-000000000000}') { $v.ToUpper() }
        }
    }
    @($ids | Select-Object -Unique)
}

# A shortcut target that is one of the browsers: by exe name, or Opera's launcher.exe.
function Test-BrowserTarget([string]$target) {
    if (-not $target) { return $false }
    $leaf = (Split-Path $target -Leaf).ToLower()
    if ($BrowserExes -contains $leaf) { return $true }
    ($leaf -eq 'launcher.exe' -and $target -match '\\Opera[^\\]*\\launcher\.exe$')
}

function Get-Shortcuts {
    $dirs = @(
        [Environment]::GetFolderPath('Desktop'),
        [Environment]::GetFolderPath('CommonDesktopDirectory'),
        (Join-Path $env:APPDATA     'Microsoft\Windows\Start Menu\Programs'),
        (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'),
        (Join-Path $env:APPDATA     'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique
    if (-not $dirs) { return @() }
    Get-ChildItem -LiteralPath $dirs -Filter *.lnk -Recurse -ErrorAction SilentlyContinue
}

# True when a browser command line carries both features in the --disable-features switch
# Chromium honours (the last one on the line).
function Test-BrowserFlags([string]$cmdLine) {
    $ms = Get-DisableFeatureSwitches $cmdLine
    if ($ms.Count -eq 0) { return $false }
    $last = @($ms[$ms.Count - 1].Groups['v'].Value -split ',' | ForEach-Object { $_.Trim() })
    @($BrowserFeatures | Where-Object { $last -notcontains $_ }).Count -eq 0
}

# The flags only work for a browser that was STARTED with them, and that is a state no
# shortcut check can see. A browser update recreates its Start Menu shortcut without them
# (seen on the machine this was written on: after Thorium's update the Desktop and taskbar
# shortcuts still carried the flags, the Start Menu one did not), and a browser started by
# another program, from a link, or by Windows in the background never reads a shortcut at
# all. A window opened later joins that flag-less process. So the running browser itself is
# asked: the main process is the one without a --type= argument.
# A command line is still only what was asked for. What the flags are FOR is that the
# browser plays its sound itself instead of through a separate audio process, so a browser
# that has the flags and such a process anyway (a policy, or a build that ignores them) is
# not fixed, and is not reported as if it were.
function Show-RunningBrowsers {
    Section 'Chromium browsers running right now'
    $mains = @(); $procs = @()
    try {
        $procs = @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object { $_.Name -and ($BrowserExes -contains $_.Name.ToLower()) -and $_.CommandLine })
        $mains = @($procs | Where-Object { $_.CommandLine -notmatch '\s--type=' })
    } catch { Report INFO ("Could not list running processes: {0}" -f $_.Exception.Message); return }
    if ($mains.Count -eq 0) { Report INFO 'None of the known Chromium browsers is running (or its command line cannot be read from this window).'; return }
    foreach ($p in $mains) {
        $audio = @($procs | Where-Object { $_.ParentProcessId -eq $p.ProcessId -and $_.CommandLine -match '--utility-sub-type=audio\.mojom\.AudioService' })
        if ((Test-BrowserFlags $p.CommandLine) -and $audio.Count -gt 0) {
            Report WARN ("{0} (pid {1}) was started with the audio flags, but still plays through a separate audio process (pid {2}), so the flags are not taking effect in it and it will not remember its volume. A browser policy or another launch switch can override them." -f $p.Name, $p.ProcessId, $audio[0].ProcessId)
        }
        elseif (Test-BrowserFlags $p.CommandLine) { Report OK ("{0} (pid {1}) is running with the audio flags." -f $p.Name, $p.ProcessId) }
        else { Report WARN ("{0} (pid {1}) is running WITHOUT the audio flags, so it will not remember its volume. Close it completely (tray and background processes too) and start it from a fixed shortcut. A browser update can recreate a shortcut without the flags - run this fix again after one." -f $p.Name, $p.ProcessId) }
    }
}

# ---- STATUS / DIAGNOSIS ----------------------------------------------------
function Show-Status([bool]$Deep) {
    Section 'Store variants and saved per-app volumes'
    if (Test-WrongProfile) {
        Report WARN ("This window runs as {0}, not as the desktop's user, so the store shown below is {0}'s - not yours. Run -Status from an ordinary PowerShell window to see your own." -f $MyIdentity.Name)
    }
    $found = @(Get-ExistingStores)
    if ($found.Count -eq 0) {
        Report WARN 'NO PropertyStore key exists (either variant). Volumes cannot persist. Run the default fix or -RebuildStore.'
    } elseif (-not (Test-Path -LiteralPath $Stores[0].PS)) {
        # the Multimedia variant alone used to pass without a word
        Report WARN 'The canonical store key is missing - only the Multimedia variant exists, which Windows did not write to on the builds checked. Run the default fix to create it.'
    }
    foreach ($s in $found) {
        $k = Get-Item -LiteralPath $s.PS
        $lbl = Get-LabelText $s.Sub
        $tag = if ($k.SubKeyCount -gt 0) { 'ACTIVE store' } else { 'empty' }
        Report INFO ("{0}: {1} entries [{2}]  label = {3}" -f $s.Name, $k.SubKeyCount, $tag, $lbl)
        # a verdict, not just the raw label: a Medium label is the defect this kit exists for
        Show-LabelVerdict $s $k.SubKeyCount $lbl
        Show-StoreEntries $s
    }
    Report INFO 'Reminder: volumes are saved PER OUTPUT DEVICE and when the app CLOSES.'

    Show-RunningBrowsers

    Section 'Bluetooth absolute volume'
    if (Test-Path $BtCtKey) {
        $v = (Get-ItemProperty $BtCtKey -Name DisableAbsoluteVolume -ErrorAction SilentlyContinue).DisableAbsoluteVolume
        if ($v -eq 1) { Report INFO 'DisableAbsoluteVolume = 1 (absolute volume is OFF - Windows controls volume in software).' }
        else { Report INFO ("DisableAbsoluteVolume = {0} (absolute volume is ON - default). Only relevant for Bluetooth audio devices." -f $(if ($null -eq $v) { '<not set>' } else { $v })) }
    } else {
        Report INFO 'Bluetooth AVRCP key is absent (no Bluetooth audio stack has initialized).'
    }
    Report INFO 'Symptom check: with a BT device active, if the MASTER slider snaps back / jumps in big steps, use -DisableBtAbsoluteVolume (reboot needed).'

    Section 'Audio enhancements / third-party effects (APOs) on ACTIVE outputs'
    $eps = Get-ActiveRenderEndpoints
    $active = @($eps | Where-Object { $_.State -eq 1 })
    foreach ($ep in $active) {
        # The on/off switch and the effect chain both live in the endpoint's FxProperties key
        # (the effects store). The switch used to be read from Properties, where Windows
        # does not keep it, so this line said "ON (default)" whatever the real setting was.
        $fx = Get-ItemProperty -LiteralPath (Join-Path $ep.Path 'FxProperties') -ErrorAction SilentlyContinue
        $sysfx = if ($fx) { $fx.$SysFxValue } else { $null }
        $fxState = if ($sysfx -eq 1) { 'enhancements OFF' } else { 'enhancements ON (default)' }
        Report INFO ("{0}  [{1}]" -f $ep.Name, $fxState)
        if ($fx) {
            $apos = @(Get-EffectClsids $fx | ForEach-Object { Resolve-ApoVendor $_ })
            # one line per DLL: a vendor usually registers several classes from one file
            foreach ($g in @($apos | Where-Object { $_.Dll } | Group-Object Dll)) {
                $r = $g.Group[0]
                $who = if ($r.Company) { $r.Company } elseif ($r.Name) { $r.Name } else { $r.Clsid }
                $n = if ($g.Count -gt 1) { ' x{0}' -f $g.Count } else { '' }
                $isMs = ($r.Company -match 'Microsoft') -or ($r.Dll -match '\\Windows\\System32\\(mf|audio|wmalfx)' )
                if ($isMs) { Report INFO ("    APO: {0}  ({1}{2})" -f $who, (Split-Path $r.Dll -Leaf), $n) }
                else { Report WARN ("    third-party APO: {0}  ({1}{2}) - can interfere; see README (enhancements section)" -f $who, (Split-Path $r.Dll -Leaf), $n) }
            }
            # a class id with no registration has no DLL to name; Split-Path on the empty
            # string used to throw here and end the whole status with a [FAIL]
            foreach ($r in @($apos | Where-Object { -not $_.Dll })) {
                Report INFO ("    effect entry {0} has no registered class - a leftover of removed audio software" -f $r.Clsid)
            }
            if ($apos.Count -eq 0) { Report INFO '    no effect APOs registered' }
        } else { Report INFO '    no FxProperties (no effects)' }
    }

    Section 'Output devices / drivers'
    $stale = @($eps | Where-Object { $_.State -ne 1 })
    Report INFO ("{0} active, {1} inactive/ghost render endpoints registered." -f $active.Count, $stale.Count)
    if ($stale.Count -ge 5) {
        Report WARN 'Many stale endpoints (old drivers / HDMI / virtual devices). Harmless, but see README to tidy up via mmsys.cpl.'
    }
    $virt = @($active | Where-Object { $_.Name -match 'Virtual|Steam Streaming|CABLE|Voicemeeter' })
    if ($virt.Count -gt 0) {
        Report INFO ("Virtual audio devices present: {0}. Note: per-app volumes/routing are stored per device; see README (Fast Startup note)." -f (@($virt | ForEach-Object { $_.Name }) -join ', '))
    }
    $fastBoot = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' -Name HiberbootEnabled -ErrorAction SilentlyContinue).HiberbootEnabled
    if ($fastBoot -eq 1) { Report INFO 'Fast Startup is ON. If apps forget their ASSIGNED OUTPUT DEVICE after a full shutdown (not restart), turn it off - see README.' }

    if ($Deep -and $Elevated) {
        Section 'IE-mode component (the canonical store lives under its subtree)'
        try {
            # it takes Windows about ten seconds to answer this one
            Report INFO 'Asking Windows about the IE-mode component (this can take ten seconds or so)...'
            $cap = Get-WindowsCapability -Online -Name 'Browser.InternetExplorer*' -ErrorAction Stop
            foreach ($c in $cap) { Report INFO ("{0}: {1}" -f $c.Name, $c.State) }
            # Information only. This was a [WARN] on every run, but current Windows 11 (24H2,
            # LTSC 2024) does not ship the component at all and the store works without it -
            # seen on such a machine with 250 saved entries.
            if (@($cap | Where-Object { $_.State -eq 'Installed' }).Count -eq 0) {
                Report INFO 'IE-mode component not installed. That is normal on current Windows 11, and the store works without it; only on an older build, if volumes do not persist, may its removal be the reason (community reports).'
            }
        } catch { Report INFO ("IE-mode component check skipped: {0}" -f $_.Exception.Message) }
    } elseif ($Deep) {
        Report INFO 'IE-mode component check skipped (it needs an elevated window).'
    }
}

# ---- FIX 1: BleachBit cleaner rule -----------------------------------------
function Fix-BleachBit {
    Section 'BleachBit "Windows Volume Mixer" cleaner rule (root cause on this machine)'
    if (-not (Test-PerUserAllowed 'the BleachBit rule')) { return }
    $ini = Join-Path $env:APPDATA 'BleachBit\bleachbit.ini'
    if (-not (Test-Path -LiteralPath $ini)) { Report INFO 'BleachBit config not found for this user. Nothing to disable. (A PORTABLE BleachBit keeps bleachbit.ini next to its exe, where this step cannot find it: switch off "Windows Volume Mixer" in its Windows cleaner list by hand.)'; return }
    $key   = 'winapp2_windows.windows_volume_mixer'
    # BleachBit's ini is UTF-8. It is read and written as exactly that, keeping its BOM (or
    # the lack of one) and its line endings. Get-Content took a file without a BOM for ANSI,
    # so a non-ASCII path in it (a whitelisted folder under a Cyrillic user name) came back
    # as mojibake, and Set-Content -Encoding UTF8 added a BOM and rewrote LF as CRLF.
    $bytes  = [IO.File]::ReadAllBytes($ini)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $skip   = if ($hasBom) { 3 } else { 0 }
    $text   = (New-Object Text.UTF8Encoding($false)).GetString($bytes, $skip, $bytes.Length - $skip)
    $nl     = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $lines  = @($text -split "`r?`n")
    $idx = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match ("^\s*" + [regex]::Escape($key) + "\s*=")) { $idx = $i; break }
    }
    if ($idx -lt 0) { Report OK 'Rule not enabled in BleachBit config. Good.'; return }
    $cur = (($lines[$idx] -split '=', 2)[1]).Trim()
    if ($cur -eq 'False') { Report OK 'Rule already disabled (= False).'; return }
    if ($CheckOnly) { Report WARN ("Rule is '{0}' - WOULD set it to False." -f $cur); return }
    # BleachBit writes its whole configuration back whenever it saves, so a copy that is
    # open right now can put the rule straight back over this edit - while this script
    # has already reported [OK]. Ask for it to be closed instead of racing it.
    if (@(Get-Process -Name 'bleachbit*' -ErrorAction SilentlyContinue).Count -gt 0) {
        Report WARN 'BleachBit is open - close it and run this again; while it runs it can save its current settings over this change.'
        return
    }
    Ensure-BackupDir
    Copy-Item -LiteralPath $ini -Destination (Join-Path $BackupDir ('bleachbit.ini.' + (Stamp) + '.bak')) -Force
    $lines[$idx] = "$key = False"
    [IO.File]::WriteAllText($ini, ($lines -join $nl), (New-Object Text.UTF8Encoding($hasBom)))
    Report OK 'Disabled the "Windows Volume Mixer" cleaner rule (backup saved).'
    Report INFO 'Any other cleaner you use: never let it touch either PropertyStore path (see README).'
}

# ---- FIX 2: store key(s) + low integrity label -----------------------------
function Fix-Store {
    Section 'Per-app volume store: key + low integrity label (both variants probed)'
    if (-not (Test-PerUserAllowed 'the volume store and its label')) { return }
    Initialize-RegLabel
    $canon = $Stores[0]
    $found = @(Get-ExistingStores)
    $haveCanon = Test-Path -LiteralPath $canon.PS
    if ($CheckOnly) {
        # a verdict for each key, and what the fix would do - it used to print the raw label
        # as [INFO] and leave it at that, so a Medium store ended in "0 warning(s)"
        if (-not $haveCanon) { Report WARN 'The canonical store key is missing - WOULD create it and label it Low.' }
        foreach ($s in $found) {
            $l = Get-LabelText $s.Sub
            if (Test-LowLabel $l) { Report INFO ("{0}: exists, label OK ({1})" -f $s.Name, $l) }
            else { Report WARN ("{0}: exists, but its label is {1} - WOULD apply the Low label (without it Chromium browsers and Store apps cannot save their volume)." -f $s.Name, $l) }
        }
        return
    }
    Ensure-BackupDir
    if (-not $haveCanon) {
        # The canonical key is the one Windows writes to. It used to be created only when NO
        # variant existed at all, so a machine that had just the empty Multimedia placeholder
        # - the very state the README describes after a reinstall - got that placeholder
        # labelled, an [OK], and still no store.
        Report INFO 'The canonical store key is missing - creating it and applying the Low label.'
        & reg.exe add $canon.Reg /f | Out-Null
        $rc = $LASTEXITCODE
        if (-not (Test-Path -LiteralPath $canon.PS)) {
            Report FAIL ("Could not create the store key (reg.exe exit {0}). Check HKCU write access, or try -RebuildStore." -f $rc)
            return
        }
        $found = @(Get-ExistingStores)
    }
    foreach ($s in $found) {
        $k = Get-Item -LiteralPath $s.PS
        if ($k.SubKeyCount -gt 0) { & reg.exe export $s.Reg (Join-Path $BackupDir ('PropertyStore.' + ($s.Name -replace '[^A-Za-z]','') + '.' + (Stamp) + '.reg')) /y | Out-Null }
        $label = [RegLabel]::Get($s.Sub)
        # The exact label Windows itself uses: Low, inherited by subkeys (OICI), no-write-up
        # only. This used to accept anything containing ";LW" - e.g. a Low label without
        # inheritance, so every per-app subkey created later came out Medium.
        if (Test-LowLabel $label) {
            Report OK ("{0}: low-integrity label OK ({1})" -f $s.Name, $label)
        } else {
            $r = [RegLabel]::SetLow($s.Sub)
            if ($r -eq 'OK') { Report OK ("{0}: applied Low label. Now: {1}" -f $s.Name, [RegLabel]::Get($s.Sub)) }
            # a [FAIL]: this label is the one thing the step is for (it was a [WARN], exit 0)
            else { Report FAIL ("{0}: could not set Low label ({1}). Alternative: run -RebuildStore so Windows recreates it natively." -f $s.Name, $r); continue }
        }
        # The saved entries below it need the label too. Windows gives it to the ones it
        # creates, but entries restored from a .reg backup come back without it - and the
        # label set on the root is not pushed down to keys that already exist.
        $c = Repair-ChildLabels $s
        if ($c.Fixed -gt 0) { Report OK ("{0}: applied the Low label to {1} saved entr{2} that lacked it." -f $s.Name, $c.Fixed, $(if ($c.Fixed -eq 1) { 'y' } else { 'ies' })) }
        if ($c.Failed -gt 0) { Report FAIL ("{0}: {1} saved entr{2} could not be given the Low label." -f $s.Name, $c.Failed, $(if ($c.Failed -eq 1) { 'y' } else { 'ies' })) }
    }
}

# The per-app entries of a store and the one subkey each of them has.
function Repair-ChildLabels($store) {
    $fixed = 0; $failed = 0
    $root = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($store.Sub)
    if ($root) {
        try {
            foreach ($n in $root.GetSubKeyNames()) {
                $subs = @($store.Sub + '\' + $n)
                $k = $root.OpenSubKey($n)
                if ($k) { foreach ($g in $k.GetSubKeyNames()) { $subs += ($store.Sub + '\' + $n + '\' + $g) }; $k.Close() }
                foreach ($p in $subs) {
                    if (Test-LowLabel ([RegLabel]::Get($p))) { continue }
                    if ([RegLabel]::SetLow($p) -eq 'OK') { $fixed++ } else { $failed++ }
                }
            }
        } finally { $root.Close() }
    }
    @{ Fixed = $fixed; Failed = $failed }
}

# Stop-Service -Force on Audiosrv also stops every service that depends on it - a vendor's
# audio service such as RtkAudioUniversalService - and starting Audiosrv again does not
# bring those back: they stayed down until the next reboot while the run said "audio
# service restarted". The ones that were running are noted before the stop and started
# again afterwards; one that will not start is a [WARN].
function Get-RunningAudioDependents {
    $names = @()
    foreach ($svc in 'AudioEndpointBuilder','Audiosrv') {
        try { $names += @(Get-Service -Name $svc -DependentServices -ErrorAction Stop | Where-Object { $_.Status -eq 'Running' } | ForEach-Object { $_.Name }) } catch { }
    }
    @($names | Where-Object { $_ -and ('Audiosrv','AudioEndpointBuilder' -notcontains $_) } | Select-Object -Unique)
}
function Start-AudioDependents([string[]]$Names) {
    foreach ($n in @($Names)) {
        if (-not $n) { continue }
        try { Start-Service -Name $n -ErrorAction Stop; Report INFO ("{0} depends on the audio service and was stopped with it; it is running again." -f $n) }
        catch { Report WARN ("{0} was running before, was stopped together with the audio service, and did not start again: {1} Start it from services.msc, or reboot." -f $n, $_.Exception.Message) }
    }
}

# ---- OPT-IN: rebuild the store natively ------------------------------------
function Rebuild-Store {
    Section 'REBUILD store: delete + let the Windows Audio service recreate it'
    if (-not (Test-PerUserAllowed 'rebuilding the volume store')) { return }
    Report INFO 'This is the "let Windows do it" repair. WARNING: all RUNNING apps reset to 100% when the audio service restarts.'
    $found = @(Get-ExistingStores)
    # The preview only reads, so it needs no admin rights. It used to be refused with [FAIL]
    # in an ordinary window, although the README offers -CheckOnly with every switch.
    if ($CheckOnly) {
        Report INFO ("WOULD: backup + delete {0} store(s), restart Audiosrv/AudioEndpointBuilder, let Windows rebuild." -f $found.Count)
        if (-not $Elevated) { Report INFO 'The real run needs an elevated PowerShell window.' }
        return
    }
    if (-not $Elevated) { Report FAIL 'Requires admin (service restart). Re-run this command from an elevated PowerShell window.'; return }
    Initialize-RegLabel
    Ensure-BackupDir
    # Every saved per-app volume lives in these keys and is about to be deleted, so the
    # backup is the only way back. reg.exe reports failure only through its exit code
    # and writes no file when it fails; unchecked, a failed export was followed by the
    # delete anyway. Verify each one, and stop before touching anything if one is bad.
    foreach ($s in $found) {
        $bk = Join-Path $BackupDir ('PropertyStore.' + ($s.Name -replace '[^A-Za-z]','') + '.' + (Stamp) + '.reg')
        & reg.exe export $s.Reg $bk /y | Out-Null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $bk) -or (Get-Item -LiteralPath $bk).Length -eq 0) {
            Report FAIL ("Backup of {0} FAILED (reg.exe exit {1}) - refusing to delete the store without it. Nothing was changed." -f $s.Name, $LASTEXITCODE)
            return
        }
        Report OK ("Backed up {0} -> {1}" -f $s.Name, (Split-Path $bk -Leaf))
    }
    # Both outcomes below are tracked, so the closing line can only claim what actually
    # happened. It used to say "Store deleted and audio service restarted" unconditionally
    # - even directly under a [FAIL] for that very restart, or after a delete that failed.
    $deleted = $true
    $restarted = $true
    $deps = Get-RunningAudioDependents
    # From the first Stop-Service to the restart is ONE try/finally, so the services are
    # started again however this part ends. A stop that only half worked (Audiosrv down,
    # AudioEndpointBuilder refusing) used to return straight away and leave the machine
    # with no audio until a reboot - and so would any error nobody anticipated.
    try {
        try {
            Stop-Service -Name Audiosrv -Force -ErrorAction Stop
            Stop-Service -Name AudioEndpointBuilder -Force -ErrorAction Stop
        } catch { Report FAIL ("Could not stop audio services: {0}" -f $_.Exception.Message); return }
        foreach ($s in $found) {
            & reg.exe delete $s.Reg /f | Out-Null
            if ($LASTEXITCODE -ne 0 -or (Test-Path $s.PS)) {
                $deleted = $false
                Report FAIL ("Could not delete {0} (reg.exe exit {1}) - it was NOT cleared." -f $s.Name, $LASTEXITCODE)
            }
        }
    } finally {
        # Each start is guarded on its own: $ErrorActionPreference is 'Stop', so one failure
        # must not skip the other start, the checks below, the Summary or the exit code.
        foreach ($svc in 'AudioEndpointBuilder','Audiosrv') {
            try { Start-Service -Name $svc -ErrorAction Stop }
            catch {
                $restarted = $false
                Report FAIL ("Could not restart {0}: {1}" -f $svc, $_.Exception.Message)
                Report INFO 'Start it from services.msc (or reboot) before relying on audio again.'
            }
        }
        if ($restarted) { Start-AudioDependents $deps }
    }
    Start-Sleep -Seconds 2
    # Empirical note (verified on my machine, build 26100): the audio service does
    # NOT reliably re-create the store chain by itself - it only writes into an
    # existing one (and ignores the Multimedia skeleton while the canonical path is
    # missing). So if the store has not reappeared, create + label it explicitly -
    # the method is verified working end-to-end on my machine.
    # What counts is the CANONICAL key, with the Low label. "A store is present again" used
    # to be accepted for any variant without looking at either: the empty Multimedia
    # placeholder, or a key a Medium-integrity writer had just created, passed as rebuilt.
    $canon = $Stores[0]
    $madeIt = $false
    $r = 'missing'
    if (-not (Test-Path -LiteralPath $canon.PS)) { & reg.exe add $canon.Reg /f | Out-Null; $madeIt = $true }
    $labelOk = $false
    if (Test-Path -LiteralPath $canon.PS) {
        $labelOk = Test-LowLabel ([RegLabel]::Get($canon.Sub))
        if (-not $labelOk) { $r = [RegLabel]::SetLow($canon.Sub); $labelOk = ($r -eq 'OK') }
    }
    if (-not $labelOk) {
        Report FAIL ("The canonical store is missing or could not be given the Low label ({0}). Re-run the default fix." -f $r)
    } elseif (-not $deleted) {
        Report WARN 'At least one store was not cleared (see above), so this is not a clean slate - check it before relying on the rebuild.'
    } elseif ($madeIt -and $restarted) {
        Report OK 'Service did not recreate the store; created + Low-labeled it explicitly (the verified method).'
    } elseif ($madeIt) {
        Report OK 'Created + Low-labeled the store explicitly (the audio service is not running, so it could not have recreated it).'
    } elseif ($restarted) {
        Report OK 'Store deleted and audio service restarted; store present again, with the Low label.'
    } else {
        Report WARN 'Store deleted and present again, but the audio service did not restart - see above.'
    }
    if ($deleted) { Report INFO 'Old entries are gone (that is the point - clean slate). Set a volume in a NON-browser app, close it, reopen - it should stick.' }
}

# ---- OPT-IN: remove ghost (NOTPRESENT) audio endpoints ---------------------
function Clean-GhostEndpoints {
    Section 'Remove ghost (NOTPRESENT) audio endpoints'
    Report INFO 'Ghosts are leftover registry entries for devices no longer present. Removing them mostly tidies the mixer/device list.'
    $roots = @(
        @{ Flow = 'Render';  Path = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render';  RegBase = 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render' },
        @{ Flow = 'Capture'; Path = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture'; RegBase = 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture' }
    )
    $targets = @()
    foreach ($r in $roots) {
        foreach ($e in (Get-ChildItem $r.Path -ErrorAction SilentlyContinue)) {
            $st = (Get-ItemProperty $e.PSPath -Name DeviceState -ErrorAction SilentlyContinue).DeviceState
            # 4 = DEVICE_STATE_NOTPRESENT, in the low four bits. The registry value carries
            # private flags above them (0x10000000 marks a device the user disabled), so an
            # exact comparison missed a ghost that had been disabled before it went away.
            if (([int]$st -band 0xF) -eq 4) {
                $pr = Get-ItemProperty (Join-Path $e.PSPath 'Properties') -ErrorAction SilentlyContinue
                $nm = $pr.'{a45c254e-df1c-4efd-8020-67d146a850e0},2'; if (-not $nm) { $nm = '(unnamed)' }
                $targets += [pscustomobject]@{
                    Flow = $r.Flow; Guid = $e.PSChildName; Name = $nm; PS = $e.PSPath
                    Reg  = ($r.RegBase + '\' + $e.PSChildName)
                    Sub  = ('SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\' + $r.Flow + '\' + $e.PSChildName)
                }
            }
        }
    }
    if ($targets.Count -eq 0) { Report OK 'No ghost (NOTPRESENT) endpoints found. Nothing to remove.'; return }
    Report INFO ("{0} ghost endpoint(s):" -f $targets.Count)
    foreach ($t in $targets) { Write-Host ("      [{0}] {1}  {2}" -f $t.Flow, $t.Name, $t.Guid) }
    Report INFO 'Real devices that are merely disconnected (a Bluetooth headset that is off, an unplugged USB DAC) also show here: they come back when reconnected, but with default settings (name, enhancements, format). Preview with -CheckOnly first if you still use any of them.'
    if ($CheckOnly) { Report INFO 'CheckOnly - not removing.'; return }
    # Only now, after the preview (which just reads and used to be refused in an ordinary
    # window). The launcher applies the main fixes only and passes no switches on, so it is
    # this command that has to be run elevated - the message used to point at the launcher.
    if (-not $Elevated) { Report FAIL 'Requires admin - these keys are TrustedInstaller-owned. Re-run this command from an elevated PowerShell window.'; return }
    Initialize-RegDel
    Ensure-BackupDir
    $bk = Join-Path $BackupDir ('MMDevices-Audio.' + (Stamp) + '.reg')
    & reg.exe export 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio' $bk /y | Out-Null
    # reg.exe signals failure only through its exit code and writes no file when it
    # fails. Unchecked, the [OK] below is a lie and the delete loop runs without a net.
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $bk) -or (Get-Item -LiteralPath $bk).Length -eq 0) {
        Report FAIL ("Backup of MMDevices\Audio FAILED (reg.exe exit {0}) - refusing to delete anything without it." -f $LASTEXITCODE)
        return
    }
    Report OK ("Backed up MMDevices\Audio -> {0}" -f (Split-Path $bk -Leaf))
    $audiosrvWasRunning = (Get-Service Audiosrv -ErrorAction SilentlyContinue).Status -eq 'Running'
    $deps = Get-RunningAudioDependents
    $done = 0
    $restartOk = $true
    # From the first Stop-Service to the restart is ONE try/finally (as in Rebuild-Store):
    # however the removals end, the services are started again before this returns.
    try {
        try { Stop-Service Audiosrv -Force -ErrorAction Stop; Stop-Service AudioEndpointBuilder -Force -ErrorAction Stop }
        catch { Report WARN ("Could not stop audio services (continuing anyway): {0}" -f $_.Exception.Message) }
        foreach ($t in $targets) {
            $st = (Get-ItemProperty $t.PS -Name DeviceState -ErrorAction SilentlyContinue).DeviceState
            if (([int]$st -band 0xF) -ne 4) { Report WARN ("skip (state changed): {0}" -f $t.Name); continue }
            $r = [RegDel]::Delete($t.Sub)
            # Fallback if an empty key remained. The Test-Path below decides the outcome, so
            # reg.exe's own error text is dropped - with the preference lowered for this one
            # call: under 'Stop', Windows PowerShell 5.1 turns a REDIRECTED stderr line of a
            # native command into a terminating error, so the bare '2>$null' this used to
            # have ended the step right here, with both audio services still stopped.
            if (Test-Path $t.PS) { & { $ErrorActionPreference = 'Continue'; & reg.exe delete $t.Reg /f 2>$null | Out-Null } }
            if (-not (Test-Path $t.PS)) { $done++; Report OK ("removed: [{0}] {1}" -f $t.Flow, $t.Name) }
            else { Report WARN ("could not remove {0} ({1}); its owner and permissions were changed in the attempt and are not put back" -f $t.Name, $r) }
        }
    } finally {
        # Each restart is checked. These used to run with -ErrorAction SilentlyContinue and the
        # summary below said "audio services restarted" in every branch, so a machine left
        # with no audio at all was told everything was fine.
        $toStart = @('AudioEndpointBuilder')
        if ($audiosrvWasRunning) { $toStart += 'Audiosrv' }
        foreach ($svc in $toStart) {
            try { Start-Service -Name $svc -ErrorAction Stop }
            catch {
                $restartOk = $false
                Report FAIL ("Could not restart {0}: {1}" -f $svc, $_.Exception.Message)
                Report INFO 'Start it from services.msc (or reboot) before relying on audio again.'
            }
        }
        if ($restartOk) { Start-AudioDependents $deps }
    }
    $svcNote = if ($restartOk) { 'audio services restarted' } else { 'audio services did NOT all restart - see above' }
    if     ($done -eq $targets.Count) { Report OK   ("Removed {0} of {1} ghost endpoint(s); {2}." -f $done, $targets.Count, $svcNote) }
    elseif ($done -eq 0)              { Report FAIL ("Removed NONE of {0} ghost endpoint(s) - see the lines above; {1}." -f $targets.Count, $svcNote) }
    else                              { Report WARN ("Removed {0} of {1} ghost endpoint(s); the rest failed - see above; {2}." -f $done, $targets.Count, $svcNote) }
    # The backup is a record, not an undo button. This line used to say "To restore, import
    # the backup", but only Windows' own audio services may create keys under
    # MMDevices\Audio - administrators cannot, so the import of a removed endpoint is refused.
    Report INFO 'A removed device that is connected again gets a fresh entry from Windows, with default settings. The MMDevices-Audio backup in the backups folder records what was there; it cannot simply be imported again, because only the audio services may create keys in that place.'
}

# ---- FIX 3: audio services -------------------------------------------------
function Fix-Services {
    Section 'Audio services (Audiosrv, AudioEndpointBuilder)'
    foreach ($svc in 'Audiosrv','AudioEndpointBuilder') {
        # Every failure below ends in the one catch - a missing service, WMI refusing, a denied
        # Set-Service - so it names the service and the real error and assumes nothing. It used
        # to call all of them "not found": "Audiosrv not found: ... Access is denied".
        try {
            $s    = Get-Service -Name $svc -ErrorAction Stop
            $mode = (Get-CimInstance Win32_Service -Filter "Name='$svc'").StartMode   # Auto/Manual/Disabled - locale-independent
            if ($CheckOnly) { Report INFO ("{0}: Status={1} StartMode={2}" -f $svc, $s.Status, $mode); continue }
            if ($mode -ne 'Auto') {
                if ($Elevated) { Set-Service -Name $svc -StartupType Automatic; Report OK "$svc set to Automatic." }
                else { Report WARN "$svc is $mode (want Automatic) - re-run elevated to fix." }
            }
            if ($s.Status -ne 'Running') {
                try { Start-Service -Name $svc; Report OK "$svc started." }
                catch { Report WARN ("{0} is stopped and could not start: {1}" -f $svc, $_.Exception.Message) }
            } else { Report OK "$svc running." }
        } catch { Report FAIL ("{0}: {1}" -f $svc, $_.Exception.Message) }
    }
}

# ---- FIX 4 / REVERT: Chromium browser launch flags -------------------------
# Chromium keeps only the LAST value of a repeated switch (its base/command_line.h: "If a
# switch is specified multiple times, only the last value is used"). This step used to
# append a second --disable-features, which silently re-enabled every feature the user
# had disabled in their own. All occurrences are merged into one switch instead, placed
# where the last one was; only that switch is touched, so other arguments - including
# quoted ones containing double spaces - are left exactly as they were.
# Every --disable-features switch in a command line, however it is quoted:
#     --disable-features=A,B      --disable-features="A, B"      "--disable-features=A,B"
# The first pattern used to be the only one recognised. A quoted list with a space in it was
# cut at the quote, and the rest glued onto the new switch ("...OutOfProcess"A, B""); a
# switch quoted as a whole was not seen at all, so a second one was appended - and Chromium
# honours only the last. Chromium lower-cases switch names, so the match ignores case.
# Group 'v' holds the list.
function Get-DisableFeatureSwitches([string]$text) {
    [regex]::Matches([string]$text, '(?i)(?<=^|\s)(?:"--disable-features=(?<v>[^"]*)"|--disable-features=(?:"(?<v>[^"]*)"|(?<v>[^"\s]*)))')
}

function Merge-DisableFeatures([string]$argsText, [string[]]$add, [string[]]$remove) {
    if ($null -eq $argsText) { $argsText = '' }
    $ms = Get-DisableFeatureSwitches $argsText
    $list = New-Object System.Collections.Generic.List[string]
    foreach ($m in $ms) { foreach ($f in ($m.Groups['v'].Value -split ',')) { $f = $f.Trim(); if ($f -and -not $list.Contains($f)) { $list.Add($f) } } }
    foreach ($f in $add) { if (-not $list.Contains($f)) { $list.Add($f) } }
    foreach ($f in $remove) { [void]$list.Remove($f) }
    $switch = if ($list.Count) { '--disable-features=' + ($list -join ',') } else { '' }
    if ($ms.Count -eq 0) {
        if (-not $switch) { return $argsText.Trim() }
        return ($argsText.TrimEnd() + ' ' + $switch).Trim()
    }
    $s = $argsText
    for ($i = $ms.Count - 1; $i -ge 0; $i--) {
        $start = $ms[$i].Index; $len = $ms[$i].Length
        if ($i -eq $ms.Count - 1 -and $switch) { $s = $s.Remove($start, $len).Insert($start, $switch); continue }
        # drop this occurrence together with the whitespace that separated it
        if ($start -gt 0 -and [char]::IsWhiteSpace($s[$start - 1])) { $start--; $len++ }
        $s = $s.Remove($start, $len)
    }
    $s.Trim()
}

function Fix-Browsers {
    Section 'Chromium browser launch flags (volume memory inside the browser)'
    if (-not (Test-PerUserAllowed 'the browser shortcut flags')) { return }
    $lnks = @(Get-Shortcuts)
    if ($lnks.Count -eq 0) { Report INFO 'No .lnk shortcuts found in the usual locations.'; return }
    $wsh = New-Object -ComObject WScript.Shell
    $touched = 0; $matched = 0
    foreach ($lnk in $lnks) {
        try {
            $sc  = $wsh.CreateShortcut($lnk.FullName)
            if (-not (Test-BrowserTarget $sc.TargetPath)) { continue }
            $exe = (Split-Path $sc.TargetPath -Leaf).ToLower()
            $matched++
            $cur  = ([string]$sc.Arguments).Trim()
            $want = if ($Revert) { Merge-DisableFeatures $cur @() $BrowserFeatures } else { Merge-DisableFeatures $cur $BrowserFeatures @() }
            # Every line names the shortcut by its full path: Desktop, Start Menu and the
            # taskbar pin folder hold same-named shortcuts, and "Thorium.lnk" three times did
            # not say which of them had been lacking the flags.
            if ($want -ceq $cur) {
                if (-not $Revert) { Report OK ("{0} - flags already present." -f $lnk.FullName) }
                continue
            }
            if ($CheckOnly) {
                if ($Revert) { Report INFO ("WOULD remove flags from: {0}" -f $lnk.FullName) }
                else { Report INFO ("WOULD add flags to: {0} ({1})" -f $lnk.FullName, $exe) }
                continue
            }
            # A shortcut holds at most 1023 characters of arguments, and Save() cuts a longer
            # list without an error - the switch added at the end is what gets lost, while
            # this used to print "[OK] Added flags" on every run. Refused before anything is
            # written, and whatever was saved is read back.
            if ($want.Length -gt 1023) {
                Report FAIL ("{0}: its argument list would be {1} characters and a shortcut holds 1023, so the flags cannot be added. Shorten the other arguments, then run this again." -f $lnk.FullName, $want.Length)
                continue
            }
            Ensure-BackupDir
            Backup-Shortcut $lnk | Out-Null
            $sc.Arguments = $want
            $sc.Save()
            $now = ([string]$wsh.CreateShortcut($lnk.FullName).Arguments).Trim()
            if ($now -cne $want) {
                Report FAIL ("{0}: the shortcut was saved, but its arguments do not read back as written - check it (a backup is in the backups folder)." -f $lnk.FullName)
                continue
            }
            $touched++
            if ($Revert) { Report OK ("Removed flags: {0}" -f $lnk.FullName) }
            else { Report OK ("Added flags: {0} ({1})" -f $lnk.FullName, $exe) }
        } catch { Report WARN ("Could not process {0}: {1}" -f $lnk.FullName, $_.Exception.Message) }
    }
    if ($matched -eq 0) { Report INFO 'No Chromium-based browser shortcuts found.' }
    Report INFO 'Taskbar pins may need re-pinning. Browser must be fully closed before relaunch. Trade-off: slightly weaker audio sandbox.'
}

# ---- OPT-IN: Bluetooth absolute volume toggles -----------------------------
function Set-BtAbsoluteVolume([bool]$Disable) {
    Section ("Bluetooth absolute volume: " + $(if ($Disable) { 'DISABLE' } else { 'ENABLE (restore default)' }))
    $want = if ($Disable) { 1 } else { 0 }
    $cur = $null
    if (Test-Path $BtCtKey) { $cur = (Get-ItemProperty $BtCtKey -Name DisableAbsoluteVolume -ErrorAction SilentlyContinue).DisableAbsoluteVolume }
    if ($cur -eq $want) { Report OK ("Already set (DisableAbsoluteVolume = {0})." -f $want); return }
    if ($CheckOnly) { Report INFO ("WOULD set DisableAbsoluteVolume = {0} (currently {1})." -f $want, $(if ($null -eq $cur) { '<not set>' } else { $cur })); return }
    # after the preview, which only reads and needs no admin rights
    if (-not $Elevated) { Report FAIL 'Requires admin (HKLM write). Re-run this command from an elevated PowerShell window.'; return }
    & reg.exe add $BtCtReg /v DisableAbsoluteVolume /t REG_DWORD /d $want /f | Out-Null
    $rc = $LASTEXITCODE
    # Confirmed by reading the value back. This used to print [OK] "written" and "a REBOOT is
    # required" unconditionally - so when reg.exe had failed, the user rebooted for nothing.
    $now = $null
    if (Test-Path $BtCtKey) { $now = (Get-ItemProperty $BtCtKey -Name DisableAbsoluteVolume -ErrorAction SilentlyContinue).DisableAbsoluteVolume }
    if ($rc -ne 0 -or $now -ne $want) {
        Report FAIL ("Could not set DisableAbsoluteVolume = {0} (reg.exe exit {1}; the value now reads {2}), so a reboot would not change anything." -f $want, $rc, $(if ($null -eq $now) { '<not set>' } else { $now }))
        return
    }
    Report OK ("DisableAbsoluteVolume = {0} written." -f $want)
    Report WARN 'A REBOOT is required for the Bluetooth stack to apply this. After disabling: set the headset hardware volume near max just once, then use Windows sliders.'
}

# ---- OPT-IN: disable audio enhancements per active endpoint ----------------
function Disable-Enhancements {
    Section 'Disable Audio Enhancements on every ACTIVE output (Disable_SysFx = 1)'
    # the preview only reads, so it needs no admin rights
    if (-not $Elevated -and -not $CheckOnly) { Report FAIL 'Requires admin (HKLM value write). Re-run this command from an elevated PowerShell window.'; return }
    $active = @(Get-ActiveRenderEndpoints | Where-Object { $_.State -eq 1 })
    if ($active.Count -eq 0) { Report WARN 'No active render endpoints found.'; return }
    foreach ($ep in $active) {
        # The switch lives in the endpoint's FxProperties key - the effects store, where the
        # Sound control panel writes it. This step used to write it under Properties: the
        # write succeeded, [OK] was printed, and Windows never looked there, so the
        # enhancements stayed on.
        $propsPath = Join-Path $ep.Path 'FxProperties'
        if (-not (Test-Path -LiteralPath $propsPath)) { Report OK ("{0}: no effects are registered for this output, so there is nothing to turn off." -f $ep.Name); continue }
        $cur = (Get-ItemProperty -LiteralPath $propsPath -ErrorAction SilentlyContinue).$SysFxValue
        if ($cur -eq 1) { Report OK ("{0}: enhancements already OFF." -f $ep.Name); continue }
        if ($CheckOnly) { Report INFO ("WOULD set enhancements OFF for: {0}" -f $ep.Name); continue }
        Ensure-BackupDir
        $bk = Join-Path $BackupDir ('endpoint.' + $ep.Guid.Trim('{}') + '.' + (Stamp) + '.reg')
        & reg.exe export ("HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\" + $ep.Guid) $bk /y | Out-Null
        # Backup first, as everywhere else in this kit. The [OK] below used to announce
        # "backup of endpoint key saved" without ever checking that it had been.
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $bk) -or (Get-Item -LiteralPath $bk).Length -eq 0) {
            Report FAIL ("{0}: backup of the endpoint key FAILED (reg.exe exit {1}) - left unchanged." -f $ep.Name, $LASTEXITCODE)
            continue
        }
        try {
            Set-ItemProperty -LiteralPath $propsPath -Name $SysFxValue -Value 1 -Type DWord
            Report OK ("{0}: enhancements set OFF (backup of endpoint key saved)." -f $ep.Name)
        } catch { Report WARN ("{0}: value write failed: {1}. Use the Settings toggle instead (README)." -f $ep.Name, $_.Exception.Message) }
    }
    if (-not $CheckOnly) { Report INFO 'Reboot (or restart the Windows Audio service - which resets live app volumes to 100%) for this to take effect.' }
    Report INFO 'Note: OEM audio apps/services (Nahimic, Dolby, DTS, Waves...) may flip enhancements back on - see README to stop them.'
}

# Every step runs under $ErrorActionPreference = 'Stop'. An error nobody anticipated inside
# one - a folder that cannot be written, a locked file, a cmdlet failing in a new way - used
# to end the WHOLE run with a raw PowerShell error: the remaining steps were skipped and no
# Summary said what had or had not been done. It is now a [FAIL] line naming the step, the
# other steps still run, and the exit code says so. (Parameter names are deliberately
# unusual: PowerShell scoping is dynamic, so the step functions can see them.)
function Invoke-Step([string]$StepName, [scriptblock]$StepBody) {
    try { & $StepBody }
    catch { Report FAIL ("{0} did not finish (line {1}): {2}" -f $StepName, $_.InvocationInfo.ScriptLineNumber, $_.Exception.Message) }
}

# ---- main ------------------------------------------------------------------
Write-Host 'Fix-AudioMixer v2 - Windows 11 per-app volume persistence' -ForegroundColor White

# The valid switches are read from this script's own param() block, so the message cannot
# drift from what is actually accepted.
if ($Unrecognized) {
    $validSwitches = @($MyInvocation.MyCommand.Parameters.Values | Where-Object {
        $_.SwitchParameter -and
        [Management.Automation.PSCmdlet]::CommonParameters -notcontains $_.Name -and
        [Management.Automation.PSCmdlet]::OptionalCommonParameters -notcontains $_.Name
    } | ForEach-Object { '-' + $_.Name })
    Report FAIL ("Unknown argument(s): {0}. Valid switches: {1}." -f ($Unrecognized -join ' '), ($validSwitches -join ' '))
    exit 2
}

# The dispatch below is one if/elseif chain, so passing two action switches would run
# only the first and never tell the user the second was ignored. Names (not the hashes)
# are collected into a real array - a lone hashtable's .Count is its KEY count, not 1.
$exclusive = @(@(
    @{ N = 'Status';                  V = $Status },
    @{ N = 'Revert';                  V = $Revert },
    @{ N = 'RebuildStore';            V = $RebuildStore },
    @{ N = 'CleanGhostEndpoints';     V = $CleanGhostEndpoints },
    @{ N = 'DisableEnhancements';     V = $DisableEnhancements },
    @{ N = 'DisableBtAbsoluteVolume'; V = $DisableBtAbsoluteVolume },
    @{ N = 'EnableBtAbsoluteVolume';  V = $EnableBtAbsoluteVolume }
) | Where-Object { $_.V } | ForEach-Object { $_.N })
if ($exclusive.Count -gt 1) {
    Report FAIL ("Mutually exclusive switches: -{0}. Pick one - only -CheckOnly combines with an action." -f ($exclusive -join ', -'))
    exit 2
}

$modeName = if ($Status) { 'Status/diagnosis' }
    elseif ($Revert) { 'Revert browser flags' }
    elseif ($RebuildStore) { 'Rebuild store' }
    elseif ($CleanGhostEndpoints) { 'Clean ghost endpoints' }
    elseif ($DisableEnhancements) { 'Disable enhancements' }
    elseif ($DisableBtAbsoluteVolume) { 'Disable BT absolute volume' }
    elseif ($EnableBtAbsoluteVolume) { 'Enable BT absolute volume' }
    elseif ($CheckOnly) { 'CheckOnly (no changes)' }
    else { 'Apply core fixes' }
Report INFO ("Mode: {0} | Elevated: {1}" -f $modeName, $Elevated)
if (-not $Elevated -and -not $Status) {
    Report WARN 'Not running as admin - some steps will be skipped. Use Fix-AudioMixer.cmd for full effect.'
}

if     ($Status)                  { Invoke-Step 'Status'                  { Show-Status $true } }
elseif ($Revert)                  { Invoke-Step 'Revert browser flags'    { Fix-Browsers } }
elseif ($RebuildStore)            { Invoke-Step 'Rebuild store'           { Rebuild-Store } }
elseif ($CleanGhostEndpoints)     { Invoke-Step 'Clean ghost endpoints'   { Clean-GhostEndpoints } }
elseif ($DisableEnhancements)     { Invoke-Step 'Disable enhancements'    { Disable-Enhancements } }
elseif ($DisableBtAbsoluteVolume) { Invoke-Step 'Bluetooth absolute volume' { Set-BtAbsoluteVolume $true } }
elseif ($EnableBtAbsoluteVolume)  { Invoke-Step 'Bluetooth absolute volume' { Set-BtAbsoluteVolume $false } }
else {
    Invoke-Step 'BleachBit rule' { Fix-BleachBit }
    Invoke-Step 'Volume store'   { Fix-Store }
    Invoke-Step 'Audio services' { Fix-Services }
    Invoke-Step 'Browser flags'  { Fix-Browsers }
    Invoke-Step 'Status'         { Show-Status $false }
}

Section 'Summary'
Report INFO ("{0} OK, {1} warning(s), {2} failure(s)" -f $script:nOK, $script:nWARN, $script:nFAIL)
if ($modeName -eq 'Apply core fixes' -and -not $CheckOnly) {
    Write-Host ''
    Write-Host "Next: set each app's volume in the mixer, then CLOSE and reopen that app once." -ForegroundColor White
    Write-Host "Windows saves an app's volume when the app closes; after that it survives reboots." -ForegroundColor White
    Write-Host "Test with a NON-browser app first. Volumes are per output device - switching devices shows fresh values by design." -ForegroundColor White
}
if ($script:nFAIL -gt 0) { exit 1 } else { exit 0 }
