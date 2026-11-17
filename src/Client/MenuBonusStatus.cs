using System;
using System.Globalization;

namespace GigaPMC.Client
{
    // A LIVE F12 calculation from the same menu PMC identity as MenuHealthSync.
    // This is deliberately NOT a native EFT stat write: ADS, movement and melee
    // do not have an authenticated live gameplay controller in the main menu.
    // No real EFT stat is claimed until an in-raid hook reports application.
    internal static class MenuBonusStatus
    {
        internal static readonly AttributeBonusKind[] Kinds = {
            AttributeBonusKind.CarryWeight,
            AttributeBonusKind.BodyStamina,
            AttributeBonusKind.AdsEndurance,
            AttributeBonusKind.MeleeDamage,
            AttributeBonusKind.SprintSpeed
        };
        internal static readonly string[] Labels = {
            "Carrying Weight", "Body Stamina", "ADS Endurance", "Melee Damage", "Movement Speed"
        };
        internal static string[] Build(bool enabled, int level, int prestige, int cap,
            Func<AttributeBonusKind, AttributeBonusRates> readRates)
        {
            if (readRates == null) throw new ArgumentNullException(nameof(readRates));
            if (level < 1 || prestige < 0 || cap < 1)
                throw new ArgumentOutOfRangeException(nameof(level),"Main-menu PMC profile/cap unavailable");
            string[] items = new string[5];
            for (int i = 0; i < 5; i++)
            {
                var rate = readRates(Kinds[i]);
                if (rate == null || !rate.IsValid())
                    throw new InvalidOperationException("Invalid F12 bonus rate: " + Labels[i]);
                if (!enabled || !rate.Enabled)
                {
                    items[i] = "OFF | F12 live | not applied in menu";
                    continue;
                }
                decimal percent = rate.TotalPercent(enabled,level,prestige,cap);
                items[i] = "+" + percent.ToString("0.##",CultureInfo.InvariantCulture)
                    + "% | F12 live | applies to final EFT result in raid";
            }
            return items;
        }
    }
}
