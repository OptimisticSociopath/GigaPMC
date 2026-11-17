using System;
using System.Globalization;

namespace GigaPMC.Client
{
    // Only actual executions of native EFT hooks may report APPLIED.
    // No preview/menu calculation can change these counters.
    internal static class NativeEvidence
    {
        private static readonly object Gate = new object();
        internal static event Action MeasuredChanged;
        private static readonly string[] Names = {"Carrying Weight", "Body Stamina", "ADS Endurance", "Melee Damage", "Movement Speed"};
        private static readonly string[] Units = {"kg threshold", "stamina output", "ADS drain", "damage", "speed"};
        private static readonly int[] Hits = new int[5];
        private static readonly float[] Native = new float[5];
        private static readonly float[] Result = new float[5];
        private static bool Finite(float x) { return !float.IsNaN(x) && !float.IsInfinity(x); }
        internal static void Reset()
        {
            lock (Gate)
            {
                Array.Clear(Hits,0,Hits.Length);
                Array.Clear(Native,0,Native.Length);
                Array.Clear(Result,0,Result.Length);
            }
            MeasuredChanged?.Invoke();
        }
        // Return true exactly once per raid and effect: caller may log first genuine hit.
        internal static bool Record(GlobalBonusId id,float native,float result)
        {
            int n=(int)id;
            if(n<0 || n>=5 || !Finite(native) || !Finite(result) || native<0f || result<0f ||
                native==result) return false;
            // All bonuses increase their output except ADS, whose cost decreases.
            if ((id==GlobalBonusId.AdsEndurance && result>=native) ||
                (id!=GlobalBonusId.AdsEndurance && result<=native)) return false;
            // MOVEMENT can be called hundreds of times per second. Repeated
            // identical/small (<0.01) readings must NOT acquire a monitor.
            if(System.Threading.Volatile.Read(ref Hits[n])>0 &&
                Math.Abs(System.Threading.Volatile.Read(ref Native[n])-native)<0.01f &&
                Math.Abs(System.Threading.Volatile.Read(ref Result[n])-result)<0.01f)
                return false;
            bool first, changed;
            lock(Gate)
            {
                first=Hits[n]==0;
                changed=first || Math.Abs(Native[n]-native)>=0.01f ||
                    Math.Abs(Result[n]-result)>=0.01f;
                if(!changed) return false;
                if(Hits[n]<int.MaxValue) Hits[n]++;
                Native[n]=native;
                Result[n]=result;
            }
            if(changed) MeasuredChanged?.Invoke();
            return first;
        }
        internal static int Count(GlobalBonusId id)
        { lock(Gate) { return Hits[(int)id]; } }
        internal static bool TryGetMeasured(GlobalBonusId id,out float original,
            out float actual,out int observations)
        {
            original=actual=0f; observations=0;
            int i=(int)id;
            if(i<0 || i>=Hits.Length) return false;
            lock(Gate)
            {
                if(Hits[i]<=0 || !Finite(Native[i]) || !Finite(Result[i])) return false;
                original=Native[i]; actual=Result[i]; observations=Hits[i];
                return true;
            }
        }
        internal static string[] RaidLines()
        {
            lock(Gate)
            {
                var output = new string[6];
                output[0] = "GigaPMC | DEV VISION [NATIVE HOOKS / CURRENT RAID]";
                for(int i=0;i<5;i++)
                {
                    if(Hits[i]==0) output[i+1] = "--  "+Names[i]+"  [NO NATIVE APPLICATION OBSERVED]";
                    else output[i+1] = Result[i].ToString("0.###",CultureInfo.InvariantCulture)+"  "+Units[i]+
                        "  "+Names[i]+"  [APPLIED "+Hits[i]+"x; EFT="+
                        Native[i].ToString("0.###",CultureInfo.InvariantCulture)+"]";
                }
                return output;
            }
        }
    }
}
