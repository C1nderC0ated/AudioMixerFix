using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AppVolumeBoosterNs
{
    // Test probe, compiled together with AppVolumeBooster.cs for its COM interop.
    //   get   <pid> <out>                  volume + mute of that pid's sessions (default device)
    //   set   <pid> <vol|-> <mute 0|1|->  set volume and/or mute of that pid's sessions
    //   watch <pid> <ms> <out>             sample volume + mute every 50 ms, log transitions
    //   gaps  <pid> <ms> <out>             RECORD that process tree's output (process loopback,
    //                                      nothing is played) and report every run of exact
    //                                      digital silence >= 1 ms between its first and last sound
    // Only ever touches the one pid it is given.
    internal static class Probes
    {
        static List<object[]> SessionsOf(uint pid)
        {
            List<object[]> r = new List<object[]>();
            IMMDevice dev = Native.DefaultRenderDevice();
            IAudioSessionManager2 mgr = Native.SessionManager(dev);
            IAudioSessionEnumerator en; Native.Check(mgr.GetSessionEnumerator(out en), "sessions");
            int count; en.GetCount(out count);
            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl sc; if (en.GetSession(i, out sc) != 0) continue;
                IAudioSessionControl2 sc2 = (IAudioSessionControl2)sc;
                uint p; sc2.GetProcessId(out p);
                if (p == pid) r.Add(new object[] { sc, (ISimpleAudioVolume)sc });
            }
            return r;
        }

        static string Fmt(ISimpleAudioVolume v)
        {
            float lvl; v.GetMasterVolume(out lvl);
            bool m; v.GetMute(out m);
            return string.Format(CultureInfo.InvariantCulture, "vol={0:F3} mute={1}", lvl, m ? 1 : 0);
        }

        [STAThread]
        public static int Main(string[] a)
        {
            string mode = a[0];
            if (mode == "devices")
            {
                StringBuilder sb = new StringBuilder();
                List<IMMDevice> devs = Native.ActiveRenderDevices();
                sb.AppendLine("count=" + devs.Count);
                foreach (IMMDevice dv in devs) { string id; dv.GetId(out id); sb.AppendLine(id); }
                sb.AppendLine("sessions=" + Native.AllRenderSessions().Count);
                File.WriteAllText(a[1], sb.ToString());
                return 0;
            }
            if (mode == "list")
            {
                System.Reflection.BindingFlags np = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                MainForm f = new MainForm();
                IntPtr h = f.Handle;
                typeof(MainForm).GetMethod("FillSessions", np).Invoke(f, null);
                System.Windows.Forms.CheckedListBox list = (System.Windows.Forms.CheckedListBox)typeof(MainForm).GetField("list", np).GetValue(f);
                StringBuilder sb = new StringBuilder();
                foreach (object it in list.Items) sb.AppendLine(it.ToString());
                File.WriteAllText(a[1], sb.ToString());
                f.Dispose();
                return 0;
            }
            uint pid = uint.Parse(a[1], CultureInfo.InvariantCulture);
            if (mode == "get")
            {
                StringBuilder sb = new StringBuilder();
                foreach (object[] s in SessionsOf(pid)) sb.AppendLine(Fmt((ISimpleAudioVolume)s[1]));
                File.WriteAllText(a[2], sb.Length == 0 ? "(no session)" : sb.ToString().Trim());
                return 0;
            }
            if (mode == "set")
            {
                foreach (object[] s in SessionsOf(pid))
                {
                    ISimpleAudioVolume v = (ISimpleAudioVolume)s[1];
                    if (a[2] != "-") v.SetMasterVolume(float.Parse(a[2], CultureInfo.InvariantCulture), IntPtr.Zero);
                    if (a[3] != "-") v.SetMute(a[3] == "1", IntPtr.Zero);
                }
                return 0;
            }
            if (mode == "watch")
            {
                int ms = int.Parse(a[2], CultureInfo.InvariantCulture);
                StringBuilder sb = new StringBuilder();
                string last = null;
                float minV = 9, maxV = -1; int samples = 0, muteSamples = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < ms)
                {
                    try
                    {
                        foreach (object[] s in SessionsOf(pid))
                        {
                            ISimpleAudioVolume v = (ISimpleAudioVolume)s[1];
                            float lvl; v.GetMasterVolume(out lvl);
                            bool m; v.GetMute(out m);
                            samples++; if (m) muteSamples++;
                            if (lvl < minV) minV = lvl; if (lvl > maxV) maxV = lvl;
                            string cur = Fmt(v);
                            if (cur != last) { sb.AppendLine(string.Format("  {0,6} ms  {1}", sw.ElapsedMilliseconds, cur)); last = cur; }
                            break;
                        }
                    }
                    catch { }
                    Thread.Sleep(50);
                }
                sb.Insert(0, string.Format(CultureInfo.InvariantCulture,
                    "samples={0} muted={1} min={2:F3} max={3:F3}\r\n", samples, muteSamples, minV, maxV));
                File.WriteAllText(a[3], sb.ToString());
                return 0;
            }
            if (mode == "gaps")
            {
                int ms = int.Parse(a[2], CultureInfo.InvariantCulture);
                string report = null;
                Native.RunMta(delegate()
                {
                    IAudioClient cl = Native.ActivateProcessLoopback(pid, false);
                    WaveFormatEx fmt = WaveFormatEx.Float32Stereo48k();
                    AutoResetEvent ev = new AutoResetEvent(false);
                    Native.Check(cl.Initialize(K.SHARED, K.LOOPBACK | K.EVENTCB, 2000000, 0, ref fmt, IntPtr.Zero), "gaps Initialize");
                    Native.Check(cl.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle()), "gaps SetEventHandle");
                    Guid iid = Native.IID_IAudioCaptureClient; object o;
                    Native.Check(cl.GetService(ref iid, out o), "gaps GetService");
                    IAudioCaptureClient cap = (IAudioCaptureClient)o;
                    List<float> fr = new List<float>(ms * 48 + 48000);
                    int discont = 0;
                    float[] buf = new float[48000 * 2];
                    Native.Check(cl.Start(), "gaps Start");
                    System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < ms)
                    {
                        ev.WaitOne(100);
                        uint pkt;
                        while (cap.GetNextPacketSize(out pkt) == 0 && pkt > 0)
                        {
                            IntPtr p; uint n, fl; ulong dp, qp;
                            if (cap.GetBuffer(out p, out n, out fl, out dp, out qp) != 0) break;
                            if ((fl & 1) != 0) discont++;
                            if ((fl & K.BUF_SILENT) != 0) { for (int i = 0; i < n; i++) fr.Add(0f); }
                            else
                            {
                                if (n * 2 > buf.Length) buf = new float[n * 2];
                                Marshal.Copy(p, buf, 0, (int)n * 2);
                                for (int i = 0; i < n; i++) fr.Add(Math.Max(Math.Abs(buf[2 * i]), Math.Abs(buf[2 * i + 1])));
                            }
                            cap.ReleaseBuffer(n);
                        }
                    }
                    cl.Stop();
                    int first = -1, last = -1;
                    for (int i = 0; i < fr.Count; i++) if (fr[i] != 0f) { if (first < 0) first = i; last = i; }
                    int gaps = 0, longest = 0; long gapFrames = 0; int run = 0;
                    StringBuilder at = new StringBuilder();
                    for (int i = Math.Max(first, 0); first >= 0 && i <= last; i++)
                    {
                        if (fr[i] == 0f) { run++; continue; }
                        if (run >= 48)
                        {
                            gaps++; gapFrames += run; if (run > longest) longest = run;
                            if (at.Length < 300) at.AppendFormat(CultureInfo.InvariantCulture, " {0}ms:{1:0.0}", (i - run - first) / 48, run / 48.0);
                        }
                        run = 0;
                    }
                    report = string.Format(CultureInfo.InvariantCulture,
                        "gaps={0} gapMs={1:0.0} longestMs={2:0.0} soundMs={3} recordedMs={4} discont={5} at:{6}",
                        gaps, gapFrames / 48.0, longest / 48.0, first < 0 ? 0 : (last - first) / 48, fr.Count / 48, discont, at.ToString());
                });
                File.WriteAllText(a[3], report ?? "(no report)");
                return 0;
            }
            return 2;
        }
    }
}
