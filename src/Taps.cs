using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Morgott.PPBridge
{
    /// <summary>Thrown by a wait probe when polling on can never become a truthful answer (the ring
    /// overwrote rows it had not scanned, the seq space restarted, the regex blew its budget). The
    /// Waiter ends at once with this code instead of burning the rest of its timeout.</summary>
    internal sealed class WaitFatal : Exception
    {
        internal readonly string Code;
        internal WaitFatal(string code, string message) : base(message) { Code = code; }
    }

    /// <summary>
    /// A bounded, sequence-numbered ring: the shared shape behind <c>log</c> and <c>events</c>. Every
    /// row gets the next seq (1, 2, ...); a reader keeps the `next` it was handed and asks for
    /// everything after it, so an empty poll costs a few bytes and nothing is re-read. Oldest rows
    /// are overwritten; a reader whose `since` fell behind the oldest row is told how many it lost.
    /// Locked, because the log callback arrives on any thread.
    /// </summary>
    internal sealed class SeqRing<T>
    {
        private readonly T[] items;
        private readonly object gate = new object();
        private int head, count;
        private long last;

        internal SeqRing(int capacity) { items = new T[capacity]; }

        internal int Capacity { get { return items.Length; } }
        internal long Last { get { lock (gate) return last; } }
        internal int Count { get { lock (gate) return count; } }

        internal long Add(T item)
        {
            lock (gate)
            {
                last++;
                items[head] = item;
                head = (head + 1) % items.Length;
                if (count < items.Length) count++;
                return last;
            }
        }

        internal void Clear()
        {
            lock (gate) { Array.Clear(items, 0, items.Length); head = count = 0; last = 0; }
        }

        /// <summary>Rows with seq &gt; since, oldest first, copied under the lock. `lost` = rows after
        /// `since` that were overwritten before this read.</summary>
        internal List<KeyValuePair<long, T>> After(long since, out long lost, out long newest)
        {
            List<KeyValuePair<long, T>> rows = new List<KeyValuePair<long, T>>();
            lock (gate)
            {
                newest = last;
                long oldest = last - count + 1;
                lost = since + 1 < oldest ? oldest - since - 1 : 0;
                long from = Math.Max(since + 1, oldest);
                for (long s = from; s <= last; s++)
                {
                    int idx = (int)(((head - (last - s) - 1) % items.Length + items.Length) % items.Length);
                    rows.Add(new KeyValuePair<long, T>(s, items[idx]));
                }
            }
            return rows;
        }
    }

    /// <summary>
    /// Paging shared by <c>log</c> and <c>events</c>: pageSize, pageBytes, since, a cached regex.
    /// Refusals use the same codes as every other paged verb (args, cursor).
    /// </summary>
    internal static class TapPage
    {
        internal const int DefaultPageSize = 25;
        internal const int MaxPageSize = 200;
        internal const int DefaultPageBytes = 8 * 1024;
        internal const int MinPageBytes = 1024;
        internal const int MaxPageBytes = 192 * 1024;

        private static readonly Dictionary<string, Regex> regexes = new Dictionary<string, Regex>();

        internal static object Args(JObject a, out long since, out bool haveSince, out int size, out int bytes)
        {
            JToken s = a == null ? null : a["since"];
            haveSince = s != null && s.Type != JTokenType.Null;
            string e1 = Protocol.LongArg(a, "since", 0, out since);
            string e2 = Protocol.IntArg(a, "pageSize", DefaultPageSize, out size);
            string e3 = Protocol.IntArg(a, "pageBytes", DefaultPageBytes, out bytes);
            if (e1 != null || e2 != null || e3 != null) return Bad("args", e1 ?? e2 ?? e3);
            if (since < 0) return Bad("args", "since must be >= 0 (0 = from the oldest row kept)");
            if (size < 1 || size > MaxPageSize) return Bad("args", "pageSize must be 1.." + MaxPageSize);
            if (bytes < MinPageBytes || bytes > MaxPageBytes) return Bad("args", "pageBytes must be " + MinPageBytes + ".." + MaxPageBytes);
            return null;
        }

        /// <summary>A since from the future means the seq space restarted (a new game process, or a
        /// cleared ring): refused like a stale cursor, never answered with a silent empty page.</summary>
        internal static object Ahead(long since, long newest)
        {
            return since > newest
                ? Bad("cursor", "since " + since + " is ahead of the newest seq " + newest + " - the game restarted or the ring was cleared; read again with since:0")
                : null;
        }

        /// <summary>A wait poll's two truth checks: a since from the future (seq space restarted) is
        /// code:"cursor" like a read; rows overwritten before they were scanned is code:"dropped" -
        /// a plain timeout would claim the row never came.</summary>
        internal static void WaitGuard(long since, long newest, long lost, string what)
        {
            if (since > newest)
                throw new WaitFatal("cursor", "since " + since + " is ahead of the newest " + what + " seq " + newest + " - the game restarted or the ring was cleared; wait again with since:0 or no since");
            if (lost > 0)
                throw new WaitFatal("dropped", lost + " " + what + " row(s) after seq " + since + " were overwritten before this poll scanned them - the awaited row may have been among them; poll more often (everyFrames) or read with a narrower source");
        }

        internal static bool TryRegex(string pattern, out Regex rx, out object refusal)
        {
            rx = null; refusal = null;
            if (string.IsNullOrEmpty(pattern)) return true;
            lock (regexes)
            {
                if (regexes.TryGetValue(pattern, out rx)) return true;
                try { rx = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }
                catch (ArgumentException ex) { refusal = Bad("args", "match is not a valid regex: " + ex.Message); return false; }
                if (regexes.Count >= 32) regexes.Clear();
                regexes[pattern] = rx;
                return true;
            }
        }

        /// <summary>Wall-clock budget for ALL the regex work of one request (a read, or one wait
        /// poll). The per-row 50 ms timeout alone let 1000 rows cost ~50 s of one frame.</summary>
        internal const int RegexBudgetMs = 100;

        /// <summary>
        /// One request's regex scan. A row whose match times out, or a scan past
        /// <see cref="RegexBudgetMs"/>, stops the scan and sets <see cref="Failure"/>: the caller
        /// refuses (code:"regex") rather than answer from a silently partial scan.
        /// </summary>
        internal sealed class Scan
        {
            private readonly Regex rx;
            private readonly System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            internal string Failure;
            internal int Tested;

            internal Scan(Regex rx) { this.rx = rx; }

            internal bool Match(string text)
            {
                if (rx == null) return true;
                if (Failure != null) return false;
                if (sw.ElapsedMilliseconds > RegexBudgetMs)
                {
                    Failure = "match regex spent the " + RegexBudgetMs + " ms request budget after " + Tested + " rows - simplify the pattern or narrow with since/level/sub";
                    return false;
                }
                Tested++;
                try { return rx.IsMatch(text ?? ""); }
                catch (RegexMatchTimeoutException)
                {
                    Failure = "match regex timed out on one row (row " + Tested + ") - the pattern backtracks catastrophically; simplify it";
                    return false;
                }
            }

            internal object Refusal() { return Bad("regex", Failure); }
        }

        internal static int Bytes(object row)
        {
            return Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(row)) + 1;
        }

        internal static object Bad(string code, string message)
        {
            return new { ok = false, code, error = Protocol.Clip(message) };
        }

        /// <summary>The reply every read shares. Absent keys are the frugal defaults: no `rows` when
        /// empty, no `hasMore`/`dropped` unless true/non-zero.</summary>
        internal static Dictionary<string, object> Reply(List<object> rows, long next, bool more, long lost)
        {
            Dictionary<string, object> d = new Dictionary<string, object> { { "ok", true } };
            if (rows.Count > 0) d["rows"] = rows;
            d["next"] = next;
            if (more) d["hasMore"] = true;
            if (lost > 0) d["dropped"] = lost;
            return d;
        }
    }

    /// <summary>
    /// The <c>log</c> verb: every Unity log message since the bridge armed, in a bounded ring. The
    /// game half hooks Application.logMessageReceivedThreaded and calls <see cref="Append"/> - from
    /// ANY thread, which is why the ring locks and why Append never throws. Pure otherwise, so the
    /// ring, the filters and the paging are all proven offline.
    /// </summary>
    internal static class LogTap
    {
        internal const int Capacity = 1000;
        internal const int StoreMsgChars = 1000;
        internal const int StoreStackChars = 1500;
        internal const int DefaultClip = 300;

        internal struct Row
        {
            internal char Level;     // L log, W warning, E error, A assert, X exception
            internal string Msg, Stack;
            /// <summary>The message's ORIGINAL length, so a clipped reply says how much was cut.</summary>
            internal int Len;
        }

        internal static readonly SeqRing<Row> Ring = new SeqRing<Row>(Capacity);

        /// <summary>True once the game half subscribed. Reported so an empty ring is never mistaken
        /// for a quiet game.</summary>
        internal static bool Hooked;

        /// <summary>The bridge's own batch result lines. Echoing them would put every result into the
        /// log twice.</summary>
        internal const string SkipPrefix = "PPCLI|";

        internal static void Append(string msg, string stack, string unityType)
        {
            try
            {
                if (msg != null && msg.StartsWith(SkipPrefix, StringComparison.Ordinal)) return;
                Ring.Add(new Row { Level = Letter(unityType), Msg = Cut(msg, StoreMsgChars, false), Len = msg == null ? 0 : msg.Length, Stack = string.IsNullOrEmpty(stack) ? null : Cut(stack, StoreStackChars, true) });
            }
            catch (Exception) { /* a log callback that throws would re-enter the logger */ }
        }

        internal static char Letter(string unityType)
        {
            switch (unityType)
            {
                case "Warning": return 'W';
                case "Error": return 'E';
                case "Assert": return 'A';
                case "Exception": return 'X';
                default: return 'L';
            }
        }

        private static string Cut(string s, int max, bool mark)
        {
            if (s == null) return "";
            if (s.Length > max) s = s.Substring(0, max) + (mark ? "...(+" + (s.Length - max) + ")" : "");
            return s.Replace("\r", "");
        }

        /// <summary>Minimum severity: "log" = all, "warning" = W and up, "error" = E/A/X.</summary>
        internal static bool LevelOk(char l, string min)
        {
            if (string.IsNullOrEmpty(min) || min == "log" || min == "all") return true;
            if (min == "error") return l == 'E' || l == 'A' || l == 'X';
            if (min == "warning") return l != 'L';
            return false;
        }

        internal static object Dispatch(string verb, JObject a)
        {
            if (verb != "log") return null;
            if (a != null && Plan.Truthy(a["status"]))
                return new { ok = true, hooked = Hooked, next = Ring.Last, stored = Ring.Count, capacity = Capacity };
            return Read(a);
        }

        private static object Read(JObject a)
        {
            long since; bool haveSince; int size, bytes;
            object bad = TapPage.Args(a, out since, out haveSince, out size, out bytes);
            if (bad != null) return bad;
            string level;
            object badLevel = LevelArg(a, out level);
            if (badLevel != null) return badLevel;
            Regex rx; object refusal;
            if (!TapPage.TryRegex(a == null ? null : (string)a["match"], out rx, out refusal)) return refusal;
            bool stack = a != null && Plan.Truthy(a["stack"]);
            int clip;
            string clipErr = Protocol.IntArg(a, "clip", DefaultClip, out clip);
            if (clipErr != null) return TapPage.Bad("args", clipErr);
            if (clip < 40 || clip > StoreMsgChars) return TapPage.Bad("args", "clip must be 40.." + StoreMsgChars);

            long lost, newest;
            List<KeyValuePair<long, Row>> all = Ring.After(haveSince ? since : 0, out lost, out newest);
            object ahead = TapPage.Ahead(since, newest);
            if (ahead != null) return ahead;
            if (!haveSince) lost = 0;

            TapPage.Scan scan = new TapPage.Scan(rx);
            List<KeyValuePair<long, Row>> hits = new List<KeyValuePair<long, Row>>();
            foreach (KeyValuePair<long, Row> kv in all)
                if (LevelOk(kv.Value.Level, level) && scan.Match(kv.Value.Msg)) hits.Add(kv);
            if (scan.Failure != null) return scan.Refusal();

            // No `since` = the TAIL: the newest page, and `next` = newest so the next poll is a delta.
            // With `since` = oldest-first after it; a full page stops at its last row.
            int start = haveSince ? 0 : Math.Max(0, hits.Count - size);
            List<object> rows = new List<object>();
            long used = 40, next = newest;
            bool more = false;
            for (int i = start; i < hits.Count; i++)
            {
                Dictionary<string, object> row = Project(hits[i].Key, hits[i].Value, stack, clip);
                int cost = TapPage.Bytes(row);
                if (rows.Count >= size || (rows.Count > 0 && used + cost > bytes))
                {
                    more = true;
                    next = hits[i - 1].Key;
                    break;
                }
                // ONE row alone over the page: cut to fit and say so, never let it past the budget.
                if (used + cost > bytes) { Fit(row, bytes - used); cost = TapPage.Bytes(row); }
                used += cost;
                rows.Add(row);
            }
            return TapPage.Reply(rows, next, more, lost);
        }

        /// <summary>Shrinks a row to fit <paramref name="room"/> bytes: stack dropped, message cut, `clipped:true`.</summary>
        private static void Fit(Dictionary<string, object> row, long room)
        {
            row.Remove("st");
            row["clipped"] = true;
            string m = (string)row["m"];
            row["m"] = "";
            long rest = room - TapPage.Bytes(row) - 2;
            bool cut;
            row["m"] = ConsolePager.ClipJson(m, Math.Max(16, rest), out cut);
        }

        internal static object LevelArg(JObject a, out string level)
        {
            JToken t = a == null ? null : a["level"];
            level = null;
            if (t == null || t.Type == JTokenType.Null) return null;
            level = t.Type == JTokenType.String ? (string)t : null;
            if (level != "log" && level != "all" && level != "warning" && level != "error")
                return TapPage.Bad("args", "level must be log|warning|error (minimum severity)");
            return null;
        }

        internal static Dictionary<string, object> Project(long seq, Row r, bool stack, int clip)
        {
            Dictionary<string, object> d = new Dictionary<string, object>
            {
                { "s", seq }, { "l", r.Level.ToString() },
                { "m", r.Len > clip ? (r.Msg.Length > clip ? r.Msg.Substring(0, clip) : r.Msg) + "...(+" + (r.Len - clip) + ")" : r.Msg }
            };
            if (stack && r.Stack != null) d["st"] = r.Stack;
            return d;
        }

        /// <summary>For `wait {log:regex}`: the first row after *since that matches, advancing
        /// *since past everything scanned so no row is tested twice.</summary>
        internal static JToken FirstMatch(Regex rx, string level, ref long since)
        {
            long lost, newest;
            List<KeyValuePair<long, Row>> rows = Ring.After(since, out lost, out newest);
            long from = since;
            TapPage.WaitGuard(since, newest, 0, "log");
            TapPage.Scan scan = new TapPage.Scan(rx);
            foreach (KeyValuePair<long, Row> kv in rows)
            {
                bool hit = LevelOk(kv.Value.Level, level) && scan.Match(kv.Value.Msg);
                if (scan.Failure != null) throw new WaitFatal("regex", scan.Failure);
                since = kv.Key;
                if (hit) return JToken.FromObject(Project(kv.Key, kv.Value, false, DefaultClip));
            }
            TapPage.WaitGuard(from, newest, lost, "log");
            return null;
        }

        internal static void Shutdown()
        {
            Hooked = false;
            Ring.Clear();
        }
    }

    /// <summary>
    /// The <c>events</c> verb: subscribe to a public C# event on a live object (or a static one on
    /// a type) and record each firing, projected SHORT at fire time - the objects in it may be gone
    /// by the time anyone reads. The handler is built with System.Linq.Expressions for whatever the
    /// event's delegate signature is, so no event type is named here. Bounded: at most
    /// <see cref="MaxSubs"/> live subscriptions and a <see cref="Capacity"/>-row ring.
    /// Subscriptions end on unsubscribe, when their target is destroyed, or when the scene it lived
    /// in unloads (the game half calls <see cref="DropWhere"/>).
    /// </summary>
    internal static class EventTap
    {
        internal const int Capacity = 1000;
        internal const int MaxSubs = 16;

        internal sealed class Sub
        {
            internal int Id;
            internal object Target;          // null for a static event
            internal EventInfo Event;
            internal Delegate Handler;
            internal bool Handles;
            /// <summary>Interlocked: an event may fire on several threads at once.</summary>
            internal int Fired;
            /// <summary>Set when RemoveEventHandler threw: the handler is STILL attached, so the sub
            /// stays in the registry (listed, retryable by unsubscribe/shutdown).</summary>
            internal string RemoveError;
        }

        /// <summary>At most this many args of one firing are recorded (the rest counted).</summary>
        internal const int MaxArgs = 8;
        /// <summary>One projected arg bigger than this is replaced by its type + size.</summary>
        internal const int MaxArgBytes = 1024;

        /// <summary>The Unity main thread's id, set by the game half at enable. A firing on any
        /// other thread records only thread-safe scalars: projecting a game object there reads
        /// Unity state off-main and (handles:true) writes the handle table unlocked. -1 = unknown
        /// (offline): the current thread counts as main.</summary>
        internal static int MainThreadId = -1;

        internal struct Row
        {
            internal int Sub;
            internal JArray Args;
        }

        internal static readonly SeqRing<Row> Ring = new SeqRing<Row>(Capacity);
        private static readonly List<Sub> subs = new List<Sub>();
        /// <summary>Why a sub id stopped - so a read or wait on it says so instead of staying empty.</summary>
        private static readonly Dictionary<int, string> ended = new Dictionary<int, string>();
        private static int nextId;

        internal static int Live { get { lock (subs) return subs.Count; } }

        internal static object Dispatch(string verb, JObject a)
        {
            if (verb != "events") return null;
            Prune();
            int modes = (a != null && a["subscribe"] != null ? 1 : 0) + (a != null && a["unsubscribe"] != null ? 1 : 0) +
                        (a != null && Plan.Truthy(a["list"]) ? 1 : 0);
            if (modes > 1) return TapPage.Bad("args", "events takes ONE of {subscribe}, {unsubscribe}, {list}, or a read {since?, sub?}");
            if (a != null && a["subscribe"] != null)
            {
                JObject s = a["subscribe"] as JObject;
                if (s == null) return TapPage.Bad("args", "subscribe needs {target|type, event}");
                Sub sub; bool existing;
                object refusal = Subscribe(s, out sub, out existing);
                if (refusal != null) return refusal;
                Dictionary<string, object> d = new Dictionary<string, object> { { "ok", true }, { "sub", sub.Id }, { "event", sub.Event.Name }, { "next", Ring.Last } };
                if (existing) d["existing"] = true;
                return d;
            }
            if (a != null && a["unsubscribe"] != null) return Unsubscribe(a["unsubscribe"]);
            if (a != null && Plan.Truthy(a["list"])) return List();
            return Read(a);
        }

        /// <summary>Resolves the target through Reflect's own grammar (@alias, handle, {$h}) or a
        /// static type, finds the PUBLIC event, and attaches a compiled handler. Idempotent: the same
        /// target+event hands back the live sub.</summary>
        internal static object Subscribe(JObject s, out Sub sub, out bool existing)
        {
            sub = null; existing = false;
            string name = (string)s["event"];
            if (string.IsNullOrEmpty(name)) return TapPage.Bad("args", "subscribe needs \"event\" (the C# event's name)");
            object target = null;
            Type type;
            JToken t = s["target"];
            if (t != null && t.Type != JTokenType.Null)
            {
                string error;
                if (!Reflect.ResolveTargetToken(t, out target, out error)) return TapPage.Bad("handle", error);
                if (target == null) return TapPage.Bad("handle", "the target is null right now (wrong phase?)");
                type = target.GetType();
            }
            else
            {
                string error;
                type = Reflect.ResolveType((string)s["type"], (string)s["assembly"], out error);
                if (type == null) return TapPage.Bad("type", error ?? "subscribe needs \"target\" (instance event) or \"type\" (static event)");
            }

            BindingFlags flags = BindingFlags.Public | BindingFlags.FlattenHierarchy | (target == null ? BindingFlags.Static : BindingFlags.Instance);
            EventInfo ev = type.GetEvent(name, flags);
            if (ev == null)
            {
                string[] known = type.GetEvents(flags).Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).Take(40).ToArray();
                return TapPage.Bad("member", "no public " + (target == null ? "static" : "instance") + " event '" + name + "' on " +
                                             Reflect.ShortName(type) + (known.Length == 0 ? " (it has none)" : " - it has: " + string.Join(", ", known)));
            }

            lock (subs)
            {
                foreach (Sub x in subs)
                    if (ReferenceEquals(x.Target, target) && x.Event.Name == ev.Name && x.Event.DeclaringType == ev.DeclaringType)
                    { sub = x; existing = true; return null; }
                if (subs.Count >= MaxSubs)
                    return TapPage.Bad("cap", "already " + MaxSubs + " live subscriptions - unsubscribe one ({\"unsubscribe\":id|\"all\"})");
            }

            Sub made = new Sub { Id = ++nextId, Target = target, Event = ev, Handles = Plan.Truthy(s["handles"]) };
            string why;
            Delegate d = Build(ev.EventHandlerType, made, out why);
            if (d == null) return TapPage.Bad("type", why);
            try { ev.AddEventHandler(target, d); }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                return TapPage.Bad("threw", "adding the handler threw " + inner.GetType().Name + ": " + inner.Message);
            }
            made.Handler = d;
            lock (subs) subs.Add(made);
            sub = made;
            return null;
        }

        /// <summary>(a, b, ...) =&gt; EventTap.Fire(sub, new object[] { a, b, ... }) for any void
        /// delegate type without by-ref parameters.</summary>
        internal static Delegate Build(Type handlerType, Sub sub, out string why)
        {
            why = null;
            MethodInfo invoke = handlerType == null ? null : handlerType.GetMethod("Invoke");
            if (invoke == null) { why = "the event's handler type has no Invoke"; return null; }
            if (invoke.ReturnType != typeof(void)) { why = "the event's delegate returns " + Reflect.ShortName(invoke.ReturnType) + " - only void events are recorded"; return null; }
            ParameterInfo[] ps = invoke.GetParameters();
            if (ps.Any(p => p.ParameterType.IsByRef)) { why = "the event's delegate has a ref/out parameter - not supported"; return null; }
            ParameterExpression[] prms = ps.Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
            Expression arr = Expression.NewArrayInit(typeof(object), prms.Select(p => (Expression)Expression.Convert(p, typeof(object))));
            MethodInfo fire = typeof(EventTap).GetMethod("Fire", BindingFlags.NonPublic | BindingFlags.Static);
            Expression body = Expression.Call(fire, Expression.Constant(sub), arr);
            try { return Expression.Lambda(handlerType, body, prms).Compile(); }
            catch (Exception ex) { why = "could not compile a handler: " + ex.GetType().Name + ": " + ex.Message; return null; }
        }

        /// <summary>Runs inside the GAME's event invocation, so it must never throw: a throwing
        /// handler would abort every handler after it (the game's own included).</summary>
        internal static void Fire(Sub sub, object[] args)
        {
            try
            {
                System.Threading.Interlocked.Increment(ref sub.Fired);
                bool main = MainThreadId < 0 || System.Threading.Thread.CurrentThread.ManagedThreadId == MainThreadId;
                JArray projected = new JArray();
                for (int i = 0; i < args.Length && i < MaxArgs; i++)
                {
                    object o = args[i];
                    JToken p;
                    try
                    {
                        if (!main) p = OffMain(o);
                        else
                        {
                            object v = sub.Handles ? Reflect.Project(o) : Reflect.Brief(o);
                            p = v == null ? JValue.CreateNull() : JToken.FromObject(v);
                        }
                    }
                    catch (Exception ex) { p = "<" + ex.GetType().Name + ">"; }
                    int b = Encoding.UTF8.GetByteCount(p.ToString(Formatting.None));
                    if (b > MaxArgBytes) p = new JObject { { "$clipped", o == null ? "null" : o.GetType().Name }, { "bytes", b } };
                    projected.Add(p);
                }
                if (args.Length > MaxArgs) projected.Add(new JObject { { "$moreArgs", args.Length - MaxArgs } });
                Ring.Add(new Row { Sub = sub.Id, Args = projected });
            }
            catch (Exception) { }
        }

        /// <summary>Off the main thread only immutable scalars are read; anything else is named by
        /// its runtime type (a managed GetType, no Unity call).</summary>
        private static JToken OffMain(object o)
        {
            if (o == null) return JValue.CreateNull();
            if (o is string) return Protocol.Clip((string)o);
            if (o is bool || o is int || o is long || o is float || o is double || o is short || o is byte || o is uint || o is ulong || o is decimal)
                return JToken.FromObject(o);
            if (o is Enum) return o.ToString();
            return new JObject { { "$offMain", o.GetType().Name } };
        }

        private static object Unsubscribe(JToken which)
        {
            List<Sub> gone = new List<Sub>();
            lock (subs)
            {
                if (which.Type == JTokenType.String && (string)which == "all") { gone.AddRange(subs); }
                else if (which.Type == JTokenType.Integer)
                {
                    int id = (int)which;
                    Sub s = subs.Find(x => x.Id == id);
                    if (s == null)
                        return TapPage.Bad("args", "no live subscription " + id + (ended.ContainsKey(id) ? " (it ended: " + ended[id] + ")" : ""));
                    gone.Add(s);
                }
                else return TapPage.Bad("args", "unsubscribe takes a sub id or \"all\"");
            }
            int removed = 0;
            List<object> failed = new List<object>();
            foreach (Sub s in gone)
            {
                string err = Drop(s, "unsubscribed");
                if (err == null) removed++; else failed.Add(new { sub = s.Id, error = err });
            }
            if (failed.Count == 0) return new { ok = true, removed };
            return new { ok = false, code = "threw", error = failed.Count + " handler(s) could not be removed and are STILL attached (kept in the registry; retry unsubscribe)", removed, failed };
        }

        /// <summary>Detaches the handler FIRST; only a detach that worked ends the sub. A throwing
        /// remove accessor leaves the handler attached, so the sub stays registered (RemoveError
        /// set, shown by list) - dropping it would lose the only reference that can ever retry.
        /// Returns null on success, else the error.</summary>
        internal static string Drop(Sub s, string reason)
        {
            lock (subs) if (!subs.Contains(s)) return null;
            try { s.Event.RemoveEventHandler(s.Target, s.Handler); }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                s.RemoveError = inner.GetType().Name + ": " + Protocol.Clip(inner.Message);
                return s.RemoveError;
            }
            lock (subs)
            {
                if (!subs.Remove(s)) return null;
                if (ended.Count >= 64) ended.Clear();
                ended[s.Id] = reason;
            }
            return null;
        }

        /// <summary>Game half, on scene unload: drops every sub whose target <paramref name="gone"/>
        /// says belonged to it.</summary>
        internal static void DropWhere(Func<object, bool> gone, string reason)
        {
            List<Sub> copy;
            lock (subs) copy = new List<Sub>(subs);
            foreach (Sub s in copy)
            {
                bool dead;
                try { dead = gone(s.Target); } catch (Exception) { dead = true; }
                if (dead) Drop(s, reason);
            }
        }

        /// <summary>A destroyed Unity target never fires again; say so rather than wait forever.</summary>
        private static void Prune()
        {
            Func<object, bool> alive = Protocol.UnityAlive;
            if (alive != null) DropWhere(o => o != null && !alive(o), "destroyed");
        }

        internal static string EndedReason(int id)
        {
            lock (subs)
            {
                if (subs.Exists(x => x.Id == id)) return null;
                string r;
                return ended.TryGetValue(id, out r) ? r : "unknown";
            }
        }

        private static object List()
        {
            List<object> rows = new List<object>();
            lock (subs)
                foreach (Sub s in subs)
                {
                    Dictionary<string, object> r = new Dictionary<string, object>
                    {
                        { "sub", s.Id }, { "event", s.Event.Name },
                        { "on", Reflect.ShortName(s.Target == null ? s.Event.DeclaringType : s.Target.GetType()) },
                        { "fired", System.Threading.Thread.VolatileRead(ref s.Fired) }
                    };
                    if (s.RemoveError != null) r["removeError"] = s.RemoveError;
                    rows.Add(r);
                }
            return new { ok = true, subs = rows, next = Ring.Last };
        }

        private static object Read(JObject a)
        {
            long since; bool haveSince; int size, bytes;
            object bad = TapPage.Args(a, out since, out haveSince, out size, out bytes);
            if (bad != null) return bad;
            int subId;
            string subErr = Protocol.IntArg(a, "sub", 0, out subId);
            if (subErr != null) return TapPage.Bad("args", subErr);
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
                if ((subId == 0 || kv.Value.Sub == subId) && (rx == null || scan.Match(kv.Value.Args.ToString(Formatting.None))))
                    hits.Add(kv);
            if (scan.Failure != null) return scan.Refusal();

            int start = haveSince ? 0 : Math.Max(0, hits.Count - size);
            List<object> rows = new List<object>();
            long used = 40, next = newest;
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
                // ONE row alone over the page: args dropped, the row says how big it was.
                if (used + cost > bytes)
                {
                    row.Remove("a");
                    row["clipped"] = cost;
                    cost = TapPage.Bytes(row);
                }
                used += cost;
                rows.Add(row);
            }
            Dictionary<string, object> d = TapPage.Reply(rows, next, more, lost);
            if (subId != 0)
            {
                string why = EndedReason(subId);
                if (why != null) d["ended"] = why;
            }
            return d;
        }

        internal static Dictionary<string, object> Project(long seq, Row r)
        {
            return new Dictionary<string, object> { { "s", seq }, { "sub", r.Sub }, { "a", r.Args } };
        }

        /// <summary>For `wait {event:...}`: the first row of <paramref name="subId"/> after *since
        /// (optionally matching), advancing *since past what was scanned.</summary>
        internal static JToken FirstMatch(int subId, Regex rx, ref long since)
        {
            long lost, newest;
            List<KeyValuePair<long, Row>> rows = Ring.After(since, out lost, out newest);
            long from = since;
            TapPage.WaitGuard(since, newest, 0, "event");
            TapPage.Scan scan = new TapPage.Scan(rx);
            foreach (KeyValuePair<long, Row> kv in rows)
            {
                bool hit = kv.Value.Sub == subId && (rx == null || scan.Match(kv.Value.Args.ToString(Formatting.None)));
                if (scan.Failure != null) throw new WaitFatal("regex", scan.Failure);
                since = kv.Key;
                if (hit) return JToken.FromObject(Project(kv.Key, kv.Value));
            }
            TapPage.WaitGuard(from, newest, lost, "event");
            return null;
        }

        internal static Sub Find(int id)
        {
            lock (subs) return subs.Find(x => x.Id == id);
        }

        internal static void Shutdown()
        {
            List<Sub> copy;
            lock (subs) copy = new List<Sub>(subs);
            foreach (Sub s in copy) Drop(s, "shutdown");
            lock (subs) ended.Clear();
            Ring.Clear();
        }
    }
}
