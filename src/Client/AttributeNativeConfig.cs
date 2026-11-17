using System;
using System.Reflection;

namespace GigaPMC.Client
{
    // P17: reads *configuration* weight vectors, not PhysicalBase live vectors.
    // IL 0x0600352B reads GlobalConfiguration.Stamina and four StaminaParameters
    // fields before applying skill and health modifiers.
    // This snapshot is NOT verified as an unmodified pre-mod/pre-raid config.
    // No reflection writes, no hooks, no profile access, no global singleton lookup.
    internal enum NativeWeightOrigin
    {
        Unknown = 0,
        PinnedGlobalConfigurationReadUncertified = 1
    }

    internal sealed class AttributeNativeConfig
    {
        private const BindingFlags Fields = BindingFlags.Instance |
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private static readonly string[] Names = {
            "WalkOverweightLimits", "BaseOverweightLimits",
            "SprintOverweightLimits", "WalkSpeedOverweightLimits"
        };
        private static readonly int[] PinnedTokens = {
            0x0400E9D6, 0x0400E9D7, 0x0400E9D8, 0x0400E9D9
        };
        private readonly BaselineRange[] limits;
        internal NativeWeightOrigin Origin => NativeWeightOrigin.PinnedGlobalConfigurationReadUncertified;

        private AttributeNativeConfig(BaselineRange[] source)
        {
            limits = (BaselineRange[])source.Clone();
        }
        internal BaselineRange GetRange(int index)
        {
            if (index < 0 || index >= 4) throw new ArgumentOutOfRangeException(nameof(index));
            return limits[index];
        }
        internal BaselineRange[] CopyRanges() => (BaselineRange[])limits.Clone();

        // Production path: exact owner, member names, type shape, metadata tokens,
        // and originating Assembly-CSharp assembly. This deliberately DOES NOT
        // promote Origin to CertifiedLivePermanent: other mods may have changed
        // configuration values before capture, even in the correct assembly.
        internal static bool TryReadPinned(object global, out AttributeNativeConfig snapshot,
            out string failure)
        {
            return TryReadCore(global, "EFT.GlobalConfiguration", "Assembly-CSharp",
                0x040068BF, PinnedTokens, out snapshot, out failure);
        }

#if GIGAPMC_OFFLINE_TESTS
        // Compiled ONLY into the offline test executable, never client DLL.
        // Uses the same parser with synthetic metadata tokens; does not weaken
        // the pinned production path.
        internal static bool TryReadSyntheticForTests(object global,
            out AttributeNativeConfig snapshot, out string failure)
        {
            snapshot = null;
            failure = "SYNTHETIC_SHAPE_INVALID";
            if (global == null) return false;
            Type root = global.GetType();
            FieldInfo stamina = root.GetField("Stamina", Fields);
            if (stamina == null || stamina.FieldType.FullName !=
                "EFT.GlobalConfiguration+StaminaParameters") return false;
            int[] tokenMap = new int[4];
            for (int i=0; i<4; i++)
            {
                FieldInfo f = stamina.FieldType.GetField(Names[i], Fields);
                if (f == null) return false;
                tokenMap[i]=f.MetadataToken;
            }
            return TryReadCore(global, "EFT.GlobalConfiguration", root.Assembly.GetName().Name,
                stamina.MetadataToken, tokenMap, out snapshot, out failure);
        }
#endif

        private static bool TryReadCore(object global, string expectedRoot,
            string expectedAssembly, int staminaToken, int[] vectorTokens,
            out AttributeNativeConfig snapshot, out string failure)
        {
            snapshot = null;
            failure = "UNKNOWN";
            try
            {
                if (global == null || global.GetType().FullName != expectedRoot)
                    return Reject("GLOBAL_CONFIGURATION_TYPE", out failure);
                Type root = global.GetType();
                if (!string.Equals(root.Assembly.GetName().Name,expectedAssembly,
                    StringComparison.Ordinal))
                    return Reject("GLOBAL_CONFIGURATION_ASSEMBLY",out failure);
                FieldInfo staminaField = root.GetField("Stamina", Fields);
                if (staminaField == null || staminaField.IsStatic ||
                    staminaField.MetadataToken != staminaToken ||
                    staminaField.FieldType.FullName != "EFT.GlobalConfiguration+StaminaParameters")
                    return Reject("STAMINA_FIELD_TOKEN_OR_TYPE",out failure);
                object values = staminaField.GetValue(global);
                if (values == null) return Reject("STAMINA_PARAMETERS_NULL",out failure);
                Type parameters = staminaField.FieldType;
                if (!ReferenceEquals(parameters.Assembly,root.Assembly))
                    return Reject("STAMINA_PARAMETERS_ASSEMBLY",out failure);
                var result = new BaselineRange[4];
                if (vectorTokens == null || vectorTokens.Length != 4)
                    return Reject("VECTOR_TOKEN_COUNT",out failure);
                for (int i=0; i<4; i++)
                {
                    FieldInfo f = parameters.GetField(Names[i], Fields);
                    if (f == null || f.IsStatic || f.MetadataToken != vectorTokens[i] ||
                        f.FieldType.FullName != "UnityEngine.Vector2")
                        return Reject("WEIGHT_FIELD_TOKEN_OR_TYPE_"+i,out failure);
                    Type vectorType=f.FieldType;
                    FieldInfo x = vectorType.GetField("x", Fields);
                    FieldInfo y = vectorType.GetField("y", Fields);
                    if (x == null || y == null || x.IsStatic || y.IsStatic ||
                        x.FieldType != typeof(float) || y.FieldType != typeof(float))
                        return Reject("VECTOR2_ABI_"+i,out failure);
                    object vector=f.GetValue(values);
                    if (vector == null) return Reject("WEIGHT_VECTOR_NULL_"+i,out failure);
                    float low=(float)x.GetValue(vector);
                    float high=(float)y.GetValue(vector);
                    if (!Valid(low) || !Valid(high) || high<low)
                        return Reject("WEIGHT_VECTOR_RANGE_"+i,out failure);
                    result[i]=new BaselineRange(low,high);
                }
                snapshot=new AttributeNativeConfig(result);
                failure="NONE";
                return true;
            }
            catch(Exception ex) when (ex is AmbiguousMatchException ||
                ex is TargetException || ex is TargetInvocationException ||
                ex is ArgumentException || ex is FieldAccessException ||
                ex is InvalidOperationException)
            {
                return Reject("REFLECTION_ABI_FAILURE",out failure);
            }
        }
        private static bool Valid(float f) => !float.IsNaN(f) &&
            !float.IsInfinity(f) && f > 0f && f <= 100000f;
        private static bool Reject(string code,out string failure)
        { failure=code; return false; }
    }
}
