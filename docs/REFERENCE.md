# PPCLI — Phoenix Point command-line interface

> **START HERE: [`PLAYBOOK.md`](../PLAYBOOK.md)** — plain intent to the exact command line, one page.
> Read it before digging anything here or in the decompile. This file is the depth behind it, and
> [`README.md`](../README.md) is the overview and the install steps.

Developer mod + PowerShell client for driving Phoenix Point programmatically. A modder's tool, not a
player feature: it is published here and never on the Steam Workshop. Read **SECURITY** below before
you arm it — the endpoint is off by default and that is deliberate.

Licensed CC BY-NC 4.0 (`LICENSE`).

**Every `file:line` citation on this page points into Phoenix Point's own DECOMPILED assembly**, not
into this repository, which neither contains nor ships it. The citations are there so a reader with
their own decompile can check a claim; without one they are provenance, not a link you can follow.
Runtime figures quoted below — latencies, timings, hit counts, dispersion — are observations from the
machine this was developed on. Nothing here reproduces them for you, and none of them is a guarantee.

## Components

| Part | What | Where |
|---|---|---|
| `PPBridge.dll` | Harmony mod, mod id `com.morgott.PPBridge` | `<install>\Mods\PPBridge\` |
| `ppcli.ps1` | PowerShell client (deploy, launch, command, parse) | this directory |

## First run

```powershell
.\ppcli.ps1 deploy                                   # build + copy the DLL into the install
New-Item -ItemType File "<install>\Mods\PPBridge\ppcli-enabled"   # ARM the endpoint (opt-in)
#   ... start the game WITH -mods (no -mods, no mods at all), enable PPBridge once in the
#       in-game mod manager, then leave it running ...
Start-Process "<install>\PhoenixPointWin64.exe" -ArgumentList '-mods'
.\ppcli.ps1 connect state                            # the gate: this must answer before anything else
.\ppcli.ps1 index                                    # ONE TIME, AFTER the gate: builds catalog\defs.ndjson
```

The build needs the reference assemblies the game already ships — `<install>\ModSDK\Assembly-CSharp.dll`,
`<install>\ModSDK\0Harmony.dll` and `<install>\PhoenixPointWin64_Data\Managed\` — plus a .NET SDK and
the .NET Framework 4.7.2 targeting pack. `deploy.ps1` passes `/p:PPRoot=` for you; a stripped copy
with no `ModSDK\` builds against another install via `-RefRoot`.

A game started **by hand** publishes an endpoint exactly like one `run` launched: `PipeServer` writes
`%LOCALAPPDATA%\ppcli\endpoints\<pid>.json` whenever the mod is enabled and armed, whoever started
the process, and `connect` reads it from there (`ppcli.ps1:205-229`).

`deploy` prints the exact `New-Item` line with your own path in it. Delete `ppcli-enabled` when you
are done for the day; the mod can stay enabled, it just goes inert.

**The install is discovered through Steam** (registry `SteamPath` + `steamapps\libraryfolders.vdf`)
when `-PPRoot` is not given. With no install found, or more than one, every command refuses by name
and asks for `-PPRoot "<install folder>"` rather than guessing — but note what that can and cannot
see: only installs inside a **Steam library**. A separate copy kept for automation is not one, so
discovery never becomes ambiguous over it; it finds the single Steam install and that is the game
you play. The ambiguity refusal is therefore not a safety net for the two-install case, which is why
every command now prints the install it chose *and* that a discovered one is the one you play.
`-ProfileId` is discovered the same way, from the single directory under
`%USERPROFILE%\AppData\LocalLow\Snapshot Games Inc\Phoenix Point\Steam\`.

**If you keep a separate copy of the game for automation, say so once.** Discovery finds the install
*Steam* knows about, which is the one you play — so a bare `deploy` writes a mod into it. Put the
automation copy's path in `ppcli-install.txt` beside `ppcli.ps1` (one line, gitignored) and it becomes
the default for every command; `deploy` then REFUSES any other install and names the
`-PPRoot '<path>' -Force` line that proceeds anyway. No such file, no change: a machine with one
install behaves exactly as before.

## SECURITY — what this actually opens

Plainly, so you can decide for yourself:

- **Nothing is on the network — but the code does not enforce that.** `PPBridge` never opens a TCP
  socket; the only listener is a Windows **named pipe** (`src\PipeServer.cs:166`), created with
  `PipeOptions.None`, and it does not inspect or reject remote clients. Windows *can* expose named
  pipes to remote clients over SMB, so what keeps this local in practice is the default pipe DACL —
  only this user can write to it — and the session token, which only ever exists in a file under this
  user's `LocalAppData`. **The token, not the transport, is the trust boundary.**
- **The PIPE needs a session token.** 128 random bits, new every launch (`PipeServer.cs:74`), checked
  before a request is looked at (`:393-400`), compared with no early exit on the first differing
  character (`Wire.cs:80-86`) so the match length does not leak. It is **not** constant-time: the loop
  runs as many times as the longer of the two strings.
- **The JOB FILE does not, and cannot.** `run` and `batch` reach a game they cold-launched through
  `Mods\PPBridge\ppcli-jobs.json`, read as `{id, verb, args}` with **no token** — the token does not
  exist until the mod loads and mints one. Its boundary is the filesystem instead: the path is fixed,
  so writing one needs write access to the mod's own folder, which is the same access that lets you
  replace `PPBridge.dll` outright; it is behind the arm marker exactly like the pipe; and it is read
  **once**, at arm time, then deleted, so it fires for the launch that placed it and never again.
- **The token is readable by you.** It is written to
  `%LOCALAPPDATA%\ppcli\endpoints\<pid>.json`, so **any process running as the same Windows user can
  read it** — which is what lets the client find the game with no configuration.
- **A token holder can run arbitrary code inside the game process, as you.** `call` resolves types by
  name with no allowlist (`src\Reflect.cs:199,207-218`); that is the point of the tool, and it is not
  sandboxed.
- **This is the trust boundary any mod DLL already has.** A mod you install runs as you inside the
  game either way. PPCLI does not widen that boundary; it makes it scriptable.

Because of the last two points the endpoint is **opt-in**: it arms only when a file named
`ppcli-enabled` sits beside `PPBridge.dll` (`src\PPBridgeMain.cs`), and both entrances — the pipe and
the job file — are behind that check. `deploy` never creates it.

**Deleting the marker disarms a session that is already running.** `Runner.Update` re-reads it every
300 frames (~5 s at 60 fps); when it is gone the pipe is stopped and no new request can reach the
mod. The frame pump deliberately keeps running: a plan already parked has a `finally` block to run,
and killing the tick would strand whatever it took. Re-arming needs a relaunch. **Enable the mod, and
arm it, only while you are using it.**

The pipe uses Windows' default DACL rather than a hand-built one (see *No DACL on the pipe* below for
why a hand-built one broke). Another local user can open the pipe but cannot write a request into it;
the token is the boundary against them. It is **not** isolation from another process running as you,
nor from an administrator or SYSTEM.

## Usage

```powershell
.\ppcli.ps1 deploy                                         # build + copy DLL to install
.\ppcli.ps1 run ping                                       # handshake + build check
.\ppcli.ps1 run state                                      # scene/phase/level snapshot
.\ppcli.ps1 run console '{"command":"ct_version","args":[]}' # any native console command
.\ppcli.ps1 batch jobs.json                                 # JSON array of {id, verb, args}
.\ppcli.ps1 connect wait '{"ready":true,"timeoutMs":120000}' # frame-polled, never blocks Update()
.\ppcli.ps1 connect snapshot '{"name":"before"}'            # save, and wait for it to finish
.\ppcli.ps1 connect var '{"name":"ai_enabled","value":"false"}'
.\ppcli.ps1 plan .\plans\spawn-at-coordinate.json '{"x":11.5,"z":-4.5,"faction":"alien"}'
.\ppcli.ps1 plan .\plans\aim-and-run.json '{"x":-0.5,"y":0.0,"z":14.5,"command":"info","cmdArgs":[]}'
.\ppcli.ps1 index                                           # ONE TIME: write catalog\defs.ndjson
```

**`run` and `batch` are not read-only, and neither side effect is obvious.** They snapshot
`Options.jopt` before launching and **restore it byte-exact afterwards** — anything changed in the
game's settings during that session is discarded (the restore exists because a mod that fails to load
makes the game rewrite `MOD_ACTIVATED` empty, silently disabling every other mod). They also
**delete any existing log** at `%TEMP%\ppcli-<install>-<pid>.log` before launching, so an empty log
can never be mistaken for a mod that printed nothing. They refuse if the install is already running,
and they stop only the PID they started. `connect` does none of this.

## The def catalog — say "crabman", not `Crabman_Gunner_TacCharacterDef`

`index` pages `find {all:true}` against **your own running** game and writes two files atomically
(temp + move, so a half-written catalog can never be read). **Run it once before anything else** —
no catalog ships with this repo, because one built on one install carries that install's mods and
pins that install's game build. Everything still works without it: values are passed through
untouched with a warning, so exact def names resolve on a fresh clone and casual names do not.

| File | What |
|---|---|
| `catalog\defs.ndjson` | one def per line, `{"f":family,"n":defName,"t":Type}`; research rows also carry `"id"` — `ResearchDef.Id` (`ResearchDef.cs:33`), which is what `GetResearchById` matches (`Research.cs:763-765`, `ResearchElement.cs:221`) and is **not** the def name. Generated by `index`; not committed. |
| `catalog\meta.json` | game version, PPBridge build stamp, generation time, row count, defs scanned, and research-id coverage (`researchIds` must equal `researchRows` — `index` refuses to write otherwise). `names.ps1` compares `rows` against the real line count and reports a catalog whose two files disagree as STALE. |
| `catalog\aliases.ndjson` | curated, hand-written, **committed**: `{"a":[casual names],"f":family,"n":exact def name}`. |

Families are derived from the def TYPE (`names.ps1`), never from a hand-maintained list of def names:
`actor`, `item`, `status`, `research`, `mission-type`, `map-plot` — and `other`, which is what the
family rules do not claim. `other` rows are DROPPED at index time — no plan var can ever resolve one,
and they are the bulk of the repository. Your own numbers are in `catalog\meta.json` after `index`:
the run that produced the catalog in this working tree scanned `defsScanned` 23,015 defs and kept
`rows` 3,389.

`plan` then normalises the caller's `defName`, `itemName` and `researchId` — and the plan file's own
`vars` defaults for those three — **locally, before the plan is sent** — the plan JSON itself goes over the wire unchanged. Precedence is deterministic and there
is no fuzzy scoring: **exact def name (or exact research id) → exact alias → UNIQUE substring →
refuse with the candidate list on stderr.** Ambiguity always refuses; guessing here spawns the wrong
creature into a real save. With no `defs.ndjson` the value is passed through with a warning, so an
exact def name works before anyone has ever run `index`.

```powershell
.\ppcli.ps1 plan .\plans\spawn-squad.json '{"defName":"crabman","count":3}'   # -> Crabman_Gunner_TacCharacterDef
```

Offline checks, no game needed — each also takes `-Falsify`, which corrupts every expectation and
demands that all of them fail, the only proof the assertions are wired to anything:

```powershell
pwsh -NoProfile -File .\tests\resolve-names.tests.ps1   # name resolution
pwsh -NoProfile -File .\tests\paths.tests.ps1           # install/profile discovery, deploy + arm refusals
pwsh -NoProfile -File .\tests\index.tests.ps1           # `index` paging and its snapshot-integrity refusals
pwsh -NoProfile -File .\tests\plans.tests.ps1           # every shipped plan: finally, output, timeoutMs
pwsh -NoProfile -File .\tests\waits.tests.ps1           # log-fault detection and the mod-frame allowlist
pwsh -NoProfile -File .\selfcheck\client-pipetest.ps1   # the PowerShell client against a stand-in server
dotnet build .\selfcheck\SelfCheck.csproj -c Release /p:PPRoot="<install>"
dotnet .\selfcheck\bin\Release\SelfCheck.dll            # PPBridge's pure half
```

Five suites, and all five ship here — an earlier version of this list named three of them and left
`index`, `plans` and `waits` documented nowhere at all.

## Parameters

| Param | Default | Notes |
|---|---|---|
| `-PPRoot` | *`ppcli-install.txt`, else discovered through Steam* | install to drive; required when you keep more than one and have not pinned one |
| `-ProfileId` | *the single profile directory* | Steam profile id; required when you have more than one |
| `-Force` | off | `deploy` only: write into an install other than the pinned one |
| `-TimeoutSeconds` | `300` | the client's own ceiling per job; at it the job is CANCELLED and the answer is `{"status":"timeout","cancelled":true}` |
| `-InitTimeoutSeconds` | `90` | `run` only: how long to wait for the mod's init line |
| `-PipeTimeoutSeconds` | `30` | ceiling on ONE pipe frame — a wedged game, not a slow one |
| `-FaultPattern` | *empty = any **mod** stack frame* | an exception whose stack matches it, logged **while the client is waiting**, ends the wait as `DEAD RUN` instead of running out the budget. A "mod frame" is decided by a namespace allowlist (`Test-ModFrame` / `$PPCLI_EngineRoots`, `waits.ps1`): a stack line whose root namespace is not the engine's. A bare `NullReferenceException` is written by a perfectly healthy session, which is why the signal is the frame and not the word. Pass a regex to narrow it to one mod |
| `-IgnoreLogFaults` | off | wait out the full budget anyway; for measuring the fault the fast-fail would cut short |
| `-CatalogDir` | `.\catalog` | where `index` writes and where `plan` resolves names from |

Six shipped plans carry a `timeoutMs` longer than the client's 300 s default — `build-mission`,
`load-mission`, `situation`, `start-campaign`, `start-mission`, `weapon-test` — so the first thing a
newcomer does, run a shipped plan, used to be cancelled mid-run and reported as a timeout on a
perfectly healthy game. `plan` therefore **derives** its ceiling: with no explicit `-TimeoutSeconds`
it becomes the plan's own `timeoutMs` + 60 s (so the plan's deadline fires first and names the step),
announced on stderr. Derived rather than raised to a bigger constant, because a constant rots the
moment a plan changes; an explicit `-TimeoutSeconds` still wins, which is how a caller deliberately
becomes the shorter clock. The engine's own hard cap is `Plan.MaxWaitMs`, 900 000 ms.

## Output contract

- **Exactly ONE compact JSON object on stdout.** Everything else on stderr.
- `stdout | ConvertFrom-Json` always works — no decoration, no banner.

## Verbs

| Verb | Input | Returns |
|---|---|---|
| `ping` | — | `{ok, protocol, build}` handshake |
| `state` | — | `{ok, phase, scene, level, levelState}` |
| `console` | `{command, args[]}` | command result or structured error |
| `var` | `{name}` / `{name, value}` | the console's **variable** surface — `console` cannot reach it |
| `call` | `{op, assembly, type, target, member, sig, typeArgs, args, value}` | `{ok, value}` / `{ok, void}` / `{ok, code, error}` |
| `roots` | — | `{ok, roots{alias: value}}` — late-bound entrances |
| `types` | `{pattern, assembly}` | matching type full names, capped at 100 |
| `members` | `{type\|h, assembly, filter}` | declared + inherited members, capped at 400 |
| `inspect` | `{h, filter}` | the handle's identity **and** its type's members |
| `items` | `{h, page, pageSize}` | one explicitly requested page of a collection — **`page` is 0-based** (`Reflect.cs:1112,1117`); `page:1` on a 14-item collection with `pageSize:20` returns nothing |
| `release` | `{h}` | `{ok, released, held}` |
| `find` | `{query, type, assembly}` | defs by name substring or exact guid → `{name, guid, type}`, capped at 100 |
| `find` (enumerate) | `{all:true, page, pageSize, query?, type?}` | the whole repository, ordinally sorted and paged → `+{total, page, pageSize, hasMore}`. **`all` is required and must be a real boolean** — a missing/empty `query` on its own still refuses, so a typo'd variable can never become a repository dump. `pageSize` defaults to 200 (≈30 KB, well inside the 64 KB response cap) and is capped at 200. This is what `ppcli.ps1 index` pages. |
| `wait` | `{ready\|phase\|call, not, timeoutMs, everyFrames}` | `{ok, waitedMs, polls, value}` or `{code:"timeout", last, lastError, predicate}` |
| `observe` | `{action: start\|stop\|mark\|read\|status, aim[3]}` | where every projectile LANDED, plus hit/miss counts and dispersion |
| `snapshot` | `{name, timeoutMs}` | `{ok, name}` — waits for the save to actually finish |
| `restore` | `{name}` | `{ok, issued:"load_game", note}` — issue only; load has no completion signal |
| `plan` | `{plan:{steps,finally,vars,output}, vars}` | one request, one structured result, per-step trace |
| `status` / `cancel` | `{jobId}` | job-table questions, answered on the pipe thread |

`batch` is implemented and shipped: one cold launch for a JSON array of `{id,verb,args}`. The gameplay
verbs an earlier spec wanted in C# (`mission.load`, `spawn.actor`, `res.set`, `equip`) ship instead as
parameterised files in `plans\` — see **Plan library** below.

## P2 — the `call` reflection runtime

`call` is structured JSON, never a parsed `Type.Member(args)` string. Implementation:
`src\Reflect.cs`, which names **no** Unity or game type — the four game facts it needs
(`RootsProbe`, `DefByGuid`, `AllDefs`, `UnityAlive`) arrive as `Protocol` delegates that
`PPBridgeMain.OnModEnabled` installs. That is what lets `selfcheck\` exercise the binder, the
overload scorer, the handle table and the DTO caps with no game running.

### Ops

| `op` | Needs | Notes |
|---|---|---|
| `invoke` | `type` (static) or `target` (instance) + `member` | `sig` / `typeArgs` when asked for |
| `get` | `member` | property or field, static or instance |
| `set` | `member` + `value` | refuses readonly/const/write-only |
| `new` | `type` + `args` | constructors are never inherited |

`target` is a handle (`"h:3:17"`), a **root alias** (`"@tac"`, re-resolved live every request), or
`{"$h":"h:3:17"}`. An explicit `type` alongside a target is a filter for reaching a base-class member.

### Argument envelopes

`{"$h":"h:e:i"}` handle · `{"$enum":"Player","type":"..."}` · `{"$type":"System.String"}` ·
`{"$def":"<guid>"}` · `{"$array":[...],"type":"..."}` · `{"$v2":[x,y]}` · `{"$v3":[x,y,z]}` ·
`{"$quat":[x,y,z,w]}`. Bare JSON works too: strings, booleans, numbers, `null`, and a bare array
binds using the parameter's own element type. Invariant culture throughout.

**No silent conversions.** A string is never parsed into a number (`"7"` into an `int` is refused —
it is far more often a mistake than an intention). An integer binds to a narrower integer parameter
only when the value survives the round trip. `$v2`/`$v3`/`$quat` bind by constructing the
*parameter's own type* from N floats, falling back to the named Unity type only for a loose
parameter.

### Overload selection

Scores: exact `0`, assignable/nullable `1`, enum-or-guid parse `2`, lossless/range-checked numeric
widening `3`. Lowest **unique** total wins. A tie is refused with the tied signatures and demands an
explicit `sig` — reflection order never decides. Ambiguous type names are refused the same way, with
the candidates listed. v1 rejects by-ref and pointer parameters; open generics need `typeArgs`.

### Handles and result projection

`h:<epoch>:<id>`, a strong lease with a 900 s TTL and a 512-entry LRU cap. `release` frees one;
**scene unload bumps the epoch** and every outstanding handle then comes back as a named refusal
rather than a crash inside a destroyed `UnityEngine.Object`. Three distinct refusals — expired,
previous-epoch, destroyed — because they are three different mistakes.

Projection **never** calls an arbitrary `ToString`, **never** enumerates an `IEnumerable`, **never**
walks properties or getters and **never** serialises fields automatically:

- primitives, strings (clipped at 2000 chars), enums (`{"$enum","type"}`) → inline
- a value type with 1–4 public primitive fields (i.e. `Vector3`) → inline as `{type,x,y,z}`
- a collection → `{h, type, count, collection:true}`; `count` only when the object already knows it
- anything else → `{h, type, name, instanceId, guid}` — the last three read only from
  `UnityEngine.Object`/`BaseDef`, which is a whitelist, not a walk
- a **reflection** response over **64 KB** is refused with advice, not truncated into a lie. That is
  the projection budget, and it is not the transport limit: the pipe frame itself is capped at
  **262 144 bytes (256 KiB)** in each direction, and the client refuses a larger request before
  connecting

### Root aliases (`roots`)

Re-read from the game on **every** request; a cached root would keep answering with the controller
of a mission that ended. Every accessor is the game's own — `GameUtl.cs:38,51,101`,
`TacticalLevelController.cs:155,161,165`, `TacticalView.cs:148,189`, `GeoLevelController.cs:209`.

`game` · `phoenix` · `defs` · `level` · `geo` · `tac` · `map` · `view` · `faction` · `selected`.
A wrong-phase alias answers `null` (not "no such alias") — those are different answers.

### Spawn an actor at a CHOSEN COORDINATE — VERIFIED IN-GAME 2026-08-25

The thing the 344 console commands provably cannot do: `spawn` takes its position from the cursor,
has no coordinate argument, and never checks standability (`TacticalDeployZone.cs:366,395-404`).
This is the P2 acceptance sequence, and it is **no longer hypothetical**.

> **Verified in-game 2026-08-25**, map `SCV_PLT_Ambush_56x56_A`, `levelState: Playing`, against a
> live `connect` pipe. Requested `(11.5, 0.0, -4.5)`, achieved
> `(11.5, 0.0, -4.5)` — **delta exactly zero** — `InPlay: true`, and the new `Crabman_1` shows up in
> the alien faction's own `Actors` enumeration. 23 `call` round-trips, zero failed steps.
> The sequence below is the transcript, not a design. Two things in the previously-documented
> version were **wrong**; both are corrected in place and called out under *Corrections*.

Run it with a tactical mission loaded and playing (`state` → `"phase":"tactical"`). `<X>` are handles
read out of the previous reply's `result.value.h`. Handles here are from one real run — yours differ.

```powershell
# 0 — the live entrances
.\ppcli.ps1 connect state          # -> {"phase":"tactical","scene":"SCV_PLT_Ambush_56x56_A",...}
.\ppcli.ps1 connect roots          # -> game/phoenix/defs/level/tac/map/view/faction/selected

# 1 — DERIVE the coordinate from the live map; never invent one. Any actor on the map is an anchor.
.\ppcli.ps1 connect call '{"op":"get","target":"@selected","member":"Pos"}'
#   -> {"type":"UnityEngine.Vector3","x":7.50000143,"y":0.043005,"z":-4.499997}
#   Target chosen: 4 m along +X from that anchor -> (11.5, ?, -4.5). Y is resolved by SnapXYZ below.

# 2 — the actor definition, by name, out of the def repository
.\ppcli.ps1 connect find '{"query":"Crabman","type":"PhoenixPoint.Tactical.Entities.TacActorDef"}'
#   -> 62 defs. Used Crabman_Gunner_TacCharacterDef = 0034048e-173a-7dd4-69b3-3b90d1c605bc
#   NOTE the `type` filter is a base-type filter: every hit reports type TacCharacterDef.

# 3 — def -> (ComponentSetDef, ActorInstanceData), exactly as the `spawn` command does it
#     (TacticalDeployZone.cs:412-413, ActorDeployData.cs:78,93)
.\ppcli.ps1 connect call '{"op":"new","type":"PhoenixPoint.Common.Levels.ActorDeployment.ActorDeployData","args":[{"$def":"0034048e-173a-7dd4-69b3-3b90d1c605bc"}]}'   # -> <DD>
.\ppcli.ps1 connect call '{"op":"invoke","target":"<DD>","member":"InitializeInstanceData","args":[]}'
.\ppcli.ps1 connect call '{"op":"get","target":"<DD>","member":"ComponentSetDef"}'
#   -> <CSD>  Base.Core.ComponentSetDef "Crabman_Template_ComponentSetDef(Clone)"
.\ppcli.ps1 connect call '{"op":"get","target":"<DD>","member":"InstanceData"}'
#   -> <INST> PhoenixPoint.Tactical.Entities.TacActorInstanceData

# 3b — OPTIONAL loadout, before the spawn: everything about the unit is a settable field on
#      TacActorInstanceData (EquipmentItems / Stats / BaseWill / AbilityTraits ...). See
#      `plans\spawn-at-coordinate.json` for the shape.

# 4 — whose side it is on. GetFactionByCommandName takes the console faction name.
#     "" = the current turn's faction; "alien" was used here so the Crabman lands on its own side.
.\ppcli.ps1 connect call '{"op":"invoke","target":"@tac","member":"GetFactionByCommandName","args":["alien"]}'  # -> <FAC>
.\ppcli.ps1 connect call '{"op":"get","target":"<FAC>","member":"TacticalFactionDef"}'
#   -> <FACDEF> "Alien_TacticalFactionDef"
.\ppcli.ps1 connect call '{"op":"get","target":"<FAC>","member":"ParticipantKind"}'
#   -> {"$enum":"Intruder","type":"PhoenixPoint.Common.Levels.Missions.TacMissionParticipant"}

# 5 — THE COORDINATE: snap it to a floor, then validate it the way the engine does.
.\ppcli.ps1 connect call '{"op":"get","type":"PhoenixPoint.Common.Utils.UnityLayers","member":"FloorAllMask"}'
#   -> <MASK>. Comes back as a HANDLE, not inline: UnityEngine.LayerMask's only field is private,
#      so it misses the "1-4 public primitive fields" inline rule. Pass it on as {"$h":"<MASK>"}.
.\ppcli.ps1 connect call '{"op":"invoke","target":"@map","member":"SnapXYZ","args":[{"$v3":[11.5,2.0,-4.5]},{"$h":"<MASK>"},0.5,true]}'
#   -> {"type":"UnityEngine.Vector3","x":11.5,"y":0.0,"z":-4.5}   = the snapped point <P>
#      Start the probe ABOVE the floor (y=2.0); SnapXYZ casts down onto the mask.
.\ppcli.ps1 connect call '{"op":"get","target":"<CSD>","member":"Components"}'
#   -> <COMP> {"type":"Base.Defs.ObjectDef[]","count":15,"collection":true}
.\ppcli.ps1 connect items '{"h":"<COMP>","pageSize":20}'
#   -> take the entry of type PhoenixPoint.Tactical.Entities.TacticalNavigationComponentDef (<NAV>,
#      here "Crabman_NavigationDef") and PhoenixPoint.Tactical.Entities.TacticalPerceptionDef
#      (<PERC>, here "Crabman_PerceptionDef").
.\ppcli.ps1 connect call '{"op":"invoke","target":"@map","member":"CanStandAt","args":[{"$h":"<NAV>"},{"$h":"<PERC>"},{"$v3":[11.5,0.0,-4.5]}]}'
#   -> true. false = pick another point and re-snap. NavMeshMap.cs:215; TacticalMap : NavMeshMap, so
#      the binder finds it by walking the hierarchy. The 3-arg overload wins on arity alone.

# 6 — SPAWN AT THAT COORDINATE (TacticalDeployZone.cs:257)
.\ppcli.ps1 connect call '{"op":"invoke","type":"PhoenixPoint.Tactical.Levels.ActorDeployment.TacticalDeployZone","member":"SpawnActor","args":[{"$h":"<CSD>"},{"$h":"<INST>"},{"$h":"<FACDEF>"},{"$enum":"Intruder","type":"PhoenixPoint.Common.Levels.Missions.TacMissionParticipant"},{"$v3":[11.5,0.0,-4.5]},{"$quat":[0,0,0,1]},null]}'
#   -> {"h":"<ACTOR>","type":"PhoenixPoint.Tactical.Entities.TacticalActor","name":"Crabman_1",...}

# 7 — the housekeeping the console command does after the spawn (TacticalDeployZone.cs:415-422)
.\ppcli.ps1 connect call '{"op":"set","target":"<ACTOR>","member":"Source","value":{"$h":"<ACTOR>"}}'
.\ppcli.ps1 connect call '{"op":"get","target":"@tac","member":"SituationCache"}'    # -> <SC>
.\ppcli.ps1 connect call '{"op":"invoke","target":"<SC>","member":"Invalidate","args":[]}'
.\ppcli.ps1 connect call '{"op":"invoke","target":"@view","member":"ResetCharacterSelectedState","args":[]}'
#   `spawn` also calls tacticalActor.StartTurn() (line 419) but ONLY when the new actor's faction is
#   the level's CurrentFaction. Spawning onto a faction that is not on turn — as here — must skip it.

# 8 — PROVE it. Three independent checks; a non-error return from step 6 proves nothing on its own.
.\ppcli.ps1 connect call '{"op":"get","target":"<ACTOR>","member":"InPlay"}'   # -> true
.\ppcli.ps1 connect call '{"op":"get","target":"<ACTOR>","member":"Pos"}'      # -> 11.5, 0.0, -4.5
.\ppcli.ps1 connect call '{"op":"get","target":"<FAC>","member":"Actors"}'     # -> <ROSTER>
.\ppcli.ps1 connect items '{"h":"<ROSTER>","page":1,"pageSize":40}'            # -> "Crabman_1" present
```

**The trap in step 6:** `ActorSpawner.cs:23` calls `DoEnterPlay()` only when `level.IsPlaying`. A
spawn before the level plays is deferred, not lost — `TacticalLevelController.OnLevelStart:636-639`
enters every actor into play — but `InPlay` will read `false` until then. On a Playing level it read
`true` on the very next round-trip, with no wait and no pumping.

#### Corrections to the previously-documented sequence

- **The component type names were wrong.** The old text said to pick the entries ending in
  `NavMeshNavigationComponentDef` and `PerceptionComponentDef`. No such entries exist in a real
  `ComponentSetDef.Components`. The actual types are `TacticalNavigationComponentDef` and
  `TacticalPerceptionDef` (both in `PhoenixPoint.Tactical.Entities`). Anyone following the old
  wording would have found nothing to pass to `CanStandAt`.
- **~~The asset preload is NOT required~~ — that claim was WRONG, and it is back in the sequence.**
  See *The preload, and the false "not required"* below. The transcript above still shows the run as
  it happened — without a preload — because the actor really did come up live, in play and correctly
  positioned. What it did **not** come up with, and what nobody looked at, is a model.
- `Source = the actor itself` looked like a typo and is not: `TacticalDeployZone.cs:415` really does
  `tacticalActorBase.Source = tacticalActorBase`. Kept, and it applies cleanly through `set`.

#### The preload, and the false "not required"

**The defect.** Every spawn plan shipped without an asset preload. Actors spawned by
`spawn-squad.json` exist, act, hold their position and appear in `Map.GetActors` — and are
**INVISIBLE**. Observed in-game 2026-08-25 on `DA_PLT_SLM_24x48_SAN0`: five defs absent from that
mission — `S_Fishman_Praetorian`, `S_SirenSuper`, `S_Chiron_FireWorm`, `Acheron`,
`Queen_Gatekeeper` — all spawned with no model (one rendered as a plant-like placeholder, the rest
as nothing at all).

**Why the original test proved nothing.** The 2026-08-25 P2 run spawned a `Crabman_Gunner` into a
map that **already contained Crabmen**. That def's addressable assets were therefore already
resident, so the spawn could not have failed for want of them however the preload was handled. The
run measured a loaded def and concluded a general capability. Assets are per-**def**: the only case
the test could speak to was the one case that could never break.

> **The general lesson, and it is not about assets.** A capability verified once against
> already-loaded state is **not verified**. Before writing "X is not required", ask what the test
> would have looked like if X *were* required — if the answer is "exactly the same", the test is
> evidence of nothing. The old text even carried the reason for its own doubt ("this Crabman's
> assets were already resident … that case is untested") in the same paragraph that declared the
> step unnecessary.

**The fix, in the plan files alone — `src\` was not touched.** `spawn-at-coordinate.json`,
`spawn-squad.json` and `situation.json` each preload the def before the spawn, and **wait** for it:

```jsonc
{ "id": "assets-loader",  "verb": "call", "save": "AL",
  "args": { "op": "get", "target": "@tac", "member": "AssetsLoader" } },
{ "id": "preload-crt",    "verb": "call", "save": "CRT",
  "args": { "op": "invoke", "target": "${AL.value.h}", "member": "LoadRoots",
            "args": [ [ { "$def": "${DEF.defs[0].guid}" } ], "ppcli" ] } },
{ "id": "loader-timing",  "verb": "call", "save": "TIM",
  "args": { "op": "get", "target": "${AL.value.h}", "member": "Timing" } },
{ "id": "preload-start",  "verb": "call", "save": "UPD",
  "args": { "op": "invoke", "target": "${TIM.value.h}", "member": "Start",
            "args": [ { "$h": "${CRT.value.h}" }, null ] } },
{ "id": "preload-done",   "verb": "wait",
  "args": { "timeoutMs": "${preloadTimeoutMs}", "everyFrames": 2,
            "call": { "op": "get", "target": "${UPD.value.h}", "member": "Stopped" } } }
```

Three things in that are the whole design:

- **`LoadRoots` (`AssetsReferencesLoader.cs:71`) rather than `StartLoadingRoots` (`:58`).** It is
  the same acquisition — `LoadRoots` *calls* `StartLoadingRoots`, so the loader still owns the roots
  and `TacticalLevelController.cs:416` still releases them on teardown — but it is an
  `IEnumerator<NextUpdate>` that ends only once `_loadCrts` is empty. `StartLoadingRoots` returns
  `void` and hands back nothing to wait on. This is the game's own idiom
  (`TacticalLevelController.cs:557-569`).
- **`Timing.Start(coroutine, catchException)` (`Timing.cs:246`) gives the completion signal.** Its
  `IUpdateable.Stopped` is a **positive**, live, **single-call** predicate — which is exactly what
  `wait` needs. The alternatives are not: the plan engine has no `not`, and both
  `AssetsReferencesLoader.IsLoading` (`:19`) and `AssetsManager.IsLoading()` are the wrong polarity
  *and* loader-**global** (`AssetsReferencesLoader.cs:19,94`), so negating one live inside a `wait`
  is not expressible. The trailing `null` is the optional `catchException`: the binder matches arity
  **exactly** (`Reflect.cs:490`), so an omitted optional parameter is a no-match, not a default.
- **It is ASYNCHRONOUS, and that is the trap the old note already named.** Firing the load and
  calling `SpawnActor` on the next round-trip waits for nothing whatsoever.

**Loading is per-def and the acquisition is refcounted** (`AssetsManager.cs:75-97`), so preloading a
def that is already resident costs a refcount bump and one frame — there is no reason to make the
step conditional.

##### VERIFIED IN-GAME 2026-08-25 — the A/B, on one map, with a control

A tactical save on `SCV_PLT_Ambush_56x56_A`, `levelState: Playing`.
Def used: **`S_Chiron_FireWorm_TacCharacterDef`**, chosen because it is **NOT resident** on that map —
`console assets_loaded_roots list_all` lists exactly eight loaded `*ComponentSetDef` roots
(`Acheron`, `Crabman_Template`, `Crate`, `Fishman`, `IntruderExitZone`, `PlayerExitZone`, `Siren`,
`Soldier_Template`) and **no `Chiron` root at all**. That check is the thing the original test never
did.

**The observable** — the actor's own visual hierarchy, not `ok:true`:
`get gameObject` → `GetComponentsInChildren<UnityEngine.Renderer>(true)` and
`…<UnityEngine.Transform>(true)`, read straight off the live handle.

| Spawn | `Renderer` | `Transform` |
|---|---|---|
| pre-fix plan, Chiron assets **COLD** | **3** | **541** |
| fixed plan, **preloaded** | **7** | **637** |
| *pre-fix plan again, after the assets were warm* | **7** | **637** |
| `Fishman_12`, a resident species already on the map | 9 | — |

The third row is the control that makes this a proof rather than a coincidence: the **same
preload-less plan** produces the **same 7 / 637** once the assets happen to be resident. The only
variable that moves the number is asset residency — not the spawn, not the position, not any other
step. A cold Chiron comes up missing **4 of its 7 renderers and 96 transforms**: present, in play,
correctly positioned, and wearing most of nothing.

##### "Every spawn is called *источник заражения*" — it is the Chiron's own name

Reported as a second symptom and it is **not a defect**. `TacticalActorBase.DisplayName` (`:230` →
`GetDisplayName():361`) reads `ViewElementDef.DisplayName1.Localize()`, and `ViewElementDef` comes
off the actor's view (`:301`). Read back live, every def gets **its own** view element and **its own**
name:

| Actor | `DisplayName` | `ViewElementDef` |
|---|---|---|
| `Chiron_2` — spawned, assets cold | `Источник заражения` | `E_View [Chiron_ActorViewDef]` |
| `Chiron_3` — spawned, preloaded | `Источник заражения` | `E_View [Chiron_ActorViewDef]` |
| `Queen_4` — spawned, preloaded | `СКИЛЛА` | `E_ViewElement [Queen_ActorViewDef]` |
| `Fishman_12` — deployed by the mission | `ТРИТОН-РАЗБОЙНИК` | `E_View [Fishman_ActorViewDef]` |
| `Siren_11` — deployed by the mission | `СИРЕНА-ПОДСТРЕКАТЕЛЬНИЦА` | `E_View [Siren_ActorViewDef]` |
| `Acheron_7` — deployed by the mission | `АХЕРОН` | `E_View [Acheron_ActorViewDef]` |
| `Crabman_6/9/10` — deployed by the mission | `АРТРОН-КАРАТЕЛЬ`, `АРТРОН-ЩИТОНОСЕЦ (ЧЕМПИОН)` | `E_View [Crabman_ActorViewDef]` |

The localization key behind the Chiron's label is **`KEY_ALN_CHIRON_NAME`**, and the Queen's is
`KEY_ALN_QUEEN_NAME`. So *Источник заражения* is simply what the Russian build calls a Chiron — the
name is correct, resolved from the right def, and **identical with and without the preload**. Nothing
about identity is degraded: the requested def reaches the spawn intact. Two consequences worth
keeping: a name that reads oddly is a **localization** question, never evidence about the spawn path;
and the preload fixes the **model**, not the label — do not expect it to change a name.

**Can a preload issued AFTER the fact rescue an already-spawned actor? UNPROVEN — leaning no.** In
the controlled attempt the cold Chiron read `3 / 541` and read **`3 / 541` again** after a later
preload of its own def completed in the same mission: no repair. One earlier, uncontrolled reading
of a different cold Chiron went `3` → `14 / 552` renderers/transforms after two further spawns —
neither the full `7 / 637` nor a stable result, and it did **not** reproduce. `AddonsCharacterBuilder`
does rebuild characters off `_assetsLoader.FinishedLoading` (`:58,137,186`), which is the plausible
mechanism, but one non-reproducing observation is not a finding. The five invisible actors that
prompted the question went with their game session before this could be tested against them.

#### What this run taught about the binder

- **Root aliases carried the whole sequence.** `@tac`, `@map`, `@view`, `@selected` were each used as
  a live `target` with no handle bookkeeping. `@selected` is the cheapest way to get a real,
  guaranteed-valid map coordinate to derive from.
- **Envelopes that worked exactly as specified, first try, no retries:** `{"$def":guid}` into a
  `ComponentSetDef`-producing ctor, `{"$h":...}` for every intermediate, `{"$v3":[x,y,z]}` into both
  `SnapXYZ` and `SpawnActor` (bare 3-element arrays would work too), `{"$quat":[0,0,0,1]}`, and
  `{"$enum":"Intruder","type":"...TacMissionParticipant"}`. A bare `null` binds a reference parameter.
- **Bare JSON floats bind to `float` parameters** — `0.5` and `true` went into `SnapXYZ` untouched.
- **The 7-arg `SpawnActor` overload resolved with no `sig`.** Nothing tied, nothing was ambiguous, and
  the static `type` + `member` form reached it without any instance of the deploy zone.
- **Surprise worth remembering:** a `get` that returns a lazy iterator (`TacticalFaction.Actors` →
  `TacticalMap+<GetTacActors>d__61`) projects as a plain handle with **no `count` and no
  `collection:true`** — the object genuinely does not know its own size. `items` still pages it
  correctly. Do not read the missing `count` as "not a collection". `items` on such a handle
  **omits** `count` entirely rather than emitting an empty one (fixed in P3 — a `"count":null` reads
  as "zero items"); `returned`, `page`, `pageSize` and `hasMore` are always there.
- Handles stayed valid across ~30 separate connections over several minutes. One request per
  connection is not one request per session: the 900 s TTL is what matters, not the pipe.

### Deliberate v1 cuts (`ponytail:` in the source)

- By-ref / `out` parameters are refused. A one-shot JSON request has no out-value protocol.
- Indexers are not reachable through `get`/`set`; `invoke get_Item` / `set_Item` instead.
- `double` → `float` is allowed at widening score even though it is formally narrowing. JSON has one
  fractional number type and nearly every game API takes `float`; the strict rule would refuse
  `12.5` for a `Vector3` component. Integers, where truncation is a genuine bug, stay range-checked.
- Def handles are not pinned across an epoch bump. `find` returns guids, so a def costs one cheap
  call to get back; a pin list would be state to keep correct for nothing.
- No `select` (dot path) / `limit` post-filter on the DTO yet. Depth is structurally 1 because
  nothing here recurses, so the byte cap and the page cap are the whole story.

### Where the spec was wrong against real code

- **"all handles released on disconnect" cannot apply.** The pipe is one request per connection
  (`PipeServer` serves a single frame and closes), so there is no session to disconnect from — a
  release-on-disconnect would destroy every handle between two calls of the same sequence. What
  actually bounds handle lifetime here: TTL, the LRU cap, explicit `release`, the scene-unload epoch
  bump, and `OnModDisabled`.
- **"registered Unity conversion 4" has nothing to register.** No conversion is registered in v1, so
  the score exists in the scale and is never awarded.
- **`SaveWithName` does not return an `IUpdateable`.** It returns `IEnumerator<NextUpdate>`
  (`PhoenixSaveManager.cs:549`); the `IUpdateable` whose `Stopped`/`Exception` is the completion
  signal comes from **`Timing.Start`** (`Timing.cs:246-254`). The distinction matters because
  `SaveWithName`'s enumerator on its own is inert until something starts it.
- **`save_game` is not idempotent and the spec's "thin wrapper" therefore is not enough.**
  `EnsureUnique` (`PhoenixSaveManager.cs:154-168`) renames the second save of a name to `<name>_1`,
  so a literal wrapper over `save_game`/`load_game` restores a stale snapshot the second time round.
  `snapshot` deletes first.
- **`journal` / `undo` (spec §4, §6.3) are not implemented and were not attempted.** Nothing in P3
  mutates a definition, so a mutation journal has nothing to journal yet; it belongs with the def-
  editing verbs, not with the plan engine. The docs deliberately do not imply rollback safety.

## P3 — `wait`, `snapshot`/`restore` and the plan engine

Implementation: `src\Plan.cs`, which — like `Reflect.cs` — names **no** Unity and **no** game type.
The one thing it cannot express as another verb (starting a save and knowing when it stopped)
arrives as two `Protocol` delegates the game half installs. That is what lets `selfcheck\` run the
step loop, the caps, the cleanup block and cancellation with no game.

### Cross-frame jobs, and why `cancel` is now real

`wait`, `snapshot` and `plan` return an `IPending` instead of a DTO. `PPBridgeMain.Runner` parks it
and calls `Tick(cancelled)` **once per frame**, inside the same 8 ms budget as everything else —
never a spin, never a block. Two consequences worth stating plainly:

- **`cancel` works, for these verbs.** The flag `PipeServer.Cancel` sets is handed to the job on its
  very next tick; a plan stops there, **runs its `finally` block**, and reports `code:"cancelled"`.
- **`cancel` still cannot interrupt a synchronous verb.** A `call` is one main-thread invocation and
  nothing can cut into it. That is the honest boundary, and it is exactly why the long-running verbs
  are the ticked ones. A not-yet-started job is refused outright (`Protocol.Refusal`).

At most 16 jobs may be parked at once; the 17th is refused rather than queued.

**The client polls to a terminal state, and its ceiling is honest.** `connect`/`plan` poll `status`
every 250 ms up to `-TimeoutSeconds` (300 s). If the job is still running at that point the client
**cancels it** — which for a plan means the `finally` block runs — and answers
`{"status":"timeout","jobId":…,"waitedSec":…,"cancelled":true,…}`. It never returns the last
`{"status":"running"}` as if that were a result: a plan's own `timeoutMs` may legitimately be longer
than the client's ceiling (see `load-mission.json`), and a bare `running` reads like an answer while
leaving the job holding whatever it changed. Poll it yourself with `connect status '{"jobId":"j1"}'`
or raise `-TimeoutSeconds` when a long plan is intended.

### `wait` — the right predicates, not the convenient ones

| Form | Predicate |
|---|---|
| `{"ready":true}` | `TacticalLevelController.HasAnyTurnStarted` (`:237,631,715`) |
| `{"phase":"tactical"}` | the `state` verb's own phase |
| `{"call":{...}}` | any `call`, truthy result, re-evaluated each poll |
| `+ "not": true` | the same predicate, inverted |

**`not` exists because half the interesting predicates are the wrong way round** — "the ability has
STOPPED executing", "the queue is EMPTY". The `System.Object.Equals(false, x)` idiom the spawn plans
use cannot express those: it needs the live value as an **argument**, and a step's args are
substituted once when the step starts, so the negation would compare the same stale value forever.
An **erroring** predicate satisfies **neither** polarity — `@tac` is null while a mission loads, and a
negated wait that read "the call failed" as "the thing is false" would come back green the instant a
level started loading.

`Level.CurrentState == Playing` flips **earlier** than `HasAnyTurnStarted`, before the level's own
waiters finish (`Level.cs:225`) — waiting on it lands mid-setup, so `ready` deliberately does not use
it. Spawn completion is `{"call":{"op":"get","target":"<actor>","member":"InPlay"}}`
(`ActorComponent.cs:114`, `ActorSpawner.cs:23`).

`timeoutMs` defaults to 30 s and is hard-capped at 600 s; `everyFrames` defaults to 10. A predicate
that **errors** counts as "not true yet" — `@tac` is null while a mission loads and that is the whole
reason to wait — but the last error is carried into the timeout result as `lastError`, so a predicate
that is broken forever still says why instead of just timing out.

**A predicate that is merely FALSE now says why too.** The timeout result carries `predicate`: the
call as it was actually evaluated, with every `${…}` already substituted. `last:false` is not a
diagnosis, and a plan's assertions routinely hold the diagnosis in their own arguments —
`weapon-test.json` computes an `AbilityDisabledState.Key`, asserts
`Equals("NotDisabled", "${KEY.value}")`, and used to report a bare `false` while the string
`NoSuitableEquipment` it had just read went nowhere. Verified in-game 2026-08-28 with a vehicle as
the shooter: `predicate.args` came back `["NotDisabled","NoSuitableEquipment"]`. One field in the
engine, so it applies to every assertion in every plan rather than to the one that hurt.

### `snapshot` / `restore` — a wrapper, not new serialization

A tactical save already carries every actor, destructible, voxel/dark volume and the level/faction/
mission state (`TacLevelSavegame.cs:45`), with per-actor position, faction, stats, statuses and
inventory. PPBridge writes **no** serialization code. What it does add is the two things the console
command gets wrong for this use:

- **`EnsureUnique` renames a colliding save** to `<name>_1` (`PhoenixSaveManager.cs:154-168`), so
  `save_game foo` twice leaves `foo` *and* `foo_1`, and `load_game foo` comes back to the stale
  first one. `snapshot` deletes an existing save of that name first, so the name asked for is the
  name stored.
- **`SaveWithName` silently does nothing** when the level has no `ISavegameProvider` (`:551`) — i.e.
  in the main menu. That is refused, not reported as a save.

Completion is the `IUpdateable` that **`Timing.Start` returns** (`Timing.cs:246-254`,
`IUpdateable.Stopped`/`Exception`) — note the spec said `SaveWithName` returns it; it returns an
`IEnumerator<NextUpdate>`. `Stopped` alone is not taken as proof: the save is asserted to exist
afterwards.

**`restore` is issue-only and says so.** `SerializationCommands.LoadGame` (`:42`) starts a coroutine
ending in `FinishLevelAndLoadGame` and hands nothing back; there is no load equivalent of the save's
`IUpdateable`. So `restore` returns `{ok, issued:"load_game", note}` and the caller follows with a
`wait` — which a plan does in the same request. It does check the save exists first, because the
command's own "Could not find savegame" is written on a later frame and can never reach the captured
console output of the call.

### The plan engine

One declarative, cross-frame, main-thread run. **Sequencing, variables, waiting, bounded branching,
bounded repetition and cleanup — and nothing else.** No expressions, no user functions, no types:
everything computational is already a `call`.

```json
{"vars":{"x":11.5}, "timeoutMs":60000, "maxSteps":200,
 "steps":[{"id":"a","verb":"call","args":{...},"save":"A","if":"${go}","onError":"fail"},
          {"id":"r","verb":"repeat","args":{"times":3,"while":"${A.value}","steps":[...]}}],
 "finally":[{"id":"cleanup","verb":"release","args":{"h":"${A.value.h}"}}],
 "output":{"actor":"${A.value.h}"}}
```

- **Variables.** `save` stores a step's whole result DTO under a name; `${name.json.path}` reads back
  into any later step (`SelectToken`, so `${DEF.defs[0].guid}` works). Alone in a string it yields
  the **token** — a number stays a number, an `{"$enum":...}` projection binds straight back as that
  envelope. Embedded, it interpolates as text. An **unset** name fails the step and names itself; it
  is never quietly null.
- **Array splice.** `"${...NAME}"` as an ELEMENT of an array expands that var's own elements into the
  surrounding array (`Plan.cs:330-334`); plain `${NAME}` is unchanged, and a spread outside an array,
  of an unset name, or of a non-array value fails the step loudly rather than guessing.
- **Caller vars beat the file's own `vars`** — that is what parameterises a stored plan without
  editing it.
- **Branching:** `if` / `unless` on a step, truthy-tested. A skipped step is traced, never a gap.
- **Repetition:** `repeat` with `times` (capped at 100), an optional `while` guard re-checked before
  each extra pass, and nesting capped at 4. Every pass still burns the global step budget.
- **Cleanup is unconditional, and it is a convention rather than a schema rule.** The engine accepts
  a plan with no `finally` (`Plan.cs:381` reads `p["finally"] as JArray`, and null is a valid answer)
  — deliberately, because an ad-hoc inline plan that takes nothing has nothing to release. Every plan
  shipped in `plans\` has one, and a plan that changes global state without one is a defect rather
  than a style choice; `selfcheck\`'s `EveryShippedPlan` asserts a `finally`, an `output` and a
  `timeoutMs` on **all thirteen** files in `plans\`, and drives two of them
  (`spawn-at-coordinate.json`, `aim-and-run.json`) far enough to prove the whole block drains on an
  early failure. When a `finally` is present it runs on success, on a failed step, on the
  step cap, on the plan timeout and on cancellation — five doors, one exit. It gets its **own** step
  budget and 15 s of grace past the plan's deadline, and a failing step inside it never aborts the
  block (most cleanup is release-what-was-never-taken). That grace is a **bound**: a `finally` that
  overruns it ends the plan with `code:"timeout"` and *"the finally block ran past its … grace
  period"*. Until 2026-08-25 both deadline checks were guarded by `!inCleanup`, so nothing ever read
  the grace deadline — a cleanup block holding a wait that could never be satisfied parked the job
  **forever**. `plan-finally-cannot-hang-forever` in `selfcheck\` is that case, and restoring the old
  guard turns it into `<never finished in 2000 ticks>`.
- **A step's own `timeoutMs` never outranks the plan's.** `load-mission.json` ships
  `timeoutMs: 540000` with a `phaseTimeoutMs: 420000` wait precisely because a save load is slow —
  so a plan that "hangs" for minutes is usually honouring the deadline it was given, not ignoring it.
  Shorten the run through the plan's own vars (`'{"phaseTimeoutMs":90000}'`); the top-level
  `timeoutMs` is read before substitution and cannot be parameterised.
- **Caps that are not advice:** `timeoutMs` 60 s default / 600 s hard, `maxSteps` 200 default / 2000
  hard, 16 steps per frame before the plan yields, 500 trace entries. An unbounded plan on the main
  thread would hang the game, so none of these are optional.
- **Result:** `{ok, code, error, step, result, steps, elapsedMs, cleanupRan, cleanupSteps, output,
  outputWithheld, trace}`. The trace is one compact line per step (`id`, `verb`, `ok`, `ms`, `error`)
  so a failed plan says exactly which step failed and why. Full step results are **not** returned —
  ask for what you want through `output`, which is what keeps a 21-step plan inside the response
  budget.
- **A FAILED plan publishes NO `output`.** The gate is in the engine, not in any plan file
  (`Plan.cs`, `PlanRun.Done`): `output` used to be resolved whether or not the run had failed, so a
  plan whose own assertion had just refused the run still handed back every figure it had measured —
  the weapon bench asserted nothing was wedged, failed that assertion, and returned hit rate, damage
  and dispersion anyway. Now `output` is `null`, `outputWithheld` says why, and `result` carries the
  failing step's own DTO — so the value that tripped the assertion (a `wait` reports it as `last`)
  still reaches the caller while the invalid figures do not.
- A plan may not run a plan. The engine cannot recurse.

### `plans\spawn-at-coordinate.json` — the 23 round-trips as ONE request

The P2 acceptance sequence above, parameterised by `defName`, `faction` and `x`/`z`:

```powershell
.\ppcli.ps1 plan .\plans\spawn-at-coordinate.json '{"defName":"Crabman_Gunner_TacCharacterDef","faction":"alien","x":11.5,"z":-4.5}'
```

21 steps, a 3-step `finally`, and an `output` carrying the actor handle, the requested position and
the achieved one. Three things in it are worth copying:

- **`ComponentSetDef.GetComponentDef<T>`** (`ComponentSetDef.cs:19`) replaces the manual `items`
  paging through `Components` — the nav and perception defs come back by type, not by name-matching.
- **An assertion written as a one-shot `wait`.** `CanStandAt` returning `false` is `ok:true` with
  `value:false`, so a plain `call` would sail past an unstandable point and spawn into a wall. A
  `wait` with `timeoutMs:1` turns any predicate into a stop.
- **`wait` on `InPlay`** rather than a read: `ActorSpawner.cs:23` defers `DoEnterPlay` when the level
  is not playing.

Offline the shipped file is parsed and *run* by `selfcheck\` — it cannot get past its first step with
no game, which is the point: that proves the file parses, that the engine walks its real shape, and
that an early failure still drains the whole cleanup block.

### `var` — the console's second surface

`ConsoleVariableAttribute` registers static **fields and properties**
(`ConsoleVariableAttribute.cs:7,36-92`) and `ConsoleCommandAttribute.Invoke` never sees any of them,
so `console god_mode` is an unknown command however it is spelled — the assignment form
`{"command":"god_mode = true"}` too. `var` is the way in. Values are **strings in both directions**
(the game's `GetValue` returns `ToString()` and `SetValue` parses through `Helper.TypeToConvertFunc`),
so a JSON `true` is sent as `"True"` rather than refused.

```powershell
.\ppcli.ps1 connect var '{"name":"god_mode"}'                  # -> {"ok":true,"name":"god_mode","value":"False"}
.\ppcli.ps1 connect var '{"name":"god_mode","value":true}'     # sets, then reads back
```

It is `call ConsoleVariableAttribute.GetValue/SetValue` with the game's three sharp edges guarded:
`HasVariable` first (an unknown name **throws** `ApplicationException`, it does not refuse),
readonly reported rather than swallowed, and the `NullReferenceException` that `GetValue` raises on
an unset `string` variable (`jira_login`, `override_menu`) turned into a named refusal — so
**never enumerate blindly**. Listing stays with `console vars`, which returns `ok:false` alongside
valid output.

### `plans\aim-and-run.json` — the cursor idiom

The cursor is a plain writable field, so no cursor-scoped command is a dead end:
`PlanarScrollCamera._cursorScreenPos` (`:157`, private, exposed at `:250`, reached through
`CameraManager.CursorScreenPos`, `CameraManager.cs:52`). `UpdateCursorPos()` (`:947`) overwrites it
only when the physical mouse moved (`:984`) and returns immediately while
`CameraBehavior.InputDisabled` is true (`:949-952`).

```powershell
.\ppcli.ps1 plan .\plans\aim-and-run.json '{"x":-0.5,"y":0.0,"z":14.5,"aimOffsetY":0.05,"command":"info","cmdArgs":[]}'
```

Freeze → aim → run → **restore in `finally`**, in one request instead of seven round-trips. The
restore is the reason this is a plan and not a snippet: a run that disables input and dies leaves the
game unusable to a human, and `finally` is the only construct here that runs on failure, on the
deadline *and* on cancel. It is the first cleanup step, before any handle is released, and
`selfcheck\` asserts that about the shipped file.

Three measured traps the plan's own comments carry:

- **Freeze first or results are intermittent** — without `InputDisabled` the identical sequence gave
  an actor on one call and "No actor under the cursor" on the next.
- **Aim height is per-actor and patchy.** `SelectAtCursor` takes only the nearest hit
  (`TacticalView.cs:724`); on one Crabman `Pos.y + 0.6` missed while `+0.4` and `+0.8` both hit.
  Probe with a cheap real command — `info` dumps *all* raycast hits and reports success where the
  cursor commands fail. Floor cells need no probing: `@map.SnapXYZ` then `aimOffsetY 0.05` was exact
  three for three.
- **A live AI turn moves the target between round-trips.** `var ai_enabled false` first.

The aim point is computed with `UnityEngine.Vector3.op_Addition` through `call` — the plan engine has
no arithmetic and does not need any.

> ponytail: `repeat` has no loop variable, so a plan cannot iterate a *list* of targets — one
> aim+run per request. Add an index binding only if batching many cursor commands ever matters.

## Plan library

Every gameplay verb the spec once wanted as new C# (`mission.load`, `spawn.actor`, `res.set`,
`research.unlock`) is a **plan file**, not a verb. The plan engine already sequences, waits, branches,
repeats and cleans up; a C# verb on top of it would be the same call chain with a worse interface and
no way to re-parameterise it without a rebuild. **`src\` was not touched.**

| Plan | Parameters | What it does | Status |
|---|---|---|---|
| `spawn-at-coordinate.json` | `defName` `faction` `x` `z` `probeY` `snapRadius` `preloadTimeoutMs` | one actor at one exact point, assets preloaded first | **VERIFIED** — placement 2026-08-25 (P2), preload + visibility same day (7 renderers / 637 transforms vs 3 / 541 without) |
| `spawn-squad.json` | `defName` `faction` `count` `minDistance` `maxDistance` `useCenter` `centerX/Y/Z` `probeY` `snapRadius` `preloadTimeoutMs` | N actors in a distance **band** around the selected actor (or an explicit point), each position validated with `CanStandAt`, achieved distance reported per actor | **VERIFIED** — placement + distance, and visibility after the preload fix; it spawned **invisible** actors until then, see *The preload, and the false "not required"* |
| `equip-actor.json` | `actor` `itemName` `container` `listMember` | give an in-play actor an item, and prove the container grew | **VERIFIED** |
| `load-mission.json` | `name` `phase` `waitReady` `phaseTimeoutMs` `readyTimeoutMs` | load a savegame and wait on `HasAnyTurnStarted` | **VERIFIED**, both halves — tactical (`phase:"tactical"` in ~20 s) and geoscape (a geoscape save loaded **from a live geoscape** → `phase:"geoscape"`, `Playing`, in 14.9 s, 2026-08-28) |
| `situation.json` | all of `spawn-squad` + `snapshot` `restoreFirst` `itemName` `equip` | restore a snapshot, place a composition at a distance with equipment, summarise the result — preloads the actor def **and** the item def (`give` does the same, `TacConsoleGameplay.cs:770`) | **PARTLY** — spawn+equip body verified, restore head not, preload verification pending |
| `set-resources.json` | `resource` `amount` | apply a resource **delta** through the shipped cheat path, wallet read back before/after | **VERIFIED** 2026-08-28 — Materials **1000 → 1500** on a campaign `start-campaign.json` began from the main menu |
| `unlock-research.json` | `researchId` | `CompleteResearch` (rewards + cascade), state read back before/after | **VERIFIED** 2026-08-28 — `PX_Alien_Fishman_ResearchDef` went **Hidden → Completed**, same campaign |
| `aim-and-run.json` | `x` `y` `z` `aimOffsetY` `command` `cmdArgs` | freeze input, aim the cursor, run a cursor-scoped command, restore | verified 2026-08-25 (P3) |
| `weapon-test.json` | `shooter` `weaponDef` `enemyDef` `distance` `tolerance` `shots` `attackType` `setSpread` `spread` `seed` `targetHp` | equip a weapon, put an enemy at a distance, fire N shots, report every impact point + dispersion | **VERIFIED** 2026-08-28 — see *The weapon bench* below |

```powershell
.\ppcli.ps1 plan .\plans\spawn-squad.json     '{"defName":"Crabman_Gunner_TacCharacterDef","faction":"alien","count":3,"minDistance":9.0,"maxDistance":11.0}'
.\ppcli.ps1 plan .\plans\equip-actor.json     '{"actor":"@selected","itemName":"PX_AssaultRifle_WeaponDef"}'
.\ppcli.ps1 plan .\plans\load-mission.json    '{"name":"TACTICAL_SAVE"}'
.\ppcli.ps1 plan .\plans\load-mission.json    '{"name":"GEOSCAPE_SAVE","phase":"geoscape","waitReady":false}'
.\ppcli.ps1 plan .\plans\situation.json       '{"snapshot":"SNAPSHOT_NAME","count":3,"minDistance":9.0,"maxDistance":11.0,"itemName":"PX_AssaultRifle_WeaponDef"}'
.\ppcli.ps1 plan .\plans\situation.json       '{"restoreFirst":false,"count":3,"minDistance":9.0,"maxDistance":11.0}'
.\ppcli.ps1 plan .\plans\set-resources.json   '{"resource":"Materials","amount":500}'
.\ppcli.ps1 plan .\plans\unlock-research.json '{"researchId":"PX_Alien_Fishman_ResearchDef"}'
```

### `spawn-squad.json` — measured, not asserted

> **Verified in-game 2026-08-25**, map `SCV_PLT_Ambush_56x56_A`, anchor
> `@selected` at `(2.50, 0.05, 2.50)`, band **9.0–11.0 m**, 126 ring candidates.
> Requested 3, spawned 3, **64 steps in 328 ms**, cleanup ran.
>
> | Actor | Achieved position | Achieved distance |
> |---|---|---|
> | `Crabman_2` | `-7.5, 2.43, 0.5` | **10.472 m** |
> | `Crabman_3` | `11.5, 0.00, 2.5` | **9.000 m** |
> | `Crabman_4` | `8.5, 0.03, -5.5` | **10.000 m** |
>
> Second, independent observation: the alien faction's own `Actors` enumeration went from 8 actors
> to 11 and lists `Crabman_2/3/4`; a fresh `get Pos` on each new handle returned exactly the three
> positions above, and `InPlay` read `true` on all three. `{"ok":true}` was never the evidence.

Distance is a **search constraint, not a promise** — the plan reports what it achieved. The search is
the engine's own composition: `TacticalMap.GetPositionsInRange` (`:446`) for the ring,
`@map.SnapXYZ` onto `UnityLayers.FloorAllMask`, then `@map.CanStandAt(nav, perception, pos)`.

Three idioms in it are new and worth stealing:

- **The engine has no `not`, so the negation is a real call.** `System.Object.Equals(false, x)` is the
  shortest expression of "not x" that always resolves, and it is what lets `repeat`'s `while` mean
  "keep searching until a standable point turns up".
- **The engine has no list building, so the game holds the list.** One `System.Collections.ArrayList`,
  one interpolated line per actor (`"${ACTOR.value.name} at ${ACH.value.x},… dist=${DIST.value}"`),
  read back at the end with `items`. That is how per-actor requested-vs-achieved survives a loop whose
  `save` names are overwritten every pass. Note `new ArrayList()` is **refused as ambiguous** — the
  binder scores the static `.cctor` alongside the `.ctor` — so the capacity ctor `args:[16]` is used.
- **`repeat times` must be a LITERAL.** `Clamp` (`Plan.cs:83-88`) only accepts a JSON integer and
  `Repeat` reads `step["args"]` **before** substitution runs, so `"times":"${count}"` silently means
  **1**. The count parameter is honoured by the `while` guard instead —
  `Utl.LesserThan(spawnedSoFar, count, 0.01)` — with the literal `times` as the hard ceiling (12).

> ponytail: random ring candidates rather than an indexed scan, because `repeat` has no loop variable
> and `${LIST.items[${k}]}` is not a thing. It also spreads the squad instead of clustering it at the
> ring's first cell. A loop variable + `until` would be ~8 lines in `Plan.cs`; add them only if
> deterministic placement ever matters.

#### A cosmetic tail step must not invert the verdict — `reset-selection`

Three spawn-squad runs on 2026-08-25 reported `FAILED` while **the spawn had already succeeded**.
The failing step was `reset-selection`, the last piece of housekeeping copied from the console
command (`TacticalDeployZone.cs:422`), and it threw
`InvalidOperationException: Map is updating, cannot calculate path!` — the navmesh was rebuilding
after in-combat explosions and `ResetCharacterSelectedState` re-runs selection, which asks for a
path.

Both halves of that were wrong. A post-spawn cleanup step has no business computing a path, and a
failure in it must not overturn the verdict of a plan whose real work is done: **a false `FAILED`
is worse than a warning, because it invites spawning the whole squad a second time.** All three
spawn plans now carry `"onError": "continue"` and `"save": "RESET"` on that step, and report
`selectionReset` in their `output`. The step is still traced with its exact error — the plan
succeeds *with a warning* rather than lying in either direction.

### `equip-actor.json` — and the trap that made its first run fail honestly

> **Verified in-game 2026-08-25.** `Crabman_4`, `PX_AssaultRifle_WeaponDef`: equipment count
> **2 → 3**, and the added instance's own `ItemDef.name` read back as `PX_AssaultRifle_WeaponDef`.
> Second, independent observation: a fresh read of the actor's `Equipments` and of every entry's
> `ItemDef` returned `Crabman_LeftHand_Grenade_WeaponDef`, `Crabman_RightHand_Gun_WeaponDef`,
> `PX_AssaultRifle_WeaponDef`.

The first run **failed**, and the failure was the point: `countBefore` and `countAfter` were both 2
while `AddItem` had returned a live `Weapon`. `EquipmentComponent.Equipments` is
`Items.OfType<Equipment>().ToList()` (`EquipmentComponent.cs:28`) — a **fresh list every call**.
Holding one handle across the add measures the same stale snapshot twice. The plan now re-reads the
list on both sides. The assertion is a one-shot `wait` on `Utl.GreaterThan(after, before, 0.01)`, so a
container that did not grow is a named failure rather than a cheerful `ok:true`.

`find` is a substring match and `defs[0]` is a hard reference, so name the def **exactly**:
`"PX_AssaultRifle"` resolves to `PX_AssaultRifle_AmmoClip_ItemDef` first.

### The `finally` contract, tested by killing a plan on purpose

`aim-and-run.json` was run with `command: "no_such_command_xyz"`. The plan failed at `run-command`,
`finally` ran all **5** steps, and an independent read afterwards returned
`CameraBehavior.InputDisabled == false`. A plan that disables input and dies does **not** leave the
game unusable to a human. Every plan here carries a `finally`, a `timeoutMs` and bounded iteration;
`situation.json` restores `ai_enabled` first, before any handle is released, for the same reason.

### What is NOT reachable, honestly

- **Launching an arbitrary map from scratch — REACHABLE, and this entry used to say otherwise.** The
  old note looked only at the GEOSCAPE commands: `create_mission` does always target the site under
  the geoscape cursor and take no coordinate, plot, seed or roster (`GeoSite.cs:1200-1203`), and
  `launch_mission` does need a roster-deployment state or a selected `GeoVehicle` at the cursor site
  (`:1143-1171`). The menu-only `loadmap` (`MapPlot.cs:320-366`) needs none of that: it assembles
  the `TacticalGameParams` itself and `MenuCrt` branches on the params type into `TacticalGameCrt`.
  `plans\start-mission.json` drives it from the main menu in ~12 s, and `build-mission.json` does
  the same with a map, mission type, roster and enemy budget you choose. Loading a save
  (`load-mission.json`) is the other answer, not the only one.
- **An absolute resource setter.** `Wallet.Apply` is a diff (`Wallet.cs:140`) and the plan engine has
  no arithmetic, so `set-resources.json` applies a **delta** and reports the wallet before and after.

### Leaving a live geoscape — VERIFIED IN-GAME 2026-08-28

Every plan that has to get back to the menu — `start-mission`, `start-campaign`, `build-mission`,
`load-mission` — used to refuse outright when a campaign was open, because doing it wedged the
process at `{"phase":"menu","level":null}` forever. They no longer refuse.

**The old diagnosis was wrong.** It named `GeoscapeView.Update`, and it is not that: setting
`UpdateStateStack=false` on its own, a different `MenuEnterReason` and deactivating the whole level
GameObject were each tried live and each still wedged. The crash is **synchronous**, inside
`Level.SetCurrentCrt` → `GeoLevelController.OnLevelEnd` → `GeoVehicle.OnExitPlay` →
`GeoMap.UnRegisterVehicles` → `UIStateVehicleSelected.OnVehichleChanged` → `ResetViewState`, where
`UIStateInitial` **re-selects the vehicle** on the way in and the aircraft info panel is rebuilt in
the middle of the teardown. Under TFTV `AircraftReworkMaintenance.GetMaintenanceFactor` NREs there;
the throw kills `SetCurrentCrt`, the geoscape scene is never unloaded, and a second `MenuCrt` starts
on top of the first.

**The fix is what the game itself does before it tears a geoscape down** to launch a mission
(`GeoLevelController.cs:1406,:1444`): set `GeoscapeView.UpdateStateStack = false`, then call
`GeoscapeView.ToLoadingState()` — so no vehicle is selected when the vehicles exit play — and only
then `FinishLevelAndGoToLobby`. `ToLoadingState` is the load-bearing one: `ResetViewState` is the
trap, because `UIStateInitial.EnterState` switches straight back to `UIStateVehicleSelected`.

Verified in ONE process: geoscape → menu (`IsPlaying` true) → a playing tactical mission, two
campaigns back to back, `build-mission` from a live geoscape, and a geoscape save loaded from a live
geoscape in 14.9 s. `start-mission` reports `cameFrom:geoscape` when it came that way. Leaving a
tactical mission was always fine and still is.

### One unverified step is left — and what the geoscape cost to reach

Every shipped plan has been run in-game except **`situation.json`'s restore head**: its spawn and
equip body is `spawn-squad.json`'s and `equip-actor.json`'s, both proven, but the snapshot-restore
step at the front was never seen to complete (the session's game process died mid-load). Run it with
`restoreFirst:false` to stay on proven ground. The table above says the same thing per plan.

Three other plans carried an UNVERIFIED label until 2026-08-28, all for one reason: nothing could
reach a live geoscape without a human playing one. `start-campaign.json` removed that, and all three
were run against the campaign it generated from the main menu.

- `load-mission.json`'s geoscape half — a geoscape save loaded **from a live geoscape**,
  `phase:"geoscape"`, `Playing`, in 14.9 s.
- `set-resources.json` — Materials **1000 → 1500**.
- `unlock-research.json` — `PX_Alien_Fishman_ResearchDef` **Hidden → Completed**. Its old default
  `PX_Alien_Autopsy_ResearchDef` does not exist in this build's repository at all; `ResearchDef.Id`
  usually equals the def name but not always, which is exactly why `index` stores the id separately.

The failed attempts that came before are still worth keeping, because what they established
generalises:

- **`load_game` on a save the build cannot open fails by STALLING, not by erroring.** There is no
  completion signal to say otherwise (`SerializationCommands.cs:44`) — which is exactly why `restore`
  is documented as issue-only. The game parks at
  `{"phase":"menu","scene":"BaseScene","levelState":"none"}` and sits there.
- **Never issue a second `restore` while one is still in flight.** That is what killed the process.
- **The usual cause is a MOD-SET mismatch, and the game log names it.** A geoscape save written with
  mods that are not activated in the install you are loading it into cannot have its level data
  deserialized:

```
[ERROR] Serialization reader: Error reading type data for type
        <SomeMod>.SomeDef, <SomeMod>, Version=1.0.0.0
[ERROR] Reading save game level objects failed! Metadata: '<save>.zsav'
[ERROR] Failed to deserialize level data from save '<save>'!
NullReferenceException ... PhoenixPoint.Common.Game.PhoenixGame+<GeoscapeGameCrt>d__81
```

- **`IsLoadable()` is not a predictor.** It checks version and DLC, never the mod set — every save in
  a 14-save set answered `true`, including the ones that could not come up. Read the save's own
  `Mods` instead: `PPSavegameMetaData.Mods` is a list of `SaveModEntry {ID, Version}`, readable live
  over the pipe (`@phoenix.SaveManager` → `GetSaves()` → `items` → `Mods`).
- **The honest fixes** are to activate the mods the save names, or to produce a geoscape save with
  the install you are driving. A save the install cannot open still stalls silently, so a geoscape
  `restore` is still a slow, unsignalled operation — but it is no longer a process-killing one: the
  teardown wedge it used to trigger is fixed (see *Leaving a live geoscape*).

## The weapon bench — `observe` and `plans\weapon-test.json`

A modder builds a weapon and wants to see it fire *now*: real numbers, and real tracers.

```powershell
.\ppcli.ps1 plan .\plans\weapon-test.json '{"weaponDef":"PX_AssaultRifle_WeaponDef","enemyDef":"crabman","distance":10.0,"shots":5}'
.\ppcli.ps1 plan .\plans\weapon-test.json '{"weaponDef":"PX_AssaultRifle_WeaponDef","distance":10.0,"shots":5,"setSpread":true,"spread":0}'
```

### `observe` — the one thing that needed new C#

Everything else in this plan is a `call`. Seeing **where a shot landed** is not, and it is worth
being precise about why every cheaper route fails:

- `call` has only `new`/`get`/`set`/`invoke` (`Reflect.cs:306-314`), so nothing can subscribe to
  `TacticalActor.DamageAppliedEvent`.
- The ability report containers are flushed either side of a volley
  (`TacticalLevelController.cs:1803,1855-1866`), so they cannot be read afterwards.
- `Map.ProjectilesInFlight` is gone by the time anyone could read it
  (`ProjectileLogic.cs:305,359`).
- The Unity log line in `TacticalActorBase.ApplyDamageInternal` (`:850-852`) prints an impact point —
  but only for **actor** damage, and it is skipped entirely under god mode (`:846`). A terrain miss
  logs nothing, and a terrain miss is exactly what measures spread.

**The seam: a Harmony PREFIX on `ProjectileLogic.OnTrajectoryEnd(CastHit, Vector3)`**
(`ProjectileLogic.cs:333`, `src\ShotPatch.cs`). Every projectile passes through it exactly once
whatever happened to it — all three flight paths end there (`:125`, `:160`/`:186`, `:272`) — and
`lastHit` carries the terminal point even when nothing alive was hit.

- **Prefix, not postfix, and that is load-bearing.** `OnTrajectoryEnd`'s last act is
  `_damageAccum.ApplyAddedDamage()` (`:355`), which ends in `_targetsData.Clear()`
  (`DamageAccumulation.cs:644,701`). A postfix reads an emptied accumulator and reports every shot
  as a miss.
- **Simulations are skipped.** The damage predictor runs this identical code every time the UI hovers
  a target — a simulation is a `ProjectileLogic` with no `Projectile` (`:41`).
- **OFF by default.** Nothing is patched until `observe {"action":"start"}`, and `stop` unpatches.
  The prefix cannot throw.

**The ring is bounded, and the answer says by how much.** It holds **512** impacts (`Shots.Capacity`)
and overwrites the oldest beyond that; `observe read` lists at most **200** rows (`Shots.MaxRows`),
again dropping the oldest from the listing, because when a run overflows it is the last shots that
are being asked about. So `impacts` is not "every impact" — read it against the three counters in the
same answer: `recorded` is everything the prefix saw, `stored` is how many the ring still holds,
`dropped` is how many it overwrote, and `returned` is how many rows the listing carries. Every hit,
damage and dispersion figure is computed over `stored`, not over `recorded`.

`observe read` gives the impact rows and `dispersion` (`mean` / `sigma` / `max`) about both the group
centroid and the aim point, plus **two families of hit and damage figures that must not be confused**:

| Per-TARGET — the weapon's score | Any-ACTOR — everything a projectile touched |
|---|---|
| `targetHits` `targetMisses` `targetHitRate` `damageOnTarget` | `hitsAnyActor` `misses` `hitRateAnyActor` `damageOnActors` `damageTotal` |
| keyed on the aimed-at actor's instance id | a bystander, a dead tree, the shooter's own body all count |

`observe start` is told the target id; without one the per-target figures read `0` rather than quietly
falling back. The bench used to report only the any-actor family, so a stray hit read as the weapon
doing more than it does — a live 10-shot run read `targetHits` **6** against `hitsAnyActor` **9**, and
`damageOnTarget` **153** against `damageOnActors` **201**.

Dispersion is measured about `TacticalAbilityTarget.GetWorkingPosition()` (`:175`) — the point the
shot was actually aimed at — and **not** the enemy's `Pos`, which is its FEET. The feet offset was
being carried as if it were weapon spread: one real run read `mean` **0.9403** about the feet and
**0.3896** about the working position, a 2.4x inflation. `enemyFeet` is still reported, so the old
figure stays derivable.

The pure half (`src\Shots.cs`) names no Unity and no game type, so `selfcheck\` exercises the ring,
the caps and the arithmetic with no game running.

**Two rows that would have lied, both caught live:**

- A projectile that hits **nothing at all** comes through with the static `SDummyHit`
  (`ProjectileLogic.cs:25,107,217`): no collider, point exactly `(0,0,0)`. Reporting that as an impact
  *at the world origin* is a fabricated coordinate — and it wrecks the figures: a run with two such
  rows read a **4.2 m** group where the real one was **0.2 m**. Such a row now comes back with null
  coordinates, still counts as a miss, and stays out of the dispersion.
- **HP before/after is not the damage measurement.** A stat is recomputed from its base plus its
  modifications, so a raised `Health.Max` does not stay raised: a run set `5000`, read `5000` back,
  and finished at `77` — the def's own max minus 63 damage. The accumulated damage figures are the
  authoritative ones, and in a clean run they reconciled exactly: `23+33+23+24 = 103 = 5000 → 4897`.

### VERIFIED IN-GAME 2026-08-28 — and the control run that makes it evidence

A tactical save on `SCV_PLT_Ambush_56x56_A`, `Playing`.
`PX_AssaultRifle_WeaponDef` on `Soldier_4`, a `Crabman_Gunner` spawned at ~10 m, 5 shots, freshly
reloaded mission before each run.

| Run | Hits | Group `mean` | `sigma` | `max` |
|---|---|---|---|---|
| stock spread (`weapon_spread` 1) | 5/5 | **0.1444 m** | 0.1113 | 0.3238 |
| **control, `spread: 0`** | 5/5 | **0.0015 m** | 0.0008 | 0.0029 |

**~100x tighter**: all five zero-spread impacts inside 3 mm of each other at 10.18 m. That is what
zero dispersion has to look like, and it is not a number a broken tap could produce. The earlier
clean run reconciled independently three ways — 5 shots → 5 projectiles → 5 charges, and the damage
sum equalled the HP delta exactly — and its one miss was recorded landing in
`GEN_Wall_1x_Full (15)`, which is precisely the observation no event and no log line can give.

### What the bench measured about the game itself

- **`Activate` is unreachable through `call`.** `Ability` (`:34`), `TacticalAbility` (`:1078`) and
  `ShootAbility` (`:152`) all declare `Activate(object)`, so the binder scores three candidates equal
  and refuses as ambiguous — and `sig` cannot break the tie, because the parameter types are
  identical. Same for `IsEnabled` (`Ability.cs:29` / `TacticalAbility.cs:367`) and for
  `BaseStat.Set` vs `StatusStat.Set`. The reachable siblings are `ExecuteAndWait`,
  `GetDisabledState` and `SetToMax`, each declared once. **`Execute` is an iterator** — calling it
  through `call` builds the state machine and never fires a shot; `ExecuteAndWait` calls `Activate`
  itself and returns a plain `NextUpdate`, so no `Timing` pump is needed anywhere.
- **Firing out of turn is clean.** Three runs with the **alien** faction current all reported
  `NotDisabled`, fired and recorded impacts — nothing in `Activate` gates on the current faction, so
  the plan does **not** end any turns. The one consequence: `@selected` is null during another
  faction's turn, so pass an explicit `shooter` handle then.
- **The weapon must be SELECTED, not merely held.** `AddItem` leaves the previous selection in place
  and the ability then reports `EquipmentNotSelected`. `Activate` would select it itself
  (`:1087-1090`) — but every enabledness check happens first.
- **`AbilityDisabledState` is not an enum.** It is a struct whose only field is a string `Key`
  (`:7,163`), and its `ToString()` localises. Read the `Key`.
- **Ammo: `TacticalItem.ReloadForFree()`** (`:830-848`) is the game's own fill-to-max and covers both
  shapes — magazines for `CompatibleAmmunition[0]`, `SetChargesToMax` otherwise. `equip-actor.json`
  adds an item and never loads one, which is why a weapon from it cannot fire.
- **A shot is not a projectile.** `PX_AssaultRifle` fires **six** projectiles per pull of the trigger
  — and the count is a PRODUCT, `GetNumberOfShots(attackType) * ProjectilesPerShot` (`Weapon.cs:202,640`).
  For `PX_AssaultRifle_WeaponDef` that is `AutoFireShotCount` 6 × `ProjectilesPerShot` 1; reading
  either field alone gets the right answer here only because the other is 1. One activation runs all six through one
  loop (`TacticalLevelController.cs:1817`). So `shots` counts **activations** and `projectiles`
  counts impacts; the bench reports both, plus `projectilesPerShot`, because without that number the
  two read as a contradiction.
- **Pacing a volley needs two predicates, and three plausible single ones are dead ends.**
  `ActionComponent.HasPlayingActions` (`:164-182`) counts `NotStarted` as well as `Playing` and looks
  perfect — but `IdleAbility` sits on that same channel permanently, so it is never false. The
  ability's own `IsExecuting` is false while a shot is merely **enqueued** (`ShootAbility.cs:173`
  takes `EnqueueAction` for a Regular shot), and each enqueue is `soloAfterCurrent`, which **cancels
  everything already queued** (`ActionComponent.cs:80-91`). `Map.HasActiveProjectiles` is false
  before a shot has spawned its projectile *and* false between two shots of one burst.
  What works: `Shots.Landed` for "this shot reached its impact", then
  `TacticalActorBase.HasExecutingAbility(null, false)` (`:695`, which explicitly skips `IdleAbility`
  at `:699`) going false for "the whole activation is over". Verified both ways live — false on an
  idle actor, true through a burst.

### Known limits

- **`shots` is accepted from 1 to 100** (the engine's own `repeat` ceiling, `Plan.cs MaxIterations`),
  as a whole number; a request outside that range, or a fractional one, is refused up front by name
  rather than truncated into a short volley reported as `ok`. **Accepted is not the same as
  answerable**, and two bounds bite before 100 does:
  - **The ring.** 100 activations of a 6-projectile burst is 600 impacts against a 512-entry ring, so
    a max-length volley fails `assert-nothing-dropped` by construction. Ask for a length whose
    projectile count fits.
  - **A target that DIES.** Once actors start dying, something throws inside `OnTrajectoryEnd`. The
    stack is cut at the Harmony wrapper, so the culprit **cannot be named** — do not attribute it to
    any particular mod or `Die` patch. The plan refuses by name rather than reporting the volley.
  - **Pacing needs the right settle predicate**, and that is what bounded volley length before it was
    understood. `Shots.Landed` answers "this shot reached its impact"; `HasExecutingAbility(null,
    false)` (`TacticalActorBase.cs:695`, which skips `IdleAbility` at `:699`) answers "the whole
    activation is over". Both are needed, because a Regular shot is *enqueued*, not played
    (`ShootAbility.cs:173`), and each enqueue is `soloAfterCurrent`, which discards everything already
    queued (`ActionComponent.cs:78-91`) — a settle that releases on the enqueue tears through a burst
    counting activations that were then thrown away, and the next `landed` waits forever on an actor
    that reads idle. Swapping that one predicate moves the *same* run between **4** and **20**
    activations, which is the falsification pair rather than one happy result. Target death was ruled
    out separately: before a failing activation the target read `InPlay` at full health, and
    `Health.SetToMax` before every shot changed nothing.
- **`recovered` must read `0`, and a non-zero value invalidates the run.** `OnTrajectoryEnd` removes
  the projectile from `ProjectilesInFlight` and clears `Projectile.IsActive` in its **last two
  statements** (`ProjectileLogic.cs:359-360`), *after* `_damageAccum.ApplyAddedDamage()` (`:355`)
  runs the whole damage chain — the game's own code and every Harmony patch sitting on it alike. One
  throw in there and the projectile is never released, so `TacticalMap.HasActiveProjectiles`
  (`TacticalMap.cs:133`) is stuck true for the rest of the mission and the game's own firing
  coroutine waits on it forever (`TacticalLevelController.cs:1759,:1797`). `ShotPatch.Unwedge` runs
  those two statements itself and returns `__exception` **unchanged**, so the throw still reaches the
  log. **The finalizer cannot say what threw** — the stack is cut at the Harmony wrapper — so
  `recovered` reports that a repair happened and never who caused it; do not read it as naming a mod,
  and do not advise removing one on the strength of it. Each repair increments the count.
  **A non-zero `recovered` FAILS the plan** — the
  `assert-nothing-wedged` step is fatal by design: the release keeps the mission playable, which is
  worth having, but the hit and damage figures were then measured *across* a repair, and reporting
  them anyway would be exactly the silently-wrong answer this bench exists to refuse. **Failing was
  not enough on its own** — the engine used to resolve `output` regardless, so the assertion fired and
  the figures came back anyway. A failed plan now publishes no `output` at all, and the predicate
  reads `Shots.Recovered` directly so the COUNT survives in the failure's `result.last`.
- **`dropped` must read `0` too, for the same reason.** The impact ring holds `Shots.Capacity` (512)
  and overwrites the OLDEST beyond that, and every statistic — hit rate, damage totals, dispersion —
  is computed over what the ring still holds. A non-zero `dropped` therefore means the answer is a
  measurement of the last 512 projectiles reported under the heading of all of them, so
  `assert-nothing-dropped` refuses the run the same way. At the plan's own ceiling this is reachable,
  not theoretical: 100 activations of a 6-projectile burst is 600. On a run that comes back at all,
  `stored`, `dropped` and `returned` are in the output — `returned` (capped at `Shots.MaxRows`, 200)
  trims the impact LISTING only and changes no statistic.
- **`shots` must be a whole number.** `1.5` sits inside 1..100, so the volley used to fire twice and
  then die minutes later at `assert-volley-not-long` with an enemy already spawned. It is now refused
  up front by `assert-shots-integral`.
- **The enemy placement is a random draw from the distance band**, so it can land behind cover and
  the run FAILS at `assert-target`. That is the takeability check working. Re-run, or set `seed`.
- **The plan does not spawn the shooter.** It equips the one you have selected. Use
  `spawn-at-coordinate.json` first if the mission has none.
- **Spawned enemies are left in place on purpose** (watching the tracers is half the point), so
  repeated runs litter the map. Reload the save between measurements.

## Measured on the development machine

Shapes to expect, not figures a reader reproduces. Build stamps and protocol strings are per-session
and are deliberately not quoted.

- **deploy** prints `Deployed PPBridge to <install>\Mods\PPBridge (build=<sha1 prefix>)`.
- **`run ping`** → `{"ok":true,"build":"…","stale":false,"done":1,"log":"…","results":[{"id":"r1","result":{"ok":true,"protocol":"…","build":"…"}}]}` — **17 s** cold.
- **`run state`** at main menu → `{"ok":true,"phase":"menu","scene":"HomeScreen","level":"HomeScreenLevel(Clone)","levelState":"Playing"}`.
- **`run console`** with `{"command":"commands","args":[]}` → **344** native commands with descriptions on that build, captured through a custom `IConsole`.
- Unknown command → `{"ok":false,"error":"unknown command 'no_such_command_xyz'"}` — structured refusal, not a throw.
- **Spawn at a chosen coordinate: PROVEN.** `SCV_PLT_Ambush_56x56_A`, requested `(11.5, 0.0, -4.5)`,
  achieved `(11.5, 0.0, -4.5)`, `InPlay: true`, 23 `call` round-trips at ~20-60 ms each. This is the
  P2 acceptance criterion and the one thing the console demonstrably cannot do.

## Gotchas

### Mod activation — the profile trap

The mod must be in the activated-mods array or `run`/`batch` **refuse to launch** (by design).

File: `...LocalLow\Snapshot Games Inc\Phoenix Point\Steam\<SteamID>\Options.jopt`

`Contents.Objects` is a flat pool of `{ObjectID, ObjectValue}` records. The options dictionary holds
`{Key, Value:{ObjectID}}` pairs, and `MOD_ACTIVATED`'s points at the record whose `CollectionValues`
**is** the activated list. `Test-ModActivated` (`paths.ps1`) walks exactly that and asks whether the
list `-contains` the id.

That is a change from the preflight's first version, which asked whether the id appeared **anywhere**
in the file. A deactivated mod satisfies that just as well as an activated one — the id is still
written into the file — so the check passed on installs where the mod was switched OFF, and the run
then died with the mod loaded and silent: the exact failure the preflight exists to prevent. The
refusal now names the array it actually read:

```text
REFUSED: 'com.morgott.PPBridge' is not in MOD_ACTIVATED in <...>\Options.jopt.
```

**Tick the mod in the in-game manager; do not rewrite this file.** The check is read-only on purpose —
re-serialising it once shrank it from 32,991 to 18,996 bytes. If you edit it anyway, note that the
element count is duplicated in `ArrayDimensions.CollectionValues` and must be kept in sync.

### Options.jopt restoration

The client restores `Options.jopt` **byte-exact** after every run and kills **only** the PID it launched.

### Build-stamp guard

`stale:false` in the ping response is what catches a deployed-but-old DLL.
A CLI without this check silently reports stale results as passes.

### No DACL on the pipe — do not add one back

The pipe is created with **no `PipeSecurity`**, on Windows' default DACL. A hand-built one-ACE DACL
was tried and it **denied this mod's own client** (`Access to the path is denied`, gate 3): the SID
it was built from is not the one the client authenticates as, and this runtime will not hand over the
real one — `WindowsIdentity.User` and `.Owner` both `throw new NotImplementedException()`, and
`NTAccount.Translate` resolves well-known accounts only.

**Security posture, honestly.** The default DACL grants creator-owner — this same user — full
control, so the client reads *and* writes; Administrators and SYSTEM get full control; Everyone and
Anonymous get read-only, so another local user can open the pipe but cannot write a request into it.
Combined with the 128-bit CSPRNG session token in a file under `%LOCALAPPDATA%` (normal per-user
ACL), checked before anything else happens to a request, that is sound **bearer** authentication
against unrelated local users.

It is explicitly **not** isolation from another process running as this same user, nor from an
administrator or SYSTEM: any of them can read the discovery file, take the token, and get full
reflection-equivalent access to the game. That is acceptable for a dev-only tool on a single-user
box, and the mod never shipping is part of the posture rather than an afterthought.

Upgrade path, only if isolation from same-user processes ever matters: `OpenProcessToken` +
`GetTokenInformation(TokenUser)` for a real SID, then the 8-arg ctor. Note that the ACL *machinery*
is fine here — `PipeSecurity`, `PipeAccessRule`, `DiscretionaryAcl.AddAccess` and the 8-arg ctor are
all implemented, and `Win32NamedPipeServer` really does pass the descriptor to `CreateNamedPipe`.
The SID was always the missing piece, and a wrong SID is worse than none.

Other Mono gaps confirmed in the same pass, all avoided: `WaitForConnectionAsync` (both overloads),
`NamedPipeClientStream.ConnectAsync(int, CancellationToken)`, `RunAsClient`,
`GetImpersonationUserName`, `PipeOptions.Asynchronous` (Mono's `ConnectNamedPipe` is synchronous
anyway) and message transmission mode (`ReadMode` / `IsMessageComplete`). `WaitForPipeDrain()` exists
but is an **empty no-op** — never treat it as a delivery guarantee. What works is the boring shape:
one background thread, `InOut` + `Byte` + `PipeOptions.None`, blocking `WaitForConnection`, blocking
framed read/write, and a **fresh** server instance per connection.

### The constructor arguments Mono rejects

The working call, and every argument in it is load-bearing:

```csharp
new NamedPipeServerStream(bareName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                          PipeOptions.None, 1024, 1024)
```

- **`outBufferSize` must be > 0.** `0` is legal on .NET Framework ("system default") and throws
  `ArgumentOutOfRangeException("bufferSize must be greater than 0")` out of Mono's `PipeStream` base
  ctor, with or without a DACL. Killed gate 2. (`inBufferSize: 0` is accepted; only the out buffer is
  checked. Both are 1024 here — Mono sizes an internal `FileStream` from this, so do not inflate it.)
- **`maxNumberOfServerInstances` must be a literal `1`.** `MaxAllowedServerInstances` (-1) is passed
  through as `0xFFFFFFFF` instead of being clamped, and Windows answers error 87.
- **The name must be bare** (`ppcli-<user>-<install>-<pid>`). Mono prepends `\\.\pipe\` itself; a full
  path becomes malformed.

No offline check can catch this class of bug — the values that kill the game are all valid on .NET 8.
The in-game self-test is the only gate for it.

### `ERROR_PIPE_CONNECTED` (535) is success, and Mono calls it failure

Mono's `Win32NamedPipeServer.WaitForConnection` does `if (!ConnectNamedPipe(...)) throw
Win32PipeError.GetException()` with no check for 535 — which Windows returns when a client connected
in the gap between `CreateNamedPipe` and `ConnectNamedPipe`. That is a normal outcome, not an error,
and it is **likely by construction** here: the startup self-test and any back-to-back client request
land in exactly that gap.

`PipeServer` catches `Win32Exception` with `NativeErrorCode == 535` and marks the stream connected.
`PipeStream.IsConnected` has a `protected` setter and `NamedPipeServerStream` is `sealed`, so there
is no subclass to do it cleanly — it is set by reflection, and `pipe-isconnected-setter-exists` in
the self-check fails if that property lookup ever stops resolving. If it cannot be set, the exception
is rethrown rather than serving a stream the runtime still thinks is idle.

### Shutting the pipe down without hanging the game

Only a connection returns from a blocking `WaitForConnection`, so `Stop()` sets the flag, connects a
throwaway client with a 1 s timeout to wake the thread, checks the flag right after accept, then
bounded-joins (2 s). Disposing the stream cross-thread is kept only to break a connection blocked
mid-read — it is not a reliable way to break the accept wait.

### "Listening" was a lie — verify reachability, not construction

The first P1 build logged `pipe ... listening` and then failed 111 times on a one-line message while
no listener existed. Two fixes, both kept:

- `PipeServer.SelfTest` connects to its own pipe with a deliberately wrong token and logs
  `PPCLI: pipe self-test OK` or `PPCLI FAILURE: ...`. Offline checks cannot see a Mono-only gap; this
  one runs where the gap lives.
- Both the `listening` line and the self-test thread start from **inside the accept loop**, after
  `CreateNamedPipe` has returned and immediately before the wait — never from `Start()`. Started
  there, the line lied through 111 failures, and then the self-test cried wolf against a name that
  did not exist yet (`ERROR_FILE_NOT_FOUND`, gate 3). A self-test that can be wrong in either
  direction is worth less than none.
- Mono's `NamedPipeClientStream.Connect(timeout)` does **not** retry while the name is missing; .NET's
  does, which is why that ordering bug is invisible offline and had to be caught in-game.
- The accept loop logs the **first** exception as `PPCLI FAILURE: ... the endpoint is DEAD` with the
  full stack, throttles repeats, and sleeps 1 s so a broken pipe cannot hot-spin a frame-budgeted game.

### PowerShell JSON array unrolling

`ConvertFrom-Json` unrolls a one-element JSON array into a scalar.
Any result that must stay an array needs `-NoEnumerate`.
This bug shipped once and was fixed.

## Related docs

- `PLAYBOOK.md` — intent to command line, one page.
- `plans\*.json` — each plan file's own header states what it does and what it is proven to do.
- Every `file:line` citation in this README points into the game's own decompiled assembly, not into
  this repo.
