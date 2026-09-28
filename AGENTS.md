# PPCLI agent brief

Runtime truth => query Phoenix Point via PPCLI. Decompiled source = intent only. One agent/connection per install.

## Invariants

- PowerShell 7. One compact JSON object on stdout; diagnostics/progress → process stderr (`[Console]::Error`, not ErrorRecords: in-process `2>$null` can't silence). `-Quiet` / env `PPCLI_QUIET=1` drops routine notes (install/pipe banner, polling); warnings/refusals/errors stay. Safe: `.\ppcli.ps1 ... | ConvertFrom-Json`.
- Endpoint opt-in: `<PPRoot>\Mods\PPBridge\ppcli-enabled` must sit beside `PPBridge.dll`. `deploy` never creates it. Delete marker = disarm; relaunch = re-arm.
- Install selection: `-PPRoot`; else `ppcli-install.txt`; else Steam discovery. Pin file beside `ppcli.ps1`, gitignored: line 1 absolute install path, optional line 2 SteamID64 profile; blank/comment lines ignored.
- Session gate: send nothing until `.\ppcli.ps1 connect state -PPRoot $PPRoot` answers. `index` only after gate.
- Handles (`h:<epoch>:<id>`) bound to session/scene; TTL 900 s. Root aliases resolved live.

## Modes and client forms

| Mode | Form | Cost/behavior |
|---|---|---|
| Live | `.\ppcli.ps1 connect <verb> '<json args>'` | Existing armed game. Measured sync round trip 17–60 ms. |
| Live plan | `.\ppcli.ps1 plan .\plans\<file>.json '<json vars>'` | Existing armed game; one request, cross-frame duration varies. Prefer over PowerShell loops. |
| Live multi | `.\ppcli.ps1 connect multi '<array>'` / `@file.json` / `-` | One process, one endpoint, sequential short connections; prevalidates array, not transactional. |
| Cold one | `.\ppcli.ps1 run <verb> '<json args>'` | Launch with `-mods`, execute, stop owned PID; ~17 s measured for menu verb. |
| Cold batch | `.\ppcli.ps1 batch .\jobs.json` | One cold launch; file = `[{"id":"x","verb":"state","args":{...}}]`. |
| Catalog | `.\ppcli.ps1 index` | Existing game; pages live defs to `catalog\defs.ndjson` + `meta.json`. |
| Deploy | `.\ppcli.ps1 deploy [-PPRoot <root>] [-Force] [-AllowRunning]` | Release build; installs DLL + metadata. |

`run`/`batch`: need PPBridge activated in selected profile, restore `Options.jopt` byte-exact after session, delete per-run log before launch, refuse already-running target install, kill only own PID. Use automation copy.

Parameters: `-PPRoot ''`; `-ProfileId ''`; `-TimeoutSeconds 300`; `-InitTimeoutSeconds 90`; `-PipeTimeoutSeconds 30`; `-FaultPattern ''` (any mod frame); `-IgnoreLogFaults`; `-CatalogDir .\catalog`; `-Quiet`; deploy-only `-Force`, `-AllowRunning`. `plan` without explicit `-TimeoutSeconds` raises client ceiling to plan's `timeoutMs` + 60 s when needed. Direct `deploy.ps1` also accepts `-RefRoot` for stripped target lacking `ModSDK`.

## Live verbs: exact argument envelopes

All shapes JSON objects. `?` = optional. No-arg verbs omit JSON arg.

| Verb | Args |
|---|---|
| `ping` | — |
| `state` | — |
| `roots` | — |
| `console` | `{command, args?:[]}` |
| `var` | read `{name}`; set-then-read `{name,value}`; values convert via strings |
| `screenshot` | `{path?,force?}`; explicit path must be absolute; omitted => timestamped PNG beside bridge files. Camera.main with `targetTexture` (upscaler) => scene written to sibling `*.scene.png`, reply adds `scenePath`. D3D12 + `timeScale==0` refused (wedges process) => use `0.0001` or `force:true`. `-Window` (client switch) grabs game window AFTER present via `PrintWindow(PW_RENDERFULLCONTENT)` => finished frame incl. upscaler + post-upscale passes, device pixels, `{ok,mode:"window",path,width,height,bytes}`; needs non-minimized window |
| `call` | new `{op:"new",type,assembly?,args?:[],sig?:[]}`; get `{op:"get",type\|target,assembly?,member,convertTo?}`; set `{op:"set",type\|target,assembly?,member,value}`; invoke `{op:"invoke",type\|target,assembly?,member,args?:[],sig?:[],typeArgs?:[]}` |
| `types` | `{pattern,assembly?}` |
| `members` | `{type\|h,assembly?,filter?,page?,pageSize?}`; page 0-based; max/default page size 400 |
| `inspect` | `{h,filter?,page?,pageSize?,values?}`; `h` also accepts root or `@def:<name\|guid>` |
| `items` | `{h,page?,pageSize?}`; page 0-based; default 50, max 200 |
| `release` | `{h}` |
| `find` | search `{query,type?,assembly?}`; enumerate `{all:true,page?,pageSize?,query?,type?,assembly?}`; enumeration default/max 200 |
| `wait` | one of `{ready:true}`, `{phase:"tactical\|geoscape\|menu\|summary\|other\|loading"}`, `{call:{...}}`, `{forMs:N}`; plus `not?`, `timeoutMs?`, `everyFrames?` |
| `observe` | start `{action:"start",target?:<actor instanceId int>}`; read `{action:"read",aim?:[x,y,z]}`; `{action:"stop\|mark\|status"}` |
| `snapshot` | `{name,timeoutMs?}` |
| `restore` | `{name}`; issue-only; follow with `wait` |
| `plan` | `{plan:{steps,finally?,vars?,output?,timeoutMs?,maxSteps?},vars?,timeoutMs?,maxSteps?}` or direct `{steps,...}` |
| `status` | `{jobId}` |
| `cancel` | `{jobId}` |

`call` targets: static `type`; instance handle; `@game`, `@phoenix`, `@defs`, `@level`, `@geo`, `@tac`, `@map`, `@view`, `@viewstate`, `@modules`, `@faction`, `@selected`; def `@def:<exact-name|guid>`. Arg envelopes: `{"$h":...}`, `{"$def":...}`, `{"$type":...}`, `{"$enum":...}`, `{"$array":[...]}`, `{"$v2":[...]}`, `{"$v3":[...]}`, `{"$quat":[...]}`, `{"$box":{"type":"System.Single","value":0.5}}` (boxes primitive as named type for param declared `Object` — bare JSON number boxes as `Double`), `{"$new":{"type":"…","args":[…],"fields":{…}}}` (builds the argument; the ONLY way to pass an inlined struct like `EarthUnits` back, since it projects without a handle — `args` picks the ctor, `sig` disambiguates it, `fields` sets fields after it). `sig` present = filter for `new` AND `invoke`: `sig:[]` = zero-arg overload only (omit `sig` for no filter); struct `new` with no args = default instance unless a non-empty `sig` names a ctor (no match => `code:"overload"`); static `.cctor` never a `new` candidate. Reflection: `new|get|set|invoke`; no event subscription, no by-ref/out/pointer calls; indexers via `get_Item`/`set_Item`; equal overload ties refused.

## Reply and exit contract

- Live transport reply: sync/cross-frame completion => `{status:"done",id,jobId,result:<verb DTO>}`. Cross-frame work may be accepted first; client polls internally, still prints one final object.
- Verb success DTO starts `{ok:true,...}`. `ok:false` verb DTO carries `error` (plus `code` for reflection/plan/observer refusals), no result-payload key like `value`, `items`, `output`; generic protocol/console/screenshot refusals may omit `code`.
- Refused verb exits 1 in live modes: `connect <verb>`, `connect multi`, client `plan`; success exits 0. Local parse/discovery/deploy errors print top-level `{ok:false,error}`, exit 1. Check `$LASTEXITCODE` immediately.
- Cold `run`/`batch` return `{ok,build,stale,done,log,results:[{id,result}]}`. Check outer `ok`, `stale`, every `results[].result.ok`; cold-mode exit status currently doesn't aggregate inner verb refusals.
- Failed plan: `output` absent/null, `outputWithheld` explains, `step` + `result` locate failure. Failed `wait` → read `result.last`, `result.lastError`, `result.predicate` first.

## Deploy/build-stamp guards

- `deploy` path-matches running `PhoenixPointWin64.exe` processes to target install. Default: refuse before build/write; live DLL can't hot-swap. `-AllowRunning`: warning only; files staged for next launch. `-Force` differs: overrides pinned-install mismatch.
- Redeploy after every bridge edit; restart game. Cold `run`/`batch` `stale:true` = loaded build != deployed DLL hash: outer `ok:false`; every reported result ghost. Use no figure.

## Operating traps

1. Launch with `-mods`; enable `com.morgott.PPBridge` once in in-game manager; arm marker separately.
2. Gate every session with `connect state`; init queries can hang. `index` only after gate.
3. Prefer one plan over repeated `connect` calls: lower latency, bounded steps/time, cross-frame waits, `finally` cleanup on success/failure/timeout/cancel.
4. Check phase/`levelState`; wrong-phase root = `null`. Menu `phase` may precede HomeScreen `level.IsPlaying`; wait for both before next load. `phase:"summary"` = game-over screen, `phase:"other"` = some other playing level (intro/cutscene): NEITHER accepts a new game — leave via `@phoenix.FinishLevelAndGoToLobby(0)` first, a `PlayNewGameResult` there ends the game coroutine and the process stops.
5. Redeploy + restart after mod edits. Never ignore `stale:true`.
6. `restore` only issues `load_game`; no completion signal. Follow with phase/readiness waits. Mod-incompatible save may stall, not error.
7. Client timeout cancels cross-frame work; plan runs `finally`. `cancel` can't interrupt sync reflection call already running.
8. Plan caps: timeout 900000 ms; steps default 200/hard 2000; repeat 100; trace 500. Caller vars override file vars.
9. Definition names: run `index` once/build. Resolution: exact def/research id -> exact alias -> unique substring -> refuse with candidates; no catalog => pass-through warning.
10. One install, one driver. Handles die on scene unload/process restart. Release early via `release` if useful.
11. `ok:false`, failed trace assertion, `status:"timeout"`, `stale:true` = failure. Never infer success from visible game state.
12. Disarm when done: delete `Mods\PPBridge\ppcli-enabled`; endpoint stops after periodic check, parked plan still gets cleanup; relaunch needed to re-arm.
13. Co-op `tools\coop.ps1 launch|relaunch -Lite` = quarter-screen tiled windows + VeryLow preset, shadows off. Unity `-screen-*` args alone useless: game re-applies profile `Options_ScreenWidth/Height/Mode` at boot (`OptionsManager.InitVideoOptions`), so `-Lite` edits each peer's profile `Options.jopt` (id = Goldberg `force_steamid.txt`, else Steam `ActiveUser`) + snapshots shared HKCU PlayerPrefs (one key for ALL installs); originals backed up once in `tools\coop-lite\`, put back by `stop` / `restore` after process exit. After `kill` without `relaunch` => `coop.ps1 restore`. A non-`-Lite` start of a still-backed install restores first. `stop` polls the process LIST until killed pids vanish (≤90 s) — never `Wait-Process` (returns in ms on a killed-but-tearing-down game). `-Lite` commit bar = 8 GB/peer (measured ~5.35), explicit `-MinFreeCommitGB` wins. `launch` checks EVERY peer (incl. `-Lite` profile editability) before starting any, records each pid (pid+start time+exe) right after its start, and rolls back a partial start (stops started peers, restores lite edits). `stop` kills only entries whose pid+start time+exe still match; legacy pid-only lines refused, never killed.

## Release (local build, no CI)

1. Bump `Version`/`AssemblyVersion`/`FileVersion` in `PPBridge.csproj` + `"Version"` in `meta.json` (`X.Y.Z.0`). No CHANGELOG; notes live on the GitHub release (`git log --oneline vPREV..HEAD`).
2. `dotnet build -c Release /p:PPRoot="D:\PP-Instance2"` (bare build fails: needs `<PPRoot>\ModSDK`). Run every `tests\*.tests.ps1` + `selfcheck\client-pipetest.ps1` (`pwsh -NoProfile -File`, each also with `-Falsify`), all exit 0.
3. Zip FLAT: `PPBridge.dll` + `meta.json` from `bin\Release\PPBridge\` → `PPBridge-X.Y.Z.zip` (no folder, no pdb).
4. `git commit -m "chore(release): vX.Y.Z"`, `git tag -a vX.Y.Z -m vX.Y.Z`, `git push origin main --tags` (no force).
5. `gh release create vX.Y.Z <zip> -R UberMorgott/PhoenixPoint-Mod-PPCli --title vX.Y.Z --notes-file <notes> --latest --verify-tag`.
6. `.\ppcli.ps1 deploy` (`-AllowRunning` if the target game runs → staged for next launch).

Next: [`PLAYBOOK.md`](PLAYBOOK.md) = intent -> command; [`docs/REFERENCE.md`](docs/REFERENCE.md) = deep protocol/plan/reflection/security reference; [`ISSUES.md`](ISSUES.md) = defect inbox—log hits there; don't derail current task.