using System;

namespace GigaPMC.Client
{
    // P16: pure independent projection from persisted skill Progress (P15),
    // pinned native formulas (P12 / P05), and supplied unmodified config values.
    // NO game/profile writes. NO hooks. DO NOT treat a projection as certified.
    internal enum BaselineProvenance
    {
        Unknown = 0,
        StaticIlProjectionOnly = 1,
        CertifiedLivePermanent = 2
    }

    internal readonly struct BaselineRange
    {
        internal readonly float Low;
        internal readonly float High;
        internal BaselineRange(float low, float high) { Low = low; High = high; }
    }

    internal sealed class AttributePermanentBaselines
    {
        internal BaselineProvenance Origin => BaselineProvenance.StaticIlProjectionOnly;
        internal float StaminaCapacity { get; }
        internal float StrengthSkillMultiplier { get; }
        internal float EnduranceSkillMultiplier { get; }
        private readonly BaselineRange[] weightLimits;
        internal BaselineRange GetWeightRange(int index)
        {
            if (index < 0 || index >= 4) throw new ArgumentOutOfRangeException(nameof(index));
            return weightLimits[index];
        }

        private AttributePermanentBaselines(float stamina, float strength,
            float endurance, BaselineRange[] weight)
        {
            StaminaCapacity = stamina;
            StrengthSkillMultiplier = strength;
            EnduranceSkillMultiplier = endurance;
            weightLimits = (BaselineRange[])weight.Clone();
        }

        // Inputs represent EFT *configuration constants*, NOT observed live
        // stamina, health controller modifiers, stimulant CapacityBuff or HUD.
        // Configured weight pairs: Walk, Base, Sprint, WalkSpeed (native order).
        // For skill level 51 (elite) P12 does not fully prove Elite override
        // ordering: FAIL CLOSED, do not silently substitute level 50.
        internal static bool TryProject(AttributePersistedSkills skills,
            float unmodifiedBaseStaminaCapacity, BaselineRange[] configuredWeightLimits,
            out AttributePermanentBaselines projected, out string failure)
        {
            projected = null;
            failure = "UNKNOWN";
            if (skills == null) { failure = "SKILLS_REQUIRED"; return false; }
            if (skills.StrengthLevel < 0 || skills.EnduranceLevel < 0 ||
                skills.StrengthLevel > 50 || skills.EnduranceLevel > 50)
            { failure = "ELITE_OR_LEVEL_UNCERTIFIED"; return false; }
            if (!FinitePositive(unmodifiedBaseStaminaCapacity) ||
                unmodifiedBaseStaminaCapacity > 100000f)
            { failure = "STAMINA_CONFIG_INVALID"; return false; }
            if (configuredWeightLimits == null || configuredWeightLimits.Length != 4)
            { failure = "WEIGHT_CONFIG_FOUR_PAIRS_REQUIRED"; return false; }
            for (int i = 0; i < 4; i++)
            {
                BaselineRange r = configuredWeightLimits[i];
                if (!FinitePositive(r.Low) || !FinitePositive(r.High) ||
                    r.High < r.Low || r.High > 100000f)
                { failure = "WEIGHT_CONFIG_RANGE_INVALID"; return false; }
            }

            // P12 IL: FloatBuff.Max(0.3f/0.5f) divides factor by 50;
            // update rule receives normal skill Level. Strength getter:
            // 1 + StrengthBuffLiftWeightInc. Endurance BaseStaminaFunction:
            // Max(1, input + input * EnduranceBuffEnduranceInc + CapacityBuff).
            // Exclude CapacityBuff (stimulants). Exclude health modifiers
            // (relative and absolute), which native EFT applies afterward.
            float strength = 1f + skills.StrengthLevel * (0.3f / 50f);
            float endurance = 1f + skills.EnduranceLevel * (0.5f / 50f);
            float stamina = Math.Max(1f, unmodifiedBaseStaminaCapacity * endurance);
            if (!FinitePositive(stamina) || !FinitePositive(strength))
            { failure = "PROJECTED_STAMINA_INVALID"; return false; }
            BaselineRange[] projectedWeight = new BaselineRange[4];
            for (int i = 0; i < 4; i++)
            {
                float lo = configuredWeightLimits[i].Low * strength;
                float hi = configuredWeightLimits[i].High * strength;
                if (!FinitePositive(lo) || !FinitePositive(hi) || hi < lo || hi > 1000000f)
                { failure = "PROJECTED_WEIGHT_INVALID"; return false; }
                projectedWeight[i] = new BaselineRange(lo,hi);
            }
            projected = new AttributePermanentBaselines(stamina,strength,endurance,projectedWeight);
            failure = "NONE";
            return true;
        }

        private static bool FinitePositive(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
    }
}
