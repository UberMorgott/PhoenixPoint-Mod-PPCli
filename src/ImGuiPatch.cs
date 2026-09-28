using System;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
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
                    Application.logMessageReceived -= OnLog;
                    ImGuiTap.PassEnded();
                    harmony.UnpatchAll(Id);
                    harmony = null;
                    return null;
                }
                if (harmony != null) return null;
                MethodInfo target = AccessTools.Method(typeof(GUI), "DoControl",
                    new[] { typeof(Rect), typeof(int), typeof(bool), typeof(bool), typeof(GUIContent), typeof(GUIStyle) });
                if (target == null) return "UnityEngine.GUI.DoControl(Rect,int,bool,bool,GUIContent,GUIStyle) was not found - Unity changed under the tap";
                string lerr = Layout();
                if (lerr != null) return lerr;
                Harmony h = new Harmony(Id);
                try
                {
                    h.Patch(target, prefix: new HarmonyMethod(typeof(ImGuiPatch), nameof(Pre)), postfix: new HarmonyMethod(typeof(ImGuiPatch), nameof(Post)));
                    // The layout-tolerant rest of a forced pass (ImGui.cs InPass): pad GetNext/PeekNext
                    // overruns, swap a mismatched BeginLayoutGroup entry; EndGUI ends the pass.
                    h.Patch(mGetNext, prefix: new HarmonyMethod(typeof(ImGuiPatch), nameof(PreNext)));
                    h.Patch(mPeekNext, prefix: new HarmonyMethod(typeof(ImGuiPatch), nameof(PreNext)));
                    h.Patch(mBeginGroup, prefix: new HarmonyMethod(typeof(ImGuiPatch), nameof(PreBeginGroup)));
                    h.Patch(mEndGUI, prefix: new HarmonyMethod(typeof(ImGuiPatch), nameof(PassOver)));
                    h.Patch(mEndGUIEx, prefix: new HarmonyMethod(typeof(ImGuiPatch), nameof(PassOver)));
                }
                catch (Exception) { try { h.UnpatchAll(Id); } catch (Exception) { } throw; }
                harmony = h;
                Application.logMessageReceived += OnLog;
                return null;
            }
            catch (Exception ex)
            {
                harmony = null;
                return ex.GetType().Name + ": " + ex.Message;
            }
        }

        // ------------------------------------------------------------------ layout-tolerant pass
        // GUILayoutGroup / GUILayoutEntry are internal: reached by reflection, touched only while
        // ImGuiTap.GuardArmed (one volatile read otherwise). Verified on the game's IMGUIModule.dll
        // (Unity 2019.4.31f1, ilspycmd): GUILayoutGroup.GetNext throws on overrun only when
        // Event.current.type == Repaint and returns its static `none` otherwise; PeekNext always throws;
        // GUILayoutUtility.BeginLayoutGroup throws ExitGUIException("Mismatched LayoutGroup") when the
        // next entry is not a group; EndGUI / EndGUIFromException close every OnGUI call (native).

        private static MethodInfo mGetNext, mPeekNext, mBeginGroup, mEndGUI, mEndGUIEx, mCreateGroup;
        private static FieldInfo fEntries, fCursor, fNone;
        private static Type tGroup;

        private static string Layout()
        {
            if (mGetNext != null) return null;
            Type g = AccessTools.TypeByName("UnityEngine.GUILayoutGroup");
            if (g == null) return "UnityEngine.GUILayoutGroup not found - Unity changed under the tap";
            MethodInfo next = AccessTools.Method(g, "GetNext", Type.EmptyTypes);
            MethodInfo peek = AccessTools.Method(g, "PeekNext", Type.EmptyTypes);
            FieldInfo entries = AccessTools.Field(g, "entries"), cursor = AccessTools.Field(g, "m_Cursor"), none = AccessTools.Field(g, "none");
            MethodInfo begin = AccessTools.Method(typeof(GUILayoutUtility), "BeginLayoutGroup", new[] { typeof(GUIStyle), typeof(GUILayoutOption[]), typeof(Type) });
            MethodInfo create = AccessTools.Method(typeof(GUILayoutUtility), "CreateGUILayoutGroupInstanceOfType", new[] { typeof(Type) });
            MethodInfo end = AccessTools.Method(typeof(GUIUtility), "EndGUI", new[] { typeof(int) });
            MethodInfo endEx = AccessTools.Method(typeof(GUIUtility), "EndGUIFromException", new[] { typeof(Exception) });
            if (next == null || peek == null || entries == null || cursor == null || none == null || begin == null || create == null || end == null || endEx == null)
                return "GUILayout internals (GetNext/PeekNext/entries/m_Cursor/none/BeginLayoutGroup/EndGUI) not found - Unity changed under the tap";
            tGroup = g; fEntries = entries; fCursor = cursor; fNone = none;
            mPeekNext = peek; mBeginGroup = begin; mCreateGroup = create; mEndGUI = end; mEndGUIEx = endEx;
            mGetNext = next;
            return null;
        }

        private static bool InForcedPass()
        {
            if (!ImGuiTap.GuardArmed) return false;
            Event e = Event.current;
            return e != null && ImGuiTap.InPass(Time.frameCount, EvName(e.rawType));
        }

        /// <summary>GetNext/PeekNext past the group's end in the forced pass: append a dummy entry
        /// first, so the original returns it instead of throwing (what non-Repaint passes return).</summary>
        private static void PreNext(object __instance)
        {
            try
            {
                if (!InForcedPass()) return;
                IList entries = (IList)fEntries.GetValue(__instance);
                if ((int)fCursor.GetValue(__instance) < entries.Count) return;
                entries.Add(fNone.GetValue(null));
                ImGuiTap.Repaired();
            }
            catch (Exception) { }
        }

        /// <summary>BeginLayoutGroup in the forced pass whose next entry is missing or not a group:
        /// put an empty group of the asked type there, so the original takes it instead of throwing.</summary>
        private static void PreBeginGroup(Type layoutType)
        {
            try
            {
                if (!InForcedPass()) return;
                EventType t = Event.current.type;
                if (t == EventType.Layout || t == EventType.Used) return;
                object top = TopLevel == null ? null : TopLevel();
                if (top == null) return;
                IList entries = (IList)fEntries.GetValue(top);
                int cur = (int)fCursor.GetValue(top);
                if (cur < entries.Count && tGroup.IsInstanceOfType(entries[cur])) return;
                object fresh = mCreateGroup.Invoke(null, new object[] { layoutType });
                if (cur < entries.Count) entries[cur] = fresh; else entries.Add(fresh);
                ImGuiTap.Repaired();
            }
            catch (Exception) { }
        }

        private static void PassOver() { if (ImGuiTap.GuardArmed) ImGuiTap.PassEnded(); }

        private static void OnLog(string msg, string stack, LogType type)
        {
            try
            {
                bool bad = type == LogType.Error || type == LogType.Exception || type == LogType.Assert
                           || (msg != null && msg.IndexOf("Exception", StringComparison.Ordinal) >= 0);
                ImGuiTap.Logged(bad, msg);
            }
            catch (Exception) { }
        }

        /// <summary>DoControl Use()s the MouseDown/MouseUp it takes, so after it runs Event.current.type
        /// reads Used: the event type at ENTRY is captured here for the postfix.</summary>
        private static void Pre(out EventType __state)
        {
            __state = EventType.Ignore;
            if (!ImGuiTap.Active) return;
            try { Event e = Event.current; if (e != null) __state = e.type; } catch (Exception) { }
        }

        private static void Post(Rect position, int id, bool on, GUIContent content, GUIStyle style, ref bool __result, EventType __state)
        {
            if (!ImGuiTap.Active) return;
            try
            {
                Event e = Event.current;
                if (e == null) return;
                EventType t = __state;
                if (t == EventType.Layout || t == EventType.Ignore) return;
                bool repaint = t == EventType.Repaint;
                bool toggle = style != null && style.name != null && style.name.IndexOf("toggle", StringComparison.OrdinalIgnoreCase) >= 0;
                float sx = float.NaN, sy = float.NaN;
                if (repaint && ImGuiTap.Recording)
                {
                    // GUI space -> GUI-screen space: unclips the group/scroll/window stack (and the
                    // clip's matrix); Y down from the top of the game view, no flip.
                    Vector2 sp = GUIUtility.GUIToScreenPoint(position.center);
                    sx = sp.x; sy = sp.y;
                }
                Vector2 mp = e.mousePosition;
                int hotId = GUIUtility.hotControl;
                if (ImGuiTap.Observe(Time.frameCount, repaint, EvName(t), Label(content),
                                     position.x, position.y, position.width, position.height,
                                     GUI.enabled, toggle, on, OwnerFn, hotId == 0,
                                     sx, sy, mp.x, mp.y, hotId == id, __result, hotId))
                {
                    __result = !on;
                    GUI.changed = true;
                }
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------ post mode: Win32
        // PostMessage only: the message goes into THIS process's own window queue. No SendInput, no
        // SetCursorPos, no SetForegroundWindow - the user's cursor and focus are never touched.

        private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        private static IntPtr gameHwnd;

        private static bool Ours(IntPtr h)
        {
            if (h == IntPtr.Zero || !IsWindow(h)) return false;
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            return pid == (uint)Process.GetCurrentProcess().Id;
        }

        /// <summary>This process's visible top-level window, class UnityWndClass preferred.</summary>
        private static IntPtr FindGameWindow()
        {
            if (Ours(gameHwnd) && IsWindowVisible(gameHwnd)) return gameHwnd;
            uint me = (uint)Process.GetCurrentProcess().Id;
            IntPtr unity = IntPtr.Zero, any = IntPtr.Zero;
            StringBuilder sb = new StringBuilder(64);
            EnumProc cb = (h, l) =>
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid != me || !IsWindowVisible(h)) return true;
                sb.Length = 0;
                GetClassNameW(h, sb, sb.Capacity);
                if (sb.ToString() == "UnityWndClass") { unity = h; return false; }
                if (any == IntPtr.Zero) any = h;
                return true;
            };
            EnumWindows(cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            gameHwnd = unity != IntPtr.Zero ? unity : any;
            return gameHwnd;
        }

        internal static ImGuiTap.WinInfo Window()
        {
            ImGuiTap.WinInfo w = new ImGuiTap.WinInfo { ScreenW = Screen.width, ScreenH = Screen.height };
            IntPtr h = FindGameWindow();
            if (h == IntPtr.Zero) { w.Error = "no visible top-level window belongs to pid " + Process.GetCurrentProcess().Id; return w; }
            RECT r;
            if (!GetClientRect(h, out r)) { w.Error = "GetClientRect failed"; return w; }
            w.Hwnd = h.ToInt64();
            w.ClientW = r.Right - r.Left;
            w.ClientH = r.Bottom - r.Top;
            return w;
        }

        internal static string PostMessage(long hwnd, int msg, int wParam, int lParam)
        {
            IntPtr h = new IntPtr(hwnd);
            if (!Ours(h)) return "hwnd 0x" + hwnd.ToString("X") + " is not a window of this process";
            if (!PostMessageW(h, (uint)msg, new IntPtr(wParam), new IntPtr(lParam))) return "PostMessageW error " + Marshal.GetLastWin32Error();
            return null;
        }

        /// <summary>Canonical event name. NOT t.ToString(): EventType carries obsolete lowercase
        /// aliases with the same values (repaint = Repaint, mouseMove = MouseMove), and on the game's
        /// Mono ToString() returns the ALIAS ("repaint") - live-verified 2026-09-28, where it made
        /// SafePass never match and every press end code:"notfired".</summary>
        internal static string EvName(EventType t)
        {
            switch (t)
            {
                case EventType.Repaint: return "Repaint";
                case EventType.MouseMove: return "MouseMove";
                case EventType.MouseDown: return "MouseDown";
                case EventType.MouseUp: return "MouseUp";
                case EventType.MouseDrag: return "MouseDrag";
                case EventType.KeyDown: return "KeyDown";
                case EventType.KeyUp: return "KeyUp";
                case EventType.ScrollWheel: return "ScrollWheel";
                case EventType.Used: return "Used";
                default: return t.ToString();
            }
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
