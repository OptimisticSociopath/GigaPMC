using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Security.Cryptography;
using System.Text.Json;

try
{
if(args.Length!=3)throw new ArgumentException("game-root client-dll evidence-folder");
string game=Path.GetFullPath(args[0]),client=Path.GetFullPath(args[1]),output=Path.GetFullPath(args[2]);
Directory.CreateDirectory(output);
string eft=Path.Combine(game,"EscapeFromTarkov_Data","Managed","Assembly-CSharp.dll");
string Hash(string p)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));
if(Hash(eft)!="EE25CEE1259777B38ED8B3E7841FDC2DB3C98540B1469FA539B1FF183476E436")
    throw new Exception("FAIL_CLOSED|UNAUDITED_EFT_ASSEMBLY");
using var native=ModuleDefinition.ReadModule(eft);
using var candidate=ModuleDefinition.ReadModule(client);
using var harmony=ModuleDefinition.ReadModule(Path.Combine(game,"BepInEx","core","0Harmony.dll"));
int passed=0;
void Check(bool ok,string name){if(!ok)throw new Exception("FAIL_CLOSED|"+name);passed++;Console.WriteLine("PASS|"+name);}
TypeDefinition Type(string n)=>native.Types.Single(t=>t.FullName==n);
MethodDefinition Method(string t,string m)=>Type(t).Methods.Single(x=>x.Name==m);
IEnumerable<MethodReference> Calls(MethodDefinition m)=>m.Body.Instructions.Where(i=>i.OpCode==OpCodes.Call||i.OpCode==OpCodes.Callvirt).Select(i=>i.Operand).OfType<MethodReference>();
var direct=Method("EFT.MovementContext","DirectApplyMotion");
var moves=Calls(direct).Where(m=>m.FullName=="UnityEngine.CollisionFlags ICharacterController::Move(UnityEngine.Vector3,System.Single)").ToArray();
Check(moves.Length==1,"ACTUAL_NATIVE_CONTROLLER_CALL_EXACTLY_ONE");
Check(direct.Body.Instructions.Any(i=>i.OpCode==OpCodes.Stfld&&i.Operand is FieldReference f&&f.Name=="InputMotion"),"ANIMATION_INPUT_STORED_BEFORE_CONTROLLER");
var visual=Method("EFT.Player","VisualPass");
var visualIl=visual.Body.Instructions;
int velocity=visualIl.Select((i,n)=>(i,n)).Single(x=>x.i.Operand is MethodReference m&&m.FullName=="UnityEngine.Vector3 EFT.Player::get_Velocity()").n;
Check(visualIl[velocity+1].Operand is MethodReference effects&&effects.Name=="ProcessEffectors","WEAPON_EFFECTOR_VELOCITY_CALL_SITE");
var ccMove=Method("SimpleCharacterController","Move");
foreach(string method in new[]{"SingleMove","CalculateVelocity","CheckSpeedLimit"})
    Check(Calls(ccMove).Any(m=>m.Name==method),"NATIVE_CONTROLLER_PIPELINE_"+method);
foreach(string field in new[]{"_fixedDeltaDistance","_maxCycleCountToHalt","_movementTransform","_speedLimit","_sqrSpeedLimit"})
    Check(Type("SimpleCharacterController").Fields.Any(f=>f.Name==field),"CONTROLLER_BINDING_"+field);
foreach(string prop in new[]{"SpeedLimit","isGrounded","isEnabled"})
    Check(Type("SimpleCharacterController").Properties.Count(p=>p.Name==prop)==1,"CONTROLLER_PROPERTY_"+prop);
foreach(string prop in new[]{"CurrentState","PlatformMotion","IsInMountedState","CharacterController","IsGrounded"})
    Check(Type("EFT.MovementContext").Properties.Count(p=>p.Name==prop)==1,"CONTEXT_PROPERTY_"+prop);
foreach(string name in new[]{"EFT.MovePlayerState","EFT.SprintPlayerState","EFT.ProneMovePlayerState","EFT.PronePlayerState","EFT.IdlePlayerState","EFT.SprintToIdleTransitionState"})
    Check(Type(name)!=null,"LOCOMOTION_STATE_"+name);
foreach(string name in new[]{"EFT.JumpPlayerState","EFT.FallDown","EFT.JumpLandingState"})
    Check(Type(name).BaseType.FullName==(name=="EFT.FallDown"?"EFT.JumpPlayerState":"EFT.MovementState"),"AIRBORNE_STATE_"+name);
var jump=Method("EFT.JumpPlayerState","ProcessJumpMovement");
Check(Calls(jump).Any(m=>m.Name=="LimitMotion")&&Calls(jump).Any(m=>m.DeclaringType.FullName=="EFT.MovementContext"&&m.Name=="ApplyMotion"),"JUMP_REACHES_SAME_NATIVE_CONTROLLER_PATH");
Check(Calls(jump).Any(m=>m.DeclaringType.FullName=="UnityEngine.Physics"&&m.Name=="get_gravity"),"NATIVE_JUMP_GRAVITY_PATH_PRESENT");
var jumpEnter=Method("EFT.JumpPlayerState","Enter");
Check(jumpEnter.Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name=="InputMotionBeforeLimit")&&
    !Calls(jumpEnter).Any(m=>m.Name=="get_Velocity"),"JUMP_START_READS_UNSCALED_INPUT_NOT_BOOSTED_WORLD_VELOCITY");
Check(Method("EFT.MovementContext","LimitMotionXZ").Body.Instructions.Any(i=>i.OpCode==OpCodes.Stfld&&i.Operand is FieldReference f&&f.Name=="InputMotionBeforeLimit"),"NATIVE_INPUT_SAVED_BEFORE_FINAL_BRIDGE");
Check(Type("EFT.MovementState").Fields.Single(f=>f.Name=="Destination").FieldType.FullName=="System.Nullable`1<UnityEngine.Vector3>","DESTINATION_ABI");
Check(!Type("EFT.ClientPlayerMovementContext").Methods.Any(m=>m.Name=="DirectApplyMotion"),"CLIENT_CONTEXT_INHERITS_FINAL_CONTROLLER_PATH");
Check(Calls(Method("EFT.ClientPlayerMovementContext","ApplyMotion")).Any(m=>m.DeclaringType.FullName=="EFT.MovementContext"&&m.Name=="ApplyMotion"),"CLIENT_OVERRIDE_SCHEDULES_BASE_MOTION");
Check(harmony.Types.Single(t=>t.FullName=="HarmonyLib.PatchProcessor").Methods.Count(m=>m.Name=="GetOriginalInstructions"&&m.Parameters.Count==2&&!m.Parameters[1].ParameterType.IsByReference)==1,"HARMONY_IL_READER_OVERLOAD_DISAMBIGUATED");

IEnumerable<TypeDefinition> Flatten(IEnumerable<TypeDefinition> types){foreach(var t in types){yield return t;foreach(var n in Flatten(t.NestedTypes))yield return n;}}
var all=Flatten(candidate.Types).SelectMany(t=>t.Methods).Where(m=>m.HasBody).ToArray();
var retired=new HashSet<string>{"InstallMovement","InstallSprintAcceleration","InstallWalkTargetClamp","InstallWalkMotion","InstallSprintLimitBridge","InstallLegacyIndependentNeverCalled"};
Check(!all.SelectMany(Calls).Any(m=>m.DeclaringType.FullName=="GigaPMC.Client.NativeAttributeEffects"&&retired.Contains(m.Name)),"COMPILED_ASSEMBLY_HAS_NO_CALLS_TO_RETIRED_MOVEMENT_INSTALLERS");
Check(!all.Any(m=>m.DeclaringType.FullName=="GigaPMC.Client.NativeAttributeEffects"&&retired.Contains(m.Name)),"RETIRED_INSTALLER_DEFINITIONS_REMOVED");
Check(!candidate.Types.Any(t=>new[]{"MovementNesting","SprintAccelerationMath","SprintIlContract","WalkTargetIlContract","WalkTargetMath","NativeSteeringGuard","ExtraDisplacementMath","SprintLimitMath","WalkMotionMath"}.Contains(t.Name)),"HISTORICAL_MOVEMENT_HELPERS_NOT_IN_CLIENT_DLL");
var live=all.Where(m=>new[]{"GigaPMC.Client.CharacterMovementBridge","GigaPMC.Client.CharacterMovementMath","GigaPMC.Client.WorldSpeedProbe","GigaPMC.Client.AirborneMovementPolicy"}.Any(n=>m.DeclaringType.FullName==n||m.DeclaringType.FullName.StartsWith(n+"/",StringComparison.Ordinal))).ToArray();
Check(!live.SelectMany(Calls).Any(m=>m.Name=="set_position"||m.Name=="set_localPosition"||m.Name=="Simulate"),"NO_BRIDGE_TRANSFORM_WRITES_OR_PHYSICS_SIMULATION");
var helper=live.Single(m=>m.Name=="Move");
Check(Calls(helper).Any(m=>m.DeclaringType.Name=="AirborneMovementPolicy"&&m.Name=="Select"),"COMPILED_BRIDGE_USES_AIRBORNE_CONTINUITY_POLICY");
Check(!Calls(helper).Any(m=>m.DeclaringType.FullName.StartsWith("System.Reflection")||m.Name is "LogInfo" or "LogWarning" or "FindObjectsOfTypeAll"),"MOVEMENT_WRAPPER_NO_REFLECTION_LOGGING_SCENE_SCAN");
Check(helper.Body.ExceptionHandlers.Any(h=>h.HandlerType==ExceptionHandlerType.Finally),"MOVEMENT_LIMIT_RESTORE_FINALLY_PRESENT");
var groundUpdate=Method("EFT.MovementContext","UpdateGroundCollision");
var groundIl=groundUpdate.Body.Instructions;
int groundContact=groundIl.Select((i,n)=>(i,n)).Single(x=>x.i.Operand is MethodReference m&&m.FullName=="System.Boolean ICharacterController::get_isGrounded()").n;
Check(groundIl[groundContact+2].OpCode==OpCodes.Or&&groundIl[groundContact+3].Operand is MethodReference store&&store.Name=="set_IsGrounded"&&
    Calls(groundUpdate).Any(m=>m.Name=="GroundCollisionRaycast"),"NATIVE_CONTEXT_GROUND_IS_CONTACT_OR_GROUND_QUERY");
Check(Method("SimpleCharacterController","get_isGrounded").Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name=="_isGrounded"),
    "RAW_CONTROLLER_GROUND_IS_SEPARATE_CONTACT_FIELD");
var bindGround=live.Single(m=>m.Name=="BindLocomotionGround");
Check(bindGround.Body.Instructions.Any(i=>i.OpCode==OpCodes.Ldstr&&(string)i.Operand=="IsGrounded")&&
    !bindGround.Body.Instructions.Any(i=>i.OpCode==OpCodes.Ldstr&&(string)i.Operand=="isGrounded"),"R2_BINDS_NATIVE_CONTEXT_GROUND_NOT_RAW_CONTACT");
var plugin=candidate.Types.Single(t=>t.Name=="Plugin");
var pluginAttribute=plugin.CustomAttributes.Single(a=>a.AttributeType.Name=="BepInPlugin");
Check((string)pluginAttribute.ConstructorArguments[2].Value=="2.0.0"&&candidate.Assembly.Name.Version==new Version(2,0,0,0),"PLUGIN_AND_ASSEMBLY_VERSION_2_0_0");
var awake=plugin.Methods.Single(m=>m.Name=="Awake").Body.Instructions;
int IntConstant(Instruction i)=>i.OpCode.Code switch {Code.Ldc_I4_0=>0,Code.Ldc_I4_1=>1,Code.Ldc_I4_2=>2,Code.Ldc_I4_3=>3,Code.Ldc_I4_4=>4,Code.Ldc_I4_5=>5,Code.Ldc_I4_6=>6,Code.Ldc_I4_7=>7,Code.Ldc_I4_8=>8,Code.Ldc_I4=>Convert.ToInt32(i.Operand),Code.Ldc_I4_S=>Convert.ToInt32(i.Operand),_=>-1};
int[] hpDefaults={1,5,4,2,2,3,3};
var partCalls=awake.Select((i,n)=>(i,n)).Where(x=>x.i.Operand is MethodReference m&&m.DeclaringType.Name=="Plugin"&&m.Name=="BindPart").ToArray();
Check(partCalls.Length==7&&partCalls.Select(x=>IntConstant(awake[x.n-2])).SequenceEqual(hpDefaults),"COMPILED_F12_BODY_PART_DEFAULTS");
int jumpToggle=awake.Select((i,n)=>(i,n)).Single(x=>x.i.OpCode==OpCodes.Ldstr&&(string)x.i.Operand=="01 Scale jump distance with movement speed").n;
int jumpStrength=awake.Select((i,n)=>(i,n)).Single(x=>x.i.OpCode==OpCodes.Ldstr&&(string)x.i.Operand=="02 Jump distance scaling strength (%)").n;
Check(IntConstant(awake[jumpToggle+1])==1&&awake[jumpStrength+1].OpCode==OpCodes.Ldc_R4&&(float)awake[jumpStrength+1].Operand==100f,"COMPILED_F12_JUMP_DEFAULT_ENABLED_100_PERCENT");
var files=new[]{eft,client,Path.Combine(game,"BepInEx","core","0Harmony.dll"),Path.Combine(game,"BepInEx","core","Mono.Cecil.dll")};
var result=new{status="STATIC_ABI_AND_CANDIDATE_AUDIT_PASS",checks=passed,eftMvid=native.Mvid,files=files.Select(p=>new{path=p,sha256=Hash(p)}),runtimeController="UNCONFIRMED_UNTIL_MOVEMENT_ARMED",unityPhysics="NOT_EXECUTED",gameplay="NOT_EXECUTED"};
File.WriteAllText(Path.Combine(output,"GAME_ABI_RESULT.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
var selected=new[]{direct,ccMove,Method("SimpleCharacterController","CheckSpeedLimit"),Method("SimpleCharacterController","SingleMove"),Method("EFT.MovementContext","OnSpeedLimiterSpeedChanged"),Method("EFT.ClientPlayerMovementContext","ApplyMotion"),groundUpdate};
File.WriteAllLines(Path.Combine(output,"MOVEMENT_CALL_GRAPH.tsv"),new[]{"Caller\tOffset\tOpcode\tCallee"}.Concat(selected.SelectMany(m=>m.Body.Instructions.Where(i=>i.Operand is MethodReference).Select(i=>$"{m.FullName}\t{i.Offset:X4}\t{i.OpCode}\t{i.Operand}"))));
Console.WriteLine("GAME_ABI_AUDIT_PASS|CHECKS="+passed+"|RUNTIME_AND_GAMEPLAY_UNCONFIRMED");
}
catch(Exception ex)
{
    Console.Error.WriteLine("GAME_ABI_AUDIT_FAIL|"+ex);
    Environment.ExitCode=1;
}


