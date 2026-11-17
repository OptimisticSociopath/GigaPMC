using System;

namespace GigaPMC.Client
{
    // P31: Capture only GigaPMC's rate, never a skill-derived kg delta.
    // Strength, Elite, stimulants and every EFT weight recalculation stay native.
    internal static class AttributeCarryMath
    {
        internal const decimal MaximumConfiguredPercent = 100000m;
        internal static bool TryFreeze(int permanentStrengthLevel, decimal percent,
            BaselineRange[] configRanges,out float frozenRatio,out string failure)
        {
            frozenRatio = 0f; failure = "INVALID_NATIVE_OR_PERCENT";
            // No upper skill-level limit: this level is NOT part of the formula.
            if (permanentStrengthLevel < 0 || percent <= 0m ||
                percent > MaximumConfiguredPercent || configRanges == null ||
                configRanges.Length != 4) return false;
            for (int i=0;i<4;i++)
            {
                BaselineRange v=configRanges[i];
                if (!Finite(v.Low) || !Finite(v.High) || v.Low <= 0f ||
                    v.High < v.Low || v.High >= 8000f)
                { failure = "INVALID_NATIVE_CONFIG_RANGE"; return false; }
            }
            frozenRatio = (float)(percent / 100m);
            if (!Finite(frozenRatio) || frozenRatio <= 0f || frozenRatio > 1000f)
                return false;
            failure = "NONE";
            return true;
        }
        internal static bool TryApply(BaselineRange[] native,float frozenRatio,
            bool encumbranceDisabled,out BaselineRange[] effective)
        {
            effective = null;
            if (encumbranceDisabled || native == null || native.Length != 4) return false;
            var values = new BaselineRange[4];
            for (int i=0;i<4;i++)
            {
                BaselineRange n=native[i];
                if (!Finite(n.Low) || !Finite(n.High) || n.Low <= 0f || n.High < n.Low)
                    return false;
                float lo, hi;
                if (!AttributeGlobalBonus.TryMultiplyFrozenRatio(GlobalBonusId.CarryingWeight,
                    n.Low,frozenRatio,8000f,out lo) ||
                    !AttributeGlobalBonus.TryMultiplyFrozenRatio(GlobalBonusId.CarryingWeight,
                    n.High,frozenRatio,8000f,out hi) || lo > hi)
                    return false;
                values[i] = new BaselineRange(lo,hi);
            }
            effective = values;
            return true;
        }
        private static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
    }
}
