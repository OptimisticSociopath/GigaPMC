using System;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace GigaPMC.Client
{
    // Narrow, event-driven adaptation of ImpactCue's GamePlayerOwner.MyPlayer
    // and Physical.Stamina path. No reference to ImpactCue's binary is needed.
    // It does not invent a Physical controller in the main menu: this is an
    // ACTUAL native capacity hook, usable when EFT has constructed that owner
    // (notably the Hideout shooting range). It never affects a running raid;
    // the existing frozen P26 hook remains responsible for the raid.
    internal static class HideoutStaminaBridge
    {
        private const string HarmonyId="com.vargor.gigapmc.p41.hideout-body-stamina";
        private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        private static Plugin plugin;
        private static ManualLogSource logger;
        private static Harmony harmony;
        private static object observedPlayer, observedStamina;
        private static PropertyInfo capacityProperty;
        private static decimal menuPercent;
        private static bool installed;
        private static bool appliedLogged;
        private static string previousReadiness;

        internal static void TryInstall(Plugin source,ManualLogSource log)
        {
            plugin=source;logger=log;
            try
            {
                var eft=AppDomain.CurrentDomain.GetAssemblies()
                    .Single(a=>a.GetName().Name=="Assembly-CSharp");
                var physicalType=eft.GetType("Physical",true);
                // Legacy owner getter is absent in the user's loaded EFT.
                // Local PMC arrives via the existing P40 native GameWorld event.
                var staminaMember=physicalType.GetProperty("Stamina",Flags);
                Type staminaType=staminaMember?.PropertyType ??
                    physicalType.GetField("Stamina",Flags)?.FieldType;
                if(staminaType==null) throw new MissingMemberException("PHYSICAL_STAMINA_MISSING");
                capacityProperty=staminaType.GetProperty("TotalCapacity",Flags);
                var capacityGetter=capacityProperty?.GetGetMethod(true);
                if(capacityGetter==null || capacityGetter.IsAbstract ||
                    capacityGetter.ReturnType!=typeof(float) ||
                    capacityGetter.GetParameters().Length!=0)
                    throw new MissingMethodException("STAMINA_TOTAL_CAPACITY_FLOAT_GETTER_MISSING");
                harmony=new Harmony(HarmonyId);
                harmony.Patch(capacityGetter,postfix:new HarmonyMethod(typeof(HideoutStaminaBridge),
                    nameof(AfterNativeCapacity)));
                HarmonyAudit.Require(capacityGetter,HarmonyId,
                    nameof(AfterNativeCapacity),"postfix");
                installed=true;
                logger?.LogInfo("[GigaPMC] P43_HIDEOUT_NATIVE_STAMINA_ARMED|GAMEWORLD_LOCAL_PLAYER_EVENT"+
                    "|PHYSICAL_STAMINA_TOTALCAPACITY|EVENT_ONLY|NO_SCAN|RAID_GUARDED");
            }
            catch(Exception ex)
            {
                installed=false;
                try { harmony?.UnpatchSelf(); } catch { }
                harmony=null; capacityProperty=null;
                logger?.LogWarning("[GigaPMC] P41_HIDEOUT_NATIVE_STAMINA_UNAVAILABLE|"+
                    ex.GetType().Name+"|"+ex.Message+"|MENU_HP_AND_KG_UNTOUCHED");
            }
        }
        private static object Read(object instance,string field) =>
            AttributeMemberReader.Read(instance,field);

        // Called by the already installed native GameWorld.OnGameStarted and
        // RegisterPlayer callbacks, never by a Unity object scan or frame poll.
        internal static void OnNativeWorldObserved(object world)
        {
            if(world==null || !installed || plugin==null || plugin.P33InRaid ||
                RaidLifecycleHook.NativeRaidSignaled) return;
            OnNativeLocalPlayerObserved(Read(world,"MainPlayer"));
        }
        internal static void OnNativeLocalPlayerObserved(object player)
        {
            if(!installed || plugin==null || plugin.P33InRaid ||
                RaidLifecycleHook.NativeRaidSignaled || player==null ||
                ReferenceEquals(observedPlayer,player)) return;
            try
            {
                var profile=Read(player,"Profile");
                if(profile?.GetType().FullName!="EFT.Profile") return;
                var info=Read(profile,"Info");
                string side=(Read(profile,"Side") ?? Read(info,"Side"))?.ToString();
                var ai=Read(player,"IsAI");
                var local=Read(player,"IsYourPlayer");
                bool eftPlayer=false;
                for(Type type=player.GetType();type!=null;type=type.BaseType)
                    if(type.FullName=="EFT.Player" &&
                        type.Assembly.GetName().Name=="Assembly-CSharp")
                    {eftPlayer=true;break;}
                if(!LocalPmcGate.IsAuthorized(true,ai as bool?,local as bool?,
                    side,eftPlayer,true)) return;
                var physical=Read(player,"Physical");
                var stamina=Read(physical,"Stamina");
                if(stamina==null || capacityProperty==null ||
                    !capacityProperty.DeclaringType.IsInstanceOfType(stamina)) return;
                observedPlayer=player;
                observedStamina=stamina;
                appliedLogged=false;
                ApplyMenuRateAndObserve();
            }
            catch(Exception ex)
            {
                if(previousReadiness!=ex.GetType().Name)
                {
                    previousReadiness=ex.GetType().Name;
                    logger?.LogWarning("[GigaPMC] P43_STAMINA_LOCAL_PLAYER_REJECTED|"+
                        previousReadiness);
                }
            }
        }

        // Outside raid only. One calculation when a verified player appears
        // or F12 changes; no Unity Update poll and no simulated measurements.
        internal static void OnMenuF12Changed()
        {
            if(!installed || plugin==null || plugin.P33InRaid ||
                RaidLifecycleHook.NativeRaidSignaled) return;
            ApplyMenuRateAndObserve();
        }
        private static void ApplyMenuRateAndObserve()
        {
            if(observedStamina==null || plugin==null || plugin.P33InRaid ||
                RaidLifecycleHook.NativeRaidSignaled) return;
            MenuStaminaEvidence.Clear();
            appliedLogged=false;
            menuPercent=0m;
            decimal pct;
            if(!plugin.P41TryMenuBodyStaminaPercent(out pct)) return;
            menuPercent=pct;
            try
            {
                // The real getter triggers AfterNativeCapacity. This is an
                // event-triggered read, not a repeated sampling timer.
                capacityProperty.GetValue(observedStamina,null);
            }
            catch(Exception ex)
            {
                logger?.LogWarning("[GigaPMC] P41_STAMINA_NATIVE_READ_UNAVAILABLE|"+
                    ex.GetType().Name);
            }
        }
        private static void AfterNativeCapacity(object __instance,ref float __result)
        {
            if(!installed || plugin==null || plugin.P33InRaid ||
                RaidLifecycleHook.NativeRaidSignaled ||
                !ReferenceEquals(__instance,observedStamina)) return;
            float original=__result;
            float adjusted=original;
            if(menuPercent>0m &&
                !AttributeGlobalBonus.TryMultiplyFinal(GlobalBonusId.BodyStamina,
                    original,menuPercent,100000f,out adjusted)) return;
            __result=adjusted;
            MenuStaminaEvidence.Record(original,adjusted);
            if(!appliedLogged && menuPercent>0m)
            {
                appliedLogged=true;
                logger?.LogInfo("[GigaPMC] P41_HIDEOUT_BODY_STAMINA_APPLIED"+
                    "|NATIVE="+original.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+
                    "|RESULT="+adjusted.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+
                    "|SOURCE=PHYSICAL_STAMINA_TOTALCAPACITY|LOCAL_PMC|OUTSIDE_RAID");
            }
        }
        internal static void OnRaidExit()
        {
            observedPlayer=observedStamina=null;
            menuPercent=0m;
            appliedLogged=false;
            MenuStaminaEvidence.Clear();
        }
    }
}
