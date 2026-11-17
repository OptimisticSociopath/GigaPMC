using System;

namespace GigaPMC.Client
{
    // Pure validation shared by the Unity bridge and executable offline tests.
    internal static class RaidSceneRules
    {
        private static readonly string[] MapIds = {
            "bigmap","customs","factory","factory4_day","factory4_night","woods","shoreline","interchange","rezervbase",
            "reserve","lighthouse","laboratory","labs","tarkovstreets",
            "streets","sandbox","groundzero","terminal","icebreaker","labyrinth"
        };
        internal static bool IsHideout(string text) =>
            !string.IsNullOrWhiteSpace(text) &&
            text.IndexOf("hideout",StringComparison.OrdinalIgnoreCase)>=0;

        internal static bool IsKnownMap(string text)
        {
            if(string.IsNullOrWhiteSpace(text)) return false;
            string value=text.Trim().ToLowerInvariant();
            if(IsHideout(value) || value.Contains("menu") || value.Contains("launcher")) return false;
            foreach(string s in MapIds)
                if(value==s || value.StartsWith(s+"_") || value.StartsWith(s+"-") ||
                    value.EndsWith("/"+s) || value.Contains("/"+s+"_")) return true;
            return false;
        }
    }
}
