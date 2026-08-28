# PPCLI playbook — intent to command

**Front door. Find your intent, copy the line, run it. Do not dig the decompile first.**
Everything below is run from the PPCLI directory against a **running** game. Depth is in `docs/REFERENCE.md`.

## First run — four rules

1. **Arm it once.** `.\ppcli.ps1 deploy`, then create the marker the deploy line prints:
   `New-Item -ItemType File "<install>\Mods\PPBridge\ppcli-enabled"`. Without it the mod loads and
   does nothing. Enable `PPBridge` in the in-game mod manager too. Then
   `.\ppcli.ps1 index` once, to resolve casual names.
2. **Wait for the gate.** `.\ppcli.ps1 connect state` must ANSWER first. Querying a still-initialising
   game hangs for minutes and looks exactly like an engine bug.
3. **Keep two installs if you automate.** One install for automation — cold-launch it, kill it, spawn
   into it — and the install you actually play. `-PPRoot "<path>"` picks which one a command means;
   with no `-PPRoot` the install is discovered through Steam, which only works when there is exactly
   one — and the one it finds is the one you PLAY. Write the automation copy's path into
   `ppcli-install.txt` beside `ppcli.ps1` and that becomes the default instead; `deploy` then refuses
   any other install by name, and prints the `-Force` line if you meant it. Reach the install you play
   with `connect` only: never cold-launch or kill it, and think twice before anything that writes to a
   real save.
4. **`connect` needs a game already running; `run`/`batch` cold-launch one (~17 s).** Redeploy after
   every mod edit (`.\ppcli.ps1 deploy`) or the game silently runs the old DLL.

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

Select one of your soldiers in a live mission, then:

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

They diverge in practice — a 10-shot run read `targetHits` 6 against `hitsAnyActor` 9. Quote the
per-target pair when you mean the weapon.

`shots` counts **activations**, `projectiles` counts impacts: a burst weapon fires several per pull
of the trigger (`PX_AssaultRifle` answers 6), and `projectilesPerShot` is reported so the two do not
read as a contradiction. The strongest clean pass is 3 activations / 18 projectiles.

**`recovered` must be `0`, and a non-zero value FAILS the run** rather than printing figures.
It means another mod threw inside a projectile's flight and left it stuck; PPBridge released it and
re-threw the exception unchanged, so the mission stays playable and the throw still reaches the log
— but the numbers would have been measured across a repair, so they are withheld.

**A long volley can end in a refusal, and that is not a ceiling.** `shots` is 1..100 and the whole
range is accepted, but once actors start dying something throws inside `OnTrajectoryEnd` and the run
refuses by name. The stack is cut at the Harmony wrapper, so there is no mod to point at.

Dispersion is about the aim point (`GetWorkingPosition()`), not the enemy's feet; `enemyFeet` is
reported too. **`spread:0` must cluster far tighter than a stock run**; measured over 5 shots at
~10 m: stock `mean 0.144 m`, zero-spread `mean 0.0015 m`.

The plan needs `weaponDef` to be unambiguous. `"PX_AssaultRifle"` is **refused** locally — it matches
the rifle *and* its ammo clip — which is the point: an ambiguous name would measure the clip.

| It said | What happened |
|---|---|
| `assert-target ... predicate was still false` | no line of sight — the enemy landed behind cover. Re-run, or change `distance`. |
| `landed ... still false after 20000 ms` | a shot did not produce a projectile. Common on a long volley once the target starts dying. Re-run, or ask for fewer shots — there is no volley-length ceiling to work around. |
| `assert-no-recovery` failed | `recovered` was non-zero: another mod threw inside a projectile's flight. The figures are withheld on purpose; re-run without that mod. |
| `assert-shots-at-most-100` / `assert-shots-at-least-1` failed | `shots` is 1..100. It is refused up front, never truncated into a short volley reported as `ok`. |
| `assert-one-weapon` failed | the name matched more than one `WeaponDef`. Name it exactly. |

Read the observer on its own with `.\ppcli.ps1 connect observe '{"action":"status"}'`.

## Cold start — from the main menu, with nothing played

Everything below starts at the HomeScreen of a game that has never had a campaign. No save, no
setup. Verified in-game 2026-08-28. They also run from a game that is mid-campaign — see the
geoscape note under the table.

| Intent | Command |
|---|---|
| launch ANY map as a playable mission | `.\ppcli.ps1 plan .\plans\start-mission.json '{"scene":"ALN_PLT_Nest_48x48_A","seed":12345}'` |
| start a real campaign (geoscape) | `.\ppcli.ps1 plan .\plans\start-campaign.json '{"difficultyIndex":1}'` |
| build a mission: my map, my roster, my enemies | `.\ppcli.ps1 plan .\plans\build-mission.json '{"scene":"ALN_PLT_Nest_48x48_A","playerCount":2,"playerCharacterDefName":"PX_Assault1_CharacterTemplateDef"}'` |
| the same, with NO player squad at all | `.\ppcli.ps1 plan .\plans\build-mission.json '{"scene":"ALN_PLT_Nest_48x48_A","playerCount":0}'` |
| fire a named geoscape event on demand | `.\ppcli.ps1 plan .\plans\fire-event.json '{"eventId":"PROG_PU1"}'` |

- `scene` is a MapPlotDef's own scene name — 213 plots ship, and `ALN_PLT_Nest_48x48_A_PlotDef`
  means `"scene":"ALN_PLT_Nest_48x48_A"`. Find one with
  `.\ppcli.ps1 connect find '{"query":"PlotDef","pageSize":50}'`, then read `Scene.SceneName` off it.
- `start-mission` takes ~12 s and reports a per-faction actor census. `start-campaign` takes ~15 s
  and reports the starting base and squad it generated.
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
| add resources (a DELTA, geoscape) | `.\ppcli.ps1 plan .\plans\set-resources.json '{"resource":"Materials","amount":500}'` |
| complete a research (geoscape) | `.\ppcli.ps1 plan .\plans\unlock-research.json '{"researchId":"fishman research"}'` |

Every plan takes the vars listed in its own file's `vars` block; the ones above are the ones worth
naming. Distance is a search constraint — the plan reports what it ACHIEVED, per actor.

## Ask the game something

| Intent | Command |
|---|---|
| what is loaded right now | `.\ppcli.ps1 connect state` |
| the live entrances (`@tac`, `@map`, `@selected`, …) | `.\ppcli.ps1 connect roots` |
| find a def by name | `.\ppcli.ps1 connect find '{"query":"Crabman"}'` |
| enumerate the def repository, one page | `.\ppcli.ps1 connect find '{"all":true,"page":0,"pageSize":200}'` |
| any of the 344 native console commands | `.\ppcli.ps1 connect console '{"command":"info","args":[]}'` |
| a console VARIABLE (`console` cannot reach these) | `.\ppcli.ps1 connect var '{"name":"ai_enabled","value":"false"}'` |
| watch where shots land (start / stop / read) | `.\ppcli.ps1 connect observe '{"action":"start"}'` · `'{"action":"read","aim":[0,0,0]}'` · `'{"action":"stop"}'` |
| read a live field / call a method | `.\ppcli.ps1 connect call '{"op":"get","target":"@selected","member":"Pos"}'` |
| what members does this handle have | `.\ppcli.ps1 connect inspect '{"h":"HANDLE"}'` |
| page a collection handle (`page` is 0-based) | `.\ppcli.ps1 connect items '{"h":"HANDLE","pageSize":20}'` |
| save / reload a named state | `.\ppcli.ps1 connect snapshot '{"name":"before"}'` · `.\ppcli.ps1 connect restore '{"name":"before"}'` |
| wait for a mission to be playable | `.\ppcli.ps1 connect wait '{"ready":true,"timeoutMs":120000}'` |
| a long plan is still running | `.\ppcli.ps1 connect status '{"jobId":"JOB_ID"}'` |

`SAVE_NAME`, `SNAPSHOT_NAME`, `HANDLE` and `JOB_ID` are **placeholders, not values**: every other line
above is paste-ready as written. `HANDLE` looks like `h:3:17` and comes out of a `find`/`call` reply
in THIS session (a handle from a previous one is dead); `JOB_ID` is the `jobId` the plan reply
carried; the save and snapshot names are whatever exists in the install you are driving.

## When it goes wrong

- **`REFUSED: no live PPBridge endpoint`** — no game is running with the endpoint armed. `run`
  cold-launches one; the mod also has to be ticked on once in that install's profile, and
  `Mods\PPBridge\ppcli-enabled` has to exist (see rule 1).
- **`REFUSED: no Phoenix Point install found` / `N installs found`** — Steam discovery could not
  decide. Pass `-PPRoot "<install folder>"`.
- **`REFUSED: '<x>' matches N defs`** — the candidates are on stderr. Name one exactly, or add an alias.
- **A plan reported FAILED but the work happened** — read the `trace`: `reset-selection` is cosmetic
  and carries `onError: continue`. Never re-run a spawn on a bare `FAILED`.
- **`"status":"timeout"`** — the CLIENT gave up (300 s) and cancelled the job, so its `finally` ran.
  Raise `-TimeoutSeconds`, or poll `connect status` with the `jobId`.
- **`stale:true`** — the session is running an older DLL. Every result in that run is a ghost:
  `.\ppcli.ps1 deploy` and start over.

Output contract: **exactly one compact JSON object on stdout**, everything else on stderr, so
`.\ppcli.ps1 connect state | ConvertFrom-Json` always works.
