using System;

namespace GigaPMC.Client
{
    public static class AttributeCapPolicy
    {
        // 0 means "not required". Never guess 79 under AutoCap.
        public static int Resolve(RaidHpSettings settings,int? detected,params AttributeBonusRates[] rates)
        {
            if (settings == null || !settings.IsValid() || rates == null)
                throw new ArgumentException("Invalid GigaPMC settings");
            if (!settings.AutoCap) return settings.ResolveCap(null);
            if (detected.HasValue && detected.Value>=1 && detected.Value<=1000)
                return settings.ResolveCap(detected);
            foreach (var rate in rates)
                if (rate != null && rate.Enabled && rate.AtMaxLevelEnabled && rate.AtMaxLevelPercent > 0m)
                    throw new InvalidOperationException("Cannot calculate enabled At-Max bonus without server cap");
            return 0;
        }
    }
}
