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
    //                                      digital silence >= 1 ms between its first and last sound,
    //                                      plus the level of the sound (rms and peak, full scale = 1)
    //   procs <pid,pid,...> <out>          the booster's own view of the processes: a line
    //                                      "pid|parent|name" for each one running, "ask|pid|name"
    //                                      for each pid asked about, and how long both lookups take
    //   sleep <ms>                         just stay alive that long
    //   claim <name> <ms> <out>            SingleWindow.Claim under a test name. First: writes
    //                                      "claimed", listens <ms> long, then "first shown=<n>"
    //                                      (n = how often a later start asked it to show).
    //                                      Not first: writes "second" at once. No window either way.
    //   shot  <out.png> [idle|boosting|waiting|empty|tray|tray-on]
    //                                      (tray, tray-on: the panel a right-click on the tray
    //                                      icon opens, with made-up text)
    //                                      a picture of the booster's window, shown far off
    //                                      screen. With a look given, the list holds stand-in
    //                                      rows and the window is dressed as in that state;
    //                                      nothing is boosted.
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
            if (mode == "sleep") { Thread.Sleep(int.Parse(a[1], CultureInfo.InvariantCulture)); return 0; }
            if (mode == "claim")
            {
                if (!SingleWindow.Claim(a[1])) { File.WriteAllText(a[3], "second"); return 0; }
                int shown = 0;
                SingleWindow.Listen(a[1], delegate { Interlocked.Increment(ref shown); });
                File.WriteAllText(a[3], "claimed");
                Thread.Sleep(int.Parse(a[2], CultureInfo.InvariantCulture));
                File.WriteAllText(a[3], "first shown=" + shown);
                return 0;
            }
            if (mode == "procs")
            {
                StringBuilder sb = new StringBuilder();
                Dictionary<uint, uint> map = Native.ParentMap();
                foreach (KeyValuePair<uint, uint> kv in map)
                    sb.AppendLine(kv.Key.ToString() + "|" + kv.Value.ToString() + "|" + (Native.ProcessNameOf(kv.Key) ?? ""));
                foreach (string s in a[1].Split(','))
                {
                    uint q; if (!uint.TryParse(s, out q)) continue;
                    sb.AppendLine("ask|" + q.ToString() + "|" + (Native.ProcessNameOf(q) ?? ""));
                }
                // medians, so that one interrupted call does not decide the result
                uint self = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                List<double> tn = new List<double>(), tm = new List<double>();
                for (int i = 0; i < 200; i++)
                {
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    Native.ProcessNameOf(self);
                    tn.Add((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                }
                for (int i = 0; i < 21; i++)
                {
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    Native.ParentMap();
                    tm.Add((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                }
                tn.Sort(); tm.Sort();
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "time|nameMs={0:F4}|mapMs={1:F3}|processes={2}", tn[tn.Count / 2], tm[tm.Count / 2], map.Count));
                File.WriteAllText(a[2], sb.ToString());
                return 0;
            }
            if (mode == "shot")
            {
                System.Reflection.BindingFlags np = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                MainForm f = new MainForm();
                f.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
                f.Location = new System.Drawing.Point(-4000, -4000);
                f.ShowInTaskbar = false;
                f.Show();
                for (int i = 0; i < 40; i++) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(10); }
                string look = a.Length > 2 ? a[2] : "";
                if (look != "")
                {
                    System.Windows.Forms.CheckedListBox list = (System.Windows.Forms.CheckedListBox)typeof(MainForm).GetField("list", np).GetValue(f);
                    List<uint> listPids = (List<uint>)typeof(MainForm).GetField("listPids", np).GetValue(f);
                    List<string> listNames = (List<string>)typeof(MainForm).GetField("listNames", np).GetValue(f);
                    list.Items.Clear(); listPids.Clear(); listNames.Clear();
                    string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    string[] exes = {
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Thorium\Application\thorium.exe"),
                        Path.Combine(win, "explorer.exe"), Path.Combine(win, @"System32\notepad.exe"),
                        Path.Combine(win, @"System32\mspaint.exe"), Path.Combine(win, @"System32\WindowsPowerShell\v1.0\powershell.exe"),
                        Path.Combine(win, @"System32\SnippingTool.exe"), Path.Combine(win, @"System32\cmd.exe") };
                    uint fake = 1000;
                    foreach (string exe in exes)
                    {
                        if (look == "empty" || !File.Exists(exe)) continue;
                        AppEntry e = new AppEntry();
                        e.Pid = fake; fake += 444;
                        e.Name = Path.GetFileNameWithoutExtension(exe);
                        e.Playing = list.Items.Count < 2;
                        e.Describe(exe);
                        list.Items.Add(e); listPids.Add(e.Pid); listNames.Add(e.Name);
                    }
                    if (list.Items.Count > 0) list.SetItemChecked(0, true);
                    if (look == "boosting" || look == "waiting")
                    {
                        System.Windows.Forms.Button start = (System.Windows.Forms.Button)typeof(MainForm).GetField("startBtn", np).GetValue(f);
                        start.Text = "Stop boost";
                        typeof(MainForm).GetMethod("StyleActionButton", np).Invoke(f, new object[] { true });
                        typeof(MainForm).GetMethod("SetBusy", np).Invoke(f, new object[] { true });
                        string title = list.Items.Count > 0 ? ((AppEntry)list.Items[0]).Title : "the app";
                        if (look == "boosting")
                            typeof(MainForm).GetMethod("Say", np).Invoke(f, new object[] { Theme.AccentLight, "Boosting " + title + " at 150%",
                                "In the Windows Volume Mixer the app's own slider sits at 4% meanwhile - that is how it works. Delay about 60 ms, dropouts: 0." });
                        else
                            typeof(MainForm).GetMethod("Say", np).Invoke(f, new object[] { Theme.Muted, "Waiting - no sound from " + title + " yet",
                                "Nothing is boosted until it plays. The boost starts by itself when it does." });
                    }
                    list.Invalidate();
                    for (int i = 0; i < 20; i++) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(10); }
                }
                System.Windows.Forms.Form pic = f;
                if (look == "tray" || look == "tray-on")
                {
                    TrayPopup pop = new TrayPopup(f);
                    pop.Location = new System.Drawing.Point(-4000, -4000);
                    pop.Show();
                    if (look == "tray") pop.Sync(false, false, Theme.Dim, "Not boosting", "Start boosts Thorium - boosted last time.", true, 150);
                    else pop.Sync(true, false, Theme.AccentLight, "Boosting Thorium at 150%", "The slider changes the boost at once.", true, 150);
                    for (int i = 0; i < 20; i++) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(10); }
                    pic = pop;
                }
                using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(pic.Width, pic.Height))
                {
                    pic.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, pic.Width, pic.Height));
                    bmp.Save(a[1], System.Drawing.Imaging.ImageFormat.Png);
                }
                if (!object.ReferenceEquals(pic, f)) pic.Close();
                f.Close();
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
                    // the level between the first and the last sound (each frame's louder channel)
                    double sq = 0; float peak = 0; int nLevel = 0;
                    for (int i = Math.Max(first, 0); first >= 0 && i <= last; i++) { sq += (double)fr[i] * fr[i]; if (fr[i] > peak) peak = fr[i]; nLevel++; }
                    report = string.Format(CultureInfo.InvariantCulture,
                        "gaps={0} gapMs={1:0.0} longestMs={2:0.0} soundMs={3} recordedMs={4} discont={5} rms={7:0.0000000} peak={8:0.0000000} at:{6}",
                        gaps, gapFrames / 48.0, longest / 48.0, first < 0 ? 0 : (last - first) / 48, fr.Count / 48, discont, at.ToString(),
                        nLevel == 0 ? 0.0 : Math.Sqrt(sq / nLevel), peak);
                });
                File.WriteAllText(a[3], report ?? "(no report)");
                return 0;
            }
            return 2;
        }
    }
}
