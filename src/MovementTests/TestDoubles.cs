// Test doubles do not model Unity physics, Harmony code generation, or EFT.
// They let the SAME production wrappers/transpilers execute deterministically.
using System.Reflection;
using System.Reflection.Emit;
namespace UnityEngine
{
    public struct Vector3
    {
        public float x,y,z;
        public Vector3(float x,float y,float z) {this.x=x;this.y=y;this.z=z;}
        public float sqrMagnitude=>x*x+y*y+z*z;
        public static Vector3 operator -(Vector3 a,Vector3 b)=>new(a.x-b.x,a.y-b.y,a.z-b.z);
    }
    [Flags] public enum CollisionFlags { None=0,Sides=1,Above=2,Below=4 }
    public class Transform { public Vector3 position; }
    public static class Time {public static int frameCount;}
    public enum KeyCode {F8,LeftControl,RightControl}
}
namespace BepInEx.Logging
{
    public class ManualLogSource
    {
        public List<string> Messages=new();
        public void LogInfo(object s)=>Messages.Add(s.ToString());
        public void LogWarning(object s)=>Messages.Add(s.ToString());
    }
}
namespace BepInEx.Bootstrap
{
    public static class Chainloader {public static Dictionary<string,object> PluginInfos=new();}
}
namespace BepInEx
{
    public class UnityInput
    {
        public static UnityInput Current=new();
        public bool GetKeyDown(UnityEngine.KeyCode k)=>false;
        public bool GetKey(UnityEngine.KeyCode k)=>false;
    }
}
namespace HarmonyLib
{
    public class CodeInstruction
    {
        public OpCode opcode;public object operand;
        public List<object> labels=new(); public List<object> blocks=new();
        public CodeInstruction(OpCode code,object operand=null){opcode=code;this.operand=operand;}
        public bool Calls(MethodInfo m)=>(opcode==OpCodes.Call||opcode==OpCodes.Callvirt)&&Equals(operand,m);
    }
    public class HarmonyMethod {public HarmonyMethod(Type t,string n){} }
    public class Patch {public string owner;public MethodInfo PatchMethod;public int priority;}
    public class Patches
    {
        public Patch[] Prefixes=Array.Empty<Patch>(),Postfixes=Array.Empty<Patch>(),Transpilers=Array.Empty<Patch>(),Finalizers=Array.Empty<Patch>();
    }
    public class Harmony
    {
        public Harmony(string id){}
        public void Patch(MethodInfo m,HarmonyMethod transpiler=null){}
        public void UnpatchSelf(){}
        public static Patches GetPatchInfo(MethodBase method)=>null;
    }
    public class PatchProcessor {}
    public static class AccessTools
    {
        public static FieldInfo Field(Type t,string n)=>t.GetField(n,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static);
        public static MethodInfo Method(Type t,string n)=>t.GetMethod(n,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static);
    }
}
