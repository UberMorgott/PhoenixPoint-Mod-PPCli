# ISSUES — PPCLI defects to fix

**If you are the agent working on PPCLI: this is your inbox. Read it at session start.**

Running log of PPCLI defects, gaps and rough edges hit by other agents while doing real work with the
tool. Every entry is written from an actual observed run, never from reading the source. If something
was suspected but not proven, it says so.

Fix an entry, then delete it from this file (the git history keeps it). Leave anything you could not
reproduce in place, with a note on what you tried.

Format per entry: what was attempted → what happened → what was expected → evidence → severity.

---

_Empty — the three entries logged on 2026-09-01 (no screenshot channel, `deploy` into a running
install, `items` refusal indistinguishable from an empty sweep) were fixed and verified live on
`D:\PP-Instance2` the same day. The three logged on 2026-09-02 (a recycled PID winning the endpoint
pick, `new` refusing a struct, an evicted handle reported as expired) were fixed and verified live
the same day — the last two on `D:\PP-Instance3`. The fourth (plan var refs did not nest, and a real
null projected as `unresolved: … is not set`) was fixed and verified on `D:\PP-Instance3` the same
day. The three open on 2026-09-05 (no way to box a primitive for an `Object` parameter, `screenshot`
wedging the process on D3D12 at `timeScale 0`, and `screenshot` losing the scene while an upscaler
renders to a camera `targetTexture`) were fixed and verified live on `D:\PP-Instance3` under
`-force-d3d12`, build `0b0c12fc`._



## 2026-09-05 — `connect call` / `connect inspect` refuse any JSON argument built from a PowerShell variable that carries a handle

- **Attempted:** pass the JSON argument as an expression instead of a single-quoted literal, so a
  handle obtained from a previous call could be reused:
  `$h = (... | ConvertFrom-Json).result.value.h`  → `$h` = `h:15:20`, then
  `.\ppcli.ps1 connect inspect ('{"h":"' + $h + '","filter":"Length"}') -PPRoot 'D:\PP-Instance3'`
  (also tried assigning the string to `$q` first and passing `$q`).
- **Happened:** exit 1 with `{"ok":false,"error":"Cannot find drive. A drive with the name '{\"status\"' does not exist."}`.
  The quoted drive name is the start of ppcli's OWN reply object, so something inside the client is
  resolving a path over its own output. The same JSON typed as a single-quoted literal works.
- **Expected:** the argument is JSON; how the caller built the string must not matter. A handle of the
  documented `h:<epoch>:<id>` shape must be passable in `h` / `target`.
- **Evidence:** live `D:\PP-Instance3`, build `3068ae67`, D3D11, tactical mission. Reproduced 3x in a
  row. Literal-arg calls in the same session (`{"op":"get","type":"UnityEngine.Time",...}`) succeeded,
  so the endpoint was healthy. Worked around by moving the whole sequence into a plan file, where
  handles stay server-side as `${VAR.value.h}`.
- **Severity:** medium. Every multi-step reflection sequence from PowerShell has to become a plan file;
  `connect call` cannot chain on a handle at all from the shell.
- **2026-09-28 retest (not reproduced):** live `D:\PP-Instance2`, build `ab0755a4`, pwsh 7 in-process: `$h` from `(connect call '{"op":"get","target":"@geo","member":"Factions"}' | ConvertFrom-Json).result.value.h`, then `connect inspect ('{"h":"' + $h + '","filter":"Count"}')` and `connect call ('{"op":"get","target":"' + $h + '","member":"Count"}')`, with and without `-PPRoot`, and via `$q` → all `ok:true`, exit 0. The client only ever `ConvertFrom-Json`s `$Arg2`; no path cmdlet sees it. A caller-side quoting layer (e.g. `pwsh -Command "..."` from bash) is the remaining suspect - need the exact command line that failed.

## 2026-09-07 — `connect screenshot` shows the level-curtain art instead of the presented frame once a mod camera draws objects over its own blit

- **Attempted:** `connect screenshot '{"path":...}'` on the geoscape with Renderforge 1.5.x live (DLSS
  Quality) after the mod started drawing the site markers with its `DlssPresent` camera (outRT blit at
  `BeforeForwardOpaque`, then the marker layer; earlier a separate `RenderforgeMarkerCam` at depth +2).
- **Happened:** the main PNG carried the loading-screen curtain art (or a stale frame) with the markers
  and the HUD composited on top; `scenePath` carried the pre-upscale scene at 1707x960. The real
  backbuffer, read by the mod itself at `WaitForEndOfFrame` (`Texture2D.ReadPixels`, `RenderTexture.active
  = null`), showed the correct globe + markers at the same moment.
- **Expected:** the main PNG = what is on screen (the note in the reply says the scene is "blank" because
  Camera.main targets a RenderTexture, but the presented frame is not blank — a second camera blits it).
- **Evidence:** live `D:\PP-Instance2`, build `69a823ae`, PIDs 24772/34008, 2026-09-07; files
  `v0-shot.png` / `v1-shot.png` vs the mod's `DumpScreen` dumps in the Renderforge session scratchpad;
  write-up `Renderforge\docs\research\geoscape-dlss-2026-09-07\marker-cam\results.md` ("Dead ends" 3).
- **Severity:** medium for graphics-mod work: the visual acceptance test silently shows a frame nobody
  ever saw. A plain end-of-frame ReadPixels of the screen (no per-camera re-render) reproduces the
  presented frame in every configuration tried.

## 2026-09-10 — `connect console` cuts a command's output at 200 lines, so a long listing cannot be read back whole

- **Attempted:** `connect console '{"command":"ct_list","args":["audio"]}'` (and `["audio","taunt"]`,
  `["audio","Barks"]`) against ContentTool on `D:\PP-Instance2`, profile `76561197996210592`.
- **Happened:** `{"ok":true,"output":[…exactly 200 strings…],"truncated":true}`. Header line says
  `7696 of 7696 media match ''`, so 7497 rows never arrive. `taunt` (263 matches) lost 64 rows,
  including all 5 in-bank ones. There is no page/offset argument to ask for the rest.
- **Expected:** either the whole output, or a paged reply the way `members` was fixed
  (`total` + `hasMore`/`cursor`), instead of a flat cut. The mod hands the capture console the whole
  text in ONE unbounded `WriteLine` on purpose, precisely because a capture is machine-readable
  (`ContentTool\src\ContentToolMain.cs:382-388`); the 60-line bound it applies to `GameConsoleWindow`
  is a UI-vertex workaround and is deliberately not applied here.
- **Evidence:** live run 2026-09-10, build `69a823ae`, PID 28896. The cap is
  `Protocol.MaxOutputLines = 200` (`PPCLI\src\Protocol.cs:55`) applied in `PPBridgeMain.Capture.WriteLine`
  (`PPCLI\src\PPBridgeMain.cs:361`, `truncated` surfaced at `:301`). Also `Protocol.MaxOutputLineChars = 2000`
  per line (`:56`) — not hit here. Write-up:
  `ContentTool\internal-docs\planning\2026-09-10-audio-names-ingame.md` §(c).
- **Severity:** medium. Any ct_ command whose whole point is a long listing (asset/audio catalogs) is
  unreadable through the bridge; the workaround is the mod's own spill file on disk, which means
  leaving the JSON path entirely.

## 2026-09-26 — `coop.ps1 dismiss` stops at the geoscape intro cutscene

- Attempted: `coop.ps1 dismiss -Side client1|client2` right after `campaign` (3 peers).
- Happened: returns `screens:["...UIStateGeoCutscene"]`, nothing dismissed; the intro video runs ~minutes.
- Expected: dismiss skips a cutscene too. Worked around with `connect call '{"op":"invoke","target":"@viewstate","member":"OnCancel","args":[]}'` (`UIStateGeoCutscene.OnCancel` = native skip, decompile `UIStateGeoCutscene.cs:89-93`), then `dismiss` again.
- Severity: low.

## 2026-09-26 — `coop.ps1 reconnect` presses RECONNECT ~0.2 s after the relaunched menu's UI init

- Attempted: `kill -Side client2` → `relaunch -Side client2 -AllowSteamInstall` → `reconnect -Side client2`, host on geoscape.
- Happened: RECONNECT pressed 02:52:08.03, 0.2 s after `[MP][general] UI initialized`; the on-demand join ran `EnterLevel → FinishLevel` and the client stayed on HomeScreen with the roster strip at `Loading 0%` (mod-side race, logged in the Multiplayer2 field results). Same sequence with a 25 s wait between `relaunch` and `reconnect` rejoined the geoscape fine.
- Expected: `reconnect` waits for the main menu to settle (or a mod readiness probe) before pressing, so the automation does not manufacture the race; the mod should also survive it (separate Multiplayer2 bug).
- Evidence: Multiplayer2 `docs\field\2026-09-26\T5-F9-after-reconnect-client2.jpg`, client2 mod log L24-L66 of that run.
- Severity: medium.

## 2026-09-26 — `coop.ps1` cannot join clients through a relay (Multiplayer2 field run 2, three peers over VPS relay)

- `lobby`/`campaign` (Do-Lobby) and the `reconnect` fallback join every client at `127.0.0.1:<Port>`; `tools\vps-relay.ps1`
  hands out one port per client (`34242`, `34243`). Attempted a WAN run → had to replicate Do-Lobby by hand (`OnGateJoin`
  per client with its own address, NEW GAME, READY, confirm). Expected: `-JoinAddress` per client (list) or `-Relay` switch
  reading the relay's `clientJoin`. Severity: medium.

## 2026-09-28 — no way to press an IMGUI (OnGUI) control (feature request, target v0.3.0)

- Attempted (ContentTool UI audit, Instance2): drive ContentTool bench, which is Unity IMGUI (`GUILayout.Button`).
- Happened: PPCLI has no verb to click an IMGUI control / inject a mouse click at screen x,y. Worked around by `call set` on private statics (`FitBench.tab`, `ModelDoctor.browserOpen`, `panelScroll`) + `invoke` of internal pick methods.
- Expected: `connect click '{"x":..,"y":..}'` (synthesized Event for OnGUI) or IMGUI button-by-label press.
- Severity: medium (blocks screenshot-driven UI testing without source knowledge). Banner half of this entry fixed by fc8f364 (diagnostics on stream 2).

<!-- Append new entries above this line. Keep them evidence-backed. -->
