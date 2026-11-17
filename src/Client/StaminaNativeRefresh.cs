using System;
using System.Globalization;
using System.Reflection;

namespace GigaPMC.Client
{
    // P46: event-bound local-PMC native stamina-capacity recalculation.
    // Evidence in GIGAPMC_P03_FOCUSED_IL: Physical.OnEnduranceLevelUp
    // explicitly calls SetDirty on Stamina.TotalCapacity. P26 formerly
    // patched BaseStaminaFunction after TotalCapacity had often been cached;
    // no call meant no bonus despite a successful Harmony registration.
    // Exactly one native dirty operation and one capacity read per raid start.
    // No Unity scan, per-frame sampling, skill writes, profile mutation,
    // global settings write, or second bonus multiplier.
    internal static class StaminaNativeRefresh
    {
        private const BindingFlags Flags = BindingFlags.Instance |
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        internal sealed class Lease
        {
            internal object Physical;
            internal object Stamina;
            internal object Total;
            internal FieldInfo Current;
            internal MethodInfo NativeRecalc;
            internal float BeforeCapacity;
            internal float BeforeCurrent;
            internal float AfterCapacity;
            internal bool Applied;
        }

        private static FieldInfo UniqueField(Type type,string name,Type fieldType)
        {
            var field = type.GetField(name,Flags);
            if(field == null || field.IsStatic ||
               (fieldType == typeof(float) && field.IsInitOnly) ||
               (fieldType != null && field.FieldType != fieldType))
                throw new MissingFieldException("P46_FIELD_ABI_DRIFT_" + name);
            return field;
        }

        private static float FinitePositive(object raw,string label)
        {
            if(raw == null || raw.GetType() != typeof(float))
                throw new MissingMemberException("P46_NATIVE_FLOAT_MISSING_" + label);
            float value = (float)raw;
            if(float.IsNaN(value) || float.IsInfinity(value) ||
               value <= 0f || value >= 8000f)
                throw new InvalidOperationException("P46_NATIVE_FLOAT_RANGE_" + label);
            return value;
        }

        private static float ReadCapacity(Lease lease)
        {
            // The native field is a cached generic Value wrapper, NOT a
            // Stamina.get_TotalCapacity():float property. The latter was the
            // failed P41 Hideout assumption. Read the wrapper's actual Value.
            object v = AttributeMemberReader.Read(lease.Total,"Value");
            return FinitePositive(v,"TOTAL_CAPACITY_VALUE");
        }

        internal static bool TryCapture(object physical,Type physicalType,
            out Lease lease,out string reason)
        {
            lease=null;reason="UNKNOWN";
            try
            {
                if(physical == null || physicalType == null ||
                   !physicalType.IsInstanceOfType(physical) ||
                   physicalType.Assembly.GetName().Name != "Assembly-CSharp")
                    throw new TypeLoadException("P46_PHYSICAL_OWNER_DRIFT");
                var stamina = AttributeMemberReader.Read(physical,"Stamina");
                if(stamina == null || stamina.GetType().FullName != "Stamina" ||
                   stamina.GetType().Assembly != physicalType.Assembly)
                    throw new TypeLoadException("P46_STAMINA_TYPE_DRIFT");
                FieldInfo current=UniqueField(stamina.GetType(),"Current",typeof(float));
                FieldInfo total=UniqueField(stamina.GetType(),"TotalCapacity",null);
                object wrapper=total.GetValue(stamina);
                if(wrapper == null || wrapper.GetType().IsValueType)
                    throw new MissingMemberException("P46_TOTAL_CAPACITY_WRAPPER_DRIFT");
                MethodInfo recalc=UniqueMethodContract.Resolve(physicalType,
                    "OnEnduranceLevelUp",typeof(void));
                var candidate=new Lease {
                    Physical=physical, Stamina=stamina,Total=wrapper,
                    Current=current, NativeRecalc=recalc
                };
                candidate.BeforeCapacity=ReadCapacity(candidate);
                object rawCurrent=current.GetValue(stamina);
                if(rawCurrent == null || rawCurrent.GetType()!=typeof(float))
                    throw new MissingMemberException("P46_CURRENT_FIELD_TYPE_DRIFT");
                candidate.BeforeCurrent=(float)rawCurrent;
                float unused;
                if(!StaminaCapacityMath.TryPreserveFraction(
                    candidate.BeforeCurrent,candidate.BeforeCapacity,
                    candidate.BeforeCapacity,out unused))
                    throw new InvalidOperationException("P46_NATIVE_FILL_INVALID");
                lease=candidate;reason="NONE";return true;
            }
            catch(Exception ex)
            {
                reason=ex.GetType().Name+"_"+ex.Message;
                return false;
            }
        }

        internal static bool TryActivate(Lease lease,float ratio,out string reason)
        {
            reason="UNKNOWN";
            if(lease==null) {reason="P46_LEASE_NULL";return false;}
            try
            {
                // MUST be called after P26 freezes delta and the Harmony
                // postfix is installed. This is the game-native dirty pathway.
                lease.NativeRecalc.Invoke(lease.Physical,null);
                float adjusted=ReadCapacity(lease);
                if(!StaminaCapacityMath.IsExpectedNativeMultiplier(
                    lease.BeforeCapacity,adjusted,ratio))
                    throw new InvalidOperationException("P46_CAPACITY_RATIO_NOT_APPLIED");
                float newCurrent;
                if(!StaminaCapacityMath.TryPreserveFraction(
                    lease.BeforeCurrent,lease.BeforeCapacity,adjusted,out newCurrent))
                    throw new InvalidOperationException("P46_CURRENT_CONSERVATION_REJECTED");
                lease.Current.SetValue(lease.Stamina,newCurrent);
                lease.AfterCapacity=adjusted;
                lease.Applied=true;
                reason="NONE";
                return true;
            }
            catch(Exception ex)
            {
                reason=ex.GetType().Name+"_"+ex.Message;
                return false;
            }
        }

        internal static bool TryRestoreNative(Lease lease,out string reason)
        {
            reason="NONE";
            if(lease==null) return true;
            try
            {
                float was=ReadCapacity(lease);
                float current=(float)lease.Current.GetValue(lease.Stamina);
                lease.NativeRecalc.Invoke(lease.Physical,null);
                float native=ReadCapacity(lease);
                if(lease.Applied)
                {
                    float restored;
                    if(!StaminaCapacityMath.TryPreserveFraction(
                        current,was,native,out restored))
                        throw new InvalidOperationException("P46_RESTORE_FILL_REJECTED");
                    lease.Current.SetValue(lease.Stamina,restored);
                }
                lease.Applied=false;
                return true;
            }
            catch(Exception ex)
            {
                reason=ex.GetType().Name+"_"+ex.Message;
                return false;
            }
        }

        internal static string Detail(Lease lease)
        {
            if(lease==null || !lease.Applied) return "UNAVAILABLE";
            float current=(float)lease.Current.GetValue(lease.Stamina);
            return "NATIVE_CAP="+lease.BeforeCapacity.ToString("R",CultureInfo.InvariantCulture)+
                "|RESULT_CAP="+lease.AfterCapacity.ToString("R",CultureInfo.InvariantCulture)+
                "|NATIVE_CURRENT="+lease.BeforeCurrent.ToString("R",CultureInfo.InvariantCulture)+
                "|RESULT_CURRENT="+current.ToString("R",CultureInfo.InvariantCulture);
        }
    }
}
