# TODO — capability gaps found by the AAP runtime audit (2026-08-29)

Standing TODO. Source: a full runtime audit of a third-party mod, **Assorted Adjustments Project
(AAP)**, driven entirely from `ppcli.ps1` against `D:\PP-Instance2` — four game sessions, ~500
`connect` calls, a def-level A/B between a modded and a vanilla session, and a set of tactical
experiments.

## Why this file exists

PPCLI carried the audit. Specific wins: **47 of 49 Harmony patch targets enumerated live** (via
`call` → `Harmony.GetAllPatchedMethods` → `GetPatchInfo(m).Owners`); def fields read **before and
after** events; a **localization defect found that pure source review had misjudged**; three of the
mod's dead features proved dead in one line each. Everything below is the opposite list: the places
where a check had to be marked **UNTESTABLE** in the audit report, or worked around. Each gap traces
to real blocked checks from that run — this is a workload, not a wishlist.

Ordered by how many audit checks each gap blocked. The last four are cost/friction rather than
blocked checks, and say so.

---

## 1. No way to drive a UI gesture or enter a ViewState — **9 checks blocked**

> **CLOSED 2026-08-29** - `roots` now names the open UI in BOTH phases: `@view` (tactical `TacticalView` / geoscape `GeoscapeView`), `@viewstate` (the top of the state stack) and `@modules` (the module holder). No new engine hook was needed - the geoscape view was always one public hop from `@geo` (`GeoLevelController.cs:101`); it simply had no name. Entering a screen is the view's OWN public lever, `call {"op":"invoke","target":"@view","member":"ToResearchState","args":[]}` and `ResetViewState(null)` to come back - verified live both ways (`UIStateVehicleSelected` -> `UIStateResearch` -> `UIStateVehicleSelected`). `@viewstate` is RE-READ every call and must never be saved: entering a UI state the stack has already popped is how this project has wedged the geoscape before, and pushing onto the private `_statesStack` by hand gets the `StateStackAction` wrong.

- Could not observe: deployment screen's "N / M" slot counter · squad-evacuation confirmation prompt ·
  facility info panel's extra rows · recruit tooltip's PERSONAL ABILITIES block · geoscape
  agenda-tracker row hover + click-to-focus · scrap-aircraft roster entry · smart base selection ·
  whether right-click suppresses a move order.
- Would have settled: 9 of the mod's 55 patches are UI patches. All are **bound** (proved). Binding
  says nothing about whether the body runs or draws. Two turned out dead via a side channel (gap 4);
  the other seven stayed UNTESTABLE.
- Tried:
  ```powershell
  .\ppcli.ps1 plan .\plans\aim-and-run.json '{"x":-2.5,"y":0.08,"z":-4.5,"command":"remove","cmdArgs":[]}'
  → step 'run-command' (console) failed: NullReferenceException
  .\ppcli.ps1 connect call '{"op":"invoke","target":"<UIStateRosterDeployment>","member":"SetUpInitialDeployment"}'
  → never attempted: there is no way to obtain a live UIState instance, and `roots` exposes none
  ```
  `build-mission.json` does stop in DEPLOYMENT, but that is the *tactical* deploy zone, not the
  geoscape `UIStateRosterDeployment` the patch targets.
- Where it broke: no verb reaches the UI layer. `roots` exposes `@tac`, `@map`, `@selected`, `@defs`,
  `@faction` — nothing for the current view or its module stack, so a UI object cannot be named at all.
- Minimal capability: **name the currently open view/module objects the way `roots` names level
  objects**, so an already-open screen's rendered text can be read back.

## 2. No way to reach a specific campaign state — **~8 checks blocked**

> **CLOSED 2026-08-29** - `plans\geo-fast-forward.json` ages a live campaign by running the real simulation: `@geo.Timing.Scale` + `Paused` (`Timing.cs:79,100`), both restored in the `finally`. Measured live: **30 real seconds at Scale 3600 advanced `ElaspedTime` `00:00:00` -> `1.00:01:37.2`** - about one in-game day per 28 real seconds, with research, faction income and the midnight pipeline all really running (`GeoLevelController.cs:761-868`). **3600 is the proved ceiling**: the hourly scheduler dequeues what is overdue in ONE pass (`TimingScheduler.cs:425-441`), so a frame that jumps several hours collapses them into one callback and can skip midnight. Age once, then `snapshot` the result. The `wait` verb gained `{"forMs":N}` for this - the one wait with no predicate, because "run for this long" is a SUCCESS and spelling it as a never-true predicate would report every healthy fast-forward as a failure.

- Blocked: all the Limited War rows, the tutorial/Jacob row, the DLC-absent row.
- `start-campaign.json` gets a fresh geoscape in ~11 s, which is superb — but a fresh geoscape has no
  faction wars, no completed research, no excavations, no tutorial. Everything AAP does on the
  geoscape needs an **aged** campaign.
- Tried: manual save-and-load is the only route, and this project's own notes already record that
  geoscape saves copied between installs do not load.
- Minimal capability: **fast-forward geoscape time under automation**, so an aged campaign state is
  reachable without a human playing to it.

## 3. No way to await or stage a game EVENT — **5 checks blocked**

> **HALF CLOSED 2026-08-29.**
> **Mission END is closed.** `plans\end-mission.json` leaves a finished mission the way the game does: console `win`/`lose` -> `TacticalLevelController.GameOver()` (`:1051-1065`, `:1092-1108`, which raises wrapping-up, runs cleanup/XP/telemetry and tears mod hooks down) -> battle summary -> the private `TacticalView.GoToGeoscape()` (`:1112-1120`), which is the summary button's own callback. Verified live from a REAL `GeoScavengingMission` launched off the geoscape: `UIStateBattleSummary` -> `phase:"geoscape"` -> `UIStateGeoModal` with `ModalType.GeoScavengeOutcome`, 13.6 s end to end. The reward screen exists ONLY after a real `GeoMission` (`GeoLevelController.cs:1412-1466`, `:683-713`); a synthetic `start-mission` has no campaign to return to.
>
> **"Await event E" is DELIBERATELY NOT BUILT** - it is engine surgery, not a small addition, and here is exactly what it would take:
> - There is no single bus to subscribe to. `Base.Eventus.EventusManager` (`:17-48`) sees only what is routed through Eventus, and the interesting lifecycle events are ordinary CLR events on the controllers: `GameWrappingUpEvent` / `GameOverEvent` / `NewTurnEvent` (`TacticalLevelController.cs:299-303`) and `HourTicked` / `DailyUpdateEvent` (`GeoLevelController.cs:340-342`). One `EventExecuted` subscription cannot cover them.
> - Subscribing reflectively means MANUFACTURING a delegate of the event's own handler type at runtime. Each event has a different signature, so this is `Reflection.Emit` (or a hand-written trampoline per arity) - `Delegate.CreateDelegate` cannot bind a generic catcher to an arbitrary signature.
> - It needs a lifetime story the bridge does not have today: unsubscribe on success, on timeout, on cancel, on scene unload / epoch change, and on server shutdown. A missed unsubscribe holds a dead `IPending` alive across a level load and fires it into a torn-down job table.
> - It needs argument projection from the handler's parameters into a reply, inside a `Reflect.Project` call made from an ARBITRARY game thread rather than the drain loop.
> Until then: persistent conditions (`IsGameOver`, the open `@viewstate`, a research state) are `wait {"call":...}` predicates and are covered. Genuinely TRANSIENT events (a return-fire trigger, one haven attack) remain unobservable - polling can and does miss them.

- Could not observe: a return-fire event (does the mod's cover-cancel ever remove an ability from the
  list?) · a faction-vs-faction haven attack (does the Limited War log rename fire at mission *start*?)
  · a research completing · an excavation starting · a mission *ending* into the geoscape reward screen.
- Would have settled: `ReturnFirePatch`'s cover-cancel is claimed never to have fired once since the
  mod's first release — could neither confirm nor refute. The Limited War block is 11 patches; only
  one was proved to fire.
- Tried:
  ```powershell
  .\ppcli.ps1 connect call '{"op":"invoke","target":"@tac","member":"GetReturnFireAbilities"}'
  → nothing binds: List`1 GetReturnFireAbilities(TacticalActor shooter, Weapon weapon,
     TacticalAbilityTarget target, ShootAbility shootAbility, Boolean getOnlyPossibleTargets,
     List`1 casualties) takes 6 args, 0 given
  ```
  `shooter` and `weapon` are available as handles; a `TacticalAbilityTarget` is not constructible, and
  `op:"new"` on it would need a valid firing solution anyway.
- Where it broke: `wait` polls a predicate on state — there is no "wait until event E is raised", and
  no way to make an event happen other than playing. Cross-cutting: no way to reach a mission-END
  transition either (`start-mission` gets *into* a mission; nothing gets *out* into the reward screen).
- Minimal capability: **a plan step that blocks until a named game event fires, with a timeout**, plus
  **a way to leave a finished mission the way the game does**, so post-mission screens become reachable.

## 4. Reflection is the only "does this member exist" test — and it is undocumented — **3 checks**

> **CLOSED 2026-08-29** - `PLAYBOOK.md` carries the row and a worked block (`AccessTools.Field`/`.Property` through `call`).

Half praise, half request. Three of AAP's dead features were proved dead in one line each:

```powershell
call '{"op":"invoke","type":"HarmonyLib.AccessTools","member":"Field",
       "args":[{"$type":"…UIModuleDeploymentMissionBriefing"},"_mission"]}'   → NULL
call … AccessTools.Field(UIStateCharacterSelected, "_contextualMenuModule")   → NULL
call … AccessTools.Field(UIFacilityInfoPopup, "transform")                    → NULL
```

Each is a `Traverse.Create(x).Field("name")` in the audited mod that silently returns null at runtime.
Asking the live assembly "does this member exist, and is it a field or a property" turned three
unfalsifiable static suspicions into hard results without opening a screen. **Preserve this.**
`{"$type":"…"}` as an argument envelope is what makes it work.

- The gap: nothing in `PLAYBOOK.md` or `README.md` suggests reflection-shaped questions ("is `X.Y` a
  field or a property?", "what is its declared type?") are answerable at all. The auditor had to reach
  for `HarmonyLib.AccessTools` unaided, after a wasted detour trying to reach the UI first.
- Minimal capability: **document it** — one playbook row: *"is this member a field, a property, or
  absent? → `AccessTools.Field` / `.Property` through `call`."*

## 5. Cursor-scoped console commands cannot be aimed at an arbitrary actor — **3 attempts, all failed**

> **CLOSED 2026-08-29** - two halves.
> **The clean kill** is `plans\kill-actor.json '{"actorName":"Crabman_10"}'`: `GameObject.Find(name)` -> `GetComponentInChildren<TacticalActorBase>` -> a boxed `DamageResult` (a struct, so `Activator.CreateInstance`, not `op:new`) -> `TacticalActorBase.ApplyDamage` (`:950`), the same terminal call a bullet makes (`DamageAccumulation.cs:565-626`). Verified live: `IsDead false -> true`, ragdoll completed, no exception, mission continued. **The old workaround's diagnosis was wrong**: `DieAbility.Activate(null)` throws because `RagdollDieAbility` casts `action.Param` to a `DeathReport` and reads its `ImpactForce` (`RagdollDieAbility.cs:68-69`) - null is not a `DeathReport`. It is not "a missing damage source"; `ApplyDamageInternal` builds a complete report itself by storing `LastDamageSource` and `CurrentDamageForce` BEFORE health crosses zero (`TacticalActorBase.cs:844-875`). `DamageTypeDef` was tested both ways and is NOT load-bearing - it is set by default only so the kill looks like a kill to the rest of the game.
> **The assertion** is `aim-and-run.json`'s new `requireActor`: it calls the view's own `SelectAtCursor()` (the very call every actor-scoped console command starts with, `TacConsoleGameplay.cs:48-54`) and refuses when `Actor` is null, instead of letting the game NRE three lines later. Verified both ways - refused on a point 32 m off the camera centre, green on `Facehugger_9` at offsets 0.2-0.8. Off by default because `blast`, `tp` and `info` legitimately aim at bare floor.

- Wanted: kill one specific actor to trigger the death-driven loot patches.
- Tried:
  ```powershell
  plan aim-and-run.json '{"x":4.5,"y":0.004,"z":16.5,"command":"blast"}'   → ok:true, nothing died
  plan aim-and-run.json '{"x":-2.5,"y":0.08,"z":-4.5,"command":"remove"}'  → NullReferenceException
  plan aim-and-run.json '{"x":-2.5,"y":0.08,"z":-4.5,"command":"blast"}'   → NullReferenceException
  ```
  The first returned `ok` with an empty `output` and no observable effect; the next two threw inside
  the game's own command. The plan's trace was green for every step up to `run-command` — the failure
  is in what the cursor resolved to, not in the plan.
- Where it broke: `aim-and-run` sets a screen-space cursor from a world point; a target not
  rendered/revealed at that moment resolves to nothing and the game's command NREs. Nothing in the
  plan asserts "the cursor is now over an actor" before running the command.
- Workaround used (worth stealing into a plan):
  ```powershell
  call '{"op":"invoke","target":"<ACTOR>","member":"GetAbility",
         "typeArgs":["PhoenixPoint.Tactical.Entities.Abilities.DieAbility"]}'   → <DIE>
  call '{"op":"invoke","target":"<DIE>","member":"Activate","args":[null]}'
  ```
  This kills the actor and the death pipeline runs (AAP's `DropItems` postfix fired and logged), but
  `Activate(null)` throws inside `RagdollDieAbility.Die` for want of a damage source, and the actors
  afterwards still report `IsDead=False`. Good enough to trigger a patch, not good enough to leave a
  clean mission state.
- Minimal capability: **a verb that kills a named actor cleanly through the real damage pipeline**, and
  **an assertion in `aim-and-run` that the cursor actually resolved to an actor** before running the
  command.

## 6. Reading one def field costs three round trips, and there is no A/B — *cost, not blocked checks*

> **CLOSED 2026-08-29** - `"@def:<name|guid>"` is a target anywhere a handle is (and `{"$def":...}` takes a name), and `inspect {"values":true}` dumps EVERY instance field, handle-free and name-sorted, for a mechanical diff: a scalar, an explicit `null`, `{"$omitted":type[,count][,guid]}` for a non-scalar, or `{"$error":...}` for a read that threw. Nothing is dropped silently, because in a diff "unchanged" and "not observed" must not look the same.

Frequency: ~60 reads in one probe script; the A/B cost two entire game sessions.

- Wanted: whether the mod changed a def, for 40 defs — i.e. the value with the mod, the value without
  it, and the difference.
- Value delivered anyway: the single most valuable table in the audit. **Ten armour defs AAP claims to
  modify are byte-identical to vanilla; three others changed.** No amount of source reading settles
  that; only the A/B does.
- Tried, per field, three calls:
  ```powershell
  connect find  '{"query":"NJ_Heavy_LeftArm_BodyPartDef"}'                            # → guid
  connect call  '{"op":"invoke","target":"@defs","member":"GetDef","args":["<guid>"]}' # → handle
  connect call  '{"op":"get","target":"<handle>","member":"Armor"}'                    # → value
  ```
  `find` returns only `{name, guid, type}`, so the guid hop is unavoidable. The whole script was then
  run twice — once against a session with the mod deactivated in `Options.jopt`, once with it active —
  and the two text files diffed by hand. Each session is a cold launch plus a profile edit plus a wait.
- Where it broke: two things — `find` cannot return field values, and there is no notion of "what did
  a mod change" at all.
- Minimal capability: **address a def by name in one step**, and **dump a def's field values** so two
  runs can be diffed mechanically instead of by eye.

## 7. Every `connect` call is a separate process; no scripted multi-call — *cost, constant*

> **PARTLY CLOSED 2026-08-29** - `connect multi` sends N verbs over one discovered endpoint in one process, and the WHOLE array is now prevalidated before row 1 is sent (a bad row 2 used to be found only after row 1 had already changed the game, and the throw took row 1's result with it). It stays sequential, not transactional. The plan step that ITERATES a live collection accumulating a projection is NOT built: `repeat` has no accumulator and no per-row scope, so it is real plan-engine surgery rather than a small addition.

The Harmony enumeration alone was **188 sequential invocations**.

- Wanted: for each of 47 patched methods, its full description and its owner list — a loop over a
  collection reading two members per row.
- This is *the* central deliverable of the audit (the binding table). It took about four minutes of
  wall clock, almost all of it PowerShell process startup — the game answers in 17-60 ms.
- Tried: a PowerShell loop calling `.\ppcli.ps1 connect …` 188 times (`enum-patches.ps1`). It works and
  is the only way. `plan` is excellent for a fixed sequence, but its JSON cannot express "for each row
  in this collection, read members A and B and collect them".
- Where it broke: `batch` cold-launches the game, so it cannot be used against a live session; `plan`
  has `repeat`, but not "repeat over a live collection accumulating a result".
- Minimal capability: **several calls over one live connection in a single invocation**, and **a plan
  step that iterates a collection handle accumulating a projection**.

## 8. Struct-valued members project as objects, and member names must be guessed — *~6 detours*

> **CLOSED 2026-08-29** - `members`/`inspect` filters take a glob (`*Coefficient*`) and are paged with a `total`; a struct's user-defined conversion is available on request as `call {"op":"get",…,"convertTo":"System.Single"}`, which answers with `converted` beside the untouched `value`. It is OPT-IN, never automatic: running `op_Implicit` is running arbitrary user code, and projection may not do that behind a reader's back.

- Wanted: a soldier's willpower as two numbers.
- Tried:
  ```powershell
  call '{"op":"get","target":"<WP>","member":"Value"}'
  → @{type=Base.Entities.Statuses.ModifiableValue; BaseValue=6; ModificationValue=0}
  ```
  The audited C# does `wp.Value` and gets a `float` through an implicit operator; through `call` the
  struct comes back. `IntValue` / `IntMax` were found only from having seen them on `GetHealth`
  earlier. Same story elsewhere: `EquipmentComponent.EquipmentItems` does not exist (it is `Items`);
  `Frenzy_StatusDef` has `SpeedCoefficient`, not `SpeedMultiplier`.
- Where it broke: `inspect` is the discovery tool and it truncates silently at a fixed budget
  (`"count":400,"truncated":true`) with no way to ask for "members matching *Coefficient*".
- Minimal capability: **let `inspect` filter or page its member list**, and **expose the scalar an
  implicit conversion would yield** alongside the struct projection.

## 9. Profile selection is not pinned per install — *trivial but constant, every call*

> **CLOSED 2026-08-29** - line 2 of `ppcli-install.txt` is that install's SteamID64.

```
REFUSED: 5 Steam profiles under …\Phoenix Point\Steam (…, …, …) -
pass -ProfileId <SteamID64> to say which one this install writes.
```

- `ppcli-install.txt` pins the install; nothing pins the profile that goes with it, so
  `-ProfileId 76561198000000000` had to be threaded through every helper function in every script
  written for the audit. The refusal itself is right — silently guessing would be worse.
- Minimal capability: **let the install pin carry its profile id**, the way `ppcli-install.txt` carries
  the install path.

## 10. Handles expire on a scene change mid-enumeration — *1 occurrence, cost one full re-run*

```
nothing binds for 'FullDescription': … handle 'h:1:2' is from epoch 1,
the current epoch is 2 (the scene changed; re-resolve it through roots/find)
```

- The intro movie ended between fetching the array and reading its first element. The error message is
  excellent — it named the cause and the fix — but a 188-call enumeration has to complete inside one
  epoch, and nothing warns up front that you are racing a scene transition.
- Minimal capability: **pin an enumeration against epoch changes**, or **a "the scene is settled,
  nothing is loading" signal** to gate long read-only sweeps on.

---

## Keep as is — these carried the audit

1. **Cold start to a real geoscape and a real tactical mission, repeatedly, in seconds.**
   `start-campaign.json` ~11 s, `start-mission.json` ~17 s, both from a menu, both re-runnable inside
   one process. Four sessions, eight cold starts, zero failures. Without this the audit does not exist:
   every def A/B, every kill test, every localization probe depended on reaching a live level on demand.
2. **Arbitrary reflection through `call`, including statics and generics.** Three results were
   impossible any other way: enumerating Harmony's own applied-patch set (`Harmony.GetAllPatchedMethods`
   → `GetPatchInfo(m).Owners`), the `AccessTools.Field(...) → NULL` proofs above, and the decisive
   `StatusStat.Set` vs `SetMax` experiment that settled a bug the static audit could only theorise about:
   ```
   baseline 6/6 → Set(36) 6/6 → Set(36) 6/6 → SetMax(36) 6/36
   ```
   `typeArgs` for generic methods and the `{"$h":…}` / `{"$type":…}` envelopes are what make this usable.
3. **The overload-mismatch error message.** Wrong arity prints every candidate signature with parameter
   names and types. Fastest API-discovery tool in the client — used deliberately, four times, to learn
   signatures with no other source. Keep it verbose.
4. **`plan`'s honesty on failure.** `outputWithheld` — *"a figure read from a run the plan itself
   refused is not a measurement"* — stopped numbers being reported off an already-failed run. In an
   audit whose whole value is that no claim is unfounded, that is worth more than the convenience it costs.

## Reproduction context

- **Subject:** Assorted Adjustments Project (AAP), a third-party Phoenix Point mod — 55 Harmony
  patches, 49 patch targets, ~40 defs claimed modified, a Limited War geoscape block (11 patches),
  9 UI patches.
- **Install:** `D:\PP-Instance2` (the automation copy), driven with an explicit `-ProfileId` for its
  own Steam profile.
- **Sessions:** four; eight cold starts via `start-campaign.json` / `start-mission.json`; ~500
  `connect` calls; one 188-call enumeration script (`enum-patches.ps1`).
- **A/B method:** the same def-probe script run twice — once with AAP deactivated in the profile's
  `Options.jopt` `MOD_ACTIVATED`, once active — outputs diffed by hand.
- **Kind of checks:** is each patch target bound? does the patch body actually run? did a def value
  change vs vanilla? does a UI screen render what the patch claims? does a feature that only exists
  during an event ever fire?

## Flagged as uncertain in the source log

- ~~Gap 5's `DieAbility.Activate(null)` workaround~~ — **SETTLED 2026-08-29.** The throw was a null
  `action.Param` where `RagdollDieAbility` wanted a `DeathReport`, not a missing damage source, and
  `plans\kill-actor.json` goes through `ApplyDamage` instead. See the gap-5 note above.
- Gap 3's `ReturnFirePatch` claim ("never fired once since first release") is the mod author's, and the
  audit could **neither confirm nor refute** it.
