using System;
namespace GigaPMC.Client
{
    // P50 R2: native fall-height attenuation for *minor* contact incidents.
    // No Unity, no HP writes, no damage-type overrides, no frame polling.
    // An actual drop beyond nativeSafeHeight+1.50m remains 100% vanilla.
    internal static class ImpactProtectionMath
    {
        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        internal static bool TryAttenuateHeight(float height,float nativeSafeHeight,
            decimal frozenMovementPercent,out float adjustedHeight,out float reduction)
        {
            adjustedHeight=height;
            reduction=0f;
            if(!Finite(height) || !Finite(nativeSafeHeight) ||
               nativeSafeHeight<0f || nativeSafeHeight>100f ||
               height<=nativeSafeHeight || height>1000f ||
               frozenMovementPercent<=0m || frozenMovementPercent>10000m)
                return false;
            double excess=(double)height-nativeSafeHeight;
            // 0% at vanilla speed (x1), gradual increase, maximum at x9+.
            // This is a protection curve, NOT a claim of real movement m/s.
            double factor=1d+(double)frozenMovementPercent/100d;
            double strength=Math.Min(1d,Math.Max(0d,(factor-1d)/8d));
            if(strength<=0d) return false;
            // At high speed only low falls up to 1.5 m ABOVE the *native*
            // no-damage threshold qualify. Full-height falls stay vanilla.
            double maxExcess=0.25d+1.25d*strength;
            if(excess<=0d || excess>maxExcess) return false;
            double result=(double)nativeSafeHeight+excess*(1d-strength);
            if(double.IsNaN(result) || double.IsInfinity(result) ||
               result<nativeSafeHeight || result>height) return false;
            adjustedHeight=(float)result;
            reduction=(float)strength;
            return Finite(adjustedHeight) && Finite(reduction) &&
                   adjustedHeight<=height && adjustedHeight>=nativeSafeHeight;
        }
    }
}
