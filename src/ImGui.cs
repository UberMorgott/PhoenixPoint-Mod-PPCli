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
    ///   - press mode "post" (OPT-IN, EXPERIMENTAL: live 0.3.1 run got code:"noevent" even with the game
    ///     focused - Unity did not turn the posted WM_LBUTTONDOWN into a MouseDown): the Repaint row also carries the
    ///     control's centre in GUI-screen space (GUIUtility.GUIToScreenPoint, Y down from the window top,
    ///     no flip). The press converts it to client pixels (<see cref="ToClient"/>), PostMessages
    ///     WM_MOUSEMOVE + WM_LBUTTONDOWN to the game's own window (never SendInput, never the real
    ///     cursor, never focus), waits until a REAL MouseDown reached DoControl for that control and
    ///     grabbed hotControl, posts WM_LBUTTONUP on a LATER frame, and reports fired only when the real
    ///     MouseUp made DoControl return the click. The patch only observes there - the body runs where a
    ///     human click runs it, between Layout passes, so a button that restructures the layout is safe.
    ///     Any down that was posted gets its up, also on timeout/cancel/scene unload.
    ///   - press mode "force" (DEFAULT): forces DoControl's return on a Repaint/MouseMove pass. The
    ///     button body then runs mid-pass; a body that adds later GUILayout controls used to break that
    ///     Repaint ("Getting control N's position in a group with only N controls", live 0.3.0/0.3.1: it
    ///     closed the ContentTool bench). SINCE 0.3.2 the rest of THAT pass is LAYOUT-TOLERANT (see
    ///     <see cref="InPass"/>): the game half pads an overrun group with a dummy entry and swaps a
    ///     mismatched group for an empty one - exactly what GetNext already does on every non-Repaint
    ///     pass, i.e. what a real click's MouseUp pass sees. Nothing is drawn at the padded rects, the next
    ///     Layout rebuilds from the new state. NOT GUIUtility.ExitGUI (the reviewed first design): an
    ///     OnGUI that wraps its body in catch(Exception) - the ContentTool bench does - swallows the
    ///     ExitGUIException and closes itself exactly like on the original ArgumentException.
    ///     After the fire the request SETTLES <see cref="SettleFrames"/> frames and reports
    ///     repaired (padded mismatches), alive (the owner drew again) and errors (error logs meanwhile).
    ///   - force fires ONLY on a SAFE pass (<see cref="SafePass"/>): Repaint or MouseMove, and only while
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
        /// <summary>Game half: this process's own game window (hwnd, client size, Unity Screen size).</summary>
        internal static Func<WinInfo> Window;
        /// <summary>Game half: PostMessage(hwnd, msg, wParam, lParam). Null = posted, else the error.</summary>
        internal static Func<long, int, int, int, string> PostMsg;

        internal const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, MK_LBUTTON = 0x0001;

        internal sealed class WinInfo
        {
            internal long Hwnd;
            internal int ClientW, ClientH, ScreenW, ScreenH;
            internal string Error;
        }

        /// <summary>MAKELPARAM(x, y): client coordinates, low word x, high word y.</summary>
        internal static int LParam(int x, int y) { return (y << 16) | (x & 0xFFFF); }

        /// <summary>GUI-screen point (Unity pixels, Y down from the window top) -> client pixel of the
        /// window. Scales when Unity renders at another size than the client area (fullscreen window at
        /// a lower resolution). False = unknown point or outside the client area.</summary>
        internal static bool ToClient(float sx, float sy, int clientW, int clientH, int screenW, int screenH, out int cx, out int cy)
        {
            cx = cy = -1;
            if (float.IsNaN(sx) || float.IsNaN(sy) || float.IsInfinity(sx) || float.IsInfinity(sy)) return false;
            if (clientW <= 0 || clientH <= 0) return false;
            double kx = screenW > 0 ? (double)clientW / screenW : 1.0;
            double ky = screenH > 0 ? (double)clientH / screenH : 1.0;
            double x = Math.Floor(sx * kx), y = Math.Floor(sy * ky);
            if (x < 0 || y < 0 || x >= clientW || y >= clientH) return false;
            cx = (int)x; cy = (int)y;
            return true;
        }

        /// <summary>The Repaint patch computes the GUI-screen point only while a list/resolve records.</summary>
        internal static bool Recording { get { return recording; } }

        /// <summary>The postfix's one-branch early out: nothing is listing and nothing is armed.</summary>
        internal static volatile bool Active;

        internal sealed class Ctl
        {
            internal string Label;
            internal int I;
            internal bool Toggle, On, Enabled;
            internal float X, Y, W, H;
            /// <summary>Centre in GUI-screen space (NaN = not computed).</summary>
            internal float SX = float.NaN, SY = float.NaN;
            internal string Owner;
        }

        private sealed class Target
        {
            internal string Label;
            internal int I;
            /// <summary>true = "post" mode: observe real MouseDown/MouseUp, never force.</summary>
            internal bool Post;
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

        // "post" mode observations (reset per press). down/up: 0 = not seen, 1 = grabbed / clicked,
        // -1 = the event reached the control but missed it.
        private static int pDown, pDownFrame, pDownHot, pUp, pUpFrame;
        private static float pDownMx = float.NaN, pDownMy = float.NaN, pUpMx = float.NaN, pUpMy = float.NaN;
        private static readonly Dictionary<string, int> evFrames = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> evLast = new Dictionary<string, int>();

        // ------------------------------------------------------------------ force: pass guard + settle

        /// <summary>Frames a fired force press keeps watching before it answers.</summary>
        internal const int SettleFrames = 2;
        internal const int MaxErrors = 3;

        /// <summary>Padded entries per fired pass; past it padding STOPS (reply capped:true) - a runaway
        /// loop after the press must not grow the live layout cache without bound.</summary>
        internal const int MaxRepairs = 512;
        internal const int MaxRepairKinds = 8;

        /// <summary>Game half's one-branch early out for the layout prefixes.</summary>
        internal static volatile bool GuardArmed;
        /// <summary>Game half: identity of the layout cache the current OnGUI call draws into
        /// (GUILayoutUtility.current); captured at fire, the guard pads only that cache.</summary>
        internal static Func<object> PassKey;
        private static object gKey;
        private static int gFrame = int.MinValue;
        private static string gEv;
        private static int repaired, unrepaired;
        private static bool capped;
        private static readonly Dictionary<string, int> repairKinds = new Dictionary<string, int>();
        private static string warn;
        private static bool settling, alive;
        private static string settleOwner;
        private static readonly List<string> errs = new List<string>();

        /// <summary>
        /// True = the caller (a GUILayout prefix) runs in the SAME pass a force press fired in (frame +
        /// raw event type + the owner's layout cache <paramref name="key"/>; the owner's
        /// EndGUI/EndGUIFromException ends it earlier via <see cref="PassEnded"/>), so a layout mismatch
        /// there is the press's doing and is padded. A later frame disarms. Another MonoBehaviour's OnGUI
        /// or a GUI.Window (own cache) in the same pass is never padded - its own defects stay visible.
        /// </summary>
        internal static bool InPass(int frame, string ev, object key)
        {
            if (!GuardArmed) return false;
            if (frame != gFrame) { PassEnded(); return false; }
            return string.Equals(ev, gEv, StringComparison.Ordinal) && (gKey == null || ReferenceEquals(key, gKey));
        }

        /// <summary>A layout prefix is about to pad one mismatch of <paramref name="kind"/> ("next",
        /// "peek", "group:&lt;Type&gt;", "area:&lt;Type&gt;"). False = cap reached, do not pad.</summary>
        internal static bool TryRepair(string kind)
        {
            if (repaired >= MaxRepairs) { capped = true; return false; }
            repaired++;
            Count(kind);
            return true;
        }

        /// <summary>A mismatch the prefix could not pad (reflection failed, no parent group).</summary>
        internal static void Unrepaired(string kind) { unrepaired++; Count("!" + kind); }

        private static void Count(string kind)
        {
            kind = kind ?? "?";
            int n;
            if (repairKinds.TryGetValue(kind, out n)) repairKinds[kind] = n + 1;
            else if (repairKinds.Count < MaxRepairKinds) repairKinds[kind] = 1;
        }

        /// <summary>A failed unpatch (Arm(false) error): surfaced as warn on the reply, never swallowed.</summary>
        internal static void Warn(string w) { if (w != null) warn = w; }

        internal static string TakeWarn() { string w = warn; warn = null; return w; }

        /// <summary>Game half: GUIUtility.EndGUI / EndGUIFromException - the fired pass is over.</summary>
        internal static void PassEnded() { GuardArmed = false; }

        /// <summary>Game half: every Unity log line while the tap is armed; kept only while a fired press
        /// settles (the fire pass itself included).</summary>
        internal static void Logged(bool bad, string msg)
        {
            if (!settling || !bad || errs.Count >= MaxErrors) return;
            msg = msg ?? "";
            int nl = msg.IndexOf('\n');
            if (nl >= 0) msg = msg.Substring(0, nl);
            errs.Add(msg.Length > 160 ? msg.Substring(0, 160) + "~" : msg);
        }

        private static void ClearSettle()
        {
            GuardArmed = false; gFrame = int.MinValue; gEv = null; gKey = null; repaired = unrepaired = 0; capped = false; repairKinds.Clear();
            settling = alive = false; settleOwner = null; errs.Clear();
        }

        private static void ClearPost()
        {
            pDown = pDownFrame = pDownHot = pUp = pUpFrame = 0;
            pDownMx = pDownMy = pUpMx = pUpMy = float.NaN;
            evFrames.Clear(); evLast.Clear();
        }

        /// <summary>
        /// One control reached DoControl on a non-Layout event (<paramref name="ev"/> = the event type
        /// at DoControl ENTRY). Returns true = force this control's press NOW (force mode only; the
        /// caller sets __result = !on and GUI.changed). <paramref name="owner"/> is lazy: a stack walk,
        /// paid only for recorded Repaint rows and fire candidates. <paramref name="sx"/>/<paramref name="sy"/>
        /// = centre in GUI-screen space (Repaint while recording), <paramref name="mx"/>/<paramref name="my"/>
        /// = Event.mousePosition, <paramref name="hot"/> = hotControl is this control after DoControl,
        /// <paramref name="result"/> = DoControl's own return, <paramref name="hotId"/> = hotControl.
        /// </summary>
        internal static bool Observe(int frame, bool repaint, string ev, string label,
                                     float x, float y, float w, float h,
                                     bool enabled, bool toggle, bool on, Func<string> owner, bool idle = true,
                                     float sx = float.NaN, float sy = float.NaN, float mx = float.NaN, float my = float.NaN,
                                     bool hot = false, bool result = false, int hotId = 0)
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
                    cur.Add(new Ctl { Label = label, I = n, Toggle = toggle || on, On = on, Enabled = enabled, X = x, Y = y, W = w, H = h, SX = sx, SY = sy, Owner = o });
                }
            }

            // Settling after a fire: did the owner draw again on a later Repaint (panel still open)?
            if (settling && repaint && !alive && frame > firedFrame && settleOwner != null
                && string.Equals(settleOwner, SafeOwner(owner), StringComparison.Ordinal))
                alive = true;

            Target t = target;
            if (t != null && t.Post)
            {
                if (!repaint)
                {
                    int lf;
                    if (!evLast.TryGetValue(ev, out lf) || lf != frame)
                    {
                        evLast[ev] = frame;
                        int c; evFrames.TryGetValue(ev, out c); evFrames[ev] = c + 1;
                        if (ev == "MouseDown" && pDown == 0) { pDownMx = mx; pDownMy = my; }
                        if (ev == "MouseUp" && pDown > 0 && pUp == 0) { pUpMx = mx; pUpMy = my; }
                    }
                }
                if ((ev == "MouseDown" || ev == "MouseUp") && n == t.I && string.Equals(label, t.Label, StringComparison.Ordinal)
                    && (t.Owner == null || string.Equals(t.Owner, SafeOwner(owner), StringComparison.Ordinal)))
                {
                    if (ev == "MouseDown" && pDown == 0) { pDown = hot ? 1 : -1; pDownFrame = frame; pDownHot = hotId; pDownMx = mx; pDownMy = my; }
                    else if (ev == "MouseUp" && pDown > 0 && pUp == 0) { pUp = result != on ? 1 : -1; pUpFrame = frame; pUpMx = mx; pUpMy = my; }
                }
                return false;
            }
            if (t != null && enabled && SafePass(ev, idle) && n == t.I && string.Equals(label, t.Label, StringComparison.Ordinal)
                && (t.Owner == null || string.Equals(t.Owner, SafeOwner(owner), StringComparison.Ordinal)))
            {
                target = null;
                fired = true; firedEv = ev; firedFrame = frame;
                // The body runs right after this returns: arm the layout-tolerant rest of THIS pass.
                errs.Clear(); repaired = unrepaired = 0; capped = false; repairKinds.Clear(); alive = false;
                settling = true; settleOwner = t.Owner;
                gKey = null;
                try { gKey = PassKey == null ? null : PassKey(); } catch (Exception) { }
                gFrame = frame; gEv = ev; GuardArmed = true;
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

        private static void RefreshActive() { Active = recording || target != null || settling; }

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
                return Bad("args", "imgui takes {list:true, owner?, match?, page?, pageSize?} OR {press:{label, owner?, index?, mode?:\"post\"|\"force\"}, waitFrames?, diag?}");
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
            // Default "force": "post" is opt-in until live-proven - on the 0.3.1 live run (D:\PP-Instance3,
            // game FOREGROUND, borderless 2560x1440) Unity delivered no MouseDown for a posted
            // WM_LBUTTONDOWN (code:"noevent", diag evs {}).
            bool post = false, diag = false;
            if (err == null && press != null && press["mode"] != null && press["mode"].Type != JTokenType.Null)
            {
                JToken mt = press["mode"];
                if (mt.Type != JTokenType.String || ((string)mt != "post" && (string)mt != "force")) err = "press.mode must be \"force\" (default) or \"post\"";
                else post = (string)mt == "post";
            }
            if (err == null && a != null && a["diag"] != null && a["diag"].Type != JTokenType.Null)
            {
                if (a["diag"].Type != JTokenType.Boolean) err = "diag must be true/false";
                else diag = (bool)a["diag"];
            }
            if (err != null) return Bad("args", err);
            string owner = Str(press != null ? press["owner"] : a["owner"]);
            string match = list ? Str(a["match"]) : null;

            if (Arm == null || FrameNow == null) return Bad("imgui", "no imgui tap installed - this is the offline half, or the mod is shutting down");
            if (busy != null) return Bad("busy", "another imgui request is still running - one at a time");
            string armErr;
            try { armErr = Arm(true); } catch (Exception ex) { armErr = ex.GetType().Name + ": " + ex.Message; }
            if (armErr != null) { Reset(); return Bad("patch", "could not install the IMGUI tap: " + armErr); }

            busy = new Pending(list, owner, match, page, size, label, index, wait, FrameNow()) { PostMode = post, Diag = diag };
            recording = true;
            RefreshActive();
            return busy;
        }

        internal const int EchoClip = 120;

        /// <summary>Caller-supplied text echoed into an error, clipped: an error never grows with its args.</summary>
        internal static string Echo(string s)
        {
            if (s == null) return "";
            return s.Length > EchoClip ? s.Substring(0, EchoClip) + "~" : s;
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
            ClearPost();
            ClearSettle();
            RefreshActive();
            string ue;
            try { ue = Arm == null ? null : Arm(false); } catch (Exception ex) { ue = ex.GetType().Name + ": " + ex.Message; }
            Warn(ue);
        }

        /// <summary>Runner destroyed: drop any request state and remove the patch, keep the delegates.</summary>
        internal static void Abort() { Reset(); }

        internal static void Shutdown()
        {
            Reset();
            fired = false;
            Arm = null;
            FrameNow = null;
            Window = null;
            PostMsg = null;
            PassKey = null;
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
            internal bool PostMode, Diag;
            private WinInfo win;
            private int cx = -1, cy = -1, upAt;
            private float sx = float.NaN, sy = float.NaN;
            private bool downPosted, upPosted;

            internal Pending(bool list, string owner, string match, int page, int size, string label, int index, int wait, int start)
            {
                this.list = list; this.owner = owner; this.match = match; this.page = page; this.size = size;
                this.label = label; this.index = index; this.wait = wait; this.start = start;
            }

            /// <summary>Any posted down gets its up, whatever ends the request - Unity must never be
            /// left with the left button held.</summary>
            public void Release()
            {
                if (done) return;
                done = true;
                if (downPosted && !upPosted) { upPosted = true; Send(WM_LBUTTONUP, 0); }
                if (busy == this) Reset();
            }

            private string Send(int msg, int wParam)
            {
                if (PostMsg == null || win == null) return "no PostMessage hook installed";
                try { return PostMsg(win.Hwnd, msg, wParam, LParam(cx, cy)); }
                catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
            }

            private JObject Fail(string code, string error, string stage)
            {
                JObject r = new JObject { ["ok"] = false, ["code"] = code, ["error"] = Protocol.Clip(error), ["fired"] = false, ["mode"] = "post" };
                if (stage != null) r["stage"] = stage;
                r["evs"] = Evs();
                if (Diag) r["diag"] = DiagObj();
                return r;
            }

            private static JObject Evs()
            {
                JObject e = new JObject();
                foreach (KeyValuePair<string, int> kv in evFrames) e[kv.Key] = kv.Value;
                return e;
            }

            private static JToken Pt(float x, float y)
            {
                if (float.IsNaN(x) || float.IsNaN(y)) return JValue.CreateNull();
                return new JArray((int)Math.Round(x), (int)Math.Round(y));
            }

            private JObject DiagObj()
            {
                JObject d = new JObject();
                if (win != null)
                {
                    d["hwnd"] = "0x" + win.Hwnd.ToString("X");
                    d["client"] = new JArray(win.ClientW, win.ClientH);
                    d["screen"] = new JArray(win.ScreenW, win.ScreenH);
                }
                d["gui"] = Pt(sx, sy);
                d["pt"] = new JArray(cx, cy);
                d["evs"] = Evs();
                if (pDown != 0) d["down"] = new JObject { ["hit"] = pDown > 0, ["mp"] = Pt(pDownMx, pDownMy), ["hot"] = pDownHot, ["f"] = pDownFrame - armedAt };
                else if (!float.IsNaN(pDownMx)) d["down"] = new JObject { ["hit"] = false, ["mp"] = Pt(pDownMx, pDownMy) };
                if (pUp != 0 || !float.IsNaN(pUpMx)) d["up"] = new JObject { ["hit"] = pUp > 0, ["mp"] = Pt(pUpMx, pUpMy), ["f"] = pUpFrame - armedAt };
                return d;
            }

            /// <summary>Releases (unpatch) FIRST, so an unpatch failure lands as warn on this very reply.</summary>
            private object End(object result)
            {
                Release();
                string w = TakeWarn();
                if (w == null || result == null) return result;
                JObject j = result as JObject ?? JObject.FromObject(result);
                j["warn"] = Protocol.Clip(w);
                return j;
            }

            public object Tick(bool cancelled)
            {
                if (done) return Bad("imgui", "request already ended");
                if (cancelled) return End(new { ok = false, code = "cancelled", error = "imgui request cancelled", fired = fired && armedAt >= 0 });
                int now = FrameNow == null ? int.MaxValue : FrameNow();
                if (!list && epoch != sceneEpoch && !(fired && armedAt >= 0))   // a fired press still answers fired

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
                    return End(new JObject { ["ok"] = false, ["code"] = "notfound", ["error"] = "no IMGUI control labelled exactly '" + Echo(label) + "'" + (owner == null ? "" : " owned by " + Echo(owner)) + (index < 0 ? "" : " with i=" + index), ["fired"] = false, ["controls"] = snap.Count, ["labels"] = seen });
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
                if (PostMode)
                {
                    sx = pick.SX; sy = pick.SY;
                    string werr = null;
                    try { win = Window == null ? null : Window(); } catch (Exception ex) { werr = ex.GetType().Name + ": " + ex.Message; }
                    if (win == null || win.Hwnd == 0)
                        return End(Fail("nohwnd", "no game window of this process to post the click to: " + (win != null && win.Error != null ? win.Error : werr ?? "no window hook installed"), null));
                    if (!ToClient(sx, sy, win.ClientW, win.ClientH, win.ScreenW, win.ScreenH, out cx, out cy))
                    {
                        JObject off = Fail("offscreen", "the control's centre is not inside the game window's client area", null);
                        off["row"] = Row(pick);
                        return End(off);
                    }
                    ClearPost();
                    target = new Target { Label = pick.Label, I = pick.I, Owner = pick.Owner, Post = true };
                    armedAt = now;
                    RefreshActive();
                    string perr = Send(WM_MOUSEMOVE, 0);
                    if (perr == null) { perr = Send(WM_LBUTTONDOWN, MK_LBUTTON); if (perr == null) downPosted = true; }
                    if (perr != null) return End(Fail("nohwnd", "PostMessage failed: " + perr, "down"));
                    return null;
                }
                target = new Target { Label = pick.Label, I = pick.I, Owner = pick.Owner };
                armedAt = now;
                RefreshActive();
                return null;
            }

            /// <summary>post mode: down posted at armedAt -> real MouseDown grabbed the control -> up
            /// posted on a LATER frame -> real MouseUp returned the click.</summary>
            private object FirePost(int now)
            {
                if (pDown == 0)
                {
                    if (now - armedAt <= wait) return null;
                    int md; evFrames.TryGetValue("MouseDown", out md);
                    return End(md > 0
                        ? Fail("missed", "a MouseDown reached OnGUI but not this control within " + wait + " frames (another control took it, or the point is off the control)", "down")
                        : Fail("noevent", "WM_LBUTTONDOWN posted, but Unity delivered no MouseDown within " + wait + " frames (unfocused window?)", "down"));
                }
                if (pDown < 0) return End(Fail("missed", "the MouseDown reached this control but missed its rect - coordinates off", "down"));
                if (!upPosted)
                {
                    if (now <= pDownFrame) return null;
                    upPosted = true; upAt = now;
                    string perr = Send(WM_LBUTTONUP, 0);
                    if (perr != null) return End(Fail("nohwnd", "PostMessage failed: " + perr, "up"));
                    return null;
                }
                if (pUp > 0)
                {
                    JObject r = new JObject { ["ok"] = true, ["fired"] = true, ["mode"] = "post", ["ev"] = "MouseUp", ["frames"] = Math.Max(0, pUpFrame - armedAt) };
                    if (Diag) r["diag"] = DiagObj();
                    return End(r);
                }
                if (pUp < 0) return End(Fail("missed", "the MouseUp released this control off its rect - no click", "up"));
                if (now - upAt > wait) return End(Fail("noevent", "WM_LBUTTONUP posted, but Unity delivered no MouseUp to the control within " + wait + " frames", "up"));
                return null;
            }

            private object Fire(int now)
            {
                if (PostMode) return FirePost(now);
                if (fired)
                {
                    // Settle: let the next Layout + Repaint run on the new state before answering.
                    if (now <= firedFrame + SettleFrames) return null;
                    JObject r = new JObject { ["ok"] = true, ["fired"] = true, ["mode"] = "force", ["ev"] = firedEv, ["frames"] = Math.Max(0, firedFrame - armedAt), ["repaired"] = repaired };
                    if (repairKinds.Count > 0)
                    {
                        JObject k = new JObject();
                        foreach (KeyValuePair<string, int> kv in repairKinds) k[kv.Key] = kv.Value;
                        r["repairs"] = k;
                    }
                    if (unrepaired > 0) r["unrepaired"] = unrepaired;
                    if (capped) r["capped"] = true;
                    if (settleOwner != null) r["alive"] = alive;
                    if (errs.Count > 0) r["errors"] = new JArray(errs.ToArray());
                    return End(r);
                }
                if (now - armedAt > wait)
                    return End(new { ok = false, code = "notfired", error = "armed, but the control was not drawn again within " + wait + " frames", fired = false });
                return null;
            }
        }
    }
}
