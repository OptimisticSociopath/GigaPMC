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

namespace GigaPMC.Client
{
    // A local-only, fail-closed attempt to refresh the PMC profile BEFORE the
    // EFT Player.Init initializes its in-raid health controller. No mid-raid
    // health edits, no bot/Scav profile edits and no profile save-file writes.
    // Must be confirmed against the actual SPT 4.1.6 EFT client ABI at runtime.
    internal static class RaidEntryPatch
    {
        private static Plugin owner;
        private static ManualLogSource log;
        private static Harmony harmony;
        private static readonly string[] Names =
            { "Head", "Chest", "Stomach", "LeftArm", "RightArm", "LeftLeg", "RightLeg" };
        private static readonly double[] Base = { 35, 85, 70, 60, 60, 65, 65 };
        private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;
        private static bool warned;

        internal static void TryInstall(Plugin source, ManualLogSource logger)
        {
            owner = source;
            log = logger;
            try
            {
                Type playerType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("EFT.Player", false)).FirstOrDefault(t => t != null);
                if (playerType == null) { logger.LogWarning("[GigaPMC] RAID_ENTRY_UNAVAILABLE|EFT.Player not loaded"); return; }
                var methods = playerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(m => m.Name == "Init" && !m.IsGenericMethod &&
                        m.GetParameters().Count(p => p.ParameterType.FullName == "EFT.Profile") == 1 &&
                        m.GetParameters().Any(IsExplicitLocalFlag)).ToArray();
                if (methods.Length != 1)
                {
                    logger.LogWarning("[GigaPMC] RAID_ENTRY_UNAVAILABLE|Safe Player.Init overload count=" + methods.Length +
                        "|No speculative patch installed");
                    return;
                }
                harmony = new Harmony("com.vargor.gigapmc.raidentry");
                MethodInfo prefix = typeof(RaidEntryPatch).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static);
                harmony.Patch(methods[0], prefix: new HarmonyMethod(prefix));
                logger.LogInfo("[GigaPMC] RAID_ENTRY_PATCH_ARMED|" + methods[0]);
            }
            catch (Exception ex)
            {
                try { harmony?.UnpatchSelf(); } catch { }
                logger.LogError("[GigaPMC] RAID_ENTRY_UNAVAILABLE|" + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool IsExplicitLocalFlag(ParameterInfo p)
        {
            if (p.ParameterType != typeof(bool)) return false;
            return p.Name != null && (p.Name.Equals("isYourPlayer", StringComparison.OrdinalIgnoreCase) ||
                p.Name.Equals("isLocalPlayer", StringComparison.OrdinalIgnoreCase) ||
                p.Name.Equals("isYourProfile", StringComparison.OrdinalIgnoreCase));
        }

        private static object Read(object holder, string key)
            => holder?.GetType().GetProperty(key, PublicInstance)?.GetValue(holder);

        private static double Finite(object raw)
        {
            double value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new InvalidDataException("Non-finite HP/level");
            return value;
        }

        private static int ReadWhole(object raw, string label)
        {
            double v = Finite(raw);
            if (v != Math.Truncate(v) || v < 0 || v > 10000)
                throw new InvalidDataException("Invalid " + label);
            return checked((int)v);
        }

        private sealed class Slot
        {
            internal object Health;
            internal PropertyInfo Maximum, Current;
            internal object OldMax, OldCurrent;
            internal double NewMax, NewCurrent;
        }

        private static void Prefix(object __instance, object[] __args, MethodBase __originalMethod)
        {
            try
            {
                if (owner == null || __args == null) return;
                ParameterInfo[] ps = __originalMethod.GetParameters();
                int localIndex = Array.FindIndex(ps, IsExplicitLocalFlag);
                if (localIndex < 0 || !(__args[localIndex] is bool) || !(bool)__args[localIndex]) return;
                int profileIndex = Array.FindIndex(ps, p => p.ParameterType.FullName == "EFT.Profile");
                if (profileIndex < 0 || __args[profileIndex] == null) return;
                var checkedProfile = __args[profileIndex];
                var checkedInfo = Read(checkedProfile,"Info");
                string checkedSide = (Read(checkedProfile,"Side") ?? Read(checkedInfo,"Side"))?.ToString();
                if (!string.Equals(checkedSide,"Bear",StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(checkedSide,"Usec",StringComparison.OrdinalIgnoreCase)) return;
                // Freeze all five bonus groups and HP config BEFORE EFT enters
                // the raid. F12 edits during the raid are pending until exit.
                if (!owner.CaptureP28RaidF12(__instance)) return;
                Apply(checkedProfile);
            }
            catch (Exception ex)
            {
                if (warned) return;
                warned = true;
                log?.LogError("[GigaPMC] RAID_ENTRY_SYNC_SKIPPED|" + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Apply(object profile)
        {
            var info = Read(profile, "Info");
            if (info == null) return;
            string side = Read(info, "Side")?.ToString();
            if (!string.Equals(side, "Bear", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(side, "Usec", StringComparison.OrdinalIgnoreCase)) return;
            int level = ReadWhole(Read(info, "Level"), "PMC level");
            int prest = Read(info, "PrestigeLevel") == null ? 0 :
                ReadWhole(Read(info, "PrestigeLevel"), "prestige");
            if (level < 1) return;
            RaidHpSettings settings = owner.CurrentRaidSettings();
            if (!settings.IsValid()) throw new InvalidDataException("F12 settings invalid");

            int? cap = null;
            if (settings.AutoCap)
            {
                string capFile = Path.Combine(Paths.GameRootPath, "SPT_Runtime", "user", "mods",
                    "GigaPMC", "config", "server-level-cap.txt");
                if (File.Exists(capFile))
                {
                    int parsed;
                    if (int.TryParse(File.ReadAllText(capFile).Trim(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out parsed)) cap = parsed;
                }
            }
            int maxLevel = settings.ResolveCap(cap);
            var bodyParts = Read(Read(profile, "Health"), "BodyParts") as IDictionary;
            if (bodyParts == null) throw new MissingMemberException("EFT.Profile.Health.BodyParts is not a dictionary");

            var slots = new List<Slot>();
            for (int i = 0; i < Names.Length; i++)
            {
                string name = Names[i];
                object key = null;
                foreach (var k in bodyParts.Keys)
                    if (string.Equals(k?.ToString(), name, StringComparison.OrdinalIgnoreCase)) { key = k; break; }
                if (key == null) throw new MissingMemberException(name + " missing");
                object part = bodyParts[key];
                object health = Read(part, "Health");
                if (health == null) throw new MissingMemberException(name + ".Health missing");
                var pmax = health.GetType().GetProperty("Maximum", PublicInstance);
                var pcur = health.GetType().GetProperty("Current", PublicInstance);
                if (pmax == null || pcur == null || !pmax.CanWrite || !pcur.CanWrite)
                    throw new MissingMemberException(name + " health properties not writable");
                var origMax = pmax.GetValue(health);
                var origCur = pcur.GetValue(health);
                double oldMax = Finite(origMax), oldCur = Finite(origCur);
                if (oldMax <= 0 || oldMax > 200000 || oldCur < 0 || oldCur > oldMax)
                    throw new InvalidDataException(name + " HP invalid");
                double desiredMax = Base[i] + (settings.Enabled ? settings.Bonus(level, prest, maxLevel, name) : 0);
                if (desiredMax <= 0 || desiredMax > 200000) throw new InvalidDataException(name + " desired HP invalid");
                double desiredCur = oldCur <= 0 ? 0 :
                    (desiredMax < oldMax ? Math.Max(0, Math.Min(desiredMax, desiredMax * oldCur / oldMax)) :
                                           Math.Max(0, Math.Min(desiredMax, desiredMax - (oldMax - oldCur))));
                slots.Add(new Slot { Health = health, Maximum = pmax, Current = pcur,
                    OldMax = origMax, OldCurrent = origCur, NewMax = desiredMax, NewCurrent = desiredCur });
            }
            int index = 0;
            try
            {
                for (; index < slots.Count; index++)
                {
                    var slot = slots[index];
                    slot.Maximum.SetValue(slot.Health, Convert.ChangeType(slot.NewMax, slot.Maximum.PropertyType, CultureInfo.InvariantCulture));
                    slot.Current.SetValue(slot.Health, Convert.ChangeType(slot.NewCurrent, slot.Current.PropertyType, CultureInfo.InvariantCulture));
                }
            }
            catch
            {
                for (int i = Math.Min(index, slots.Count - 1); i >= 0; i--)
                {
                    try { slots[i].Maximum.SetValue(slots[i].Health, slots[i].OldMax);
                          slots[i].Current.SetValue(slots[i].Health, slots[i].OldCurrent); }
                    catch { /* fail closed: best-effort restore, report in log */ }
                }
                throw;
            }
            log?.LogInfo("[GigaPMC] RAID_ENTRY_HP_APPLIED|L" + level + "|P" + prest +
                "|CAP" + maxLevel + "|" + string.Join("|", slots.Select((s, i) =>
                    Names[i] + "=" + s.NewMax.ToString("0", CultureInfo.InvariantCulture))));
        }
    }
}
