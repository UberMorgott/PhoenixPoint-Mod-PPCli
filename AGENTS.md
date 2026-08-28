# Instructions for agents using PPCLI

For every question about **runtime behavior**, use PPCLI first. Do not start by searching decompiled source. Source describes intent; PPCLI reports what the running game actually did. Use source only after runtime evidence identifies the code path or PPCLI cannot observe it.

Run PowerShell 7 from the PPCLI directory. Set the target explicitly when more than one install exists:

```powershell
$PPRoot         = 'C:\path\to\Phoenix Point'          # the install you actually play; `connect` only
$AutomationRoot = 'C:\path\to\Phoenix Point copy'     # the install `run` and `batch` may cold-launch
```

Every invocation writes exactly one compact JSON object to stdout and diagnostics to stderr. Parse it directly: `$r = .\ppcli.ps1 connect state -PPRoot $PPRoot | ConvertFrom-Json`. For live requests, the verb result is normally under `$r.result`.

## Operating discipline

1. Gate every live session with `.\ppcli.ps1 connect state -PPRoot $PPRoot`. Wait until it actually **answers** before sending anything else. Querying a still-initializing game can hang for minutes and looks exactly like an engine bug.
2. Prefer a plan over a loop of pipe calls. Spawning one actor at a coordinate takes 23 `call` round-trips by hand and one request with `plans\spawn-at-coordinate.json`. Plans also run their `finally` cleanup after success, failure, timeout, or cancellation.
3. Run `.\ppcli.ps1 deploy -PPRoot $PPRoot` after **every** PPBridge mod edit, then restart the game session. Otherwise the game silently runs the old DLL. If a cold-run result says `stale:true`, believe it: every result from that run is a ghost. Redeploy and repeat the run.
4. Keep two installs. Use an automation install for `run` and `batch`; PPCLI may cold-launch and stop that install. Use `connect` only against the install you actually play. Never cold-launch the play install, and do not mutate a real save without explicit intent.
5. The endpoint is opt-in. `Mods\PPBridge\ppcli-enabled` must exist and `com.morgott.PPBridge` must be enabled in the profile. Delete the marker and exit or disable the mod when finished.

## Client commands and live verbs

Use these exact shapes. Add `-PPRoot $PPRoot` as shown; handles and job ids must come from the current session.

| Command or verb | Exact PowerShell 7 shape |
|---|---|
| `deploy` | `.\ppcli.ps1 deploy -PPRoot $PPRoot` |
| `connect` | `.\ppcli.ps1 connect state -PPRoot $PPRoot` |
| `run` | `.\ppcli.ps1 run state -PPRoot $AutomationRoot` |
| `batch` | `.\ppcli.ps1 batch .\jobs.json -PPRoot $AutomationRoot` where `jobs.json` is a JSON array such as `[{"id":"s1","verb":"state"}]` |
| `index` | `.\ppcli.ps1 index -PPRoot $PPRoot` |
| `plan` | `.\ppcli.ps1 plan .\plans\spawn-at-coordinate.json '{"defName":"crabman","faction":"alien","x":11.5,"z":-4.5}' -PPRoot $PPRoot` |
| `ping` | `.\ppcli.ps1 connect ping -PPRoot $PPRoot` |
| `state` | `.\ppcli.ps1 connect state -PPRoot $PPRoot` |
| `console` | `.\ppcli.ps1 connect console '{"command":"info","args":[]}' -PPRoot $PPRoot` |
| `var` | `.\ppcli.ps1 connect var '{"name":"ai_enabled","value":"false"}' -PPRoot $PPRoot` (`{"name":"ai_enabled"}` reads only) |
| `call` — `new` | `.\ppcli.ps1 connect call '{"op":"new","type":"System.Text.StringBuilder","args":["PPCLI"]}' -PPRoot $PPRoot` |
| `call` — `get` | `.\ppcli.ps1 connect call '{"op":"get","target":"@selected","member":"Pos"}' -PPRoot $PPRoot` |
| `call` — `set` | `.\ppcli.ps1 connect call '{"op":"set","type":"UnityEngine.Time","member":"timeScale","value":1.0}' -PPRoot $PPRoot` |
| `call` — `invoke` | `.\ppcli.ps1 connect call '{"op":"invoke","type":"System.Math","member":"Abs","args":[-7]}' -PPRoot $PPRoot` |
| `roots` | `.\ppcli.ps1 connect roots -PPRoot $PPRoot` |
| `types` | `.\ppcli.ps1 connect types '{"pattern":"TacticalActor"}' -PPRoot $PPRoot` |
| `members` | `.\ppcli.ps1 connect members '{"type":"PhoenixPoint.Tactical.Entities.TacticalActor","filter":"Health"}' -PPRoot $PPRoot` |
| `inspect` | `.\ppcli.ps1 connect inspect '{"h":"h:3:17","filter":"Position"}' -PPRoot $PPRoot` |
| `items` | `.\ppcli.ps1 connect items '{"h":"h:3:17","page":0,"pageSize":20}' -PPRoot $PPRoot` |
| `release` | `.\ppcli.ps1 connect release '{"h":"h:3:17"}' -PPRoot $PPRoot` |
| `find` | `.\ppcli.ps1 connect find '{"query":"Crabman","type":"PhoenixPoint.Tactical.Entities.TacActorDef"}' -PPRoot $PPRoot` |
| `wait` | `.\ppcli.ps1 connect wait '{"ready":true,"timeoutMs":120000}' -PPRoot $PPRoot` |
| `observe` | `.\ppcli.ps1 connect observe '{"action":"start"}' -PPRoot $PPRoot`; then use `'{"action":"mark"}'`, `'{"action":"read","aim":[0,0,0]}'`, `'{"action":"status"}'`, or `'{"action":"stop"}'` |
| `snapshot` | `.\ppcli.ps1 connect snapshot '{"name":"before-test","timeoutMs":30000}' -PPRoot $PPRoot` |
| `restore` | `.\ppcli.ps1 connect restore '{"name":"before-test"}' -PPRoot $PPRoot`; then `wait` because restore only issues `load_game` |
| `status` | `.\ppcli.ps1 connect status '{"jobId":"j12"}' -PPRoot $PPRoot` |
| `cancel` | `.\ppcli.ps1 connect cancel '{"jobId":"j12"}' -PPRoot $PPRoot` |

Use `@game`, `@phoenix`, `@defs`, `@level`, `@geo`, `@tac`, `@map`, `@view`, `@faction`, and `@selected` as live `call` targets. Use `find` for definitions, `types` and `members` for discovery, `inspect` for object identity plus members, `items` for collection pages, and `release` when a handle is no longer needed. Handles die on scene unload and process restart.

## Getting into a situation from nothing

Do not ask a human to load a save first. From the main menu, with no campaign ever played:

| Intent | Command |
|---|---|
| Any shipped map, as a playable mission (about 12 seconds). | `.\ppcli.ps1 plan .\plans\start-mission.json '{"scene":"ALN_PLT_Nest_48x48_A","seed":12345}' -PPRoot $PPRoot` |
| A real campaign, with a generated base and squad (about 15 seconds). | `.\ppcli.ps1 plan .\plans\start-campaign.json '{"difficultyIndex":1}' -PPRoot $PPRoot` |
| A mission whose map, mission type, player roster and enemy budget you choose. `playerCount:0` gives an empty squad. | `.\ppcli.ps1 plan .\plans\build-mission.json '{"scene":"ALN_PLT_Nest_48x48_A","playerCount":2}' -PPRoot $PPRoot` |
| A named geoscape event, on demand. | `.\ppcli.ps1 plan .\plans\fire-event.json '{"eventId":"PROG_PU1"}' -PPRoot $PPRoot` |

`scene` is a `MapPlotDef`'s own `Scene.SceneName`; find plots with `find '{"query":"PlotDef","pageSize":50}'`.

## Traps to check first

- **Leaving a running geoscape is handled, but only by the plans that handle it.** A bare `FinishLevelAndGoToLobby` or `restore` against a live geoscape still wedges the process — the teardown re-selects the vehicle (`GeoMap.UnRegisterVehicles` to `UIStateVehicleSelected.OnVehichleChanged` to `ResetViewState`) and a throw while that panel rebuilds kills `Level.SetCurrentCrt`. Symptom: `state` reports `phase:menu`, `scene:HomeScreen`, `level:null` forever, and only a process restart recovers. The cold-start plans and `load-mission.json` do what the game itself does first — `GeoscapeView.UpdateStateStack = false`, then `GeoscapeView.ToLoadingState()` — and are verified across a geoscape, back to the menu, and into a mission in one process. Use them rather than issuing the lobby call yourself.
- `loadmap` is menu-only. Inside a tactical level it throws `InvalidCastException` from `TacticalGameCrt`. `start-mission.json` handles the transition for you.
- Returning to the menu is not instant even when `state` says `phase:menu`. The phase flips as soon as the old level is gone; the HomeScreen level is up several seconds later, and anything sent in that gap is swallowed. Wait for `@level.IsPlaying` as well as the phase.

- Partial item names match ammunition as well as weapons. A direct `find` for `PX_AssaultRifle` returns the ammo clip and weapon, and a `defs[0]` path can select `PX_AssaultRifle_AmmoClip_ItemDef`. The local name resolver may refuse the ambiguity instead. Symptom: ammo is added or tested instead of the weapon. Use the exact `PX_AssaultRifle_WeaponDef` name and a `WeaponDef` type filter.
- `god_mode` invalidates damage measurement. The damage path returns before its report and before HP changes. Symptom: shots land but measured damage is silently zero. Keep `god_mode` false; `plans\weapon-test.json` saves, disables, and restores it.
- `shots` counts **activations**, not projectiles, and is accepted from 1 to 100. A burst weapon fires several projectiles per pull of the trigger (`PX_AssaultRifle` answers 6 to `GetNumberOfShots`), so read `projectiles` and `projectilesPerShot` before concluding the counts disagree. The documented "approximately six shots per volley" ceiling was a defect in the plan's settle predicate, not a property of the game; it is fixed. A request outside 1..100 is refused up front rather than truncated into a short volley reported as `ok:true`.
- **The clean run to quote is small.** The strongest full pass with `recovered:0` is 3 activations and 18 projectiles. Long volleys do run, but a request for more shots than the target survives can end in a named refusal: once actors start dying, something throws inside `ProjectileLogic.OnTrajectoryEnd`, and the stack is cut at the Harmony wrapper so the throwing code cannot be identified. Do not attribute it to a named mod.
- **`recovered` must read `0`, and a non-zero value FAILS the plan.** It counts projectiles another mod's exception left stuck in flight and that PPBridge released so the volley could continue; the exception is re-thrown unchanged and still reaches the log. The figures are withheld rather than printed, because they would have been measured across a repair of the game. The count itself is still in the output.
- `plans\equip-actor.json` adds a weapon but does not load a magazine. Symptom: the weapon is present but cannot fire or has zero charges. Use `plans\weapon-test.json`, which calls `TacticalItem.ReloadForFree`, or explicitly reload after equipping.

## Worked example

Plain request: **“Test the Phoenix assault rifle against a Crabman at 10 metres for five shots.”**

```powershell
$r = .\ppcli.ps1 plan .\plans\weapon-test.json '{"weaponDef":"PX_AssaultRifle_WeaponDef","enemyDef":"crabman","distance":10.0,"shots":5}' -PPRoot $PPRoot | ConvertFrom-Json
$r.result.output
```

The returned output contains the exact weapon and enemy defs, requested and achieved distance, shots requested and fired, projectile count, armor, charges before and after, aim point, `enemyFeet`, every impact, dispersion, and two separate families of hit figures:

- **Per target** — `targetHits`, `targetHitRate`, `damageOnTarget`. Keyed on the aimed-at actor's instance id. This is the weapon's score against the target, and it is what to quote.
- **Any actor** — `hitsAnyActor`, `misses`, `hitRateAnyActor`, `damageTotal`, `damageOnActors`. These count every actor a projectile touched, bystanders and the shooter included, so they read high. A live 10-shot run returned `targetHits` 6 against `hitsAnyActor` 9, and `damageOnTarget` 153 against `damageOnActors` 201.

`dispersion` is measured about the aim point, `TacticalAbilityTarget.GetWorkingPosition()`, not about the enemy's feet. The feet position is reported separately as `enemyFeet`; measuring about it inflated one real run's `mean` from 0.3896 to 0.9403.

Treat `ok:false`, a failed assertion in `trace`, or `stale:true` as a failed measurement; do not infer success from visible game state alone.
