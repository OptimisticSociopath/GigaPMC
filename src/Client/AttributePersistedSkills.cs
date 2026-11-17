using System;
using System.Collections;
using System.Reflection;

namespace GigaPMC.Client
{
    // P15: immutable, profile-descriptor skill PROGRESS snapshot. The source is
    // EFT.Profile.SkillsInfo.Common, not live SummaryLevel or FloatBuff.Value.
    // Static IL evidence: P14 PROFILE_SKILL_BOUNDARY and BASESKILL_ORIGIN.
    // This class NEVER writes to EFT and is NOT connected to gameplay in P15.
    // Reading skill progress is NOT certification of a permanent game-stat
    // baseline. P10's runtime gate remains false.
    internal sealed class AttributePersistedSkills
    {
        internal float StrengthProgress { get; }
        internal float EnduranceProgress { get; }
        internal int StrengthLevel { get; }
        internal int EnduranceLevel { get; }

        private AttributePersistedSkills(float strength, float endurance)
        {
            StrengthProgress = strength;
            EnduranceProgress = endurance;
            // Exact BaseSkill.get_Level IL: Clamp(Current, 0, 5100), /100f,
            // conv.i4. The progress came from SkillInfoDescriptor.Progress.
            StrengthLevel = (int)(Math.Min(5100f, Math.Max(0f, strength)) / 100f);
            EnduranceLevel = (int)(Math.Min(5100f, Math.Max(0f, endurance)) / 100f);
        }

        private const BindingFlags Instance = BindingFlags.Instance |
            BindingFlags.Public | BindingFlags.NonPublic;

        // Snapshot only; P15 does not call this from gameplay hooks.
        // Rejects ambiguous, malformed, or unexpectedly shaped descriptors.
        internal static bool TryRead(object profile,
            out AttributePersistedSkills snapshot, out string failure)
        {
            snapshot = null;
            failure = "UNKNOWN";
            try
            {
                if (profile == null || profile.GetType().FullName != "EFT.Profile")
                    return Reject("PROFILE_TYPE", out failure);
                PropertyInfo skillsGetter = profile.GetType().GetProperty("SkillsInfo", Instance);
                if (skillsGetter == null || skillsGetter.GetIndexParameters().Length != 0 ||
                    skillsGetter.GetMethod == null)
                    return Reject("PROFILE_SKILLS_GETTER", out failure);
                object skills = skillsGetter.GetValue(profile, null);
                if (skills == null || skills.GetType().FullName != "EFT.SkillsDescriptor")
                    return Reject("SKILLS_DESCRIPTOR_TYPE", out failure);
                FieldInfo common = skills.GetType().GetField("Common", Instance);
                if (common == null || common.IsStatic || !(common.GetValue(skills) is IEnumerable items))
                    return Reject("COMMON_FIELD", out failure);
                bool sawStrength = false, sawEndurance = false;
                float strength = 0f, endurance = 0f;
                foreach (object item in items)
                {
                    if (item == null ||
                        item.GetType().FullName != "EFT.SkillsDescriptor+SkillInfoDescriptor")
                        return Reject("SKILL_DESCRIPTOR_ENTRY_TYPE", out failure);
                    Type entryType = item.GetType();
                    FieldInfo idField = entryType.GetField("Id", Instance);
                    FieldInfo progressField = entryType.GetField("Progress", Instance);
                    if (idField == null || progressField == null ||
                        idField.IsStatic || progressField.IsStatic ||
                        progressField.FieldType != typeof(float) || !idField.FieldType.IsEnum)
                        return Reject("SKILL_DESCRIPTOR_FIELD_ABI", out failure);
                    object rawId = idField.GetValue(item);
                    if (rawId == null) return Reject("SKILL_ID_NULL", out failure);
                    string name = rawId.ToString();
                    if (name != "Strength" && name != "Endurance") continue;
                    float progress = (float)progressField.GetValue(item);
                    // Unlike BaseSkill.ClampCurrent, reject out-of-bounds input:
                    // fail-closed rather than hide an unexpected profile value.
                    if (float.IsNaN(progress) || float.IsInfinity(progress) ||
                        progress < 0f || progress > 5100f)
                        return Reject("PROGRESS_INVALID", out failure);
                    if (name == "Strength")
                    {
                        if (sawStrength) return Reject("DUPLICATE_STRENGTH", out failure);
                        sawStrength = true;
                        strength = progress;
                    }
                    else
                    {
                        if (sawEndurance) return Reject("DUPLICATE_ENDURANCE", out failure);
                        sawEndurance = true;
                        endurance = progress;
                    }
                }
                if (!sawStrength || !sawEndurance)
                    return Reject("REQUIRED_SKILL_MISSING", out failure);
                snapshot = new AttributePersistedSkills(strength, endurance);
                failure = "NONE";
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException ||
                ex is AmbiguousMatchException || ex is InvalidOperationException ||
                ex is TargetInvocationException || ex is TargetException ||
                ex is InvalidCastException || ex is MemberAccessException)
            {
                return Reject("REFLECTION_FAILURE_" + ex.GetType().Name, out failure);
            }
        }

        private static bool Reject(string code, out string failure)
        {
            failure = code;
            return false;
        }
    }
}
