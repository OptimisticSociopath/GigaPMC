using System;

namespace GigaPMC.Client
{
    // Observations are made only inside the verified, local native stamina
    // capacity getter. This never substitutes a percentage for an EFT reading.
    // Main-thread only: no locks, timers, file access or Unity object searches.
    internal static class MenuStaminaEvidence
    {
        private static bool valid;
        private static float native, result;
        internal static event Action Changed;
        internal static void Clear()
        {
            if (!valid) return;
            valid=false; native=result=0f;
            Changed?.Invoke();
        }
        internal static bool Record(float before,float after)
        {
            if (float.IsNaN(before) || float.IsInfinity(before) || before <= 0f ||
                float.IsNaN(after) || float.IsInfinity(after) || after <= 0f ||
                after < before || after >= 100000f) return false;
            if (valid && Math.Abs(native-before)<0.01f &&
                Math.Abs(result-after)<0.01f) return false;
            valid=true; native=before; result=after;
            Changed?.Invoke();
            return true;
        }
        internal static bool TryRead(out float before,out float after)
        {
            before=native; after=result;
            return valid;
        }
    }
}
