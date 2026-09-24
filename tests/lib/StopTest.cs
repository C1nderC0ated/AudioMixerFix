using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace AppVolumeBoosterNs
{
    // Stress test: 8 threads stop the same running engine at the same instant - half
    // through Stop(true), half through the private StopBecause - N times over, on the
    // private near-silent player only.
    internal static class StopTest
    {
        static float PlayerVolume(uint pid)
        {
            foreach (IAudioSessionControl sc in Native.AllRenderSessions())
            {
                uint p; ((IAudioSessionControl2)sc).GetProcessId(out p);
                if (p != pid) continue;
                float v; ((ISimpleAudioVolume)sc).GetMasterVolume(out v); return v;
            }
            return -1;
        }

        [MTAThread]
        public static int Main(string[] a)
        {
            uint player = uint.Parse(a[1]);
            int iterations = int.Parse(a[2]);
            StringBuilder sb = new StringBuilder();
            int exceptions = 0, multiEvents = 0, earlyReturns = 0;
            MethodInfo because = typeof(BoostEngine).GetMethod("StopBecause", BindingFlags.NonPublic | BindingFlags.Instance);
            for (int it = 0; it < iterations; it++)
            {
                BoostEngine e = new BoostEngine(new List<uint> { player }, 100, false, false);
                int events = 0;
                e.StoppedEvent += delegate { Interlocked.Increment(ref events); };
                e.Start();
                Thread.Sleep(250);
                ManualResetEvent go = new ManualResetEvent(false);
                List<Thread> ts = new List<Thread>();
                for (int k = 0; k < 8; k++)
                {
                    int kk = k;
                    Thread t = new Thread(delegate()
                    {
                        go.WaitOne();
                        try
                        {
                            if (kk % 2 == 0)
                            {
                                e.Stop(true);
                                // Stop() must not return before the sliders are back
                                if (PlayerVolume(player) < 0.1f) Interlocked.Increment(ref earlyReturns);
                            }
                            else because.Invoke(e, new object[] { "race " + kk });
                        }
                        catch (Exception ex)
                        {
                            Exception x = ex.InnerException ?? ex;
                            Interlocked.Increment(ref exceptions);
                            lock (sb) sb.AppendLine("    exception: " + x.GetType().Name + ": " + x.Message);
                        }
                    });
                    t.IsBackground = true;
                    t.Start();
                    ts.Add(t);
                }
                go.Set();
                foreach (Thread t in ts) t.Join(15000);
                if (events > 1) multiEvents++;
            }
            sb.Insert(0, string.Format("iterations={0} exceptions={1} doubleStoppedEvents={2} stopReturnedBeforeRestore={3}\r\n",
                iterations, exceptions, multiEvents, earlyReturns));
            File.WriteAllText(a[0], sb.ToString());
            return (exceptions == 0 && multiEvents == 0 && earlyReturns == 0) ? 0 : 1;
        }
    }
}
