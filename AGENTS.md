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

## Traps to check first

- Partial item names match ammunition as well as weapons. A direct `find` for `PX_AssaultRifle` returns the ammo clip and weapon, and a `defs[0]` path can select `PX_AssaultRifle_AmmoClip_ItemDef`. The local name resolver may refuse the ambiguity instead. Symptom: ammo is added or tested instead of the weapon. Use the exact `PX_AssaultRifle_WeaponDef` name and a `WeaponDef` type filter.
- `god_mode` invalidates damage measurement. The damage path returns before its report and before HP changes. Symptom: shots land but measured damage is silently zero. Keep `god_mode` false; `plans\weapon-test.json` saves, disables, and restores it.
- `plans\equip-actor.json` adds a weapon but does not load a magazine. Symptom: the weapon is present but cannot fire or has zero charges. Use `plans\weapon-test.json`, which calls `TacticalItem.ReloadForFree`, or explicitly reload after equipping.

## Worked example

Plain request: **“Test the Phoenix assault rifle against a Crabman at 10 metres for five shots.”**

```powershell
$r = .\ppcli.ps1 plan .\plans\weapon-test.json '{"weaponDef":"PX_AssaultRifle_WeaponDef","enemyDef":"crabman","distance":10.0,"shots":5}' -PPRoot $PPRoot | ConvertFrom-Json
$r.result.output
```

The returned output contains the exact weapon and enemy defs, requested and achieved distance, shots requested and fired, projectile count, `hits`, `misses`, `hitRate`, `damageTotal`, `damageOnActors`, armor, charges before and after, aim point, every impact, and dispersion. Treat `ok:false`, a failed assertion in `trace`, or `stale:true` as a failed measurement; do not infer success from visible game state alone.
