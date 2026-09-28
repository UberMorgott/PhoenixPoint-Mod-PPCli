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
`-force-d3d12`, build `0b0c12fc`. The four open on 2026-09-07/26 (`screenshot` showing the curtain
under a mod camera, `coop.ps1 dismiss` stopping at a cutscene, `reconnect` pressing into an
unsettled menu, no relay join) were fixed in 0.3.3 on `D:\PP-Instance3` (screenshot, dismiss and
menu readiness live; relay offline)._



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
- **2026-09-28 note (not re-verified):** `console` now runs once and pages the capture by opaque `cursor` (`3be9b6f`, `5d9fb6c`), which is the paged reply asked for. Live check on `D:\PP-Instance3` found no command with >200 output lines on that install (its ContentTool `ct_list audio`/`bundles` print 62), so the whole-listing read-back is still unproven - rerun the original `ct_list audio` on an install whose ContentTool prints the full listing, then delete this entry.

<!-- Append new entries above this line. Keep them evidence-backed. -->