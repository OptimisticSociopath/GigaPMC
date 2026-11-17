using System;

namespace GigaPMC.Client
{
    // P31: Native EFT fully computes BaseStaminaFunction first (Elite included).
    // GigaPMC freezes only its independent rate and scales the final EFT result.
    internal static class AttributeStaminaMath
    {
        internal const decimal MaximumConfiguredPercent = 100000m;
        internal static bool TryFreeze(int trainedEndurance,decimal percent,
            float nativeConfigCapacity,out float frozenRatio,out string failure)
        {
            frozenRatio = 0f; failure = "INVALID_NATIVE_OR_PERCENT";
            // No level-51 exclusion and no Endurance contribution formula here.
            if (trainedEndurance < 0 || percent <= 0m ||
                percent > MaximumConfiguredPercent || float.IsNaN(nativeConfigCapacity) ||
                float.IsInfinity(nativeConfigCapacity) || nativeConfigCapacity <= 0f ||
                nativeConfigCapacity > 100000f) return false;
            frozenRatio = (float)(percent / 100m);
            if (float.IsNaN(frozenRatio) || float.IsInfinity(frozenRatio) ||
                frozenRatio <= 0f || frozenRatio > 1000f) return false;
            failure = "NONE";
            return true;
        }
        internal static bool TryApply(float nativeResult,float frozenRatio,out float effective)
            => AttributeGlobalBonus.TryMultiplyFrozenRatio(GlobalBonusId.BodyStamina,
                nativeResult,frozenRatio,8000f,out effective);
    }
}
