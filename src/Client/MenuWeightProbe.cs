using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

namespace GigaPMC.Client
{
    // Read ONLY selected known menu panels from user's P34 EFT ABI report.
    // The UI displayed native weight is NOT a verified GigaPMC-increased limit.
    // Never write text, skill progress, physical fields or buff registries here.
    internal static class MenuWeightProbe
    {
        private const BindingFlags Any=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        private static readonly string[] TargetTypes={
            "EFT.UI.CharacteristicsPanel","EFT.UI.Health.HealthParametersPanel"
        };
        private static readonly string[][] TargetFields={
            new[]{"_charWeight","_weightAttribute"},new[]{"_weight"}
        };
        private static int nextFrame;
        private static string lastReport;
        internal static void Reset() { nextFrame=0; lastReport=null; }
        private static string ReadNativeUiText(object field)
        {
            if(field==null) return null;
            if(field is string text) return text;
            Type type=field.GetType();
            // Only a publicly exposed UI text property, never ToString()
            // of a native gameplay object (which might contain a profile ID).
            PropertyInfo property=type.GetProperty("text",Any);
            if(property==null || property.PropertyType!=typeof(string) ||
                property.GetIndexParameters().Length!=0) return null;
            string value=property.GetValue(field,null) as string;
            return value!=null && value.Length<=100 ? value : null;
        }
        internal static void Tick(Plugin plugin)
        {
            if(plugin==null || plugin.P33InRaid || MenuCarryHook.Installed || Time.frameCount<nextFrame) return;
            nextFrame=Time.frameCount+120;
            try
            {
                int level,prestige,cap; object profile;
                if(!AttributeDevVision.TryReadMenuPmc(out level,out prestige,out cap,out profile)) return;
                Assembly eft=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(
                    a=>a.GetName().Name=="Assembly-CSharp");
                if(eft==null) return;
                int matches=0;
                for(int t=0;t<TargetTypes.Length;t++)
                {
                    Type panelType=eft.GetType(TargetTypes[t],false);
                    if(panelType==null || !typeof(UnityEngine.Component).IsAssignableFrom(panelType)) continue;
                    UnityEngine.Object[] panels=Resources.FindObjectsOfTypeAll(panelType);
                    for(int j=0;j<panels.Length && j<8;j++)
                    {
                        var panel=panels[j] as UnityEngine.Component;
                        if(panel==null || panel.gameObject==null || !panel.gameObject.activeInHierarchy) continue;
                        foreach(string name in TargetFields[t])
                        {
                            FieldInfo field=panelType.GetField(name,Any);
                            if(field==null) continue;
                            string text=ReadNativeUiText(field.GetValue(panel));
                            if(string.IsNullOrWhiteSpace(text)) continue;
                            // The text is an EFT-native UI observation, not a
                            // GigaPMC post-bonus statistic or gameplay evidence.
                            string sanitized=Regex.Replace(text,@"[^0-9\s,./a-zA-Z]","");
                            if(sanitized.Length>60) continue;
                            matches++;
                            string report=TargetTypes[t]+"."+name+"="+sanitized;
                            if(report!=lastReport)
                            {
                                lastReport=report;
                                plugin.P35Log("MENU_NATIVE_WEIGHT_UI_SEEN|"+report+
                                    "|OBSERVATION_ONLY|NOT_A_GIGAPMC_FINAL_VALUE");
                            }
                        }
                    }
                }
                if(matches==0 && lastReport!= "NO_MATCH")
                {
                    lastReport="NO_MATCH";
                    plugin.P35Log("MENU_NATIVE_WEIGHT_UI_NOT_OBSERVED|KNOWN_PANELS_ONLY|NO_INFERRED_KG");
                }
            }
            catch(Exception ex)
            {
                string message="MENU_NATIVE_WEIGHT_UI_READ_FAILED|"+ex.GetType().Name;
                if(lastReport!=message) { lastReport=message; plugin.P35Log(message); }
            }
        }
    }
}
