using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Base.Core;
using Base.Defs;
using Base.Levels;
using Base.Serialization;
using Base.Utils;
using Base.Utils.GameConsole;
using PhoenixPoint.Common.Game;
using PhoenixPoint.Common.Saves;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Modding;
using PhoenixPoint.Tactical.Levels;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Morgott.PPBridge
{
    /// <summary>
    /// PPCLI's in-game half: a dev-only endpoint that executes jobs an agent wrote to a file and
    /// prints each result to the Unity log as one <c>PPCLI|&lt;reqid&gt;|&lt;json&gt;</c> line.
    ///
    /// Dev-only by construction and never shipped: with no <c>ppcli-jobs.json</c> beside the DLL
    /// nothing is armed and this mod does nothing but log its own build stamp. It carries no Harmony
    /// patch and no dependency on any other mod.
    /// </summary>
    public class PPBridgeMain : ModMain
    {
        internal const string JobFile = "ppcli-jobs.json";
        /// <summary>
        /// The opt-in marker. A token holder gets reflection-equivalent access to the game process,
        /// so the endpoint does not arm just because the mod is enabled: it arms when this file
        /// exists beside the DLL, and deleting it disarms without touching the mod list.
        /// </summary>
        internal const string ArmFile = "ppcli-enabled";
        private static ModLogger log;
        private static PipeServer pipe;

        /// <summary>
        /// The mod's own folder. ModEntry.Directory is authoritative: the loader uses
        /// Assembly.Load(byte[]), so Assembly.Location is empty and there is nothing else to read it
        /// off (same reason ContentTool takes it from here).
        /// </summary>
        internal static string ModDir { get; private set; }

        public override void OnModEnabled()
        {
            log = Logger;
            ModDir = Instance?.Entry?.Directory;
            Protocol.BuildStamp = BuildStamp();
            Protocol.StateProbe = State;
            Protocol.ConsoleRun = RunConsole;
            Protocol.VarRun = RunVar;
            Protocol.RootsProbe = Roots;
            Protocol.DefByGuid = guid => { DefRepository r = GameUtl.GameComponent<DefRepository>(); return r == null ? null : r.GetDef(guid); };
            Protocol.AllDefs = () =>
            {
                DefRepository r = GameUtl.GameComponent<DefRepository>();
                return r == null || r.DefRepositoryDef == null ? new BaseDef[0] : (System.Collections.IEnumerable)r.DefRepositoryDef.AllDefs;
            };
            // Unity's overloaded == is the ONLY thing that knows a destroyed object from a live one,
            // and it is a game-half concept: `(UnityEngine.Object)x == null` is false for a plain
            // managed object and true for a destroyed one, which is exactly the test a handle needs.
            Protocol.UnityAlive = o => !(o is UnityEngine.Object) || (UnityEngine.Object)o != null;
            Protocol.SnapshotStart = StartSnapshot;
            Protocol.SaveExists = SaveExists;
            // The `observe` verb's game half. Installing the delegate does NOT install the patch -
            // that happens on `observe {"action":"start"}` and is undone on stop, so a session that
            // never measures a shot carries no Harmony patch at all.
            Shots.Arm = ShotPatch.Arm;
            // Every handle taken in the old scene is a destroyed object once it unloads. Bumping the
            // epoch turns each of them into a named refusal instead of a crash inside a later call.
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            // Same shape as ContentTool's init line, and for the same reason: the client refuses to
            // believe any result below it until this stamp matches the DLL it deployed.
            log?.LogInfo("PPBridge " + Assembly.GetExecutingAssembly().GetName().Version +
                         " | build=" + Protocol.BuildStamp + " | protocol=" + Protocol.Version);

            // OPT-IN, and it gates BOTH entrances - the pipe and the job file - because they reach
            // the same dispatcher. Enabled-but-not-armed is the resting state: the mod logs the line
            // above and nothing else in the process is reachable from outside it.
            if (!File.Exists(Path.Combine(ModDir ?? ".", ArmFile)))
            {
                log?.LogInfo("PPBridge: OFF - no '" + ArmFile + "' beside the DLL. Create that file to arm the endpoint.");
                return;
            }

            try { Runner.Arm(ModGO, Path.Combine(ModDir ?? ".", JobFile), Say); }
            catch (Exception ex) { log?.LogError("PPBridge arm THREW " + ex); }

            try
            {
                pipe = new PipeServer(Runner.Enqueue, Say);
                pipe.Start(InstallRoot());
            }
            catch (Exception ex) { log?.LogError("PPBridge pipe THREW " + ex); }
        }

        public override void OnModDisabled()
        {
            StopPipe();
            // Before anything else: a Harmony patch that outlives the mod that installed it points
            // at a method in an assembly this DLL is about to stop owning.
            Shots.Shutdown();
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            Reflect.NewEpoch();          // nothing may keep a strong lease on a game object past this
            Protocol.StateProbe = null;
            Protocol.ConsoleRun = null;
            Protocol.VarRun = null;
            Protocol.RootsProbe = null;
            Protocol.DefByGuid = null;
            Protocol.AllDefs = null;
            Protocol.UnityAlive = null;
            Protocol.SnapshotStart = null;
            Protocol.SaveExists = null;
            log = null;
        }

        private static void OnSceneUnloaded(Scene scene) { Reflect.NewEpoch(); }

        internal static void StopPipe()
        {
            PipeServer p = pipe;
            pipe = null;
            if (p != null) p.Stop();
        }

        /// <summary>
        /// The install this session belongs to, for the discovery file and the pipe name. ModDir is
        /// &lt;install&gt;\Mods\PPBridge, so the install is two levels up; anything unexpected falls
        /// back to ModDir itself, which is still unique per install.
        /// </summary>
        private static string InstallRoot()
        {
            try
            {
                DirectoryInfo mods = Directory.GetParent(ModDir ?? ".");
                return mods != null && mods.Parent != null ? mods.Parent.FullName : ModDir;
            }
            catch (Exception) { return ModDir; }
        }

        internal static void Say(string msg) { log?.LogInfo(msg); }

        /// <summary>
        /// Which DLL this session actually loaded - the first 8 hex of its SHA-1. AssemblyVersion is
        /// a constant and Assembly.Location is empty, so neither can tell a fresh deploy from a stale
        /// one, and a stale DLL reports green (ContentTool paid for this lesson three runs in a row).
        /// </summary>
        private static string BuildStamp()
        {
            try
            {
                string dll = Path.Combine(ModDir ?? "", "PPBridge.dll");
                if (!File.Exists(dll)) return "no-dll";
                using (SHA1 sha = SHA1.Create())
                {
                    byte[] h = sha.ComputeHash(File.ReadAllBytes(dll));
                    StringBuilder b = new StringBuilder(8);
                    for (int i = 0; i < 4; i++) b.Append(h[i].ToString("x2"));
                    return b.ToString();
                }
            }
            catch (Exception) { return "unreadable"; }
        }

        // ------------------------------------------------------------------ verbs (main thread only)

        /// <summary>
        /// Phase is read off the level the game itself is holding: GameUtl.CurrentLevel() plus the
        /// controller component that level carries (the exact test CreatureGate.Current uses,
        /// CreatureGate.cs:542-546). A level that is not Playing is still loading or unloading.
        /// </summary>
        private static object State()
        {
            Level lvl = GameUtl.CurrentLevel();
            string phase;
            if (lvl == null) phase = "menu";
            else if (!lvl.IsPlaying) phase = "loading";
            else if (lvl.GetComponent<GeoLevelController>() != null) phase = "geoscape";
            else if (lvl.GetComponent<TacticalLevelController>() != null) phase = "tactical";
            else phase = "menu";
            return new
            {
                ok = true,
                phase,
                scene = SceneManager.GetActiveScene().name,
                level = lvl == null ? null : lvl.name,
                levelState = lvl == null ? "none" : lvl.CurrentState.ToString()
            };
        }

        /// <summary>
        /// The named entrances into the live game, every one of them re-read from the game HERE and
        /// now - a cached root would keep answering with the controller of a mission that ended.
        /// Each accessor is the game's own:
        ///   GameUtl.Game()/GameComponent&lt;T&gt;/CurrentLevel  - GameUtl.cs:38,51,101
        ///   level.GetComponent&lt;GeoLevelController/TacticalLevelController&gt; - the component IS on
        ///     the level, it is not a cast (TacticalDeployZone.cs:378)
        ///   tac.Map / tac.View                              - TacticalLevelController.cs:155,165
        ///   geo.ViewerFaction                               - GeoLevelController.cs:209
        ///   tac.View.ViewerFaction                          - TacticalView.cs:189, set from
        ///     Factions.FirstOrDefault(f =&gt; f.IsControlledByPlayer) at TacticalLevelController.cs:634
        ///     and read exactly this way by the win/lose commands at :1099
        ///   tac.View.SelectedActor                          - TacticalView.cs:148
        /// A null root is reported as null rather than omitted: "wrong phase" and "no such alias"
        /// are different answers.
        /// </summary>
        private static Dictionary<string, object> Roots()
        {
            Level lvl = GameUtl.CurrentLevel();
            GeoLevelController geo = lvl == null ? null : lvl.GetComponent<GeoLevelController>();
            TacticalLevelController tac = lvl == null ? null : lvl.GetComponent<TacticalLevelController>();
            return new Dictionary<string, object>
            {
                { "game", GameUtl.Game() },
                { "phoenix", GameUtl.GameComponent<PhoenixGame>() },
                { "defs", GameUtl.GameComponent<DefRepository>() },
                { "level", lvl },
                { "geo", geo },
                { "tac", tac },
                { "map", tac == null ? null : tac.Map },
                { "view", tac == null ? null : (object)tac.View },
                { "faction", geo != null ? geo.ViewerFaction : (tac == null || tac.View == null ? null : (object)tac.View.ViewerFaction) },
                { "selected", tac == null || tac.View == null ? null : tac.View.SelectedActor }
            };
        }

        /// <summary>
        /// Executes an ALREADY-REGISTERED console command. ConsoleCommandAttribute.LoadCommands scans
        /// only its own assembly (ConsoleCommandAttribute.cs:44), so a mod cannot be found that way -
        /// but Invoke is public static (:105) and takes already-tokenized arguments, so this is the
        /// legal route. The IConsole we hand it is ours, which is the only way the command's output
        /// comes back instead of going to the game's console window.
        /// </summary>
        private static object RunConsole(string command, string[] args)
        {
            if (!ConsoleCommandAttribute.HasCommand(command))
                return Protocol.Fail("unknown command '" + command + "'");
            Capture cap = new Capture();
            try { ConsoleCommandAttribute.Invoke(command, args ?? new string[0], cap); }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                return new { ok = false, output = cap.Lines.ToArray(), error = inner.GetType().Name + ": " + inner.Message };
            }
            catch (Exception ex)
            {
                return new { ok = false, output = cap.Lines.ToArray(), error = ex.GetType().Name + ": " + ex.Message };
            }
            return new { ok = true, output = cap.Lines.ToArray(), truncated = cap.Truncated };
        }

        /// <summary>
        /// The console's other half. ConsoleVariableAttribute registers static FIELDS and PROPERTIES
        /// (ConsoleVariableAttribute.cs:7,36-92) and ConsoleCommandAttribute.Invoke never sees any of
        /// them, so `console god_mode` is an unknown command no matter how it is spelled - this verb
        /// is the only way in. Three guards, all of them the game's own sharp edges:
        ///   - HasVariable first (:94), because SetValue/GetValue THROW ApplicationException on an
        ///     unknown name rather than refusing.
        ///   - readonly is reported, not swallowed: SetValue throws for one (:110-113).
        ///   - GetValue does .ToString() on the raw value (:134-136), so an unset string variable
        ///     (jira_login, override_menu) NREs. That is a game bug and it gets a named refusal
        ///     instead of taking the drain loop with it.
        /// </summary>
        private static object RunVar(string name, string value)
        {
            if (!ConsoleVariableAttribute.HasVariable(name))
                return Protocol.Fail("unknown variable '" + name + "' (run console `vars` for the list)");
            bool wrote = false;
            try
            {
                if (value != null) { ConsoleVariableAttribute.SetValue(name, value); wrote = true; }
                return new { ok = true, name, value = ConsoleVariableAttribute.GetValue(name) };
            }
            catch (NullReferenceException)
            {
                return Protocol.Fail("variable '" + name + "' is unset, and the game's GetValue calls " +
                                     "ToString() on it" + (wrote ? " (the write DID happen)" : ""));
            }
            catch (Exception ex) { return Protocol.Fail(ex.GetType().Name + ": " + ex.Message); }
        }

        /// <summary>
        /// The point of the console verb: collect what the command wrote instead of losing it.
        /// IConsole.Write/WriteLine take a FORMAT string plus args (IConsole.cs:9-11), so user data
        /// arrives as "{0}" + the value and must go through string.Format - and a command that hands
        /// us a literal brace must not take the run down with a FormatException.
        /// </summary>
        private sealed class Capture : IConsole
        {
            internal readonly List<string> Lines = new List<string>();
            internal bool Truncated;

            public object Context { get { return null; } }

            /// <summary>Refused, not implemented: re-entering the dispatcher from inside a command
            /// is a recursion this endpoint has no budget for.</summary>
            public bool ExecuteCommandLine(string line) { return false; }

            public void Write(string format, params object[] args) { Add(format, args); }
            public void WriteLine(string format, params object[] args) { Add(format, args); }

            private void Add(string format, object[] args)
            {
                string s;
                try { s = args != null && args.Length > 0 ? string.Format(format, args) : format; }
                catch (FormatException) { s = format; }
                foreach (string part in (s ?? "").Split('\n'))
                {
                    if (Lines.Count >= Protocol.MaxOutputLines) { Truncated = true; return; }
                    Lines.Add(Protocol.Clip(part));
                }
            }
        }

        // ------------------------------------------------------------------ snapshot (P3)

        /// <summary>
        /// EnsureUnique consults the already-loaded save dictionary and hands back a DIFFERENT name
        /// when the given one is taken (PhoenixSaveManager.cs:154-168). That makes it the save
        /// manager's only public, synchronous existence test - everything else is a coroutine.
        /// </summary>
        private static bool SaveExists(string name)
        {
            PhoenixGame game = GameUtl.GameComponent<PhoenixGame>();
            PhoenixSaveManager sm = game == null ? null : game.SaveManager;
            return sm != null && !string.Equals(sm.EnsureUnique(name), name, StringComparison.Ordinal);
        }

        /// <summary>
        /// Starts a save and returns the poll the plan engine ticks. The whole save is the GAME'S
        /// own - `save_game` does exactly this (SerializationCommands.cs:19-25) - and the completion
        /// signal is the IUpdateable that Timing.Start hands back (Timing.cs:246-254,
        /// IUpdateable.Stopped/Exception). Note the spec's wording: it is Timing.Start that returns
        /// the IUpdateable; SaveWithName itself returns an IEnumerator&lt;NextUpdate&gt;.
        /// </summary>
        private static Func<object> StartSnapshot(string name)
        {
            PhoenixGame game = GameUtl.GameComponent<PhoenixGame>();
            PhoenixSaveManager sm = game == null ? null : game.SaveManager;
            if (sm == null) return () => Protocol.Fail("no PhoenixSaveManager - the game is not up yet");

            // SaveWithName does NOTHING AT ALL when the level has no ISavegameProvider
            // (PhoenixSaveManager.cs:551): the coroutine finishes instantly and no file is written.
            // Refusing here is the difference between "no snapshot" and a snapshot that silently is not one.
            Level lvl = GameUtl.CurrentLevel();
            if (lvl == null || lvl.GetComponent<ISavegameProvider>() == null)
                return () => Protocol.Fail("this level has no ISavegameProvider, so the save would write " +
                                           "nothing - snapshot needs a geoscape or tactical level, not the menu");

            IUpdateable run;
            try { run = game.Timing.Start(SnapshotCrt(sm, name)); }
            catch (Exception ex) { return () => Protocol.Fail("could not start the save: " + ex.Message); }

            return () =>
            {
                if (!run.Stopped) return null;
                if (run.Exception != null)
                    return Protocol.Fail("the save threw " + run.Exception.GetType().Name + ": " + run.Exception.Message);
                // Do not take Stopped as proof: assert the file is actually there under the asked-for name.
                return SaveExists(name)
                    ? (object)new { ok = true, name }
                    : Protocol.Fail("the save coroutine finished but no savegame called '" + name + "' exists");
            };
        }

        /// <summary>
        /// Delete-then-save, and the delete is the point: EnsureUnique would otherwise rename a
        /// second snapshot of the same name to "&lt;name&gt;_1", and `restore &lt;name&gt;` would then
        /// come back to the FIRST, stale one. Both halves are the game's own coroutines.
        /// </summary>
        private static IEnumerator<NextUpdate> SnapshotCrt(PhoenixSaveManager sm, string name)
        {
            ByRef<SavegameMetaData> existing = new ByRef<SavegameMetaData>();
            yield return Timing.Current.Call(sm.GetSaveGame(name, existing));
            PPSavegameMetaData old = existing.Value as PPSavegameMetaData;
            if (old != null) yield return Timing.Current.Call(sm.DeleteSaveGame(old));
            yield return Timing.Current.Call(sm.SaveWithName(name, null));
        }

        // ------------------------------------------------------------------ main-thread dispatch

        /// <summary>
        /// Drains jobs on the MAIN THREAD and nowhere else. In P0 the only producer is the job file,
        /// read once at scene-ready; the queue is a ConcurrentQueue and the drain is budgeted anyway
        /// because P1 adds a pipe thread as a second producer and nothing game-side may ever run off
        /// the main thread.
        /// </summary>
        private sealed class Runner : MonoBehaviour
        {
            private const int SettleFrames = 30;        // ~half a second between readiness checks
            private const float ReadyTimeoutSeconds = 120f;
            private const long BudgetMs = 8;            // per frame, so a fat batch cannot stall render
            private const int MaxQueued = 64;           // bounded: a flooding client gets refused, not obeyed
            /// <summary>Cross-frame jobs alive at once. Each one costs a Tick every single frame.</summary>
            private const int MaxPending = 16;

            private static Runner instance;

            /// <summary>A job that has started but not finished: a wait, a snapshot, or a plan.</summary>
            private sealed class Parked
            {
                internal Job Job;
                internal IPending Work;
            }

            private readonly List<Parked> parked = new List<Parked>();
            private readonly ConcurrentQueue<Job> queue = new ConcurrentQueue<Job>();
            private List<Job> fileJobs;                 // held back until a level is up, unlike pipe jobs
            private Action<string> say;
            private int frame, done, depth;
            private float started;
            private bool ready, saidDone;

            /// <summary>
            /// The pipe thread's ONLY way in. It enqueues and returns; the verb itself never runs off
            /// the main thread. False means the queue is full, which is a refusal, not a wait.
            /// </summary>
            internal static bool Enqueue(Job job)
            {
                Runner r = instance;
                if (r == null) { job.Complete(Protocol.Fail("no runner: the mod is shutting down")); return true; }
                if (Interlocked.Increment(ref r.depth) > MaxQueued) { Interlocked.Decrement(ref r.depth); return false; }
                r.queue.Enqueue(job);
                return true;
            }

            /// <summary>
            /// Always armed now, with or without a job file: the pipe needs a main-thread pump for the
            /// whole session, not just for the length of a batch.
            /// </summary>
            internal static void Arm(GameObject modGo, string path, Action<string> say)
            {
                GameObject go = modGo;
                if (go == null) { go = new GameObject("ppcli"); DontDestroyOnLoad(go); }
                Runner r = go.AddComponent<Runner>();
                r.say = say;
                r.started = Time.realtimeSinceStartup;
                instance = r;

                if (!File.Exists(path)) return;         // the normal case: no batch armed, pipe only

                string error;
                List<Job> jobs = Protocol.Parse(File.ReadAllText(path), out error);
                if (error != null) say("PPCLI|PARSE|" + error);
                r.fileJobs = jobs;
                say("PPCLI: armed with " + jobs.Count + " job(s) from " + path);
            }

            private void Update()
            {
                if (!ready && ++frame % SettleFrames == 0)
                {
                    Level lvl = GameUtl.CurrentLevel();
                    bool timedOut = Time.realtimeSinceStartup - started > ReadyTimeoutSeconds;
                    if (lvl != null && lvl.IsPlaying || timedOut)
                    {
                        if (timedOut) say("PPCLI: no level reached Playing within " + ReadyTimeoutSeconds +
                                          "s - running anyway, verbs that need one will say so");
                        ready = true;
                        // The batch waits for a level; a pipe client asking `state` at the main menu
                        // must be answered immediately, so only these jobs were ever held back. They
                        // bypass the depth cap - the file's own MaxJobs cap already bounded them, and
                        // silently dropping a job the user wrote is worse than a deep queue.
                        if (fileJobs != null)
                            foreach (Job j in fileJobs) { Interlocked.Increment(ref depth); queue.Enqueue(j); }
                    }
                }

                Stopwatch sw = Stopwatch.StartNew();
                TickParked(sw);
                Job job;
                while (sw.ElapsedMilliseconds < BudgetMs && queue.TryDequeue(out job))
                {
                    Interlocked.Decrement(ref depth);
                    Run(job);
                }

                // Only the batch entrance has a DONE, and only once: the runner now outlives it.
                // A parked job counts as unfinished - DONE before a plan's own cleanup ran would be
                // the same lie the queue-drain check used to tell about coroutines.
                if (fileJobs != null && ready && !saidDone && queue.IsEmpty && parked.Count == 0)
                {
                    // ponytail: DONE fires as soon as the queue drains, so a verb that outlives its
                    // own call would be cut short. P0/P1 verbs are all synchronous; when an async
                    // verb lands it needs a pending-counter gate like ContentTool's Dev.AsyncGate
                    // (AutoRun.cs:15-17,89-98) before DONE may print.
                    say("PPCLI|DONE|" + done);
                    saidDone = true;
                }
            }

            /// <summary>
            /// Executes one job and hands back a finished DTO. The result is fully projected HERE, on
            /// the main thread: serializing a live game object on the pipe thread is a bug even when
            /// it appears to work, so the waiter is only ever given a string.
            /// </summary>
            private void Run(Job job)
            {
                object result;
                try
                {
                    result = Protocol.Refusal(job) ?? Protocol.Dispatch(job);
                }
                // One failing job must not take the rest of the list, or the pump, with it.
                catch (Exception ex) { result = Protocol.Fail("THREW " + ex.Message); }

                // P3: wait / snapshot / plan do not finish in the frame they start. They are parked
                // and ticked, never spun on - blocking here would freeze the game for the whole
                // duration of the very thing the verb exists to wait for.
                IPending work = result as IPending;
                if (work != null)
                {
                    if (parked.Count >= MaxPending)
                    {
                        Finish(job, Protocol.Fail("too many jobs already waiting (" + MaxPending +
                                                  ") - cancel one or wait for it to finish"));
                        return;
                    }
                    parked.Add(new Parked { Job = job, Work = work });
                    return;
                }
                Finish(job, result);
            }

            /// <summary>
            /// One Tick per parked job per frame, inside the same frame budget as everything else.
            /// This is also where `cancel` becomes real: the flag the pipe thread set is handed to
            /// the job HERE, every frame, and a cross-frame job can act on it - which a synchronous
            /// verb structurally cannot.
            /// </summary>
            private void TickParked(Stopwatch sw)
            {
                for (int i = parked.Count - 1; i >= 0; i--)
                {
                    if (sw.ElapsedMilliseconds >= BudgetMs) return;
                    Parked p = parked[i];
                    object result;
                    try { result = p.Work.Tick(p.Job.Cancelled); }
                    catch (Exception ex) { result = Protocol.Fail("a waiting job THREW " + ex.Message); }
                    if (result == null) continue;
                    parked.RemoveAt(i);
                    Finish(p.Job, result);
                }
            }

            private void Finish(Job job, object result)
            {
                done++;
                if (job.Done == null) { say(Protocol.Marker(job.Id, result)); return; }
                // The waiter must wake whatever happened above, so this projection is in a finally.
                try { result = Protocol.Reproject(result); }
                finally { job.Complete(result); }
            }

            private void OnApplicationQuit() { StopPipe(); }
            private void OnDestroy() { if (instance == this) instance = null; }
        }
    }
}
