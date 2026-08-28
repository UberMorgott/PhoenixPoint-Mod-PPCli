using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Morgott.PPBridge
{
    /// <summary>
    /// The <c>observe</c> verb: a bounded ring of terminal projectile impacts plus the dispersion
    /// arithmetic over them. Like Reflect.cs and Plan.cs this names NO Unity and NO game type - the
    /// one thing it cannot do without the game (installing the patch that feeds it) arrives as the
    /// <see cref="Arm"/> delegate the game half sets - so the ring, the caps and the statistics are
    /// all exercisable in the offline self-check with no game running.
    ///
    /// OFF by default and off again on <c>stop</c>: nothing is patched until somebody asks, so a
    /// session that never measures pays nothing. Why a tap is needed at all rather than a plan:
    /// <c>call</c> has only new/get/set/invoke (Reflect.cs:306-314) so nothing can subscribe to an
    /// event, the ability report containers are flushed either side of a volley
    /// (TacticalLevelController.cs:1803,1855-1866), and Map.ProjectilesInFlight is gone by the time
    /// anyone could read it (ProjectileLogic.cs:305,359).
    /// </summary>
    internal static class Shots
    {
        /// <summary>Bounded, dropping the OLDEST. A bench fires tens of shots, not thousands, and an
        /// unbounded buffer written from inside a game-loop patch is a leak, not a measurement.</summary>
        internal const int Capacity = 512;

        /// <summary>Rows one response may carry. The ring is bigger than this on purpose: the stats
        /// are computed over EVERYTHING recorded, and only the listing is trimmed.</summary>
        internal const int MaxRows = 200;

        internal struct Impact
        {
            internal float X, Y, Z;
            /// <summary>The actor that stopped it, or null - terrain, a prop, or nothing at all.
            /// That null is the whole reason this exists: a terrain miss is what measures spread.</summary>
            internal string Actor;
            /// <summary>The collider that stopped it: a body part on an actor, a surface otherwise.
            /// NULL means nothing was hit at all, and then <see cref="X"/>/<see cref="Y"/>/<see cref="Z"/>
            /// are NOT an impact point - see <see cref="HasGeometry"/>.</summary>
            internal string Part;
            internal float Damage, Armor;
            internal int Targets;
            /// <summary>Instance id of the actor that stopped it, 0 for none. Compared against
            /// <see cref="TargetId"/>, because a NAME is not an identity - two Crabmen on the map
            /// carry the same GameObject name and a bystander would then read as the target.</summary>
            internal int ActorId;
            /// <summary>Health damage this projectile put into <see cref="TargetId"/> ALONE, summed
            /// by the patch from the game's own per-target accumulation. <see cref="Damage"/> is the
            /// projectile's total across every actor it touched, which is a different number the
            /// moment a shot clips a bystander.</summary>
            internal float TargetDamage;

            /// <summary>
            /// MEASURED, and it is the difference between a number and a lie. When a projectile hits
            /// NOTHING - past everything, out to max range - ProjectileLogic still calls
            /// OnTrajectoryEnd, but with the static SDummyHit (ProjectileLogic.cs:25,107,217): no
            /// collider and a point of exactly (0,0,0). Reporting that as an impact AT the world
            /// origin is a fabricated coordinate, and it wrecks the dispersion figures - a live run
            /// with two such rows had its group spread read 4.2 m instead of 0.2 m. So a
            /// geometry-less row is still COUNTED (it is a miss, and a real one), and is left out of
            /// the dispersion arithmetic.
            /// </summary>
            internal bool HasGeometry { get { return Part != null; } }
        }

        /// <summary>Installed by the game half: true installs the patch, false removes it. Returns
        /// null on success and a reason otherwise, so <c>start</c> can refuse loudly instead of
        /// recording nothing.</summary>
        internal static Func<bool, string> Arm;

        private static readonly Impact[] ring = new Impact[Capacity];
        private static int head, stored, dropped, total, mark;

        /// <summary>Checked first by the patch, so an unobserved session costs one bool read per
        /// projectile - and the patch is not even installed then.</summary>
        internal static bool On;

        /// <summary>The actor the volley is AIMED at, by Unity instance id; 0 means "not told".
        /// Set by <c>observe {"action":"start","target":N}</c> and read by the patch. Without it a
        /// bystander - or the shooter's own body - counts as a hit and inflates every figure the
        /// bench reports for the weapon.</summary>
        internal static int TargetId;

        /// <summary>Projectiles that were left stuck in flight by a throw inside the game's own
        /// OnTrajectoryEnd and that ShotPatch.Unwedge released. NOT a statistic: any non-zero value
        /// means SOMETHING threw during damage resolution, and every figure taken after it was
        /// measured on a game that had to be repaired mid-volley. WHICH patch threw is not knowable
        /// from here - the stack is cut at the Harmony wrapper, which names the patched method and
        /// never the patch - so this counts the repair and attributes nothing. Reported, never hidden.</summary>
        internal static int Recovered;

        /// <summary>Impacts since <c>start</c>. A live, POSITIVE, single-read predicate, which is
        /// exactly the shape <c>wait</c> can use.</summary>
        internal static int Recorded { get { return total; } }

        /// <summary>Impacts the ring OVERWROTE - recorded, then pushed out by a later one. NOT a
        /// statistic either: the hit rate, the damage totals and the dispersion are all computed over
        /// what the ring still HOLDS, so any non-zero value here means those figures were taken over a
        /// truncated window and are not the figures the caller asked for. A live, single-read predicate
        /// so a plan can assert on it, exactly like <see cref="Recovered"/>.</summary>
        internal static int Dropped { get { return dropped; } }

        /// <summary>Impacts since the last <c>mark</c>. This is how a plan paces a volley: mark,
        /// fire, wait for this to go non-zero. It measures the thing itself - the projectile
        /// landing - rather than an animation or a queue that only correlates with it.</summary>
        internal static int Landed { get { return total - mark; } }

        /// <summary>Called from the patch, on the main thread. Never throws and never allocates
        /// beyond the two strings the caller already has.</summary>
        internal static void Record(float x, float y, float z, string actor, string part,
                                    float damage, float armor, int targets,
                                    int actorId = 0, float targetDamage = 0f)
        {
            if (!On) return;
            ring[head] = new Impact
            {
                X = x, Y = y, Z = z,
                Actor = actor, Part = part,
                Damage = damage, Armor = armor, Targets = targets,
                ActorId = actorId, TargetDamage = targetDamage
            };
            head = (head + 1) % Capacity;
            if (stored < Capacity) stored++; else dropped++;
            total++;
        }

        /// <summary>Returns null for a verb this file does not own, so Protocol falls through.</summary>
        internal static object Dispatch(string verb, JObject a)
        {
            if (verb != "observe") return null;
            string action = a == null ? null : (string)a["action"];
            switch (action)
            {
                case "start": return Start(a);
                case "stop": return Stop();
                case "mark": mark = total; return new { ok = true, mark, observing = On };
                case "read": return Read(a);
                case "status": return new { ok = true, observing = On, recorded = total, landed = Landed, stored, dropped, recovered = Recovered };
                default: return Bad("observe needs {\"action\":\"start|stop|mark|read|status\"}");
            }
        }

        /// <summary>Releases the patch and forgets everything. Called from OnModDisabled: a patch
        /// that outlives the mod that installed it is a crash waiting for the next reload.</summary>
        internal static void Shutdown()
        {
            On = false;
            try { if (Arm != null) Arm(false); }
            catch (Exception) { }
            Arm = null;
            TargetId = 0;
            head = stored = dropped = total = mark = Recovered = 0;
            Array.Clear(ring, 0, ring.Length);
        }

        private static object Bad(string message) { return new { ok = false, code = "observe", error = message }; }

        private static object Start(JObject a)
        {
            if (Arm == null) return Bad("no shot observer installed - this is the offline half, or the mod is shutting down");
            JToken t = a == null ? null : a["target"];
            // A target that was GIVEN must be usable. Silently ignoring a malformed one would report
            // targetHits:0 for a volley that hit nothing but the target, which is the worst answer of
            // the three (right shape, wrong number, no complaint).
            if (t != null && t.Type != JTokenType.Null && t.Type != JTokenType.Integer)
                return Bad("observe start's \"target\" must be an actor's integer instanceId");
            string error = Arm(true);
            if (error != null) return Bad("could not install the observer: " + error);
            head = stored = dropped = total = mark = Recovered = 0;
            Array.Clear(ring, 0, ring.Length);
            TargetId = t == null || t.Type == JTokenType.Null ? 0 : (int)(long)t;
            On = true;
            return new { ok = true, observing = true, capacity = Capacity, target = TargetId };
        }

        private static object Stop()
        {
            // On goes false FIRST: the patch may still be mid-flight when the unpatch lands, and a
            // record into a ring nobody will read is harmless while a half-armed patch is not.
            On = false;
            string error = Arm == null ? null : Arm(false);
            return new { ok = true, observing = false, recorded = total, stored, dropped, recovered = Recovered, unpatchError = error };
        }

        private static object Read(JObject a)
        {
            List<Impact> items = Snapshot();
            float[] aim = Aim(a);
            int hits = 0, targetHits = 0;
            float damage = 0f, armor = 0f, onActors = 0f, onTarget = 0f;
            // Only rows with a real impact point reach the dispersion arithmetic - see Impact.HasGeometry.
            List<Impact> placed = new List<Impact>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                // A HIT is "an actor stopped it", never "damage was dealt": a fully armoured hit does
                // zero health damage and is still a hit.
                if (items[i].Actor != null) { hits++; onActors += items[i].Damage; }
                // ...and a hit ON THE TARGET is a stricter thing again: identity, not "some actor".
                // Both figures stay at zero when no target was named, rather than quietly falling
                // back to the all-actor totals under a name that promises otherwise.
                if (TargetId != 0)
                {
                    if (items[i].ActorId == TargetId) targetHits++;
                    onTarget += items[i].TargetDamage;
                }
                damage += items[i].Damage;
                armor += items[i].Armor;
                if (items[i].HasGeometry) placed.Add(items[i]);
            }

            List<object> rows = new List<object>();
            // The OLDEST rows are dropped from the listing, not the newest: when a run overflows the
            // response cap it is the last shots that are being asked about.
            for (int i = Math.Max(0, items.Count - MaxRows); i < items.Count; i++)
            {
                Impact m = items[i];
                rows.Add(new
                {
                    x = m.HasGeometry ? (object)m.X : null,
                    y = m.HasGeometry ? (object)m.Y : null,
                    z = m.HasGeometry ? (object)m.Z : null,
                    actor = m.Actor, part = m.Part,
                    onTarget = TargetId != 0 && m.ActorId == TargetId,
                    damage = m.Damage, damageOnTarget = m.TargetDamage,
                    armor = m.Armor, targets = m.Targets
                });
            }

            return new
            {
                ok = true,
                observing = On,
                recorded = total,
                stored = items.Count,
                dropped,
                // Non-zero means the game had to be unwedged mid-volley - see Shots.Recovered. It
                // rides in the SAME answer as the numbers it invalidates, so a caller cannot read
                // the dispersion without also seeing that something threw while it was measured.
                recovered = Recovered,
                // THREE different questions, and mixing them is how a bench lies. `hits` is "an actor
                // stopped it" - ANY actor, the shooter and every bystander included. `targetHits` is
                // "the actor this volley was aimed at stopped it", and it is the one that measures the
                // weapon against the target. It reads 0 when no target was given to `start`.
                hits,
                misses = items.Count - hits,
                hitRate = items.Count == 0 ? 0.0 : Math.Round((double)hits / items.Count, 4),
                target = TargetId == 0 ? (object)null : TargetId,
                targetHits,
                targetMisses = items.Count - targetHits,
                targetHitRate = items.Count == 0 ? 0.0 : Math.Round((double)targetHits / items.Count, 4),
                // The authoritative damage figure. An actor's Health.Max does NOT stay where it was
                // put - the game recomputes a stat from its base and modifications - so an HP
                // before/after read around a raised target HP is not a measurement of the weapon.
                // This is the sum the game itself accumulated per projectile.
                damageTotal = Math.Round(damage, 4),
                // Split out, because a projectile that misses often still damages what it DID hit -
                // a live run put 33 into a dead tree, and a single "damage" figure that mixed that
                // in with the target's would read as the weapon doing more damage than it does.
                damageOnActors = Math.Round(onActors, 4),
                // ...and stricter still: what the TARGET actually took, summed from the game's own
                // per-target accumulation rather than from the projectile's total. A shot that clips
                // a bystander adds to damageOnActors and not to this.
                damageOnTarget = Math.Round(onTarget, 4),
                armorTotal = Math.Round(armor, 4),
                // Rows that hit nothing at all and therefore have no impact point.
                noGeometry = items.Count - placed.Count,
                aim = aim == null ? null : new { x = aim[0], y = aim[1], z = aim[2] },
                dispersion = Stats(placed, aim),
                returned = rows.Count,
                impacts = rows.ToArray()
            };
        }

        /// <summary>The ring in arrival order, oldest first.</summary>
        private static List<Impact> Snapshot()
        {
            List<Impact> got = new List<Impact>(stored);
            int start = stored < Capacity ? 0 : head;
            for (int i = 0; i < stored; i++) got.Add(ring[(start + i) % Capacity]);
            return got;
        }

        /// <summary>The aim point, or null. Refused rather than half-read: a 2-element "aim" is a
        /// caller mistake and silently dropping it would report dispersion about nothing.</summary>
        private static float[] Aim(JObject a)
        {
            JArray arr = a == null ? null : a["aim"] as JArray;
            if (arr == null || arr.Count != 3) return null;
            float[] p = new float[3];
            for (int i = 0; i < 3; i++)
            {
                if (arr[i].Type != JTokenType.Integer && arr[i].Type != JTokenType.Float) return null;
                p[i] = (float)(double)arr[i];
            }
            return p;
        }

        /// <summary>
        /// Dispersion, about the impacts' own centroid and - when one was given - about the aim
        /// point. Both are reported because they answer different questions: spread about the
        /// centroid is the weapon's cone, spread about the aim point also carries any systematic
        /// bias between where the shot was aimed and where the group actually sits.
        /// </summary>
        internal static Dictionary<string, object> Stats(IList<Impact> items, float[] aim)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            int n = items == null ? 0 : items.Count;
            d["n"] = n;
            if (n == 0) return d;

            double sx = 0, sy = 0, sz = 0;
            for (int i = 0; i < n; i++) { sx += items[i].X; sy += items[i].Y; sz += items[i].Z; }
            double cx = sx / n, cy = sy / n, cz = sz / n;
            d["centroid"] = new Dictionary<string, object> { { "x", Round(cx) }, { "y", Round(cy) }, { "z", Round(cz) } };
            d["aboutCentroid"] = Spread(items, cx, cy, cz);
            if (aim != null) d["aboutAim"] = Spread(items, aim[0], aim[1], aim[2]);
            return d;
        }

        /// <summary>
        /// mean / sigma / max of the radial distance from one point. Sigma is the POPULATION
        /// standard deviation of those distances, from the sums rather than in two passes - and the
        /// variance is clamped at zero, because E[d^2] - E[d]^2 goes slightly NEGATIVE in floating
        /// point when every distance is identical, which is exactly what a spread:0 control run
        /// produces. Without the clamp the control run - the one that proves the numbers are real -
        /// would report NaN.
        /// </summary>
        private static Dictionary<string, object> Spread(IList<Impact> items, double x, double y, double z)
        {
            int n = items.Count;
            double sum = 0, sumSq = 0, max = 0;
            for (int i = 0; i < n; i++)
            {
                double dx = items[i].X - x, dy = items[i].Y - y, dz = items[i].Z - z;
                double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                sum += dist;
                sumSq += dist * dist;
                if (dist > max) max = dist;
            }
            double mean = sum / n;
            double variance = sumSq / n - mean * mean;
            return new Dictionary<string, object>
            {
                { "mean", Round(mean) },
                { "sigma", Round(Math.Sqrt(variance < 0 ? 0 : variance)) },
                { "max", Round(max) }
            };
        }

        private static double Round(double v) { return Math.Round(v, 4); }
    }
}
