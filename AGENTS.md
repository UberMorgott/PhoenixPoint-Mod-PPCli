# PPCLI for coding agents

PPCLI drives a running Phoenix Point from PowerShell 7. For any question about **runtime** behaviour
— what a value actually is, whether a patch took effect, whether an ability fires — query the game
with PPCLI instead of reading decompiled source. Source tells you intent; PPCLI tells you what the
process did.

This page is self-contained: an agent that has read only this file can bootstrap the tool and drive
it. Depth lives in `docs/REFERENCE.md`; intent-to-command lives in `PLAYBOOK.md`.

Every invocation writes **exactly one compact JSON object to stdout** and diagnostics to stderr, so
`... | ConvertFrom-Json` always works. For live verbs the answer is under `.result`.

## Bootstrap — once per machine

Prerequisites: **PowerShell 7**, a **.NET SDK**, the **.NET Framework 4.7.2 targeting pack**, and a
Phoenix Point install. The reference assemblies are the ones the game already ships — no separate
download:

```powershell
$PPRoot = 'C:\Program Files (x86)\Steam\steamapps\common\Phoenix Point'   # holds PhoenixPointWin64.exe
Test-Path (Join-Path $PPRoot 'ModSDK\Assembly-CSharp.dll')                        # must be True
Test-Path (Join-Path $PPRoot 'PhoenixPointWin64_Data\Managed\UnityEngine.CoreModule.dll')   # True
```

If `ModSDK\` is missing (a stripped automation copy), build against another install with
`.\deploy.ps1 -PPRoot $PPRoot -RefRoot '<a full install>'`.

```powershell
# 1. build + install the mod. /p:PPRoot= is what the csproj needs; deploy.ps1 passes it for you.
.\ppcli.ps1 deploy -PPRoot $PPRoot

# 2. ARM the endpoint. deploy never creates this file; without it the mod loads and does nothing.
New-Item -ItemType File -Force (Join-Path $PPRoot 'Mods\PPBridge\ppcli-enabled')

# 3. ACTIVATE the mod, once. Launch with -mods, tick PPBridge (id com.morgott.PPBridge) in the
#    in-game mod manager, quit. This writes com.morgott.PPBridge into the profile's MOD_ACTIVATED.
Start-Process (Join-Path $PPRoot 'PhoenixPointWin64.exe') -ArgumentList '-mods'

# 4. launch again with -mods and leave it running. -mods is REQUIRED: without it no mod loads.
Start-Process (Join-Path $PPRoot 'PhoenixPointWin64.exe') -ArgumentList '-mods'

# 5. THE GATE. Nothing else may be sent until this answers.
.\ppcli.ps1 connect state -PPRoot $PPRoot | ConvertFrom-Json

# 6. build the local name catalog, once per game build, on a settled game.
.\ppcli.ps1 index -PPRoot $PPRoot
```

A game **you** launched by hand publishes an endpoint exactly like one `run` launched — the mod
writes `%LOCALAPPDATA%\ppcli\endpoints\<pid>.json` whenever it is enabled and armed. `connect`
finds it there. `REFUSED: no live PPBridge endpoint` means no game is running with the mod both
**enabled** and **armed**, not that hand-launching is unsupported.

If the profile directory `%USERPROFILE%\AppData\LocalLow\Snapshot Games Inc\Phoenix Point\Steam\`
holds more than one `<SteamID64>`, add `-ProfileId <SteamID64>` to `run` and `batch`. If Steam
discovery finds zero or several installs, `-PPRoot` is required on every command.

## Operating discipline

1. **Gate every session** with `connect state`. Querying a still-initialising game hangs for minutes
   and looks exactly like an engine bug.
2. **`index` comes after the gate**, never before: it pages the live repository.
3. **Redeploy after every mod edit** (`.\ppcli.ps1 deploy`) and restart the game, or the session runs
   the old DLL. `stale:true` in a `run` result means every figure in it is a ghost.
4. **Prefer a plan to a loop of `connect` calls.** Spawning one actor at a coordinate is 23 `call`
   round-trips by hand and one request as a plan, and a plan runs its `finally` cleanup on success,
   failure, timeout and cancellation.
5. **Two installs if you automate.** `run` and `batch` cold-launch and stop a process; point them at
   an automation copy. Reach the install a human plays with `connect` only, and treat anything that
   writes to a real save as needing explicit permission.
6. **Disarm when finished**: delete `Mods\PPBridge\ppcli-enabled`. That prevents the next launch from
   arming; it does not stop an endpoint already running — exit the game or disable the mod for that.

## Client commands

`-PPRoot $PPRoot` is shown on the first line only; add it to every command when discovery cannot
pick one install.

| Command | Shape |
|---|---|
| `deploy` | `.\ppcli.ps1 deploy -PPRoot $PPRoot` |
| `connect` | `.\ppcli.ps1 connect <verb> '<json args>'` — one verb against a running game |
| `run` | `.\ppcli.ps1 run <verb> '<json args>'` — cold-launches a game, one verb, stops it again |
| `batch` | `.\ppcli.ps1 batch .\jobs.json` — one cold launch for a JSON array of `{id,verb,args}` |
| `plan` | `.\ppcli.ps1 plan .\plans\<file>.json '<json vars>'` |
| `index` | `.\ppcli.ps1 index` |

`run` and `batch` are the fallback for when nothing is running. Two side effects to know before
using them: they **snapshot `Options.jopt` and restore it byte-exact afterwards**, so any setting
changed inside that session is discarded; and they **delete any existing log** at
`%TEMP%\ppcli-<install>-<pid>.log` before launching. `connect` does neither.

| Parameter | Default | What it does |
|---|---|---|
| `-PPRoot` | `ppcli-install.txt`, else Steam discovery | which install the command means |
| `-ProfileId` | the single profile directory | needed by `run`/`batch` when several profiles exist |
| `-TimeoutSeconds` | `300`, or a plan's own `timeoutMs` + 60 s | the client's own ceiling on a job; `plan` derives it when you do not pass one, so a long plan is not cancelled mid-run |
| `-InitTimeoutSeconds` | `90` | how long `run` waits for the mod to print its init line |
| `-PipeTimeoutSeconds` | `30` | ceiling on one pipe frame — a wedged game, not a slow one |
| `-FaultPattern` | empty = any **mod** stack frame | an exception whose stack matches it, logged while the client is waiting, ends the wait as `DEAD RUN` instead of running out the budget; pass a regex to narrow it to one mod |
| `-IgnoreLogFaults` | off | wait out the full budget even when the log faults |
| `-CatalogDir` | `.\catalog` | where `index` writes and `plan` resolves names from |
| `-Force` | off | `deploy` only: write into an install other than the pinned one |

## Live verbs

```powershell
.\ppcli.ps1 connect ping                     # {ok, protocol, build}
.\ppcli.ps1 connect state                    # {ok, phase, scene, level, levelState}
.\ppcli.ps1 connect roots                    # the live entrances
.\ppcli.ps1 connect console '{"command":"info","args":[]}'
.\ppcli.ps1 connect var '{"name":"ai_enabled"}'                  # read
.\ppcli.ps1 connect var '{"name":"ai_enabled","value":"false"}'  # write, then read back
.\ppcli.ps1 connect call '{"op":"get","target":"@selected","member":"Pos"}'
.\ppcli.ps1 connect call '{"op":"invoke","type":"System.Math","member":"Abs","args":[-7]}'
.\ppcli.ps1 connect call '{"op":"new","type":"System.Text.StringBuilder","args":["PPCLI"]}'
.\ppcli.ps1 connect call '{"op":"set","type":"UnityEngine.Time","member":"timeScale","value":1.0}'
.\ppcli.ps1 connect find '{"query":"Crabman","type":"PhoenixPoint.Tactical.Entities.TacActorDef"}'
.\ppcli.ps1 connect find '{"all":true,"page":0,"pageSize":200}'  # the whole repository, paged
.\ppcli.ps1 connect types '{"pattern":"TacticalActor"}'
.\ppcli.ps1 connect members '{"type":"PhoenixPoint.Tactical.Entities.TacticalActor","filter":"Health"}'
.\ppcli.ps1 connect inspect '{"h":"HANDLE"}'
.\ppcli.ps1 connect items '{"h":"HANDLE","page":0,"pageSize":20}'   # page is 0-BASED
.\ppcli.ps1 connect release '{"h":"HANDLE"}'
.\ppcli.ps1 connect wait '{"ready":true,"timeoutMs":120000}'
.\ppcli.ps1 connect observe '{"action":"start"}'
.\ppcli.ps1 connect observe '{"action":"status"}'
.\ppcli.ps1 connect observe '{"action":"read","aim":[0,0,0]}'
.\ppcli.ps1 connect observe '{"action":"stop"}'
.\ppcli.ps1 connect snapshot '{"name":"before-test","timeoutMs":30000}'
.\ppcli.ps1 connect restore '{"name":"before-test"}'   # issue-only; follow with a wait
.\ppcli.ps1 connect status '{"jobId":"JOB_ID"}'
.\ppcli.ps1 connect cancel '{"jobId":"JOB_ID"}'
```

`HANDLE` (`h:3:17`) and `JOB_ID` are placeholders — take them from a reply in **this** session.
Handles die on scene unload and on process restart, and expire after 900 s.

Root aliases usable as a `call` `target`, re-resolved live on every request: `@game`, `@phoenix`,
`@defs`, `@level`, `@geo`, `@tac`, `@map`, `@view`, `@faction`, `@selected`. A wrong-phase alias
answers `null`, which is a different answer from "no such alias".

**What `call` cannot do**, so you do not spend a round-trip finding out: it has only `new` / `get` /
`set` / `invoke`, so **events cannot be subscribed to**; by-ref, `out` and pointer parameters are
**refused**; indexers are reachable only as `invoke get_Item` / `set_Item`; and two overloads that
bind equally well are **refused as ambiguous** — when the tied declarations have identical parameter
types even `sig` cannot separate them, which makes e.g. `Ability.Activate(object)` unreachable.

## Getting a target — how to obtain an actor handle

Several plans need a shooter or an anchor. `@selected` is whatever the human selected in the game,
and it is **null during another faction's turn**. To get a handle without touching the UI:

```powershell
# 1. the factions of the running mission
.\ppcli.ps1 connect call '{"op":"get","target":"@tac","member":"Factions"}'      # -> <FACTIONS>
.\ppcli.ps1 connect items '{"h":"<FACTIONS>","page":0,"pageSize":10}'           # each row is a TacticalFaction

# 2. which one is which - the def name says it (Phoenix_TacticalFactionDef, Alien_TacticalFactionDef, ...)
.\ppcli.ps1 connect call '{"op":"get","target":"<FACTION>","member":"TacticalFactionDef"}'

# 3. that faction's actors, and pick one
.\ppcli.ps1 connect call '{"op":"get","target":"<FACTION>","member":"Actors"}'  # -> <ROSTER>
.\ppcli.ps1 connect items '{"h":"<ROSTER>","page":0,"pageSize":40}'             # -> h + type + name per row
```

Or skip step 1 and read `@faction.Actors` directly, which is the faction currently on turn.

**`Actors` yields `TacticalActorBase`, so the first rows are ZONES, not soldiers** — a live page
opened with `TacticalDeployZone "Deploy_Player_1x1_Elite_Grunt_Drone"` and
`TacticalExitZone "PlayerExitZone"` before reaching
`TacticalActor "NJ_Armadillo_1"`. Take the first row whose `type` is exactly
`PhoenixPoint.Tactical.Entities.TacticalActor`; page with `page`/`pageSize` while `hasMore` is true.

`Actors` is also a lazy iterator: it projects as a plain handle with **no `count` and no
`collection:true`**, and `items` still pages it correctly. A missing `count` does not mean "not a
collection". Pass the chosen row's `h` wherever a plan takes an actor — `"shooter":"h:3:17"`,
`"actor":"h:3:17"`.

## Names — say `crabman`, not `Crabman_Gunner_TacCharacterDef`

`index` writes `catalog\defs.ndjson` from **your** running game; nothing usable ships in the repo,
because a catalog carries the mods and the game build of the install that produced it. `plan`
resolves `defName`, `itemName` and `researchId` **locally, before the request is sent**: exact def
name (or exact research id) → exact alias → unique substring → **refuse with the candidates on
stderr**. Ambiguity never guesses. With no catalog the value is passed through with a warning, so
exact def names work on a fresh clone and casual ones do not.

`researchId` is `ResearchDef.Id`, matched exactly. It usually equals the def name but not always,
which is why `index` records it separately; `console research_stats` lists them.

## Plans

Each file's own header (`"//needs"`, `"//run"`) is authoritative. Prerequisites are not optional —
a plan run in the wrong phase fails at its first step.

| Plan | Needs | Key vars |
|---|---|---|
| `start-mission.json` | nothing; works from the HomeScreen | `scene` `seed` `tags` |
| `start-campaign.json` | nothing; works from the HomeScreen | `difficultyIndex` |
| `build-mission.json` | nothing; works from the HomeScreen | `scene` `playerCount` `missionTypeDefName` `playerFactionDefName` `playerCharacterDefName` |
| `fire-event.json` | a live **geoscape** | `eventId` |
| `set-resources.json` | a live **geoscape** | `resource` `amount` (a **delta**) |
| `unlock-research.json` | a live **geoscape** | `researchId` |
| `load-mission.json` | a savegame that this install's mod set can open | `name` `phase` `waitReady` |
| `spawn-at-coordinate.json` | a tactical mission that is **Playing** | `defName` `faction` `x` `z` |
| `spawn-squad.json` | a tactical mission that is **Playing**, and a **selected actor** to measure from unless `useCenter` is true | `defName` `count` `minDistance` `maxDistance` `useCenter` `centerX/Y/Z` |
| `equip-actor.json` | an actor already **in play** | `actor` `itemName` `container` `listMember` |
| `aim-and-run.json` | a tactical mission | `x` `y` `z` `aimOffsetY` `command` `cmdArgs` |
| `weapon-test.json` | a tactical mission that is **Playing**, and a shooter — selected, or passed as `shooter` | `weaponDef` `enemyDef` `distance` `shots` `setSpread` `spread` `seed` `shooter` |
| `situation.json` | a tactical mission (`restoreFirst:false`), or a snapshot to restore | all of `spawn-squad` plus `snapshot` `restoreFirst` `itemName` |

**Plan timeout vs client timeout.** A plan carries its own `timeoutMs`; the client independently
gives up after `-TimeoutSeconds`, cancels the job — which runs its `finally` — and answers
`{"status":"timeout",...,"cancelled":true}`. Six shipped plans declare more than the 300 s default,
so `plan` **derives** its ceiling instead: when you do not pass `-TimeoutSeconds`, it becomes the
plan's own `timeoutMs` plus 60 s, and says so on stderr. Pass one explicitly only to be the shorter
clock deliberately. The engine caps a plan's `timeoutMs` at 900 000 ms.

Caller vars override the file's own `vars`. Caps that are not advice: `maxSteps` 200 default / 2000
hard, `repeat times` 100, 500 trace entries. The result is
`{ok, code, error, step, steps, elapsedMs, cleanupRan, cleanupSteps, output, trace}` — full step
results are never returned, so ask for what you want through the plan's `output`.

## The weapon bench

```powershell
.\ppcli.ps1 plan .\plans\weapon-test.json '{"weaponDef":"PX_AssaultRifle_WeaponDef","enemyDef":"crabman","distance":10.0,"shots":5}'
```

It equips and reloads the shooter, spawns an enemy at the requested distance, fires N real
activations and reports measured impacts. Read the output with two rules:

- **`shots` counts activations, not projectiles.** A burst weapon fires several per pull of the
  trigger; `projectilesPerShot` and `projectiles` are both reported so the counts do not read as a
  contradiction. Accepted range is 1..100, refused up front outside it rather than truncated.
- **Two families of hit figures, and mixing them is how a bench lies.** `targetHits`,
  `targetHitRate`, `damageOnTarget` are keyed on the aimed-at actor's instance id — that is the
  weapon's score, and the pair to quote. `hitsAnyActor`, `misses`, `hitRateAnyActor`,
  `damageOnActors`, `damageTotal` count every actor a projectile touched, bystanders and the
  shooter's own body included, so they read higher.

`dispersion` is measured about the aim point (`TacticalAbilityTarget.GetWorkingPosition()`), not
about the enemy's `Pos`, which is its feet; `enemyFeet` is reported separately.

Two counters must read `0`, and each fails the run otherwise:

- **`recovered`** — projectiles that a throw inside the damage chain left stuck in flight and that
  PPBridge released so the volley could continue. The throw is re-raised unchanged and still reaches
  the log, but **the stack is cut at the Harmony wrapper, so the throwing code cannot be identified —
  do not attribute it to any mod.**
- **`dropped`** — impacts the 512-entry ring overwrote. Every statistic is computed over what the ring
  still holds, so a non-zero value means the figures describe the last 512 projectiles under the
  heading of all of them. Reachable at the ceiling: 100 activations of a 6-projectile burst is 600.

`returned` is how many impact ROWS the listing carries (capped at 200); it trims the listing only and
changes no statistic. `shots` must also be a whole number — `1.5` is refused up front by name.

**A failed plan publishes no `output` at all.** On failure `output` is null, `outputWithheld` says
why, and `result` carries the failing step's own DTO — for a `wait`, the value that tripped it is in
`result.last`. So never read figures out of a failed run: there are none to read, by design.

## Traps worth knowing before the first run

- **Returning to the menu is not instant** even when `state` says `phase:menu`. The phase flips as
  soon as the old level is gone; the HomeScreen level is up seconds later and anything sent in that
  gap is swallowed. Wait for `@level.IsPlaying` as well as the phase.
- **Leaving a live geoscape is handled only by the plans that handle it.** A bare
  `FinishLevelAndGoToLobby` or `restore` against a running geoscape wedges the process at
  `phase:menu, level:null` until it is restarted. `start-mission`, `start-campaign`,
  `build-mission` and `load-mission` do what the game itself does first
  (`GeoscapeView.UpdateStateStack = false`, then `GeoscapeView.ToLoadingState()`). Use them.
- **`loadmap` is menu-only** — inside a tactical level it throws `InvalidCastException`.
  `start-mission.json` handles the transition.
- **A geoscape save the install cannot open fails by stalling, not by erroring.** The usual cause is
  a mod-set mismatch, and the game log names it. `restore` is issue-only because the game exposes no
  load-completion signal.
- **Partial item names match ammunition too.** `PX_AssaultRifle` matches the rifle and its ammo clip;
  name the def exactly (`PX_AssaultRifle_WeaponDef`).
- **`god_mode` invalidates damage measurement** — the damage path returns before its report. Shots
  land and measured damage is silently zero. `weapon-test.json` saves, clears and restores it.
- **`equip-actor.json` adds an item but loads no magazine**, so a weapon from it cannot fire. The
  weapon bench calls `TacticalItem.ReloadForFree` itself.
- **A plan can report `FAILED` after its work succeeded.** `reset-selection` is cosmetic and carries
  `onError: continue`; read the `trace` before re-running a spawn.
- **One connection at a time.** Do not drive one install from several agents concurrently.

## Reading a result

Treat `ok:false`, a failed assertion in `trace`, or `stale:true` as a failed measurement. Never infer
success from visible game state alone. `{"status":"running"}` is never a final answer — the client
converts it into a timeout with `cancelled:true` rather than returning it. A failed plan has no
`output`; read `outputWithheld`, `step` and `result` instead.

Deleting `Mods\PPBridge\ppcli-enabled` disarms a **running** session within a few seconds: the pipe
stops and no new request reaches the mod. A plan already parked still runs its `finally`. Re-arming
requires relaunching the game.

Every `file:line` reference on this page and in `docs/REFERENCE.md` points into the game's own
**decompiled** assembly, which this repository does not contain and does not ship.
