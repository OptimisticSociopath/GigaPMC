using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace GigaPMC.Client
{
    // P50 R7: strictly read-only, bounded, event-driven native movement study.
    // NEVER invoke speed setters, raise speed events, modify motion, or scan
    // GameObjects. Called from the existing native DirectApplyMotion Harmony hook.
    internal static class NativeReadOnlyProbe
    {
        internal struct Snapshot
        {
            internal bool Armed;
            internal Vector3 Before;
        }
        private static bool attempted, complete;
        private static object boundContext;
        private static Transform location;
        private static int attempts, nonzero, zero;
        private static double sum, min, max;
        private const int EventBudget = 24;
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        internal static bool Complete => complete;
        internal static void Disable(string reason,ManualLogSource log) => Stop(log,reason);
        internal static void Reset()
        {
            attempted=false; complete=false; boundContext=null; location=null;
            attempts=nonzero=zero=0; sum=0d; min=double.PositiveInfinity; max=0d;
        }
        private static void Say(ManualLogSource log,string message) =>
            log?.LogInfo("[GigaPMC] P27_P50_R7_"+message);
        private static void Stop(ManualLogSource log,string reason)
        {
            complete=true;
            log?.LogWarning("[GigaPMC] P27_P50_R7_PROBE_UNAVAILABLE|"+reason+
                "|P50_R6_R2_GAMEPLAY_UNMODIFIED");
        }
        private static PropertyInfo FloatProperty(Type ctx,string name)
        {
            var p=ctx.GetProperty(name,Any);
            return p!=null && p.PropertyType==typeof(float) && p.GetIndexParameters().Length==0 &&
                p.GetGetMethod(true)!=null && !p.GetGetMethod(true).IsStatic ? p : null;
        }
        private static string Value(object context,PropertyInfo p)
        {
            if(p==null) return "ABI_MISSING";
            try
            {
                float f=(float)p.GetValue(context,null);
                return !float.IsNaN(f) && !float.IsInfinity(f) ? f.ToString("R",Inv) : "NONFINITE";
            }
            catch { return "READ_UNAVAILABLE"; }
        }
        private static bool NativeSpeedSetterAbi(Type context)
        {
            return context.GetMethods(Any).Count(m=>m.Name=="SetCharacterMovementSpeed" &&
                m.ReturnType==typeof(void) && !m.IsStatic &&
                m.GetParameters().Length==2 &&
                m.GetParameters()[0].ParameterType==typeof(float) &&
                m.GetParameters()[1].ParameterType==typeof(bool))==1;
        }
        private static bool SpeedEventAbi(Type context)
        {
            return context.GetMethods(Any).Count(m=>m.Name=="RaiseChangeSpeedEvent" &&
                m.ReturnType==typeof(void) && !m.IsStatic &&
                m.GetParameters().Length==0)==1;
        }
        private static void Initialize(object context,object player,decimal percent,ManualLogSource log)
        {
            attempted=true;
            boundContext=context;
            try
            {
                Type t=context.GetType();
                // No method is invoked except read-only getters. Setters and
                // speed change events are checked for ABI shape ONLY.
                var getter=t.GetProperty("PlayerTransform",Any);
                object raw=(getter!=null && getter.GetIndexParameters().Length==0 &&
                    getter.GetGetMethod(true)!=null) ? getter.GetValue(context,null) : null;
                string positionSource="EFT_PLAYER_TRANSFORM";
                location=raw as Transform;
                if(location==null)
                {
                    var c=player as Component;
                    if(c!=null) { location=c.transform; positionSource="PMC_COMPONENT_FALLBACK_NOT_EFT_NATIVE"; }
                }
                var candidateNames=t.GetFields(Any)
                    .Where(f=>f.Name.IndexOf("controller",StringComparison.OrdinalIgnoreCase)>=0 ||
                        f.Name.IndexOf("motor",StringComparison.OrdinalIgnoreCase)>=0)
                    .Take(8).Select(f=>f.Name+":"+f.FieldType.Name).ToArray();
                Say(log,"NATIVE_MOVEMENT_ABI|SET_CHARACTER_SPEED="+
                    (NativeSpeedSetterAbi(t)?"VALID_READONLY":"MISSING_OR_AMBIGUOUS")+
                    "|RAISE_SPEED_EVENT="+(SpeedEventAbi(t)?"VALID_READONLY":"MISSING_OR_AMBIGUOUS")+
                    "|MAX_SPEED="+Value(context,FloatProperty(t,"MaxSpeed"))+
                    "|CHARACTER_MOVEMENT_SPEED="+Value(context,FloatProperty(t,"CharacterMovementSpeed"))+
                    "|SMOOTHED_CHARACTER_SPEED="+Value(context,FloatProperty(t,"SmoothedCharacterMovementSpeed"))+
                    "|STATE_SPEED_LIMIT="+Value(context,FloatProperty(t,"StateSpeedLimit"))+
                    "|POSITION_SOURCE="+(location==null?"UNAVAILABLE":positionSource)+
                    "|F12_PERCENT="+percent.ToString(Inv)+
                    "|NO_SETTERS_CALLED|NO_ANIMATION_OR_MOTION_CHANGE");
                Say(log,"NATIVE_CONTROLLER_FIELD_CANDIDATES|COUNT="+candidateNames.Length+
                    "|TYPES="+(candidateNames.Length==0?"NONE":string.Join(";",candidateNames))+
                    "|METADATA_ONLY|NO_FIELD_VALUE_READ|NO_SCENE_SCAN");
                if(location==null) Stop(log,"NO_SAFE_POSITION_REFERENCE");
            }
            catch(Exception ex) { Stop(log,"ABI_READ_"+ex.GetType().Name); }
        }
        internal static void Before(object context,object player,decimal percent,
            Vector3 motion,float deltaTime,ManualLogSource log,ref Snapshot snapshot)
        {
            snapshot=default(Snapshot);
            if(complete || context==null || player==null ||
                deltaTime<=0f || deltaTime>0.1f ||
                motion.x*motion.x+motion.z*motion.z<0.00000001f) return;
            if(!attempted) Initialize(context,player,percent,log);
            if(complete || location==null || !ReferenceEquals(context,boundContext)) return;
            try { snapshot.Before=location.position; snapshot.Armed=true; }
            catch(Exception ex) { Stop(log,"BEFORE_"+ex.GetType().Name); }
        }
        internal static void After(Snapshot snapshot,float deltaTime,ManualLogSource log)
        {
            if(complete || !snapshot.Armed || location==null) return;
            try
            {
                Vector3 change=location.position-snapshot.Before;
                double mps;
                if(!NativeSpeedMath.TryDirectCallSpeed(change.x,change.y,change.z,
                    deltaTime,out mps)) return;
                attempts++;
                if(mps<0.00001d) zero++;
                else { nonzero++;sum+=mps;min=Math.Min(min,mps);max=Math.Max(max,mps); }
                if(attempts>=EventBudget)
                {
                    complete=true;
                    Say(log,"DIRECT_CALL_XZ_MPS_SAMPLE|CALLBACKS="+attempts+
                        "|NONZERO="+nonzero+"|ZERO="+zero+
                        "|MEAN="+(nonzero==0?"UNOBSERVED":(sum/nonzero).ToString("F4",Inv))+
                        "|MIN="+(nonzero==0?"UNOBSERVED":min.ToString("F4",Inv))+
                        "|MAX="+(nonzero==0?"UNOBSERVED":max.ToString("F4",Inv))+
                        "|WITHIN_DIRECT_APPLY_MOTION_ONLY|NOT_FULL_FRAME_VELOCITY"+
                        "|EVENT_BOUNDED|NO_PERIODIC_SCANS|NO_EFT_WRITES");
                }
            }
            catch(Exception ex) { Stop(log,"AFTER_"+ex.GetType().Name); }
        }
    }
}
