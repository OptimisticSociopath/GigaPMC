using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace GigaPMC.Client
{
    // P09 EXPERIMENTAL ONLY: first two native hooks (weight and body stamina).
    // Not a release-ready implementation. Explicit F12 opt-in required.
    // There is NO runtime observer, logging poller, process injector or profile writer.
    // This extension never changes the known-working HP classes or server DLL.
    internal static class AttributeRuntime
    {
        private const string EftSha = "EE25CEE1259777B38ED8B3E7841FDC2DB3C98540B1469FA539B1FF183476E436";
        private const BindingFlags InstanceAny = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly object Gate = new object();
        private static Plugin owner;
        private static ManualLogSource log;
        private static Harmony harmony;
        private static State state;
        private static bool warned;
        private static bool installAttempted;

        private sealed class State
        {
            internal object Player;
            internal Dictionary<AttributeBonusKind, decimal> Rates;
            internal AttributePreRaidAuthority Authority;
        }

        internal static void TryInstall(Plugin source, ManualLogSource logger)
        {
            if (installAttempted) return;
            installAttempted = true;
            owner = source;
            log = logger;
            if (!source.P09ExperimentalIntegrationEnabled)
            {
                logger.LogInfo("[GigaPMC] P09_RUNTIME_DISABLED|EXPERIMENTAL_OPT_IN_REQUIRED|HP_UNTOUCHED");
                return;
            }
            // P10 hard gate: a value sampled from an ordinary native postfix
            // can already contain a stimulant, injury, or another mod's output.
            // Until skill-only provenance is independently verified, do not
            // register either experimental Harmony hook, even when F12 opt-in
            // is checked. The known-working HP hooks are unaffected.
            if (!AttributePreRaidAuthority.CertifiedSkillReaderPresent)
            {
                logger.LogWarning("[GigaPMC] P10_BLOCKED_UNVERIFIED_PRE_RAID_BASELINE|NO_HARMONY_PATCH|HP_UNTOUCHED");
                return;
            }
            try
            {
                string eftPath = Path.Combine(Paths.GameRootPath,"EscapeFromTarkov_Data","Managed","Assembly-CSharp.dll");
                if (!File.Exists(eftPath)) throw new InvalidDataException("EFT client assembly missing");
                string actual;
                using (var stream = File.OpenRead(eftPath))
                using (var sha = SHA256.Create())
                    actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                if (!string.Equals(actual,EftSha,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("EFT DLL SHA-256 differs from audited SPT 4.1.6");

                Type player = TypeFromEft("EFT.Player");
                Type physical = TypeFromEft("Physical");
                Type physicalBase = TypeFromEft("PhysicalBase");
                if (player.Assembly != physical.Assembly || player.Assembly != physicalBase.Assembly)
                    throw new InvalidDataException("EFT types must originate in one SHA-pinned client assembly");
                var localFlag = player.GetMethods(InstanceAny)
                    .Where(m => m.Name == "Init" && !m.IsGenericMethod &&
                        m.GetParameters().Count(p => p.ParameterType.FullName == "EFT.Profile") == 1 &&
                        m.GetParameters().Any(IsExplicitLocalFlag)).ToArray();
                if (localFlag.Length != 1) throw new MissingMethodException("Unique EFT.Player.Init(local PMC) not found");

                MethodInfo stamina = ExactMethod(physical,"BaseStaminaFunction",0x060034C8,typeof(float),typeof(float));
                MethodInfo weight = ExactMethod(physicalBase,"UpdateWeightLimits",0x0600352B,typeof(void));
                VerifyWeightFields(physicalBase);
                harmony = new Harmony("com.vargor.gigapmc.attributes.p09.experimental");
                harmony.Patch(localFlag[0], prefix: new HarmonyMethod(typeof(AttributeRuntime),nameof(OnLocalPlayerInit)));
                harmony.Patch(stamina, postfix: new HarmonyMethod(typeof(AttributeRuntime),nameof(OnStaminaCalculated)));
                harmony.Patch(weight, postfix: new HarmonyMethod(typeof(AttributeRuntime),nameof(OnWeightCalculated)));
                logger.LogWarning("[GigaPMC] P09_EXPERIMENTAL_ARMED|CARRY_WEIGHT|BODY_STAMINA|NOT_RUNTIME_VERIFIED|OTHER_7_NOT_APPLIED");
            }
            catch (Exception ex)
            {
                try { harmony?.UnpatchSelf(); } catch { }
                harmony = null;
                logger.LogError("[GigaPMC] P09_FAIL_CLOSED_NO_HOOKS|" + ex.GetType().Name + "|" + ex.Message);
            }
        }

        private static Type TypeFromEft(string fullName)
        {
            Type[] t = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(fullName,false))
                .Where(x => x != null).ToArray();
            if (t.Length != 1) throw new TypeLoadException("Unique EFT type not found: " + fullName);
            return t[0];
        }

        private static MethodInfo ExactMethod(Type type,string name,int token,Type returnType,params Type[] parameters)
        {
            MethodInfo[] found = type.GetMethods(InstanceAny)
                .Where(m => m.MetadataToken == token && m.Name == name && m.ReturnType == returnType &&
                    m.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters)).ToArray();
            if (found.Length != 1) throw new MissingMethodException("Pinned EFT hook mismatch: " + name);
            return found[0];
        }

        private static void VerifyWeightFields(Type t)
        {
            foreach (string name in new[]{"WalkOverweightLimits","BaseOverweightLimits","SprintOverweightLimits","WalkSpeedOverweightLimits"})
            {
                var f = t.GetField(name,InstanceAny);
                if (f == null || f.FieldType != typeof(Vector2) || f.IsInitOnly || f.IsStatic)
                    throw new MissingFieldException("Pinned weight vector missing: " + name);
            }
        }

        private static bool IsExplicitLocalFlag(ParameterInfo p) =>
            p.ParameterType == typeof(bool) && p.Name != null &&
            (p.Name.Equals("isYourPlayer",StringComparison.OrdinalIgnoreCase) ||
             p.Name.Equals("isLocalPlayer",StringComparison.OrdinalIgnoreCase) ||
             p.Name.Equals("isYourProfile",StringComparison.OrdinalIgnoreCase));

        private static object Member(object obj, string name)
        {
            if (obj == null) return null;
            Type t = obj.GetType();
            PropertyInfo prop = t.GetProperty(name,InstanceAny);
            if (prop != null && prop.GetIndexParameters().Length == 0) return prop.GetValue(obj,null);
            return t.GetField(name,InstanceAny)?.GetValue(obj);
        }

        private static int Whole(object raw)
        {
            double v = Convert.ToDouble(raw,CultureInfo.InvariantCulture);
            if (double.IsNaN(v) || double.IsInfinity(v) || v != Math.Truncate(v) || v < 0 || v > 10000)
                throw new InvalidDataException("Invalid player level or prestige");
            return (int)v;
        }

        private static int? ServerCap()
        {
            string path = Path.Combine(Paths.GameRootPath,"SPT_Runtime","user","mods","GigaPMC","config","server-level-cap.txt");
            int value;
            return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(),NumberStyles.Integer,
                CultureInfo.InvariantCulture,out value) ? (int?)value : null;
        }

        private static void OnLocalPlayerInit(object __instance, object[] __args, MethodBase __originalMethod)
        {
            try
            {
                ParameterInfo[] ps = __originalMethod.GetParameters();
                int local = Array.FindIndex(ps,IsExplicitLocalFlag);
                if (local < 0 || local >= __args.Length || !(__args[local] is bool) || !(bool)__args[local]) return;
                int pi = Array.FindIndex(ps,p=>p.ParameterType.FullName=="EFT.Profile");
                if (pi < 0 || pi>=__args.Length || __args[pi] == null) return;
                object info = Member(__args[pi],"Info");
                string side = Member(info,"Side")?.ToString();
                if (!string.Equals(side,"Bear",StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(side,"Usec",StringComparison.OrdinalIgnoreCase)) return;
                int level = Whole(Member(info,"Level"));
                object p = Member(info,"PrestigeLevel");
                int prestige = p == null ? 0 : Whole(p);
                if (level < 1) return;
                int cap = owner.CurrentRaidSettings().ResolveCap(ServerCap());
                var rates = new Dictionary<AttributeBonusKind,decimal>();
                foreach (AttributeBonusKind kind in AttributeBonusMath.OrderedKinds)
                    rates[kind] = owner.P09BonusRates(kind).TotalPercent(owner.P09GlobalEnabled,level,prestige,cap);
                var authority = new AttributePreRaidAuthority();
                authority.Begin(__instance);
                lock (Gate) state = new State {Player=__instance,Rates=rates,Authority=authority};
                log?.LogWarning("[GigaPMC] P09_PRE_RAID_PERCENT_SNAPSHOT|L" + level + "|P" + prestige + "|CAP" + cap +
                    "|CARRY=" + rates[AttributeBonusKind.CarryWeight] + "|STAMINA=" + rates[AttributeBonusKind.BodyStamina] +
                    "|OTHER_7_NOT_APPLIED");
            }
            catch (Exception ex) { WarnOnce("P09_RAID_SNAPSHOT_SKIPPED",ex); lock (Gate) state = null; }
        }

        // P10: no auto-freeze from the first native output, ever. Calculations
        // cannot change EFT until an independent certified skills-only reader
        // supplies per-metric baselines to State.Authority (not yet provided).
        private static bool MatchingPhysical(object actual)
        {
            if (state == null || state.Player == null || actual == null) return false;
            object physical = Member(state.Player,"Physical");
            return physical != null && ReferenceEquals(physical,actual);
        }

        private static void OnStaminaCalculated(object __instance, ref float __result)
        {
            try
            {
                lock (Gate)
                {
                    if (!MatchingPhysical(__instance) || state.Authority == null) return;
                    float corrected;
                    if (state.Authority.TryApply(state.Player,BaselineMetric.BodyStamina,__result,out corrected))
                        __result = corrected;
                }
            }
            catch (Exception ex) { WarnOnce("P10_STAMINA_HOOK_SKIPPED",ex); }
        }

        private static void OnWeightCalculated(object __instance)
        {
            try
            {
                lock (Gate)
                {
                    if (!MatchingPhysical(__instance) || state.Authority == null) return;
                    string[] names = {"WalkOverweightLimits","BaseOverweightLimits","SprintOverweightLimits","WalkSpeedOverweightLimits"};
                    var fields = names.Select(n=>FindInstanceField(__instance.GetType(),n)).ToArray();
                    if (fields.Any(f=>f==null || f.FieldType!=typeof(Vector2))) return;
                    Vector2[] native = fields.Select(f=>(Vector2)f.GetValue(__instance)).ToArray();
                    Vector2[] changed = new Vector2[fields.Length];
                    // Transactional preflight: all EIGHT component deltas must
                    // originate from certified, same-raid skill-only baselines.
                    for (int i=0;i<fields.Length;i++)
                    {
                        float lower,upper;
                        var lo = (BaselineMetric)(1+i*2);
                        var hi = (BaselineMetric)(2+i*2);
                        if (!state.Authority.TryApply(state.Player,lo,native[i].x,out lower) ||
                            !state.Authority.TryApply(state.Player,hi,native[i].y,out upper)) return;
                        changed[i]=new Vector2(lower,upper);
                    }
                    int written=0;
                    try
                    {
                        for (;written<fields.Length;written++) fields[written].SetValue(__instance,changed[written]);
                    }
                    catch
                    {
                        for (int i=written;i>=0;i--)
                            try { fields[i].SetValue(__instance,native[i]); } catch { }
                        throw;
                    }
                }
            }
            catch (Exception ex) { WarnOnce("P10_WEIGHT_HOOK_SKIPPED",ex); }
        }

        private static FieldInfo FindInstanceField(Type type,string name)
        {
            for (Type t=type;t!=null;t=t.BaseType)
            {
                FieldInfo f=t.GetField(name,InstanceAny|BindingFlags.DeclaredOnly);
                if (f!=null) return f;
            }
            return null;
        }

        private static void WarnOnce(string code,Exception ex)
        {
            if (warned) return;
            warned = true;
            log?.LogError("[GigaPMC] " + code + "|" + ex.GetType().Name + "|" + ex.Message);
        }
    }
}
