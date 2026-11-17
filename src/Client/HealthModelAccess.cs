using System;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace GigaPMC.Client
{
    // Profile Health DTOs and active HealthValue controllers have DIFFERENT ABIs.
    // This strictly checks a writable contract at runtime; it never force-sets
    // readonly properties or writes raw memory. Supported writes are reversible.
    public sealed class HealthModelAccess
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly object health;
        private readonly PropertyInfo max;
        private readonly PropertyInfo current;
        private readonly PropertyInfo minimum;
        // EFT.Profile.HealthInfo.ValueInfo uses public numeric fields, not the
        // HealthValue controller properties. Do not confuse those two ABIs.
        private readonly FieldInfo profileCurrentField;
        private readonly FieldInfo profileMaximumField;
        private readonly MethodInfo init;
        private readonly FieldInfo maxField;
        private readonly FieldInfo valueField;
        private readonly FieldInfo valueMaxField;
        private readonly FieldInfo valueCurrentField;
        private readonly FieldInfo valueMinField;
        private readonly int mode;

        public string Strategy { get; }
        public double Current => mode == 5
            ? Numeric(profileCurrentField.GetValue(health), "CurrentField")
            : Numeric(current.GetValue(health), "Current");
        public double Maximum => mode == 5
            ? Numeric(profileMaximumField.GetValue(health), "MaximumField")
            : Numeric(max.GetValue(health), "Maximum");

        public HealthModelAccess(object target)
        {
            health = target ?? throw new ArgumentNullException(nameof(target));
            Type type = target.GetType();
            max = type.GetProperty("Maximum", Flags);
            current = type.GetProperty("Current", Flags);
            minimum = type.GetProperty("Minimum", Flags);
            // The EFT.Profile+HealthInfo+ValueInfo DTO is NOT EFT.HealthSystem.HealthValue.
            // It typically declares Current/Maximum as public float FIELDS. The old
            // code rejected it at GetProperty() and never reached the coroutine.
            // Verify the exact field names, both numeric types, both mutable, class
            // reference semantics and valid numeric values before accepting writes.
            if (max == null && current == null)
            {
                profileCurrentField = type.GetField("Current", Flags);
                profileMaximumField = type.GetField("Maximum", Flags);
                if (!type.IsValueType && profileCurrentField != null && profileMaximumField != null &&
                    IsWritableNumericField(profileCurrentField) &&
                    IsWritableNumericField(profileMaximumField))
                {
                    double rawCurrent = Numeric(profileCurrentField.GetValue(health), "CurrentField");
                    double rawMaximum = Numeric(profileMaximumField.GetValue(health), "MaximumField");
                    if (rawMaximum <= 0 || rawMaximum > 200000 ||
                        rawCurrent < 0 || rawCurrent > rawMaximum)
                        throw new InvalidOperationException("Health profile field values out of range: " + TypeLabel(type));
                    mode = 5;
                    Strategy = "ProfileValueInfoFields";
                    return;
                }
                throw new MissingMemberException("Health model missing writable numeric properties/fields: " +
                    TypeLabel(type) + "|CurrentField=" + FieldShape(profileCurrentField) +
                    "|MaximumField=" + FieldShape(profileMaximumField) +
                    "|IsValueType=" + type.IsValueType);
            }
            if (max == null || current == null || max.GetIndexParameters().Length != 0 ||
                current.GetIndexParameters().Length != 0 || !IsNumeric(max.PropertyType) ||
                !IsNumeric(current.PropertyType))
                throw new MissingMemberException("Health model missing numeric Current/Maximum properties: " + TypeLabel(type));

            // Preflight values before choosing a writer.
            double mx = Maximum;
            double cur = Current;
            if (mx <= 0 || mx > 200000 || cur < 0 || cur > mx)
                throw new InvalidOperationException("Health model values out of range: " + TypeLabel(type));

            // Legacy/server-style DTO: both properties have setters.
            if (max.CanWrite && current.CanWrite)
            {
                mode = 1;
                Strategy = "PropertySetters";
                return;
            }

            // EFT.HealthSystem.HealthValue: Maximum is read-only, but native
            // Init(cur,min,max) is provided by the exact user audited ABI.
            init = type.GetMethods(Flags).SingleOrDefault(m => m.Name == "Init" &&
                m.ReturnType == typeof(void) && m.GetParameters().Length == 3 &&
                m.GetParameters().All(p => IsNumeric(p.ParameterType)));
            if (init != null && minimum != null && IsNumeric(minimum.PropertyType))
            {
                mode = 2;
                Strategy = "NativeInit";
                return;
            }

            // Immutable Maximum property backed by a mutable instance field.
            // Require direct agreement between that field and the property.
            maxField = new[] { "Maximum", "_maximum", "<Maximum>k__BackingField" }
                .Select(n => type.GetField(n, Flags))
                .FirstOrDefault(f => f != null && !f.IsStatic && !f.IsInitOnly &&
                    !f.IsLiteral && IsNumeric(f.FieldType));
            if (maxField != null && current.CanWrite &&
                Math.Abs(Numeric(maxField.GetValue(health), "MaximumField") - mx) < 0.0001)
            {
                mode = 3;
                Strategy = "MaximumBackingField";
                return;
            }

            // EFT.HealthValue also exposes Value: a struct with independent
            // Current/Maximum/Minimum members. Only choose this strategy if
            // public getters match the selected struct's field values exactly.
            valueField = type.GetField("Value", Flags);
            if (valueField != null && !valueField.IsStatic && !valueField.IsInitOnly &&
                valueField.FieldType.IsValueType)
            {
                Type nested = valueField.FieldType;
                valueMaxField = FindMutableNumericField(nested, "Maximum");
                valueCurrentField = FindMutableNumericField(nested, "Current");
                valueMinField = FindMutableNumericField(nested, "Minimum");
                object boxed = valueField.GetValue(health);
                if (boxed != null && valueMaxField != null && valueCurrentField != null &&
                    Math.Abs(Numeric(valueMaxField.GetValue(boxed), "Value.Maximum") - mx) < 0.0001 &&
                    Math.Abs(Numeric(valueCurrentField.GetValue(boxed), "Value.Current") - cur) < 0.0001)
                {
                    mode = 4;
                    Strategy = "ValueStruct";
                    return;
                }
            }
            throw new MissingMemberException("No verified writable Health DTO strategy: " + TypeLabel(type) +
                "|MaxSetter=" + max.CanWrite + "|CurrentSetter=" + current.CanWrite +
                "|Init=" + (init != null) + "|MaxField=" + (maxField?.Name ?? "none") +
                "|ValueField=" + (valueField?.FieldType.FullName ?? "none"));
        }

        private static bool IsWritableNumericField(FieldInfo f)
            => !f.IsStatic && !f.IsInitOnly && !f.IsLiteral && IsNumeric(f.FieldType);
        private static string FieldShape(FieldInfo f)
            => f == null ? "missing" : f.FieldType.Name + (IsWritableNumericField(f) ? ":writable" : ":readonly_or_unsupported");

        private static FieldInfo FindMutableNumericField(Type t, string name)
            => new[] { name, "_" + char.ToLowerInvariant(name[0]) + name.Substring(1),
                "<" + name + ">k__BackingField" }
                .Select(n => t.GetField(n, Flags))
                .FirstOrDefault(f => f != null && !f.IsStatic && !f.IsInitOnly &&
                    !f.IsLiteral && IsNumeric(f.FieldType));

        private static string TypeLabel(Type t) => t.FullName ?? t.Name;
        private static bool IsNumeric(Type t) => t == typeof(float) || t == typeof(double);
        private static double Numeric(object v, string label)
        {
            if (v == null) throw new InvalidOperationException(label + " is null");
            double n = Convert.ToDouble(v, CultureInfo.InvariantCulture);
            if (double.IsNaN(n) || double.IsInfinity(n))
                throw new InvalidOperationException(label + " is not finite");
            return n;
        }
        private static object ConvertNum(double value, Type type)
        {
            if (!IsNumeric(type) || double.IsNaN(value) || double.IsInfinity(value) ||
                value < -200000 || value > 200000)
                throw new ArgumentOutOfRangeException(nameof(value));
            return Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
        }

        public void Write(double newCurrent, double newMaximum)
        {
            if (newMaximum <= 0 || newMaximum > 200000 ||
                newCurrent < 0 || newCurrent > newMaximum)
                throw new ArgumentOutOfRangeException(nameof(newMaximum));
            switch (mode)
            {
                case 1:
                    // Adjust max first when raising, current first when lowering.
                    if (newMaximum >= Maximum)
                    {
                        max.SetValue(health, ConvertNum(newMaximum, max.PropertyType));
                        current.SetValue(health, ConvertNum(newCurrent, current.PropertyType));
                    }
                    else
                    {
                        current.SetValue(health, ConvertNum(Math.Min(newCurrent, Maximum), current.PropertyType));
                        max.SetValue(health, ConvertNum(newMaximum, max.PropertyType));
                    }
                    break;
                case 2:
                    var p = init.GetParameters();
                    double min = Numeric(minimum.GetValue(health), "Minimum");
                    init.Invoke(health, new[] {
                        ConvertNum(newCurrent, p[0].ParameterType),
                        ConvertNum(min, p[1].ParameterType),
                        ConvertNum(newMaximum, p[2].ParameterType)
                    });
                    break;
                case 3:
                    if (newMaximum >= Maximum)
                    {
                        maxField.SetValue(health, ConvertNum(newMaximum, maxField.FieldType));
                        current.SetValue(health, ConvertNum(newCurrent, current.PropertyType));
                    }
                    else
                    {
                        current.SetValue(health, ConvertNum(Math.Min(newCurrent, Maximum), current.PropertyType));
                        maxField.SetValue(health, ConvertNum(newMaximum, maxField.FieldType));
                    }
                    break;
                case 4:
                    object copy = valueField.GetValue(health);
                    if (copy == null) throw new InvalidOperationException("Health Value field became null");
                    valueMaxField.SetValue(copy, ConvertNum(newMaximum, valueMaxField.FieldType));
                    valueCurrentField.SetValue(copy, ConvertNum(newCurrent, valueCurrentField.FieldType));
                    valueField.SetValue(health, copy);
                    break;
                case 5:
                    // EFT.Profile.HealthInfo.ValueInfo: direct mutable numeric fields.
                    // Keep the intermediate state valid when max shrinks and verify
                    // actual values via Current/Maximum readback after the write.
                    if (newMaximum >= Maximum)
                    {
                        profileMaximumField.SetValue(health, ConvertNum(newMaximum, profileMaximumField.FieldType));
                        profileCurrentField.SetValue(health, ConvertNum(newCurrent, profileCurrentField.FieldType));
                    }
                    else
                    {
                        profileCurrentField.SetValue(health, ConvertNum(Math.Min(newCurrent, Maximum), profileCurrentField.FieldType));
                        profileMaximumField.SetValue(health, ConvertNum(newMaximum, profileMaximumField.FieldType));
                    }
                    break;
                default:
                    throw new InvalidOperationException("Unsupported health writer mode");
            }
            if (Math.Abs(Maximum - newMaximum) > 0.0001 ||
                Math.Abs(Current - newCurrent) > 0.0001)
                throw new InvalidOperationException("Health model write did not commit; mode=" + Strategy);
        }
    }
}
