using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace GigaPMC.Client
{
    // Client-only direct sync of the MAIN MENU Health controller after an F12 edit.
    // Bound exclusively to MainMenuShowOperation, never EFT.Player / in-raid UI.
    // Structural ABI gates are strict: unknown EFT builds fail closed without native coroutine polling.
    internal static class MenuHealthSync
    {
        private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly string[] PartNames = { "Head", "Chest", "Stomach", "LeftArm", "RightArm", "LeftLeg", "RightLeg" };
        private static readonly double[] BaseHp = { 35, 85, 70, 60, 60, 65, 65 };
        private static ManualLogSource log;
        private static Plugin owner;
        private static Harmony harmony;
        private static MethodInfo controllerRefresh;
        private static MethodInfo panelShow;
        private static WeakReference observedMenu;
        private static WeakReference observedPanel;
        private static object[] observedPanelArgs;
        private static bool installed;
        private static int lastMenuObservationFrame = -1;
        private static bool refreshing;
        private static bool queued;
        private static bool rebinding;
        private static DateTime queuedAt;
        private static bool failureLogged;

        internal static void TryInstall(Plugin plugin, ManualLogSource logger)
        {
            owner = plugin;
            log = logger;
            try
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .SingleOrDefault(a => a.GetType("EFT.MainMenuShowOperation", false) != null);
                if (assembly == null) throw new MissingMemberException("EFT.MainMenuShowOperation assembly unavailable");
                Type menu = assembly.GetType("EFT.MainMenuShowOperation", true);
                Type panel = assembly.GetType("EFT.UI.Health.InventoryScreenHealthPanel", true);
                if (panel == null) throw new MissingMemberException("InventoryScreenHealthPanel unavailable");
                controllerRefresh = menu.GetMethods(Flags).SingleOrDefault(m =>
                    m.Name == "UpdateHealthController" && m.GetParameters().Length == 0 && m.ReturnType == typeof(IEnumerator));
                var init = menu.GetMethods(Flags).SingleOrDefault(m =>
                    m.Name == "Init" && m.GetParameters().Length == 0 &&
                    m.ReturnType == typeof(System.Threading.Tasks.Task));
                panelShow = panel.GetMethods(Flags).SingleOrDefault(m =>
                    m.Name == "Show" && m.GetParameters().Length == 4 && m.ReturnType == typeof(void));
                if (controllerRefresh == null || init == null || panelShow == null)
                    throw new MissingMemberException("MainMenu.Init(Task)/UpdateHealthController(IEnumerator) or HealthPanel.Show(void) ABI mismatch");
                if (menu.GetProperty("IsInSession", Flags) == null ||
                    menu.GetProperty("HealthController", Flags) == null ||
                    menu.GetField("profile_0", Flags) == null)
                    throw new MissingMemberException("Main menu safety properties/profile field missing");
                harmony = new Harmony("com.vargor.gigapmc.healthmenu");
                var capture = typeof(MenuHealthSync).GetMethod("ObserveMenu", BindingFlags.Static | BindingFlags.NonPublic);
                var capturePanel = typeof(MenuHealthSync).GetMethod("ObservePanel", BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(controllerRefresh, postfix: new HarmonyMethod(capture));
                harmony.Patch(init, postfix: new HarmonyMethod(capture));
                harmony.Patch(panelShow, prefix: new HarmonyMethod(capturePanel));
                installed = true;
                logger.LogInfo("[GigaPMC] MENU_HEALTH_PATCH_ARMED|UpdateHealthController=IEnumerator|Init=Task|Panel.Show=void");
            }
            catch (Exception ex)
            {
                try { harmony?.UnpatchSelf(); } catch { }
                installed = false;
                logger.LogWarning("[GigaPMC] MENU_HEALTH_UNAVAILABLE|" + ex.GetType().Name + "|" + ex.Message);
            }
        }

        private static void ObserveMenu(object __instance)
        {
            if (!installed || __instance == null) return;
            observedMenu = new WeakReference(__instance);
            lastMenuObservationFrame = Time.frameCount;
        }

        private static void ObservePanel(object __instance, object[] __args)
        {
            if (!installed || rebinding || __instance == null || __args == null || __args.Length != 4) return;
            observedPanel = new WeakReference(__instance);
            observedPanelArgs = (object[])__args.Clone();
        }

        // P29: reuse the existing, ABI-verified HP menu observer; no additional
        // Harmony patches or gameplay writes. A pre-raid menu from before
        // Player.Init is NOT evidence that the current raid has ended.
        internal static bool ConfirmedOutOfRaidMenuAfter(int raidStartFrame)
        {
            if (!installed || raidStartFrame < 0 ||
                lastMenuObservationFrame <= raidStartFrame) return false;
            try
            {
                object menu = observedMenu?.Target;
                if (menu == null || menu.GetType().FullName != "EFT.MainMenuShowOperation") return false;
                object session = Read(menu, "IsInSession");
                if (!(session is bool) || (bool)session) return false;
                object profile = Read(menu, "profile_0");
                object health = Read(menu, "HealthController");
                return profile?.GetType().FullName == "EFT.Profile" &&
                    health?.GetType().FullName == "EFT.HealthSystem.OfflineHealthController";
            }
            catch { return false; }
        }

        internal static void Queue()
        {
            if (!installed) return;
            if (!queued) log?.LogInfo("[GigaPMC] MENU_HEALTH_REFRESH_QUEUED|F12_CHANGED");
            queued = true;
            failureLogged = false;
            queuedAt = DateTime.UtcNow;
        }

        // Called by BepInEx MonoBehaviour.Update, on Unity's main thread.
        // A short retry window handles transitions without persistent polling.
        internal static void Tick()
        {
            if (!installed || !queued || rebinding || refreshing) return;
            // Coalesce F12 slider steps. No background thread, no timer object.
            if (DateTime.UtcNow - queuedAt < TimeSpan.FromMilliseconds(250)) return;
            object menu = observedMenu?.Target;
            if (menu == null || Read(menu, "HealthController") == null)
            {
                if (DateTime.UtcNow - queuedAt < TimeSpan.FromSeconds(5)) return;
                queued = false;
                log?.LogWarning("[GigaPMC] MENU_HEALTH_REFRESH_SKIPPED|Main menu controller not observed");
                return;
            }
            queued = false;
            refreshing = true;
            try { Refresh(menu); }
            catch (Exception ex)
            {
                if (!failureLogged)
                {
                    failureLogged = true;
                    log?.LogWarning("[GigaPMC] MENU_HEALTH_REFRESH_SKIPPED|" + ex.GetType().Name + "|" + ex.Message);
                }
            }
            finally { refreshing = false; }
        }

        private static object Read(object instance, string name)
        {
            if (instance == null) return null;
            var t = instance.GetType();
            var p = t.GetProperty(name, Flags);
            if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(instance);
            return t.GetField(name, Flags)?.GetValue(instance);
        }

        private static double Number(object obj, string label)
        {
            if (obj == null) throw new InvalidDataException(label + " missing");
            double n = Convert.ToDouble(obj, CultureInfo.InvariantCulture);
            if (double.IsNaN(n) || double.IsInfinity(n)) throw new InvalidDataException(label + " not finite");
            return n;
        }

        private static int Whole(object obj, string label, int minimum, int maximum)
        {
            double n = Number(obj, label);
            if (n != Math.Truncate(n) || n < minimum || n > maximum) throw new InvalidDataException(label + " invalid");
            return checked((int)n);
        }

        private sealed class Slot
        {
            internal object PartKey;
            internal object Health;
            internal HealthModelAccess Writer;
            internal double OldMaximum;
            internal double OldCurrent;
            internal double NewMaximum;
            internal double NewCurrent;
            internal HealthModelAccess LiveWriter;
            internal double OldLiveMaximum;
            internal double OldLiveCurrent;
        }

        private static void Refresh(object menu)
        {
            var inSessionValue = Read(menu, "IsInSession");
            if (!(inSessionValue is bool) || (bool)inSessionValue)
                throw new InvalidOperationException("Only out-of-raid main menu may refresh");
            var currentController = Read(menu, "HealthController");
            if (currentController == null ||
                currentController.GetType().FullName != "EFT.HealthSystem.OfflineHealthController")
                throw new InvalidOperationException("Unexpected menu health controller type");
            var profile = Read(menu, "profile_0");
            if (profile?.GetType().FullName != "EFT.Profile")
                throw new InvalidOperationException("Current menu PMC profile not available");
            var info = Read(profile, "Info");
            if (info == null) throw new MissingMemberException("EFT.Profile.Info missing");
            string side = (Read(profile, "Side") ?? Read(info, "Side"))?.ToString();
            if (!string.Equals(side, "Bear", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(side, "Usec", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Not a BEAR/USEC PMC");
            int level = Whole(Read(info, "Level"), "Level", 1, 10000);
            object pVal = Read(profile, "PrestigeLevel") ?? Read(info, "PrestigeLevel");
            int prestige = pVal == null ? 0 : Whole(pVal, "Prestige", 0, 10000);
            RaidHpSettings settings = owner.CurrentRaidSettings();
            if (!settings.IsValid()) throw new InvalidDataException("F12 settings validation failed");
            int? detectedCap = null;
            if (settings.AutoCap)
            {
                string path = Path.Combine(Paths.GameRootPath, "SPT_Runtime", "user", "mods", "GigaPMC", "config", "server-level-cap.txt");
                if (!File.Exists(path)) throw new FileNotFoundException("SPT level cap cache missing", path);
                if (!int.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cap))
                    throw new InvalidDataException("SPT level cap cache invalid");
                detectedCap = cap;
            }
            int maxLevel = settings.ResolveCap(detectedCap);
            var bodyParts = Read(Read(profile, "Health"), "BodyParts") as IDictionary;
            if (bodyParts == null) throw new MissingMemberException("EFT.Profile.Health.BodyParts dictionary missing");
            var slots = new List<Slot>();
            for (int i = 0; i < PartNames.Length; i++)
            {
                string name = PartNames[i];
                object key = null;
                foreach (var k in bodyParts.Keys)
                    if (string.Equals(k?.ToString(), name, StringComparison.OrdinalIgnoreCase)) { key = k; break; }
                if (key == null) throw new MissingMemberException(name + " not found");
                object part = bodyParts[key];
                object hp = Read(part, "Health");
                if (hp == null) throw new MissingMemberException(name + ".Health missing");
                HealthModelAccess writer;
                try { writer = new HealthModelAccess(hp); }
                catch (Exception ex) { throw new MissingMemberException(name + ": " + ex.Message); }
                double oldMax = writer.Maximum;
                double oldCurrent = writer.Current;
                if (oldMax <= 0 || oldMax > 200000 || oldCurrent < 0 || oldCurrent > oldMax)
                    throw new InvalidDataException(name + " old HP invalid");
                double nextMax = BaseHp[i] + (settings.Enabled ? settings.Bonus(level, prestige, maxLevel, name) : 0);
                if (nextMax <= 0 || nextMax > 200000) throw new InvalidDataException(name + " new HP invalid");
                double nextCurrent = oldCurrent <= 0 ? 0 :
                    (nextMax < oldMax ? Math.Max(0, Math.Min(nextMax, nextMax * oldCurrent / oldMax)) :
                                        Math.Max(0, Math.Min(nextMax, nextMax - (oldMax - oldCurrent))));
                slots.Add(new Slot { PartKey = key, Health = hp, Writer = writer, OldMaximum = oldMax,
                    OldCurrent = oldCurrent, NewMaximum = nextMax, NewCurrent = nextCurrent });
            }

            // No native UpdateHealthController() invocation here. The user's
            // SPT 4.1.6 coroutine was observed to start and never finish, so the
            // original implementation retained refreshing=true indefinitely.
            // Instead modify the existing out-of-raid controller's stable live
            // HealthValue objects, using their audited native Init method.
            // Refuse updates if they cannot be resolved to stable references.
            var liveWriters = ResolveLiveParts(currentController, slots);
            if (liveWriters.Count != PartNames.Length)
                throw new InvalidOperationException("Live health controller has less than seven parts");
            foreach (var slot in slots)
            {
                var writer = liveWriters[slot.PartKey];
                if (writer.Strategy != "NativeInit" && writer.Strategy != "PropertySetters")
                    throw new InvalidOperationException("Unsafe live health writer for " + slot.PartKey + "|" + writer.Strategy);
                slot.LiveWriter = writer;
                slot.OldLiveMaximum = writer.Maximum;
                slot.OldLiveCurrent = writer.Current;
                if (slot.OldLiveMaximum <= 0 || slot.OldLiveMaximum > 200000 ||
                    slot.OldLiveCurrent < 0 || slot.OldLiveCurrent > slot.OldLiveMaximum)
                    throw new InvalidOperationException("Invalid live health values " + slot.PartKey);
            }

            log?.LogInfo("[GigaPMC] MENU_HEALTH_MODEL_STRATEGIES|" +
                string.Join("|", slots.Select((x, i) => PartNames[i] + "=" +
                    x.Writer.Strategy + "/" + x.LiveWriter.Strategy)));

            // Update the cached profile and its live, already-displayed
            // controller in one synchronous main-thread transaction.
            // Neither backend network calls nor coroutine waits are involved.
            // Never change anything while an in-raid session is active.
            if (Read(menu, "IsInSession") is not bool outOfRaid || outOfRaid)
                throw new InvalidOperationException("Raid transition before direct controller commit");
            int profileWritten = 0;
            int liveWritten = 0;
            try
            {
                foreach (var slot in slots)
                {
                    profileWritten++; // Include a partly-mutated failed slot in rollback.
                    slot.Writer.Write(slot.NewCurrent, slot.NewMaximum);
                }
                foreach (var slot in slots)
                {
                    liveWritten++; // Init() can modify state before throwing.
                    slot.LiveWriter.Write(slot.NewCurrent, slot.NewMaximum);
                }
                foreach (var slot in slots)
                {
                    // Verify reference-backed profile and live values, not
                    // merely the boxed object used by a reflection setter.
                    var profileHp = Read(bodyParts[slot.PartKey], "Health");
                    var profileRead = new HealthModelAccess(profileHp);
                    if (Math.Abs(profileRead.Maximum - slot.NewMaximum) > 0.001 ||
                        Math.Abs(profileRead.Current - slot.NewCurrent) > 0.001 ||
                        Math.Abs(slot.LiveWriter.Maximum - slot.NewMaximum) > 0.001 ||
                        Math.Abs(slot.LiveWriter.Current - slot.NewCurrent) > 0.001)
                        throw new InvalidOperationException("Direct write failed readback for " + slot.PartKey);
                }
            }
            catch
            {
                for (int i = liveWritten - 1; i >= 0; i--)
                    try { slots[i].LiveWriter.Write(slots[i].OldLiveCurrent, slots[i].OldLiveMaximum); }
                    catch (Exception ex) { log?.LogError("[GigaPMC] MENU_HEALTH_ROLLBACK_FAILED|LIVE|" + slots[i].PartKey + "|" + ex.Message); }
                for (int i = profileWritten - 1; i >= 0; i--)
                    try { slots[i].Writer.Write(slots[i].OldCurrent, slots[i].OldMaximum); }
                    catch (Exception ex) { log?.LogError("[GigaPMC] MENU_HEALTH_ROLLBACK_FAILED|PROFILE|" + slots[i].PartKey + "|" + ex.Message); }
                throw;
            }

            log?.LogInfo("[GigaPMC] MENU_HP_PROFILE_APPLIED|L" + level + "|P" + prestige + "|CAP" + maxLevel +
                "|" + string.Join("|", slots.Select((x, i) => PartNames[i] + "=" + x.NewMaximum.ToString("0", CultureInfo.InvariantCulture))));
            log?.LogInfo("[GigaPMC] MENU_HEALTH_CONTROLLER_REFRESHED|DIRECT_CONTROLLER|7/7");
            TryRebindOpenPanel(currentController, currentController);
        }

        private static Dictionary<object, HealthModelAccess> ResolveLiveParts(object controller, List<Slot> slots)
        {
            var result = new Dictionary<object, HealthModelAccess>();
            var states = Read(controller, "BodyState") as IDictionary;
            if (states != null)
            {
                foreach (var slot in slots)
                {
                    object stateKey = null;
                    foreach (var k in states.Keys)
                        if (string.Equals(k?.ToString(), slot.PartKey?.ToString(), StringComparison.OrdinalIgnoreCase))
                        { stateKey = k; break; }
                    if (stateKey == null)
                        throw new MissingMemberException("BodyState missing " + slot.PartKey);
                    var value = Read(states[stateKey], "Health");
                    if (value == null || value.GetType().IsValueType)
                        throw new MissingMemberException("BodyState.Health not a stable health reference for " + slot.PartKey);
                    result.Add(slot.PartKey, new HealthModelAccess(value));
                }
                log?.LogInfo("[GigaPMC] MENU_HEALTH_LIVE_BINDING|BodyState|7/7");
                return result;
            }

            // Compatibility fallback only when the engine returns exactly the
            // same stable HealthValue object on two successive non-rounded reads.
            var methods = controller.GetType().GetMethods(Flags)
                .Where(m => m.Name == "GetBodyPartHealth" && m.GetParameters().Length == 2 &&
                    m.GetParameters()[0].ParameterType.IsEnum &&
                    m.GetParameters()[1].ParameterType == typeof(bool)).ToArray();
            if (methods.Length != 1)
                throw new MissingMemberException("Live BodyState inaccessible; GetBodyPartHealth ABI candidates=" + methods.Length);
            var method = methods[0];
            foreach (var slot in slots)
            {
                object part;
                try { part = Enum.Parse(method.GetParameters()[0].ParameterType, slot.PartKey.ToString(), true); }
                catch (Exception ex) { throw new MissingMemberException("Bodypart enum " + slot.PartKey + ": " + ex.Message); }
                object first = method.Invoke(controller, new[] { part, (object)false });
                object second = method.Invoke(controller, new[] { part, (object)false });
                if (first == null || first.GetType().IsValueType || !ReferenceEquals(first, second))
                    throw new InvalidOperationException("GetBodyPartHealth does not expose stable reference: " + slot.PartKey);
                result.Add(slot.PartKey, new HealthModelAccess(first));
            }
            log?.LogInfo("[GigaPMC] MENU_HEALTH_LIVE_BINDING|GetBodyPartHealth|7/7");
            return result;
        }

        private static void TryRebindOpenPanel(object oldController, object newController)
        {
            object panel = observedPanel?.Target;
            if (panel == null || observedPanelArgs == null || panelShow == null)
            {
                log?.LogInfo("[GigaPMC] MENU_HEALTH_PANEL_REBIND_SKIPPED|No panel previously observed");
                return;
            }
            var component = panel as Component;
            if (component == null || component.gameObject == null || !component.gameObject.activeInHierarchy)
            {
                log?.LogInfo("[GigaPMC] MENU_HEALTH_PANEL_REBIND_SKIPPED|Panel not active");
                return;
            }
            var args = (object[])observedPanelArgs.Clone();
            // Never rebind a panel observed with an unrelated character or raid controller.
            if (!ReferenceEquals(args[0], oldController))
            {
                log?.LogWarning("[GigaPMC] MENU_HEALTH_PANEL_REBIND_SKIPPED|Controller identity mismatch");
                return;
            }
            args[0] = newController;
            try
            {
                rebinding = true;
                panelShow.Invoke(panel, args);
                observedPanelArgs = (object[])args.Clone();
                log?.LogInfo("[GigaPMC] MENU_HEALTH_PANEL_REBOUND");
            }
            catch (Exception ex)
            {
                log?.LogWarning("[GigaPMC] MENU_HEALTH_PANEL_REBIND_SKIPPED|" + ex.GetType().Name + "|" + ex.Message);
            }
            finally { rebinding = false; }
        }
    }
}
