using System;

namespace GigaPMC.Server;

public sealed class VitalitySettings
{
    public bool Enabled { get; set; } = true;
    public int BonusHpEvery10Levels { get; set; } = 5;
    public int BonusHpAtMaxLevel { get; set; } = 15;
    public int BonusHpPerPrestige { get; set; } = 5;
    public int MaxPmcLevel { get; set; } = 79;

    public int ConfigSchemaVersion { get; set; } = 200;
    public int HeadHpPerLevel { get; set; } = 1;
    public int ChestHpPerLevel { get; set; } = 5;
    public int StomachHpPerLevel { get; set; } = 4;
    public int LeftArmHpPerLevel { get; set; } = 2;
    public int RightArmHpPerLevel { get; set; } = 2;
    public int LeftLegHpPerLevel { get; set; } = 3;
    public int RightLegHpPerLevel { get; set; } = 3;
    public bool PrestigeMultiplicative { get; set; } = false;
    public bool AutoDetectMaxPmcLevel { get; set; } = true;

    public bool IsValid() =>
        BonusHpEvery10Levels is >= 0 and <= 1000 &&
        BonusHpAtMaxLevel is >= 0 and <= 1000 &&
        BonusHpPerPrestige is >= 0 and <= 1000 &&
        MaxPmcLevel is >= 1 and <= 1000 &&
        ConfigSchemaVersion is 0 or 110 or 200 &&
        HeadHpPerLevel is >= 0 and <= 100 &&
        ChestHpPerLevel is >= 0 and <= 100 &&
        StomachHpPerLevel is >= 0 and <= 100 &&
        LeftArmHpPerLevel is >= 0 and <= 100 &&
        RightArmHpPerLevel is >= 0 and <= 100 &&
        LeftLegHpPerLevel is >= 0 and <= 100 &&
        RightLegHpPerLevel is >= 0 and <= 100;
}

public static class VitalityMath
{
    // This is the same maximum-level concept SPT's ProfileHelper.GetMaxLevel()
    // derives from globalTable.Configuration.Exp.Level.ExperienceTable.Length - 1.
    public static int ResolveMaxLevel(VitalitySettings settings, int? detectedGameMaxLevel)
    {
        if (!settings.IsValid()) throw new ArgumentException("Invalid settings", nameof(settings));
        if (!settings.AutoDetectMaxPmcLevel) return settings.MaxPmcLevel;
        if (!detectedGameMaxLevel.HasValue || detectedGameMaxLevel.Value < 1 || detectedGameMaxLevel.Value > 1000)
            throw new ArgumentOutOfRangeException(nameof(detectedGameMaxLevel),
                "Game max PMC level unavailable or out of supported range. No guessed max-level bonus.");
        return detectedGameMaxLevel.Value;
    }

    public static long PrestigeBonus(int prestigeLevel, VitalitySettings settings)
    {
        if (!settings.IsValid() || prestigeLevel < 0 || prestigeLevel > 10000)
            throw new ArgumentOutOfRangeException(nameof(prestigeLevel));
        checked
        {
            return settings.PrestigeMultiplicative
                ? (long)settings.BonusHpPerPrestige * prestigeLevel * (prestigeLevel + 1) / 2
                : (long)settings.BonusHpPerPrestige * prestigeLevel;
        }
    }

    public static long BonusForPart(int level, int prestigeLevel, VitalitySettings settings, int maxLevel, string bodyPart)
    {
        if (!settings.IsValid() || level < 1 || level > 10000 || prestigeLevel < 0 ||
            prestigeLevel > 10000 || maxLevel < 1 || maxLevel > 1000)
            throw new ArgumentOutOfRangeException(nameof(level));

        int partPerLevel = bodyPart switch
        {
            "Head" => settings.HeadHpPerLevel,
            "Chest" => settings.ChestHpPerLevel,
            "Stomach" => settings.StomachHpPerLevel,
            "LeftArm" => settings.LeftArmHpPerLevel,
            "RightArm" => settings.RightArmHpPerLevel,
            "LeftLeg" => settings.LeftLegHpPerLevel,
            "RightLeg" => settings.RightLegHpPerLevel,
            _ => throw new ArgumentException("Unknown body part", nameof(bodyPart))
        };

        checked
        {
            return (long)(level - 1) * partPerLevel
                + (long)(level / 10) * settings.BonusHpEvery10Levels
                + (level >= maxLevel ? settings.BonusHpAtMaxLevel : 0)
                + PrestigeBonus(prestigeLevel, settings);
        }
    }
}
