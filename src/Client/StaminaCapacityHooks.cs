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
    // P26: optional, experimental, LOCAL PMC in-raid-only gameplay hook.
    // Native Physical.BaseStaminaFunction(float) RETURN is preserved and a
    // separate frozen GigaPMC percentage scales that final native result.
    // Never writes to profile, HP, EFT configs, or the server. No timers.
    internal static class StaminaCapacityHooks
    {
        private const string EftPin = "EE25CEE1259777B38ED8B3E7841FDC2DB3C98540B1469FA539B1FF183476E436";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly object Gate = new object();
        private static readonly LocalTrialAuthority sampler = new LocalTrialAuthority();
        private static Plugin owner;
        private static ManualLogSource logger;
        private static Harmony harmony;
        private static MethodInfo target;
        private static Type physicalType;
        private static object candidateWorld, activeWorld, activePlayer, activeProfile, activePhysical;
        private static float candidateCapacity;
        private static decimal candidatePercent;
        private static int candidateLevel, candidatePrestige, candidateCap;
        private static float delta; // P31 frozen percentage ratio, NOT a stamina-point delta.
        private static decimal frozenPercent;
        private static int frozenLevel, frozenPrestige, frozenCap;
        private static int nextFrame;
        private static StaminaNativeRefresh.Lease p46Lease;
        private static bool permanentlyBlocked, warned, appliedLogged;

        private static void Status(string value) => logger?.LogInfo("[GigaPMC] P26_" + value);
        private static void Block(string why)
        {
            if (warned) return;
            warned = true;
            logger?.LogWarning("[GigaPMC] P26_STAMINA_BLOCKED|" + why + "|HP_UNCHANGED");
        }
        // One session initialization, after the native start callback and
        // the immutable F12 capture. The EFT postfix runs on native recalcs.
        internal static void ArmOnce(Plugin source, ManualLogSource log)
        {
            if(source==null) return;
            owner=source;logger=log;
            if(!source.P26StaminaTrialEnabled || permanentlyBlocked) return;
            if(harmony==null && !Install()) return;
            try { Sample(); }
            catch(Exception ex)
            {
                ResetSession();
                Block("ONE_SHOT_"+ex.GetType().Name);
            }
        }
        internal static void OnRaidEnded()
        {
            if(harmony!=null) Disarm();
            else ResetSession();
        }
        private static Type FindUnique(string name)
        {
            Type[] types = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType(name,false)).Where(t => t != null).ToArray();
            if (types.Length != 1) throw new TypeLoadException("EFT_UNIQUE_TYPE_REQUIRED_" + name);
            return types[0];
        }
        private static bool Install()
        {
            try
            {
                if (owner == null || !owner.P26StaminaTrialEnabled)
                    throw new InvalidOperationException("EXPLICIT_OPT_IN_REQUIRED");
                // No runtime disk hash. Loaded-code method ABI + Harmony owner
                // are the runtime gates; the offline runner pins EFT SHA256.
                physicalType = FindUnique("Physical");
                if (physicalType.Assembly.GetName().Name != "Assembly-CSharp")
                    throw new TypeLoadException("PHYSICAL_ASSEMBLY_MISMATCH");
                target = UniqueMethodContract.Resolve(physicalType,
                    "BaseStaminaFunction",typeof(float),typeof(float));
                var method = new HarmonyMethod(typeof(StaminaCapacityHooks), nameof(AfterNativeStamina));
                method.priority = Priority.Last;
                var patch = new Harmony("com.vargor.gigapmc.p26.experimental.stamina");
                harmony = patch; // Publish first so any patch exception can be unpatched.
                patch.Patch(target, postfix: method);
                HarmonyAudit.Require(target,"com.vargor.gigapmc.p26.experimental.stamina",
                    nameof(AfterNativeStamina),"postfix");
                Status("P38_LIVE_ABI_STAMINA|RUNTIME_TOKEN="+target.MetadataToken.ToString("X8")+"|UNIQUE_SIGNATURE");
                Status("P36_HARMONY_OWNER_REGISTERED|TOKEN=060034C8|ID=body-stamina");
                Status("STAMINA_ARMED|WAIT_FOR_LOCAL_PMC_AND_THREE_SAMPLES|NO_HP_MUTATION");
                return true;
            }
            catch (Exception ex)
            {
                permanentlyBlocked = true;
                Block("ABI_OR_HOOK_" + ex.GetType().Name + "_" + ex.Message);
                try { harmony?.UnpatchSelf(); } catch { }
                harmony = null;
                return false;
            }
        }
        private static object Read(object obj,string name) => AttributeMemberReader.Read(obj,name);
        private static bool GetLocal(out object world,out object player,out object profile,out object physical)
        {
            world = player = profile = physical = null;
            world = RaidLifecycleHook.CurrentWorld;
            if (world == null) return false;
            player = Read(world,"MainPlayer");
            if (player == null || (Read(player,"IsAI") is bool ai && ai)) return false;
            bool eftPlayer = false;
            for (Type p=player.GetType(); p!=null; p=p.BaseType)
                if (p.FullName == "EFT.Player" && p.Assembly.GetName().Name == "Assembly-CSharp")
                    eftPlayer = true;
            if (!eftPlayer) return false;
            profile = Read(player,"Profile");
            if (profile == null || profile.GetType().FullName != "EFT.Profile") return false;
            object info = Read(profile,"Info");
            string side = (Read(profile,"Side") ?? Read(info,"Side"))?.ToString();
            if (!string.Equals(side,"Bear",StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(side,"Usec",StringComparison.OrdinalIgnoreCase)) return false;
            if (owner == null || !owner.P29IsCapturedLocalPlayer(player)) return false;
            physical = Read(player,"Physical");
            return physical != null && physicalType.IsInstanceOfType(physical);
        }
        private static int Whole(object value,int min,int max)
        {
            if (value == null) throw new InvalidDataException("MISSING_PROFILE_LEVEL");
            double d = Convert.ToDouble(value,System.Globalization.CultureInfo.InvariantCulture);
            if (double.IsNaN(d) || double.IsInfinity(d) || d != Math.Truncate(d) || d < min || d > max)
                throw new InvalidDataException("INVALID_PROFILE_LEVEL");
            return checked((int)d);
        }
        // Server experience-table cap is stable for this game process.
        // Never read a file on each half-second raid sample (anti-virus I/O stall).
        private static readonly Lazy<int?> P40CachedServerCap = new Lazy<int?>(() =>
        { string path = Path.Combine(Paths.GameRootPath,"SPT_Runtime","user","mods","GigaPMC","config","server-level-cap.txt");
            int value;
            return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(),out value) ? (int?)value : null; });
        private static int? Cap() => P40CachedServerCap.Value;
        private static void Sample()
        {
            object world, player, profile, physical;
            if (!GetLocal(out world,out player,out profile,out physical))
            { ResetSession(); return; }
            object info=Read(profile,"Info");
            int level=Whole(Read(info,"Level"),1,10000);
            object prestigeValue=Read(profile,"PrestigeLevel") ?? Read(info,"PrestigeLevel");
            int prestige=prestigeValue==null?0:Whole(prestigeValue,0,10000);
            int cap=owner.CurrentRaidSettings().ResolveCap(Cap());
            decimal percent=owner.P09BonusRates(AttributeBonusKind.BodyStamina)
                .TotalPercent(owner.P09GlobalEnabled,level,prestige,cap);
            if(percent<=0m || percent>100000m) { ResetSession(); return; }
            lock(Gate)
            {
                if(delta>0f && ReferenceEquals(world,activeWorld) &&
                    ReferenceEquals(player,activePlayer) &&
                    ReferenceEquals(profile,activeProfile) &&
                    ReferenceEquals(physical,activePhysical) &&
                    frozenPercent==percent && frozenLevel==level &&
                    frozenPrestige==prestige && frozenCap==cap) return;
                if(delta>0f) ResetSession();
            }
            // A single verified lease replaces the repeated three-sample
            // profile scan. The immutable owner identity has already passed.
            if(percent<=0m || percent>100000m) return;
            // EFT BaseStaminaFunction returns the final native calculation.
            // Do NOT reject the raid because SkillsDescriptor/skill-progress
            // readers or global stamina baseline introspection are missing.
            float rate=(float)(percent/100m);
            if(float.IsNaN(rate) || float.IsInfinity(rate) || rate<=0f || rate>1000f)
            { ResetSession(); Block("P34_INVALID_FROZEN_PERCENT"); return; }
            // P46: capture the existing native cached stamina capacity BEFORE
            // enabling the P26 postfix ratio. Otherwise a stale TotalCapacity
            // can prevent BaseStaminaFunction from executing after raid start.
            StaminaNativeRefresh.Lease refresh;
            string why;
            if(!StaminaNativeRefresh.TryCapture(physical,physicalType,out refresh,out why))
            {
                ResetSession();
                Block("P46_NATIVE_CAPTURE_"+why);
                return;
            }
            lock(Gate)
            {
                activeWorld=world;activePlayer=player;
                activeProfile=profile;activePhysical=physical;
                frozenPercent=percent;frozenLevel=level;frozenPrestige=prestige;frozenCap=cap;
                delta=rate;appliedLogged=false;
            }
            Status("STAMINA_SNAPSHOT_FROZEN|SKILL_DESCRIPTOR_NOT_REQUIRED"+
                "|BONUS_PERCENT="+percent+"|FROZEN_RATIO="+rate+
                "|ID="+AttributeGlobalBonus.Identity(GlobalBonusId.BodyStamina)+
                "|P46_NATIVE_DIRTY_RECALC_NEXT|HP_UNCHANGED");
            if(!StaminaNativeRefresh.TryActivate(refresh,rate,out why))
            {
                // Disable only Body Stamina and recompute native capacity with
                // the GigaPMC ratio cleared, never leave a cached boosted max.
                ResetSession();
                string restoreWhy;
                if(!StaminaNativeRefresh.TryRestoreNative(refresh,out restoreWhy))
                    Block("P46_RESTORE_AFTER_FAILURE_"+restoreWhy);
                else Block("P46_NATIVE_REFRESH_"+why);
                return;
            }
            p46Lease=refresh;
            Status("P46_NATIVE_CAPACITY_REFRESH_APPLIED|"+
                StaminaNativeRefresh.Detail(refresh)+
                "|ONE_SHOT|NO_STACK|LOCAL_PMC|F12_FROZEN");
        }
        private static void ResetSession()
        {
            lock (Gate)
            {
                delta = 0f;
                activeWorld = activePlayer = activeProfile = activePhysical = null;
                frozenPercent = 0m; frozenLevel = frozenPrestige = frozenCap = 0;
                appliedLogged = false;
                p46Lease = null;
            }
            candidateWorld = null;
            candidateCapacity = 0f; candidatePercent = 0m;
            candidateLevel = candidatePrestige = candidateCap = 0;
            sampler.Reset();
        }
        private static void Disarm()
        {
            var lease=p46Lease;
            ResetSession();
            try { harmony?.UnpatchSelf(); } catch { }
            harmony = null;
            if(lease!=null)
            {
                string reason;
                if(StaminaNativeRefresh.TryRestoreNative(lease,out reason))
                    Status("P46_NATIVE_CAPACITY_RESTORED|ONE_SHOT|OUT_OF_RAID");
                else
                    Block("P46_RAID_EXIT_NATIVE_RESTORE_"+reason);
            }
            Status("STAMINA_OFF|UNPATCHED|NO_HP_CHANGE");
        }
        private static void AfterNativeStamina(object __instance, ref float __result)
        {
            try
            {
                lock (Gate)
                {
                    if (owner == null || !owner.P26StaminaTrialEnabled || delta <= 0f ||
                        !ReferenceEquals(activePhysical,__instance)) return;
                    float newValue;
                    if (!AttributeStaminaMath.TryApply(__result,delta,out newValue) || newValue <= __result) return;
                    float eftNative=__result;
                    __result = newValue;
                    NativeEvidence.Record(GlobalBonusId.BodyStamina,eftNative,newValue);
                    if (!appliedLogged)
                    {
                        appliedLogged = true;
                        Status("P31_FINAL_EFT_STAMINA_MULTIPLIED|ID=" +
                            AttributeGlobalBonus.Identity(GlobalBonusId.BodyStamina) +
                            "|ORIGINAL_NATIVE_PRESERVED|HP_UNCHANGED");
                    }
                }
            }
            catch (Exception ex) { Block("POSTFIX_"+ex.GetType().Name); }
        }
    }
}
