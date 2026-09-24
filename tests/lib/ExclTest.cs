using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;

namespace AppVolumeBoosterNs
{
    // Calls BoostEngine.ClaimExclusivity() on an all-audio engine that is NEVER started,
    // so no all-audio capture or ducking ever happens. Prints OK or the refusal message.
    internal static class ExclTest
    {
        [MTAThread]
        public static int Main(string[] a)
        {
            BoostEngine e = new BoostEngine(null, 100, true, false);
            MethodInfo claim = typeof(BoostEngine).GetMethod("ClaimExclusivity", BindingFlags.NonPublic | BindingFlags.Instance);
            string r;
            try
            {
                claim.Invoke(e, null);
                r = "OK";
                // release what a successful claim took, so nothing lingers
                FieldInfo f = typeof(BoostEngine).GetField("allAudioMutex", BindingFlags.NonPublic | BindingFlags.Instance);
                Mutex m = (Mutex)f.GetValue(e);
                if (m != null) m.Close();
            }
            catch (TargetInvocationException ex) { r = "REFUSED: " + ex.InnerException.Message; }
            File.WriteAllText(a[0], r);
            return 0;
        }
    }
}
