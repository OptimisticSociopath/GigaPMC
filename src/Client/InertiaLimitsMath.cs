using System;

namespace GigaPMC.Client
{
    // P49: EFT's PhysicalBase.BaseInertiaLimits (Vector3) is a DIFFERENT
    // kg-threshold family than the four Vector2 carrying-weight limits.
    // PhysicalBase.OnWeightUpdated computes PhysicalBase.Inertia from this
    // vector and the REAL equipment weight via native CalculateValue.
    // Only the two kg boundaries change; the third native coefficient stays.
    // Do not replace the weight, Strength skill, animation or velocity.
    internal static class InertiaLimitsMath
    {
        internal static bool TryScale(float nativeLower,float nativeUpper,float nativeThird,
            float frozenCarryRatio,out float adjustedLower,out float adjustedUpper,out float adjustedThird)
        {
            adjustedLower=nativeLower;
            adjustedUpper=nativeUpper;
            adjustedThird=nativeThird;
            if(!Finite(nativeLower) || !Finite(nativeUpper) || !Finite(nativeThird) ||
                !Finite(frozenCarryRatio) || frozenCarryRatio<=0f || frozenCarryRatio>1000f ||
                nativeLower<0f || nativeUpper<=nativeLower || nativeUpper>=8000f)
                return false;
            float scale=1f+frozenCarryRatio;
            double lo=(double)nativeLower*scale;
            double hi=(double)nativeUpper*scale;
            if(lo<0.0 || hi<=lo || hi>=8000.0 ||
                double.IsNaN(lo) || double.IsInfinity(lo) ||
                double.IsNaN(hi) || double.IsInfinity(hi))
                return false;
            adjustedLower=(float)lo;
            adjustedUpper=(float)hi;
            // Preserve the original third component: its ABI semantics are
            // not proven to be a weight threshold. Do not scale it blindly.
            return true;
        }
        private static bool Finite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
