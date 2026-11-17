using System;

namespace GigaPMC.Client
{
    // P30: bounded pure calculations, callable from native Harmony trial hooks.
    internal static class AttributeGameplayGuards
    {
        internal static bool ConfirmedAds(bool? before, bool? after)
            => before == true && after == true;

        // Native ProcessHit IL: knifeBase * (1 + StrengthBuffMeleePowerInc).
        // Add our GigaPMC percentage to the FACTOR before the native mul.
        // Never multiply an already Strength-buffed damage result again.
        internal static bool TryAddMeleeFactor(float nativeStrengthFactor,
            decimal percent, out float combined)
        {
            combined = nativeStrengthFactor;
            if (float.IsNaN(nativeStrengthFactor) || float.IsInfinity(nativeStrengthFactor) ||
                nativeStrengthFactor < 0f || nativeStrengthFactor > 10000f ||
                percent < 0m || percent > 10000m) return false;
            double sum = (double)nativeStrengthFactor + (double)percent / 100d;
            if (double.IsNaN(sum) || double.IsInfinity(sum) || sum > 10000d) return false;
            combined = (float)sum;
            return true;
        }

        // Sprint/prone are native bool flags. Other modes are approximate
        // and only accepted within coherent physical controller ranges.
        // This does NOT certify mode classification in real gameplay.
        internal static bool TryClassifyMovement(bool sprint, bool prone,
            float pose, float relativeSpeed, out int percentageIndex)
        {
            percentageIndex = -1;
            if (sprint && prone) return false;
            if (sprint) { percentageIndex = 2; return true; }
            if (prone) { percentageIndex = 6; return true; }
            if (float.IsNaN(pose) || float.IsInfinity(pose) ||
                float.IsNaN(relativeSpeed) || float.IsInfinity(relativeSpeed) ||
                pose < 0f || pose > 1f || relativeSpeed <= 0f || relativeSpeed > 1f)
                return false;
            // The cutoff is a provisional heuristic. Only runtime profiling
            // can prove EFT covert/crouch/walk mapping across modded states.
            percentageIndex = relativeSpeed <= 0.30f ? 4 : pose < 0.65f ? 5 : 3;
            return true;
        }
    }
}
