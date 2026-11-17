namespace GigaPMC.Server;

public sealed class VitalitySettings
{
    public bool Enabled { get; set; } = true;
    public int BonusHpPerLevel { get; set; } = 1;
    public int BonusHpEvery10Levels { get; set; } = 5;
    public int BonusHpAtMaxLevel { get; set; } = 12;
    public int BonusHpPerPrestige { get; set; } = 5;
    public int MaxPmcLevel { get; set; } = 79;

    public bool IsValid() =>
        BonusHpPerLevel is >= 0 and <= 100 &&
        BonusHpEvery10Levels is >= 0 and <= 1000 &&
        BonusHpAtMaxLevel is >= 0 and <= 1000 &&
        BonusHpPerPrestige is >= 0 and <= 1000 &&
        MaxPmcLevel is >= 1 and <= 1000;
}

public static class VitalityMath
{
    public static int Bonus(int level, int prestige, VitalitySettings settings)
    {
        if (!settings.IsValid() || level < 1 || prestige < 0)
            throw new System.ArgumentOutOfRangeException(nameof(level));
        checked
        {
            return (level - 1) * settings.BonusHpPerLevel
                + (level / 10) * settings.BonusHpEvery10Levels
                + (level >= settings.MaxPmcLevel ? settings.BonusHpAtMaxLevel : 0)
                + prestige * settings.BonusHpPerPrestige;
        }
    }
}
