using System;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;

namespace GigaPMC.Client
{
    // One-time, read-only audit of methods in the loaded/prepatched EFT assembly.
    // No source IL or user identity is copied; the log contains method shapes.
    internal static class LiveAbiAudit
    {
        private static bool reported;
        private static readonly string[][] Targets = {
            new[]{"PhysicalBase","UpdateWeightLimits","OnWeightLimitsUpdated"},
            new[]{"Physical","BaseStaminaFunction"},
            new[]{"Stamina","Process"},
            new[]{"EFT.Player+BaseKnifeController","ProcessHit"},
            new[]{"EFT.MovementContext","get_ClampedSpeed"},
            new[]{"EFT.UI.Health.HealthParametersPanel","CG_Show"}
        };
        internal static void Report(ManualLogSource log)
        {
            if(reported || log==null) return;
            reported=true;
            var eft=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(
                a=>a.GetName().Name=="Assembly-CSharp");
            if(eft==null) { log.LogWarning("[GigaPMC] P38_LIVE_ABI|ASSEMBLY_NOT_LOADED");return; }
            foreach(var row in Targets)
            {
                try
                {
                    var type=eft.GetType(row[0],false);
                    if(type==null) { log.LogWarning("[GigaPMC] P38_LIVE_ABI|TYPE_MISSING|"+row[0]);continue; }
                    foreach(var name in row.Skip(1))
                    {
                        var methods=type.GetMethods(BindingFlags.Instance|BindingFlags.Public|
                            BindingFlags.NonPublic|BindingFlags.DeclaredOnly)
                            .Where(m=>m.Name==name).Take(6).ToArray();
                        if(methods.Length==0)
                        { log.LogWarning("[GigaPMC] P38_LIVE_ABI|METHOD_MISSING|"+row[0]+"."+name);continue; }
                        foreach(var method in methods)
                        {
                            string signature=method.ReturnType.Name+"("+
                                string.Join(",",method.GetParameters().Select(x=>x.ParameterType.Name))+ ")";
                            log.LogInfo("[GigaPMC] P38_LIVE_ABI|"+row[0]+"."+name+
                                "|TOKEN="+method.MetadataToken.ToString("X8")+
                                "|SHAPE="+signature+"|BODY="+(method.GetMethodBody()!=null));
                        }
                    }
                }
                catch(Exception ex)
                { log.LogWarning("[GigaPMC] P38_LIVE_ABI|PROBE_FAIL|"+row[0]+"|"+ex.GetType().Name); }
            }
        }
    }
}
