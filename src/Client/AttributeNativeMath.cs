using System;

namespace GigaPMC.Client
{
    // Pure numerical contract for P27 runtime trials. The native result is
    // supplied fresh by EFT on each call (never our previous result).
    internal static class AttributeNativeMath
    {
        internal static bool TryAddPercent(float nativeValue, decimal percent, out float result)
        {
            result = nativeValue;
            if (float.IsNaN(nativeValue) || float.IsInfinity(nativeValue) || nativeValue < 0 || nativeValue > 100000 ||
                percent < 0 || percent > 10000) return false;
            double computed = (double)nativeValue * (1d + (double)percent / 100d);
            if (double.IsNaN(computed) || double.IsInfinity(computed) || computed > 100000d) return false;
            result = (float)computed;
            return true;
        }
        // Return the stamina drain that should remain after an ADS bonus.
        // A zero/positive native restoration is unaffected. This never alters
        // Health, max HP, skill progress, or the hands stamina capacity buff.
        internal static bool TryReduceDrain(float before, float after, decimal percent, out float result)
        {
            result = after;
            if (float.IsNaN(before) || float.IsNaN(after) || float.IsInfinity(before) || float.IsInfinity(after) ||
                before < 0 || after < 0 || before > 100000 || after > 100000 || percent < 0 || percent > 10000)
                return false;
            if (after >= before || percent == 0m) return true;
            double retained = (before - after) / (1d + (double)percent / 100d);
            double value = before - retained;
            if (value < after || value > before || double.IsNaN(value) || double.IsInfinity(value)) return false;
            result = (float)value;
            return true;
        }
    }
}
