using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace GigaPMC.Client
{
    // BepInEx ConfigurationManager custom percent slider; 0.25 percentage
    // point granularity for all FIVE F12 tracks (3 + 6 prestige percent fields
    // per track). Supports non-integer input, +/- quarter point buttons and
    // preserves existing legacy values until the user makes an edit.
    // This is an OnGUI callback, NEVER a background scan/Update task.
    internal static class PercentStepDrawer
    {
        private sealed class EditBuffer { internal string Text; internal bool Editing; }
        private static readonly Dictionary<string,EditBuffer> buffers=
            new Dictionary<string,EditBuffer>(StringComparer.Ordinal);

        internal static void Draw(ConfigEntryBase entry)
        {
            if(entry==null || !(entry.BoxedValue is float current)) return;
            var range=entry.Description?.AcceptableValues as AcceptableValueRange<float>;
            float max=range==null ? 100f : range.MaxValue;
            string key=entry.Definition.Section+"\u001f"+entry.Definition.Key;
            EditBuffer state;
            if(!buffers.TryGetValue(key,out state))
            { state=new EditBuffer(); buffers[key]=state; }
            if(!state.Editing) state.Text=PercentStepMath.Label(current);
            string control="GigaPMC_QTR_"+key;

            GUILayout.BeginHorizontal();
            try
            {
                // Exactly 0..100%, quarter-point grid; the total calculated
                // bonus is not subject to the slider's individual setting cap.
                float windowLow=PercentStepMath.SliderLower(current,max);
                float windowHigh=PercentStepMath.SliderUpper(current,max);
                float raw=GUILayout.HorizontalSlider(current,windowLow,windowHigh,
                    GUILayout.MinWidth(70f),GUILayout.ExpandWidth(true));
                if(!Mathf.Approximately(raw,current))
                {
                    float snapped=PercentStepMath.Snap(raw,max);
                    if(snapped!=current) entry.BoxedValue=snapped;
                    current=(float)entry.BoxedValue;
                    state.Text=PercentStepMath.Label(current);
                    state.Editing=false;
                }

                GUI.SetNextControlName(control);
                string next=GUILayout.TextField(state.Text,GUILayout.Width(61f));
                if(!string.Equals(next,state.Text,StringComparison.Ordinal))
                { state.Text=next; state.Editing=true; }

                // +/- allow precise quarter steps even with a narrow GUI.
                int adjustment=0;
                if(GUILayout.Button("-",GUILayout.Width(23f))) adjustment=-1;
                if(GUILayout.Button("+",GUILayout.Width(23f))) adjustment=1;
                if(adjustment!=0)
                {
                    float parsed;
                    if(state.Editing && PercentStepMath.TryParse(state.Text,max,out parsed))
                        current=parsed;
                    current=PercentStepMath.Move(current,adjustment,max);
                    entry.BoxedValue=current;
                    state.Text=PercentStepMath.Label(current);
                    state.Editing=false;
                    GUI.FocusControl(null);
                }
                else if(state.Editing)
                {
                    var ev=Event.current;
                    bool returnPressed=ev!=null && ev.type==EventType.KeyDown &&
                        (ev.keyCode==KeyCode.Return || ev.keyCode==KeyCode.KeypadEnter);
                    bool escapePressed=ev!=null && ev.type==EventType.KeyDown &&
                        ev.keyCode==KeyCode.Escape;
                    bool focusLeft=GUI.GetNameOfFocusedControl()!=control;
                    if(escapePressed)
                    {
                        state.Text=PercentStepMath.Label((float)entry.BoxedValue);
                        state.Editing=false;
                        GUI.FocusControl(null);
                    }
                    else if(returnPressed || focusLeft)
                    {
                        float committed;
                        if(PercentStepMath.TryParse(state.Text,max,out committed) &&
                           committed!=(float)entry.BoxedValue)
                            entry.BoxedValue=committed;
                        state.Text=PercentStepMath.Label((float)entry.BoxedValue);
                        state.Editing=false;
                        if(returnPressed) GUI.FocusControl(null);
                    }
                }
            }
            finally { GUILayout.EndHorizontal(); }
        }
    }
}
