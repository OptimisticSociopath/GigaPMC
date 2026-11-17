using System;

namespace GigaPMC.Client
{
    // P17: weight-only permanent-skill *projection*, independent of the still
    // unresolved physical StaminaCapacity field. Not a certified raid baseline.
    internal sealed class AttributeWeightBaseline
    {
        private readonly BaselineRange[] limits;
        internal float StrengthMultiplier { get; }
        internal NativeWeightOrigin ConfigOrigin =>
            NativeWeightOrigin.PinnedGlobalConfigurationReadUncertified;

        private AttributeWeightBaseline(float multiplier, BaselineRange[] values)
        {
            StrengthMultiplier=multiplier;
            limits=(BaselineRange[])values.Clone();
        }
        internal BaselineRange GetRange(int index)
        {
            if (index<0 || index>=4) throw new ArgumentOutOfRangeException(nameof(index));
            return limits[index];
        }
        internal static bool TryProject(AttributePersistedSkills skills,
            AttributeNativeConfig nativeConfig, out AttributeWeightBaseline projected,
            out string failure)
        {
            projected=null;
            failure="UNKNOWN";
            if (skills==null || nativeConfig==null)
            { failure="PERSISTED_SKILL_AND_NATIVE_CONFIG_REQUIRED"; return false; }
            if (skills.StrengthLevel < 0 || skills.StrengthLevel > 50)
            { failure="STRENGTH_ELITE_OR_LEVEL_UNCERTIFIED"; return false; }
            if (nativeConfig.Origin!=NativeWeightOrigin.PinnedGlobalConfigurationReadUncertified)
            { failure="CONFIG_ORIGIN_INVALID"; return false; }
            float factor=1f + skills.StrengthLevel*(0.3f/50f);
            var result=new BaselineRange[4];
            for (int i=0;i<4;i++)
            {
                BaselineRange config=nativeConfig.GetRange(i);
                float low=config.Low*factor;
                float high=config.High*factor;
                if (float.IsNaN(low) || float.IsNaN(high) || float.IsInfinity(low) ||
                    float.IsInfinity(high) || low<=0f || high<low || high>1000000f)
                { failure="PROJECTED_WEIGHT_INVALID_"+i; return false; }
                result[i]=new BaselineRange(low,high);
            }
            projected=new AttributeWeightBaseline(factor,result);
            failure="NONE";
            return true;
        }
    }
}
