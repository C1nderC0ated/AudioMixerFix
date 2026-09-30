using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AppVolumeBoosterNs
{
    // Test helper: a process that opens a SILENT shared render stream, closes it, and opens
    // it again - what a browser does around a pause, and a game when it re-opens its audio.
    // Nothing is ever written to the stream, so nothing is heard, and the stream is not
    // persisted (no entry is left in the per-app volume store).
    //   sessionmaker.exe <report> <hold1 ms> <gap ms> <hold2 ms>
    // The report gets one line every 200 ms: "<ms> round=<1|2> vol=<own session volume>".
    internal static class SessionMaker
    {
        const uint NoPersist = 0x00080000;   // AUDCLNT_STREAMFLAGS_NOPERSIST

        [MTAThread]
        public static int Main(string[] a)
        {
            string report = a[0];
            int hold1 = int.Parse(a[1]), gap = int.Parse(a[2]), hold2 = int.Parse(a[3]);
            StringBuilder sb = new StringBuilder();
            Stopwatch sw = Stopwatch.StartNew();
            for (int round = 1; round <= 2; round++)
            {
                IAudioClient client = Native.ActivateClient(Native.DefaultRenderDevice());
                WaveFormatEx fmt = WaveFormatEx.Float32Stereo48k();
                Native.Check(client.Initialize(K.SHARED, K.AUTOCONVERT | K.SRC_QUALITY | NoPersist, 2000000, 0, ref fmt, IntPtr.Zero), "Initialize");
                Guid iid = Native.IID_ISimpleAudioVolume; object ov;
                Native.Check(client.GetService(ref iid, out ov), "GetService");
                ISimpleAudioVolume vol = (ISimpleAudioVolume)ov;
                Native.Check(client.Start(), "Start");
                long until = sw.ElapsedMilliseconds + (round == 1 ? hold1 : hold2);
                while (sw.ElapsedMilliseconds < until)
                {
                    float v; vol.GetMasterVolume(out v);
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0} round={1} vol={2:F3}", sw.ElapsedMilliseconds, round, v));
                    File.WriteAllText(report, sb.ToString());
                    Thread.Sleep(200);
                }
                client.Stop();
                // every reference gone: the session expires and leaves the session list
                Marshal.FinalReleaseComObject(vol);
                Marshal.FinalReleaseComObject(client);
                GC.Collect(); GC.WaitForPendingFinalizers();
                if (round == 1) Thread.Sleep(gap);
            }
            return 0;
        }
    }
}
