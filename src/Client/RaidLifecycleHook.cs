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
                var session=assemblies.Select(a=>a.GetType("EFT.EftClientBackendSession",false))
                    .Single(t=>t!=null);
                if(!typeof(UnityEngine.Object).IsAssignableFrom(world))
                    throw new TypeLoadException("EFT_WORLD_NOT_UNITY_OBJECT");
                const BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly;
                var worldStarted=world.GetMethods(flags).Single(m=>m.Name=="OnGameStarted" &&
                    m.ReturnType==typeof(void) && !m.IsAbstract);
                var nativeRaid=session.GetMethods(flags).Single(m=>m.Name=="LocalRaidStarted" && !m.IsAbstract);
                harmony=new Harmony(OwnerId);
                harmony.Patch(worldStarted,postfix:new HarmonyMethod(typeof(RaidLifecycleHook),nameof(NativeOnGameStarted)));
                harmony.Patch(nativeRaid,postfix:new HarmonyMethod(typeof(RaidLifecycleHook),nameof(NativeLocalRaidStarted)));
                HarmonyAudit.Require(worldStarted,OwnerId,nameof(NativeOnGameStarted),"postfix");
                HarmonyAudit.Require(nativeRaid,OwnerId,nameof(NativeLocalRaidStarted),"postfix");
                // Registered-player callback is optional; when available it covers late player initialization.
                var registrations=world.GetMethods(flags).Where(m=>m.Name=="RegisterPlayer" && !m.IsAbstract).ToArray();
                if(registrations.Length==1)
                {
                    var registration=registrations[0];
                    harmony.Patch(registration,postfix:new HarmonyMethod(typeof(RaidLifecycleHook),nameof(NativePlayerRegistered)));
                    HarmonyAudit.Require(registration,OwnerId,nameof(NativePlayerRegistered),"postfix");
                }
                logger?.LogInfo("[GigaPMC] P40_R3_NATIVE_RAID_EVENTS_ARMED|ON_GAME_STARTED|LOCAL_RAID_STARTED|REGISTER="+
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
        private static void NativeOnGameStarted(object __instance)
        {
            if(harmony==null || owner==null) return;
            var world=__instance as UnityEngine.Object;
            if(world==null) return;
            if(!ReferenceEquals(activeWorld,world)) firedForWorld=false;
            activeWorld=world;
            // Hideout can create a real EFT.Player without a raid lease.
            HideoutStaminaBridge.OnNativeWorldObserved(world);
            TryCaptureFromNativeEvents();
        }
        private static void NativeLocalRaidStarted()
        {
            if(harmony==null) return;
            confirmedLocalRaid=true;
            TryCaptureFromNativeEvents();
        }
        private static void NativePlayerRegistered(object __instance)
        {
            if(harmony==null || !ReferenceEquals(__instance,activeWorld)) return;
            HideoutStaminaBridge.OnNativeWorldObserved(__instance);
            TryCaptureFromNativeEvents();
        }
        private static void TryCaptureFromNativeEvents()
        {
            if(owner==null || !confirmedLocalRaid || firedForWorld || activeWorld==null) return;
            object player=AttributeMemberReader.Read(activeWorld,"MainPlayer");
            if(player==null) return; // Not initialized yet; only a real native callback can retry.
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
        internal static void OnVerifiedMenuReturn()
        {
            // A verified post-raid HP menu event is the ONLY reset authority.
            activeWorld=null;confirmedLocalRaid=false;firedForWorld=false;
        }
    }
}
