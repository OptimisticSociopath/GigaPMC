using System;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace GigaPMC.Client
{
    // A real local raid is authorized by TWO native lifecycle events:
    // EFT.GameWorld.OnGameStarted and EFT.EftClientBackendSession.LocalRaidStarted.
    // Hideout GameWorld / MainPlayer by itself is NEVER sufficient.
    // No map-name heuristics, no Unity.Find, no periodic retry loops.
    internal static class RaidLifecycleHook
    {
        private const string OwnerId="com.vargor.gigapmc.p40.native-raid-lifecycle";
        private static Plugin owner;
        private static ManualLogSource logger;
        private static Harmony harmony;
        private static UnityEngine.Object activeWorld;
        private static bool confirmedLocalRaid, firedForWorld;
        private static object fikaCandidateWorld, fikaCandidatePlayer;

        internal static object CurrentWorld => activeWorld==null ? null : (object)activeWorld;
        internal static bool NativeRaidSignaled => confirmedLocalRaid;
        internal static bool IsAuthenticatedWorldPlayer(object player)
        {
            var world=CurrentWorld;
            return confirmedLocalRaid && world!=null && player!=null && owner!=null &&
                ReferenceEquals(AttributeMemberReader.Read(world,"MainPlayer"),player);
        }
        internal static void TryInstall(Plugin plugin,ManualLogSource log)
        {
            owner=plugin;logger=log;
            try
            {
                var assemblies=AppDomain.CurrentDomain.GetAssemblies();
                var world=assemblies.Select(a=>a.GetType("EFT.GameWorld",false)).Single(t=>t!=null);
                // Fika uses its own SetLocalPlayer/CoopGame lifecycle.
                // Do not require the single-player LocalRaidStarted ABI in Fika.
                Type session=null;
                if(!plugin.IsFikaClient)
                    session=assemblies.Select(a=>a.GetType("EFT.EftClientBackendSession",false))
                        .Single(t=>t!=null);
                if(!typeof(UnityEngine.Object).IsAssignableFrom(world))
                    throw new TypeLoadException("EFT_WORLD_NOT_UNITY_OBJECT");
                const BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly;
                var worldStarted=world.GetMethods(flags).Single(m=>m.Name=="OnGameStarted" &&
                    m.ReturnType==typeof(void) && !m.IsAbstract);
                MethodInfo nativeRaid=null;
                if(!plugin.IsFikaClient)
                    nativeRaid=session.GetMethods(flags).Single(m=>m.Name=="LocalRaidStarted" && !m.IsAbstract);
                harmony=new Harmony(OwnerId);
                var lateStart=new HarmonyMethod(typeof(RaidLifecycleHook),nameof(NativeOnGameStarted));
                lateStart.priority=Priority.Last;
                var finalizer=new HarmonyMethod(typeof(RaidLifecycleHook),nameof(LastChanceGameStarted));
                finalizer.priority=Priority.Last;
                // Finalizer still observes world readiness if another mod's
                // postfix throws. Never swallow or rewrite the other exception.
                harmony.Patch(worldStarted,postfix:lateStart,finalizer:finalizer);
                HarmonyAudit.Require(worldStarted,OwnerId,nameof(NativeOnGameStarted),"postfix");
                if(nativeRaid!=null)
                {
                    var lateRaid=new HarmonyMethod(typeof(RaidLifecycleHook),nameof(NativeLocalRaidStarted));
                    lateRaid.priority=Priority.Last;
                    harmony.Patch(nativeRaid,postfix:lateRaid);
                    HarmonyAudit.Require(nativeRaid,OwnerId,nameof(NativeLocalRaidStarted),"postfix");
                }
                // Registered-player callback is optional; when available it covers late player initialization.
                var registrations=world.GetMethods(flags).Where(m=>m.Name=="RegisterPlayer" && !m.IsAbstract).ToArray();
                if(registrations.Length==1)
                {
                    var registration=registrations[0];
                    var lateRegistration=new HarmonyMethod(typeof(RaidLifecycleHook),nameof(NativePlayerRegistered));
                    lateRegistration.priority=Priority.Last;
                    harmony.Patch(registration,postfix:lateRegistration);
                    HarmonyAudit.Require(registration,OwnerId,nameof(NativePlayerRegistered),"postfix");
                }
                logger?.LogInfo("[GigaPMC] P204_NATIVE_RAID_EVENTS_ARMED|ON_GAME_STARTED|LOCAL_RAID_STARTED="+
                    (nativeRaid!=null?"YES":"FIKA_NOT_REQUIRED")+"|REGISTER="+
                    (registrations.Length==1?"YES":"UNAVAILABLE")+"|NO_MAP_NAMES|NO_POLLING");
            }
            catch(Exception ex)
            {
                try { harmony?.UnpatchSelf(); } catch { }
                harmony=null;activeWorld=null;confirmedLocalRaid=false;firedForWorld=false;
                logger?.LogWarning("[GigaPMC] P40_R3_NATIVE_RAID_EVENTS_UNAVAILABLE|"+ex.GetType().Name+
                    "|RAID_BONUSES_SAFE_OFF|MENU_HP_AND_KG_UNTOUCHED");
            }
        }
        private static void LastChanceGameStarted(object __instance, Exception __exception)
        {
            // Called even if a different Harmony postfix fails; never mask
            // that exception. No specific third-party mod is referenced here.
            if (__exception==null) return; // Healthy postfix path already observed the world.
            logger?.LogWarning("[GigaPMC] P201_OTHER_GAME_STARTED_PATCH_ERROR|"+
                __exception.GetType().Name+"|TRY_NATIVE_CAPTURE");
            try { NativeOnGameStarted(__instance); }
            catch(Exception ex) { logger?.LogWarning("[GigaPMC] P201_WORLD_FINALIZER_ERROR|"+ex.GetType().Name); }
        }
        // Called by the OPTIONAL Fika BaseGameController.SetLocalPlayer
        // postfix, not by a global Unity scan. This is an explicit native Fika
        // co-op session owner witness. We still require MainPlayer identity.
        internal static void OnFikaLocalPlayerAssigned(object world,object player)
        {
            if(owner==null || !owner.IsFikaClient || world==null || player==null) return;
            try
            {
                // The runtime Fika type and ownership are verified by our
                // optional reflection bridge before any raid lease is granted.
                fikaCandidateWorld=world;
                fikaCandidatePlayer=player;
                TryConfirmFikaOwner("FIKA_SET_LOCAL_PLAYER");
            }
            catch(Exception ex)
            { logger?.LogWarning("[GigaPMC] P203_FIKA_OWNER_ERROR|"+ex.GetType().Name); }
        }
        private static void TryConfirmFikaOwner(string reason)
        {
            if(owner==null || !owner.IsFikaClient || fikaCandidateWorld==null ||
                fikaCandidatePlayer==null) return;
            var world=fikaCandidateWorld as UnityEngine.Object;
            if(world==null) return;
            // No remote player, AI or Scav can satisfy this identity.
            if(!ReferenceEquals(AttributeMemberReader.Read(world,"MainPlayer"),fikaCandidatePlayer))
            {
                logger?.LogInfo("[GigaPMC] P203_FIKA_OWNER_PENDING|WAIT_NATIVE_WORLD_EVENT");
                return;
            }
            if(!FikaLocalBridge.IsVerifiedLocalPmc(fikaCandidatePlayer)) return;
            if(!ReferenceEquals(activeWorld,world)) firedForWorld=false;
            activeWorld=world;
            confirmedLocalRaid=true;
            logger?.LogInfo("[GigaPMC] P203_FIKA_OWNER_AUTHENTICATED|LOCAL_PMC|"+reason+
                "|HP_SERVER_ONLY|BONUSES_CLIENT_LOCAL");
            TryCaptureFromNativeEvents(reason);
        }
        private static void NativeOnGameStarted(object __instance)
        {
            try
            {
                if(harmony==null || owner==null) return;
                var world=__instance as UnityEngine.Object;
                if(world==null) return;
                if(!ReferenceEquals(activeWorld,world)) firedForWorld=false;
                activeWorld=world;
                // Do not mistake a Fika co-op world for the local Hideout
                // before its own explicit SetLocalPlayer authorization event.
                if(!owner.IsFikaClient) HideoutStaminaBridge.OnNativeWorldObserved(world);
                if(owner.IsFikaClient)
                {
                    // Fika uses a different raid lifecycle. Its LOCAL owner
                    // callback, not EFT.LocalRaidStarted, grants authority.
                    if(ReferenceEquals(fikaCandidateWorld,world))
                        TryConfirmFikaOwner("FIKA_WORLD_STARTED");
                }
                else TryCaptureFromNativeEvents("WORLD_STARTED");
            }
            catch(Exception ex) { logger?.LogWarning("[GigaPMC] P201_WORLD_EVENT_ERROR|"+ex.GetType().Name); }
        }
        private static void NativeLocalRaidStarted()
        {
            try
            {
                if(harmony==null || owner==null || owner.IsFikaClient) return;
                confirmedLocalRaid=true;
                TryCaptureFromNativeEvents("LOCAL_RAID_STARTED");
            }
            catch(Exception ex) { logger?.LogWarning("[GigaPMC] P201_LOCAL_RAID_EVENT_ERROR|"+ex.GetType().Name); }
        }
        private static void NativePlayerRegistered(object __instance)
        {
            try
            {
                if(harmony==null || !ReferenceEquals(__instance,activeWorld)) return;
                if(owner!=null && !owner.IsFikaClient)
                    HideoutStaminaBridge.OnNativeWorldObserved(__instance);
                if(owner!=null && owner.IsFikaClient)
                {
                    if(ReferenceEquals(fikaCandidateWorld,__instance))
                        TryConfirmFikaOwner("FIKA_PLAYER_REGISTERED");
                }
                else TryCaptureFromNativeEvents("PLAYER_REGISTERED");
            }
            catch(Exception ex) { logger?.LogWarning("[GigaPMC] P201_REGISTER_EVENT_ERROR|"+ex.GetType().Name); }
        }
        internal static void OnPlayerInitReturned(object player)
        {
            try
            {
                if(owner!=null && player!=null && IsAuthenticatedWorldPlayer(player))
                    owner.P201RetryPendingEffects(player,"PLAYER_INIT_RETURNED");
            }
            catch(Exception ex) { logger?.LogWarning("[GigaPMC] P201_PLAYER_INIT_RETRY_ERROR|"+ex.GetType().Name); }
        }
        private static void TryCaptureFromNativeEvents(string reason)
        {
            if(owner==null || !confirmedLocalRaid || activeWorld==null) return;
            object player=AttributeMemberReader.Read(activeWorld,"MainPlayer");
            if(player==null) return; // Not initialized yet; only a real native callback can retry.
            if(firedForWorld)
            {
                try { owner.P201RetryPendingEffects(player,reason); }
                catch(Exception ex) { logger?.LogWarning("[GigaPMC] P201_EVENT_RETRY_ERROR|"+ex.GetType().Name); }
                return;
            }
            try
            {
                owner.P40OnNativeGameStarted(activeWorld);
                if(owner.P29IsCapturedLocalPlayer(player)) firedForWorld=true;
            }
            catch(Exception ex)
            {
                logger?.LogWarning("[GigaPMC] P40_R3_NATIVE_CAPTURE_REJECTED|"+ex.GetType().Name);
            }
        }
        // Explicit Fika CoopGame teardown witness. Only the Fika
        // session already observed through SetLocalPlayer may end its lease.
        // No dependency on a Fika DLL and no recurring world discovery.
        internal static void OnFikaSessionEnded(object stoppedWorld,string eventName)
        {
            if(owner==null || !owner.IsFikaClient || stoppedWorld==null) return;
            if(!ReferenceEquals(stoppedWorld,activeWorld) &&
               !ReferenceEquals(stoppedWorld,fikaCandidateWorld)) return;
            try { owner.P204OnFikaSessionEnded(eventName); }
            catch(Exception ex)
            { logger?.LogWarning("[GigaPMC] P204_FIKA_END_CALLBACK_ERROR|"+ex.GetType().Name); }
        }
        internal static void OnVerifiedMenuReturn()
        {
            // A verified post-raid HP menu event is the ONLY reset authority.
            activeWorld=null;confirmedLocalRaid=false;firedForWorld=false;
            fikaCandidateWorld=fikaCandidatePlayer=null;
        }
    }
}
