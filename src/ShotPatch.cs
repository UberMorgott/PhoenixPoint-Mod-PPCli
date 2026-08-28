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
                h.Patch(target, new HarmonyMethod(typeof(ShotPatch), nameof(Observe)));
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
                             damage, armor, targets);
            }
            // A patch that can throw inside the game loop is a defect. This one cannot: a measurement
            // tap must never be able to take the shot it is measuring down with it.
            catch (Exception) { }
        }
    }
}
