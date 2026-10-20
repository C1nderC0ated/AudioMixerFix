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
//      The direct path sits 28 dB (at 100%) to 42 dB (at 500%) below the boosted copy and
//      ahead of it by the latency, 60-160 ms: a faint pre-echo on headphones with sharp
//      transients.
//   3. A soft clipper above 0.95 full-scale prevents harsh digital clipping.
//   4. On stop / last target exit / booster exit the mixer sliders are restored. A
//      state file in %LocalAppData%\AppVolumeBooster (one per user, shared by every
//      copy of the exe) self-heals volumes if the booster was killed hard.
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
        public const uint NOPERSIST = 0x00080000;
        public const uint BUF_SILENT = 2;
        public const int StateActive = 1, StateExpired = 2;
        public const string SysSoundsName = "#SystemSounds";
        // Chromium browsers release their audio session about 5 s after the last tab goes
        // quiet, so the session list alone shows them only while they play.
        public static readonly string[] QuietBrowsers = { "chrome", "msedge", "thorium", "brave", "vivaldi", "opera", "browser" };
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
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, IntPtr ctx);
        [PreserveSig] int GetMasterVolume(out float level);
        // BOOL is 4 bytes; a plain C# bool in a [ComImport] interface marshals as a 2-byte
        // VARIANT_BOOL, which read the right answer only by luck of the stack padding.
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
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

        [DllImport("kernel32.dll")]
        static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")]
        static extern bool GetExitCodeProcess(IntPtr h, out uint code);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, System.Text.StringBuilder name, ref uint size);
        [DllImport("kernel32.dll")]
        static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool Process32FirstW(IntPtr snap, ref ProcessEntry32 e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool Process32NextW(IntPtr snap, ref ProcessEntry32 e);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ProcessEntry32
        {
            public uint dwSize, cntUsage, th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

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

        // Rounded corners and a border in the window's own colour, for the tray panel.
        // Windows 11 only; older versions ignore both attributes.
        public static void UseRoundCorners(IntPtr hwnd, Color border)
        {
            if (hwnd == IntPtr.Zero) return;
            int round = 2;                                             // DWMWCP_ROUND
            int colour = border.R | (border.G << 8) | (border.B << 16); // COLORREF
            try
            {
                DwmSetWindowAttribute(hwnd, 33, ref round, 4);          // DWMWA_WINDOW_CORNER_PREFERENCE
                DwmSetWindowAttribute(hwnd, 34, ref colour, 4);         // DWMWA_BORDER_COLOR
            }
            catch { }
        }

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr h);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr h);

        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(int pid);

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

        // The exe name without ".exe", or null when no such process is running - what
        // Process.ProcessName gives. That property was used directly, and Process.GetProcessById
        // takes a snapshot of every process and thread on the machine to answer for one pid:
        // 2-3 ms and 84 KB of garbage per call, several calls per audio session, on every
        // pass of the watcher (measured: 38 ms per pass with 12 sessions). The name is now
        // asked of the one process; the old way remains for a process that cannot be opened.
        public static string ProcessNameOf(uint pid)
        {
            if (pid == 0) return null;
            IntPtr h = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
            if (h != IntPtr.Zero)
            {
                try
                {
                    uint code;
                    // exited, and only still there because someone holds a handle to it
                    if (GetExitCodeProcess(h, out code) && code != 259) return null;
                    System.Text.StringBuilder sb = new System.Text.StringBuilder(1024);
                    uint n = (uint)sb.Capacity;
                    if (QueryFullProcessImageNameW(h, 0, sb, ref n) && n > 0)
                    {
                        string f = sb.ToString();
                        int slash = f.LastIndexOf('\\');
                        if (slash >= 0) f = f.Substring(slash + 1);
                        if (f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) f = f.Substring(0, f.Length - 4);
                        if (f.Length > 0) return f;
                    }
                }
                catch { }
                finally { CloseHandle(h); }
            }
            try { return Process.GetProcessById((int)pid).ProcessName; }
            catch { return null; }
        }

        // The full path of a process's exe, or null (not running, or it may not be asked).
        public static string ProcessPathOf(uint pid)
        {
            if (pid == 0) return null;
            IntPtr h = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
            if (h == IntPtr.Zero) return null;
            try
            {
                uint code;
                if (GetExitCodeProcess(h, out code) && code != 259) return null;
                System.Text.StringBuilder sb = new System.Text.StringBuilder(1024);
                uint n = (uint)sb.Capacity;
                if (QueryFullProcessImageNameW(h, 0, sb, ref n) && n > 0) return sb.ToString();
            }
            catch { }
            finally { CloseHandle(h); }
            return null;
        }

        // pid -> parent pid of every process. This was a WMI query: 350-780 ms for the first
        // one in a process (most of the wait between pressing Start and the boost), 50-120 ms
        // for each later one - and one is taken whenever an unknown process opens an audio
        // session, while the ducking lock is held. A Toolhelp snapshot gives the same two
        // numbers in a few milliseconds; WMI is only asked if that snapshot cannot be taken.
        public static Dictionary<uint, uint> ParentMap()
        {
            Dictionary<uint, uint> parent = new Dictionary<uint, uint>();
            IntPtr snap = CreateToolhelp32Snapshot(2, 0);   // TH32CS_SNAPPROCESS
            if (snap != IntPtr.Zero && snap != new IntPtr(-1))
            {
                try
                {
                    ProcessEntry32 e = new ProcessEntry32();
                    e.dwSize = (uint)Marshal.SizeOf(typeof(ProcessEntry32));
                    if (Process32FirstW(snap, ref e))
                    {
                        do { parent[e.th32ProcessID] = e.th32ParentProcessID; }
                        while (Process32NextW(snap, ref e));
                    }
                }
                catch { }
                finally { CloseHandle(snap); }
            }
            if (parent.Count == 0) parent = ParentMapWmi();
            return parent;
        }

        // In a method of its own, so that System.Management is not even loaded unless needed.
        static Dictionary<uint, uint> ParentMapWmi()
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

        public static bool IsBoosterName(string name)
        {
            return name != null && name.IndexOf("AppVolumeBooster", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Every booster process holds a named event for its lifetime, so another instance
        // can tell "that pid is a booster" without trusting the exe name. A renamed copy
        // used to count as dead: SelfHeal then saw its own lines as orphans every 5 s and
        // un-ducked its live targets under the running boost.
        static EventWaitHandle aliveEvent;
        static string AliveName(uint pid) { return "AppVolumeBooster.Alive." + pid.ToString(CultureInfo.InvariantCulture); }
        public static void MarkAlive()
        {
            try { aliveEvent = new EventWaitHandle(false, EventResetMode.ManualReset, AliveName((uint)Process.GetCurrentProcess().Id)); }
            catch { }
        }
        public static bool IsBoosterPid(uint pid)
        {
            if (pid == 0) return false;
            EventWaitHandle h;
            try { if (EventWaitHandle.TryOpenExisting(AliveName(pid), out h)) { h.Close(); return true; } }
            catch { }
            return IsBoosterName(ProcessNameOf(pid));
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
        // One file per user, under %LocalAppData%\AppVolumeBooster, shared by every copy of
        // the exe. It used to sit next to each exe, so two copies in different folders kept
        // separate notes: the second could find an app at 4% (held there by the first), find
        // no note of its own, record 4% as the "prior", and leave the app there for good.
        // The exe's own folder is only the fallback for a profile without a usable
        // %LocalAppData%; a folder that cannot be written is skipped (it used to be written
        // into an empty catch, which switched crash recovery off without a word).
        static string cachedPath;
        static string PathOf()
        {
            if (cachedPath != null) return cachedPath;
            string dir = null;
            try
            {
                dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppVolumeBooster");
                Directory.CreateDirectory(dir);
            }
            catch { dir = null; }
            if (dir == null || !CanWrite(dir))
            {
                dir = null;
                try { dir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location); }
                catch { }
                if (dir == null || !CanWrite(dir)) dir = System.IO.Path.GetTempPath();
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

        // False when the cross-process lock could not be taken in time: the caller tries
        // again on a later pass, because a note that was never written cannot heal anything.
        public static bool AddOwn(string targetExe, float prior)
        {
            return UnderLock(delegate()
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
                    // a live booster is recognised by the event it holds, not by its exe name
                    if (int.TryParse(parts[0], out bpid) && bpid > 0) alive = Native.IsBoosterPid((uint)bpid);
                    if (alive) { keep.Add(l); heldByLive.Add(parts[1]); } else dead.Add(parts);
                }
                if (dead.Count > 0)
                {
                    bool[] resolved = new bool[dead.Count];
                    bool[] failed = new bool[dead.Count];
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
                                // EVERY session of that name is checked, not only the first: with
                                // two sessions of one app (two outputs, two instances) the first
                                // one seen used to resolve the line and the other stayed at 4%.
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
                                    // A restore that fails (session object dead, audio service
                                    // restarting) used to be reported as healed and its note deleted,
                                    // leaving the app at 4% with nothing left to repair it.
                                    if (v.SetMasterVolume(prior, IntPtr.Zero) < 0) { failed[di] = true; continue; }
                                    if (!healedNames.Contains(parts[1])) healedNames.Add(parts[1]);
                                }
                                resolved[di] = true;
                            }
                        }
                    }
                    catch { }
                    for (int di = 0; di < dead.Count; di++)
                        if (!resolved[di] || failed[di]) keep.Add(string.Join("|", dead[di]));
                }
                // Only when something changed. The window calls this every 5 s, and it used
                // to rewrite the file each time for the whole length of a boost.
                bool same = keep.Count == lines.Count;
                for (int i = 0; same && i < keep.Count; i++) same = keep[i] == lines[i];
                if (!same) WriteAll(keep);
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
        readonly List<uint> polledPids = new List<uint>();   // targets the watcher looks at itself

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
        readonly List<IAudioSessionEnumerator> liveEnums = new List<IAudioSessionEnumerator>();   // the armed enumerators, kept alive
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
        HashSet<uint> wantPidsSnapshot = new HashSet<uint>();
        readonly object lifeLock = new object();   // Start and Stop never overlap
        int watchPeriod = 500;
        long lastGlitches;
        int cleanTicks;
        public string RestoreWarning = "";

        class Ducked
        {
            public ISimpleAudioVolume Vol;
            public float Prior;
            public uint Pid;
            public string Exe;
            public string InstanceId;
            public bool Muted;                // as found - recorded, never changed
            public bool Noted;                // its line is in the state file
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
        // how many sessions are being held at the duck level right now
        public int DuckedCount { get { lock (duckLock) { return ducked.Count; } } }

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
                // A Stop that arrives while the start-up still runs (the window closing, a
                // target closing) waits for it and then undoes it, instead of running a
                // shutdown in parallel with a start that went on creating clients and the
                // timer for an engine nobody could stop any more.
                lock (lifeLock)
                {
                    try { StartCore(); }
                    catch
                    {
                        try { Stop(true); } catch { }
                        throw;
                    }
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
            RestoreWarning = "";
            sysCapOk = false;
            ClaimExclusivity();

            devId = Native.DefaultRenderDeviceId();
            try { System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency; } catch { }
            // one process snapshot serves the root promotion, the trees and CollapseToRoots
            // (it used to be taken three times, all on the window's thread)
            Dictionary<uint, uint> parentMap = ParentMap();
            // Without the process list nothing below can be trusted: every tree would hold just
            // its root, so the check that the booster is not INSIDE the app it boosts (the
            // feedback loop) would pass whatever the truth, and an app's helper processes
            // would be captured but not ducked. It used to carry on with an empty list.
            if (!boostAll && CopyTargets().Length > 0 && !parentMap.ContainsKey(selfPid))
                throw new InvalidOperationException("the list of running processes could not be read, so it cannot be ruled out that this booster is running inside the app it would boost - nothing was started ('Boost all audio' does not need that list)");
            if (!boostAll) PromoteToRoots(parentMap);
            RebuildTrees(parentMap);

            WaveFormatEx fmt = WaveFormatEx.Float32Stereo48k();
            List<IAudioClient> pendingCaps = new List<IAudioClient>();
            if (boostAll)
            {
                pendingCaps.Add(Native.ActivateProcessLoopback(selfPid, true));
            }
            else
            {
                List<uint> roots = CollapseToRoots(CopyTargets(), parentMap);
                List<string> capFails = new List<string>();
                List<uint> failedRoots = new List<uint>();
                foreach (uint root in roots)
                {
                    if (SelfInTreeOf(root))
                    {
                        capFails.Add((Native.ProcessNameOf(root) ?? root.ToString()) +
                            " (this booster is running inside that process tree, so capturing it would feed back on itself - "
                            + "start the booster outside that app, or use 'Boost all audio')");
                        failedRoots.Add(root);
                        continue;
                    }
                    try { pendingCaps.Add(Native.ActivateProcessLoopback(root, false)); }
                    catch (Exception ex) { capFails.Add((Native.ProcessNameOf(root) ?? root.ToString()) + " (" + ex.Message + ")"); failedRoots.Add(root); }
                }
                if (failedRoots.Count > 0)
                {
                    // A target that could not be captured must not be ducked either. It used to
                    // stay in the target list, so it sat at 4% for the whole boost with no
                    // boosted copy to make up for it - quieter than before the boost.
                    lock (targetLock)
                    {
                        foreach (uint r in failedRoots)
                        {
                            HashSet<uint> tr = treesByRoot.ContainsKey(r) ? treesByRoot[r] : null;
                            targetPids.RemoveAll(delegate(uint p) { return p == r || (tr != null && tr.Contains(p)); });
                        }
                    }
                    RebuildTrees(parentMap);
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

            // Registered and armed BEFORE the first duck pass, so a session that appears while
            // that pass runs is not left to the watcher half a second later.
            sessionNote = new SessionCreatedHandler();
            sessionNote.Fn = DuckOne;
            foreach (IAudioSessionManager2 m in Native.ActiveSessionManagers())
            {
                try
                {
                    Native.Check(m.RegisterSessionNotification(sessionNote), "RegisterSessionNotification");
                    liveMgrs.Add(m);
                    // The registration is armed only by enumerating the sessions through THIS
                    // manager object: "You must call IAudioSessionEnumerator::GetCount to begin
                    // receiving notifications" (RegisterSessionNotification, Remarks). Without
                    // it the callback never fired at all, and every new session waited for the
                    // next watcher pass - up to 500 ms at full volume under the boosted copy.
                    IAudioSessionEnumerator en;
                    if (m.GetSessionEnumerator(out en) >= 0 && en != null) { int n; en.GetCount(out n); liveEnums.Add(en); }
                }
                catch { }
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
            renClient = Native.ActivateClient(dev);
            renEv = new AutoResetEvent(false);
            // NOPERSIST: the boosted copy's own mixer entry is not saved. It starts at 100%
            // each time, and no entry for this exe is added to the per-app volume store -
            // every copy of the exe at a new path used to leave one there for good.
            Native.Check(renClient.Initialize(K.SHARED, K.EVENTCB | K.AUTOCONVERT | K.SRC_QUALITY | K.NOPERSIST, 2000000, 0, ref fmt, IntPtr.Zero), "render Initialize");
            Native.Check(renClient.SetEventHandle(renEv.SafeWaitHandle.DangerousGetHandle()), "render SetEventHandle");
            uint renBuf; Native.Check(renClient.GetBufferSize(out renBuf), "render GetBufferSize");
            Guid iidr = Native.IID_IAudioRenderClient; object orr;
            Native.Check(renClient.GetService(ref iidr, out orr), "render GetService");
            IAudioRenderClient render = (IAudioRenderClient)orr;

            Guid iidv = Native.IID_ISimpleAudioVolume; object ov;
            Native.Check(renClient.GetService(ref iidv, out ov), "render volume");
            // Unmuted (a muted copy is a silent boost); the level is whatever the session
            // starts at - 100%, since it is not persisted.
            ((ISimpleAudioVolume)ov).SetMute(false, IntPtr.Zero);

            foreach (IAudioClient cap in capClients)
                Native.Check(cap.Start(), "capture Start");
            Native.Check(renClient.Start(), "render Start");
            foreach (Thread t in capThreads) t.Start();
            renThread = new Thread(delegate() { RenderLoop(render, renBuf); });
            renThread.IsBackground = true;
            renThread.Priority = ThreadPriority.Highest;
            renThread.Start();

            watchPeriod = boostAll ? 150 : 500;
            lastGlitches = Interlocked.Read(ref Glitches); cleanTicks = 0;
            watcher = new System.Threading.Timer(WatcherTick, null, watchPeriod, watchPeriod);
            // The exit hooks come LAST, once everything they may have to undo exists.
            if (!boostAll) HookTargets();
        }

        void HookTargets()
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
                    if (p.HasExited) TargetExited(pid);
                }
                catch
                {
                    // Not being ALLOWED to wait for a process is not the process being gone.
                    // An app running as administrator cannot be waited on by a booster that is
                    // not, and that used to end the boost at once with "target closed". The
                    // watcher looks at such a target on every pass instead.
                    if (Native.ProcessNameOf(pid) == null) TargetExited(pid);
                    else lock (targetLock) { if (!polledPids.Contains(pid)) polledPids.Add(pid); }
                }
            }
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
            int capFail = 0;
            while (running)
            {
                ev.WaitOne(100);
                uint pkt;
                int hrN = capture.GetNextPacketSize(out pkt);
                // Like NoteRenderResult on the render side: a capture client that keeps failing
                // (its stream invalidated while the output stays healthy) used to leave the
                // target ducked and silent with the window still saying Boosting. The loop
                // wakes at least every 100 ms, so 20 failures in a row is about 2 s.
                if (hrN < 0)
                {
                    if (++capFail >= 20 && fatalReason == null)
                        fatalReason = "the captured app's audio stream stopped responding (0x" + hrN.ToString("X8") + ") - press Start again";
                    continue;
                }
                capFail = 0;
                while (running && pkt > 0)
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
                    if (capture.GetNextPacketSize(out pkt) != 0) break;
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
            if (Native.IsBoosterName(sname) || Native.IsBoosterPid(spid)) return false;
            if (boostAll) return true;
            if (isSys || spid == 0) return wantSystemSounds && sysCapOk;
            HashSet<uint> snap = wantPidsSnapshot;
            if (snap != null && snap.Contains(spid)) return true;
            return TryAdopt(spid);
        }

        // A process a target spawned after the last tree rebuild - a browser's new audio
        // helper, a launcher's game - was captured and amplified at once (the OS follows the
        // live tree) but refused ducking for up to 10 s, until the next rebuild. An unknown
        // pid is now placed in the tree on the spot, with the same process snapshot the
        // rebuild uses. A pid found not to belong is not looked at again for ten seconds,
        // and no more than four snapshots a second are taken.
        long lastAdopt;
        readonly Dictionary<uint, long> adoptChecked = new Dictionary<uint, long>();
        bool TryAdopt(uint spid)
        {
            if (boostAll || spid == 0 || !running) return false;
            long now = Stopwatch.GetTimestamp();
            lock (targetLock)
            {
                long at;
                if (adoptChecked.TryGetValue(spid, out at) && now - at < Stopwatch.Frequency * 10) return false;
                if (now - lastAdopt < Stopwatch.Frequency / 4) return false;
                adoptChecked[spid] = now; lastAdopt = now;
            }
            RebuildTrees(ParentMap());
            HashSet<uint> snap = wantPidsSnapshot;
            return snap != null && snap.Contains(spid);
        }

        void DuckOne(IAudioSessionControl sc)
        {
            if (sc == null || !running) return;
            lock (duckLock)
            {
                // re-checked under the lock: a duck that had queued behind a Stop used to
                // re-duck a session the Stop had just restored, and leave the app at 4%
                if (!running) return;
                try
                {
                    IAudioSessionControl2 sc2 = (IAudioSessionControl2)sc;
                    uint spid; sc2.GetProcessId(out spid);
                    int st; sc2.GetState(out st);
                    if (st == K.StateExpired) return;
                    string sname = Native.ProcessNameOf(spid);
                    bool isSys = Native.IsSystemSounds(sc2) || spid == 0;
                    if (!WantSession(spid, isSys, sname)) return;

                    ISimpleAudioVolume v = (ISimpleAudioVolume)sc;
                    float lvl; v.GetMasterVolume(out lvl);
                    // Ducked FIRST. The bookkeeping below (instance id, the note on disk under a
                    // cross-process lock) used to come before the duck, so a session that had
                    // just appeared played at full volume under the boosted copy meanwhile.
                    // Mute is the user's decision and is never changed here. This used to
                    // unmute every session it ducked and never recorded that it had, so
                    // "Boost all audio" made apps you had deliberately muted audible (4% x
                    // boost/0.04 = normal volume) and left them unmuted after Stop. A muted
                    // target now stays silent while boosted - unmute it in the mixer.
                    if (lvl > DUCK + 0.003f || lvl < DUCK - 0.003f)
                    {
                        int hrD = v.SetMasterVolume(DUCK, IntPtr.Zero);
                        if (hrD < 0)
                        {
                            // Not ducked means full volume under the boosted copy. It used to be
                            // ignored, leaving only the slider for a human to notice.
                            if (StartWarning.IndexOf("could not be lowered") < 0)
                                StartWarning = (StartWarning == "" ? "" : StartWarning + " ") + "The mixer slider of " + (sname ?? "an app") +
                                    " could not be lowered (0x" + hrD.ToString("X8") + "), so it plays at full volume under the boosted copy - stop the boost.";
                            return;
                        }
                    }

                    string inst = null;
                    try { sc2.GetSessionInstanceIdentifier(out inst); } catch { }
                    if (inst == null) inst = "pid:" + spid.ToString() + ":" + ducked.Count.ToString();
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
                        known.Noted = StateFile.AddOwn(known.Exe, known.Prior);
                    }
                    else
                    {
                        // A session that was closed and opened again by the same process - a
                        // browser does it 5 s after a pause, a game when it re-opens its audio -
                        // comes back under the SAME instance id, as a new object. Measured: the
                        // first object still sets the volume of the re-opened session, so the
                        // restore worked either way; the newest one is kept so that it never
                        // depends on an object from before the session was closed.
                        known.Vol = v;
                        if (!known.Noted) known.Noted = StateFile.AddOwn(known.Exe, known.Prior);
                    }
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

        void RebuildTrees() { RebuildTrees(ParentMap()); }

        void RebuildTrees(Dictionary<uint, uint> parent)
        {
            if (boostAll) { wantPidsSnapshot = new HashSet<uint>(); return; }
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

        // The checked pid is whichever process owns the audio session. In a Chromium browser
        // started without the kit's flags (and in Electron apps such as Discord) that is a
        // helper process the browser recycles: when it went away the boost stopped with
        // "target closed" while the browser played on, and its replacement - a sibling of
        // the old pid - was neither ducked nor captured. Each target is therefore lifted to
        // its top-most ancestor with the same exe name: the browser itself. The walk stops
        // short of any ancestor of this booster - a player started from the same shell as
        // the booster would otherwise "belong" to that shell, and capturing the shell's tree
        // would capture the booster.
        void PromoteToRoots(Dictionary<uint, uint> parent)
        {
            HashSet<uint> mine = AncestorsOf(selfPid, parent);
            lock (targetLock)
            {
                List<uint> roots = new List<uint>();
                foreach (uint pid in targetPids)
                {
                    uint r = RootOf(pid, parent, mine);
                    if (!roots.Contains(r)) roots.Add(r);
                }
                targetPids.Clear();
                targetPids.AddRange(roots);
            }
        }

        static HashSet<uint> AncestorsOf(uint pid, Dictionary<uint, uint> parent)
        {
            HashSet<uint> r = new HashSet<uint>();
            uint walk = pid; int guard = 0;
            while (guard++ < 64 && parent.ContainsKey(walk))
            {
                uint pp = parent[walk];
                if (pp == walk || pp == 0 || r.Contains(pp)) break;
                r.Add(pp);
                walk = pp;
            }
            return r;
        }

        static uint RootOf(uint pid, Dictionary<uint, uint> parent, HashSet<uint> stopAt)
        {
            string name = Native.ProcessNameOf(pid);
            if (name == null) return pid;
            uint root = pid; int guard = 0;
            while (guard++ < 64 && parent.ContainsKey(root))
            {
                uint pp = parent[root];
                if (pp == root || pp == 0 || stopAt.Contains(pp)) break;
                if (!string.Equals(Native.ProcessNameOf(pp), name, StringComparison.OrdinalIgnoreCase)) break;
                root = pp;
            }
            return root;
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
                    if (Native.IsBoosterName(n) || Native.IsBoosterPid(spid)) return (n ?? "booster") + " pid " + spid.ToString();
                }
                catch { }
            }
            return null;
        }

        static Dictionary<uint, uint> ParentMap() { return Native.ParentMap(); }

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

        static List<uint> CollapseToRoots(uint[] pids, Dictionary<uint, uint> parent)
        {
            List<uint> roots = new List<uint>();
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
        int inTick;

        void WatcherTick(object state)
        {
            if (!running) return;
            // The timer does not wait for a pass to finish. With many sessions ("boost all
            // audio" runs a pass every 150 ms) passes used to pile up behind the ducking
            // lock; a pass that finds the previous one still running is skipped instead.
            if (Interlocked.Exchange(ref inTick, 1) == 1) return;
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
                uint[] polled = null;
                lock (targetLock) { if (polledPids.Count > 0) polled = polledPids.ToArray(); }
                if (polled != null)
                {
                    foreach (uint pp in polled)
                    {
                        if (Native.ProcessNameOf(pp) != null) continue;
                        lock (targetLock) { polledPids.Remove(pp); }
                        TargetExited(pp);
                    }
                    if (!running) return;
                }
                if (!boostAll && watchTick % 20 == 0) RebuildTrees();
                DuckPass();
                // After 90 s without a dropout the standing queue comes back down one step. It
                // used to stay wherever a busy moment had pushed it until the next Start, so a
                // game's loading screen left a video's sound 100 ms behind for the evening.
                long g = Interlocked.Read(ref Glitches);
                if (g != lastGlitches) { lastGlitches = g; cleanTicks = 0; }
                else if (++cleanTicks * watchPeriod >= 90000)
                {
                    cleanTicks = 0;
                    int np = targetPadFrames - 960;
                    if (np >= 2400) targetPadFrames = np;
                }
            }
            catch { }
            finally { Interlocked.Exchange(ref inTick, 0); }
        }

        void TargetExited(uint pid)
        {
            if (!running) return;
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
            RestorePids(tree);
            bool remain;
            lock (targetLock) { remain = targetPids.Count > 0; }
            if (!remain && !boostAll && !(wantSystemSounds && sysCapOk))
                StopBecause("target closed");
        }

        void RestorePids(HashSet<uint> pids)
        {
            lock (duckLock)
            {
                for (int i = ducked.Count - 1; i >= 0; i--)
                {
                    Ducked d = ducked[i];
                    if (!pids.Contains(d.Pid)) continue;
                    bool ok;
                    try { ok = d.Vol.SetMasterVolume(d.Prior, IntPtr.Zero) >= 0; }
                    catch { ok = false; }
                    ducked.RemoveAt(i);
                    // There is one note line per exe name. While another session of that name
                    // is still ducked the line stays as it is: turned into an orphan line, it
                    // let the next self-heal pass un-duck that other session under the boost.
                    bool sameNameStillDucked = false;
                    foreach (Ducked o in ducked)
                        if (string.Equals(o.Exe, d.Exe, StringComparison.OrdinalIgnoreCase)) { sameNameStillDucked = true; break; }
                    if (sameNameStillDucked) continue;
                    // A restore that worked needs no note. The line used to be kept as an orphan
                    // after every target exit, and if the user later set that app below 5% on
                    // purpose, the stale line "healed" it back to the old level. Only a restore
                    // that failed leaves its line for the next run.
                    if (ok) StateFile.RemoveTarget(d.Exe);
                    else StateFile.OrphanTarget(d.Exe);
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
            lock (lifeLock) { StopUnderLock(restoreVolumes); }
        }

        void StopUnderLock(bool restoreVolumes)
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
            liveEnums.Clear();
            sessionNote = null;
            if (allAudioMutex != null) { try { allAudioMutex.Close(); } catch { } allAudioMutex = null; }
            foreach (IAudioClient cap in capClients)
                try { cap.Stop(); } catch { }
            try { if (renClient != null) renClient.Stop(); } catch { }
            if (restoreVolumes)
            {
                int failed = 0;
                lock (duckLock)
                {
                    foreach (Ducked d in ducked)
                    {
                        // A failed restore (session object dead after an audio-service restart)
                        // used to count as success: the note was deleted and the window said
                        // "restored" while the app sat at 4%. The note now stays for self-heal.
                        try { if (d.Vol.SetMasterVolume(d.Prior, IntPtr.Zero) < 0) failed++; }
                        catch { failed++; }
                    }
                    ducked.Clear();
                }
                if (failed > 0) RestoreWarning = failed + " mixer slider(s) could not be restored; the booster's note is kept and the next run repairs them.";
                if (failed > 0) StateFile.OrphanOwn();
                else StateFile.RemoveOwn();
            }
            foreach (Process p in watched)
                try { p.Dispose(); } catch { }
            watched.Clear();
            lock (targetLock) { polledPids.Clear(); }
        }
    }

    // ===================================== UI ========================================
    internal static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(25, 24, 31);
        public static readonly Color Surface = Color.FromArgb(38, 37, 47);
        public static readonly Color SurfaceHot = Color.FromArgb(54, 52, 68);
        public static readonly Color Text = Color.FromArgb(241, 240, 246);
        public static readonly Color Muted = Color.FromArgb(164, 162, 180);
        public static readonly Color Dim = Color.FromArgb(112, 110, 126);      // text of a disabled control
        // Purple. The deep one carries white text (the Start button, a check mark); the light
        // one is for thin things on the dark background (the slider, numbers, the status dot).
        public static readonly Color Accent = Color.FromArgb(124, 58, 237);
        public static readonly Color AccentHot = Color.FromArgb(139, 92, 246);
        public static readonly Color AccentLight = Color.FromArgb(178, 152, 252);
        public static readonly Color AccentDim = Color.FromArgb(88, 58, 150);  // a checked box that cannot be changed now
        public static readonly Color Border = Color.FromArgb(72, 70, 88);
        public static readonly Color Stop = Color.FromArgb(176, 72, 72);
        public static readonly Color StopHot = Color.FromArgb(196, 88, 88);
        public static readonly Color Groove = Color.FromArgb(60, 58, 76);
        public static readonly Color Warn = Color.FromArgb(232, 178, 76);

        public static void Button(Button b, Color fill, Color fillHot)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = (fill == Surface) ? Border : fill;
            b.FlatAppearance.MouseOverBackColor = fillHot;
            b.FlatAppearance.MouseDownBackColor = fill;
            b.UseVisualStyleBackColor = false;
            b.BackColor = fill;
            b.ForeColor = Text;
            b.Cursor = Cursors.Hand;
        }

        public static System.Drawing.Drawing2D.GraphicsPath Round(RectangleF r, float radius)
        {
            System.Drawing.Drawing2D.GraphicsPath p = new System.Drawing.Drawing2D.GraphicsPath();
            float d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d < 1) d = 1;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // The app's icon, drawn rather than shipped (the exe is one .cs file): a speaker on a
        // rounded square - purple with sound waves while a boost runs, grey without them while
        // it does not. The window and the tray icon use it.
        public static Bitmap AppGlyph(int size, bool on)
        {
            Bitmap b = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                float s = size / 16f;
                using (System.Drawing.Drawing2D.GraphicsPath p = Round(new RectangleF(0.5f * s, 0.5f * s, 15f * s, 15f * s), 3.5f * s))
                using (SolidBrush bg = new SolidBrush(on ? Accent : Color.FromArgb(78, 76, 94)))
                    g.FillPath(bg, p);
                Color ink = on ? Color.White : Color.FromArgb(214, 212, 226);
                using (SolidBrush fg = new SolidBrush(ink))
                    g.FillPolygon(fg, new PointF[] {
                        new PointF(3f * s, 6f * s), new PointF(5.5f * s, 6f * s), new PointF(8.6f * s, 3.2f * s),
                        new PointF(8.6f * s, 12.8f * s), new PointF(5.5f * s, 10f * s), new PointF(3f * s, 10f * s) });
                if (on)
                {
                    using (Pen w = new Pen(ink, Math.Max(1.1f, 1.25f * s)))
                    {
                        w.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                        w.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                        g.DrawArc(w, 8.6f * s - 2.5f * s, 8f * s - 2.5f * s, 5f * s, 5f * s, -50, 100);
                        g.DrawArc(w, 8.6f * s - 4.6f * s, 8f * s - 4.6f * s, 9.2f * s, 9.2f * s, -48, 96);
                    }
                }
            }
            return b;
        }

        // An Icon made of such a picture. Its handle goes on the list, for DestroyIcon once the
        // window is gone (Icon.FromHandle does not free it).
        public static Icon ToIcon(Bitmap b, List<IntPtr> handles)
        {
            using (b)
            {
                IntPtr h = b.GetHicon();
                handles.Add(h);
                return Icon.FromHandle(h);
            }
        }

        // The box of a list row or of an option: purple with a white mark when it is on.
        public static void CheckGlyph(Graphics g, RectangleF box, bool on, bool enabled, bool focus, Color empty)
        {
            System.Drawing.Drawing2D.SmoothingMode was = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Color fill = on ? (enabled ? Accent : AccentDim) : empty;
            Color edge = on ? fill : (focus ? AccentLight : (enabled ? Muted : Border));
            using (System.Drawing.Drawing2D.GraphicsPath p = Round(box, box.Width * 0.24f))
            {
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
                using (Pen pen = new Pen(edge, Math.Max(1f, box.Width * 0.07f))) g.DrawPath(pen, p);
            }
            if (on)
            {
                using (Pen pen = new Pen(enabled ? Color.White : Color.FromArgb(214, 208, 232), Math.Max(1.6f, box.Width * 0.12f)))
                {
                    pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                    g.DrawLines(pen, new PointF[] {
                        new PointF(box.X + box.Width * 0.25f, box.Y + box.Height * 0.53f),
                        new PointF(box.X + box.Width * 0.43f, box.Y + box.Height * 0.70f),
                        new PointF(box.X + box.Width * 0.76f, box.Y + box.Height * 0.32f) });
                }
            }
            g.SmoothingMode = was;
        }
    }

    // A check box painted with the window's own colours (the system one keeps the system's
    // accent colour and a light box).
    internal class DarkCheck : CheckBox
    {
        public DarkCheck()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            float s = g.DpiX / 96f;
            float side = 18 * s;
            RectangleF box = new RectangleF(1 * s, (Height - side) / 2f, side, side);
            Theme.CheckGlyph(g, box, Checked, Enabled, Focused, Theme.Surface);
            int tx = (int)(box.Right + 10 * s);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(tx, 0, Math.Max(1, Width - tx), Height),
                Enabled ? ForeColor : (Checked ? Theme.Muted : Theme.Dim),
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    }

    // A small filled circle: with a digit it numbers a step, without text it is the status light.
    internal class Badge : Control
    {
        Color fill = Theme.Accent;
        public Color Fill { get { return fill; } set { fill = value; Invalidate(); } }

        public Badge()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            BackColor = Theme.Bg;
            ForeColor = Color.White;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float d = Math.Min(Width, Height) - 1;
            using (SolidBrush b = new SolidBrush(fill))
                g.FillEllipse(b, (Width - d) / 2f, (Height - d) / 2f, d, d);
            if (Text != "")
                TextRenderer.DrawText(g, Text, Font, ClientRectangle, ForeColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
    }

    // One row of the app list. ToString() is the row as plain text - what a screen reader
    // and the kit's tests read; the list paints the row itself (see AppList).
    internal class AppEntry
    {
        public uint Pid;
        public string Name;      // the process name
        public string Title;     // what the app calls itself (its exe's description), else the name
        public bool Playing;
        public bool Quiet;       // a running browser that has no audio session at the moment
        public bool ShowPid;     // another row has the same title
        public Image Picture;

        public override string ToString()
        {
            return Name + "  (pid " + Pid + (Playing ? ", playing)" : (Quiet ? ", no sound right now)" : ")"));
        }

        static readonly Dictionary<string, string> titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, Image> icons = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);

        // The name and the icon an app shows everywhere else in Windows, looked up once per exe.
        public void Describe(string exePath)
        {
            Title = Name;
            if (string.IsNullOrEmpty(exePath)) return;
            string t;
            if (!titles.TryGetValue(exePath, out t))
            {
                t = null;
                try { t = FileVersionInfo.GetVersionInfo(exePath).FileDescription; }
                catch { }
                if (t != null) t = t.Trim();
                if (string.IsNullOrEmpty(t) || t.Length > 60) t = null;
                titles[exePath] = t;
            }
            if (t != null) Title = t;
            Image ic;
            if (!icons.TryGetValue(exePath, out ic))
            {
                ic = null;
                try
                {
                    using (Icon i = System.Drawing.Icon.ExtractAssociatedIcon(exePath))
                        if (i != null) ic = i.ToBitmap();
                }
                catch { }
                icons[exePath] = ic;
            }
            Picture = ic;
        }
    }

    // The app list: a CheckedListBox that paints its own rows - a purple check box, the app's
    // icon and name, and on the right whether it is playing.
    internal class AppList : CheckedListBox
    {
        public override int ItemHeight
        {
            get { return Font.Height * 2; }
            set { }
        }

        // What an empty list says instead of being a blank box.
        public string EmptyText = "";

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (Items.Count != 0 || EmptyText == "") return;
            if (m.Msg == 0x000F)                                   // WM_PAINT
            {
                using (Graphics g = CreateGraphics()) PaintEmpty(g);
            }
            else if (m.Msg == 0x0318 && m.WParam != IntPtr.Zero)   // WM_PRINTCLIENT
            {
                using (Graphics g = Graphics.FromHdc(m.WParam)) PaintEmpty(g);
            }
        }

        void PaintEmpty(Graphics g)
        {
            Rectangle area = ClientRectangle;
            TextFormatFlags f = TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
            Size need = TextRenderer.MeasureText(g, EmptyText, Font, new Size(area.Width - 40, area.Height), f);
            Rectangle box = new Rectangle(area.X + 20, area.Y + Math.Max(0, (area.Height - need.Height) / 2), area.Width - 40, need.Height);
            TextRenderer.DrawText(g, EmptyText, Font, box, Theme.Muted, BackColor, f);
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle r = e.Bounds;
            if (e.Index < 0 || e.Index >= Items.Count)
            {
                using (SolidBrush b = new SolidBrush(BackColor)) g.FillRectangle(b, r);
                return;
            }
            float s = g.DpiX / 96f;
            bool sel = (e.State & DrawItemState.Selected) != 0 && Focused && Enabled;
            using (SolidBrush b = new SolidBrush(sel ? Theme.SurfaceHot : BackColor)) g.FillRectangle(b, r);

            object item = Items[e.Index];
            AppEntry a = item as AppEntry;
            bool on = GetItemChecked(e.Index);
            // while a boost runs the list cannot be changed, but it still shows what is boosted
            Color ink = Enabled ? Theme.Text : (on ? Theme.Text : Theme.Dim);
            float side = 18 * s;
            RectangleF box = new RectangleF(r.X + 10 * s, r.Y + (r.Height - side) / 2f, side, side);
            Theme.CheckGlyph(g, box, on, Enabled, false, Theme.Bg);
            float x = box.Right + 10 * s;
            if (a != null && a.Picture != null)
            {
                float ic = 20 * s;
                System.Drawing.Drawing2D.InterpolationMode was = g.InterpolationMode;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(a.Picture, new RectangleF(x, r.Y + (r.Height - ic) / 2f, ic, ic));
                g.InterpolationMode = was;
                x += ic + 9 * s;
            }

            string right = (a == null) ? "" : (a.Playing ? "playing" : "no sound right now");
            int rightW = 0;
            if (right != "")
            {
                Size rs = TextRenderer.MeasureText(g, right, Font, new Size(int.MaxValue, r.Height), TextFormatFlags.NoPadding);
                Rectangle rr = new Rectangle(r.Right - rs.Width - (int)(12 * s), r.Y, rs.Width, r.Height);
                Color rc = (!Enabled && !on) ? Theme.Dim : (a.Playing ? Theme.AccentLight : Theme.Muted);
                TextRenderer.DrawText(g, right, Font, rr, rc,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                rightW = rs.Width + (int)(24 * s);
                if (a.Playing)
                {
                    float d = 7 * s;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (SolidBrush b = new SolidBrush(rc))
                        g.FillEllipse(b, rr.X - d - 6 * s, r.Y + (r.Height - d) / 2f, d, d);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
                    rightW += (int)(d + 6 * s);
                }
            }

            string title = (a == null) ? item.ToString() : (a.ShowPid ? a.Title + "  (pid " + a.Pid + ")" : a.Title);
            TextRenderer.DrawText(g, title, Font, new Rectangle((int)x, r.Y, Math.Max(1, r.Right - (int)x - rightW), r.Height),
                ink,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    // Classic TrackBar ignores WinForms colors (ticks/thumb stay light). This one
    // paints with the same palette as the rest of the window, and labels its scale.
    internal class DarkSlider : Control
    {
        int min = 100, max = 500, val = 150;
        bool dragging;
        Font scaleFont;

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

        // sizes are in 96-dpi pixels, scaled with the control (the form scales its height)
        float Scale1 { get { return Height / 58f; } }
        int Pad { get { return (int)Math.Round(12 * Scale1); } }

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
            // to the nearest 5: nobody wants 237%
            return min + (int)Math.Round(t * (max - min) / 5f) * 5;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float s = Scale1;
            float grooveH = 8 * s, thumbR = 10 * s;
            float midY = 14 * s;
            RectangleF groove = new RectangleF(Pad, midY - grooveH / 2, Math.Max(1, Width - 2 * Pad), grooveH);
            using (System.Drawing.Drawing2D.GraphicsPath p = Theme.Round(groove, grooveH / 2))
            using (SolidBrush b = new SolidBrush(Theme.Groove))
                g.FillPath(b, p);
            int tx = XFromValue();
            if (tx - Pad > 0)
            {
                using (System.Drawing.Drawing2D.GraphicsPath p = Theme.Round(new RectangleF(Pad, groove.Y, tx - Pad, grooveH), grooveH / 2))
                using (SolidBrush b = new SolidBrush(Focused ? Theme.AccentLight : Theme.AccentHot))
                    g.FillPath(b, p);
            }
            using (SolidBrush b = new SolidBrush(Theme.Text))
                g.FillEllipse(b, tx - thumbR, midY - thumbR, thumbR * 2, thumbR * 2);
            using (Pen p = new Pen(Theme.Accent, 3 * s))
                g.DrawEllipse(p, tx - thumbR, midY - thumbR, thumbR * 2, thumbR * 2);

            // the scale: a mark every 50, a number every 100
            if (scaleFont == null) scaleFont = new Font(Font.FontFamily, 8.25f);
            int span = max - min;
            if (span <= 0) return;
            using (SolidBrush tick = new SolidBrush(Theme.Dim))
            {
                for (int v = min; v <= max; v += 50)
                {
                    float x = Pad + (v - min) / (float)span * (Width - 2 * Pad);
                    bool major = (v % 100) == 0;
                    g.FillRectangle(tick, x - 0.5f * s, midY + 14 * s, Math.Max(1f, s), (major ? 6 : 3) * s);
                    if (!major) continue;
                    string label = v + "%";
                    Size ls = TextRenderer.MeasureText(g, label, scaleFont, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
                    int lx = (int)Math.Round(x - ls.Width / 2f);
                    if (lx < 0) lx = 0;
                    if (lx + ls.Width > Width) lx = Width - ls.Width;
                    TextRenderer.DrawText(g, label, scaleFont, new Point(lx, (int)(midY + 22 * s)), Theme.Muted, TextFormatFlags.NoPadding);
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

        // Arrow keys are dialog-navigation keys unless the control claims them; without
        // this they moved the focus to the next control and OnKeyDown never saw them.
        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            if (k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down) return true;
            return base.IsInputKey(keyData);
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

    // What the window remembers between runs, in settings.txt next to the crash note
    // (%LocalAppData%\AppVolumeBooster): one "name=value" line for each setting that is not
    // at its default - with all of them at their defaults there is no file. It is written only
    // on the user's own actions: a click on Auto-refresh, a boost that starts, the first trip
    // to the tray.
    //   autorefresh=0      the list changes only when Refresh is pressed
    //   last=name|name     the apps (process names) of the last boost, for Start in the tray
    //   trayhint=1         the "still running in the tray" notice has been shown once
    internal static class Settings
    {
        static string PathOf()
        {
            return System.IO.Path.Combine(System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppVolumeBooster"), "settings.txt");
        }

        static Dictionary<string, string> Load()
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string p = PathOf();
                if (!File.Exists(p)) return d;
                foreach (string line in File.ReadAllLines(p))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch { }
            return d;
        }

        static string Get(string key, string def)
        {
            string v;
            return Load().TryGetValue(key, out v) ? v : def;
        }

        static void Set(string key, string value, string def)
        {
            try
            {
                Dictionary<string, string> d = Load();
                if (value == def) d.Remove(key); else d[key] = value;
                string p = PathOf();
                if (d.Count == 0) { if (File.Exists(p)) File.Delete(p); return; }
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p));
                List<string> lines = new List<string>();
                foreach (KeyValuePair<string, string> kv in d) lines.Add(kv.Key + "=" + kv.Value);
                File.WriteAllLines(p, lines.ToArray());
            }
            catch { }
        }

        public static bool AutoRefresh
        {
            get { return Get("autorefresh", "1") != "0"; }
            set { Set("autorefresh", value ? "1" : "0", "1"); }
        }

        // "|" because it is the one character that cannot be in a file name
        public static string[] LastApps
        {
            get { return Get("last", "").Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries); }
            set { Set("last", value == null ? "" : string.Join("|", value), ""); }
        }

        public static bool TrayHintShown
        {
            get { return Get("trayhint", "0") == "1"; }
            set { Set("trayhint", value ? "1" : "0", "0"); }
        }
    }

    // What a right-click on the tray icon opens: the boost, its slider and Start/Stop, without
    // the window. It closes when it loses the focus, or on Esc.
    internal class TrayPopup : Form
    {
        readonly MainForm main;
        internal readonly Badge Dot = new Badge();
        internal readonly Label State = new Label();
        internal readonly Label Pct = new Label();
        internal readonly DarkSlider Slider = new DarkSlider();
        internal readonly Button Go = new Button();
        internal readonly Label What = new Label();
        readonly Button openBtn = new Button();
        readonly Button exitBtn = new Button();
        int styled = -1;   // which colours Go has: 0 purple (Start), 1 red (Stop)

        public TrayPopup(MainForm owner)
        {
            main = owner;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Text = "App Volume Booster";
            ClientSize = new Size(300, 296);
            Font = new Font("Segoe UI", 10f);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;

            PictureBox logo = new PictureBox();
            logo.Image = Theme.AppGlyph(20, true);
            logo.SizeMode = PictureBoxSizeMode.Zoom;
            logo.SetBounds(16, 14, 20, 20);
            logo.BackColor = Theme.Bg;
            Label title = new Label();
            title.Text = "App Volume Booster";
            title.Font = MainForm.Semibold(10.5f);
            title.SetBounds(42, 12, 242, 24);
            title.TextAlign = ContentAlignment.MiddleLeft;
            title.BackColor = Theme.Bg;
            title.ForeColor = Theme.Text;

            Dot.SetBounds(17, 51, 10, 10);
            Dot.Fill = Theme.Dim;
            State.SetBounds(34, 44, 250, 24);
            State.AutoEllipsis = true;
            State.TextAlign = ContentAlignment.MiddleLeft;
            State.BackColor = Theme.Bg;
            State.ForeColor = Theme.Text;

            Label how = new Label();
            how.Text = "How loud?";
            how.Font = new Font("Segoe UI", 9.5f);
            how.SetBounds(16, 84, 140, 24);
            how.TextAlign = ContentAlignment.MiddleLeft;
            how.BackColor = Theme.Bg;
            how.ForeColor = Theme.Muted;
            Pct.Font = MainForm.Semibold(17f);
            Pct.TextAlign = ContentAlignment.MiddleRight;
            Pct.SetBounds(150, 74, 136, 38);
            Pct.BackColor = Theme.Bg;
            Pct.ForeColor = Theme.AccentLight;

            Slider.Minimum = 100; Slider.Maximum = (int)(BoostEngine.MAXBOOST * 100);
            Slider.SetBounds(6, 114, 288, 58);
            Slider.ValueChanged += delegate { Pct.Text = Slider.Value + "%"; main.SetBoostFromTray(Slider.Value); };

            Go.Text = "Start boost";
            Go.Font = MainForm.Semibold(11f);
            Go.SetBounds(16, 180, 268, 40);
            Go.Click += delegate { main.TrayToggle(); };

            What.Font = new Font("Segoe UI", 9f);
            What.SetBounds(16, 224, 268, 32);
            What.BackColor = Theme.Bg;
            What.ForeColor = Theme.Muted;

            openBtn.Text = "Open window";
            openBtn.Font = new Font("Segoe UI", 9f);
            openBtn.SetBounds(16, 258, 130, 28);
            Theme.Button(openBtn, Theme.Surface, Theme.SurfaceHot);
            openBtn.Click += delegate { main.RestoreFromTray(); };
            exitBtn.Text = "Exit";
            exitBtn.Font = new Font("Segoe UI", 9f);
            exitBtn.SetBounds(154, 258, 130, 28);
            Theme.Button(exitBtn, Theme.Surface, Theme.SurfaceHot);
            exitBtn.Click += delegate { Hide(); main.Close(); };

            Controls.Add(logo);
            Controls.Add(title);
            Controls.Add(Dot);
            Controls.Add(State);
            Controls.Add(how);
            Controls.Add(Pct);
            Controls.Add(Slider);
            Controls.Add(Go);
            Controls.Add(What);
            Controls.Add(openBtn);
            Controls.Add(exitBtn);
            Slider.TabIndex = 0; Go.TabIndex = 1; openBtn.TabIndex = 2; exitBtn.TabIndex = 3;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;   // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.UseRoundCorners(Handle, Theme.Border);
        }

        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); Hide(); }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Hide(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen p = new Pen(Theme.Border))
                e.Graphics.DrawRectangle(p, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        // Shows what the window shows: on = a boost runs, busy = it is starting, canStart =
        // there is something Start would boost.
        internal void Sync(bool on, bool busy, Color dot, string state, string what, bool canStart, int value)
        {
            Dot.Fill = dot;
            if (State.Text != state) State.Text = state;
            if (What.Text != what) What.Text = what;
            string go = busy ? "Starting..." : (on ? "Stop boost" : "Start boost");
            if (Go.Text != go) Go.Text = go;
            Go.Enabled = !busy && (on || canStart);
            int want = on ? 1 : 0;
            if (want != styled)
            {
                styled = want;
                if (on) Theme.Button(Go, Theme.Stop, Theme.StopHot); else Theme.Button(Go, Theme.Accent, Theme.AccentHot);
            }
            if (Slider.Value != value) Slider.Value = value;   // its ValueChanged sets the window's slider, which has it already
            Pct.Text = Slider.Value + "%";
        }
    }

    // The window, laid out as the three things a person does with it, top to bottom:
    // choose what to make louder, choose how loud, press the button. Below the button one
    // line says in plain words what is going on, and a smaller one gives the details.
    internal class MainForm : Form
    {
        CheckedListBox list = new AppList();
        Button refreshBtn = new Button();
        CheckBox chkAuto = new DarkCheck();   // "Auto-refresh", next to the Refresh button
        CheckBox chkSys = new DarkCheck();
        CheckBox chkAll = new DarkCheck();
        DarkSlider slider = new DarkSlider();
        Label boostLbl = new Label();
        Label boostHint = new Label();
        Button startBtn = new Button();
        Badge statusDot = new Badge();
        Label status = new Label();
        Label detail = new Label();
        System.Windows.Forms.Timer uiTimer = new System.Windows.Forms.Timer();
        BoostEngine engine;
        List<uint> listPids = new List<uint>();
        List<string> listNames = new List<string>();   // the process name each pid had when listed
        int tickN;
        int hint;                 // 1 = the "what to do" line is showing, 2 = the "no app yet" line
        string boostWhat = "";    // the chosen apps by name, for the status line
        string[] chosenNames = new string[0];   // the process names of the boost being started
        NotifyIcon tray = new NotifyIcon();
        TrayPopup popup;
        Icon trayOn, trayOff;
        bool trayIsOn;
        readonly List<IntPtr> iconHandles = new List<IntPtr>();

        const string ReadyLine = "Choose an app, set how loud, then press Start boost.";

        internal static Font Semibold(float size)
        {
            Font f = new Font("Segoe UI Semibold", size);
            if (f.Name != "Segoe UI Semibold") { f.Dispose(); f = new Font("Segoe UI", size, FontStyle.Bold); }
            return f;
        }

        Label Heading(string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Semibold(11.5f);
            l.SetBounds(x, y, w, 26);
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.BackColor = Theme.Bg;
            l.ForeColor = Theme.Text;
            return l;
        }

        Badge Step(string digit, int x, int y)
        {
            Badge b = new Badge();
            b.Text = digit;
            b.Font = Semibold(10f);
            b.SetBounds(x, y, 26, 26);
            return b;
        }

        public MainForm()
        {
            // the layout below is in 96-dpi pixels; this scales it with the display instead of
            // letting the text grow inside boxes that did not (the process is DPI-aware)
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            Text = "App Volume Booster";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            ClientSize = new Size(500, 584);
            Font = new Font("Segoe UI", 10f);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;

            // ---- the tray ----
            // Minimised, the window goes to the notification area instead of the taskbar. A
            // click on its icon brings the window back; a right-click opens a small panel with
            // the boost's slider and Start/Stop (TrayPopup).
            int small = SystemInformation.SmallIconSize.Width;
            trayOn = Theme.ToIcon(Theme.AppGlyph(small, true), iconHandles);
            trayOff = Theme.ToIcon(Theme.AppGlyph(small, false), iconHandles);
            Icon = Theme.ToIcon(Theme.AppGlyph(SystemInformation.IconSize.Width, true), iconHandles);
            tray.Icon = trayOff;
            tray.Text = "App Volume Booster - not boosting";
            tray.MouseUp += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right) ShowPopup();
                else if (e.Button == MouseButtons.Left) RestoreFromTray();
            };
            tray.BalloonTipClicked += delegate { RestoreFromTray(); };
            Resize += delegate { if (WindowState == FormWindowState.Minimized && Visible) ToTray(); };

            // ---- 1: what ----
            Badge one = Step("1", 20, 16);
            Label appLbl = Heading("Choose what to make louder", 54, 16, 226);

            refreshBtn.Text = "Refresh";
            refreshBtn.Font = new Font("Segoe UI", 9f);
            refreshBtn.SetBounds(396, 16, 84, 27);
            Theme.Button(refreshBtn, Theme.Surface, Theme.SurfaceHot);
            refreshBtn.Click += delegate { FillSessions(); };

            // The list refreshing itself is a choice: some would rather it only changed when
            // they press Refresh. The choice is kept for the next run - saved on a click, not
            // on every change of the box, so nothing but the user's own click writes the file.
            chkAuto.Text = "Auto-refresh";
            chkAuto.Font = new Font("Segoe UI", 9f);
            chkAuto.SetBounds(284, 16, 108, 27);
            chkAuto.Checked = Settings.AutoRefresh;
            chkAuto.CheckedChanged += delegate { ShowEmptyText(); };
            chkAuto.Click += delegate { Settings.AutoRefresh = chkAuto.Checked; if (chkAuto.Checked && engine == null && !starting) RefreshList(true); };
            ToolTip tip = new ToolTip();
            tip.SetToolTip(chkAuto, "While nothing is being boosted, update the list every 2 seconds.\r\nOff: the list only changes when you press Refresh.");

            list.CheckOnClick = true;
            list.IntegralHeight = false;
            list.BorderStyle = BorderStyle.FixedSingle;
            list.BackColor = Theme.Surface;
            list.ForeColor = Theme.Text;
            list.SetBounds(20, 52, 460, 182);

            ShowEmptyText();

            chkSys.Text = "Also boost Windows system sounds (beeps, notifications)";
            chkSys.SetBounds(20, 244, 460, 28);

            chkAll.Text = "Boost all audio on this device (every app and Windows sounds)";
            chkAll.SetBounds(20, 274, 460, 28);
            chkAll.CheckedChanged += delegate { SyncModeEnabled(); };

            // ---- 2: how loud ----
            Badge two = Step("2", 20, 322);
            Label volLbl = Heading("How loud?", 54, 322, 200);

            boostLbl.Text = "150%";
            boostLbl.Font = Semibold(20f);
            boostLbl.TextAlign = ContentAlignment.MiddleRight;
            boostLbl.SetBounds(340, 312, 142, 42);
            boostLbl.BackColor = Theme.Bg;
            boostLbl.ForeColor = Theme.AccentLight;

            slider.Minimum = 100; slider.Maximum = (int)(BoostEngine.MAXBOOST * 100);
            slider.Value = 150;
            slider.SetBounds(8, 362, 484, 58);
            slider.ValueChanged += delegate
            {
                ShowBoost();
                if (engine != null && !engine.Stopped) { engine.SetBoost(slider.Value); UpdateStatus(); }
                SyncTray();
            };

            boostHint.SetBounds(20, 422, 460, 22);
            boostHint.Font = new Font("Segoe UI", 9f);
            boostHint.BackColor = Theme.Bg;
            boostHint.ForeColor = Theme.Muted;
            ShowBoost();

            // ---- 3: go ----
            startBtn.Text = "Start boost";
            startBtn.Font = Semibold(12f);
            startBtn.SetBounds(20, 456, 460, 46);
            Theme.Button(startBtn, Theme.Accent, Theme.AccentHot);
            startBtn.Click += delegate { ToggleBoost(); };

            statusDot.SetBounds(20, 521, 12, 12);
            statusDot.Fill = Theme.Dim;
            status.SetBounds(40, 515, 440, 24);
            status.AutoEllipsis = true;
            status.BackColor = Theme.Bg;
            status.ForeColor = Theme.Text;
            detail.SetBounds(40, 540, 440, 38);
            detail.Font = new Font("Segoe UI", 9f);
            detail.BackColor = Theme.Bg;
            detail.ForeColor = Theme.Muted;
            SayReady();

            Controls.Add(one);
            Controls.Add(appLbl);
            Controls.Add(refreshBtn);
            Controls.Add(chkAuto);
            Controls.Add(list);
            Controls.Add(chkSys);
            Controls.Add(chkAll);
            Controls.Add(two);
            Controls.Add(volLbl);
            Controls.Add(boostLbl);
            Controls.Add(slider);
            Controls.Add(boostHint);
            Controls.Add(startBtn);
            Controls.Add(statusDot);
            Controls.Add(status);
            Controls.Add(detail);
            // the keyboard goes through the window in the order it is read
            list.TabIndex = 0; chkSys.TabIndex = 1; chkAll.TabIndex = 2; slider.TabIndex = 3; startBtn.TabIndex = 4; chkAuto.TabIndex = 5; refreshBtn.TabIndex = 6;

            uiTimer.Interval = 1000;
            uiTimer.Tick += delegate { OnTick(); };
            uiTimer.Start();

            HandleCreated += delegate
            {
                Native.UseDarkTitleBar(Handle);
                Native.UseDarkExplorerTheme(Handle);
                Native.UseDarkExplorerTheme(list.Handle);
            };
            Load += delegate
            {
                string healed = StateFile.SelfHeal();
                FillSessions();
                if (healed != null) Say(Theme.AccentLight, "Put " + healed + " back to its normal volume.", "A previous boost had ended without doing that.");
            };
            FormClosing += delegate { if (engine != null) engine.Stop(true); };
        }

        // Once a second.
        void OnTick()
        {
            UpdateStatus();
            tickN++;
            bool idle = (engine == null && !starting);
            // With "Auto-refresh" on, the list keeps itself up to date while nothing is being
            // boosted: an app that starts playing appears without anyone pressing Refresh.
            if (idle && chkAuto.Checked && tickN % 2 == 0 && WindowState != FormWindowState.Minimized) RefreshList(true);
            if (tickN % 5 == 0)
            {
                string healed = null;
                try { healed = StateFile.SelfHeal(); } catch { }
                if (healed != null && idle)
                    Say(Theme.AccentLight, "Put " + healed + " back to its normal volume.", "A boost had ended without doing that.");
            }
        }

        // What the empty list says depends on whether it will fill by itself.
        void ShowEmptyText()
        {
            ((AppList)list).EmptyText = chkAuto.Checked
                ? "Nothing is playing yet.\r\nStart a video or some music -\r\nthe app shows up here by itself."
                : "Nothing is playing yet.\r\nStart a video or some music,\r\nthen press Refresh.";
            list.Invalidate();
        }

        // ---- the two status lines ----
        void Say(Color dot, string headline, string more)
        {
            hint = 0;
            statusDot.Fill = dot;
            status.Text = headline;
            detail.Text = more ?? "";
            SyncTray();
        }

        void SayReady()
        {
            Say(Theme.Dim, ReadyLine, "");
            hint = 1;
        }

        void SayNoApps()
        {
            Say(Theme.Dim, "No app is playing sound yet.", "You can still boost all audio - the second box under the list.");
            hint = 2;
        }

        // The slider's value in words: the big number, and what it means.
        void ShowBoost()
        {
            int v = slider.Value;
            boostLbl.Text = v + "%";
            if (v <= 100) boostHint.Text = "100% is the normal volume - no boost.";
            else
                boostHint.Text = (v / 100.0).ToString("0.##", CultureInfo.InvariantCulture) + " times the normal volume" +
                    (v >= 300 ? " - very loud; loud passages may sound squashed." : ".");
        }

        static string Friendly(string stopReason)
        {
            if (stopReason == "target closed") return "the app was closed";
            return stopReason;
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
            list.Invalidate();
        }

        void FillSessions() { RefreshList(false); }

        // Rebuilds the app list. The periodic refresh (auto) leaves the list alone when nothing
        // about it has changed, and only ever swaps the two "what to do" lines of the status.
        void RefreshList(bool auto)
        {
            List<uint> keep = new List<uint>();
            List<string> keepNames = new List<string>();
            for (int i = 0; i < list.Items.Count; i++)
                if (list.GetItemChecked(i) && i < listPids.Count)
                {
                    keep.Add(listPids[i]);
                    if (i < listNames.Count) keepNames.Add(listNames[i]);
                }

            List<AppEntry> fresh = new List<AppEntry>();
            try
            {
                uint self = (uint)Process.GetCurrentProcess().Id;
                List<IAudioSessionControl> all = Native.AllRenderSessions();
                Dictionary<uint, AppEntry> byPid = new Dictionary<uint, AppEntry>();
                for (int i = 0; i < all.Count; i++)
                {
                    IAudioSessionControl sc = all[i];
                    IAudioSessionControl2 sc2 = (IAudioSessionControl2)sc;
                    uint pid; sc2.GetProcessId(out pid);
                    int st; sc2.GetState(out st);
                    if (pid == 0 || pid == self || st == K.StateExpired) continue;
                    AppEntry a;
                    if (!byPid.TryGetValue(pid, out a))
                    {
                        string name = Native.ProcessNameOf(pid);
                        if (name == null || Native.IsBoosterName(name) || Native.IsBoosterPid(pid)) continue;
                        a = new AppEntry();
                        a.Pid = pid; a.Name = name;
                        byPid[pid] = a;
                        fresh.Add(a);
                    }
                    if (st == K.StateActive) a.Playing = true;
                }
                // A Chromium browser gives up its audio session about 5 s after its last tab
                // goes quiet, and then Windows has nothing to list: the browser was "only
                // sometimes" there to check. A running one without a session is offered
                // anyway - its oldest process is the browser itself - and its sound is
                // ducked and boosted the moment it starts.
                Process[] procs = null;   // one snapshot for all the names (it was one per name)
                foreach (string bn in K.QuietBrowsers)
                {
                    bool listed = false;
                    foreach (AppEntry a in fresh)
                        if (string.Equals(a.Name, bn, StringComparison.OrdinalIgnoreCase)) { listed = true; break; }
                    if (listed) continue;
                    if (procs == null) procs = Process.GetProcesses();
                    Process oldest = null; DateTime oldestAt = DateTime.MaxValue;
                    foreach (Process pr in procs)
                    {
                        if (!string.Equals(pr.ProcessName, bn, StringComparison.OrdinalIgnoreCase)) continue;
                        try { if (pr.StartTime < oldestAt) { oldestAt = pr.StartTime; oldest = pr; } }
                        catch { }
                    }
                    if (oldest == null || (uint)oldest.Id == self) continue;
                    AppEntry q = new AppEntry();
                    q.Pid = (uint)oldest.Id; q.Name = oldest.ProcessName; q.Quiet = true;
                    fresh.Add(q);
                }
            }
            catch (Exception ex)
            {
                if (!auto) Say(Theme.Warn, "The list of apps could not be read.", ex.Message);
                return;
            }

            if (auto && SameList(fresh)) return;

            foreach (AppEntry a in fresh) a.Describe(Native.ProcessPathOf(a.Pid));
            foreach (AppEntry a in fresh)
                foreach (AppEntry b in fresh)
                    if (!object.ReferenceEquals(a, b) && string.Equals(a.Title, b.Title, StringComparison.OrdinalIgnoreCase)) { a.ShowPid = true; break; }
            fresh.Sort(delegate(AppEntry a, AppEntry b)
            {
                if (a.Playing != b.Playing) return a.Playing ? -1 : 1;
                int c = string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : a.Pid.CompareTo(b.Pid);
            });

            // A check stays with its app: with the same process, or - when that one is gone,
            // as after the app was restarted - with the first row of the same name.
            List<string> byName = new List<string>();
            for (int i = 0; i < keep.Count && i < keepNames.Count; i++)
            {
                bool still = false;
                foreach (AppEntry a in fresh) if (a.Pid == keep[i]) { still = true; break; }
                if (!still) byName.Add(keepNames[i]);
            }

            int top = list.TopIndex;
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                listPids.Clear();
                listNames.Clear();
                foreach (AppEntry a in fresh)
                {
                    listPids.Add(a.Pid);
                    listNames.Add(a.Name);
                    list.Items.Add(a);
                    bool on = keep.Contains(a.Pid);
                    if (!on)
                    {
                        for (int i = 0; i < byName.Count; i++)
                            if (string.Equals(byName[i], a.Name, StringComparison.OrdinalIgnoreCase)) { on = true; byName.RemoveAt(i); break; }
                    }
                    if (on) list.SetItemChecked(list.Items.Count - 1, true);
                }
                if (top > 0 && top < list.Items.Count) list.TopIndex = top;
            }
            finally { list.EndUpdate(); }
            list.Invalidate();

            bool idle = (engine == null || engine.Stopped) && !starting;
            if (list.Items.Count == 0)
            {
                if (idle && !chkAll.Checked && !chkSys.Checked && (!auto || hint == 1)) SayNoApps();
            }
            else if (hint == 2) SayReady();
        }

        // True when the list already shows exactly these apps, in these states.
        bool SameList(List<AppEntry> fresh)
        {
            if (fresh.Count != list.Items.Count) return false;
            foreach (AppEntry a in fresh)
            {
                bool found = false;
                for (int i = 0; i < list.Items.Count; i++)
                {
                    AppEntry b = list.Items[i] as AppEntry;
                    if (b == null) return false;
                    if (b.Pid == a.Pid) { found = (b.Playing == a.Playing && b.Quiet == a.Quiet && b.Name == a.Name); break; }
                }
                if (!found) return false;
            }
            return true;
        }

        List<uint> CheckedPids()
        {
            List<uint> pids = new List<uint>();
            for (int i = 0; i < list.Items.Count; i++)
                if (list.GetItemChecked(i) && i < listPids.Count) pids.Add(listPids[i]);
            return pids;
        }

        // The chosen apps as the status line names them: "Thorium", "Thorium and Discord", ...
        string CheckedTitles(bool all, bool sys)
        {
            if (all) return "all audio";
            List<string> t = new List<string>();
            for (int i = 0; i < list.Items.Count; i++)
            {
                if (!list.GetItemChecked(i)) continue;
                AppEntry a = list.Items[i] as AppEntry;
                string s = (a != null) ? a.Title : list.Items[i].ToString();
                if (!t.Contains(s)) t.Add(s);
            }
            if (sys) t.Add("Windows sounds");
            if (t.Count == 0) return "audio";
            if (t.Count == 1) return t[0];
            if (t.Count > 3) return t[0] + ", " + t[1] + " and " + (t.Count - 2) + " more";
            string last = t[t.Count - 1];
            t.RemoveAt(t.Count - 1);
            return string.Join(", ", t.ToArray()) + " and " + last;
        }

        void SetBusy(bool busy)
        {
            if (busy)
            {
                list.Enabled = false;
                refreshBtn.Enabled = false;
                chkSys.Enabled = false;
                chkAll.Enabled = false;
                list.Invalidate();
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
            if (starting) return;
            if (engine != null)
            {
                string why = (engine.Stopped && engine.StopReason != "") ? engine.StopReason : null;
                BoostEngine old = engine;
                old.Stop(true);
                engine = null;
                startBtn.Text = "Start boost";
                StyleActionButton(false);
                SetBusy(false);
                // refreshed here too: after a manual Stop the list kept pids that had since
                // gone (a restarted browser), and the next Start failed on one of them
                FillSessions();
                SayStopped(why, old.RestoreWarning);
                return;
            }
            List<uint> pids = CheckedPids();
            bool all = chkAll.Checked;
            bool sys = chkSys.Checked && !all;
            if (!all && pids.Count == 0 && !sys)
            {
                Say(Theme.Warn, "Nothing is chosen yet.", "Check an app in the list, or one of the two boxes below it.");
                return;
            }
            // A list entry is a pid noted when the list was filled. If that process has gone,
            // or the pid now belongs to something else, the boost must not start on it.
            string stale = all ? null : StaleChecked();
            if (stale != null)
            {
                FillSessions();
                Say(Theme.Warn, stale + " has closed or restarted.", "The list was updated - check the app again.");
                return;
            }
            boostWhat = CheckedTitles(all, sys);
            chosenNames = all ? new string[0] : CheckedNames();
            BoostEngine mine;
            try { mine = new BoostEngine(pids, slider.Value, all, sys); }
            catch (Exception ex)
            {
                FillSessions();
                Say(Theme.Warn, "The boost could not start.", ex.Message + " The list was updated - check the app again.");
                return;
            }
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
                        engine = null;
                        SetBusy(false);
                        // the list first: with the only playing app gone it ends in "No app
                        // is playing sound yet", which used to overwrite the reason
                        FillSessions();
                        SayStopped(mine.StopReason, mine.RestoreWarning);
                        // with the window in the tray nothing else would show that it ended
                        if (tray.Visible && !Visible && mine.StopReason != "")
                        {
                            string r = Friendly(mine.StopReason);
                            tray.ShowBalloonTip(5000, "Boost stopped", char.ToUpperInvariant(r[0]) + r.Substring(1) + ".", ToolTipIcon.None);
                        }
                    });
                }
                catch { }
            };
            // The start runs off the window's thread: its process snapshot and the capture
            // activation froze the window for 0.5-1 s (longer on a cold WMI), which is what
            // "sluggish" looked like. The button is disabled meanwhile.
            starting = true;
            SetBusy(true);
            startBtn.Enabled = false;
            Say(Theme.AccentLight, "Starting...", "");
            ThreadPool.QueueUserWorkItem(delegate
            {
                Exception err = null;
                try { mine.Start(); }
                catch (Exception ex) { err = ex; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        starting = false;
                        startBtn.Enabled = true;
                        // stopped itself already, or a stale start: its own notification resets
                        // the window, or already has
                        if (!object.ReferenceEquals(engine, mine)) return;
                        if (err != null)
                        {
                            engine = null;
                            SetBusy(false);
                            FillSessions();
                            Say(Theme.Warn, "The boost could not start.", err.Message);
                            return;
                        }
                        if (mine.Stopped) return;
                        // remembered for Start in the tray, which takes these when nothing is chosen
                        if (chosenNames.Length > 0) Settings.LastApps = chosenNames;
                        startBtn.Text = "Stop boost";
                        StyleActionButton(true);
                        UpdateStatus();
                    });
                }
                catch { }
            });
        }

        bool starting;

        string[] CheckedNames()
        {
            List<string> r = new List<string>();
            for (int i = 0; i < list.Items.Count && i < listNames.Count; i++)
                if (list.GetItemChecked(i) && !r.Contains(listNames[i])) r.Add(listNames[i]);
            return r.ToArray();
        }

        // ---- the tray ----
        internal void ToTray()
        {
            if (popup != null) popup.Hide();
            Hide();
            tray.Visible = true;
            SyncTray();
            if (!Settings.TrayHintShown)
            {
                // once: on Windows 11 a new tray icon starts out in the hidden part of the tray
                Settings.TrayHintShown = true;
                tray.ShowBalloonTip(6000, "App Volume Booster is still running",
                    "It is in the notification area now. Right-click its icon to change the boost; click it to open the window.", ToolTipIcon.None);
            }
        }

        internal void RestoreFromTray()
        {
            if (popup != null) popup.Hide();
            Show();
            WindowState = FormWindowState.Normal;
            tray.Visible = false;
            Activate();
            Native.SetForegroundWindow(Handle);
        }

        void ShowPopup()
        {
            if (popup == null) popup = new TrayPopup(this);
            // the list does not refresh itself while the window is in the tray: brought up to
            // date here, so that what the panel offers to boost is what is running now
            if (engine == null && !starting) RefreshList(true);
            SyncTray();
            Point c = Cursor.Position;
            Rectangle wa = Screen.FromPoint(c).WorkingArea;
            int x = Math.Max(wa.Left, Math.Min(c.X - popup.Width / 2, wa.Right - popup.Width));
            int y = c.Y - popup.Height - 8;
            if (y < wa.Top) y = c.Y + 8;   // a taskbar at the top of the screen
            y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - popup.Height));
            popup.Location = new Point(x, y);
            popup.Show();
            popup.Activate();
            Native.SetForegroundWindow(popup.Handle);
        }

        internal void SetBoostFromTray(int v) { if (slider.Value != v) slider.Value = v; }

        // Start or Stop in the tray panel.
        internal void TrayToggle()
        {
            if (starting) return;
            if (engine == null && !PrepareTrayBoost()) return;
            ToggleBoost();
        }

        // Puts the checks a Start in the tray uses, and says whether there is anything to boost.
        // What is chosen in the window counts; with nothing chosen, TrayPick decides.
        bool PrepareTrayBoost()
        {
            RefreshList(true);
            if (chkAll.Checked || chkSys.Checked || CheckedPids().Count > 0) return true;
            string why;
            List<int> pick = TrayPick(Entries(), Settings.LastApps, out why);
            if (pick.Count == 0)
            {
                Say(Theme.Warn, "Nothing to boost yet.", "Start some music or a video first, or open the window and choose an app.");
                return false;
            }
            foreach (int i in pick) list.SetItemChecked(i, true);
            return true;
        }

        List<AppEntry> Entries()
        {
            List<AppEntry> r = new List<AppEntry>();
            foreach (object o in list.Items) r.Add(o as AppEntry);
            return r;
        }

        // Which rows a Start in the tray takes when nothing is chosen: every app of the last
        // boost that is in the list (its playing row, if it has several), or - with none of
        // them there - the first app that is playing. Only that one: a guess stays small.
        internal static List<int> TrayPick(List<AppEntry> rows, string[] last, out string why)
        {
            List<int> r = new List<int>();
            why = "";
            foreach (string name in last ?? new string[0])
            {
                int best = -1;
                for (int i = 0; i < rows.Count; i++)
                {
                    AppEntry a = rows[i];
                    if (a == null || !string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (best < 0 || (a.Playing && !rows[best].Playing)) best = i;
                }
                if (best >= 0 && !r.Contains(best)) r.Add(best);
            }
            if (r.Count > 0) { why = "boosted last time"; return r; }
            for (int i = 0; i < rows.Count; i++)
                if (rows[i] != null && rows[i].Playing) { r.Add(i); why = "playing now"; break; }
            return r;
        }

        // What Start in the tray panel would boost, in words.
        string TrayPreview(out bool can)
        {
            can = true;
            if (chkAll.Checked) return "Start boosts all audio, as chosen in the window.";
            if (chkSys.Checked || CheckedPids().Count > 0)
                return "Start boosts " + CheckedTitles(false, chkSys.Checked) + ", as chosen in the window.";
            string why;
            List<int> pick = TrayPick(Entries(), Settings.LastApps, out why);
            if (pick.Count == 0) { can = false; return "Nothing is playing. Start some music or a video, or open the window."; }
            List<string> names = new List<string>();
            foreach (int i in pick)
            {
                AppEntry a = (AppEntry)list.Items[i];
                if (!names.Contains(a.Title)) names.Add(a.Title);
            }
            return "Start boosts " + string.Join(" and ", names.ToArray()) + " - " + why + ".";
        }

        // The tray icon, its tooltip and the panel follow the window.
        void SyncTray()
        {
            if (trayOn == null) return;   // the window is still being built
            bool on = engine != null && !engine.Stopped && !starting;
            if (on != trayIsOn) { tray.Icon = on ? trayOn : trayOff; trayIsOn = on; }
            string tip = on ? status.Text : "App Volume Booster - not boosting";
            if (tip.Length > 63) tip = tip.Substring(0, 60) + "...";   // NotifyIcon refuses more than 63
            if (tray.Text != tip) tray.Text = tip;
            if (popup == null) return;
            bool idle = engine == null && !starting;
            // the window's "what to do" lines speak of its list and buttons: the panel says less
            string state = (idle && hint != 0) ? "Not boosting" : status.Text;
            bool can = true;
            string what = idle ? TrayPreview(out can) : "The slider changes the boost at once.";
            popup.Sync(engine != null && !starting, starting, statusDot.Fill, state, what, can, slider.Value);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { tray.Visible = false; tray.Dispose(); } catch { }
                if (popup != null) { try { popup.Dispose(); } catch { } popup = null; }
            }
            base.Dispose(disposing);
            if (disposing)
            {
                foreach (IntPtr h in iconHandles) Native.DestroyIcon(h);
                iconHandles.Clear();
            }
        }

        // why == null: the user pressed Stop. Otherwise the boost ended by itself, and says why.
        void SayStopped(string why, string restoreWarning)
        {
            string more = string.IsNullOrEmpty(restoreWarning) ? "The volume is back to normal." : restoreWarning;
            if (string.IsNullOrEmpty(why)) Say(Theme.Dim, "Stopped.", more);
            else Say(Theme.Warn, "Stopped: " + Friendly(why), more);
        }

        // The name of the first checked entry whose process is gone or is now another program.
        string StaleChecked()
        {
            for (int i = 0; i < list.Items.Count; i++)
            {
                if (!list.GetItemChecked(i) || i >= listPids.Count || i >= listNames.Count) continue;
                string now = Native.ProcessNameOf(listPids[i]);
                if (now == null || !string.Equals(now, listNames[i], StringComparison.OrdinalIgnoreCase))
                {
                    AppEntry a = list.Items[i] as AppEntry;
                    return (a != null) ? a.Title : listNames[i];
                }
            }
            return null;
        }

        void UpdateStatus()
        {
            if (starting || engine == null || engine.Stopped) return;
            string warn = engine.StartWarning;
            string numbers = "Delay about " + engine.LatencyMs + " ms, dropouts: " + engine.Glitches + ".";
            if (engine.DuckedCount > 0)
            {
                // while boosting, each boosted app's own slider in the Volume Mixer is at 4%:
                // said here, because it is the one thing about a boost that looks broken
                Say(warn != "" ? Theme.Warn : Theme.AccentLight,
                    "Boosting " + boostWhat + " at " + slider.Value + "%",
                    warn != "" ? warn : "In the Windows Volume Mixer the app's own slider sits at 4% meanwhile - that is how it works. " + numbers);
            }
            else
            {
                // it used to claim "held at 4%" even when nothing was: the checked app silent,
                // or not the process that is making the sound
                Say(warn != "" ? Theme.Warn : Theme.Muted,
                    "Waiting - no sound from " + boostWhat + " yet",
                    warn != "" ? warn : "Nothing is boosted until it plays. The boost starts by itself when it does.");
            }
        }
    }

    // One booster window per Windows session. Starting the exe again while its window is
    // open - in the tray, or behind other windows - brings that window back instead of
    // opening a second one: in the tray it was easy to forget, and a second window meant a
    // second booster. The command line is not affected; scripts run as many as they like.
    internal static class SingleWindow
    {
        public const string Name = "AppVolumeBooster.Window";
        static Mutex held;               // kept for the life of the window
        static EventWaitHandle asked;    // set by a later start: "show yourself"

        // True when this process is now THE window. False when another one is, and was asked
        // to show itself. When that one cannot be asked (another user's, or one running at
        // another integrity level), this start opens a window of its own as before.
        public static bool Claim(string name)
        {
            bool createdNew;
            Mutex m;
            try { m = new Mutex(false, "Local\\" + name, out createdNew); }
            catch { return true; }
            if (createdNew) { held = m; return true; }
            m.Close();
            try
            {
                EventWaitHandle ev;
                if (!EventWaitHandle.TryOpenExisting("Local\\" + name + ".Show", out ev)) return true;
                using (ev)
                {
                    // this start is the one the user just made, so it may hand the foreground on
                    Native.AllowSetForegroundWindow(-1);   // ASFW_ANY
                    ev.Set();
                }
                return false;
            }
            catch { return true; }
        }

        // Calls show (on a thread-pool thread) each time a later start asks for the window.
        public static void Listen(string name, ThreadStart show)
        {
            try
            {
                asked = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\" + name + ".Show");
                ThreadPool.RegisterWaitForSingleObject(asked, delegate { show(); }, null, -1, false);
            }
            catch { }
        }
    }

    // ==================================== entry ======================================
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            Native.MarkAlive();
            try { Native.SetProcessDpiAwareness(1); } catch { }

            if (args.Length > 0) return CliMain(args);

            // a window is open already: it has been asked to come back, and this start ends here
            if (!SingleWindow.Claim(SingleWindow.Name)) return 0;
            MainForm form = null;
            SingleWindow.Listen(SingleWindow.Name, delegate
            {
                MainForm f = form;
                if (f == null) return;
                try { f.BeginInvoke((MethodInvoker)delegate { f.RestoreFromTray(); }); }
                catch { }   // not shown yet, or closing
            });

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            form = new MainForm();
            Application.Run(form);
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
                if (string.Equals(args[i], "--log", StringComparison.OrdinalIgnoreCase) && !args[i + 1].StartsWith("--")) log = args[i + 1];

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
                    (eng.StartWarning == "" ? "" : " warning=" + eng.StartWarning) + (eng.RestoreWarning == "" ? "" : " restore=" + eng.RestoreWarning),
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
