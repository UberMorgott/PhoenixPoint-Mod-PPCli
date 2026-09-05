# PPCLI agent brief

Runtime truth => query Phoenix Point with PPCLI. Decompiled source = intent only. One agent/connection per install.

## Invariants

- PowerShell 7. Exactly one compact JSON object on stdout; diagnostics/progress on stderr. Safe: `.\ppcli.ps1 ... | ConvertFrom-Json`.
- Endpoint opt-in: `<PPRoot>\Mods\PPBridge\ppcli-enabled` must sit beside `PPBridge.dll`. `deploy` never creates it. Delete marker to disarm; relaunch to re-arm.
- Install selection: `-PPRoot`; else `ppcli-install.txt`; else Steam discovery. Pin file beside `ppcli.ps1`, gitignored: line 1 absolute install path, optional line 2 SteamID64 profile; blank/comment lines ignored.
- Session gate: send nothing until `.\ppcli.ps1 connect state -PPRoot $PPRoot` answers. `index` only after gate.
- Handles (`h:<epoch>:<id>`) are session/scene-bound; TTL 900 s. Root aliases are resolved live.

## Modes and client forms

| Mode | Form | Cost/behavior |
|---|---|---|
| Live | `.\ppcli.ps1 connect <verb> '<json args>'` | Existing armed game. Measured synchronous round trip 17–60 ms. |
| Live plan | `.\ppcli.ps1 plan .\plans\<file>.json '<json vars>'` | Existing armed game; one request, cross-frame duration varies. Prefer this over PowerShell loops. |
| Live multi | `.\ppcli.ps1 connect multi '<array>'` / `@file.json` / `-` | One process, one endpoint, sequential short connections; prevalidates array, not transactional. |
| Cold one | `.\ppcli.ps1 run <verb> '<json args>'` | Launch with `-mods`, execute, stop owned PID; measured ~17 s for a menu verb. |
| Cold batch | `.\ppcli.ps1 batch .\jobs.json` | One cold launch; file is `[{"id":"x","verb":"state","args":{...}}]`. |
| Catalog | `.\ppcli.ps1 index` | Existing game; pages live defs to `catalog\defs.ndjson` + `meta.json`. |
| Deploy | `.\ppcli.ps1 deploy [-PPRoot <root>] [-Force] [-AllowRunning]` | Release build; installs DLL + metadata. |

`run`/`batch` require PPBridge activated in the selected profile, restore `Options.jopt` byte-exact after the session, delete their per-run log before launch, refuse an already-running target install, and kill only the PID they started. Use an automation copy.

Parameters: `-PPRoot ''`; `-ProfileId ''`; `-TimeoutSeconds 300`; `-InitTimeoutSeconds 90`; `-PipeTimeoutSeconds 30`; `-FaultPattern ''` (any mod frame); `-IgnoreLogFaults`; `-CatalogDir .\catalog`; deploy-only `-Force`, `-AllowRunning`. `plan` without explicit `-TimeoutSeconds` raises the client ceiling to the plan's `timeoutMs` + 60 s when needed. Direct `deploy.ps1` additionally accepts `-RefRoot` for a stripped target lacking `ModSDK`.

## Live verbs: exact argument envelopes

All shapes are JSON objects. `?` = optional. No-argument verbs omit the JSON argument.

| Verb | Args |
|---|---|
| `ping` | — |
| `state` | — |
| `roots` | — |
| `console` | `{command, args?:[]}` |
| `var` | read `{name}`; set-then-read `{name,value}`; values convert through strings |
| `screenshot` | `{path?,force?}`; explicit path must be absolute; omitted => timestamped PNG beside bridge files. Camera.main with a `targetTexture` (upscaler) => scene written to a sibling `*.scene.png`, reply adds `scenePath`. D3D12 + `timeScale==0` refused (wedges the process) => use `0.0001` or `force:true`. `-Window` (client switch) grabs the game window AFTER present via `PrintWindow(PW_RENDERFULLCONTENT)` => the finished frame incl. upscaler + post-upscale passes, device pixels, `{ok,mode:"window",path,width,height,bytes}`; needs a non-minimized window |
| `call` | new `{op:"new",type,assembly?,args?:[]}`; get `{op:"get",type\|target,assembly?,member,convertTo?}`; set `{op:"set",type\|target,assembly?,member,value}`; invoke `{op:"invoke",type\|target,assembly?,member,args?:[],sig?:[],typeArgs?:[]}` |
| `types` | `{pattern,assembly?}` |
| `members` | `{type\|h,assembly?,filter?,page?,pageSize?}`; page 0-based; max/default page size 400 |
| `inspect` | `{h,filter?,page?,pageSize?,values?}`; `h` also accepts a root or `@def:<name\|guid>` |
| `items` | `{h,page?,pageSize?}`; page 0-based; default 50, max 200 |
| `release` | `{h}` |
| `find` | search `{query,type?,assembly?}`; enumerate `{all:true,page?,pageSize?,query?,type?,assembly?}`; enumeration default/max 200 |
| `wait` | one of `{ready:true}`, `{phase:"tactical\|geoscape\|menu\|loading"}`, `{call:{...}}`, `{forMs:N}`; plus `not?`, `timeoutMs?`, `everyFrames?` |
| `observe` | start `{action:"start",target?:<actor instanceId int>}`; read `{action:"read",aim?:[x,y,z]}`; `{action:"stop\|mark\|status"}` |
| `snapshot` | `{name,timeoutMs?}` |
| `restore` | `{name}`; issue-only; follow with `wait` |
| `plan` | `{plan:{steps,finally?,vars?,output?,timeoutMs?,maxSteps?},vars?,timeoutMs?,maxSteps?}` or direct `{steps,...}` |
| `status` | `{jobId}` |
| `cancel` | `{jobId}` |

`call` targets: static `type`; instance handle; `@game`, `@phoenix`, `@defs`, `@level`, `@geo`, `@tac`, `@map`, `@view`, `@viewstate`, `@modules`, `@faction`, `@selected`; def `@def:<exact-name|guid>`. Argument envelopes: `{"$h":...}`, `{"$def":...}`, `{"$type":...}`, `{"$enum":...}`, `{"$array":[...]}`, `{"$v2":[...]}`, `{"$v3":[...]}`, `{"$quat":[...]}`, `{"$box":{"type":"System.Single","value":0.5}}` (boxes a primitive as a named type for a parameter declared `Object` — a bare JSON number boxes as `Double`). Reflection supports `new|get|set|invoke`; no event subscription, by-ref/out/pointer calls; indexers via `get_Item`/`set_Item`; equal overload ties refuse.

## Reply and exit contract

- Live transport reply: synchronous/cross-frame completion => `{status:"done",id,jobId,result:<verb DTO>}`. Cross-frame work may first be accepted; client polls internally and still prints one final object.
- Verb success DTO starts `{ok:true,...}`. An `ok:false` verb DTO carries `error` (plus `code` for reflection/plan/observer refusals) and no result-payload key such as `value`, `items`, or `output`; generic protocol/console/screenshot refusals may omit `code`.
- A refused verb exits 1 in live modes: `connect <verb>`, `connect multi`, and client `plan`; success exits 0. Local parse/discovery/deploy errors print top-level `{ok:false,error}` and exit 1. Check `$LASTEXITCODE` immediately.
- Cold `run`/`batch` return `{ok,build,stale,done,log,results:[{id,result}]}`. Inspect outer `ok`, `stale`, and every `results[].result.ok`; current cold-mode exit status does not aggregate inner verb refusals.
- Failed plan: `output` absent/null, `outputWithheld` explains, `step` + `result` identify failure. For failed `wait`, read `result.last`, `result.lastError`, `result.predicate` first.

## Deploy/build-stamp guards

- `deploy` path-matches running `PhoenixPointWin64.exe` processes to the target install. Default: refuse before build/write; live DLL cannot hot-swap. `-AllowRunning`: warning only; files staged for next launch. `-Force` is different: overrides a pinned-install mismatch.
- Redeploy after every bridge edit; restart game. In cold `run`/`batch`, `stale:true` means loaded build != deployed DLL hash: outer `ok:false`; every reported result is a ghost. Do not use any figure.

## Operating traps

1. Launch with `-mods`; enable `com.morgott.PPBridge` once in the in-game manager; arm marker separately.
2. Gate every session with `connect state`; initialization queries can hang. `index` after gate only.
3. Prefer one plan to repeated `connect` calls: lower latency, bounded steps/time, cross-frame waits, `finally` cleanup on success/failure/timeout/cancel.
4. Check phase/`levelState`; wrong-phase root = `null`. Menu `phase` may precede HomeScreen `level.IsPlaying`; wait for both before next load.
5. Redeploy + restart after mod edits. Never ignore `stale:true`.
6. `restore` only issues `load_game`; it has no completion signal. Follow with phase/readiness waits. A mod-incompatible save may stall instead of error.
7. Client timeout cancels cross-frame work; plan runs `finally`. `cancel` cannot interrupt a synchronous reflection call already executing.
8. Plan caps: timeout 900000 ms; steps default 200/hard 2000; repeat 100; trace 500. Caller vars override file vars.
9. Definition names: run `index` once/build. Resolution: exact def/research id -> exact alias -> unique substring -> refuse candidates; no catalog => pass-through warning.
10. One install, one driver. Handles die on scene unload/process restart. Release early with `release` if useful.
11. Treat `ok:false`, failed trace assertion, `status:"timeout"`, or `stale:true` as failure. Never infer success from visible game state.
12. Disarm when done: delete `Mods\PPBridge\ppcli-enabled`; endpoint stops after the periodic check, parked plan still gets cleanup; relaunch required to re-arm.

Next: [`PLAYBOOK.md`](PLAYBOOK.md) = intent -> command; [`docs/REFERENCE.md`](docs/REFERENCE.md) = deep protocol/plan/reflection/security reference; [`ISSUES.md`](ISSUES.md) = defect inbox—log what you hit there; do not derail the current task.
