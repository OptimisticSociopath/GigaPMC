using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx.Logging;
using HarmonyLib;

namespace GigaPMC.Client
{
    // Post-event observer of the SAME EFT menu lifecycle already used by HP.
    // Preserves MenuHealthSync byte-for-byte; no Update polling, no Unity scans.
    internal static class MenuEventBridge
    {
        private const string Id="com.vargor.gigapmc.p40r3.menu-lifecycle";
        private static Harmony harmony;
        private static Plugin owner;
        internal static void TryInstall(Plugin plugin, ManualLogSource log)
        {
            owner=plugin;
            try
            {
                var menu=AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a=>a.GetType("EFT.MainMenuShowOperation",false))
                    .Single(t=>t!=null);
                var flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
                var refresh=menu.GetMethods(flags).Single(m=>m.Name=="UpdateHealthController" &&
                    m.GetParameters().Length==0 && m.ReturnType==typeof(IEnumerator));
                var init=menu.GetMethods(flags).Single(m=>m.Name=="Init" &&
                    m.GetParameters().Length==0 && m.ReturnType==typeof(Task));
                var postfix=new HarmonyMethod(typeof(MenuEventBridge),nameof(AfterNativeMenuOperation));
                postfix.priority=Priority.Last; // Run after MenuHealthSync's normal postfix.
                harmony=new Harmony(Id);
                harmony.Patch(refresh,postfix:postfix);
                harmony.Patch(init,postfix:postfix);
                HarmonyAudit.Require(refresh,Id,nameof(AfterNativeMenuOperation),"postfix");
                HarmonyAudit.Require(init,Id,nameof(AfterNativeMenuOperation),"postfix");
                log?.LogInfo("[GigaPMC] P40_R3_MENU_EVENT_ARMED|HP_NATIVE_INIT_AND_UPDATE|NO_POLL");
            }
            catch(Exception ex)
            {
                try { harmony?.UnpatchSelf(); } catch { }
                harmony=null;
                log?.LogWarning("[GigaPMC] P40_R3_MENU_EVENT_UNAVAILABLE|"+ex.GetType().Name+
                    "|RAID_LEASE_WILL_NOT_RELEASE_WITHOUT_NATIVE_MENU_WITNESS");
            }
        }
        private static void AfterNativeMenuOperation(object __instance)
        {
            try { owner?.P40R3AfterNativeMenuEvent(); }
            catch(Exception ex) { /* Do not interrupt EFT or HP. */ }
        }
    }
}
