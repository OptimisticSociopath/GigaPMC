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

    public static VitalitySettings LoadSettings(string sptRuntime, bool isFika)
    {
        var file = GetSettingsPath(sptRuntime, isFika);
        if (!File.Exists(file)) return new VitalitySettings();
        try
        {
            var config = JsonSerializer.Deserialize<VitalitySettings>(File.ReadAllText(file),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            // Invalid configs safely use defaults: never use partially parsed values.
            return config != null && config.IsValid() ? config : new VitalitySettings();
        }
        catch (IOException) { return new VitalitySettings(); }
        catch (UnauthorizedAccessException) { return new VitalitySettings(); }
        catch (JsonException) { return new VitalitySettings(); }
    }
}
