using System;

namespace GigaPMC.Client
{
    internal sealed class JumpDistanceSettings
    {
        internal static readonly JumpDistanceSettings Default=new JumpDistanceSettings(true,100f);
        internal bool Enabled { get; }
        internal float StrengthPercent { get; }
        internal JumpDistanceSettings(bool enabled,float strengthPercent)
        {
            if(float.IsNaN(strengthPercent)||float.IsInfinity(strengthPercent)||strengthPercent<0f||strengthPercent>100f)
                throw new ArgumentOutOfRangeException(nameof(strengthPercent));
            Enabled=enabled;StrengthPercent=strengthPercent;
        }
    }
}
