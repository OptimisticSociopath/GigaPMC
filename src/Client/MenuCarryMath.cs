using System;

namespace GigaPMC.Client
{
    // A main-menu UI readout is not a live EFT.Player.Physical controller.
    // These samples are populated ONLY by the pinned EFT menu IL endpoint,
    // after EFT has calculated its skill/health/equipment-adjusted kg values.
    internal static class MenuCarryMath
    {
        private static readonly object Gate = new object();
        internal static event Action MeasuredChanged;
        private static bool valid;
        private static float nativeUpper, effectiveUpper;

        internal static void Reset()
        {
            lock (Gate) { valid = false; nativeUpper = effectiveUpper = 0f; }
            MeasuredChanged?.Invoke();
        }
        internal static bool TryScale(float eftNativeKg, decimal gigapmcPercent, out float effectiveKg)
        {
            return AttributeGlobalBonus.TryMultiplyFinal(
                GlobalBonusId.CarryingWeight, eftNativeKg,
                gigapmcPercent, 8000f, out effectiveKg);
        }
        internal static void RecordUpper(float eftNativeKg, float finalKg)
        {
            if (float.IsNaN(eftNativeKg) || float.IsInfinity(eftNativeKg) ||
                float.IsNaN(finalKg) || float.IsInfinity(finalKg) ||
                eftNativeKg <= 0f || finalKg < eftNativeKg || finalKg >= 8000f)
                return;
            bool changed;
            lock (Gate)
            {
                changed=!valid || nativeUpper!=eftNativeKg || effectiveUpper!=finalKg;
                nativeUpper = eftNativeKg;
                effectiveUpper = finalKg;
                valid = true;
            }
            if(changed) MeasuredChanged?.Invoke();
        }
        internal static bool TryRead(out float eftNativeKg, out float finalKg)
        {
            lock (Gate)
            {
                eftNativeKg = nativeUpper;
                finalKg = effectiveUpper;
                return valid;
            }
        }
    }
}
