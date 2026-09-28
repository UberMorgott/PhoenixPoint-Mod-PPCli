using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Morgott.PPBridge
{
    /// <summary>
    /// The <c>trace</c> verb's PURE half: "did this method run, with what arguments, and what did it
    /// return" for ANY method of the game or of a mod, without editing that mod. The Harmony patch is
    /// the game half (src\TracePatch.cs) and reaches this file only through <see cref="Enter"/> /
    /// <see cref="Leave"/>; everything else - method resolution, the refusals, the caps, the ring,
    /// the paging, TTL/maxHits/scene ends - names no Unity or Harmony type and is proven offline by
    /// selfcheck with a fake Arm/Disarm pair.
    ///
    /// Same shape as <c>observe</c>: nothing is patched until a `start`, and every trace is removed
    /// again on stop, TTL, maxHits (on the next main-thread frame), scene unload (unless keepScene)
    /// and bridge shutdown - a session that never traces carries no patch.
    /// </summary>
    internal static class TraceTap
    {
        internal const int MaxLive = 8;
        internal const int Capacity = 2000;
        internal const int DefaultMaxHits = 100;
        internal const int HardMaxHits = 1000;
        internal const int DefaultTtlMs = 300000;
        internal const int MaxTtlMs = 900000;
        internal const int MaxStack = 3;
        internal const int MaxArgs = 8;
        /// <summary>One projected value bigger than this JSON is replaced by {$clipped:type,bytes}.</summary>
        internal const int MaxValueBytes = 512;
        /// <summary>Mono's JIT inlines a non-virtual callee whose IL body is shorter than this
        /// (mini/method-to-ir.c INLINE_LENGTH_LIMIT 20). A caller JITted with the body inlined never
        /// reaches the patched method, so its hits are silently missing.</summary>
        internal const int InlineIlBytes = 20;

        internal sealed class Spec
        {
            internal int Id;
            internal MethodBase Method;
            internal string Name;
            internal bool Args, Self, Ret;
            internal int Stack, MaxHits;
            internal bool KeepScene;
            internal DateTime Expires;
            /// <summary>Every call seen while patched (Interlocked). Only the first MaxHits are rows.</summary>
            internal int Hits;
            /// <summary>Set (any thread) when the trace must end; the next main-thread Tick unpatches.</summary>
            internal volatile string Ending;
            internal string RemoveError;
            internal bool WantsRet { get { MethodInfo mi = Method as MethodInfo; return Ret && mi != null && mi.ReturnType != typeof(void); } }
        }

        /// <summary>One recorded call. A class, not a struct: the postfix fills <see cref="R"/> into
        /// the row the prefix already put in the ring.</summary>
        internal sealed class Row
        {
            internal int Trace;
            internal JArray A;
            internal JToken T;
            internal volatile JToken R;
            internal volatile bool HasR;
            internal string[] F;
            internal bool Off;
        }

        internal sealed class Ended
        {
            internal int Hits;
            internal string Reason;
            internal string Name;
        }

        internal static readonly SeqRing<Row> Ring = new SeqRing<Row>(Capacity);
        private static readonly List<Spec> live = new List<Spec>();
        private static readonly Dictionary<int, Ended> ended = new Dictionary<int, Ended>();
        /// <summary>Copy-on-write lookup for the patch: read lock-free from any thread.</summary>
        private static volatile Dictionary<MethodBase, Spec> byMethod = new Dictionary<MethodBase, Spec>();
        private static int nextId;
        private static volatile int liveCount;

        /// <summary>Game half: install / remove the Harmony patch. Null = done, else why not.</summary>
        internal static Func<Spec, string> Arm;
        internal static Func<Spec, string> Disarm;
        /// <summary>Clock, replaceable offline.</summary>
        internal static Func<DateTime> Now = () => DateTime.UtcNow;

        [ThreadStatic] private static bool inHit;

        internal static int Live { get { return liveCount; } }

        internal static object Dispatch(string verb, JObject a)
        {
            if (verb != "trace") return null;
            int modes = (a != null && a["start"] != null ? 1 : 0) + (a != null && a["stop"] != null ? 1 : 0) +
                        (a != null && Plan.Truthy(a["list"]) ? 1 : 0);
            if (modes > 1) return TapPage.Bad("args", "trace takes ONE of {start}, {stop}, {list}, or a read {since?, id?, match?}");
            Tick();
            if (a != null && a["start"] != null)
            {
                JObject s = a["start"] as JObject;
                if (s == null) return TapPage.Bad("args", "start needs {type, method, sig?, args?, self?, ret?, stack?, maxHits?, ttlMs?, keepScene?, force?}");
                Spec spec; bool existing; string warn;
                object refusal = Start(s, out spec, out existing, out warn);
                if (refusal != null) return refusal;
                Dictionary<string, object> d = new Dictionary<string, object> { { "ok", true }, { "id", spec.Id }, { "method", spec.Name }, { "next", Ring.Last } };
                if (existing) d["existing"] = true;
                if (warn != null) d["warn"] = warn;
                return d;
            }
            if (a != null && a["stop"] != null) return Stop(a["stop"]);
            if (a != null && Plan.Truthy(a["list"])) return List();
            return Read(a);
        }

        // ------------------------------------------------------------------ start

        internal static object Start(JObject s, out Spec spec, out bool existing, out string warn)
        {
            spec = null; existing = false; warn = null;
            if (Arm == null) return TapPage.Bad("unsupported", "no trace patcher installed (offline, or the game half failed to load)");
            MethodBase m;
            object refusal = Resolve(s, out m);
            if (refusal != null) return refusal;
            refusal = Check(m, Plan.Truthy(s["force"]), out warn);
            if (refusal != null) return refusal;

            int maxHits, ttl, stack;
            string e1 = Protocol.IntArg(s, "maxHits", DefaultMaxHits, out maxHits);
            string e2 = Protocol.IntArg(s, "ttlMs", DefaultTtlMs, out ttl);
            string e3 = Protocol.IntArg(s, "stack", 0, out stack);
            if (e1 != null || e2 != null || e3 != null) return TapPage.Bad("args", e1 ?? e2 ?? e3);
            if (maxHits < 1 || maxHits > HardMaxHits) return TapPage.Bad("args", "maxHits must be 1.." + HardMaxHits);
            if (ttl < 1 || ttl > MaxTtlMs) return TapPage.Bad("args", "ttlMs must be 1.." + MaxTtlMs);
            if (stack < 0 || stack > MaxStack) return TapPage.Bad("args", "stack must be 0.." + MaxStack);

            lock (live)
            {
                foreach (Spec x in live)
                    if (x.Method == m && x.Ending == null)
                    {
                        spec = x; existing = true;
                        if (x.Args != Plan.Truthy(s["args"]) || x.Self != Plan.Truthy(s["self"]) || x.Ret != Plan.Truthy(s["ret"]) || x.Stack != stack || x.MaxHits != maxHits)
                            warn = Join(warn, "already traced as id " + x.Id + " with DIFFERENT options - those stay; stop it first to change them");
                        return null;
                    }
                if (live.Count >= MaxLive)
                    return TapPage.Bad("cap", "already " + MaxLive + " live traces - stop one ({\"stop\":id|\"all\"})");
            }
            Spec made = new Spec
            {
                Id = Interlocked.Increment(ref nextId), Method = m, Name = Display(m),
                Args = Plan.Truthy(s["args"]), Self = Plan.Truthy(s["self"]), Ret = Plan.Truthy(s["ret"]),
                Stack = stack, MaxHits = maxHits, KeepScene = Plan.Truthy(s["keepScene"]),
                Expires = Now().AddMilliseconds(ttl)
            };
            if (made.Ret && !made.WantsRet) warn = Join(warn, "ret:true ignored - " + made.Name + " returns nothing");
            // Registered BEFORE the patch goes in: the very first call may arrive on the next
            // instruction, from another thread.
            lock (live)
            {
                live.Add(made);
                Publish();
            }
            string why;
            try { why = Arm(made); }
            catch (Exception ex) { why = ex.GetType().Name + ": " + ex.Message; }
            if (why != null)
            {
                lock (live) { live.Remove(made); Publish(); }
                return TapPage.Bad("threw", "patching " + made.Name + " failed: " + Protocol.Clip(why));
            }
            spec = made;
            return null;
        }

        private static string Join(string a, string b) { return a == null ? b : a + "; " + b; }

        /// <summary>Must be called under lock(live).</summary>
        private static void Publish()
        {
            Dictionary<MethodBase, Spec> d = new Dictionary<MethodBase, Spec>();
            foreach (Spec x in live) d[x.Method] = x;
            byMethod = d;
            liveCount = live.Count;
        }

        /// <summary>{type, assembly?, method, sig?}: the one DECLARED method (".ctor" = an instance
        /// constructor). Overloads without a sig refuse with every signature listed.</summary>
        internal static object Resolve(JObject s, out MethodBase m)
        {
            m = null;
            string typeName = (string)s["type"], name = (string)s["method"];
            if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(name)) return TapPage.Bad("args", "start needs \"type\" and \"method\"");
            string error;
            Type type = Reflect.ResolveType(typeName, (string)s["assembly"], out error);
            if (type == null) return TapPage.Bad("type", error ?? "no type '" + typeName + "'");
            if (name == ".cctor") return TapPage.Bad("args", "a static constructor runs once, before anyone could trace it - not traceable");

            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            List<MethodBase> found = name == ".ctor"
                ? type.GetConstructors(all).Where(c => !c.IsStatic).Cast<MethodBase>().ToList()
                : type.GetMethods(all).Where(x => x.Name == name).Cast<MethodBase>().ToList();
            if (found.Count == 0)
            {
                MethodInfo inherited = null;
                try { inherited = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy).FirstOrDefault(x => x.Name == name); }
                catch (Exception) { }
                if (inherited != null && inherited.DeclaringType != type)
                    return TapPage.Bad("member", "'" + name + "' is not declared on " + Reflect.ShortName(type) + " - it is declared on " +
                                                 Reflect.ShortName(inherited.DeclaringType) + "; trace that type (the patch sees every caller)");
                return TapPage.Bad("member", "no method '" + name + "' declared on " + Reflect.ShortName(type));
            }
            JArray sig = s["sig"] as JArray;
            if (sig != null)
            {
                string[] want = sig.Select(t => (string)t).ToArray();
                found = found.Where(x => Reflect.Matches(x, want)).ToList();
                if (found.Count == 0) return TapPage.Bad("overload", "no overload of " + name + " matches sig [" + string.Join(", ", want) + "]");
            }
            if (found.Count > 1)
            {
                string[] sigs = found.Take(12).Select(x => Reflect.Sig(x)).ToArray();
                return TapPage.Bad("ambiguous", found.Count + " overloads of " + name + " - pass \"sig\":[param type names]: " + string.Join(" | ", sigs));
            }
            m = found[0];
            return null;
        }

        private static readonly string[] UnsafeAssemblies = { "mscorlib", "netstandard", "0Harmony", "Newtonsoft.Json", "PPBridge" };

        /// <summary>The refusals that keep a trace from being silently wrong or dangerous. Null = go.</summary>
        internal static object Check(MethodBase m, bool force, out string warn)
        {
            warn = null;
            Type dt = m.DeclaringType;
            string asm = dt == null ? "" : dt.Assembly.GetName().Name;
            if (dt == null || UnsafeAssemblies.Contains(asm, StringComparer.OrdinalIgnoreCase) ||
                asm.StartsWith("System", StringComparison.OrdinalIgnoreCase) || asm.StartsWith("Mono.", StringComparison.OrdinalIgnoreCase))
                return TapPage.Bad("unsafe", "'" + asm + "' is runtime/bridge code the trace itself calls into (recursion, every-call cost) - trace the game or mod method that calls it");
            if (m.IsGenericMethodDefinition || m.ContainsGenericParameters || dt.ContainsGenericParameters)
                return TapPage.Bad("generic", Display(m) + " is an open generic - Harmony would patch a shared instantiation, not the one you mean; trace a non-generic caller");
            if (m.IsAbstract || dt.IsInterface)
                return TapPage.Bad("abstract", Display(m) + " has no body - trace the overriding method on the concrete type");
            MethodInfo asInfo = m as MethodInfo;
            if ((asInfo != null && (asInfo.ReturnType.IsByRef || asInfo.ReturnType.IsPointer)) || m.GetParameters().Any(p => p.ParameterType.IsPointer))
                return TapPage.Bad("signature", Display(m) + " has a pointer parameter or a ref/pointer return - the shared patch cannot box it");
            MethodImplAttributes impl = m.GetMethodImplementationFlags();
            if ((impl & (MethodImplAttributes.InternalCall | MethodImplAttributes.Native | MethodImplAttributes.Runtime)) != 0 ||
                (m.Attributes & MethodAttributes.PinvokeImpl) != 0)
                return TapPage.Bad("extern", Display(m) + " is extern/native (no IL) - Harmony cannot patch it");
            MethodBody body;
            try { body = m.GetMethodBody(); } catch (Exception) { body = null; }
            if (body == null)
                return TapPage.Bad("extern", Display(m) + " has no IL body - nothing to patch");
            int il = 0;
            try { byte[] bytes = body.GetILAsByteArray(); il = bytes == null ? 0 : bytes.Length; } catch (Exception) { }
            bool virt = m.IsVirtual && !m.IsFinal;
            bool noInline = (impl & MethodImplAttributes.NoInlining) != 0;
            if (!virt && !noInline && il < InlineIlBytes && body.ExceptionHandlingClauses.Count == 0)
            {
                string risk = Display(m) + " is " + il + " bytes of IL and non-virtual - Mono inlines such methods into their callers, so calls from already-JITted callers never reach a patch and would be MISSING from the count";
                if (!force) return TapPage.Bad("inline", risk + ". Pass force:true to trace anyway (then a low count proves nothing), or trace its caller");
                warn = "inline risk: " + risk;
            }
            return null;
        }

        internal static string Display(MethodBase m)
        {
            return (m.DeclaringType == null ? "" : Reflect.ShortName(m.DeclaringType) + ".") + m.Name +
                   "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name).ToArray()) + ")";
        }

        // ------------------------------------------------------------------ the patch's two calls

        /// <summary>Called by the prefix of EVERY patched call, on whatever thread made it. Never
        /// throws. Returns the row for the postfix to finish, or null when nothing was recorded.</summary>
        internal static object Enter(MethodBase original, object instance, object[] args)
        {
            if (inHit) return null;                 // the projection itself reached a traced method
            Spec s;
            if (original == null || !byMethod.TryGetValue(original, out s)) return null;
            if (s.Ending != null) return null;
            int n = Interlocked.Increment(ref s.Hits);
            if (n > s.MaxHits)
            {
                if (s.Ending == null) s.Ending = "maxHits";
                return null;
            }
            inHit = true;
            try
            {
                bool main = EventTap.MainThreadId < 0 || Thread.CurrentThread.ManagedThreadId == EventTap.MainThreadId;
                Row r = new Row { Trace = s.Id, Off = !main };
                if (s.Args && args != null)
                {
                    JArray a = new JArray();
                    for (int i = 0; i < args.Length && i < MaxArgs; i++) a.Add(Value(args[i], main));
                    if (args.Length > MaxArgs) a.Add(new JObject { { "$moreArgs", args.Length - MaxArgs } });
                    r.A = a;
                }
                if (s.Self && instance != null) r.T = Value(instance, main);
                if (s.Stack > 0) r.F = Frames(s.Stack);
                if (n == s.MaxHits) s.Ending = "maxHits";
                // ret:true publishes the row COMPLETE, at return: a row put in the ring now and
                // finished later could be read (and a cursor moved past it) before its return value existed.
                if (s.WantsRet) return r;
                Ring.Add(r);
                return null;
            }
            catch (Exception) { return null; }
            finally { inHit = false; }
        }

        /// <summary>The postfix: the return value into the row the prefix recorded.</summary>
        internal static void Leave(object state, object result)
        {
            Row r = state as Row;
            if (r == null || inHit) return;
            inHit = true;
            try
            {
                r.R = Value(result, !r.Off);
                r.HasR = true;
                Ring.Add(r);
            }
            catch (Exception) { }
            finally { inHit = false; }
        }

        private static JToken Value(object o, bool main)
        {
            JToken p;
            try
            {
                if (!main) p = EventTap.OffMain(o);
                else
                {
                    object v = Reflect.Brief(o);
                    p = v == null ? JValue.CreateNull() : JToken.FromObject(v);
                }
            }
            catch (Exception ex) { return "<" + ex.GetType().Name + ">"; }
            int b = Encoding.UTF8.GetByteCount(p.ToString(Formatting.None));
            if (b > MaxValueBytes) p = new JObject { { "$clipped", o == null ? "null" : o.GetType().Name }, { "bytes", b } };
            return p;
        }

        /// <summary>The CALLERS of the traced method: our own frames and the patched method itself
        /// (Harmony's replacement, often named DMD&lt;...&gt;) are skipped.</summary>
        private static string[] Frames(int take)
        {
            System.Diagnostics.StackFrame[] frames = new System.Diagnostics.StackTrace(1, false).GetFrames();
            List<string> outp = new List<string>();
            if (frames == null) return outp.ToArray();
            bool pastOurs = false, pastPatched = false;
            foreach (System.Diagnostics.StackFrame f in frames)
            {
                MethodBase mb = null;
                try { mb = f.GetMethod(); } catch (Exception) { }
                Type t = mb == null ? null : mb.DeclaringType;
                bool ours = t != null && (t == typeof(TraceTap) || t.Name == "TracePatch");
                if (!pastOurs) { if (ours) continue; pastOurs = true; }
                if (!pastPatched) { pastPatched = true; continue; }
                outp.Add(mb == null ? "?" : (t == null ? "" : Reflect.ShortName(t) + ".") + mb.Name);
                if (outp.Count >= take) break;
            }
            return outp.ToArray();
        }

        // ------------------------------------------------------------------ ends

        /// <summary>Main thread, once a frame (Runner.Update) and before every trace verb: unpatches
        /// traces that hit maxHits or passed their TTL. Cheap when nothing is traced.</summary>
        internal static void Tick()
        {
            if (liveCount == 0) return;
            DateTime now = Now();
            List<Spec> copy;
            lock (live) copy = new List<Spec>(live);
            foreach (Spec s in copy)
            {
                if (s.Ending == null && now > s.Expires) s.Ending = "ttl";
                if (s.Ending != null) End(s, s.Ending);
            }
        }

        /// <summary>Removes the patch FIRST; only a removal that worked ends the trace (a throwing
        /// unpatch leaves it listed with removeError, retryable by stop). Null = ended.</summary>
        internal static string End(Spec s, string reason)
        {
            lock (live) if (!live.Contains(s)) return null;
            if (s.Ending == null) s.Ending = reason;       // stop recording at once, whatever follows
            string why = null;
            try { if (Disarm != null) why = Disarm(s); }
            catch (Exception ex) { why = ex.GetType().Name + ": " + ex.Message; }
            if (why != null) { s.RemoveError = Protocol.Clip(why); return s.RemoveError; }
            lock (live)
            {
                if (!live.Remove(s)) return null;
                Publish();
                if (ended.Count >= 64) ended.Clear();
                ended[s.Id] = new Ended { Hits = Thread.VolatileRead(ref s.Hits), Reason = reason, Name = s.Name };
            }
            return null;
        }

        /// <summary>Game half, on scene unload: every trace not started with keepScene ends.</summary>
        internal static void SceneUnloaded()
        {
            List<Spec> copy;
            lock (live) copy = new List<Spec>(live);
            foreach (Spec s in copy) if (!s.KeepScene) End(s, "scene");
        }

        internal static void Shutdown()
        {
            List<Spec> copy;
            lock (live) copy = new List<Spec>(live);
            foreach (Spec s in copy) End(s, "shutdown");
            lock (live) { ended.Clear(); }
            Ring.Clear();
        }

        internal static Spec Find(int id)
        {
            lock (live) return live.Find(x => x.Id == id);
        }

        /// <summary>Null while live; the reason once ended; "unknown" for an id never seen.</summary>
        internal static string EndedReason(int id)
        {
            lock (live)
            {
                if (live.Exists(x => x.Id == id)) return null;
                Ended e;
                return ended.TryGetValue(id, out e) ? e.Reason : "unknown";
            }
        }

        internal static int? HitsOf(int id)
        {
            lock (live)
            {
                Spec s = live.Find(x => x.Id == id);
                if (s != null) return Thread.VolatileRead(ref s.Hits);
                Ended e;
                return ended.TryGetValue(id, out e) ? e.Hits : (int?)null;
            }
        }

        private static object Stop(JToken which)
        {
            List<Spec> gone = new List<Spec>();
            lock (live)
            {
                if (which.Type == JTokenType.String && (string)which == "all") gone.AddRange(live);
                else if (which.Type == JTokenType.Integer)
                {
                    int id = (int)which;
                    Spec s = live.Find(x => x.Id == id);
                    if (s == null)
                    {
                        Ended e;
                        if (ended.TryGetValue(id, out e)) return new { ok = true, stopped = 0, hits = e.Hits, ended = e.Reason };
                        return TapPage.Bad("args", "no trace " + id);
                    }
                    gone.Add(s);
                }
                else return TapPage.Bad("args", "stop takes a trace id or \"all\"");
            }
            int stopped = 0, hits = 0;
            List<object> failed = new List<object>();
            foreach (Spec s in gone)
            {
                hits += Thread.VolatileRead(ref s.Hits);
                string err = End(s, "stopped");
                if (err == null) stopped++; else failed.Add(new { id = s.Id, error = err });
            }
            if (failed.Count == 0) return new { ok = true, stopped, hits };
            return new { ok = false, code = "threw", error = failed.Count + " patch(es) could not be removed and are STILL live (retry stop)", stopped, failed };
        }

        private static object List()
        {
            List<object> rows = new List<object>();
            lock (live)
            {
                foreach (Spec s in live)
                {
                    Dictionary<string, object> r = new Dictionary<string, object>
                    {
                        { "id", s.Id }, { "method", s.Name }, { "hits", Thread.VolatileRead(ref s.Hits) }, { "maxHits", s.MaxHits },
                        { "ttlMs", Math.Max(0, (int)(s.Expires - Now()).TotalMilliseconds) }
                    };
                    if (s.Ending != null) r["ending"] = s.Ending;
                    if (s.RemoveError != null) r["removeError"] = s.RemoveError;
                    rows.Add(r);
                }
                foreach (KeyValuePair<int, Ended> kv in ended.OrderBy(k => k.Key))
                    rows.Add(new Dictionary<string, object> { { "id", kv.Key }, { "method", kv.Value.Name }, { "hits", kv.Value.Hits }, { "ended", kv.Value.Reason } });
            }
            return new { ok = true, traces = rows, next = Ring.Last };
        }

        // ------------------------------------------------------------------ read / wait

        private static object Read(JObject a)
        {
            long since; bool haveSince; int size, bytes;
            object bad = TapPage.Args(a, out since, out haveSince, out size, out bytes);
            if (bad != null) return bad;
            int id;
            string idErr = Protocol.IntArg(a, "id", 0, out id);
            if (idErr != null) return TapPage.Bad("args", idErr);
            if (id != 0 && HitsOf(id) == null) return TapPage.Bad("args", "no trace " + id);
            Regex rx; object refusal;
            if (!TapPage.TryRegex(a == null ? null : (string)a["match"], out rx, out refusal)) return refusal;

            long lost, newest;
            List<KeyValuePair<long, Row>> all = Ring.After(haveSince ? since : 0, out lost, out newest);
            object ahead = TapPage.Ahead(since, newest);
            if (ahead != null) return ahead;
            if (!haveSince) lost = 0;

            TapPage.Scan scan = new TapPage.Scan(rx);
            List<KeyValuePair<long, Row>> hits = new List<KeyValuePair<long, Row>>();
            foreach (KeyValuePair<long, Row> kv in all)
                if ((id == 0 || kv.Value.Trace == id) && (rx == null || scan.Match(JsonConvert.SerializeObject(Project(kv.Key, kv.Value)))))
                    hits.Add(kv);
            if (scan.Failure != null) return scan.Refusal();

            int start = haveSince ? 0 : Math.Max(0, hits.Count - size);
            List<object> rows = new List<object>();
            long used = 60, next = newest;
            bool more = false;
            for (int i = start; i < hits.Count; i++)
            {
                Dictionary<string, object> row = Project(hits[i].Key, hits[i].Value);
                int cost = TapPage.Bytes(row);
                if (rows.Count >= size || (rows.Count > 0 && used + cost > bytes))
                {
                    more = true;
                    next = hits[i - 1].Key;
                    break;
                }
                if (used + cost > bytes)
                {
                    row.Remove("a"); row.Remove("t"); row.Remove("r"); row.Remove("f");
                    row["clipped"] = cost;
                    cost = TapPage.Bytes(row);
                }
                used += cost;
                rows.Add(row);
            }
            Dictionary<string, object> d = TapPage.Reply(rows, next, more, lost);
            if (id != 0)
            {
                d["hits"] = HitsOf(id) ?? 0;
                string why = EndedReason(id);
                if (why != null) d["ended"] = why;
            }
            return d;
        }

        internal static Dictionary<string, object> Project(long seq, Row r)
        {
            Dictionary<string, object> d = new Dictionary<string, object> { { "s", seq }, { "id", r.Trace } };
            if (r.A != null) d["a"] = r.A;
            if (r.T != null) d["t"] = r.T;
            if (r.HasR) d["r"] = r.R;
            if (r.F != null && r.F.Length > 0) d["f"] = r.F;
            if (r.Off) d["off"] = true;
            return d;
        }

        /// <summary>For `wait {trace:id}`: the first row of that trace after *since.</summary>
        internal static JToken FirstMatch(int id, Regex rx, ref long since)
        {
            long lost, newest;
            List<KeyValuePair<long, Row>> rows = Ring.After(since, out lost, out newest);
            long from = since;
            TapPage.WaitGuard(since, newest, 0, "trace");
            TapPage.Scan scan = new TapPage.Scan(rx);
            foreach (KeyValuePair<long, Row> kv in rows)
            {
                bool hit = kv.Value.Trace == id && (rx == null || scan.Match(JsonConvert.SerializeObject(Project(kv.Key, kv.Value))));
                if (scan.Failure != null) throw new WaitFatal("regex", scan.Failure);
                since = kv.Key;
                if (hit) return JToken.FromObject(Project(kv.Key, kv.Value));
            }
            TapPage.WaitGuard(from, newest, lost, "trace");
            return null;
        }
    }
}
