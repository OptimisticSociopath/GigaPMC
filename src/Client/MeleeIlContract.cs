using System;
using System.Reflection;
using System.Reflection.Emit;

namespace GigaPMC.Client
{
    // Loaded SPT 4.1.6 ProcessHit converts SkillManager.FloatBuff via
    // op_Implicit(float), NOT a get_ method. Validate the actual buff type
    // rather than accepting any arbitrary float-returning call.
    internal static class MeleeIlContract
    {
        internal static bool IsNativeStrengthTail(FieldInfo buff,MethodInfo converter,
            OpCode add,OpCode mul)
        {
            if(buff==null || converter==null || buff.IsStatic ||
                buff.Name!="StrengthBuffMeleePowerInc" ||
                add!=OpCodes.Add || mul!=OpCodes.Mul ||
                converter.Name!="op_Implicit" || !converter.IsStatic ||
                converter.ReturnType!=typeof(float)) return false;
            var p=converter.GetParameters();
            return p.Length==1 && p[0].ParameterType==buff.FieldType &&
                converter.DeclaringType==buff.FieldType;
        }
    }
}
