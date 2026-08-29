# PPCLI capability-gap research — independent audit

Scope: source review plus live probes against the automation install `D:\PP-Instance2` and its own
Steam profile, on 2026-08-29. I did not connect to, launch, kill, or write into the human Steam
install. `DEC` below
means `decompiled/AssemblyCSharp/Assembly-CSharp/src`. No result returned `stale:true`.

## Gap 1 — REACHABLE

- The gap premise is already partly stale. PPBridge has a tactical-only `@view` root:
  `PPCLI/src/PPBridgeMain.cs:229`, `PPCLI/src/PPBridgeMain.cs:240`,
  `PPCLI/src/PPBridgeMain.cs:243`. There is no geoscape `@view`, but the live geoscape view is one
  ordinary hop from an existing root because `GeoLevelController.View` is public
  (`DEC/PhoenixPoint.Geoscape.Levels/GeoLevelController.cs:101`). Tactical has the same ownership
  edge at `TacticalLevelController.View` (`DEC/PhoenixPoint.Tactical.Levels/TacticalLevelController.cs:165`).
- Real geoscape ownership chain:
  `@geo -> GeoLevelController.View -> GeoscapeView`. From the view:
  `CurrentViewState` exposes the top state (`DEC/PhoenixPoint.Geoscape.View/GeoscapeView.cs:193`),
  `GeoscapeModules` and `CommonModules` expose the module holders
  (`DEC/PhoenixPoint.Geoscape.View/GeoscapeView.cs:62`, `:65`), and private `_statesStack` is the
  actual owner (`DEC/PhoenixPoint.Geoscape.View/GeoscapeView.cs:108`, `:334`). PPCLI `get` searches
  private fields and properties through the type hierarchy (`PPCLI/src/Reflect.cs:469`, `:473`).
- Real tactical chain:
  `@view` (or `@tac -> View`) `-> TacticalView.CurrentState`, `TacticalModules`, `CommonModules`.
  Those members are at `DEC/PhoenixPoint.Tactical.View/TacticalView.cs:171`, `:114`, `:117`; the
  backing stack is `_statesStack` at `:93`, constructed at `:982`.
- The state stack is not merely observable. Its exact transition lever is
  `StateStack<TContext>.SwitchToState(IState<TContext> state, StateStackAction stackAction)` at
  `DEC/Base.UI/StateStack.cs:50`; `CurrentState` is at `:23`. For named common geoscape screens the
  cheaper, safer levers are the view's public methods, e.g. `GeoscapeView.ToResearchState()`
  (`DEC/PhoenixPoint.Geoscape.View/GeoscapeView.cs:696`) and `ResetViewState(UIStateInitial.Params)`
  (`:414`). For a state without a public `ToX` method, construct the concrete state and invoke
  `SwitchToState` on the `_statesStack` handle. That last route is constructor/data-dependent; it is
  not a promise that every state has a parameterless constructor.
- Modules are stable named fields, not a Unity hierarchy search PPCLI must reinvent. For example,
  `GeoscapeModulesData.FactionDataTracker`, `DeploymentMissionBriefingModule`, and `ModalModule` are
  at `DEC/Base.UI/GeoscapeModulesData.cs:66`, `:78`, `:104`.

Live proof — ownership and private stack, current reward modal:

```text
.\ppcli.ps1 connect call '{"op":"get","target":"@geo","member":"View"}'
=> {"ok":true,"value":{"h":"h:32:223","type":"PhoenixPoint.Geoscape.View.GeoscapeView","name":"GeoscapeLevel(Clone)","instanceId":-442580}}

.\ppcli.ps1 connect call '{"op":"get","target":{"$h":"h:32:211"},"member":"_statesStack"}'
=> {"ok":true,"value":{"h":"h:32:224","type":"Base.UI.StateStack`1[[PhoenixPoint.Geoscape.View.GeoscapeViewContext, Assembly-CSharp, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null]]"}}

.\ppcli.ps1 connect call '{"op":"get","target":{"$h":"h:32:211"},"member":"CurrentViewState"}'
=> {"ok":true,"value":{"h":"h:32:225","type":"PhoenixPoint.Geoscape.View.ViewStates.UIStateGeoModal"}}

.\ppcli.ps1 connect call '{"op":"get","target":{"$h":"h:32:211"},"member":"GeoscapeModules"}'
=> {"ok":true,"value":{"h":"h:32:226","type":"Base.UI.GeoscapeModulesData","name":"GeoscapeUICanvas","instanceId":3076738}}
```

Live proof — entering a state:

```text
get @geo.View.CurrentViewState
=> PhoenixPoint.Geoscape.View.ViewStates.UIStateVehicleSelected (h:4:17)
invoke GeoscapeView.ToResearchState []
=> {"ok":true,"void":true}
get GeoscapeView.CurrentViewState
=> PhoenixPoint.Geoscape.View.ViewStates.UIStateResearch (h:4:21)
invoke GeoscapeView.ResetViewState [null]
=> {"ok":true,"void":true}
```

Conclusion: the nine UI checks do not need a new engine hook. A convenience `@view` that resolves
both phases would reduce a hop, but it is ergonomics, not capability.

## Gap 2 — REACHABLE

- Exact lever: `@geo -> Timing`, then set `Base.Core.Timing.Scale` and `Paused`. The root property is
  `GeoLevelController.Timing { get; private set; }` at
  `DEC/PhoenixPoint.Geoscape.Levels/GeoLevelController.cs:229`; elapsed campaign time is
  `ElaspedTime => Timing.Now - Timing.StartTime` at `:310`. `Timing.Now`, `Scale`, and `Paused` are at
  `DEC/Base.Core/Timing.cs:55`, `:79`, `:100`.
- The built-in console lever is exactly
  `GeoLevelController.SetSpeed(IConsole console, float scale)`, command `geo_speed`, and assigns
  `Timing.Scale = scale * 300f` (`DEC/PhoenixPoint.Geoscape.Levels/GeoLevelController.cs:1683`).
  Direct `call set` avoids the multiplier ambiguity.
- This is real simulation, not a calendar cosmetic. The geoscape starts an hourly scheduled
  callback at `DEC/PhoenixPoint.Geoscape.Levels/GeoLevelController.cs:761`; it runs research,
  faction income/bases/manufacturing and `HourTicked` at `:777-833`. At midnight it invokes the
  building/diplomacy/alien/mission daily pipeline at `:779-781` and `:836-868`.

Live proof from a fresh Easy campaign:

```text
.\ppcli.ps1 connect call '{"op":"get","target":"@geo","member":"Timing"}'
=> {"ok":true,"value":{"h":"h:4:15","type":"Base.Core.Timing"}}
.\ppcli.ps1 connect call '{"op":"get","target":{"$h":"h:4:15"},"member":"Scale"}'
=> {"ok":true,"value":3600.0}
.\ppcli.ps1 connect call '{"op":"get","target":{"$h":"h:4:15"},"member":"Paused"}'
=> {"ok":true,"value":true}
.\ppcli.ps1 connect call '{"op":"set","target":{"$h":"h:4:15"},"member":"Paused","value":false}'
=> {"ok":true,"set":"Paused"}
# wait 28 real seconds, then set Paused=true
.\ppcli.ps1 connect call '{"op":"invoke","target":{"$h":"h:4:31"},"member":"ToString","args":[]}'
=> {"ok":true,"value":"1.00:02:09.6000000"}
```

Thus the stock `Scale=3600` advanced one day, two minutes, 9.6 seconds in 28 real seconds, and the
endpoint remained healthy. Age once, pause, and save/snapshot the aged campaign; subsequent audits
should restore the fixture rather than pay the aging cost again.

- Do **not** teleport `StartTime` or choose an arbitrarily huge scale and assume every hour is
  replayed. The scheduler takes `Timing.Now` once (`DEC/Base.Core/TimingScheduler.cs:425`), dequeues
  overdue work at `:428-441`, and the hourly callback schedules its next run from the current
  `Timing` (`DEC/PhoenixPoint.Geoscape.Levels/GeoLevelController.cs:833`). A frame that jumps over
  multiple hours can therefore collapse them into one callback and miss midnight. `Scale` changes
  do reschedule updateables (`DEC/Base.Core/Timing.cs:79-95`), but that does not synthesize every
  skipped hour. Keep advancement below one in-game hour per rendered frame; the proved stock 3600
  setting is the conservative default.
- A separate `geo_speed 30` (`Scale=9000`) trial lost its endpoint before a post-read. It returned no
  `stale:true`, but the session ended, so I exclude it from the evidence and make no stability claim
  for 9000.

## Gap 3 — REACHABLE BUT COSTLY

### Awaiting events

- There is an event bus, but it is **not general**. `Base.Eventus.EventusManager` offers
  `RegisterHandler(Type, EventusHandler)`, `UnregisterHandler`, `RaiseEvent`, and `EventExecuted`
  (`DEC/Base.Eventus/EventusManager.cs:17-48`). `PlayEventDirect` invokes handlers for each
  `BaseEventData` and then `EventExecuted` (`:79-96`). It sees only definitions routed through
  Eventus.
- Important game lifecycle events bypass that bus. Tactical exposes ordinary CLR events such as
  `GameWrappingUpEvent`, `GameOverEvent`, and `NewTurnEvent` directly on
  `TacticalLevelController` (`DEC/PhoenixPoint.Tactical.Levels/TacticalLevelController.cs:299-303`);
  geoscape exposes `HourTicked` and `DailyUpdateEvent` directly
  (`DEC/PhoenixPoint.Geoscape.Levels/GeoLevelController.cs:340-342`). Therefore a single subscription
  to `EventusManager.EventExecuted` cannot implement “await any game event.”
- Current `call` cannot manufacture and retain an arbitrary delegate, and a pipe request cannot
  block Unity's main thread. A proper bridge primitive is still feasible but nontrivial: register a
  reflected CLR event handler, complete a cross-frame job on invocation, project its arguments, and
  **always unsubscribe** on success, timeout, cancellation, scene unload/epoch change, and server
  shutdown. Persistent conditions (`IsGameOver`, current state, research complete) should remain
  polling predicates. Transient events require that subscription primitive or an event-specific
  latch; polling alone can miss them.

### Ending a mission and reaching rewards

- The canonical win lever already exists: console `win` sets friendly factions to `Won`, enemies to
  `Defeated`, then calls `TacticalLevelController.GameOver()`
  (`DEC/PhoenixPoint.Tactical.Levels/TacticalLevelController.cs:1051-1065`, `:1092-1108`). `GameOver`
  raises wrapping-up, does cleanup/XP/telemetry, raises `GameOverEvent`, and calls mod teardown
  (`:825-843`). This is materially better than assigning `IsGameOver` or calling `FinishLevel`
  prematurely.
- `TacticalView.OnGameOver` enters the appropriate battle summary/cutscene
  (`DEC/PhoenixPoint.Tactical.View/TacticalView.cs:1062-1109`, `:1130-1136`). The callback that really
  leaves tactical is private `TacticalView.GoToGeoscape()`, which calls
  `PhoenixGame.FinishLevel(new TacticalGameResult { LocalPlayerFaction, Result,
  ContextHelpData, IsTutorial })` (`DEC/PhoenixPoint.Tactical.View/TacticalView.cs:1112-1120`).
  `PhoenixGame.FinishLevel(ILevelParams)` records the result and pulses the level monitor
  (`DEC/PhoenixPoint.Common.Game/PhoenixGame.cs:262-265`).
- The reward path exists only when tactical was launched from a real geoscape mission. Launch stores
  `_missionToComplete` before `FinishLevel(gameParams)`
  (`DEC/PhoenixPoint.Geoscape.Levels/GeoLevelController.cs:1412-1466`). On return, PhoenixGame loads
  the current geoscape (`DEC/PhoenixPoint.Common.Game/PhoenixGame.cs:647-672`), and geoscape calls
  `_missionToComplete.Complete(result)` then resets the view with `LastMission` and
  `CompletedMissionResult` (`DEC/PhoenixPoint.Geoscape.Levels/GeoLevelController.cs:683-713`). A
  synthetic `start-mission` is enough to test tactical UI, but not enough to prove reward UI.

Live proof used a real fresh-campaign scavenging mission:

```text
GeoSite.ActiveMission
=> PhoenixPoint.Geoscape.Entities.GeoScavengingMission (h:4:169)
GeoMission.MissionDef
=> OScavCratesALN_CustomMissionTypeDef, guid 5aae1387-9e60-5cd4-e8c0-6fbee58099c6
GeoMission.GetDefaultDeploymentSetup(GeoVehicle.Owner, GeoVehicle)
=> List<GeoCharacter>, count 7 (h:4:177)
new GeoSquad(h:4:177); GeoMission.Launch(squad)
=> {"ok":true,"void":true}
.\ppcli.ps1 connect state
=> {"ok":true,"phase":"tactical","scene":"SCV_OVR_PLT_56x56_B","level":"TacticalLevel(Clone)","levelState":"Playing"}

.\ppcli.ps1 connect console '{"command":"win","args":[]}'
=> {"ok":true,"output":[],"truncated":false}
get @tac.IsGameOver
=> {"ok":true,"value":true}
get @view.CurrentState
=> PhoenixPoint.Tactical.View.ViewStates.UIStateBattleSummary (h:29:210)
invoke @view.GoToGeoscape []
=> {"ok":true,"void":true}
.\ppcli.ps1 connect state
=> {"ok":true,"phase":"geoscape","scene":"Geoscape","level":"GeoscapeLevel(Clone)","levelState":"Playing"}
get GeoscapeView.CurrentViewState
=> PhoenixPoint.Geoscape.View.ViewStates.UIStateGeoModal (h:32:212)
get UIStateGeoModal.ModalType
=> {"$enum":"GeoScavengeOutcome","type":"PhoenixPoint.Common.Utils.ModalType"}
get UIStateGeoModal.ModalData
=> PhoenixPoint.Geoscape.Entities.GeoScavengingMission (h:32:216)
```

Cost: a general await verb needs bridge work and careful lifetime cleanup; a reward-screen test must
start from a real `GeoMission`. The mission-exit lever itself is already callable today.

## Gap 5 — REACHABLE

- Correct terminal entry: `TacticalActorBase.ApplyDamage(DamageResult damageResult)` at
  `DEC/PhoenixPoint.Tactical.Entities/TacticalActorBase.cs:950`. This is also the terminal path used
  by weapon damage: `DamageAccumulation.GenerateStandardDamageTargetData(...)` builds a
  `DamageResult` with `Source`, damage, force, hit, origin, and damage type
  (`DEC/PhoenixPoint.Tactical.Entities/DamageAccumulation.cs:381-422`), and
  `ApplyAddedDamage_Default()` eventually invokes `parent.ApplyDamage(damageResult)` (`:565-626`).
  Projectile impact feeds that accumulator at
  `DEC/PhoenixPoint.Tactical.Entities.Weapons/ProjectileLogic.cs:355-369`.
- Exact minimal lethal argument shape, matching the game's own cheats:

```csharp
new PhoenixPoint.Tactical.Entities.DamageResult {
    ArmorDamage = 0f,
    HealthDamage = 99999f,
    ImpactForce = Vector3.up * 200f,
    DamageOrigin = target.Pos,
    Source = damageSource
}
```

  Fields and types are authoritative at `DEC/PhoenixPoint.Tactical.Entities/DamageResult.cs:11-39`.
  The game's `damage` console constructs this shape at
  `DEC/PhoenixPoint.Tactical.Console/TacConsoleGameplay.cs:340-347`; `remove` uses lethal 99999 at
  `:520-540`. For natural kill attribution, use the weapon/ability/projectile source that produced
  the hit; for a dev kill, an actor or console source still runs the death pipeline.
- Why the audit workaround failed: health crossing zero calls `Die()`, constructs a **nonnull**
  `DeathReport`, reports actor death, then passes that report to the preferred ability
  (`DEC/PhoenixPoint.Tactical.Entities/TacticalActorBase.cs:616-628`). `RagdollDieAbility` casts
  `action.Param` to `DeathReport` and immediately reads its `ImpactForce`
  (`DEC/PhoenixPoint.Tactical.Entities.Abilities/RagdollDieAbility.cs:68-69`). Therefore
  `DieAbility.Activate(null)` fails because its parameter is null. “It wants a damage source” is not
  the correct diagnosis. The damage source is consumed earlier to populate `DeathReport.Killer`;
  force is copied from `CurrentDamageForce` (`DEC/PhoenixPoint.Tactical.Entities/DeathReport.cs:34-42`).
- The order matters: `ApplyDamageInternal` stores `LastDamageSource` and `CurrentDamageForce` before
  subtracting health (`DEC/PhoenixPoint.Tactical.Entities/TacticalActorBase.cs:844-875`), so the
  synchronous health-change/death callback sees a complete report.

PPCLI exact construction (a struct has no reflected constructor, so `op:new` refuses it at
`PPCLI/src/Reflect.cs:410-414`; box its default through `Activator`):

```text
call {"op":"invoke","type":"System.Activator","member":"CreateInstance",
      "args":[{"$type":"PhoenixPoint.Tactical.Entities.DamageResult"}],"sig":["System.Type"]}
=> DamageResult h:29:208
set h:29:208 HealthDamage=99999.0
set h:29:208 ArmorDamage=0.0
set h:29:208 ImpactForce={"$v3":[0,200,0]}
set h:29:208 DamageOrigin={"$v3":[-3.5,0.04748635,13.5]}
set h:29:208 Source={"$h":"h:29:197"}
invoke Fishman_14.ApplyDamage [{"$h":"h:29:208"}]
=> {"ok":true,"void":true}
```

Live outcome:

```text
before: get Fishman_14.IsDead => false
after 3 s: get Fishman_14.IsDead => true
get Fishman_14.LastDamageSource
=> TacticalActor Soldier_6 (h:29:209)
Player.log:
Applying damage 99999 HP + 0 ARM to actor "Fishman_14"...
Ability Fishman_Die_AbilityDef of type RagdollDieAbility activated ... Parameter: <PhoenixPoint.Tactical.Entities.DeathReport>.
Actor Fishman_14 died with force 200 from TacticalActor
```

No `RagdollDieAbility`/null exception followed. The log did say the source was an “unknown source”
and “Was it a cheat?” because the live probe supplied a `TacticalActor`, not its weapon. That is an
attribution warning, not a broken death: `IsDead=true` and the ragdoll completed. The actual-weapon
source shape is source-proved above; only the actor-source variant was proved live.

## Review of the cheap-gap plan

### Gap 9 — WRONG AS SHIPPED

- Sound core: explicit `-ProfileId` wins because `Invoke-Jobs` calls discovery only when it is empty
  (`PPCLI/ppcli.ps1:99`), and absent pin line 2 retains the zero/one/many profile behavior
  (`PPCLI/paths.ps1:166-183`). Invalid/missing pinned profiles refuse (`PPCLI/paths.ps1:151-163`).
- Wrong association: documentation says line 1 install + line 2 profile are “one fact”
  (`PPCLI/paths.ps1:32-35`), but an explicit **different** `-PPRoot` still calls
  `Find-PPProfileId` without an install/pin compatibility check (`PPCLI/ppcli.ps1:99`). On a real run
  `Find-PPProfileId` unconditionally consults line 2 (`PPCLI/paths.ps1:169-173`). Result: a pin for
  Instance2 can silently supply Instance2's profile default while `-PPRoot` selects another install.
  The tests cover pin fixtures but no “explicit other PPRoot ignores pinned profile” case
  (`PPCLI/tests/paths.tests.ps1:148-164`).
- Better rule: line 2 is eligible only when the effective `PPRoot` equals line 1 after canonical path
  comparison. Otherwise use ordinary discovery/refusal unless `-ProfileId` was explicit. Add the
  missing cross-install test. Also reject a third non-comment pin line instead of silently ignoring
  it (`Get-PPPinLines` currently returns all at `PPCLI/paths.ps1:38-41`).
- Stdout/epoch/deploy: no direct violation. Refusals still go through the client's JSON error path;
  no handles or DLL deployment are involved. The defect is selecting the wrong profile for an
  explicitly selected install.

### Gap 4 — WRONG DOCUMENTATION, SOUND LEVER

- `HarmonyLib.AccessTools.Field(Type,string)` / `.Property(Type,string)` through static `call` is the
  right no-DLL-change lever. The `{"$type":"..."}` binder is appropriate, and a null result is a
  useful absence answer.
- The paste-ready field example is not paste-ready. It names
  `PhoenixPoint.Home.View.ViewModules.UIModuleDeploymentMissionBriefing`
  (`PPCLI/PLAYBOOK.md:269`), but the authoritative namespace is
  `PhoenixPoint.Geoscape.View.ViewModules` at
  `DEC/PhoenixPoint.Geoscape.View.ViewModules/UIModuleDeploymentMissionBriefing.cs:11-13`.
- Live disagreement:

```text
AccessTools.Field(PhoenixPoint.Home.View.ViewModules.UIModuleDeploymentMissionBriefing, "_mission")
=> {"ok":false,"code":"overload","error":"... no type 'PhoenixPoint.Home.View.ViewModules.UIModuleDeploymentMissionBriefing'"}
AccessTools.Field(PhoenixPoint.Geoscape.View.ViewModules.UIModuleDeploymentMissionBriefing, "_mission")
=> {"ok":true,"value":null}
```

  The corrected type proves the intended absent-field result; the documented type only proves a
  typo. Fix the namespace. No stdout, epoch, or deploy-rule issue exists because this is one ordinary
  read-only `call`.

### Gap 6 — PARTLY SOUND; THE “WHOLE FIELD SET” CLAIM IS WRONG

- Sound: `@def:<exact-name-or-guid>` as a target and `{"$def":"..."}` as an argument remove the
  `find -> guid -> GetDef -> get` ceremony. The resolver tries GUID, then exact case-insensitive
  name, and refuses duplicates (`PPCLI/src/Reflect.cs:327-397`). Re-resolving the def per call also
  respects the epoch model better than persisting a handle.
- Cost: name lookup enumerates every def on every named call (`PPCLI/src/Reflect.cs:368-376`). That is
  correctness-safe but O(all defs) on the game thread. Cache only if runtime-def creation/clearing
  has an explicit invalidation story; otherwise the scan is the safer trade.
- Wrong/incomplete dump: `values:true` is **not** a whole field set. Its own implementation says and
  does “scalar instance fields only,” silently omitting non-scalars and read failures
  (`PPCLI/src/Reflect.cs:1221-1251`). It also omits null because `TryScalar(null)` returns false
  (`PPCLI/src/Reflect.cs:1041-1045`). Mechanical diffs will miss null-to-object changes, referenced
  defs, arrays/lists, nested payloads, and any field whose read threw. The TODO now narrows the claim
  to scalar fields (`PPCLI/TODO-audit-capabilities.md:134`), but that is a retreat from the stated
  “whole field set” requirement, not closure of it.
- Better shape: include every field name with either a scalar, explicit `null`, stable def/object
  identity (`type/name/guid`), bounded collection identities, or explicit markers such as
  `$omitted`, `$error`, `$cycle`, `$truncated`. Alternatively accept caller-specified field paths and
  return a value/error for every requested path. Silent omission is unacceptable for a mechanical
  diff because “unchanged” and “not observed” become indistinguishable.
- Stdout/epoch/deploy: name addressing is sound. Bounded structured dumps are required to stay under
  the 64-KiB reply limit; they must not recursively mint session handles into a supposedly stable
  cross-session diff.

### Gap 8 — PAGING SOUND; GENERIC IMPLICIT CONVERSION WRONG

- Sound: `inspect` now filters and pages the complete member list with `page`, `pageSize`, `total`,
  and `hasMore` (`PPCLI/src/Reflect.cs:1255-1311`). That fixes silent truncation without affecting
  handles or stdout framing.
- `ModifiableValue` itself explains the observed audit discrepancy: public fields are `BaseValue`
  and `ModificationValue`, `EndValue` adds them, and its implicit float operator returns `EndValue`
  (`DEC/Base.Entities.Statuses/ModifiableValue.cs:8-35`). Exposing that particular conversion on
  request is useful.
- I disagree with automatically executing arbitrary `op_Implicit` during **every projection**.
  `Project -> TryScalar -> TryInlineStruct -> TryImplicitScalar` does exactly that
  (`PPCLI/src/Reflect.cs:991-999`, `:1041-1072`, `:1087-1104`). It violates the surrounding safety
  invariant that projection reads fields and does not run arbitrary user code (`:1056-1059`). An
  operator may allocate, mutate, log, block, or throw after side effects; swallowing the exception
  does not undo those effects.
- The comment “the number C# would actually see” is false. `var x = wp.Value` sees the declared
  `ModifiableValue`; conversion occurs only in a target-type context. C# user-defined conversion can
  also be declared on the destination type, which this source-type-only scan cannot discover without
  knowing the requested destination. If two compatible operators return the same type, the code
  keeps the first reflection-order result instead of applying C# overload selection (`:1091-1101`).
- Better lever: make conversion opt-in and explicit, e.g. `get` with `"convertTo":"System.Single"`
  or a `convert` call naming the destination type. Return the ordinary inert struct projection plus
  a separately requested conversion result. Do not label an automatically chosen value `$scalar`.
  This preserves genericity and forces ambiguity to be resolved by the caller.
- Stdout/epoch/deploy: paging is safe. The conversion result is scalar and handle-free, but automatic
  operator execution adds game-thread behavioral risk unrelated to framing.

### Gap 7 — BETTER LEVER EXISTS, AND THE CURRENT COMMIT MOSTLY USES IT

- A literal persistent pipe connection is the wrong design. The server accepts one pipe instance
  (`PPCLI/src/PipeServer.cs:164-166`) and serves exactly one request before the connection is disposed
  (`PPCLI/src/PipeServer.cs:258-266`). Holding it open would require a DLL/protocol change and would
  monopolize the only server instance, obstructing status/cancel/other clients. That would trigger
  deploy safety for no gameplay benefit.
- The current commit correctly attacks the real cost: `connect multi` discovers one endpoint and
  runs N requests inside one PowerShell process (`PPCLI/ppcli.ps1:361-397`), while `Invoke-Pipe`
  deliberately opens/disposes a short connection for each request (`PPCLI/ppcli.ps1:234-258`). Keep
  this implementation. Fix the PLAYBOOK label “one process, one connection”
  (`PPCLI/PLAYBOOK.md:259`) to “one process, one endpoint; one short connection per request.”
- The one-JSON-object stdout contract holds in the live happy path: stderr printed install/pipe
  notes, then stdout contained one aggregate object:

```text
.\ppcli.ps1 connect multi '[{"id":"s","verb":"state"},{"id":"r","verb":"roots"}]'
=> {"ok":true,"count":2,"failed":0,"results":[{"id":"s","ok":true,"reply":...},{"id":"r","ok":true,"reply":...}]}
```

  Do not replace the aggregate with `Send-Verb` in a loop; `Send-Verb` serializes immediately
  (`PPCLI/ppcli.ps1:310-313`) and would emit N stdout objects.
- Prevalidate the entire request array before sending row 1. Current code discovers a missing
  `verb` only inside the execution loop (`PPCLI/ppcli.ps1:385-388`): row 1 may mutate the game, row 2
  can then make the invocation throw, and the aggregate loses row 1's result. Likewise, document
  that transport exceptions abort with prior side effects already committed; this is sequential,
  not transactional.
- Epoch semantics are acceptable: each call resolves roots live; old handles must still return an
  explicit stale-handle error after a scene transition. Reusing the endpoint token is not reusing an
  object handle.
- This is only a partial gap-7 solution. A fixed request array removes process startup, but it cannot
  express “iterate this live collection and accumulate a projection,” because later requests cannot
  bind handles/items produced by earlier replies. The TODO now admits that limitation
  (`PPCLI/TODO-audit-capabilities.md:159`). Add a bounded `foreach`/projection plan step or a client
  request language with reply references and an accumulator; do not call the original enumeration
  gap fully closed.

## Disagreements with the other engineer

- Gap 1's premise is obsolete: tactical `@view` already exists, and geoscape UI is reachable at
  `@geo.View`; no new engine root is required.
- Gap 5's failure is a null `DeathReport` parameter, not merely a missing damage source.
- Gap 9 incorrectly treats a pinned profile as the default even when explicit `-PPRoot` selects a
  different install.
- Gap 4's recommended lever is valid, but its paste-ready authoritative type name is wrong and fails
  live.
- Gap 6 did not implement a whole-field dump; it implemented a silent scalar subset.
- Gap 8 should not execute arbitrary user-defined conversions as an automatic side effect of reading
  a value.
- Gap 7 should mean one PowerShell process, not one persistent pipe. The current code made the right
  correction, but docs misdescribe it and live collection accumulation remains absent.
- Extremely large geoscape time jumps are not automatically safe: the hourly scheduler can collapse
  skipped hours rather than replay them.

## What I proved live vs only read

### Proved live on `D:\PP-Instance2`

- Fresh campaign cold-started to geoscape and answered normally; no `stale:true` result occurred.
- `@geo.View`, private `_statesStack`, `CurrentViewState`, `GeoscapeModules`, `CommonModules`, named
  UIModules, and a public transition into `UIStateResearch` were reachable through current `call`.
- Stock `Timing.Scale=3600`, unpaused for 28 real seconds, advanced `ElaspedTime` by
  `1.00:02:09.6000000`; the endpoint stayed healthy.
- A real `GeoScavengingMission` launched from geoscape with seven campaign characters and entered
  tactical scene `SCV_OVR_PLT_56x56_B`.
- `DamageResult -> TacticalActor.ApplyDamage` killed `Fishman_14`: `IsDead false -> true`, nonnull
  `DeathReport`, ragdoll completed, no null exception. The live `Source` was `Soldier_6`, not a
  weapon; the attribution warning is recorded above.
- `console win -> IsGameOver -> UIStateBattleSummary -> GoToGeoscape` returned to the campaign and
  staged the real `GeoScavengeOutcome` modal with the completed mission as data.
- The gap-4 documented Home namespace fails type binding; the corrected Geoscape namespace returns
  null for the absent `_mission` field.
- `connect multi` returned one aggregate JSON object for two requests on the current implementation.

### Source-proved, not live-proved

- Arbitrary concrete `ViewState` construction and direct private `StateStack.SwitchToState`; only
  the public `ToResearchState` transition was exercised live.
- Eventus coverage boundaries and the feasibility/cost of a generic reflected CLR-event await verb;
  no bridge event subscriber was written in this research-only task.
- Weapon `DamagePayload -> DamageAccumulation -> ApplyDamage` and natural weapon-source attribution;
  the terminal damage path was live, but the live source object was an actor.
- Speeds above stock 3600. The 9000 trial was inconclusive after session termination and is excluded.
- Cheap-gap implementation safety/correctness findings other than the two explicit live probes; they
  follow directly from the cited PPCLI source and tests. No source was edited and no commit was made.
