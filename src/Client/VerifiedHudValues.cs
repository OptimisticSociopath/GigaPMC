using System;
using System.Globalization;

namespace GigaPMC.Client
{
    // Only verified numbers returned by executed EFT hooks appear in the HUD.
    // In the main menu, there is an EFT.Profile but no proven Physical/Hands/
    // Movement/Knife controller corresponding to the final in-raid results.
    // Showing a F12 percentage or an IL projection as a measured value is forbidden.
    internal static class VerifiedHudValues
    {
        private static readonly string[] Names={
            "Carrying Weight", "Body Stamina", "ADS Endurance",
            "Melee Damage", "Movement Speed"
        };
        private static readonly string[] Units={
            "kg threshold", "stamina output", "ADS drain", "damage", "speed"
        };
        internal static string[] NoMeasuredMenuResults()
        {
            string[] rows=new string[6];
            rows[0]="GigaPMC | EFT MENU STATS";
            for(int i=0;i<5;i++) rows[i+1]="--  "+Names[i];
            float eft,final;
            if(MenuCarryMath.TryRead(out eft,out final))
                rows[1]=final.ToString("0.###",CultureInfo.InvariantCulture)+
                    " kg  "+Names[0];
            float staminaNative, staminaApplied;
            if(MenuStaminaEvidence.TryRead(out staminaNative,out staminaApplied))
                rows[2]=staminaApplied.ToString("0.###",CultureInfo.InvariantCulture)+
                    " capacity  "+Names[1];
            return rows;
        }
        internal static string[] MeasuredRaidResults()
        {
            string[] rows=new string[6];
            rows[0]="GigaPMC | VERIFIED STATS | RAID";
            for(int i=0;i<5;i++)
            {
                float before,after; int hits;
                if(!NativeEvidence.TryGetMeasured((GlobalBonusId)i,out before,out after,out hits))
                    rows[i+1]="--  "+Names[i];
                else
                    rows[i+1]=after.ToString("0.###",CultureInfo.InvariantCulture)+
                        " "+Units[i]+"  "+Names[i];
            }
            // P44: never conflate EFT's two speed getters in one "speed"
            // number. Neither getter is verified world displacement in m/s.
            string speedGetterRow=MovementEndpointEvidence.HudRow();
            if(speedGetterRow!=null) rows[5]=speedGetterRow;
            return rows;
        }

        // Profile-derived F12 percentages are available BEFORE any movement, ADS,
        // stamina or melee native call. Label them CONFIGURED, never measured.
        // The old verified-only methods above remain unchanged for existing callers.
        internal static string[] WithConfiguredRates(string[] measured,decimal[] configured,bool inRaid)
        {
            if(measured==null || measured.Length!=6 || configured==null || configured.Length!=5)
                return measured;
            var output=(string[])measured.Clone();
            output[0]=inRaid ? "GigaPMC | RAID | F12 FROZEN" : "GigaPMC | MENU | PROFILE BONUS";
            for(int i=0;i<5;i++)
            {
                decimal pct=configured[i];
                if(pct<0m || pct>100000m) continue;
                string bonus="+"+pct.ToString("0.###",CultureInfo.InvariantCulture)+"% F12";
                if(measured[i+1].StartsWith("--",StringComparison.Ordinal))
                    output[i+1]=bonus+" configured | native --  "+Names[i];
                else
                    output[i+1]=measured[i+1]+"  ["+bonus+" configured]";
            }
            return output;
        }
        internal static bool HasOnlyActualValues(string[] rows)
        {
            if(rows==null || rows.Length!=6) return false;
            for(int i=1;i<rows.Length;i++)
                if(rows[i]==null || rows[i].Contains("PREVIEW") ||
                   rows[i].Contains("F12 live") || rows[i].Contains('%')) return false;
            return true;
        }
    }
}
