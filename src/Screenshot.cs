using System;
using System.Collections;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Morgott.PPBridge
{
    /// <summary>
    /// The <c>screenshot</c> verb: the framebuffer as a PNG on disk, so a visual claim can be proved
    /// instead of argued through reflection.
    ///
    /// It is a CROSS-FRAME verb by necessity. The backbuffer is only complete at end of frame, so the
    /// capture runs in a coroutine behind <c>WaitForEndOfFrame</c> and the verb returns an
    /// <see cref="IPending"/> that the Runner ticks (PPBridgeMain.cs:588-599, 609-622) until the file
    /// exists - the response is never sent before the bytes are written. ScreenCapture.CaptureScreenshot(path)
    /// is deliberately NOT used: it is asynchronous with no completion signal, so the client would be
    /// handed a path to a file that is not there yet.
    ///
    /// This file names Unity types, so it is not compiled into the offline self-check; Protocol reaches
    /// it through the <see cref="Protocol.CaptureRun"/> delegate the game half installs, exactly like
    /// every other game-side hook.
    /// </summary>
    internal static class Screenshot
    {
        /// <summary>A capture that has not landed within this many ms is reported as a timeout rather
        /// than parked forever - the pump has only MaxPending slots.</summary>
        private const int TimeoutMs = 10000;

        private static int seq;
        private static MonoBehaviour host;

        /// <summary>Installed as <see cref="Protocol.CaptureRun"/>. Returns an IPending, or an error DTO.</summary>
        internal static object Run(JObject a)
        {
            string path;
            try { path = Resolve(a); }
            catch (Exception ex) { return Protocol.Fail(ex.Message); }

            // A frozen clock under D3D12 never reaches end of frame: the process stops responding and
            // only Stop-Process ends it (observed 2026-09-03, PID 20232, same map/seed fine on D3D11).
            // Refused here rather than parked in the pump, because a hung game looks like a bridge bug.
            if (Time.timeScale == 0f &&
                SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Direct3D12 &&
                !(a != null && a["force"] != null && (bool)a["force"]))
            {
                return Protocol.Fail("refusing to capture: Time.timeScale is 0 under D3D12, which wedges " +
                    "the process at WaitForEndOfFrame. Use timeScale 0.0001 (0.0002 game seconds per real " +
                    "second - frozen for any practical purpose), or pass \"force\":true to try anyway.");
            }

            MonoBehaviour go = Host();
            if (go == null) return Protocol.Fail("no coroutine host - the mod is shutting down");

            Capture c = new Capture(path);
            go.StartCoroutine(c.Shoot());
            return c;
        }

        /// <summary>Beside the bridge's own files (ModDir - the job file and the arm marker live there),
        /// or wherever the caller asked. An explicit path must be absolute: a relative one would land in
        /// the game's working directory, which is not where the caller thinks it is.</summary>
        private static string Resolve(JObject a)
        {
            JToken t = a == null ? null : a["path"];
            if (t != null && t.Type != JTokenType.Null)
            {
                string given = (string)t;
                if (string.IsNullOrEmpty(given) || !Path.IsPathRooted(given))
                    throw new Exception("screenshot's \"path\" must be an absolute path");
                return given;
            }
            return Path.Combine(PPBridgeMain.ModDir ?? ".",
                                "ppcli-shot-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + (++seq) + ".png");
        }

        private static MonoBehaviour Host()
        {
            if (host != null) return host;
            GameObject go = new GameObject("ppcli-screenshot");
            UnityEngine.Object.DontDestroyOnLoad(go);
            host = go.AddComponent<Runner>();
            return host;
        }

        private sealed class Runner : MonoBehaviour { }

        /// <summary>The pixels of a render texture as PNG. RenderTexture.active is restored: leaving it
        /// pointed at someone else's target corrupts the next thing the engine draws.</summary>
        private static byte[] Grab(RenderTexture rt)
        {
            RenderTexture prev = RenderTexture.active;
            Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                return ImageConversion.EncodeToPNG(tex);
            }
            finally
            {
                RenderTexture.active = prev;
                UnityEngine.Object.Destroy(tex);
            }
        }

        /// <summary>Written by the coroutine and read by Tick, both on the main thread - no locking.</summary>
        private sealed class Capture : IPending
        {
            private readonly string path;
            private readonly DateTime deadline = DateTime.UtcNow.AddMilliseconds(TimeoutMs);
            private object result;
            private bool abandoned;

            internal Capture(string path) { this.path = path; }

            public object Tick(bool cancelled)
            {
                if (result != null) return result;
                if (cancelled)
                {
                    abandoned = true;
                    return new { ok = false, code = "cancelled", error = "cancelled before the frame ended" };
                }
                if (DateTime.UtcNow > deadline)
                {
                    abandoned = true;
                    return new { ok = false, code = "timeout", error = "no end-of-frame within " + TimeoutMs + " ms" };
                }
                return null;
            }

            internal IEnumerator Shoot()
            {
                // The one thing that makes this correct: everything below runs AFTER the frame is
                // fully rendered. A capture taken anywhere else in Update order reads a half-drawn
                // backbuffer. No try/catch may wrap a yield, so the guarded part starts after it.
                yield return new WaitForEndOfFrame();

                // Tick may already have reported cancellation or a timeout for this job. Writing the PNG
                // now would create a file for a request the client was told had failed.
                if (abandoned) yield break;

                Texture2D tex = null;
                try
                {
                    tex = ScreenCapture.CaptureScreenshotAsTexture();
                    byte[] png = ImageConversion.EncodeToPNG(tex);
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllBytes(path, png);

                    // An upscaler (DLSS/FSR) renders the scene into a camera targetTexture and blits it
                    // to the backbuffer at PRESENT time - after this end-of-frame capture. The PNG above
                    // then holds IMGUI over a blank field, which reads as "the game rendered nothing".
                    // The scene really is somewhere: in that render texture. It goes beside the shot,
                    // and the reply says so instead of leaving the caller to guess.
                    RenderTexture rt = Camera.main == null ? null : Camera.main.targetTexture;
                    if (rt == null)
                    {
                        result = new { ok = true, path, width = tex.width, height = tex.height, bytes = png.Length };
                    }
                    else
                    {
                        string scenePath = Path.ChangeExtension(path, null) + ".scene.png";
                        byte[] scene = Grab(rt);
                        File.WriteAllBytes(scenePath, scene);
                        result = new
                        {
                            ok = true, path, width = tex.width, height = tex.height, bytes = png.Length,
                            scenePath, sceneWidth = rt.width, sceneHeight = rt.height, sceneBytes = scene.Length,
                            note = "Camera.main renders into targetTexture '" + rt.name + "', so the frame " +
                                   "above carries UI over a blank scene - the 3D scene is in scenePath, at the " +
                                   "camera's own (pre-upscale) resolution."
                        };
                    }
                }
                catch (Exception ex) { result = Protocol.Fail(ex.GetType().Name + ": " + ex.Message); }
                // The texture is created outside the GC's Unity-object lifetime; leaking one per shot
                // is a leak of a full-screen RGBA buffer.
                finally { if (tex != null) UnityEngine.Object.Destroy(tex); }
            }
        }
    }
}
