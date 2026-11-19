using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using BepInEx;
using UnityEngine;

namespace GigaPMC.Client
{
    // Only an opt-in, read-only IMGUI panel. It does not patch EFT methods,
    // write game/profile values or turn on any gameplay hook. F12 changes
    // update its percentage preview in the menu without restarting the game.
    internal static class AttributeDevVision
    {
        private static readonly BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
        private static readonly BindingFlags InstanceAny = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        // Compact 10pt rows: close vertical spacing without text overlap.
        private static readonly int RowHeight = 14;
        private static string[] lines = Array.Empty<string>();
        private static bool dirty = true;
        private static string previousP25Status;
        private static GUIStyle font;
        private static GUIContent[] cachedContents=Array.Empty<GUIContent>();
        private static float measuredWidth=-1f;
        private static int measuredScreenWidth=-1;
        static AttributeDevVision()
        {
            NativeEvidence.MeasuredChanged += Invalidate;
            MovementEndpointEvidence.Changed += Invalidate;
            MenuCarryMath.MeasuredChanged += Invalidate;
            MenuStaminaEvidence.Changed += Invalidate;
        }
        private static GUIStyle shadow;
        // P21: same-panel source continuity; never authorizes gameplay hooks.
        private static readonly AttributeReadOnlySession p21Session = new AttributeReadOnlySession();

        internal static void Invalidate() { dirty = true; }
        internal static void Clear()
        {
            lines = Array.Empty<string>();
            cachedContents=Array.Empty<GUIContent>();
            measuredWidth=-1f;
            dirty = true;
            p21Session.Reset();
            previousP25Status = null;
        }

        internal static void Tick(Plugin plugin)
        {
            if(plugin==null || !plugin.P19DevVisionEnabled)
            {
                if(lines.Length!=0) Clear();
                return;
            }
            if(!dirty) return;
            dirty=false;
            try
            {
                // P35: never substitute F12 percentages, level-based formulas,
                // IL projections, or cached previous-raid samples for actual
                // output of a native gameplay hook.
                // Read one cached, event-driven profile/F12 snapshot. This is
                // display-only; no Unity scan or new native calculation.
                var native = plugin.P33InRaid ? VerifiedHudValues.MeasuredRaidResults()
                                               : VerifiedHudValues.NoMeasuredMenuResults();
                decimal[] configured;
                if(plugin.P42TryHudPercents(out configured))
                    native=VerifiedHudValues.WithConfiguredRates(native,configured,plugin.P33InRaid);
                SetLines(native);
            }
            catch(Exception)
            {
                SetLines(VerifiedHudValues.NoMeasuredMenuResults());
            }
        }

        private static void SetLines(string[] proposed)
        {
            if(proposed==null) return;
            if(lines.Length==proposed.Length &&
                System.Linq.Enumerable.SequenceEqual(lines,proposed)) return;
            lines=proposed;
            cachedContents=new GUIContent[lines.Length];
            for(int i=0;i<lines.Length;i++) cachedContents[i]=new GUIContent(lines[i]);
            measuredWidth=-1f;
        }

        // Never scan or mutate game/profile files. Require the uniquely
        // recognized GameWorld.MainPlayer, a local PMC, and two pinned physical
        // fields. Failure leaves -- rather than substituting a guessed value.
        private static AttributeNativeReadOnly TryReadRuntimeNativeFields(
            out object localPlayer, out object localProfile,
            out AttributePersistedSkills trained)
        {
            localPlayer = null;
            localProfile = null;
            trained = null;
            try
            {
                Assembly eft = null;
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    if (assembly.GetName().Name == "Assembly-CSharp")
                    { eft = assembly; break; }
                Type gameWorld = eft == null ? null : eft.GetType("EFT.GameWorld",false);
                if (gameWorld == null || !typeof(UnityEngine.Object).IsAssignableFrom(gameWorld)) return null;
                UnityEngine.Object world = UnityEngine.Object.FindObjectOfType(gameWorld);
                if (world == null) return null;
                object player = Read(world,"MainPlayer");
                if (player == null || player.GetType().Assembly != eft) return null;
                bool isEftPlayer = false;
                for (Type t=player.GetType(); t != null; t=t.BaseType)
                    if (t.FullName == "EFT.Player") { isEftPlayer=true; break; }
                if (!isEftPlayer) return null;
                object profile = Read(player,"Profile");
                if (profile == null || profile.GetType().FullName != "EFT.Profile") return null;
                object info = Read(profile,"Info");
                string side = (Read(profile,"Side") ?? Read(info,"Side"))?.ToString();
                if (!string.Equals(side,"Bear",StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(side,"Usec",StringComparison.OrdinalIgnoreCase)) return null;
                object physical = Read(player,"Physical");
                AttributeNativeReadOnly data;
                string reason;
                if (!AttributeNativeReadOnly.TryReadPinned(physical,true,out data,out reason))
                    return null;
                localPlayer = player;
                localProfile = profile;
                string skillFailure;
                AttributePersistedSkills.TryRead(profile,out trained,out skillFailure);
                return data;
            }
            catch (Exception) { return null; }
        }

        // Read-only inspection of the existing, SHA-pinned Health-menu observer.
        // Avoid touching any Health/HP source file or registering duplicate Harmony patches.
        // One shared successful cap; failed early reads can recover after menu readiness.

        internal static bool TryReadMenuPmc(out int level, out int prestige, out int cap,
            out object menuProfile)
        {
            level=prestige=cap=0;
            menuProfile = null;
            FieldInfo field=typeof(MenuHealthSync).GetField("observedMenu",PrivateStatic);
            WeakReference reference=field == null ? null : field.GetValue(null) as WeakReference;
            object menu=reference == null ? null : reference.Target;
            if (menu == null || menu.GetType().FullName != "EFT.MainMenuShowOperation") return false;
            object inRaid=Read(menu,"IsInSession");
            if (!(inRaid is bool) || (bool)inRaid) return false;
            object profile=Read(menu,"profile_0");
            if (profile == null || profile.GetType().FullName != "EFT.Profile") return false;
            object info=Read(profile,"Info");
            if (info == null) return false;
            string side=(Read(profile,"Side") ?? Read(info,"Side"))?.ToString();
            if (!string.Equals(side,"Bear",StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(side,"Usec",StringComparison.OrdinalIgnoreCase)) return false;
            if (!Whole(Read(info,"Level"),1,10000,out level)) return false;
            object prestigeRaw=Read(profile,"PrestigeLevel") ?? Read(info,"PrestigeLevel");
            if (prestigeRaw != null && !Whole(prestigeRaw,0,10000,out prestige)) return false;
            // Match the existing server-authoritative cap rule. No fallback to 79
            // when automatic detection is enabled and server cap is unavailable.
            int? detected=ServerLevelCapCache.Read();
            try { cap = Plugin.P19CurrentLevelCap(detected); }
            catch (ArgumentException) { if (detected.HasValue) return false; cap=0; }
            catch (InvalidOperationException) { if (detected.HasValue) return false; cap=0; }
            menuProfile = profile;
            return true;
        }

        private static object Read(object obj,string name)
        {
            if (obj == null) return null;
            return AttributeMemberReader.Read(obj,name);
        }
        private static bool Whole(object raw,int min,int max,out int value)
        {
            value=0;
            if (raw == null) return false;
            double n=Convert.ToDouble(raw,CultureInfo.InvariantCulture);
            if (double.IsNaN(n) || double.IsInfinity(n) || n<min || n>max || n!=Math.Truncate(n)) return false;
            value=checked((int)n);
            return true;
        }

        internal static void Draw()
        {
            if (Event.current == null || Event.current.type != EventType.Repaint || lines.Length == 0) return;
            if (font == null)
            {
                font=new GUIStyle(GUI.skin.label) { fontSize=AttributeDevVisionText.BodyFontSize,
                    alignment=TextAnchor.MiddleLeft, wordWrap=false, richText=false };
                font.normal.textColor=Color.white;
                shadow=new GUIStyle(font);
                shadow.normal.textColor=Color.black;
                measuredWidth=-1f;
            }
            float height=lines.Length*RowHeight;
            float top=Math.Max(0f,(Screen.height-height)/2f);
            // Fit the actual text. No 200-620px fake two-column layout:
            // the numeric value and label remain adjacent in one GUI.Label.
            if(measuredWidth<0f || measuredScreenWidth!=Screen.width)
            {
                float textWidth=1f;
                for(int i=0;i<cachedContents.Length;i++)
                {
                    float measured=font.CalcSize(cachedContents[i]).x;
                    if(float.IsNaN(measured) || float.IsInfinity(measured) || measured<0f) return;
                    textWidth=Mathf.Max(textWidth,measured);
                }
                measuredWidth=textWidth;
                measuredScreenWidth=Screen.width;
            }
            float width=Mathf.Min(Mathf.Max(1f,Screen.width-28f),measuredWidth+6f);
            for(int i=0;i<lines.Length;i++)
            {
                Rect rect=new Rect(14f,top+i*RowHeight,width,RowHeight);
                GUI.Label(new Rect(rect.x+1f,rect.y+1f,rect.width,rect.height),cachedContents[i],shadow);
                GUI.Label(rect,cachedContents[i],font);
            }
        }
    }
}
