using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Morgott.PPBridge
{
    /// <summary>
    /// The one runnable check for PPBridge's pure half: job-file JSON -> dispatch -> marker line.
    /// No game, no Unity, no test framework - it compiles src\Protocol.cs directly and asserts on it,
    /// which is possible only because the two game-touching verbs arrive as delegates.
    /// </summary>
    // --- P2 fixtures. Real types in a real loaded assembly, because Reflect resolves and binds
    // against the running AppDomain and a mock would prove nothing about that.
    internal enum Season { Winter, Summer }

    /// <summary>A stand-in for UnityEngine.Vector3: a struct with a three-float ctor and three
    /// public primitive fields, which is exactly what $v3 binding and inline projection key on.</summary>
    internal struct V3
    {
        public float x, y, z;
        public V3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }

    /// <summary>A stand-in for BaseDef: `name` property + `Guid` field, the two things find/$def read.</summary>
    internal class FakeDef
    {
        public string Guid;
        public string name { get; set; }
    }

    internal class Ov
    {
        // = 0 only to silence CS0649: this field is written by the binder, never by C# code here.
        public int Field = 0;
        public string Prop { get; set; }

        public static string M(int x) { return "int"; }
        public static string M(long x) { return "long"; }
        public static string M(string x) { return "string"; }
        public static string M(object x) { return "object"; }

        // Deliberately tied: (integer, integer) scores 3+1 either way round.
        public static string T(int a, object b) { return "int,object"; }
        public static string T(object a, int b) { return "object,int"; }

        // An OVERRIDE. It arrives at the scorer alongside Object.ToString() with the same signature
        // and the same score, which used to make every ToString() on an overriding type ambiguous.
        public override string ToString() { return "ov-tostring"; }

        public static string TakeOv(Ov o) { return o == null ? "null" : "ov"; }
        public static string TakeDef(FakeDef d) { return d.name; }
        public static string TakeSeason(Season s) { return s.ToString(); }
        // Invariant on purpose: this machine's culture writes 2,5 and the assertion is about binding.
        public static string TakeV3(V3 v)
        {
            return v.x.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" +
                   v.y.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" +
                   v.z.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        public static int TakeInts(int[] a) { return a.Length; }
        public static int TakeList(List<int> a) { return a.Count; }
        public static string TakeType(Type t) { return t.Name; }
        public static string Byref(ref int x) { return "ref"; }
        public static V3 MakeV3() { return new V3(1f, 2.5f, 3f); }
        public static void Nothing() { }
        public static T Generic<T>(T x) { return x; }

        // --- P3 fixtures.
        public static int Counter;
        public static int Bump() { return ++Counter; }
        /// <summary>A real compiler-generated iterator: it does NOT know its own size, which is the
        /// exact shape (TacticalMap+&lt;GetTacActors&gt;d__61) that used to project an empty count.</summary>
        public static IEnumerable<int> Lazy() { yield return 1; yield return 2; yield return 3; }
        public static List<string> Fat()
        {
            List<string> big = new List<string>();
            for (int i = 0; i < 200; i++) big.Add(new string('q', 4000));
            return big;
        }
    }

    internal static class SelfCheck
    {
        private static int failures;

        private static void Check(string name, bool ok, string detail)
        {
            if (!ok) { failures++; Console.WriteLine("FAIL " + name + ": " + detail); }
        }

        /// <summary>Feed raw bytes through the reader exactly as a pipe would deliver them.</summary>
        private static string ReadBack(byte[] frame) { string e; return ReadBack(frame, out e); }

        private static string ReadBack(byte[] frame, out string error)
        {
            using (MemoryStream ms = new MemoryStream(frame)) return Wire.Read(ms, out error);
        }

        /// <summary>
        /// Runs the REAL PipeServer and talks to it over a real named pipe. The previous version of
        /// this file passed while the endpoint was dead in-game, because it only ever tested pure
        /// functions - nothing here had ever opened a pipe.
        ///
        /// Honest limit: this runs on .NET 8, and what actually broke P1 was a Mono-only gap
        /// (WindowsIdentity.User is unimplemented in the game's mscorlib). No offline check on this
        /// runtime can see that. The in-game counterpart is PipeServer.SelfTest, which connects to
        /// its own pipe at startup and logs PPCLI FAILURE if it cannot.
        /// </summary>
        private static void PipeChecks()
        {
            // "park" stands in for a verb that outlives its call: it is never completed, which is the
            // only way to reach the accepted / status / cancel path. Everything else finishes inline.
            List<Job> parked = new List<Job>();
            List<string> log = new List<string>();
            PipeServer server = new PipeServer(job =>
                                               {
                                                   if (job.Verb == "park") { lock (parked) parked.Add(job); return true; }
                                                   job.Complete(new { ok = true, echoed = job.Verb });
                                                   return true;
                                               },
                                               msg => { lock (log) log.Add(msg); });
            try
            {
                server.Start(@"C:\SelfCheckInstall");

                // The client's real discovery path: everything it needs comes out of this file.
                string epFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                             "ppcli", "endpoints", Process.GetCurrentProcess().Id + ".json");
                Check("pipe-endpoint-written", File.Exists(epFile), epFile + " missing");
                if (!File.Exists(epFile)) return;
                JObject ep = JObject.Parse(File.ReadAllText(epFile));
                string pipe = (string)ep["pipe"], token = (string)ep["token"];
                Check("pipe-endpoint-fields", !string.IsNullOrEmpty(pipe) && !string.IsNullOrEmpty(token) &&
                                              (string)ep["install"] == @"C:\SelfCheckInstall", ep.ToString());

                string good = Call(pipe, "{\"token\":\"" + token + "\",\"id\":\"s1\",\"verb\":\"ping\"}");
                Check("pipe-accepts-a-connection", good != null && good.Contains("\"status\":\"done\""), "" + good);
                Check("pipe-result-embeds-raw", good != null && good.Contains("\"result\":{\"ok\":true"), "" + good);

                string bad = Call(pipe, "{\"token\":\"not-the-token\",\"id\":\"s2\",\"verb\":\"ping\"}");
                Check("pipe-refuses-bad-token", bad != null && bad.Contains("bad or missing session token"), "" + bad);
                Check("pipe-bad-token-runs-nothing", bad != null && !bad.Contains("echoed"), "" + bad);

                // An oversized frame must come back as an answer, not as a dropped connection.
                string oversize = CallRaw(pipe, Oversize());
                Check("pipe-answers-oversized-frame", oversize != null && oversize.Contains("outside 1.."), "" + oversize);

                // ...and the server must still be there afterwards.
                string after = Call(pipe, "{\"token\":\"" + token + "\",\"id\":\"s3\",\"verb\":\"state\"}");
                Check("pipe-survives-a-bad-frame", after != null && after.Contains("\"status\":\"done\""), "" + after);

                // The one fragile part of the ERROR_PIPE_CONNECTED recovery: it reaches a protected
                // setter by name. If that lookup ever stops resolving, the race becomes an accept
                // failure - so run the real method, on a real unconnected server stream.
                using (NamedPipeServerStream probe = new NamedPipeServerStream("ppcli-selfcheck-markconnected-" + Process.GetCurrentProcess().Id,
                                                                               PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None, 1024, 1024))
                {
                    Check("pipe-markconnected-works", PipeServer.MarkConnected(probe) && probe.IsConnected,
                          "the ERROR_PIPE_CONNECTED recovery cannot set IsConnected");
                }

                // The self-test must be true in BOTH directions. It once fired before CreateNamedPipe
                // had returned and reported a failure that was not real, which is no better than the
                // silent success it replaced.
                string selftest = null;
                for (int i = 0; i < 100 && selftest == null; i++)
                {
                    lock (log) selftest = log.Find(m => m.Contains("self-test"));
                    if (selftest == null) Thread.Sleep(50);
                }
                Check("selftest-ran", selftest != null, "no self-test line in 5s");
                Check("selftest-green", selftest != null && selftest.Contains("self-test OK"), "" + selftest);
                string failure;
                lock (log) failure = log.Find(m => m.Contains("FAILURE"));
                Check("selftest-no-false-alarm", failure == null, "" + failure);
                string listening;
                lock (log) listening = log.Find(m => m.Contains("listening"));
                Check("listening-announced", listening != null && listening.Contains("session token required"), "" + listening);

                CancelChecks(pipe, token, parked);

                server.Stop();
                Check("pipe-endpoint-removed-on-stop", !File.Exists(epFile), epFile + " survived Stop()");
            }
            catch (Exception ex) { Check("pipe-checks-threw", false, ex.ToString()); }
            finally { try { server.Stop(); } catch (Exception) { } }
        }

        /// <summary>
        /// Is `cancel` real or decorative? This follows one job all the way: accepted -> running ->
        /// cancel -> the flag actually set ON THE JOB -> the main thread's own decision to refuse it.
        /// The one thing it cannot prove is stopping a job already executing, which is a synchronous
        /// main-thread call that nothing can interrupt (documented at PipeServer.Cancel).
        /// </summary>
        private static void CancelChecks(string pipe, string token, List<Job> parked)
        {
            // Takes PipeServer.InlineWaitMs to come back by design: it is the "did not finish in
            // time" path, and there is no way to observe it without waiting for it.
            string accepted = Call(pipe, "{\"token\":\"" + token + "\",\"id\":\"p1\",\"verb\":\"park\"}");
            Check("job-accepted-when-slow", accepted != null && accepted.Contains("\"status\":\"accepted\""), "" + accepted);
            string jobId = accepted == null ? null : (string)JObject.Parse(accepted)["jobId"];
            if (jobId == null) { Check("job-has-an-id", false, "" + accepted); return; }

            string running = Call(pipe, "{\"token\":\"" + token + "\",\"id\":\"p2\",\"verb\":\"status\",\"args\":{\"jobId\":\"" + jobId + "\"}}");
            Check("job-status-running", running != null && running.Contains("\"status\":\"running\""), "" + running);

            string cancelled = Call(pipe, "{\"token\":\"" + token + "\",\"id\":\"p3\",\"verb\":\"cancel\",\"args\":{\"jobId\":\"" + jobId + "\"}}");
            Check("job-cancel-acknowledged", cancelled != null && cancelled.Contains("\"status\":\"cancelling\""), "" + cancelled);

            Job job;
            lock (parked) job = parked.Count > 0 ? parked[parked.Count - 1] : null;
            Check("job-cancel-reaches-the-job", job != null && job.Cancelled, "the flag never arrived");
            Check("job-cancel-is-obeyed", job != null && Protocol.Compact(Protocol.Refusal(job)).Contains("cancelled before it started"),
                  job == null ? "no job" : Protocol.Compact(Protocol.Refusal(job)));

            Check("job-cancel-unknown-id", Call(pipe, "{\"token\":\"" + token + "\",\"id\":\"p4\",\"verb\":\"cancel\",\"args\":{\"jobId\":\"nope\"}}").Contains("no such job"), "unknown job id accepted");

            // The deadline is the other refusal the main thread makes, and it is not on any timer:
            // it is checked when the job is finally reached.
            Check("job-deadline-refused",
                  Protocol.Compact(Protocol.Refusal(new Job { Done = new ManualResetEventSlim(false), Deadline = DateTime.UtcNow.AddSeconds(-1) })).Contains("deadline passed"),
                  "an expired job would still run");
            Check("job-healthy-runs", Protocol.Refusal(new Job { Done = new ManualResetEventSlim(false), Deadline = DateTime.UtcNow.AddMinutes(1) }) == null,
                  "a fine job was refused");
            // A file job has no Done and no deadline; it must never be refused for one.
            Check("file-job-has-no-deadline", Protocol.Refusal(new Job { Id = "f1", Verb = "ping" }) == null, "a batch job was refused for a deadline it never had");

            if (job != null) job.Complete(new { ok = true, finished = true });
            Check("job-status-collects-the-result",
                  Call(pipe, "{\"token\":\"" + token + "\",\"id\":\"p5\",\"verb\":\"status\",\"args\":{\"jobId\":\"" + jobId + "\"}}").Contains("\"finished\":true"),
                  "the finished result never came back");
        }

        // ------------------------------------------------------------------ P2: the reflection runtime

        private const string OvType = "Morgott.PPBridge.Ov";

        /// <summary>One verb, compacted exactly as the client would receive it.</summary>
        private static string R(string verb, string json)
        {
            return Protocol.Compact(Reflect.Dispatch(verb, JObject.Parse(json)));
        }

        private static string Handle(string json)
        {
            JToken h = JObject.Parse(R("call", json))["value"];
            return h == null ? null : (string)h["h"];
        }

        /// <summary>
        /// Everything the binder, the scorer, the handle table and the DTO caps do, against real
        /// types in this real AppDomain. No game is needed for any of it, which is the entire reason
        /// Reflect.cs names no Unity type and takes its four game facts as delegates.
        /// </summary>
        private static void ReflectChecks()
        {
            // --- overload selection: a unique lowest score, never reflection order.
            Check("overload-integer-prefers-long",
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'M','args':[5]}").Contains("\"value\":\"long\""),
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'M','args':[5]}"));
            Check("overload-string-prefers-string",
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'M','args':['x']}").Contains("\"value\":\"string\""),
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'M','args':['x']}"));
            Check("overload-falls-back-to-object",
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'M','args':[true]}").Contains("\"value\":\"object\""),
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'M','args':[true]}"));

            // A TIE is the case that must never be resolved by guessing.
            string tie = R("call", "{'op':'invoke','type':'" + OvType + "','member':'T','args':[1,1]}");
            Check("overload-tie-refuses", tie.Contains("\"code\":\"ambiguous\""), tie);
            Check("overload-tie-lists-candidates", tie.Contains("T(Int32 a, Object b)") && tie.Contains("T(Object a, Int32 b)"), tie);
            Check("overload-sig-breaks-the-tie",
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'T','args':[1,1],'sig':['Int32','Object']}").Contains("\"value\":\"int,object\""),
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'T','args':[1,1],'sig':['Int32','Object']}"));
            // An override is NOT a tie: the base declaration it hides loses. Wallet.ToString() vs
            // Object.ToString() is the live case - set-resources.json could not read its own wallet.
            string ovh = Handle("{'op':'new','type':'" + OvType + "','args':[]}");
            Check("override-beats-the-base-declaration",
                  R("call", "{'op':'invoke','target':'" + ovh + "','member':'ToString','args':[]}").Contains("\"value\":\"ov-tostring\""),
                  R("call", "{'op':'invoke','target':'" + ovh + "','member':'ToString','args':[]}"));
            Check("overload-sig-that-matches-nothing",
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'T','args':[1,1],'sig':['Single','Single']}").Contains("\"code\":\"overload\""),
                  "an impossible sig was accepted");
            Check("overload-nothing-binds",
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'TakeSeason','args':[true]}").Contains("\"code\":\"overload\""),
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'TakeSeason','args':[true]}"));
            Check("overload-wrong-arity",
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'TakeSeason','args':[1,2]}").Contains("takes 1 args"),
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'TakeSeason','args':[1,2]}"));

            // v1's two flat refusals.
            Check("byref-refused",
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'Byref','args':[1]}").Contains("by-ref"),
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'Byref','args':[1]}"));
            Check("open-generic-needs-typeargs",
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'Generic','args':['hi']}").Contains("typeArgs"),
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'Generic','args':['hi']}"));
            Check("closed-generic-runs",
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'Generic','typeArgs':['System.String'],'args':['hi']}").Contains("\"value\":\"hi\""),
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'Generic','typeArgs':['System.String'],'args':['hi']}"));

            // --- type resolution: ambiguity is an error, not a coin toss.
            Check("type-unknown", R("call", "{'op':'invoke','type':'No.Such.Type','member':'X','args':[]}").Contains("\"code\":\"type\""),
                  "an unknown type resolved");
            string ambiguous = R("members", "{'type':'Job'}");
            Check("type-ambiguous-or-unique", ambiguous.Contains("ambiguous") || ambiguous.Contains("\"ok\":true"), ambiguous);

            // --- argument envelopes.
            string ov = Handle("{'op':'new','type':'" + OvType + "','args':[]}");
            Check("new-returns-a-handle", ov != null && ov.StartsWith("h:"), "" + ov);
            Check("envelope-h",
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'TakeOv','args':[{'$h':'" + ov + "'}]}").Contains("\"value\":\"ov\""),
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'TakeOv','args':[{'$h':'" + ov + "'}]}"));
            Check("envelope-h-wrong-type",
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'TakeDef','args':[{'$h':'" + ov + "'}]}").Contains("\"code\":\"overload\""),
                  "a handle of the wrong type was bound anyway");
            Check("envelope-enum",
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'TakeSeason','args':[{'$enum':'Summer'}]}").Contains("\"value\":\"Summer\""),
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'TakeSeason','args':[{'$enum':'Summer'}]}"));
            Check("enum-bare-string",
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'TakeSeason','args':['Winter']}").Contains("\"value\":\"Winter\""),
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'TakeSeason','args':['Winter']}"));
            Check("enum-unknown-name-lists-values",
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'TakeSeason','args':['Autumn']}").Contains("Winter"),
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'TakeSeason','args':['Autumn']}"));
            Check("envelope-v3",
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'TakeV3','args':[{'$v3':[1,2.5,3]}]}").Contains("1|2.5|3"),
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'TakeV3','args':[{'$v3':[1,2.5,3]}]}"));
            Check("envelope-v3-wrong-arity",
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'TakeV3','args':[{'$v3':[1,2]}]}").Contains("expected 3"),
                  "a two-component $v3 was accepted");
            Check("array-bare",
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'TakeInts','args':[[1,2,3]]}").Contains("\"value\":3"),
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'TakeInts','args':[[1,2,3]]}"));
            Check("array-envelope-into-list",
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'TakeList','args':[{'$array':[1,2],'type':'System.Int32'}]}").Contains("\"value\":2"),
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'TakeList','args':[{'$array':[1,2],'type':'System.Int32'}]}"));
            Check("array-element-refused",
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'TakeInts','args':[[1,'x']]}").Contains("element 1"),
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'TakeInts','args':[[1,'x']]}"));
            Check("envelope-type",
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'TakeType','args':[{'$type':'System.String'}]}").Contains("\"value\":\"String\""),
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'TakeType','args':[{'$type':'System.String'}]}"));
            Check("envelope-unknown-tag",
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'TakeSeason','args':[{'$nope':1}]}").Contains("unknown envelope"),
                  "an unknown envelope was bound");
            Check("bare-object-is-not-an-envelope",
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'TakeSeason','args':[{'a':1}]}").Contains("tagged envelope"),
                  "a bare JSON object was bound");

            Protocol.DefByGuid = g => g == "g1" ? new FakeDef { Guid = "g1", name = "hello" } : null;
            Check("envelope-def",
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'TakeDef','args':[{'$def':'g1'}]}").Contains("\"value\":\"hello\""),
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'TakeDef','args':[{'$def':'g1'}]}"));
            Check("envelope-def-unknown",
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'TakeDef','args':[{'$def':'nope'}]}").Contains("no def with guid"),
                  "an unknown guid bound");

            // --- get/set, and the numeric trust boundary. Silent truncation is the bug this exists
            // to prevent, so a value that does not survive the trip is refused rather than rounded.
            Check("set-field", R("call", "{'op':'set','type':'" + OvType + "','target':'" + ov + "','member':'Field','value':7}").Contains("\"ok\":true"),
                  R("call", "{'op':'set','type':'" + OvType + "','target':'" + ov + "','member':'Field','value':7}"));
            Check("get-field", R("call", "{'op':'get','target':'" + ov + "','member':'Field'}").Contains("\"value\":7"),
                  R("call", "{'op':'get','target':'" + ov + "','member':'Field'}"));
            Check("set-out-of-range-refused",
                  R("call", "{'op':'set','target':'" + ov + "','member':'Field','value':3000000000}").Contains("out of range"),
                  R("call", "{'op':'set','target':'" + ov + "','member':'Field','value':3000000000}"));
            Check("set-string-into-int-refused",
                  R("call", "{'op':'set','target':'" + ov + "','member':'Field','value':'7'}").Contains("\"code\":\"bind\""),
                  "a numeric string was coerced into an int");
            Check("set-property", R("call", "{'op':'set','target':'" + ov + "','member':'Prop','value':'p'}").Contains("\"ok\":true"),
                  R("call", "{'op':'set','target':'" + ov + "','member':'Prop','value':'p'}"));
            Check("get-property", R("call", "{'op':'get','target':'" + ov + "','member':'Prop'}").Contains("\"value\":\"p\""),
                  R("call", "{'op':'get','target':'" + ov + "','member':'Prop'}"));
            Check("get-unknown-member", R("call", "{'op':'get','target':'" + ov + "','member':'Nope'}").Contains("\"code\":\"member\""),
                  "an unknown member was read");
            Check("instance-member-without-target",
                  R("call", "{'op':'get','type':'" + OvType + "','member':'Field'}").Contains("no target was given"),
                  "an instance field was read with no instance");

            // --- projection: inline for known value types, a handle for everything else, and NEVER
            // an enumeration or a property walk.
            string v3 = R("call", "{'op':'invoke','type':'" + OvType + "','member':'MakeV3','args':[]}");
            Check("project-struct-inline", v3.Contains("\"x\":1") && v3.Contains("\"z\":3") && !v3.Contains("\"h\":"), v3);
            Check("project-void", R("call", "{'op':'invoke','type':'" + OvType + "','member':'Nothing','args':[]}").Contains("\"void\":true"),
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'Nothing','args':[]}"));

            List<int> numbers = new List<int>();
            for (int i = 0; i < 120; i++) numbers.Add(i);
            string listHandle = Reflect.Track(numbers);
            string page0 = R("items", "{'h':'" + listHandle + "','pageSize':50}");
            Check("items-first-page", page0.Contains("\"returned\":50") && page0.Contains("\"hasMore\":true") && page0.Contains("\"count\":120"), page0);
            string page2 = R("items", "{'h':'" + listHandle + "','page':2,'pageSize':50}");
            Check("items-last-page", page2.Contains("\"returned\":20") && page2.Contains("\"hasMore\":false"), page2);
            Check("items-page-size-capped", R("items", "{'h':'" + listHandle + "','pageSize':500}").Contains("pageSize must be"),
                  "an unbounded page size was accepted");
            Check("items-not-enumerable", R("items", "{'h':'" + ov + "'}").Contains("not enumerable"),
                  "a non-collection was enumerated");
            // A lazy iterator has no size until it is walked. The field is OMITTED rather than
            // emitted empty - "count":null reads as "zero items" to a client, and the P2 gate saw
            // exactly that on TacticalFaction.Actors.
            string lazy = R("items", "{'h':'" + Reflect.Track(Ov.Lazy()) + "','pageSize':10}");
            Check("items-omits-an-unknown-count", !lazy.Contains("\"count\"") && lazy.Contains("\"returned\":3"), lazy);
            Check("items-still-reports-a-known-count", R("items", "{'h':'" + listHandle + "','pageSize':1}").Contains("\"count\":120"),
                  "a countable collection stopped reporting its count");

            // The two caps that stop one request from costing thousands of tokens.
            List<string> longs = new List<string> { new string('w', 5000) };
            string clipped = R("items", "{'h':'" + Reflect.Track(longs) + "','pageSize':1}");
            Check("dto-clips-long-strings", clipped.Contains("(clipped)") && clipped.Length < 4000, "" + clipped.Length);
            string fat = R("items", "{'h':'" + Reflect.Track(Ov.Fat()) + "','pageSize':200}");
            Check("dto-response-byte-cap", fat.Contains("\"code\":\"cap\"") && fat.Length < Reflect.MaxResponseBytes, "" + fat.Length);

            // --- discovery verbs.
            Check("types-finds-a-type", R("types", "{'pattern':'Morgott.PPBridge.Ov'}").Contains(OvType),
                  R("types", "{'pattern':'Morgott.PPBridge.Ov'}"));
            Check("types-needs-a-pattern", R("types", "{}").Contains("\"code\":\"args\""), "an empty pattern was accepted");
            string members = R("members", "{'type':'" + OvType + "'}");
            Check("members-lists-methods", members.Contains("M static String TakeV3(V3 v)"), members.Substring(0, Math.Min(400, members.Length)));
            Check("members-lists-inherited", members.Contains("<Object>"), "no inherited member reported");
            Check("members-filter", R("members", "{'type':'" + OvType + "','filter':'TakeV3'}").Contains("\"count\":1"),
                  R("members", "{'type':'" + OvType + "','filter':'TakeV3'}"));
            string inspect = R("inspect", "{'h':'" + ov + "'}");
            Check("inspect-describes-the-handle", inspect.Contains("\"type\":\"" + OvType + "\"") && inspect.Contains("\"self\":"), inspect.Substring(0, Math.Min(300, inspect.Length)));

            Protocol.AllDefs = () => new List<object> { new FakeDef { Guid = "g1", name = "hello_def" }, new FakeDef { Guid = "g2", name = "other" } };
            Check("find-by-name", R("find", "{'query':'hello'}").Contains("\"guid\":\"g1\""), R("find", "{'query':'hello'}"));
            Check("find-by-guid", R("find", "{'query':'g2'}").Contains("\"name\":\"other\""), R("find", "{'query':'g2'}"));
            Check("find-type-filter", R("find", "{'query':'hello','type':'Morgott.PPBridge.FakeDef'}").Contains("\"count\":1"),
                  R("find", "{'query':'hello','type':'Morgott.PPBridge.FakeDef'}"));
            Check("find-needs-a-query", R("find", "{}").Contains("\"code\":\"args\""), "an empty query was accepted");

            // --- find {all:true}: enumeration is OPT-IN, ordered and paged. The flag is what stops a
            // typo'd variable from turning into a dump of the whole def repository, so a missing query
            // WITHOUT it must still refuse; and the sort must be total, or a page boundary would skip
            // or duplicate a row while an index is being built.
            Check("find-all-refuses-without-the-flag", R("find", "{'page':0}").Contains("\"code\":\"args\""),
                  R("find", "{'page':0}"));
            Check("find-all-refuses-a-non-boolean-flag", R("find", "{'all':'true'}").Contains("\"code\":\"args\""),
                  R("find", "{'all':'true'}"));
            Protocol.AllDefs = () => new List<object>
            {
                new FakeDef { Guid = "g3", name = "ccc" }, new FakeDef { Guid = "g1", name = "aaa" },
                null, new FakeDef { Guid = "g2", name = "bbb" }
            };
            string allPage0 = R("find", "{'all':true,'page':0,'pageSize':2}");
            string allPage1 = R("find", "{'all':true,'page':1,'pageSize':2}");
            Check("find-all-pages", allPage0.Contains("\"count\":2") && allPage0.Contains("\"total\":3") &&
                                    allPage0.Contains("\"hasMore\":true"), allPage0);
            Check("find-all-is-ordered", allPage0.IndexOf("aaa", StringComparison.Ordinal) <
                                         allPage0.IndexOf("bbb", StringComparison.Ordinal) &&
                                         !allPage0.Contains("ccc"), allPage0);
            Check("find-all-last-page", allPage1.Contains("ccc") && !allPage1.Contains("bbb") &&
                                        allPage1.Contains("\"hasMore\":false"), allPage1);
            Check("find-all-past-the-end", R("find", "{'all':true,'page':99,'pageSize':2}").Contains("\"count\":0"),
                  R("find", "{'all':true,'page':99,'pageSize':2}"));
            Check("find-all-filters-by-query", R("find", "{'all':true,'query':'bb'}").Contains("\"total\":1"),
                  R("find", "{'all':true,'query':'bb'}"));

            // --- roots: late-bound every call, and usable as a target with @.
            int probeCalls = 0;
            Ov root = new Ov { Prop = "iam-root" };
            Protocol.RootsProbe = () => { probeCalls++; return new Dictionary<string, object> { { "thing", root }, { "nothing", null } }; };
            Check("roots-projects", R("roots", "{}").Contains("\"thing\":{\"h\":"), R("roots", "{}"));
            Check("roots-reports-null-roots", R("roots", "{}").Contains("\"nothing\":null"), R("roots", "{}"));
            Check("root-alias-as-target",
                  R("call", "{'op':'get','target':'@thing','member':'Prop'}").Contains("iam-root"),
                  R("call", "{'op':'get','target':'@thing','member':'Prop'}"));
            Check("root-alias-unknown", R("call", "{'op':'get','target':'@nope','member':'Prop'}").Contains("no root 'nope'"),
                  "an unknown alias resolved");
            Check("root-alias-null", R("call", "{'op':'get','target':'@nothing','member':'Prop'}").Contains("is null right now"),
                  "a null root was used as a target");
            Check("roots-are-late-bound", probeCalls >= 6, probeCalls + " probe calls - a cached root would have called it once");

            // --- handle lifetime. This is the last group on purpose: it bumps the epoch.
            Check("release-frees", R("release", "{'h':'" + listHandle + "'}").Contains("\"released\":true"),
                  R("release", "{'h':'" + listHandle + "'}"));
            Check("release-twice-is-honest", R("release", "{'h':'" + listHandle + "'}").Contains("\"released\":false"),
                  "a second release claimed to free something");
            Check("released-handle-refused", R("inspect", "{'h':'" + listHandle + "'}").Contains("expired or was released"),
                  "a released handle still resolved");
            Check("malformed-handle-refused", R("inspect", "{'h':'not-a-handle'}").Contains("\"code\":\"handle\""),
                  "a malformed handle resolved");

            Protocol.UnityAlive = o => false;
            Check("destroyed-object-refused", R("inspect", "{'h':'" + ov + "'}").Contains("destroyed"),
                  R("inspect", "{'h':'" + ov + "'}"));
            Protocol.UnityAlive = null;

            string survivor = Reflect.Track(new Ov());
            Check("handle-resolves-before-the-epoch-bump", R("inspect", "{'h':'" + survivor + "'}").Contains("\"ok\":true"),
                  R("inspect", "{'h':'" + survivor + "'}"));
            Reflect.NewEpoch();
            string afterUnload = R("inspect", "{'h':'" + survivor + "'}");
            Check("previous-epoch-handle-refused", afterUnload.Contains("is from epoch") && afterUnload.Contains("\"code\":\"handle\""), afterUnload);
            Check("new-handles-work-after-an-epoch-bump",
                  R("inspect", "{'h':'" + Reflect.Track(new Ov()) + "'}").Contains("\"ok\":true"),
                  "the table never recovered from an epoch bump");

            // --- and the verbs really are reachable through the dispatcher a client talks to.
            Check("protocol-routes-call",
                  Protocol.Compact(Protocol.Dispatch(new Job { Id = "x", Verb = "call", Args = JObject.Parse("{'op':'invoke','type':'" + OvType + "','member':'M','args':[5]}") })).Contains("\"value\":\"long\""),
                  "call is not reachable through Protocol.Dispatch");
            Check("protocol-still-refuses-unknown-verbs",
                  Protocol.Compact(Protocol.Dispatch(new Job { Id = "x", Verb = "definitely-not-a-verb" })).Contains("unknown verb"),
                  "an unknown verb stopped being refused");

            Protocol.RootsProbe = null;
            Protocol.AllDefs = null;
            Protocol.DefByGuid = null;
        }

        // ------------------------------------------------------------------ P3: wait + the plan engine

        /// <summary>
        /// Runs a cross-frame verb the way PPBridgeMain.Runner does - one Tick per "frame", never a
        /// spin - and hands back the finished DTO. <paramref name="cancelAt"/> is the tick from which
        /// the cancel flag is raised, which is the only way to prove `cancel` reaches a running job.
        /// </summary>
        private static string Drive(object started, int maxTicks, int cancelAt, int sleepMs)
        {
            if (!(started is IPending p)) return Protocol.Compact(started);
            for (int i = 0; i < maxTicks; i++)
            {
                if (sleepMs > 0) Thread.Sleep(sleepMs);
                object r = p.Tick(cancelAt >= 0 && i >= cancelAt);
                if (r != null) return Protocol.Compact(r);
            }
            return "<never finished in " + maxTicks + " ticks>";
        }

        private static string Run(string verb, string json, int maxTicks = 200, int cancelAt = -1, int sleepMs = 0)
        {
            return Drive(Protocol.Dispatch(new Job { Id = "s", Verb = verb, Args = JObject.Parse(json) }),
                         maxTicks, cancelAt, sleepMs);
        }

        /// <summary>The TOP-LEVEL code of a finished DTO, or the raw text when there is no DTO at all
        /// (a job that never finished is exactly what these checks are hunting).</summary>
        private static string TopCode(string dto)
        {
            try { return (string)JObject.Parse(dto)["code"]; }
            catch (Exception) { return "<no result: " + dto + ">"; }
        }

        private static Shots.Impact At(float x, float y, float z, string actor)
        {
            return new Shots.Impact { X = x, Y = y, Z = z, Actor = actor };
        }

        /// <summary>
        /// The observer's PURE half - the ring, the caps, the hit/miss split and the dispersion
        /// arithmetic. The Harmony patch that feeds it cannot run here, so what is proven is
        /// everything downstream of one call to Shots.Record, which is where all the logic is.
        /// </summary>
        private static void ShotChecks()
        {
            Shots.Arm = null;
            Shots.Shutdown();
            Check("observe-needs-an-action", Run("observe", "{}").Contains("observe needs"), "an actionless observe was accepted");
            Check("observe-start-without-a-game", Run("observe", "{'action':'start'}").Contains("no shot observer installed"),
                  "start armed nothing and said nothing");

            // A patch that could not be installed must REFUSE, not open an observer that will
            // silently record zero impacts - that is the failure mode this whole file exists to
            // avoid reporting as a measurement.
            Shots.Arm = on => "the seam moved";
            Check("observe-start-refuses-a-failed-patch", Run("observe", "{'action':'start'}").Contains("the seam moved"),
                  "a failed patch install came back green");
            Check("observe-off-after-a-failed-start", !Shots.On, "On was left true by a failed start");

            bool armed = false;
            Shots.Arm = on => { armed = on; return null; };

            // OFF by default: a Record before start must be dropped on the floor.
            Shots.Record(9f, 9f, 9f, "ghost", "ghost", 1f, 0f, 1);
            Check("observe-records-nothing-while-off", Shots.Recorded == 0, "recorded=" + Shots.Recorded);

            Check("observe-start", Run("observe", "{'action':'start'}").Contains("\"observing\":true"), "start refused");
            Check("observe-start-arms-the-patch", armed && Shots.On, "armed=" + armed + " on=" + Shots.On);

            Shots.Record(1f, 0f, 0f, "Crabman_1", "Torso", 30f, 5f, 1);
            Shots.Record(2f, 0f, 0f, null, "Terrain", 0f, 0f, 0);
            Shots.Record(3f, 0f, 0f, null, "Wall", 0f, 0f, 0);
            string read = Run("observe", "{'action':'read','aim':[0,0,0]}");
            Check("observe-read-counts-hits-and-misses",
                  read.Contains("\"hits\":1") && read.Contains("\"misses\":2"), read);
            // A hit is "an actor stopped it", NOT "damage was dealt": a fully-armoured hit does zero
            // health damage and is still a hit, and scoring on damage would call it a miss.
            Check("observe-read-carries-the-impact-points",
                  read.Contains("\"x\":1.0") && read.Contains("\"actor\":\"Crabman_1\"") && read.Contains("\"part\":\"Terrain\""), read);
            Check("observe-read-reports-the-aim", read.Contains("\"aim\":{\"x\":0.0"), read);

            // Row 2 is a terrain hit carrying damage of its own: the total and the on-target figure
            // must not be the same number, or a shot that mauled a tree would read as damage on the
            // enemy.
            Shots.Record(7f, 0f, 0f, null, "Tree", 11f, 0f, 1);
            string split = Run("observe", "{'action':'read'}");
            Check("observe-read-sums-the-damage",
                  split.Contains("\"damageTotal\":41.0") && split.Contains("\"damageOnActors\":30.0") &&
                  split.Contains("\"armorTotal\":5.0"), split);

            Check("observe-read-has-no-empty-rows", read.Contains("\"noGeometry\":0") && read.Contains("\"n\":3"), read);

            // THE row that must not lie. A projectile that hits nothing comes through
            // OnTrajectoryEnd with the static SDummyHit: no collider, point exactly (0,0,0). The
            // three rows above all have a collider; this one does not, so its coordinates must come
            // back NULL and it must stay out of the dispersion arithmetic - a live run with two such
            // rows read a 4.2 m group where the real one was 0.2 m.
            Shots.Record(0f, 0f, 0f, null, null, 0f, 0f, 0);
            string nothing = Run("observe", "{'action':'read'}");
            Check("observe-no-geometry-is-reported", nothing.Contains("\"noGeometry\":1"), nothing);
            Check("observe-no-geometry-has-null-coordinates",
                  nothing.Contains("{\"x\":null,\"y\":null,\"z\":null,\"actor\":null,\"part\":null"), nothing);
            Check("observe-no-geometry-still-counts-as-a-miss", nothing.Contains("\"misses\":4"), nothing);
            Check("observe-no-geometry-is-out-of-the-dispersion", nothing.Contains("\"n\":4"), nothing);

            // mark/Landed is the shot-pacing predicate: it measures the projectile landing itself.
            Run("observe", "{'action':'mark'}");
            Check("observe-mark-zeroes-landed", Shots.Landed == 0, "landed=" + Shots.Landed);
            Shots.Record(4f, 0f, 0f, null, "Terrain", 0f, 0f, 0);
            Check("observe-landed-counts-since-the-mark", Shots.Landed == 1 && Shots.Recorded == 6,
                  "landed=" + Shots.Landed + " recorded=" + Shots.Recorded);

            Check("observe-stop-disarms", Run("observe", "{'action':'stop'}").Contains("\"observing\":false") && !armed && !Shots.On,
                  "armed=" + armed + " on=" + Shots.On);

            // The ring is BOUNDED and drops the OLDEST. An unbounded buffer written from inside a
            // game-loop patch is a leak, and truncating the newest would throw away the shots the
            // caller is actually asking about.
            Run("observe", "{'action':'start'}");
            for (int i = 0; i < Shots.Capacity + 40; i++) Shots.Record(i, 0f, 0f, null, "t", 0f, 0f, 0);
            string full = Run("observe", "{'action':'read'}");
            Check("observe-ring-is-bounded",
                  full.Contains("\"stored\":" + Shots.Capacity) && full.Contains("\"dropped\":40") &&
                  full.Contains("\"recorded\":" + (Shots.Capacity + 40)), full);
            Check("observe-listing-is-capped", full.Contains("\"returned\":" + Shots.MaxRows), full);
            // The stats are computed over EVERYTHING stored, not over the trimmed listing.
            Check("observe-stats-use-the-whole-ring", full.Contains("\"n\":" + Shots.Capacity), full);
            Run("observe", "{'action':'stop'}");

            // --- THE TARGET SPLIT. "an actor stopped it" and "the actor this volley was aimed at
            // stopped it" are different questions, and answering the first under the name of the
            // second is how a bystander - or the shooter's own body - inflates a weapon's score.
            Check("observe-start-refuses-a-non-integer-target",
                  Run("observe", "{'action':'start','target':'Crabman_1'}").Contains("integer instanceId"),
                  "a name was accepted as a target id");
            Run("observe", "{'action':'start','target':4242}");
            Shots.Record(1f, 0f, 0f, "Crabman_1", "Torso", 30f, 5f, 1, 4242, 30f);   // THE target
            Shots.Record(2f, 0f, 0f, "Crabman_1", "Torso", 12f, 0f, 1, 77, 0f);      // a namesake, not it
            Shots.Record(3f, 0f, 0f, null, "Wall", 0f, 0f, 0);                       // terrain
            string keyed = Run("observe", "{'action':'read'}");
            Check("observe-target-hits-are-not-actor-hits",
                  keyed.Contains("\"hits\":2") && keyed.Contains("\"targetHits\":1") &&
                  keyed.Contains("\"targetMisses\":2"), keyed);
            // The two same-named rows are the point: a NAME cannot tell two Crabmen apart, an
            // instance id can, and the bench keys on the id.
            Check("observe-damage-on-target-is-not-damage-on-actors",
                  keyed.Contains("\"damageOnActors\":42.0") && keyed.Contains("\"damageOnTarget\":30.0"), keyed);
            Check("observe-target-is-echoed", keyed.Contains("\"target\":4242"), keyed);
            Run("observe", "{'action':'stop'}");

            // Told nothing, the target family must read NOTHING - never the all-actor totals wearing
            // a name that promises they are the target's.
            Run("observe", "{'action':'start'}");
            Shots.Record(1f, 0f, 0f, "Crabman_1", "Torso", 30f, 5f, 1, 4242, 30f);
            string untold = Run("observe", "{'action':'read'}");
            Check("observe-untold-target-scores-nothing",
                  untold.Contains("\"target\":null") && untold.Contains("\"targetHits\":0") &&
                  untold.Contains("\"damageOnTarget\":0.0") && untold.Contains("\"hits\":1"), untold);
            Run("observe", "{'action':'stop'}");

            // --- the arithmetic, against numbers worked out by hand.
            List<Shots.Impact> line = new List<Shots.Impact> { At(1, 0, 0, null), At(2, 0, 0, null), At(3, 0, 0, null) };
            Dictionary<string, object> stats = Shots.Stats(line, new[] { 0f, 0f, 0f });
            string statsJson = Protocol.Compact(stats);
            // about (0,0,0): distances 1,2,3 -> mean 2, sigma sqrt(14/3 - 4) = 0.8165, max 3
            Check("stats-about-the-aim-point",
                  statsJson.Contains("\"aboutAim\":{\"mean\":2.0,\"sigma\":0.8165,\"max\":3.0}"), statsJson);
            // about the centroid (2,0,0): distances 1,0,1 -> mean 0.6667, sigma 0.4714, max 1
            Check("stats-about-the-centroid",
                  statsJson.Contains("\"centroid\":{\"x\":2.0,\"y\":0.0,\"z\":0.0}") &&
                  statsJson.Contains("\"aboutCentroid\":{\"mean\":0.6667,\"sigma\":0.4714,\"max\":1.0}"), statsJson);

            // THE control-run case, and the reason the variance is clamped: three identical impacts
            // give E[d^2] - E[d]^2 a tiny NEGATIVE value in floating point, and an unclamped sqrt
            // returns NaN - so a perfect spread:0 group, the very thing that proves the bench
            // measures anything, would come back unreadable.
            List<Shots.Impact> same = new List<Shots.Impact> { At(5, 0, 0, null), At(5, 0, 0, null), At(5, 0, 0, null) };
            string tight = Protocol.Compact(Shots.Stats(same, new[] { 0f, 0f, 0f }));
            Check("stats-zero-spread-is-zero-not-nan",
                  tight.Contains("\"aboutCentroid\":{\"mean\":0.0,\"sigma\":0.0,\"max\":0.0}") &&
                  tight.Contains("\"aboutAim\":{\"mean\":5.0,\"sigma\":0.0,\"max\":5.0}") && !tight.Contains("NaN"), tight);

            Check("stats-on-nothing-is-not-an-error", Protocol.Compact(Shots.Stats(new List<Shots.Impact>(), null)) == "{\"n\":0}",
                  Protocol.Compact(Shots.Stats(new List<Shots.Impact>(), null)));

            Shots.Shutdown();
        }

        private static void PlanChecks()
        {
            Ov root = new Ov { Prop = "iam-root" };
            Protocol.RootsProbe = () => new Dictionary<string, object> { { "thing", root } };
            Protocol.StateProbe = () => new { ok = true, phase = "tactical" };
            Ov.Counter = 0;

            // --- wait. A predicate that is already true must not cost a frame of waiting; one that
            // never becomes true must come back as a NAMED timeout, never as a hang.
            root.Field = 0;
            Check("wait-needs-a-predicate", Run("wait", "{}").Contains("\"code\":\"args\""),
                  "wait with no predicate was accepted");
            Check("wait-times-out",
                  Run("wait", "{'call':{'op':'get','target':'@thing','member':'Field'},'timeoutMs':1,'everyFrames':1}", 50, -1, 2)
                      .Contains("\"code\":\"timeout\""),
                  Run("wait", "{'call':{'op':'get','target':'@thing','member':'Field'},'timeoutMs':1,'everyFrames':1}", 50, -1, 2));
            root.Field = 7;
            string waited = Run("wait", "{'call':{'op':'get','target':'@thing','member':'Field'},'timeoutMs':5000,'everyFrames':1}");
            Check("wait-succeeds-when-the-predicate-turns-true", waited.Contains("\"ok\":true") && waited.Contains("\"value\":7"), waited);
            Check("wait-on-a-phase", Run("wait", "{'phase':'tactical','timeoutMs':5000,'everyFrames':1}").Contains("\"ok\":true"),
                  Run("wait", "{'phase':'tactical','timeoutMs':5000,'everyFrames':1}"));
            Check("wait-on-the-wrong-phase-times-out",
                  Run("wait", "{'phase':'geoscape','timeoutMs':1,'everyFrames':1}", 50, -1, 2).Contains("\"code\":\"timeout\""),
                  "a phase that never matched came back green");
            // A predicate that is broken forever must still SAY so - a bare timeout would send an
            // agent looking at the game instead of at its own typo.
            Check("wait-timeout-reports-the-last-error",
                  Run("wait", "{'call':{'op':'get','target':'@nope','member':'Field'},'timeoutMs':1,'everyFrames':1}", 50, -1, 2)
                      .Contains("no root 'nope'"),
                  Run("wait", "{'call':{'op':'get','target':'@nope','member':'Field'},'timeoutMs':1,'everyFrames':1}", 50, -1, 2));
            // --- `not`. Half the interesting predicates are the wrong way round ("the ability has
            // STOPPED executing") and cannot be written as System.Object.Equals(false, x): that
            // needs the live value as an ARGUMENT, and arguments are substituted once per step.
            root.Field = 0;
            Check("wait-not-succeeds-on-a-falsy-predicate",
                  Run("wait", "{'not':true,'call':{'op':'get','target':'@thing','member':'Field'},'timeoutMs':5000,'everyFrames':1}")
                      .Contains("\"ok\":true"),
                  Run("wait", "{'not':true,'call':{'op':'get','target':'@thing','member':'Field'},'timeoutMs':5000,'everyFrames':1}"));
            root.Field = 7;
            Check("wait-not-times-out-on-a-truthy-predicate",
                  Run("wait", "{'not':true,'call':{'op':'get','target':'@thing','member':'Field'},'timeoutMs':1,'everyFrames':1}", 50, -1, 2)
                      .Contains("still true after"),
                  Run("wait", "{'not':true,'call':{'op':'get','target':'@thing','member':'Field'},'timeoutMs':1,'everyFrames':1}", 50, -1, 2));
            // THE trap: an erroring predicate must satisfy NEITHER polarity. @tac is null while a
            // mission loads, and a negated wait that took "the call failed" for "the thing is false"
            // would return green the instant a level started loading.
            Check("wait-not-does-not-accept-an-error-as-false",
                  Run("wait", "{'not':true,'call':{'op':'get','target':'@nope','member':'Field'},'timeoutMs':1,'everyFrames':1}", 50, -1, 2)
                      .Contains("\"code\":\"timeout\""),
                  Run("wait", "{'not':true,'call':{'op':'get','target':'@nope','member':'Field'},'timeoutMs':1,'everyFrames':1}", 50, -1, 2));

            root.Field = 0;
            Check("wait-is-cancellable",
                  Run("wait", "{'call':{'op':'get','target':'@thing','member':'Field'},'timeoutMs':60000,'everyFrames':1}", 50, 2, 0)
                      .Contains("\"code\":\"cancelled\""),
                  "a running wait ignored the cancel flag");

            // --- the plan engine. Steps in order, results into named variables, variables back into
            // later steps.
            string ordered = Run("plan", @"{'plan':{'steps':[
                {'id':'a','verb':'call','args':{'op':'invoke','type':'" + OvType + @"','member':'Bump','args':[]},'save':'A'},
                {'id':'b','verb':'call','args':{'op':'invoke','type':'" + OvType + @"','member':'Bump','args':[]},'save':'B'}],
                'output':{'first':'${A.value}','second':'${B.value}'}}}");
            Check("plan-runs-steps-in-order", ordered.Contains("\"first\":1") && ordered.Contains("\"second\":2"), ordered);
            Check("plan-counts-its-steps", ordered.Contains("\"steps\":2"), ordered);
            Check("plan-traces-every-step", ordered.Contains("\"id\":\"a\"") && ordered.Contains("\"id\":\"b\""), ordered);

            // Substitution alone in a string yields the TOKEN, so a number stays a number - the
            // difference between binding 7 to an int parameter and being refused for passing "7".
            string typed = Run("plan", @"{'plan':{'vars':{'n':7},'steps':[
                {'id':'s','verb':'call','args':{'op':'set','type':'" + OvType + @"','member':'Counter','value':'${n}'}},
                {'id':'g','verb':'call','args':{'op':'get','type':'" + OvType + @"','member':'Counter'},'save':'G'}],
                'output':{'got':'${G.value}'}}}");
            Check("plan-substitution-keeps-a-number-a-number", typed.Contains("\"got\":7"), typed);
            Check("plan-interpolates-inside-a-string",
                  Run("plan", @"{'plan':{'vars':{'n':'Take'},'steps':[
                      {'id':'x','verb':'call','args':{'op':'invoke','type':'" + OvType + @"','member':'${n}Season','args':['Summer']},'save':'X'}],
                      'output':{'v':'${X.value}'}}}").Contains("\"v\":\"Summer\""),
                  "an embedded ${var} did not interpolate");
            // Never silently null: an unset variable is a failed step with the name in the message.
            string missing = Run("plan", "{'plan':{'steps':[{'id':'m','verb':'call','args':{'op':'get','target':'${NOPE}','member':'Field'}}]}}");
            Check("plan-unknown-var-fails-the-step", missing.Contains("\"code\":\"step\"") && missing.Contains("${NOPE}"), missing);

            // --- the MANDATORY finally. It must run on success, on failure, on a cap, on a timeout
            // and on cancellation - five doors, one exit.
            const string Bump = "{'verb':'call','args':{'op':'invoke','type':'" + OvType + "','member':'Bump','args':[]},'save':'F'}";
            Ov.Counter = 100;
            string okFinally = Run("plan", "{'plan':{'steps':[{'id':'p','verb':'ping'}],'finally':[" + Bump + "],'output':{'f':'${F.value}'}}}");
            Check("plan-finally-runs-on-success", okFinally.Contains("\"f\":101") && okFinally.Contains("\"cleanupRan\":true"), okFinally);

            Ov.Counter = 200;
            string failFinally = Run("plan", "{'plan':{'steps':[{'id':'boom','verb':'no-such-verb'}],'finally':[" + Bump + "],'output':{'f':'${F.value}'}}}");
            Check("plan-fails-on-a-bad-step", failFinally.Contains("\"code\":\"step\"") && failFinally.Contains("\"step\":\"boom\""), failFinally);
            Check("plan-finally-runs-on-failure", failFinally.Contains("\"f\":201"), failFinally);

            Ov.Counter = 300;
            string capped = Run("plan", "{'plan':{'maxSteps':3,'steps':[{'verb':'ping'},{'verb':'ping'},{'verb':'ping'},{'verb':'ping'},{'verb':'ping'}],'finally':[" + Bump + "]}}");
            Check("plan-step-cap-stops-it", capped.Contains("\"code\":\"cap\""), capped);
            Check("plan-finally-runs-after-a-cap", capped.Contains("\"cleanupRan\":true") && Ov.Counter == 301, "counter=" + Ov.Counter);

            Ov.Counter = 400;
            string timedOut = Run("plan", "{'plan':{'timeoutMs':1,'steps':[{'verb':'ping'}],'finally':[" + Bump + "]}}", 50, -1, 3);
            Check("plan-times-out", timedOut.Contains("\"code\":\"timeout\""), timedOut);
            Check("plan-finally-runs-after-a-timeout", timedOut.Contains("\"cleanupRan\":true") && Ov.Counter == 401, "counter=" + Ov.Counter);

            // A wait that can NEVER be satisfied is the shape a real plan dies of: `restore` a save
            // the build cannot open, then wait for a phase that never arrives. The plan's OWN
            // deadline has to end it - the step's far larger timeoutMs must not be what decides -
            // and the cleanup block still has to run.
            Ov.Counter = 600;
            const string StuckWait = "{'id':'stuck','verb':'wait','args':{'call':{'op':'get','target':'@thing','member':'Field'},'timeoutMs':600000,'everyFrames':1}}";
            string neverWait = Run("plan", "{'plan':{'timeoutMs':30,'steps':[" + StuckWait + "],'finally':[" + Bump + "]}}", 500, -1, 2);
            Check("plan-timeout-ends-a-never-satisfiable-wait", TopCode(neverWait) == "timeout", neverWait);
            Check("plan-finally-runs-after-a-never-satisfiable-wait",
                  neverWait.Contains("\"cleanupRan\":true") && Ov.Counter == 601, "counter=" + Ov.Counter + " " + neverWait);

            // The same wait INSIDE the cleanup block. Both timeout checks used to be guarded by
            // !inCleanup, so the grace deadline was never read and this plan parked forever.
            Ov.Counter = 700;
            int grace = Plan.FinallyGraceMs;
            Plan.FinallyGraceMs = 60;                  // the identical proof, 15 seconds faster
            string neverCleanup;
            try { neverCleanup = Run("plan", "{'plan':{'timeoutMs':30,'steps':[{'id':'p','verb':'ping'}],'finally':[" + StuckWait + "," + Bump + "]}}", 2000, -1, 1); }
            finally { Plan.FinallyGraceMs = grace; }
            Check("plan-finally-cannot-hang-forever",
                  TopCode(neverCleanup) == "timeout" && neverCleanup.Contains("grace period"), neverCleanup);

            Ov.Counter = 500;
            string cancelled = Run("plan",
                "{'plan':{'timeoutMs':60000,'steps':[{'id':'w','verb':'wait','args':{'call':{'op':'get','target':'@thing','member':'Field'},'timeoutMs':60000,'everyFrames':1}}],'finally':[" + Bump + "]}}",
                50, 2, 0);
            // The TOP-LEVEL code, parsed - not a substring. A plan that merely let the cancelled wait
            // fail as an ordinary step also carries "cancelled" somewhere in its trace, and a
            // Contains() here passed while the engine ignored the flag entirely.
            Check("plan-cancel-stops-a-waiting-step", (string)JObject.Parse(cancelled)["code"] == "cancelled", cancelled);
            Check("plan-finally-runs-after-a-cancel", cancelled.Contains("\"cleanupRan\":true") && Ov.Counter == 501, "counter=" + Ov.Counter);

            // --- bounded branching and repetition.
            Check("plan-if-skips-a-step",
                  Run("plan", "{'plan':{'vars':{'go':false},'steps':[{'id':'s','verb':'ping','if':'${go}'}]}}").Contains("\"skipped\":\"if\""),
                  "a falsy if still ran the step");
            Check("plan-if-runs-a-step",
                  !Run("plan", "{'plan':{'vars':{'go':true},'steps':[{'id':'s','verb':'ping','if':'${go}'}]}}").Contains("skipped"),
                  "a truthy if skipped the step");
            Check("plan-unless-inverts",
                  Run("plan", "{'plan':{'vars':{'go':true},'steps':[{'id':'s','verb':'ping','unless':'${go}'}]}}").Contains("\"skipped\":\"unless\""),
                  "unless did not invert");
            Check("plan-onerror-continue",
                  Run("plan", "{'plan':{'steps':[{'id':'bad','verb':'no-such-verb','onError':'continue'},{'id':'good','verb':'ping'}]}}")
                      .Contains("\"ok\":true"),
                  Run("plan", "{'plan':{'steps':[{'id':'bad','verb':'no-such-verb','onError':'continue'},{'id':'good','verb':'ping'}]}}"));

            string repeated = Run("plan", "{'plan':{'steps':[{'id':'r','verb':'repeat','args':{'times':5,'steps':[{'verb':'ping'}]}}]}}");
            Check("plan-repeat-runs-the-body", repeated.Contains("\"steps\":6"), repeated);   // the repeat itself + 5 passes
            // The iteration cap is not advice: a plan asking for 100000 passes gets MaxIterations.
            string overRepeat = Run("plan", "{'plan':{'maxSteps':2000,'steps':[{'id':'r','verb':'repeat','args':{'times':100000,'steps':[{'verb':'ping'}]}}]}}");
            Check("plan-repeat-is-capped", overRepeat.Contains("\"steps\":" + (Plan.MaxIterations + 1)), overRepeat);
            Check("plan-repeat-needs-a-body",
                  Run("plan", "{'plan':{'steps':[{'id':'r','verb':'repeat','args':{'times':3}}]}}").Contains("repeat needs args"),
                  "a bodyless repeat was accepted");
            Check("plan-may-not-run-a-plan",
                  Run("plan", "{'plan':{'steps':[{'id':'r','verb':'plan','args':{'steps':[]}}]}}").Contains("may not run a plan"),
                  "a plan started a plan");

            // Caller vars override the stored plan's own defaults - that is what parameterises a
            // plan file without editing it.
            Check("plan-caller-vars-win",
                  Run("plan", "{'plan':{'vars':{'n':1},'steps':[],'output':{'n':'${n}'}},'vars':{'n':42}}").Contains("\"n\":42"),
                  Run("plan", "{'plan':{'vars':{'n':1},'steps':[],'output':{'n':'${n}'}},'vars':{'n':42}}"));
            Check("plan-needs-steps", Run("plan", "{'plan':{}}").Contains("\"code\":\"args\""), "a stepless plan was accepted");

            // --- snapshot / restore, against the delegates the game half installs.
            Check("snapshot-needs-a-name", Run("snapshot", "{}").Contains("snapshot needs {name}"), "a nameless snapshot was accepted");
            Check("snapshot-without-a-runner", Run("snapshot", "{'name':'x'}").Contains("no snapshot runner"), "snapshot ran with no game");

            int polls = 0;
            Protocol.SnapshotStart = n => () => ++polls < 3 ? null : (object)new { ok = true, name = n };
            string snap = Run("snapshot", "{'name':'gate'}");
            Check("snapshot-waits-for-the-save-to-stop", snap.Contains("\"name\":\"gate\"") && polls == 3, snap + " polls=" + polls);
            Protocol.SnapshotStart = n => () => null;      // a save that never stops
            Check("snapshot-times-out", Run("snapshot", "{'name':'gate','timeoutMs':1}", 50, -1, 3).Contains("\"code\":\"timeout\""),
                  "a save that never finished came back green");

            string loaded = null;
            Protocol.ConsoleRun = (c, a) => { loaded = c + " " + string.Join(" ", a); return new { ok = true, output = new string[0] }; };
            Protocol.SaveExists = n => n == "gate";
            Check("restore-refuses-a-missing-save", Run("restore", "{'name':'ghost'}").Contains("no savegame called 'ghost'"),
                  "a missing savegame was 'restored'");
            Check("restore-issued-nothing-for-a-missing-save", loaded == null, "" + loaded);
            string restored = Run("restore", "{'name':'gate'}");
            Check("restore-issues-load_game", loaded == "load_game gate", "" + loaded);
            Check("restore-admits-it-cannot-confirm", restored.Contains("no completion signal"), restored);

            // --- var: the console's OTHER surface, which the console verb structurally cannot reach.
            Check("var-without-a-runner", Run("var", "{'name':'god_mode'}").Contains("no variable runner"),
                  "var ran with no game");
            string sawName = null, sawValue = "unset";
            Protocol.VarRun = (n, v) => { sawName = n; sawValue = v; return new { ok = true, name = n, value = "False" }; };
            Check("var-needs-a-name", Run("var", "{}").Contains("var needs {name}"), "a nameless var was accepted");
            Check("var-gets", Run("var", "{'name':'god_mode'}").Contains("\"value\":\"False\"") && sawName == "god_mode" && sawValue == null,
                  "name=" + sawName + " value=" + sawValue);
            // Values are STRINGS in both directions - that is the game's own contract - so a JSON
            // boolean must arrive as text rather than being refused by the binder.
            Check("var-sets-with-a-string-value", Run("var", "{'name':'god_mode','value':true}").Contains("\"ok\":true") && sawValue == "True",
                  "value=" + sawValue);
            Check("var-sets-a-number-as-text", Run("var", "{'name':'weapon_spread','value':0}").Contains("\"ok\":true") && sawValue == "0",
                  "value=" + sawValue);
            Protocol.VarRun = null;

            ShippedPlanChecks("spawn-at-coordinate.json", "mission-ready");
            ShippedPlanChecks("aim-and-run.json", "camera-director");
            AimPlanChecks();

            Protocol.SnapshotStart = null;
            Protocol.SaveExists = null;
            Protocol.ConsoleRun = null;
            Protocol.RootsProbe = null;
            Protocol.StateProbe = null;
        }

        /// <summary>
        /// The plan file that actually ships is loaded and RUN here. No game means it cannot get past
        /// its first step - but that is the point: this proves the file parses, that the engine walks
        /// its real shape, and that an early failure still runs the cleanup block instead of leaving
        /// it stranded. A syntax error in the shipped plan would otherwise only surface in-game.
        /// </summary>
        private static JObject ShippedPlan(string name)
        {
            string path = null;
            for (DirectoryInfo d = new DirectoryInfo(AppContext.BaseDirectory); d != null && path == null; d = d.Parent)
            {
                string candidate = Path.Combine(d.FullName, "plans", name);
                if (File.Exists(candidate)) path = candidate;
            }
            Check(name + "-found", path != null, "no plans\\" + name + " above " + AppContext.BaseDirectory);
            if (path == null) return null;
            try { return JObject.Parse(File.ReadAllText(path)); }
            catch (Exception ex) { Check(name + "-parses", false, ex.Message); return null; }
        }

        private static void ShippedPlanChecks(string name, string firstStep)
        {
            JObject file = ShippedPlan(name);
            if (file == null) return;
            Check(name + "-parses", true, "");
            JArray fin = file["finally"] as JArray;
            Check(name + "-has-a-finally", fin != null && fin.Count > 0, "no cleanup block");
            Check(name + "-declares-its-outputs", file["output"] is JObject o && o.Count > 0, "no output block");
            if (fin == null) return;

            // readyTimeoutMs 1: with no game the first step cannot succeed, so this drives the whole
            // failure path in a few milliseconds.
            JObject req = new JObject { { "plan", file }, { "vars", new JObject { { "readyTimeoutMs", 1 } } }, { "timeoutMs", 20000 } };
            string ran = Drive(Protocol.Dispatch(new Job { Id = "sp", Verb = "plan", Args = req }), 500, -1, 1);
            Check(name + "-fails-at-its-first-step", (string)JObject.Parse(ran)["step"] == firstStep, ran);
            // cleanupSteps, not cleanupRan: every one of those releases FAILS here (the handles were
            // never taken), and the count is the only thing that proves the block did not stop at
            // the first of them.
            Check(name + "-runs-every-cleanup-step", (int)JObject.Parse(ran)["cleanupSteps"] == fin.Count, ran);
        }

        /// <summary>
        /// The one thing about aim-and-run that is not a generic plan property: it disables the
        /// human's input, so the cleanup block MUST put it back. A plan that dies after freezing the
        /// camera and never restores it leaves the game unusable, and no offline run can prove that
        /// in-game - what it can prove is that the shipped file still says so.
        /// </summary>
        private static void AimPlanChecks()
        {
            JObject file = ShippedPlan("aim-and-run.json");
            if (file == null) return;
            JArray steps = file["steps"] as JArray, fin = file["finally"] as JArray;
            if (steps == null || fin == null) { Check("aim-plan-shape", false, "no steps/finally"); return; }

            Func<JArray, bool, JToken> inputStep = (block, want) =>
            {
                foreach (JToken t in block)
                {
                    JObject a = t["args"] as JObject;
                    if (a != null && (string)a["op"] == "set" && (string)a["member"] == "InputDisabled" &&
                        a["value"] != null && (bool)a["value"] == want) return t;
                }
                return null;
            };
            Check("aim-plan-freezes-the-camera", inputStep(steps, true) != null,
                  "no step sets InputDisabled true - without it the real mouse keeps overwriting the cursor");
            Check("aim-plan-restores-input-in-finally", inputStep(fin, false) != null,
                  "the cleanup block does not set InputDisabled back to false");
            Check("aim-plan-restores-before-releasing",
                  (string)fin[0]["id"] == "restore-input",
                  "the restore is not the FIRST cleanup step, so a released handle could strand input disabled");
        }

        /// <summary>A length prefix claiming more than the cap, with nothing behind it.</summary>
        private static byte[] Oversize()
        {
            byte[] frame = new byte[4];
            BitConverter.GetBytes(Wire.MaxFrameBytes + 1).CopyTo(frame, 0);
            return frame;
        }

        private static string Call(string pipe, string json) { return CallRaw(pipe, Wire.Encode(json)); }

        private static string CallRaw(string pipe, byte[] frame)
        {
            using (NamedPipeClientStream c = new NamedPipeClientStream(".", pipe, PipeDirection.InOut))
            {
                c.Connect(10000);
                c.Write(frame, 0, frame.Length);
                c.Flush();
                string error;
                return Wire.Read(c, out error) ?? ("<no reply: " + error + ">");
            }
        }

        private static int Main()
        {
            string error;

            // --- parse: the happy path
            List<Job> jobs = Protocol.Parse(
                "[{\"id\":\"1\",\"verb\":\"ping\"}," +
                "{\"id\":\"2\",\"verb\":\"state\"}," +
                "{\"id\":\"3\",\"verb\":\"console\",\"args\":{\"command\":\"ct_version\",\"args\":[\"a\",1]}}]",
                out error);
            Check("parse-count", jobs.Count == 3, jobs.Count + " job(s)");
            Check("parse-clean", error == null, "" + error);
            Check("parse-args", jobs[2].Args != null && (string)jobs[2].Args["command"] == "ct_version", "args lost");

            // --- parse: the trust boundary. A '|' in an id would forge a second marker field.
            jobs = Protocol.Parse("[{\"id\":\"a|b\",\"verb\":\"ping\"},{\"id\":\"ok\",\"verb\":\"ping\"},{\"id\":\"x\"}]", out error);
            Check("parse-rejects-pipe-id", jobs.Count == 1 && jobs[0].Id == "ok", jobs.Count + " job(s) survived");
            Check("parse-names-reason", error != null, "no reason reported");

            jobs = Protocol.Parse("not json", out error);
            Check("parse-garbage", jobs.Count == 0 && error != null, "" + error);

            StringBuilder big = new StringBuilder("[");
            for (int i = 0; i < Protocol.MaxJobs + 10; i++) big.Append(i > 0 ? "," : "").Append("{\"id\":\"j").Append(i).Append("\",\"verb\":\"ping\"}");
            jobs = Protocol.Parse(big.Append("]").ToString(), out error);
            Check("parse-caps-jobs", jobs.Count == Protocol.MaxJobs && error != null, jobs.Count + " job(s)");

            // --- dispatch
            Protocol.BuildStamp = "deadbeef";
            string ping = Protocol.Marker("1", Protocol.Dispatch(new Job { Id = "1", Verb = "ping" }));
            Check("ping-shape", ping.StartsWith("PPCLI|1|{") && ping.Contains("\"build\":\"deadbeef\"") &&
                                ping.Contains("\"protocol\":\"" + Protocol.Version + "\""), ping);

            string unknown = Protocol.Marker("2", Protocol.Dispatch(new Job { Id = "2", Verb = "nope" }));
            Check("unknown-verb", unknown.Contains("\"ok\":false") && unknown.Contains("nope"), unknown);

            // state and console are refusals until the game half installs them - a null delegate must
            // never become a NullReferenceException that kills the drain loop.
            Check("state-uninstalled", Protocol.Marker("3", Protocol.Dispatch(new Job { Id = "3", Verb = "state" })).Contains("\"ok\":false"), "state threw or passed");

            Protocol.StateProbe = () => new { ok = true, phase = "menu" };
            Check("state-installed", Protocol.Marker("3b", Protocol.Dispatch(new Job { Id = "3b", Verb = "state" })).Contains("\"phase\":\"menu\""), "installed probe not called");

            string sawCommand = null; string[] sawArgs = null;
            Protocol.ConsoleRun = (c, a) => { sawCommand = c; sawArgs = a; return new { ok = true, output = new[] { "line one\nline two" } }; };
            List<Job> one = Protocol.Parse("[{\"id\":\"c1\",\"verb\":\"console\",\"args\":{\"command\":\"ct_version\",\"args\":[\"x\",7]}}]", out error);
            string marker = Protocol.Marker(one[0].Id, Protocol.Dispatch(one[0]));
            Check("console-command", sawCommand == "ct_version", "" + sawCommand);
            Check("console-args", sawArgs != null && sawArgs.Length == 2 && sawArgs[0] == "x" && sawArgs[1] == "7",
                  sawArgs == null ? "null" : string.Join(",", sawArgs));
            Check("console-no-command", Protocol.Marker("c2", Protocol.Dispatch(new Job { Id = "c2", Verb = "console" })).Contains("\"ok\":false"), "empty console args accepted");

            // --- the marker line must survive the log: one line, and the two field separators are
            // the FIRST two pipes, whatever the payload contains.
            Check("marker-single-line", marker.IndexOf('\n') < 0 && marker.IndexOf('\r') < 0, "marker spans lines");
            string[] parts = marker.Split(new[] { '|' }, 3);
            Check("marker-fields", parts.Length == 3 && parts[0] == "PPCLI" && parts[1] == "c1" && parts[2].StartsWith("{"), marker);
            Check("marker-escapes-newline", parts[2].Contains("line one\\nline two"), parts[2]);

            Check("clip", Protocol.Clip(new string('y', Protocol.MaxOutputLineChars + 50)).EndsWith("...(clipped)"), "no clip");

            // --- P1 framing: what goes on the wire must come back off it unchanged, including the
            // characters a length-prefixed protocol exists to survive.
            string payload = "{\"verb\":\"ping\",\"s\":\"рус | \\n   }\"}";
            Check("frame-roundtrip", ReadBack(Wire.Encode(payload)) == payload, "" + ReadBack(Wire.Encode(payload)));
            Check("frame-prefix", BitConverter.ToInt32(Wire.Encode("ab"), 0) == Encoding.UTF8.GetByteCount("ab"), "wrong length prefix");

            // --- P1 framing: the trust boundary. A hostile prefix must cost a named refusal, never
            // an allocation and never an exception on the pipe thread.
            byte[] huge = new byte[8];
            BitConverter.GetBytes(Wire.MaxFrameBytes + 1).CopyTo(huge, 0);
            Check("frame-rejects-oversize", ReadBack(huge, out error) == null && error != null && error.Contains("outside"), "" + error);

            byte[] negative = new byte[8];
            BitConverter.GetBytes(-16).CopyTo(negative, 0);
            Check("frame-rejects-negative", ReadBack(negative, out error) == null && error != null, "negative length accepted");

            byte[] truncated = new byte[6];
            BitConverter.GetBytes(64).CopyTo(truncated, 0);
            Check("frame-rejects-truncated", ReadBack(truncated, out error) == null && error != null && error.Contains("truncated"), "" + error);
            Check("frame-rejects-empty", ReadBack(new byte[0], out error) == null && error != null, "empty stream accepted");

            string tooBig = ReadBack(Wire.Encode(new string('z', Wire.MaxFrameBytes + 100)));
            Check("frame-encode-oversize-is-an-error-not-a-throw", tooBig != null && tooBig.Contains("exceeds the"), "" + tooBig);

            // --- P1 token: the only thing between a local process and this endpoint.
            Check("token-match", Wire.TokenOk("cafebabe", "cafebabe"), "matching token refused");
            Check("token-mismatch", !Wire.TokenOk("cafebabe", "cafebabf"), "wrong token accepted");
            Check("token-short", !Wire.TokenOk("cafebabe", "cafe"), "truncated token accepted");
            Check("token-long", !Wire.TokenOk("cafebabe", "cafebabecafebabe"), "extended token accepted");
            Check("token-null", !Wire.TokenOk("cafebabe", null) && !Wire.TokenOk(null, "x") && !Wire.TokenOk("", ""), "null/empty token accepted");

            // --- P1 discovery file: a crash leaves one behind, and a live-looking stale file would
            // send a client at a pipe nobody is listening on.
            string live = "{\"pipe\":\"ppcli-a-b-4242\",\"pid\":4242,\"token\":\"x\"}";
            Check("endpoint-live", !Wire.IsStale(live, p => p == 4242, out error), "" + error);
            Check("endpoint-stale-pid", Wire.IsStale(live, p => false, out error) && error.Contains("4242"), "" + error);
            Check("endpoint-no-pid", Wire.IsStale("{\"pipe\":\"x\"}", p => true, out error) && error != null, "pidless file trusted");
            int pid;
            Check("endpoint-pid-parse", Wire.TryPid("{\"a\":1,\"pid\": 31337 ,\"b\":2}", out pid) && pid == 31337, "" + pid);
            Check("endpoint-pid-garbage", !Wire.TryPid("{\"pid\":\"nope\"}", out pid), "string pid accepted");

            // --- P1 projection: the result is frozen to JSON on the main thread and must embed
            // verbatim, not as an escaped string, when the pipe thread wraps it.
            string wrapped = Protocol.Compact(new { status = "done", result = Protocol.Reproject(new { ok = true, phase = "menu" }) });
            Check("reproject-embeds-raw", wrapped.Contains("\"result\":{\"ok\":true,\"phase\":\"menu\"}"), wrapped);

            ReflectChecks();
            PlanChecks();
            ShotChecks();
            PipeChecks();

            Console.WriteLine(failures == 0 ? "ppcli selfcheck: PASS" : "ppcli selfcheck: " + failures + " FAILURE(S)");
            return failures == 0 ? 0 : 1;
        }
    }
}
