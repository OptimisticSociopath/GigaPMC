using System;
using System.Reflection;

namespace GigaPMC.Client
{
    // P18: strictly reads original EFT StaminaParameters.Capacity metadata field.
    // Provenance is LIMITED to an assembly/field identity; live config may have
    // been changed by SPT/mod loaders. It is NOT certified as unmodified.
    internal enum CapacityOrigin
    {
        Unknown = 0,
        PinnedGlobalConfigurationReadUncertified = 1
    }

    internal sealed class AttributeStaminaConfig
    {
        private const BindingFlags InstanceFields = BindingFlags.Instance |
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        internal float Capacity { get; }
        internal CapacityOrigin Origin => CapacityOrigin.PinnedGlobalConfigurationReadUncertified;
        private AttributeStaminaConfig(float capacity) { Capacity = capacity; }

        // P17 IL: PhysicalBase::.ctor 0x0600353F, ldfield 0x040068BF,
        // ldfield StaminaParameters.Capacity 0x0400E9C9, stfield 0x04002932.
        internal static bool TryReadPinned(object global, out AttributeStaminaConfig snapshot,
            out string failure)
        {
            return TryRead(global, "EFT.GlobalConfiguration", "Assembly-CSharp",
                0x040068BF, 0x0400E9C9, out snapshot, out failure);
        }

#if GIGAPMC_OFFLINE_TESTS
        internal static bool TryReadSyntheticForTests(object global,
            out AttributeStaminaConfig snapshot, out string failure)
        {
            snapshot = null;
            failure = "SYNTHETIC_ABI_INVALID";
            if (global == null) return false;
            Type root = global.GetType();
            FieldInfo sf = root.GetField("Stamina", InstanceFields);
            if (sf == null || sf.FieldType.FullName !=
                "EFT.GlobalConfiguration+StaminaParameters") return false;
            FieldInfo capacity = sf.FieldType.GetField("Capacity", InstanceFields);
            if (capacity == null) return false;
            return TryRead(global, "EFT.GlobalConfiguration", root.Assembly.GetName().Name,
                sf.MetadataToken, capacity.MetadataToken, out snapshot, out failure);
        }
#endif
        private static bool TryRead(object global, string expectedRoot,
            string expectedAssembly, int staminaToken, int capacityToken,
            out AttributeStaminaConfig snapshot, out string failure)
        {
            snapshot = null;
            failure = "UNKNOWN";
            try
            {
                if (global == null || global.GetType().FullName != expectedRoot)
                    return Reject("ROOT_TYPE", out failure);
                Type root = global.GetType();
                if (!string.Equals(root.Assembly.GetName().Name, expectedAssembly,
                    StringComparison.Ordinal)) return Reject("ROOT_ASSEMBLY", out failure);
                FieldInfo sf = root.GetField("Stamina", InstanceFields);
                if (sf == null || sf.IsStatic || sf.MetadataToken != staminaToken ||
                    sf.FieldType.FullName != "EFT.GlobalConfiguration+StaminaParameters" ||
                    !ReferenceEquals(sf.FieldType.Assembly, root.Assembly))
                    return Reject("STAMINA_ABI", out failure);
                object param = sf.GetValue(global);
                if (param == null) return Reject("PARAMS_NULL", out failure);
                FieldInfo cf = sf.FieldType.GetField("Capacity", InstanceFields);
                if (cf == null || cf.IsStatic || cf.FieldType != typeof(float) ||
                    cf.MetadataToken != capacityToken) return Reject("CAPACITY_ABI", out failure);
                object value = cf.GetValue(param);
                if (!(value is float f) || float.IsNaN(f) || float.IsInfinity(f) ||
                    f <= 0f || f > 100000f) return Reject("CAPACITY_INVALID", out failure);
                snapshot = new AttributeStaminaConfig(f);
                failure = "NONE";
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException ||
                ex is AmbiguousMatchException || ex is TargetException ||
                ex is TargetInvocationException || ex is FieldAccessException ||
                ex is InvalidOperationException)
            {
                return Reject("REFLECTION_ABI_FAILURE", out failure);
            }
        }
        private static bool Reject(string code, out string failure)
        { failure = code; return false; }
    }
}
