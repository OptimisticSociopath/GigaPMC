using System;

namespace GigaPMC.Client
{
    // P18 source-model only: this object is NOT an authority for live gameplay.
    // Tracks 1 body stamina and 8 weight thresholds, frozen from P15/P17/P18
    // projections. No EFT or Unity API calls; no Harmony hooks; no profile writes.
    internal sealed class AttributeBaselineCandidate
    {
        private readonly float[] values; // Body, Walk L/U, Base L/U, Sprint L/U, Speed L/U.
        private readonly float nativeCapacity;
        private readonly BaselineRange[] nativeRanges;
        internal int StrengthLevel { get; }
        internal int EnduranceLevel { get; }
        internal BaselineProvenance Origin => BaselineProvenance.StaticIlProjectionOnly;
        internal float GetValue(int index)
        {
            if (index < 0 || index >= 9) throw new ArgumentOutOfRangeException(nameof(index));
            return values[index];
        }
        internal float[] CopyValues() => (float[])values.Clone();
        private AttributeBaselineCandidate(int strength, int endurance,
            float capacity, BaselineRange[] ranges, float[] source)
        {
            StrengthLevel = strength;
            EnduranceLevel = endurance;
            nativeCapacity = capacity;
            nativeRanges = (BaselineRange[])ranges.Clone();
            values = (float[])source.Clone();
        }
        internal static bool TryProject(AttributePersistedSkills persisted,
            AttributeNativeConfig weightConfig, AttributeStaminaConfig staminaConfig,
            out AttributeBaselineCandidate candidate, out string failure)
        {
            candidate = null;
            failure = "BASELINE_INPUTS_MISSING";
            if (persisted == null || weightConfig == null || staminaConfig == null)
                return false;
            if (weightConfig.Origin != NativeWeightOrigin.PinnedGlobalConfigurationReadUncertified ||
                staminaConfig.Origin != CapacityOrigin.PinnedGlobalConfigurationReadUncertified)
            { failure = "ORIGIN_UNEXPECTED"; return false; }
            if (!AttributePermanentBaselines.TryProject(persisted, staminaConfig.Capacity,
                weightConfig.CopyRanges(), out AttributePermanentBaselines p16, out failure))
                return false;
            var data = new float[9];
            data[0] = p16.StaminaCapacity;
            for (int i = 0; i < 4; i++)
            {
                BaselineRange pair = p16.GetWeightRange(i);
                data[1 + i * 2] = pair.Low;
                data[2 + i * 2] = pair.High;
            }
            candidate = new AttributeBaselineCandidate(persisted.StrengthLevel,
                persisted.EnduranceLevel, staminaConfig.Capacity, weightConfig.CopyRanges(), data);
            failure = "NONE";
            return true;
        }
        internal bool SameSourceConfigAs(AttributeBaselineCandidate other)
        {
            if (other == null || nativeCapacity != other.nativeCapacity) return false;
            for (int i = 0; i < 4; i++)
                if (nativeRanges[i].Low != other.nativeRanges[i].Low ||
                    nativeRanges[i].High != other.nativeRanges[i].High) return false;
            return true;
        }
        internal bool SameAs(AttributeBaselineCandidate other)
        {
            if (other == null || StrengthLevel != other.StrengthLevel ||
                EnduranceLevel != other.EnduranceLevel) return false;
            for (int i = 0; i < 9; i++) if (values[i] != other.values[i]) return false;
            return true;
        }
    }

    // One synthetic, explicitly local PMC / scene / profile / raid identity.
    // Three identical samples gate the candidate. Changing data within a raid
    // TAINTS and blocks the snapshot, rather than silently replacing it.
    // Frozen deltas are NEVER applied: certification remains unavailable.
    internal sealed class AttributeRaidLifecycle
    {
        private object owner;
        private object scene;
        private string profileId;
        private string raidId;
        private AttributeBaselineCandidate baseline;
        private float[] frozen;
        private int samples;
        private bool tainted;

        internal int StableSamples => samples;
        internal bool Ready => samples >= 3 && !tainted;
        internal bool HasModelDelta => frozen != null;
        internal bool IsTainted => tainted;
        internal bool IsGameplayCertified => false;

        internal bool Observe(object localPlayer, object raidScene, string profile,
            string raid, bool explicitlyLocalPmc, bool initialized,
            AttributeBaselineCandidate candidate)
        {
            if (localPlayer == null || raidScene == null ||
                string.IsNullOrWhiteSpace(profile) || string.IsNullOrWhiteSpace(raid) ||
                !explicitlyLocalPmc || !initialized || candidate == null ||
                candidate.Origin != BaselineProvenance.StaticIlProjectionOnly)
            { Reset(); return false; }
            if (!ReferenceEquals(localPlayer, owner) || !ReferenceEquals(raidScene, scene) ||
                !string.Equals(profile, profileId, StringComparison.Ordinal) ||
                !string.Equals(raid, raidId, StringComparison.Ordinal))
            {
                Reset();
                owner = localPlayer;
                scene = raidScene;
                profileId = profile;
                raidId = raid;
                baseline = candidate;
                samples = 1;
                return false;
            }
            if (tainted) return false;
            // A changed configuration is a real provenance drift: fail closed.
            if (!baseline.SameSourceConfigAs(candidate))
            {
                tainted = true; frozen = null; samples = 0;
                return false;
            }
            if (!baseline.SameAs(candidate))
            {
                // EFT must still progress skills normally during the raid.
                // After a successful freeze, KEEP the old model contribution:
                // do not retarget it to a new durable skill level mid-raid.
                if (frozen != null) return true;
                tainted = true; frozen = null; samples = 0;
                return false;
            }
            if (samples < 3) samples++;
            return Ready;
        }

        internal bool TryFreezeModel(object localPlayer, string raid,
            AttributeBonusRates weightRates, AttributeBonusRates staminaRates,
            int pmcLevel, int prestige, int detectedMaxLevel, out string failure)
        {
            failure = "NOT_READY_OR_ALREADY_FROZEN";
            if (!Ready || frozen != null || !ReferenceEquals(owner, localPlayer) ||
                !string.Equals(raid, raidId, StringComparison.Ordinal) ||
                baseline == null || weightRates == null || staminaRates == null ||
                !weightRates.IsValid() || !staminaRates.IsValid()) return false;
            try
            {
                decimal weightPercent = weightRates.TotalPercent(true, pmcLevel, prestige, detectedMaxLevel);
                decimal staminaPercent = staminaRates.TotalPercent(true, pmcLevel, prestige, detectedMaxLevel);
                var result = new float[9];
                for (int i = 0; i < 9; i++)
                    result[i] = AttributeFrozenDelta.Freeze(baseline.GetValue(i),
                        i == 0 ? staminaPercent : weightPercent);
                frozen = result;
                failure = "NONE_STATIC_MODEL_NOT_CERTIFIED";
                return true;
            }
            catch (ArgumentException) { failure = "INVALID_PERCENT_OR_METRIC"; return false; }
            catch (OverflowException) { failure = "FREEZE_OVERFLOW"; return false; }
        }

        // Deliberately a COPY of a projection, NOT an Apply() method.
        internal float[] CopyModelDeltas()
            => frozen == null ? Array.Empty<float>() : (float[])frozen.Clone();

        internal bool End(object localPlayer, string raid)
        {
            if (!ReferenceEquals(localPlayer, owner) ||
                !string.Equals(raid, raidId, StringComparison.Ordinal)) return false;
            Reset();
            return true;
        }
        internal void Reset()
        {
            owner = null; scene = null; profileId = null; raidId = null;
            baseline = null; frozen = null; samples = 0; tainted = false;
        }
    }
}
