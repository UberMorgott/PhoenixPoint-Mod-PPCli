using System;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json.Linq;

namespace Morgott.PPBridge
{
    /// <summary>What <c>act {list}</c> asked for, already validated.</summary>
    internal sealed class ActList
    {
        internal string Actor;
        internal string Ability;
        internal int AbilityIndex = -1;
        internal string Src;
        internal bool Targets, All;
        internal int Page, PageSize;
    }

    /// <summary>What <c>act {use}</c> asked for, already validated.</summary>
    internal sealed class ActUse
    {
        internal string Actor;
        internal string Ability;
        internal int AbilityIndex = -1;
        internal string Src;
        internal string TargetActor;
        /// <summary>x,y,z, or null. Exactly one of TargetActor / TargetPos, or neither.</summary>
        internal float[] TargetPos;
    }

    /// <summary>
    /// The game half's handle on ONE issued action. Everything here runs on the main thread, polled
    /// once per frame by <see cref="ActTap"/>'s pending job.
    /// </summary>
    internal interface IActProbe
    {
        /// <summary>Null while the action is still playing out; the finished DTO once it settled.</summary>
        JObject Poll();
        /// <summary>Why it has not settled yet - the body of a timeout reply.</summary>
        JObject Flags();
        /// <summary>Unsubscribe whatever the probe hooked. Idempotent.</summary>
        void Release();
    }

    /// <summary>
    /// The pure half of <c>act</c> - an agent plays a real tactical turn: list the abilities of an
    /// actor, use one (move, shoot, overwatch, any ability) and wait until the game has SETTLED, end
    /// the turn. Names no Unity or game type: the game half (ActGame.cs) installs four delegates, so
    /// argument checking, the single-flight gate, timeout/cancel and every reply's byte budget run
    /// offline in the self-check.
    ///
    /// Contract (token-frugal: defaults 25 rows, no echo of the request, absent = default):
    ///   act {list:{actor?,ability?,src?,targets?,all?}, page?, pageSize?}
    ///       -> {ok, actor:{n,f,ap,apMax,wp,hp,pos}, turn, total, abilities:[{i,def,t,src?,dis?,tk,ap?}], hasMore?}
    ///          targets:true needs `ability` (ONE ability's targets, capped) -> {..., targets:[{a}|{p}], capped?}
    ///   act {squad:true|"all", page?, pageSize?} -> {ok, turn, total, actors:[{n,f?,ap,hp,pos,veh?,sel?}], hasMore?}
    ///   act {use:{actor?,ability,src?,target?:{actor}|{pos:[x,y,z]}}, waitMs?}
    ///       -> {ok, ability, ms, frames, exec, ap:[before,after], hp?:[before,after], dead?, pos?}
    ///   act {endTurn:true} -> {ok, faction, requested:true}
    /// Refusal codes: args, act (offline), scene, noactor, noability, ambiguous, disabled(+dis),
    /// notarget, targetKind, turn, busy, cap, timeout, stale, cancelled, threw.
    /// Main thread only, like every verb.
    /// </summary>
    internal static class ActTap
    {
        internal const int DefaultPageSize = 25;
        internal const int MaxPageSize = 100;
        internal const int DefaultWaitMs = 20000;
        internal const int MaxWaitMs = 120000;
        /// <summary>Targets one `targets:true` / one target match may enumerate. GetTargets() on a
        /// jump or throw is a line-of-sight test per candidate, so this is a cost bound.</summary>
        internal const int MaxScan = 2000;
        /// <summary>Def/actor names longer than this end in "~" (real ones are ~20-40 chars).</summary>
        internal const int NameClip = 48;

        /// <summary>Game half: list one actor's abilities (and one ability's targets).</summary>
        internal static Func<ActList, object> ListRun;
        /// <summary>Game half: (which: "mine"|"all", page, size) -> actors.</summary>
        internal static Func<string, int, int, object> SquadRun;
        /// <summary>Game half: validate + Activate; a refusal DTO, or an <see cref="IActProbe"/>.</summary>
        internal static Func<ActUse, object> UseStart;
        /// <summary>Game half: request the end of the player's turn.</summary>
        internal static Func<object> EndTurnRun;
        /// <summary>Milliseconds, monotonic. A field so the self-check can move time.</summary>
        internal static Func<long> NowMs = () => clock.ElapsedMilliseconds;
        private static readonly Stopwatch clock = Stopwatch.StartNew();

        /// <summary>One `use` in flight at a time: a second action issued before the first settled
        /// is exactly the "all move at once" race the settle exists to prevent.</summary>
        private static Pending busy;

        internal static object Bad(string code, string message) { return new { ok = false, code, error = Protocol.Clip(message) }; }

        internal static object Dispatch(string verb, JObject a)
        {
            if (verb != "act") return null;
            if (a == null) a = new JObject();
            JObject list = a["list"] as JObject;
            JObject use = a["use"] as JObject;
            JToken endT = a["endTurn"];
            JToken squadT = a["squad"];
            bool end = endT != null && endT.Type == JTokenType.Boolean && (bool)endT;
            string squad = squadT == null ? null
                         : squadT.Type == JTokenType.Boolean && (bool)squadT ? "mine"
                         : squadT.Type == JTokenType.String && (string)squadT == "all" ? "all" : "?";
            int modes = (list != null ? 1 : 0) + (use != null ? 1 : 0) + (end ? 1 : 0) + (squad != null ? 1 : 0);
            if (modes != 1 || squad == "?" || (endT != null && !end) || (a["list"] != null && list == null) || (a["use"] != null && use == null))
                return Bad("args", "act takes exactly one of {list:{actor?,ability?,src?,targets?,all?}}, {squad:true|\"all\"}, " +
                                   "{use:{actor?,ability,src?,target?}, waitMs?} or {endTurn:true}");

            int page, size = DefaultPageSize, wait = DefaultWaitMs;
            string err = Protocol.IntArg(a, "page", 0, out page)
                         ?? Protocol.IntArg(a, "pageSize", DefaultPageSize, out size)
                         ?? Protocol.IntArg(a, "waitMs", DefaultWaitMs, out wait);
            if (err == null && page < 0) err = "page must be >= 0";
            if (err == null && (size < 1 || size > MaxPageSize)) err = "pageSize must be 1.." + MaxPageSize;
            if (err == null && (wait < 1 || wait > MaxWaitMs)) err = "waitMs must be 1.." + MaxWaitMs;
            if (err != null) return Bad("args", err);

            if (list != null)
            {
                ActList r = new ActList { Page = page, PageSize = size };
                err = Str(list, "actor", out r.Actor) ?? Str(list, "src", out r.Src)
                      ?? AbilityArg(list, false, out r.Ability, out r.AbilityIndex)
                      ?? Bool(list, "targets", out r.Targets) ?? Bool(list, "all", out r.All);
                if (err != null) return Bad("args", err);
                if (r.Targets && r.Ability == null && r.AbilityIndex < 0)
                    return Bad("cap", "targets:true lists ONE ability's targets - name it with `ability` (enumerating every ability's targets is a line-of-sight test per candidate)");
                if (ListRun == null) return Bad("act", "no act runner installed - this is the offline half, or the mod is shutting down");
                return ListRun(r);
            }
            if (squad != null)
            {
                if (SquadRun == null) return Bad("act", "no act runner installed - this is the offline half, or the mod is shutting down");
                return SquadRun(squad, page, size);
            }
            if (end)
            {
                if (EndTurnRun == null) return Bad("act", "no act runner installed - this is the offline half, or the mod is shutting down");
                if (busy != null) return Bad("busy", "an act use is still settling - wait for it before ending the turn");
                return EndTurnRun();
            }

            ActUse u = new ActUse();
            err = Str(use, "actor", out u.Actor) ?? Str(use, "src", out u.Src) ?? AbilityArg(use, true, out u.Ability, out u.AbilityIndex)
                  ?? TargetArg(use["target"], u);
            if (err != null) return Bad("args", err);
            if (UseStart == null) return Bad("act", "no act runner installed - this is the offline half, or the mod is shutting down");
            if (busy != null) return Bad("busy", "another act use is still settling - one action at a time");

            object started;
            try { started = UseStart(u); }
            catch (Exception ex) { return Bad("threw", ex.GetType().Name + ": " + ex.Message); }
            IActProbe probe = started as IActProbe;
            if (probe == null) return started;          // a refusal: nothing was issued
            busy = new Pending(probe, wait, NowMs());
            return busy;
        }

        /// <summary>Scene unload / mod shutdown: the probe's subscriptions go, the gate opens.</summary>
        internal static void Abort()
        {
            Pending p = busy;
            if (p != null) p.Release();
            busy = null;
        }

        internal static void Shutdown()
        {
            Abort();
            ListRun = null; SquadRun = null; UseStart = null; EndTurnRun = null;
        }

        // ------------------------------------------------------------------ arg helpers

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

        /// <summary>`ability` = a def name / type alias (string) or the `i` a list reply handed out.</summary>
        private static string AbilityArg(JObject o, bool required, out string name, out int index)
        {
            name = null; index = -1;
            JToken t = o["ability"];
            if (t == null || t.Type == JTokenType.Null) return required ? "use needs `ability`: a def name, a type alias (move, shoot, overwatch, ...) or the `i` from act list" : null;
            if (t.Type == JTokenType.String) { name = (string)t; return name.Length == 0 ? "ability must not be empty" : null; }
            string err = Protocol.IntArg(o, "ability", -1, out index);
            if (err != null) return "ability must be a string or a JSON integer i";
            return index < 0 ? "ability index must be >= 0" : null;
        }

        private static string TargetArg(JToken t, ActUse u)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            JObject o = t as JObject;
            if (o == null) return "target must be {actor:\"name|h:..|@selected\"} or {pos:[x,y,z]}";
            JToken ta = o["actor"], tp = o["pos"];
            if ((ta != null) == (tp != null) || o.Count != 1) return "target takes exactly one of {actor} or {pos}";
            if (ta != null)
            {
                if (ta.Type != JTokenType.String || ((string)ta).Length == 0) return "target.actor must be a non-empty string";
                u.TargetActor = (string)ta;
                return null;
            }
            JArray p = tp as JArray;
            if (p == null || p.Count != 3) return "target.pos must be [x,y,z]";
            float[] v = new float[3];
            for (int i = 0; i < 3; i++)
            {
                if (p[i].Type != JTokenType.Integer && p[i].Type != JTokenType.Float) return "target.pos must hold three numbers";
                double d = (double)p[i];
                if (double.IsNaN(d) || double.IsInfinity(d) || Math.Abs(d) > 100000) return "target.pos out of range";
                v[i] = (float)d;
            }
            u.TargetPos = v;
            return null;
        }

        // ------------------------------------------------------------------ reply shaping (budgeted)

        internal static string Clip(string s)
        {
            if (s == null) return null;
            return s.Length > NameClip ? s.Substring(0, NameClip) + "~" : s;
        }

        internal static double R2(double v) { return Math.Round(v, 2); }

        internal static JArray Pos(float x, float y, float z) { return new JArray(R2(x), R2(y), R2(z)); }

        /// <summary>One ability row. Absent = default: no `dis` = usable, no `src` = sourced by the actor.</summary>
        internal static JObject AbilityRow(int i, string def, string type, string src, string dis, string tk, float ap)
        {
            JObject o = new JObject { ["i"] = i, ["def"] = Clip(def), ["t"] = Clip(type) };
            if (src != null) o["src"] = Clip(src);
            if (dis != null && dis != "NotDisabled") o["dis"] = Clip(dis);
            o["tk"] = tk;
            if (ap > 0) o["ap"] = R2(ap);
            return o;
        }

        internal static JObject ActorRow(string name, string faction, float ap, float apMax, float wp, float hp, JArray pos)
        {
            JObject o = new JObject { ["n"] = Clip(name) };
            if (faction != null) o["f"] = Clip(faction);
            o["ap"] = R2(ap);
            if (apMax > 0) o["apMax"] = R2(apMax);
            o["wp"] = R2(wp);
            o["hp"] = R2(hp);
            o["pos"] = pos;
            return o;
        }

        /// <summary>Pages any row list the same way: {total, rows[page], hasMore?}.</summary>
        internal static void PageInto(JObject reply, string key, List<JObject> rows, int page, int size)
        {
            reply["total"] = rows.Count;
            if (page > 0) reply["page"] = page;
            JArray arr = new JArray();
            long from = (long)page * size;
            for (long i = from; i < rows.Count && i < from + size; i++) arr.Add(rows[(int)i]);
            reply[key] = arr;
            if (from + size < rows.Count) reply["hasMore"] = true;
        }

        // ------------------------------------------------------------------ the cross-frame half

        /// <summary>
        /// Polls the probe once per frame until it settles, the caller's waitMs runs out, or the job
        /// is cancelled. Neither timeout nor cancel UNDOES anything: the action was issued and the
        /// game plays it out - both replies say so with issued:true.
        /// </summary>
        internal sealed class Pending : IPending, IReleasable
        {
            private readonly IActProbe probe;
            private readonly int wait;
            private readonly long start;
            private bool done;

            internal Pending(IActProbe probe, int wait, long start) { this.probe = probe; this.wait = wait; this.start = start; }

            public void Release()
            {
                if (done) return;
                done = true;
                if (busy == this) busy = null;
                try { probe.Release(); } catch (Exception) { }
            }

            private object End(object r) { Release(); return r; }

            public object Tick(bool cancelled)
            {
                if (done) return Bad("act", "request already ended");
                if (cancelled)
                    return End(new { ok = false, code = "cancelled", issued = true, error = "cancelled while settling - the action was already issued and is NOT undone" });
                JObject r;
                try { r = probe.Poll(); }
                catch (Exception ex) { return End(new { ok = false, code = "threw", issued = true, error = Protocol.Clip(ex.GetType().Name + ": " + ex.Message) }); }
                if (r != null)
                {
                    if (r["ms"] == null) r["ms"] = NowMs() - start;
                    return End(r);
                }
                long ms = NowMs() - start;
                if (ms <= wait) return null;
                JObject flags;
                try { flags = probe.Flags(); } catch (Exception) { flags = null; }
                return End(new JObject
                {
                    ["ok"] = false, ["code"] = "timeout", ["issued"] = true,
                    ["error"] = "not settled after " + wait + " ms - the action is still playing out (not cancelled); `settle` names what is still busy",
                    ["settle"] = flags
                });
            }
        }
    }
}
