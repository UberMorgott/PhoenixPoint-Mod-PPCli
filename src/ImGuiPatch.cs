using System;
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
                    harmony.UnpatchAll(Id);
                    harmony = null;
                    return null;
                }
                if (harmony != null) return null;
                MethodInfo target = AccessTools.Method(typeof(GUI), "DoControl",
                    new[] { typeof(Rect), typeof(int), typeof(bool), typeof(bool), typeof(GUIContent), typeof(GUIStyle) });
                if (target == null) return "UnityEngine.GUI.DoControl(Rect,int,bool,bool,GUIContent,GUIStyle) was not found - Unity changed under the tap";
                Harmony h = new Harmony(Id);
                h.Patch(target, prefix: new HarmonyMethod(typeof(ImGuiPatch), nameof(Pre)), postfix: new HarmonyMethod(typeof(ImGuiPatch), nameof(Post)));
                harmony = h;
                return null;
            }
            catch (Exception ex)
            {
                harmony = null;
                return ex.GetType().Name + ": " + ex.Message;
            }
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
