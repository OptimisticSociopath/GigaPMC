using System;
using BepInEx.Configuration;
using UnityEngine;

namespace GigaPMC.Client
{
    // Same OUT-OF-RAID menu owner/cap as the proven HP controller, no new ABI
    // assumptions and no fake writes to skill progress or global EFT buffs.
    // These five READ-ONLY F12 values update without restarting the game.
    internal sealed class MenuBonusLive
    {
        private readonly ConfigEntry<string>[] status = new ConfigEntry<string>[5];
        private int nextFrame;
        private string lastState;
        private string lastValues;
        private bool refreshing;
        internal bool Refreshing => refreshing;
        internal void Invalidate() { nextFrame = 0; }
        internal MenuBonusLive(ConfigFile file)
        {
            for (int i = 0; i < 5; i++)
                status[i] = file.Bind("11 Live F12 Bonus Status (read-only)",
                    (i+1).ToString("D2") + " " + MenuBonusStatus.Labels[i],
                    "WAITING FOR MAIN-MENU PMC",
                    new ConfigDescription("Actual F12 percentage; NOT a native EFT statistic. " +
                        "Real global bonus is applied by its gameplay hook during a local PMC raid.",
                        null, new ConfigurationManagerAttributes { ReadOnly = true }));
        }
        internal void Tick(Plugin plugin)
        {
            if (plugin == null || Time.frameCount < nextFrame) return;
            nextFrame = Time.frameCount + 12;
            string[] values;
            string state;
            if (plugin.P33InRaid)
            {
                values = new string[5];
                for (int i=0;i<5;i++) values[i] = "RAID FROZEN | F12 changes deferred until raid ends";
                state = "RAID_FROZEN";
            }
            else
            {
                int level,prestige,cap;
                object profile;
                if (!AttributeDevVision.TryReadMenuPmc(out level,out prestige,out cap,out profile))
                {
                    values = new string[5];
                    for (int i=0;i<5;i++) values[i] = "WAITING FOR MAIN-MENU PMC / LEVEL CAP";
                    state = "MENU_PMC_UNAVAILABLE";
                }
                else
                {
                    try
                    {
                        values = MenuBonusStatus.Build(plugin.P09GlobalEnabled,level,prestige,cap,
                            plugin.P09BonusRates);
                        state = "MENU_F12_LIVE|L"+level+"|P"+prestige+"|CAP"+cap;
                    }
                    catch (Exception ex)
                    {
                        values = new string[5];
                        for (int i=0;i<5;i++) values[i] = "INVALID F12 / " + ex.GetType().Name;
                        state = "MENU_BONUS_INVALID|"+ex.GetType().Name;
                    }
                }
            }
            refreshing = true;
            try
            {
                for (int i=0;i<5;i++)
                    if (!string.Equals(status[i].Value,values[i],StringComparison.Ordinal))
                        status[i].Value=values[i];
            }
            finally { refreshing=false; }
            string snapshot = string.Join(";",values);
            if (lastState != state || lastValues != snapshot)
            {
                lastState=state;
                lastValues=snapshot;
                plugin.P33Log(state + "|FIVE_BONUSES|READ_ONLY_F12_STATUS|NOT_A_NATIVE_MENU_STAT_WRITE|VALUES=" + snapshot);
            }
        }
    }
}
