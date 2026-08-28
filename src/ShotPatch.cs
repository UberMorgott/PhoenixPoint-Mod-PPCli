using System;
using System.Reflection;
using Base.Entities;
using Base.Levels;
using HarmonyLib;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Entities.Weapons;
using UnityEngine;

namespace Morgott.PPBridge
{
    /// <summary>
    /// The game half of <c>observe</c>, and the only new C# the weapon bench genuinely needed.
    ///
    /// THE SEAM: <c>ProjectileLogic.OnTrajectoryEnd(CastHit lastHit, Vector3 lastDir)</c>
    /// (ProjectileLogic.cs:333). Every projectile passes through it exactly once, whatever happened
    /// to it - all three flight paths end there (:125, :160/:186, :272) - and <c>lastHit</c> carries
    /// the TERMINAL impact point even when nothing alive was hit. That last part is the point:
    ///   - the impact EVENT path is guarded by <c>_damageAccum != null</c> (:335), so a projectile
    ///     that damaged nobody may raise nothing at all;
    ///   - the Unity log line in TacticalActorBase.ApplyDamageInternal (:850-852) covers actor
    ///     damage only, and is skipped entirely under god_mode (:846);
    /// so neither can see a terrain miss - which is the observation that measures spread.
    ///
    /// PREFIX, and that is not a style choice. OnTrajectoryEnd's last act is
    /// <c>_damageAccum.ApplyAddedDamage()</c> (:355), which ends in <c>_targetsData.Clear()</c>
    /// (DamageAccumulation.cs:644,701). A postfix would read an accumulator the game had already
    /// emptied and would report every single shot as a miss.
    /// </summary>
    internal static class ShotPatch
    {
        private const string Id = "com.morgott.PPBridge.shots";
        private static Harmony harmony;

        /// <summary>
        /// DamageAccumulation's own per-target list. The public GetAllTargetDamageResults() (:709)
        /// yields the DamageResults with the TARGET stripped off, so it can answer "how much damage
        /// did this projectile do" and cannot answer "how much did it do TO THIS ACTOR" - which is
        /// the figure a weapon bench is actually after. Null if the field ever moves, and then the
        /// per-target damage is simply reported as zero rather than guessed.
        /// </summary>
        private static readonly FieldInfo TargetsData = AccessTools.Field(typeof(DamageAccumulation), "_targetsData");

        /// <summary>
        /// Installs or removes the patch. Null on success, a reason otherwise - the verb refuses
        /// loudly rather than starting an observer that would record nothing. Unpatching rather
        /// than leaving a gated patch in place is deliberate: "costs nothing when not observing"
        /// should be true of the game loop, not just of this file.
        /// </summary>
        internal static string Arm(bool on)
        {
            try
            {
                if (!on)
                {
                    if (harmony == null) return null;
                    harmony.UnpatchAll(Id);
                    harmony = null;
                    return null;
                }
                if (harmony != null) return null;
                MethodInfo target = AccessTools.Method(typeof(ProjectileLogic), "OnTrajectoryEnd");
                if (target == null)
                    return "ProjectileLogic.OnTrajectoryEnd was not found - the game changed under the observer";
                Harmony h = new Harmony(Id);
                h.Patch(target, new HarmonyMethod(typeof(ShotPatch), nameof(Observe)),
                        finalizer: new HarmonyMethod(typeof(ShotPatch), nameof(Unwedge)));
                harmony = h;
                return null;
            }
            catch (Exception ex)
            {
                harmony = null;
                return ex.GetType().Name + ": " + ex.Message;
            }
        }

        /// <summary>
        /// One terminal impact. <c>____damageAccum</c> is Harmony's private-field injection for
        /// ProjectileLogic's <c>_damageAccum</c> (three underscores, then the field's own name).
        /// </summary>
        private static void Observe(ProjectileLogic __instance, CastHit lastHit, DamageAccumulation ____damageAccum)
        {
            if (!Shots.On) return;
            try
            {
                // The damage PREDICTOR runs this identical code every time the UI hovers a target -
                // a simulation is simply a ProjectileLogic with no Projectile (:41). Recording those
                // would bury the handful of real shots under hundreds of phantom ones.
                if (__instance == null || __instance.IsSimulation) return;

                Collider collider = lastHit.Collider;
                float damage = 0f, armor = 0f;
                int targets = 0;
                if (____damageAccum != null)
                {
                    foreach (DamageResult r in ____damageAccum.GetAllTargetDamageResults())
                    {
                        damage += r.HealthDamage;
                        armor += r.ArmorDamage;
                        targets++;
                    }
                }
                // The game's own way of asking "whose is this collider" (ProjectileLogic.cs:131).
                ActorComponent actor = collider == null ? null : collider.GetComponentInParent<ActorComponent>();
                Shots.Record(lastHit.Point.x, lastHit.Point.y, lastHit.Point.z,
                             actor == null ? null : actor.name,
                             collider == null ? null : collider.name,
                             damage, armor, targets,
                             actor == null ? 0 : actor.GetInstanceID(),
                             OnTarget(____damageAccum));
            }
            // A patch that can throw inside the game loop is a defect. This one cannot: a measurement
            // tap must never be able to take the shot it is measuring down with it.
            catch (Exception) { }
        }

        /// <summary>
        /// A REAL WEDGE, AND NOT THE VOLLEY CEILING - this comment claimed it was, and the claim was
        /// wrong. OnTrajectoryEnd removes the projectile from <c>TacticalActor.Map.ProjectilesInFlight</c>
        /// and clears <c>Projectile.IsActive</c> at its very LAST two statements
        /// (ProjectileLogic.cs:359-360) - AFTER <c>_damageAccum.ApplyAddedDamage()</c> (:355), which
        /// runs the whole damage chain and with it every mod's Harmony patch on
        /// <c>TacticalActor.Die</c>. One throw in there and the projectile is never removed and never
        /// deactivated, so <c>TacticalMap.HasActiveProjectiles</c> (TacticalMap.cs:133) is stuck TRUE
        /// for the rest of the mission - and the game's own firing coroutine waits on exactly that,
        /// twice (TacticalLevelController.cs:1759, :1797). That is a genuine way to strand a mission,
        /// it was observed live (twice in one 20-activation run), and this releases it.
        ///
        /// What it is NOT is the ~6-shot ceiling. MEASURED, and it falsifies the first draft of this
        /// comment: with this finalizer installed the bench still died on pass 6 reporting
        /// <c>recovered:0</c> - nothing had wedged, and the ceiling was still there. The ceiling was
        /// the settle predicate treating one projectile of a six-round burst as one shot; see
        /// weapon-test.json's own note. Two independent faults, one of which happened to be found
        /// while chasing the other.
        ///
        /// So: run the two statements the game could not reach, and let the exception continue. The
        /// stuck projectile is unwedged whoever threw and whyever - this finalizer names no mod and
        /// tests for nothing. Swallowing is NOT on the table: <c>__exception</c> is returned unchanged,
        /// so the throw still reaches the log and whoever wrote the bad patch still learns about it.
        /// Counted, surfaced as <c>recovered</c>, and FATAL to the bench that saw it: a silent repair
        /// of somebody else's crash is how a bench starts measuring a broken game.
        /// </summary>
        private static Exception Unwedge(ProjectileLogic __instance, Exception __exception)
        {
            if (__exception == null || __instance == null) return __exception;
            try
            {
                Projectile p = __instance.Projectile;
                // IsActive is the game's OWN "still in flight" flag and the one HasActiveProjectiles
                // reads, so it is also the honest test for "the cleanup did not run".
                if (p != null && p.IsActive)
                {
                    __instance.TacticalActor.Map.ProjectilesInFlight.Remove(p);
                    p.OnTrajectoryEnd();
                    Shots.Recovered++;
                    Debug.LogError("PPBridge: a projectile was left in flight by a throw inside "
                                   + "OnTrajectoryEnd and has been released; the throw follows.");
                }
            }
            catch (Exception) { }
            return __exception;
        }

        /// <summary>
        /// Health damage this projectile put into <see cref="Shots.TargetId"/> alone. Matched on
        /// GetActor() rather than on the receiver itself: a shot resolves against a BODY PART or a
        /// carried item (both are IDamageReceivers), and comparing those to the actor would score
        /// every real hit as zero. 0 when no target was named, when the field moved, or when this
        /// projectile did not reach the target at all.
        /// </summary>
        private static float OnTarget(DamageAccumulation accum)
        {
            if (Shots.TargetId == 0 || accum == null || TargetsData == null) return 0f;
            System.Collections.IEnumerable rows = TargetsData.GetValue(accum) as System.Collections.IEnumerable;
            if (rows == null) return 0f;
            float sum = 0f;
            foreach (DamageAccumulation.TargetData td in rows)
            {
                if (td == null || td.Target == null) continue;
                TacticalActorBase hit = td.Target.GetActor();
                if (hit != null && hit.GetInstanceID() == Shots.TargetId) sum += td.DamageResult.HealthDamage;
            }
            return sum;
        }
    }
}
