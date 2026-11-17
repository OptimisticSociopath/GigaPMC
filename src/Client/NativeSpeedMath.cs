using System;
namespace GigaPMC.Client
{
    // Pure read-only displacement arithmetic; the result is limited to the
    // DirectApplyMotion callback interval, not asserted to be full-frame m/s.
    internal static class NativeSpeedMath
    {
        internal static bool TryDirectCallSpeed(float x,float y,float z,float dt,out double speed)
        {
            speed=0d;
            if(float.IsNaN(x)||float.IsNaN(y)||float.IsNaN(z)||float.IsNaN(dt)||
                float.IsInfinity(x)||float.IsInfinity(y)||float.IsInfinity(z)||float.IsInfinity(dt)||
                dt<=0f || dt>0.10f || Math.Abs(y)>0.25f) return false;
            double distance=Math.Sqrt((double)x*x+(double)z*z);
            if(distance>0.5d || double.IsInfinity(distance) || double.IsNaN(distance)) return false;
            speed=distance/dt;
            return !double.IsInfinity(speed) && !double.IsNaN(speed);
        }
    }
}
