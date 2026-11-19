using System;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace GigaPMC.Client
{
    // P03 optional Fika interop; NO reference to Fika.Core.dll or any Fika API
    // during compile, installation or execution without Fika present.
    // Based on Fika's public BaseGameController.SetLocalPlayer(FikaPlayer).
    internal static class FikaLocalBridge
    {
        private const string PatchId="com.vargor.gigapmc.p203.fika-local-owner";
        private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        private static Plugin owner;
        private static ManualLogSource log;
        private static Harmony harmony;
        private static FieldInfo gameWorldField;
        private static Type fikaPlayerType;
        private static PropertyInfo isHeadless, isSpectator;
        private static bool warned;

        internal static void TryInstall(Plugin plugin,ManualLogSource logger)
        {
            if(plugin==null || !plugin.IsFikaClient) return;
            owner=plugin; log=logger;
            try
            {
                var matched=AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a=>a.GetType("Fika.Core.Main.GameMode.BaseGameController",false))
                    .Where(t=>t!=null).ToArray();
                if(matched.Length!=1) throw new TypeLoadException("FIKA_GAME_CONTROLLER_ABI");
                var type=matched[0];
                fikaPlayerType=type.Assembly.GetType("Fika.Core.Main.Players.FikaPlayer",true);
                if(fikaPlayerType==null) throw new TypeLoadException("FIKA_PLAYER_ABI");
                var backend=type.Assembly.GetType("Fika.Core.Main.Utils.FikaBackendUtils",true);
                isHeadless=backend.GetProperty("IsHeadless",BindingFlags.Public|BindingFlags.Static);
                isSpectator=backend.GetProperty("IsSpectator",BindingFlags.Public|BindingFlags.Static);
                if(isHeadless?.PropertyType!=typeof(bool) ||
                   isSpectator?.PropertyType!=typeof(bool))
                    throw new MissingMemberException("FIKA_HEADLESS_SPECTATOR_ABI");
                var methods=type.GetMethods(Flags|BindingFlags.DeclaredOnly)
                    .Where(m=>m.Name=="SetLocalPlayer" && !m.IsStatic &&
                        m.ReturnType==typeof(void) && m.GetParameters().Length==1 &&
                        m.GetParameters()[0].ParameterType==fikaPlayerType).ToArray();
                if(methods.Length!=1) throw new MissingMethodException("FIKA_SET_LOCAL_PLAYER_ABI");
                gameWorldField=type.GetField("_gameWorld",Flags|BindingFlags.DeclaredOnly);
                if(gameWorldField==null || gameWorldField.IsStatic ||
                    gameWorldField.FieldType.FullName!="EFT.GameWorld")
                    throw new MissingFieldException("FIKA_GAME_WORLD_ABI");
                harmony=new Harmony(PatchId);
                var late=new HarmonyMethod(typeof(FikaLocalBridge),nameof(AfterFikaLocalOwner));
                late.priority=Priority.Last;
                harmony.Patch(methods[0],postfix:late);
                HarmonyAudit.Require(methods[0],PatchId,nameof(AfterFikaLocalOwner),"postfix");
                InstallFikaEndHooks(type.Assembly,logger);
                logger?.LogInfo("[GigaPMC] P203_FIKA_LOCAL_OWNER_HOOK_READY|OPTIONAL|NO_DEPENDENCY");
            }
            catch(Exception ex)
            {
                try { harmony?.UnpatchSelf(); } catch { }
                harmony=null;
                logger?.LogWarning("[GigaPMC] P203_FIKA_LOCAL_OWNER_HOOK_BLOCKED|"+
                    ex.GetType().Name+"|"+ex.Message+"|GAMEPLAY_SAFE_OFF");
            }
        }
        // The same optional Fika assembly contains CoopGame's explicit
        // Stop, StopFromCancel and Dispose callbacks. Resolve each separately:
        // a changed shutdown signature must NOT disable the local-owner hook.
        private static void InstallFikaEndHooks(Assembly assembly,ManualLogSource logger)
        {
            var coop=assembly.GetType("Fika.Core.Main.GameMode.CoopGame",false);
            if(coop==null)
            {
                logger?.LogWarning("[GigaPMC] P204_FIKA_END_HOOK_UNAVAILABLE|COOP_TYPE");
                return;
            }
            var methods=coop.GetMethods(Flags|BindingFlags.DeclaredOnly);
            foreach(string name in new[]{"Stop","StopFromCancel","Dispose"})
            {
                try
                {
                    MethodInfo target=null;
                    if(name=="Stop")
                        target=methods.Single(m=>m.Name==name && m.ReturnType==typeof(void) &&
                            m.GetParameters().Length==4);
                    else if(name=="StopFromCancel")
                        target=methods.Single(m=>m.Name==name && m.ReturnType==typeof(void) &&
                            m.GetParameters().Length==2);
                    else
                        target=methods.Single(m=>m.Name==name && m.ReturnType==typeof(void) &&
                            m.GetParameters().Length==0);
                    var pre=new HarmonyMethod(typeof(FikaLocalBridge),nameof(BeforeFikaShutdown));
                    pre.priority=Priority.Last;
                    harmony.Patch(target,prefix:pre);
                    HarmonyAudit.Require(target,PatchId,nameof(BeforeFikaShutdown),"prefix");
                    logger?.LogInfo("[GigaPMC] P204_FIKA_END_HOOK_READY|"+name);
                }
                catch(Exception ex)
                {
                    logger?.LogWarning("[GigaPMC] P204_FIKA_END_HOOK_SKIPPED|"+name+
                        "|"+ex.GetType().Name);
                }
            }
        }
        private static void BeforeFikaShutdown(object __instance,MethodBase __originalMethod)
        {
            try
            {
                if(__instance==null || __originalMethod==null || owner==null ||
                    !owner.IsFikaClient) return;
                // CoopGame.GameWorld is a normal EFT inherited property;
                // failure to resolve is a safe-off, not a reason to guess.
                object world=AttributeMemberReader.Read(__instance,"GameWorld");
                if(world==null)
                {
                    log?.LogWarning("[GigaPMC] P204_FIKA_END_WORLD_UNAVAILABLE|"+
                        __originalMethod.Name);
                    return;
                }
                RaidLifecycleHook.OnFikaSessionEnded(world,__originalMethod.Name);
            }
            catch(Exception ex)
            {
                log?.LogWarning("[GigaPMC] P204_FIKA_END_CALLBACK_FAILED|"+
                    ex.GetType().Name);
            }
        }
        internal static bool IsVerifiedLocalPmc(object player)
        {
            if(player==null || fikaPlayerType==null ||
                !fikaPlayerType.IsInstanceOfType(player)) return false;
            try
            {
                // Never grant bonuses to Fika's AI/headless or spectators.
                if(isHeadless==null || isSpectator==null ||
                    (bool)isHeadless.GetValue(null,null) ||
                    (bool)isSpectator.GetValue(null,null)) return false;
                var local=AttributeMemberReader.Read(player,"IsYourPlayer");
                var ai=AttributeMemberReader.Read(player,"IsAI");
                var profile=AttributeMemberReader.Read(player,"Profile");
                var info=AttributeMemberReader.Read(profile,"Info");
                var side=(AttributeMemberReader.Read(profile,"Side") ??
                    AttributeMemberReader.Read(info,"Side"))?.ToString();
                // Strict local ownership. Unknown booleans fail CLOSED.
                return local is bool isLocal && isLocal && ai is bool isAi && !isAi &&
                    profile?.GetType().FullName=="EFT.Profile" &&
                    (string.Equals(side,"Bear",StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(side,"Usec",StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }
        private static void AfterFikaLocalOwner(object __instance,object[] __args)
        {
            if(harmony==null || __instance==null || __args==null || __args.Length!=1) return;
            try
            {
                object player=__args[0];
                if(!IsVerifiedLocalPmc(player)) return;
                object world=gameWorldField.GetValue(__instance);
                owner?.P203OnFikaLocalOwner(world,player);
            }
            catch(Exception ex)
            {
                if(warned) return;
                warned=true;
                log?.LogWarning("[GigaPMC] P203_FIKA_OWNER_CALLBACK_FAILED|"+
                    ex.GetType().Name+"|NO_SCANS");
            }
        }
    }
}
