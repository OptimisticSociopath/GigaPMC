#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace GigaPMC.Client
{
    // Pure, testable migration of pre-1.1.0 F12 keys. Never edits old files.
    // After new settings are bound BepInEx persists the new names on its own.
    public sealed class LegacySettingsReader
    {
        private readonly string cfgText;
        private readonly string jsonText;

        public LegacySettingsReader(string cfgText, string jsonText)
        {
            this.cfgText = cfgText ?? string.Empty;
            this.jsonText = jsonText ?? string.Empty;
        }

        public static LegacySettingsReader FromFiles(string cfgPath, string jsonPath)
        {
            string cfg = File.Exists(cfgPath) ? File.ReadAllText(cfgPath) : string.Empty;
            string json = File.Exists(jsonPath) ? File.ReadAllText(jsonPath) : string.Empty;
            return new LegacySettingsReader(cfg, json);
        }

        public string? ReadCfg(string section, string key)
        {
            string currentSection = string.Empty;
            using (var reader = new StringReader(cfgText))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith("#") || trimmed.StartsWith(";")) continue;
                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        currentSection = trimmed.Substring(1, trimmed.Length - 2);
                        continue;
                    }
                    int pos = trimmed.IndexOf('=');
                    if (pos <= 0 || currentSection != section) continue;
                    if (trimmed.Substring(0, pos).Trim() == key)
                        return trimmed.Substring(pos + 1).Trim();
                }
            }
            return null;
        }

        private string? ReadJsonValue(string field)
        {
            // Numeric/boolean scalar values only. No general JSON parser is
            // required in the client, which targets netstandard2.1.
            var match = Regex.Match(jsonText,
                "\\\"" + Regex.Escape(field) + "\\\"\\s*:\\s*(?<v>true|false|-?[0-9]+)\\s*(?=[,}])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success ? match.Groups["v"].Value : null;
        }

        public int Int(string oldSection, string oldKey, string jsonKey,
                       int newDefault, int minValue, int maxValue, int? previousDefault = null)
        {
            string? raw = ReadCfg(oldSection, oldKey) ?? ReadJsonValue(jsonKey);
            int result;
            if (raw == null || !int.TryParse(raw, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out result) || result < minValue || result > maxValue)
                return newDefault;
            if (previousDefault.HasValue && result == previousDefault.Value)
                return newDefault;
            return result;
        }

        public bool Bool(string oldSection, string oldKey, string jsonKey, bool newDefault)
        {
            string? raw = ReadCfg(oldSection, oldKey) ?? ReadJsonValue(jsonKey);
            bool result;
            return raw != null && bool.TryParse(raw, out result) ? result : newDefault;
        }
    }
}
