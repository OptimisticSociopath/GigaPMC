using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Helpers.Profile;

namespace GigaPMC.Server;

// SPT 4.1 metadata: no package.json and no JS entry point.
public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.vargor.gigapmc";
    public string Name { get; init; } = "GigaPMC - For Tarkov Enjoyers";
    public string Author { get; init; } = "Vargor / OptimisticSociopath";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("2.0.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.6");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public string License { get; init; } = "MIT";
    public bool HasPrepatcher { get; init; } = false;
}

[Injectable(TypePriority = OnLoadOrder.PostLoad)]
public sealed class ServerEntry(ISptLogger<ServerEntry> log, PlayerProfileHealthPatch patch,
    ProfileHelper profileHelper) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            patch.Enable();
            var fika = FikaRules.IsFikaBackend(AppContext.BaseDirectory);
            if (!fika)
            {
                try { LevelCapCache.Publish(AppContext.BaseDirectory, profileHelper.GetMaxLevel()); }
                catch (Exception capError) {
                    Console.WriteLine("[GigaPMC] Client raid level-cap cache unavailable: " + capError.Message);
                }
            }
            log.Success(fika
                ? "[GigaPMC] Fika backend detected: per-PMC HP active; settings are server-authoritative (config/fika-server-settings.json, or defaults)."
                : "[GigaPMC] Single-player backend: per-PMC HP active; local F12 overrides enabled.");
        }
        catch (Exception e)
        {
            // Never prevent the server from starting if a future SPT patch renames the target.
            log.Error("[GigaPMC] Hook disabled (ABI mismatch): " + e.GetType().Name + ": " + e.Message);
        }
        return Task.CompletedTask;
    }
}

[Injectable]
public sealed class PlayerProfileHealthPatch : AbstractPatch
{
    private static readonly string[] Parts = { "Head", "Chest", "Stomach", "LeftArm", "RightArm", "LeftLeg", "RightLeg" };
    private static readonly double[] BaseHp = { 35, 85, 70, 60, 60, 65, 65 };
    private static readonly object Guard = new();
    private static bool _alreadyWarned;
    private static string? _lastAppliedSummary;

    protected override MethodBase GetTargetMethod()
    {
        var core = typeof(IModMetadata).Assembly;
        var targetType = core.GetType("SPTarkov.Server.Core.Helpers.Profile.ProfileHelper", false)
                      ?? core.GetTypes().FirstOrDefault(t => t.Name == "ProfileHelper" && t.Namespace?.Contains("Helpers.Profile") == true);
        if (targetType == null)
            throw new MissingMemberException("SPT ProfileHelper type not found");

        var matches = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.Name == "GetPmcProfile" && m.GetParameters().Length == 1 && !typeof(Task).IsAssignableFrom(m.ReturnType))
            .ToArray();
        if (matches.Length != 1)
            throw new MissingMemberException("Expected exactly one synchronous GetPmcProfile(sessionId) overload; got " + matches.Length);
        return matches[0];
    }

    [PatchPostfix]
    public static void Postfix(object __instance, object __result)
    {
        if (__result == null) return;
        try { Apply(__instance, __result); }
        catch (Exception e)
        {
            lock (Guard)
            {
                if (_alreadyWarned) return;
                _alreadyWarned = true;
                Console.WriteLine("[GigaPMC] Profile health left unchanged: " + e.GetType().Name + ": " + e.Message);
            }
        }
    }

    private static object? Read(object? owner, string name)
        => owner?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(owner);

    private static double NeedDouble(object? obj, string label)
    {
        if (obj == null) throw new InvalidDataException(label + " missing");
        var n = Convert.ToDouble(obj, System.Globalization.CultureInfo.InvariantCulture);
        if (!double.IsFinite(n)) throw new InvalidDataException(label + " nonfinite");
        return n;
    }

    private static VitalitySettings LoadSettings()
    {
        var sptRuntime = AppContext.BaseDirectory;
        var fika = FikaRules.IsFikaBackend(sptRuntime);
        return FikaRules.LoadSettings(sptRuntime, fika);
    }

    private sealed record Plan(object Health, PropertyInfo MaxProp, PropertyInfo CurrentProp,
        double OldMaximum, double OldCurrent, double NewMaximum, double NewCurrent);

    private static void Apply(object profileHelper, object profile)
    {
        var info = Read(profile, "Info");
        var health = Read(profile, "Health");
        var parts = Read(health, "BodyParts") as IDictionary;
        if (info == null || parts == null) return;

        // Older profiles may have a null PrestigeLevel before the first prestige.
        // Null means zero, but malformed non-null prestige values fail closed.
        var level = (int)NeedDouble(Read(info, "Level"), "Info.Level");
        var prestige = FikaRules.ReadPrestige(Read(info, "PrestigeLevel"));
        if (level < 1 || prestige < 0 || level > 10000 || prestige > 10000) return;

        var settings = LoadSettings();
        // Use the live server's XP progression table via ProfileHelper.GetMaxLevel().
        // No hardcoded 79 when a progression mod extends the game level cap.
        int? detectedGameMax = null;
        if (settings.Enabled && settings.AutoDetectMaxPmcLevel)
        {
            var method = profileHelper?.GetType().GetMethod("GetMaxLevel", BindingFlags.Public | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
            if (method == null || method.ReturnType != typeof(int))
                throw new MissingMemberException("ProfileHelper.GetMaxLevel() not available; refusing to guess max level");
            detectedGameMax = (int)method.Invoke(profileHelper, null)!;
        }
        int maxLevel = settings.Enabled ? VitalityMath.ResolveMaxLevel(settings, detectedGameMax) : settings.MaxPmcLevel;
        // Keep local client raid-entry math aligned with SPT's active XP table.
        if (!FikaRules.IsFikaBackend(AppContext.BaseDirectory))
        {
            try
            {
                int? cacheCap = detectedGameMax;
                if (!cacheCap.HasValue)
                {
                    var capGetter = profileHelper?.GetType().GetMethod("GetMaxLevel", Type.EmptyTypes);
                    if (capGetter?.Invoke(profileHelper, null) is int result) cacheCap = result;
                }
                if (cacheCap.HasValue) LevelCapCache.Publish(AppContext.BaseDirectory, cacheCap.Value);
            }
            catch (Exception) { /* server-side HP remains authoritative */ }
        }

        var plan = new List<Plan>();
        for (int i = 0; i < Parts.Length; i++)
        {
            if (!parts.Contains(Parts[i])) return; // No partial seven-part writes.
            var entry = parts[Parts[i]];
            var partHealth = Read(entry, "Health");
            if (partHealth == null) return;
            var maxProp = partHealth.GetType().GetProperty("Maximum");
            var currentProp = partHealth.GetType().GetProperty("Current");
            if (maxProp == null || currentProp == null || !maxProp.CanWrite || !currentProp.CanWrite) return;
            double oldMax = NeedDouble(maxProp.GetValue(partHealth), Parts[i] + ".Maximum");
            double oldCurrent = NeedDouble(currentProp.GetValue(partHealth), Parts[i] + ".Current");
            if (oldMax <= 0 || oldCurrent < 0 || oldCurrent > oldMax || oldMax > 200000) return;
            long bonus = settings.Enabled ? VitalityMath.BonusForPart(level, prestige, settings, maxLevel, Parts[i]) : 0;
            if (bonus < 0 || bonus > 200000) return; // No partial body-part writes.
            var newMax = BaseHp[i] + bonus;
            if (newMax > 200000) return;
            var wounds = oldMax - oldCurrent;
            // When maximum health shrinks (prestige / F12 changes), preserve the
            // fraction of remaining HP so a living limb is not destroyed solely by
            // a change in the maximum. When it grows, preserve absolute wounds.
            var newCurrent = oldCurrent <= 0 ? 0 :
                (newMax < oldMax ? Math.Max(0, Math.Min(newMax, newMax * oldCurrent / oldMax)) :
                                   Math.Max(0, Math.Min(newMax, newMax - wounds)));
            plan.Add(new Plan(partHealth, maxProp, currentProp, oldMax, oldCurrent, newMax, newCurrent));
        }

        lock (profile)
        {
            int index = 0;
            try
            {
                for (; index < plan.Count; index++)
                {
                    var p = plan[index];
                    p.MaxProp.SetValue(p.Health, p.NewMaximum);
                    p.CurrentProp.SetValue(p.Health, p.NewCurrent);
                }
            }
            catch
            {
                // If a setter fails, recover all seven slots, including the current one.
                for (int i = Math.Min(index, plan.Count - 1); i >= 0; i--)
                {
                    try {
                        plan[i].MaxProp.SetValue(plan[i].Health, plan[i].OldMaximum);
                              plan[i].CurrentProp.SetValue(plan[i].Health, plan[i].OldCurrent); }
                    catch { /* caller gets failure; no further writing attempted */ }
                }
                throw;
            }
        }

        // Observable confirmation that the server actually applied the CURRENT
        // JSON settings. F12 alone does not guarantee the backend processed them.
        string summary = $"L{level}|P{prestige}|CAP{maxLevel}|" +
            string.Join("|", plan.Select((p, i) => Parts[i] + "=" + p.NewMaximum.ToString("0", System.Globalization.CultureInfo.InvariantCulture)));
        lock (Guard)
        {
            if (!string.Equals(summary, _lastAppliedSummary, StringComparison.Ordinal))
            {
                _lastAppliedSummary = summary;
                Console.WriteLine("[GigaPMC] APPLY_PMC_HEALTH|" + summary);
            }
        }
    }
}

// Small, non-profile cache so the local client can detect a non-79 game cap
// before Player.Init. Published from the running SPT server; not user-editable.
internal static class LevelCapCache
{
    private static int _lastCap = -1;
    internal static void Publish(string runtime, int cap)
    {
        if (cap < 1 || cap > 1000) throw new InvalidDataException("SPT max level out of range");
        if (_lastCap == cap) return;
        string dir = Path.Combine(runtime, "user", "mods", "GigaPMC", "config");
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException(dir);
        string target = Path.Combine(dir, "server-level-cap.txt");
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, cap.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.Move(temporary, target, true);
            _lastCap = cap;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

