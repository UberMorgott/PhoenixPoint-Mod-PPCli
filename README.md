# PPCLI

Terminal control channel into a running **Phoenix Point**: the `PPBridge` developer mod (`com.morgott.PPBridge`, version in [`meta.json`](meta.json)) plus the PowerShell 7 client `ppcli.ps1`.

**Why:** decompiled code shows intent; PPCLI shows what the game actually did. Developers and AI agents use it to read live state, confirm a mod patch took effect, and drive the game (reflection, console, UI, tactical actions) without playing by hand.

**Development tool only.** The `PPBridge` DLL is also on the Steam Workshop (off until armed); the client is only here. It can change a save through reflection - point it at an automation copy of the game, not the install you play.

## Install

Needs Windows, PowerShell 7, a .NET SDK, and a Phoenix Point install with `ModSDK\`.

```powershell
$PPRoot = 'D:\path\to\PhoenixPoint'
.\ppcli.ps1 deploy -PPRoot $PPRoot        # build + copy into $PPRoot\Mods\PPBridge
New-Item -ItemType File (Join-Path $PPRoot 'Mods\PPBridge\ppcli-enabled')   # arm the endpoint
```

1. Launch the game with `-mods`, enable **PPBridge** in the mod manager once, restart with `-mods`.
2. First request, and wait for its answer before anything else:
   `.\ppcli.ps1 connect state -PPRoot $PPRoot | ConvertFrom-Json`
3. Done: delete `ppcli-enabled` (disarms; `deploy` never creates it).

Install selection: `-PPRoot`, else line 1 of `ppcli-install.txt` (optional line 2 = SteamID64 profile), else Steam discovery.

## For agents

Compressed contract. Rules: [`AGENTS.md`](AGENTS.md) · intent -> command: [`PLAYBOOK.md`](PLAYBOOK.md) · full envelopes/limits: [`docs/REFERENCE.md`](docs/REFERENCE.md) · bug inbox: [`ISSUES.md`](ISSUES.md).

- **Gate:** one driver per install. Send nothing until `connect state` answers; `index` only after. Handles `h:<epoch>:<id>` die on scene change/restart (TTL 900 s).
- **Wire:** stdout = ONE compact JSON object; notes -> stderr, off by default (`-Verbose`/`PPCLI_VERBOSE=1` on, `-Quiet`/`PPCLI_QUIET=1` off). Live reply `{status:"done",result:{ok:true,...}}`; `status:"timeout"` keeps `jobId`. Refusal `ok:false` + `error` (+`code`), no payload key, exit 1 - check `$LASTEXITCODE` at once. Bad paging args -> `code:"args"`, never clamped.
- **Modes:** `connect <verb> '<json>'` (live, 17-60 ms) · `plan <file> '<vars>'` (bounded cross-frame steps + `finally`; prefer over client loops) · `connect multi '<array>'|@file.json|-` (sequential, not transactional) · `run <verb> '<json>'` / `batch <file>` (cold launch ~17 s, restores options, kills only own PID) -> check outer `ok`, `stale`, each `results[].result.ok` · `index` (def catalog -> `catalog\defs.ndjson`) · `test <dir|file> [-Cold] [-JUnit x.xml]` (mod test harness: `*.test.json` = plan + `expect`, one pass/fail summary) · `deploy`.
- **Safety:** `act use`/`endTurn` and `ui click` (also inside `multi`/`batch`/`plan`) against a Steam-library install are refused without `-AllowMutate`. `deploy` refuses a running target (`-AllowRunning` = stage for next launch; `-Force` = ignore pinned install). `stale:true` = old DLL, discard results. A dispatched click is not proof of effect.
- **Options:** `-PPRoot` `-ProfileId` `-TimeoutSeconds`(300) `-PipeTimeoutSeconds`(30) `-FaultPattern` `-IgnoreLogFaults` `-Window` `-AllowMutate` `-AllowRunning` `-Force` `-Quiet`/`-Verbose`; `test`: `-Cold` `-JUnit` `-Only`.

| Verb | Purpose | Key args |
|---|---|---|
| `ping` `state` `roots` | Build/protocol, scene/phase, live root handles | Gate on `state`; re-resolve roots after scene change |
| `call` | Reflection new/get/set/invoke | `op`, `type`\|`target`, `member`, `args?`, `sig?`; `@tac`, `@geo`, `@def:<name>`... |
| `types` `members` `inspect` | Discover types/members, read values | `pattern` / `type`\|`h`; `page?` `pageSize?`; `values:true` |
| `find` `items` `release` | Find defs, page a collection, free a handle | `query`\|`all:true`; `h` |
| `console` | Run native command once, page output | `command`, `args?`, `pageLines?`, `pageBytes?`; `cursor` (120 s) |
| `var` | Get/set console variable | `name`, `value?` (string) |
| `log` `events` | Page Unity log; C# event subscriptions | `since` -> `next`, `match?`; `subscribe:{target\|type,event}` |
| `wait` | Cross-frame predicate | `ready` `phase` `call` `forMs` `log` `event` `trace`; `not?` `timeoutMs?` |
| `trace` | Runtime Harmony hook on any game/mod method: did it run, args, return | `start:{type,method,sig?,args?,ret?,stack?}` -> `id`; `{id}` -> `hits`+rows; `stop`; ends on TTL/maxHits/scene |
| `plan` `status` `cancel` | Run / inspect / cancel a job | `plan:{steps,finally?,vars?}`; `jobId` |
| `observe` | Record live observations | `action`: start, read, mark, status, stop |
| `screenshot` | PNG of the frame | `path?` (absolute), `mode?` `backbuffer`\|`capture`; client `-Window` = window grab |
| `act` | Tactical squad, abilities, use, end turn | `squad` `list` `use:{ability,target?}` `endTurn` |
| `ui` | Native uGUI tree / click | `tree:{match?}`, `from?`; `click:{label\|path}` |
| `imgui` | Mod OnGUI controls | `list:true`; `press:{label,owner?,index?}`; read `alive`/`errors` |
| `snapshot` `restore` | Named game state | `name`; `restore` only issues - follow with `wait` |

## License

[CC BY-NC 4.0](LICENSE). Copyright (c) 2026 Morgott.
