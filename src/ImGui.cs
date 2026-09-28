using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Morgott.PPBridge
{
    /// <summary>
    /// The pure half of <c>imgui</c>: list and PRESS IMGUI (OnGUI) buttons and toggles in-process -
    /// no SendInput, so the user's focus and cursor are never touched. Names no Unity type; the game
    /// half (ImGuiPatch.cs) installs <see cref="Arm"/> + <see cref="FrameNow"/> and feeds every
    /// control through <see cref="Observe"/>, so the whole matching/arming logic runs offline.
    ///
    /// THE SEAM (verified on the game's own UnityEngine.IMGUIModule.dll, Unity 2019.4.31f1, ilspycmd):
    /// GUILayout.Button(..) -> GUILayout.DoButton -> GUI.Button(Rect,GUIContent,GUIStyle) -> GUI.Button(Rect,int,..)
    /// -> GUI.DoButton -> GUI.DoControl(Rect,int,bool on,bool hover,GUIContent,GUIStyle); GUI.Toggle's DoToggle
    /// lands in the same DoControl. DoControl is the ONLY caller-independent point both reach, and it
    /// is too big for Mono to inline, unlike the 2-call GUI.Button wrappers. Its return is "button
    /// clicked" (on=false -> true) or "toggle's new value" (!on) - so forcing <c>!on</c> is one press.
    ///
    /// Semantics:
    ///   - a control's identity inside a frame = (label, i): i = how many controls with the same label
    ///     came before it in that event pass. Stable frame to frame for the same UI.
    ///   - list/resolve read ONE complete Repaint pass (the frame before the current Update).
    ///   - press fires ONLY on a SAFE pass (<see cref="SafePass"/>): Repaint or MouseMove, and only while
    ///     no control holds the mouse (GUIUtility.hotControl == 0). Never on MouseDown/MouseUp/MouseDrag/
    ///     Key*/Used: forcing a real MouseDown true would run the button body, then the native MouseUp
    ///     on the same control would run it AGAIN and hotControl would stay grabbed. Layout is skipped:
    ///     GUILayout returns the dummy result there and the layout pass must match the next pass.
    ///   - one imgui request at a time; the Harmony patch lives only while a request is running.
    /// Main thread only, like every verb.
    /// </summary>
    internal static class ImGuiTap
    {
        internal const int DefaultPageSize = 25;
        internal const int MaxPageSize = 200;
        internal const int DefaultWaitFrames = 30;
        internal const int MaxWaitFrames = 600;
        /// <summary>Controls recorded per frame; past it the list says <c>more:true</c>.</summary>
        internal const int MaxRows = 1000;
        internal const int LabelClip = 80;
        /// <summary>Frames a list/resolve waits for a Repaint pass before it reports "no controls".</summary>
        internal const int ResolveFrames = 3;

        /// <summary>Game half: true installs the DoControl postfix, false removes it. Null = OK.</summary>
        internal static Func<bool, string> Arm;
        /// <summary>Game half: Time.frameCount.</summary>
        internal static Func<int> FrameNow;

        /// <summary>The postfix's one-branch early out: nothing is listing and nothing is armed.</summary>
        internal static volatile bool Active;

        internal sealed class Ctl
        {
            internal string Label;
            internal int I;
            internal bool Toggle, On, Enabled;
            internal float X, Y, W, H;
            internal string Owner;
        }

        private sealed class Target
        {
            internal string Label;
            internal int I;
            /// <summary>FULL type name of the OnGUI that drew the resolved control, re-checked at fire
            /// time: if the UI reordered and (label, i) now belongs to another owner, it does not fire.</summary>
            internal string Owner;
        }

        private static bool recording;
        private static List<Ctl> cur = new List<Ctl>();
        private static int curFrame = int.MinValue;
        private static bool curMore;
        private static List<Ctl> last = new List<Ctl>();
        private static int lastFrame = int.MinValue;
        private static bool lastMore;
        private static readonly Dictionary<string, int> counts = new Dictionary<string, int>();
        private static int countFrame = int.MinValue;
        private static Target target;
        private static string firedEv;
        private static int firedFrame;
        private static bool fired;
        private static Pending busy;

        /// <summary>
        /// One control reached DoControl on a non-Layout event. Returns true = force this control's
        /// press NOW (the caller sets __result = !on and GUI.changed). <paramref name="owner"/> is
        /// lazy: a stack walk, paid only for recorded Repaint rows.
        /// </summary>
        internal static bool Observe(int frame, bool repaint, string ev, string label,
                                     float x, float y, float w, float h,
                                     bool enabled, bool toggle, bool on, Func<string> owner, bool idle = true)
        {
            if (!Active) return false;
            label = label ?? "";
            if (frame != countFrame) { counts.Clear(); countFrame = frame; }
            string key = ev + "\u0001" + label;
            int n;
            counts.TryGetValue(key, out n);
            counts[key] = n + 1;

            if (recording && repaint)
            {
                if (frame != curFrame)
                {
                    last = cur; lastFrame = curFrame; lastMore = curMore;
                    cur = new List<Ctl>(); curFrame = frame; curMore = false;
                }
                if (cur.Count >= MaxRows) curMore = true;
                else
                {
                    string o = null;
                    try { o = owner == null ? null : owner(); } catch (Exception) { }
                    cur.Add(new Ctl { Label = label, I = n, Toggle = toggle || on, On = on, Enabled = enabled, X = x, Y = y, W = w, H = h, Owner = o });
                }
            }

            Target t = target;
            if (t != null && enabled && SafePass(ev, idle) && n == t.I && string.Equals(label, t.Label, StringComparison.Ordinal)
                && (t.Owner == null || string.Equals(t.Owner, SafeOwner(owner), StringComparison.Ordinal)))
            {
                target = null;
                fired = true; firedEv = ev; firedFrame = frame;
                RefreshActive();
                return true;
            }
            return false;
        }

        /// <summary>The only passes a forced press may ride: nothing native can also click there.
        /// <paramref name="idle"/> = GUIUtility.hotControl == 0 (no control mid-click).</summary>
        internal static bool SafePass(string ev, bool idle)
        {
            return idle && (ev == "Repaint" || ev == "MouseMove");
        }

        private static void RefreshActive() { Active = recording || target != null; }

        /// <summary>The newest Repaint pass that is COMPLETE at frame <paramref name="now"/> (its frame is
        /// already over) and not older than <paramref name="since"/>; null if none yet.</summary>
        internal static List<Ctl> Complete(int now, int since, out int frame, out bool more)
        {
            frame = 0; more = false;
            if (curFrame < now && curFrame >= since) { frame = curFrame; more = curMore; return cur; }
            if (lastFrame < now && lastFrame >= since) { frame = lastFrame; more = lastMore; return last; }
            return null;
        }

        internal static object Dispatch(string verb, JObject a)
        {
            if (verb != "imgui") return null;
            bool list = a != null && a["list"] != null && a["list"].Type == JTokenType.Boolean && (bool)a["list"];
            JObject press = a == null ? null : a["press"] as JObject;
            if (list == (press != null))
                return Bad("args", "imgui takes {list:true, owner?, match?, page?, pageSize?} OR {press:{label, owner?, index?}, waitFrames?}");
            int page = 0, size = DefaultPageSize, wait = DefaultWaitFrames, index = -1;
            string err = Protocol.IntArg(a, "page", 0, out page)
                         ?? Protocol.IntArg(a, "pageSize", DefaultPageSize, out size)
                         ?? Protocol.IntArg(a, "waitFrames", DefaultWaitFrames, out wait);
            if (err == null && page < 0) err = "page must be >= 0";
            if (err == null && (size < 1 || size > MaxPageSize)) err = "pageSize must be 1.." + MaxPageSize;
            if (err == null && (wait < 1 || wait > MaxWaitFrames)) err = "waitFrames must be 1.." + MaxWaitFrames;
            string label = null;
            if (err == null && press != null)
            {
                JToken lt = press["label"];
                if (lt == null || lt.Type != JTokenType.String) err = "press needs {label:\"...\"} (a string, exact)";
                else label = (string)lt;
                if (err == null && press["index"] != null && press["index"].Type != JTokenType.Null)
                {
                    err = Protocol.IntArg(press, "index", -1, out index);
                    if (err == null && index < 0) err = "index must be >= 0";
                }
            }
            if (err != null) return Bad("args", err);
            string owner = Str(press != null ? press["owner"] : a["owner"]);
            string match = list ? Str(a["match"]) : null;

            if (Arm == null || FrameNow == null) return Bad("imgui", "no imgui tap installed - this is the offline half, or the mod is shutting down");
            if (busy != null) return Bad("busy", "another imgui request is still running - one at a time");
            string armErr;
            try { armErr = Arm(true); } catch (Exception ex) { armErr = ex.GetType().Name + ": " + ex.Message; }
            if (armErr != null) { Reset(); return Bad("patch", "could not install the IMGUI tap: " + armErr); }

            busy = new Pending(list, owner, match, page, size, label, index, wait, FrameNow());
            recording = true;
            RefreshActive();
            return busy;
        }

        private static string Str(JToken t) { return t == null || t.Type != JTokenType.String || ((string)t).Length == 0 ? null : (string)t; }

        internal static object Bad(string code, string message) { return new { ok = false, code, error = Protocol.Clip(message) }; }

        private static string SafeOwner(Func<string> owner)
        {
            try { return owner == null ? null : owner(); } catch (Exception) { return null; }
        }

        /// <summary>Owner match: <paramref name="have"/> is the FULL type name; <paramref name="want"/>
        /// may be the full name or the short one, case-insensitive.</summary>
        internal static bool OwnerIs(string have, string want)
        {
            if (want == null) return true;
            if (have == null) return false;
            if (string.Equals(have, want, StringComparison.OrdinalIgnoreCase)) return true;
            return string.Equals(Short(have), want, StringComparison.OrdinalIgnoreCase);
        }

        internal static string Short(string full)
        {
            int dot = full == null ? -1 : full.LastIndexOf('.');
            return dot < 0 ? full : full.Substring(dot + 1);
        }

        private static int sceneEpoch;

        /// <summary>Game half, on every scene unload: an armed press belongs to the UI of the scene it
        /// was resolved in, so it is dropped (the request ends code:"scene") rather than fired into
        /// whatever draws the same label next.</summary>
        internal static void SceneUnloaded()
        {
            sceneEpoch++;
            target = null;
            RefreshActive();
        }

        internal static JObject Row(Ctl c)
        {
            JObject o = new JObject();
            o["l"] = c.Label.Length > LabelClip ? c.Label.Substring(0, LabelClip) + "~" : c.Label;
            if (c.I > 0) o["i"] = c.I;
            if (c.Toggle) o["k"] = "t";
            if (c.On) o["on"] = true;
            if (!c.Enabled) o["dis"] = true;
            if (c.Owner != null) o["o"] = Short(c.Owner);
            o["r"] = new JArray((int)Math.Round(c.X), (int)Math.Round(c.Y), (int)Math.Round(c.W), (int)Math.Round(c.H));
            return o;
        }

        /// <summary>Stops recording, disarms, removes the patch. Idempotent.</summary>
        private static void Reset()
        {
            recording = false;
            target = null;
            busy = null;
            RefreshActive();
            cur = new List<Ctl>(); last = new List<Ctl>();
            curFrame = lastFrame = countFrame = int.MinValue;
            counts.Clear();
            try { if (Arm != null) Arm(false); } catch (Exception) { }
        }

        internal static void Shutdown()
        {
            Reset();
            fired = false;
            Arm = null;
            FrameNow = null;
        }

        /// <summary>
        /// list: wait for one complete Repaint pass, answer rows. press: same wait, resolve the label
        /// to exactly one (label, i), arm it, wait up to waitFrames for the fire.
        /// </summary>
        internal sealed class Pending : IPending, IReleasable
        {
            private readonly bool list;
            private readonly string owner, match, label;
            private readonly int page, size, index, wait, start;
            private readonly int epoch = sceneEpoch;
            private int armedAt = -1;
            private bool done;

            internal Pending(bool list, string owner, string match, int page, int size, string label, int index, int wait, int start)
            {
                this.list = list; this.owner = owner; this.match = match; this.page = page; this.size = size;
                this.label = label; this.index = index; this.wait = wait; this.start = start;
            }

            public void Release() { if (done) return; done = true; if (busy == this) Reset(); }

            private object End(object result) { Release(); return result; }

            public object Tick(bool cancelled)
            {
                if (done) return Bad("imgui", "request already ended");
                if (cancelled) return End(new { ok = false, code = "cancelled", error = "imgui request cancelled", fired = fired && armedAt >= 0 });
                int now = FrameNow == null ? int.MaxValue : FrameNow();
                if (!list && epoch != sceneEpoch)
                    return End(new { ok = false, code = "scene", error = "a scene unloaded before the press fired - the UI it was resolved in is gone", fired = false });
                if (armedAt >= 0) return Fire(now);

                int frame; bool more;
                List<Ctl> snap = Complete(now, start, out frame, out more);
                if (snap == null)
                {
                    if (now < start + ResolveFrames) return null;
                    snap = new List<Ctl>();        // frames passed and no OnGUI control drew at all
                }
                recording = false;
                RefreshActive();
                return list ? End(List(snap, more)) : Resolve(snap, now);
            }

            private object List(List<Ctl> snap, bool more)
            {
                List<Ctl> hit = new List<Ctl>();
                foreach (Ctl c in snap)
                {
                    if (!OwnerIs(c.Owner, owner)) continue;
                    if (match != null && c.Label.IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    hit.Add(c);
                }
                JObject r = new JObject { ["ok"] = true, ["total"] = hit.Count };
                if (page > 0) r["page"] = page;
                JArray rows = new JArray();
                long from = (long)page * size;
                for (long i = from; i < hit.Count && i < from + size; i++) rows.Add(Row(hit[(int)i]));
                if (from + size < hit.Count) r["hasMore"] = true;
                if (more) r["more"] = true;
                r["rows"] = rows;
                return r;
            }

            private object Resolve(List<Ctl> snap, int now)
            {
                List<Ctl> cand = new List<Ctl>();
                foreach (Ctl c in snap)
                    if (string.Equals(c.Label, label, StringComparison.Ordinal) && OwnerIs(c.Owner, owner) && (index < 0 || c.I == index))
                        cand.Add(c);
                if (cand.Count == 0)
                {
                    JArray seen = new JArray();
                    HashSet<string> uniq = new HashSet<string>();
                    foreach (Ctl c in snap) if (uniq.Add(c.Label) && seen.Count < 10) seen.Add(Row(c)["l"]);
                    return End(new JObject { ["ok"] = false, ["code"] = "notfound", ["error"] = "no IMGUI control labelled exactly '" + Protocol.Clip(label) + "'" + (owner == null ? "" : " owned by " + owner) + (index < 0 ? "" : " with i=" + index), ["fired"] = false, ["controls"] = snap.Count, ["labels"] = seen });
                }
                if (cand.Count > 1)
                {
                    JArray rows = new JArray();
                    for (int i = 0; i < cand.Count && i < 10; i++) rows.Add(Row(cand[i]));
                    return End(new JObject { ["ok"] = false, ["code"] = "ambiguous", ["error"] = cand.Count + " controls match - pass index (the row's i, absent = 0) and/or owner", ["fired"] = false, ["candidates"] = rows });
                }
                Ctl pick = cand[0];
                if (!pick.Enabled) return End(new JObject { ["ok"] = false, ["code"] = "disabled", ["error"] = "the control is drawn disabled (GUI.enabled=false) - a real click would not register either", ["fired"] = false, ["row"] = Row(pick) });
                fired = false;
                target = new Target { Label = pick.Label, I = pick.I, Owner = pick.Owner };
                armedAt = now;
                RefreshActive();
                return null;
            }

            private object Fire(int now)
            {
                if (fired)
                {
                    JObject r = new JObject { ["ok"] = true, ["fired"] = true, ["ev"] = firedEv, ["frames"] = Math.Max(0, firedFrame - armedAt) };
                    return End(r);
                }
                if (now - armedAt > wait)
                    return End(new { ok = false, code = "notfired", error = "armed, but the control was not drawn again within " + wait + " frames", fired = false });
                return null;
            }
        }
    }
}
