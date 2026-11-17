using System;
using System.Collections.Generic;

namespace GigaPMC.Client
{
    // P31: stable GigaPMC-local IDs; these are NOT EFT buff IDs, skill IDs,
    // or arbitrary writes to another mod's/global EFT modifier collection.
    // Each hooked endpoint must choose exactly one ID and must pass a fresh
    // native EFT result to prevent GigaPMC-on-GigaPMC stacking.
    internal enum GlobalBonusId
    {
        CarryingWeight = 0,
        BodyStamina = 1,
        AdsEndurance = 2,
        MeleeDamage = 3,
        MovementSpeed = 4
    }

    internal static class AttributeGlobalBonus
    {
        private const string Root = "com.vargor.gigapmc.globalbonus.v1.";
        private static readonly string[] Names = {
            Root + "carrying-weight", Root + "body-stamina",
            Root + "ads-endurance", Root + "melee-damage",
            Root + "movement-speed"
        };
        internal static int Count => Names.Length;
        internal static string Identity(GlobalBonusId id)
        {
            int index = (int)id;
            if (index < 0 || index >= Names.Length) throw new ArgumentOutOfRangeException(nameof(id));
            return Names[index];
        }
        internal static string[] CopyIds() => (string[])Names.Clone();
        internal static GlobalBonusId MovementId(int nativeLegacyIndex)
        {
            // Compatibility with the previous seven-slot hook array, not
            // a new mod ID or a second movement configuration.
            if (nativeLegacyIndex < 2 || nativeLegacyIndex > 6)
                throw new ArgumentOutOfRangeException(nameof(nativeLegacyIndex));
            return GlobalBonusId.MovementSpeed;
        }
        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        // Applied to a FRESH EFT result after the original computation.
        // Other game's modifiers are already reflected in nativeResult.
        // This is multiplication of the complete result, NOT an addition of
        // a clean skill-derived baseline. No writes to EFT GlobalConfiguration.
        internal static bool TryMultiplyFinal(GlobalBonusId id,float nativeResult,
            decimal percent,float exclusiveCeiling,out float finalResult)
        {
            finalResult = nativeResult;
            if ((int)id < 0 || (int)id >= Count ||
                !Finite(nativeResult) || nativeResult < 0f ||
                !Finite(exclusiveCeiling) || exclusiveCeiling <= 0f ||
                nativeResult >= exclusiveCeiling || percent < 0m || percent > 100000m)
                return false;
            double number = (double)nativeResult * (1d + (double)percent / 100d);
            if (double.IsNaN(number) || double.IsInfinity(number) ||
                number < nativeResult || number >= exclusiveCeiling) return false;
            finalResult = (float)number;
            return Finite(finalResult) && finalResult >= nativeResult;
        }
        // Float ratio is captured ONCE from F12 at local PMC raid entry.
        // This avoids reconstructing any native strength/elite/endurance formula.
        internal static bool TryMultiplyFrozenRatio(GlobalBonusId id,float nativeResult,
            float ratio,float exclusiveCeiling,out float finalResult)
        {
            finalResult = nativeResult;
            if (!Finite(ratio) || ratio <= 0f || ratio > 1000f) return false;
            return TryMultiplyFinal(id,nativeResult,(decimal)ratio*100m,exclusiveCeiling,out finalResult);
        }
        // ADS Endurance is a cost reduction: use the final EFT drain after
        // native buffs/debuffs and divide THAT amount by (1 + GigaPMC %).
        internal static bool TryReduceFinalDrain(GlobalBonusId id,float before,float after,
            decimal percent,out float finalAfter)
        {
            finalAfter = after;
            if (id != GlobalBonusId.AdsEndurance || !Finite(before) || !Finite(after) ||
                before < 0f || after < 0f || before > 100000f || after > 100000f ||
                percent < 0m || percent > 100000m) return false;
            if (after >= before || percent == 0m) return true;
            double nativeDrain = (double)before - after;
            double reduced = nativeDrain / (1d + (double)percent / 100d);
            double restored = before - reduced;
            if (double.IsNaN(restored) || double.IsInfinity(restored) || restored < after || restored > before)
                return false;
            finalAfter = (float)restored;
            return Finite(finalAfter);
        }
    }
}
