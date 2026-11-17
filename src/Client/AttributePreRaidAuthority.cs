using System;
using System.Collections.Generic;

namespace GigaPMC.Client
{
    // P10: formal provenance gate. No profile/skill writes, no runtime observer.
    // A native postfix value is NOT evidence of pre-raid permanent base + skills.
    // The actual EFT certified-skill-only reader has NOT been implemented.
    internal enum BaselineOrigin
    {
        Unknown = 0,
        NativeLiveCalculation = 1,
        CertifiedPermanentBaseAndSkills = 2
    }

    internal enum BaselineMetric
    {
        BodyStamina = 0,
        WalkLower = 1, WalkUpper = 2,
        BaseLower = 3, BaseUpper = 4,
        SprintLower = 5, SprintUpper = 6,
        WalkSpeedLower = 7, WalkSpeedUpper = 8
    }

    internal sealed class AttributePreRaidAuthority
    {
        // Deliberately false until an independently verified, skill-only EFT
        // source exists. This MUST NOT be toggled to true just for testing.
        internal static bool CertifiedSkillReaderPresent => false;

        private object activePlayer;
        private readonly Dictionary<BaselineMetric, float> frozenDeltas = new Dictionary<BaselineMetric, float>();
        internal int FrozenCount => frozenDeltas.Count;
        internal bool HasActivePlayer => activePlayer != null;

        // Same player re-init is idempotent. A different raid object removes
        // ALL previous per-raid state, including prior frozen deltas.
        internal bool Begin(object player)
        {
            if (player == null) return false;
            if (ReferenceEquals(activePlayer, player)) return false;
            activePlayer = player;
            frozenDeltas.Clear();
            return true;
        }

        internal void End(object player)
        {
            if (player == null || !ReferenceEquals(activePlayer,player)) return;
            frozenDeltas.Clear();
            activePlayer = null;
        }

        // Only an actual *certified* permanent base+skills source may enter
        // here. The current P10 runtime has no such source and never calls
        // Authorize. Unit tests exercise the contract with synthetic inputs.
        internal bool Authorize(object player, BaselineMetric metric, float permanentBaseAndSkills,
            decimal percent, BaselineOrigin origin, bool temporaryEffectsExcluded)
        {
            if (player == null || !ReferenceEquals(activePlayer,player) ||
                !Enum.IsDefined(typeof(BaselineMetric),metric) ||
                origin != BaselineOrigin.CertifiedPermanentBaseAndSkills ||
                !temporaryEffectsExcluded || frozenDeltas.ContainsKey(metric)) return false;
            if (float.IsNaN(permanentBaseAndSkills) || float.IsInfinity(permanentBaseAndSkills) ||
                permanentBaseAndSkills <= 0f || permanentBaseAndSkills > 1000000f ||
                percent < 0m || percent > 100000m) return false;
            try
            {
                frozenDeltas.Add(metric,AttributeFrozenDelta.Freeze(permanentBaseAndSkills,percent));
                return true;
            }
            catch (ArgumentException) { return false; }
            catch (OverflowException) { return false; }
        }

        // Only original/unmodified EFT calculation output should be passed.
        // Native debuffs and other gameplay still occur in the original path.
        internal bool TryApply(object player,BaselineMetric metric,float nativeCurrent,out float modified)
        {
            modified = nativeCurrent;
            if (player == null || !ReferenceEquals(activePlayer,player) ||
                !frozenDeltas.TryGetValue(metric,out float delta)) return false;
            try
            {
                modified = AttributeFrozenDelta.Apply(nativeCurrent,delta);
                return true;
            }
            catch (ArgumentException) { return false; }
            catch (OverflowException) { return false; }
        }
    }
}
