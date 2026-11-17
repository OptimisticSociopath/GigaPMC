using System;
using System.Globalization;

namespace GigaPMC.Client
{
    // Pure, no-Unity display model; suitable for offline .NET regression tests.
    // Distinguishes live/measured values from offline F12 predictions.
    internal static class AttributeDevVisionText
    {
        internal const int BodyFontSize = 10;
        internal const int MetricCount = 5;
        internal static readonly string[] Names = {
            "Carrying Weight", "Body Stamina", "ADS Endurance", "Melee Damage",
            "Movement Speed"
        };

        // A single compact line: real resulting value FIRST, then the metric.
        // No native value is certified in P19; the first field is deliberately
        // "--" instead of a fabricated post-bonus number. Never pad columns.
        private static string FormatValueFirst(int metric, string status)
        {
            return "--  " + Names[metric] + "  " + status;
        }

        internal static string[] Build(bool globalEnabled, bool hasPmcContext,
            int level, int prestige, int cap,
            Func<AttributeBonusKind, AttributeBonusRates> rates)
        {
            if (rates == null) throw new ArgumentNullException(nameof(rates));
            string[] lines = new string[MetricCount + 1];
            lines[0] = "GigaPMC | DEV VISION [PREVIEW / NOT APPLIED]";
            for (int i=0; i<MetricCount; i++)
            {
                AttributeBonusKind kind=(AttributeBonusKind)i;
                AttributeBonusRates r=rates(kind);
                if (r == null || !r.IsValid())
                { lines[i+1]=FormatValueFirst(i, "[INVALID CONFIG]"); continue; }
                if (!globalEnabled || !r.Enabled)
                { lines[i+1]=FormatValueFirst(i, "[OFF]"); continue; }
                if (!hasPmcContext)
                {
                    lines[i+1]=FormatValueFirst(i, "[LEVEL UNKNOWN / NO LIVE SOURCE]");
                    continue;
                }
                try
                {
                    decimal pct = r.TotalPercent(globalEnabled, level, prestige, cap);
                    lines[i+1]=FormatValueFirst(i, "[+"+
                        pct.ToString("0.##",CultureInfo.InvariantCulture)+"% PREVIEW]");
                }
                catch (ArgumentException)
                { lines[i+1]=FormatValueFirst(i, "[UNKNOWN INPUTS]"); }
            }
            return lines;
        }

        // P20: only genuine, bounded native FIELD reads replace the first two
        // "--" values. Never call this projection or claim GigaPMC has applied it.
        internal static string[] WithNativeFields(string[] preview,
            AttributeNativeReadOnly native)
        {
            if (preview == null || preview.Length != MetricCount + 1)
                throw new ArgumentException("Invalid preview", nameof(preview));
            string[] copy = (string[])preview.Clone();
            if (native == null) return copy;
            copy[0] = "GigaPMC | DEV VISION [NATIVE FIELDS / BONUS NOT APPLIED]";
            copy[1] = native.BaseOverweightUpper.ToString("0.##", CultureInfo.InvariantCulture) +
                "kg  Carrying Weight  [NATIVE OVERWEIGHT UPPER / NO BONUS]";
            copy[2] = native.StaminaCapacity.ToString("0.##", CultureInfo.InvariantCulture) +
                " base  Body Stamina  [NATIVE CAPACITY FIELD / NO BONUS]";
            return copy;
        }

        // P25: PRE-RAID model, never a real applied stat. Same five F12
        // percentages as P24, plus up to TWO marked P16 IL projections.
        internal static string[] WithMenuProjection(string[] preview,
            AttributeMenuProjection model)
        {
            if (preview == null || preview.Length != MetricCount + 1)
                throw new ArgumentException("Invalid preview", nameof(preview));
            string[] copy = (string[])preview.Clone();
            if (model == null) return copy;
            copy[0] = "GigaPMC | PRE-RAID STR=" + model.StrengthLevel +
                " END=" + model.EnduranceLevel + " [ESTIMATE / NOT APPLIED]";
            if (model.CarryUpperWithBonusModel.HasValue &&
                !copy[1].Contains("[OFF]", StringComparison.Ordinal))
                copy[1] = model.CarryUpperWithBonusModel.Value.ToString("0.##",CultureInfo.InvariantCulture) +
                    "kg  Carrying Weight  [+" + model.Percents[0].ToString("0.##",CultureInfo.InvariantCulture) +
                    "% MODEL / NOT APPLIED]";
            if (model.StaminaCapacityWithBonusModel.HasValue &&
                !copy[2].Contains("[OFF]", StringComparison.Ordinal))
                copy[2] = model.StaminaCapacityWithBonusModel.Value.ToString("0.##",CultureInfo.InvariantCulture) +
                    " base  Body Stamina  [+" + model.Percents[1].ToString("0.##",CultureInfo.InvariantCulture) +
                    "% MODEL / NOT APPLIED]";
            return copy;
        }

        internal static bool ContainsClaimOfAppliedLiveStats(string[] lines)
        {
            if (lines == null) return true;
            foreach (string s in lines)
                if (s == null || s.Contains("[LIVE]", StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
