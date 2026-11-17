using System;
using System.Globalization;

namespace GigaPMC.Client
{
    // P21: ephemeral READ-ONLY identity and native-source sampling. This is
    // *not* an input for gameplay hooks and never certifies a pre-raid baseline.
    // No profile ID is stored, serialized, logged, or transmitted.
    internal sealed class AttributeReadOnlySession
    {
        private object player;
        private object profile;
        private float capacity;
        private float upper;
        private float strengthProgress;
        private float enduranceProgress;
        private int samples;
        internal int Samples => samples;
        internal bool Stable => samples >= 3;
        internal bool IsGameplayCertified => false;

        internal void Reset()
        {
            player = null; profile = null;
            capacity = upper = strengthProgress = enduranceProgress = 0f;
            samples = 0;
        }

        internal string Observe(object localPlayer, object localProfile,
            AttributeNativeReadOnly native, AttributePersistedSkills skills)
        {
            if (localPlayer == null || localProfile == null || native == null || skills == null)
            { Reset(); return "NO_CONFIRMED_LOCAL_SOURCE"; }
            bool sameOwner = ReferenceEquals(player, localPlayer) &&
                ReferenceEquals(profile, localProfile);
            bool sameValues = sameOwner && capacity == native.StaminaCapacity &&
                upper == native.BaseOverweightUpper &&
                strengthProgress == skills.StrengthProgress &&
                enduranceProgress == skills.EnduranceProgress;
            if (!sameValues)
            {
                // A changed character, profile, trained XP or native value
                // always invalidates the previous sequence. No old sample may
                // be reused after a menu/raid transition.
                player = localPlayer;
                profile = localProfile;
                capacity = native.StaminaCapacity;
                upper = native.BaseOverweightUpper;
                strengthProgress = skills.StrengthProgress;
                enduranceProgress = skills.EnduranceProgress;
                samples = 1;
                return "SAMPLE_1_OF_3";
            }
            if (samples < 3) samples++;
            return samples == 3 ? "SAMPLE_3_OF_3_UNCERTIFIED" : "SAMPLE_2_OF_3";
        }

        // Never replaces the P20 value/label ordering. Only the header shows
        // the descriptor skill levels and the number of agreeing observations.
        internal static string[] Annotate(string[] nativeLines,
            AttributePersistedSkills skills, string sourceStatus)
        {
            if (nativeLines == null || nativeLines.Length != 6)
                throw new ArgumentException("Expected DEV VISION header + five bonuses",nameof(nativeLines));
            var result = (string[])nativeLines.Clone();
            if (skills == null)
            {
                result[0] += " | P21 SKILLS UNAVAILABLE [READ ONLY]";
            }
            else
            {
                string sample = sourceStatus == "SAMPLE_3_OF_3_UNCERTIFIED" ? "3/3" :
                    sourceStatus == "SAMPLE_2_OF_3" ? "2/3" : "1/3";
                result[0] += " | STR " + skills.StrengthLevel.ToString(CultureInfo.InvariantCulture) +
                    " END " + skills.EnduranceLevel.ToString(CultureInfo.InvariantCulture) +
                    " | P21 " + sample + " [UNCERTIFIED]";
            }
            return result;
        }
    }
}
