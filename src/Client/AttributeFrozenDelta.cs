using System;

namespace GigaPMC.Client
{
    // Isolated, deterministic P09 arithmetic. NEVER modifies EFT skills or profile values.
    public static class AttributeFrozenDelta
    {
        public static float Freeze(float nativeAtRaidEntry, decimal percent)
        {
            if (float.IsNaN(nativeAtRaidEntry) || float.IsInfinity(nativeAtRaidEntry) ||
                nativeAtRaidEntry <= 0f || nativeAtRaidEntry > 1000000f ||
                percent < 0m || percent > 100000m)
                throw new ArgumentOutOfRangeException("Invalid native baseline or percent");
            double delta = (double)nativeAtRaidEntry * (double)percent / 100.0;
            if (double.IsNaN(delta) || double.IsInfinity(delta) || delta > 100000000d)
                throw new OverflowException("Unbounded frozen attribute delta");
            return (float)delta;
        }

        // Call on the unmodified output of EFT's original calculation, NEVER on
        // a previous GigaPMC result. No stacking on repeated recalculations.
        public static float Apply(float nativeCurrent, float frozenDelta)
        {
            if (float.IsNaN(nativeCurrent) || float.IsInfinity(nativeCurrent) ||
                float.IsNaN(frozenDelta) || float.IsInfinity(frozenDelta) ||
                nativeCurrent < 0f || frozenDelta < 0f)
                throw new ArgumentOutOfRangeException("Invalid native current or frozen delta");
            double sum = (double)nativeCurrent + frozenDelta;
            if (sum > 100000000d) throw new OverflowException("Unbounded output");
            return (float)sum;
        }
    }
}
