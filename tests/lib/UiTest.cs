using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AppVolumeBoosterNs
{
    // Drives the real MainForm (via reflection): first its app list, then the stop/start
    // race, then the tray, using real engines on private near-silent player processes - never
    // on any real app.
    // Arguments: <report file> <player pid> [<second player pid>]. The second player is
    // KILLED in step 7 to check the status line after a self-stop; the first is left alone.
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
        // the start runs off the UI thread now: pump until the window has heard back
        static void WaitStart(MainForm f)
        {
            for (int i = 0; i < 1500; i++)
            {
                Application.DoEvents();
                if (!(bool)F(f, "starting")) return;
                Thread.Sleep(10);
            }
        }
        static void FireStopped(BoostEngine e)
        {
            EventHandler h = (EventHandler)typeof(BoostEngine).GetField("StoppedEvent", NP).GetValue(e);
            if (h != null) h(e, EventArgs.Empty);
        }
        // the list as the window would have filled it: one checked entry for that player
        static void ListOnly(MainForm f, uint pid, string label)
        {
            CheckedListBox list = (CheckedListBox)F(f, "list");
            List<uint> listPids = (List<uint>)F(f, "listPids");
            List<string> listNames = (List<string>)F(f, "listNames");
            list.Items.Clear(); listPids.Clear(); listNames.Clear();
            list.Items.Add(label); listPids.Add(pid); listNames.Add(Process.GetProcessById((int)pid).ProcessName);
            list.SetItemChecked(0, true);
        }

        static AppEntry Row(string name, bool playing)
        {
            AppEntry e = new AppEntry();
            e.Name = name; e.Title = name; e.Playing = playing;
            return e;
        }

        [STAThread]
        public static int Main(string[] a)
        {
            Native.MarkAlive();   // as Program.Main does: this process holds boosts too
            uint player = uint.Parse(a[1]);
            uint player2 = a.Length > 2 ? uint.Parse(a[2]) : 0;
            MainForm f = new MainForm();
            // shown, in step 9, far off the screen
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new System.Drawing.Point(-4000, -4000);
            IntPtr h = f.Handle;
            Label status = (Label)F(f, "status");
            ListOnly(f, player, "player");
            BoostEngine leftover = null;
            try
            {
                log.AppendLine("  -- 0. the app list: what a row says, and what happens to a check when the list is refreshed --");
                MethodInfo fill = typeof(MainForm).GetMethod("FillSessions", NP);      // the Refresh button
                MethodInfo refresh = typeof(MainForm).GetMethod("RefreshList", NP);    // (true) = the window's own periodic refresh
                CheckedListBox rows = (CheckedListBox)F(f, "list");
                List<uint> rowPids = (List<uint>)F(f, "listPids");
                List<string> rowNames = (List<string>)F(f, "listNames");
                fill.Invoke(f, null);
                int at = rowPids.IndexOf(player);
                Check(at >= 0, "the player is in the list");
                if (at >= 0)
                {
                    AppEntry row = rows.Items[at] as AppEntry;
                    Check(row != null && row.Title == "Windows PowerShell" && row.Picture != null,
                        "under the name and icon Windows shows for it: \"" + (row == null ? "?" : row.Title) + "\" (the list used to say \"powershell (pid ...)\")");
                    Check(row != null && row.ToString().StartsWith("powershell  (pid " + player), "its plain-text form still names the process and the pid");
                    rows.SetItemChecked(at, true);
                    // another app starting or stopping its sound changes the list for real, so this gets three tries
                    bool leftAlone = false;
                    for (int i = 0; i < 3 && !leftAlone; i++)
                    {
                        at = rowPids.IndexOf(player);
                        object before = at >= 0 ? rows.Items[at] : null;
                        refresh.Invoke(f, new object[] { true });
                        at = rowPids.IndexOf(player);
                        leftAlone = at >= 0 && object.ReferenceEquals(rows.Items[at], before);
                    }
                    at = rowPids.IndexOf(player);
                    Check(leftAlone, "the periodic refresh leaves a list alone that has not changed");
                    Check(at >= 0 && rows.GetItemChecked(at), "and the check is still there");
                    fill.Invoke(f, null);
                    at = rowPids.IndexOf(player);
                    Check(at >= 0 && rows.GetItemChecked(at), "a rebuilt list (the Refresh button) keeps the check on the same app");
                    // the checked app "restarts": its old process id is gone, its name is back under a new one
                    if (at >= 0) rowPids[at] = 3999999996;
                    fill.Invoke(f, null);
                    int byName = 0;
                    for (int i = 0; i < rows.Items.Count; i++)
                        if (rows.GetItemChecked(i) && rowNames[i] == "powershell") byName++;
                    Check(byName == 1, "when the checked app comes back as a new process, the check goes to a row of the same name (" + byName + " checked)");
                }
                // "Auto-refresh": off, the list only changes on Refresh; on, a second or two
                // brings a missing app back. (The box is set from code here, which saves nothing:
                // only a click of the user's writes the setting.)
                CheckBox auto = (CheckBox)F(f, "chkAuto");
                MethodInfo tick = typeof(MainForm).GetMethod("OnTick", NP);
                bool autoWas = auto.Checked;
                auto.Checked = false;
                rows.Items.Clear(); rowPids.Clear(); rowNames.Clear();
                for (int i = 0; i < 6; i++) tick.Invoke(f, null);
                Check(rows.Items.Count == 0, "with Auto-refresh off, the list is left as it is (" + rows.Items.Count + " rows after 6 ticks)");
                auto.Checked = true;
                for (int i = 0; i < 4 && rowPids.IndexOf(player) < 0; i++) tick.Invoke(f, null);
                Check(rowPids.IndexOf(player) >= 0, "with it on, the player is back in the list within a few ticks");
                auto.Checked = autoWas;
                string ready = status.Text;
                Check(ready.IndexOf("press Start boost") >= 0, "the window says what to do next (" + ready + ")");
                ListOnly(f, player, "player");   // from here on, exactly one known row

                log.AppendLine("  -- 1. start a real boost on the private player --");
                Toggle(f);
                WaitStart(f);
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
                ListOnly(f, player, "player");   // the stop refreshed the list; check the player again
                if (afterClick == null) { Toggle(f); WaitStart(f); }
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

                if (player2 != 0)
                {
                    log.AppendLine("  -- 7. boost the second player, then kill it: the status line must say why the boost ended --");
                    ListOnly(f, player2, "player2");
                    Toggle(f);
                    WaitStart(f);
                    BoostEngine D = (BoostEngine)F(f, "engine");
                    Check(D != null && !D.Stopped, "engine D is running on the second player");
                    leftover = D;
                    try { Process.GetProcessById((int)player2).Kill(); } catch { }
                    for (int i = 0; i < 800 && F(f, "engine") != null; i++) { Application.DoEvents(); Thread.Sleep(10); }
                    Pump();
                    Check(F(f, "engine") == null, "the engine stopped itself after its target was killed");
                    Check(status.Text.StartsWith("Stopped: the app was closed"), "the status keeps the reason, in plain words (" + status.Text + ")");
                    leftover = null;

                    log.AppendLine("  -- 8. the list still shows that dead player checked (a stale entry): Start must refuse it --");
                    CheckedListBox list = (CheckedListBox)F(f, "list");
                    List<uint> listPids = (List<uint>)F(f, "listPids");
                    List<string> listNames = (List<string>)F(f, "listNames");
                    list.Items.Clear(); listPids.Clear(); listNames.Clear();
                    list.Items.Add("player2"); listPids.Add(player2); listNames.Add("powershell"); list.SetItemChecked(0, true);
                    Toggle(f);
                    WaitStart(f);
                    Check(F(f, "engine") == null, "no boost was started on a process that is gone");
                    Check(status.Text.IndexOf("has closed or restarted") >= 0, "and the window says so (" + status.Text + ")");
                }

                log.AppendLine("  -- 9. the tray: what Start there takes, minimising into it, Start/Stop and the slider in its panel --");
                // no "still running" notice on the screen during a test (the rig puts settings.txt back)
                Settings.TrayHintShown = true;
                // the choice when nothing is checked - on made-up rows, nothing is boosted here
                string why;
                AppEntry aimp = Row("AIMP", true), thorQuiet = Row("thorium", false), thorPlaying = Row("thorium", true), ps = Row("powershell", true);
                List<int> pick = MainForm.TrayPick(new List<AppEntry> { aimp, thorQuiet, ps }, new string[] { "thorium" }, out why);
                Check(pick.Count == 1 && pick[0] == 1 && why == "boosted last time", "the app of the last boost is taken, also while it is quiet");
                pick = MainForm.TrayPick(new List<AppEntry> { aimp, thorQuiet, thorPlaying }, new string[] { "Thorium" }, out why);
                Check(pick.Count == 1 && pick[0] == 2, "of two rows of that app, the one that is playing");
                pick = MainForm.TrayPick(new List<AppEntry> { aimp, thorQuiet, ps }, new string[0], out why);
                Check(pick.Count == 1 && pick[0] == 0 && why == "playing now", "with no last boost, the first app that is playing - and only that one");
                pick = MainForm.TrayPick(new List<AppEntry> { thorQuiet, ps }, new string[] { "gone" }, out why);
                Check(pick.Count == 1 && pick[0] == 1, "a last app that is not running falls back to the one playing");
                pick = MainForm.TrayPick(new List<AppEntry> { thorQuiet }, new string[] { "gone" }, out why);
                Check(pick.Count == 0, "nothing playing and nothing remembered: nothing is taken");

                // minimised, the window goes to the tray (it is shown far off the screen)
                NotifyIcon tray = (NotifyIcon)F(f, "tray");
                f.Show();
                Pump();
                f.WindowState = FormWindowState.Minimized;
                Pump();
                Check(!f.Visible && tray.Visible, "minimised, the window leaves the taskbar and its icon is in the tray");
                f.RestoreFromTray();
                Pump();
                Check(f.Visible && f.WindowState == FormWindowState.Normal && !tray.Visible, "a click on the icon brings it back, and the icon goes");

                // Start in the panel, with the player checked in the window. The checks are looked
                // at BEFORE anything starts: a Start from the tray must never reach a real app.
                ListOnly(f, player, "player");
                TrayPopup pop = new TrayPopup(f);
                SetF(f, "popup", pop);
                bool prepared = (bool)typeof(MainForm).GetMethod("PrepareTrayBoost", NP).Invoke(f, null);
                List<uint> chosen = (List<uint>)typeof(MainForm).GetMethod("CheckedPids", NP).Invoke(f, null);
                bool onlyPlayer = prepared && chosen.Count == 1 && chosen[0] == player;
                typeof(MainForm).GetMethod("SyncTray", NP).Invoke(f, null);   // as a right-click on the icon does
                Check(onlyPlayer, "with the player checked in the window, the tray takes that - and only that (" + chosen.Count + " checked)");
                Check(pop.What.Text.IndexOf("as chosen in the window") >= 0, "and the panel says so (" + pop.What.Text + ")");
                if (onlyPlayer)
                {
                    Toggle(f);
                    WaitStart(f);
                    BoostEngine T = (BoostEngine)F(f, "engine");
                    leftover = T;
                    Check(T != null && !T.Stopped, "Start boosts it");
                    Check(pop.Go.Text == "Stop boost" && pop.Go.Enabled, "the panel's button now reads Stop boost");
                    string[] last = Settings.LastApps;
                    Check(last.Length == 1 && last[0] == "powershell", "the app is remembered as the last boost (" + string.Join("|", last) + ")");
                    pop.Slider.Value = 200;
                    DarkSlider main = (DarkSlider)F(f, "slider");
                    Check(main.Value == 200 && T != null && T.BoostPercent == 200, "the panel's slider changes the running boost (" + (T == null ? 0 : T.BoostPercent) + "%)");
                    main.Value = 150;
                    Check(pop.Slider.Value == 150, "and follows the window's slider");
                    f.TrayToggle();
                    Check(F(f, "engine") == null && T != null && T.Stopped, "Stop in the panel stops it");
                    Check(pop.Go.Text == "Start boost", "and the button reads Start boost again");
                    leftover = null;
                }

                log.AppendLine("  -- 10. the exe started again while the window is in the tray: that window comes back --");
                // as Program.Main does it, under a name of this test's own (never the real one)
                string name = "AppVolumeBooster.Test." + Process.GetCurrentProcess().Id;
                Check(SingleWindow.Claim(name), "the first start claims the window");
                SingleWindow.Listen(name, delegate { try { f.BeginInvoke((MethodInvoker)delegate { f.RestoreFromTray(); }); } catch { } });
                f.ToTray();
                Pump();
                Check(!f.Visible && tray.Visible, "the window is in the tray");
                Check(!SingleWindow.Claim(name), "a second start finds it, asks it to show itself, and opens nothing of its own");
                for (int i = 0; i < 300 && !f.Visible; i++) { Application.DoEvents(); Thread.Sleep(10); }
                Check(f.Visible && f.WindowState == FormWindowState.Normal && !tray.Visible, "and the window is back, out of the tray");
                f.Hide();
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
