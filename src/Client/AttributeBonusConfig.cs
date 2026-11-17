using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;

namespace GigaPMC.Client
{
    // P32: exactly FIVE independent percent tracks in F12. Movement Speed
    // owns ONE set of values used for ALL native movement computations.
    // Old per-mode configs are read for one-time migration, never bound as UI.
    internal sealed class AttributeBonusConfig
    {
        private sealed class Track
        {
            internal ConfigEntry<bool> Enabled, PerLevelEnabled, EveryTenEnabled, AtMaxEnabled, PrestigeEnabled;
            internal ConfigEntry<bool> PrestigeAccumulated;
            internal ConfigEntry<float> PerLevel, EveryTen, AtMax;
            internal ConfigEntry<float>[] Prestige = new ConfigEntry<float>[6];

            private static string OldSection(AttributeBonusKind kind)
            {
                switch (kind)
                {
                    case AttributeBonusKind.SprintSpeed: return "10 Bonus - Sprint Speed";
                    case AttributeBonusKind.WalkSpeed: return "11 Bonus - Walk Speed";
                    case AttributeBonusKind.CovertSpeed: return "12 Bonus - Covert Speed";
                    case AttributeBonusKind.CrouchSpeed: return "13 Bonus - Crouch Speed";
                    case AttributeBonusKind.ProneSpeed: return "14 Bonus - Prone Speed";
                    default: return null;
                }
            }
            internal static string Key(AttributeBonusKind kind, int number, string name)
                => number.ToString("D2", CultureInfo.InvariantCulture) + " " + name;
            private static ConfigEntry<T> Migrate<T>(ConfigEntry<T> entry, LegacySettingsReader legacy,
                AttributeBonusKind kind, string newSection, string newKey, string oldKey,
                Func<string,T?> parse) where T : struct
            {
                if (legacy == null || legacy.ReadCfg(newSection,newKey) != null) return entry;
                string oldSection = OldSection(kind);
                string raw = null;
                if (kind == AttributeBonusKind.SprintSpeed)
                {
                    // P31's 76-entry Movement section used these exact Sprint keys.
                    // Migrate Sprint (first mode) as the single global Movement rate.
                    raw = legacy.ReadCfg(newSection,
                        (15 - 15 + int.Parse(oldKey.Substring(0,2),CultureInfo.InvariantCulture))
                            .ToString("D2",CultureInfo.InvariantCulture) + " Sprint - " + oldKey.Substring(3));
                }
                if (raw == null && oldSection != null) raw = legacy.ReadCfg(oldSection,oldKey);
                if (raw == null) return entry;
                T? value = parse(raw);
                if (value.HasValue) entry.Value = value.Value;
                return entry;
            }
            private static bool? ParseBool(string raw)
            {
                bool parsed; return bool.TryParse(raw,out parsed) ? (bool?)parsed : null;
            }
            private static float? ParseFloat(string raw)
            {
                float parsed;
                return float.TryParse(raw,NumberStyles.Float,CultureInfo.InvariantCulture,out parsed) &&
                    !float.IsNaN(parsed) && !float.IsInfinity(parsed) && parsed >= 0 && parsed <= 100
                    ? (float?)parsed : null;
            }
            private static ConfigEntry<bool> Flag(ConfigFile config,LegacySettingsReader legacy,
                AttributeBonusKind kind,string section,int idx,string name,bool value)
            {
                string key=Key(kind,idx,name);
                var entry=config.Bind(section,key,value,
                    "GigaPMC independent percent bonus. This setting does not replace temporary EFT effects.");
                return Migrate(entry,legacy,kind,section,key,idx.ToString("D2")+" "+name,ParseBool);
            }
            private static ConfigEntry<float> Percent(ConfigFile config,LegacySettingsReader legacy,
                AttributeBonusKind kind,string section,int idx,string name,float value,float max)
            {
                string key=Key(kind,idx,name);
                // Runtime ConfigurationManager uses our quarter-percent drawer.
                // Test double does not depend on Unity/BepInEx ConfigurationManager.
#if GIGAPMC_OFFLINE_TESTS
                var description=new ConfigDescription(
                    "Percentage points; edit in 0.25 increments. Existing values survive migration.",
                    new AcceptableValueRange<float>(0f,max));
#else
                var description=new ConfigDescription(
                    "Percentage points; slider, +/- and numeric input use 0.25 steps. " +
                    "PMC level and prestige only; temporary EFT buffs remain native.",
                    new AcceptableValueRange<float>(0f,max),
                    new ConfigurationManagerAttributes {
                        ShowRangeAsPercent=false,
                        CustomDrawer=PercentStepDrawer.Draw
                    });
#endif
                var entry=config.Bind(section,key,value,description);
                return Migrate(entry,legacy,kind,section,key,idx.ToString("D2")+" "+name,
                    raw => { float? v=ParseFloat(raw); return v.HasValue && v.Value<=max ? v:null; });
            }
            internal Track(ConfigFile config,string section,AttributeBonusKind kind,LegacySettingsReader legacy)
            {
                AttributeBonusRates d=AttributeBonusMath.Defaults(kind);
                Enabled=Flag(config,legacy,kind,section,1,"Enabled",true);
                PerLevelEnabled=Flag(config,legacy,kind,section,2,"Per Level Enabled",true);
                PerLevel=Percent(config,legacy,kind,section,3,"Per Level Percent",(float)d.PerLevelPercent,100f);
                EveryTenEnabled=Flag(config,legacy,kind,section,4,"Every 10 Levels Enabled",true);
                EveryTen=Percent(config,legacy,kind,section,5,"Every 10 Levels Percent",(float)d.EveryTenPercent,100f);
                AtMaxEnabled=Flag(config,legacy,kind,section,6,"At Max Level Enabled",true);
                AtMax=Percent(config,legacy,kind,section,7,"At Max Level Percent",(float)d.AtMaxLevelPercent,100f);
                PrestigeEnabled=Flag(config,legacy,kind,section,8,"Prestige Enabled",true);
                PrestigeAccumulated=Flag(config,legacy,kind,section,9,"Prestige Accumulated (Off - Fixed)",false);
                for(int i=0;i<6;i++)
                    Prestige[i]=Percent(config,legacy,kind,section,10+i,
                        "Prestige P"+(i+1)+" Percent",(float)d.PrestigeTiers[i],100f);
            }
            internal AttributeBonusRates Snapshot()
            {
                var t=new AttributeBonusRates {
                    Enabled=Enabled.Value,
                    PerLevelEnabled=PerLevelEnabled.Value, EveryTenEnabled=EveryTenEnabled.Value,
                    AtMaxLevelEnabled=AtMaxEnabled.Value,PrestigeEnabled=PrestigeEnabled.Value,
                    PrestigeMode=PrestigeAccumulated.Value ? PercentPrestigeMode.Accumulated:PercentPrestigeMode.Fixed,
                    PerLevelPercent=(decimal)PerLevel.Value,
                    EveryTenPercent=(decimal)EveryTen.Value,
                    AtMaxLevelPercent=(decimal)AtMax.Value
                };
                for(int i=0;i<6;i++) t.PrestigeTiers[i]=(decimal)Prestige[i].Value;
                if(!t.IsValid()) throw new InvalidOperationException("INVALID_ATTRIBUTE_PERCENT_CONFIG");
                return t;
            }
        }

        private readonly Dictionary<AttributeBonusKind,Track> tracks=new Dictionary<AttributeBonusKind,Track>();
        private AttributeBonusConfig(ConfigFile config,LegacySettingsReader legacy)
        {
            // Bind exactly 5 x 15 settings. Never bind Sprint/Walk/Crouch etc
            // separately. Older keys remain ignored in CFG until cleaned manually.
            foreach(var kind in new[]{AttributeBonusKind.CarryWeight,
                AttributeBonusKind.BodyStamina,AttributeBonusKind.AdsEndurance,
                AttributeBonusKind.MeleeDamage,AttributeBonusKind.SprintSpeed})
                tracks.Add(kind,new Track(config,Section(kind),kind,legacy));
        }
        internal static AttributeBonusConfig Bind(ConfigFile config)
        {
            LegacySettingsReader previous=null;
            try { previous=LegacySettingsReader.FromFiles(config.ConfigFilePath,null); }
            catch { /* The absence of a legacy config must not break HP initialization. */ }
            return new AttributeBonusConfig(config,previous);
        }
        internal AttributeBonusRates Snapshot(AttributeBonusKind kind)
        {
            // All 5 movement states share one configured rate, independent of
            // the movement mode chosen by EFT. No hidden per-mode multipliers.
            return tracks[AttributeBonusMath.IsMovement(kind)
                ? AttributeBonusKind.SprintSpeed : kind].Snapshot();
        }
        private static string Section(AttributeBonusKind kind)
        {
            switch(kind)
            {
                case AttributeBonusKind.CarryWeight:return "06 Bonus - Carrying Weight";
                case AttributeBonusKind.BodyStamina:return "07 Bonus - Body Stamina";
                case AttributeBonusKind.AdsEndurance:return "08 Bonus - ADS Endurance";
                case AttributeBonusKind.MeleeDamage:return "09 Bonus - Melee Damage";
                case AttributeBonusKind.SprintSpeed:
                case AttributeBonusKind.WalkSpeed:
                case AttributeBonusKind.CovertSpeed:
                case AttributeBonusKind.CrouchSpeed:
                case AttributeBonusKind.ProneSpeed:return "10 Bonus - Movement Speed";
                default:throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }
    }
}
