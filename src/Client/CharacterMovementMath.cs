using System;

namespace GigaPMC.Client
{
    // Scalar-only policy, shared by the real bridge and executable offline tests.
    internal static class CharacterMovementMath
    {
        internal static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
        internal static float Multiplier(decimal percent) =>
            percent >= 0m && percent <= 10000m ? 1f + (float)(percent / 100m) : 1f;

        // Budget the existing solver's internal substeps, never add Move calls.
        // A saturated budget reduces this frame's bonus, not the native movement.
        // Y, gravity, elapsed time and native collision settings remain unchanged.
        internal static float FrameFactor(float x,float y,float z,float dt,float requested,
            float fixedDistance,int nativeMaxCycles)
        {
            if(!Finite(x)||!Finite(y)||!Finite(z)||!Finite(dt)||!Finite(requested)||
               !Finite(fixedDistance)||dt<=0f||dt>0.1f||requested<=1f||
               fixedDistance<=0f||fixedDistance>0.25f||nativeMaxCycles<4) return 1f;
            double horizontal=Math.Sqrt((double)x*x+(double)z*z);
            if(horizontal<0.000001 || horizontal/dt>20d) return 1f;
            // 64 native collision substeps maximum per amplified call.
            double budget=fixedDistance*Math.Min(64,nativeMaxCycles-2);
            double available=budget*budget-(double)y*y;
            if(available<=0) return 1f;
            return (float)Math.Max(1d,Math.Min(requested,Math.Sqrt(available)/horizontal));
        }

        internal static float Limit(float native,float factor) =>
            native < 0f ? native : native * factor;

        // Destination is consumed by prone's CalculateMotionShortage. Keep the
        // collision deficit in animation units, without moving any Transform.
        internal static float Destination(float expected,float before,float after,float factor) =>
            expected + (after-before)*(1f-1f/factor);
    }
}
