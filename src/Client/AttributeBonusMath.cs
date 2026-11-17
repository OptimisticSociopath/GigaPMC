using System;
using System.Collections.Generic;

namespace GigaPMC.Client
{
    // P08 — pure math. This module does NOT modify EFT or player profiles.
    // The unchanged Health/HP code lives in other source files.
    public enum AttributeBonusKind
    {
        CarryWeight, BodyStamina, AdsEndurance, MeleeDamage,
        SprintSpeed, WalkSpeed, CovertSpeed, CrouchSpeed, ProneSpeed
    }

    public enum PercentPrestigeMode { Fixed, Accumulated }

    public sealed class AttributeBonusRates
    {
        public bool Enabled = true;
        public bool PerLevelEnabled = true;
        public bool EveryTenEnabled = true;
        public bool AtMaxLevelEnabled = true;
        public bool PrestigeEnabled = true;
        public decimal PerLevelPercent;
        public decimal EveryTenPercent;
        public decimal AtMaxLevelPercent;
        public PercentPrestigeMode PrestigeMode = PercentPrestigeMode.Fixed;
        public decimal[] PrestigeTiers = new decimal[6];

        public AttributeBonusRates Copy()
        {
            return new AttributeBonusRates {
                Enabled=Enabled, PerLevelEnabled=PerLevelEnabled,
                EveryTenEnabled=EveryTenEnabled, AtMaxLevelEnabled=AtMaxLevelEnabled,
                PrestigeEnabled=PrestigeEnabled, PerLevelPercent=PerLevelPercent,
                EveryTenPercent=EveryTenPercent, AtMaxLevelPercent=AtMaxLevelPercent,
                PrestigeMode=PrestigeMode, PrestigeTiers=(decimal[])PrestigeTiers.Clone()
            };
        }

        public bool IsValid()
        {
            if (PrestigeTiers == null || PrestigeTiers.Length != 6 ||
                !Enum.IsDefined(typeof(PercentPrestigeMode), PrestigeMode)) return false;
            if (!Range(PerLevelPercent, 0, 100) ||
                !Range(EveryTenPercent, 0, 100) ||
                !Range(AtMaxLevelPercent, 0, 100)) return false;
            foreach (decimal tier in PrestigeTiers)
                if (!Range(tier, 0, 100)) return false;
            return true;
        }

        private static bool Range(decimal value, decimal min, decimal max)
            => value >= min && value <= max;

        // FIXED: single configured tier. ACCUMULATED: sum of reached tiers.
        // Unknown prestige above P6 uses P6 (six-tier configuration ceiling).
        public decimal PrestigePercent(int prestige)
        {
            if (!IsValid() || prestige < 0 || prestige > 10000)
                throw new ArgumentOutOfRangeException(nameof(prestige));
            if (prestige == 0 || !Enabled || !PrestigeEnabled) return 0m;
            int tier = Math.Min(prestige, 6);
            if (PrestigeMode == PercentPrestigeMode.Fixed) return PrestigeTiers[tier - 1];
            decimal result = 0m;
            for (int i = 0; i < tier; i++) result += PrestigeTiers[i];
            return result;
        }

        public decimal TotalPercent(bool globalEnabled, int pmcLevel, int prestige, int detectedMaxLevel)
        {
            if (!IsValid() || pmcLevel < 1 || pmcLevel > 10000 || prestige < 0 ||
                prestige > 10000 || detectedMaxLevel < 1 || detectedMaxLevel > 1000)
                throw new ArgumentOutOfRangeException("PMC level, prestige, cap or rates invalid");
            if (!globalEnabled || !Enabled) return 0m;
            decimal result = 0m;
            if (PerLevelEnabled) result += (pmcLevel - 1) * PerLevelPercent;
            if (EveryTenEnabled) result += (pmcLevel / 10) * EveryTenPercent;
            if (AtMaxLevelEnabled && pmcLevel >= detectedMaxLevel) result += AtMaxLevelPercent;
            if (PrestigeEnabled) result += PrestigePercent(prestige);
            return result;
        }
    }

    public static class AttributeBonusMath
    {
        public static readonly AttributeBonusKind[] OrderedKinds = {
            AttributeBonusKind.CarryWeight, AttributeBonusKind.BodyStamina,
            AttributeBonusKind.AdsEndurance, AttributeBonusKind.MeleeDamage,
            AttributeBonusKind.SprintSpeed, AttributeBonusKind.WalkSpeed,
            AttributeBonusKind.CovertSpeed, AttributeBonusKind.CrouchSpeed,
            AttributeBonusKind.ProneSpeed
        };

        public static bool IsMovement(AttributeBonusKind kind)
            => kind >= AttributeBonusKind.SprintSpeed && kind <= AttributeBonusKind.ProneSpeed;

        public static AttributeBonusRates Defaults(AttributeBonusKind kind)
        {
            if (!Enum.IsDefined(typeof(AttributeBonusKind), kind))
                throw new ArgumentOutOfRangeException(nameof(kind));
            bool move = IsMovement(kind);
            return new AttributeBonusRates {
                Enabled=true, PerLevelEnabled=true, EveryTenEnabled=true,
                AtMaxLevelEnabled=true, PrestigeEnabled=true,
                PerLevelPercent=move ? .25m : 1m,
                EveryTenPercent=move ? 1m : 3m,
                AtMaxLevelPercent=move ? 5m : 10m,
                PrestigeMode=PercentPrestigeMode.Fixed,
                PrestigeTiers=move
                    ? new decimal[]{2m,4m,6m,8m,10m,12m}
                    : new decimal[]{5m,8m,11m,14m,17m,20m}
            };
        }

        // No native game stat is ever mutated by this pure function.
        public static decimal Delta(decimal unmodifiedPermanentStat, decimal totalPercent)
        {
            if (unmodifiedPermanentStat < 0 || totalPercent < 0 || totalPercent > 100000)
                throw new ArgumentOutOfRangeException("Nonnegative native permanent stat and percent required");
            return checked(unmodifiedPermanentStat * totalPercent / 100m);
        }

        public sealed class PreRaidBaseline
        {
            public decimal NativePermanentValue { get; private set; }
            public decimal GigaPmcPercent { get; private set; }
            public decimal GigaPmcDelta { get; private set; }
            internal PreRaidBaseline(decimal nativeValue, decimal percent)
            {
                NativePermanentValue = nativeValue;
                GigaPmcPercent = percent;
                GigaPmcDelta = Delta(nativeValue, percent);
            }
            public decimal StableBaseline => NativePermanentValue + GigaPmcDelta;
            // Only call with the native result NOT ALREADY MODIFIED by GigaPMC.
            // EFT integration hooks are intentionally not enabled in P08.
            public decimal CombineNativeUnmodified(decimal nativeCurrentValue)
                => nativeCurrentValue + GigaPmcDelta;
        }

        public static PreRaidBaseline Freeze(decimal nativePermanentValue, decimal percent)
            => new PreRaidBaseline(nativePermanentValue, percent);
    }
}
