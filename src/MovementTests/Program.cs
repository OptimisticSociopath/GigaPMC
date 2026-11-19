using System.Reflection;
using System.Reflection.Emit;
using GigaPMC.Client;
using UnityEngine;
using HarmonyLib;

try
{
if(args.Contains("--verify-failure-reporting")) throw new InvalidOperationException("EXPECTED_TEST_FAILURE_NO_WINDOWS_DIALOG");
int passed=0;
void Check(bool ok,string name) {if(!ok)throw new Exception("FAIL|"+name);passed++;Console.WriteLine("PASS|"+name);}
bool Near(float a,float b)=>Math.Abs(a-b)<0.0001f;
var bridge=typeof(CharacterMovementBridge);
void Set(string name,object value)=>bridge.GetField(name,BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,value);
T Get<T>(string name)=>(T)bridge.GetField(name,BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
var move=bridge.GetMethod("Move",BindingFlags.NonPublic|BindingFlags.Static)
    .CreateDelegate<Func<object,Vector3,float,object,CollisionFlags>>();
var visual=bridge.GetMethod("VisualVelocity",BindingFlags.NonPublic|BindingFlags.Static)
    .CreateDelegate<Func<Vector3,object,Vector3>>();
object player=new(),ctx=new(),cc=new(),state=new WalkState();
Transform root=new(); Vector3? expected=null; Vector3 seen=default;
float limit=7f,seenLimit=0f; bool grounded=true,mounted=false,throws=false;
int calls=0;
Func<object,Vector3,float,CollisionFlags> solver=(c,m,dt)=>
{
    calls++;seen=m;seenLimit=limit;
    if(throws)throw new InvalidOperationException("NATIVE_SOLVER_FAILURE");
    root.position=new(root.position.x+m.x,root.position.y+m.y,root.position.z+m.z);
    return CollisionFlags.Sides|CollisionFlags.Below;
};
void Setup(float factor=2f)
{
    CharacterMovementBridge.Disarm();
    root.position=default;expected=new Vector3(.1f,-.01f,0);limit=7;calls=0;throws=false;
    grounded=true;mounted=false;Time.frameCount=100;
    Set("player",player);Set("context",ctx);Set("controller",cc);Set("movementTransform",root);
    Set("multiplier",factor);Set("nativeMove",solver);
    Set("stateOf",(Func<object,object>)(_=>state));
    Set("platformMotion",(Func<Vector3>)(()=>default));
    Set("grounded",(Func<bool>)(()=>grounded));Set("enabled",(Func<bool>)(()=>true));
    Set("rawControllerGrounded",(Func<bool>)(()=>false));
    Set("mounted",(Func<bool>)(()=>mounted));Set("speedLimit",(Func<float>)(()=>limit));
    Set("setSpeedLimit",(Action<float>)(v=>limit=v));
    Set("fixedDistance",(Func<float>)(()=>.1f));Set("maxCycles",(Func<int>)(()=>1000));
    Set("destination",(Func<object,Vector3?>)(_=>expected));
    Set("setDestination",(Action<object,Vector3?>)((_,v)=>expected=v));
    Get<HashSet<Type>>("states").Add(typeof(WalkState));
    Set("jumpState",typeof(JumpPlayerState));Set("fallState",typeof(FallDown));Set("landingState",typeof(JumpLandingState));
}

foreach(decimal percent in new[]{0m,25m,50m,100m,1600m,1801m})
{
    float f=CharacterMovementMath.Multiplier(percent);Setup(f);
    move(cc,new Vector3(.1f,-.01f,.03f),.02f,ctx);
    Check(Near(seen.x,.1f*f)&&Near(seen.z,.03f*f)&&seen.y==-.01f&&calls==1&&limit==7,
        "PRODUCTION_WRAPPER_PERCENT_"+percent+"_XZ_ONLY_ONE_CALL_LIMIT_RESTORED");
}
Setup();var result=move(cc,new(.1f,-.01f,0),.02f,ctx);
Check(result==(CollisionFlags.Sides|CollisionFlags.Below)&&seenLimit==14,"COLLISION_FLAGS_RETURNED_LIMIT_APPLIED_DURING_CALL");
Check(Near(expected.Value.x,.2f)&&expected.Value.y==-.01f,"PRONE_DESTINATION_NO_PHANTOM_SHORTAGE_FREE_MOVE");
var v=visual(new(10,3,4),player);
Check(v.x==5&&v.y==3&&v.z==2,"WEAPON_VISUAL_XZ_ONLY_NORMALIZED");
Check(visual(new(10,3,4),new object()).x==10,"OTHER_PLAYER_VISUAL_UNCHANGED");
Time.frameCount=102;
Check(visual(new(10,3,4),player).x==10,"STALE_VISUAL_FACTOR_EXPIRES");
Setup();throws=true;
try{move(cc,new(.1f,0,0),.02f,ctx);Check(false,"EXPECTED_EXCEPTION");}catch(InvalidOperationException){}
Check(limit==7&&calls==1&&!Get<bool>("moving"),"EXCEPTION_RESTORES_LIMIT_NO_RETRY_NO_STUCK_SCOPE");
Setup();move(cc,new(.1f,0,0),.02f,new object());
Check(seen.x==.1f&&seenLimit==7&&calls==1,"BOT_SCAV_REMOTE_CONTEXT_UNCHANGED");
Setup();move(new object(),new(.1f,0,0),.02f,ctx);
Check(seen.x==.1f&&seenLimit==7,"SWAPPED_CONTROLLER_UNCHANGED");
Setup();grounded=false;move(cc,new(.1f,1,0),.02f,ctx);
Check(seen.x==.1f&&seen.y==1,"AIRBORNE_JUMP_UNCHANGED");
Setup();mounted=true;move(cc,new(.1f,0,0),.02f,ctx);
Check(seen.x==.1f,"MOUNTED_UNCHANGED");
Setup();Set("platformMotion",(Func<Vector3>)(()=>new(1,0,0)));move(cc,new(.1f,0,0),.02f,ctx);
Check(seen.x==.1f,"MOVING_PLATFORM_UNCHANGED");
Setup();state=new VaultState();move(cc,new(.1f,0,0),.02f,ctx);
Check(seen.x==.1f,"VAULT_UNKNOWN_STATE_UNCHANGED");state=new WalkState();
Setup();limit=-1;move(cc,new(.1f,0,0),.02f,ctx);
Check(seenLimit==-1&&limit==-1,"UNLIMITED_NATIVE_SENTINEL_PRESERVED");
Setup(101);move(cc,new(.3f,-.01f,0),.02f,ctx);
Check(seen.x<6.4f&&seen.x>.3f&&Get<long>("budgetLimited")==1,"EXTREME_BONUS_BOUNDED_NATIVE_SOLVER_BUDGET");
Setup();move(cc,new(.1f,-.01f,0),.2f,ctx);
Check(seen.x==.1f,"LONG_FRAME_PRESERVES_NATIVE_MOTION");
foreach(float invalid in new[]{float.NaN,float.PositiveInfinity,float.NegativeInfinity})
    Check(CharacterMovementMath.FrameFactor(invalid,0,.1f,.02f,2,.1f,1000)==1,"NONFINITE_REJECT_"+invalid);
Setup();Set("nativeMove",(Func<object,Vector3,float,CollisionFlags>)((_,m,dt)=>{
    calls++;root.position=new(.05f,0,0);return CollisionFlags.Sides;
}));
move(cc,new(.1f,0,0),.02f,ctx);
Check(Near(expected.Value.x-root.position.x,.075f),"BLOCKED_MOVE_SHORTAGE_REMAINS_NATIVE_UNITS");
Setup();for(int i=0;i<10000;i++)move(cc,new(.1f,0,0),.02f,ctx);
long allocated=GC.GetAllocatedBytesForCurrentThread();
for(int i=0;i<10000;i++)move(cc,new(.1f,0,0),.02f,ctx);
allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;
Check(allocated==0,"PRODUCTION_MOVE_WRAPPER_10000_CALLS_ZERO_MANAGED_ALLOCATIONS_BYTES_"+allocated);

// Execute actual production transpilers with instruction-data doubles.
var moveTarget=typeof(Targets).GetMethod(nameof(Targets.Move));Set("moveMethod",moveTarget);
List<CodeInstruction> Transpile(string name,List<CodeInstruction> il) =>
    ((IEnumerable<CodeInstruction>)bridge.GetMethod(name,BindingFlags.NonPublic|BindingFlags.Static)
        .Invoke(null,new object[]{il})).ToList();
var call=new CodeInstruction(OpCodes.Callvirt,moveTarget);object label=new(),block=new();
call.labels.Add(label);call.blocks.Add(block);
var patched=Transpile("MoveTranspiler",new(){new(OpCodes.Nop),call,new(OpCodes.Ret)});
Check(ReferenceEquals(patched[1],call)&&patched[1].opcode==OpCodes.Ldarg_0&&
    patched[1].labels[0]==label&&patched[1].blocks[0]==block&&
    ((MethodInfo)patched[2].operand).Name=="Move","MOVE_TRANSPILER_PRESERVES_LABELS_AND_EXCEPTION_BLOCKS");
try{Transpile("MoveTranspiler",new(){new(OpCodes.Ret)});Check(false,"EXPECTED_ANCHOR_REJECTION");}
catch(TargetInvocationException e){Check(e.InnerException is InvalidOperationException,"MISSING_MOVE_ANCHOR_FAIL_CLOSED");}
var velocity=typeof(Targets).GetMethod(nameof(Targets.Velocity));Set("velocityMethod",velocity);
var effects=typeof(Targets).GetMethod(nameof(Targets.ProcessEffectors));
patched=Transpile("VisualTranspiler",new(){new(OpCodes.Call,velocity),new(OpCodes.Callvirt,effects),new(OpCodes.Ret)});
Check(patched.Count==5&&Equals(patched[0].operand,velocity)&&patched[1].opcode==OpCodes.Ldarg_0&&
    ((MethodInfo)patched[2].operand).Name=="VisualVelocity"&&Equals(patched[3].operand,effects),
    "VISUAL_TRANSPILER_CHANGES_ONLY_EFFECTOR_ARGUMENT");
// Runtime resolver must support abstract interfaces and inherited implementation.
var methodResolver=bridge.GetMethod("Method",BindingFlags.NonPublic|BindingFlags.Static);
var resolved=(MethodInfo)methodResolver.Invoke(null,new object[]{typeof(IMover),"Move",typeof(CollisionFlags),new[]{typeof(Vector3),typeof(float)}});
Check(resolved.IsAbstract,"INTERFACE_ABI_WITHOUT_METHOD_BODY_ACCEPTED");
resolved=(MethodInfo)methodResolver.Invoke(null,new object[]{typeof(DerivedMover),"Move",typeof(CollisionFlags),new[]{typeof(Vector3),typeof(float)}});
Check(resolved.DeclaringType==typeof(BaseMover),"INHERITED_CONTEXT_DISPATCH_RESOLVED");

// P07 regression: Fika-style subclass inherits native DirectApplyMotion;
// only a genuinely different override must be rejected.
var identical=bridge.GetMethod("SameDirectImplementation",BindingFlags.NonPublic|BindingFlags.Static)
    .CreateDelegate<Func<MethodInfo,MethodInfo,bool>>();
var baseDirect=(MethodInfo)methodResolver.Invoke(null,new object[]{typeof(NativeMovementContext),
    "DirectApplyMotion",typeof(void),new[]{typeof(Vector3),typeof(float)}});
var inheritedDirect=(MethodInfo)methodResolver.Invoke(null,new object[]{typeof(FikaInheritedMovementContext),
    "DirectApplyMotion",typeof(void),new[]{typeof(Vector3),typeof(float)}});
var overriddenDirect=(MethodInfo)methodResolver.Invoke(null,new object[]{typeof(GenuineOverrideMovementContext),
    "DirectApplyMotion",typeof(void),new[]{typeof(Vector3),typeof(float)}});
Check(identical(baseDirect,inheritedDirect)&&baseDirect.DeclaringType==inheritedDirect.DeclaringType,
    "P07_FIKA_INHERITED_DIRECT_APPLY_ACCEPTED_BY_METADATA_IDENTITY");
Check(!identical(baseDirect,overriddenDirect)&&baseDirect.DeclaringType!=overriddenDirect.DeclaringType,
    "P07_GENUINE_DIRECT_APPLY_OVERRIDE_REMAINS_BLOCKED");

// Reproduce the condition R1 missed: EFT detects support with its raycast,
// while the last solver penetration contact is false on a flat surface.
Setup();
var groundContext=new GroundContext{IsGrounded=true};
var bindGround=bridge.GetMethod("BindLocomotionGround",BindingFlags.NonPublic|BindingFlags.Static)
    .CreateDelegate<Func<object,Func<bool>>>();
Set("grounded",bindGround(groundContext));
bool rawContact=false;
Set("rawControllerGrounded",(Func<bool>)(()=>rawContact));
move(cc,new(.1f,0,0),.02f,ctx);
Check(Near(seen.x,.2f)&&calls==1,"R1_REGRESSION_FLAT_CONTEXT_TRUE_RAW_CONTACT_FALSE_BOOSTS");
rawContact=true;move(cc,new(.1f,0,0),.02f,ctx);
Check(Near(seen.x,.2f),"CURB_CONTACT_DOES_NOT_CHANGE_MULTIPLIER");
groundContext.IsGrounded=false;move(cc,new(.1f,0,0),.02f,ctx);
Check(Near(seen.x,.2f),"DESCENDING_CONTACT_LOSS_RETAINS_HORIZONTAL_BONUS");
groundContext.IsGrounded=true;
for(int i=0;i<100;i++){rawContact=(i%2)==0;move(cc,new(.1f,0,0),.02f,ctx);if(!Near(seen.x,.2f))throw new Exception("CONTACT_DEPENDENT_BOOST");}
Check(true,"100_ALTERNATING_PENETRATION_CONTACTS_CONSTANT_BONUS");

// Actual wrapper: repeated ground/air changes must keep horizontal scaling.
state=new WalkState();Setup(11f);
bool continuous=true;
for(int i=0;i<300;i++)
{
    grounded=i%4==0;state=grounded?new WalkState():new FallDown();
    move(cc,new(.05f,-.015f,.01f),.02f,ctx);
    continuous&=Near(seen.x,.55f)&&Near(seen.z,.11f)&&seen.y==-.015f&&limit==7;
}
Check(continuous&&calls==300,"DOWN_STAIRS_300_GROUND_AIR_TRANSITIONS_CONSTANT_BONUS_ONE_NATIVE_MOVE");
foreach(bool sprint in new[]{false,true})
foreach(float strength in new[]{0f,50f,100f})
{
    state=new WalkState();Setup(3f);Set("jumpStrengthPercent",strength);
    float x=sprint?.14f:.05f;
    move(cc,new(x,0,0),.02f,ctx);
    state=new JumpPlayerState();grounded=true;
    move(cc,new(x,.08f,0),.02f,ctx);
    float want=x*(1f+2f*strength/100f);
    Check(Near(seen.x,want)&&seen.y==.08f&&limit==7,"JUMP_TAKEOFF_"+sprint+"_STRENGTH_"+strength);
    grounded=false;state=new FallDown();move(cc,new(x,-.04f,0),.02f,ctx);
    Check(Near(seen.x,want)&&seen.y==-.04f,"JUMP_DESCENT_RETAINS_OWN_SETTING_"+sprint+"_"+strength);
    state=new JumpLandingState();grounded=true;move(cc,new(x,-.01f,0),.02f,ctx);
    Check(Near(seen.x,x*3f),"LANDING_RESTORES_MOVEMENT_BONUS_"+sprint+"_"+strength);
}
state=new WalkState();Setup(5f);Set("scaleJump",false);move(cc,new(.1f,0,0),.02f,ctx);
state=new JumpPlayerState();grounded=false;move(cc,new(.1f,.1f,0),.02f,ctx);
Check(Near(seen.x,.1f)&&seen.y==.1f,"JUMP_DISABLED_IS_NATIVE_HORIZONTAL_AND_VERTICAL");
state=new WalkState();Setup(5f);grounded=false;move(cc,new(.1f,-.02f,0),.02f,ctx);
Check(Near(seen.x,.1f),"MID_AIR_SPAWN_WITHOUT_GROUND_HISTORY_NOT_BOOSTED");
grounded=true;move(cc,new(.1f,0,0),.02f,ctx);mounted=true;move(cc,new(.1f,0,0),.02f,ctx);
mounted=false;grounded=false;move(cc,new(.1f,0,0),.02f,ctx);
Check(Near(seen.x,.1f),"MOUNTED_TRANSITION_CLEARS_AIRBORNE_HISTORY");
Check(AirborneMovementPolicy.JumpFactor(3f,true,float.NaN)==1f&&
    AirborneMovementPolicy.JumpFactor(3f,true,101f)==1f,"INVALID_JUMP_STRENGTH_FAILS_NATIVE");
state=new WalkState();

var sampleLog=new BepInEx.Logging.ManualLogSource();
double tick=0;
void StartSample(object mode)
{
    Setup();sampleLog.Messages.Clear();state=new IdlePlayerState();root.position=default;tick=0;
    WorldSpeedProbe.Arm(root,_=>state,ctx,sampleLog,100m);
    WorldSpeedProbe.TickAt(tick,true);
    for(int i=0;i<20;i++){tick+=.02;WorldSpeedProbe.TickAt(tick,false);}
    if(WorldSpeedProbe.Measuring)throw new Exception("RECORDED_IDLE_START");
    state=mode;Get<HashSet<Type>>("states").Add(mode.GetType());
    for(int i=0;i<80&&!WorldSpeedProbe.Measuring;i++)
    {tick+=.02;root.position=new(root.position.x+.04f,0,0);WorldSpeedProbe.TickAt(tick,false);}
    if(!WorldSpeedProbe.Measuring)throw new Exception("STEADY_SAMPLE_NOT_STARTED");
}
StartSample(new MovePlayerState());
Check(sampleLog.Messages.Any(x=>x.Contains("AFTER_STABLE_MOVEMENT"))&&
    sampleLog.Messages.Any(x=>x.Contains("STATE=MovePlayerState")),"PROBE_WAITS_FOR_LOCOMOTION_AFTER_IDLE");
for(int i=0;i<270&&WorldSpeedProbe.Measuring;i++)
{
    tick+=.02;move(cc,new(.04f,0,0),.02f,ctx);WorldSpeedProbe.TickAt(tick,false);
}
Check(sampleLog.Messages.Any(x=>x.Contains("STATUS=COMPLETE")&&x.Contains("XZ_MPS=4.000")&&x.Contains("OTHER_STATE_FRAMES=0")),
    "PROBE_STEADY_WORLD_DISPLACEMENT_4_MPS");
Check(sampleLog.Messages.Any(x=>x.Contains("MOVEMENT_GATE_SAMPLE")&&x.Contains("RAW_CONTACT_TRUE=0")&&
    !x.Contains("CONTEXT_TRUE_RAW_FALSE=0")&&x.Contains("MAX_FACTOR=2.00000")),
    "PROBE_REPORTS_ACTUAL_WRAPPER_GATE_DISAGREEMENT_AND_FACTOR");
StartSample(new MovePlayerState());
for(int i=0;i<270&&WorldSpeedProbe.Measuring;i++)
{
    grounded=i%4==0;state=grounded?new MovePlayerState():new FallDown();
    tick+=.02;move(cc,new(.04f,-.01f,0),.02f,ctx);WorldSpeedProbe.TickAt(tick,false);
}
Check(sampleLog.Messages.Any(x=>x.Contains("STATUS=COMPLETE")&&x.Contains("XZ_MPS=4.000")&&
    x.Contains("DESCENT_FRAMES=")&&!x.Contains("DESCENT_FRAMES=0")),"PROBE_MEASURES_DESCENT_TRANSITIONS_WITHOUT_MIXING_WALK_AND_SPRINT");
StartSample(new MovePlayerState());state=new JumpPlayerState();tick+=.02;WorldSpeedProbe.TickAt(tick,false);
Check(!WorldSpeedProbe.Measuring&&sampleLog.Messages.Any(x=>x.Contains("INVALID_STATE_CHANGED")),"PROBE_INTENTIONAL_JUMP_INVALIDATES_WALK_SAMPLE");
StartSample(new SprintPlayerState());state=new IdlePlayerState();tick+=.02;WorldSpeedProbe.TickAt(tick,false);
Check(!WorldSpeedProbe.Measuring&&sampleLog.Messages.Any(x=>x.Contains("STATUS=INVALID_STATE_CHANGED"))&&
    !sampleLog.Messages.Any(x=>x.Contains("STATUS=COMPLETE")),"MIXED_IDLE_SPRINT_SAMPLE_IS_INVALID_NOT_COMPLETE");
StartSample(new MovePlayerState());tick+=.3;WorldSpeedProbe.TickAt(tick,false);
Check(!WorldSpeedProbe.Measuring&&sampleLog.Messages.Any(x=>x.Contains("STATUS=INVALID_FRAME_GAP_OR_POSITION")),
    "PROBE_REJECTS_LONG_FRAME");
WorldSpeedProbe.Arm(root,_=>new IdlePlayerState(),ctx,sampleLog,25m);
WorldSpeedProbe.TickAt(0,true);WorldSpeedProbe.TickAt(30,false);
Check(sampleLog.Messages.Any(x=>x.Contains("TIMEOUT_NO_STEADY_MOVEMENT")),"PROBE_HAS_BOUNDED_WAIT");
WorldSpeedProbe.Disarm();
state=new WalkState();Setup();
long counterBefore=typeof(WorldSpeedProbe).GetField("contextGround",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null) is long c0?c0:-1;
for(int i=0;i<1000;i++)move(cc,new(.04f,0,0),.02f,ctx);
long counterAfter=(long)typeof(WorldSpeedProbe).GetField("contextGround",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
Check(counterBefore==0&&counterAfter==0,"NO_CONTROLLER_DIAGNOSTICS_OUTSIDE_USER_SAMPLE");
Console.WriteLine("MOVEMENT_OFFLINE_PASS|CHECKS="+passed+"|UNITY_PHYSICS_AND_LIVE_HARMONY_NOT_EXECUTED");
}
catch(Exception ex)
{
    Console.Error.WriteLine("MOVEMENT_OFFLINE_FAIL|"+ex);
    Environment.ExitCode=1;
}
class WalkState {} class VaultState {}
class JumpPlayerState {} class FallDown : JumpPlayerState {} class JumpLandingState {}
class GroundContext {public bool IsGrounded{get;set;}}
class IdlePlayerState{} class MovePlayerState{} class SprintPlayerState{}
interface IMover{CollisionFlags Move(Vector3 m,float dt);}
class BaseMover:IMover{public virtual CollisionFlags Move(Vector3 m,float dt)=>0;}
class DerivedMover:BaseMover{}
static class Targets
{
    public static CollisionFlags Move(Vector3 m,float dt)=>0;
    public static Vector3 Velocity()=>default;
    public static void ProcessEffectors(){}
}


class NativeMovementContext {public virtual void DirectApplyMotion(Vector3 m,float dt) {}}
class FikaInheritedMovementContext:NativeMovementContext {}
class GenuineOverrideMovementContext:NativeMovementContext {public override void DirectApplyMotion(Vector3 m,float dt) {}}
