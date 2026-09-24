using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AppVolumeBoosterNs
{
    // Drives the real MainForm (via reflection) through the stop/start race, using real
    // engines on a private near-silent player process - never on any real app.
    internal static class UiTest
    {
        static readonly BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
        static StringBuilder log = new StringBuilder();
        static bool anyFail;
        static void Check(bool c, string what) { log.AppendLine((c ? "    PASS  " : "    FAIL  ") + what); if (!c) anyFail = true; }
        static object F(MainForm f, string n) { return typeof(MainForm).GetField(n, NP).GetValue(f); }
        static void SetF(MainForm f, string n, object v) { typeof(MainForm).GetField(n, NP).SetValue(f, v); }
        static void Toggle(MainForm f) { typeof(MainForm).GetMethod("ToggleBoost", NP).Invoke(f, null); }
        static void Pump() { for (int i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(10); } }
        static void FireStopped(BoostEngine e)
        {
            EventHandler h = (EventHandler)typeof(BoostEngine).GetField("StoppedEvent", NP).GetValue(e);
            if (h != null) h(e, EventArgs.Empty);
        }

        [STAThread]
        public static int Main(string[] a)
        {
            uint player = uint.Parse(a[1]);
            MainForm f = new MainForm();
            IntPtr h = f.Handle;
            Label status = (Label)F(f, "status");
            CheckedListBox list = (CheckedListBox)F(f, "list");
            List<uint> listPids = (List<uint>)F(f, "listPids");
            list.Items.Clear(); listPids.Clear();
            list.Items.Add("player"); listPids.Add(player); list.SetItemChecked(0, true);
            BoostEngine leftover = null;
            try
            {
                log.AppendLine("  -- 1. start a real boost on the private player --");
                Toggle(f);
                BoostEngine A = (BoostEngine)F(f, "engine");
                Check(A != null && !A.Stopped, "engine A is running (" + status.Text + ")");

                log.AppendLine("  -- 2. A stops itself (as on 'target closed'); its notification is still queued --");
                A.Stop(true);

                log.AppendLine("  -- 3. user clicks the button, which still reads 'Stop boost' --");
                Toggle(f);
                object afterClick = F(f, "engine");
                Check(afterClick == null, "the click was a STOP - no new boost was started");
                if (afterClick != null) leftover = (BoostEngine)afterClick;

                log.AppendLine("  -- 4. user immediately starts again; engine C takes over --");
                if (afterClick == null) Toggle(f);
                BoostEngine C = (BoostEngine)F(f, "engine");
                Check(C != null && !C.Stopped && !object.ReferenceEquals(C, A), "engine C is running");

                log.AppendLine("  -- 5. A's stale notification finally arrives --");
                FireStopped(A);
                Pump();
                Check(object.ReferenceEquals(F(f, "engine"), C), "C is still the current engine (not orphaned)");
                leftover = C;

                log.AppendLine("  -- 6. stop C normally --");
                Toggle(f);
                Check(F(f, "engine") == null && C.Stopped, "C stopped from the UI");
                leftover = null;
            }
            finally
            {
                if (leftover != null && !leftover.Stopped) { leftover.Stop(true); log.AppendLine("    (cleanup: stopped an orphaned engine)"); }
            }
            File.WriteAllText(a[0], log.ToString());
            f.Dispose();
            return anyFail ? 1 : 0;
        }
    }
}
