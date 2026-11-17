using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace GigaPMC.Client
{
    // P37: menu-specific FINAL RESULT hook, distinct from the raid-only
    // PhysicalBase.UpdateWeightLimits postfix in AttributeP24CarryTrial.
    // EFT's CG_Show computes both limits from GlobalConfiguration, SkillManager
    // and IHealthController. We multiply the native FINAL float locals right
    // after the EFT computations, BEFORE SetParameterValue/SetWarningColor.
    // No profile, skill, global configuration, health or physical writes.
    internal static class MenuCarryHook
    {
        private const string EftHash = "EE25CEE1259777B38ED8B3E7841FDC2DB3C98540B1469FA539B1FF183476E436";
        private const string PatchOwner = "com.vargor.gigapmc.globalbonus.v1.carrying-weight.menu";
        private const BindingFlags Any = BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        private static Harmony harmony;
        private static Plugin owner;
        private static ManualLogSource logger;
        private static MethodInfo showWeight;
        private static Type panelType;
        private static bool installed, blocked, queued, refreshedOnce;
        private static int nextFrame, menuRefreshAttempts;
        private static DateTime queuedAt;
        private static string lastApplication;
        private static bool seenLower, seenUpper;
        private static WeakReference lastObservedPanel;

        internal static bool Installed => installed;
        internal static void TryInstall(Plugin plugin, ManualLogSource log)
        {
            owner = plugin;
            logger = log;
            if (installed || blocked) return;
            try
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(
                    a => a.GetName().Name == "Assembly-CSharp");
                string dll = Path.Combine(Paths.GameRootPath,
                    "EscapeFromTarkov_Data", "Managed", "Assembly-CSharp.dll");
                if (!File.Exists(dll)) throw new FileNotFoundException("EFT_CLIENT_MISSING");
                string actual;
                using (var stream = File.OpenRead(dll))
                using (var sha = SHA256.Create())
                    actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                if (!string.Equals(EftHash,actual,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("EFT_416_SHA256_MISMATCH");
                panelType = assembly.GetType("EFT.UI.Health.HealthParametersPanel", true);
                if (!typeof(Component).IsAssignableFrom(panelType))
                    throw new TypeLoadException("WEIGHT_MENU_PANEL_NOT_COMPONENT");
                try
                {
                    showWeight = UniqueMethodContract.Resolve(panelType,"CG_Show",typeof(void));
                }
                catch (MissingMethodException)
                {
                    // Runtime prepatch may rename the private method. One-time
                    // search by BOTH exact native limit calculations only.
                    // No method is selected when zero/multiple shapes match.
                    showWeight = MenuIlSelector.ResolveFromNativeIl(panelType);
                    logger?.LogInfo("[GigaPMC] P39_MENU_WEIGHT_IL_FALLBACK_SELECTED|"+
                        showWeight.Name+"|TOKEN="+showWeight.MetadataToken.ToString("X8"));
                }
                // P38: select by unique loaded-code signature, not prepatch token.
                // P38: check four exact native field names and instance ownership.
                // The prepatch may renumber metadata tokens; IL shape is verified separately.
                foreach (var item in new[] {
                    new { Name="_weight", Token=0x0400C61E },
                    new { Name="_health", Token=0x0400C620 },
                    new { Name="_skills", Token=0x0400C622 },
                    new { Name="_inventory", Token=0x0400C621 }
                })
                {
                    var field = panelType.GetField(item.Name,Any|BindingFlags.DeclaredOnly);
                    if (field==null || field.IsStatic)
                        throw new MissingFieldException("CG_SHOW_FIELD_ABI_"+item.Name);
                }
                harmony = new Harmony(PatchOwner);
                harmony.Patch(showWeight,
                    transpiler:new HarmonyMethod(typeof(MenuCarryHook),nameof(AfterNativeCalculations)));
                HarmonyAudit.Require(showWeight,PatchOwner,
                    nameof(AfterNativeCalculations),"transpiler");
                installed = true;
                logger.LogInfo("[GigaPMC] P38_LIVE_ABI_MENU_WEIGHT|TOKEN="+showWeight.MetadataToken.ToString("X8")+"|UNIQUE_SIGNATURE|IL_ANCHOR_VERIFIED");
                logger.LogInfo("[GigaPMC] P37_MENU_NATIVE_WEIGHT_HOOK_REGISTERED|TOKEN=060101C5"+
                    "|OWNER="+PatchOwner+"|UI_ONLY|GAMEPLAY_RAID_NOT_CERTIFIED");
                Queue();
            }
            catch (Exception ex)
            {
                try { harmony?.UnpatchSelf(); } catch { }
                installed=false;
                blocked=true;
                MenuCarryMath.Reset();
                logger?.LogWarning("[GigaPMC] P37_MENU_NATIVE_WEIGHT_HOOK_BLOCKED|"+
                    ex.GetType().Name+"|"+ex.Message);
            }
        }

        // Use the actual EFT IL anchors, not arbitrary screen text or a fixed
        // skill level. The current EFT IL has two disjoint computations:
        // LowerOverweightLimit->relative*health+absolute->stloc.3;
        // UpperOverweightLimit->relative*health+absolute->stloc.s 4.
        private static IEnumerable<CodeInstruction> AfterNativeCalculations(
            IEnumerable<CodeInstruction> input)
        {
            var code=input.ToList();
            int lower=-1, upper=-1, lowerStore=-1, upperStore=-1;
            for(int i=0;i<code.Count;i++)
            {
                if ((code[i].opcode != OpCodes.Callvirt && code[i].opcode != OpCodes.Call) ||
                    !(code[i].operand is MethodInfo method) ||
                    method.DeclaringType?.FullName != "EFT.GlobalConfiguration+StaminaParameters")
                    continue;
                if(method.Name=="get_LowerOverweightLimit")
                {
                    if(lower>=0) throw new InvalidProgramException("P37_DUPLICATE_LOWER_GETTER");
                    lower=i;
                }
                else if(method.Name=="get_UpperOverweightLimit")
                {
                    if(upper>=0) throw new InvalidProgramException("P37_DUPLICATE_UPPER_GETTER");
                    upper=i;
                }
            }
            // The pinned IL matches exactly: getter, ldloc.1, mul, ldloc.2,
            // add, store. Accept NO other instruction topology.
            if(lower<0 || upper<0 || upper<=lower ||
               !MatchesNativeStore(code,lower,OpCodes.Stloc_3) ||
               !MatchesNativeStore(code,upper,OpCodes.Stloc_S))
                throw new InvalidProgramException("P37_MENU_WEIGHT_IL_ANCHOR_DRIFT");
            lowerStore=lower+5;
            upperStore=upper+5;
            object upperLocal = code[upperStore].operand;
            if (upperLocal==null) throw new InvalidProgramException("P37_UPPER_LOCAL_MISSING");
            var scale=AccessTools.Method(typeof(MenuCarryHook),nameof(ScaleAfterEft));
            if(scale==null) throw new MissingMethodException("P37_SCALE_METHOD_NOT_FOUND");
            var result=new List<CodeInstruction>(code.Count+10);
            for(int i=0;i<code.Count;i++)
            {
                result.Add(code[i]);
                if(i==lowerStore)
                {
                    result.Add(new CodeInstruction(OpCodes.Ldloc_3));
                    result.Add(new CodeInstruction(OpCodes.Ldarg_0));
                    result.Add(new CodeInstruction(OpCodes.Ldc_I4_0));
                    result.Add(new CodeInstruction(OpCodes.Call,scale));
                    result.Add(new CodeInstruction(OpCodes.Stloc_3));
                }
                if(i==upperStore)
                {
                    result.Add(new CodeInstruction(OpCodes.Ldloc_S,upperLocal));
                    result.Add(new CodeInstruction(OpCodes.Ldarg_0));
                    result.Add(new CodeInstruction(OpCodes.Ldc_I4_1));
                    result.Add(new CodeInstruction(OpCodes.Call,scale));
                    result.Add(new CodeInstruction(OpCodes.Stloc_S,upperLocal));
                }
            }
            return result;
        }
        private static bool MatchesNativeStore(IList<CodeInstruction> code,int getter,OpCode store)
        {
            if(getter+4>=code.Count) return false;
            return code[getter+1].opcode==OpCodes.Ldloc_1 &&
                   code[getter+2].opcode==OpCodes.Mul &&
                   code[getter+3].opcode==OpCodes.Ldloc_2 &&
                   code[getter+4].opcode==OpCodes.Add &&
                   getter+5<code.Count && code[getter+5].opcode==store;
        }
        private static float ScaleAfterEft(float nativeKg,object panel,bool upper)
        {
            if (!installed || owner==null || panel==null ||
                !panelType.IsInstanceOfType(panel)) return nativeKg;
            try
            {
                // Native CG_Show callback gives us the real component instance.
                // Cache it; never scan all Unity objects on F12 changes.
                lastObservedPanel = new WeakReference(panel);
                int level=0,prestige=0,cap=0;
                decimal percent;
                if(owner.P33InRaid)
                {
                    // During a raid MenuHealthSync has NO out-of-raid profile.
                    // Use the same frozen native multiplier as PhysicalBase.
                    float ratio;
                    if(!CarryCapacityHooks.TryGetFrozenRaidRatio(out ratio))
                        return nativeKg;
                    percent=(decimal)ratio*100m;
                    // Do not double-apply if a future EFT UI path already
                    // receives a value equal to the measured final threshold.
                    float native,final; int observations;
                    if(NativeEvidence.TryGetMeasured(GlobalBonusId.CarryingWeight,
                        out native,out final,out observations) &&
                        Math.Abs(nativeKg-final)<Math.Max(0.05f,final*0.001f))
                        return nativeKg;
                }
                else
                {
                    object profile;
                    if (!AttributeDevVision.TryReadMenuPmc(
                        out level,out prestige,out cap,out profile)) return nativeKg;
                    percent=owner.P09BonusRates(AttributeBonusKind.CarryWeight)
                        .TotalPercent(owner.P09GlobalEnabled,level,prestige,cap);
                }
                if(percent<0m || percent>100000m) return nativeKg;
                float modified;
                if (!MenuCarryMath.TryScale(nativeKg,percent,out modified)) return nativeKg;
                if(upper)
                {
                    seenUpper=true;
                    MenuCarryMath.RecordUpper(nativeKg,modified);
                    string stamp=(owner.P33InRaid ? "RAID/" : "MENU/")+level+"/"+prestige+"/"+nativeKg.ToString("R")+"/"+
                        modified.ToString("R");
                    if(stamp!=lastApplication)
                    {
                        lastApplication=stamp;
                        logger?.LogInfo("[GigaPMC] P37_MENU_NATIVE_WEIGHT_APPLIED"+
                            "|EFT_UPPER_KG="+nativeKg+"|GIGAPMC_UPPER_KG="+modified+
                            "|ID="+AttributeGlobalBonus.Identity(GlobalBonusId.CarryingWeight)+
                            "|IL_NATIVE_AFTER_SKILL_HEALTH|UI_ONLY|RAID_UNVERIFIED");
                    }
                }
                else seenLower=true;
                return modified;
            }
            catch (Exception ex)
            {
                logger?.LogWarning("[GigaPMC] P37_MENU_NATIVE_WEIGHT_SCALE_SKIPPED|"+
                    ex.GetType().Name);
                return nativeKg;
            }
        }
        internal static void Queue()
        {
            if(!installed) return;
            MenuCarryMath.Reset();
            lastApplication=null;
            seenLower=seenUpper=false;
            queuedAt=DateTime.UtcNow;
            queued=true;
            menuRefreshAttempts=0;
        }
        internal static void Tick(Plugin plugin)
        {
            if(!installed || plugin==null || plugin.P33InRaid) return;
            if(!queued || DateTime.UtcNow-queuedAt<TimeSpan.FromMilliseconds(250) ||
               Time.frameCount<nextFrame) return;
            nextFrame=Time.frameCount+120;
            if(menuRefreshAttempts++ >= 10)
            {
                queued=false;
                logger?.LogWarning("[GigaPMC] P37_MENU_WEIGHT_REFRESH_UNAVAILABLE"+
                    "|NO_ACTIVE_EFT_PANEL|NO_FABRICATED_VALUES");
                return;
            }
            int level,prestige,cap;
            object profile;
            if(!AttributeDevVision.TryReadMenuPmc(
                out level,out prestige,out cap,out profile)) return;
            try
            {
                var panel=lastObservedPanel?.Target as Component;
                if(panel == null || panel.gameObject==null ||
                   !panel.gameObject.activeInHierarchy)
                {
                    queued=false; // Native CG_Show will reacquire when panel next opens.
                    return;
                }
                object weight=panelType.GetField("_weight",Any|BindingFlags.DeclaredOnly)?.GetValue(panel);
                object skills=panelType.GetField("_skills",Any|BindingFlags.DeclaredOnly)?.GetValue(panel);
                object health=panelType.GetField("_health",Any|BindingFlags.DeclaredOnly)?.GetValue(panel);
                object inventory=panelType.GetField("_inventory",Any|BindingFlags.DeclaredOnly)?.GetValue(panel);
                if(weight==null || skills==null || health==null || inventory==null)
                { queued=false;return; }
                showWeight.Invoke(panel,null);
                if(seenLower && seenUpper)
                {
                    queued=false; refreshedOnce=true;
                    logger?.LogInfo("[GigaPMC] P40_MENU_NATIVE_WEIGHT_REFRESHED"+
                        "|F12_LIVE|CACHED_PANEL|ZERO_UNITY_SCANS");
                }
            }
            catch(Exception ex)
            {
                logger?.LogWarning("[GigaPMC] P37_MENU_WEIGHT_REFRESH_RETRY|"+ex.GetType().Name);
            }
        }
    }
}
