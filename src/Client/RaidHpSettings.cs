using System;

namespace GigaPMC.Client
{
    // Pure client-side copy of the server formula, kept free of Unity and
    // Harmony for independent .NET 10 regression tests. Only local PMC use.
    public sealed class RaidHpSettings
    {
        public bool Enabled = true;
        public int Head = 1, Chest = 5, Stomach = 4;
        public int LeftArm = 2, RightArm = 2, LeftLeg = 3, RightLeg = 3;
        public int EveryTen = 5, AtMax = 15, Prestige = 5;
        public bool Multiplicative = false, AutoCap = true;
        public int ManualCap = 79;

        public bool IsValid() =>
            InRange(Head, 0, 100) && InRange(Chest, 0, 100) && InRange(Stomach, 0, 100) &&
            InRange(LeftArm, 0, 100) && InRange(RightArm, 0, 100) &&
            InRange(LeftLeg, 0, 100) && InRange(RightLeg, 0, 100) &&
            InRange(EveryTen, 0, 1000) && InRange(AtMax, 0, 1000) &&
            InRange(Prestige, 0, 1000) && InRange(ManualCap, 1, 1000);

        private static bool InRange(int x, int a, int b) => x >= a && x <= b;

        public int ResolveCap(int? serverDetectedCap)
        {
            if (!IsValid()) throw new ArgumentException("Invalid F12 settings");
            if (!AutoCap) return ManualCap;
            if (!serverDetectedCap.HasValue || !InRange(serverDetectedCap.Value, 1, 1000))
                throw new InvalidOperationException("SPT server level cap unavailable; no client fallback to 79");
            return serverDetectedCap.Value;
        }

        public long Bonus(int level, int prestigeLevel, int maxLevel, string part)
        {
            if (!IsValid() || level < 1 || level > 10000 || prestigeLevel < 0 ||
                prestigeLevel > 10000 || maxLevel < 1 || maxLevel > 1000)
                throw new ArgumentOutOfRangeException("PMC level/prestige/cap");
            int rate;
            switch (part)
            {
                case "Head": rate = Head; break;
                case "Chest": rate = Chest; break;
                case "Stomach": rate = Stomach; break;
                case "LeftArm": rate = LeftArm; break;
                case "RightArm": rate = RightArm; break;
                case "LeftLeg": rate = LeftLeg; break;
                case "RightLeg": rate = RightLeg; break;
                default: throw new ArgumentException("Unknown body part", nameof(part));
            }
            checked
            {
                long prest = Multiplicative
                    ? (long)Prestige * prestigeLevel * (prestigeLevel + 1) / 2
                    : (long)Prestige * prestigeLevel;
                return (long)(level - 1) * rate + (long)(level / 10) * EveryTen +
                    (level >= maxLevel ? AtMax : 0) + prest;
            }
        }
    }
}
