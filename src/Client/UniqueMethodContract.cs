using System;
using System.Linq;
using System.Reflection;

namespace GigaPMC.Client
{
    // Runtime identity is the loaded assembly's structural method shape,
    // not a metadata token captured from an unpatched DLL on disk.
    internal static class UniqueMethodContract
    {
        internal static MethodInfo Resolve(Type type,string name,Type result,params Type[] args)
        {
            if(type==null || string.IsNullOrWhiteSpace(name) || result==null)
                throw new MissingMethodException("P38_INVALID_METHOD_REQUEST");
            var all=type.GetMethods(BindingFlags.DeclaredOnly|BindingFlags.Instance|
                BindingFlags.Public|BindingFlags.NonPublic)
                .Where(m=>m.Name==name && !m.IsStatic &&
                    m.ReturnType==result && m.DeclaringType==type &&
                    m.GetParameters().Select(p=>p.ParameterType).SequenceEqual(args) &&
                    !m.IsGenericMethod && !m.IsAbstract).ToArray();
            if(all.Length!=1 || all[0].GetMethodBody()==null)
                throw new MissingMethodException("P38_RUNTIME_UNIQUE_SIGNATURE_REQUIRED|"+
                    type.FullName+"."+name+"|MATCHES="+all.Length);
            return all[0];
        }
    }
}
