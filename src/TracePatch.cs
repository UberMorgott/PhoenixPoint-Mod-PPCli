using System;
using System.Reflection;
using HarmonyLib;

namespace Morgott.PPBridge
{
    /// <summary>
    /// The game half of <c>trace</c>: one shared prefix/postfix pair that Harmony puts on whatever
    /// method a `trace {start}` named. Everything else (which method, refusals, caps, the ring, ends)
    /// is the pure half in src\Trace.cs. Patches exist only while a trace is live: Disarm removes
    /// ONLY this bridge's patches from that one method, never another mod's.
    ///
    /// No finalizer, on purpose: a Harmony finalizer wraps the original in try/catch and rethrows,
    /// and a rethrown exception can lose the frames PPCLI's dead-run detector reads mod names from.
    /// A call that throws therefore records its row (prefix) but no `r` (postfix never runs).
    /// </summary>
    internal static class TracePatch
    {
        private const string Id = "com.morgott.PPBridge.trace";
        private static Harmony harmony;

        internal static string Arm(TraceTap.Spec s)
        {
            try
            {
                if (harmony == null) harmony = new Harmony(Id);
                HarmonyMethod pre = new HarmonyMethod(typeof(TracePatch), nameof(Pre));
                HarmonyMethod post = s.WantsRet ? new HarmonyMethod(typeof(TracePatch), nameof(Post)) : null;
                harmony.Patch(s.Method, prefix: pre, postfix: post);
                return null;
            }
            catch (Exception ex)
            {
                // A half-applied patch must not outlive the refusal.
                try { harmony.Unpatch(s.Method, HarmonyPatchType.All, Id); } catch (Exception) { }
                Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                return inner.GetType().Name + ": " + inner.Message;
            }
        }

        internal static string Disarm(TraceTap.Spec s)
        {
            if (harmony == null) return null;
            try { harmony.Unpatch(s.Method, HarmonyPatchType.All, Id); return null; }
            catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
        }

        /// <summary>Bridge shutdown backstop: whatever the pure half could not remove.</summary>
        internal static void UnpatchAll()
        {
            try { if (harmony != null) harmony.UnpatchAll(Id); } catch (Exception) { }
        }

        private static void Pre(MethodBase __originalMethod, object __instance, object[] __args, out object __state)
        {
            __state = null;
            try { __state = TraceTap.Enter(__originalMethod, __instance, __args); }
            catch (Exception) { }
        }

        private static void Post(object __state, object __result)
        {
            try { TraceTap.Leave(__state, __result); }
            catch (Exception) { }
        }
    }
}
