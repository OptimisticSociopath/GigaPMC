using System;

namespace GigaPMC.Client
{
    // P25: the SAME attribute percent calculation as the in-raid P24 track,
    // but evaluated against the existing MAIN-MENU PMC profile. No game writes.
    // Two optional values are P16 IL-based estimates, NEVER measured stats.
    internal sealed class AttributeMenuProjection
    {
        internal int StrengthLevel { get; }
        internal int EnduranceLevel { get; }
        internal decimal[] Percents { get; }
        internal decimal? NativeCarryUpperModel { get; }
        internal decimal? CarryUpperWithBonusModel { get; }
        internal decimal? NativeStaminaCapacityModel { get; }
        internal decimal? StaminaCapacityWithBonusModel { get; }
        internal string ModelReason { get; }

        private AttributeMenuProjection(int str, int end, decimal[] percents,
            decimal? nativeCarry, decimal? totalCarry,
            decimal? nativeStamina, decimal? totalStamina, string reason)
        {
            StrengthLevel = str;
            EnduranceLevel = end;
            Percents = (decimal[])percents.Clone();
            NativeCarryUpperModel = nativeCarry;
            CarryUpperWithBonusModel = totalCarry;
            NativeStaminaCapacityModel = nativeStamina;
            StaminaCapacityWithBonusModel = totalStamina;
            ModelReason = reason;
        }

        // If native config is unavailable outside the raid, return all nine
        // verified percentage previews without inventing a numerical base.
        internal static bool TryCreate(bool globalEnabled, int level, int prestige, int cap,
            Func<AttributeBonusKind,AttributeBonusRates> rateProvider,
            AttributePersistedSkills trained, BaselineRange[] configuredWeight,
            float? configuredCapacity, out AttributeMenuProjection result,
            out string failure)
        {
            result = null;
            failure = "UNKNOWN";
            if (trained == null || rateProvider == null || level < 1 || level > 10000 ||
                prestige < 0 || prestige > 10000 || cap < 1 || cap > 1000)
            { failure = "PROFILE_OR_RATES_INVALID"; return false; }
            var percents = new decimal[AttributeBonusMath.OrderedKinds.Length];
            try
            {
                for (int i = 0; i < percents.Length; ++i)
                {
                    var rate = rateProvider(AttributeBonusMath.OrderedKinds[i]);
                    if (rate == null || !rate.IsValid())
                    { failure = "INVALID_F12_RATE_" + i; return false; }
                    percents[i] = rate.TotalPercent(globalEnabled,level,prestige,cap);
                }
            }
            catch (ArgumentException) { failure = "PERCENT_INPUT_INVALID"; return false; }
            catch (OverflowException) { failure = "PERCENT_OVERFLOW"; return false; }

            decimal? carryNative = null, carryWith = null;
            decimal? staminaNative = null, staminaWith = null;
            string source = "CONFIG_UNAVAILABLE_PERCENT_ONLY";
            if (configuredWeight != null && configuredCapacity.HasValue)
            {
                AttributePermanentBaselines projected;
                string reason;
                if (AttributePermanentBaselines.TryProject(trained, configuredCapacity.Value,
                    configuredWeight, out projected, out reason))
                {
                    try
                    {
                        decimal c = (decimal)projected.GetWeightRange(1).High;
                        decimal s = (decimal)projected.StaminaCapacity;
                        carryNative = c;
                        staminaNative = s;
                        carryWith = checked(c + AttributeBonusMath.Delta(c, percents[0]));
                        staminaWith = checked(s + AttributeBonusMath.Delta(s, percents[1]));
                        source = "STATIC_IL_PROJECTION_UNCERTIFIED";
                    }
                    catch (ArgumentOutOfRangeException) { source = "PREVIEW_PERCENT_OUT_OF_MODEL_RANGE"; }
                    catch (OverflowException) { source = "PROJECTION_OVERFLOW"; }
                }
                else source = reason;
            }
            result = new AttributeMenuProjection(trained.StrengthLevel, trained.EnduranceLevel,
                percents,carryNative,carryWith,staminaNative,staminaWith,source);
            failure = "NONE";
            return true;
        }
    }
}
