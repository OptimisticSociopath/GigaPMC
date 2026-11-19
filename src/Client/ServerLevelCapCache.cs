using System;
using System.Globalization;
using System.IO;
using BepInEx;

namespace GigaPMC.Client
{
    // Shared between menu, Carry, Stamina and other raid effects.
    // Good values are immutable for a process; missing/invalid files are NOT
    // permanently cached. Only callers on native/menu events can trigger IO.
    internal static class ServerLevelCapCache
    {
        private static readonly object Sync = new object();
        private static int? valid;
        private static DateTime nextAttemptUtc;
        internal static int? Read()
        {
            lock (Sync)
            {
                if (valid.HasValue) return valid;
                if (DateTime.UtcNow < nextAttemptUtc) return null;
                nextAttemptUtc = DateTime.UtcNow.AddSeconds(5);
                try
                {
                    string path=Path.Combine(Paths.GameRootPath,"SPT_Runtime","user","mods",
                        "GigaPMC","config","server-level-cap.txt");
                    if (!File.Exists(path)) return null;
                    int parsed;
                    if (!int.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out parsed) || parsed<1 || parsed>1000)
                        return null;
                    valid=parsed;
                    return valid;
                }
                catch (IOException) { return null; }
                catch (UnauthorizedAccessException) { return null; }
            }
        }
        // Only a verified menu transition may request a new read after failure;
        // NEVER clear a successful cap during a raid or recalculate live bonuses.
        internal static void OnMenuReady()
        {
            lock (Sync) if (!valid.HasValue) nextAttemptUtc=DateTime.MinValue;
        }
    }
}
