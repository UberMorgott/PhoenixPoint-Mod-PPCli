using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Morgott.PPBridge
{
    /// <summary>
    /// The game half of <c>imgui</c>: a postfix on the private <c>GUI.DoControl</c> - the one method
    /// both GUI.Button/GUILayout.Button and GUI.Toggle/GUILayout.Toggle end in (see ImGui.cs for the
    /// verified call route). Installed only while an imgui request runs, removed when it ends, like
    /// ShotPatch. Never throws into the caller's OnGUI.
    /// </summary>
    internal static class ImGuiPatch
    {
        private const string Id = "com.morgott.PPBridge.imgui";
        private static Harmony harmony;
        private static readonly Func<string> OwnerFn = Owner;

        internal static string Arm(bool on)
        {
            try
            {
                if (!on)
                {
                    if (harmony == null) return null;
                    harmony.UnpatchAll(Id);
                    harmony = null;
                    return null;
                }
                if (harmony != null) return null;
                MethodInfo target = AccessTools.Method(typeof(GUI), "DoControl",
                    new[] { typeof(Rect), typeof(int), typeof(bool), typeof(bool), typeof(GUIContent), typeof(GUIStyle) });
                if (target == null) return "UnityEngine.GUI.DoControl(Rect,int,bool,bool,GUIContent,GUIStyle) was not found - Unity changed under the tap";
                Harmony h = new Harmony(Id);
                h.Patch(target, postfix: new HarmonyMethod(typeof(ImGuiPatch), nameof(Post)));
                harmony = h;
                return null;
            }
            catch (Exception ex)
            {
                harmony = null;
                return ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static void Post(Rect position, bool on, GUIContent content, GUIStyle style, ref bool __result)
        {
            if (!ImGuiTap.Active) return;
            try
            {
                Event e = Event.current;
                if (e == null) return;
                EventType t = e.type;
                if (t == EventType.Layout) return;
                bool toggle = style != null && style.name != null && style.name.IndexOf("toggle", StringComparison.OrdinalIgnoreCase) >= 0;
                if (ImGuiTap.Observe(Time.frameCount, t == EventType.Repaint, t.ToString(), Label(content),
                                     position.x, position.y, position.width, position.height,
                                     GUI.enabled, toggle, on, OwnerFn, GUIUtility.hotControl == 0))
                {
                    __result = !on;
                    GUI.changed = true;
                }
            }
            catch (Exception) { }
        }

        private static string Label(GUIContent c)
        {
            if (c == null) return "";
            if (!string.IsNullOrEmpty(c.text)) return c.text;
            if (!string.IsNullOrEmpty(c.tooltip)) return c.tooltip;
            return c.image != null ? "#img:" + c.image.name : "";
        }

        /// <summary>First stack frame outside Unity, Harmony and this bridge = the OnGUI that drew the
        /// control. Nested/compiler types are walked up to the outermost declaring type; FULL name (namespace kept) so a press binds to exactly that type.</summary>
        private static string Owner()
        {
            // COST CAP (Codex review P2): a stack walk per control per Repaint is up to 1000 walks a
            // frame. Unity selects one GUILayout cache per OnGUI MonoBehaviour (and per GUI.Window)
            // before calling it (GUILayoutUtility.Begin/SelectIDList -> current.topLevel), so that
            // reference identifies "the same OnGUI call": walk once per (topLevel, frame, event),
            // reuse for its other controls. Hard ceiling MaxWalks per frame, past it owner = null.
            // Caveat: a MonoBehaviour with useGUILayout=false never calls Begin, so its controls may
            // inherit the previous behaviour's cached owner.
            int frame = Time.frameCount;
            Event e = Event.current;
            EventType ev = e == null ? EventType.Ignore : e.type;
            object key = null;
            try { key = TopLevel == null ? null : TopLevel(); } catch (Exception) { }
            if (key != null && ReferenceEquals(key, cacheKey) && frame == cacheFrame && ev == cacheEv) return cacheOwner;
            if (frame != walkFrame) { walkFrame = frame; walks = 0; }
            if (++walks > MaxWalks) return null;
            string owner = Walk();
            cacheKey = key; cacheFrame = frame; cacheEv = ev; cacheOwner = owner;
            return owner;
        }

        internal const int MaxWalks = 64;
        private static object cacheKey;
        private static int cacheFrame = -1, walkFrame = -1, walks;
        private static EventType cacheEv;
        private static string cacheOwner;
        private static readonly Func<object> TopLevel = MakeTopLevel();

        private static Func<object> MakeTopLevel()
        {
            try
            {
                MethodInfo g = AccessTools.PropertyGetter(typeof(GUILayoutUtility), "topLevel");
                return g == null ? null : (Func<object>)Delegate.CreateDelegate(typeof(Func<object>), g);
            }
            catch (Exception) { return null; }
        }

        private static string Walk()
        {
            StackFrame[] frames = new StackTrace(2, false).GetFrames();
            if (frames == null) return null;
            foreach (StackFrame f in frames)
            {
                MethodBase m = f.GetMethod();
                Type ty = m == null ? null : m.DeclaringType;
                if (ty == null) continue;
                while (ty.DeclaringType != null) ty = ty.DeclaringType;
                string ns = ty.Namespace ?? "";
                if (ns.StartsWith("UnityEngine", StringComparison.Ordinal) || ns.StartsWith("HarmonyLib", StringComparison.Ordinal)
                    || ns == "Morgott.PPBridge" || ns.StartsWith("System", StringComparison.Ordinal)) continue;
                return ty.FullName;
            }
            return null;
        }
    }
}
