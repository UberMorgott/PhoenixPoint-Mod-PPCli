# PPCLI playbook — intent to command

**Front door. Find your intent, copy the line, run it. Do not dig the decompile first.**
Everything below is run from the PPCLI directory against a **running** game. Depth is in `docs/REFERENCE.md`.

## First run — five rules, in this order

1. **Arm it once.** `.\ppcli.ps1 deploy`, then create the marker the deploy line prints:
   `New-Item -ItemType File "<install>\Mods\PPBridge\ppcli-enabled"`. Without it the mod loads and
   does nothing. Then launch the game **with `-mods`** — no `-mods`, no mods at all — and enable
   `PPBridge` once in the in-game mod manager.
2. **Wait for the gate.** `.\ppcli.ps1 connect state` must ANSWER first. Querying a still-initialising
   game hangs for minutes and looks exactly like an engine bug.
3. **Then `index`, once.** `.\ppcli.ps1 index` pages the LIVE repository into `catalog\`, so it needs
   the gate to have answered. It is what makes casual names like `"defName":"crabman"` resolve.
4. **Keep two installs if you automate.** One install for automation — cold-launch it, kill it, spawn
   into it — and the install you actually play. `-PPRoot "<path>"` picks which one a command means;
   with no `-PPRoot` the install is discovered through Steam, which only works when there is exactly
   one — and the one it finds is the one you PLAY. Write the automation copy's path into
   `ppcli-install.txt` beside `ppcli.ps1` (one line, gitignored) and that
   becomes the default instead; `deploy` then refuses
   any other install by name, and prints the `-Force` line if you meant it. Reach the install you play
   with `connect` only: never cold-launch or kill it, and think twice before anything that writes to a
   real save.
5. **`connect` needs a game already running; `run`/`batch` cold-launch one** (~17 s to a menu answer
   on the machine this was measured on). A game YOU launched by hand is fine — the mod publishes its
   endpoint whenever it is enabled and armed, whoever started the process. Redeploy after every mod
   edit (`.\ppcli.ps1 deploy`) or the game silently runs the old DLL.
   **`deploy` refuses an install whose game process is running** — checked before build and copy,
   matched by the running process's executable path against the resolved `-PPRoot` (never by process
   name), so a game running in the OTHER install does not block the deploy. `-AllowRunning` downgrades
   the refusal to a warning and proceeds (for staging files for the next launch); `-Force` is a
   separate switch that overrides the pinned install, not the running-process guard.
   **`run` and `batch` are not free**: they restore `Options.jopt` byte-exact afterwards, discarding
   any setting changed during that session, and they delete the run's log before launching.
   `connect` does neither.

## Names — say it plainly, the client resolves it

`defName`, `itemName` and `researchId` are resolved LOCALLY before the plan is sent:
exact def name (or research id) → exact alias → unique substring → **refuse with candidates**.
Ambiguous never guesses. So `"defName":"crabman"` is enough.

| Intent | Command |
|---|---|
| build the name catalog (ONE TIME, needs a running game) | `.\ppcli.ps1 index` |
| add a casual name | edit `catalog\aliases.ndjson` (curated, committed) |

Without `catalog\defs.ndjson` names are passed through untouched and a warning says so — exact def
names keep working, casual ones do not.

## I built a weapon — show me it firing, and give me numbers

**Needs a tactical mission that is PLAYING, and a shooter that is a SOLDIER.** Either select one of
your soldiers in the game, or pass an explicit handle as `"shooter"` — see *Getting a handle* below.
The plan does **not** spawn the shooter; use `spawn-at-coordinate.json` first if the mission has none.

**`start-mission` leaves a VEHICLE selected**, so the default `"shooter":"@selected"` walks straight
into `assert-enabled` reporting `NoSuitableEquipment` — an armoured car cannot hold an assault rifle.
Pick a soldier's handle by its `GameTags` (below) and pass it explicitly.

| Intent | Command |
|---|---|
| test a weapon at 10 m, 5 shots | `.\ppcli.ps1 plan .\plans\weapon-test.json '{"weaponDef":"PX_AssaultRifle_WeaponDef","enemyDef":"crabman","distance":10.0,"shots":5}'` |
| a longer volley — `shots` is 1..100, but read the two notes below the output first | `.\ppcli.ps1 plan .\plans\weapon-test.json '{"weaponDef":"PX_AssaultRifle_WeaponDef","enemyDef":"crabman","distance":10.0,"shots":20}'` |
| the same, with dispersion switched OFF (the control) | `.\ppcli.ps1 plan .\plans\weapon-test.json '{"weaponDef":"PX_AssaultRifle_WeaponDef","distance":10.0,"shots":5,"setSpread":true,"spread":0}'` |
| the same placement every time | add `"seed":13` |
| fire while it is NOT your turn | add `"shooter":"HANDLE"` — `@selected` is null during another faction's turn |
| just watch the tracers | run any of the above and look at the game; the enemy and the weapon are left in place |

You get back every impact point with what it hit, charges used, `dispersion` — `mean` / `sigma` /
`max` of the group, about its own centroid and about the aim point — and **two families of hit
figures, which are not the same question**:

- **per-TARGET**, the weapon's actual score: `targetHits` / `targetMisses` / `targetHitRate` /
  `damageOnTarget`, keyed on the aimed-at actor's instance id.
- **any-ACTOR**, everything a projectile touched, bystanders and the shooter included:
  `hitsAnyActor` / `misses` / `hitRateAnyActor` / `damageOnActors` / `damageTotal`.

They diverge in practice — one measured 10-shot run read `targetHits` 6 against `hitsAnyActor` 9.
Quote the per-target pair when you mean the weapon.

`shots` counts **activations**, `projectiles` counts impacts: a burst weapon fires several per pull
of the trigger — a product, `GetNumberOfShots(attackType) * ProjectilesPerShot`, which for
`PX_AssaultRifle` is 6 × 1 — and `projectilesPerShot` is reported so the two do not
read as a contradiction.

Impacts live in a ring of **512** and `observe read` lists at most **200** rows, oldest dropped from
the listing. The output says which is which: `projectiles` is everything that landed, `stored` is what
the ring still held, `dropped` is what it overwrote, `returned` is how many rows the listing carries.
Every statistic is over `stored` — so **`dropped` must be `0`, and a non-zero value FAILS the run**.
`returned` trims the listing only and changes nothing.

**`recovered` must be `0`, and a non-zero value FAILS the run** rather than printing figures.
Something threw inside the damage chain and left a projectile stuck; PPBridge released it and
re-threw the exception unchanged, so the mission stays playable and the throw still reaches the log
— but the numbers would have been measured across a repair, so they are withheld. The stack is cut
at the Harmony wrapper: `recovered` says that something threw, never **who**. Do not blame a mod.

**A long volley can end in a refusal.** `shots` is a whole number in 1..100 and the whole range is
accepted, but accepted is not answerable: 100 activations of a 6-projectile burst is 600 impacts
against a 512-entry ring, so a max-length volley fails `assert-nothing-dropped` by construction, and
once actors start dying something throws inside `OnTrajectoryEnd` and the run refuses by name.

Dispersion is about the aim point (`GetWorkingPosition()`), not the enemy's feet; `enemyFeet` is
reported too. **`spread:0` must cluster far tighter than a stock run.** Measured here over 5 shots at
~10 m: stock `mean 0.144 m`, zero-spread `mean 0.0015 m` — an example of the shape to expect, not a
figure your run will reproduce.

The plan needs `weaponDef` to be unambiguous. `"PX_AssaultRifle"` is **refused** locally — it matches
the rifle *and* its ammo clip — which is the point: an ambiguous name would measure the clip.

| It said | What happened |
|---|---|
| `assert-target ... predicate was still false` | no line of sight — the enemy landed behind cover. Re-run, or change `distance`. |
| `landed ... still false after 20000 ms` | a shot did not produce a projectile. Common on a long volley once the target starts dying. Re-run, or ask for fewer shots — there is no volley-length ceiling to work around. |
| `assert-no-recovery` failed | `recovered` was non-zero: something threw inside a projectile's flight and the bridge had to release it. The figures are withheld on purpose. The stack is cut at the Harmony wrapper, so the source is not identifiable from the result — read the game log. |
| `assert-shots-at-most-100` / `assert-shots-at-least-1` failed | `shots` is 1..100. It is refused up front, never truncated into a short volley reported as `ok`. |
| `assert-shots-integral` failed | `shots` must be a whole number — there is no half an activation. Refused before anything is touched. |
| `assert-nothing-dropped` failed | the volley overflowed the 512-impact ring, so every figure would have covered only the last 512 projectiles. Ask for fewer shots. |
| `assert-one-weapon` failed | the name matched more than one `WeaponDef`. Name it exactly. |
| `assert-enabled ... predicate was still false` | the shooter cannot take this shot, and the failure's `predicate.args` names the reason as the game's own `AbilityDisabledState.Key` — e.g. `["NotDisabled","NoSuitableEquipment"]` means the shooter is a VEHICLE, not a soldier. Pick a soldier and pass it as `shooter`. |

Read the observer on its own with `.\ppcli.ps1 connect observe '{"action":"status"}'`.

## Cold start — from the main menu, with nothing played

Everything below starts at the HomeScreen of a game that has never had a campaign. No save, no
setup. Verified in-game 2026-08-28. They also run from a game that is mid-campaign — see the
geoscape note under the table.

| Intent | Command |
|---|---|
| launch ANY map as a playable mission | `.\ppcli.ps1 plan .\plans\start-mission.json '{"scene":"ALN_PLT_Nest_48x48_A","seed":12345}'` |
| start a real campaign (geoscape) | `.\ppcli.ps1 plan .\plans\start-campaign.json '{"difficultyIndex":1}'` |
| build a mission: my map, my roster, my enemies | `.\ppcli.ps1 plan .\plans\build-mission.json '{"scene":"ALN_PLT_Nest_48x48_A","playerCount":2,"playerCharacterDefName":"PX_Assault1_Base_TacCharacterDef"}'` |
| the same, with NO player squad at all | `.\ppcli.ps1 plan .\plans\build-mission.json '{"scene":"ALN_PLT_Nest_48x48_A","playerCount":0}'` |
| fire a named geoscape event on demand | `.\ppcli.ps1 plan .\plans\fire-event.json '{"eventId":"PROG_PU1"}'` |

- These plans ask for 600-900 s and the client follows them: `plan` derives its own ceiling from the
  plan's `timeoutMs` + 60 s unless you pass `-TimeoutSeconds` yourself.
- `scene` is a MapPlotDef's own scene name. All 209 `MapPlotDef` rows in the catalog built here are
  named `<scene>_PlotDef`, so `ALN_PLT_Nest_48x48_A_PlotDef` means `"scene":"ALN_PLT_Nest_48x48_A"`.
  `find` returns only `{name, guid, type}`; to read the name off the def itself it is three calls:

  ```powershell
  .\ppcli.ps1 connect find '{"query":"_PlotDef","pageSize":50}'
  .\ppcli.ps1 connect call '{"op":"invoke","target":"@defs","member":"GetDef","args":["<guid>"]}'
  .\ppcli.ps1 connect call '{"op":"get","target":"<PLOT>","member":"Scene"}'
  .\ppcli.ps1 connect call '{"op":"get","target":"<SCENEREF>","member":"SceneName"}'
  ```
- Measured here: `start-mission` ~12 s, reporting a per-faction actor census; `start-campaign` ~15 s,
  reporting the starting base and squad it generated.
- **A running GEOSCAPE is left for you now.** These plans (and `load-mission`) no longer refuse when
  a campaign is open: they do what the game itself does before tearing a geoscape down
  (`GeoLevelController.cs:1406,:1444`) — set `GeoscapeView.UpdateStateStack=false`, call
  `GeoscapeView.ToLoadingState()`, then `FinishLevelAndGoToLobby`. `start-mission` reports
  `cameFrom:geoscape` when it came that way. Verified in ONE process: geoscape → menu → mission, two
  campaigns back to back, `build-mission` from a geoscape, and a geoscape save loaded from a live
  geoscape in 14.9 s. Leaving a TACTICAL mission was always fine and still is.
  The old wedge was never `GeoscapeView.Update` — `UpdateStateStack=false` alone, a different
  `MenuEnterReason` and deactivating the whole level GameObject each still hung. It is synchronous,
  in `Level.SetCurrentCrt` → `GeoLevelController.OnLevelEnd` → `GeoVehicle.OnExitPlay` →
  `GeoMap.UnRegisterVehicles` → `UIStateVehicleSelected.OnVehichleChanged` → `ResetViewState`, where
  `UIStateInitial` re-selects the vehicle and TFTV's `AircraftReworkMaintenance.GetMaintenanceFactor`
  NREs rebuilding the aircraft panel mid-teardown. `ToLoadingState` fixes it by leaving no vehicle
  selected.
- `build-mission` stops in DEPLOYMENT by design: an explicit player roster has to be placed. It
  reports `turnStarted:false`; pass `waitReady:true` only for a build that needs no placement.

## Tactical — the plans

**Every line below needs a tactical mission that is PLAYING** (`connect state` → `"phase":"tactical"`,
`"levelState":"Playing"`). `spawn-squad` and `situation` additionally need a **selected actor** to
measure distances from, unless you pass `useCenter:true` with an explicit centre; `equip-actor` needs
an actor already in play. The geoscape lines at the end of the table need a live geoscape,
not a mission: `start-campaign.json` gets there from the main menu.

| Intent | Command |
|---|---|
| spawn 3 crabmen near my soldiers | `.\ppcli.ps1 plan .\plans\spawn-squad.json '{"defName":"crabman","count":3,"minDistance":9.0,"maxDistance":11.0}'` |
| spawn a different creature | `.\ppcli.ps1 plan .\plans\spawn-squad.json '{"defName":"chiron","count":1}'` |
| spawn a squad around an exact point | `.\ppcli.ps1 plan .\plans\spawn-squad.json '{"defName":"crabman","count":3,"useCenter":true,"centerX":0.0,"centerY":0.0,"centerZ":0.0}'` |
| put ONE actor on ONE exact coordinate | `.\ppcli.ps1 plan .\plans\spawn-at-coordinate.json '{"defName":"crabman","faction":"alien","x":11.5,"z":-4.5}'` |
| give the selected actor a weapon | `.\ppcli.ps1 plan .\plans\equip-actor.json '{"actor":"@selected","itemName":"assault rifle"}'` |
| load a savegame and wait for the turn | `.\ppcli.ps1 plan .\plans\load-mission.json '{"name":"SAVE_NAME"}'` |
| load a GEOSCAPE save | `.\ppcli.ps1 plan .\plans\load-mission.json '{"name":"SAVE_NAME","phase":"geoscape","waitReady":false}'` |
| restore a snapshot, then place an equipped composition | `.\ppcli.ps1 plan .\plans\situation.json '{"snapshot":"SNAPSHOT_NAME","defName":"crabman","count":3,"itemName":"assault rifle"}'` |
| the same, on the mission already loaded | `.\ppcli.ps1 plan .\plans\situation.json '{"restoreFirst":false,"defName":"crabman","count":3}'` |
| run a cursor-scoped console command at a point | `.\ppcli.ps1 plan .\plans\aim-and-run.json '{"x":-0.5,"y":0.0,"z":14.5,"command":"info","cmdArgs":[]}'` |
| …and REFUSE unless the cursor really landed on an actor | add `"requireActor":true` |
| kill ONE named actor, cleanly, through the real damage pipeline | `.\ppcli.ps1 plan .\plans\kill-actor.json '{"actorName":"Crabman_10"}'` |
| …and attribute the kill to a killer | add `"source":"HANDLE"` |
| end the mission and go back to the geoscape | `.\ppcli.ps1 plan .\plans\end-mission.json '{"outcome":"win"}'` |
| age a live campaign — run the geoscape clock fast (geoscape) | `.\ppcli.ps1 plan .\plans\geo-fast-forward.json '{"forMs":60000}'` |
| add resources (a DELTA, geoscape) | `.\ppcli.ps1 plan .\plans\set-resources.json '{"resource":"Materials","amount":500}'` |
| complete a research (geoscape) | `.\ppcli.ps1 plan .\plans\unlock-research.json '{"researchId":"fishman research"}'` |

Every plan takes the vars listed in its own file's `vars` block; the ones above are the ones worth
naming. Distance is a search constraint — the plan reports what it ACHIEVED, per actor.

### Getting a handle — how to name an actor without touching the UI

`@selected` is whatever the human selected in the game, and it is **null during another faction's
turn**. Four calls get a real actor handle instead:

```powershell
.\ppcli.ps1 connect call '{"op":"get","target":"@tac","member":"Factions"}'      # -> <FACTIONS>
.\ppcli.ps1 connect items '{"h":"<FACTIONS>","page":0,"pageSize":10}'           # rows are TacticalFaction
.\ppcli.ps1 connect call '{"op":"get","target":"<FACTION>","member":"TacticalActors"}'  # -> <ROSTER>
.\ppcli.ps1 connect items '{"h":"<ROSTER>","page":0,"pageSize":40}'             # rows carry h, type, name
```

`get <FACTION> TacticalFactionDef` says which side it is (`Phoenix_TacticalFactionDef`,
`Alien_TacticalFactionDef`, …). Or skip the first two lines and read `@faction.TacticalActors` — the
faction currently on turn.

**`TacticalActors`, not `Actors`.** `Actors` yields `TacticalActorBase`, so a real page opened with
`TacticalDeployZone` and `TacticalExitZone` entries before the first fighter. `TacticalActors`
(`TacticalFaction.cs:70`) is the same enumeration already filtered to `TacticalActor`, so the zones
never appear and there is nothing to skip past.

**A `TacticalActor` is not necessarily a SOLDIER, and the first one usually is not.** On a real
`start-mission` roster the first row was `NJ_Armadillo_1` — an armoured car — and the four soldiers
followed it. It is also what the game leaves SELECTED, so `@selected` is that vehicle and a plan that
hands it a rifle fails. Ask the game's own question rather than reading the name: a vehicle carries
`Vehicle_TagDef` in its `GameTags` and a soldier does not (`CharacterTemplateExtension.cs:36` is the
game testing exactly this, `GameTags.Contains(SharedGameTags.VehicleTag)`):

```powershell
.\ppcli.ps1 connect call '{"op":"get","target":"<ACTOR>","member":"GameTags"}'  # -> <TAGS>
.\ppcli.ps1 connect items '{"h":"<TAGS>","page":0,"pageSize":20}'              # Vehicle_TagDef present?
```

Read live on one roster: `NJ_Armadillo_1` → `…, Vehicle_TagDef, Vehicle_ClassTagDef, …`;
`Soldier_2` → `Organic_SubstanceTypeTagDef, Human_TagDef, Technician_ClassTagDef, …` and **no**
`Vehicle_TagDef`. Do NOT test `Vehicle` (`TacticalActorBase.cs:222`) — it answers with a
`VehicleComponent` for a plain soldier too, so it separates nothing.

`TacticalActors` is a lazy iterator, so it projects with **no `count` and no `collection:true`** —
`items` still pages it, and a missing `count` does not mean "not a collection".
Pass a row's `h` wherever a plan takes an actor: `"shooter":"h:3:17"`, `"actor":"h:3:17"`.

### The open screen — reading and changing it

`roots` names the live UI the same way it names the level. Three roots, both phases:

| root | tactical | geoscape |
|---|---|---|
| `@view` | `TacticalView` | `GeoscapeView` |
| `@viewstate` | `TacticalView.CurrentState` | `GeoscapeView.CurrentViewState` |
| `@modules` | `TacticalModulesData` | `GeoscapeModulesData` |

That is enough to answer "does this screen render what the patch claims": `@viewstate` says which
screen is open, `@modules` reaches the named module holders (`ModalModule`,
`DeploymentMissionBriefingModule`, `FactionDataTracker`, …), and ordinary `get`/`inspect` read the
text a module is showing.

To CHANGE screen, invoke the view's **own public `To*State()` method** —
`ToResearchState`, `ToDiplomacyState`, `ToPhoenixpediaState`, `ToBaseLayoutState`,
`ToManufacturingState`, `ToMemorialState`, `ToRosterState`, … and `ResetViewState(null)` to go back:

```powershell
.\ppcli.ps1 connect call '{"op":"invoke","target":"@view","member":"ToResearchState","args":[]}'
.\ppcli.ps1 connect roots       # roots.viewstate.type -> ...ViewStates.UIStateResearch
.\ppcli.ps1 connect call '{"op":"invoke","target":"@view","member":"ResetViewState","args":[null]}'
```

**Never re-Enter a popped UI state, and never push onto `_statesStack` by hand.** `@viewstate` is
re-read from the live view on every `roots` call precisely so nobody holds one: a handle to a state
the stack has already popped still resolves, and entering it is how this project has wedged the
geoscape before. The `To*State()` methods each pick their own `StateStackAction`
(`GeoscapeView.cs:414-759`), which is the part a hand-rolled push gets wrong.

### Ageing a campaign — reaching a state nobody played to

A fresh `start-campaign` geoscape has no faction wars, no completed research and no excavations.
`geo-fast-forward` runs the real simulation instead of faking a date:

```powershell
.\ppcli.ps1 plan .\plans\geo-fast-forward.json '{"forMs":60000}'
```

- **Rate, measured:** `Scale` 3600 for 30 real seconds advanced the campaign `1.00:01:37.2` — about
  **one in-game day per 28 real seconds**.
- **3600 is the ceiling this project has proved.** Higher is not automatically better: the hourly
  scheduler dequeues what is overdue in ONE pass, so a frame that jumps several hours collapses them
  into one callback and can skip midnight entirely. Keep it under one in-game hour per frame.
- The plan restores `Scale` **and** `Paused` in its `finally`, whatever happens.
- Ageing is the expensive part, so pay for it once: `connect snapshot '{"name":"aged"}'` afterwards
  and `restore` it in later runs.

## Ask the game something

| Intent | Command |
|---|---|
| what is loaded right now | `.\ppcli.ps1 connect state` |
| the live entrances (`@tac`, `@map`, `@view`, `@viewstate`, `@modules`, `@selected`, …) | `.\ppcli.ps1 connect roots` |
| what screen is open RIGHT NOW (either phase) | `.\ppcli.ps1 connect roots` → `roots.viewstate.type` |
| open a geoscape screen | `.\ppcli.ps1 connect call '{"op":"invoke","target":"@view","member":"ToResearchState","args":[]}'` |
| go back to the default geoscape screen | `.\ppcli.ps1 connect call '{"op":"invoke","target":"@view","member":"ResetViewState","args":[null]}'` |
| read a named UI module of the open screen | `.\ppcli.ps1 connect inspect '{"h":"@modules","filter":"*Module*"}'` |
| yield for a span of real time inside a plan | a step `{"verb":"wait","args":{"forMs":30000}}` |
| find a def by name | `.\ppcli.ps1 connect find '{"query":"Crabman"}'` |
| enumerate the def repository, one page | `.\ppcli.ps1 connect find '{"all":true,"page":0,"pageSize":200}'` |
| any of the 344 native console commands | `.\ppcli.ps1 connect console '{"command":"info","args":[]}'` |
| a console VARIABLE (`console` cannot reach these) | `.\ppcli.ps1 connect var '{"name":"ai_enabled","value":"false"}'` |
| start watching where shots land | `.\ppcli.ps1 connect observe '{"action":"start"}'` |
| read the impacts so far | `.\ppcli.ps1 connect observe '{"action":"read","aim":[0,0,0]}'` |
| stop watching (and unpatch) | `.\ppcli.ps1 connect observe '{"action":"stop"}'` |
| read a live field / call a method | `.\ppcli.ps1 connect call '{"op":"get","target":"@selected","member":"Pos"}'` |
| read one field OFF A DEF, by name, in one call | `.\ppcli.ps1 connect call '{"op":"get","target":"@def:Crabman_Gunner_TacCharacterDef","member":"Armor"}'` |
| dump a def's values — EVERY field, nulls and non-scalars marked (diff two runs to see what a mod changed) | `.\ppcli.ps1 connect inspect '{"h":"@def:Crabman_Gunner_TacCharacterDef","values":true}'` |
| the number the game's C# sees for a `ModifiableValue` (opt-in: it runs an operator) | `.\ppcli.ps1 connect call '{"op":"get","target":"HANDLE","member":"Value","convertTo":"System.Single"}'` |
| what members does this handle have | `.\ppcli.ps1 connect inspect '{"h":"HANDLE"}'` |
| …only the ones matching a name | `.\ppcli.ps1 connect inspect '{"h":"HANDLE","filter":"*Coefficient*"}'` |
| …the next page of them (400 per page) | `.\ppcli.ps1 connect inspect '{"h":"HANDLE","page":1}'` |
| page a collection handle (`page` is 0-based) | `.\ppcli.ps1 connect items '{"h":"HANDLE","pageSize":20}'` |
| is `X.Y` a **field**, a **property**, or absent? | `.\ppcli.ps1 connect call '{"op":"invoke","type":"HarmonyLib.AccessTools","member":"Field","args":[{"$type":"PhoenixPoint.Modding.SomeClass"},"_someField"]}'` |
| save a named state | `.\ppcli.ps1 connect snapshot '{"name":"before"}'` |
| reload it (issue-only — follow with a `wait`) | `.\ppcli.ps1 connect restore '{"name":"before"}'` |
| wait for a mission to be playable | `.\ppcli.ps1 connect wait '{"ready":true,"timeoutMs":120000}'` |
| capture a screenshot of the game | `.\ppcli.ps1 connect screenshot` |
| …to a chosen path (must be absolute) | `.\ppcli.ps1 connect screenshot '{"path":"C:\\Temp\\shot.png"}'` |
| a long plan is still running | `.\ppcli.ps1 connect status '{"jobId":"JOB_ID"}'` |
| MANY calls at once (ONE PowerShell process, N short connections) | `.\ppcli.ps1 connect multi '[{"id":"a","verb":"state"},{"id":"b","verb":"roots"}]'` |
| …the same, from a generated file | `.\ppcli.ps1 connect multi @requests.json` |

`connect multi` is **one PowerShell process and one endpoint, with one short connection per
request** — not one held-open pipe. It is **sequential, not transactional**: the whole array is
validated before row 1 is sent, but once sending starts a transport failure leaves every earlier
row's side effects committed.

`SAVE_NAME`, `SNAPSHOT_NAME`, `HANDLE` and `JOB_ID` are **placeholders, not values**: every other line
above is paste-ready as written. `HANDLE` looks like `h:3:17` and comes out of a `find`/`call` reply
in THIS session (a handle from a previous one is dead); `JOB_ID` is the `jobId` the plan reply
carried; the save and snapshot names are whatever exists in the install you are driving.

### "Does this member even exist?" — ask the live assembly, not the source

Reflection-shaped questions are answerable through `call` alone, with no screen open and no mission
loaded. `{"$type":"..."}` is the envelope that passes a **type** as an argument, so Harmony's own
lookups become one-line probes:

```powershell
# a FIELD:    non-null handle = it exists, null = it does not
.\ppcli.ps1 connect call '{"op":"invoke","type":"HarmonyLib.AccessTools","member":"Field","args":[{"$type":"PhoenixPoint.Geoscape.View.ViewModules.UIModuleDeploymentMissionBriefing"},"DeployButton"]}'
# the same type with a member it does NOT have answers "value":null - that is the absence result
.\ppcli.ps1 connect call '{"op":"invoke","type":"HarmonyLib.AccessTools","member":"Field","args":[{"$type":"PhoenixPoint.Geoscape.View.ViewModules.UIModuleDeploymentMissionBriefing"},"_mission"]}'
# a PROPERTY: same shape, .Property instead
.\ppcli.ps1 connect call '{"op":"invoke","type":"HarmonyLib.AccessTools","member":"Property","args":[{"$type":"..."},"Name"]}'
# the declared TYPE of the field that came back
.\ppcli.ps1 connect call '{"op":"get","target":"<FIELDINFO>","member":"FieldType"}'
```

`null` is the whole answer: a `Traverse.Create(x).Field("name")` in some mod that names a member the
live assembly does not have returns null at runtime and does nothing, silently. Three of an audited
mod's dead features were proved dead in one line each this way. `inspect`/`members` answer the same
question in bulk (`{"filter":"*Coefficient*"}`); `AccessTools` answers it about a **named** member,
including a private one, and says which kind it is.

## When it goes wrong

- **`REFUSED: '<path>' has Phoenix Point running (PID <n>)`** — `deploy` detected a game process
  whose executable lives inside the target install. The live process cannot hot-swap the DLL, so
  every result measured after such a deploy would be stale. Pass `-AllowRunning` to downgrade to a
  warning and stage the files for the next launch. Nothing is built and nothing is written otherwise.
- **`REFUSED: no live PPBridge endpoint`** — no game is running with the endpoint armed. `run`
  cold-launches one; the mod also has to be ticked on once in that install's profile, and
  `Mods\PPBridge\ppcli-enabled` has to exist (see rule 1).
- **`REFUSED: no Phoenix Point install found` / `N installs found`** — Steam discovery could not
  decide. Pass `-PPRoot "<install folder>"`.
- **`REFUSED: '<x>' matches N defs`** — the candidates are on stderr. Name one exactly, or add an alias.
- **A plan reported FAILED but the work happened** — read the `trace`: `reset-selection` is cosmetic
  and carries `onError: continue`. Never re-run a spawn on a bare `FAILED`.
- **A failed plan has NO `output`** — that is deliberate, not a bug. `outputWithheld` says why,
  `step` names the step, and `result` carries its DTO (a `wait` puts the offending value in
  `result.last`, and `result.predicate` is the assertion itself with its variables already
  substituted — for an `Equals("NotDisabled", "${KEY.value}")` shape the reason is right there in
  `predicate.args`). Figures from a run the plan itself refused are not a measurement.
- **The endpoint went quiet mid-session** — check `Mods\PPBridge\ppcli-enabled` still exists. It is
  re-read every few seconds; deleting it disarms a running game. Re-create it and relaunch.
- **`"status":"timeout"`** — the CLIENT gave up and cancelled the job, so its `finally` ran. For a
  `plan` the ceiling is already the plan's own `timeoutMs` + 60 s unless you passed `-TimeoutSeconds`
  yourself; raise it, or poll `connect status` with the `jobId`.
- **`stale:true`** — the session is running an older DLL. Every result in that run is a ghost:
  `.\ppcli.ps1 deploy` and start over.

Output contract: **exactly one compact JSON object on stdout**, everything else on stderr, so
`.\ppcli.ps1 connect state | ConvertFrom-Json` always works. **Exit code 1 for any `ok:false` or
non-`done` reply** — a refusal is now distinguishable from an empty-but-valid result by both the
payload shape and `$LASTEXITCODE`. Example: `connect items '{"pageSize":400}'` exits 1 with
`{"ok":false,"code":"args","error":"pageSize must be 1..200"}` and no `items` key at all;
`pageSize` is deliberately not clamped (a silent clamp makes a caller that pages by its requested
size skip records). An empty-but-valid sweep returns `"items":[]` with exit 0.

Every `file:line` on this page points into Phoenix Point's own **decompiled** assembly, which this
repository does not ship. Timings and figures quoted here were measured on the development machine;
they are shapes to expect, not guarantees.
