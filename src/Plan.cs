using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Morgott.PPBridge
{
    /// <summary>
    /// A job that outlives the frame it started on. Ticked once per frame by PPBridgeMain.Runner,
    /// on the MAIN THREAD, and never blocking it: every implementation here returns immediately with
    /// null meaning "still running". This is also what makes <c>cancel</c> real rather than
    /// decorative - a synchronous verb cannot be interrupted, a ticked one is asked every frame.
    /// </summary>
    internal interface IPending
    {
        /// <summary>Null while still running; the final DTO when done. Never throws.</summary>
        object Tick(bool cancelled);
    }

    /// <summary>
    /// P3: the temporal half of PPCLI - <c>wait</c>, <c>snapshot</c>/<c>restore</c> and the plan
    /// engine. Like Reflect.cs this names NO Unity and NO game type: the one thing it cannot express
    /// in terms of other verbs (starting a save and knowing when it finished) arrives as a delegate
    /// the game half installs, so the whole file is exercisable offline.
    ///
    /// Deliberately NOT a scripting language. It has sequencing, variables, waiting, bounded
    /// branching, bounded repetition and cleanup - and no expressions, no user functions and no
    /// types. Everything computational is already a `call`.
    /// </summary>
    internal static class Plan
    {
        // --- caps. Every one of them is the difference between a plan and a hung game.
        internal const int DefaultWaitMs = 30000;
        /// <summary>The ceiling on ONE `wait` and on a whole plan. It must be at least the longest
        /// timeoutMs any shipped plan asks for, or the clamp silently halves it: start-campaign.json
        /// asks 900000 and was being honoured as 600000, so a campaign that legitimately took 11
        /// minutes came back as a timeout with no hint that the number it was measured against was
        /// not the number it declared.</summary>
        internal const int MaxWaitMs = 900000;
        internal const int DefaultPollFrames = 10;
        internal const int DefaultPlanMs = 60000;
        internal const int DefaultMaxSteps = 200;
        internal const int HardMaxSteps = 2000;
        /// <summary>Per `repeat`, per pass. A plan is still bounded by maxSteps on top of this.</summary>
        internal const int MaxIterations = 100;
        internal const int MaxNesting = 4;
        /// <summary>Steps run in ONE frame before the plan yields, so a long plan cannot stall render.</summary>
        internal const int StepsPerTick = 16;
        /// <summary>How much extra time the cleanup block gets after the plan's own deadline. Not a
        /// const so the self-check can shorten it and still prove the grace is a real bound.</summary>
        internal static int FinallyGraceMs = 15000;
        internal const int MaxTrace = 500;

        /// <summary>Returns null for a verb this file does not own, so Protocol falls through.</summary>
        internal static object Dispatch(string verb, JObject a)
        {
            switch (verb)
            {
                case "wait": return Waiter.Create(a);
                case "plan": return PlanRun.Create(a);
                case "snapshot": return Snapshot(a);
                case "restore": return Restore(a);
                default: return null;
            }
        }

        internal static object Bad(string code, string message)
        {
            return new { ok = false, code, error = Protocol.Clip(message) };
        }

        internal static bool Truthy(JToken t)
        {
            if (t == null) return false;
            switch (t.Type)
            {
                case JTokenType.Null:
                case JTokenType.Undefined: return false;
                case JTokenType.Boolean: return (bool)t;
                case JTokenType.Integer: return (long)t != 0;
                case JTokenType.Float: return (double)t != 0;
                case JTokenType.String: return ((string)t ?? "").Length > 0;
                case JTokenType.Array: return ((JArray)t).Count > 0;
                default: return true;    // an object, i.e. a handle DTO, is a thing that exists
            }
        }

        internal static int Clamp(JToken t, int fallback, int min, int max)
        {
            if (t == null || t.Type != JTokenType.Integer) return fallback;
            long v = (long)t;
            return v < min ? min : v > max ? max : (int)v;
        }

        // ------------------------------------------------------------------ snapshot / restore

        /// <summary>
        /// A THIN wrapper over the game's own save path - PPBridge writes no serialization code. A
        /// tactical save already carries every actor, destructible, voxel/dark volume and the
        /// level/faction/mission state (TacLevelSavegame.cs:45), with per-actor position, faction,
        /// stats, statuses and inventory.
        ///
        /// Two traps the console command does not handle and this does:
        ///   - PhoenixSaveManager.EnsureUnique (:154-168) renames a colliding save to "&lt;name&gt;_1",
        ///     so `save_game foo` twice leaves foo AND foo_1 and `restore foo` would come back to the
        ///     FIRST one. The game half deletes an existing save of that name first, so the name a
        ///     snapshot is stored under is the name that was asked for.
        ///   - SaveWithName silently does NOTHING when the current level has no ISavegameProvider
        ///     (:551), i.e. in the main menu. That is refused rather than reported as a save.
        /// </summary>
        private static object Snapshot(JObject a)
        {
            string name = a == null ? null : (string)a["name"];
            if (string.IsNullOrEmpty(name)) return Bad("args", "snapshot needs {name}");
            if (Protocol.SnapshotStart == null) return Bad("args", "no snapshot runner installed");
            return new Polled(Protocol.SnapshotStart(name), Clamp(a["timeoutMs"], DefaultWaitMs, 1, MaxWaitMs));
        }

        /// <summary>
        /// The native `load_game` and nothing else. It returns as soon as the command is issued
        /// because LOAD HAS NO COMPLETION SIGNAL - SerializationCommands.LoadGame (:42) starts a
        /// coroutine that ends in FinishLevelAndLoadGame, and nothing observable is handed back. The
        /// honest contract is therefore "issued", and the caller follows it with a `wait`; a plan does
        /// exactly that in one request.
        ///
        /// The existence check is worth its two lines: the command's own "Could not find savegame"
        /// runs on a LATER frame, inside the coroutine, so it can never reach the captured console
        /// output of this call and a missing save would otherwise look like a successful restore.
        /// </summary>
        private static object Restore(JObject a)
        {
            string name = a == null ? null : (string)a["name"];
            if (string.IsNullOrEmpty(name)) return Bad("args", "restore needs {name}");
            if (Protocol.ConsoleRun == null) return Bad("args", "no console runner installed");
            if (Protocol.SaveExists != null && !Protocol.SaveExists(name))
                return Bad("state", "no savegame called '" + name + "' - nothing was loaded");
            object console = Protocol.ConsoleRun("load_game", new[] { name });
            return new
            {
                ok = true,
                issued = "load_game",
                name,
                note = "load has no completion signal; follow with wait {\"ready\":true} in tactical " +
                       "or wait {\"phase\":\"geoscape\"}",
                console
            };
        }

        // ------------------------------------------------------------------ pendings

        /// <summary>Wraps a game-half poll that answers null while it runs. Used by snapshot.</summary>
        private sealed class Polled : IPending
        {
            private readonly Func<object> poll;
            private readonly DateTime deadline;
            private readonly DateTime started = DateTime.UtcNow;

            internal Polled(Func<object> poll, int timeoutMs)
            {
                this.poll = poll;
                deadline = started.AddMilliseconds(timeoutMs);
            }

            public object Tick(bool cancelled)
            {
                int ms = (int)(DateTime.UtcNow - started).TotalMilliseconds;
                if (cancelled) return new { ok = false, code = "cancelled", error = "cancelled after " + ms + " ms", waitedMs = ms };
                object done;
                try { done = poll == null ? Bad("args", "no poll installed") : poll(); }
                catch (Exception ex) { return Bad("threw", ex.GetType().Name + ": " + ex.Message); }
                if (done != null) return done;
                if (DateTime.UtcNow > deadline)
                    return new { ok = false, code = "timeout", error = "still running after " + ms + " ms", waitedMs = ms };
                return null;
            }
        }

        /// <summary>
        /// `wait` - the frame-polled predicate. It NEVER blocks Update: one cheap evaluation every
        /// `everyFrames` frames and an immediate return either way.
        ///
        /// The predicates are the RIGHT ones, which is the whole point of the verb:
        ///   - {"ready":true} is TacticalLevelController.HasAnyTurnStarted (:237,631,715), not
        ///     Level.CurrentState == Playing. Playing flips EARLIER, before the level's own waiters
        ///     finish (Level.cs:225), so a wait on it returns into a mission that is not yet driveable.
        ///   - {"phase":"..."} is the `state` verb's own phase field.
        ///   - {"call":{...}} is any `call` at all, re-evaluated each poll; that is how "the actor is
        ///     InPlay" is expressed (ActorComponent.cs:114, ActorSpawner.cs:23).
        ///   - {"forMs":N} has no predicate at all: it yields N ms of REAL time and then SUCCEEDS.
        ///     The one thing the other three cannot say, and the fast-forward primitive - see the
        ///     `duration` field for why a never-true predicate is not an acceptable substitute.
        ///
        /// A predicate that ERRORS counts as "not true yet" on purpose: @tac is null while a mission
        /// loads, and the whole reason to wait is that the thing is not there yet. The last error is
        /// carried into the timeout result, so a predicate that is permanently broken still says why.
        /// </summary>
        internal sealed class Waiter : IPending
        {
            private readonly JObject call;        // the predicate, already in `call` form
            private readonly string phase;        // or a phase name to match against `state`
            private readonly int every;
            private readonly bool negate;
            /// <summary>The one wait with NO predicate: yield for a fixed span of real time and then
            /// SUCCEED. It exists because game time is driven by real time through Base.Core.Timing
            /// (Timing.cs:79,100) - "let the geoscape run for 30 s at Scale 3600" is the fast-forward
            /// primitive, and there is no single fixed-argument call that turns "the campaign clock
            /// has passed T" into a bool. Spelling it as a predicate that never comes true would
            /// report a healthy fast-forward as a step FAILURE.</summary>
            private readonly bool duration;
            private readonly DateTime started = DateTime.UtcNow;
            private readonly DateTime deadline;
            private int countdown, polls;
            private string lastError;
            private JToken last;

            private Waiter(JObject call, string phase, int every, int timeoutMs, bool negate, bool duration = false)
            {
                this.call = call;
                this.phase = phase;
                this.every = every;
                this.negate = negate;
                this.duration = duration;
                countdown = 1;                     // evaluate on the very first tick
                deadline = started.AddMilliseconds(timeoutMs);
            }

            internal static object Create(JObject a)
            {
                if (a == null) return Bad("args", "wait needs {ready|phase|call} and an optional timeoutMs");
                int every = Clamp(a["everyFrames"], DefaultPollFrames, 1, 600);
                int timeout = Clamp(a["timeoutMs"], DefaultWaitMs, 1, MaxWaitMs);
                // The engine has no `not`, and half the interesting predicates are the wrong way
                // round: "the ability has STOPPED executing", "the list is EMPTY". Expressing those
                // through System.Object.Equals(false, x) needs the live value as an ARGUMENT, and
                // arguments are substituted once when the step starts - so it cannot be done at all.
                bool negate = Truthy(a["not"]);

                JToken forMs = a["forMs"];
                if (forMs != null && forMs.Type == JTokenType.Integer)
                    return new Waiter(null, null, every, Clamp(forMs, DefaultWaitMs, 1, MaxWaitMs), negate: false, duration: true);

                string phase = (string)a["phase"];
                if (!string.IsNullOrEmpty(phase)) return new Waiter(null, phase, every, timeout, negate);

                if (Truthy(a["ready"]))
                    return new Waiter(JObject.Parse("{'op':'get','target':'@tac','member':'HasAnyTurnStarted'}"),
                                      null, every, timeout, negate);

                JObject call = a["call"] as JObject;
                if (call != null) return new Waiter(call, null, every, timeout, negate);

                return Bad("args", "wait needs one of {\"ready\":true} (HasAnyTurnStarted), " +
                                   "{\"phase\":\"tactical|geoscape|menu|loading\"}, {\"call\":{...}} " +
                                   "or {\"forMs\":N} (yield N ms of REAL time, then succeed)");
            }

            public object Tick(bool cancelled)
            {
                int ms = (int)(DateTime.UtcNow - started).TotalMilliseconds;
                if (cancelled) return new { ok = false, code = "cancelled", error = "cancelled after " + ms + " ms", waitedMs = ms, polls };

                if (duration)
                {
                    if (DateTime.UtcNow <= deadline) return null;
                    return new { ok = true, waitedMs = ms, polls, negated = false, value = (JToken)null };
                }

                if (--countdown <= 0)
                {
                    countdown = every;
                    polls++;
                    if (Satisfied()) return new { ok = true, waitedMs = ms, polls, negated = negate, value = last };
                }
                if (DateTime.UtcNow <= deadline) return null;
                return new
                {
                    ok = false,
                    code = "timeout",
                    error = "the predicate was still " + (negate ? "true" : "false") + " after " + ms +
                            " ms and " + polls + " polls",
                    waitedMs = ms,
                    polls,
                    negated = negate,
                    last,
                    lastError,
                    // THE PREDICATE ITSELF, already substituted. `last:false` says the assertion
                    // failed and never says WHY, and an assertion of the shape
                    // Equals("NotDisabled", "${KEY.value}") has already READ the reason into its own
                    // arguments one step earlier - weapon-test.json's `assert-enabled` computed
                    // "WeaponNotOwned" and then threw it away. Echoing the substituted call back
                    // hands every such assertion its reason for free, in the engine, rather than one
                    // plan at a time. Null for a {"phase":...} wait, which has no call.
                    predicate = call
                };
            }

            /// <summary>
            /// The predicate with <c>not</c> applied. An ERROR satisfies NEITHER polarity: @tac is
            /// null while a mission loads, and "the call failed" must never read as "the thing is
            /// false" - a negated wait would otherwise return the instant a level started loading.
            /// </summary>
            private bool Satisfied()
            {
                bool got = Evaluate();
                return lastError == null && got != negate;
            }

            private bool Evaluate()
            {
                try
                {
                    if (phase != null)
                    {
                        if (Protocol.StateProbe == null) { lastError = "no state probe installed"; return false; }
                        JToken got = JToken.FromObject(Protocol.StateProbe())["phase"];
                        last = got;
                        lastError = null;
                        return got != null && string.Equals((string)got, phase, StringComparison.OrdinalIgnoreCase);
                    }

                    JToken dto = JToken.FromObject(Reflect.Dispatch("call", call));
                    if (!Truthy(dto["ok"]))
                    {
                        lastError = (string)dto["error"];
                        last = null;
                        return false;
                    }
                    lastError = null;
                    last = dto["value"];
                    return Truthy(last);
                }
                catch (Exception ex) { lastError = ex.GetType().Name + ": " + ex.Message; return false; }
            }
        }

        // ------------------------------------------------------------------ the plan engine

        /// <summary>
        /// One declarative, cross-frame, main-thread run. Wire shape:
        /// <code>
        /// {"vars":{...}, "timeoutMs":60000, "maxSteps":200,
        ///  "steps":[ {"id":"dd","verb":"call","args":{...},"save":"DD","if":"${X}","onError":"fail"} ],
        ///  "finally":[ ... ],
        ///  "output":{"actor":"${ACTOR.value.h}"}}
        /// </code>
        /// Substitution is the whole "language": <c>${name.json.path}</c> inside any string in a
        /// step's args. Alone in a string it is replaced by the stored TOKEN (so a number stays a
        /// number); embedded, it is interpolated as text. An unresolvable name FAILS the step - it is
        /// never quietly null, which is how a plan would otherwise call the right method with the
        /// wrong argument.
        /// </summary>
        internal sealed class PlanRun : IPending
        {
            private sealed class Frame
            {
                internal JArray Steps;
                internal int Index;
                internal int Left;        // extra passes still allowed
                internal JToken While;    // re-checked before each extra pass; null = a plain block
            }

            private static readonly Regex Whole = new Regex(@"^\$\{([A-Za-z0-9_][A-Za-z0-9_.\[\]]*)\}$");
            private static readonly Regex Embedded = new Regex(@"\$\{([A-Za-z0-9_][A-Za-z0-9_.\[\]]*)\}");
            /// <summary>The ONE extra rule: <c>"${...NAME}"</c> as an element of an ARRAY splices that
            /// array variable's elements into the surrounding array. Plain <c>${NAME}</c> stays what it
            /// always was - one value, nested - because a `call` arg legitimately IS an array when the
            /// method takes one; only the spread form flattens.</summary>
            private static readonly Regex Spread = new Regex(@"^\$\{\.\.\.([A-Za-z0-9_][A-Za-z0-9_.\[\]]*)\}$");

            private readonly JObject vars = new JObject();
            private readonly JArray cleanup;
            private readonly JObject output;
            private readonly List<Frame> stack = new List<Frame>();
            private readonly List<object> trace = new List<object>();
            private readonly DateTime started = DateTime.UtcNow;
            private readonly DateTime deadline;
            private readonly int maxSteps;

            private IPending inner;
            private string innerId, innerVerb, innerOnError, pendingSave;
            private DateTime innerStarted;
            /// <summary>Steps run in the CURRENT block. Cleanup gets its own budget - sharing one
            /// counter meant a plan that died on the step cap could never run its own cleanup.</summary>
            private int executed;
            private int mainSteps;
            private bool inCleanup;
            private object failure;          // the FIRST real failure; cleanup never overwrites it

            private PlanRun(JArray steps, JArray cleanup, JObject output, JObject seed, int timeoutMs, int maxSteps)
            {
                this.cleanup = cleanup;
                this.output = output;
                this.maxSteps = maxSteps;
                if (seed != null) foreach (KeyValuePair<string, JToken> kv in seed) vars[kv.Key] = kv.Value;
                deadline = started.AddMilliseconds(timeoutMs);
                stack.Add(new Frame { Steps = steps });
            }

            internal static object Create(JObject a)
            {
                if (a == null) return Bad("args", "plan needs {plan:{steps:[...]}} or {steps:[...]}");
                JObject p = a["plan"] as JObject ?? a;
                JArray steps = p["steps"] as JArray;
                if (steps == null) return Bad("args", "plan needs a \"steps\" array");

                // Caller vars win over the plan file's own defaults - that is what parameterises a
                // stored plan without editing it.
                JObject seed = new JObject();
                JObject fileVars = p["vars"] as JObject;
                if (fileVars != null) foreach (KeyValuePair<string, JToken> kv in fileVars) seed[kv.Key] = kv.Value;
                JObject callerVars = a["vars"] as JObject;
                if (callerVars != null) foreach (KeyValuePair<string, JToken> kv in callerVars) seed[kv.Key] = kv.Value;

                return new PlanRun(steps,
                                   p["finally"] as JArray,
                                   p["output"] as JObject,
                                   seed,
                                   Clamp(a["timeoutMs"] ?? p["timeoutMs"], DefaultPlanMs, 1, MaxWaitMs),
                                   Clamp(a["maxSteps"] ?? p["maxSteps"], DefaultMaxSteps, 1, HardMaxSteps));
            }

            public object Tick(bool cancelled)
            {
                try { return Step(cancelled); }
                // A throwing engine must still hand back a result, or the job never completes and the
                // client polls until its own timeout with nothing to show for it.
                catch (Exception ex) { return Done(Bad("threw", "the plan engine threw: " + ex.GetType().Name + ": " + ex.Message)); }
            }

            private object Step(bool cancelled)
            {
                if (cancelled && !inCleanup) return EnterCleanup(new { ok = false, code = "cancelled", error = "cancelled by the client" });
                if (Expired()) return OnDeadline(" ms deadline");

                for (int budget = 0; budget < StepsPerTick; budget++)
                {
                    if (inner != null)
                    {
                        object got = inner.Tick(cancelled);
                        if (got == null) return null;                 // still waiting; next frame
                        inner = null;
                        object refused = Record(innerId, innerVerb, got, (int)(DateTime.UtcNow - innerStarted).TotalMilliseconds, innerOnError);
                        if (refused != null) return refused;
                        continue;
                    }

                    JObject step = Next();
                    if (step == null)
                    {
                        if (!inCleanup) return EnterCleanup(null);
                        return Done(null);
                    }

                    if (++executed > maxSteps)
                        return inCleanup ? Done(null)
                                         : EnterCleanup(Bad("cap", "the plan hit its " + maxSteps + " step cap - raise maxSteps or shorten the plan"));

                    object refusal = Execute(step);
                    if (refusal != null) return refusal;
                    if (inner != null) return null;                   // a wait started; yield the frame
                    if (Expired()) return OnDeadline(" ms deadline, mid-step");
                }
                return null;                                          // budget spent, resume next frame
            }

            /// <summary>
            /// The ONE door the clock uses, called only once <see cref="Expired"/> is already true.
            /// In the main block it opens cleanup (null = the loop drains it next tick); in cleanup
            /// itself it ENDS the plan, because the grace period is a bound and not a suggestion.
            /// Without that half, a `finally` holding a wait that can never be satisfied parked the
            /// job forever: both timeout checks used to be guarded by !inCleanup, so nothing ever
            /// read the grace deadline and FinallyGraceMs was decorative.
            /// </summary>
            private object OnDeadline(string what)
            {
                if (inCleanup)
                    return Done(Bad("timeout", "the finally block ran past its " + FinallyGraceMs + " ms grace period"));
                return EnterCleanup(new
                {
                    ok = false,
                    code = "timeout",
                    error = "the plan ran past its " + (int)(deadline - started).TotalMilliseconds + what
                });
            }

            private bool Expired()
            {
                return DateTime.UtcNow > (inCleanup ? deadline.AddMilliseconds(FinallyGraceMs) : deadline);
            }

            /// <summary>The next step, popping and repeating frames as needed. Null = nothing left.</summary>
            private JObject Next()
            {
                while (stack.Count > 0)
                {
                    Frame f = stack[stack.Count - 1];
                    if (f.Index < f.Steps.Count)
                    {
                        JObject step = f.Steps[f.Index++] as JObject;
                        if (step == null) continue;                   // a non-object entry is skipped, not fatal
                        return step;
                    }
                    // The block ran out. Another pass only if there is one left AND the guard holds.
                    // An unresolvable guard ends the loop rather than throwing: `while` naming a var
                    // that no longer exists means "stop", never "keep going forever".
                    if (f.Left > 0 && (f.While == null || Truthy(Try(f.While))))
                    {
                        f.Left--;
                        f.Index = 0;
                        continue;
                    }
                    stack.RemoveAt(stack.Count - 1);
                }
                return null;
            }

            /// <summary>Runs one step. Returns a finished plan DTO only when the plan must stop here.</summary>
            private object Execute(JObject step)
            {
                string id = (string)step["id"] ?? ("#" + executed);
                string verb = (string)step["verb"];
                string onError = (string)step["onError"] ?? "fail";
                pendingSave = null;

                // Bounded branching: a skipped step is recorded, so a trace never has a silent gap.
                // A guard that names an unset variable is a FAILED step, not a silent skip.
                try
                {
                    if (step["if"] != null && !Truthy(Resolve(step["if"]))) { trace.Add(new { id, verb, skipped = "if" }); return null; }
                    if (step["unless"] != null && Truthy(Resolve(step["unless"]))) { trace.Add(new { id, verb, skipped = "unless" }); return null; }
                }
                catch (Exception ex) { return Record(id, verb, Bad("var", ex.Message), 0, onError); }

                if (verb == "repeat") return Repeat(step, id);
                if (string.IsNullOrEmpty(verb)) return Record(id, verb, Bad("args", "step has no verb"), 0, onError);
                // Bounded by construction: a plan cannot start a plan, so the engine cannot recurse.
                if (verb == "plan") return Record(id, verb, Bad("args", "a plan may not run a plan"), 0, onError);

                JObject args;
                try { args = Resolve(step["args"]) as JObject; }
                catch (Exception ex) { return Record(id, verb, Bad("var", ex.Message), 0, onError); }

                DateTime t0 = DateTime.UtcNow;
                pendingSave = (string)step["save"];
                object result = Protocol.Dispatch(new Job { Id = id, Verb = verb, Args = args });
                IPending pending = result as IPending;
                if (pending != null)
                {
                    // A cross-frame step is parked, not run: the save happens when it FINISHES, which
                    // is the whole difference between `wait` as a step and `wait` as a call.
                    inner = pending;
                    innerId = id;
                    innerVerb = verb;
                    innerOnError = onError;
                    innerStarted = t0;
                    return null;
                }
                return Record(id, verb, result, (int)(DateTime.UtcNow - t0).TotalMilliseconds, onError);
            }

            /// <summary>Bounded repetition. `times` is capped and every pass still burns maxSteps.</summary>
            private object Repeat(JObject step, string id)
            {
                if (stack.Count >= MaxNesting)
                    return Record(id, "repeat", Bad("cap", "repeat nested deeper than " + MaxNesting), 0, "fail");
                JObject a = step["args"] as JObject;
                JArray body = a == null ? null : a["steps"] as JArray;
                if (body == null) return Record(id, "repeat", Bad("args", "repeat needs args {times, steps[]}"), 0, "fail");
                int times = Clamp(a["times"], 1, 1, MaxIterations);
                trace.Add(new { id, verb = "repeat", times });
                // Left counts the EXTRA passes: the first one is the frame simply being walked.
                stack.Add(new Frame { Steps = body, Left = times - 1, While = a["while"] });
                return null;
            }

            /// <summary>Files a finished step's result, saves it, and decides whether the plan goes on.</summary>
            private object Record(string id, string verb, object result, int ms, string onError)
            {
                JToken dto;
                try { dto = JToken.FromObject(result); }
                catch (Exception ex) { dto = JObject.FromObject(Bad("dto", "unprojectable step result: " + ex.Message)); }

                string save = pendingSave;
                pendingSave = null;
                if (save != null) vars[save] = dto;

                bool ok = Truthy(dto["ok"]);
                if (trace.Count < MaxTrace)
                {
                    if (ok) trace.Add(new { id, verb, ok = true, ms });
                    else trace.Add(new { id, verb, ok = false, ms, error = Protocol.Clip((string)dto["error"]), code = (string)dto["code"] });
                }
                if (ok || onError == "continue") return null;
                return Fail(id, verb, dto);
            }

            /// <summary>
            /// Called only from Record, which has already traced the step. A failing step inside the
            /// CLEANUP block never aborts it - cleanup exists to run after something already went
            /// wrong, so most of its steps are expected to be release-what-was-never-taken, and
            /// letting the first of those end the block would strand every release after it.
            /// </summary>
            private object Fail(string id, string verb, JToken dto)
            {
                object why = new
                {
                    ok = false,
                    code = "step",
                    error = "step '" + id + "' (" + verb + ") failed: " + Protocol.Clip((string)dto["error"]),
                    step = id,
                    result = dto
                };
                return inCleanup ? null : EnterCleanup(why);
            }

            /// <summary>
            /// The MANDATORY cleanup. It runs on success, on failure, on timeout and on cancellation -
            /// there is exactly one door out of the step loop and it goes through here. It gets its own
            /// grace period past the plan's deadline so an expired plan still releases what it took.
            /// </summary>
            private object EnterCleanup(object why)
            {
                if (failure == null) failure = why;
                if (inCleanup) return Done(null);
                mainSteps = executed;
                executed = 0;
                inCleanup = true;
                inner = null;
                pendingSave = null;
                stack.Clear();
                if (cleanup == null || cleanup.Count == 0) return Done(null);
                stack.Add(new Frame { Steps = cleanup });
                trace.Add(new { verb = "finally", steps = cleanup.Count });
                return null;                                           // the loop drains it next tick
            }

            private object Done(object lateFailure)
            {
                if (failure == null) failure = lateFailure;
                JObject f = failure == null ? null : JObject.FromObject(failure);

                // A FAILED plan PUBLISHES NO OUTPUT, and that rule lives HERE rather than in any plan
                // file. `output` used to be resolved unconditionally, so a plan whose own assertion had
                // just refused the run still handed back every figure it had measured - the weapon
                // bench asserted that nothing was wedged, failed that assertion, and returned hit rate,
                // damage and dispersion anyway. A caller reading those numbers is reading a measurement
                // the plan itself said was invalid. The plan engine has no conditional output and
                // should not grow one: putting the gate in the engine means the NEXT plan author gets
                // it without knowing it exists, where a per-plan opt-in is one line away from being
                // forgotten. What replaces the figures is the failing step's own result DTO, which
                // carries the value that failed (a `wait` reports it as `last`), so the reason and the
                // number that caused it still reach the caller.
                JObject outs = null;
                string withheld = null;
                if (output != null && f != null)
                {
                    withheld = "the plan failed" + ((string)f["step"] == null ? "" : " at step '" + (string)f["step"] + "'") +
                               ", so its " + output.Count + " output field(s) were NOT resolved: a figure read from a run " +
                               "the plan itself refused is not a measurement. See `result` for the step that failed.";
                }
                else if (output != null)
                {
                    outs = new JObject();
                    foreach (KeyValuePair<string, JToken> kv in output)
                    {
                        try { outs[kv.Key] = Resolve(kv.Value); }
                        catch (Exception ex) { outs[kv.Key] = "unresolved: " + ex.Message; }
                    }
                }
                return new
                {
                    ok = failure == null,
                    code = f == null ? null : (string)f["code"],
                    error = f == null ? null : (string)f["error"],
                    step = f == null ? null : (string)f["step"],
                    // The failing step's whole DTO. It is what the withheld output is replaced BY, so
                    // the number the assertion tripped on is still in the answer.
                    result = f == null ? null : f["result"],
                    steps = inCleanup ? mainSteps : executed,
                    elapsedMs = (int)(DateTime.UtcNow - started).TotalMilliseconds,
                    // Two fields, because "the block was entered" and "N steps of it ran" are
                    // different claims and a plan with an empty finally would otherwise report the
                    // first as if it were the second.
                    cleanupRan = inCleanup,
                    cleanupSteps = inCleanup ? executed : 0,
                    output = outs,
                    outputWithheld = withheld,
                    trace = trace.ToArray()
                };
            }

            // -------------------------------------------------------------- substitution

            /// <summary>
            /// Replaces every <c>${path}</c> in a token tree. Alone in a string it yields the stored
            /// TOKEN, so a number stays a number and a nested object stays an object; embedded, it is
            /// interpolated. Throws on an unknown name - the caller turns that into a failed step.
            /// </summary>
            private JToken Resolve(JToken t)
            {
                if (t == null) return null;
                if (t.Type == JTokenType.String)
                {
                    string s = (string)t;
                    if (s.IndexOf("${", StringComparison.Ordinal) < 0) return t;
                    // A spread that reached here is not inside an array, and neither regex below
                    // matches it - it would be handed on as the literal text "${...X}" and the step
                    // would call the right method with a nonsense argument.
                    if (Spread.IsMatch(s))
                        throw new InvalidOperationException(s + " spreads an array and only works as an " +
                            "element of an array; use ${" + Spread.Match(s).Groups[1].Value + "} for the value itself");
                    Match whole = Whole.Match(s);
                    if (whole.Success) return Lookup(whole.Groups[1].Value).DeepClone();
                    StringBuilder b = new StringBuilder();
                    int at = 0;
                    foreach (Match m in Embedded.Matches(s))
                    {
                        b.Append(s, at, m.Index - at);
                        JToken v = Lookup(m.Groups[1].Value);
                        b.Append(v.Type == JTokenType.String ? (string)v : v.ToString(Newtonsoft.Json.Formatting.None));
                        at = m.Index + m.Length;
                    }
                    return b.Append(s, at, s.Length - at).ToString();
                }
                if (t.Type == JTokenType.Object)
                {
                    JObject o = new JObject();
                    foreach (KeyValuePair<string, JToken> kv in (JObject)t) o[kv.Key] = Resolve(kv.Value);
                    return o;
                }
                if (t.Type == JTokenType.Array)
                {
                    JArray arr = new JArray();
                    foreach (JToken item in (JArray)t)
                    {
                        Match spread = item.Type == JTokenType.String ? Spread.Match((string)item) : null;
                        if (spread == null || !spread.Success) { arr.Add(Resolve(item)); continue; }
                        JToken v = Lookup(spread.Groups[1].Value);
                        JArray items = v as JArray;
                        // Loud, because a spread of a single string is exactly the case where guessing
                        // "they meant one element" would quietly build the wrong argument list.
                        if (items == null)
                            throw new InvalidOperationException("${..." + spread.Groups[1].Value +
                                "} spreads an array, but that value is a " + v.Type);
                        foreach (JToken el in items) arr.Add(el.DeepClone());
                    }
                    return arr;
                }
                return t;
            }

            /// <summary>Resolve, or null if anything is missing. Only for guards, never for args.</summary>
            private JToken Try(JToken t)
            {
                try { return Resolve(t); }
                catch (Exception) { return null; }
            }

            private JToken Lookup(string path)
            {
                JToken v = vars.SelectToken(path, false);
                if (v == null || v.Type == JTokenType.Null)
                    throw new InvalidOperationException("${" + path + "} is not set (known: " +
                        string.Join(", ", Names()) + ")");
                return v;
            }

            private string[] Names()
            {
                List<string> n = new List<string>();
                foreach (KeyValuePair<string, JToken> kv in vars) { if (n.Count == 20) { n.Add("..."); break; } n.Add(kv.Key); }
                return n.ToArray();
            }
        }
    }
}
