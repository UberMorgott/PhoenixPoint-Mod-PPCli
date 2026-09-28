# PPCLI agent brief

Runtime truth => query Phoenix Point via PPCLI. Decompiled source = intent only. One agent/connection per install. Intent -> command: [`PLAYBOOK.md`](PLAYBOOK.md) (index at top). Full envelopes/prose: [`docs/REFERENCE.md`](docs/REFERENCE.md) § "Full verb envelopes".

## Invariants

- PowerShell 7. ONE compact JSON object on stdout; diagnostics → stderr. Notes off by default; `-Verbose`/`PPCLI_VERBOSE=1` on, `-Quiet`/`PPCLI_QUIET=1` force off. Safe: `.\ppcli.ps1 ... | ConvertFrom-Json`.
- Opt-in: `<PPRoot>\Mods\PPBridge\ppcli-enabled` beside `PPBridge.dll`; `deploy` never creates it. Delete = disarm; relaunch = re-arm.
- Install: `-PPRoot`; else `ppcli-install.txt` (line 1 path, opt. line 2 SteamID64); else Steam discovery.
- GATE: send nothing until `.\ppcli.ps1 connect state -PPRoot $PPRoot` answers. `index` only after gate.
- Handles `h:<epoch>:<id>`: per session/scene, TTL 900 s. Root aliases resolved live.

## Modes

| Mode | Form |
|---|---|
| Live (17–60 ms) | `.\ppcli.ps1 connect <verb> '<json>'` |
| Live plan (prefer over loops) | `.\ppcli.ps1 plan .\plans\<file>.json '<vars>'` |
| Live multi | `.\ppcli.ps1 connect multi '<array>'` / `@file.json` / `-` (sequential, not transactional) |
| Cold one (~17 s) / batch | `.\ppcli.ps1 run <verb> '<json>'` / `batch .\jobs.json` (`[{"id","verb","args"}]`) |
| Catalog | `.\ppcli.ps1 index` → `catalog\defs.ndjson` |
| Deploy | `.\ppcli.ps1 deploy [-PPRoot] [-Force] [-AllowRunning]` |

`run`/`batch` restore `Options.jopt`, delete run log, refuse running target, kill only own PID — use automation copy.

## Verbs (short; `?` = optional)

| Verb | Args → reply |
|---|---|
| `ping` `state` `roots` | — |
| `console` | `{command,args?,pageLines?,pageBytes?}` / `{cursor,...}` → `{output,total,hasMore,cursor?,clipped?,truncated?}`; runs ONCE; 50 lines / 8 KiB default; cursor 120 s, bad → `code:"cursor"` |
| `var` | `{name}` / `{name,value}` (strings) |
| `screenshot` | `{path?(absolute),mode?,force?}` → `{ok,mode,path,width,height,bytes}`; `mode` `backbuffer` (default, 0.3.3: end-of-frame ReadPixels = presented frame, incl. mod camera blits) / `capture` (old CaptureScreenshotAsTexture + upscaler `scenePath`); other → `code:"args"`. `-Window` = finished frame via PrintWindow |
| `call` | `{op:"new\|get\|set\|invoke",type\|target,assembly?,member,args?,sig?,typeArgs?,value?,convertTo?}` |
| `types` | `{pattern,assembly?,page?,pageSize?(25/100),generated?}` |
| `members` / `inspect` | `{type\|h,filter?,page?,pageSize?(50/400),inherited?,generated?}`; inspect `values:true` |
| `items` | `{h,page?,pageSize?(50/200)}` |
| `release` | `{h}` |
| `find` | `{query,type?,page?,pageSize?(25/100),guids?}` / `{all:true,...}` (50/200) → `{count,total,page,pageSize,hasMore,defs}` |
| `wait` | `{ready}` `{phase}` `{call}` `{forMs}` `{log:"rx",level?,since?}` `{event:{sub\|target+event,match?,since?}}` + `not?,timeoutMs?,everyFrames?` |
| `log` | `{since?,level?,match?,pageSize?,pageBytes?,clip?,stack?}` → `{rows?:[{s,l,m}],next,hasMore?,dropped?}`; pass `next` as `since` |
| `events` | `{subscribe:{target\|type,event}}` → `{sub,next}`; `{since?,sub?,match?}` → `{rows?:[{s,sub,a}],next,ended?}`; `{unsubscribe}` `{list:true}` |
| `imgui` | `{list:true,owner?,match?}` → `{total,rows:[{l,i?,k?,on?,dis?,o,r}]}`; `{press:{label,owner?,index?,mode?},waitFrames?,diag?}` → `{fired:true,mode,ev,repaired,repairs?,unrepaired?,capped?,alive?,errors?,warn?}` (force settles 2 frames) / `code:"ambiguous\|notfound\|disabled\|nohwnd\|offscreen\|noevent\|missed\|notfired\|scene\|busy"`; mode `force` (default) = forced on Repaint/MouseMove; `post` = PostMessage'd real click (opt-in, EXPERIMENTAL, live-failed 0.3.1: `noevent` even focused). Bound to owner |
| `act` | `{squad:true\|"all"}` → `{turn:{n,f,mine},actors}`; `{list:{actor?,ability?,src?,targets?,all?}}` → `{actor,turn,abilities:[{i,def,t,src?,dis?,tk,ap?}]}` (`targets:true` needs `ability`, capped); `{use:{actor?,ability,src?,target?:{actor}\|{pos:[x,y,z]}},waitMs?(20000)}` → settles → `{exec,ap:[b,a],hp?,dead?,pos?}`; `{endTurn:true}`. Codes `turn busy disabled(+dis) notarget targetKind noability noactor ambiguous(+candidates) scene cap timeout(+settle) cancelled threw stale`; timeout/cancel = `issued:true`, not undone. Live-verified 0.3.1 |
| `ui` | game's native uGUI (PP screens, TFTV panels). `{tree:{match?,root?,interactable?,all?,full?},from?,pageSize?(25/200),pageBytes?(8K)}` → `{total,rows:[{l,p,t,dis?,hid?,off?,r:[x,y,w,h]}],next?}` (`next` → pass as `from`; `r` = screenshot px, top-left; labels LOCALIZED → prefer `path`; `all` = + switched-off screens, slow; `full` = unclipped `l`/`p`); `{click:{label\|path,index?,force?},waitFrames?(1)}` → raycast FIRST, then StandaloneInputModule sequence from the real hit → `{clicked:p,handler:"pointerClick"\|"pointerDown",target?,frames}`; `ok` = DISPATCHED, confirm effect. Codes `blocked(+top) noraycast` (nothing sent; `force:true` overrides) `unsupported`(slider/scrollbar) `threw`(handler exception, click happened) `notfound(+near) ambiguous(+candidates w/ path) disabled(+row) noclick args`. `diag:true` → `scanMs`. `click` = MUTATING (`-AllowMutate` on the played install). Live-verified 0.3.2 |
| `observe` | `{action:"start",target?}` / `read {aim?,page?,pageSize?(10/200)}` / `stop\|mark\|status` |
| `snapshot` / `restore` | `{name,timeoutMs?}` / `{name}` (issue-only → follow with `wait`) |
| `plan` | `{plan:{steps,finally?,vars?,output?,timeoutMs?,maxSteps?,trace?},vars?}`; `trace:"errors"` default, `"full"` opt-in |
| `status` / `cancel` | `{jobId}` |

`call` targets: `type`, handle, `@game @phoenix @defs @level @geo @tac @map @view @viewstate @modules @faction @selected @soldier` (selected if a soldier, else player's first alive non-vehicle), `@def:<name|guid>`. Arg envelopes `$h $def $type $enum $array $v2 $v3 $quat $box $new`; `sig:[]` = zero-arg only. No by-ref/out; indexers via `get_Item`/`set_Item`.

## Reply and exit contract

- Transport: `{status:"done",result:<DTO>}`; `status:"timeout"` keeps `jobId`.
- Success `{ok:true,...}`. `ok:false` has `error` (+`code`), NO payload key. Refused verb exits 1 (`connect`, `multi`, `plan`); check `$LASTEXITCODE` at once. Local errors → `{ok:false,error}`, exit 1.
- Cold `run`/`batch` → `{ok,build,stale,done,log,results:[{id,result}]}`; check outer `ok`, `stale`, every `results[].result.ok` (exit code does not aggregate).
- Plan omits nulls: failure `code,error,step?,result?`, no `output`.
- Paging: `hasMore` only. Bad `page`/`pageSize`/`pageLines`/`pageBytes`/`since`/`index`/`waitFrames` → `code:"args"`, never clamped; strict JSON ints. `call` threw → `at` ≤3 frames. Failed `wait` → read `result.last`/`lastError`/`predicate`.

## Deploy / build-stamp

- `deploy` refuses when target install's game runs (exe path match); `-AllowRunning` = stage for next launch; `-Force` = override pinned install only.
- Redeploy + restart after every bridge edit. `stale:true` = ghost results, use no figure.

## Traps

1. Launch `-mods`; enable `com.morgott.PPBridge` once; arm marker separately.
2. Gate with `connect state` — init queries hang.
3. Plan over `connect` loops: bounded, cross-frame, `finally` always runs.
4. Wrong-phase root = `null`. `phase:"summary"`/`"other"` refuse new game → `@phoenix.FinishLevelAndGoToLobby(0)` first.
5. Never ignore `stale:true`.
6. `restore` = issue only; incompatible save may stall silently.
7. Client timeout cancels cross-frame work; sync reflection call can't be interrupted.
8. Caps: plan 900000 ms; steps 200/2000; repeat 100; trace 500. Caller vars override file vars.
9. Names: `index` once/build; exact → alias → unique substring → refuse w/ candidates.
10. One install, one driver. Handles die on scene unload/restart.
11. `ok:false`, failed assertion, `timeout`, `stale:true` = failure; never infer success from visuals.
12. Disarm when done: delete `ppcli-enabled`.
13. Co-op `-Lite`/`stop`/`restore` details → REFERENCE § "Full verb envelopes" (Client).
14. `imgui` default `force` runs the body mid-Repaint. Since 0.3.2 the rest of that pass pads layout mismatches (no more `ArgumentException: Getting control N's position...` closing the ContentTool bench; live unit row → `repaired:98,alive:true`). Still read `alive` (false = panel gone) + `errors`; never use `GUIUtility.ExitGUI`-style fixes - FitBench's `catch (Exception)` closes on it. `post` (opt-in) does not click live (`noevent`).
15. `act use`/`endTurn` (also inside `multi`/`batch`/`plan`) against a Steam-library install (the one the user plays) → client refuses unless `-AllowMutate`. `NewTurnEvent` also fires for AI turns → re-check `act {squad:true}` `turn.mine`.
16. `connect` wait + mod exception in log: client pings the main thread; answers → job continues, reply gets `logFaults[]`; silent → DEAD RUN. Cold `run`/`batch` still fast-fail on any mod frame.

## Release (local build, no CI)

1. Bump `Version`/`AssemblyVersion`/`FileVersion` in `PPBridge.csproj` + `meta.json` (`X.Y.Z.0`).
2. `dotnet build -c Release /p:PPRoot="D:\PP-Instance2"`; SelfCheck (`dotnet build .\selfcheck\SelfCheck.csproj -c Release /p:PPRoot=...` then `dotnet .\selfcheck\bin\Release\SelfCheck.dll`); every `tests\*.tests.ps1` + `selfcheck\client-pipetest.ps1`, each also `-Falsify`, all exit 0.
3. Zip FLAT `PPBridge.dll` + `meta.json` → `PPBridge-X.Y.Z.zip`.
4. `git commit -m "chore(release): vX.Y.Z"`, `git tag -a vX.Y.Z -m vX.Y.Z`, `git push origin main --tags`.
5. `gh release create vX.Y.Z <zip> -R UberMorgott/PhoenixPoint-Mod-PPCli --title vX.Y.Z --notes-file <notes> --latest --verify-tag`.
6. `.\ppcli.ps1 deploy` (`-AllowRunning` if game runs).

[`ISSUES.md`](ISSUES.md) = PPCLI defect inbox — log hits, don't derail.
