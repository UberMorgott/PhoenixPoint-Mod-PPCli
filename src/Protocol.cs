using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Morgott.PPBridge
{
    /// <summary>One request, from the job file or from the pipe.</summary>
    internal sealed class Job
    {
        internal string Id;
        internal string Verb;
        internal JObject Args;

        // --- pipe path only; all null/default on the file path.
        /// <summary>Per-request completion. Never one shared event: two callers would wake on each
        /// other's result. Signalled in a finally on the main thread.</summary>
        internal ManualResetEventSlim Done;
        internal object Result;
        /// <summary>Realtime UTC after which the main thread refuses to start this job at all.</summary>
        internal DateTime Deadline;
        internal volatile bool Cancelled;
        /// <summary>When the result was produced - the only thing the job table prunes on.</summary>
        internal DateTime FinishedUtc;

        /// <summary>Signals the waiter exactly once, whatever happened. Safe to call twice.</summary>
        internal void Complete(object result)
        {
            Result = result;
            FinishedUtc = DateTime.UtcNow;
            if (Done != null) Done.Set();
        }
    }

    /// <summary>
    /// The half of PPBridge that touches NO game and NO Unity type: parse a job file, dispatch a
    /// verb, format a marker line. That is exactly the part an offline self-check can run, and it is
    /// why the two game-touching verbs arrive as delegates the game half installs at OnModEnabled
    /// rather than as direct calls.
    ///
    /// Wire format of the job file is a bare JSON array:
    ///   [ {"id":"1","verb":"ping"},
    ///     {"id":"2","verb":"console","args":{"command":"ct_version","args":[]}} ]
    /// </summary>
    internal static class Protocol
    {
        internal const string Version = "ppcli/1";

        // Trust boundary: the job file is written by a client we do not control, and every one of
        // these caps exists so a malformed or hostile file costs a refusal instead of the process.
        internal const int MaxFileBytes = 256 * 1024;
        internal const int MaxJobs = 256;
        /// <summary>What one console run may CAPTURE. Paged out by <see cref="ConsolePager"/>, so this
        /// is a memory bound on the snapshot, not the size of a reply.</summary>
        internal const int MaxOutputLines = 20000;
        internal const int MaxOutputLineChars = 2000;
        /// <summary>Captured characters per run, the second half of the memory bound (~2 MB UTF-16).</summary>
        internal const int MaxCaptureChars = 1024 * 1024;
        private static readonly Regex IdOk = new Regex("^[A-Za-z0-9_.-]{1,64}$");

        /// <summary>Installed by the game half. Null offline, which is what the self-check exercises.</summary>
        internal static Func<object> StateProbe;

        /// <summary>Installed by the game half: (command, args) -> result DTO.</summary>
        internal static Func<string, string[], object> ConsoleRun;

        /// <summary>
        /// Installed by the game half: (name, valueOrNull) -> result DTO. The console's SECOND
        /// surface - <c>ConsoleVariableAttribute</c> registers static fields and properties, and
        /// <see cref="ConsoleRun"/> structurally cannot reach any of them (it only ever sees the
        /// command list, while variables live in CommandLineParser's own path).
        /// </summary>
        internal static Func<string, string, object> VarRun;

        /// <summary>Reported by <c>ping</c>; set from the loaded DLL's SHA-1 at enable.</summary>
        internal static string BuildStamp = "offline";

        // --- P2 hooks. Reflect.cs is pure managed reflection and names no Unity or game type; these
        // four delegates are the whole of what the game half has to supply for it to work, and their
        // being delegates is what lets the offline self-check run the binder with no game at all.

        /// <summary>Named late-bound aliases, re-evaluated on EVERY request (never cached).</summary>
        internal static Func<Dictionary<string, object>> RootsProbe;

        /// <summary>Indexed def lookup, i.e. <c>DefRepository.GetDef(guid)</c>.</summary>
        internal static Func<string, object> DefByGuid;

        /// <summary>Every def in the repository, for <c>find</c>'s name scan.</summary>
        internal static Func<System.Collections.IEnumerable> AllDefs;

        /// <summary>
        /// Unity's null semantics for a handle target: a destroyed UnityEngine.Object is a live
        /// managed reference that throws on nearly every use, and only the game half can say so.
        /// </summary>
        internal static Func<object, bool> UnityAlive;

        // --- P3 hooks. Everything temporal EXCEPT the save is expressible as other verbs, so these
        // two are the whole game-side surface the plan engine needs.

        /// <summary>
        /// Starts a snapshot and hands back a poll: null while the save is still running, a result
        /// DTO when it is done. A poll rather than a callback because the result must be produced on
        /// the main thread's own tick, exactly like every other verb.
        /// </summary>
        internal static Func<string, Func<object>> SnapshotStart;

        /// <summary>
        /// Does a savegame by this name exist? Synchronous by way of
        /// <c>PhoenixSaveManager.EnsureUnique</c> (:154-168), which consults the already-loaded save
        /// dictionary and returns a DIFFERENT name when the given one is taken - the only public
        /// non-coroutine existence test the save manager has.
        /// </summary>
        internal static Func<string, bool> SaveExists;

        /// <summary>
        /// Installed by the game half: args -> an <see cref="IPending"/> that captures the framebuffer
        /// at end of frame, or an error DTO. A delegate for the usual reason - Screenshot.cs names
        /// Unity types and this file must stay compilable with no game at all.
        /// </summary>
        internal static Func<JObject, object> CaptureRun;

        /// <summary><c>screenshot</c> mode "backbuffer": a plain end-of-frame ReadPixels of the screen
        /// with <c>RenderTexture.active = null</c> - the frame that is presented, whatever cameras and
        /// blits (an upscaler's, a mod's marker pass) produced it (ISSUES 2026-09-07).</summary>
        internal const string ShotBackbuffer = "backbuffer";
        /// <summary><c>screenshot</c> mode "capture": ScreenCapture.CaptureScreenshotAsTexture, plus
        /// the Camera.main targetTexture as <c>scenePath</c> when an upscaler renders into one.</summary>
        internal const string ShotCapture = "capture";
        internal const string ShotDefault = ShotBackbuffer;

        /// <summary>The screenshot mode asked for, or the default. A mode that is not a known string is
        /// refused (code "args") rather than silently mapped to the default: a caller comparing two
        /// modes must never get the same capture twice without being told.</summary>
        internal static string ShotMode(JObject a, out string mode)
        {
            mode = ShotDefault;
            JToken t = a == null ? null : a["mode"];
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type != JTokenType.String) return "screenshot's \"mode\" must be \"" + ShotBackbuffer + "\" or \"" + ShotCapture + "\"";
            string m = (string)t;
            if (m != ShotBackbuffer && m != ShotCapture) return "screenshot's \"mode\" must be \"" + ShotBackbuffer + "\" or \"" + ShotCapture + "\", not \"" + m + "\"";
            mode = m;
            return null;
        }

        /// <summary>
        /// Never throws: a bad file yields an empty list and a named reason, because a parse failure
        /// that reached the caller as an exception would kill the run instead of reporting it.
        /// </summary>
        internal static List<Job> Parse(string json, out string error)
        {
            error = null;
            List<Job> jobs = new List<Job>();
            if (json == null || json.Length == 0) { error = "empty job file"; return jobs; }
            if (json.Length > MaxFileBytes) { error = "job file over " + MaxFileBytes + " bytes"; return jobs; }

            JArray arr;
            try { arr = JArray.Parse(json); }
            catch (Exception ex) { error = "not a JSON array: " + ex.Message; return jobs; }

            foreach (JToken t in arr)
            {
                if (jobs.Count >= MaxJobs) { error = "more than " + MaxJobs + " jobs, the rest were dropped"; break; }
                JObject o = t as JObject;
                if (o == null) { error = "a job entry is not an object"; continue; }
                string id = (string)o["id"];
                string verb = (string)o["verb"];
                // The id goes into a pipe-delimited marker line verbatim, so it is validated here
                // and nowhere else - a '|' or a newline in it would forge a second result.
                if (id == null || !IdOk.IsMatch(id)) { error = "a job has a missing or illegal id"; continue; }
                if (string.IsNullOrEmpty(verb)) { error = "job '" + id + "' has no verb"; continue; }
                jobs.Add(new Job { Id = id, Verb = verb, Args = o["args"] as JObject });
            }
            return jobs;
        }

        /// <summary>
        /// Why the main thread should NOT run this job, or null to run it. Lives here rather than in
        /// the Unity half so that `cancel` and the deadline are provable offline - the two things
        /// that decide whether the cancel verb is real or decorative.
        /// </summary>
        internal static object Refusal(Job job)
        {
            if (job.Cancelled) return Fail("cancelled before it started");
            if (job.Done != null && DateTime.UtcNow > job.Deadline) return Fail("deadline passed before the main thread reached it");
            return null;
        }

        /// <summary>Always returns a DTO; a throwing verb becomes an error DTO, never an exception.</summary>
        internal static object Dispatch(Job job)
        {
            try
            {
                switch (job.Verb)
                {
                    case "ping":
                        return new { ok = true, protocol = Version, build = BuildStamp };
                    case "state":
                        return StateProbe == null ? Fail("no state probe installed") : StateProbe();
                    case "console":
                        return Console(job.Args);
                    case "var":
                        return Var(job.Args);
                    // Cross-frame: the result is an IPending the Runner ticks until the PNG is on
                    // disk, so the client never gets a path to a file that is not written yet.
                    case "screenshot":
                    {
                        string shotMode;
                        string shotErr = ShotMode(job.Args, out shotMode);
                        if (shotErr != null) return new { ok = false, code = "args", error = shotErr };
                        return CaptureRun == null ? Fail("no screenshot capture installed") : CaptureRun(job.Args);
                    }
                    default:
                        // P2's verbs live in Reflect and P3's in Plan; both answer null for anything
                        // they do not own, so an unknown verb still gets the same refusal it always
                        // did. Plan goes first because a cross-frame verb may return an IPending
                        // rather than a DTO, and only the Runner knows what to do with one.
                        return Plan.Dispatch(job.Verb, job.Args)
                               ?? Shots.Dispatch(job.Verb, job.Args)
                               ?? LogTap.Dispatch(job.Verb, job.Args)
                               ?? EventTap.Dispatch(job.Verb, job.Args)
                               ?? ImGuiTap.Dispatch(job.Verb, job.Args)
                               ?? ActTap.Dispatch(job.Verb, job.Args)
                               ?? UiTap.Dispatch(job.Verb, job.Args)
                               ?? Reflect.Dispatch(job.Verb, job.Args)
                               ?? Fail("unknown verb '" + job.Verb + "'");
                }
            }
            catch (Exception ex) { return Fail(ex.GetType().Name + ": " + ex.Message); }
        }

        private static object Console(JObject args)
        {
            if (ConsoleRun == null) return Fail("no console runner installed");
            string command = args == null ? null : (string)args["command"];
            JToken cursor = args == null ? null : args["cursor"];
            bool haveCursor = cursor != null && cursor.Type != JTokenType.Null;
            if (haveCursor && !string.IsNullOrEmpty(command))
                return new { ok = false, code = "args", error = "console takes {command,...} OR {cursor}, not both - a cursor pages a run that already happened" };
            int lines, bytes;
            object refusal = ConsolePager.PageArgs(args, out lines, out bytes);
            if (refusal != null) return refusal;
            if (haveCursor) return ConsolePager.Next(cursor.Type == JTokenType.String ? (string)cursor : null, lines, bytes);
            if (string.IsNullOrEmpty(command)) return Fail("console needs {command, args[]}, or {cursor} for the next page");
            List<string> list = new List<string>();
            JArray a = args["args"] as JArray;
            if (a != null) foreach (JToken t in a) list.Add(t == null || t.Type == JTokenType.Null ? "" : t.ToString());
            return ConsolePager.First(ConsoleRun(command, list.ToArray()), lines, bytes);
        }

        /// <summary>
        /// Get with {name}, set-then-read-back with {name, value}. Values are strings in BOTH
        /// directions - that is the game's own contract (ConsoleVariableAttribute.GetValue returns
        /// ToString(), SetValue parses through Helper.TypeToConvertFunc) - so a JSON true becomes
        /// "True" here rather than being refused.
        /// </summary>
        private static object Var(JObject args)
        {
            if (VarRun == null) return Fail("no variable runner installed");
            string name = args == null ? null : (string)args["name"];
            if (string.IsNullOrEmpty(name)) return Fail("var needs {name}, plus {value} to set it");
            JToken v = args["value"];
            return VarRun(name, v == null || v.Type == JTokenType.Null ? null : v.ToString());
        }

        internal static object Fail(string message)
        {
            return new { ok = false, error = message };
        }

        /// <summary>
        /// The one thing the client greps. Single line by construction: Newtonsoft's default
        /// Formatting.None emits no newline, and any newline inside a string value is escaped to
        /// \n by the JSON writer itself.
        /// </summary>
        internal static string Marker(string id, object payload)
        {
            return "PPCLI|" + id + "|" + Compact(payload);
        }

        /// <summary>
        /// Freezes a verb's result into plain JSON while still on the main thread. The pipe thread
        /// then only ever copies bytes - it can never reach a live game object through a lazy getter,
        /// which is the whole reason this exists rather than handing the object over directly. JRaw
        /// embeds verbatim, so the response is not double-encoded.
        /// </summary>
        internal static object Reproject(object result)
        {
            return new JRaw(Compact(result));
        }

        /// <summary>One line of compact JSON, or a compact error saying why there is not one.</summary>
        internal static string Compact(object payload)
        {
            try { return JsonConvert.SerializeObject(payload); }
            catch (Exception ex) { return JsonConvert.SerializeObject(Fail("unserializable result: " + ex.Message)); }
        }

        /// <summary>Clip captured console output to what a log line and a client can carry.</summary>
        internal static string Clip(string line)
        {
            if (line == null) return "";
            line = line.Replace("\r", "");
            return line.Length > MaxOutputLineChars ? line.Substring(0, MaxOutputLineChars) + ClipMark : line;
        }

        internal const string ClipMark = " ...(clipped)";

        /// <summary>
        /// STRICT integer arg, shared by every paged verb. Absent/null = <paramref name="def"/>; a
        /// JSON integer inside the target range = its value; anything else - "2", true, 1.5, a
        /// number past Int32/Int64 - is an error message (caller refuses with code:"args"). A plain
        /// (int)JToken cast accepts all three of those, rounding 1.5 to 2.
        /// </summary>
        internal static string LongArg(JObject a, string key, long def, out long value)
        {
            value = def;
            JToken t = a == null ? null : a[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type != JTokenType.Integer || !(((JValue)t).Value is long || ((JValue)t).Value is int))
                return key + " must be a JSON integer (not a string, boolean or fraction)";
            value = Convert.ToInt64(((JValue)t).Value, System.Globalization.CultureInfo.InvariantCulture);
            return null;
        }

        internal static string IntArg(JObject a, string key, int def, out int value)
        {
            value = def;
            long l;
            string err = LongArg(a, key, def, out l);
            if (err != null) return err;
            if (l < int.MinValue || l > int.MaxValue) return key + " is out of range";
            value = (int)l;
            return null;
        }
    }

    /// <summary>
    /// Console output, paged. A command RUNS ONCE; its captured lines become a snapshot and the
    /// reply carries the first page plus an opaque <c>cursor</c> for the rest. A cursor read never
    /// re-runs the command - `give_item` twice is not "the next page" - so an expired or unknown
    /// cursor is a refusal (<c>code:"cursor"</c>), never a silent re-run.
    ///
    /// Bounded three ways, because a snapshot is memory the game process holds for a caller that may
    /// never come back: at most <see cref="MaxSnapshots"/> live at once (oldest evicted), each dies
    /// <see cref="TtlSeconds"/> after its last read, and all of them together stay under
    /// <see cref="MaxStoreChars"/>. A page is limited by lines AND by the UTF-8 bytes of the WHOLE
    /// reply JSON (the pipe frame is 256 KiB, Wire.cs:17), and always carries at least one line so
    /// paging can never stall: a line that alone overflows the budget is clipped to fit, and every
    /// page reports `clipped:N` = lines on it not shown whole (2000-char capture clip or page fit).
    /// The run's own fields (ok, error, code, truncated) ride on EVERY page, so a caller checking
    /// only the last page still sees a failed run. A cursor is a server-held token (offset never in
    /// the string), so it cannot be edited into a skip, a repeat or an empty page.
    /// Main thread only, like every verb.
    /// </summary>
    internal static class ConsolePager
    {
        internal const int DefaultPageLines = 50;
        internal const int MaxPageLines = 2000;
        internal const int DefaultPageBytes = 8 * 1024;
        internal const int MinPageBytes = 1024;
        /// <summary>192 KiB: the rest of the 256 KiB frame is headroom for the envelope.</summary>
        internal const int MaxPageBytes = 192 * 1024;
        internal const int TtlSeconds = 120;
        internal const int MaxSnapshots = 4;
        internal const long MaxStoreChars = 4L * 1024 * 1024;

        /// <summary>The clock. A field so the offline check can expire a cursor without sleeping.</summary>
        internal static Func<DateTime> Now = () => DateTime.UtcNow;

        private sealed class Snap
        {
            internal string[] Lines;
            internal long Chars;
            internal DateTime Touched;
            /// <summary>The run's own fields minus output: repeated on every page.</summary>
            internal JObject Meta;
            /// <summary>Issued cursor token -> offset. Only the server knows the offset.</summary>
            internal readonly Dictionary<string, int> Cursors = new Dictionary<string, int>();
            internal readonly Dictionary<int, string> Tokens = new Dictionary<int, string>();
        }

        private static readonly List<Snap> snaps = new List<Snap>();
        private static readonly Random rng = new Random();

        internal static int Live { get { Prune(); return snaps.Count; } }

        internal static void Reset() { snaps.Clear(); }

        internal static object PageArgs(JObject a, out int lines, out int bytes)
        {
            string e1 = Protocol.IntArg(a, "pageLines", DefaultPageLines, out lines);
            string e2 = Protocol.IntArg(a, "pageBytes", DefaultPageBytes, out bytes);
            if (e1 != null || e2 != null) return new { ok = false, code = "args", error = e1 ?? e2 };
            if (lines < 1 || lines > MaxPageLines)
                return new { ok = false, code = "args", error = "pageLines must be 1.." + MaxPageLines };
            if (bytes < MinPageBytes || bytes > MaxPageBytes)
                return new { ok = false, code = "args", error = "pageBytes must be " + MinPageBytes + ".." + MaxPageBytes };
            return null;
        }

        /// <summary>The first page of a run that just happened. Every other field of the runner's
        /// DTO (ok, error, code, truncated) is kept - on this page AND every later one; only
        /// `output` is paged.</summary>
        internal static object First(object result, int pageLines, int pageBytes)
        {
            JObject dto;
            try { dto = JObject.FromObject(result); }
            catch (Exception) { return result; }
            JArray output = dto["output"] as JArray;
            if (output == null) return dto;                       // a refusal with no output at all

            string[] lines = new string[output.Count];
            long chars = 0;
            for (int i = 0; i < lines.Length; i++) { lines[i] = (string)output[i] ?? ""; chars += lines[i].Length; }
            dto.Remove("output");
            // `truncated` now means one thing only: the CAPTURE hit its bound and lines were lost.
            // "There is more to read" is `hasMore`, and a false truncated is left out.
            JToken tr = dto["truncated"];
            if (tr != null && tr.Type == JTokenType.Boolean && !(bool)tr) dto.Remove("truncated");
            // A thrown command's message is not bounded by the capture; one line's worth is.
            if (dto["error"] != null && dto["error"].Type == JTokenType.String) dto["error"] = Protocol.Clip((string)dto["error"]);

            return Page(dto, lines, chars, null, 0, pageLines, pageBytes);
        }

        /// <summary>The page after a cursor. Reading the last page frees the snapshot.</summary>
        internal static object Next(string cursor, int pageLines, int pageBytes)
        {
            Prune();
            int offset = 0;
            Snap s = cursor == null ? null : snaps.Find(x => x.Cursors.TryGetValue(cursor, out offset));
            if (s == null)
                return new
                {
                    ok = false, code = "cursor",
                    error = "console cursor '" + Protocol.Clip(cursor) + "' is unknown or expired - pass the `cursor` string a console " +
                            "reply handed back, unchanged. A snapshot lives " + TtlSeconds + " s after its last read and at most " +
                            MaxSnapshots + " are kept. Nothing was re-run; run the command again if you still want its output"
                };
            s.Touched = Now();
            return Page(s.Meta, s.Lines, s.Chars, s, offset, pageLines, pageBytes);
        }

        /// <summary>
        /// One page: the run's fields + as many lines from <paramref name="from"/> as fit pageLines
        /// and pageBytes of the WHOLE reply. The envelope is measured first with every optional
        /// key at its widest (hasMore:false, a cursor, clipped), so the real reply is never larger.
        /// Always at least one line: one that alone overflows is clipped to fit and counted.
        /// </summary>
        private static object Page(JObject meta, string[] lines, long chars, Snap s, int from, int pageLines, int pageBytes)
        {
            JObject dto = (JObject)meta.DeepClone();
            // The error is repeated on every page, so it gets at most a quarter of every page.
            if (dto["error"] != null && dto["error"].Type == JTokenType.String)
            {
                bool cut;
                dto["error"] = ClipJson((string)dto["error"], pageBytes / 4, out cut);
            }
            dto["output"] = new JArray();
            if (s != null) dto["offset"] = lines.Length;
            dto["total"] = lines.Length;
            dto["hasMore"] = false;
            dto["cursor"] = new string('f', TokenChars);
            dto["clipped"] = MaxPageLines;
            long budget = pageBytes - Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(dto));

            JArray page = new JArray();
            int clipped = 0;
            long used = 0;
            int i = from;
            for (; i < lines.Length && page.Count < pageLines; i++)
            {
                long comma = page.Count == 0 ? 0 : 1;
                string line = lines[i];
                long cost = JsonBytes(line) + comma;
                if (used + cost > budget)
                {
                    if (page.Count > 0) break;
                    bool cut;
                    line = ClipJson(line, budget, out cut);   // alone too big: fit it, never skip the budget
                    cost = JsonBytes(line);
                    clipped++;
                    page.Add(line);
                    used += cost;
                    i++;
                    break;
                }
                if (IsCaptureClipped(line)) clipped++;
                page.Add(line);
                used += cost;
            }

            dto["output"] = page;
            if (s != null) dto["offset"] = from;
            bool more = i < lines.Length;
            dto["hasMore"] = more;
            dto.Remove("cursor");
            dto.Remove("clipped");
            if (clipped > 0) dto["clipped"] = clipped;
            if (more)
            {
                if (s == null) { s = Store(lines, chars, meta); }
                // Same offset, same token: a retried read answers the identical page + cursor.
                string token;
                if (!s.Tokens.TryGetValue(i, out token)) { token = NewToken(); s.Tokens[i] = token; s.Cursors[token] = i; }
                dto["cursor"] = token;
            }
            else if (s != null) snaps.Remove(s);
            return dto;
        }

        /// <summary>The 2000-char capture clip (Protocol.Clip) leaves this exact shape behind.</summary>
        private static bool IsCaptureClipped(string line)
        {
            return line.Length == Protocol.MaxOutputLineChars + Protocol.ClipMark.Length && line.EndsWith(Protocol.ClipMark, StringComparison.Ordinal);
        }

        private static long JsonBytes(string s) { return Encoding.UTF8.GetByteCount(JsonConvert.ToString(s)); }

        /// <summary>The longest prefix of <paramref name="s"/> + ClipMark whose serialised JSON
        /// string is at most <paramref name="maxBytes"/> UTF-8 bytes. Never splits a surrogate pair.</summary>
        internal static string ClipJson(string s, long maxBytes, out bool cut)
        {
            cut = false;
            if (s == null || JsonBytes(s) <= maxBytes) return s;
            cut = true;
            int lo = 0, hi = s.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (JsonBytes(s.Substring(0, mid) + Protocol.ClipMark) <= maxBytes) lo = mid; else hi = mid - 1;
            }
            if (lo > 0 && char.IsHighSurrogate(s[lo - 1])) lo--;
            return lo == 0 && JsonBytes(Protocol.ClipMark) > maxBytes ? "" : s.Substring(0, lo) + Protocol.ClipMark;
        }

        /// <summary>24 hex chars of randomness: guessable neither as an offset nor as another snapshot.</summary>
        private const int TokenChars = 24;

        private static string NewToken()
        {
            byte[] b = new byte[TokenChars / 2];
            lock (rng) rng.NextBytes(b);
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }

        private static Snap Store(string[] lines, long chars, JObject meta)
        {
            Prune();
            while (snaps.Count > 0 && (snaps.Count >= MaxSnapshots || Total() + chars > MaxStoreChars))
                snaps.RemoveAt(0);                                    // oldest first; they are kept in creation order
            Snap s = new Snap { Lines = lines, Chars = chars, Touched = Now(), Meta = meta };
            snaps.Add(s);
            return s;
        }

        private static long Total()
        {
            long t = 0;
            foreach (Snap s in snaps) t += s.Chars;
            return t;
        }

        private static void Prune()
        {
            DateTime cut = Now().AddSeconds(-TtlSeconds);
            snaps.RemoveAll(s => s.Touched < cut);
        }

    }
}
