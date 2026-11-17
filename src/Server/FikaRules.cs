using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GigaPMC.Server;

/// <summary>
/// Fika's SPT backend is shared by all human players. The shared backend owns the
/// numeric settings and each profile gets its own level/prestige calculation.
/// No Fika API assembly is required. This is a backend/profile compatibility layer,
/// not a claim of verified in-raid health replication.
/// </summary>
public static class FikaRules
{
    public static bool IsFikaBackend(string sptRuntimeDirectory, string[]? loadedAssemblyNames = null)
    {
        var mods = Path.Combine(sptRuntimeDirectory, "user", "mods");
        if (Directory.Exists(mods))
        {
            foreach (var directory in Directory.EnumerateDirectories(mods))
            {
                var name = Path.GetFileName(directory);
                if (name.Equals("fika-server", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Fika-Server", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        if (loadedAssemblyNames == null)
            loadedAssemblyNames = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetName().Name ?? "").ToArray();

        return loadedAssemblyNames.Any(n => n.Equals("Fika.Server", StringComparison.OrdinalIgnoreCase) ||
                                            n.Equals("Fika.Server.Core", StringComparison.OrdinalIgnoreCase) ||
                                            n.Equals("FikaServer", StringComparison.OrdinalIgnoreCase));
    }

    // Older real profiles can carry a null PrestigeLevel even at prestige zero.
    public static int ReadPrestige(object? raw)
    {
        if (raw == null) return 0;
        var n = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
        if (!double.IsFinite(n) || n < 0 || n > 10000 || n != Math.Truncate(n))
            throw new InvalidDataException("Invalid Info.PrestigeLevel");
        return checked((int)n);
    }

    public static string GetSettingsPath(string sptRuntime, bool isFika)
        => Path.Combine(sptRuntime, "user", "mods", "GigaPMC", "config",
            isFika ? "fika-server-settings.json" : "f12-overrides.json");

    private static bool HasPropertyIgnoreCase(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static VitalitySettings LoadSettings(string sptRuntime, bool isFika)
    {
        var file = GetSettingsPath(sptRuntime, isFika);
        if (!File.Exists(file)) return new VitalitySettings();
        try
        {
            var config = JsonSerializer.Deserialize<VitalitySettings>(File.ReadAllText(file),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            // A former configuration may still contain obsolete single-rate and
            // mode-switch fields. Both are deliberately ignored: seven
            // per-body-part values are now always authoritative.
            using var parsed = JsonDocument.Parse(File.ReadAllText(file));
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                return new VitalitySettings();
            if (config != null && !HasPropertyIgnoreCase(parsed.RootElement, "ConfigSchemaVersion"))
            {
                // Only upgrade the previous published default values. Nondefault
                // custom per-part settings remain unchanged. Schema 110 files
                // already have explicit values and are not reset by upgrading.
                if (config.ChestHpPerLevel == 2) config.ChestHpPerLevel = 5;
                if (config.StomachHpPerLevel == 1) config.StomachHpPerLevel = 4;
                if (config.BonusHpAtMaxLevel == 12) config.BonusHpAtMaxLevel = 15;
                config.ConfigSchemaVersion = 200;
            }
            // Invalid configs safely use defaults: never use partially parsed values.
            return config != null && config.IsValid() ? config : new VitalitySettings();
        }
        catch (IOException) { return new VitalitySettings(); }
        catch (UnauthorizedAccessException) { return new VitalitySettings(); }
        catch (JsonException) { return new VitalitySettings(); }
    }
}
