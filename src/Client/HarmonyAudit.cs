using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace GigaPMC.Client
{
    // Verify Harmony's actual registered owner, callback name and patch kind.
    // This proves registration ONLY. It cannot prove an EFT call executed.
    internal static class HarmonyAudit
    {
        internal static void Require(MethodBase target,string owner,string callback,string kind)
        {
            if (target == null || string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(callback))
                throw new InvalidOperationException("P36_HARMONY_AUDIT_INVALID_INPUT");
            var info = Harmony.GetPatchInfo(target);
            if (info == null) throw new InvalidOperationException("P36_HARMONY_NO_PATCH_INFO_"+kind);
            System.Collections.Generic.IEnumerable<Patch> matches;
            switch (kind)
            {
                case "prefix": matches = info.Prefixes; break;
                case "postfix": matches = info.Postfixes; break;
                case "transpiler": matches = info.Transpilers; break;
                default: throw new InvalidOperationException("P36_UNKNOWN_PATCH_KIND");
            }
            if (!matches.Any(p=>string.Equals(p.owner,owner,StringComparison.Ordinal) &&
                p.PatchMethod != null && p.PatchMethod.Name == callback &&
                p.PatchMethod.DeclaringType != null &&
                p.PatchMethod.DeclaringType.Assembly == typeof(HarmonyAudit).Assembly))
                throw new InvalidOperationException("P36_HARMONY_OWNER_NOT_REGISTERED_"+owner+"_"+callback);
        }
    }
}
