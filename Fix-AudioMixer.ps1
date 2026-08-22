#Requires -Version 5.1
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

You can run this script multiple times without issue. It creates backups of any changes it makes, storing them in a "backups" folder next to the script itself.
The script is compatible with different regional settings, as it only uses SIDs, service names, and registry values, and never processes localized command output.

USAGE (run in an elevated PowerShell window, or double-click Fix-AudioMixer.cmd):
`.\Fix-AudioMixer.ps1` Applies core fixes (this is the default behavior).
`.\Fix-AudioMixer.ps1 -CheckOnly` Reports problems without making any changes.
`.\Fix-AudioMixer.ps1 -Status` Provides a complete diagnostic report on the store, Bluetooth, APOs, and endpoints.
`.\Fix-AudioMixer.ps1 -Revert` Removes browser flags that this script previously added.
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
    [switch]$EnableBtAbsoluteVolume
)

$ErrorActionPreference = 'Stop'

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
$BrowserExes  = @('thorium.exe','chrome.exe','msedge.exe','brave.exe','vivaldi.exe','opera.exe','opera_gx.exe','browser.exe')
$BtCtKey      = 'HKLM:\SYSTEM\CurrentControlSet\Control\Bluetooth\Audio\AVRCP\CT'
$BtCtReg      = 'HKLM\SYSTEM\CurrentControlSet\Control\Bluetooth\Audio\AVRCP\CT'
$MMRender     = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render'
$SysFxValue   = '{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5'   # PKEY_AudioEndpoint_Disable_SysFx: 1 = enhancements OFF
$FxSlotFmtid  = 'd04e05a6-594b-4fb6-a80d-01af5eed7d1d'       # PKEY_FX_* effect-chain slots
$ScriptDir    = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$BackupDir    = Join-Path $ScriptDir 'backups'
$Elevated     = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

function Ensure-BackupDir { if (-not (Test-Path $BackupDir)) { New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null } }
function Stamp { (Get-Date).ToString('yyyyMMdd-HHmmss') }

# ---- registry integrity-label helper (the part a .reg file cannot do) -------
if (-not ('RegLabel' -as [type])) {
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
if (-not ('RegDel' -as [type])) {
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
function Get-ExistingStores { @($Stores | Where-Object { Test-Path $_.PS }) }

function Get-LabelText([string]$sub) {
    $l = [RegLabel]::Get($sub)
    if ([string]::IsNullOrWhiteSpace($l) -or $l -eq 'S:') { '(none = Medium)' } else { $l }
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
            if ($dll -and (Test-Path $dll)) { $co = (Get-Item $dll).VersionInfo.CompanyName }
            return [pscustomobject]@{ Clsid = $clsid; Name = $name; Dll = $dll; Company = $co }
        }
    }
    [pscustomobject]@{ Clsid = $clsid; Name = '(unregistered)'; Dll = ''; Company = '' }
}

function Get-Shortcuts {
    $dirs = @(
        [Environment]::GetFolderPath('Desktop'),
        [Environment]::GetFolderPath('CommonDesktopDirectory'),
        (Join-Path $env:APPDATA     'Microsoft\Windows\Start Menu\Programs'),
        (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'),
        (Join-Path $env:APPDATA     'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar')
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique
    if (-not $dirs) { return @() }
    Get-ChildItem -Path $dirs -Filter *.lnk -Recurse -ErrorAction SilentlyContinue
}

# ---- STATUS / DIAGNOSIS ----------------------------------------------------
function Show-Status([bool]$Deep) {
    Section 'Store variants and saved per-app volumes'
    $found = Get-ExistingStores
    if ($found.Count -eq 0) {
        Report WARN 'NO PropertyStore key exists (either variant). Volumes cannot persist. Run the default fix or -RebuildStore.'
    }
    foreach ($s in $found) {
        $k = Get-Item $s.PS
        $lbl = Get-LabelText $s.Sub
        $tag = if ($k.SubKeyCount -gt 0) { 'ACTIVE store' } else { 'empty' }
        Report INFO ("{0}: {1} entries [{2}]  label = {3}" -f $s.Name, $k.SubKeyCount, $tag, $lbl)
        foreach ($sk in ($k.GetSubKeyNames() | Select-Object -First 15)) {
            $def = (Get-ItemProperty (Join-Path $s.PS $sk) -ErrorAction SilentlyContinue).'(default)'
            $app = '(system sounds)'
            if ($def -and $def -notmatch '\|#') {
                $tail = $def.Substring($def.LastIndexOf('\') + 1)
                $cut = $tail.IndexOf('%'); if ($cut -ge 0) { $tail = $tail.Substring(0, $cut) }
                if ($tail) { $app = $tail }
            }
            Write-Host ("      - {0}" -f $app)
        }
    }
    Report INFO 'Reminder: volumes are saved PER OUTPUT DEVICE and when the app CLOSES.'

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
        $props = Get-ItemProperty (Join-Path $ep.Path 'Properties') -ErrorAction SilentlyContinue
        $sysfx = $props.$SysFxValue
        $fxState = if ($sysfx -eq 1) { 'enhancements OFF' } else { 'enhancements ON (default)' }
        Report INFO ("{0}  [{1}]" -f $ep.Name, $fxState)
        $fx = Get-ItemProperty (Join-Path $ep.Path 'FxProperties') -ErrorAction SilentlyContinue
        if ($fx) {
            # match GUID-shaped VALUES in any FxProperties slot: vendors register under
            # several property sets (classic PKEY_FX d04e05a6-..., composite 1f7d339a-..., etc.)
            $clsids = @($fx.PSObject.Properties | Where-Object { $_.Value -is [string] -and $_.Value -match '^\{[0-9A-Fa-f-]{36}\}$' -and $_.Value -ne '{00000000-0000-0000-0000-000000000000}' })
            foreach ($c in $clsids) {
                $r = Resolve-ApoVendor $c.Value
                $who = if ($r.Company) { $r.Company } elseif ($r.Name) { $r.Name } else { $r.Clsid }
                $isMs = ($r.Company -match 'Microsoft') -or ($r.Dll -match '\\Windows\\System32\\(mf|audio|wmalfx)' )
                if ($isMs) { Report INFO ("    APO: {0}  ({1})" -f $who, (Split-Path $r.Dll -Leaf)) }
                else { Report WARN ("    third-party APO: {0}  ({1}) - can interfere; see README (enhancements section)" -f $who, (Split-Path $r.Dll -Leaf)) }
            }
            if ($clsids.Count -eq 0) { Report INFO '    no effect APOs registered' }
        } else { Report INFO '    no FxProperties (no effects)' }
    }

    Section 'Output devices / drivers'
    $stale = @($eps | Where-Object { $_.State -ne 1 })
    Report INFO ("{0} active, {1} inactive/ghost render endpoints registered." -f $active.Count, $stale.Count)
    if ($stale.Count -ge 5) {
        Report WARN 'Many stale endpoints (old drivers / HDMI / virtual devices). Harmless, but see README to tidy up via mmsys.cpl.'
    }
    $virt = @($eps | Where-Object { $_.Name -match 'Virtual|Steam Streaming|CABLE|Voicemeeter' })
    if ($virt.Count -gt 0) {
        Report INFO ("Virtual audio devices present: {0}. Note: per-app volumes/routing are stored per device; see README (Fast Startup note)." -f (@($virt | ForEach-Object { $_.Name }) -join ', '))
    }
    $fastBoot = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' -Name HiberbootEnabled -ErrorAction SilentlyContinue).HiberbootEnabled
    if ($fastBoot -eq 1) { Report INFO 'Fast Startup is ON. If apps forget their ASSIGNED OUTPUT DEVICE after a full shutdown (not restart), turn it off - see README.' }

    if ($Deep -and $Elevated) {
        Section 'IE-mode component (the canonical store lives under its subtree)'
        try {
            $cap = Get-WindowsCapability -Online -Name 'Browser.InternetExplorer*' -ErrorAction Stop
            foreach ($c in $cap) { Report INFO ("{0}: {1}" -f $c.Name, $c.State) }
            if (@($cap | Where-Object { $_.State -eq 'Installed' }).Count -eq 0) {
                Report WARN 'IE-mode component not installed. Community reports link its REMOVAL to a broken volume store; if persistence fails on this machine, consider re-adding it.'
            }
        } catch { Report INFO ("IE-mode component check skipped: {0}" -f $_.Exception.Message) }
    }
}

# ---- FIX 1: BleachBit cleaner rule -----------------------------------------
function Fix-BleachBit {
    Section 'BleachBit "Windows Volume Mixer" cleaner rule (root cause on this machine)'
    $ini = Join-Path $env:APPDATA 'BleachBit\bleachbit.ini'
    if (-not (Test-Path $ini)) { Report INFO 'BleachBit config not found for this user. Nothing to disable.'; return }
    $key   = 'winapp2_windows.windows_volume_mixer'
    $lines = @(Get-Content -LiteralPath $ini)
    $idx = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match ("^\s*" + [regex]::Escape($key) + "\s*=")) { $idx = $i; break }
    }
    if ($idx -lt 0) { Report OK 'Rule not enabled in BleachBit config. Good.'; return }
    $cur = (($lines[$idx] -split '=', 2)[1]).Trim()
    if ($cur -eq 'False') { Report OK 'Rule already disabled (= False).'; return }
    if ($CheckOnly) { Report WARN ("Rule is '{0}' - WOULD set it to False." -f $cur); return }
    Ensure-BackupDir
    Copy-Item -LiteralPath $ini -Destination (Join-Path $BackupDir ('bleachbit.ini.' + (Stamp) + '.bak')) -Force
    $lines[$idx] = "$key = False"
    Set-Content -LiteralPath $ini -Value $lines -Encoding UTF8
    Report OK 'Disabled the "Windows Volume Mixer" cleaner rule (backup saved).'
    Report INFO 'Any other cleaner you use: never let it touch either PropertyStore path (see README).'
}

# ---- FIX 2: store key(s) + low integrity label -----------------------------
function Fix-Store {
    Section 'Per-app volume store: key + low integrity label (both variants probed)'
    $found = Get-ExistingStores
    if ($CheckOnly) {
        if ($found.Count -eq 0) { Report INFO 'no store key exists - WOULD create the canonical one and label it Low' }
        foreach ($s in $found) { Report INFO ("{0}: exists, label = {1}" -f $s.Name, (Get-LabelText $s.Sub)) }
        return
    }
    Ensure-BackupDir
    if ($found.Count -eq 0) {
        Report INFO 'No store key found - creating the canonical path and applying the Low label.'
        & reg.exe add $Stores[0].Reg /f | Out-Null
        $found = Get-ExistingStores
    }
    foreach ($s in $found) {
        $k = Get-Item $s.PS
        if ($k.SubKeyCount -gt 0) { & reg.exe export $s.Reg (Join-Path $BackupDir ('PropertyStore.' + ($s.Name -replace '[^A-Za-z]','') + '.' + (Stamp) + '.reg')) /y | Out-Null }
        $label = [RegLabel]::Get($s.Sub)
        if ($label -like '*;LW*') {
            Report OK ("{0}: low-integrity label OK ({1})" -f $s.Name, $label)
        } else {
            $r = [RegLabel]::SetLow($s.Sub)
            if ($r -eq 'OK') { Report OK ("{0}: applied Low label. Now: {1}" -f $s.Name, [RegLabel]::Get($s.Sub)) }
            else { Report WARN ("{0}: could not set Low label ({1}). Alternative: run -RebuildStore so Windows recreates it natively." -f $s.Name, $r) }
        }
    }
}

# ---- OPT-IN: rebuild the store natively ------------------------------------
function Rebuild-Store {
    Section 'REBUILD store: delete + let the Windows Audio service recreate it'
    Report INFO 'This is the "let Windows do it" repair. WARNING: all RUNNING apps reset to 100% when the audio service restarts.'
    if (-not $Elevated) { Report FAIL 'Requires admin (service restart). Re-run elevated.'; return }
    $found = Get-ExistingStores
    if ($CheckOnly) { Report INFO ("WOULD: backup + delete {0} store(s), restart Audiosrv/AudioEndpointBuilder, let Windows rebuild." -f $found.Count); return }
    Ensure-BackupDir
    foreach ($s in $found) { & reg.exe export $s.Reg (Join-Path $BackupDir ('PropertyStore.' + ($s.Name -replace '[^A-Za-z]','') + '.' + (Stamp) + '.reg')) /y | Out-Null }
    try {
        Stop-Service -Name Audiosrv -Force -ErrorAction Stop
        Stop-Service -Name AudioEndpointBuilder -Force -ErrorAction Stop
    } catch { Report FAIL ("Could not stop audio services: {0}" -f $_.Exception.Message); return }
    foreach ($s in $found) { & reg.exe delete $s.Reg /f | Out-Null }
    Start-Service -Name AudioEndpointBuilder
    Start-Service -Name Audiosrv
    Start-Sleep -Seconds 2
    # Empirical note (verified on my machine, build 26100): the audio service does
    # NOT reliably re-create the store chain by itself - it only writes into an
    # existing one (and ignores the Multimedia skeleton while the canonical path is
    # missing). So if the store has not reappeared, create + label it explicitly -
    # the method is verified working end-to-end on my machine.
    if ((Get-ExistingStores).Count -eq 0) {
        & reg.exe add $Stores[0].Reg /f | Out-Null
        $r = [RegLabel]::SetLow($Stores[0].Sub)
        if ($r -eq 'OK') { Report OK 'Service did not recreate the store; created + Low-labeled it explicitly (the verified method).' }
        else { Report FAIL ("Store still missing and label set failed ({0}). Re-run the default fix." -f $r) }
    } else {
        Report OK 'Store deleted and audio service restarted; store present again.'
    }
    Report INFO 'Old entries are gone (that is the point - clean slate). Set a volume in a NON-browser app, close it, reopen - it should stick.'
}

# ---- OPT-IN: remove ghost (NOTPRESENT) audio endpoints ---------------------
function Clean-GhostEndpoints {
    Section 'Remove ghost (NOTPRESENT) audio endpoints'
    Report INFO 'Ghosts are leftover registry entries for devices no longer present. Removing them is cosmetic (tidies the mixer/device list).'
    if (-not $Elevated) { Report FAIL 'Requires admin - these keys are TrustedInstaller-owned. Re-run elevated (Fix-AudioMixer.cmd).'; return }
    $roots = @(
        @{ Flow = 'Render';  Path = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render';  RegBase = 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render' },
        @{ Flow = 'Capture'; Path = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture'; RegBase = 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture' }
    )
    $targets = @()
    foreach ($r in $roots) {
        foreach ($e in (Get-ChildItem $r.Path -ErrorAction SilentlyContinue)) {
            $st = (Get-ItemProperty $e.PSPath -Name DeviceState -ErrorAction SilentlyContinue).DeviceState
            if ([int]$st -eq 4) {   # 4 = DEVICE_STATE_NOTPRESENT
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
    Report INFO 'HDMI / NVIDIA / USB / Bluetooth endpoints reappear when that output is next connected - that is normal.'
    if ($CheckOnly) { Report INFO 'CheckOnly - not removing.'; return }
    Ensure-BackupDir
    $bk = Join-Path $BackupDir ('MMDevices-Audio.' + (Stamp) + '.reg')
    & reg.exe export 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio' $bk /y | Out-Null
    Report OK ("Backed up MMDevices\Audio -> {0}" -f (Split-Path $bk -Leaf))
    $audiosrvWasRunning = (Get-Service Audiosrv -ErrorAction SilentlyContinue).Status -eq 'Running'
    try { Stop-Service Audiosrv -Force -ErrorAction Stop; Stop-Service AudioEndpointBuilder -Force -ErrorAction Stop }
    catch { Report WARN ("Could not stop audio services (continuing anyway): {0}" -f $_.Exception.Message) }
    $done = 0
    foreach ($t in $targets) {
        $st = (Get-ItemProperty $t.PS -Name DeviceState -ErrorAction SilentlyContinue).DeviceState
        if ([int]$st -ne 4) { Report WARN ("skip (state changed): {0}" -f $t.Name); continue }
        $r = [RegDel]::Delete($t.Sub)
        if (Test-Path $t.PS) { & reg.exe delete $t.Reg /f 2>$null | Out-Null }   # fallback if an empty key remained
        if (-not (Test-Path $t.PS)) { $done++; Report OK ("removed: [{0}] {1}" -f $t.Flow, $t.Name) }
        else { Report WARN ("could not remove {0} ({1})" -f $t.Name, $r) }
    }
    Start-Service AudioEndpointBuilder -ErrorAction SilentlyContinue
    if ($audiosrvWasRunning) { Start-Service Audiosrv -ErrorAction SilentlyContinue }
    Report OK ("Removed {0} of {1} ghost endpoint(s); audio services restarted." -f $done, $targets.Count)
    Report INFO 'To restore, import the MMDevices-Audio backup .reg from the backups folder.'
}

# ---- FIX 3: audio services -------------------------------------------------
function Fix-Services {
    Section 'Audio services (Audiosrv, AudioEndpointBuilder)'
    foreach ($svc in 'Audiosrv','AudioEndpointBuilder') {
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
        } catch { Report FAIL ("{0} not found: {1}" -f $svc, $_.Exception.Message) }
    }
}

# ---- FIX 4 / REVERT: Chromium browser launch flags -------------------------
function Fix-Browsers {
    Section 'Chromium browser launch flags (volume memory inside the browser)'
    $lnks = @(Get-Shortcuts)
    if ($lnks.Count -eq 0) { Report INFO 'No .lnk shortcuts found in the usual locations.'; return }
    $wsh = New-Object -ComObject WScript.Shell
    $touched = 0; $matched = 0
    foreach ($lnk in $lnks) {
        try {
            $sc  = $wsh.CreateShortcut($lnk.FullName)
            $exe = if ($sc.TargetPath) { (Split-Path $sc.TargetPath -Leaf).ToLower() } else { '' }
            if (-not $exe -or ($BrowserExes -notcontains $exe)) { continue }
            $matched++
            $has = $sc.Arguments -match 'AudioServiceSandbox'
            if ($Revert) {
                if (-not $has) { continue }
                if ($CheckOnly) { Report INFO ("WOULD remove flags from: {0}" -f $lnk.Name); continue }
                Ensure-BackupDir
                Copy-Item $lnk.FullName (Join-Path $BackupDir ($lnk.Name + '.' + (Stamp) + '.bak')) -Force
                $sc.Arguments = (($sc.Arguments -replace [regex]::Escape($BrowserFlags), '') -replace '\s+', ' ').Trim()
                $sc.Save(); $touched++
                Report OK ("Removed flags: {0}" -f $lnk.Name)
                continue
            }
            if ($has) { Report OK ("{0} - flags already present." -f $lnk.Name); continue }
            if ($CheckOnly) { Report INFO ("WOULD add flags to: {0} ({1})" -f $lnk.Name, $exe); continue }
            Ensure-BackupDir
            Copy-Item $lnk.FullName (Join-Path $BackupDir ($lnk.Name + '.' + (Stamp) + '.bak')) -Force
            $sc.Arguments = ("$($sc.Arguments) $BrowserFlags").Trim()
            $sc.Save(); $touched++
            Report OK ("Added flags: {0} ({1})" -f $lnk.Name, $exe)
        } catch { Report WARN ("Could not process {0}: {1}" -f $lnk.Name, $_.Exception.Message) }
    }
    if ($matched -eq 0) { Report INFO 'No Chromium-based browser shortcuts found.' }
    Report INFO 'Taskbar pins may need re-pinning. Browser must be fully closed before relaunch. Trade-off: slightly weaker audio sandbox.'
}

# ---- OPT-IN: Bluetooth absolute volume toggles -----------------------------
function Set-BtAbsoluteVolume([bool]$Disable) {
    Section ("Bluetooth absolute volume: " + $(if ($Disable) { 'DISABLE' } else { 'ENABLE (restore default)' }))
    if (-not $Elevated) { Report FAIL 'Requires admin (HKLM write). Re-run elevated.'; return }
    $want = if ($Disable) { 1 } else { 0 }
    $cur = $null
    if (Test-Path $BtCtKey) { $cur = (Get-ItemProperty $BtCtKey -Name DisableAbsoluteVolume -ErrorAction SilentlyContinue).DisableAbsoluteVolume }
    if ($cur -eq $want) { Report OK ("Already set (DisableAbsoluteVolume = {0})." -f $want); return }
    if ($CheckOnly) { Report INFO ("WOULD set DisableAbsoluteVolume = {0} (currently {1})." -f $want, $(if ($null -eq $cur) { '<not set>' } else { $cur })); return }
    & reg.exe add $BtCtReg /v DisableAbsoluteVolume /t REG_DWORD /d $want /f | Out-Null
    Report OK ("DisableAbsoluteVolume = {0} written." -f $want)
    Report WARN 'A REBOOT is required for the Bluetooth stack to apply this. After disabling: set the headset hardware volume near max just once, then use Windows sliders.'
}

# ---- OPT-IN: disable audio enhancements per active endpoint ----------------
function Disable-Enhancements {
    Section 'Disable Audio Enhancements on every ACTIVE output (Disable_SysFx = 1)'
    if (-not $Elevated) { Report FAIL 'Requires admin (HKLM value write). Re-run elevated.'; return }
    $active = @(Get-ActiveRenderEndpoints | Where-Object { $_.State -eq 1 })
    if ($active.Count -eq 0) { Report WARN 'No active render endpoints found.'; return }
    foreach ($ep in $active) {
        $propsPath = Join-Path $ep.Path 'Properties'
        $cur = (Get-ItemProperty $propsPath -ErrorAction SilentlyContinue).$SysFxValue
        if ($cur -eq 1) { Report OK ("{0}: enhancements already OFF." -f $ep.Name); continue }
        if ($CheckOnly) { Report INFO ("WOULD set enhancements OFF for: {0}" -f $ep.Name); continue }
        Ensure-BackupDir
        & reg.exe export ("HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\" + $ep.Guid) (Join-Path $BackupDir ('endpoint.' + $ep.Guid.Trim('{}') + '.' + (Stamp) + '.reg')) /y | Out-Null
        try {
            Set-ItemProperty -Path $propsPath -Name $SysFxValue -Value 1 -Type DWord
            Report OK ("{0}: enhancements set OFF (backup of endpoint key saved)." -f $ep.Name)
        } catch { Report WARN ("{0}: value write failed: {1}. Use the Settings toggle instead (README)." -f $ep.Name, $_.Exception.Message) }
    }
    if (-not $CheckOnly) { Report INFO 'Reboot (or restart the Windows Audio service - which resets live app volumes to 100%) for this to take effect.' }
    Report INFO 'Note: OEM audio apps/services (Nahimic, Dolby, DTS, Waves...) may flip enhancements back on - see README to stop them.'
}

# ---- main ------------------------------------------------------------------
Write-Host 'Fix-AudioMixer v2 - Windows 11 per-app volume persistence' -ForegroundColor White
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

if     ($Status)                { Show-Status $true }
elseif ($Revert)                { Fix-Browsers }
elseif ($RebuildStore)          { Rebuild-Store }
elseif ($CleanGhostEndpoints)   { Clean-GhostEndpoints }
elseif ($DisableEnhancements)   { Disable-Enhancements }
elseif ($DisableBtAbsoluteVolume) { Set-BtAbsoluteVolume $true }
elseif ($EnableBtAbsoluteVolume)  { Set-BtAbsoluteVolume $false }
else {
    Fix-BleachBit
    Fix-Store
    Fix-Services
    Fix-Browsers
    Show-Status $false
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
