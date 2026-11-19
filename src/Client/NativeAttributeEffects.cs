using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace GigaPMC.Client
{
    // Captures raid-frozen bonuses and owns the ADS, melee and fall hooks.
    // CharacterMovementBridge exclusively applies the movement bonus.
    // Historical log keys, Harmony IDs and reset state remain compatible with
    // the verified release; retired movement installers are not present.
    internal static class NativeAttributeEffects
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static Plugin owner;
        private static ManualLogSource log;
        private static Harmony adsHarmony, meleeHarmony, movementHarmony, sprintAccelerationHarmony, walkClampHarmony, walkMotionHarmony, fallGuardHarmony, sprintLimitHarmony;
        private static bool adsBlocked, meleeBlocked, movementBlocked, walkClampBlocked, sprintLimitBlocked;
        private static bool installationAudited;
        private const string AdsId = "com.vargor.gigapmc.p36.ads";
        private const string MeleeId = "com.vargor.gigapmc.p36.melee";
        private const string FallGuardId = "com.vargor.gigapmc.p50r2.movement-impact-protection";
        private static MethodInfo handsProcess, movementGetter, meleeProcess, sprintAccelerationMethod, walkTargetMethod;
        private static bool movementGetterWarned;
        // R2: target follows the 4.1.x managed movement state pathway.
        private static FieldInfo staminaCurrent;
        private static object localPlayer, localProfile, localHands, localMovement;
        private static readonly decimal[] percents = new decimal[7];
        private static bool blocked, warnedAds, warnedMovement, loggedElite;
        // One broken bonus calculation must not invalidate the others.
        // Captured rates still come exclusively from the immutable raid lease.
        private static bool p201RateRetryNeeded;
        private static readonly bool[] loggedMovementKinds = new bool[7];
        private static int appliedAds, appliedMelee;
        private static int p44LoggedMovementEndpoints;
        private static bool p45SprintAccelLogged;
        private static bool p47WalkTargetLogged;
        private static bool p48NativeTargetLogged;
        private static bool p50WalkMotionBlocked, p50WalkMotionRuntimeDisabled, p50WalkMotionLogged;
        private static MethodInfo p50WalkMotionMethod, p50SprintEnabledGetter, p50CurrentStateGetter;
        private static CharacterController p50R6CharacterController;
        private static bool p50R6ControllerAttempted;
        private static bool p50R9LimitLogged;
        private static bool p50FallGuardBlocked, p50FallGuardLogged;
        private static MethodInfo p50FallPlayerGetter;
        private static FieldInfo p50FallSafeHeightField;
        private static Type p50MovePlayerState, p50SprintPlayerState;
        private static FieldInfo p50MovementStateContext;
        // Cache nonlocal EFT movement states: a crowd of bots should not pay
        // Reflection.GetValue each movement update; resolved once per state.
        private sealed class MovementStateIdentity : IEqualityComparer<object>
        {
            public new bool Equals(object a,object b) => ReferenceEquals(a,b);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
        private static readonly HashSet<object> p47OtherMovementStates =
            new HashSet<object>(new MovementStateIdentity());
        private static object previousProfile;
        private static int previousLevel,previousPrestige;
        private static string previousEndpointReadiness;

        internal static bool NeedsNativeRetry => owner!=null && owner.P27EffectsTrialEnabled &&
            !blocked && (localPlayer==null || p201RateRetryNeeded ||
            (localHands==null && !adsBlocked) ||
            (localMovement==null && !movementBlocked));
        private static void Log(string message) => log?.LogInfo("[GigaPMC] P27_" + message);
        // No Update-driven sampling. Called once immediately after F12 lease
        // capture for this PMC. Missing endpoints stay safely disabled.
        internal static void ArmOnce(Plugin plugin, ManualLogSource logger)
        {
            owner=plugin;log=logger;
            if(plugin==null || !plugin.P27EffectsTrialEnabled || blocked) return;
            try
            {
                if((!adsBlocked && adsHarmony==null) ||
                   (!meleeBlocked && meleeHarmony==null) ||
                   (!movementBlocked && movementHarmony==null) ||
                   (!walkClampBlocked && walkClampHarmony==null) ||
                   (!p50WalkMotionBlocked && walkMotionHarmony==null) ||
                   (!sprintLimitBlocked && sprintLimitHarmony==null) ||
                   (!p50FallGuardBlocked && fallGuardHarmony==null)) InstallIndependent();
                if(blocked) return;
                SampleProfile();
                if(localPlayer==null)
                {
                    Log("P201_OTHER_EFFECTS_PENDING|LOCAL_PMC_NOT_READY|NO_FALSE_APPLIED");
                    return;
                }
                // P51 movement bridge is independent: an exception there must
                // not erase successfully sampled ADS and melee percentages.
                if(localMovement!=null && !movementBlocked)
                {
                    try
                    {
                        CharacterMovementBridge.Arm(localPlayer,localMovement,percents[2],log,
                            owner.RaidJumpScalingEnabled,owner.RaidJumpScalingStrength);
                    }
                    catch(Exception ex)
                    {
                        try { CharacterMovementBridge.Disarm(); } catch { }
                        Log("P201_MOVEMENT_BRIDGE_FAILED|"+ex.GetType().Name+
                            "|ADS_MELEE_SNAPSHOTS_PRESERVED");
                    }
                }
                Log("P201_EFFECT_PERCENT_CAPTURED|F12_LEASE|ADS="+percents[0]+
                    "|MELEE="+percents[1]+"|MOVEMENT="+percents[2]+
                    "|RATE_RETRY_PENDING="+p201RateRetryNeeded+
                    "|NOT_NATIVE_APPLICATION_PROOF");
            }
            catch(Exception ex)
            {
                ClearSample();
                Log("P40_R2_SNAPSHOT_UNAVAILABLE|"+ex.GetType().Name+
                    "|NO_RETRY_POLL|HOOKS_UNAFFECTED");
            }
        }
        internal static void OnRaidEnded() { Disarm(); }
        private static Type UniqueEft(string name)
        {
            var all = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name,false))
                .Where(t => t != null).ToArray();
            if (all.Length != 1 || all[0].Assembly.GetName().Name != "Assembly-CSharp")
                throw new TypeLoadException("EFT_TYPE_UNIQUE_ABI_" + name);
            return all[0];
        }
        private static MethodInfo Find(Type type,string name,int token,Type returnType,params Type[] parameters)
        {
            return UniqueMethodContract.Resolve(type,name,returnType,parameters);
        }
        // P36: a bad/changed ProcessHit IL must never disable ADS and Movement.
        // Each endpoint has its OWN Harmony owner, failure flag and readback.
        // A successful offline build is not a claim that any endpoint executed.
        private static void InstallIndependent()
        {
            if (!adsBlocked && adsHarmony == null) InstallAds();
            if (!meleeBlocked && meleeHarmony == null) InstallMelee();
            if (!p50FallGuardBlocked && fallGuardHarmony == null) InstallFallGuard();
            // Resolve for SampleProfile's identity check, without patching a getter.
            try
            {
                movementGetter=UniqueMethodContract.Resolve(UniqueEft("EFT.MovementContext"),
                    "get_ClampedSpeed",typeof(float));
            }
            catch(Exception ex)
            {
                // Movement unavailable must not prevent ADS/Melee snapshots.
                movementGetter=null;
                // Do not make temporary readiness failures permanent. Attempts
                // are bounded by the native raid lifecycle (never Update).
                if(!movementGetterWarned)
                {
                    movementGetterWarned=true;
                    Log("P201_MOVEMENT_GETTER_UNAVAILABLE|"+ex.GetType().Name+"|ADS_MELEE_INDEPENDENT");
                }
            }
        }
        private static void InstallAds()
        {
            Harmony attempt = null;
            try
            {
                Type stamina = UniqueEft("Stamina");
                handsProcess = Find(stamina,"Process",0x060034F1,typeof(void),typeof(float));
                staminaCurrent = stamina.GetField("Current",Any|BindingFlags.DeclaredOnly);
                if (staminaCurrent == null || 
                    staminaCurrent.FieldType != typeof(float) || staminaCurrent.IsStatic)
                    throw new MissingFieldException("HANDS_STAMINA_CURRENT_ABI_DRIFT");
                attempt = new Harmony(AdsId);
                var after = new HarmonyMethod(typeof(NativeAttributeEffects),nameof(HandsAfter));
                after.priority = Priority.Last;
                attempt.Patch(handsProcess,
                    prefix:new HarmonyMethod(typeof(NativeAttributeEffects),nameof(HandsBefore)),postfix:after);
                HarmonyAudit.Require(handsProcess,AdsId,nameof(HandsBefore),"prefix");
                HarmonyAudit.Require(handsProcess,AdsId,nameof(HandsAfter),"postfix");
                adsHarmony = attempt;
                Log("P38_LIVE_ABI_ADS|TOKEN="+handsProcess.MetadataToken.ToString("X8")+"|UNIQUE_SIGNATURE");
                Log("P36_ADS_HARMONY_REGISTERED|TOKEN=060034F1|OWNER="+AdsId);
            }
            catch(Exception ex)
            {
                try { attempt?.UnpatchSelf(); } catch { }
                adsBlocked = true;
                Log("P36_ADS_HOOK_BLOCKED|"+ex.GetType().Name+"|"+ex.Message+
                    "|MELEE_AND_MOVEMENT_UNAFFECTED");
            }
        }
        private static void InstallMelee()
        {
            Harmony attempt = null;
            try
            {
                Type knife = UniqueEft("EFT.Player+BaseKnifeController");
                Type raycast = UniqueEft("EFT.Player+KnifeRaycastHit");
                var hits = knife.GetMethods(Any|BindingFlags.DeclaredOnly)
                    .Where(m=>m.Name=="ProcessHit" &&
                        m.GetParameters().Length==2 &&
                        m.GetParameters()[0].ParameterType==raycast).ToArray();
                if(hits.Length!=1) throw new MissingMethodException("MELEE_PROCESS_HIT_SIGNATURE_DRIFT");
                meleeProcess=hits[0];
                Type damageInfo=UniqueEft("EFT.Ballistics.DamageInfo");
                var damage=damageInfo.GetField("Damage",Any|BindingFlags.DeclaredOnly);
                if(damage==null || damage.FieldType!=typeof(float))
                    throw new MissingFieldException("MELEE_DAMAGE_FIELD_ABI_DRIFT");
                attempt = new Harmony(MeleeId);
                var lateMelee=new HarmonyMethod(typeof(NativeAttributeEffects),nameof(MeleeTranspiler));
                lateMelee.priority=Priority.Last;
                attempt.Patch(meleeProcess,transpiler:lateMelee);
                HarmonyAudit.Require(meleeProcess,MeleeId,nameof(MeleeTranspiler),"transpiler");
                meleeHarmony=attempt;
                Log("P38_LIVE_ABI_MELEE|TOKEN="+meleeProcess.MetadataToken.ToString("X8")+"|IL_ANCHOR_VERIFIED");
                Log("P36_MELEE_HARMONY_REGISTERED|TOKEN=06012543|OWNER="+MeleeId);
            }
            catch(Exception ex)
            {
                try { attempt?.UnpatchSelf(); } catch { }
                meleeBlocked=true;
                Log("P36_MELEE_HOOK_BLOCKED|"+ex.GetType().Name+"|"+ex.Message+
                    "|ADS_AND_MOVEMENT_UNAFFECTED");
            }
        }
        // P50 R2: separate, opt-in-by-existing-Movement-Speed-only safeguard.
        // Use the local PMC's already frozen Movement Speed %, native
        // _fallSafeHeight and EFT's own HandleFall(float) calculation. Never
        // suppress the entire method or alter bullet/explosion/melee damage.
        private static void InstallFallGuard()
        {
            Harmony attempt=null;
            try
            {
                var health=UniqueEft("EFT.HealthSystem.ActiveHealthController");
                var native=UniqueMethodContract.Resolve(health,
                    "HandleFall",typeof(float),typeof(float));
                var parms=native.GetParameters();
                if(parms.Length!=1 || parms[0].Name!="height")
                    throw new MissingMethodException("P50_R2_NATIVE_FALL_PARAMETER_DRIFT");
                // The reference mod uses ActiveHealthController.Player and
                // ActiveHealthController._fallSafeHeight. Both are required.
                var playerProp=health.GetProperty("Player",Any);
                var playerGetter=playerProp?.GetGetMethod(true);
                if(playerGetter==null || playerGetter.IsStatic ||
                   playerGetter.GetParameters().Length!=0)
                    throw new MissingMemberException("P50_R2_HEALTH_OWNER_GETTER_DRIFT");
                FieldInfo safe=null;
                for(Type t=health;t!=null;t=t.BaseType)
                {
                    var candidate=t.GetField("_fallSafeHeight",Any|BindingFlags.DeclaredOnly);
                    if(candidate==null) continue;
                    if(safe!=null) throw new AmbiguousMatchException("P50_R2_FALL_SAFE_HEIGHT_AMBIGUOUS");
                    safe=candidate;
                }
                if(safe==null || safe.IsStatic || safe.FieldType!=typeof(float))
                    throw new MissingFieldException("P50_R2_FALL_SAFE_HEIGHT_DRIFT");
                // Native prefix only alters the height ARGUMENT for a tiny,
                // verified excess over EFT's current no-damage threshold.
                // Full falls preserve the original argument and method.
                attempt=new Harmony(FallGuardId);
                var before=new HarmonyMethod(typeof(NativeAttributeEffects),
                    nameof(P50R2FallHeightPrefix));
                before.priority=Priority.Last;
                attempt.Patch(native,prefix:before);
                HarmonyAudit.Require(native,FallGuardId,
                    nameof(P50R2FallHeightPrefix),"prefix");
                p50FallPlayerGetter=playerGetter;
                p50FallSafeHeightField=safe;
                fallGuardHarmony=attempt;
                Log("P50_R2_FALL_GUARD_REGISTERED|TOKEN="+
                    native.MetadataToken.ToString("X8")+
                    "|NATIVE_HANDLE_FALL_HEIGHT|NATIVE_SAFE_HEIGHT|LOCAL_PMC|OWNER="+FallGuardId);
            }
            catch(Exception ex)
            {
                try { attempt?.UnpatchSelf(); } catch { }
                p50FallGuardBlocked=true;
                Log("P50_R2_FALL_GUARD_BLOCKED|"+ex.GetType().Name+"|"+ex.Message+
                    "|ALL_OTHER_BONUSES_UNAFFECTED|NO_GLOBAL_FALL_IMMUNITY");
            }
        }
        private static void P50R2FallHeightPrefix(object __instance,ref float height)
        {
            if(fallGuardHarmony==null || p50FallGuardBlocked || owner==null ||
               !owner.P27EffectsTrialEnabled || localPlayer==null ||
               percents[2]<=0m || __instance==null ||
               p50FallPlayerGetter==null || p50FallSafeHeightField==null) return;
            float originalHeight=height;
            try
            {
                if(!owner.P29IsCapturedLocalPlayer(localPlayer) ||
                   !ReferenceEquals(p50FallPlayerGetter.Invoke(__instance,null),localPlayer))
                    return;
                float safe=(float)p50FallSafeHeightField.GetValue(__instance);
                float adjusted,reduction;
                if(!ImpactProtectionMath.TryAttenuateHeight(height,safe,
                    percents[2],out adjusted,out reduction)) return;
                float original=height;
                height=adjusted; // EFT's existing fall-damage path still runs.
                if(!p50FallGuardLogged)
                {
                    p50FallGuardLogged=true;
                    Log("P50_R2_MICRO_FALL_ATTENUATED|NATIVE_HEIGHT="+
                        original.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+
                        "|SAFE_HEIGHT="+safe.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+
                        "|EFT_INPUT_HEIGHT="+adjusted.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+
                        "|REDUCTION="+reduction.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+
                        "|FROZEN_MOVEMENT_PERCENT="+percents[2].ToString(System.Globalization.CultureInfo.InvariantCulture)+
                        "|LOCAL_PMC|LARGE_FALLS_VANILLA");
                }
            }
            catch(Exception ex)
            {
                // Never abort EFT native falling logic on a guard failure.
                height=originalHeight;
                if(!p50FallGuardBlocked)
                {
                    p50FallGuardBlocked=true;
                    log?.LogWarning("[GigaPMC] P50_R2_FALL_GUARD_RUNTIME_BLOCKED|"+
                        ex.GetType().Name+"|VANILLA_FALL_DAMAGE_RETAINED");
                }
            }
        }

        private static object Read(object source,string key)
            => source==null ? null : AttributeMemberReader.Read(source,key);
        private static int? ReadServerLevelCap() => ServerLevelCapCache.Read();
        private static void SampleProfile()
        {
            var world=RaidLifecycleHook.CurrentWorld;
            object player=Read(world,"MainPlayer");
            object profile=Read(player,"Profile");
            if (world == null || player == null || profile == null ||
                (Read(player,"IsAI") is bool ai && ai) || profile.GetType().FullName!="EFT.Profile")
            { ClearSample(); return; }
            if (owner == null || !owner.P29IsCapturedLocalPlayer(player))
            { ClearSample(); return; }
            bool validPlayer=false;
            for(Type type=player.GetType();type!=null;type=type.BaseType)
                if(type.FullName=="EFT.Player" && type.Assembly.GetName().Name=="Assembly-CSharp")
                    validPlayer=true;
            var info=Read(profile,"Info");
            string side=(Read(profile,"Side")??Read(info,"Side"))?.ToString();
            if(!validPlayer || (side!="Usec" && side!="Bear")) { ClearSample(); return; }
            object physical=Read(player,"Physical");
            object hands=Read(physical,"HandsStamina");
            // DrakiaXYZ SetSpeed 1.5.0 (SPT 4.1.x) reads this from
            // Player.CurrentManagedState.MovementContext. Do not assume
            // Player.MovementContext exists on SPT 4.1.6.
            object managedState=Read(player,"CurrentManagedState");
            object movement=Read(managedState,"MovementContext");
            if(hands==null || staminaCurrent==null ||
                !staminaCurrent.DeclaringType.IsInstanceOfType(hands)) hands=null;
            if(movement==null || movementGetter==null ||
                !movementGetter.DeclaringType.IsInstanceOfType(movement)) movement=null;
            // P31 final-EFT percent needs level/prestige only. The unrelated
            // SkillsDescriptor may be absent or obfuscated in SPT 4.1.6.
            // One missing hands/movement target must not suppress MELEE.
            object levelObject=Read(info,"Level");
            object prestigeObject=Read(profile,"PrestigeLevel")??Read(info,"PrestigeLevel");
            int level=checked(Convert.ToInt32(levelObject));
            int prestige=prestigeObject==null?0:checked(Convert.ToInt32(prestigeObject));
            if(level<1 || level>10000 || prestige<0 || prestige>10000)
            { ClearSample(); return; }
            // Each rate has its own cap policy and failure boundary. For
            // example, malformed ADS configuration must not suppress melee.
            // The server cap is read once on this authenticated native event.
            var hpSettings=owner.CurrentRaidSettings();
            int? detectedCap=ReadServerLevelCap();
            var kinds=new[]{AttributeBonusKind.AdsEndurance,
                AttributeBonusKind.MeleeDamage,AttributeBonusKind.SprintSpeed};
            var computed=new decimal[7]; // legacy native-hook offsets
            bool retryRate=false;
            for(int i=0;i<3;i++)
            {
                try
                {
                    var rate=owner.P09BonusRates(kinds[i]);
                    int cap=AttributeCapPolicy.Resolve(hpSettings,detectedCap,rate);
                    decimal value=rate.TotalPercent(owner.P09GlobalEnabled,level,prestige,cap);
                    if(value<0m || value>10000m)
                        throw new InvalidDataException("P201_PERCENT_OUT_OF_RANGE");
                    computed[i]=value;
                }
                catch(Exception ex)
                {
                    retryRate=true;
                    computed[i]=0m; // Fail closed for this effect only.
                    Log("P201_RATE_UNAVAILABLE|KIND="+kinds[i]+"|"+
                        ex.GetType().Name+"|OTHER_EFFECTS_CONTINUE");
                }
            }
            for(int i=3;i<7;i++) computed[i]=computed[2];
            bool drift=!ReferenceEquals(previousProfile,profile) ||
                previousLevel!=level || previousPrestige!=prestige;
            previousProfile=profile;
            previousLevel=level;previousPrestige=prestige;
            localPlayer=player;localProfile=profile;localHands=hands;localMovement=movement;
            p201RateRetryNeeded=retryRate;
            Array.Copy(computed,percents,7);
            string readiness="ADS="+(hands!=null?"NATIVE_OBJECT":"NOT_RESOLVED")+
                "|MOVEMENT="+(movement!=null?"NATIVE_OBJECT":"NOT_RESOLVED")+
                "|MELEE_PLAYER="+(player!=null?"CAPTURED":"NO_PLAYER");
            if(!string.Equals(previousEndpointReadiness,readiness,StringComparison.Ordinal))
            {
                previousEndpointReadiness=readiness;
                Log("P36_ENDPOINT_READINESS|"+readiness+"|NO_GAMEPLAY_PROOF");
            }
            // No recomputation is permitted during this raid. EFT may still
            // update its own native values; our fixed multipliers are last.
        }
        private static void ClearSample()
        {
            CharacterMovementBridge.Disarm();
            localPlayer=localProfile=localHands=localMovement=null;
            previousProfile=null;
            p201RateRetryNeeded=false;
            Array.Clear(percents,0,7);
        }
        private static void Disarm()
        {
            ClearSample();
            p44LoggedMovementEndpoints=0;
            p45SprintAccelLogged=false;
            p47WalkTargetLogged=false;
            p48NativeTargetLogged=false;
            p50WalkMotionLogged=false;
            p50WalkMotionRuntimeDisabled=false;
            p50R6CharacterController=null;
            p50R6ControllerAttempted=false;
            NativeReadOnlyProbe.Reset();
            SprintReadOnlyProbe.Reset();
            p50R9LimitLogged=false;
            p50FallGuardLogged=false;
            p47OtherMovementStates.Clear();
            MovementEndpointEvidence.Reset();
            try { adsHarmony?.UnpatchSelf(); } catch(Exception ex)
            { log?.LogError("[GigaPMC] P36_ADS_UNPATCH_ERROR|"+ex.GetType().Name); }
            try { meleeHarmony?.UnpatchSelf(); } catch(Exception ex)
            { log?.LogError("[GigaPMC] P36_MELEE_UNPATCH_ERROR|"+ex.GetType().Name); }
            try { movementHarmony?.UnpatchSelf(); } catch(Exception ex)
            { log?.LogError("[GigaPMC] P36_MOVEMENT_UNPATCH_ERROR|"+ex.GetType().Name); }
            try { sprintAccelerationHarmony?.UnpatchSelf(); } catch(Exception ex)
            { log?.LogError("[GigaPMC] P45_SPRINT_ACCEL_UNPATCH_ERROR|"+ex.GetType().Name); }
            try { walkClampHarmony?.UnpatchSelf(); } catch(Exception ex)
            { log?.LogError("[GigaPMC] P47_WALK_TARGET_UNPATCH_ERROR|"+ex.GetType().Name); }
            try { walkMotionHarmony?.UnpatchSelf(); } catch(Exception ex)
            { log?.LogError("[GigaPMC] P50_WALK_MOTION_UNPATCH_ERROR|"+ex.GetType().Name); }
            try { sprintLimitHarmony?.UnpatchSelf(); } catch(Exception ex)
            { log?.LogError("[GigaPMC] P50_R9_SPRINT_LIMIT_UNPATCH_ERROR|"+ex.GetType().Name); }
            try { fallGuardHarmony?.UnpatchSelf(); } catch(Exception ex)
            { log?.LogError("[GigaPMC] P50_R2_FALL_GUARD_UNPATCH_ERROR|"+ex.GetType().Name); }
            adsHarmony=meleeHarmony=movementHarmony=sprintAccelerationHarmony=walkClampHarmony=walkMotionHarmony=fallGuardHarmony=sprintLimitHarmony=null;
            p50FallPlayerGetter=null;
            p50FallSafeHeightField=null;
            p50MovementStateContext=null;
            p50CurrentStateGetter=null;
            p50SprintEnabledGetter=null;
            p50MovePlayerState=null;
            p50SprintPlayerState=null;
            p50WalkMotionMethod=null;
            installationAudited=false;
        }
        // QuickAim v1.0 exposed get_IsAiming; local SPT 4.1.6 ABI independently
        // identifies EFT.Player.AbstractHandsController::get_IsAiming at 060125B4.
        // Read only the captured local PMC's CURRENT hands controller, never
        // another player's arms or a cached bool that can go stale in-raid.
        private static bool? GetLocalHandsAiming()
        {
            if (owner == null || localPlayer == null || !owner.P29IsCapturedLocalPlayer(localPlayer))
                return null;
            object controller = Read(localPlayer, "HandsController");
            if (controller == null) return null;
            object state = Read(controller, "IsAiming");
            return state is bool value ? (bool?)value : null;
        }
        private static void HandsBefore(object __instance,ref float __state)
        {
            __state=float.NaN;
            if(owner==null || !owner.P27EffectsTrialEnabled || !ReferenceEquals(localHands,__instance) ||
                percents[0]<=0m) return;
            try
            {
                if (!AttributeGameplayGuards.ConfirmedAds(GetLocalHandsAiming(), true)) return;
                __state=(float)staminaCurrent.GetValue(__instance);
            }
            catch { /* Never abort native stamina processing. */ }
        }
        private static void HandsAfter(object __instance,float __state)
        {
            if(float.IsNaN(__state) || owner==null || !owner.P27EffectsTrialEnabled ||
                !ReferenceEquals(localHands,__instance)) return;
            try
            {
                if (!AttributeGameplayGuards.ConfirmedAds(true, GetLocalHandsAiming())) return;
                float native=(float)staminaCurrent.GetValue(__instance);
                float adjusted;
                if(AttributeGlobalBonus.TryReduceFinalDrain(GlobalBonusId.AdsEndurance,
                    __state,native,percents[0],out adjusted) && adjusted>native)
                {
                    staminaCurrent.SetValue(__instance,adjusted);
                    NativeEvidence.Record(GlobalBonusId.AdsEndurance,
                        __state-native,__state-adjusted);
                    if(++appliedAds==1) Log("P31_ADS_FINAL_EFT_DRAIN|ID="+AttributeGlobalBonus.Identity(GlobalBonusId.AdsEndurance)+
                        "|EXPERIMENTAL|NOT_RUNTIME_CERTIFIED");
                }
            }
            catch(Exception ex)
            { if(!warnedAds){warnedAds=true;log?.LogWarning("[GigaPMC] P27_ADS_POSTFIX_FAIL|"+ex.GetType().Name);} }
        }
        // P31: the exact native IL calculates damage * native Strength first.
        // Insert AFTER its final multiplication and BEFORE DamageInfo.Damage
        // store, so our distinct GigaPMC modifier is LAST for this EFT path.
        // This does not claim an API to append arbitrary EFT-global buffs.
        private static IEnumerable<CodeInstruction> MeleeTranspiler(IEnumerable<CodeInstruction> stream)
        {
            var il = stream.ToList();
            var damageInfo=UniqueEft("EFT.Ballistics.DamageInfo");
            var damage=damageInfo.GetField("Damage",Any|BindingFlags.DeclaredOnly);
            var hook=AccessTools.Method(typeof(NativeAttributeEffects),nameof(MeleeFinalOutput));
            if (damage==null || hook==null) throw new MissingFieldException("P31_MELEE_NATIVE_FINAL_ANCHOR_MISSING");
            var stores=Enumerable.Range(0,il.Count).Where(i=>il[i].opcode==OpCodes.Stfld &&
                il[i].operand is FieldInfo f && f.MetadataToken==damage.MetadataToken).ToArray();
            if (stores.Length!=1) throw new InvalidOperationException("P31_MELEE_DAMAGE_STORES_"+stores.Length);
            int store=stores[0];
            if (store<4 ||
                !(il[store-3].operand is MethodInfo op) ||
                !(il[store-4].operand is FieldInfo buff) ||
                !MeleeIlContract.IsNativeStrengthTail(buff,op,
                    il[store-2].opcode,il[store-1].opcode))
                throw new InvalidOperationException("P31_MELEE_NATIVE_FINAL_IL_DRIFT");
            for (int i=0;i<il.Count;i++)
            {
                // Native mul leaves [DamageInfo object, native damage] on IL stack.
                // The hook consumes only native damage + controller and leaves
                // [DamageInfo object, GigaPMC-adjusted damage] for stfld.
                if (i==store)
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call,hook);
                }
                yield return il[i];
            }
        }
        private static float MeleeFinalOutput(float nativeDamage,object controller)
        {
            if(owner==null || !owner.P27EffectsTrialEnabled || localPlayer==null || percents[1]<=0m)
                return nativeDamage;
            try
            {
                if(!owner.P29IsCapturedLocalPlayer(localPlayer) ||
                    !ReferenceEquals(Read(controller,"_player"),localPlayer)) return nativeDamage;
                float changed;
                if(!AttributeGlobalBonus.TryMultiplyFinal(GlobalBonusId.MeleeDamage,
                    nativeDamage,percents[1],100000f,out changed) || changed <= nativeDamage)
                    return nativeDamage;
                NativeEvidence.Record(GlobalBonusId.MeleeDamage,nativeDamage,changed);
                if(++appliedMelee==1)
                    Log("P31_MELEE_FINAL_EFT_DAMAGE|ID="+AttributeGlobalBonus.Identity(GlobalBonusId.MeleeDamage)+
                        "|AFTER_NATIVE_STRENGTH|NO_GLOBAL_EFT_MUTATION|EXPERIMENTAL");
                return changed;
            }
            catch { return nativeDamage; }
        }
    }
}
