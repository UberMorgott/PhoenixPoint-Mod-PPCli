using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Morgott.PPBridge
{
    /// <summary>
    /// The pipe transport's pure half: framing, the discovery-file record, token comparison and
    /// stale-endpoint detection. Touches no game, no Unity and no pipe type, which is the only
    /// reason the offline self-check can run it (PipeSecurity and WindowsIdentity live one file
    /// over in PipeServer.cs and are deliberately kept out of here).
    /// </summary>
    internal static class Wire
    {
        /// <summary>Hard cap on one frame. A hostile length prefix costs a refusal, not an OOM.</summary>
        internal const int MaxFrameBytes = 256 * 1024;

        /// <summary>No BOM: the length prefix already delimits the payload and a BOM would corrupt it.</summary>
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        /// <summary>
        /// 4-byte little-endian length + UTF-8 JSON. An oversized payload is replaced by an error
        /// frame rather than thrown: this runs on the pipe thread, where an exception is a dropped
        /// connection with no explanation to the client.
        /// </summary>
        internal static byte[] Encode(string json)
        {
            byte[] body = Utf8.GetBytes(json ?? "");
            if (body.Length == 0 || body.Length > MaxFrameBytes)
                body = Utf8.GetBytes("{\"status\":\"error\",\"error\":\"response of " + body.Length +
                                     " bytes exceeds the " + MaxFrameBytes + " byte frame limit\"}");
            byte[] frame = new byte[4 + body.Length];
            frame[0] = (byte)body.Length;
            frame[1] = (byte)(body.Length >> 8);
            frame[2] = (byte)(body.Length >> 16);
            frame[3] = (byte)(body.Length >> 24);
            Buffer.BlockCopy(body, 0, frame, 4, body.Length);
            return frame;
        }

        /// <summary>
        /// Reads one frame. Never throws for a malformed or hostile frame - it returns null and a
        /// named reason, so the caller can answer with a structured error instead of dying.
        /// </summary>
        internal static string Read(Stream s, out string error)
        {
            error = null;
            byte[] head = new byte[4];
            if (!ReadExact(s, head, 4)) { error = "no complete length prefix"; return null; }
            int len = head[0] | (head[1] << 8) | (head[2] << 16) | (head[3] << 24);
            if (len <= 0 || len > MaxFrameBytes)
            {
                error = "frame length " + len + " outside 1.." + MaxFrameBytes;
                return null;
            }
            byte[] body = new byte[len];
            if (!ReadExact(s, body, len)) { error = "frame truncated, wanted " + len + " bytes"; return null; }
            return Utf8.GetString(body);
        }

        private static bool ReadExact(Stream s, byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n;
                try { n = s.Read(buf, got, count - got); }
                catch (IOException) { return false; }
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        /// <summary>
        /// Equality that does not leak HOW MUCH of the token matched: there is no early exit on the
        /// first differing character, so a caller cannot recover the token by timing it out one
        /// character at a time. The token is the only thing standing between a local process and a
        /// reflection-equivalent endpoint, so it is not compared with ==.
        ///
        /// What it is NOT, said plainly: the loop runs max(expected, given) times, so the cost still
        /// depends on the lengths - both of which the caller already knows, one being its own input
        /// and the other a fixed 32 hex characters. Indexing BOTH strings modulo their own length is
        /// what keeps a short guess from ending the loop early.
        /// </summary>
        internal static bool TokenOk(string expected, string given)
        {
            if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(given)) return false;
            int n = expected.Length > given.Length ? expected.Length : given.Length;
            int diff = expected.Length ^ given.Length;
            for (int i = 0; i < n; i++) diff |= expected[i % expected.Length] ^ given[i % given.Length];
            return diff == 0;
        }

        /// <summary>
        /// A discovery file survives a crash, so every reader must decide whether it still describes
        /// a live game. <paramref name="alive"/> is injected so the self-check can answer that
        /// question without spawning processes.
        /// </summary>
        internal static bool IsStale(string endpointJson, Func<int, bool> alive, out string reason)
        {
            int pid;
            if (!TryPid(endpointJson, out pid)) { reason = "no usable pid field"; return true; }
            if (!alive(pid)) { reason = "pid " + pid + " is gone"; return true; }
            reason = null;
            return false;
        }

        /// <summary>
        /// Deliberately not a JSON parse: this runs over files written by an unknown past version and
        /// only ever needs one integer out of them.
        /// </summary>
        internal static bool TryPid(string endpointJson, out int pid)
        {
            pid = 0;
            if (endpointJson == null) return false;
            int i = endpointJson.IndexOf("\"pid\"", StringComparison.Ordinal);
            if (i < 0) return false;
            i = endpointJson.IndexOf(':', i);
            if (i < 0) return false;
            int start = ++i;
            while (start < endpointJson.Length && endpointJson[start] == ' ') start++;
            int end = start;
            while (end < endpointJson.Length && endpointJson[end] >= '0' && endpointJson[end] <= '9') end++;
            return end > start && int.TryParse(endpointJson.Substring(start, end - start), out pid) && pid > 0;
        }

        internal static bool PidAlive(int pid)
        {
            try { using (Process.GetProcessById(pid)) return true; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
    }
}
