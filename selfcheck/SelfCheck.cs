using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
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
        // The two shapes a value dump used to drop SILENTLY: a null field and a non-scalar one.
        public string Missing = null;   // = null only to silence CS0649; the dump must SAY it is null
        public List<int> Tags = new List<int> { 1, 2 };
    }

    /// <summary>A struct with an implicit conversion to a scalar - ModifiableValue's shape.</summary>
    internal struct Scal
    {
        public float v;
        public Scal(float v) { this.v = v; }
        public static implicit operator float(Scal s) { return s.v * 2f; }
    }

    internal class Ov
    {
        // = 0 only to silence CS0649: this field is written by the binder, never by C# code here.
        public int Field = 0;
        public Scal Scalar = new Scal(2.5f);
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

        /// <summary>
        /// THE PAIR THAT SEPARATES CHARACTERS FROM BYTES. Both return the same NUMBER OF CHARACTERS -
        /// 100 x 500 = 50000, under the 65536 cap when counted as chars - but 'я' is two bytes in
        /// UTF-8, so only one of them is over the cap in BYTES. A cap that measured chars would let
        /// both through; Reflect.cs:182 measures Encoding.UTF8.GetByteCount, so exactly one must be
        /// refused. Without the ASCII half the Cyrillic half proves nothing: a payload can always be
        /// refused for being too long in any unit.
        ///
        /// MANY SHORT strings rather than a few long ones, and that is not arbitrary: individual
        /// strings are clipped before the response is measured, so 4000-char elements never reach the
        /// byte cap at all - the first draft of this pair used them and the Cyrillic half came back a
        /// comfortable 20245 chars, passing a check that was testing nothing. 500 is well under the
        /// clip.
        /// </summary>
        public static List<string> WideAscii()
        {
            List<string> big = new List<string>();
            for (int i = 0; i < 100; i++) big.Add(new string('q', 500));
            return big;
        }

        public static List<string> WideCyrillic()
        {
            List<string> big = new List<string>();
            for (int i = 0; i < 100; i++) big.Add(new string('я', 500));
            return big;
        }
    }

    // --- events fixtures: DeathReport's shape (public fields, one a nested object) and the three
    // delegate shapes the tap must handle - a custom void delegate, a generic Action, a static event -
    // plus the one it must refuse (a non-void delegate).
    internal class FakeReport
    {
        public FakeDef Actor;
        public FakeDef Killer;
        public bool FromFire;
        public bool FromFall;
        public V3 Force;
        public Season When;
    }

    internal delegate void FakeDeathHandler(FakeReport report);
    internal delegate void WideHandler(int a, int b, int c, int d, int e, int f, int g, int h, int i, int j);

    internal class Emitter
    {
        public event FakeDeathHandler Death;
        public event Action<int, string> Pair;
        public event Func<int> Returns;
        public static event Action<string> StaticPing;

        public void Die(FakeReport r) { Death?.Invoke(r); }
        public void Both(int n, string s) { Pair?.Invoke(n, s); }
        public static void Ping(string s) { StaticPing?.Invoke(s); }
        public int DeathHandlers { get { return Death == null ? 0 : Death.GetInvocationList().Length; } }

        // C+D review fixtures: a 10-arg event, a fat-arg event, a remove accessor that throws.
        public event WideHandler Many;
        public event Action<string> Big;
        private Action<int> sticky;
        public bool RefuseRemove = true;
        public event Action<int> Sticky { add { sticky += value; } remove { if (RefuseRemove) throw new InvalidOperationException("remove refused"); sticky -= value; } }
        public void FireMany() { Many?.Invoke(1, 2, 3, 4, 5, 6, 7, 8, 9, 10); }
        public void FireBig(string s) { Big?.Invoke(s); }
        public void FireSticky(int n) { sticky?.Invoke(n); }
        public int StickyHandlers { get { return sticky == null ? 0 : sticky.GetInvocationList().Length; } }
        public int ReturnsHandlers { get { return Returns == null ? 0 : Returns.GetInvocationList().Length; } }
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
            // A struct's default instance needs no ctor, but a sig naming one that does not exist must
            // still refuse - the Activator shortcut used to skip the sig and hand back default(V3).
            string v3Default = R("call", "{'op':'new','type':'Morgott.PPBridge.V3','args':[]}");
            Check("struct-new-no-args-is-the-default", v3Default.Contains("\"ok\":true"), v3Default);
            string v3EmptySig = R("call", "{'op':'new','type':'Morgott.PPBridge.V3','args':[],'sig':[]}");
            Check("struct-new-empty-sig-is-the-default", v3EmptySig.Contains("\"ok\":true"), v3EmptySig);
            string v3BadSig = R("call", "{'op':'new','type':'Morgott.PPBridge.V3','args':[],'sig':['Int32']}");
            Check("struct-new-sig-without-a-match-refuses", v3BadSig.Contains("\"code\":\"overload\""), v3BadSig);
            string scalNoCtor = R("call", "{'op':'new','type':'Morgott.PPBridge.Scal','sig':['Single','Single']}");
            Check("struct-new-sig-arity-mismatch-refuses", scalNoCtor.Contains("\"ok\":false"), scalNoCtor);

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

            // ...and that the cap counts BYTES, not characters. The two payloads carry the SAME 50000
            // characters; only the Cyrillic one is over 65536 UTF-8 bytes. One passes and one is
            // refused, so the assertion is about the unit rather than about length - see Ov.WideAscii.
            string wideOk = R("items", "{'h':'" + Reflect.Track(Ov.WideAscii()) + "','pageSize':100}");
            string wideBig = R("items", "{'h':'" + Reflect.Track(Ov.WideCyrillic()) + "','pageSize':100}");
            Check("dto-cap-lets-the-same-length-through-in-ascii", !wideOk.Contains("\"code\":\"cap\"") && wideOk.Contains("\"returned\":100"),
                  "50000 ASCII chars were refused, so the multi-byte half proves nothing: " + wideOk.Length);
            Check("dto-cap-counts-utf8-bytes-not-chars", wideBig.Contains("\"code\":\"cap\""),
                  "50000 two-byte chars (100000 bytes) passed a 65536-BYTE cap: " + wideBig.Length);

            // --- discovery verbs.
            Check("types-finds-a-type", R("types", "{'pattern':'Morgott.PPBridge.Ov'}").Contains(OvType),
                  R("types", "{'pattern':'Morgott.PPBridge.Ov'}"));
            Check("types-needs-a-pattern", R("types", "{}").Contains("\"code\":\"args\""), "an empty pattern was accepted");
            string ovTypes = R("types", "{'pattern':'Morgott.PPBridge.Ov'}");
            Check("types-hides-compiler-generated", !ovTypes.Contains("<Lazy>") && ovTypes.Contains("\"hidden\":"), ovTypes);
            Check("types-generated-on-request", R("types", "{'pattern':'Morgott.PPBridge.Ov','generated':true}").Contains("<Lazy>"),
                  R("types", "{'pattern':'Morgott.PPBridge.Ov','generated':true}"));
            string tp0 = R("types", "{'pattern':'Morgott.PPBridge.','pageSize':1}");
            string tp1 = R("types", "{'pattern':'Morgott.PPBridge.','page':1,'pageSize':1}");
            Check("types-pages", tp0.Contains("\"hasMore\":true") && tp0 != tp1 && !tp0.Contains("\"truncated\""), tp0);
            Check("types-page-size-refused-not-clamped", R("types", "{'pattern':'Ov','pageSize':101}").Contains("\"code\":\"args\""),
                  R("types", "{'pattern':'Ov','pageSize':101}"));
            string members = R("members", "{'type':'" + OvType + "'}");
            Check("members-lists-methods", members.Contains("M static String TakeV3(V3 v)"), members.Substring(0, Math.Min(400, members.Length)));
            Check("members-hides-framework-inherited-by-default", !members.Contains("<Object>") && members.Contains("\"hidden\":"), members);
            string membersAll = R("members", "{'type':'" + OvType + "','inherited':true,'pageSize':400}");
            Check("members-lists-inherited-on-request", membersAll.Contains("<Object>"), "no inherited member reported");
            Check("members-default-page-is-small", R("members", "{'type':'" + OvType + "','inherited':true}").Contains("\"pageSize\":50"), "default page is not 50");
            Check("members-no-truncated-twin", !members.Contains("\"truncated\""), members);
            Check("members-filter", R("members", "{'type':'" + OvType + "','filter':'TakeV3'}").Contains("\"count\":1"),
                  R("members", "{'type':'" + OvType + "','filter':'TakeV3'}"));
            string inspect = R("inspect", "{'h':'" + ov + "'}");
            Check("inspect-describes-the-handle", inspect.Contains("\"type\":\"" + OvType + "\"") && inspect.Contains("\"self\":"), inspect.Substring(0, Math.Min(300, inspect.Length)));

            Protocol.AllDefs = () => new List<object> { new FakeDef { Guid = "g1", name = "hello_def" }, new FakeDef { Guid = "g2", name = "other" } };
            Check("find-by-name", R("find", "{'query':'hello'}").Contains("\"name\":\"hello_def\""), R("find", "{'query':'hello'}"));
            Check("find-guid-is-opt-in", !R("find", "{'query':'hello'}").Contains("\"guid\"") &&
                  R("find", "{'query':'hello','guids':true}").Contains("\"guid\":\"g1\""), R("find", "{'query':'hello','guids':true}"));
            Check("find-query-pages", R("find", "{'query':'e','pageSize':1}").Contains("\"hasMore\":true") &&
                  R("find", "{'query':'e','page':1,'pageSize':1}").Contains("\"name\":\"other\""), R("find", "{'query':'e','page':1,'pageSize':1}"));
            Check("find-query-page-size-refused-not-clamped", R("find", "{'query':'e','pageSize':101}").Contains("\"code\":\"args\""),
                  R("find", "{'query':'e','pageSize':101}"));
            Check("find-by-guid", R("find", "{'query':'g2'}").Contains("\"name\":\"other\""), R("find", "{'query':'g2'}"));
            Check("find-type-filter", R("find", "{'query':'hello','type':'Morgott.PPBridge.FakeDef'}").Contains("\"count\":1"),
                  R("find", "{'query':'hello','type':'Morgott.PPBridge.FakeDef'}"));
            Check("find-needs-a-query", R("find", "{}").Contains("\"code\":\"args\""), "an empty query was accepted");

            // --- a def is addressable BY NAME, so reading one of its fields is ONE round trip.
            Check("def-target-by-name", R("inspect", "{'h':'@def:hello_def'}").Contains("\"type\":\"Morgott.PPBridge.FakeDef\""),
                  R("inspect", "{'h':'@def:hello_def'}"));
            Check("def-target-by-guid", R("inspect", "{'h':'@def:g1'}").Contains("\"ok\":true"), R("inspect", "{'h':'@def:g1'}"));
            Check("def-target-unknown-name-points-at-find", R("inspect", "{'h':'@def:hello'}").Contains("use `find`"),
                  R("inspect", "{'h':'@def:hello'}"));
            Check("def-envelope-takes-a-name",
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'TakeDef','args':[{'$def':'other'}]}").Contains("\"value\":\"other\""),
                  R("call", "{'op':'invoke','type':'" + OvType + "','member':'TakeDef','args':[{'$def':'other'}]}"));

            // --- the value dump: FIELDS only, no handles, so two runs diff mechanically. EVERY field
            // appears - a silent omission makes "unchanged" and "not observed" identical in a diff.
            string dump = R("inspect", "{'h':'@def:hello_def','values':true}");
            Check("inspect-values-dumps-fields", dump.Contains("\"Guid\":\"g1\"") && dump.Contains("\"name\":\"hello_def\""), dump);
            Check("inspect-values-says-null-out-loud", dump.Contains("\"Missing\":null"), dump);
            Check("inspect-values-marks-a-non-scalar-field",
                  dump.Contains("\"$omitted\":\"List<Int32>\"") && dump.Contains("\"count\":2"), dump);
            Check("inspect-values-is-byte-identical-on-a-second-read",
                  dump == R("inspect", "{'h':'@def:hello_def','values':true}"), dump);
            Check("inspect-values-drops-the-member-list", !dump.Contains("\"members\""), dump);
            Check("inspect-values-needs-an-instance", R("members", "{'type':'" + OvType + "','values':true}").Contains("\"code\":\"args\""),
                  R("members", "{'type':'" + OvType + "','values':true}"));

            // --- member discovery: a glob, and PAGING instead of a silent cut at the cap.
            Check("members-glob-filter", R("members", "{'type':'" + OvType + "','filter':'*TakeV3*'}").Contains("\"count\":1"),
                  R("members", "{'type':'" + OvType + "','filter':'*TakeV3*'}"));
            Check("members-glob-that-matches-nothing",
                  R("members", "{'type':'" + OvType + "','filter':'*NoSuchMember*'}").Contains("\"count\":0"),
                  R("members", "{'type':'" + OvType + "','filter':'*NoSuchMember*'}"));
            string mp0 = R("members", "{'type':'" + OvType + "','pageSize':1}");
            string mp1 = R("members", "{'type':'" + OvType + "','page':1,'pageSize':1}");
            Check("members-pages", mp0.Contains("\"count\":1") && mp0.Contains("\"hasMore\":true") && !mp0.Contains("\"total\":1"), mp0);
            Check("members-page-1-is-a-different-member", mp0 != mp1 && mp1.Contains("\"page\":1"), mp1);
            Check("members-past-the-end", R("members", "{'type':'" + OvType + "','page':9999,'pageSize':1}").Contains("\"count\":0"),
                  R("members", "{'type':'" + OvType + "','page':9999,'pageSize':1}"));

            // --- a user-defined conversion runs ONLY when the caller names the destination type.
            // Running an operator is running arbitrary user code, so a plain `get` must not do it.
            string scal = R("call", "{'op':'get','target':'" + ov + "','member':'Scalar'}");
            Check("a-plain-get-runs-no-conversion-operator", scal.Contains("\"v\":2.5") && !scal.Contains("$scalar") && !scal.Contains("converted"), scal);
            string conv = R("call", "{'op':'get','target':'" + ov + "','member':'Scalar','convertTo':'System.Single'}");
            Check("convert-to-runs-the-operator-on-request", conv.Contains("\"v\":2.5") && conv.Contains("\"converted\":5"), conv);
            Check("convert-to-an-impossible-type-refuses",
                  R("call", "{'op':'get','target':'" + ov + "','member':'Scalar','convertTo':'System.DateTime'}").Contains("\"code\":\"convert\""),
                  R("call", "{'op':'get','target':'" + ov + "','member':'Scalar','convertTo':'System.DateTime'}"));

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
            Check("find-all-guids-on-request", !allPage0.Contains("\"guid\"") &&
                  R("find", "{'all':true,'guids':true}").Contains("\"guid\":\"g1\""), R("find", "{'all':true,'guids':true}"));

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

        private static string Console1(string json)
        {
            return Protocol.Compact(Protocol.Dispatch(new Job { Id = "k", Verb = "console", Args = JObject.Parse(json) }));
        }

        /// <summary>
        /// Stage B: a console command RUNS ONCE, the reply is a small first page plus a cursor, the
        /// cursor pages the SAME capture (the command is never re-run), and an expired or unknown
        /// cursor is a named refusal. Pages are bounded by lines AND by UTF-8 bytes.
        /// </summary>
        private static void ConsolePagerChecks()
        {
            ConsolePager.Reset();
            int runs = 0;
            List<string> big = new List<string>();
            for (int i = 0; i < 1000; i++) big.Add("line " + i + " " + new string('x', 60));
            Protocol.ConsoleRun = (c, a) =>
            {
                runs++;
                if (c == "boom") return new { ok = false, output = new[] { "partial" }, error = "NullReferenceException: x" };
                if (c == "wide") { List<string> w = new List<string>(); for (int i = 0; i < 100; i++) w.Add(new string('я', 1500)); return new { ok = true, output = w.ToArray(), truncated = false }; }
                if (c == "few") return new { ok = true, output = new[] { "a", "b" }, truncated = false };
                if (c == "boomBig") return new { ok = false, output = big.ToArray(), error = "NullReferenceException: after 1000 lines" };
                if (c == "boomFat") return new { ok = false, output = new[] { "partial" }, error = new string('\u0001', 1900) };
                if (c == "long") return new { ok = true, output = new[] { Protocol.Clip(new string('q', 5000)), "short" }, truncated = false };
                return new { ok = true, output = big.ToArray(), truncated = false };
            };

            string first = Console1("{'command':'many'}");
            JObject f = JObject.Parse(first);
            Check("console-first-page-is-small", ((JArray)f["output"]).Count == ConsolePager.DefaultPageLines &&
                  (int)f["total"] == 1000 && (bool)f["hasMore"] && f["cursor"] != null, first.Substring(0, 200));
            Check("console-false-truncated-is-left-out", f["truncated"] == null, first.Substring(0, 200));
            Check("console-first-page-under-the-byte-default", Encoding.UTF8.GetByteCount(first) <= ConsolePager.DefaultPageBytes + 256,
                  "bytes=" + Encoding.UTF8.GetByteCount(first));

            string cur = (string)f["cursor"];
            string second = Console1("{'cursor':'" + cur + "','pageLines':100}");
            JObject s = JObject.Parse(second);
            Check("console-cursor-pages-the-same-capture", runs == 1 && (string)((JArray)s["output"])[0] == big[50] &&
                  (int)s["offset"] == 50, second.Substring(0, Math.Min(200, second.Length)));

            // Drain it. The last page frees the snapshot, and the drained cursor is then unknown.
            string last = null; string c2 = (string)s["cursor"]; int reads = 0; int seen = 50 + ((JArray)s["output"]).Count;
            while (c2 != null && reads++ < 100)
            {
                last = Console1("{'cursor':'" + c2 + "','pageLines':2000,'pageBytes':196608}");
                JObject l = JObject.Parse(last);
                seen += ((JArray)l["output"]).Count;
                c2 = (string)l["cursor"];
            }
            Check("console-cursor-reaches-the-end", seen == 1000 && last.Contains("\"hasMore\":false") && runs == 1, "seen=" + seen + " runs=" + runs);
            Check("console-drained-snapshot-is-freed", ConsolePager.Live == 0, "live=" + ConsolePager.Live);
            string gone = Console1("{'cursor':'" + cur + "'}");
            Check("console-unknown-cursor-refused", gone.Contains("\"ok\":false") && gone.Contains("\"code\":\"cursor\"") && runs == 1, gone);
            Check("console-garbage-cursor-refused", Console1("{'cursor':'nope'}").Contains("\"code\":\"cursor\""), Console1("{'cursor':'nope'}"));

            // TTL: a cursor read after 120 s of silence is refused and NOTHING is re-run.
            DateTime t0 = DateTime.UtcNow;
            ConsolePager.Now = () => t0;
            string fresh = (string)JObject.Parse(Console1("{'command':'many'}"))["cursor"];
            ConsolePager.Now = () => t0.AddSeconds(ConsolePager.TtlSeconds + 1);
            string expired = Console1("{'cursor':'" + fresh + "'}");
            Check("console-expired-cursor-refused", expired.Contains("\"code\":\"cursor\"") && expired.Contains("Nothing was re-run") && runs == 2, expired);
            ConsolePager.Now = () => DateTime.UtcNow;

            // At most four snapshots; the fifth run evicts the oldest.
            ConsolePager.Reset();
            string oldest = (string)JObject.Parse(Console1("{'command':'many'}"))["cursor"];
            for (int i = 0; i < ConsolePager.MaxSnapshots; i++) Console1("{'command':'many'}");
            Check("console-snapshot-count-is-bounded", ConsolePager.Live == ConsolePager.MaxSnapshots, "live=" + ConsolePager.Live);
            Check("console-oldest-snapshot-evicted", Console1("{'cursor':'" + oldest + "'}").Contains("\"code\":\"cursor\""), "oldest survived");

            // BYTES, not lines: 1500 two-byte chars a line is ~3 KB, so the 8 KB default holds 2.
            string wide = Console1("{'command':'wide'}");
            Check("console-page-bounded-by-utf8-bytes", ((JArray)JObject.Parse(wide)["output"]).Count == 2 &&
                  Encoding.UTF8.GetByteCount(wide) <= ConsolePager.DefaultPageBytes + 256, "bytes=" + Encoding.UTF8.GetByteCount(wide));
            string one = Console1("{'command':'wide','pageBytes':1024}");
            Check("console-page-never-empty", ((JArray)JObject.Parse(one)["output"]).Count == 1, one.Substring(0, 120));

            string small = Console1("{'command':'few'}");
            Check("console-short-output-has-no-cursor", !small.Contains("cursor") && small.Contains("\"hasMore\":false"), small);
            string failed = Console1("{'command':'boom'}");
            Check("console-failure-keeps-its-output", failed.Contains("\"ok\":false") && failed.Contains("partial") && failed.Contains("NullReference"), failed);

            Check("console-page-lines-refused-not-clamped", Console1("{'command':'few','pageLines':0}").Contains("\"code\":\"args\"") &&
                  Console1("{'command':'few','pageLines':2001}").Contains("\"code\":\"args\""), Console1("{'command':'few','pageLines':0}"));
            Check("console-page-bytes-refused-not-clamped", Console1("{'command':'few','pageBytes':196609}").Contains("\"code\":\"args\""),
                  Console1("{'command':'few','pageBytes':196609}"));
            Check("console-command-and-cursor-refused", Console1("{'command':'few','cursor':'" + oldest + "'}").Contains("\"code\":\"args\""), "both accepted");

            // --- Codex review of A+B.
            // A run that printed pages and THEN threw: every page says so, not only the first.
            string bf = Console1("{'command':'boomBig'}");
            JObject bfo = JObject.Parse(bf);
            string bn = Console1("{'cursor':'" + (string)bfo["cursor"] + "'}");
            Check("console-error-on-every-page", bf.Contains("\"ok\":false") && bn.Contains("\"ok\":false") && bn.Contains("NullReference") && !bn.Contains("\"ok\":true"), bn.Substring(0, Math.Min(200, bn.Length)));
            // pageBytes is the WHOLE reply, and a line too big for it is clipped + counted, not let through.
            string w1 = Console1("{'command':'wide','pageBytes':1024}");
            Check("console-page-bytes-is-the-whole-reply", B8(w1) <= 1024 && (int)JObject.Parse(w1)["clipped"] == 1, "bytes=" + B8(w1));
            string wc = (string)JObject.Parse(w1)["cursor"]; int wreads = 0, wmax = 0, wseen = 1;
            while (wc != null && wreads++ < 200)
            {
                string wp = Console1("{'cursor':'" + wc + "','pageBytes':1024}");
                wmax = Math.Max(wmax, B8(wp)); wseen += ((JArray)JObject.Parse(wp)["output"]).Count;
                wc = (string)JObject.Parse(wp)["cursor"];
            }
            Check("console-every-page-within-page-bytes", wmax <= 1024 && wseen == 100, "max=" + wmax + " seen=" + wseen);
            string fatErr = Console1("{'command':'boomFat','pageBytes':1024}");
            Check("console-fat-error-still-within-page-bytes", B8(fatErr) <= 1024 && fatErr.Contains("\"ok\":false"), "bytes=" + B8(fatErr));
            // The 2000-char capture clip is data loss the reply must name.
            string lng = Console1("{'command':'long'}");
            Check("console-capture-clip-is-flagged", (int?)JObject.Parse(lng)["clipped"] == 1, lng.Substring(0, 100));
            Check("console-unclipped-page-has-no-clip-flag", !Console1("{'command':'few'}").Contains("clipped"), "clipped on a short page");
            // The cursor is a server-held token: an edited one is refused, never an empty/skipped page.
            string tc = (string)JObject.Parse(Console1("{'command':'many'}"))["cursor"];
            string tampered = tc.Contains(":") ? tc.Substring(0, tc.IndexOf(':') + 1) + "1000"
                                              : tc.Substring(0, tc.Length - 1) + (tc.EndsWith("0") ? "1" : "0");
            string tr2 = Console1("{'cursor':'" + tampered + "'}");
            Check("console-tampered-cursor-refused", tr2.Contains("\"code\":\"cursor\"") && !tc.Contains(":"), tr2.Substring(0, Math.Min(160, tr2.Length)));
            Check("console-cursor-reread-is-idempotent", Console1("{'cursor':'" + tc + "'}") == Console1("{'cursor':'" + tc + "'}"), "same cursor, different page");
            // fire-event.json: its SHIPPED trigger + receipt-complete steps, verbatim. A receipt that
            // overran one page must fail the plan, not publish a partial `consoleOutput`.
            string pdir = null;
            for (DirectoryInfo d = new DirectoryInfo(AppContext.BaseDirectory); d != null && pdir == null; d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, "plans"))) pdir = Path.Combine(d.FullName, "plans");
            JArray fsteps = (JArray)JObject.Parse(File.ReadAllText(Path.Combine(pdir, "fire-event.json")))["steps"];
            JArray pair = new JArray();
            foreach (JToken st in fsteps) if ((string)st["id"] == "trigger" || (string)st["id"] == "receipt-complete") pair.Add(st.DeepClone());
            Func<string, string> fire = cmd =>
            {
                pair[0]["args"]["command"] = cmd;                   // the stand-in runner's command name
                return Run("plan", new JObject { { "plan", new JObject { { "vars", new JObject { { "eventId", "X" } } }, { "steps", pair } } } }
                    .ToString(Newtonsoft.Json.Formatting.None));
            };
            // 2500 lines is past the widest page (2000 lines), so the receipt cannot be whole.
            List<string> huge = new List<string>(); for (int i = 0; i < 2500; i++) huge.Add("site " + i);
            Func<string, string[], object> prev = Protocol.ConsoleRun;
            Protocol.ConsoleRun = (c, a) => c == "huge" ? new { ok = true, output = huge.ToArray(), truncated = false } : prev(c, a);
            string incomplete = fire("huge"), whole = fire("few");
            Protocol.ConsoleRun = prev;
            Check("fire-event-partial-receipt-fails", pair.Count == 2 && incomplete.Contains("\"ok\":false") && incomplete.Contains("receipt-incomplete"),
                  incomplete.Substring(0, Math.Min(300, incomplete.Length)));
            Check("fire-event-whole-receipt-passes", whole.Contains("\"ok\":true"), whole.Substring(0, Math.Min(300, whole.Length)));
            foreach (string junk in new[] { "'60'", "true", "1.5" })
                Check("console-page-lines-strict-int-" + junk.Trim('\''), Console1("{'command':'few','pageLines':" + junk + "}").Contains("\"code\":\"args\""),
                      Console1("{'command':'few','pageLines':" + junk + "}"));
            ConsolePager.Reset();
            Protocol.ConsoleRun = null;
        }

        private static int B8(string s) { return Encoding.UTF8.GetByteCount(s); }

        private static string V(string verb, string json)
        {
            return Protocol.Compact(Protocol.Dispatch(new Job { Id = "t", Verb = verb, Args = JObject.Parse(json) }));
        }

        /// <summary>Starts a cross-frame job and hands back the IPending (or the refusal DTO).</summary>
        private static object Start(string verb, string json)
        {
            return Protocol.Dispatch(new Job { Id = "w", Verb = verb, Args = JObject.Parse(json) });
        }

        /// <summary>
        /// Stage C: the log ring - seq numbers, tail vs delta reads, dropped count, level + regex
        /// filters, clip and stack opt-in, the bridge's own markers skipped, thread-safe appends, the
        /// `wait {log}` predicate, and the byte budget of a default page and of an empty poll.
        /// </summary>
        // ------------------------------------------------------------------ imgui (pure half)

        private static int imFrame;
        private static bool imIdle = true;

        /// <summary>One simulated OnGUI frame: Layout (skipped by the patch, so never observed), an
        /// optional input event, then Repaint - each pass visiting the same controls in draw order.
        /// Returns the labels whose press was forced.</summary>
        private static List<string> ImFrame(string[] labels, bool[] enabled, string inputEv, string owner)
        {
            List<string> forced = new List<string>();
            foreach (string ev in inputEv == null ? new[] { "Repaint" } : new[] { inputEv, "Repaint" })
                for (int i = 0; i < labels.Length; i++)
                    if (ImGuiTap.Observe(imFrame, ev == "Repaint", ev, labels[i], i * 10, 5, 100, 20,
                                         enabled == null || enabled[i], false, false, () => owner, imIdle))
                        forced.Add(ev + ":" + labels[i] + "#" + i);
            return forced;
        }

        /// <summary>Runs a cross-frame imgui request: Update (Tick) then OnGUI, frame by frame.</summary>
        private static string ImRun(string json, string[] labels, bool[] enabled, string inputEv, List<string> forced, int frames = 50)
        {
            object r = Protocol.Dispatch(new Job { Id = "t", Verb = "imgui", Args = JObject.Parse(json) });
            IPending p = r as IPending;
            if (p == null) return Protocol.Compact(r);
            for (int f = 0; f < frames; f++)
            {
                imFrame++;
                object done = p.Tick(false);
                if (done != null) return Protocol.Compact(done);
                forced.AddRange(ImFrame(labels, enabled, inputEv, "Mods.Bench.BenchUI"));
            }
            return "never finished";
        }

        private static void ImGuiChecks()
        {
            int arms = 0, disarms = 0;
            ImGuiTap.Shutdown();
            Check("imgui-offline-refuses", V("imgui", "{'list':true}").Contains("\"code\":\"imgui\""), V("imgui", "{'list':true}"));
            ImGuiTap.Arm = on => { if (on) arms++; else disarms++; return null; };
            ImGuiTap.FrameNow = () => imFrame;
            imFrame = 100;
            string[] ui = { "Run bench", "Stop", "Run bench", "Export" };
            List<string> forced = new List<string>();

            Check("imgui-args-neither", V("imgui", "{}").Contains("\"code\":\"args\""), V("imgui", "{}"));
            Check("imgui-args-both", V("imgui", "{'list':true,'press':{'label':'x'}}").Contains("\"code\":\"args\""), "both accepted");
            Check("imgui-args-strict-int", V("imgui", "{'list':true,'pageSize':'5'}").Contains("\"code\":\"args\"") &&
                  V("imgui", "{'list':true,'pageSize':201}").Contains("\"code\":\"args\"") &&
                  V("imgui", "{'press':{'label':'x','index':1.5}}").Contains("\"code\":\"args\"") &&
                  V("imgui", "{'press':{'label':'x'},'waitFrames':0}").Contains("\"code\":\"args\""), "non-strict int accepted");
            Check("imgui-args-label-string", V("imgui", "{'press':{'label':3}}").Contains("\"code\":\"args\""), "numeric label accepted");
            Check("imgui-args-refusal-does-not-patch", arms == 0, "arms=" + arms);

            // list: Layout never observed; rows from ONE complete Repaint pass; i = nth same label.
            JObject l = JObject.Parse(ImRun("{'list':true}", ui, null, null, forced));
            JArray rows = (JArray)l["rows"];
            Check("imgui-list-rows", (int)l["total"] == 4 && rows.Count == 4 && (string)rows[2]["l"] == "Run bench" && (int)rows[2]["i"] == 1 &&
                  rows[0]["i"] == null && (string)rows[0]["o"] == "BenchUI" && ((JArray)rows[1]["r"]).Count == 4, l.ToString(Newtonsoft.Json.Formatting.None));
            Check("imgui-list-unpatches", arms == 1 && disarms >= 1 && !ImGuiTap.Active, "arms=" + arms + " disarms=" + disarms);
            JObject m = JObject.Parse(ImRun("{'list':true,'match':'BENCH','pageSize':1}", ui, null, null, forced));
            Check("imgui-list-match-page", (int)m["total"] == 2 && ((JArray)m["rows"]).Count == 1 && (bool)m["hasMore"], m.ToString(Newtonsoft.Json.Formatting.None));
            JObject o = JObject.Parse(ImRun("{'list':true,'owner':'Other'}", ui, null, null, forced));
            Check("imgui-list-owner-filter", (int)o["total"] == 0, o.ToString(Newtonsoft.Json.Formatting.None));
            JObject none = JObject.Parse(ImRun("{'list':true}", new string[0], null, null, forced));
            Check("imgui-list-no-ongui", (bool)none["ok"] && (int)none["total"] == 0, none.ToString(Newtonsoft.Json.Formatting.None));
            Check("imgui-list-never-fires", forced.Count == 0, string.Join(",", forced));

            // press: ambiguous label without index -> candidates, nothing fired.
            string amb = ImRun("{'press':{'label':'Run bench'}}", ui, null, null, forced);
            Check("imgui-press-ambiguous", amb.Contains("\"code\":\"ambiguous\"") && JObject.Parse(amb)["candidates"].Count() == 2 && forced.Count == 0, amb);
            string nf = ImRun("{'press':{'label':'run bench'}}", ui, null, null, forced);
            Check("imgui-press-notfound-exact-case", nf.Contains("\"code\":\"notfound\"") && nf.Contains("\"labels\":[\"Run bench\",\"Stop\",\"Export\"]"), nf);

            // press by index: fires ONCE, on the matched instance only, first non-Layout event.
            string ok = ImRun("{'press':{'label':'Run bench','index':1}}", ui, null, null, forced);
            Check("imgui-press-fires-once", ok.Contains("\"ok\":true") && ok.Contains("\"fired\":true") && forced.Count == 1 && forced[0] == "Repaint:Run bench#2", ok + " " + string.Join(",", forced));
            Check("imgui-press-unpatches", !ImGuiTap.Active && disarms == arms, "arms=" + arms + " disarms=" + disarms);
            forced.Clear();
            string ev = ImRun("{'press':{'label':'Stop','owner':'benchui'}}", ui, null, "MouseMove", forced);
            Check("imgui-press-input-event-first", ev.Contains("\"ev\":\"MouseMove\"") && forced.Count == 1 && forced[0] == "MouseMove:Stop#1", ev + " " + string.Join(",", forced));
            // P1 double action: a real MouseDown/MouseUp pass is never forced (the native MouseUp would
            // run the body again); the press waits for Repaint. hotControl held -> never fires.
            forced.Clear();
            string md = ImRun("{'press':{'label':'Stop'}}", ui, null, "MouseDown", forced);
            Check("imgui-press-skips-mousedown", md.Contains("\"ev\":\"Repaint\"") && forced.Count == 1 && forced[0] == "Repaint:Stop#1", md + " " + string.Join(",", forced));
            forced.Clear();
            string mu = ImRun("{'press':{'label':'Stop'}}", ui, null, "MouseUp", forced);
            Check("imgui-press-skips-mouseup", mu.Contains("\"ev\":\"Repaint\"") && forced.Count == 1 && forced[0] == "Repaint:Stop#1", mu + " " + string.Join(",", forced));
            forced.Clear();
            imIdle = false;
            string hot = ImRun("{'press':{'label':'Stop'},'waitFrames':5}", ui, null, null, forced);
            imIdle = true;
            Check("imgui-press-not-while-hotcontrol", hot.Contains("\"code\":\"notfired\"") && forced.Count == 0, hot);
            Check("imgui-safe-pass", ImGuiTap.SafePass("Repaint", true) && ImGuiTap.SafePass("MouseMove", true) && !ImGuiTap.SafePass("MouseDown", true) &&
                  !ImGuiTap.SafePass("MouseUp", true) && !ImGuiTap.SafePass("Used", true) && !ImGuiTap.SafePass("KeyDown", true) && !ImGuiTap.SafePass("Repaint", false), "safe pass set");
            // P1 owner bound at resolve: after it, "Stop" i=0 is drawn by ANOTHER type -> no fire.
            forced.Clear();
            IPending po = (IPending)Protocol.Dispatch(new Job { Id = "t", Verb = "imgui", Args = JObject.Parse("{'press':{'label':'Stop','owner':'mods.bench.benchui'},'waitFrames':5}") });
            string pos = null;
            for (int f = 0; f < 30 && pos == null; f++)
            {
                imFrame++;
                object d = po.Tick(false);
                if (d != null) { pos = Protocol.Compact(d); break; }
                forced.AddRange(ImFrame(ui, null, null, f == 0 ? "Mods.Bench.BenchUI" : "Other.Panel"));
            }
            Check("imgui-press-bound-to-owner", pos != null && pos.Contains("\"code\":\"notfired\"") && forced.Count == 0, pos + " " + string.Join(",", forced));
            // P1 scene unload drops an armed press.
            forced.Clear();
            IPending ps = (IPending)Protocol.Dispatch(new Job { Id = "t", Verb = "imgui", Args = JObject.Parse("{'press':{'label':'Stop'}}") });
            string sc = null;
            for (int f = 0; f < 30 && sc == null; f++)
            {
                imFrame++;
                if (f == 2) ImGuiTap.SceneUnloaded();
                object d = ps.Tick(false);
                if (d != null) { sc = Protocol.Compact(d); break; }
                if (f < 2) { forced.AddRange(ImFrame(new[] { "Export" }, null, null, "X")); if (f == 0) forced.AddRange(ImFrame(ui, null, null, "X")); }
                else forced.AddRange(ImFrame(ui, null, null, "X"));
            }
            Check("imgui-press-scene-unload", sc != null && sc.Contains("\"code\":\"scene\"") && forced.Count == 0 && !ImGuiTap.Active, sc + " " + string.Join(",", forced));
            forced.Clear();
            string dis = ImRun("{'press':{'label':'Export'}}", ui, new[] { true, true, true, false }, null, forced);
            Check("imgui-press-disabled", dis.Contains("\"code\":\"disabled\"") && forced.Count == 0, dis);
            // armed, then the control disappears: notfired after waitFrames, and the patch is gone.
            object pr = Protocol.Dispatch(new Job { Id = "t", Verb = "imgui", Args = JObject.Parse("{'press':{'label':'Export'},'waitFrames':5}") });
            IPending pp = (IPending)pr;
            Check("imgui-busy", V("imgui", "{'list':true}").Contains("\"code\":\"busy\""), V("imgui", "{'list':true}"));
            string nfd = null;
            for (int f = 0; f < 30 && nfd == null; f++)
            {
                imFrame++;
                object d = pp.Tick(false);
                if (d != null) { nfd = Protocol.Compact(d); break; }
                forced.AddRange(ImFrame(f == 0 ? ui : new[] { "Run bench" }, null, null, "BenchUI"));
            }
            Check("imgui-press-notfired", nfd != null && nfd.Contains("\"code\":\"notfired\"") && nfd.Contains("\"fired\":false") && forced.Count == 0 && !ImGuiTap.Active, nfd);
            // cancel releases the patch.
            IPending pc = (IPending)Protocol.Dispatch(new Job { Id = "t", Verb = "imgui", Args = JObject.Parse("{'list':true}") });
            string cx = Protocol.Compact(pc.Tick(true));
            Check("imgui-cancel-releases", cx.Contains("\"code\":\"cancelled\"") && !ImGuiTap.Active && disarms == arms, cx + " arms=" + arms + " disarms=" + disarms);

            // budget: 25 default rows of 80-char labels stay small.
            string[] fat = new string[400];
            for (int i = 0; i < fat.Length; i++) fat[i] = new string('w', 200) + i;
            string big = ImRun("{'list':true}", fat, null, null, forced);
            int bytes = Encoding.UTF8.GetByteCount(big);
            Check("imgui-list-budget", bytes <= 3600 && JObject.Parse(big)["rows"].Count() == ImGuiTap.DefaultPageSize && (bool)JObject.Parse(big)["hasMore"], bytes + " B");
            ImGuiTap.Shutdown();
        }

        private static void LogChecks()
        {
            Func<string, int> B = s => Encoding.UTF8.GetByteCount(s);
            LogTap.Shutdown();
            string empty = V("log", "{}");
            Check("log-empty-ring", empty == "{\"ok\":true,\"next\":0}", empty);

            for (int i = 1; i <= 1500; i++)
                LogTap.Append("line " + i + (i % 100 == 0 ? " boom" : ""), "at Frame" + i, i % 100 == 0 ? "Error" : i % 10 == 0 ? "Warning" : "Log");
            JObject tail = JObject.Parse(V("log", "{}"));
            JArray rows = (JArray)tail["rows"];
            Check("log-tail-is-newest-page", rows.Count == TapPage.DefaultPageSize && (long)rows[rows.Count - 1]["s"] == 1500 &&
                  (long)tail["next"] == 1500 && tail["dropped"] == null && tail["hasMore"] == null, tail.ToString(Newtonsoft.Json.Formatting.None).Substring(0, 200));
            Check("log-no-stack-by-default", rows[0]["st"] == null, rows[0].ToString());

            JObject delta = JObject.Parse(V("log", "{'since':1490}"));
            Check("log-delta-after-since", ((JArray)delta["rows"]).Count == 10 && (long)delta["rows"][0]["s"] == 1491 && delta["hasMore"] == null, delta.ToString());

            JObject behind = JObject.Parse(V("log", "{'since':100}"));
            Check("log-dropped-counts-overwritten-rows", (long)behind["dropped"] == 400 && (long)behind["rows"][0]["s"] == 501 &&
                  (bool)behind["hasMore"] && (long)behind["next"] == 525, behind.ToString(Newtonsoft.Json.Formatting.None).Substring(0, 120) + " next=" + behind["next"]);

            string idle = V("log", "{'since':1500}");
            Check("log-empty-poll-is-tiny", idle == "{\"ok\":true,\"next\":1500}" && B(idle) <= 30, idle);

            JObject errs = JObject.Parse(V("log", "{'since':0,'level':'error','pageSize':200}"));
            Check("log-level-filter", ((JArray)errs["rows"]).Count == 10 && ((JArray)errs["rows"]).All(r => (string)r["l"] == "E"), errs.ToString(Newtonsoft.Json.Formatting.None).Substring(0, 150));
            JObject warn = JObject.Parse(V("log", "{'since':0,'level':'warning','pageSize':200}"));
            Check("log-warning-includes-errors", ((JArray)warn["rows"]).Count == 100, "" + ((JArray)warn["rows"]).Count);
            JObject rx = JObject.Parse(V("log", "{'since':0,'match':'^line 14\\\\d0 boom$'}"));
            Check("log-regex-filter", ((JArray)rx["rows"]).Count == 1 && (string)rx["rows"][0]["m"] == "line 1400 boom", rx.ToString(Newtonsoft.Json.Formatting.None));
            Check("log-bad-regex-refused", V("log", "{'match':'('}").Contains("\"code\":\"args\""), V("log", "{'match':'('}"));
            Check("log-bad-level-refused", V("log", "{'level':'loud'}").Contains("\"code\":\"args\""), V("log", "{'level':'loud'}"));
            Check("log-page-size-refused-not-clamped", V("log", "{'pageSize':0}").Contains("\"code\":\"args\"") && V("log", "{'pageSize':201}").Contains("\"code\":\"args\""), V("log", "{'pageSize':0}"));
            Check("log-since-from-the-future-refused", V("log", "{'since':99999}").Contains("\"code\":\"cursor\""), V("log", "{'since':99999}"));
            JObject st = JObject.Parse(V("log", "{'since':1499,'stack':true}"));
            Check("log-stack-opt-in", (string)st["rows"][0]["st"] == "at Frame1500", st.ToString(Newtonsoft.Json.Formatting.None));

            LogTap.Append("PPCLI|7|{\"ok\":true}", null, "Log");
            LogTap.Append(new string('z', 5000), null, "Log");
            JObject clipped = JObject.Parse(V("log", "{'since':1500}"));
            Check("log-skips-bridge-markers-and-clips", ((JArray)clipped["rows"]).Count == 1 && ((string)clipped["rows"][0]["m"]).Length < 330 &&
                  ((string)clipped["rows"][0]["m"]).EndsWith("...(+4700)"), clipped.ToString(Newtonsoft.Json.Formatting.None).Substring(0, 80));

            // Any thread: the Unity callback is the THREADED one.
            long before = LogTap.Ring.Last;
            Thread[] ts = new Thread[4];
            for (int t = 0; t < ts.Length; t++) { ts[t] = new Thread(() => { for (int i = 0; i < 500; i++) LogTap.Append("x", null, "Log"); }); ts[t].Start(); }
            foreach (Thread t in ts) t.Join();
            Check("log-thread-safe-append", LogTap.Ring.Last == before + 2000 && LogTap.Ring.Count == LogTap.Capacity, "last=" + LogTap.Ring.Last);

            // wait {log}: only lines AFTER the wait starts count.
            LogTap.Append("mission ready OLD", null, "Log");
            IPending w = Start("wait", "{'log':'mission ready','everyFrames':1,'timeoutMs':5000}") as IPending;
            object t1 = w == null ? "no pending" : w.Tick(false);
            LogTap.Append("mission ready NEW", null, "Log");
            string t2 = w == null ? "" : Protocol.Compact(w.Tick(false));
            Check("log-wait-sees-only-new-lines", t1 == null && t2.Contains("\"ok\":true") && t2.Contains("mission ready NEW"), "" + t1 + " / " + t2);
            Check("log-wait-bad-regex-refused", Protocol.Compact(Start("wait", "{'log':'['}")).Contains("\"code\":\"args\""), "accepted");
            string planLog = Run("plan", "{'plan':{'steps':[{'id':'w','verb':'wait','args':{'log':'x','since':0,'timeoutMs':1000},'save':'W'}],'output':{'m':'${W.value.m}'}}}");
            Check("log-wait-in-a-plan", planLog.Contains("\"ok\":true") && planLog.Contains("\"m\":"), planLog);

            // --- Codex review of C+D.
            // P1 regex: ONE budget per request, not 50 ms per row - 40 catastrophic rows used to cost
            // ~2 s of one frame and answer "no rows".
            LogTap.Shutdown();
            for (int i = 0; i < 40; i++) LogTap.Append(new string('a', 28) + "!", null, "Log");
            Stopwatch rsw = Stopwatch.StartNew();
            string slow = V("log", "{'since':0,'match':'^(a+)+$'}");
            rsw.Stop();
            Check("log-regex-request-budget", slow.Contains("\"code\":\"regex\"") && rsw.ElapsedMilliseconds < 1000, rsw.ElapsedMilliseconds + " ms " + slow);
            // Rows each well under the 50 ms row timeout but together far past the request budget.
            LogTap.Shutdown();
            for (int i = 0; i < LogTap.Capacity; i++) LogTap.Append(new string('a', 16) + "!", null, "Log");
            rsw = Stopwatch.StartNew();
            string slow2 = V("log", "{'since':0,'match':'^(a+)+$'}");
            rsw.Stop();
            Check("log-regex-total-budget", slow2.Contains("\"code\":\"regex\"") && rsw.ElapsedMilliseconds < 1000, rsw.ElapsedMilliseconds + " ms " + slow2);
            IPending rw = Start("wait", "{'log':'^(a+)+$','since':0,'everyFrames':1,'timeoutMs':60000}") as IPending;
            string rwr = rw == null ? "no pending" : Protocol.Compact(rw.Tick(false));
            Check("log-wait-regex-budget-fails-fast", rwr.Contains("\"code\":\"regex\""), rwr);

            // P2 wait: bad level refused, future since refused, ring loss reported.
            Check("log-wait-bad-level-refused", Protocol.Compact(Start("wait", "{'log':'x','level':'fatal'}")).Contains("\"code\":\"args\""), "accepted");
            Check("log-wait-future-since-refused", Protocol.Compact(Start("wait", "{'log':'x','since':999999}")).Contains("\"code\":\"cursor\""), "accepted");
            IPending lw = Start("wait", "{'log':'never-said','everyFrames':1,'timeoutMs':60000}") as IPending;
            for (int i = 0; i < LogTap.Capacity + 5; i++) LogTap.Append("noise " + i, null, "Log");
            string lwr = lw == null ? "no pending" : Protocol.Compact(lw.Tick(false));
            Check("log-wait-reports-ring-loss", lwr.Contains("\"code\":\"dropped\""), lwr);
            // ...and a ring that restarts UNDER a running wait is code:"cursor", not a silent rewind.
            IPending cw = Start("wait", "{'log':'never-said','everyFrames':1,'timeoutMs':60000}") as IPending;
            LogTap.Shutdown();
            string cwr = cw == null ? "no pending" : Protocol.Compact(cw.Tick(false));
            Check("log-wait-ring-restart-is-cursor", cwr.Contains("\"code\":\"cursor\""), cwr);

            // P2 size: one row alone bigger than pageBytes is cut to fit and flagged.
            long fatFrom = LogTap.Ring.Last;
            LogTap.Append(new string('\u0001', 1000), new string('\u0002', 1500), "Error");
            string one = V("log", "{'since':" + fatFrom + ",'pageBytes':1024,'clip':1000,'stack':true}");
            Check("log-single-row-fits-page-bytes", B(one) <= 1024 && one.Contains("\"clipped\":true"), "bytes=" + B(one));

            // Budget: 1000 rows of 1000-char messages, default tail read.
            LogTap.Shutdown();
            for (int i = 0; i < LogTap.Capacity; i++) LogTap.Append(new string('m', 1000), new string('s', 1500), "Error");
            string fat = V("log", "{}");
            Check("budget-log-default", B(fat) <= TapPage.DefaultPageBytes + 256, "bytes=" + B(fat));
            LogTap.Shutdown();
        }

        /// <summary>
        /// Stage D: event subscriptions built with Expression for arbitrary delegate signatures,
        /// short projection at fire time, idempotent subscribe, caps, unsubscribe/scene drop with a
        /// named reason, `wait {event}` owning a temporary subscription, and the byte budgets.
        /// </summary>
        private static void EventChecks()
        {
            Func<string, int> B = s => Encoding.UTF8.GetByteCount(s);
            EventTap.Shutdown();
            Emitter em = new Emitter();
            Protocol.RootsProbe = () => new Dictionary<string, object> { { "em", em } };

            JObject sub = JObject.Parse(V("events", "{'subscribe':{'target':'@em','event':'Death'}}"));
            int id = (int)sub["sub"];
            Check("events-subscribe", (bool)sub["ok"] && id > 0 && (string)sub["event"] == "Death" && (long)sub["next"] == 0 && em.DeathHandlers == 1, sub.ToString(Newtonsoft.Json.Formatting.None));
            JObject again = JObject.Parse(V("events", "{'subscribe':{'target':'@em','event':'Death'}}"));
            Check("events-subscribe-is-idempotent", (int)again["sub"] == id && (bool)again["existing"] && em.DeathHandlers == 1, again.ToString(Newtonsoft.Json.Formatting.None));

            em.Die(new FakeReport { Actor = new FakeDef { Guid = "g-actor", name = "Crab" }, FromFire = true, Force = new V3(1f, 2f, 3f), When = Season.Summer });
            JObject read = JObject.Parse(V("events", "{'since':0,'sub':" + id + "}"));
            JToken a0 = read["rows"] == null ? null : read["rows"][0]["a"][0];
            Check("events-brief-projection", a0 != null && (string)a0["type"] == "FakeReport" && (string)a0["Actor"]["Guid"] == "g-actor" &&
                  (bool)a0["FromFire"] && a0["FromFall"] == null && a0["Killer"] == null && (float)a0["Force"]["y"] == 2f &&
                  (string)a0["When"] == "Summer" && a0["Actor"]["h"] == null, read.ToString(Newtonsoft.Json.Formatting.None));

            JObject pair = JObject.Parse(V("events", "{'subscribe':{'target':'@em','event':'Pair'}}"));
            em.Both(5, "x");
            JObject pr = JObject.Parse(V("events", "{'since':1,'sub':" + (int)pair["sub"] + "}"));
            Check("events-generic-action-args", pr["rows"] != null && pr["rows"][0]["a"].ToString(Newtonsoft.Json.Formatting.None) == "[5,\"x\"]", pr.ToString(Newtonsoft.Json.Formatting.None));

            JObject st = JObject.Parse(V("events", "{'subscribe':{'type':'Morgott.PPBridge.Emitter','event':'StaticPing'}}"));
            Emitter.Ping("pong");
            string sr = V("events", "{'since':2,'sub':" + (int)st["sub"] + "}");
            Check("events-static-event", sr.Contains("\"a\":[\"pong\"]"), sr);

            string ret = V("events", "{'subscribe':{'target':'@em','event':'Returns'}}");
            Check("events-non-void-refused", ret.Contains("\"code\":\"type\"") && em.ReturnsHandlers == 0, ret);
            string none = V("events", "{'subscribe':{'target':'@em','event':'Nope'}}");
            Check("events-unknown-event-lists-known", none.Contains("\"code\":\"member\"") && none.Contains("Death") && none.Contains("Pair"), none);
            Check("events-null-root-refused", V("events", "{'subscribe':{'target':'@missing','event':'Death'}}").Contains("\"code\":\"handle\""), "accepted");
            Check("events-one-mode-at-a-time", V("events", "{'subscribe':{'target':'@em','event':'Death'},'list':true}").Contains("\"code\":\"args\""), "accepted");
            string list = V("events", "{'list':true}");
            Check("events-list", list.Contains("\"sub\":" + id) && list.Contains("\"fired\":1") && list.Contains("\"on\":\"Emitter\""), list);

            string un = V("events", "{'unsubscribe':" + id + "}");
            em.Die(new FakeReport());
            string after = V("events", "{'since':3,'sub':" + id + "}");
            Check("events-unsubscribe-detaches", un.Contains("\"removed\":1") && em.DeathHandlers == 0 && after == "{\"ok\":true,\"next\":3,\"ended\":\"unsubscribed\"}", un + " / " + after);

            EventTap.DropWhere(o => o == em, "scene");
            string sc = V("events", "{'since':3,'sub':" + (int)pair["sub"] + "}");
            Check("events-scene-drop-is-named", sc.Contains("\"ended\":\"scene\"") && EventTap.Live == 1, sc + " live=" + EventTap.Live);
            V("events", "{'unsubscribe':'all'}");
            Check("events-unsubscribe-all", EventTap.Live == 0, "live=" + EventTap.Live);

            // Cap: 16 live subscriptions, the 17th refused.
            List<Emitter> many = new List<Emitter>();
            string capped = null;
            for (int i = 0; i <= EventTap.MaxSubs; i++)
            {
                Emitter e = new Emitter();
                many.Add(e);
                capped = V("events", "{'subscribe':{'target':'" + Reflect.Track(e) + "','event':'Death'}}");
            }
            Check("events-sub-cap", EventTap.Live == EventTap.MaxSubs && capped.Contains("\"code\":\"cap\""), capped);
            V("events", "{'unsubscribe':'all'}");

            // wait {event}: a temporary subscription, only NEW firings, detached when the wait ends.
            IPending w = Start("wait", "{'event':{'target':'@em','event':'Death','match':'g-killer'},'everyFrames':1,'timeoutMs':5000}") as IPending;
            object t1 = w == null ? "no pending" : w.Tick(false);
            em.Die(new FakeReport { Actor = new FakeDef { Guid = "other" } });
            object t2 = w == null ? "no pending" : w.Tick(false);
            em.Die(new FakeReport { Killer = new FakeDef { Guid = "g-killer" } });
            string t3 = w == null ? "" : Protocol.Compact(w.Tick(false));
            Check("events-wait-temp-subscription", t1 == null && t2 == null && t3.Contains("\"ok\":true") && t3.Contains("g-killer") &&
                  em.DeathHandlers == 0 && EventTap.Live == 0, t1 + " / " + t2 + " / " + t3 + " handlers=" + em.DeathHandlers);

            // wait on a sub that then ends fails with the reason, not a bare timeout.
            int keep = (int)JObject.Parse(V("events", "{'subscribe':{'target':'@em','event':'Death'}}"))["sub"];
            IPending w2 = Start("wait", "{'event':{'sub':" + keep + "},'timeoutMs':1}") as IPending;
            EventTap.DropWhere(o => true, "scene");
            Thread.Sleep(5);
            string t4 = w2 == null ? "" : Protocol.Compact(w2.Tick(false));
            Check("events-wait-names-ended-sub", t4.Contains("\"code\":\"timeout\"") && t4.Contains("ended: scene"), t4);
            Check("events-wait-unknown-sub-refused", Protocol.Compact(Start("wait", "{'event':{'sub':9999}}")).Contains("\"code\":\"args\""), "accepted");

            // --- Codex review of C+D.
            EventTap.Shutdown();
            // P1 leak: a wait {event} the plan abandons (deadline / cancel) or the Runner refuses to
            // park must drop the subscription it made.
            string pd = Run("plan", "{'plan':{'timeoutMs':30,'steps':[{'id':'w','verb':'wait','args':{'event':{'target':'@em','event':'Death'},'timeoutMs':60000}}]}}", 400, -1, 1);
            Check("events-plan-deadline-releases-wait-sub", pd.Contains("\"code\":\"timeout\"") && em.DeathHandlers == 0 && EventTap.Live == 0, pd + " handlers=" + em.DeathHandlers);
            string pc = Run("plan", "{'plan':{'steps':[{'id':'w','verb':'wait','args':{'event':{'target':'@em','event':'Death'},'timeoutMs':60000}}]}}", 50, 3);
            Check("events-plan-cancel-releases-wait-sub", pc.Contains("\"code\":\"cancelled\"") && em.DeathHandlers == 0 && EventTap.Live == 0, pc + " handlers=" + em.DeathHandlers);
            object parkRefused = Start("wait", "{'event':{'target':'@em','event':'Death'},'timeoutMs':60000}");
            bool subbed = em.DeathHandlers == 1;
            IReleasable rel = parkRefused as IReleasable;
            if (rel != null) rel.Release();
            Check("events-discarded-wait-releases-sub", subbed && rel != null && em.DeathHandlers == 0 && EventTap.Live == 0, "handlers=" + em.DeathHandlers);

            // P1 threads: a firing off the main thread records only scalars, never projects objects
            // (no Unity reads, no handle-table writes); Fired counts every concurrent firing.
            EventTap.MainThreadId = Thread.CurrentThread.ManagedThreadId;
            int offId = (int)JObject.Parse(V("events", "{'subscribe':{'target':'@em','event':'Death','handles':true}}"))["sub"];
            long offFrom = EventTap.Ring.Last;
            Thread bg = new Thread(() => em.Die(new FakeReport { Actor = new FakeDef { Guid = "bg" } }));
            bg.Start(); bg.Join();
            string offRead = V("events", "{'since':" + offFrom + ",'sub':" + offId + "}");
            Check("events-off-main-thread-not-projected", offRead.Contains("\"$offMain\":\"FakeReport\"") && !offRead.Contains("\"bg\""), offRead);
            int pairId = (int)JObject.Parse(V("events", "{'subscribe':{'target':'@em','event':'Pair'}}"))["sub"];
            Thread[] firers = new Thread[4];
            for (int t = 0; t < firers.Length; t++) { firers[t] = new Thread(() => { for (int i = 0; i < 2000; i++) em.Both(i, "x"); }); firers[t].Start(); }
            foreach (Thread t in firers) t.Join();
            Check("events-fired-counts-concurrent-firings", V("events", "{'list':true}").Contains("\"fired\":8000"), V("events", "{'list':true}"));
            EventTap.MainThreadId = -1;

            // P2 size: at most MaxArgs args per record, one fat arg replaced by type+size, one row
            // alone over pageBytes cut to fit with a flag.
            int manyId = (int)JObject.Parse(V("events", "{'subscribe':{'target':'@em','event':'Many'}}"))["sub"];
            em.FireMany();
            JObject mr = JObject.Parse(V("events", "{'sub':" + manyId + "}"));
            JArray ma = (JArray)mr["rows"][0]["a"];
            Check("events-arg-count-capped", ma.Count == EventTap.MaxArgs + 1 && (int)ma[EventTap.MaxArgs]["$moreArgs"] == 2, ma.ToString(Newtonsoft.Json.Formatting.None));
            // Brief keeps 160 chars a string; 160 control chars escape to ~960 bytes, two of them to ~2 KB.
            Emitter em2 = new Emitter();
            int bigId = (int)JObject.Parse(V("events", "{'subscribe':{'target':'" + Reflect.Track(em2) + "','event':'Death'}}"))["sub"];
            em2.Die(new FakeReport { Actor = new FakeDef { Guid = new string('\u0001', 160) }, Killer = new FakeDef { Guid = new string('\u0001', 160) } });
            string br = V("events", "{'sub':" + bigId + "}");
            Check("events-fat-arg-replaced", br.Contains("\"$clipped\":\"FakeReport\"") && B(br) < 400, "bytes=" + B(br));
            long pairFrom = EventTap.Ring.Last;
            em.Both(1, new string('\u0001', 160));
            string pr1 = V("events", "{'since':" + pairFrom + ",'sub':" + pairId + ",'pageBytes':1024}");
            Check("events-single-row-fits-page-bytes", B(pr1) <= 1024 && pr1.Contains("\"clipped\":"), "bytes=" + B(pr1));

            // P2 Drop: a remove accessor that throws leaves the handler attached - the sub stays
            // registered with the error, and a later unsubscribe can still detach it.
            int stickyId = (int)JObject.Parse(V("events", "{'subscribe':{'target':'@em','event':'Sticky'}}"))["sub"];
            string us = V("events", "{'unsubscribe':" + stickyId + "}");
            string sl = V("events", "{'list':true}");
            Check("events-failed-remove-is-reported", us.Contains("\"ok\":false") && us.Contains("remove refused") && em.StickyHandlers == 1 &&
                  sl.Contains("\"sub\":" + stickyId) && sl.Contains("removeError"), us + " / " + sl);
            em.RefuseRemove = false;
            string us2 = V("events", "{'unsubscribe':" + stickyId + "}");
            Check("events-failed-remove-retryable", us2.Contains("\"removed\":1") && em.StickyHandlers == 0, us2);

            // P2 wait: future since = code:"cursor" like a read.
            Check("events-wait-future-since-refused", Protocol.Compact(Start("wait", "{'event':{'sub':" + pairId + ",'since':999999}}")).Contains("\"code\":\"cursor\""), "accepted");
            // P2 wait: ring loss past since is its own failure, not a plain timeout.
            IPending lw = Start("wait", "{'event':{'sub':" + pairId + ",'match':'never'},'everyFrames':1,'timeoutMs':60000}") as IPending;
            for (int i = 0; i < EventTap.Capacity + 5; i++) em.Die(new FakeReport());
            string lwr = lw == null ? "no pending" : Protocol.Compact(lw.Tick(false));
            Check("events-wait-reports-ring-loss", lwr.Contains("\"code\":\"dropped\""), lwr);
            V("events", "{'unsubscribe':'all'}");

            // Budget: 1000 firings with a fat report, default tail read; empty poll.
            EventTap.Shutdown();
            V("events", "{'subscribe':{'target':'@em','event':'Death'}}");
            for (int i = 0; i < EventTap.Capacity; i++)
                em.Die(new FakeReport { Actor = new FakeDef { Guid = System.Guid.NewGuid().ToString() }, Killer = new FakeDef { Guid = new string('k', 400) }, FromFire = true });
            string fat = V("events", "{}");
            Check("budget-events-default", B(fat) <= TapPage.DefaultPageBytes + 256, "bytes=" + B(fat));
            string idle = V("events", "{'since':" + EventTap.Ring.Last + "}");
            Check("budget-events-empty-poll", B(idle) <= 30, idle);
            EventTap.Shutdown();
            Protocol.RootsProbe = null;
        }

        /// <summary>
        /// THE TOKEN BUDGET, per verb, in bytes. Every reply lands in a calling agent's context -
        /// often in a loop - so an unasked-for answer that GROWS is a regression even when every
        /// field in it is correct. Each figure is the default reply on a deliberately large input;
        /// raise a budget only on purpose, with the reason in the commit.
        /// </summary>
        private static void BudgetChecks()
        {
            Func<string, int> B = s => Encoding.UTF8.GetByteCount(s);

            string members = R("members", "{'type':'System.String'}");
            Check("budget-members-default", B(members) <= 6000, "bytes=" + B(members));
            string types = R("types", "{'pattern':'System.'}");
            Check("budget-types-default", B(types) <= 3000, "bytes=" + B(types));

            List<object> defs = new List<object>();
            for (int i = 0; i < 400; i++) defs.Add(new FakeDef { Guid = System.Guid.NewGuid().ToString(), name = "Crabman" + i + "_Basic_AlienMutationVariationDef" });
            Protocol.AllDefs = () => defs;
            string find = R("find", "{'query':'Crabman'}");
            Check("budget-find-default", B(find) <= 2500, "bytes=" + B(find));
            string findAll = R("find", "{'all':true}");
            Check("budget-find-all-default", B(findAll) <= 4500, "bytes=" + B(findAll));

            // Review: (int)JToken took "2", true and 1.5 (rounded) as page sizes. One strict helper now.
            foreach (string junk in new[] { "'2'", "true", "1.5", "9999999999" })
            {
                string j = junk.Trim('\'');
                Check("strict-int-find-" + j, R("find", "{'query':'Crabman','pageSize':" + junk + "}").Contains("\"code\":\"args\""), R("find", "{'query':'Crabman','pageSize':" + junk + "}"));
                Check("strict-int-find-page-" + j, R("find", "{'query':'Crabman','page':" + junk + "}").Contains("\"code\":\"args\""), R("find", "{'query':'Crabman','page':" + junk + "}"));
                Check("strict-int-types-" + j, R("types", "{'pattern':'System.','pageSize':" + junk + "}").Contains("\"code\":\"args\""), "accepted");
                Check("strict-int-members-" + j, R("members", "{'type':'System.String','pageSize':" + junk + "}").Contains("\"code\":\"args\""), "accepted");
                Check("strict-int-log-" + j, V("log", "{'pageSize':" + junk + "}").Contains("\"code\":\"args\""), V("log", "{'pageSize':" + junk + "}"));
                if (j != "9999999999")                             // a legal long since: that one is code:"cursor"
                    Check("strict-int-log-since-" + j, V("log", "{'since':" + junk + "}").Contains("\"code\":\"args\""), V("log", "{'since':" + junk + "}"));
            }
            Check("strict-int-accepts-a-real-integer", R("find", "{'query':'Crabman','pageSize':2}").Contains("\"pageSize\":2"), "pageSize:2 refused");

            List<string> steps = new List<string>();
            for (int i = 0; i < 80; i++) steps.Add("{'id':'p" + i + "','verb':'ping'}");
            string plan = Run("plan", "{'plan':{'steps':[" + string.Join(",", steps) + "]}}");
            Check("budget-plan-80-green-steps", B(plan) <= 200, "bytes=" + B(plan) + " " + plan);

            Shots.Arm = on => null;
            Run("observe", "{'action':'start'}");
            for (int i = 0; i < Shots.Capacity; i++) Shots.Record(i, 0f, 0f, "Crabman_1", "Torso", 10f, 1f, 1);
            string observed = Run("observe", "{'action':'read'}");
            Check("budget-observe-read-default", B(observed) <= 2500, "bytes=" + B(observed));
            Run("observe", "{'action':'stop'}");

            List<string> lines = new List<string>();
            for (int i = 0; i < 5000; i++) lines.Add("variable_" + i + " : Boolean = True  [Persistent] some description text here");
            Protocol.ConsoleRun = (c, a) => new { ok = true, output = lines.ToArray(), truncated = false };
            string console = Console1("{'command':'vars'}");
            Check("budget-console-first-page", B(console) <= ConsolePager.DefaultPageBytes + 256, "bytes=" + B(console));
            Protocol.ConsoleRun = null;
            ConsolePager.Reset();
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
            Check("observe-default-page-is-small", full.Contains("\"returned\":" + Shots.DefaultRows) && full.Contains("\"hasMore\":true"), full);
            string fullMax = Run("observe", "{'action':'read','pageSize':" + Shots.MaxRows + "}");
            Check("observe-listing-is-capped", fullMax.Contains("\"returned\":" + Shots.MaxRows), fullMax);
            // page 0 is the NEWEST end: the last impact recorded was x = Capacity+39.
            Check("observe-page-0-is-the-newest", full.Contains("\"x\":" + (Shots.Capacity + 39) + ".0"), full);
            string summaryOnly = Run("observe", "{'action':'read','pageSize':0}");
            Check("observe-summary-only", summaryOnly.Contains("\"impacts\":[]") && summaryOnly.Contains("\"n\":" + Shots.Capacity), summaryOnly);
            Check("observe-page-size-refused-not-clamped", Run("observe", "{'action':'read','pageSize':201}").Contains("\"ok\":false"),
                  Run("observe", "{'action':'read','pageSize':201}"));
            // Review: page+1 was int math - page:int.MaxValue wrapped to an empty page with hasMore:true.
            string farPage = Run("observe", "{'action':'read','page':2147483647,'pageSize':10}");
            Check("observe-huge-page-is-empty-and-last", farPage.Contains("\"returned\":0") && farPage.Contains("\"hasMore\":false"), farPage);
            foreach (string junk in new[] { "'2'", "true", "1.5" })
                Check("observe-page-size-strict-int-" + junk.Trim('\''), Run("observe", "{'action':'read','pageSize':" + junk + "}").Contains("\"code\":\"args\""),
                      Run("observe", "{'action':'read','pageSize':" + junk + "}"));
            string lastPage = Run("observe", "{'action':'read','page':51,'pageSize':10}");
            Check("observe-last-page", lastPage.Contains("\"returned\":2") && lastPage.Contains("\"hasMore\":false"), lastPage);
            // The stats are computed over EVERYTHING stored, not over the trimmed listing.
            Check("observe-stats-use-the-whole-ring", full.Contains("\"n\":" + Shots.Capacity), full);
            // ...which is exactly why truncation may not be invisible. Statistics over the retained
            // window are NOT the statistics that were asked for, so the drop count is a live,
            // single-read property a plan can assert on, the same shape as Landed and Recovered -
            // weapon-test.json refuses the whole run on it.
            Check("observe-drops-are-assertable", Shots.Dropped == 40, "Shots.Dropped=" + Shots.Dropped);
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
            // --- {"forMs"}. A bounded YIELD, and the one wait whose whole point is that time passing
            // is a SUCCESS. Fast-forwarding the geoscape is "run at this Scale for this long", and
            // spelling that as a never-true predicate would report every healthy run as a failure.
            Check("wait-for-a-span-does-not-finish-early",
                  Run("wait", "{'forMs':5000}", 3, -1, 0).Contains("never finished"),
                  Run("wait", "{'forMs':5000}", 3, -1, 0));
            Check("wait-for-a-span-succeeds-when-it-elapses",
                  Run("wait", "{'forMs':1}", 50, -1, 2).Contains("\"ok\":true"),
                  Run("wait", "{'forMs':1}", 50, -1, 2));
            // A predicate that is broken forever must still SAY so - a bare timeout would send an
            // agent looking at the game instead of at its own typo.
            Check("wait-timeout-reports-the-last-error",
                  Run("wait", "{'call':{'op':'get','target':'@nope','member':'Field'},'timeoutMs':1,'everyFrames':1}", 50, -1, 2)
                      .Contains("no root 'nope'"),
                  Run("wait", "{'call':{'op':'get','target':'@nope','member':'Field'},'timeoutMs':1,'everyFrames':1}", 50, -1, 2));
            // A FALSE assertion says why too: `last:false` on its own sent a real reader hunting for
            // hours, because the step before it had already computed the reason and the failure threw
            // it away. The substituted predicate is echoed back, so the reason rides in its arguments.
            Check("wait-timeout-echoes-the-substituted-predicate",
                  Run("wait", "{'call':{'op':'invoke','type':'System.Object','member':'Equals','args':['NotDisabled','WeaponNotOwned']},'timeoutMs':1,'everyFrames':1}", 50, -1, 2)
                      .Contains("WeaponNotOwned"),
                  Run("wait", "{'call':{'op':'invoke','type':'System.Object','member':'Equals','args':['NotDisabled','WeaponNotOwned']},'timeoutMs':1,'everyFrames':1}", 50, -1, 2));
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
                'output':{'first':'${A.value}','second':'${B.value}'}},'trace':'full'}");
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
            // The counter, NOT the output block: a failed plan no longer publishes one (below), so
            // reading `f` out of the answer would now be checking the wrong thing entirely.
            Check("plan-finally-runs-on-failure", Ov.Counter == 201, "counter=" + Ov.Counter);

            // --- THE FIGURES OF A FAILED RUN ARE NOT FIGURES. `output` used to be resolved whether or
            // not the plan had failed, so a bench whose own assertion had just refused the run still
            // handed back every number it had measured. The gate is in the engine and not in any plan
            // file, so the next plan author gets it without knowing it exists.
            Check("plan-withholds-output-on-failure",
                  !failFinally.Contains("\"f\":201") && !failFinally.Contains("\"output\":"), failFinally);
            Check("plan-says-the-output-was-withheld",
                  failFinally.Contains("\"outputWithheld\":\"the plan failed at step 'boom'"), failFinally);
            // ...and what replaces it: the failing step's own DTO, so the value that tripped the
            // assertion still reaches the caller. This is how the weapon bench reports the count of
            // wedged or dropped projectiles it refused on.
            Check("plan-returns-the-failing-step-result", failFinally.Contains("\"result\":{"), failFinally);
            Check("plan-still-publishes-output-on-success", !okFinally.Contains("outputWithheld") && okFinally.Contains("\"output\":{"), okFinally);
            Check("plan-success-carries-no-null-fields", !okFinally.Contains(":null") && !okFinally.Contains("\"trace\""), okFinally);
            Check("plan-errors-trace-drops-the-fatal-row", !failFinally.Contains("\"trace\""), failFinally);
            string contTrace = Run("plan", "{'plan':{'steps':[{'id':'bad','verb':'no-such-verb','onError':'continue'},{'id':'good','verb':'ping'}]}}");
            Check("plan-errors-trace-keeps-a-continued-failure", contTrace.Contains("\"id\":\"bad\"") && !contTrace.Contains("\"id\":\"good\""), contTrace);
            Check("plan-trace-mode-refuses-junk", Run("plan", "{'plan':{'steps':[{'verb':'ping'}]},'trace':'all'}").Contains("\"code\":\"args\""), "trace:all accepted");
            // Review P1: the errors trace must not share the full trace's 500-row buffer - 510
            // green steps used to fill it, and the continued failure after them vanished.
            List<string> many = new List<string>();
            for (int i = 0; i < Plan.MaxTrace + 10; i++) many.Add("{'verb':'ping'}");
            many.Add("{'id':'late','verb':'no-such-verb','onError':'continue'}");
            string lateFail = Run("plan", "{'plan':{'maxSteps':" + (Plan.MaxTrace + 20) + ",'steps':[" + string.Join(",", many) + "]}}");
            Check("plan-errors-trace-survives-500-green-steps", lateFail.Contains("\"id\":\"late\"") && lateFail.Contains("\"ok\":true"),
                  lateFail.Length > 300 ? lateFail.Substring(0, 300) : lateFail);

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
                  Run("plan", "{'plan':{'vars':{'go':false},'steps':[{'id':'s','verb':'ping','if':'${go}'}]},'trace':'full'}").Contains("\"skipped\":\"if\""),
                  "a falsy if still ran the step");
            Check("plan-if-runs-a-step",
                  !Run("plan", "{'plan':{'vars':{'go':true},'steps':[{'id':'s','verb':'ping','if':'${go}'}]}}").Contains("skipped"),
                  "a truthy if skipped the step");
            Check("plan-unless-inverts",
                  Run("plan", "{'plan':{'vars':{'go':true},'steps':[{'id':'s','verb':'ping','unless':'${go}'}]},'trace':'full'}").Contains("\"skipped\":\"unless\""),
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
            Check("restore-says-it-only-issued", restored.Contains("\"issued\":\"load_game\"") && !restored.Contains("\"note\""), restored);

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

            // --- ARRAY SPREAD into a console arg list. "${...NAME}" AS AN ELEMENT OF AN ARRAY splices
            // that variable's elements in; plain "${NAME}" still nests one value, because a `call` arg
            // legitimately IS an array when the method takes one. Everything else refuses by name -
            // a silently wrong argument list is the failure mode worth buying these five asserts for,
            // and it is not hypothetical: a plain ${TAGS} in an arg list produces the empty argument
            // "map1||z", which loadmap would have taken as a real, blank tag.
            string[] spliced = null;
            Protocol.ConsoleRun = (c, a) => { spliced = a; return new { ok = true, output = new string[0] }; };

            Run("plan", "{'vars':{'TAGS':['a','b']},'finally':[],'steps':[{'id':'s','verb':'console'," +
                        "'args':{'command':'loadmap','args':['map1','${...TAGS}','z']}}]}");
            Check("plan-spread-splices-into-the-arg-list", spliced != null && string.Join("|", spliced) == "map1|a|b|z",
                  spliced == null ? "the console step never ran" : string.Join("|", spliced));

            spliced = null;
            Run("plan", "{'vars':{'TAGS':['a','b']},'finally':[],'steps':[{'id':'s','verb':'console'," +
                        "'args':{'command':'loadmap','args':['map1','${TAGS}']}}]}");
            Check("plan-plain-var-still-nests", spliced != null && spliced.Length == 2,
                  spliced == null ? "null" : "a plain ${VAR} flattened, which breaks every array-taking call: " + spliced.Length);

            Check("plan-spread-of-a-non-array-refuses",
                  Run("plan", "{'vars':{'TAGS':'notalist'},'finally':[],'steps':[{'id':'s','verb':'console'," +
                              "'args':{'command':'loadmap','args':['${...TAGS}']}}]}").Contains("spreads an array"),
                  "a scalar was spread instead of refused");
            Check("plan-spread-outside-an-array-refuses",
                  Run("plan", "{'vars':{'TAGS':['a']},'finally':[],'steps':[{'id':'s','verb':'console'," +
                              "'args':{'command':'${...TAGS}','args':[]}}]}").Contains("element of an array"),
                  "a spread in a scalar position was accepted");
            Check("plan-spread-of-an-unset-var-refuses",
                  Run("plan", "{'finally':[],'steps':[{'id':'s','verb':'console','args':{'command':'loadmap'," +
                              "'args':['${...GONE}']}}]}").Contains("is not set"),
                  "an unset variable spread to nothing instead of failing");

            Protocol.ConsoleRun = (c, a) => { loaded = c + " " + string.Join(" ", a); return new { ok = true, output = new string[0] }; };

            EveryShippedPlan();
            // ...and these two are DRIVEN as well, which is a stronger claim than parsing: the engine
            // walks their real shape and their cleanup block still runs after an early failure.
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
        /// <summary>
        /// EVERY file in plans\, not the two that get driven. `finally` is a CONVENTION - the engine
        /// accepts finally:null on purpose, because an inline ad-hoc plan that takes nothing has
        /// nothing to release - so the only thing that can keep the shipped files honest is a check
        /// that reads all of them. It parses each one too, which is worth having on its own: a syntax
        /// error in a plan nobody drives here used to surface only in-game.
        /// </summary>
        private static void EveryShippedPlan()
        {
            string dir = null;
            for (DirectoryInfo d = new DirectoryInfo(AppContext.BaseDirectory); d != null && dir == null; d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, "plans"))) dir = Path.Combine(d.FullName, "plans");
            Check("plans-dir-found", dir != null, "no plans\\ above " + AppContext.BaseDirectory);
            if (dir == null) return;
            string[] files = Directory.GetFiles(dir, "*.json");
            Check("plans-dir-is-not-empty", files.Length > 0, dir + " holds no plan files");
            foreach (string f in files)
            {
                string name = Path.GetFileName(f);
                JObject p;
                try { p = JObject.Parse(File.ReadAllText(f)); }
                catch (Exception ex) { Check(name + "-parses", false, ex.Message); continue; }
                Check(name + "-has-a-finally", p["finally"] is JArray fin && (fin.Count > 0 || p["//finally"] != null), "no cleanup block (an empty one must say why in //finally)");
                Check(name + "-declares-its-outputs", p["output"] is JObject o && o.Count > 0, "no output block");
                // The client derives its own ceiling from this, so a plan without one silently gets
                // the 300 s default back and long plans are cancelled mid-run again.
                Check(name + "-declares-a-timeout", p["timeoutMs"] != null, "no timeoutMs");
                // A declared timeout the engine would silently halve is worse than none: Plan.Clamp
                // caps it and says nothing, so the plan is measured against a deadline it never asked for.
                Check(name + "-timeout-is-not-clamped", (int)p["timeoutMs"] <= Plan.MaxWaitMs,
                      "timeoutMs " + p["timeoutMs"] + " exceeds MaxWaitMs " + Plan.MaxWaitMs);
            }
        }

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
            Check(name + "-has-a-finally", fin != null && (fin.Count > 0 || file["//finally"] != null), "no cleanup block (an empty one must say why in //finally)");
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
            ConsolePagerChecks();
            LogChecks();
            EventChecks();
            ImGuiChecks();
            BudgetChecks();
            PipeChecks();

            Console.WriteLine(failures == 0 ? "ppcli selfcheck: PASS" : "ppcli selfcheck: " + failures + " FAILURE(S)");
            return failures == 0 ? 0 : 1;
        }
    }
}
