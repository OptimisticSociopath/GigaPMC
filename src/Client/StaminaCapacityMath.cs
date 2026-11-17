using System;

namespace GigaPMC.Client
{
    // P46: one-shot conservation of the existing stamina fill fraction when
    // EFT's native maximum capacity is refreshed after P26 is armed.
    // Does NOT change drain/regen/skills/global EFT configs.
    internal static class StaminaCapacityMath
    {
        internal static bool TryPreserveFraction(float current, float oldCapacity,
            float newCapacity, out float newCurrent)
        {
            newCurrent = current;
            if(float.IsNaN(current) || float.IsInfinity(current) ||
               float.IsNaN(oldCapacity) || float.IsInfinity(oldCapacity) ||
               float.IsNaN(newCapacity) || float.IsInfinity(newCapacity) ||
               oldCapacity <= 0f || oldCapacity >= 8000f ||
               newCapacity <= 0f || newCapacity >= 8000f ||
               current < 0f || current > oldCapacity * 1.001f)
                return false;
            double fraction = Math.Min(1d, (double)current / oldCapacity);
            double candidate = fraction * newCapacity;
            if(double.IsNaN(candidate) || double.IsInfinity(candidate) ||
               candidate < 0d || candidate > newCapacity) return false;
            newCurrent = (float)candidate;
            return !float.IsNaN(newCurrent) && !float.IsInfinity(newCurrent) &&
                   newCurrent >= 0f && newCurrent <= newCapacity;
        }

        internal static bool IsExpectedNativeMultiplier(float before,float after,float ratio)
        {
            if(float.IsNaN(before) || float.IsInfinity(before) ||
               float.IsNaN(after) || float.IsInfinity(after) ||
               float.IsNaN(ratio) || float.IsInfinity(ratio) ||
               before <= 0f || after <= before || after >= 8000f ||
               ratio <= 0f || ratio > 1000f) return false;
            double expected = (double)before * (1d + ratio);
            return Math.Abs(after - expected) <= Math.Max(0.025d, expected * 0.002d);
        }
    }
}
