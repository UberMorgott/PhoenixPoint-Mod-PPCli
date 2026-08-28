# PPCLI

PPCLI is a Windows command-line control channel into a running Phoenix Point for mod developers and their coding agents. It combines the dev-only PPBridge mod (`com.morgott.PPBridge`) with a PowerShell 7 client, and returns exactly one compact JSON object on stdout while sending diagnostics to stderr, so every result can be piped directly into `ConvertFrom-Json`. Decompiled source tells you what the code appears intended to do; PPCLI lets you query, invoke, and measure what the running game actually did.

Three more pages, and nothing else to read: [`PLAYBOOK.md`](PLAYBOOK.md) turns a plain intent into the exact command line, [`docs/REFERENCE.md`](docs/REFERENCE.md) is the deep reference behind every verb, plan and measured trap, and [`AGENTS.md`](AGENTS.md) is the operating brief for a coding agent driving PPCLI.

## Cold start — no campaign, no save, no setup

From the main menu of a game that has never played anything, PPCLI can put you in a real situation in about a dozen seconds. This is the shortest route to a reproducible test.

| Intent | Command |
|---|---|
| Launch any shipped map as a playable mission. | `.\ppcli.ps1 plan .\plans\start-mission.json '{"scene":"ALN_PLT_Nest_48x48_A","seed":12345}'` |
| Start a real campaign; the game generates the factions, the starting base, and the initial squad. | `.\ppcli.ps1 plan .\plans\start-campaign.json '{"difficultyIndex":1}'` |
| Build a mission with your own map, mission type, roster, and enemy budget — including an empty player squad. | `.\ppcli.ps1 plan .\plans\build-mission.json '{"scene":"ALN_PLT_Nest_48x48_A","playerCount":2}'` |
| Fire a named geoscape event on demand. | `.\ppcli.ps1 plan .\plans\fire-event.json '{"eventId":"PROG_PU1"}'` |

`scene` is a `MapPlotDef`'s own scene name; 213 plots ship with the game, and `ALN_PLT_Nest_48x48_A_PlotDef` is reached as `"scene":"ALN_PLT_Nest_48x48_A"`.

A **running geoscape cannot be left** from outside the game. All of these refuse immediately if one is open, because returning to the lobby (or loading a save) out of a live geoscape tears the level down while its view keeps updating, and the game never reaches the main menu again. Quit the campaign from the in-game pause menu, or restart the game. Leaving a tactical mission is safe, and the plans do it for you.

## What it can do

Console commands are one surface. Reflection, definition discovery, plans, live-object inspection, saves, and projectile measurement are separate surfaces.

| Surface | What it gives you | Example |
|---|---|---|
| `console` | Runs any of the 344 native console commands and captures its console output. | `.\ppcli.ps1 connect console '{"command":"info","args":[]}'` |
| `var` | Reads or writes approximately 74 console variables. Variables are not reachable through `console`. | `.\ppcli.ps1 connect var '{"name":"ai_enabled","value":"false"}'` |
| `call` | Arbitrary reflection into the live process: `new`, `get`, `set`, and `invoke` on public or private members. Targets can be handles or live roots such as `@tac`, `@map`, `@view`, and `@selected`. This is the general escape hatch: anything the loaded game code can do is callable. | `.\ppcli.ps1 connect call '{"op":"get","target":"@selected","member":"Pos"}'` |
| `find` | Searches the live definition repository by name substring, exact GUID, and optional type. | `.\ppcli.ps1 connect find '{"query":"Crabman","type":"PhoenixPoint.Tactical.Entities.TacActorDef"}'` |
| `index` | Pages the live definition repository into `catalog\defs.ndjson` and `catalog\meta.json`. Plans can then resolve plain names such as `crabman` locally and immediately. | `.\ppcli.ps1 index` |
| `plan` | Runs a declarative multi-step sequence from `plans\*.json`, with waits, timeouts, assertions, saved intermediate values, and a required `finally` cleanup block. Cleanup runs on success, failure, timeout, and cancellation. One request replaces repeated pipe round-trips. | `.\ppcli.ps1 plan .\plans\spawn-at-coordinate.json '{"defName":"crabman","faction":"alien","x":11.5,"z":-4.5}'` |
| Shot observer and `plans\weapon-test.json` | Equips and reloads a selected soldier, spawns an enemy at a requested distance, fires N real shots, and returns impact points, armor, dispersion about the aim point, and hits and damage counted twice over — once against the aimed-at target (`targetHits`, `damageOnTarget`) and once against any actor a projectile touched (`hitsAnyActor`, `damageOnActors`). The result is measured from live projectiles, not inferred from weapon definitions. | `.\ppcli.ps1 plan .\plans\weapon-test.json '{"weaponDef":"PX_AssaultRifle_WeaponDef","enemyDef":"crabman","distance":10.0,"shots":5}'` |
| `ping` | Reports protocol and loaded bridge build. | `.\ppcli.ps1 connect ping` |
| `state` | Reports phase, scene, level, and level state. | `.\ppcli.ps1 connect state` |
| `roots` | Returns the current live entrances: `game`, `phoenix`, `defs`, `level`, `geo`, `tac`, `map`, `view`, `faction`, and `selected`. | `.\ppcli.ps1 connect roots` |
| `types` | Searches types loaded in the game process; an assembly filter is optional. | `.\ppcli.ps1 connect types '{"pattern":"TacticalActor"}'` |
| `members` | Lists constructors, fields, properties, and methods for a type or handle, including inherited private members. | `.\ppcli.ps1 connect members '{"type":"PhoenixPoint.Tactical.Entities.TacticalActor","filter":"Health"}'` |
| `inspect` | Projects a handle's identity and lists members of its runtime type. | `.\ppcli.ps1 connect inspect '{"h":"h:3:17","filter":"Position"}'` |
| `items` | Reads a bounded, zero-based page from a collection handle without implicitly walking the whole collection. | `.\ppcli.ps1 connect items '{"h":"h:3:17","page":0,"pageSize":20}'` |
| `wait` | Polls by frame until tactical readiness, a phase, or an arbitrary reflected predicate matches. | `.\ppcli.ps1 connect wait '{"ready":true,"timeoutMs":120000}'` |
| `observe` | Starts, marks, reads, stops, or checks the projectile-impact observer independently of the weapon plan. | `.\ppcli.ps1 connect observe '{"action":"read","aim":[0,0,0]}'` |
| `snapshot` | Replaces a named save and waits until saving finishes. | `.\ppcli.ps1 connect snapshot '{"name":"before-test","timeoutMs":30000}'` |
| `restore` | Checks that a named save exists and issues the game's `load_game`; follow it with `wait` because the game exposes no load-completion signal. | `.\ppcli.ps1 connect restore '{"name":"before-test"}'` |
| `status` | Polls a previously accepted long-running job without queueing a game-thread operation. | `.\ppcli.ps1 connect status '{"jobId":"j12"}'` |
| `cancel` | Requests cancellation of a queued or cross-frame job; a plan proceeds through `finally`. It cannot interrupt a synchronous reflection call already executing. | `.\ppcli.ps1 connect cancel '{"jobId":"j12"}'` |
| `release` | Releases one leased object handle before its 900-second TTL. | `.\ppcli.ps1 connect release '{"h":"h:3:17"}'` |
| `connect` | Sends one verb to an already-running game. Typical measured latency is 17–60 ms. | `.\ppcli.ps1 connect state` |
| `run` | Cold-launches one isolated game process, executes one verb through the job-file entrance, checks the loaded build, and stops only the process it launched. A menu result takes about 17 seconds on the measured system. | `.\ppcli.ps1 run state` |
| `batch` | Cold-launches once for a JSON array of `{id,verb,args}` jobs. | `.\ppcli.ps1 batch .\jobs.json` with `jobs.json` containing `[{"id":"s1","verb":"state"}]` |
| `deploy` | Builds PPBridge and installs `PPBridge.dll` plus `meta.json` under the selected game's `Mods\PPBridge` directory. | `.\ppcli.ps1 deploy -PPRoot $PPRoot` |

All commands above are PowerShell 7 commands run from the repository root. Add `-PPRoot $PPRoot` when Steam discovery cannot select exactly one install or when you keep multiple installs. A live handle such as `h:3:17` is only an example shape; use a handle returned by the current game session.

## Install and first run

1. Install PowerShell 7, a .NET SDK, and the .NET Framework 4.7.2 targeting pack. Install the Phoenix Point Mod SDK. The game root used for references must contain `PhoenixPointWin64.exe`, `ModSDK\Assembly-CSharp.dll`, and `PhoenixPointWin64_Data\Managed\UnityEngine.CoreModule.dll`.

2. Clone this repository, open PowerShell 7 in its root, and set the install explicitly:

   ```powershell
   $PPRoot = 'C:\Program Files (x86)\Steam\steamapps\common\Phoenix Point'
   ```

   The value is the directory containing `PhoenixPointWin64.exe`, not `Mods` or `ModSDK`.

3. Verify the reference path with the same property used by the project, then deploy:

   ```powershell
   dotnet build .\PPBridge.csproj -c Release /p:PPRoot="$PPRoot"
   .\ppcli.ps1 deploy -PPRoot $PPRoot
   ```

   `deploy` performs a Release build and installs `PPBridge.dll` and `meta.json` in `$PPRoot\Mods\PPBridge`. If the deploy target is an automation copy without `ModSDK`, run `.\deploy.ps1 -PPRoot $PPRoot -RefRoot 'C:\path\to\a\full\Phoenix Point install'` instead.

   If you keep a separate copy of the game for automation, write its path into `ppcli-install.txt` beside `ppcli.ps1`, one line. Every command then defaults to that install instead of the one Steam discovery finds, and `deploy` refuses any other install until you repeat it as `-PPRoot '<path>' -Force`. Without that file nothing changes.

4. Arm the endpoint explicitly. `deploy` does not create this file:

   ```powershell
   New-Item -ItemType File -Force (Join-Path $PPRoot 'Mods\PPBridge\ppcli-enabled')
   ```

5. Start that install with mod loading enabled, open the in-game mod manager, enable **PPBridge**, then exit the game. Its id is `com.morgott.PPBridge`. Activation must be recorded in:

   ```text
   %USERPROFILE%\AppData\LocalLow\Snapshot Games Inc\Phoenix Point\Steam\<SteamID64>\Options.jopt
   ```

   The `MOD_ACTIVATED` object's `CollectionValues` array must contain the exact value `com.morgott.PPBridge`. Use the in-game manager; do not rewrite `Options.jopt` by hand. If that `Steam\` directory holds more than one `<SteamID64>` profile, `run` and `batch` refuse until you name the one this install writes with `-ProfileId <SteamID64>`. If activation is skipped, `run` refuses before launch with `REFUSED: 'com.morgott.PPBridge' is not activated in ...`. A manually launched game exposes no endpoint, so `connect` returns one JSON error containing `REFUSED: no live PPBridge endpoint`.

6. Start the game again with `-mods` and wait until the main menu is visibly settled:

   ```powershell
   Start-Process (Join-Path $PPRoot 'PhoenixPointWin64.exe') -ArgumentList '-mods'
   ```

7. Make the first live query and parse stdout directly:

   ```powershell
   $reply = .\ppcli.ps1 connect state -PPRoot $PPRoot | ConvertFrom-Json
   $reply.result
   ```

   A normal answer contains `ok`, `phase`, `scene`, `level`, and `levelState`. Do not send further runtime commands while this gate does not answer.

8. Once that gate answers, build the local definition-name catalog once for this game build:

   ```powershell
   .\ppcli.ps1 index -PPRoot $PPRoot
   ```

   `index` pages the live repository, so it needs a settled game; running it before the gate answers is the hang described above.

## Security

PPBridge listens only on a Windows named pipe created by [`PipeServer.cs`](src/PipeServer.cs#L166); it never opens a TCP socket and exposes nothing to the network. Before the pipe or job-file entrance starts, [`PPBridgeMain.cs`](src/PPBridgeMain.cs) requires the opt-in marker `Mods\PPBridge\ppcli-enabled`. The marker is checked when the mod is enabled. Removing it prevents arming on the next load; to stop an endpoint that is already running, exit the game or disable the mod.

Every process launch generates a 128-bit random bearer token. The token is written to `%LOCALAPPDATA%\ppcli\endpoints\<pid>.json` and checked with the length-independent comparison in [`Wire.cs`](src/Wire.cs#L80). Any process running as the same Windows user can read that file. A token holder can execute arbitrary code inside the game process as that user because [`Reflect.ResolveType`](src/Reflect.cs#L199) has no type allowlist. This is the same trust boundary as loading any other mod DLL into the game. Arm and enable PPBridge only while using it; remove the marker and exit or disable the mod afterward.

## Limitations

- **A live geoscape is a one-way street.** Returning to the lobby or loading a save from outside the game wedges the process: the level is destroyed while `GeoscapeView` keeps updating and throwing every frame, and the main menu never comes back. The cold-start plans refuse rather than trigger it. To start a second campaign in one session, restart the game.
- **UNVERIFIED:** the geoscape half of `plans\load-mission.json`. Its tactical path is verified. Against a live geoscape it hits the limitation above, and it can also report success without loading anything, because its phase predicate is already true on the level it is leaving. `restore` itself is issue-only; the plan's waits are the completion protocol.
- `plans\build-mission.json` stops in the deployment phase when the player roster is explicit, which is correct: the squad has to be placed. It reports `turnStarted:false` rather than waiting for a turn that no unattended run will ever start.
- `plans\start-mission.json` does not expose `loadmap`'s plot and parcel tag filter; the plan engine cannot splice a list into a console argument list.
- `plans\weapon-test.json` needs a playing tactical mission and a selected shooter. It deliberately leaves the spawned enemy and equipped weapon in place. Random placement can fail the line-of-sight assertion; use `seed` to reproduce a placement.
- Volleys beyond approximately six consecutive shots lose a projectile. `shots` is accepted from 1 to 100, and the plan fails its `landed` assertion by name instead of returning a short volley. The cause is pacing: the same seed completes 10 of 10 when about 3 seconds of dead time follows each activation. Target death was investigated and ruled out. No honest settle predicate is known — the ability's `IsExecuting` is already false while a shot is merely enqueued, and `TacticalActorBase.HasExecutingAbility(null, false)` never goes false. Use five shots or fewer for now.
- Handles belong to one process and epoch. A game restart or scene unload invalidates them.
- PPBridge serves one pipe connection at a time. Do not drive one install concurrently from multiple agents.

## License

[CC BY-NC 4.0](LICENSE). Copyright (c) 2026 Morgott.
