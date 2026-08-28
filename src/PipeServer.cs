using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Morgott.PPBridge
{
    /// <summary>
    /// The pipe entrance to the SAME dispatcher the job file uses. This thread only reads, parses,
    /// validates and enqueues; every verb runs on the main thread and comes back as a finished DTO
    /// (see PPBridgeMain.Runner). Nothing here ever touches a game object.
    ///
    /// One request per connection: the client connects, writes one frame, reads one frame, closes.
    /// ponytail: connections are served one at a time - a second client waits. Two agents driving one
    /// install at once is not a case this endpoint has; if it ever is, spin a server instance per
    /// connection with maxNumberOfServerInstances raised.
    /// </summary>
    internal sealed class PipeServer
    {
        /// <summary>How long the pipe thread waits for the main thread before it answers "accepted".</summary>
        private const int InlineWaitMs = 3000;
        /// <summary>After this, the main thread refuses to even start the job.</summary>
        private const int JobDeadlineMs = 120000;
        /// <summary>Finished jobs nobody collected are dropped after this.</summary>
        private const int ResultTtlMs = 300000;
        private const int MaxTrackedJobs = 64;
        /// <summary>
        /// MUST be > 0. On .NET Framework 0 means "system default" and is perfectly legal, which is
        /// why every offline check passed it; the game's Mono instead throws
        /// ArgumentOutOfRangeException("bufferSize must be greater than 0") straight out of the
        /// PipeStream base ctor, with or without a DACL. Small on purpose: Mono sizes an internal
        /// FileStream from this too, and frames are capped separately by Wire.MaxFrameBytes, so a
        /// big response does not need a big pipe buffer.
        /// </summary>
        private const int BufferBytes = 1024;

        /// <summary>ERROR_PIPE_CONNECTED - the client won the race to connect between CreateNamedPipe
        /// and ConnectNamedPipe. Benign, and this Mono reports it as a failure.</summary>
        private const int PipeAlreadyConnected = 535;

        private readonly Func<Job, bool> enqueue;
        private readonly Action<string> say;
        private readonly Dictionary<string, Job> jobs = new Dictionary<string, Job>();
        private readonly object gate = new object();

        private string pipeName, token, endpointFile;
        private int nextJobId;
        private volatile bool stopped;
        private bool announced;
        private NamedPipeServerStream current;
        private Thread thread;

        internal PipeServer(Func<Job, bool> enqueue, Action<string> say)
        {
            this.enqueue = enqueue;
            this.say = say;
        }

        // ------------------------------------------------------------------ lifecycle

        internal void Start(string installPath)
        {
            // Never a fixed name: two installs run side by side, and a predictable name would let
            // them answer each other's clients.
            pipeName = "ppcli-" + Hash(Environment.UserDomainName + "\\" + Environment.UserName) +
                       "-" + Hash((installPath ?? "?").ToLowerInvariant()) +
                       "-" + System.Diagnostics.Process.GetCurrentProcess().Id;
            token = RandomToken();

            SweepStaleEndpoints();
            endpointFile = WriteEndpoint(installPath);

            // The self-test is NOT started here: the pipe name does not exist until CreateNamedPipe
            // returns inside the loop, and a self-test that fires early reports a failure that is not
            // real. The loop launches it once, immediately before it blocks on the first accept.
            thread = new Thread(Loop) { IsBackground = true, Name = "ppcli-pipe" };
            thread.Start();
        }

        /// <summary>
        /// The accept thread is parked inside a native blocking wait, and only a connection returns
        /// from it - so wake it with one, then join. Disposing the stream from another thread does not
        /// reliably break that wait; it is kept only for a connection blocked mid-read.
        /// </summary>
        internal void Stop()
        {
            if (stopped) return;
            stopped = true;
            try { using (NamedPipeClientStream c = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut)) c.Connect(1000); }
            catch (Exception) { }
            try { Thread t = thread; if (t != null) t.Join(2000); } catch (Exception) { }
            try { NamedPipeServerStream s = current; if (s != null) s.Dispose(); } catch (Exception) { }
            try { if (endpointFile != null) File.Delete(endpointFile); } catch (Exception) { }
            endpointFile = null;
        }

        // ------------------------------------------------------------------ discovery file

        private static string EndpointDir()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "ppcli\\endpoints");
        }

        private string WriteEndpoint(string installPath)
        {
            string dir = EndpointDir();
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, System.Diagnostics.Process.GetCurrentProcess().Id + ".json");
            File.WriteAllText(path, Protocol.Compact(new
            {
                pipe = pipeName,
                pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                install = installPath,
                protocol = Protocol.Version,
                build = Protocol.BuildStamp,
                token
            }), new UTF8Encoding(false));
            return path;
        }

        /// <summary>A crash leaves a file behind. Whoever starts next owns the cleanup.</summary>
        private void SweepStaleEndpoints()
        {
            try
            {
                string dir = EndpointDir();
                if (!Directory.Exists(dir)) return;
                foreach (string f in Directory.GetFiles(dir, "*.json"))
                {
                    string reason;
                    if (!Wire.IsStale(File.ReadAllText(f), Wire.PidAlive, out reason)) continue;
                    File.Delete(f);
                    say("PPCLI: removed stale endpoint " + Path.GetFileName(f) + " (" + reason + ")");
                }
            }
            catch (Exception ex) { say("PPCLI: endpoint sweep failed: " + ex.Message); }
        }

        // ------------------------------------------------------------------ accept loop

        /// <summary>
        /// ponytail: no per-user ACE - Windows' DEFAULT pipe DACL, deliberately. A hand-built one-ACE
        /// DACL denied this mod's own client, because the SID it was built from is not the one the
        /// client authenticates as and this runtime will not hand over the real one
        /// (WindowsIdentity.User and .Owner both throw; NTAccount.Translate resolves well-known
        /// accounts only). The default grants creator-owner - this same user - full control, so the
        /// client can read AND write, while another local user gets read-only and cannot write a
        /// request. The SESSION TOKEN is the trust boundary; see Handle().
        /// Upgrade path, only if isolation from same-user processes ever matters: OpenProcessToken +
        /// GetTokenInformation(TokenUser) for a real SID, then the 8-arg ctor.
        ///
        /// Every other argument is load-bearing too: a literal 1 for maxNumberOfServerInstances
        /// (this Mono forwards -1 as 0xFFFFFFFF and Windows answers 87), buffers > 0, and
        /// InOut/Byte/None because Mono's ConnectNamedPipe is synchronous and message mode
        /// (ReadMode / IsMessageComplete) is incomplete there.
        /// </summary>
        private NamedPipeServerStream Create()
        {
            return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                                             PipeOptions.None, BufferBytes, BufferBytes);
        }

        /// <summary>
        /// After ERROR_PIPE_CONNECTED the pipe genuinely IS connected - Mono threw instead of setting
        /// the flag, and every read then refuses with "Pipe is not connected". PipeStream.IsConnected
        /// has a protected setter and NamedPipeServerStream is sealed, so there is no subclass to do
        /// this cleanly. False = could not, and the caller rethrows rather than serving a stream the
        /// runtime still believes is idle.
        /// </summary>
        internal static bool MarkConnected(NamedPipeServerStream s)
        {
            MethodInfo setter = typeof(PipeStream).GetProperty("IsConnected")?.GetSetMethod(true);
            if (setter == null) return false;
            setter.Invoke(s, new object[] { true });
            return true;
        }

        private void Loop()
        {
            int failures = 0;
            while (!stopped)
            {
                NamedPipeServerStream s = null;
                try
                {
                    s = Create();
                    current = s;
                    // Both of these happen HERE and once: CreateNamedPipe has returned, so the name
                    // exists and the next statement is the wait. Said from Start() instead, the line
                    // lied through 111 failures and the self-test cried wolf against a name that did
                    // not exist yet. A self-test that can be wrong either way is worth nothing.
                    if (!announced)
                    {
                        announced = true;
                        say("PPCLI: pipe " + pipeName + " listening (default pipe DACL, session token required), endpoint " + endpointFile);
                        new Thread(SelfTest) { IsBackground = true, Name = "ppcli-selftest" }.Start();
                    }
                    // Not an error: a client that connected during the gap between CreateNamedPipe
                    // and ConnectNamedPipe leaves the pipe connected and this Mono throwing anyway.
                    // Every request the client sends back-to-back can land in that gap.
                    try { s.WaitForConnection(); }
                    catch (Win32Exception ex) when (ex.NativeErrorCode == PipeAlreadyConnected) { if (!MarkConnected(s)) throw; }
                    if (stopped) break;
                    Serve(s);
                    failures = 0;
                }
                catch (Exception ex)
                {
                    if (stopped) break;
                    // The first one is the diagnosis and it says FAILURE out loud, with the stack:
                    // this loop once repeated a bare one-line message 111 times under a log line
                    // claiming the pipe was listening, and nobody could see what was throwing.
                    if (failures++ == 0) say("PPCLI FAILURE: pipe accept loop threw, the endpoint is DEAD: " + ex);
                    else if (failures % 50 == 0) say("PPCLI FAILURE: pipe accept loop still failing (" + failures + "x): " + ex.Message);
                    // A failure that repeats instantly is a hot loop on a frame-budgeted game.
                    Thread.Sleep(1000);
                }
                finally
                {
                    current = null;
                    try { if (s != null) s.Dispose(); } catch (Exception) { }
                }
            }
        }

        /// <summary>
        /// Proves the pipe is REACHABLE, not merely constructed - the offline check cannot, because
        /// the gap that broke P1 exists only in the game's Mono. A deliberately wrong token keeps
        /// this to one suspect: it must come back refused, which means accept + framing + dispatch
        /// entry all work, and it never needs the main thread to be pumping yet.
        /// </summary>
        private void SelfTest()
        {
            try
            {
                using (NamedPipeClientStream c = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
                {
                    c.Connect(10000);
                    byte[] frame = Wire.Encode("{\"token\":\"self-test-wrong-token\",\"verb\":\"ping\"}");
                    c.Write(frame, 0, frame.Length);
                    c.Flush();
                    string error;
                    string reply = Wire.Read(c, out error);
                    if (reply != null && reply.Contains("token")) say("PPCLI: pipe self-test OK, " + pipeName + " accepts connections");
                    else say("PPCLI FAILURE: pipe self-test got an unexpected answer: " + (reply ?? error));
                }
            }
            catch (Exception ex) { say("PPCLI FAILURE: pipe self-test could not reach its own pipe: " + ex); }
        }

        private void Serve(NamedPipeServerStream s)
        {
            string error;
            string json = Wire.Read(s, out error);
            // A bad frame is answered, not thrown: the client otherwise sees a closed pipe and has
            // no idea whether it was rejected or the game died.
            if (json == null) { Reply(s, new { status = "error", error = error }); return; }
            Reply(s, Handle(json));
        }

        private void Reply(NamedPipeServerStream s, object payload)
        {
            try
            {
                byte[] frame = Wire.Encode(Protocol.Compact(payload));
                s.Write(frame, 0, frame.Length);
                s.Flush();
                s.WaitForPipeDrain();
            }
            catch (Exception) { /* client hung up mid-answer; the next connection is unaffected */ }
        }

        // ------------------------------------------------------------------ request handling

        private object Handle(string json)
        {
            JObject req;
            try { req = JObject.Parse(json); }
            catch (Exception ex) { return new { status = "error", error = "not a JSON object: " + ex.Message }; }

            // TRUST BOUNDARY. This endpoint is reflection-equivalent from P2 onward; the token check
            // is the first thing that happens after the bytes are readable and is never optional.
            if (!Wire.TokenOk(token, (string)req["token"]))
                return new { status = "error", error = "bad or missing session token" };

            string verb = (string)req["verb"];
            if (string.IsNullOrEmpty(verb)) return new { status = "error", error = "no verb" };
            string reqId = (string)req["id"] ?? "?";

            // status/cancel are job-table questions, not game questions - answering them here keeps
            // a poll from queueing work on the main thread.
            if (verb == "status") return Status((string)(req["args"] == null ? null : req["args"]["jobId"]));
            if (verb == "cancel") return Cancel((string)(req["args"] == null ? null : req["args"]["jobId"]));

            Job job = new Job
            {
                Id = reqId,
                Verb = verb,
                Args = req["args"] as JObject,
                Done = new ManualResetEventSlim(false),
                Deadline = DateTime.UtcNow.AddMilliseconds(JobDeadlineMs)
            };

            string jobId;
            lock (gate)
            {
                Prune();
                if (jobs.Count >= MaxTrackedJobs)
                    return new { status = "error", error = "job table full (" + MaxTrackedJobs + ")" };
                jobId = "j" + (++nextJobId);
                jobs[jobId] = job;
            }
            if (!enqueue(job))
            {
                lock (gate) jobs.Remove(jobId);
                return new { status = "error", error = "queue full, the main thread is behind" };
            }

            if (job.Done.Wait(InlineWaitMs))
            {
                lock (gate) jobs.Remove(jobId);
                return new { status = "done", id = reqId, jobId, result = job.Result };
            }
            return new { status = "accepted", id = reqId, jobId };
        }

        private object Status(string jobId)
        {
            Job job;
            lock (gate)
            {
                if (jobId == null || !jobs.TryGetValue(jobId, out job))
                    return new { status = "error", error = "no such job '" + jobId + "' (finished and collected, or expired)" };
                if (!job.Done.IsSet) return new { status = "running", jobId, id = job.Id };
                jobs.Remove(jobId);
            }
            return new { status = "done", id = job.Id, jobId, result = job.Result };
        }

        private object Cancel(string jobId)
        {
            lock (gate)
            {
                Job job;
                if (jobId == null || !jobs.TryGetValue(jobId, out job))
                    return new { status = "error", error = "no such job '" + jobId + "'" };
                if (job.Done.IsSet) return new { status = "done", jobId, id = job.Id, result = job.Result };
                // What this really does, honestly: it sets a flag the MAIN THREAD reads. A job that
                // has not started is refused outright (Protocol.Refusal). A cross-frame job - wait,
                // snapshot, plan - is handed the flag on its very next Tick and stops there, which
                // for a plan means running its `finally` block and then reporting code:"cancelled".
                // What it still cannot do is interrupt a SYNCHRONOUS verb already executing: a `call`
                // is one main-thread invocation and nothing can cut into it. That is the honest
                // boundary, and it is why the long-running verbs are the ticked ones.
                job.Cancelled = true;
                return new { status = "cancelling", jobId, id = job.Id };
            }
        }

        /// <summary>Caller holds the lock.</summary>
        private void Prune()
        {
            DateTime cut = DateTime.UtcNow.AddMilliseconds(-ResultTtlMs);
            List<string> dead = null;
            foreach (KeyValuePair<string, Job> kv in jobs)
            {
                if (!kv.Value.Done.IsSet || kv.Value.FinishedUtc > cut) continue;
                (dead ?? (dead = new List<string>())).Add(kv.Key);
            }
            if (dead != null) foreach (string k in dead) jobs.Remove(k);
        }

        // ------------------------------------------------------------------ identity

        private static string Hash(string s)
        {
            using (SHA1 sha = SHA1.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
                StringBuilder b = new StringBuilder(8);
                for (int i = 0; i < 4; i++) b.Append(h[i].ToString("x2"));
                return b.ToString();
            }
        }

        private static string RandomToken()
        {
            byte[] raw = new byte[16];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(raw);
            StringBuilder b = new StringBuilder(32);
            foreach (byte x in raw) b.Append(x.ToString("x2"));
            return b.ToString();
        }
    }
}
