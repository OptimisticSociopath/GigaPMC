using System;
using System.Diagnostics;
using System.Globalization;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace GigaPMC.Client
{
    internal enum MovementGateReason { ZeroBonus, Disabled, Airborne, Mounted, Platform, State, FrameBudget, InvalidLimit, Boosted }

    // Ctrl+F8 waits for one second of steady locomotion, then records five
    // seconds in the SAME state. Controller counters exist only in this window.
    internal static class WorldSpeedProbe
    {
        private static Transform root;
        private static Func<object,object> stateOf;
        private static object context;
        private static ManualLogSource log;
        private static bool waiting,recording;
        private static Vector3 last;
        private static Type state;
        private static double armedAt,stableSince,start,previous,distance,elapsed,maxFrame;
        private static int samples,rejected,stateChanges;
        private static int descentFrames;
        private static decimal percent;
        private static readonly long[] gates=new long[9];
        private static long rawGround,contextGround,contextOnly,controllerSamples;
        private static double requestXZ,deliveredXZ,solvedXZ,maxSolvedMps,maxFactor;
        private static float minLimit,maxLimit;
        internal static bool Measuring => recording;
        private static double Now => (double)Stopwatch.GetTimestamp()/Stopwatch.Frequency;
        private static double XZ(Vector3 v) => Math.Sqrt((double)v.x*v.x+(double)v.z*v.z);
        internal static void Arm(Transform position,Func<object,object> stateGetter,object ctx,
            ManualLogSource logger,decimal frozenPercent)
        {
            Disarm();
            root=position;stateOf=stateGetter;context=ctx;log=logger;percent=frozenPercent;
        }
        internal static void Tick()
        {
            if(root==null || context==null) return;
            bool trigger=!recording && !waiting && UnityInput.Current.GetKeyDown(KeyCode.F8) &&
                (UnityInput.Current.GetKey(KeyCode.LeftControl)||UnityInput.Current.GetKey(KeyCode.RightControl));
            if(trigger||waiting||recording) TickAt(Now,trigger);
        }
        // Same production state machine exercised by deterministic offline tests.
        internal static void TickAt(double now,bool trigger)
        {
            if(root==null || context==null) return;
            if(trigger&&!waiting&&!recording)
            {
                waiting=true;armedAt=previous=now;stableSince=-1;
                last=root.position;state=null;
                log.LogInfo("[GigaPMC] MOVEMENT_WORLD_SAMPLE_ARMED|BUILD="+CharacterMovementBridge.Build+
                    "|WAIT_FOR_STEADY_MOVEMENT=1_SECOND|TIMEOUT=30_SECONDS|FROZEN_PERCENT="+percent);
                return;
            }
            if(!waiting&&!recording) return;
            double dt=now-previous;
            Vector3 position=root.position,delta=position-last;
            Type mode=stateOf(context)?.GetType();
            bool descentTransition=state!=null&&(mode?.Name=="FallDown"||mode?.Name=="JumpLandingState");
            // Preserve the walk/sprint sample through native descent transitions;
            // intentional jumping or switching walk/sprint still invalidates it.
            if(descentTransition)mode=state;
            double xz=XZ(delta);
            bool valid=dt>0&&dt<=.25&&!double.IsNaN(xz)&&!double.IsInfinity(xz)&&xz/dt<=1000;
            previous=now;last=position;
            if(waiting)
            {
                if(now-armedAt>=30) {Finish("TIMEOUT_NO_STEADY_MOVEMENT");return;}
                string name=mode?.Name;
                bool locomotion=name=="MovePlayerState"||name=="SprintPlayerState"||
                    name=="ProneMovePlayerState"||name=="PronePlayerState";
                if(!valid||!locomotion||xz/dt<.1)
                {stableSince=-1;state=mode;return;}
                if(mode!=state||stableSince<0){state=mode;stableSince=now;return;}
                if(now-stableSince<1) return;
                waiting=false;recording=true;start=now;
                distance=elapsed=maxFrame=0;samples=rejected=stateChanges=descentFrames=0;
                ResetCounters();
                log.LogInfo("[GigaPMC] MOVEMENT_WORLD_SAMPLE_BEGIN|5_SECONDS|STATE="+state.Name+
                    "|FROZEN_PERCENT="+percent+"|AFTER_STABLE_MOVEMENT");
                return;
            }
            if(mode!=state){stateChanges++;Finish("INVALID_STATE_CHANGED");return;}
            if(!valid){rejected++;Finish("INVALID_FRAME_GAP_OR_POSITION");return;}
            if(descentTransition)descentFrames++;
            distance+=xz;elapsed+=dt;samples++;maxFrame=Math.Max(maxFrame,dt);
            if(now-start>=5) Finish("COMPLETE");
        }
        private static void ResetCounters()
        {
            Array.Clear(gates,0,gates.Length);
            rawGround=contextGround=contextOnly=controllerSamples=0;
            requestXZ=deliveredXZ=solvedXZ=maxSolvedMps=maxFactor=0;
            minLimit=float.PositiveInfinity;maxLimit=float.NegativeInfinity;
        }
        internal static void ObserveGate(MovementGateReason gate,bool nativeGround,bool rawContact)
        {
            if(!recording) return;
            gates[(int)gate]++;
            if(nativeGround)contextGround++;
            if(rawContact)rawGround++;
            if(nativeGround&&!rawContact)contextOnly++;
        }
        internal static void ObserveMotion(Vector3 request,Vector3 delivered,Vector3 solved,
            float dt,float limit,float factor)
        {
            if(!recording) return;
            double r=XZ(request),d=XZ(delivered),s=XZ(solved);
            if(dt<=0||!CharacterMovementMath.Finite(dt)||double.IsNaN(r+d+s)||double.IsInfinity(r+d+s))return;
            controllerSamples++;requestXZ+=r;deliveredXZ+=d;solvedXZ+=s;
            maxSolvedMps=Math.Max(maxSolvedMps,s/dt);maxFactor=Math.Max(maxFactor,factor);
            if(CharacterMovementMath.Finite(limit)){minLimit=Math.Min(minLimit,limit);maxLimit=Math.Max(maxLimit,limit);}
        }
        private static void Finish(string status)
        {
            bool hadRecording=recording;
            waiting=recording=false;
            if(!hadRecording)
            {
                log?.LogInfo("[GigaPMC] MOVEMENT_WORLD_SAMPLE|STATUS="+status+"|NO_MEASUREMENT");return;
            }
            log?.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[GigaPMC] MOVEMENT_WORLD_SAMPLE|STATUS={0}|STATE={1}|PERCENT={2}|XZ_METERS={3:F5}|ELAPSED={4:F5}|XZ_MPS={5:F5}|FRAMES={6}|REJECTED={7}|OTHER_STATE_FRAMES={8}|MAX_FRAME_MS={9:F2}|DESCENT_FRAMES={10}|ANIMATIONS_AND_COLLISIONS_REQUIRE_OBSERVATION",
                status,state?.Name,percent,distance,elapsed,elapsed>0?distance/elapsed:0,samples,rejected,stateChanges,maxFrame*1000,descentFrames));
            log?.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[GigaPMC] MOVEMENT_GATE_SAMPLE|STATE={0}|CALLS={1}|BOOSTED={2}|ZERO={3}|DISABLED={4}|AIRBORNE={5}|MOUNTED={6}|PLATFORM={7}|STATE_SKIPPED={8}|FRAME_BUDGET={9}|INVALID_LIMIT={10}|CONTEXT_GROUND_TRUE={11}|RAW_CONTACT_TRUE={12}|CONTEXT_TRUE_RAW_FALSE={13}|REQUEST_XZ={14:F5}|DELIVERED_XZ={15:F5}|SOLVED_XZ={16:F5}|MAX_SOLVED_MPS={17:F5}|MAX_FACTOR={18:F5}|NATIVE_LIMIT_MIN={19}|NATIVE_LIMIT_MAX={20}|CONTROLLER_CALL_INTERVALS_NOT_WORLD_SPEED",
                state?.Name,controllerSamples,gates[8],gates[0],gates[1],gates[2],gates[3],gates[4],gates[5],gates[6],gates[7],
                contextGround,rawGround,contextOnly,requestXZ,deliveredXZ,solvedXZ,maxSolvedMps,maxFactor,minLimit,maxLimit));
        }
        internal static void Disarm()
        {
            if(recording||waiting)Finish("CANCELLED_RAID_END");
            root=null;context=null;stateOf=null;state=null;ResetCounters();
        }
    }
}

