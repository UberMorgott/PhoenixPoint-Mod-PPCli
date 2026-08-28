# PPCLI

PPCLI is a Windows command-line control channel into a running Phoenix Point for mod developers and their coding agents. It combines the dev-only PPBridge mod (`com.morgott.PPBridge`) with a PowerShell 7 client, and returns exactly one compact JSON object on stdout while sending diagnostics to stderr, so every result can be piped directly into `ConvertFrom-Json`. Decompiled source tells you what the code appears intended to do; PPCLI lets you query, invoke, and measure what the running game actually did.

Three more pages, and nothing else to read: [`PLAYBOOK.md`](PLAYBOOK.md) turns a plain intent into the exact command line, [`docs/REFERENCE.md`](docs/REFERENCE.md) is the deep reference behind every verb, plan and measured trap, and [`AGENTS.md`](AGENTS.md) is the operating brief for a coding agent driving PPCLI.

## Cold start — no campaign, no save, no setup

From the main menu of a game that has never played anything, PPCLI can put you in a real situation in one request — measured here at roughly a dozen seconds. This is the shortest route to a reproducible test.

| Intent | Command |
|---|---|
| Launch any shipped map as a playable mission. | `.\ppcli.ps1 plan .\plans\start-mission.json '{"scene":"ALN_PLT_Nest_48x48_A","seed":12345}'` |
| Start a real campaign; the game generates the factions, the starting base, and the initial squad. | `.\ppcli.ps1 plan .\plans\start-campaign.json '{"difficultyIndex":1}'` |
| Build a mission with your own map, mission type, roster, and enemy budget — including an empty player squad. | `.\ppcli.ps1 plan .\plans\build-mission.json '{"scene":"ALN_PLT_Nest_48x48_A","playerCount":2}'` |
| Fire a named geoscape event on demand. | `.\ppcli.ps1 plan .\plans\fire-event.json '{"eventId":"PROG_PU1"}'` |

`scene` is a `MapPlotDef`'s own scene name. Find one, and read the name off the def:

```powershell
.\ppcli.ps1 connect find '{"query":"_PlotDef","pageSize":50}'                        # -> {name, guid, type}
.\ppcli.ps1 connect call '{"op":"invoke","target":"@defs","member":"GetDef","args":["<guid>"]}'   # -> <PLOT>
.\ppcli.ps1 connect call '{"op":"get","target":"<PLOT>","member":"Scene"}'           # -> <SCENEREF>
.\ppcli.ps1 connect call '{"op":"get","target":"<SCENEREF>","member":"SceneName"}'   # -> ALN_PLT_Nest_48x48_A
```

`find` returns only `{name, guid, type}`, so the two `call` steps are what turn a search hit into a
scene name. In practice the def name already carries it: all 209 `MapPlotDef` rows in the catalog
built on the machine this was measured on are named `<scene>_PlotDef`, so
`ALN_PLT_Nest_48x48_A_PlotDef` is reached as `"scene":"ALN_PLT_Nest_48x48_A"`. Use the reads above
when a name does not follow that shape.

**A running geoscape can be left.** These plans no longer refuse when a campaign is open: they do what the game itself does before tearing a geoscape down (`GeoLevelController.cs:1406,:1444`) — set `GeoscapeView.UpdateStateStack` to false, call `GeoscapeView.ToLoadingState()` so that no vehicle is selected while the vehicles exit play, and only then return to the lobby. `start-mission.json` reports `cameFrom:geoscape` when it came that way. Verified in one process: geoscape to menu to a playing mission, and two campaigns back to back. Leaving a tactical mission was always safe and still is.

## What it can do

Console commands are one surface. Reflection, definition discovery, plans, live-object inspection, saves, and projectile measurement are separate surfaces.

| Surface | What it gives you | Example |
|---|---|---|
| `console` | Runs any of the 344 native console commands and captures its console output. | `.\ppcli.ps1 connect console '{"command":"info","args":[]}'` |
| `var` | Reads or writes approximately 74 console variables. Variables are not reachable through `console`. | `.\ppcli.ps1 connect var '{"name":"ai_enabled","value":"false"}'` |
| `call` | Arbitrary reflection into the live process: `new`, `get`, `set`, and `invoke` on public or private members. Targets can be handles or live roots such as `@tac`, `@map`, `@view`, and `@selected`. Its scope is wide but not total: events cannot be subscribed to, by-ref and pointer parameters are refused, and two overloads that bind equally well are refused as ambiguous — which makes a member declared identically on several types in one hierarchy unreachable. | `.\ppcli.ps1 connect call '{"op":"get","target":"@selected","member":"Pos"}'` |
| `find` | Searches the live definition repository by name substring, exact GUID, and optional type. | `.\ppcli.ps1 connect find '{"query":"Crabman","type":"PhoenixPoint.Tactical.Entities.TacActorDef"}'` |
| `index` | Pages the live definition repository into `catalog\defs.ndjson` and `catalog\meta.json`. Plans can then resolve plain names such as `crabman` locally and immediately. | `.\ppcli.ps1 index` |
| `plan` | Runs a declarative multi-step sequence from `plans\*.json`, with waits, timeouts, assertions, saved intermediate values, and a required `finally` cleanup block. Cleanup runs on success, failure, timeout, and cancellation. A step reads an earlier value back as `${NAME.json.path}`, and `"${...NAME}"` used as an array element splices that variable's own elements into the surrounding array. One request replaces repeated pipe round-trips. | `.\ppcli.ps1 plan .\plans\spawn-at-coordinate.json '{"defName":"crabman","faction":"alien","x":11.5,"z":-4.5}'` |
| Shot observer and `plans\weapon-test.json` | Equips and reloads a selected soldier, spawns an enemy at a requested distance, fires N real shots, and returns impact points, armor, dispersion about the aim point, and hits and damage counted twice over — once against the aimed-at target (`targetHits`, `damageOnTarget`) and once against any actor a projectile touched (`hitsAnyActor`, `damageOnActors`). The result is measured from live projectiles, not inferred from weapon definitions. `shots` counts activations and is accepted from 1 to 100; a burst weapon fires several projectiles per activation, and a non-zero `recovered` fails the run — see the note below the table. |
| `find` (enumerate) | `{"all":true,...}` pages the whole definition repository rather than searching it. `all` must be a real boolean, so a typo'd variable can never become a repository dump. | `.\ppcli.ps1 connect find '{"all":true,"page":0,"pageSize":200}'` | `.\ppcli.ps1 plan .\plans\weapon-test.json '{"weaponDef":"PX_AssaultRifle_WeaponDef","enemyDef":"crabman","distance":10.0,"shots":5}'` |
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
| `connect` | Sends one verb to an already-running game. On the machine these pages were measured on, a verb answered in 17–60 ms. | `.\ppcli.ps1 connect state` |
| `run` | Cold-launches one isolated game process, executes one verb through the job-file entrance, checks the loaded build, and stops only the process it launched. A menu result took about 17 seconds there. See the warning under *Install and first run*. | `.\ppcli.ps1 run state` |
| `batch` | Cold-launches once for a JSON array of `{id,verb,args}` jobs. | `.\ppcli.ps1 batch .\jobs.json` with `jobs.json` containing `[{"id":"s1","verb":"state"}]` |
| `deploy` | Builds PPBridge and installs `PPBridge.dll` plus `meta.json` under the selected game's `Mods\PPBridge` directory. | `.\ppcli.ps1 deploy -PPRoot $PPRoot` |

`shots` counts pulls of the trigger, not projectiles: `PX_AssaultRifle` fires six per activation, and `projectilesPerShot` is reported alongside both counts. `recovered` counts projectiles that a throw inside the damage chain left stuck in flight and that PPBridge had to release for the volley to continue; the exception is re-thrown unchanged, so it still reaches the log. The stack is cut at the Harmony wrapper, so the throwing code **cannot be identified** — the count says something threw, never who. A non-zero `recovered` **fails the run**, and a failed run publishes no figures at all, because they would have been measured across a repair of the game. The count itself survives in the failing step's result (`result.last`).

Impacts are held in a ring of 512 and `observe read` lists at most 200 rows, dropping the oldest from the listing. The plan reports all four numbers: `projectiles` is everything that landed, `stored` is what the ring still held, `dropped` is what it overwrote, and `returned` is how many rows the listing carries. Every statistic is computed over `stored`, so a non-zero `dropped` **fails the run** for the same reason a non-zero `recovered` does; `returned` trims the listing only.

A **failed plan publishes no `output` at all** — `outputWithheld` says why, and `result` carries the failing step's own DTO, so the value that tripped an assertion still reaches the caller while the invalid figures do not.

All commands above are PowerShell 7 commands run from the repository root. A live handle such as `h:3:17` is only an example shape; use a handle returned by the current game session.

**Which install a command means, said plainly.** Discovery reads Steam's own registry key and `libraryfolders.vdf`, so it only ever sees installs *Steam* knows about — and it refuses by name only when that count is zero or more than one. A separate copy you keep for automation is not a Steam library entry, so discovery does not see it, does not become ambiguous, and quietly answers with the install you **play**. Every command therefore prints the install it chose and how it chose it, and the fix is not a refusal you can wait for: name the copy with `-PPRoot`, or pin it once in `ppcli-install.txt`.

## Install and first run

1. Install PowerShell 7 and a .NET SDK. The reference assemblies are the ones Phoenix Point already ships — there is nothing else to download — so the game root used for references must contain `PhoenixPointWin64.exe`, `ModSDK\Assembly-CSharp.dll`, `ModSDK\0Harmony.dll`, and `PhoenixPointWin64_Data\Managed\UnityEngine.CoreModule.dll`.

   The project targets .NET Framework 4.7.2, which normally wants the matching targeting pack. Install it **if the build cannot resolve the reference assemblies** and says so — on one machine checked here the `.NETFramework` reference-assemblies directory was empty and the build still succeeded in 1.4 s, so it is not an unconditional prerequisite. That is one data point, not a guarantee that you will never need it.

2. Clone this repository and open PowerShell 7 in its root. Then find the install — the client already knows how, so ask it rather than guessing a path:

   ```powershell
   . .\paths.ps1
   $PPRoot = Find-PPInstall      # ppcli-install.txt if you have one, else Steam's registry
                                 # key + steamapps\libraryfolders.vdf
   $PPRoot
   ```

   That is the same discovery every command uses. It answers with one install, or refuses by name and tells you to pass `-PPRoot`. If you know the path already, or you keep the game outside a Steam library, set it by hand instead:

   ```powershell
   $PPRoot = 'X:\<your Steam library>\steamapps\common\Phoenix Point'
   ```

   The value is the directory containing `PhoenixPointWin64.exe`, not `Mods` or `ModSDK`.

   **Discovery finds the install you PLAY.** It sees Steam libraries and nothing else, so on a machine with one Steam install and a separate automation copy it is not ambiguous — it simply answers with the Steam one. Every command prints the install it chose and how, so read that line before a `deploy` writes anything.

3. Verify the reference path with the same property used by the project, then deploy:

   ```powershell
   dotnet build .\PPBridge.csproj -c Release /p:PPRoot="$PPRoot"
   .\ppcli.ps1 deploy -PPRoot $PPRoot
   ```

   `deploy` performs a Release build and installs `PPBridge.dll` and `meta.json` in `$PPRoot\Mods\PPBridge`. If the deploy target is an automation copy without `ModSDK`, run `.\deploy.ps1 -PPRoot $PPRoot -RefRoot 'C:\path\to\a\full\Phoenix Point install'` instead.

   If you keep a separate copy of the game for automation, write its path into `ppcli-install.txt` beside `ppcli.ps1`, one line. Every command then defaults to that install instead of the one Steam discovery finds, and `deploy` refuses any other install until you repeat it as `-PPRoot '<path>' -Force`. Without that file nothing changes. It is gitignored: the path is specific to your machine.

4. Arm the endpoint explicitly. `deploy` does not create this file:

   ```powershell
   New-Item -ItemType File -Force (Join-Path $PPRoot 'Mods\PPBridge\ppcli-enabled')
   ```

5. Start that install **with `-mods`** — without that argument no mod loads at all — open the in-game mod manager, enable **PPBridge**, then exit the game:

   ```powershell
   Start-Process (Join-Path $PPRoot 'PhoenixPointWin64.exe') -ArgumentList '-mods'
   ```

   Its id is `com.morgott.PPBridge`. Enabling it records the activation in:

   ```text
   %USERPROFILE%\AppData\LocalLow\Snapshot Games Inc\Phoenix Point\Steam\<SteamID64>\Options.jopt
   ```

   The `MOD_ACTIVATED` object's `CollectionValues` array must contain the exact value `com.morgott.PPBridge`. Use the in-game manager; do not rewrite `Options.jopt` by hand. If activation is skipped, `run` refuses before launch rather than launching into a silent failure.

   If that `Steam\` directory holds more than one profile, `run` and `batch` refuse until you name the one this install writes with `-ProfileId <SteamID64>`. Yours is the 17-digit numeric directory holding an `Options.jopt` that the game you just closed had written — the tools leave directories there too (one is literally named `WorkshopTool` and has no `Options.jopt` at all):

   ```powershell
   Get-ChildItem "$env:USERPROFILE\AppData\LocalLow\Snapshot Games Inc\Phoenix Point\Steam" -Directory |
     Where-Object { Test-Path (Join-Path $_.FullName 'Options.jopt') } |
     Sort-Object { (Get-Item (Join-Path $_.FullName 'Options.jopt')).LastWriteTime } -Descending |
     Select-Object Name, FullName
   ```

   The top row is the profile that install just wrote. `connect` never asks for this; only `run` and `batch` do.

6. Start the game again with `-mods` and leave it running until the main menu is visibly settled:

   ```powershell
   Start-Process (Join-Path $PPRoot 'PhoenixPointWin64.exe') -ArgumentList '-mods'
   ```

   A game you started by hand publishes an endpoint exactly like one `run` launched: the mod writes `%LOCALAPPDATA%\ppcli\endpoints\<pid>.json` whenever it is enabled and armed, and `connect` finds it there. `REFUSED: no live PPBridge endpoint` means no such game is running — the mod is not enabled, or the `ppcli-enabled` marker is missing — not that hand-launching is unsupported.

   Nothing deletes that file when the game dies, and it is not meant to: the **client** sweeps it, checking each file's `pid` against the live process list and deleting the ones whose process is gone (`swept stale endpoint …` on stderr). So a leftover `<pid>.json` after a crash is expected and needs no cleanup from you.

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

   `index` pages the live repository, so it needs a settled game; running it before the gate answers is the hang described above. It streams one `page N: ...` progress line per page to **stderr** — a hundred-odd lines on a modded install — while the single JSON object stays on stdout, as it does for every command. Read it with `| ConvertFrom-Json`, not by eye, and check `$LASTEXITCODE`: every verb sets one.

### Before you use `run` or `batch`

They cold-launch a game process of their own, and two of their side effects are destructive rather than merely surprising:

- They read `Options.jopt` before launching and **restore it byte-exact afterwards**. Anything you change in the game's settings during that session — graphics, keybinds, the mod list — is discarded. The restore exists because a mod that fails to load makes the game rewrite `MOD_ACTIVATED` empty, silently disabling every other mod.
- They **delete any existing log** at `%TEMP%\ppcli-<install>-<pid>.log` before launching, so that an empty log cannot be mistaken for a mod that printed nothing.

They also refuse outright if a game is already running from that install, and they stop only the process they started. `connect` does none of this. Point `run` and `batch` at an automation copy, and reach the install you actually play with `connect` only.

### Client parameters

| Parameter | Default | What it does |
|---|---|---|
| `-PPRoot` | `ppcli-install.txt`, else Steam discovery | which install the command means |
| `-ProfileId` | the single profile directory | needed by `run`/`batch` when several profiles exist |
| `-TimeoutSeconds` | `300`, or a plan's own `timeoutMs` + 60 s | the client's own ceiling on one job, after which it cancels the job and answers `timeout`; `plan` derives it when you do not pass one |
| `-InitTimeoutSeconds` | `90` | how long `run` waits for the mod's init line |
| `-PipeTimeoutSeconds` | `30` | ceiling on one pipe frame; a wedged game, not a slow one |
| `-FaultPattern` | empty = any **mod** stack frame | an exception whose stack matches it, logged while the client is waiting, ends the wait as `DEAD RUN` instead of running out the budget; pass a regex to narrow it to one mod |
| `-IgnoreLogFaults` | off | wait out the full budget even when the log faults |
| `-CatalogDir` | `.\catalog` | where `index` writes and `plan` resolves names from |
| `-Force` | off | `deploy` only: write into an install other than the pinned one |

## Security

Stated exactly, so you can decide for yourself.

**The marker gates everything.** Neither entrance opens unless `Mods\PPBridge\ppcli-enabled` sits beside the DLL ([`PPBridgeMain.cs`](src/PPBridgeMain.cs)). `deploy` never creates it. It is re-read every few seconds while the game runs, so **deleting it disarms a running session**: the pipe stops and no new request can reach the mod. A plan already parked keeps ticking so its `finally` block can run. Re-arming requires relaunching the game.

**There are two entrances and they are gated differently.**

- The **pipe** requires a session token. 128 random bits, minted per launch, checked before the request is looked at.
- The **job file** — `Mods\PPBridge\ppcli-jobs.json`, which is how `run` and `batch` reach a game they cold-launched — is read **without a token**, and cannot be otherwise: the token does not exist until the mod loads and mints it. What bounds it instead is the filesystem. The path is fixed, so writing one needs write access to the mod's own folder, which is the same access that lets you replace `PPBridge.dll` outright; it is gated by the marker like the pipe; and it is read once at arm time and then deleted, so it fires for the launch that placed it and never again.

**The token is readable by you, and therefore by anything running as you.** It is written to `%LOCALAPPDATA%\ppcli\endpoints\<pid>.json` — that is what lets the client find the game with no configuration. Comparison ([`Wire.cs`](src/Wire.cs#L80)) has no early exit on the first differing character, so a caller cannot recover the token one character at a time; it is **not** constant-time in the strict sense, because the loop still runs as many times as the longer of the two strings.

**A token holder runs arbitrary code inside the game process, as you.** [`Reflect.ResolveType`](src/Reflect.cs#L199) has no type allowlist. That is the point of the tool, and it is not sandboxed. It is also the trust boundary any mod DLL already has: a mod you install runs as you either way. PPCLI does not widen it; it makes it scriptable.

**About the network.** PPBridge never opens a TCP socket — the only listener is a Windows named pipe ([`PipeServer.cs`](src/PipeServer.cs#L166)), created with `PipeOptions.None`. It does **not** inspect or reject remote clients, and there is no `PipeOptions` flag that would, so "unreachable from another machine" is not something this code enforces: Windows can in principle expose a named pipe over SMB to an authenticated client. What keeps it local in practice is the default pipe DACL — only this user can write to it — together with the session token, which only ever exists in a file under this user's `LocalAppData`. **The token, not the transport, is the trust boundary.** That DACL is Windows' default, not a hand-built one (a hand-built one denied this mod's own client): creator-owner — this user — and Administrators and SYSTEM get full control, while Everyone and Anonymous get read-only, so another identity can open the pipe but cannot write a request into it. The token is the boundary against unrelated local users. It is **not** isolation from another process running as you, nor from an administrator or SYSTEM.

Arm and enable PPBridge only while using it; delete the marker afterwards.

## Limitations

- Every shipped plan has been run in-game with one exception: `plans\situation.json`'s **restore head** is unverified. Its spawn and equip body is the same body as `plans\spawn-squad.json` and `plans\equip-actor.json`, both proven; the snapshot-restore step at the front was never seen to complete. Run it with `restoreFirst:false` to stay on proven ground.
- Six plans declare a `timeoutMs` longer than the client's 300 s default, so `plan` derives its ceiling from the plan's own `timeoutMs` plus 60 s whenever you do not pass `-TimeoutSeconds`; an explicit value still wins and is how you deliberately become the shorter clock. The engine caps a plan's `timeoutMs` at 900 000 ms.
- `plans\unlock-research.json` takes a `ResearchElement.ResearchID`, matched exactly — not a substring and not the def's display name. It usually equals the def name, but not always, which is why `index` records the id separately. `console research_stats` lists them.
- A geoscape save the target install cannot open — usually a mod-set mismatch — fails by **stalling**, not by erroring. `restore` is issue-only because the game exposes no load-completion signal, so the plan's waits are the completion protocol, and a save that never comes up simply times out.
- `plans\build-mission.json` stops in the deployment phase when the player roster is explicit, which is correct: the squad has to be placed. It reports `turnStarted:false` rather than waiting for a turn that no unattended run will ever start.
- `plans\start-mission.json` exposes `loadmap`'s plot and parcel tag filter: pass `tags` as an array and its elements arrive as separate console arguments — `'{"scene":"ALN_PLT_Nest_48x48_A","tags":["plot_a","parcel_b"]}'`. It defaults to empty, which changes nothing.
- `plans\weapon-test.json` needs a playing tactical mission and a shooter that is a **soldier**. `start-mission` leaves a vehicle selected, so the default `"shooter":"@selected"` fails at `assert-enabled` — the failure names the game's own reason, `NoSuitableEquipment`, in `predicate.args`. Take a soldier's handle out of `@faction.TacticalActors` (a vehicle carries `Vehicle_TagDef` in its `GameTags`; see `PLAYBOOK.md`) and pass it as `shooter`. The plan deliberately leaves the spawned enemy and equipped weapon in place.
- The enemy's placement is drawn at random inside the distance band, so a run can fail at `assert-target` (no line of sight), at `landed` (a shot that produced no projectile), or at `assert-nothing-wedged`. **`seed` is a reproducibility knob, not a remedy** — it makes a placement repeat, including a bad one. What actually helps is a re-run, a different `distance`, fewer `shots`, or a **fresh mission**: rehearsal runs that kept failing across four different seeds succeeded immediately on a newly started one.
- When an assertion fails, the failing step's DTO carries `predicate` — the assertion with its variables already substituted — alongside `last`. That is usually where the reason is: `predicate.args` of `["NotDisabled","NoSuitableEquipment"]` says what `last:false` cannot.
- A long volley against a target that dies can end in a named refusal rather than in figures. Once actors start dying, something throws inside `ProjectileLogic.OnTrajectoryEnd`; the stack is cut at the Harmony wrapper, so the throwing code cannot be identified and no mod can be named as the cause. `shots` is accepted from 1 to 100, as a whole number, but accepted is not answerable: a run that asks for more shots than the target survives may refuse, and 100 activations of a 6-projectile burst is 600 impacts against a 512-entry ring, so a max-length volley fails the no-truncation assertion by construction.
- `call` reaches a lot, but not everything: it has only `new`, `get`, `set` and `invoke`, so events cannot be subscribed to; by-ref, `out` and pointer parameters are refused; indexers are reachable only as `invoke get_Item`/`set_Item`; and a tie between two equally-binding overloads is refused, which leaves a member declared identically on several types in one hierarchy unreachable even with an explicit `sig`.
- Handles belong to one process and epoch. A game restart or scene unload invalidates them.
- PPBridge serves one pipe connection at a time. Do not drive one install concurrently from multiple agents.
- Runtime figures quoted on these pages — latencies, timings, dispersion, hit counts — are observations from the machine PPCLI was developed on, not guarantees. Nothing shipped here reproduces them for you.
- Every `file:line` reference on these pages points into Phoenix Point's own **decompiled** assembly. This repository does not contain it and does not ship it; the citations are there so a reader with their own decompile can check the claim.

## License

[CC BY-NC 4.0](LICENSE). Copyright (c) 2026 Morgott.
