using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace GigaPMC.Client
{
    // One movement owner; no getter, sprint-acceleration, normalized
    // animation-speed, early ApplyMotion or Transform-position patches.
    internal static class CharacterMovementBridge
    {
        internal const string Build = "2.0.0";
        private const string Id = "com.vargor.gigapmc.p51.controller";
        private static Harmony harmony;
        private static ManualLogSource log;
        private static object player, context, controller;
        private static Transform movementTransform;
        private static float multiplier=1f, visualFactor=1f;
        private static int visualFrame=-1;
        private static bool moving, disabled;
        private static MethodInfo moveMethod, velocityMethod;
        private static Func<object,Vector3,float,CollisionFlags> nativeMove;
        private static Func<object,object> stateOf;
        private static Func<Vector3> platformMotion;
        private static Func<bool> grounded, rawControllerGrounded, enabled, mounted;
        private static Func<float> speedLimit, fixedDistance;
        private static Func<int> maxCycles;
        private static Action<float> setSpeedLimit;
        private static Func<object,Vector3?> destination;
        private static Action<object,Vector3?> setDestination;
        private static readonly HashSet<Type> states=new HashSet<Type>();
        private static Type jumpState, fallState, landingState;
        private static readonly AirborneMovementPolicy airborne=new AirborneMovementPolicy();
        private static bool scaleJump=true;
        private static float jumpStrengthPercent=100f;
        private static long amplified, budgetLimited;

        private static Type Eft(string name)
        {
            var found=AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name=="Assembly-CSharp")
                .Select(a=>a.GetType(name,false)).Where(t=>t!=null).ToArray();
            if(found.Length!=1) throw new TypeLoadException(name);
            return found[0];
        }
        private static MethodInfo Method(Type t,string name,Type result,params Type[] args)
        {
            // Interface methods have no IL body; inherited virtual methods must
            // resolve to their actual declaring type (ClientPlayer context).
            var matches=t.GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)
                .Where(m=>m.Name==name && m.ReturnType==result && !m.IsGenericMethod &&
                    m.GetParameters().Select(p=>p.ParameterType).SequenceEqual(args)).ToArray();
            if(matches.Length!=1) throw new MissingMethodException(t.FullName+"."+name);
            return matches[0];
        }
        private static Func<T> Bound<T>(object target,string property)
        {
            var p=target.GetType().GetProperty(property,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance);
            if(p==null || p.PropertyType!=typeof(T) || p.GetGetMethod(true)==null)
                throw new MissingMemberException(target.GetType().FullName,property);
            return (Func<T>)Delegate.CreateDelegate(typeof(Func<T>),target,p.GetGetMethod(true));
        }
        private static Func<T> Field<T>(object target,string name)
        {
            var field=AccessTools.Field(target.GetType(),name);
            if(field==null || field.IsStatic || field.FieldType!=typeof(T)) throw new MissingFieldException(name);
            return Expression.Lambda<Func<T>>(Expression.Field(Expression.Constant(target),field)).Compile();
        }
        private static void RequireCalls(MethodInfo method,params string[] names)
        {
            // The bundled Harmony exposes ILGenerator from mscorlib. Resolve
            // this startup-only inspection reflectively for netstandard2.1.
            var reader=typeof(PatchProcessor).GetMethods(BindingFlags.Public|BindingFlags.Static)
                .Single(m=>m.Name=="GetOriginalInstructions" && m.GetParameters().Length==2 &&
                    !m.GetParameters()[1].ParameterType.IsByRef);
            var il=((IEnumerable<CodeInstruction>)reader.Invoke(null,new object[]{method,null})).ToArray();
            foreach(string name in names)
                if(!il.Any(i=>i.operand is MethodInfo m && m.Name==name))
                    throw new InvalidOperationException("IL_ANCHOR_MISSING|"+method.Name+"|"+name);
        }
        private static void Audit(MethodInfo method)
        {
            var p=Harmony.GetPatchInfo(method);
            var patches=p==null ? Enumerable.Empty<Patch>() :
                p.Prefixes.Concat(p.Postfixes).Concat(p.Transpilers).Concat(p.Finalizers);
            log.LogInfo("[GigaPMC] MOVEMENT_PATCH_CHAIN|"+method.DeclaringType.FullName+"."+method.Name+
                "|"+string.Join(";",patches.Select(x=>x.owner+":"+x.PatchMethod.Name+":"+x.priority)));
        }
        internal static void Arm(object localPlayer,object movement,decimal percent,ManualLogSource logger,
            bool enableJumpScaling=true,float jumpStrength=100f)
        {
            Disarm(); log=logger;
            if(localPlayer==null || movement==null) return;
            multiplier=CharacterMovementMath.Multiplier(percent);
            scaleJump=enableJumpScaling;
            jumpStrengthPercent=jumpStrength;
            try
            {
                // Refuse a combined run even at SSJ x1: its extra hooks invalidate
                // attribution and can be reconfigured while the raid is running.
                if(Chainloader.PluginInfos.ContainsKey("ciallo.SuperSprintJump"))
                    throw new InvalidOperationException("CONFLICTING_MOVEMENT_MOD_SUPER_SPRINT_JUMP");
                var ctx=Eft("EFT.MovementContext"); var playerType=Eft("EFT.Player");
                var simple=Eft("SimpleCharacterController"); var iface=Eft("ICharacterController");
                var stateType=Eft("EFT.MovementState");
                if(!ctx.IsInstanceOfType(movement) || !playerType.IsInstanceOfType(localPlayer))
                    throw new InvalidOperationException("LOCAL_IDENTITY_ABI");
                var getController=Method(ctx,"get_CharacterController",iface);
                object cc=getController.Invoke(movement,null);
                if(cc==null || cc.GetType()!=simple)
                    throw new InvalidOperationException("UNSUPPORTED_CONTROLLER|"+(cc?.GetType().FullName??"NULL"));
                var direct=Method(ctx,"DirectApplyMotion",typeof(void),typeof(Vector3),typeof(float));
                if(Method(movement.GetType(),"DirectApplyMotion",typeof(void),typeof(Vector3),typeof(float))!=direct)
                    throw new InvalidOperationException("DIRECT_APPLY_OVERRIDE_UNAUDITED");
                moveMethod=Method(iface,"Move",typeof(CollisionFlags),typeof(Vector3),typeof(float));
                velocityMethod=Method(playerType,"get_Velocity",typeof(Vector3));
                var visual=Method(playerType,"VisualPass",typeof(void));
                var ccMove=Method(simple,"Move",typeof(CollisionFlags),typeof(Vector3),typeof(float));
                var check=Method(simple,"CheckSpeedLimit",typeof(void),typeof(Vector3),typeof(float));
                RequireCalls(ccMove,"SingleMove","CalculateVelocity","CheckSpeedLimit");
                RequireCalls(check,"get_sqrMagnitude","get_normalized","set_IsSpeedLimitWasEnabledAtTheFrame");
                RequireCalls(direct,"get_PlatformMotion","Move");
                RequireCalls(visual,"get_Motion","get_Velocity","ProcessEffectors");
                // Method signatures and actual dispatch, not obsolete metadata tokens.
                var c=Expression.Parameter(typeof(object),"controller");
                var m=Expression.Parameter(typeof(Vector3),"motion");
                var dt=Expression.Parameter(typeof(float),"dt");
                nativeMove=Expression.Lambda<Func<object,Vector3,float,CollisionFlags>>(
                    Expression.Call(Expression.Convert(c,iface),moveMethod,m,dt),c,m,dt).Compile();
                var obj=Expression.Parameter(typeof(object),"obj");
                stateOf=Expression.Lambda<Func<object,object>>(Expression.Convert(
                    Expression.Property(Expression.Convert(obj,ctx),"CurrentState"),typeof(object)),obj).Compile();
                var dest=AccessTools.Field(stateType,"Destination");
                if(dest==null || dest.FieldType!=typeof(Vector3?)) throw new MissingFieldException("Destination");
                destination=Expression.Lambda<Func<object,Vector3?>>(
                    Expression.Field(Expression.Convert(obj,stateType),dest),obj).Compile();
                var v=Expression.Parameter(typeof(Vector3?),"value");
                setDestination=Expression.Lambda<Action<object,Vector3?>>(Expression.Assign(
                    Expression.Field(Expression.Convert(obj,stateType),dest),v),obj,v).Compile();
                platformMotion=Bound<Vector3>(movement,"PlatformMotion");
                mounted=Bound<bool>(movement,"IsInMountedState");
                // R1 incorrectly used the solver's last penetration-contact flag.
                // EFT's UpdateGroundCollision combines it with a ground query and
                // stores the result in MovementContext.IsGrounded. Use that same
                // locomotion/animation decision; do NOT add our own physics query.
                grounded=BindLocomotionGround(movement);
                rawControllerGrounded=Bound<bool>(cc,"isGrounded");
                enabled=Bound<bool>(cc,"isEnabled");
                speedLimit=Bound<float>(cc,"SpeedLimit");
                setSpeedLimit=(Action<float>)Delegate.CreateDelegate(typeof(Action<float>),cc,
                    Method(simple,"set_SpeedLimit",typeof(void),typeof(float)));
                fixedDistance=Field<float>(cc,"_fixedDeltaDistance");
                maxCycles=Field<int>(cc,"_maxCycleCountToHalt");
                movementTransform=Field<Transform>(cc,"_movementTransform")();
                if(movementTransform==null || fixedDistance()<=0 || fixedDistance()>0.25f)
                    throw new InvalidOperationException("CONTROLLER_SUBSTEP_ABI");
                foreach(string name in new[]{"EFT.MovePlayerState","EFT.SprintPlayerState",
                    "EFT.ProneMovePlayerState","EFT.PronePlayerState","EFT.IdlePlayerState",
                    "EFT.SprintToIdleTransitionState"}) states.Add(Eft(name));
                jumpState=Eft("EFT.JumpPlayerState");
                fallState=Eft("EFT.FallDown");
                landingState=Eft("EFT.JumpLandingState");
                RequireCalls(Method(jumpState,"ProcessJumpMovement",typeof(void),typeof(float)),"LimitMotion","ApplyMotion");
                foreach(var method in new[]{direct,visual,ccMove,check}) Audit(method);
                player=localPlayer; context=movement; controller=cc;
                harmony=new Harmony(Id);
                harmony.Patch(visual,transpiler:new HarmonyMethod(typeof(CharacterMovementBridge),nameof(VisualTranspiler)));
                harmony.Patch(direct,transpiler:new HarmonyMethod(typeof(CharacterMovementBridge),nameof(MoveTranspiler)));
                HarmonyAudit.Require(visual,Id,nameof(VisualTranspiler),"transpiler");
                HarmonyAudit.Require(direct,Id,nameof(MoveTranspiler),"transpiler");
                foreach(var method in new[]{direct,visual}) Audit(method);
                log.LogInfo("[GigaPMC] MOVEMENT_ARMED|BUILD="+Build+"|CONTROLLER="+cc.GetType().FullName+
                    "|CONTEXT="+movement.GetType().FullName+"|FROZEN_PERCENT="+percent+
                    "|FACTOR="+multiplier+"|NATIVE_STEP="+fixedDistance()+
                    "|GROUND_GATE=MovementContext.IsGrounded|AIRBORNE_CONTINUITY=ON|JUMP_SCALING="+scaleJump+
                    "|JUMP_STRENGTH_PERCENT="+jumpStrengthPercent+"|MAX_BOOSTED_SUBSTEPS=64");
                WorldSpeedProbe.Arm(movementTransform,stateOf,context,logger,percent);
            }
            catch(Exception ex)
            {
                Disarm();
                log?.LogWarning("[GigaPMC] MOVEMENT_BLOCKED|"+ex.GetType().Name+"|"+ex.Message+
                    "|NATIVE_MOVEMENT_RETAINED|ADS_HP_CARRY_STAMINA_MELEE_UNAFFECTED");
            }
        }

        private static Func<bool> BindLocomotionGround(object movement) => Bound<bool>(movement,"IsGrounded");

        private static IEnumerable<CodeInstruction> MoveTranspiler(IEnumerable<CodeInstruction> source)
        {
            var il=source.ToList();
            int[] anchors=Enumerable.Range(0,il.Count).Where(i=>il[i].Calls(moveMethod)).ToArray();
            if(anchors.Length!=1) throw new InvalidOperationException("MOVEMENT_MOVE_CALL_COUNT="+anchors.Length);
            // Original stack: controller, already limited motion + platform, dt.
            // Add context; wrapper returns exactly the original CollisionFlags.
            var original=il[anchors[0]]; original.opcode=OpCodes.Ldarg_0; original.operand=null;
            il.Insert(anchors[0]+1,new CodeInstruction(OpCodes.Call,
                AccessTools.Method(typeof(CharacterMovementBridge),nameof(Move))));
            return il;
        }
        private static IEnumerable<CodeInstruction> VisualTranspiler(IEnumerable<CodeInstruction> source)
        {
            var il=source.ToList();
            int[] anchors=Enumerable.Range(0,il.Count).Where(i=>il[i].Calls(velocityMethod)).ToArray();
            if(anchors.Length!=1 || anchors[0]+1>=il.Count ||
               !(il[anchors[0]+1].operand is MethodInfo target) || target.Name!="ProcessEffectors")
                throw new InvalidOperationException("MOVEMENT_VISUAL_CALL_SHAPE_DRIFT");
            // Only the velocity argument delivered to weapon effectors is changed.
            // Physics, thrown objects, damage and other velocity readers stay native.
            il.Insert(anchors[0]+1,new CodeInstruction(OpCodes.Ldarg_0));
            il.Insert(anchors[0]+2,new CodeInstruction(OpCodes.Call,
                AccessTools.Method(typeof(CharacterMovementBridge),nameof(VisualVelocity))));
            return il;
        }
        private static Vector3 VisualVelocity(Vector3 native,object who)
        {
            if(ReferenceEquals(who,player) && visualFactor>1f &&
               Time.frameCount-visualFrame>=0 && Time.frameCount-visualFrame<=1)
            { native.x/=visualFactor; native.z/=visualFactor; }
            return native;
        }

        // Called only at the native controller call site. No reflection,
        // allocations, scene queries or per-frame logging in this path.
        private static CollisionFlags Move(object cc,Vector3 motion,float dt,object ctx)
        {
            if(!ReferenceEquals(ctx,context) || !ReferenceEquals(cc,controller) || moving || disabled)
                return nativeMove(cc,motion,dt);
            visualFactor=1f; visualFrame=Time.frameCount;
            if(multiplier<=1f) return Unscaled(cc,motion,dt,MovementGateReason.ZeroBonus);
            if(!enabled()) {airborne.Reset();return Unscaled(cc,motion,dt,MovementGateReason.Disabled);}
            if(mounted()) {airborne.Reset();return Unscaled(cc,motion,dt,MovementGateReason.Mounted);}
            if(platformMotion().sqrMagnitude>0f) {airborne.Reset();return Unscaled(cc,motion,dt,MovementGateReason.Platform);}
            object state=stateOf(ctx);
            Type type=state?.GetType();
            LocomotionPhase phase=type==jumpState&&type!=null?LocomotionPhase.Jump:
                type==fallState&&type!=null?LocomotionPhase.Fall:
                type==landingState&&type!=null?LocomotionPhase.Landing:
                type!=null&&states.Contains(type)?LocomotionPhase.Ground:LocomotionPhase.Excluded;
            bool onGround=grounded();
            float requested=airborne.Select(phase,onGround,multiplier,scaleJump,jumpStrengthPercent);
            if(phase==LocomotionPhase.Excluded)return Unscaled(cc,motion,dt,MovementGateReason.State);
            if(requested<=1f)return Unscaled(cc,motion,dt,MovementGateReason.Airborne);
            float factor=CharacterMovementMath.FrameFactor(motion.x,motion.y,motion.z,dt,requested,
                fixedDistance(),maxCycles());
            float limit=speedLimit();
            if(factor<=1f) return Unscaled(cc,motion,dt,MovementGateReason.FrameBudget);
            if(!CharacterMovementMath.Finite(limit) ||
               !CharacterMovementMath.Finite(CharacterMovementMath.Limit(limit,factor)))
                return Unscaled(cc,motion,dt,MovementGateReason.InvalidLimit);
            if(WorldSpeedProbe.Measuring)
                WorldSpeedProbe.ObserveGate(MovementGateReason.Boosted,onGround,rawControllerGrounded());
            Vector3 before=movementTransform.position;
            Vector3? expected=destination(state);
            Vector3 physical=motion; physical.x*=factor; physical.z*=factor;
            CollisionFlags result;
            moving=true;
            try
            {
                setSpeedLimit(CharacterMovementMath.Limit(limit,factor));
                result=nativeMove(cc,physical,dt); // exactly ONE native solver call
            }
            finally
            {
                try { setSpeedLimit(limit); } // never retry a partially completed move
                finally { moving=false; }
            }
            if(WorldSpeedProbe.Measuring)
                WorldSpeedProbe.ObserveMotion(motion,physical,movementTransform.position-before,dt,limit,factor);
            if(expected.HasValue)
            {
                Vector3 target=expected.Value, after=movementTransform.position;
                target.x=CharacterMovementMath.Destination(target.x,before.x,after.x,factor);
                target.z=CharacterMovementMath.Destination(target.z,before.z,after.z,factor);
                setDestination(state,target);
            }
            visualFactor=factor;
            amplified++;
            if(factor<requested-0.0001f) budgetLimited++;
            return result;
        }
        private static CollisionFlags Unscaled(object cc,Vector3 motion,float dt,MovementGateReason gate)
        {
            if(!WorldSpeedProbe.Measuring) return nativeMove(cc,motion,dt);
            WorldSpeedProbe.ObserveGate(gate,grounded(),rawControllerGrounded());
            Vector3 before=movementTransform.position;
            float limit=speedLimit();
            CollisionFlags result=nativeMove(cc,motion,dt);
            WorldSpeedProbe.ObserveMotion(motion,motion,movementTransform.position-before,dt,limit,1f);
            return result;
        }
        internal static void Disarm()
        {
            WorldSpeedProbe.Disarm();
            if(harmony!=null)
            {
                log?.LogInfo("[GigaPMC] MOVEMENT_SUMMARY|AMPLIFIED_CALLS="+amplified+
                    "|BUDGET_LIMITED_CALLS="+budgetLimited+"|NOT_GAMEPLAY_CERTIFICATION");
                harmony.UnpatchSelf(); harmony=null;
            }
            player=context=controller=null; movementTransform=null; states.Clear();
            airborne.Reset();jumpState=fallState=landingState=null;
            scaleJump=true;jumpStrengthPercent=100f;
            multiplier=visualFactor=1f; visualFrame=-1; moving=disabled=false;
            amplified=budgetLimited=0;
            // Keep compiled dispatch until Harmony removes all call sites.
        }
    }
}

