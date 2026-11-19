using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GigaPMC.Client
{
    // Flat, strict mirror of the server's VitalitySettings JSON scalar schema.
    // Only the *explicitly opted-in, same-machine SPT server administrator*
    // may write this file. Remote clients cannot edit shared Fika HP through F12.
    public static class FikaHpHostConfig
    {
        private static readonly Regex KeyToken = new Regex("\"(?<key>[A-Za-z][A-Za-z0-9]*)\"",
            RegexOptions.CultureInvariant);
        private static readonly Regex ValueToken = new Regex(@"(?:true|false|-?[0-9]+)",
            RegexOptions.CultureInvariant);
        private static readonly HashSet<string> Keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "ConfigSchemaVersion", "Enabled", "HeadHpPerLevel", "ChestHpPerLevel", "StomachHpPerLevel",
            "LeftArmHpPerLevel", "RightArmHpPerLevel", "LeftLegHpPerLevel", "RightLegHpPerLevel",
            "BonusHpEvery10Levels", "BonusHpAtMaxLevel", "BonusHpPerPrestige", "PrestigeMultiplicative",
            "AutoDetectMaxPmcLevel", "MaxPmcLevel"
        };

        private static void Skip(string json, ref int pos)
        {
            while (pos < json.Length && char.IsWhiteSpace(json[pos])) pos++;
        }

        public static bool TryParse(string json, out RaidHpSettings settings)
        {
            settings = new RaidHpSettings();
            if (string.IsNullOrWhiteSpace(json)) return false;
            int pos = 0;
            Skip(json, ref pos);
            if (pos >= json.Length || json[pos++] != '{') return false;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool first = true;
            while (true)
            {
                Skip(json, ref pos);
                if (pos >= json.Length) return false;
                if (json[pos] == '}')
                {
                    pos++; Skip(json, ref pos);
                    if (pos != json.Length || !settings.IsValid()) return false;
                    // Match FikaRules.LoadSettings migration for pre-schema files.
                    if (!seen.Contains("ConfigSchemaVersion"))
                    {
                        if (settings.Chest == 2) settings.Chest = 5;
                        if (settings.Stomach == 1) settings.Stomach = 4;
                        if (settings.AtMax == 12) settings.AtMax = 15;
                    }
                    return true;
                }
                if (!first)
                {
                    if (json[pos++] != ',') return false;
                    Skip(json, ref pos);
                }
                first = false;
                var key = KeyToken.Match(json, pos);
                if (!key.Success || key.Index != pos) return false;
                string name = key.Groups["key"].Value;
                if (!Keys.Contains(name) || !seen.Add(name)) return false;
                pos += key.Length;
                Skip(json, ref pos);
                if (pos >= json.Length || json[pos++] != ':') return false;
                Skip(json, ref pos);
                var value = ValueToken.Match(json, pos);
                if (!value.Success || value.Index != pos) return false;
                pos += value.Length;
                string raw = value.Value;
                int number;
                bool boolean;
                switch (name.ToLowerInvariant())
                {
                    case "configschemaversion":
                        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ||
                            number != 0 && number != 110 && number != 200) return false;
                        break;
                    case "enabled":
                        if (!bool.TryParse(raw, out boolean)) return false;
                        settings.Enabled = boolean; break;
                    case "prestigemultiplicative":
                        if (!bool.TryParse(raw, out boolean)) return false;
                        settings.Multiplicative = boolean; break;
                    case "autodetectmaxpmclevel":
                        if (!bool.TryParse(raw, out boolean)) return false;
                        settings.AutoCap = boolean; break;
                    default:
                        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return false;
                        if (name.Equals("HeadHpPerLevel", StringComparison.OrdinalIgnoreCase)) settings.Head = number;
                        else if (name.Equals("ChestHpPerLevel", StringComparison.OrdinalIgnoreCase)) settings.Chest = number;
                        else if (name.Equals("StomachHpPerLevel", StringComparison.OrdinalIgnoreCase)) settings.Stomach = number;
                        else if (name.Equals("LeftArmHpPerLevel", StringComparison.OrdinalIgnoreCase)) settings.LeftArm = number;
                        else if (name.Equals("RightArmHpPerLevel", StringComparison.OrdinalIgnoreCase)) settings.RightArm = number;
                        else if (name.Equals("LeftLegHpPerLevel", StringComparison.OrdinalIgnoreCase)) settings.LeftLeg = number;
                        else if (name.Equals("RightLegHpPerLevel", StringComparison.OrdinalIgnoreCase)) settings.RightLeg = number;
                        else if (name.Equals("BonusHpEvery10Levels", StringComparison.OrdinalIgnoreCase)) settings.EveryTen = number;
                        else if (name.Equals("BonusHpAtMaxLevel", StringComparison.OrdinalIgnoreCase)) settings.AtMax = number;
                        else if (name.Equals("BonusHpPerPrestige", StringComparison.OrdinalIgnoreCase)) settings.Prestige = number;
                        else if (name.Equals("MaxPmcLevel", StringComparison.OrdinalIgnoreCase)) settings.ManualCap = number;
                        else return false;
                        break;
                }
            }
        }

        public static bool TryRead(string path, out RaidHpSettings settings, out string reason)
        {
            settings = new RaidHpSettings();
            reason = null;
            try
            {
                if (!File.Exists(path)) return true; // Same defaults as SPT server.
                if (!TryParse(File.ReadAllText(path), out settings))
                { reason = "INVALID_FIKA_HP_JSON"; return false; }
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            { reason = ex.GetType().Name; return false; }
        }

        public static string ToJson(RaidHpSettings s)
        {
            if (s == null || !s.IsValid()) throw new ArgumentException("Invalid server HP settings");
            string B(bool value) => value ? "true" : "false";
            return "{\n" +
                "  \"ConfigSchemaVersion\": 200,\n" +
                "  \"Enabled\": " + B(s.Enabled) + ",\n" +
                "  \"HeadHpPerLevel\": " + s.Head + ",\n" +
                "  \"ChestHpPerLevel\": " + s.Chest + ",\n" +
                "  \"StomachHpPerLevel\": " + s.Stomach + ",\n" +
                "  \"LeftArmHpPerLevel\": " + s.LeftArm + ",\n" +
                "  \"RightArmHpPerLevel\": " + s.RightArm + ",\n" +
                "  \"LeftLegHpPerLevel\": " + s.LeftLeg + ",\n" +
                "  \"RightLegHpPerLevel\": " + s.RightLeg + ",\n" +
                "  \"BonusHpEvery10Levels\": " + s.EveryTen + ",\n" +
                "  \"BonusHpAtMaxLevel\": " + s.AtMax + ",\n" +
                "  \"BonusHpPerPrestige\": " + s.Prestige + ",\n" +
                "  \"PrestigeMultiplicative\": " + B(s.Multiplicative) + ",\n" +
                "  \"AutoDetectMaxPmcLevel\": " + B(s.AutoCap) + ",\n" +
                "  \"MaxPmcLevel\": " + s.ManualCap + "\n}\n";
        }

        public static void WriteAtomic(string path, RaidHpSettings settings)
        {
            string json = ToJson(settings);
            string directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                throw new DirectoryNotFoundException(directory);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
