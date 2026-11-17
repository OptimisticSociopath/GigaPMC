using System;
using System.Globalization;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace GigaPMC.Client
{
    // P50 R8: sprint-only read-only physics evidence. Never changes native
    // values, motion vectors, transforms, animation, F12, or collision systems.
    // Activated solely by the already-owned DirectApplyMotion native event.
    internal static class SprintReadOnlyProbe
    {
        internal struct Snapshot
        {
            internal bool Armed;
            internal Vector3 Before;
        }
        private const int EventBudget = 120;
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static bool started, complete;
        private static object boundContext;
        private static Transform position;
        private static PropertyInfo sprintLimit, sprintSpeed, maxSpeed, characterSpeed, smoothedSpeed, stateSpeedLimit;
        private static int callbacks, zeros, nonzero;
        private static double total, maximum, minimum;
        internal static bool Complete => complete;
        internal static void Reset()
        {
            started=complete=false;
            boundContext=null; position=null;
            sprintLimit=sprintSpeed=maxSpeed=characterSpeed=smoothedSpeed=stateSpeedLimit=null;
            callbacks=zeros=nonzero=0;
            total=maximum=0d; minimum=double.PositiveInfinity;
        }
        private static void Say(ManualLogSource log,string text) =>
            log?.LogInfo("[GigaPMC] P27_P50_R8_"+text);
        internal static void Disable(string reason,ManualLogSource log) => Stop(log,reason);
        private static void Stop(ManualLogSource log,string reason)
        {
            complete=true;
            log?.LogWarning("[GigaPMC] P27_P50_R8_SPRINT_PROBE_UNAVAILABLE|"+reason+
                "|R7_BASELINE_UNCHANGED|NO_SPRINT_OR_ANIMATION_WRITES");
        }
        private static PropertyInfo ReadableFloat(Type t,string name)
        {
            var p=t.GetProperty(name,Any);
            return p!=null && p.PropertyType==typeof(float) &&
                p.GetIndexParameters().Length==0 && p.GetGetMethod(true)!=null &&
                !p.GetGetMethod(true).IsStatic ? p : null;
        }
        private static string Read(object ctx,PropertyInfo p)
        {
            if(p==null) return "ABI_MISSING";
            try
            {
                float v=(float)p.GetValue(ctx,null);
                return float.IsNaN(v)||float.IsInfinity(v) ? "NONFINITE" : v.ToString("R",Inv);
            }
            catch { return "READ_UNAVAILABLE"; }
        }
        private static string Values(object ctx)
        {
            return "STATE_SPRINT_SPEED_LIMIT="+Read(ctx,sprintLimit)+
                "|SPRINT_SPEED="+Read(ctx,sprintSpeed)+
                "|MAX_SPEED="+Read(ctx,maxSpeed)+
                "|CHARACTER_MOVEMENT_SPEED="+Read(ctx,characterSpeed)+
                "|SMOOTHED_CHARACTER_SPEED="+Read(ctx,smoothedSpeed)+
                "|STATE_SPEED_LIMIT="+Read(ctx,stateSpeedLimit);
        }
        private static void Initialize(object ctx,object player,decimal percent,ManualLogSource log)
        {
            started=true; boundContext=ctx;
            try
            {
                Type t=ctx.GetType();
                sprintLimit=ReadableFloat(t,"StateSprintSpeedLimit");
                sprintSpeed=ReadableFloat(t,"SprintSpeed");
                maxSpeed=ReadableFloat(t,"MaxSpeed");
                characterSpeed=ReadableFloat(t,"CharacterMovementSpeed");
                smoothedSpeed=ReadableFloat(t,"SmoothedCharacterMovementSpeed");
                stateSpeedLimit=ReadableFloat(t,"StateSpeedLimit");
                // Same already-captured local PMC as R7. No hierarchy or scene scan.
                var component=player as Component;
                position=component!=null ? component.transform : null;
                if(position==null) { Stop(log,"LOCAL_PLAYER_POSITION_UNAVAILABLE"); return; }
                Say(log,"SPRINT_NATIVE_START|"+Values(ctx)+
                    "|F12_PERCENT="+percent.ToString(Inv)+
                    "|EVENT_BUDGET="+EventBudget+
                    "|POSITION_SOURCE=CAPTURED_PMC_COMPONENT"+
                    "|NO_SETTERS_CALLED|NO_EFT_OR_ANIMATION_WRITES");
            }
            catch(Exception ex) { Stop(log,"INITIALIZE_"+ex.GetType().Name); }
        }
        internal static void Before(object ctx,object player,decimal percent,
            Vector3 motion,float deltaTime,ManualLogSource log,ref Snapshot snapshot)
        {
            snapshot=default(Snapshot);
            if(complete || ctx==null || player==null || deltaTime<=0f ||
                deltaTime>0.10f || motion.x*motion.x+motion.z*motion.z<=1e-8f) return;
            if(!started) Initialize(ctx,player,percent,log);
            if(complete || position==null || !ReferenceEquals(ctx,boundContext)) return;
            try { snapshot.Before=position.position; snapshot.Armed=true; }
            catch(Exception ex) { Stop(log,"BEFORE_"+ex.GetType().Name); }
        }
        internal static void After(Snapshot snapshot,float deltaTime,ManualLogSource log)
        {
            if(complete || !snapshot.Armed || position==null) return;
            try
            {
                Vector3 delta=position.position-snapshot.Before;
                double mps;
                if(!NativeSpeedMath.TryDirectCallSpeed(delta.x,delta.y,delta.z,
                    deltaTime,out mps)) return;
                callbacks++;
                if(mps<.00001d) zeros++;
                else
                {
                    nonzero++; total+=mps;
                    maximum=Math.Max(maximum,mps);
                    minimum=Math.Min(minimum,mps);
                }
                if(callbacks==EventBudget)
                {
                    complete=true;
                    Say(log,"SPRINT_DIRECT_CALL_XZ_MPS|CALLBACKS="+callbacks+
                        "|NONZERO="+nonzero+"|ZERO="+zeros+
                        "|MEAN="+(nonzero==0?"UNOBSERVED":(total/nonzero).ToString("F4",Inv))+
                        "|MIN="+(nonzero==0?"UNOBSERVED":minimum.ToString("F4",Inv))+
                        "|MAX="+(nonzero==0?"UNOBSERVED":maximum.ToString("F4",Inv))+
                        "|"+Values(boundContext)+
                        "|DIRECT_APPLY_MOTION_ONLY|NOT_FULL_FRAME_SPEED"+
                        "|EVENT_BOUNDED|NO_PERIODIC_SCAN|NO_EFT_WRITES");
                }
            }
            catch(Exception ex) { Stop(log,"AFTER_"+ex.GetType().Name); }
        }
    }
}
