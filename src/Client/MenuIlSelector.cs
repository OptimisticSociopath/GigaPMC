using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace GigaPMC.Client
{
    // P39: runtime prepatchers can rename/renumber the private CG_Show method.
    // Never patch the first similar-looking method. Require the two *independent*
    // original EFT lower and upper limit calculations in a single void() method.
    // The existing Harmony transpiler is a SECOND and stronger IL shape gate.
    internal static class MenuIlSelector
    {
        private const BindingFlags Any=BindingFlags.Instance|BindingFlags.Public|
            BindingFlags.NonPublic|BindingFlags.DeclaredOnly;

        private static int Matches(byte[] il,int methodToken,byte[] suffix,out int first)
        {
            first=-1;
            if(il==null || il.Length==0) return 0;
            var token=BitConverter.GetBytes(methodToken);
            int count=0;
            for(int i=0;i<=il.Length-(1+4+suffix.Length);i++)
            {
                // call/callvirt followed by exact pinned scalar IL calculation
                if(il[i]!=0x6f && il[i]!=0x28) continue;
                bool match=true;
                for(int j=0;j<4;j++) if(il[i+1+j]!=token[j]) match=false;
                for(int j=0;j<suffix.Length;j++)
                    if(il[i+5+j]!=suffix[j]) match=false;
                if(!match) continue;
                if(count==0) first=i;
                count++;
            }
            return count;
        }
        internal static bool ContainsTwoNativeLimitAnchors(byte[] il,int lowerToken,int upperToken)
        {
            if(lowerToken<=0 || upperToken<=0 || lowerToken==upperToken) return false;
            int lower,upper;
            int lowCount=Matches(il,lowerToken,new byte[]{0x07,0x5A,0x08,0x58,0x0D},out lower);
            int highCount=Matches(il,upperToken,new byte[]{0x07,0x5A,0x08,0x58,0x13,0x04},out upper);
            return lowCount==1 && highCount==1 && upper>lower;
        }
        internal static MethodInfo ResolveFromNativeIl(Type panelType)
        {
            if(panelType==null || panelType.Assembly.GetName().Name!="Assembly-CSharp")
                throw new TypeLoadException("P39_EFT_PANEL_ASSEMBLY_REQUIRED");
            var native=panelType.Assembly.GetType("EFT.GlobalConfiguration+StaminaParameters",false);
            if(native==null) throw new TypeLoadException("P39_STAMINA_PARAMETERS_NOT_FOUND");
            var lower=native.GetMethods(Any).Where(m=>m.Name=="get_LowerOverweightLimit" &&
                m.GetParameters().Length==0 && m.ReturnType==typeof(float)).ToArray();
            var upper=native.GetMethods(Any).Where(m=>m.Name=="get_UpperOverweightLimit" &&
                m.GetParameters().Length==0 && m.ReturnType==typeof(float)).ToArray();
            if(lower.Length!=1 || upper.Length!=1)
                throw new MissingMethodException("P39_NATIVE_LIMIT_GETTERS_NOT_UNIQUE");
            var found=new List<MethodInfo>();
            foreach(var method in panelType.GetMethods(Any))
            {
                if(method.IsStatic || method.IsAbstract || method.IsGenericMethod ||
                    method.DeclaringType!=panelType || method.ReturnType!=typeof(void) ||
                    method.GetParameters().Length!=0) continue;
                byte[] il;
                try { il=method.GetMethodBody()?.GetILAsByteArray(); }
                catch { continue; }
                if(ContainsTwoNativeLimitAnchors(il,lower[0].MetadataToken,upper[0].MetadataToken))
                    found.Add(method);
            }
            if(found.Count!=1)
                throw new MissingMethodException("P39_MENU_WEIGHT_IL_UNIQUE_REQUIRED|MATCHES="+found.Count);
            return found[0];
        }
    }
}
