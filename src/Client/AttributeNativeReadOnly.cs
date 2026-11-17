using System;
using System.Reflection;

namespace GigaPMC.Client
{
    // P20: reads exactly TWO native EFT fields for DEV VISION, never writes.
    // StaminaCapacity is NOT current stamina. BaseOverweightLimits.y is NOT
    // a complete carry-capacity model. Neither is GigaPMC's applied output.
    internal sealed class AttributeNativeReadOnly
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public |
            BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        internal float StaminaCapacity { get; }
        internal float BaseOverweightUpper { get; }
        private AttributeNativeReadOnly(float stamina, float upper)
        { StaminaCapacity = stamina; BaseOverweightUpper = upper; }

        internal static bool TryReadPinned(object physical, bool explicitlyLocalPmc,
            out AttributeNativeReadOnly snapshot, out string failure)
        {
            snapshot = null;
            failure = "NOT_LOCAL_PMC_OR_NO_PHYSICAL";
            if (!explicitlyLocalPmc || physical == null) return false;
            Type actual = physical.GetType();
            if (actual.Assembly.GetName().Name != "Assembly-CSharp")
            { failure = "UNPINNED_ASSEMBLY"; return false; }
            Type owner = null;
            for (Type t = actual; t != null; t = t.BaseType)
                if (t.FullName == "PhysicalBase") { owner = t; break; }
            if (owner == null)
            { failure = "PHYSICAL_BASE_TYPE_MISSING"; return false; }
            return TryReadCore(physical, owner, true, out snapshot, out failure);
        }

#if GIGAPMC_OFFLINE_TESTS
        // Synthetic-only test seam: deliberately cannot be called by the game DLL.
        internal static bool TryReadSyntheticForTests(object source, bool local,
            out AttributeNativeReadOnly snapshot, out string failure)
        {
            snapshot = null;
            failure = "NOT_LOCAL_PMC_OR_NO_PHYSICAL";
            if (!local || source == null) return false;
            return TryReadCore(source, source.GetType(), false, out snapshot, out failure);
        }
#endif
        private static bool TryReadCore(object physical, Type owner, bool pinned,
            out AttributeNativeReadOnly snapshot, out string failure)
        {
            snapshot = null;
            failure = "NATIVE_FIELDS_MISSING_OR_DIFFERENT_ABI";
            try
            {
                FieldInfo capacity = owner.GetField("StaminaCapacity", Fields);
                FieldInfo limits = owner.GetField("BaseOverweightLimits", Fields);
                if (capacity == null || limits == null || capacity.FieldType != typeof(float)) return false;
                if (pinned && (capacity.MetadataToken != 0x04002932 ||
                    limits.MetadataToken != 0x04002939 ||
                    limits.FieldType.FullName != "UnityEngine.Vector2" ||
                    owner.Assembly.GetName().Name != "Assembly-CSharp")) return false;
                object raw = limits.GetValue(physical);
                if (raw == null) return false;
                FieldInfo x = raw.GetType().GetField("x", Fields);
                FieldInfo y = raw.GetType().GetField("y", Fields);
                if (x == null || y == null || x.FieldType != typeof(float) ||
                    y.FieldType != typeof(float)) return false;
                float s = (float)capacity.GetValue(physical);
                float low = (float)x.GetValue(raw);
                float high = (float)y.GetValue(raw);
                failure = "INVALID_NATIVE_FIELD_VALUES";
                if (!Finite(s) || !Finite(low) || !Finite(high) ||
                    s <= 0f || s > 100000f || low < 0f || high <= 0f ||
                    high > 100000f || low > high) return false;
                snapshot = new AttributeNativeReadOnly(s, high);
                failure = "NONE_NATIVE_FIELDS_UNCERTIFIED";
                return true;
            }
            catch (Exception)
            {
                failure = "NATIVE_REFLECTION_FAILURE";
                return false;
            }
        }
        private static bool Finite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
