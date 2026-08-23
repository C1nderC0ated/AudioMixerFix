// =====================================================================================
// AppVolumeBooster.cs - boost one application's volume past 100% (up to 200%).
//
// Part of the AudioMixerFix kit (optional add-on). Single source file, builds with the
// in-box .NET Framework compiler - no SDK, no NuGet, no admin, no drivers:
//   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /t:winexe
//       /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll
//       AppVolumeBooster.cs
//
// HOW IT WORKS (duck-and-boost relay, verified end-to-end on Win11 24H2 26100.9168):
//   1. Windows' process-loopback capture API (ActivateAudioInterfaceAsync +
//      VAD\Process_Loopback, the API OBS uses for per-app audio capture) captures ONLY
//      the target app's audio. On this build the tap is post-session-volume.
//   2. The target's Volume Mixer slider is therefore ducked to 4% (near-silent direct
//      path), and the captured signal is multiplied by boost/0.04 (float32, lossless),
//      then re-rendered to the default output. Direct-path bleed sits 28-34 dB below
//      the boosted copy - inaudible.
//   3. A soft clipper above 0.95 full-scale prevents harsh digital clipping.
//   4. On stop / target exit / booster exit the mixer slider is restored. A state file
//      next to the exe self-heals volumes if the booster was killed hard.
//
// CLI (for scripting/testing; the GUI appears when run with no args):
//   AppVolumeBooster.exe --pid <N> | --name <exe> [--boost 100..200] [--seconds <S>] [--log <file>]
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
        int RegisterSessionNotification(IntPtr n);
        int UnregisterSessionNotification(IntPtr n);
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

        public static IAudioClient ActivateProcessLoopback(uint pid)
        {
            IAudioClient result = null;
            Exception err = null;
            Thread t = new Thread(delegate()
            {
                try
                {
                    // AUDIOCLIENT_ACTIVATION_PARAMS { PROCESS_LOOPBACK, { pid, INCLUDE_TREE } } in VT_BLOB
                    IntPtr blob = Marshal.AllocHGlobal(12);
                    Marshal.WriteInt32(blob, 0, 1);
                    Marshal.WriteInt32(blob, 4, (int)pid);
                    Marshal.WriteInt32(blob, 8, 0);
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
            try { return Process.GetProcessById((int)pid).ProcessName; }
            catch { return null; }
        }
    }

    // =============================== state file (self-heal) ==========================
    // One line per active boost: <boosterPid>|<targetExeName>|<priorVolume>
    // If the booster dies without restoring the ducked slider, the next run of any
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

        public static void AddOwn(string targetExe, float prior)
        {
            Mutex m = Mtx();
            try
            {
                m.WaitOne(3000);
                List<string> lines = ReadAll();
                string own = Process.GetCurrentProcess().Id.ToString();
                lines.RemoveAll(delegate(string l) { return l.StartsWith(own + "|"); });
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
                string own = Process.GetCurrentProcess().Id.ToString();
                lines.RemoveAll(delegate(string l) { return l.StartsWith(own + "|"); });
                if (lines.Count == 0) { try { File.Delete(PathOf()); } catch { } }
                else File.WriteAllLines(PathOf(), lines.ToArray());
            }
            catch { }
            finally { try { m.ReleaseMutex(); } catch { } m.Close(); }
        }

        // Mark our own lines as orphaned (boosterPid 0) so any later SelfHeal - in this
        // process or a future one - keeps trying to restore the target's slider until
        // the app is seen again. Used when the target dies while ducked, because a
        // restore written to an expired session does not reliably reach the store.
        public static void OrphanOwn()
        {
            Mutex m = Mtx();
            try
            {
                m.WaitOne(3000);
                List<string> lines = ReadAll();
                string own = Process.GetCurrentProcess().Id.ToString();
                for (int i = 0; i < lines.Count; i++)
                    if (lines[i].StartsWith(own + "|")) lines[i] = "0" + lines[i].Substring(own.Length);
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
                        try { Process p = Process.GetProcessById(bpid); alive = (p.ProcessName.IndexOf("AppVolumeBooster", StringComparison.OrdinalIgnoreCase) >= 0); }
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
                            uint spid; ((IAudioSessionControl2)sc).GetProcessId(out spid);
                            string sname = Native.ProcessNameOf(spid);
                            if (sname == null) continue;
                            for (int di = 0; di < dead.Count; di++)
                            {
                                if (resolved[di]) continue;
                                string[] parts = dead[di];
                                if (!string.Equals(sname + ".exe", parts[1], StringComparison.OrdinalIgnoreCase) &&
                                    !string.Equals(sname, parts[1], StringComparison.OrdinalIgnoreCase)) continue;
                                ISimpleAudioVolume v = (ISimpleAudioVolume)sc;
                                float cur; v.GetMasterVolume(out cur);
                                if (cur <= BoostEngine.DUCK + 0.01f) // still ducked -> repair
                                {
                                    float prior;
                                    if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out prior)) prior = 1.0f;
                                    v.SetMasterVolume(prior, IntPtr.Zero);
                                    if (!healedNames.Contains(parts[1])) healedNames.Add(parts[1]);
                                }
                                resolved[di] = true; // healed, or seen healthy - either way done
                            }
                        }
                    }
                    catch { }
                    for (int di = 0; di < dead.Count; di++)
                        if (!resolved[di]) keep.Add(string.Join("|", dead[di])); // app not seen yet - retry later
                }
                if (keep.Count == 0) { try { File.Delete(PathOf()); } catch { } }
                else File.WriteAllLines(PathOf(), keep.ToArray());
            }
            catch { }
            finally { try { m.ReleaseMutex(); } catch { } m.Close(); }
            return healedNames.Count > 0 ? string.Join(", ", healedNames.ToArray()) : null;
        }
    }

    // ================================== boost engine =================================
    internal class BoostEngine
    {
        public const float DUCK = 0.04f;      // ducked mixer level of the target while boosting
        public const float MAXBOOST = 2.0f;   // 200%

        readonly uint targetPid;
        readonly string targetExe;
        volatile float gain;                  // boost / DUCK
        float boost;

        readonly object ringLock = new object();
        float[] ring = new float[48000 * 2 * 2]; // 2 s stereo
        int rHead, rTail, rCount;

        volatile bool running;
        volatile int targetPadFrames = 2400;  // standing render queue: ~50 ms; grows on underrun
        Thread capThread, renThread;
        System.Threading.Timer watcher;
        IAudioClient capClient, renClient;
        AutoResetEvent capEv, renEv;
        string devId;
        Process targetProc;

        class Ducked { public ISimpleAudioVolume Vol; public float Prior; public uint Pid; }
        readonly List<Ducked> ducked = new List<Ducked>();
        readonly object duckLock = new object();

        public long CapSamples, RenFrames, Glitches;
        public volatile bool Stopped = true;
        public string StopReason = "";
        public event EventHandler StoppedEvent;

        public BoostEngine(uint pid, int boostPercent)
        {
            targetPid = pid;
            targetExe = Native.ProcessNameOf(pid);
            if (targetExe == null) throw new ArgumentException("process " + pid + " is not running");
            SetBoost(boostPercent);
        }

        public void SetBoost(int percent)
        {
            if (percent < 100) percent = 100;
            if (percent > (int)(MAXBOOST * 100)) percent = (int)(MAXBOOST * 100);
            boost = percent / 100.0f;
            gain = boost / DUCK;
        }

        public string TargetExe { get { return targetExe; } }
        public uint TargetPid { get { return targetPid; } }
        public int LatencyMs { get { return targetPadFrames / 48 + 10; } }

        public void SetInitialPadMs(int ms)
        {
            if (ms < 10) ms = 10;
            if (ms > 150) ms = 150;
            targetPadFrames = ms * 48;
        }

        public void Start()
        {
            Native.RunMta(delegate() { StartCore(); });
        }

        void StartCore()
        {
            if (!Stopped) return;
            running = true;
            Stopped = false;
            StopReason = "";

            targetProc = Process.GetProcessById((int)targetPid);
            targetProc.EnableRaisingEvents = true;
            targetProc.Exited += delegate { targetGone = true; StopBecause("target closed"); };

            devId = Native.DefaultRenderDeviceId();
            // relay prefers few, short GC pauses while active
            try { System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency; } catch { }
            cachedTree = DescendantsOf(targetPid);
            DuckPass(cachedTree); // duck before the relay starts (avoids a loud overlap)

            // capture: process loopback on the target pid tree
            capClient = Native.ActivateProcessLoopback(targetPid);
            WaveFormatEx fmt = WaveFormatEx.Float32Stereo48k();
            capEv = new AutoResetEvent(false);
            Native.Check(capClient.Initialize(K.SHARED, K.LOOPBACK | K.EVENTCB, 2000000, 0, ref fmt, IntPtr.Zero), "capture Initialize");
            Native.Check(capClient.SetEventHandle(capEv.SafeWaitHandle.DangerousGetHandle()), "capture SetEventHandle");
            Guid iidc = Native.IID_IAudioCaptureClient; object oc;
            Native.Check(capClient.GetService(ref iidc, out oc), "capture GetService");
            IAudioCaptureClient capture = (IAudioCaptureClient)oc;

            // render: default device, engine converts our 48k float to the mix format
            IMMDevice dev = Native.DefaultRenderDevice();
            renClient = Native.ActivateClient(dev);
            renEv = new AutoResetEvent(false);
            Native.Check(renClient.Initialize(K.SHARED, K.EVENTCB | K.AUTOCONVERT | K.SRC_QUALITY, 2000000, 0, ref fmt, IntPtr.Zero), "render Initialize");
            Native.Check(renClient.SetEventHandle(renEv.SafeWaitHandle.DangerousGetHandle()), "render SetEventHandle");
            uint renBuf; Native.Check(renClient.GetBufferSize(out renBuf), "render GetBufferSize");
            Guid iidr = Native.IID_IAudioRenderClient; object orr;
            Native.Check(renClient.GetService(ref iidr, out orr), "render GetService");
            IAudioRenderClient render = (IAudioRenderClient)orr;

            // our own relay session must sit at 100%, unmuted
            Guid iidv = Native.IID_ISimpleAudioVolume; object ov;
            Native.Check(renClient.GetService(ref iidv, out ov), "render volume");
            ((ISimpleAudioVolume)ov).SetMasterVolume(1.0f, IntPtr.Zero);
            ((ISimpleAudioVolume)ov).SetMute(false, IntPtr.Zero);

            capThread = new Thread(delegate() { CaptureLoop(capture); });
            renThread = new Thread(delegate() { RenderLoop(render, renBuf); });
            capThread.IsBackground = true; renThread.IsBackground = true;
            capThread.Priority = ThreadPriority.Highest;
            renThread.Priority = ThreadPriority.Highest;

            Native.Check(capClient.Start(), "capture Start");
            Native.Check(renClient.Start(), "render Start");
            capThread.Start(); renThread.Start();

            watcher = new System.Threading.Timer(WatcherTick, null, 2000, 2000);
        }

        void CaptureLoop(IAudioCaptureClient capture)
        {
            uint idx = 0;
            try { Native.AvSetMmThreadCharacteristics("Pro Audio", ref idx); } catch { }
            float[] tmp = new float[48000 * 2];
            while (running)
            {
                capEv.WaitOne(100);
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
                        {
                            float s = tmp[i] * g;
                            // soft clip above 0.95 to avoid harsh digital clipping
                            if (s > 0.95f) s = 0.95f + 0.05f * (float)Math.Tanh((s - 0.95f) / 0.05f);
                            else if (s < -0.95f) s = -0.95f - 0.05f * (float)Math.Tanh((-s - 0.95f) / 0.05f);
                            tmp[i] = s;
                        }
                    }
                    Interlocked.Add(ref CapSamples, samples);
                    Push(tmp, samples);
                    capture.ReleaseBuffer(frames);
                }
            }
        }

        void RenderLoop(IAudioRenderClient render, uint renBuf)
        {
            uint idx = 0;
            try { Native.AvSetMmThreadCharacteristics("Pro Audio", ref idx); } catch { }
            float[] tmp = new float[renBuf * 2];
            while (running)
            {
                renEv.WaitOne(100);
                uint target = (uint)targetPadFrames;
                uint pad; if (renClient.GetCurrentPadding(out pad) != 0) continue;
                if (pad >= target) continue;
                uint room = renBuf - pad;
                uint want = target - pad; if (want > room) want = room;
                int gotSamples = Pop(tmp, (int)want * 2);
                uint gotFrames = (uint)(gotSamples / 2);
                if (gotFrames == 0)
                {
                    if (pad == 0)
                    {
                        // Underrun: queue 20 ms of silence to re-prime. Before the first
                        // real audio has rendered this is just startup priming; after
                        // that it is an audible gap, so count it and enlarge the standing
                        // buffer - trading a little latency for glitch-free audio on a
                        // loaded system.
                        IntPtr ps; if (render.GetBuffer(960, out ps) == 0)
                        { for (int i = 0; i < 960 * 2 * 4; i += 8) Marshal.WriteInt64(ps, i, 0); render.ReleaseBuffer(960, 0); }
                        if (Interlocked.Read(ref RenFrames) > 0)
                        {
                            Interlocked.Increment(ref Glitches);
                            int np = targetPadFrames + 960;   // +20 ms per glitch
                            if (np > 7200) np = 7200;         // cap at 150 ms
                            targetPadFrames = np;
                        }
                    }
                    continue;
                }
                IntPtr p; if (render.GetBuffer(gotFrames, out p) != 0) continue;
                Marshal.Copy(tmp, 0, p, (int)gotFrames * 2);
                render.ReleaseBuffer(gotFrames, 0);
                Interlocked.Add(ref RenFrames, gotFrames);
            }
        }

        void Push(float[] data, int n)
        {
            lock (ringLock)
            {
                for (int i = 0; i < n; i++)
                {
                    if (rCount == ring.Length) { rTail = (rTail + 1) % ring.Length; rCount--; }
                    ring[rHead] = data[i]; rHead = (rHead + 1) % ring.Length; rCount++;
                }
            }
        }

        int Pop(float[] dst, int n)
        {
            lock (ringLock)
            {
                int take = Math.Min(n, rCount);
                take -= take % 2; // whole frames only
                for (int i = 0; i < take; i++) { dst[i] = ring[rTail]; rTail = (rTail + 1) % ring.Length; }
                rCount -= take;
                return take;
            }
        }

        // Duck every render session belonging to the target's process tree; remember priors.
        // Writes the volume only when it actually differs: a no-op re-set every tick makes
        // audiosrv re-ramp the session, which can tick audibly in the captured stream.
        void DuckPass(HashSet<uint> tree)
        {
            lock (duckLock)
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
                        uint spid; ((IAudioSessionControl2)sc).GetProcessId(out spid);
                        if (!tree.Contains(spid)) continue;
                        bool known = false;
                        foreach (Ducked d in ducked) if (d.Pid == spid) { known = true; break; }
                        ISimpleAudioVolume v = (ISimpleAudioVolume)sc;
                        float lvl; v.GetMasterVolume(out lvl);
                        if (!known)
                        {
                            float prior = lvl;
                            if (prior <= DUCK + 0.005f) prior = 1.0f; // ducked leftover: treat as 100%
                            Ducked d = new Ducked(); d.Vol = v; d.Prior = prior; d.Pid = spid;
                            ducked.Add(d);
                            StateFile.AddOwn(targetExe, d.Prior);
                        }
                        bool mut; v.GetMute(out mut);
                        if (mut) v.SetMute(false, IntPtr.Zero);
                        if (lvl > DUCK + 0.003f || lvl < DUCK - 0.003f)
                            v.SetMasterVolume(DUCK, IntPtr.Zero); // assert only when it drifted
                    }
                }
                catch { }
            }
        }

        static HashSet<uint> DescendantsOf(uint root)
        {
            HashSet<uint> tree = new HashSet<uint>();
            tree.Add(root);
            try
            {
                Dictionary<uint, uint> parent = new Dictionary<uint, uint>();
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT ProcessId,ParentProcessId FROM Win32_Process"))
                foreach (ManagementObject mo in s.Get())
                {
                    uint pid = Convert.ToUInt32(mo["ProcessId"]);
                    uint ppid = Convert.ToUInt32(mo["ParentProcessId"]);
                    parent[pid] = ppid;
                }
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    foreach (KeyValuePair<uint, uint> kv in parent)
                        if (tree.Contains(kv.Value) && !tree.Contains(kv.Key)) { tree.Add(kv.Key); grew = true; }
                }
            }
            catch { }
            return tree;
        }

        int watchTick;
        HashSet<uint> cachedTree;

        void WatcherTick(object state)
        {
            if (!running) return;
            try
            {
                // target still alive?
                if (targetProc.HasExited) { StopBecause("target closed"); return; }
                // default device changed? -> restart cleanly on the new device
                string cur = Native.DefaultRenderDeviceId();
                if (cur != null && devId != null && cur != devId) { StopBecause("output device changed - press Start again"); return; }
                // Re-assert ducks; pick up new sessions (browsers spawn them per stream).
                // The WMI process-tree walk is expensive, so refresh it only every 5th
                // tick (10 s); the cheap session pass runs every 2 s with the cached tree.
                watchTick++;
                if (cachedTree == null || watchTick % 5 == 0) cachedTree = DescendantsOf(targetPid);
                DuckPass(cachedTree);
            }
            catch { }
        }

        bool targetGone;

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
            try { if (capThread != null) capThread.Join(500); } catch { }
            try { if (renThread != null) renThread.Join(500); } catch { }
            try { System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Interactive; } catch { }
            Native.RunMta(delegate() { StopComCore(restoreVolumes); });
        }

        void StopComCore(bool restoreVolumes)
        {
            try { if (capClient != null) capClient.Stop(); } catch { }
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
                // If the target died while ducked, a write to its expired session does not
                // reliably reach the persisted store (verified on 26100.9168), so keep the
                // state line as an orphan; SelfHeal repairs the slider when the app is next
                // seen. On a normal stop the live-session restore above is reliable.
                if (targetGone) StateFile.OrphanOwn();
                else StateFile.RemoveOwn();
            }
        }
    }

    // ===================================== UI ========================================
    internal class MainForm : Form
    {
        ComboBox combo = new ComboBox();
        Button refreshBtn = new Button();
        TrackBar slider = new TrackBar();
        Label boostLbl = new Label();
        Button startBtn = new Button();
        Label status = new Label();
        System.Windows.Forms.Timer uiTimer = new System.Windows.Forms.Timer();
        BoostEngine engine;
        List<uint> comboPids = new List<uint>();
        int tickN;

        public MainForm()
        {
            Text = "App Volume Booster";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            ClientSize = new Size(430, 190);
            Font = new Font("Segoe UI", 9f);

            Label appLbl = new Label();
            appLbl.Text = "Application:"; appLbl.SetBounds(12, 15, 80, 20);
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.SetBounds(95, 12, 240, 24);
            refreshBtn.Text = "Refresh"; refreshBtn.SetBounds(343, 11, 75, 25);
            refreshBtn.Click += delegate { FillSessions(); };

            Label volLbl = new Label();
            volLbl.Text = "Boost:"; volLbl.SetBounds(12, 55, 80, 20);
            slider.Minimum = 100; slider.Maximum = 200;
            slider.TickFrequency = 10; slider.SmallChange = 5; slider.LargeChange = 25;
            slider.Value = 150;
            slider.SetBounds(90, 48, 250, 45);
            slider.ValueChanged += delegate
            {
                boostLbl.Text = slider.Value + "%";
                if (engine != null && !engine.Stopped) engine.SetBoost(slider.Value);
            };
            boostLbl.Text = "150%"; boostLbl.SetBounds(350, 55, 60, 20);

            startBtn.Text = "Start boost"; startBtn.SetBounds(12, 105, 120, 30);
            startBtn.Click += delegate { ToggleBoost(); };

            status.SetBounds(12, 145, 406, 35);
            status.Text = "Pick an app that is playing audio, set the boost, press Start.";

            Controls.Add(appLbl); Controls.Add(combo); Controls.Add(refreshBtn);
            Controls.Add(volLbl); Controls.Add(slider); Controls.Add(boostLbl);
            Controls.Add(startBtn); Controls.Add(status);

            uiTimer.Interval = 1000;
            uiTimer.Tick += delegate
            {
                UpdateStatus();
                tickN++;
                if (tickN % 5 == 0) // periodic self-heal sweep (repairs orphaned ducks)
                {
                    string healed = null;
                    try { healed = StateFile.SelfHeal(); } catch { }
                    if (healed != null && (engine == null || engine.Stopped))
                        status.Text = "Restored mixer volume for: " + healed + ".";
                }
            };
            uiTimer.Start();

            Load += delegate
            {
                string healed = StateFile.SelfHeal();
                FillSessions();
                if (healed != null) status.Text = "Restored mixer volume for: " + healed + " (left ducked by a previous run).";
            };
            FormClosing += delegate { if (engine != null) engine.Stop(true); };
        }

        void FillSessions()
        {
            combo.Items.Clear(); comboPids.Clear();
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
                    if (!names.ContainsKey(pid))
                    {
                        string name = Native.ProcessNameOf(pid);
                        if (name == null) continue;
                        pids.Add(pid); names[pid] = name; playing[pid] = false;
                    }
                    if (st == K.StateActive) playing[pid] = true;
                }
                // actively-playing apps first, then alphabetical
                pids.Sort(delegate(uint a, uint b)
                {
                    if (playing[a] != playing[b]) return playing[a] ? -1 : 1;
                    return string.Compare(names[a], names[b], StringComparison.OrdinalIgnoreCase);
                });
                foreach (uint pid in pids)
                {
                    comboPids.Add(pid);
                    combo.Items.Add(names[pid] + "  (pid " + pid + (playing[pid] ? ", playing)" : ")"));
                }
                if (combo.Items.Count > 0 && combo.SelectedIndex < 0) combo.SelectedIndex = 0;
                if (combo.Items.Count == 0) status.Text = "No apps with audio sessions found - start playback in the app first, then Refresh.";
            }
            catch (Exception ex) { status.Text = "Session list failed: " + ex.Message; }
        }

        void ToggleBoost()
        {
            if (engine != null && !engine.Stopped)
            {
                engine.Stop(true);
                engine = null;
                startBtn.Text = "Start boost";
                status.Text = "Stopped - mixer volume restored.";
                return;
            }
            if (combo.SelectedIndex < 0) { status.Text = "Pick an application first."; return; }
            uint pid = comboPids[combo.SelectedIndex];
            try
            {
                engine = new BoostEngine(pid, slider.Value);
                engine.StoppedEvent += delegate
                {
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            startBtn.Text = "Start boost";
                            status.Text = "Stopped: " + engine.StopReason;
                            engine = null;
                            FillSessions();
                        });
                    }
                    catch { }
                };
                engine.Start();
                startBtn.Text = "Stop boost";
                status.Text = "Boosting " + engine.TargetExe + ".exe at " + slider.Value + "% (latency ~" + engine.LatencyMs + " ms).";
            }
            catch (Exception ex)
            {
                engine = null;
                status.Text = "Start failed: " + ex.Message;
            }
        }

        void UpdateStatus()
        {
            if (engine == null || engine.Stopped) return;
            status.Text = "Boosting " + engine.TargetExe + ".exe at " + slider.Value +
                "%   relayed " + (engine.RenFrames / 48000) + " s, glitches " + engine.Glitches +
                ", latency ~" + engine.LatencyMs + " ms  (app's mixer slider held at 4% by design)";
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
            uint pid = 0; string name = null; int boostPct = 150; double seconds = 0; string log = null; int padMs = 0;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--pid" && i + 1 < args.Length) pid = uint.Parse(args[++i]);
                else if (a == "--name" && i + 1 < args.Length) name = args[++i];
                else if (a == "--boost" && i + 1 < args.Length) boostPct = int.Parse(args[++i]);
                else if (a == "--seconds" && i + 1 < args.Length) seconds = double.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (a == "--padms" && i + 1 < args.Length) padMs = int.Parse(args[++i]);
                else if (a == "--log" && i + 1 < args.Length) log = args[++i];
            }
            try
            {
                StateFile.SelfHeal();
                if (pid == 0 && name != null)
                {
                    Process[] ps = Process.GetProcessesByName(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name);
                    if (ps.Length == 0) throw new ArgumentException("no process named " + name);
                    pid = (uint)ps[0].Id;
                }
                if (pid == 0) throw new ArgumentException("--pid or --name required");

                BoostEngine eng = new BoostEngine(pid, boostPct);
                if (padMs > 0) eng.SetInitialPadMs(padMs);
                ManualResetEvent stopped = new ManualResetEvent(false);
                eng.StoppedEvent += delegate { stopped.Set(); };
                eng.Start();
                if (seconds > 0) stopped.WaitOne((int)(seconds * 1000));
                else stopped.WaitOne();
                string why = eng.StopReason;
                eng.Stop(true);
                string line = string.Format(CultureInfo.InvariantCulture,
                    "ok capSamples={0} renFrames={1} glitches={2} boost={3} latencyMs={4} stopReason={5}",
                    eng.CapSamples, eng.RenFrames, eng.Glitches, boostPct, eng.LatencyMs, why == "" ? "timer" : why);
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
