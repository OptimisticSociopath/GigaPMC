using System;
using System.Globalization;

namespace GigaPMC.Client
{
    // Getter results are not measured world-space velocity (m/s).
    // Separate EFT endpoints: never overwrite sprint with clamped speed or vice versa.
    // Event-driven, bounded change reporting; no Unity object scans or timers.
    internal enum SpeedEndpoint { Clamped = 0, Sprint = 1 }

    internal static class MovementEndpointEvidence
    {
        private static readonly object Gate = new object();
        private static readonly bool[] Seen = new bool[2];
        private static readonly float[] Original = new float[2];
        private static readonly float[] Applied = new float[2];
        internal static event Action Changed;

        internal static void Reset()
        {
            lock(Gate)
            {
                Array.Clear(Seen,0,Seen.Length);
                Array.Clear(Original,0,Original.Length);
                Array.Clear(Applied,0,Applied.Length);
            }
            Changed?.Invoke();
        }
        private static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
        // Entered only after successful native final multiplication in MovementAfter.
        // The fast path does NOT acquire a lock for unchanged getter readings.
        internal static bool Record(SpeedEndpoint endpoint,float original,float applied)
        {
            int n=(int)endpoint;
            if(n<0 || n>1 || !Finite(original) || !Finite(applied) ||
                original<=0f || applied<=original) return false;
            if(Seen[n] && Math.Abs(Original[n]-original)<0.05f &&
                Math.Abs(Applied[n]-applied)<0.1f) return false;
            bool first, changed;
            lock(Gate)
            {
                first=!Seen[n];
                changed=first || Math.Abs(Original[n]-original)>=0.05f ||
                    Math.Abs(Applied[n]-applied)>=0.1f;
                if(!changed) return false;
                Seen[n]=true;
                Original[n]=original;
                Applied[n]=applied;
            }
            Changed?.Invoke();
            return first;
        }
        internal static bool TryRead(SpeedEndpoint endpoint,out float original,out float applied)
        {
            original=applied=0f;
            int n=(int)endpoint;
            if(n<0 || n>1) return false;
            lock(Gate)
            {
                if(!Seen[n]) return false;
                original=Original[n];applied=Applied[n];
                return true;
            }
        }
        // ONE movement bonus remains; these are two different EFT getter outputs.
        // Do not interpret their relative sizes as a measured sprint/walk speed ratio.
        internal static string HudRow()
        {
            float c0,c1,s0,s1;
            bool c=TryRead(SpeedEndpoint.Clamped,out c0,out c1);
            bool s=TryRead(SpeedEndpoint.Sprint,out s0,out s1);
            if(!c && !s) return null;
            string cv=c?c1.ToString("0.###",CultureInfo.InvariantCulture):"--";
            string sv=s?s1.ToString("0.###",CultureInfo.InvariantCulture):"--";
            return cv+" C / "+sv+" S  EFT getters (not m/s)  Movement Speed";
        }
    }
}
