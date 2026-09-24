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
//       | --all  [--system-sounds|--system] [--boost 100..500] [--seconds <S>]
//       [--padms <10..150>] [--log <file>]
//   Anything not in that list is rejected, not ignored. This list is mirrored in
//   VOLUME-BOOSTER.md - keep the parser, this comment and that file in step.
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
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr props);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(uint shareMode, uint streamFlags, long bufferDuration, long periodicity, ref WaveFormatEx format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(uint shareMode, ref WaveFormatEx format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devPos, out ulong qpcPos);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint streamFlags, out IAudioSessionControl sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint streamFlags, out ISimpleAudioVolume audioVolume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
        [PreserveSig] int RegisterSessionNotification(IAudioSessionNotification n);
        [PreserveSig] int UnregisterSessionNotification(IAudioSessionNotification n);
        [PreserveSig] int RegisterDuckNotification(IntPtr s, IntPtr n);
        [PreserveSig] int UnregisterDuckNotification(IntPtr n);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr ctx);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr ctx);
        [PreserveSig] int GetGroupingParam(out Guid g);
        [PreserveSig] int SetGroupingParam(ref Guid g, IntPtr ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr e);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr e);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr ctx);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr ctx);
        [PreserveSig] int GetGroupingParam(out Guid g);
        [PreserveSig] int SetGroupingParam(ref Guid g, IntPtr ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr e);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr e);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint pid);
        // [PreserveSig] is REQUIRED here. Without it the CLR marshals this as
        // HRESULT IsSystemSoundsSession([out,retval] int*) - the native method takes no
        // such pointer and never writes it, so the value read back is always 0, i.e.
        // "yes, system sounds" for EVERY session. That made WantSession reject every
        // app in per-app mode, so nothing was ducked while it was still captured and
        // multiplied by boost/0.04 - constant gross distortion. Boost-all was immune
        // because it returns true before ever reaching the system-sounds test.
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, IntPtr ctx);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute(bool mute, IntPtr ctx);
        [PreserveSig] int GetMute(out bool mute);
    }

    [ComImport, Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionNotification
    {
        [PreserveSig] int OnSessionCreated(IAudioSessionControl newSession);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation op);
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

        // Now that every COM method above carries [PreserveSig], these are REAL HRESULTs,
        // and an HRESULT is a failure only when it is negative. Several of the calls made
        // here legitimately return a non-zero SUCCESS code - S_FALSE (1) from Stop() on an
        // already-stopped client, AUDCLNT_S_BUFFER_EMPTY (0x08890001) from GetBuffer - so
        // testing "hr != 0" would start throwing on perfectly good returns. Before the
        // PreserveSig pass this function could never fire at all: the value handed back was
        // always 0 regardless of what really happened, and genuine failures surfaced as
        // COMExceptions thrown by the marshaller instead.
        public static void Check(int hr, string what)
        {
            if (hr < 0) throw new COMException(what + " failed (hr=0x" + hr.ToString("X8") + ")", hr);
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
                    // BLOB { ULONG cbSize; BYTE* pBlobData }: the pointer follows cbSize at its
                    // natural alignment - offset 12 in a 32-bit process, 16 in a 64-bit one.
                    // Hard-coding 16 left pBlobData NULL in a 32-bit process (on 32-bit
                    // Windows 10), so activation failed there.
                    Marshal.WriteIntPtr(pv, 8 + IntPtr.Size, blob);
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

        // Every ACTIVE render endpoint, the default one first. Process-loopback capture is
        // not tied to an endpoint - Microsoft's Application Loopback sample says so in so
        // many words - so an app playing on a second output is captured like any other.
        // Ducking therefore has to cover every output as well; when it only looked at the
        // default device, an app routed elsewhere was heard at full volume PLUS a copy
        // multiplied by boost/DUCK.
        public static List<IMMDevice> ActiveRenderDevices()
        {
            List<IMMDevice> r = new List<IMMDevice>();
            string defId = null;
            try { IMMDevice def = DefaultRenderDevice(); def.GetId(out defId); r.Add(def); }
            catch { }
            IntPtr pc;
            if (Enumerator().EnumAudioEndpoints(K.eRender, 1 /* DEVICE_STATE_ACTIVE */, out pc) < 0 || pc == IntPtr.Zero) return r;
            try
            {
                IMMDeviceCollection col = (IMMDeviceCollection)Marshal.GetObjectForIUnknown(pc);
                uint n; if (col.GetCount(out n) < 0) return r;
                for (uint i = 0; i < n; i++)
                {
                    IMMDevice dv; if (col.Item(i, out dv) < 0 || dv == null) continue;
                    string id; dv.GetId(out id);
                    if (id != null && id == defId) continue;
                    r.Add(dv);
                }
            }
            finally { Marshal.Release(pc); }
            return r;
        }

        public static List<IAudioSessionManager2> ActiveSessionManagers()
        {
            List<IAudioSessionManager2> r = new List<IAudioSessionManager2>();
            foreach (IMMDevice dv in ActiveRenderDevices())
            {
                try { r.Add(SessionManager(dv)); }
                catch { }
            }
            return r;
        }

        // Every audio session on every active output.
        public static List<IAudioSessionControl> AllRenderSessions()
        {
            List<IAudioSessionControl> r = new List<IAudioSessionControl>();
            foreach (IAudioSessionManager2 m in ActiveSessionManagers())
            {
                try
                {
                    IAudioSessionEnumerator en; if (m.GetSessionEnumerator(out en) < 0) continue;
                    int count; en.GetCount(out count);
                    for (int i = 0; i < count; i++)
                    {
                        IAudioSessionControl sc; if (en.GetSession(i, out sc) != 0 || sc == null) continue;
                        r.Add(sc);
                    }
                }
                catch { }
            }
            return r;
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

        // Drops the oldest samples so at most keep remain (kept even: stereo frames).
        // Returns how many were dropped.
        public int TrimTo(int keep)
        {
            lock (gate)
            {
                keep -= keep % 2;
                if (rCount <= keep) return 0;
                int drop = rCount - keep;
                rTail = (rTail + drop) % ring.Length;
                rCount = keep;
                return drop;
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
        // Next to the exe when that folder is writable (as documented), otherwise under
        // %LocalAppData%\AppVolumeBooster. The fallback used to apply only if the exe's folder
        // could not even be determined; a folder that merely was not writable (a write-
        // protected stick, Program Files for a standard user) made every write fail inside
        // an empty catch - crash recovery silently switched off. The choice depends only on
        // that folder, so every instance of the same exe agrees on it.
        static string cachedPath;
        static string PathOf()
        {
            if (cachedPath != null) return cachedPath;
            string dir = null;
            try { dir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location); }
            catch { }
            if (dir == null || !CanWrite(dir))
            {
                dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppVolumeBooster");
                try { Directory.CreateDirectory(dir); }
                catch { }
            }
            cachedPath = System.IO.Path.Combine(dir, "booster-state.txt");
            return cachedPath;
        }

        // Probes with a throwaway file that deletes itself on close - never the state file.
        static bool CanWrite(string dir)
        {
            try
            {
                string probe = System.IO.Path.Combine(dir, "booster-state.probe." + Process.GetCurrentProcess().Id.ToString());
                using (new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                return true;
            }
            catch { return false; }
        }

        static Mutex Mtx()
        {
            return new Mutex(false, "AppVolumeBooster.StateFile");
        }

        // Runs 'work' under the cross-process state-file lock, and reports whether the
        // lock was actually taken. The timeout used to be ignored: on a contended
        // machine two boosters could each read, then each write, and the loser's line -
        // a slider still ducked at 4% - simply vanished. Skipping the update is the safe
        // failure, because SelfHeal re-checks the real slider level before acting anyway.
        // An abandoned mutex means the previous owner died holding it; we hold it now.
        static bool UnderLock(ThreadStart work)
        {
            Mutex m = Mtx();
            bool held = false;
            try
            {
                try { held = m.WaitOne(3000); }
                catch (AbandonedMutexException) { held = true; }
                catch { held = false; }
                if (!held) return false;
                try { work(); }
                catch { }
                return true;
            }
            finally
            {
                if (held) { try { m.ReleaseMutex(); } catch { } }
                m.Close();
            }
        }

        // Temp file + Replace. This file exists precisely so that killing the booster
        // cannot strand a ducked slider, so it must never be caught half-written by
        // exactly that. On failure the previous contents survive, which is the safe
        // direction: a stale line gets re-checked against the live slider, a lost one
        // is gone for good.
        static void WriteAll(List<string> lines)
        {
            string path = PathOf();
            if (lines.Count == 0) { try { File.Delete(path); } catch { } return; }
            string tmp = path + ".tmp";
            try
            {
                File.WriteAllLines(tmp, lines.ToArray());
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch { }
            }
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
            UnderLock(delegate()
            {
                List<string> lines = ReadAll();
                string own = OwnPid();
                lines.RemoveAll(delegate(string l)
                {
                    string[] parts = l.Split('|');
                    return parts.Length == 3 && parts[0] == own && SameTarget(parts[1], targetExe);
                });
                lines.Add(own + "|" + targetExe + "|" + prior.ToString("F4", CultureInfo.InvariantCulture));
                WriteAll(lines);
            });
        }

        public static void RemoveOwn()
        {
            UnderLock(delegate()
            {
                List<string> lines = ReadAll();
                string own = OwnPid();
                lines.RemoveAll(delegate(string l) { return l.StartsWith(own + "|"); });
                WriteAll(lines);
            });
        }

        public static void RemoveTarget(string targetExe)
        {
            UnderLock(delegate()
            {
                List<string> lines = ReadAll();
                string own = OwnPid();
                lines.RemoveAll(delegate(string l)
                {
                    string[] parts = l.Split('|');
                    return parts.Length == 3 && parts[0] == own && SameTarget(parts[1], targetExe);
                });
                WriteAll(lines);
            });
        }

        // Mark matching own lines as orphaned (boosterPid 0) so any later SelfHeal
        // keeps trying to restore the slider until the app is seen again.
        public static void OrphanOwn()
        {
            RewriteOwnPrefix("0");
        }

        public static void OrphanTarget(string targetExe)
        {
            UnderLock(delegate()
            {
                List<string> lines = ReadAll();
                string own = OwnPid();
                for (int i = 0; i < lines.Count; i++)
                {
                    string[] parts = lines[i].Split('|');
                    if (parts.Length == 3 && parts[0] == own && SameTarget(parts[1], targetExe))
                        lines[i] = "0" + lines[i].Substring(own.Length);
                }
                WriteAll(lines);
            });
        }

        static void RewriteOwnPrefix(string newPid)
        {
            UnderLock(delegate()
            {
                List<string> lines = ReadAll();
                string own = OwnPid();
                for (int i = 0; i < lines.Count; i++)
                    if (lines[i].StartsWith(own + "|")) lines[i] = newPid + lines[i].Substring(own.Length);
                WriteAll(lines);
            });
        }

        // The prior some booster recorded for this target, if any line mentions it - its
        // own earlier line, another live instance's, or an orphan left by a crash. Read
        // without the lock on purpose: WriteAll replaces the file atomically, so a reader
        // sees either the old or the new version, never a torn one.
        public static bool TryGetRecordedPrior(string targetExe, out float prior)
        {
            prior = 1.0f;
            foreach (string l in ReadAll())
            {
                string[] parts = l.Split('|');
                if (parts.Length != 3 || !SameTarget(parts[1], targetExe)) continue;
                float p;
                if (float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out p)) { prior = p; return true; }
            }
            return false;
        }

        static List<string> ReadAll()
        {
            List<string> r = new List<string>();
            try { if (File.Exists(PathOf())) r.AddRange(File.ReadAllLines(PathOf())); }
            catch { }
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
            List<string> healedNames = new List<string>();
            UnderLock(delegate()
            {
                List<string> lines = ReadAll();
                if (lines.Count == 0) return;
                List<string> keep = new List<string>();
                List<string[]> dead = new List<string[]>();
                List<string> heldByLive = new List<string>();
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
                    if (alive) { keep.Add(l); heldByLive.Add(parts[1]); } else dead.Add(parts);
                }
                if (dead.Count > 0)
                {
                    bool[] resolved = new bool[dead.Count];
                    try
                    {
                        List<IAudioSessionControl> all = Native.AllRenderSessions();
                        for (int i = 0; i < all.Count; i++)
                        {
                            IAudioSessionControl sc = all[i];
                            IAudioSessionControl2 sc2 = (IAudioSessionControl2)sc;
                            uint spid; sc2.GetProcessId(out spid);
                            string sname = Native.ProcessNameOf(spid);
                            bool isSys = Native.IsSystemSounds(sc2) || spid == 0;
                            for (int di = 0; di < dead.Count; di++)
                            {
                                if (resolved[di]) continue;
                                string[] parts = dead[di];
                                if (!LineMatchesSession(parts[1], spid, sname, isSys)) continue;
                                // Sessions are matched by app NAME, so a dead line can match a
                                // session that a LIVE booster is holding at 4% right now - this
                                // instance or another. "Healing" that used to un-duck it while it
                                // was still being captured: a burst at full volume plus the
                                // boosted copy until the next re-duck. Leave the line for later;
                                // the live booster restores that app itself when it stops.
                                bool held = false;
                                foreach (string hx in heldByLive) if (SameTarget(hx, parts[1])) { held = true; break; }
                                if (held) continue;
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
                WriteAll(keep);
            });
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
        volatile int targetPadFrames = 2400;  // standing render queue: 50 ms; +20 ms per dropout, max 150
        Thread renThread;
        System.Threading.Timer watcher;
        IAudioClient renClient;
        AutoResetEvent renEv;
        readonly List<IAudioSessionManager2> liveMgrs = new List<IAudioSessionManager2>();
        SessionCreatedHandler sessionNote;
        string devId;
        volatile bool sysCapOk;
        // Set by the render thread when the output keeps failing; the watcher turns it
        // into a normal stop (the render thread must not stop the engine itself: Stop
        // joins that very thread).
        volatile string fatalReason;
        int renFailStreak;
        const string AllAudioMutexName = "AppVolumeBooster.AllAudio";
        Mutex allAudioMutex;                  // held for the whole of an all-audio boost
        volatile bool anyTargetGone;
        HashSet<uint> wantPidsSnapshot = new HashSet<uint>();

        class Ducked
        {
            public ISimpleAudioVolume Vol;
            public float Prior;
            public uint Pid;
            public string Exe;
            public string InstanceId;
            public bool Muted;                // as found - recorded, never changed
        }
        readonly List<Ducked> ducked = new List<Ducked>();
        readonly object duckLock = new object();

        public long CapSamples, RenFrames, Glitches, TrimmedSamples;
        // Backlog allowed to stay in a capture ring after each render pass: 40 ms. Normal
        // jitter is one or two 10 ms packets, so steady-state audio never reaches this.
        const int MaxBacklogSamples = 1920 * 2;
        public volatile bool Stopped = true;
        // Exactly one caller performs a shutdown. Stop() used to guard itself with
        // "if (Stopped) return; Stopped = true;" - two statements, not atomic - and it is
        // reached from the UI thread, the target-exit handler and the watcher (whose ticks
        // can overlap). Two concurrent shutdowns raced over the process list and could throw
        // "Collection was modified" on a thread-pool thread, which ends the process.
        // 1 = claimed (initially: nothing to stop until StartCore resets it).
        int stopClaim = 1;
        readonly ManualResetEvent stopDone = new ManualResetEvent(true);
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
        // The boost actually in force after clamping. The CLI used to log the
        // REQUESTED percentage, so --boost 5000 logged 5000 while running at 500.
        public int BoostPercent { get { return (int)Math.Round((double)(boost * 100.0f)); } }
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
            Interlocked.Exchange(ref stopClaim, 0);
            stopDone.Reset();
            running = true;
            Stopped = false;
            StopReason = "";
            StartWarning = "";
            anyTargetGone = false;
            sysCapOk = false;
            ClaimExclusivity();

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
                    if (SelfInTreeOf(root))
                    {
                        capFails.Add((Native.ProcessNameOf(root) ?? root.ToString()) +
                            " (this booster is running inside that process tree, so capturing it would feed back on itself - "
                            + "start the booster outside that app, or use 'Boost all audio')");
                        continue;
                    }
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
            if (!boostAll)
            {
                List<string> muted = new List<string>();
                lock (duckLock)
                {
                    foreach (Ducked dk in ducked)
                    {
                        if (!dk.Muted) continue;
                        string nm = dk.Exe == K.SysSoundsName ? "system sounds" : dk.Exe;
                        if (!muted.Contains(nm)) muted.Add(nm);
                    }
                }
                if (muted.Count > 0)
                    StartWarning = (StartWarning == "" ? "" : StartWarning + " ") +
                        "Muted in the Volume Mixer, so silent until you unmute it there: " +
                        string.Join(", ", muted.ToArray()) + ".";
            }
            foreach (IAudioClient cap in pendingCaps)
                FinishCapture(cap, fmt);

            IMMDevice dev = Native.DefaultRenderDevice();
            // a new session on ANY active output is ducked the moment it appears, rather
            // than up to one watcher period later at full volume
            sessionNote = new SessionCreatedHandler();
            sessionNote.Fn = DuckOne;
            foreach (IAudioSessionManager2 m in Native.ActiveSessionManagers())
            {
                try { Native.Check(m.RegisterSessionNotification(sessionNote), "RegisterSessionNotification"); liveMgrs.Add(m); }
                catch { }
            }

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
                    // != 0 is deliberate and stays: the only non-zero success here is
                    // AUDCLNT_S_BUFFER_EMPTY, and breaking out on "no data" is exactly
                    // what we want, same as breaking out on a genuine error.
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

        // With [PreserveSig] the render calls return HRESULTs instead of throwing, and the
        // loop used to just 'continue' on any error - so a dead output (device invalidated,
        // taken over in exclusive mode, audio service restarted) left the target ducked at
        // 4% and silent while the window kept saying "Boosting". ~2 s of nothing but
        // failures (the loop wakes at least every 100 ms) now ends the boost properly.
        void NoteRenderResult(int hr)
        {
            if (hr >= 0) { renFailStreak = 0; return; }
            if (++renFailStreak >= 20 && fatalReason == null)
                fatalReason = "the audio output stopped responding (0x" + hr.ToString("X8") + ") - press Start again";
        }

        void RenderLoop(IAudioRenderClient render, uint renBuf)
        {
            uint idx = 0;
            try { Native.AvSetMmThreadCharacteristics("Pro Audio", ref idx); } catch { }
            float[] mix = new float[renBuf * 2];
            float[] tmp = new float[renBuf * 2];
            SampleRing[] rings = mixRings.ToArray();
            // Until real audio flows - at start, and again after a dropout - the queue is held
            // at the target with silence, so the first audio lands behind a full queue.
            bool flowing = false;
            long lastWrite = 0;          // Stopwatch timestamp of the last write
            uint queuedAtWrite = 0;      // frames queued right after it
            while (running)
            {
                renEv.WaitOne(100);
                // Stop() clears 'running' and then winds the capture threads down, so a pass
                // that starts after that finds the rings empty on purpose. It used to count
                // that as a glitch - the stray "glitches=1" at the end of many clean runs.
                if (!running) break;
                uint pad; int hrPad = renClient.GetCurrentPadding(out pad);
                NoteRenderResult(hrPad);
                if (hrPad != 0) continue;
                // An empty queue is a dropout only if the device really ran out. It takes one
                // 10 ms period at a time, so that is when more time has passed since the last
                // write than was queued then, plus a period (9 ms, leaving room for timing
                // jitter); a pass that is merely just in time finds the queue empty too, with
                // nothing lost. A real dropout - a stall on either thread: capture keeps
                // delivering, silence included, as long as this output runs, so it is never the
                // target just going quiet - is counted ONCE and raises the standing queue by
                // 20 ms ONCE. It used to be counted again for every 20 ms the queue stayed empty
                // (one 300 ms capture stall read as 14-15 glitches and pushed latency to the
                // 160 ms cap), and not at all when audio was waiting in the ring, which is
                // exactly what a stall of THIS thread leaves (a 130 ms gap, 0 glitches).
                if (flowing && pad == 0)
                {
                    long since = (Stopwatch.GetTimestamp() - lastWrite) * 48000 / Stopwatch.Frequency;
                    if (since - queuedAtWrite >= 432)
                    {
                        flowing = false;
                        Interlocked.Increment(ref Glitches);
                        int np = targetPadFrames + 960;
                        if (np > 7200) np = 7200;
                        targetPadFrames = np;
                    }
                }
                uint target = (uint)targetPadFrames;
                if (pad >= target) continue;
                uint room = renBuf - pad;
                uint want = target - pad; if (want > room) want = room;
                int samples = (int)want * 2;
                if (samples > mix.Length) { mix = new float[samples]; tmp = new float[samples]; }
                Array.Clear(mix, 0, samples);
                int maxGot = 0;
                for (int r = 0; r < rings.Length; r++)
                {
                    // Resuming after a dropout: start from the NEWEST audio. Anything older is
                    // late already, and playing it would add its age to the latency for good.
                    if (!flowing)
                    {
                        int late = rings[r].TrimTo(samples);
                        if (late > 0) Interlocked.Add(ref TrimmedSamples, late);
                    }
                    int n = rings[r].Pop(tmp, samples);
                    for (int i = 0; i < n; i++) mix[i] += tmp[i];
                    if (n > maxGot) maxGot = n;
                }
                // Whatever is still in a ring after the pass is capped at 40 ms. A stall of this
                // thread used to leave its whole backlog there for good - 260 ms of extra latency
                // after one 300 ms stall in testing; that case is now resynced above, and a
                // shorter stall is absorbed by the top-up. What is left is slow build-up (a
                // target on another device whose clock runs a little fast), and this caps it. A
                // stall on the CAPTURE side cannot build a backlog: process loopback discards
                // what it could not deliver in time.
                for (int r = 0; r < rings.Length; r++)
                {
                    int dropped = rings[r].TrimTo(MaxBacklogSamples);
                    if (dropped > 0) Interlocked.Add(ref TrimmedSamples, dropped);
                }
                uint gotFrames = (uint)(maxGot / 2);
                // Hold the queue at the target with silence until audio flows. Without this it
                // only ever held what the first pass happened to find in the ring: 0-20 ms
                // instead of the intended 50, and exactly 0 on every pass in most runs, so a
                // hiccup of more than a few ms could be heard - while the status line said ~60 ms.
                if (!flowing && want > gotFrames)
                {
                    uint silence = want - gotFrames;
                    IntPtr ps; int hrS = render.GetBuffer(silence, out ps);
                    NoteRenderResult(hrS);
                    if (hrS != 0) continue;
                    render.ReleaseBuffer(silence, K.BUF_SILENT);
                    pad += silence;
                    lastWrite = Stopwatch.GetTimestamp(); queuedAtWrite = pad;
                }
                if (gotFrames == 0) continue;
                for (int i = 0; i < maxGot; i++) mix[i] = Native.SoftClip(mix[i]);
                IntPtr p; int hrB = render.GetBuffer(gotFrames, out p);
                NoteRenderResult(hrB);
                if (hrB != 0) continue;
                Marshal.Copy(mix, 0, p, (int)gotFrames * 2);
                render.ReleaseBuffer(gotFrames, 0);
                Interlocked.Add(ref RenFrames, gotFrames);
                lastWrite = Stopwatch.GetTimestamp(); queuedAtWrite = pad + gotFrames;
                flowing = true;
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
                        string exe = isSys ? K.SysSoundsName : (sname ?? ("pid" + spid.ToString()));
                        float prior = lvl;
                        // A slider at the duck level only counts as "left ducked by a booster"
                        // when a booster actually recorded this app - then that recorded prior
                        // is the real one. Otherwise it is the user's own quiet setting and is
                        // kept. This used to turn ANY level at or below 4.5% into 100%, so an
                        // app you had set to 3% came back at full volume after a boost, and
                        // Windows then saved 100% as its remembered volume.
                        if (prior <= DUCK + 0.005f)
                        {
                            float recorded;
                            if (StateFile.TryGetRecordedPrior(exe, out recorded)) prior = recorded;
                        }
                        known = new Ducked();
                        known.Vol = v;
                        known.Prior = prior;
                        known.Pid = spid;
                        known.Exe = exe;
                        known.InstanceId = inst;
                        bool wasMuted;
                        known.Muted = v.GetMute(out wasMuted) == 0 && wasMuted;
                        ducked.Add(known);
                        StateFile.AddOwn(known.Exe, known.Prior);
                    }
                    // Mute is the user's decision and is never changed here. This used to
                    // unmute every session it ducked and never recorded that it had, so
                    // "Boost all audio" made apps you had deliberately muted audible (4% x
                    // boost/0.04 = normal volume) and left them unmuted after Stop. A muted
                    // target now stays silent while boosted - unmute it in the mixer.
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
                foreach (IAudioSessionControl sc in Native.AllRenderSessions()) DuckOne(sc);
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

        // True when THIS booster process sits inside the given target's process tree.
        //
        // Per-app capture is PROCESS_LOOPBACK with INCLUDE_PROCESS_TREE, so if the booster
        // is a descendant of the target it captures its own rendered output, multiplies it
        // by boost/DUCK, renders that, and captures it again: runaway feedback, at up to
        // 500%. Double-clicking the exe is safe (its parent is explorer), but launching it
        // from a shell or script and then boosting that shell walks straight into it.
        // "Boost all audio" is immune - it captures with EXCLUDE_PROCESS_TREE on itself.
        bool SelfInTreeOf(uint root)
        {
            HashSet<uint> tree = null;
            lock (targetLock)
            {
                if (treesByRoot.ContainsKey(root)) tree = treesByRoot[root];
            }
            if (tree == null) tree = DescendantsOf(root, ParentMap());
            return tree.Contains(selfPid);
        }

        // "Boost all audio" captures everything except THIS process - which includes the
        // boosted output of any other booster instance, so that copy was boosted a second
        // time (and two all-audio instances would feed each other). The combination is
        // refused in both directions: an all-audio boost holds a named mutex that makes any
        // other boost refuse to start, and it will not start while another booster is
        // actively playing. Called first in StartCore, before anything is ducked.
        void ClaimExclusivity()
        {
            if (boostAll)
            {
                bool createdNew;
                Mutex m = new Mutex(false, AllAudioMutexName, out createdNew);
                if (!createdNew)
                {
                    m.Close();
                    throw new InvalidOperationException("another booster is already boosting all audio - stop it first");
                }
                string other = OtherActiveBooster();
                if (other != null)
                {
                    m.Close();
                    throw new InvalidOperationException("another booster is running (" + other + "); 'Boost all audio' would capture its boosted output and boost it a second time - stop it first");
                }
                allAudioMutex = m;
            }
            else
            {
                Mutex m;
                if (Mutex.TryOpenExisting(AllAudioMutexName, out m))
                {
                    m.Close();
                    throw new InvalidOperationException("another booster is boosting all audio, which would capture this boost's output and boost it again - stop that one first");
                }
            }
        }

        string OtherActiveBooster()
        {
            foreach (IAudioSessionControl sc in Native.AllRenderSessions())
            {
                try
                {
                    IAudioSessionControl2 sc2 = (IAudioSessionControl2)sc;
                    uint spid; sc2.GetProcessId(out spid);
                    if (spid == selfPid) continue;
                    int st; sc2.GetState(out st);
                    if (st != K.StateActive) continue;
                    string n = Native.ProcessNameOf(spid);
                    if (Native.IsBoosterName(n)) return n + " pid " + spid.ToString();
                }
                catch { }
            }
            return null;
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
                string fatal = fatalReason;
                if (fatal != null) { StopBecause(fatal); return; }
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
            // Another stop already owns the shutdown - it restores everything and reports.
            if (Interlocked.CompareExchange(ref stopClaim, 1, 0) != 0) return;
            StopReason = reason;
            StopClaimed(true);
            EventHandler h = StoppedEvent;
            if (h != null) h(this, EventArgs.Empty);
        }

        public void Stop(bool restoreVolumes)
        {
            if (Interlocked.CompareExchange(ref stopClaim, 1, 0) != 0)
            {
                // Somebody else is already stopping (target closed, output changed, the
                // render thread gave up). WAIT for them instead of returning at once: closing
                // the window on an early return used to end the process while that other
                // stop was still restoring the sliders on a background thread.
                stopDone.WaitOne(5000);
                return;
            }
            StopClaimed(restoreVolumes);
        }

        void StopClaimed(bool restoreVolumes)
        {
            try
            {
                Stopped = true;
                running = false;
                try { if (watcher != null) watcher.Dispose(); } catch { }
                foreach (Thread t in capThreads)
                    try { t.Join(500); } catch { }
                try { if (renThread != null) renThread.Join(500); } catch { }
                try { System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Interactive; } catch { }
                Native.RunMta(delegate() { StopComCore(restoreVolumes); });
            }
            finally { stopDone.Set(); }
        }

        void StopComCore(bool restoreVolumes)
        {
            foreach (IAudioSessionManager2 m in liveMgrs)
            {
                try { if (sessionNote != null) m.UnregisterSessionNotification(sessionNote); }
                catch { }
            }
            liveMgrs.Clear();
            sessionNote = null;
            if (allAudioMutex != null) { try { allAudioMutex.Close(); } catch { } allAudioMutex = null; }
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
                List<IAudioSessionControl> all = Native.AllRenderSessions();
                List<uint> pids = new List<uint>();
                Dictionary<uint, string> names = new Dictionary<uint, string>();
                Dictionary<uint, bool> playing = new Dictionary<uint, bool>();
                for (int i = 0; i < all.Count; i++)
                {
                    IAudioSessionControl sc = all[i];
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
            // Any engine at all means the button currently reads "Stop boost", so this click
            // is a stop - even when the engine already stopped itself (target closed, output
            // changed) and its notification is still queued. This used to test !Stopped, so
            // a click in that window STARTED a new boost, which the queued notification then
            // orphaned by nulling the field: a boost the window could no longer stop, left
            // running (and its targets ducked) after the window closed.
            if (engine != null)
            {
                string why = (engine.Stopped && engine.StopReason != "") ? engine.StopReason : null;
                engine.Stop(true);
                engine = null;
                startBtn.Text = "Start boost";
                StyleActionButton(false);
                SetBusy(false);
                status.Text = why != null ? "Stopped: " + why : "Stopped - mixer volume restored.";
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
                BoostEngine mine = new BoostEngine(pids, slider.Value, all, sys);
                engine = mine;
                mine.StoppedEvent += delegate
                {
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            // Only the engine that is still current may reset the UI. A stale
                            // notification from an earlier engine must never touch a newer one.
                            if (!object.ReferenceEquals(engine, mine)) return;
                            startBtn.Text = "Start boost";
                            StyleActionButton(false);
                            status.Text = "Stopped: " + mine.StopReason;
                            engine = null;
                            SetBusy(false);
                            FillSessions();
                        });
                    }
                    catch { }
                };
                mine.Start();
                startBtn.Text = "Stop boost";
                StyleActionButton(true);
                SetBusy(true);
                string warn = mine.StartWarning;
                status.Text = "Boosting " + mine.TargetSummary + " at " + slider.Value + "% (latency ~" + mine.LatencyMs + " ms)."
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

        // Consumes the value that follows a flag. A flag at the end of the line has
        // no value; so does one followed by another flag ("--boost --log x"), which
        // would otherwise be parsed AS the value and fail with a raw FormatException.
        static string NextValue(string flag, string[] args, ref int i)
        {
            if (i + 1 >= args.Length) throw new ArgumentException("missing value after " + flag);
            string v = args[i + 1];
            if (v.StartsWith("--")) throw new ArgumentException("missing value after " + flag + " (next token is " + v + ")");
            i++;
            return v;
        }

        // A log write must never throw: it used to happen inside the catch block while
        // reporting the real error, so an unwritable --log turned any failure into an
        // unhandled exception - a crash dialog in the middle of a script.
        static bool TryLog(string path, string text)
        {
            if (path == null) return false;
            try { File.WriteAllText(path, text); return true; }
            catch { return false; }
        }

        static int CliMain(string[] args)
        {
            List<uint> pids = new List<uint>();
            List<string> names = new List<string>();
            int boostPct = 150; double seconds = 0; string log = null; int padMs = 0;
            bool all = false; bool sys = false;

            // --log is resolved before anything can throw: this is a /t:winexe with no
            // console attached, so the log file is the only channel an argument error
            // has to reach the caller at all.
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], "--log", StringComparison.OrdinalIgnoreCase)) log = args[i + 1];

            // Exit 3 = the log cannot be written - checked before anything is started, since
            // no result could be reported afterwards.
            if (log != null && !TryLog(log, "")) return 3;

            try
            {
                // Parsing lives inside the try so a malformed number is reported rather
                // than crashing, and an unrecognised flag is rejected rather than
                // dropped: "--bost 300" used to run silently at the default 150.
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i].ToLowerInvariant();
                    if (a == "--pid") pids.Add(uint.Parse(NextValue(a, args, ref i), CultureInfo.InvariantCulture));
                    else if (a == "--name") names.Add(NextValue(a, args, ref i));
                    else if (a == "--boost") boostPct = int.Parse(NextValue(a, args, ref i), CultureInfo.InvariantCulture);
                    else if (a == "--seconds")
                    {
                        seconds = double.Parse(NextValue(a, args, ref i), CultureInfo.InvariantCulture);
                        // Beyond ~24.8 days (seconds * 1000) overflows an int, the wait below then
                        // threw AFTER the engine had started - and the process exited with the
                        // targets still ducked. Negative or NaN values waited forever.
                        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0 || seconds > int.MaxValue / 1000.0)
                            throw new ArgumentException("--seconds must be between 0 and " + (int.MaxValue / 1000).ToString() + " (0 = until the target closes)");
                    }
                    else if (a == "--padms") padMs = int.Parse(NextValue(a, args, ref i), CultureInfo.InvariantCulture);
                    else if (a == "--log") log = NextValue(a, args, ref i);
                    else if (a == "--all") all = true;
                    else if (a == "--system-sounds" || a == "--system") sys = true;
                    else throw new ArgumentException("unknown argument: " + args[i]);
                }

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
                string why;
                try
                {
                    if (seconds > 0) stopped.WaitOne((int)(seconds * 1000));
                    else stopped.WaitOne();
                    why = eng.StopReason;
                }
                finally { eng.Stop(true); }   // on every path: never exit with targets ducked
                string line = string.Format(CultureInfo.InvariantCulture,
                    "ok capSamples={0} renFrames={1} glitches={2} trimmedMs={8} boost={3} latencyMs={4} targets={5} stopReason={6}{7}",
                    eng.CapSamples, eng.RenFrames, eng.Glitches, eng.BoostPercent, eng.LatencyMs, eng.TargetSummary, why == "" ? "timer" : why,
                    eng.StartWarning == "" ? "" : " warning=" + eng.StartWarning,
                    Interlocked.Read(ref eng.TrimmedSamples) / 96);
                TryLog(log, line + "\r\n");
                return 0;
            }
            catch (Exception ex)
            {
                TryLog(log, "ERROR " + ex + "\r\n");
                return 1;
            }
        }
    }
}
