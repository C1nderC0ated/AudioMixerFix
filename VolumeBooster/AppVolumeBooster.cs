// =====================================================================================
// AppVolumeBooster.cs - boost application volume past 100% (up to 500%).
//
// Part of the AudioMixerFix kit (optional add-on). Single source file, builds with the
// in-box .NET Framework compiler - no SDK, no NuGet, no admin, no drivers:
//   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /t:winexe
//       /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll
//       AppVolumeBooster.cs
//
// HOW IT WORKS (duck-and-boost relay, verified end-to-end on Win11 24H2 26100.9168):
//   1. Windows' process-loopback capture API (ActivateAudioInterfaceAsync +
//      VAD\Process_Loopback, the API OBS uses for per-app audio capture) captures the
//      chosen app(s). On this build the tap is post-session-volume.
//   2. Each target's Volume Mixer slider is therefore ducked to 4% (near-silent direct
//      path), and the captured signal is multiplied by boost/0.04 (float32, lossless),
//      then mixed (if more than one target) and re-rendered to the default output.
//      Direct-path bleed sits 28-34 dB below the boosted copy - inaudible.
//   3. A soft clipper above 0.95 full-scale prevents harsh digital clipping.
//   4. On stop / last target exit / booster exit the mixer sliders are restored. A
//      state file next to the exe self-heals volumes if the booster was killed hard.
//
// MODES
//   - One or more applications (checkboxes): INCLUDE-tree loopback per selected
//     process tree, mixed into a single boosted output. Other apps stay untouched.
//   - Optional Windows system sounds: ducked like an app and captured via pid-0
//     loopback when the OS allows it. Windows does not expose a real process for
//     that mixer entry; if activation fails, use "boost all audio" instead.
//   - Boost all audio on this device: EXCLUDE-tree loopback of this booster process
//     (everything else, including system sounds) and duck every other session.
//
// CLI (for scripting/testing; the GUI appears when run with no args):
//   AppVolumeBooster.exe --pid <N> [--pid <N> ...] | --name <exe> [--name <exe> ...]
//       | --all  [--system-sounds] [--boost 100..500] [--seconds <S>] [--log <file>]
// =====================================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace AppVolumeBoosterNs
{
    // ================================ WASAPI interop =================================
    internal static class K
    {
        public const int CLSCTX_ALL = 0x17;
        public const int eRender = 0;
        public const int eMultimedia = 1;
        public const uint SHARED = 0;
        public const uint LOOPBACK = 0x00020000;
        public const uint EVENTCB = 0x00040000;
        public const uint AUTOCONVERT = 0x80000000;
        public const uint SRC_QUALITY = 0x08000000;
        public const uint BUF_SILENT = 2;
        public const int StateActive = 1, StateExpired = 2;
        public const string SysSoundsName = "#SystemSounds";
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WaveFormatEx
    {
        public ushort wFormatTag, nChannels;
        public uint nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
        public static WaveFormatEx Float32Stereo48k()
        {
            WaveFormatEx f = new WaveFormatEx();
            f.wFormatTag = 3; f.nChannels = 2; f.nSamplesPerSec = 48000;
            f.wBitsPerSample = 32; f.nBlockAlign = 8; f.nAvgBytesPerSec = 384000; f.cbSize = 0;
            return f;
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        int OpenPropertyStore(int access, out IntPtr props);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out int state);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        int Initialize(uint shareMode, uint streamFlags, long bufferDuration, long periodicity, ref WaveFormatEx format, IntPtr sessionGuid);
        int GetBufferSize(out uint frames);
        int GetStreamLatency(out long latency);
        int GetCurrentPadding(out uint padding);
        int IsFormatSupported(uint shareMode, ref WaveFormatEx format, out IntPtr closest);
        int GetMixFormat(out IntPtr format);
        int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
        int Start();
        int Stop();
        int Reset();
        int SetEventHandle(IntPtr handle);
        int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devPos, out ulong qpcPos);
        int ReleaseBuffer(uint frames);
        int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioRenderClient
    {
        int GetBuffer(uint frames, out IntPtr data);
        int ReleaseBuffer(uint frames, uint flags);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        int GetAudioSessionControl(IntPtr sessionGuid, uint streamFlags, out IAudioSessionControl sessionControl);
        int GetSimpleAudioVolume(IntPtr sessionGuid, uint streamFlags, out ISimpleAudioVolume audioVolume);
        int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
        int RegisterSessionNotification(IAudioSessionNotification n);
        int UnregisterSessionNotification(IAudioSessionNotification n);
        int RegisterDuckNotification(IntPtr s, IntPtr n);
        int UnregisterDuckNotification(IntPtr n);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        int GetCount(out int count);
        int GetSession(int index, out IAudioSessionControl session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl
    {
        int GetState(out int state);
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr ctx);
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr ctx);
        int GetGroupingParam(out Guid g);
        int SetGroupingParam(ref Guid g, IntPtr ctx);
        int RegisterAudioSessionNotification(IntPtr e);
        int UnregisterAudioSessionNotification(IntPtr e);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        int GetState(out int state);
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr ctx);
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr ctx);
        int GetGroupingParam(out Guid g);
        int SetGroupingParam(ref Guid g, IntPtr ctx);
        int RegisterAudioSessionNotification(IntPtr e);
        int UnregisterAudioSessionNotification(IntPtr e);
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetProcessId(out uint pid);
        int IsSystemSoundsSession();
        int SetDuckingPreference(bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISimpleAudioVolume
    {
        int SetMasterVolume(float level, IntPtr ctx);
        int GetMasterVolume(out float level);
        int SetMute(bool mute, IntPtr ctx);
        int GetMute(out bool mute);
    }

    [ComImport, Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionNotification
    {
        int OnSessionCreated(IAudioSessionControl newSession);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceAsyncOperation
    {
        int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceCompletionHandler
    {
        int ActivateCompleted(IActivateAudioInterfaceAsyncOperation op);
    }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAgileObject { }

    [ComVisible(true)]
    internal class ActivateCompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public ManualResetEvent Done = new ManualResetEvent(false);
        public int ActivateHr;
        public object Activated;
        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            int hr; object obj;
            int rc = op.GetActivateResult(out hr, out obj);
            ActivateHr = (rc != 0) ? rc : hr;
            Activated = obj;
            Done.Set();
            return 0;
        }
    }

    [ComVisible(true)]
    internal class SessionCreatedHandler : IAudioSessionNotification, IAgileObject
    {
        public delegate void SessionFn(IAudioSessionControl sc);
        public SessionFn Fn;
        public int OnSessionCreated(IAudioSessionControl newSession)
        {
            try { if (Fn != null) Fn(newSession); }
            catch { }
            return 0;
        }
    }

    internal static class Native
    {
        [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
        public static extern void ActivateAudioInterfaceAsync(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            ref Guid riid, IntPtr activationParams,
            IActivateAudioInterfaceCompletionHandler completionHandler,
            out IActivateAudioInterfaceAsyncOperation activationOperation);

        [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

        [DllImport("shcore.dll")]
        public static extern int SetProcessDpiAwareness(int value);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        public static void UseDarkTitleBar(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            int on = 1;
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Win10 20H1+); 19 = older 1809 attribute
            if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref on, 4);
        }

        public static void UseDarkExplorerTheme(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try { SetWindowTheme(hwnd, "DarkMode_Explorer", null); }
            catch { }
        }

        public static readonly Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        public static readonly Guid IID_IAudioSessionManager2 = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
        public static readonly Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
        public static readonly Guid IID_IAudioRenderClient = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
        public static readonly Guid IID_ISimpleAudioVolume = new Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8");

        public static void Check(int hr, string what)
        {
            if (hr != 0) throw new COMException(what + " failed (hr=0x" + hr.ToString("X8") + ")", hr);
        }

        // WASAPI interfaces have no marshaling proxy: an RCW created on an MTA thread
        // must never be called from an STA thread (E_NOINTERFACE). The GUI runs STA,
        // so every engine COM operation is funneled through a joined MTA thread.
        public static void RunMta(ThreadStart work)
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA) { work(); return; }
            Exception err = null;
            Thread t = new Thread(delegate() { try { work(); } catch (Exception ex) { err = ex; } });
            t.SetApartmentState(ApartmentState.MTA);
            t.IsBackground = true;
            t.Start();
            t.Join();
            if (err != null) throw err;
        }

        // AUDIOCLIENT_ACTIVATION_PARAMS { PROCESS_LOOPBACK, { pid, INCLUDE=0 or EXCLUDE=1 } }
        public static IAudioClient ActivateProcessLoopback(uint pid, bool excludeTree)
        {
            IAudioClient result = null;
            Exception err = null;
            Thread t = new Thread(delegate()
            {
                try
                {
                    IntPtr blob = Marshal.AllocHGlobal(12);
                    Marshal.WriteInt32(blob, 0, 1);
                    Marshal.WriteInt32(blob, 4, (int)pid);
                    Marshal.WriteInt32(blob, 8, excludeTree ? 1 : 0);
                    IntPtr pv = Marshal.AllocHGlobal(24);
                    for (int i = 0; i < 24; i += 8) Marshal.WriteInt64(pv, i, 0);
                    Marshal.WriteInt16(pv, 0, (short)65); // VT_BLOB
                    Marshal.WriteInt32(pv, 8, 12);
                    Marshal.WriteIntPtr(pv, 16, blob);
                    try
                    {
                        ActivateCompletionHandler handler = new ActivateCompletionHandler();
                        Guid iid = IID_IAudioClient;
                        IActivateAudioInterfaceAsyncOperation op;
                        ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, pv, handler, out op);
                        if (!handler.Done.WaitOne(5000)) throw new TimeoutException("audio activation timed out");
                        Check(handler.ActivateHr, "process-loopback activation");
                        result = (IAudioClient)handler.Activated;
                        GC.KeepAlive(op);
                    }
                    finally { Marshal.FreeHGlobal(pv); Marshal.FreeHGlobal(blob); }
                }
                catch (Exception ex) { err = ex; }
            });
            t.SetApartmentState(ApartmentState.MTA);
            t.IsBackground = true;
            t.Start();
            t.Join();
            if (err != null) throw err;
            return result;
        }

        public static IMMDeviceEnumerator Enumerator()
        {
            return (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        }

        public static IMMDevice DefaultRenderDevice()
        {
            IMMDevice dev;
            Check(Enumerator().GetDefaultAudioEndpoint(K.eRender, K.eMultimedia, out dev), "GetDefaultAudioEndpoint");
            return dev;
        }

        public static string DefaultRenderDeviceId()
        {
            try
            {
                IMMDevice dev = DefaultRenderDevice();
                string id; dev.GetId(out id);
                return id;
            }
            catch { return null; }
        }

        public static IAudioClient ActivateClient(IMMDevice dev)
        {
            Guid iid = IID_IAudioClient; object o;
            Check(dev.Activate(ref iid, K.CLSCTX_ALL, IntPtr.Zero, out o), "Activate(IAudioClient)");
            return (IAudioClient)o;
        }

        public static IAudioSessionManager2 SessionManager(IMMDevice dev)
        {
            Guid iid = IID_IAudioSessionManager2; object o;
            Check(dev.Activate(ref iid, K.CLSCTX_ALL, IntPtr.Zero, out o), "Activate(IAudioSessionManager2)");
            return (IAudioSessionManager2)o;
        }

        public static string ProcessNameOf(uint pid)
        {
            if (pid == 0) return null;
            try { return Process.GetProcessById((int)pid).ProcessName; }
            catch { return null; }
        }

        public static bool IsBoosterName(string name)
        {
            return name != null && name.IndexOf("AppVolumeBooster", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsSystemSounds(IAudioSessionControl2 sc2)
        {
            try { return sc2.IsSystemSoundsSession() == 0; }
            catch { return false; }
        }

        public static float SoftClip(float s)
        {
            if (s > 0.95f) return 0.95f + 0.05f * (float)Math.Tanh((s - 0.95f) / 0.05f);
            if (s < -0.95f) return -0.95f - 0.05f * (float)Math.Tanh((-s - 0.95f) / 0.05f);
            return s;
        }
    }

    // =============================== sample ring (one per capture) ===================
    internal class SampleRing
    {
        readonly object gate = new object();
        float[] ring = new float[48000 * 2 * 2]; // 2 s stereo
        int rHead, rTail, rCount;

        public void Push(float[] data, int n)
        {
            lock (gate)
            {
                for (int i = 0; i < n; i++)
                {
                    if (rCount == ring.Length) { rTail = (rTail + 1) % ring.Length; rCount--; }
                    ring[rHead] = data[i]; rHead = (rHead + 1) % ring.Length; rCount++;
                }
            }
        }

        public int Pop(float[] dst, int n)
        {
            lock (gate)
            {
                int take = Math.Min(n, rCount);
                take -= take % 2;
                for (int i = 0; i < take; i++) { dst[i] = ring[rTail]; rTail = (rTail + 1) % ring.Length; }
                rCount -= take;
                return take;
            }
        }
    }

    // =============================== state file (self-heal) ==========================
    // One line per ducked target: <boosterPid>|<targetExeName>|<priorVolume>
    // System sounds use the synthetic name #SystemSounds.
    // If the booster dies without restoring a ducked slider, the next run of any
    // booster instance restores it (only lines whose boosterPid is no longer alive).
    internal static class StateFile
    {
        static string PathOf()
        {
            string dir;
            try { dir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location); }
            catch { dir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); }
            return System.IO.Path.Combine(dir, "booster-state.txt");
        }

        static Mutex Mtx()
        {
            return new Mutex(false, "AppVolumeBooster.StateFile");
        }

        static string OwnPid()
        {
            return Process.GetCurrentProcess().Id.ToString();
        }

        static bool SameTarget(string stored, string targetExe)
        {
            return string.Equals(stored, targetExe, StringComparison.OrdinalIgnoreCase);
        }

        public static void AddOwn(string targetExe, float prior)
        {
            Mutex m = Mtx();
            try
            {
                m.WaitOne(3000);
                List<string> lines = ReadAll();
                string own = OwnPid();
                lines.RemoveAll(delegate(string l)
                {
                    string[] parts = l.Split('|');
                    return parts.Length == 3 && parts[0] == own && SameTarget(parts[1], targetExe);
                });
                lines.Add(own + "|" + targetExe + "|" + prior.ToString("F4", CultureInfo.InvariantCulture));
                File.WriteAllLines(PathOf(), lines.ToArray());
            }
            catch { }
            finally { try { m.ReleaseMutex(); } catch { } m.Close(); }
        }

        public static void RemoveOwn()
        {
            Mutex m = Mtx();
            try
            {
                m.WaitOne(3000);
                List<string> lines = ReadAll();
                string own = OwnPid();
                lines.RemoveAll(delegate(string l) { return l.StartsWith(own + "|"); });
                if (lines.Count == 0) { try { File.Delete(PathOf()); } catch { } }
                else File.WriteAllLines(PathOf(), lines.ToArray());
            }
            catch { }
            finally { try { m.ReleaseMutex(); } catch { } m.Close(); }
        }

        public static void RemoveTarget(string targetExe)
        {
            Mutex m = Mtx();
            try
            {
                m.WaitOne(3000);
                List<string> lines = ReadAll();
                string own = OwnPid();
                lines.RemoveAll(delegate(string l)
                {
                    string[] parts = l.Split('|');
                    return parts.Length == 3 && parts[0] == own && SameTarget(parts[1], targetExe);
                });
                if (lines.Count == 0) { try { File.Delete(PathOf()); } catch { } }
                else File.WriteAllLines(PathOf(), lines.ToArray());
            }
            catch { }
            finally { try { m.ReleaseMutex(); } catch { } m.Close(); }
        }

        // Mark matching own lines as orphaned (boosterPid 0) so any later SelfHeal
        // keeps trying to restore the slider until the app is seen again.
        public static void OrphanOwn()
        {
            RewriteOwnPrefix("0");
        }

        public static void OrphanTarget(string targetExe)
        {
            Mutex m = Mtx();
            try
            {
                m.WaitOne(3000);
                List<string> lines = ReadAll();
                string own = OwnPid();
                for (int i = 0; i < lines.Count; i++)
                {
                    string[] parts = lines[i].Split('|');
                    if (parts.Length == 3 && parts[0] == own && SameTarget(parts[1], targetExe))
                        lines[i] = "0" + lines[i].Substring(own.Length);
                }
                File.WriteAllLines(PathOf(), lines.ToArray());
            }
            catch { }
            finally { try { m.ReleaseMutex(); } catch { } m.Close(); }
        }

        static void RewriteOwnPrefix(string newPid)
        {
            Mutex m = Mtx();
            try
            {
                m.WaitOne(3000);
                List<string> lines = ReadAll();
                string own = OwnPid();
                for (int i = 0; i < lines.Count; i++)
                    if (lines[i].StartsWith(own + "|")) lines[i] = newPid + lines[i].Substring(own.Length);
                File.WriteAllLines(PathOf(), lines.ToArray());
            }
            catch { }
            finally { try { m.ReleaseMutex(); } catch { } m.Close(); }
        }

        static List<string> ReadAll()
        {
            List<string> r = new List<string>();
            try { if (File.Exists(PathOf())) r.AddRange(File.ReadAllLines(PathOf())); } catch { }
            return r;
        }

        static bool LineMatchesSession(string stored, uint spid, string sname, bool isSys)
        {
            if (string.Equals(stored, K.SysSoundsName, StringComparison.OrdinalIgnoreCase))
                return isSys || spid == 0;
            if (sname == null) return false;
            return string.Equals(sname + ".exe", stored, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(sname, stored, StringComparison.OrdinalIgnoreCase);
        }

        // Restore mixer sliders left ducked by a dead (or orphan-marked) booster line.
        // Lines whose app is not currently visible are KEPT so a later run can heal
        // them once the app reappears; lines whose app is seen healthy are resolved.
        public static string SelfHeal()
        {
            Mutex m = Mtx();
            List<string> healedNames = new List<string>();
            try
            {
                m.WaitOne(3000);
                List<string> lines = ReadAll();
                if (lines.Count == 0) return null;
                List<string> keep = new List<string>();
                List<string[]> dead = new List<string[]>();
                foreach (string l in lines)
                {
                    string[] parts = l.Split('|');
                    if (parts.Length != 3) continue;
                    int bpid;
                    bool alive = false;
                    if (int.TryParse(parts[0], out bpid) && bpid > 0)
                    {
                        try { Process p = Process.GetProcessById(bpid); alive = Native.IsBoosterName(p.ProcessName); }
                        catch { alive = false; }
                    }
                    if (alive) keep.Add(l); else dead.Add(parts);
                }
                if (dead.Count > 0)
                {
                    bool[] resolved = new bool[dead.Count];
                    try
                    {
                        IMMDevice dev = Native.DefaultRenderDevice();
                        IAudioSessionManager2 mgr = Native.SessionManager(dev);
                        IAudioSessionEnumerator en; Native.Check(mgr.GetSessionEnumerator(out en), "sessions");
                        int count; en.GetCount(out count);
                        for (int i = 0; i < count; i++)
                        {
                            IAudioSessionControl sc; if (en.GetSession(i, out sc) != 0) continue;
                            IAudioSessionControl2 sc2 = (IAudioSessionControl2)sc;
                            uint spid; sc2.GetProcessId(out spid);
                            string sname = Native.ProcessNameOf(spid);
                            bool isSys = Native.IsSystemSounds(sc2) || spid == 0;
                            for (int di = 0; di < dead.Count; di++)
                            {
                                if (resolved[di]) continue;
                                string[] parts = dead[di];
                                if (!LineMatchesSession(parts[1], spid, sname, isSys)) continue;
                                ISimpleAudioVolume v = (ISimpleAudioVolume)sc;
                                float cur; v.GetMasterVolume(out cur);
                                if (cur <= BoostEngine.DUCK + 0.01f)
                                {
                                    float prior;
                                    if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out prior)) prior = 1.0f;
                                    v.SetMasterVolume(prior, IntPtr.Zero);
                                    if (!healedNames.Contains(parts[1])) healedNames.Add(parts[1]);
                                }
                                resolved[di] = true;
                            }
                        }
                    }
                    catch { }
                    for (int di = 0; di < dead.Count; di++)
                        if (!resolved[di]) keep.Add(string.Join("|", dead[di]));
                }
                if (keep.Count == 0) { try { File.Delete(PathOf()); } catch { } }
                else File.WriteAllLines(PathOf(), keep.ToArray());
            }
            catch { }
            finally { try { m.ReleaseMutex(); } catch { } m.Close(); }
            if (healedNames.Count == 0) return null;
            for (int i = 0; i < healedNames.Count; i++)
                if (healedNames[i] == K.SysSoundsName) healedNames[i] = "system sounds";
            return string.Join(", ", healedNames.ToArray());
        }
    }

    // ================================== boost engine =================================
    internal class BoostEngine
    {
        public const float DUCK = 0.04f;      // ducked mixer level of a target while boosting
        public const float MAXBOOST = 5.0f;   // 500%

        readonly bool boostAll;
        readonly bool wantSystemSounds;
        readonly uint selfPid;
        volatile float gain;                  // boost / DUCK
        float boost;

        readonly object targetLock = new object();
        readonly List<uint> targetPids = new List<uint>();
        readonly Dictionary<uint, HashSet<uint>> treesByRoot = new Dictionary<uint, HashSet<uint>>();
        readonly List<Process> watched = new List<Process>();

        readonly List<IAudioClient> capClients = new List<IAudioClient>();
        readonly List<AutoResetEvent> capEvs = new List<AutoResetEvent>();
        readonly List<Thread> capThreads = new List<Thread>();
        readonly List<SampleRing> mixRings = new List<SampleRing>();

        volatile bool running;
        volatile int targetPadFrames = 2400;  // standing render queue: ~50 ms; grows on underrun
        Thread renThread;
        System.Threading.Timer watcher;
        IAudioClient renClient;
        AutoResetEvent renEv;
        IAudioSessionManager2 liveMgr;
        SessionCreatedHandler sessionNote;
        string devId;
        volatile bool sysCapOk;
        volatile bool anyTargetGone;
        HashSet<uint> wantPidsSnapshot = new HashSet<uint>();

        class Ducked
        {
            public ISimpleAudioVolume Vol;
            public float Prior;
            public uint Pid;
            public string Exe;
            public string InstanceId;
        }
        readonly List<Ducked> ducked = new List<Ducked>();
        readonly object duckLock = new object();

        public long CapSamples, RenFrames, Glitches;
        public volatile bool Stopped = true;
        public string StopReason = "";
        public string StartWarning = "";
        public event EventHandler StoppedEvent;

        public BoostEngine(IList<uint> pids, int boostPercent, bool allAudio, bool systemSounds)
        {
            boostAll = allAudio;
            wantSystemSounds = systemSounds && !allAudio;
            selfPid = (uint)Process.GetCurrentProcess().Id;
            if (!boostAll)
            {
                if (pids != null)
                {
                    foreach (uint pid in pids)
                    {
                        if (pid == 0 || pid == selfPid) continue;
                        if (!targetPids.Contains(pid)) targetPids.Add(pid);
                    }
                }
                if (targetPids.Count == 0 && !wantSystemSounds)
                    throw new ArgumentException("pick at least one application, system sounds, or all audio");
                foreach (uint pid in targetPids)
                    if (Native.ProcessNameOf(pid) == null)
                        throw new ArgumentException("process " + pid + " is not running");
            }
            SetBoost(boostPercent);
        }

        public void SetBoost(int percent)
        {
            if (percent < 100) percent = 100;
            if (percent > (int)(MAXBOOST * 100)) percent = (int)(MAXBOOST * 100);
            boost = percent / 100.0f;
            gain = boost / DUCK;
        }

        public string TargetSummary
        {
            get
            {
                if (boostAll) return "all audio";
                List<string> names = new List<string>();
                lock (targetLock)
                {
                    foreach (uint pid in targetPids)
                    {
                        string n = Native.ProcessNameOf(pid);
                        if (n != null && !names.Contains(n)) names.Add(n);
                    }
                }
                if (wantSystemSounds) names.Add("system sounds");
                if (names.Count == 0) return "audio";
                return string.Join(", ", names.ToArray());
            }
        }

        public int LatencyMs { get { return targetPadFrames / 48 + 10; } }
        public bool BoostAll { get { return boostAll; } }

        public void SetInitialPadMs(int ms)
        {
            if (ms < 10) ms = 10;
            if (ms > 150) ms = 150;
            targetPadFrames = ms * 48;
        }

        public void Start()
        {
            Native.RunMta(delegate()
            {
                try { StartCore(); }
                catch
                {
                    try { Stop(true); } catch { }
                    throw;
                }
            });
        }

        void StartCore()
        {
            if (!Stopped) return;
            running = true;
            Stopped = false;
            StopReason = "";
            StartWarning = "";
            anyTargetGone = false;
            sysCapOk = false;

            if (!boostAll)
            {
                uint[] snap;
                lock (targetLock) { snap = targetPids.ToArray(); }
                foreach (uint pid in snap)
                {
                    try
                    {
                        Process p = Process.GetProcessById((int)pid);
                        p.EnableRaisingEvents = true;
                        uint captured = pid;
                        p.Exited += delegate { TargetExited(captured); };
                        watched.Add(p);
                    }
                    catch { TargetExited(pid); }
                }
            }

            devId = Native.DefaultRenderDeviceId();
            try { System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency; } catch { }
            RebuildTrees();

            WaveFormatEx fmt = WaveFormatEx.Float32Stereo48k();
            List<IAudioClient> pendingCaps = new List<IAudioClient>();
            if (boostAll)
            {
                pendingCaps.Add(Native.ActivateProcessLoopback(selfPid, true));
            }
            else
            {
                List<uint> roots = CollapseToRoots(CopyTargets());
                List<string> capFails = new List<string>();
                foreach (uint root in roots)
                {
                    try { pendingCaps.Add(Native.ActivateProcessLoopback(root, false)); }
                    catch (Exception ex) { capFails.Add((Native.ProcessNameOf(root) ?? root.ToString()) + " (" + ex.Message + ")"); }
                }
                if (wantSystemSounds)
                {
                    try
                    {
                        pendingCaps.Add(Native.ActivateProcessLoopback(0, false));
                        sysCapOk = true;
                    }
                    catch (Exception ex)
                    {
                        StartWarning = "Windows could not capture system sounds alone (" + ex.Message +
                            "). Other selected apps will still be boosted. Use 'Boost all audio' to include system sounds.";
                    }
                }
                if (pendingCaps.Count == 0)
                {
                    if (wantSystemSounds && !sysCapOk && CopyTargets().Length == 0)
                        throw new InvalidOperationException("Windows cannot capture system sounds as their own stream. Use 'Boost all audio' to include them.");
                    string extra = capFails.Count > 0 ? ": " + string.Join("; ", capFails.ToArray()) : "";
                    throw new InvalidOperationException("no capture stream started" + extra);
                }
                if (capFails.Count > 0 && StartWarning == "")
                    StartWarning = "Some apps could not be captured: " + string.Join("; ", capFails.ToArray());
            }

            DuckPass(); // duck before the relay starts (avoids a loud overlap)
            foreach (IAudioClient cap in pendingCaps)
                FinishCapture(cap, fmt);

            IMMDevice dev = Native.DefaultRenderDevice();
            liveMgr = Native.SessionManager(dev);
            sessionNote = new SessionCreatedHandler();
            sessionNote.Fn = DuckOne;
            try { Native.Check(liveMgr.RegisterSessionNotification(sessionNote), "RegisterSessionNotification"); }
            catch { sessionNote = null; }

            renClient = Native.ActivateClient(dev);
            renEv = new AutoResetEvent(false);
            Native.Check(renClient.Initialize(K.SHARED, K.EVENTCB | K.AUTOCONVERT | K.SRC_QUALITY, 2000000, 0, ref fmt, IntPtr.Zero), "render Initialize");
            Native.Check(renClient.SetEventHandle(renEv.SafeWaitHandle.DangerousGetHandle()), "render SetEventHandle");
            uint renBuf; Native.Check(renClient.GetBufferSize(out renBuf), "render GetBufferSize");
            Guid iidr = Native.IID_IAudioRenderClient; object orr;
            Native.Check(renClient.GetService(ref iidr, out orr), "render GetService");
            IAudioRenderClient render = (IAudioRenderClient)orr;

            Guid iidv = Native.IID_ISimpleAudioVolume; object ov;
            Native.Check(renClient.GetService(ref iidv, out ov), "render volume");
            ((ISimpleAudioVolume)ov).SetMasterVolume(1.0f, IntPtr.Zero);
            ((ISimpleAudioVolume)ov).SetMute(false, IntPtr.Zero);

            foreach (IAudioClient cap in capClients)
                Native.Check(cap.Start(), "capture Start");
            Native.Check(renClient.Start(), "render Start");
            foreach (Thread t in capThreads) t.Start();
            renThread = new Thread(delegate() { RenderLoop(render, renBuf); });
            renThread.IsBackground = true;
            renThread.Priority = ThreadPriority.Highest;
            renThread.Start();

            int period = boostAll ? 150 : 500;
            watcher = new System.Threading.Timer(WatcherTick, null, period, period);
        }

        void FinishCapture(IAudioClient cap, WaveFormatEx fmt)
        {
            AutoResetEvent ev = new AutoResetEvent(false);
            Native.Check(cap.Initialize(K.SHARED, K.LOOPBACK | K.EVENTCB, 2000000, 0, ref fmt, IntPtr.Zero), "capture Initialize");
            Native.Check(cap.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle()), "capture SetEventHandle");
            Guid iidc = Native.IID_IAudioCaptureClient; object oc;
            Native.Check(cap.GetService(ref iidc, out oc), "capture GetService");
            IAudioCaptureClient capture = (IAudioCaptureClient)oc;
            SampleRing ring = new SampleRing();
            capClients.Add(cap);
            capEvs.Add(ev);
            mixRings.Add(ring);
            Thread t = new Thread(delegate() { CaptureLoop(capture, ev, ring); });
            t.IsBackground = true;
            t.Priority = ThreadPriority.Highest;
            capThreads.Add(t);
        }

        void CaptureLoop(IAudioCaptureClient capture, AutoResetEvent ev, SampleRing ring)
        {
            uint idx = 0;
            try { Native.AvSetMmThreadCharacteristics("Pro Audio", ref idx); } catch { }
            float[] tmp = new float[48000 * 2];
            while (running)
            {
                ev.WaitOne(100);
                uint pkt;
                while (running && capture.GetNextPacketSize(out pkt) == 0 && pkt > 0)
                {
                    IntPtr p; uint frames, fl; ulong dp, qp;
                    if (capture.GetBuffer(out p, out frames, out fl, out dp, out qp) != 0) break;
                    int samples = (int)frames * 2;
                    if (samples > tmp.Length) tmp = new float[samples];
                    float g = gain;
                    if ((fl & K.BUF_SILENT) != 0) Array.Clear(tmp, 0, samples);
                    else
                    {
                        Marshal.Copy(p, tmp, 0, samples);
                        for (int i = 0; i < samples; i++)
                            tmp[i] = Native.SoftClip(tmp[i] * g);
                    }
                    Interlocked.Add(ref CapSamples, samples);
                    ring.Push(tmp, samples);
                    capture.ReleaseBuffer(frames);
                }
            }
        }

        void RenderLoop(IAudioRenderClient render, uint renBuf)
        {
            uint idx = 0;
            try { Native.AvSetMmThreadCharacteristics("Pro Audio", ref idx); } catch { }
            float[] mix = new float[renBuf * 2];
            float[] tmp = new float[renBuf * 2];
            SampleRing[] rings = mixRings.ToArray();
            while (running)
            {
                renEv.WaitOne(100);
                uint target = (uint)targetPadFrames;
                uint pad; if (renClient.GetCurrentPadding(out pad) != 0) continue;
                if (pad >= target) continue;
                uint room = renBuf - pad;
                uint want = target - pad; if (want > room) want = room;
                int samples = (int)want * 2;
                if (samples > mix.Length) { mix = new float[samples]; tmp = new float[samples]; }
                Array.Clear(mix, 0, samples);
                int maxGot = 0;
                for (int r = 0; r < rings.Length; r++)
                {
                    int n = rings[r].Pop(tmp, samples);
                    for (int i = 0; i < n; i++) mix[i] += tmp[i];
                    if (n > maxGot) maxGot = n;
                }
                uint gotFrames = (uint)(maxGot / 2);
                if (gotFrames == 0)
                {
                    if (pad == 0)
                    {
                        IntPtr ps; if (render.GetBuffer(960, out ps) == 0)
                        { for (int i = 0; i < 960 * 2 * 4; i += 8) Marshal.WriteInt64(ps, i, 0); render.ReleaseBuffer(960, 0); }
                        if (Interlocked.Read(ref RenFrames) > 0)
                        {
                            Interlocked.Increment(ref Glitches);
                            int np = targetPadFrames + 960;
                            if (np > 7200) np = 7200;
                            targetPadFrames = np;
                        }
                    }
                    continue;
                }
                for (int i = 0; i < maxGot; i++) mix[i] = Native.SoftClip(mix[i]);
                IntPtr p; if (render.GetBuffer(gotFrames, out p) != 0) continue;
                Marshal.Copy(mix, 0, p, (int)gotFrames * 2);
                render.ReleaseBuffer(gotFrames, 0);
                Interlocked.Add(ref RenFrames, gotFrames);
            }
        }

        bool WantSession(uint spid, bool isSys, string sname)
        {
            if (spid == selfPid) return false;
            if (Native.IsBoosterName(sname)) return false;
            if (boostAll) return true;
            if (isSys || spid == 0) return wantSystemSounds && sysCapOk;
            HashSet<uint> snap = wantPidsSnapshot;
            return snap != null && snap.Contains(spid);
        }

        void DuckOne(IAudioSessionControl sc)
        {
            if (sc == null || !running) return;
            lock (duckLock)
            {
                try
                {
                    IAudioSessionControl2 sc2 = (IAudioSessionControl2)sc;
                    uint spid; sc2.GetProcessId(out spid);
                    int st; sc2.GetState(out st);
                    if (st == K.StateExpired) return;
                    string sname = Native.ProcessNameOf(spid);
                    bool isSys = Native.IsSystemSounds(sc2) || spid == 0;
                    if (!WantSession(spid, isSys, sname)) return;

                    string inst = null;
                    try { sc2.GetSessionInstanceIdentifier(out inst); } catch { }
                    if (inst == null) inst = "pid:" + spid.ToString() + ":" + ducked.Count.ToString();

                    ISimpleAudioVolume v = (ISimpleAudioVolume)sc;
                    float lvl; v.GetMasterVolume(out lvl);
                    Ducked known = null;
                    foreach (Ducked d in ducked)
                        if (d.InstanceId == inst) { known = d; break; }
                    if (known == null)
                    {
                        float prior = lvl;
                        if (prior <= DUCK + 0.005f) prior = 1.0f;
                        known = new Ducked();
                        known.Vol = v;
                        known.Prior = prior;
                        known.Pid = spid;
                        known.Exe = isSys ? K.SysSoundsName : (sname ?? ("pid" + spid.ToString()));
                        known.InstanceId = inst;
                        ducked.Add(known);
                        StateFile.AddOwn(known.Exe, known.Prior);
                    }
                    bool mut; v.GetMute(out mut);
                    if (mut) v.SetMute(false, IntPtr.Zero);
                    if (lvl > DUCK + 0.003f || lvl < DUCK - 0.003f)
                        v.SetMasterVolume(DUCK, IntPtr.Zero);
                }
                catch { }
            }
        }

        void DuckPass()
        {
            try
            {
                IMMDevice dev = Native.DefaultRenderDevice();
                IAudioSessionManager2 mgr = Native.SessionManager(dev);
                IAudioSessionEnumerator en; Native.Check(mgr.GetSessionEnumerator(out en), "sessions");
                int count; en.GetCount(out count);
                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl sc; if (en.GetSession(i, out sc) != 0) continue;
                    DuckOne(sc);
                }
            }
            catch { }
        }

        uint[] CopyTargets()
        {
            lock (targetLock) { return targetPids.ToArray(); }
        }

        void RebuildTrees()
        {
            if (boostAll) { wantPidsSnapshot = new HashSet<uint>(); return; }
            Dictionary<uint, uint> parent = ParentMap();
            HashSet<uint> snap = new HashSet<uint>();
            lock (targetLock)
            {
                treesByRoot.Clear();
                foreach (uint pid in targetPids)
                {
                    HashSet<uint> tree = DescendantsOf(pid, parent);
                    treesByRoot[pid] = tree;
                    foreach (uint x in tree) snap.Add(x);
                }
            }
            wantPidsSnapshot = snap;
        }

        static Dictionary<uint, uint> ParentMap()
        {
            Dictionary<uint, uint> parent = new Dictionary<uint, uint>();
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT ProcessId,ParentProcessId FROM Win32_Process"))
                foreach (ManagementObject mo in s.Get())
                {
                    uint pid = Convert.ToUInt32(mo["ProcessId"]);
                    uint ppid = Convert.ToUInt32(mo["ParentProcessId"]);
                    parent[pid] = ppid;
                }
            }
            catch { }
            return parent;
        }

        static HashSet<uint> DescendantsOf(uint root, Dictionary<uint, uint> parent)
        {
            HashSet<uint> tree = new HashSet<uint>();
            tree.Add(root);
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (KeyValuePair<uint, uint> kv in parent)
                    if (tree.Contains(kv.Value) && !tree.Contains(kv.Key)) { tree.Add(kv.Key); grew = true; }
            }
            return tree;
        }

        static List<uint> CollapseToRoots(uint[] pids)
        {
            List<uint> roots = new List<uint>();
            Dictionary<uint, uint> parent = ParentMap();
            foreach (uint pid in pids)
            {
                bool covered = false;
                uint walk = pid;
                int guard = 0;
                while (parent.ContainsKey(walk) && guard++ < 64)
                {
                    uint pp = parent[walk];
                    if (pp == walk) break;
                    walk = pp;
                    if (walk != pid)
                    {
                        bool selected = false;
                        foreach (uint s in pids) if (s == walk) { selected = true; break; }
                        if (selected) { covered = true; break; }
                    }
                }
                if (!covered) roots.Add(pid);
            }
            return roots;
        }

        int watchTick;

        void WatcherTick(object state)
        {
            if (!running) return;
            try
            {
                string cur = Native.DefaultRenderDeviceId();
                if (cur != null && devId != null && cur != devId)
                {
                    StopBecause("output device changed - press Start again");
                    return;
                }
                watchTick++;
                if (!boostAll && watchTick % 20 == 0) RebuildTrees();
                DuckPass();
            }
            catch { }
        }

        void TargetExited(uint pid)
        {
            if (!running) return;
            anyTargetGone = true;
            HashSet<uint> tree = null;
            HashSet<uint> snap = new HashSet<uint>();
            lock (targetLock)
            {
                if (treesByRoot.ContainsKey(pid)) tree = treesByRoot[pid];
                targetPids.Remove(pid);
                treesByRoot.Remove(pid);
                foreach (HashSet<uint> t in treesByRoot.Values)
                    foreach (uint x in t) snap.Add(x);
            }
            wantPidsSnapshot = snap;
            if (tree == null)
            {
                tree = new HashSet<uint>();
                tree.Add(pid);
            }
            RestorePids(tree, true);
            bool remain;
            lock (targetLock) { remain = targetPids.Count > 0; }
            if (!remain && !boostAll && !(wantSystemSounds && sysCapOk))
                StopBecause("target closed");
        }

        void RestorePids(HashSet<uint> pids, bool orphan)
        {
            lock (duckLock)
            {
                for (int i = ducked.Count - 1; i >= 0; i--)
                {
                    Ducked d = ducked[i];
                    if (!pids.Contains(d.Pid)) continue;
                    try { d.Vol.SetMasterVolume(d.Prior, IntPtr.Zero); } catch { }
                    if (orphan) StateFile.OrphanTarget(d.Exe);
                    else StateFile.RemoveTarget(d.Exe);
                    ducked.RemoveAt(i);
                }
            }
        }

        void StopBecause(string reason)
        {
            if (Stopped) return;
            StopReason = reason;
            Stop(true);
            EventHandler h = StoppedEvent;
            if (h != null) h(this, EventArgs.Empty);
        }

        public void Stop(bool restoreVolumes)
        {
            if (Stopped) return;
            Stopped = true;
            running = false;
            try { if (watcher != null) watcher.Dispose(); } catch { }
            foreach (Thread t in capThreads)
                try { t.Join(500); } catch { }
            try { if (renThread != null) renThread.Join(500); } catch { }
            try { System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Interactive; } catch { }
            Native.RunMta(delegate() { StopComCore(restoreVolumes); });
        }

        void StopComCore(bool restoreVolumes)
        {
            try
            {
                if (liveMgr != null && sessionNote != null)
                    liveMgr.UnregisterSessionNotification(sessionNote);
            }
            catch { }
            sessionNote = null;
            liveMgr = null;
            foreach (IAudioClient cap in capClients)
                try { cap.Stop(); } catch { }
            try { if (renClient != null) renClient.Stop(); } catch { }
            if (restoreVolumes)
            {
                lock (duckLock)
                {
                    foreach (Ducked d in ducked)
                    {
                        try { d.Vol.SetMasterVolume(d.Prior, IntPtr.Zero); } catch { }
                    }
                    ducked.Clear();
                }
                if (anyTargetGone) StateFile.OrphanOwn();
                else StateFile.RemoveOwn();
            }
            foreach (Process p in watched)
                try { p.Dispose(); } catch { }
            watched.Clear();
        }
    }

    // ===================================== UI ========================================
    internal static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(28, 29, 33);
        public static readonly Color Surface = Color.FromArgb(40, 42, 48);
        public static readonly Color SurfaceHot = Color.FromArgb(54, 56, 64);
        public static readonly Color Text = Color.FromArgb(236, 237, 240);
        public static readonly Color Muted = Color.FromArgb(156, 160, 170);
        public static readonly Color Accent = Color.FromArgb(80, 156, 236);
        public static readonly Color AccentHot = Color.FromArgb(104, 176, 248);
        public static readonly Color Border = Color.FromArgb(68, 70, 78);
        public static readonly Color Stop = Color.FromArgb(176, 72, 72);
        public static readonly Color StopHot = Color.FromArgb(196, 88, 88);
        public static readonly Color Groove = Color.FromArgb(58, 60, 68);

        public static void Button(Button b, Color fill, Color fillHot)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.MouseOverBackColor = fillHot;
            b.FlatAppearance.MouseDownBackColor = fill;
            b.UseVisualStyleBackColor = false;
            b.BackColor = fill;
            b.ForeColor = Text;
            b.Cursor = Cursors.Hand;
        }

        public static void Check(CheckBox c)
        {
            c.FlatStyle = FlatStyle.Standard;
            c.UseVisualStyleBackColor = false;
            c.BackColor = Bg;
            c.ForeColor = Text;
        }
    }

    // Classic TrackBar ignores WinForms colors (ticks/thumb stay light). This one
    // paints with the same palette as the rest of the window.
    internal class DarkSlider : Control
    {
        int min = 100, max = 500, val = 150;
        bool dragging;

        public int Minimum
        {
            get { return min; }
            set { min = value; if (val < min) Value = min; Invalidate(); }
        }
        public int Maximum
        {
            get { return max; }
            set { max = value; if (val > max) Value = max; Invalidate(); }
        }
        public int Value
        {
            get { return val; }
            set
            {
                int v = value;
                if (v < min) v = min;
                if (v > max) v = max;
                if (v == val) return;
                val = v;
                Invalidate();
                EventHandler h = ValueChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }
        public event EventHandler ValueChanged;

        public DarkSlider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            BackColor = Theme.Bg;
        }

        int Pad { get { return 10; } }
        int ThumbR { get { return 7; } }

        float T
        {
            get
            {
                int span = max - min;
                if (span <= 0) return 0;
                return (val - min) / (float)span;
            }
        }

        int XFromValue()
        {
            int inner = Width - 2 * Pad;
            if (inner < 1) inner = 1;
            return Pad + (int)Math.Round(T * inner);
        }

        int ValueFromX(int x)
        {
            int inner = Width - 2 * Pad;
            if (inner < 1) inner = 1;
            float t = (x - Pad) / (float)inner;
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            return min + (int)Math.Round(t * (max - min));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            int midY = Height / 2 - 4;
            int grooveH = 6;
            Rectangle groove = new Rectangle(Pad, midY - grooveH / 2, Math.Max(1, Width - 2 * Pad), grooveH);
            using (SolidBrush b = new SolidBrush(Theme.Groove))
                g.FillRectangle(b, groove);
            int fillW = XFromValue() - Pad;
            if (fillW > 0)
            {
                using (SolidBrush b = new SolidBrush(Focused ? Theme.AccentHot : Theme.Accent))
                    g.FillRectangle(b, new Rectangle(Pad, groove.Y, fillW, grooveH));
            }
            int tx = XFromValue();
            int ty = midY;
            using (SolidBrush b = new SolidBrush(Theme.Text))
                g.FillEllipse(b, tx - ThumbR, ty - ThumbR, ThumbR * 2, ThumbR * 2);
            using (Pen p = new Pen(Theme.Accent, 1.5f))
                g.DrawEllipse(p, tx - ThumbR, ty - ThumbR, ThumbR * 2, ThumbR * 2);
            using (SolidBrush tick = new SolidBrush(Theme.Muted))
            {
                int span = max - min;
                if (span > 0)
                {
                    for (int v = min; v <= max; v += 50)
                    {
                        float t = (v - min) / (float)span;
                        int x = Pad + (int)Math.Round(t * (Width - 2 * Pad));
                        g.FillRectangle(tick, x, groove.Bottom + 5, 1, 4);
                    }
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                Focus();
                dragging = true;
                Capture = true;
                Value = ValueFromX(e.X);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging) Value = ValueFromX(e.X);
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            dragging = false;
            Capture = false;
            base.OnMouseUp(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            Focus();
            int step = (e.Delta > 0) ? 5 : -5;
            if ((ModifierKeys & Keys.Shift) != 0) step *= 10;
            Value = val + step;
            ((HandledMouseEventArgs)e).Handled = true;
            base.OnMouseWheel(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down) { Value = val - 5; e.Handled = true; }
            else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up) { Value = val + 5; e.Handled = true; }
            else if (e.KeyCode == Keys.PageDown) { Value = val - 50; e.Handled = true; }
            else if (e.KeyCode == Keys.PageUp) { Value = val + 50; e.Handled = true; }
            else if (e.KeyCode == Keys.Home) { Value = min; e.Handled = true; }
            else if (e.KeyCode == Keys.End) { Value = max; e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    }

    internal class MainForm : Form
    {
        CheckedListBox list = new CheckedListBox();
        Button refreshBtn = new Button();
        CheckBox chkSys = new CheckBox();
        CheckBox chkAll = new CheckBox();
        DarkSlider slider = new DarkSlider();
        Label boostLbl = new Label();
        Button startBtn = new Button();
        Label status = new Label();
        System.Windows.Forms.Timer uiTimer = new System.Windows.Forms.Timer();
        BoostEngine engine;
        List<uint> listPids = new List<uint>();
        int tickN;

        public MainForm()
        {
            Text = "App Volume Booster";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            ClientSize = new Size(470, 400);
            Font = new Font("Segoe UI", 9f);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;

            Label appLbl = new Label();
            appLbl.Text = "Applications (check one or more):";
            appLbl.SetBounds(12, 10, 340, 18);
            appLbl.BackColor = Theme.Bg;
            appLbl.ForeColor = Theme.Text;

            list.CheckOnClick = true;
            list.IntegralHeight = false;
            list.HorizontalScrollbar = true;
            list.BorderStyle = BorderStyle.FixedSingle;
            list.BackColor = Theme.Surface;
            list.ForeColor = Theme.Text;
            list.SetBounds(12, 30, 356, 148);

            refreshBtn.Text = "Refresh";
            refreshBtn.SetBounds(376, 30, 82, 25);
            Theme.Button(refreshBtn, Theme.Surface, Theme.SurfaceHot);
            refreshBtn.Click += delegate { FillSessions(); };

            chkSys.Text = "Windows system sounds (beeps, notifications)";
            chkSys.SetBounds(12, 184, 446, 22);
            Theme.Check(chkSys);

            chkAll.Text = "Boost all audio on this device (includes system sounds)";
            chkAll.SetBounds(12, 206, 446, 22);
            Theme.Check(chkAll);
            chkAll.CheckedChanged += delegate { SyncModeEnabled(); };

            Label volLbl = new Label();
            volLbl.Text = "Boost:";
            volLbl.SetBounds(12, 242, 80, 20);
            volLbl.BackColor = Theme.Bg;
            volLbl.ForeColor = Theme.Text;
            slider.Minimum = 100; slider.Maximum = (int)(BoostEngine.MAXBOOST * 100);
            slider.Value = 150;
            slider.SetBounds(90, 236, 280, 45);
            slider.ValueChanged += delegate
            {
                boostLbl.Text = slider.Value + "%";
                if (engine != null && !engine.Stopped) engine.SetBoost(slider.Value);
            };
            boostLbl.Text = "150%";
            boostLbl.SetBounds(380, 242, 70, 20);
            boostLbl.BackColor = Theme.Bg;
            boostLbl.ForeColor = Theme.Text;

            startBtn.Text = "Start boost";
            startBtn.SetBounds(12, 288, 120, 30);
            Theme.Button(startBtn, Theme.Accent, Theme.AccentHot);
            startBtn.Click += delegate { ToggleBoost(); };

            status.SetBounds(12, 328, 446, 60);
            status.BackColor = Theme.Bg;
            status.ForeColor = Theme.Muted;
            status.Text = "Check the apps to boost, optionally include system sounds or all audio, then press Start.";

            Controls.Add(appLbl);
            Controls.Add(list);
            Controls.Add(refreshBtn);
            Controls.Add(chkSys);
            Controls.Add(chkAll);
            Controls.Add(volLbl);
            Controls.Add(slider);
            Controls.Add(boostLbl);
            Controls.Add(startBtn);
            Controls.Add(status);

            uiTimer.Interval = 1000;
            uiTimer.Tick += delegate
            {
                UpdateStatus();
                tickN++;
                if (tickN % 5 == 0)
                {
                    string healed = null;
                    try { healed = StateFile.SelfHeal(); } catch { }
                    if (healed != null && (engine == null || engine.Stopped))
                        status.Text = "Restored mixer volume for: " + healed + ".";
                }
            };
            uiTimer.Start();

            HandleCreated += delegate
            {
                Native.UseDarkTitleBar(Handle);
                Native.UseDarkExplorerTheme(Handle);
                Native.UseDarkExplorerTheme(list.Handle);
                Native.UseDarkExplorerTheme(chkSys.Handle);
                Native.UseDarkExplorerTheme(chkAll.Handle);
            };
            Load += delegate
            {
                string healed = StateFile.SelfHeal();
                FillSessions();
                if (healed != null) status.Text = "Restored mixer volume for: " + healed + " (left ducked by a previous run).";
            };
            FormClosing += delegate { if (engine != null) engine.Stop(true); };
        }

        void StyleActionButton(bool stopping)
        {
            if (stopping) Theme.Button(startBtn, Theme.Stop, Theme.StopHot);
            else Theme.Button(startBtn, Theme.Accent, Theme.AccentHot);
        }

        void SyncModeEnabled()
        {
            bool idle = (engine == null || engine.Stopped);
            bool all = chkAll.Checked;
            list.Enabled = idle && !all;
            refreshBtn.Enabled = idle && !all;
            chkSys.Enabled = idle && !all;
            chkAll.Enabled = idle;
        }

        void FillSessions()
        {
            List<uint> keep = new List<uint>();
            for (int i = 0; i < list.Items.Count; i++)
                if (list.GetItemChecked(i) && i < listPids.Count) keep.Add(listPids[i]);

            list.Items.Clear();
            listPids.Clear();
            try
            {
                uint self = (uint)Process.GetCurrentProcess().Id;
                IMMDevice dev = Native.DefaultRenderDevice();
                IAudioSessionManager2 mgr = Native.SessionManager(dev);
                IAudioSessionEnumerator en; Native.Check(mgr.GetSessionEnumerator(out en), "sessions");
                int count; en.GetCount(out count);
                List<uint> pids = new List<uint>();
                Dictionary<uint, string> names = new Dictionary<uint, string>();
                Dictionary<uint, bool> playing = new Dictionary<uint, bool>();
                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl sc; if (en.GetSession(i, out sc) != 0) continue;
                    IAudioSessionControl2 sc2 = (IAudioSessionControl2)sc;
                    uint pid; sc2.GetProcessId(out pid);
                    int st; sc2.GetState(out st);
                    if (pid == 0 || pid == self || st == K.StateExpired) continue;
                    string name = Native.ProcessNameOf(pid);
                    if (name == null || Native.IsBoosterName(name)) continue;
                    if (!names.ContainsKey(pid))
                    {
                        pids.Add(pid); names[pid] = name; playing[pid] = false;
                    }
                    if (st == K.StateActive) playing[pid] = true;
                }
                pids.Sort(delegate(uint a, uint b)
                {
                    if (playing[a] != playing[b]) return playing[a] ? -1 : 1;
                    return string.Compare(names[a], names[b], StringComparison.OrdinalIgnoreCase);
                });
                foreach (uint pid in pids)
                {
                    listPids.Add(pid);
                    list.Items.Add(names[pid] + "  (pid " + pid + (playing[pid] ? ", playing)" : ")"));
                    if (keep.Contains(pid)) list.SetItemChecked(list.Items.Count - 1, true);
                }
                if (list.Items.Count == 0 && (engine == null || engine.Stopped) && !chkAll.Checked && !chkSys.Checked)
                    status.Text = "No apps with audio sessions found - start playback in an app first, then Refresh. You can still boost all audio or system sounds.";
            }
            catch (Exception ex) { status.Text = "Session list failed: " + ex.Message; }
        }

        List<uint> CheckedPids()
        {
            List<uint> pids = new List<uint>();
            for (int i = 0; i < list.Items.Count; i++)
                if (list.GetItemChecked(i) && i < listPids.Count) pids.Add(listPids[i]);
            return pids;
        }

        void SetBusy(bool busy)
        {
            if (busy)
            {
                list.Enabled = false;
                refreshBtn.Enabled = false;
                chkSys.Enabled = false;
                chkAll.Enabled = false;
            }
            else SyncModeEnabled();
        }

        void ToggleBoost()
        {
            if (engine != null && !engine.Stopped)
            {
                engine.Stop(true);
                engine = null;
                startBtn.Text = "Start boost";
                StyleActionButton(false);
                SetBusy(false);
                status.Text = "Stopped - mixer volume restored.";
                return;
            }
            List<uint> pids = CheckedPids();
            bool all = chkAll.Checked;
            bool sys = chkSys.Checked && !all;
            if (!all && pids.Count == 0 && !sys)
            {
                status.Text = "Check at least one application, Windows system sounds, or Boost all audio.";
                return;
            }
            try
            {
                engine = new BoostEngine(pids, slider.Value, all, sys);
                engine.StoppedEvent += delegate
                {
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            startBtn.Text = "Start boost";
                            StyleActionButton(false);
                            status.Text = "Stopped: " + engine.StopReason;
                            engine = null;
                            SetBusy(false);
                            FillSessions();
                        });
                    }
                    catch { }
                };
                engine.Start();
                startBtn.Text = "Stop boost";
                StyleActionButton(true);
                SetBusy(true);
                string warn = engine.StartWarning;
                status.Text = "Boosting " + engine.TargetSummary + " at " + slider.Value + "% (latency ~" + engine.LatencyMs + " ms)."
                    + (warn != "" ? " " + warn : "");
            }
            catch (Exception ex)
            {
                engine = null;
                SetBusy(false);
                status.Text = "Start failed: " + ex.Message;
            }
        }

        void UpdateStatus()
        {
            if (engine == null || engine.Stopped) return;
            string warn = engine.StartWarning;
            status.Text = "Boosting " + engine.TargetSummary + " at " + slider.Value +
                "%   relayed " + (engine.RenFrames / 48000) + " s, glitches " + engine.Glitches +
                ", latency ~" + engine.LatencyMs + " ms  (mixer slider(s) held at 4% by design)"
                + (warn != "" ? "  " + warn : "");
        }
    }

    // ==================================== entry ======================================
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            try { Native.SetProcessDpiAwareness(1); } catch { }

            if (args.Length > 0) return CliMain(args);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }

        static int CliMain(string[] args)
        {
            List<uint> pids = new List<uint>();
            List<string> names = new List<string>();
            int boostPct = 150; double seconds = 0; string log = null; int padMs = 0;
            bool all = false; bool sys = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--pid" && i + 1 < args.Length) pids.Add(uint.Parse(args[++i]));
                else if (a == "--name" && i + 1 < args.Length) names.Add(args[++i]);
                else if (a == "--boost" && i + 1 < args.Length) boostPct = int.Parse(args[++i]);
                else if (a == "--seconds" && i + 1 < args.Length) seconds = double.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (a == "--padms" && i + 1 < args.Length) padMs = int.Parse(args[++i]);
                else if (a == "--log" && i + 1 < args.Length) log = args[++i];
                else if (a == "--all") all = true;
                else if (a == "--system-sounds" || a == "--system") sys = true;
            }
            try
            {
                StateFile.SelfHeal();
                foreach (string name in names)
                {
                    string n = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;
                    Process[] ps = Process.GetProcessesByName(n);
                    if (ps.Length == 0) throw new ArgumentException("no process named " + name);
                    foreach (Process p in ps)
                    {
                        uint pid = (uint)p.Id;
                        if (!pids.Contains(pid)) pids.Add(pid);
                    }
                }
                if (!all && pids.Count == 0 && !sys) throw new ArgumentException("--pid, --name, --system-sounds, or --all required");

                BoostEngine eng = new BoostEngine(pids, boostPct, all, sys);
                if (padMs > 0) eng.SetInitialPadMs(padMs);
                ManualResetEvent stopped = new ManualResetEvent(false);
                eng.StoppedEvent += delegate { stopped.Set(); };
                eng.Start();
                if (seconds > 0) stopped.WaitOne((int)(seconds * 1000));
                else stopped.WaitOne();
                string why = eng.StopReason;
                eng.Stop(true);
                string line = string.Format(CultureInfo.InvariantCulture,
                    "ok capSamples={0} renFrames={1} glitches={2} boost={3} latencyMs={4} targets={5} stopReason={6}",
                    eng.CapSamples, eng.RenFrames, eng.Glitches, boostPct, eng.LatencyMs, eng.TargetSummary, why == "" ? "timer" : why);
                if (log != null) File.WriteAllText(log, line + "\r\n");
                return 0;
            }
            catch (Exception ex)
            {
                if (log != null) File.WriteAllText(log, "ERROR " + ex + "\r\n");
                return 1;
            }
        }
    }
}
