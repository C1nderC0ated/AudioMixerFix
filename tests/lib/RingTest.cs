using System;
using System.IO;

namespace AppVolumeBoosterNs
{
    internal static class RingTest
    {
        public static int Main(string[] a)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            bool ok = true;
            SampleRing r = new SampleRing();
            float[] data = new float[10000];
            for (int i = 0; i < data.Length; i++) data[i] = i;
            r.Push(data, data.Length);

            int d0 = r.TrimTo(20000);
            sb.AppendLine("under the limit: dropped " + d0 + " (want 0)"); ok &= d0 == 0;

            int d1 = r.TrimTo(3841);                   // odd limit -> rounded down to 3840
            sb.AppendLine("trim to 3841: dropped " + d1 + " (want 6160)"); ok &= d1 == 6160;

            float[] outb = new float[20000];
            int got = r.Pop(outb, outb.Length);
            sb.AppendLine("left " + got + " samples, first=" + outb[0] + " last=" + outb[got - 1] + " (want 3840, 6160, 9999: the NEWEST kept)");
            ok &= got == 3840 && outb[0] == 6160f && outb[got - 1] == 9999f;

            sb.AppendLine(ok ? "RINGTEST PASS" : "RINGTEST FAIL");
            File.WriteAllText(a[0], sb.ToString());
            return ok ? 0 : 1;
        }
    }
}
