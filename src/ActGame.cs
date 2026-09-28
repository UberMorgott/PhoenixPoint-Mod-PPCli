using System;
using System.Collections.Generic;
using Base.Core;
using Base.Levels;
using PhoenixPoint.Common.Entities;
using PhoenixPoint.Common.Levels.Params;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Entities.Abilities;
using PhoenixPoint.Tactical.Entities.Equipments;
using PhoenixPoint.Tactical.Levels;
using PhoenixPoint.Tactical.View;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Morgott.PPBridge
{
    /// <summary>
    /// The game half of <c>act</c> (pure half + contract: Act.cs). Every lever is the one the game's
    /// own UI pulls, so an action taken here is the action a player's click takes:
    ///   - activation: ability.Activate(target) then View.UpdateSquadMembersActionAndWillPoints(),
    ///     the body of TacticalViewState.ActivateAbility (TacticalViewState.cs:259-277). The UI's
    ///     UIStateWaiting push is NOT mirrored - the open view state is left alone.
    ///   - enabledness: GetDisabledState(IgnoreEquipmentNotSelected) (TacticalAbility.cs:372,
    ///     IgnoredAbilityDisabledStatesFilter.cs:10). Nothing is selected BEFORE the checks - a refusal
    ///     must change nothing; Activate selects the source weapon itself (TacticalAbility.cs:1087-1090).
    ///   - targets, per kind (a bare TacticalAbilityTarget(actor) aims at the feet and skips LoF):
    ///       actor -> the element of a FRESH GetTargets() (TacticalAbility.cs:565) whose Actor is it;
    ///                for Shoot that set is already TryGetShootTarget'ed (ShootAbility.cs:177-192)
    ///       move  -> nearest MoveAbilityTargetData of GetTargetsData() (MoveAbility.cs:163) .ToTarget(),
    ///                the UI's own path (UIStateCharacterSelected.cs:936-941)
    ///       shoot at pos -> ShootAbility.GetShootTarget (UIStateShoot.cs:1172-1182)
    ///       overwatch -> GetAbilityTargetCone(aim, DefaultValue deg) (UIStateOverwatchAbilitySelected.cs:117,300-307)
    ///       none  -> GetTargets().FirstOrDefault() like UIStateAbilitySelected.cs:629 / reload :631,
    ///                or null for TargetResult.None (InventoryAbility, UIStateCharacterSelected.cs:624)
    ///   - settle: the AI's own per-action block (TacticalFaction.cs:616-622): view not waiting for
    ///     active+queued abilities (TacticalView.cs:867), actor not executing (TacticalActorBase.cs:695),
    ///     not navigating/facing - PLUS !IsEnqueued/!IsExecuting and an AbilityExecutedEvent for THIS
    ///     ability (TacticalAbility.cs:1057), because a Regular shot is ENQUEUED (ShootAbility.cs:173)
    ///     and the view test alone can pass before it starts. An ability that plays no action at all
    ///     never raises the event: it is taken as synchronous after QuietFrames of complete idleness.
    ///   - turn: CurrentFaction == actor's faction, IsControlledByPlayer, IsPlayingTurn (the human
    ///     turn loop, TacticalFaction.cs:471-483). endTurn = RequestEndTurn (:382) under the same guard.
    /// </summary>
    internal static class ActGame
    {
        /// <summary>Frames of total idleness after which an ability that raised nothing is synchronous.</summary>
        private const int QuietFrames = 10;
        /// <summary>Settled frames required once the event arrived (the AI yields one frame first).</summary>
        private const int SettledFrames = 2;
        /// <summary>Busy-then-idle frames with NO event before the action counts as settled anyway.</summary>
        private const int NoEventFrames = 30;
        private const float MoveTolerance = 0.75f;
        private const float PosTolerance = 0.75f;

        internal static void Install()
        {
            ActTap.ListRun = List;
            ActTap.SquadRun = Squad;
            ActTap.UseStart = Use;
            ActTap.EndTurnRun = EndTurn;
        }

        // ------------------------------------------------------------------ scene / actor / turn

        private static object Scene(out TacticalLevelController tac)
        {
            tac = null;
            Level lvl = GameUtl.CurrentLevel();
            if (lvl == null || !lvl.IsPlaying) return ActTap.Bad("scene", "no playing level - act needs a tactical mission");
            tac = lvl.GetComponent<TacticalLevelController>();
            if (tac == null) return ActTap.Bad("scene", "the current level is not a tactical mission");
            if (tac.IsGameOver) return ActTap.Bad("scene", "the mission is over (IsGameOver)");
            return null;
        }

        private static JObject Turn(TacticalLevelController tac)
        {
            TacticalFaction f = tac.CurrentFaction;
            JObject t = new JObject { ["n"] = tac.TurnNumber, ["f"] = Faction(f) };
            t["mine"] = f != null && f.IsControlledByPlayer && f.IsPlayingTurn;
            return t;
        }

        private static string Faction(TacticalFaction f)
        {
            if (f == null) return null;
            try { return f.TacticalFactionDef.ShortName; } catch (Exception) { return f.TacticalFactionDef == null ? null : f.TacticalFactionDef.name; }
        }

        /// <summary>@alias / h:handle through the `call` grammar, else an exact GameObject name.</summary>
        private static T ResolveActor<T>(TacticalLevelController tac, string tok, string what, out object bad) where T : TacticalActorBase
        {
            bad = null;
            tok = tok ?? "@selected";
            object o = null;
            if (tok.StartsWith("@", StringComparison.Ordinal) || tok.StartsWith("h:", StringComparison.Ordinal))
            {
                string e;
                if (!Reflect.ResolveTargetToken(new JValue(tok), out o, out e)) { bad = ActTap.Bad(what, e); return null; }
            }
            else
            {
                int n = 0;
                foreach (T a in tac.Map.GetActors<T>())
                    if (a != null && string.Equals(a.name, tok, StringComparison.Ordinal)) { o = a; n++; }
                if (n > 1) { bad = ActTap.Bad("ambiguous", n + " actors are named '" + ActTap.Clip(tok) + "' - pass a handle"); return null; }
            }
            T ta = o as T;
            if (ta == null || (ta as UnityEngine.Object) == null)
                bad = ActTap.Bad(what, o == null ? "no actor '" + ActTap.Clip(tok) + "' (exact GameObject name, h:handle or @selected; @selected is null outside the player's turn)"
                                                 : "'" + ActTap.Clip(tok) + "' is a " + o.GetType().Name + ", not a " + typeof(T).Name);
            return ta;
        }

        private static float F(Func<float> f) { try { return f(); } catch (Exception) { return 0f; } }

        private static JObject ActorDto(TacticalActor a, bool faction)
        {
            Vector3 p = a.Pos;
            return ActTap.ActorRow(a.name, faction ? Faction(a.TacticalFaction) : null,
                F(() => a.CharacterStats.ActionPoints), F(() => a.CharacterStats.ActionPoints.Max),
                F(() => a.CharacterStats.WillPoints), F(() => a.Health), ActTap.Pos(p.x, p.y, p.z));
        }

        // ------------------------------------------------------------------ abilities

        /// <summary>ALL abilities; `i` is the index into THIS list whether or not passives are shown.</summary>
        private static List<TacticalAbility> Abilities(TacticalActor a)
        {
            List<TacticalAbility> l = new List<TacticalAbility>();
            foreach (TacticalAbility ab in a.GetAbilities<TacticalAbility>()) if (ab != null) l.Add(ab);
            return l;
        }

        private static bool Active(TacticalAbility ab) { try { return ab.ActiveAbility; } catch (Exception) { return false; } }

        private static string DefName(TacticalAbility ab) { try { return ab.TacticalAbilityDef.name; } catch (Exception) { return null; } }

        private static string SrcName(TacticalAbility ab)
        {
            object s;
            try { s = ab.Source; } catch (Exception) { return null; }
            if (s == null || ReferenceEquals(s, ab.TacticalActorBase)) return null;
            TacticalItem ti = s as TacticalItem;
            if (ti != null) { try { return ti.TacticalItemDef.name; } catch (Exception) { return ti.GetType().Name; } }
            UnityEngine.Object uo = s as UnityEngine.Object;
            if (uo != null) return uo.name;
            return s.GetType().Name;
        }

        private static TargetResult Result(TacticalAbility ab)
        {
            try { return ab.OriginTargetData.TargetResult; } catch (Exception) { return TargetResult.None; }
        }

        /// <summary>What `target` this ability takes. Anything but these is code:"targetKind" when a
        /// target is passed explicitly (an omitted target still gets the UI's first-target default).</summary>
        private static string Kind(TacticalAbility ab)
        {
            if (ab is MoveAbility) return "move";
            if (ab is OverwatchAbility) return "cone";
            if (ab is ShootAbility) return "actor|pos";
            switch (Result(ab))
            {
                case TargetResult.None: return "none";
                case TargetResult.Actor: return "actor";
                case TargetResult.Position: return "pos";
                case TargetResult.ActorAndPosition: return "actor|pos";
                default: return Result(ab).ToString().ToLowerInvariant();
            }
        }

        private static string Dis(TacticalAbility ab)
        {
            try { return ab.GetDisabledState(IgnoredAbilityDisabledStatesFilter.IgnoreEquipmentNotSelected).Key; }
            catch (Exception ex) { return "threw:" + ex.GetType().Name; }
        }

        private static JObject Row(int i, TacticalAbility ab)
        {
            return ActTap.AbilityRow(i, DefName(ab), ab.GetType().Name, SrcName(ab), Dis(ab), Kind(ab), F(() => ab.ActionPointCost));
        }

        private static bool NameMatch(TacticalAbility ab, string want)
        {
            string def = DefName(ab);
            if (def != null && (string.Equals(def, want, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(def, want + "_AbilityDef", StringComparison.OrdinalIgnoreCase))) return true;
            string t = ab.GetType().Name;
            if (t.EndsWith("Ability", StringComparison.Ordinal)) t = t.Substring(0, t.Length - "Ability".Length);
            return string.Equals(t, want, StringComparison.OrdinalIgnoreCase);
        }

        private static TacticalAbility Pick(List<TacticalAbility> all, string name, int index, string src, out int picked, out object bad)
        {
            bad = null; picked = -1;
            List<int> hit = new List<int>();
            if (index >= 0)
            {
                if (index < all.Count) hit.Add(index);
            }
            else
                for (int i = 0; i < all.Count; i++)
                    if (Active(all[i]) && NameMatch(all[i], name)) hit.Add(i);
            if (src != null) hit.RemoveAll(i => !string.Equals(SrcName(all[i]), src, StringComparison.OrdinalIgnoreCase));
            if (hit.Count == 0)
            {
                bad = ActTap.Bad("noability", "no active ability " + (index >= 0 ? "i=" + index : "'" + ActTap.Clip(name) + "'") +
                                              (src == null ? "" : " from src '" + ActTap.Clip(src) + "'") + " - act list shows them");
                return null;
            }
            if (hit.Count > 1)
            {
                JArray c = new JArray();
                for (int k = 0; k < hit.Count && k < 10; k++) c.Add(Row(hit[k], all[hit[k]]));
                bad = new JObject { ["ok"] = false, ["code"] = "ambiguous", ["error"] = hit.Count + " abilities match - pass `i` or `src`", ["candidates"] = c };
                return null;
            }
            picked = hit[0];
            return all[hit[0]];
        }

        // ------------------------------------------------------------------ list / squad

        private static object List(ActList r)
        {
            TacticalLevelController tac;
            object bad = Scene(out tac);
            if (bad != null) return bad;
            TacticalActor actor = ResolveActor<TacticalActor>(tac, r.Actor, "noactor", out bad);
            if (actor == null) return bad;
            List<TacticalAbility> all = Abilities(actor);
            JObject reply = new JObject { ["ok"] = true, ["actor"] = ActorDto(actor, true), ["turn"] = Turn(tac) };

            if (r.Ability != null || r.AbilityIndex >= 0)
            {
                int i;
                TacticalAbility ab = Pick(all, r.Ability, r.AbilityIndex, r.Src, out i, out bad);
                if (ab == null) return bad;
                reply["ability"] = Row(i, ab);
                if (!r.Targets) return reply;
                bool capped;
                List<JObject> rows = TargetRows(ab, out capped);
                ActTap.PageInto(reply, "targets", rows, r.Page, r.PageSize);
                if (capped) reply["capped"] = true;
                return reply;
            }

            List<JObject> abs = new List<JObject>();
            for (int i = 0; i < all.Count; i++)
                if (r.All || Active(all[i]))
                    if (r.Src == null || string.Equals(SrcName(all[i]), r.Src, StringComparison.OrdinalIgnoreCase))
                        abs.Add(Row(i, all[i]));
            ActTap.PageInto(reply, "abilities", abs, r.Page, r.PageSize);
            return reply;
        }

        private static List<JObject> TargetRows(TacticalAbility ab, out bool capped)
        {
            capped = false;
            List<JObject> rows = new List<JObject>();
            MoveAbility mv = ab as MoveAbility;
            if (mv != null)
            {
                foreach (MoveAbilityTargetData d in mv.GetTargetsData())
                {
                    if (rows.Count >= ActTap.MaxScan) { capped = true; break; }
                    rows.Add(new JObject { ["p"] = ActTap.Pos(d.Position.x, d.Position.y, d.Position.z), ["len"] = ActTap.R2(d.PathLength) });
                }
                return rows;
            }
            foreach (TacticalAbilityTarget t in ab.GetTargets())
            {
                if (rows.Count >= ActTap.MaxScan) { capped = true; break; }
                if (t == null) continue;
                JObject o = new JObject();
                if (t.Actor != null) o["a"] = ActTap.Clip(t.Actor.name);
                Vector3 p = t.PositionToApply;
                if (!float.IsNaN(p.x) && t.Actor == null) o["p"] = ActTap.Pos(p.x, p.y, p.z);
                if (o.Count > 0) rows.Add(o);
            }
            return rows;
        }

        private static object Squad(string which, int page, int size)
        {
            TacticalLevelController tac;
            object bad = Scene(out tac);
            if (bad != null) return bad;
            TacticalFaction viewer = tac.View == null ? null : tac.View.ViewerFaction;
            TacticalActor sel = tac.View == null ? null : tac.View.SelectedActor;
            List<JObject> rows = new List<JObject>();
            foreach (TacticalActor a in tac.Map.GetActors<TacticalActor>())
            {
                if (a == null || a.IsDead || !a.InPlay) continue;
                bool mine = a.TacticalFaction == viewer;
                if (which == "mine" ? !mine : !(mine || a.IsRevealedToViewer)) continue;
                JObject o = ActorDto(a, which == "all");
                if (IsVehicle(a)) o["veh"] = true;
                if (a == sel) o["sel"] = true;
                rows.Add(o);
            }
            JObject reply = new JObject { ["ok"] = true, ["turn"] = Turn(tac) };
            ActTap.PageInto(reply, "actors", rows, page, size);
            return reply;
        }

        /// <summary>Vehicle_TagDef (CharacterTemplateExtension.cs:36). TacticalActorBase.Vehicle does NOT
        /// separate them - it answers with a VehicleComponent for a plain soldier too.</summary>
        internal static bool IsVehicle(TacticalActorBase a)
        {
            try
            {
                foreach (var t in a.GameTags) if (t != null && t.name == "Vehicle_TagDef") return true;
            }
            catch (Exception) { }
            return false;
        }

        // ------------------------------------------------------------------ use

        private static object Use(ActUse u)
        {
            TacticalLevelController tac;
            object bad = Scene(out tac);
            if (bad != null) return bad;
            // Every kill reaches PhoenixStatisticsManager.OnActorKilled -> PhoenixGame.GetCurrentGameTime,
            // which dereferences it (PhoenixGame.cs:718-723) and throws mid-ability on a loadmap mission.
            TacticalGameParams gp = null;
            try { gp = tac.TacticalGameParams; } catch (Exception) { }
            if (gp != null && gp.GlobalTime == null)
                return ActTap.Bad("scene", "TacticalGameParams.GlobalTime is null (a loadmap mission started before v0.3.1's start-mission.json) - a kill would throw inside the ability. Re-run start-mission.json, or set it: call set on @tac.TacticalGameParams GlobalTime {\"$new\":{\"type\":\"Base.Utils.UnityDateTime\"}}");

            TacticalActor actor = ResolveActor<TacticalActor>(tac, u.Actor, "noactor", out bad);
            if (actor == null) return bad;
            if (actor.IsDead) return ActTap.Bad("noactor", "'" + ActTap.Clip(actor.name) + "' is dead");

            TacticalFaction cf = tac.CurrentFaction;
            if (cf == null || cf != actor.TacticalFaction || !cf.IsControlledByPlayer || !cf.IsPlayingTurn)
                return new JObject { ["ok"] = false, ["code"] = "turn", ["error"] = "not this actor's player turn - wait for NewTurnEvent, then check act squad's turn.mine", ["turn"] = Turn(tac) };

            TacticalView view = tac.View;
            if ((view != null && view.IsWaitingForActiveAndQueuedAbilitiesAndMapUpdate()) || actor.HasExecutingAbility(null, false) ||
                actor.TacticalNav.IsNavigating || actor.TacticalNav.IsExecutingFacing)
                return ActTap.Bad("busy", "the game is still playing out an action - retry when it settles");

            List<TacticalAbility> all = Abilities(actor);
            int idx;
            TacticalAbility ab = Pick(all, u.Ability, u.AbilityIndex, u.Src, out idx, out bad);
            if (ab == null) return bad;

            string dis = Dis(ab);
            if (dis != "NotDisabled")
                return new JObject { ["ok"] = false, ["code"] = "disabled", ["dis"] = dis, ["error"] = "the game reports this ability disabled: " + dis };

            TacticalActorBase targetActor;
            TacticalAbilityTarget target = Target(tac, ab, u, out targetActor, out bad);
            if (bad != null) return bad;

            Probe probe = new Probe(tac, actor, ab, DefName(ab), targetActor);
            try
            {
                ab.Activate(target);
                if (view != null) view.UpdateSquadMembersActionAndWillPoints();
            }
            catch (Exception ex)
            {
                probe.Release();
                return new { ok = false, code = "threw", issued = true, error = Protocol.Clip("Activate threw " + ex.GetType().Name + ": " + ex.Message) };
            }
            return probe;
        }

        private static object Kindless(TacticalAbility ab, string what)
        {
            return ActTap.Bad("targetKind", "'" + ActTap.Clip(DefName(ab)) + "' takes target kind '" + Kind(ab) + "', not " + what);
        }

        private static TacticalAbilityTarget Target(TacticalLevelController tac, TacticalAbility ab, ActUse u, out TacticalActorBase targetActor, out object bad)
        {
            bad = null;
            targetActor = null;
            if (u.TargetActor != null)
            {
                targetActor = ResolveActor<TacticalActorBase>(tac, u.TargetActor, "notarget", out bad);
                if (targetActor == null) return null;
            }
            Vector3 pos = u.TargetPos == null ? Vector3.zero : new Vector3(u.TargetPos[0], u.TargetPos[1], u.TargetPos[2]);

            MoveAbility mv = ab as MoveAbility;
            if (mv != null)
            {
                if (u.TargetPos == null) { bad = Kindless(ab, u.TargetActor != null ? "an actor" : "no target (needs {pos:[x,y,z]})"); return null; }
                MoveAbilityTargetData best = null;
                float bestD = float.PositiveInfinity;
                foreach (MoveAbilityTargetData d in mv.GetTargetsData())
                {
                    float dd = (d.Position - pos).sqrMagnitude;
                    if (dd < bestD) { bestD = dd; best = d; }
                }
                if (best == null || Mathf.Sqrt(bestD) > MoveTolerance)
                {
                    bad = new JObject
                    {
                        ["ok"] = false, ["code"] = "notarget",
                        ["error"] = "no reachable tile within " + MoveTolerance + " m of that pos (act list {ability:'move',targets:true} lists them)",
                        ["nearest"] = best == null ? null : ActTap.Pos(best.Position.x, best.Position.y, best.Position.z)
                    };
                    return null;
                }
                return best.ToTarget();
            }

            OverwatchAbility ow = ab as OverwatchAbility;
            if (ow != null)
            {
                if (targetActor == null && u.TargetPos == null) { bad = Kindless(ab, "no target (needs {pos} or {actor} to aim the cone at)"); return null; }
                Vector3 aim = targetActor != null ? targetActor.Pos : pos;
                float spread = ow.GetConeSpreadSettings().DefaultValue * Mathf.Deg2Rad;
                return new TacticalAbilityTarget { Cone = ow.GetAbilityTargetCone(aim, spread) };
            }

            string kind = Kind(ab);
            if (targetActor != null)
            {
                if (!kind.Contains("actor")) { bad = Kindless(ab, "an actor"); return null; }
                int n = 0;
                foreach (TacticalAbilityTarget t in ab.GetTargets())
                {
                    if (++n > ActTap.MaxScan) { bad = ActTap.Bad("cap", "the ability offers more than " + ActTap.MaxScan + " targets and none of the first ones is that actor"); return null; }
                    if (t != null && t.Actor == targetActor) return t;
                }
                bad = ActTap.Bad("notarget", "'" + ActTap.Clip(targetActor.name) + "' is not in this ability's current target set (line of fire, range, relation) - act list {ability,targets:true}");
                return null;
            }

            if (u.TargetPos != null)
            {
                ShootAbility sh = ab as ShootAbility;
                if (sh != null)
                {
                    TacticalAbilityTarget ground = new TacticalAbilityTarget(pos) { AttackType = sh.AttackType };
                    TacticalAbilityTarget st = sh.GetShootTarget(ground, null, sh.OriginTargetData);
                    if (st == null) bad = ActTap.Bad("notarget", "the weapon cannot shoot at that pos (range / line of fire)");
                    return st;
                }
                if (!kind.Contains("pos")) { bad = Kindless(ab, "a pos"); return null; }
                TacticalAbilityTarget best = null;
                float bestD = float.PositiveInfinity;
                int n = 0;
                foreach (TacticalAbilityTarget t in ab.GetTargets())
                {
                    if (++n > ActTap.MaxScan) { bad = ActTap.Bad("cap", "the ability offers more than " + ActTap.MaxScan + " positions"); return null; }
                    if (t == null || float.IsNaN(t.PositionToApply.x)) continue;
                    float dd = (t.PositionToApply - pos).sqrMagnitude;
                    if (dd < bestD) { bestD = dd; best = t; }
                }
                if (best == null || Mathf.Sqrt(bestD) > PosTolerance) { bad = ActTap.Bad("notarget", "no valid position within " + PosTolerance + " m of that pos"); return null; }
                return best;
            }

            if (ab is ShootAbility) { bad = Kindless(ab, "no target (needs {actor} or {pos})"); return null; }
            foreach (TacticalAbilityTarget t in ab.GetTargets()) if (t != null) return t;
            if (Result(ab) == TargetResult.None) return null;
            bad = ActTap.Bad("notarget", "no target given and the ability offers none");
            return null;
        }

        /// <summary>One issued action, polled once per frame until the game has settled.</summary>
        private sealed class Probe : IActProbe
        {
            private readonly TacticalLevelController tac;
            private readonly TacticalActor actor;
            private readonly TacticalAbility ab;
            private readonly string def;
            private readonly TacticalActorBase target;
            private readonly float ap0, hp0;
            private readonly bool subscribed;
            private bool executed, everBusy, released;
            private int frames, settled, idle;

            internal Probe(TacticalLevelController tac, TacticalActor actor, TacticalAbility ab, string def, TacticalActorBase target)
            {
                this.tac = tac; this.actor = actor; this.ab = ab; this.def = def; this.target = target;
                ap0 = F(() => actor.CharacterStats.ActionPoints);
                hp0 = target == null ? 0f : F(() => target.Health);
                // BEFORE Activate: an action that finishes inside Activate raises the event synchronously.
                try { tac.AbilityExecutedEvent += OnExecuted; subscribed = true; } catch (Exception) { }
            }

            private void OnExecuted(TacticalAbility a, object p) { if (a == ab) executed = true; }

            public void Release()
            {
                if (released) return;
                released = true;
                if (subscribed) try { tac.AbilityExecutedEvent -= OnExecuted; } catch (Exception) { }
            }

            private bool Alive(UnityEngine.Object o) { return o != null; }

            public JObject Poll()
            {
                Level lvl = GameUtl.CurrentLevel();
                if (!Alive(tac) || lvl == null || lvl.GetComponent<TacticalLevelController>() != tac)
                    return new JObject { ["ok"] = false, ["code"] = "stale", ["issued"] = true, ["error"] = "the mission changed or ended while the action was settling" };
                frames++;
                bool actorAlive = Alive(actor) && !actor.IsDead;
                bool viewBusy = tac.View != null && tac.View.IsWaitingForActiveAndQueuedAbilitiesAndMapUpdate();
                bool exec = actorAlive && actor.HasExecutingAbility(null, false);
                bool nav = actorAlive && (actor.TacticalNav.IsNavigating || actor.TacticalNav.IsExecutingFacing);
                bool mine = ab.IsEnqueued || ab.IsExecuting;
                bool busy = viewBusy || exec || nav || mine;
                if (busy) { everBusy = true; settled = 0; idle = 0; }
                else { settled++; idle++; }

                string how = null;
                if (tac.IsGameOver && !mine) how = executed ? "event" : "gameover";
                else if (executed && settled >= SettledFrames) how = "event";
                else if (!executed && !everBusy && idle >= QuietFrames) how = "sync";
                else if (!executed && everBusy && idle >= NoEventFrames) how = "noevent";
                if (how == null) return null;
                return Done(how, actorAlive);
            }

            private JObject Done(string how, bool actorAlive)
            {
                JObject r = new JObject { ["ok"] = true, ["ability"] = ActTap.Clip(def), ["exec"] = how, ["frames"] = frames };
                r["ap"] = new JArray(ActTap.R2(ap0), ActTap.R2(actorAlive ? F(() => actor.CharacterStats.ActionPoints) : 0f));
                if (target != null && Alive(target))
                {
                    r["hp"] = new JArray(ActTap.R2(hp0), ActTap.R2(F(() => target.Health)));
                    if (target.IsDead) r["dead"] = true;
                }
                if (!actorAlive) r["actorDead"] = true;
                else if (ab is MoveAbility) { Vector3 p = actor.Pos; r["pos"] = ActTap.Pos(p.x, p.y, p.z); }
                if (tac.IsGameOver) r["gameOver"] = true;
                return r;
            }

            public JObject Flags()
            {
                bool actorAlive = Alive(actor) && !actor.IsDead;
                return new JObject
                {
                    ["view"] = Alive(tac) && tac.View != null && tac.View.IsWaitingForActiveAndQueuedAbilitiesAndMapUpdate(),
                    ["exec"] = actorAlive && actor.HasExecutingAbility(null, false),
                    ["nav"] = actorAlive && actor.TacticalNav.IsNavigating,
                    ["face"] = actorAlive && actor.TacticalNav.IsExecutingFacing,
                    ["enq"] = ab.IsEnqueued,
                    ["event"] = executed,
                    ["frames"] = frames
                };
            }
        }

        // ------------------------------------------------------------------ end turn

        private static object EndTurn()
        {
            TacticalLevelController tac;
            object bad = Scene(out tac);
            if (bad != null) return bad;
            TacticalFaction cf = tac.CurrentFaction;
            if (cf == null || !cf.IsControlledByPlayer || !cf.IsPlayingTurn)
                return new JObject { ["ok"] = false, ["code"] = "turn", ["error"] = "not the player's turn - nothing to end", ["turn"] = Turn(tac) };
            cf.RequestEndTurn();
            return new JObject { ["ok"] = true, ["requested"] = true, ["turn"] = Turn(tac) };
        }
    }
}
