using System;
using System.IO;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;

namespace GigaPMC.Client
{
    [BepInPlugin("com.vargor.gigapmc", "GigaPMC - For Tarkov Enjoyers", "1.0.0")]
    [BepInDependency("com.fika.core", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        private ConfigEntry<bool> isEnabled;
        private ConfigEntry<int> perLevel;
        private ConfigEntry<int> eachTen;
        private ConfigEntry<int> atMax;
        private ConfigEntry<int> prestige;
        private ConfigEntry<int> maxLevel;
        private bool isFika;

        private void Awake()
        {
            // Fika hosts and guests share a remote SPT backend. A local JSON write
            // on a guest PC MUST NOT be mistaken for a change to that backend.
            isFika = Chainloader.PluginInfos.ContainsKey("com.fika.core");
            if (isFika)
            {
                Config.Bind("00 Fika", "Configuration authority",
                    "Fika server admin (config/fika-server-settings.json)",
                    new ConfigDescription(
                        "All players receive the bonuses configured by the shared SPT server. F12 on a guest cannot change server HP.",
                        null, new ConfigurationManagerAttributes { ReadOnly = true }));
                Logger.LogInfo("[GigaPMC] Fika client: local F12 HP edits disabled. Server admin controls shared HP settings.");
                return;
            }

            isEnabled = Config.Bind("01 General", "Enabled", true,
                "GigaPMC by Vargor / OptimisticSociopath. HP scaling only on your PMC, never bots or Scav.");
            perLevel = Config.Bind("02 HP Bonuses", "Bonus HP per level", 1,
                new ConfigDescription("Absolute HP for each level above 1 (not a percentage).",
                    new AcceptableValueRange<int>(0, 100),
                    new ConfigurationManagerAttributes { ShowRangeAsPercent = false }));
            eachTen = Config.Bind("02 HP Bonuses", "Bonus HP every 10 levels", 5,
                new ConfigDescription("Extra HP at levels 10, 20, 30...", new AcceptableValueRange<int>(0, 1000)));
            atMax = Config.Bind("02 HP Bonuses", "Bonus HP at maximum level", 12,
                new ConfigDescription("One-time additional HP at max PMC level.", new AcceptableValueRange<int>(0, 1000)));
            prestige = Config.Bind("02 HP Bonuses", "Bonus HP per prestige", 5,
                new ConfigDescription("Starting HP on every part per prestige level.", new AcceptableValueRange<int>(0, 1000)));
            maxLevel = Config.Bind("03 Level Rules", "Maximum PMC level", 79,
                new ConfigDescription("Level threshold for the maximum-level bonus.", new AcceptableValueRange<int>(1, 1000)));

            Config.SettingChanged += SettingsChanged;
            SaveSettings();
            Logger.LogInfo("[GigaPMC] Single-player F12 settings enabled.");
        }

        private void SettingsChanged(object sender, SettingChangedEventArgs args)
        {
            if (!isFika) SaveSettings();
        }

        private void SaveSettings()
        {
            if (isFika) return;
            try
            {
                string folder = Path.Combine(Paths.GameRootPath, "SPT_Runtime", "user", "mods", "GigaPMC", "config");
                if (!Directory.Exists(folder))
                {
                    Logger.LogWarning("GigaPMC server folder not found locally. F12 sync unavailable (normal when using a remote server).");
                    return;
                }
                string file = Path.Combine(folder, "f12-overrides.json");
                string json = "{\n"
                    + "  \"Enabled\": " + (isEnabled.Value ? "true" : "false") + ",\n"
                    + "  \"BonusHpPerLevel\": " + perLevel.Value + ",\n"
                    + "  \"BonusHpEvery10Levels\": " + eachTen.Value + ",\n"
                    + "  \"BonusHpAtMaxLevel\": " + atMax.Value + ",\n"
                    + "  \"BonusHpPerPrestige\": " + prestige.Value + ",\n"
                    + "  \"MaxPmcLevel\": " + maxLevel.Value + "\n} \n";
                string temp = file + ".tmp";
                File.WriteAllText(temp, json);
                string backup = file + ".bak";
                if (File.Exists(file))
                {
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Replace(temp, file, backup);
                }
                else File.Move(temp, file);
            }
            catch (Exception ex)
            {
                Logger.LogError("Cannot sync local F12 to SPT server: " + ex.Message);
            }
        }
    }

    internal sealed class ConfigurationManagerAttributes
    {
        public bool? ShowRangeAsPercent;
        public bool? ReadOnly;
    }
}
