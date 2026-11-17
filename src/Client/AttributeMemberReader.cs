using System;
using System.Reflection;

namespace GigaPMC.Client
{
    // P21: bounded read-only reflection through inherited EFT instance members.
    // The former P20 helper missed nonpublic members declared on base classes.
    // Never performs a SetValue, invokes a method with arguments, or patches EFT.
    internal static class AttributeMemberReader
    {
        private const BindingFlags Flags = BindingFlags.Instance |
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        internal static object Read(object instance, string memberName)
        {
            if (instance == null || string.IsNullOrEmpty(memberName)) return null;
            int depth = 0;
            for (Type t = instance.GetType(); t != null && depth++ < 32; t = t.BaseType)
            {
                PropertyInfo p = t.GetProperty(memberName, Flags);
                if (p != null && p.GetIndexParameters().Length == 0 &&
                    p.GetGetMethod(true) != null)
                    return p.GetValue(instance, null);
                FieldInfo f = t.GetField(memberName, Flags);
                if (f != null && !f.IsStatic) return f.GetValue(instance);
            }
            return null;
        }
    }
}
