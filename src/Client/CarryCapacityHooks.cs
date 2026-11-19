using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace GigaPMC.Client
{
    // P31: final-native-value Carrying Weight multiplier. Default OFF. The previous
    // P09/P10/P19 certification gates remain false and are NOT bypassed.
    // No HP hooks/fields, no file writes, no profile mutation, no Fika.
    internal static class CarryCapacityHooks
    {
        private const string EftPin="EE25CEE1259777B38ED8B3E7841FDC2DB3C98540B1469FA539B1FF183476E436";
        private const BindingFlags Any=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        private static readonly string[] FieldNames={"WalkOverweightLimits","BaseOverweightLimits",
            "SprintOverweightLimits","WalkSpeedOverweightLimits"};
        private static readonly int[] Tokens={0x04002938,0x04002939,0x0400293B,0x0400293C};
        private static readonly object Sync=new object();
        private static readonly LocalTrialAuthority samples=new LocalTrialAuthority();
        private static Harmony patch;
        private static MethodInfo updateLimits;
        private static MethodInfo refreshWeight;
        private static FieldInfo[] fields;
        private static FieldInfo bypassField;
        private static FieldInfo inertiaLimitsField, inertiaReadbackField;
        private static bool inertiaWarned, inertiaLogged;
        private static bool nativeEncumbranceBypassLogged;
        private static Plugin owner;
        private static ManualLogSource logger;
        private static bool permanentlyBlocked;
        private static object candidateWorld;
        private static object activeWorld,activePlayer,activeProfile,activePhysical;
        private static float frozenRatio;
        private static int nativeRetryBudget=4;
        private static int nextFrame;
        private static bool warned;

        // UI-only read. Exposes the already-frozen raid ratio, never an F12
        // reread or a guessed value. No Unity scans or disk access.
        internal static bool NeedsNativeRetry => owner!=null && owner.P24CarryTrialEnabled &&
            frozenRatio<=0f && !permanentlyBlocked;
        internal static bool TryGetFrozenRaidRatio(out float ratio)
        {
            lock(Sync)
            {
                ratio=frozenRatio;
                return ratio>0f && activePhysical!=null && owner!=null &&
                    owner.P33InRaid && owner.P24CarryTrialEnabled;
            }
        }
        private static void Status(string s) => logger?.LogInfo("[GigaPMC] P24_"+s);
        private static void Block(string s)
        {
            if (warned) return;
            warned=true;
            logger?.LogWarning("[GigaPMC] P24_TRIAL_BLOCKED|"+s+"|HP_UNCHANGED");
        }
        // Called only once after the local PMC's immutable F12 raid lease is captured.
        // No level/prestige sampling on Update, no repeating file hashes.
        internal static void ArmOnce(Plugin source,ManualLogSource log)
        {
            if (source==null) return;
            owner=source;logger=log;
            if (!source.P24CarryTrialEnabled || permanentlyBlocked) return;
            if (patch==null && !Install()) return;
            try { Sample(); }
            catch(Exception ex)
            {
                Block("ONE_SHOT_"+ex.GetType().Name);
                ResetSession();
            }
        }
        internal static void OnRaidEnded()
        {
            if(patch!=null) Disarm();
            else ResetSession();
        }
        private static Type FindUniqueType(string name)
        {
            Type[] found=AppDomain.CurrentDomain.GetAssemblies()
                .Select(a=>a.GetType(name,false)).Where(x=>x!=null).ToArray();
            if (found.Length!=1) throw new TypeLoadException("UNIQUE_EFT_TYPE_REQUIRED:"+name);
            return found[0];
        }
        private static FieldInfo Field(Type type,string name)
        {
            for(Type t=type;t!=null;t=t.BaseType)
            {
                var f=t.GetField(name,Any|BindingFlags.DeclaredOnly);
                if(f!=null) return f;
            }
            return null;
        }
        private static bool Install()
        {
            try
            {
                if (owner==null || owner.P24CarryTrialEnabled==false)
                    throw new InvalidOperationException("EXPLICIT_OPT_IN_REQUIRED");
                // EFT/Spt hashes are verified ONCE by the Windows build guard.
                // Runtime binds the loaded method + field ABI; never stream a
                // large game DLL on the main thread at raid start.
                Type physical=FindUniqueType("PhysicalBase");
                if(physical.Assembly.GetName().Name!="Assembly-CSharp")
                    throw new TypeLoadException("EFT_PHYSICAL_ASSEMBLY_MISMATCH");
                updateLimits=UniqueMethodContract.Resolve(physical,
                    "UpdateWeightLimits",typeof(void));
                refreshWeight=UniqueMethodContract.Resolve(physical,
                    "OnWeightLimitsUpdated",typeof(void));
                var ff=new FieldInfo[4];
                for(int i=0;i<4;i++)
                {
                    ff[i]=physical.GetField(FieldNames[i],Any|BindingFlags.DeclaredOnly);
                    if(ff[i]==null ||
                        ff[i].FieldType!=typeof(Vector2) || ff[i].IsStatic || ff[i].IsInitOnly)
                        throw new MissingFieldException("NATIVE_FIELD_ABI_"+FieldNames[i]);
                }
                bypassField=physical.GetField("_encumberDisabled",Any|BindingFlags.DeclaredOnly);
                if(bypassField==null ||
                    bypassField.FieldType!=typeof(bool))
                    throw new MissingFieldException("ENCUMBRANCE_NATIVE_BYPASS_ABI");
                // P49 ABI: native inertia thresholds are a separate Vector3.
                // UpdateWeightLimits writes BaseInertiaLimits; the same EFT
                // OnWeightLimitsUpdated() next calls virtual OnWeightUpdated()
                // which recalculates Inertia/side/diagonal from this vector.
                inertiaLimitsField=physical.GetField("BaseInertiaLimits",Any|BindingFlags.DeclaredOnly);
                inertiaReadbackField=physical.GetField("Inertia",Any|BindingFlags.DeclaredOnly);
                if(inertiaLimitsField==null || inertiaLimitsField.FieldType!=typeof(Vector3) ||
                    inertiaLimitsField.IsStatic || inertiaLimitsField.IsInitOnly ||
                    inertiaReadbackField==null || inertiaReadbackField.FieldType!=typeof(float) ||
                    inertiaReadbackField.IsStatic)
                    throw new MissingFieldException("P49_NATIVE_INERTIA_ABI_DRIFT");
                fields=ff;
                var h=new Harmony("com.vargor.gigapmc.p24.experimental.carry");
                var postfix=new HarmonyMethod(typeof(CarryCapacityHooks),nameof(AfterNativeWeightRecalculation));
                postfix.priority=Priority.Last;
                patch=h; // Set before Patch(): rollback can always unpatch on failure.
                h.Patch(updateLimits,postfix:postfix);
                HarmonyAudit.Require(updateLimits,"com.vargor.gigapmc.p24.experimental.carry",
                    nameof(AfterNativeWeightRecalculation),"postfix");
                HarmonyAudit.ReportPostfixOwners(updateLimits,"CARRY",logger);
                Status("P38_LIVE_ABI_CARRY|RUNTIME_TOKEN="+updateLimits.MetadataToken.ToString("X8")+"|UNIQUE_SIGNATURE|FIELD_TYPES_VERIFIED");
                Status("P36_HARMONY_OWNER_REGISTERED|TOKEN=0600352B|ID=carrying-weight");
                Status("ARMED|EXPERIMENTAL_CARRY_ONLY|WAITING_FOR_THREE_CLEAN_SAMPLES|DEFAULT_OFF");
                return true;
            }
            catch(Exception ex)
            {
                permanentlyBlocked=true;
                Block("ABI_OR_PATCH_"+ex.GetType().Name+"_"+ex.Message);
                try { patch?.UnpatchSelf(); } catch { }
                patch=null;
                return false;
            }
        }
        private static object Read(object value,string name) => AttributeMemberReader.Read(value,name);
        private static bool GetLocal(out object world,out object player,out object profile,out object physical)
        {
            world=player=profile=physical=null;
            world=RaidLifecycleHook.CurrentWorld;
            if(world==null) return false;
            player=Read(world,"MainPlayer");
            if(player==null || Read(player,"IsAI") is bool ai && ai) return false;
            bool eftPlayer=false;
            for(Type p=player.GetType();p!=null;p=p.BaseType)
                if(p.FullName=="EFT.Player" && p.Assembly.GetName().Name=="Assembly-CSharp")
                    eftPlayer=true;
            if(!eftPlayer) return false;
            profile=Read(player,"Profile");
            if(profile==null || profile.GetType().FullName!="EFT.Profile") return false;
            object info=Read(profile,"Info");
            string side=(Read(profile,"Side") ?? Read(info,"Side"))?.ToString();
            if(!string.Equals(side,"Bear",StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(side,"Usec",StringComparison.OrdinalIgnoreCase)) return false;
            if (owner == null || !owner.P29IsCapturedLocalPlayer(player)) return false;
            physical=Read(player,"Physical");
            return physical!=null && updateLimits.DeclaringType.IsAssignableFrom(physical.GetType());
        }
        private static object GlobalConfiguration()
        {
            // EFT's normal Singleton<GlobalConfiguration>.Instance source.
            // Require the EXPECTED generic owner; never scan arbitrary objects.
            Type global=FindUniqueType("EFT.GlobalConfiguration");
            Type generic=null;
            foreach(var name in new[]{"Comfort.Common.Singleton`1","EFT.Singleton`1"})
            {
                Type[] matches=AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a=>a.GetType(name,false)).Where(x=>x!=null).ToArray();
                if(matches.Length==1) { generic=matches[0]; break; }
            }
            if(generic==null) return null;
            Type specific=generic.MakeGenericType(global);
            PropertyInfo prop=specific.GetProperty("Instance",BindingFlags.Public|BindingFlags.Static);
            if(prop==null || prop.PropertyType!=global) return null;
            return prop.GetValue(null,null);
        }
        private static int Number(object value,int min,int max)
        {
            if(value==null) throw new InvalidDataException("MISSING_LEVEL_OR_PRESTIGE");
            double d=Convert.ToDouble(value,System.Globalization.CultureInfo.InvariantCulture);
            if(double.IsNaN(d)||double.IsInfinity(d)||d!=Math.Truncate(d)||d<min||d>max)
                throw new InvalidDataException("INVALID_LEVEL_OR_PRESTIGE");
            return checked((int)d);
        }
        private static int? Cap() => ServerLevelCapCache.Read();
        private static void Sample(bool inNativeRecalculation=false)
        {
            object world,player,profile,physical;
            if(!GetLocal(out world,out player,out profile,out physical))
            {
                ResetSession(); return;
            }
            if(activePlayer!=null)
            {
                if(!ReferenceEquals(activeWorld,world) || !ReferenceEquals(activePlayer,player) ||
                    !ReferenceEquals(activeProfile,profile) || !ReferenceEquals(activePhysical,physical))
                { ResetSession(); }
                else
                {
                    // The P31 rate is frozen by the shared F12 raid lease.
                    // If another mod changes EFT global weight configuration,
                    // let EFT produce NEW native vectors; our postfix scales
                    // those fresh results, never a stale calculated kg delta.
                    return;
                }
            }
            object info=Read(profile,"Info");
            int level=Number(Read(info,"Level"),1,10000);
            object p=Read(profile,"PrestigeLevel") ?? Read(info,"PrestigeLevel");
            int prestige=p==null?0:Number(p,0,10000);
            var rates=owner.P09BonusRates(AttributeBonusKind.CarryWeight);
            int cap=AttributeCapPolicy.Resolve(owner.CurrentRaidSettings(),Cap(),rates);
            decimal percent=rates.TotalPercent(owner.P09GlobalEnabled,level,prestige,cap);
            // The authenticated F12 lease already pins this exact local PMC.
            // A synthetic three-sample gate would be redundant and could
            // repeatedly read the profile during an otherwise frozen raid.
            if(percent<=0m || percent>100000m) return;
            // Native WeightLimits are recomputed by EFT before our postfix.
            // Strength, Elite, MULE and all other native buffs/debuffs are
            // ALREADY included. No SkillsDescriptor dependency is needed.
            float frozen=(float)(percent/100m);
            if(float.IsNaN(frozen) || float.IsInfinity(frozen) || frozen<=0f || frozen>1000f)
            { ResetSession(); Block("P34_INVALID_FROZEN_PERCENT"); return; }
            lock(Sync)
            {
                activeWorld=world; activePlayer=player;
                activeProfile=profile; activePhysical=physical;
                frozenRatio=frozen;
            }
            Status("P31_CARRY_PERCENT_SNAPSHOT_FROZEN|ID="+AttributeGlobalBonus.Identity(GlobalBonusId.CarryingWeight)+
                "|ONE_SHOT|SKILL_DESCRIPTOR_NOT_REQUIRED"+
                "|BONUS_PERCENT="+percent+"|NO_HP_CHANGE|IN_RAID_ONLY");
            // P35: ONCE after three stable PMC samples, ask EFT to recompute
            // its OWN four limits. Our postfix then adds the GigaPMC percentage
            // on the fresh native result. OnWeightLimitsUpdated alone only
            // notifies consumers; it does not guarantee a native recompute.
            // Never call UpdateWeightLimits from inside our postfix.
            if (inNativeRecalculation)
            {
                // We are already INSIDE UpdateWeightLimits postfix: the EFT
                // vectors were just calculated. Never recursively invoke it.
                Status("P201_NATIVE_CARRY_LATE_CAPTURE|POSTFIX_AFTER_EFT|NO_REENTRANT_REFRESH");
                return;
            }
            updateLimits.Invoke(physical,null);
            refreshWeight.Invoke(physical,null);
            // Native readback after OnWeightUpdated, not a fabricated movement
            // speed. No new hook and no per-frame profile polling.
            if(inertiaLogged && inertiaReadbackField!=null)
            {
                float nativeResult=(float)inertiaReadbackField.GetValue(physical);
                if(!float.IsNaN(nativeResult) && !float.IsInfinity(nativeResult))
                    Status("P49_NATIVE_INERTIA_READBACK|VALUE="+
                        nativeResult.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+
                        "|AFTER_EFT_ON_WEIGHT_UPDATED|NOT_WORLD_MPS");
            }
            Status("P35_NATIVE_WEIGHT_INITIAL_RECALC_REQUESTED|FRESH_EFT_BASE|POSTFIX_AFTER_NATIVE");
        }
        private static void ResetSession()
        {
            lock(Sync)
            {
                activeWorld=activePlayer=activeProfile=activePhysical=null;
                frozenRatio=0f;
            }
            samples.Reset(); candidateWorld=null;
            nativeRetryBudget=4;
            inertiaWarned=false; inertiaLogged=false;
            nativeEncumbranceBypassLogged=false;
        }
        private static void Disarm()
        {
            object oldPhysical=activePhysical;
            ResetSession();
            try { patch?.UnpatchSelf(); } catch { }
            patch=null;
            Status("OFF|HARMONY_UNPATCHED|NO_HP_CHANGE");
            try
            {
                if(oldPhysical!=null)
                {
                    // P35: after UnpatchSelf, restore four native EFT vectors.
                    // A notification-only call would leave our multiplied
                    // weight thresholds resident in the departing controller.
                    updateLimits?.Invoke(oldPhysical,null);
                    refreshWeight?.Invoke(oldPhysical,null);
                    Status("P35_NATIVE_WEIGHT_DISARM_RECALCULATED|EFT_ONLY");
                }
            }
            catch(Exception ex) { Block("DISARM_RECALC_"+ex.GetType().Name); }
        }
        // Optional bootstrap on the local PMC's *native* weight recalculation.
        // No Unity Update scan; never inspect or change bot Physical objects.
        // This covers a Physical instance becoming ready after raid-entry events.
        private static void TryCaptureFromNativeWeight(object instance)
        {
            if (nativeRetryBudget<=0 || owner==null || !owner.P24CarryTrialEnabled ||
                permanentlyBlocked || frozenRatio>0f || instance==null) return;
            object world=RaidLifecycleHook.CurrentWorld;
            if(world==null) return;
            object local=Read(world,"MainPlayer");
            if(local==null || !owner.P29IsCapturedLocalPlayer(local) ||
                !ReferenceEquals(Read(local,"Physical"),instance)) return;
            nativeRetryBudget--;
            try { Sample(true); }
            catch(Exception ex)
            {
                Block("P201_NATIVE_CAPTURE_"+ex.GetType().Name);
            }
        }
        private static void AfterNativeWeightRecalculation(object __instance)
        {
            try
            {
                // On-demand one-shot fallback if earlier lease events ran
                // before the local Physical was initialized.
                if(frozenRatio<=0f) TryCaptureFromNativeWeight(__instance);
                lock(Sync)
                {
                    if(frozenRatio<=0f || !ReferenceEquals(activePhysical,__instance) ||
                        !owner.P24CarryTrialEnabled) return;
                    bool bypass=(bool)bypassField.GetValue(__instance);
                    if(bypass)
                    {
                        if(!nativeEncumbranceBypassLogged)
                        {
                            nativeEncumbranceBypassLogged=true;
                            Status("P204_NATIVE_ENCUMBRANCE_BYPASS|NATIVE_FLAG_TRUE|NO_GIGAPMC_WRITE");
                        }
                        return;
                    }
                    if(nativeEncumbranceBypassLogged)
                    {
                        nativeEncumbranceBypassLogged=false;
                        Status("P204_NATIVE_ENCUMBRANCE_RESTORED|FRESH_EFT_WEIGHT_RECALC");
                    }
                    BaselineRange[] native=new BaselineRange[4];
                    for(int i=0;i<4;i++)
                    {
                        Vector2 v=(Vector2)fields[i].GetValue(__instance);
                        native[i]=new BaselineRange(v.x,v.y);
                    }
                    BaselineRange[] changed;
                    if(!AttributeCarryMath.TryApply(native,frozenRatio,false,out changed)) return;
                    // The extra P49 calculation happens on the same NATIVE
                    // recalculation event. Never touch Inventory.TotalWeight,
                    // global config or Unity Transform/CharacterController.
                    Vector3 nativeInertia=(Vector3)inertiaLimitsField.GetValue(__instance);
                    float inertiaLo,inertiaHi,inertiaThird;
                    bool inertiaValid=InertiaLimitsMath.TryScale(
                        nativeInertia.x,nativeInertia.y,nativeInertia.z,frozenRatio,
                        out inertiaLo,out inertiaHi,out inertiaThird);
                    if(!inertiaValid && !inertiaWarned)
                    {
                        inertiaWarned=true;
                        Status("P49_INERTIA_LIMITS_SKIPPED|INVALID_NATIVE_RANGE_OR_OVERFLOW|"+
                            "CARRYING_WEIGHT_FOUR_THRESHOLDS_STILL_APPLY");
                    }
                    int updated=0;
                    bool inertiaWritten=false;
                    try
                    {
                        for(;updated<4;updated++)
                            fields[updated].SetValue(__instance,new Vector2(changed[updated].Low,changed[updated].High));
                        if(inertiaValid)
                        {
                            inertiaLimitsField.SetValue(__instance,
                                new Vector3(inertiaLo,inertiaHi,inertiaThird));
                            inertiaWritten=true;
                        }
                    }
                    catch
                    {
                        // Transaction: restore ALL five independently
                        // captured native values on an unexpected write error.
                        try { inertiaLimitsField.SetValue(__instance,nativeInertia); } catch { }
                        for(int i=0;i<4;i++)
                            try { fields[i].SetValue(__instance,new Vector2(native[i].Low,native[i].High)); } catch { }
                        throw;
                    }
                    if(inertiaWritten && !inertiaLogged)
                    {
                        inertiaLogged=true;
                        Status("P49_NATIVE_INERTIA_LIMITS_APPLIED|NATIVE_LOW="+
                            nativeInertia.x.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+
                            "|NATIVE_HIGH="+nativeInertia.y.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+
                            "|APPLIED_LOW="+inertiaLo.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+
                            "|APPLIED_HIGH="+inertiaHi.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+
                            "|THIRD_NATIVE_UNCHANGED|CARRY_RATIO_FROZEN="+
                            frozenRatio.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+
                            "|LOCAL_PMC_ONLY|ON_WEIGHT_UPDATED_NEXT|NO_EXTRA_HOOK");
                    }
                    if (NativeEvidence.Record(GlobalBonusId.CarryingWeight,
                        native[1].High,changed[1].High))
                        Status("P34_REAL_NATIVE_APPLICATION|ID="+
                            AttributeGlobalBonus.Identity(GlobalBonusId.CarryingWeight)+
                            "|EFT="+native[1].High+"|AFTER="+changed[1].High+
                            "|UNITS=KG_THRESHOLD|MENU_UI_NOT_CLAIMED");
                }
            }
            catch(Exception ex) { Block("POSTFIX_"+ex.GetType().Name); }
        }
    }
}
