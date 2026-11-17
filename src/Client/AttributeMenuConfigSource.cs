using System;
using System.Linq;
using System.Reflection;

namespace GigaPMC.Client
{
    // P25 read-only, optional MAIN-MENU native CONFIG source; the same EFT
    // singleton shape used by P24, and the same pinned P17/P18 field readers.
    // Never report this as a live PMC value or an unmodified pre-mod baseline.
    internal static class AttributeMenuConfigSource
    {
        internal static bool TryRead(out BaselineRange[] ranges, out float capacity,
            out string failure)
        {
            ranges = null; capacity = 0f; failure = "NATIVE_CONFIG_NOT_AVAILABLE";
            try
            {
                var eft = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => a.GetName().Name == "Assembly-CSharp").ToArray();
                if (eft.Length != 1) { failure = "EFT_ASSEMBLY_NOT_UNIQUE"; return false; }
                Type global = eft[0].GetType("EFT.GlobalConfiguration",false);
                if (global == null) { failure = "GLOBAL_TYPE_MISSING"; return false; }
                Type singleton = null;
                foreach (string name in new [] {"Comfort.Common.Singleton`1", "EFT.Singleton`1"})
                {
                    var types = AppDomain.CurrentDomain.GetAssemblies()
                        .Select(a => a.GetType(name,false)).Where(t => t != null).ToArray();
                    if (types.Length == 1) { singleton = types[0]; break; }
                }
                if (singleton == null) { failure = "SINGLETON_UNAVAILABLE"; return false; }
                Type specific = singleton.MakeGenericType(global);
                var property = specific.GetProperty("Instance",BindingFlags.Public | BindingFlags.Static);
                if (property == null || property.PropertyType != global)
                { failure = "SINGLETON_ABI"; return false; }
                object native = property.GetValue(null,null);
                AttributeNativeConfig weight;
                AttributeStaminaConfig stamina;
                string reason;
                if (!AttributeNativeConfig.TryReadPinned(native,out weight,out reason))
                { failure = "WEIGHT_" + reason; return false; }
                if (!AttributeStaminaConfig.TryReadPinned(native,out stamina,out reason))
                { failure = "STAMINA_" + reason; return false; }
                ranges = weight.CopyRanges(); capacity = stamina.Capacity;
                failure = "NONE";
                return true;
            }
            catch (Exception ex)
            { failure = "CONFIG_REFLECTION_" + ex.GetType().Name; return false; }
        }
    }
}
