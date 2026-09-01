using System;
using System.Collections.Generic;
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
        internal const int MaxOutputLines = 200;
        internal const int MaxOutputLineChars = 2000;
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
                        return CaptureRun == null ? Fail("no screenshot capture installed") : CaptureRun(job.Args);
                    default:
                        // P2's verbs live in Reflect and P3's in Plan; both answer null for anything
                        // they do not own, so an unknown verb still gets the same refusal it always
                        // did. Plan goes first because a cross-frame verb may return an IPending
                        // rather than a DTO, and only the Runner knows what to do with one.
                        return Plan.Dispatch(job.Verb, job.Args)
                               ?? Shots.Dispatch(job.Verb, job.Args)
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
            if (string.IsNullOrEmpty(command)) return Fail("console needs {command, args[]}");
            List<string> list = new List<string>();
            JArray a = args["args"] as JArray;
            if (a != null) foreach (JToken t in a) list.Add(t == null || t.Type == JTokenType.Null ? "" : t.ToString());
            return ConsoleRun(command, list.ToArray());
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
            return line.Length > MaxOutputLineChars ? line.Substring(0, MaxOutputLineChars) + " ...(clipped)" : line;
        }
    }
}
