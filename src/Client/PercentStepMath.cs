using System;
using System.Globalization;

namespace GigaPMC.Client
{
    // Pure quarter-percentage-point grid. No quantization during config
    // deserialization: existing custom F12 values remain unchanged until edited.
    internal static class PercentStepMath
    {
        internal const float Step = 0.25f;
        internal static float Snap(double value, float maximum)
        {
            if(double.IsNaN(value) || double.IsInfinity(value) || maximum <= 0f ||
               float.IsNaN(maximum) || float.IsInfinity(maximum))
                throw new ArgumentOutOfRangeException(nameof(value));
            double bounded=Math.Max(0d,Math.Min((double)maximum,value));
            return (float)(Math.Round(bounded*4d,0,MidpointRounding.AwayFromZero)/4d);
        }
        internal static bool TryParse(string raw, float maximum, out float value)
        {
            value=0f;
            if(string.IsNullOrWhiteSpace(raw)) return false;
            string text=raw.Trim().Replace(',','.');
            double n;
            if(!double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out n) ||
                double.IsNaN(n) || double.IsInfinity(n) || n<0d || n>(double)maximum) return false;
            value=Snap(n,maximum);
            return true;
        }
        // One fixed F12 slider: 0..100%. The computed TOTAL bonus is separate,
        // and is intentionally not limited to 100% by this UI component.
        internal static float SliderLower(float current,float maximum) => 0f;
        internal static float SliderUpper(float current,float maximum)
            => Math.Min(100f,maximum);
        internal static float Move(float current,int steps,float maximum)
            => Snap((double)current+((double)steps*0.25d),maximum);
        internal static string Label(float value)
            => value.ToString("0.##",CultureInfo.InvariantCulture);
    }
}
