using System;

namespace GigaPMC.Client
{
    // P19: canonical additive boundary. No EFT values are written here.
    // A native source is required to certify that the incoming result has NOT
    // already received a GigaPMC delta. The source gate remains SAFE-OFF.
    internal static class AttributeAdditiveContract
    {
        internal static bool TryCompose(float nativeCurrent, float frozenDelta,
            bool originalUnmodified, bool nativeOperationPermitsBonus,
            out float effective, out string reason)
        {
            effective = nativeCurrent;
            reason = "NATIVE_RESULT_UNCERTIFIED";
            if (!IsFiniteNonnegative(nativeCurrent) || !IsFiniteNonnegative(frozenDelta))
            { reason = "NONFINITE_OR_NEGATIVE_INPUT"; return false; }
            if (!originalUnmodified) return false; // Never apply to previous GigaPMC output.
            if (!nativeOperationPermitsBonus)
            { reason = "NATIVE_HARD_BLOCK_PRESERVED"; return true; }
            try
            {
                effective = AttributeFrozenDelta.Apply(nativeCurrent, frozenDelta);
                reason = "ADDITIVE_NATIVE_PLUS_FROZEN";
                return true;
            }
            catch (ArgumentException) { reason = "BAD_COMPOSITION"; return false; }
            catch (OverflowException) { reason = "COMPOSITION_OVERFLOW"; return false; }
        }

        private static bool IsFiniteNonnegative(float v)
            => !float.IsNaN(v) && !float.IsInfinity(v) && v >= 0f;

        // Deliberately fixed: P19 has no certified EFT live hooks for any
        // category, therefore this cannot be interpreted as a runtime feed.
        internal static bool GameplayHookCertificationPresent => false;
    }
}
