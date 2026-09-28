using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Morgott.PPBridge
{
    /// <summary>One clickable uGUI element the game half found this frame. Pure data plus an opaque
    /// <see cref="Ref"/> (the GameObject) only the game half reads back.</summary>
    internal sealed class UiNode
    {
        /// <summary>Visible text (child Text / TMP_Text, rich-text tags stripped), else the GO name.</summary>
        internal string Label;
        /// <summary>FULL hierarchy path, "Root/Child/Button" ("[k]" = k-th same-named sibling, k&gt;0).</summary>
        internal string Path;
        /// <summary>Short component type: PhoenixGeneralButton, Button, Toggle, ...</summary>
        internal string Type;
        internal bool Interactable = true;
        /// <summary>false = on no screen pixel, or faded out (CanvasGroup alpha 0): a human cannot see it.</summary>
        internal bool Visible = true;
        /// <summary>Screen rect, top-left origin, Y DOWN (screenshot pixels).</summary>
        internal int X, Y, W, H;
        /// <summary>A Slider/Scrollbar: its value moves by dragging, which a click cannot do.</summary>
        internal bool Drag;
        internal object Ref;
    }

    /// <summary>What the game half's click did.</summary>
    internal sealed class UiClickResult
    {
        /// <summary>"pointerClick" | "pointerDown" (press handler is not the click handler).
        /// Null = nothing on the object handles a click - then <see cref="Error"/>.</summary>
        internal string Handler;
        /// <summary>FULL path of the object that took the press when it is not the element itself
        /// (a child button, or the ancestor that owns the element).</summary>
        internal string Target;
        /// <summary>Dispatched anyway (force) although the raycast said "blocked"/"noraycast".</summary>
        internal string Warn;
        internal string Error;
        /// <summary>Refused BEFORE any event went out: "blocked" (something else is on top at the
        /// centre) or "noraycast" (nothing raycastable there) - a real mouse click would not land.</summary>
        internal string Refuse;
        /// <summary>FULL path of the object the raycast hit first (blocked), for the reply's top.</summary>
        internal string Top;
        /// <summary>First line of an exception a handler threw during the dispatch (ExecuteEvents
        /// swallows it into the log) - the events WERE sent, the action behind them failed.</summary>
        internal string Threw;
    }

    /// <summary>
    /// The pure half of <c>ui</c>: the game's native uGUI (Phoenix Point screens, TFTV panels) as
    /// compact rows, and a real pointer click on one - instead of hand-written reflection into view
    /// states. Names no Unity type: the game half (UiGame.cs) installs <see cref="Scan"/>,
    /// <see cref="ClickRun"/> and <see cref="FrameNow"/>, so matching, paths, paging and every reply
    /// budget run offline in the self-check.
    ///
    /// Contract (token-frugal: default 25 rows, absent = default):
    ///   ui {tree:{match?, root?, interactable?, all?}, page?, pageSize?}
    ///       -> {ok, total, rows:[{l, p, t, dis?, hid?, r:[x,y,w,h]}], hasMore?}
    ///          match = substring of label OR path (case-insensitive); root = substring a row's FULL
    ///          path must contain; interactable:true drops dis rows; all:true keeps invisible ones.
    ///          p = the last <see cref="PathSegs"/> path segments ("~/" = more above) - pass it back as `path`.
    ///   ui {click:{label|path, index?, force?}, waitFrames?}
    ///       -> {ok, clicked:p, handler:"pointerClick"|"pointerDown", target?, warn?, frames}
    ///          target = p of the object that took the press when it is not the element itself.
    ///          label = exact (case-insensitive, whitespace-collapsed) text; path = a row's p, or any
    ///          '/'-aligned suffix of the full path. index picks among several matches (row order).
    ///          Only visible rows are clickable. A raycast at the element's centre runs FIRST: another
    ///          element on top -> blocked(+top), nothing raycastable -> noraycast, both before any
    ///          event is sent; force:true dispatches anyway (warn says why it would have refused).
    ///          The click is a real pointer sequence (enter, down, up, click, exit); waitFrames
    ///          (default 1) lets the UI react before the reply. ok:true = the events were DISPATCHED
    ///          and no handler threw (a swallowed handler exception -> threw, click not undone) - NOT
    ///          that the screen changed: check the effect with a follow-up ui tree / state / screenshot.
    ///   diag:true (either form) adds diag:{scanMs, nodes, cached}. One scan per frame is shared by
    ///   every request in that frame; a click drops it.
    /// Refusals: args, ui (offline), notfound(+near), ambiguous(+candidates), disabled(+row),
    ///           blocked(+top,row), noraycast(+row), unsupported(+row: Slider/Scrollbar), noclick, threw.
    /// Main thread only, like every verb.
    /// </summary>
    internal static class UiTap
    {
        internal const int DefaultPageSize = 25;
        internal const int MaxPageSize = 200;
        internal const int DefaultWaitFrames = 1;
        internal const int MaxWaitFrames = 600;
        internal const int LabelClip = 40;
        internal const int SegClip = 32;
        internal const int PathSegs = 3;
        internal const int MaxCandidates = 8;
        internal const int EchoClip = 80;

        /// <summary>Game half: every clickable uGUI element in the loaded scenes, draw order.</summary>
        internal static Func<List<UiNode>> Scan;
        /// <summary>Game half: run the pointer sequence on node.Ref. bool = force (dispatch even when
        /// the raycast at the centre is blocked or empty).</summary>
        internal static Func<UiNode, bool, UiClickResult> ClickRun;
        /// <summary>Game half: Time.frameCount.</summary>
        internal static Func<int> FrameNow;

        internal static object Bad(string code, string message) { return new { ok = false, code, error = Protocol.Clip(message) }; }

        internal static void Shutdown() { Scan = null; ClickRun = null; FrameNow = null; cache = null; }

        // One scan per frame: a tree and a click (or two pages) in the same frame share it. A click
        // drops it - what it changed must not be answered from the scan before it.
        private static List<UiNode> cache;
        private static int cacheFrame;
        private static Func<List<UiNode>> cacheScan;
        private static double lastMs;
        private static bool lastCached;
        private static int lastCount;

        private static List<UiNode> Scanned()
        {
            int frame = FrameNow == null ? int.MinValue : FrameNow();
            if (cache != null && FrameNow != null && frame == cacheFrame && cacheScan == Scan) { lastCached = true; return cache; }
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            cache = null;
            List<UiNode> nodes = Scan();
            lastMs = sw.Elapsed.TotalMilliseconds;
            lastCached = false;
            lastCount = nodes == null ? 0 : nodes.Count;
            cache = FrameNow == null ? null : nodes;
            cacheFrame = frame;
            cacheScan = Scan;
            return nodes ?? new List<UiNode>();
        }

        /// <summary>diag:true - scanMs = wall time of the last real scan (0-cost when cached).</summary>
        private static JObject Diag()
        {
            return new JObject { ["scanMs"] = Math.Round(lastMs, 2), ["nodes"] = lastCount, ["cached"] = lastCached };
        }

        internal static object Dispatch(string verb, JObject a)
        {
            if (verb != "ui") return null;
            if (a == null) a = new JObject();
            JObject tree = a["tree"] as JObject;
            JObject click = a["click"] as JObject;
            if ((tree == null) == (click == null) || (a["tree"] != null && tree == null) || (a["click"] != null && click == null))
                return Bad("args", "ui takes exactly one of {tree:{match?,root?,interactable?,all?}, page?, pageSize?} or {click:{label|path, index?, force?}, waitFrames?}");

            int page = 0, size = DefaultPageSize, wait = DefaultWaitFrames, index = -1;
            bool diag = false;
            string err = Bool(a, "diag", out diag)
                         ?? Protocol.IntArg(a, "page", 0, out page)
                         ?? Protocol.IntArg(a, "pageSize", DefaultPageSize, out size)
                         ?? Protocol.IntArg(a, "waitFrames", DefaultWaitFrames, out wait);
            if (err == null && page < 0) err = "page must be >= 0";
            if (err == null && (size < 1 || size > MaxPageSize)) err = "pageSize must be 1.." + MaxPageSize;
            if (err == null && (wait < 0 || wait > MaxWaitFrames)) err = "waitFrames must be 0.." + MaxWaitFrames;
            if (err != null) return Bad("args", err);

            if (tree != null)
            {
                string match = null, root = null; bool inter = false, all = false;
                err = Str(tree, "match", out match) ?? Str(tree, "root", out root) ?? Bool(tree, "interactable", out inter) ?? Bool(tree, "all", out all);
                if (err != null) return Bad("args", err);
                if (Scan == null) return Bad("ui", "no ui runner installed - this is the offline half, or the mod is shutting down");
                List<UiNode> nodes;
                try { nodes = Scanned(); }
                catch (Exception ex) { return Bad("threw", ex.GetType().Name + ": " + ex.Message); }
                JObject tr = Tree(nodes, match, root, inter, all, page, size);
                if (diag) tr["diag"] = Diag();
                return tr;
            }

            string label = null, path = null; bool force = false;
            err = Str(click, "label", out label) ?? Str(click, "path", out path) ?? Bool(click, "force", out force);
            if (err == null && (label == null) == (path == null)) err = "click takes exactly one of label or path";
            if (err == null && click["index"] != null && click["index"].Type != JTokenType.Null)
            {
                err = Protocol.IntArg(click, "index", -1, out index);
                if (err == null && index < 0) err = "index must be >= 0";
            }
            if (err != null) return Bad("args", err);
            if (Scan == null || ClickRun == null) return Bad("ui", "no ui runner installed - this is the offline half, or the mod is shutting down");

            List<UiNode> all2;
            try { all2 = Scanned(); }
            catch (Exception ex) { return Bad("threw", ex.GetType().Name + ": " + ex.Message); }
            UiNode pick;
            object refusal = Resolve(all2, label, path, index, out pick);
            if (refusal != null) return refusal;
            if (pick.Drag)
                return new JObject
                {
                    ["ok"] = false, ["code"] = "unsupported",
                    ["error"] = "a " + pick.Type + " changes its value by dragging - ui click does not drag (a press would just jump it to the centre); set .value through call instead",
                    ["row"] = Row(pick)
                };

            UiClickResult res;
            cache = null;                                   // the click may change what is on screen
            try { res = ClickRun(pick, force); }
            catch (Exception ex) { return Bad("threw", ex.GetType().Name + ": " + ex.Message); }
            if (res != null && res.Refuse != null)
            {
                JObject no = new JObject
                {
                    ["ok"] = false, ["code"] = res.Refuse,
                    ["error"] = res.Refuse == "blocked"
                        ? "another element is on top at the centre - a real mouse click would land there; nothing was dispatched (force:true clicks anyway)"
                        : "nothing raycastable at the centre (raycasts off, e.g. CanvasGroup.blocksRaycasts=false) - a real mouse click would miss; nothing was dispatched (force:true clicks anyway)"
                };
                if (res.Top != null) no["top"] = ShortPath(res.Top);
                no["row"] = Row(pick);
                return no;
            }
            if (res == null || res.Handler == null)
                return new JObject { ["ok"] = false, ["code"] = "noclick", ["error"] = Protocol.Clip(res != null && res.Error != null ? res.Error : "nothing on the element handles a pointer click"), ["row"] = Row(pick) };
            if (res.Threw != null)
                return new JObject
                {
                    ["ok"] = false, ["code"] = "threw", ["clicked"] = ShortPath(pick.Path), ["handler"] = res.Handler ?? "none",
                    ["error"] = Protocol.Clip("the click WAS dispatched but a handler threw (logged by Unity, not undone): " + res.Threw)
                };
            JObject reply = new JObject { ["ok"] = true, ["clicked"] = ShortPath(pick.Path), ["handler"] = res.Handler };
            if (res.Target != null) reply["target"] = ShortPath(res.Target);
            if (diag) reply["diag"] = Diag();
            if (res.Warn != null) reply["warn"] = Protocol.Clip(res.Warn);
            if (wait == 0 || FrameNow == null) { reply["frames"] = 0; return reply; }
            return new Pending(reply, wait, FrameNow());
        }

        // ------------------------------------------------------------------ pure logic

        internal static JObject Tree(List<UiNode> nodes, string match, string root, bool interactableOnly, bool all, int page, int size)
        {
            List<UiNode> hit = new List<UiNode>();
            foreach (UiNode n in nodes)
            {
                if (!all && !n.Visible) continue;
                if (interactableOnly && !n.Interactable) continue;
                if (root != null && (n.Path ?? "").IndexOf(root, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (match != null && (n.Label ?? "").IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0
                                  && (n.Path ?? "").IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0) continue;
                hit.Add(n);
            }
            JObject r = new JObject { ["ok"] = true, ["total"] = hit.Count };
            if (page > 0) r["page"] = page;
            JArray rows = new JArray();
            long from = (long)page * size;
            for (long i = from; i < hit.Count && i < from + size; i++) rows.Add(Row(hit[(int)i]));
            r["rows"] = rows;
            if (from + size < hit.Count) r["hasMore"] = true;
            return r;
        }

        /// <summary>Label/path -> exactly one visible node, or the refusal DTO.</summary>
        internal static object Resolve(List<UiNode> nodes, string label, string path, int index, out UiNode pick)
        {
            pick = null;
            List<UiNode> cand = new List<UiNode>();
            string want = label != null ? Norm(label) : null;
            string wantPath = path != null ? StripShort(path) : null;
            foreach (UiNode n in nodes)
            {
                if (!n.Visible) continue;
                if (want != null ? string.Equals(Norm(n.Label), want, StringComparison.OrdinalIgnoreCase)
                                 : PathMatches(n.Path, wantPath))
                    cand.Add(n);
            }
            string what = label != null ? "label '" + Echo(label) + "'" : "path '" + Echo(path) + "'";
            if (cand.Count == 0)
            {
                JArray near = new JArray();
                string key = label ?? LastSeg(wantPath);
                foreach (UiNode n in nodes)
                    if (n.Visible && near.Count < MaxCandidates && key.Length > 0
                        && ((n.Label ?? "").IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0 || (n.Path ?? "").IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0))
                        near.Add(Row(n));
                int vis = 0; foreach (UiNode n in nodes) if (n.Visible) vis++;
                return new JObject { ["ok"] = false, ["code"] = "notfound", ["error"] = "no visible ui element with " + what + " (" + vis + " visible) - ui {tree:{match}} lists them", ["near"] = near };
            }
            if (index >= 0)
            {
                if (index >= cand.Count)
                    return new JObject { ["ok"] = false, ["code"] = "notfound", ["error"] = "index " + index + " but only " + cand.Count + " element(s) match " + what };
                pick = cand[index];
            }
            else if (cand.Count > 1)
            {
                JArray rows = new JArray();
                for (int i = 0; i < cand.Count && i < MaxCandidates; i++) rows.Add(Row(cand[i]));
                return new JObject { ["ok"] = false, ["code"] = "ambiguous", ["error"] = cand.Count + " elements match " + what + " - pass index (candidate order) or a longer path", ["candidates"] = rows };
            }
            else pick = cand[0];
            if (!pick.Interactable)
            {
                UiNode p = pick; pick = null;
                return new JObject { ["ok"] = false, ["code"] = "disabled", ["error"] = "the element is not interactable - a real click would do nothing", ["row"] = Row(p) };
            }
            return null;
        }

        /// <summary>'/'-aligned suffix match, segment by segment, case-sensitive (GO names). A clipped
        /// segment from a row's p ("Name~" or "Name~[k]") matches any segment with that prefix (and [k]).</summary>
        internal static bool PathMatches(string full, string want)
        {
            if (full == null || string.IsNullOrEmpty(want)) return false;
            string[] f = full.Split('/'), w = want.Split('/');
            if (w.Length > f.Length) return false;
            for (int i = 1; i <= w.Length; i++)
                if (!SegMatches(f[f.Length - i], w[w.Length - i])) return false;
            return true;
        }

        private static bool SegMatches(string have, string want)
        {
            if (string.Equals(have, want, StringComparison.Ordinal)) return true;
            int tilde = want.IndexOf('~');
            if (tilde < 0) return false;
            string prefix = want.Substring(0, tilde), tail = want.Substring(tilde + 1);
            string haveTail = "";
            int br = have.LastIndexOf('[');
            if (tail.Length > 0) { if (br < 0) return false; haveTail = have.Substring(br); }
            else if (br > 0 && have.EndsWith("]", StringComparison.Ordinal) && Bracketed(have.Substring(br))) return false;
            return string.Equals(tail, haveTail, StringComparison.Ordinal) && have.StartsWith(prefix, StringComparison.Ordinal);
        }

        private static bool Bracketed(string s)
        {
            if (s.Length < 3 || s[0] != '[' || s[s.Length - 1] != ']') return false;
            for (int i = 1; i < s.Length - 1; i++) if (!char.IsDigit(s[i])) return false;
            return true;
        }

        /// <summary>A segment clipped to <see cref="SegClip"/> chars; a sibling suffix "[k]" survives.</summary>
        internal static string ClipSeg(string s)
        {
            string tail = "";
            int br = s.LastIndexOf('[');
            if (br > 0 && Bracketed(s.Substring(br))) { tail = s.Substring(br); s = s.Substring(0, br); }
            return (s.Length > SegClip ? s.Substring(0, SegClip) + "~" : s) + tail;
        }

        /// <summary>The row's p: the last <see cref="PathSegs"/> segments, each clipped; "~/" = more above.</summary>
        internal static string ShortPath(string full)
        {
            if (full == null) return "";
            string[] segs = full.Split('/');
            int from = Math.Max(0, segs.Length - PathSegs);
            StringBuilder sb = new StringBuilder();
            if (from > 0) sb.Append("~/");
            for (int i = from; i < segs.Length; i++)
            {
                if (i > from) sb.Append('/');
                sb.Append(ClipSeg(segs[i]));
            }
            return sb.ToString();
        }

        /// <summary>A pasted p back to a matchable suffix: drops the "~/" lead.</summary>
        internal static string StripShort(string p)
        {
            return p.StartsWith("~/", StringComparison.Ordinal) ? p.Substring(2) : p;
        }

        private static string LastSeg(string p)
        {
            if (p == null) return "";
            int s = p.LastIndexOf('/');
            string seg = s < 0 ? p : p.Substring(s + 1);
            int br = seg.IndexOf('[');
            return (br > 0 ? seg.Substring(0, br) : seg).TrimEnd('~');
        }

        /// <summary>Visible text as a human reads it: rich-text tags gone, whitespace collapsed.</summary>
        internal static string CleanText(string s)
        {
            if (s == null) return null;
            StringBuilder sb = new StringBuilder(Math.Min(s.Length, 256));
            bool tag = false, space = false;
            foreach (char c in s)
            {
                if (c == '<') { tag = true; continue; }
                if (tag) { if (c == '>') tag = false; continue; }
                if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
                if (space) { sb.Append(' '); space = false; }
                sb.Append(c);
                if (sb.Length > 200) break;
            }
            return sb.ToString();
        }

        private static string Norm(string s) { return CleanText(s) ?? ""; }

        internal static JObject Row(UiNode n)
        {
            string l = n.Label ?? "";
            JObject o = new JObject { ["l"] = l.Length > LabelClip ? l.Substring(0, LabelClip) + "~" : l, ["p"] = ShortPath(n.Path), ["t"] = n.Type };
            if (!n.Interactable) o["dis"] = true;
            if (!n.Visible) o["hid"] = true;
            o["r"] = new JArray(n.X, n.Y, n.W, n.H);
            return o;
        }

        internal static string Echo(string s)
        {
            if (s == null) return "";
            return s.Length > EchoClip ? s.Substring(0, EchoClip) + "~" : s;
        }

        private static string Str(JObject o, string key, out string value)
        {
            value = null;
            JToken t = o[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type != JTokenType.String || ((string)t).Length == 0) return key + " must be a non-empty string";
            value = (string)t;
            return null;
        }

        private static string Bool(JObject o, string key, out bool value)
        {
            value = false;
            JToken t = o[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type != JTokenType.Boolean) return key + " must be true or false";
            value = (bool)t;
            return null;
        }

        /// <summary>The click already happened; this only lets the UI react for waitFrames frames.
        /// Cancel does not undo it - the reply says so.</summary>
        internal sealed class Pending : IPending
        {
            private readonly JObject reply;
            private readonly int wait, start;
            private bool done;

            internal Pending(JObject reply, int wait, int start) { this.reply = reply; this.wait = wait; this.start = start; }

            public object Tick(bool cancelled)
            {
                if (done) return Bad("ui", "request already ended");
                int now = FrameNow == null ? int.MaxValue : FrameNow();
                if (cancelled) { done = true; return new { ok = false, code = "cancelled", clicked = (string)reply["clicked"], error = "cancelled while waiting frames - the click already happened and is NOT undone" }; }
                if (now != int.MaxValue && now - start < wait) return null;
                done = true;
                reply["frames"] = now == int.MaxValue ? wait : now - start;
                return reply;
            }
        }
    }
}
