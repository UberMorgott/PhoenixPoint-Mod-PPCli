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

## 2026-09-05 — `call {"op":"new"}` ignores `sig`, so an ambiguous constructor cannot be selected

- **Attempted:** `{"op":"new","type":"System.Collections.ArrayList","args":[],"sig":[]}` inside a plan
  step, to build a zero-element accumulator.
- **Happened:** `step 'acc' (call) failed: score 0 is a tie across 2 overloads - pass "sig" with the
  parameter type names` — the error asks for `sig`, but `sig` was already present and empty, and
  adding it changed nothing.
- **Expected:** `"sig": []` selects the parameterless constructor, the same way `sig` disambiguates
  `invoke`.
- **Evidence:** live `D:\PP-Instance3`, build `3068ae67`. Failed identically with and without `sig`.
  Worked around by dropping the accumulator and generating explicit indexed steps.
- **Severity:** low. Only bites types with an `()`/`(int)`/`(ICollection)` constructor family.

## 2026-09-06 — a handle cannot be passed as an ARGUMENT from the shell, and the envelope that works is undocumented

- **Attempted:** `connect call '{"op":"invoke","type":"Morgott.ContentTool.Dev.FitBench","member":"ShowPrototype","args":["h:4:17","h:4:28"]}'`
  — pass two handles that a previous `call`/`items` reply returned as arguments to a static method.
- **Happened:** `{"ok":false,"code":"overload","error":"nothing binds for 'ShowPrototype': static String
  ShowPrototype(PrototypeRecord record, PrototypeVariant variant) arg 0 (PrototypeRecord): a string cannot
  bind to PrototypeRecord"}`, exit 1. A bare `h:N:M` string is never resolved back to its handle.
- **Expected:** either a bare `h:N:M` argument resolves to the live object, or `PLAYBOOK.md` names the
  envelope that does. `PLAYBOOK.md:343` documents only `{"$type":"..."}` for passing a TYPE, and the
  refusal message does not name any alternative.
- **Evidence:** live `D:\PP-Instance2`, build `3068ae67`, PID 12424, 2026-09-06. Found by trial that
  `{"$h":"h:4:17"}` DOES work — `{"op":"invoke",...,"args":[{"$h":"h:4:17"},{"$h":"h:4:28"}]}` returned
  `{"ok":true,"value":null}` and the prototype was really shown. Same envelope worked for an instance
  method: `{"op":"invoke","target":"h:4:49","member":"PickTarget","args":[{"$h":"h:4:34"}]}` → `ok:true`.
- **Severity:** low (documentation). The capability exists and works; it is only unfindable — the
  overload refusal costs a round trip and reads like "this cannot be done from the shell".

## 2026-09-06 — stderr chatter is on the INFORMATION stream, so `2>$null` does not silence it

- **Attempted:** silence the per-call `pipe ppcli-<id> (pid N, build=…, ppcli/1)` banner in a polling
  loop with `& .\ppcli.ps1 … connect call $json 2>$null`, then with `2>&1` plus a filter.
- **Happened:** the banner still reached the transcript on every one of ~200 poll iterations; `2>$null`
  and `2>&1`+filter both left it visible.
- **Expected:** `2>$null` silences per-call diagnostics, per the output contract's "everything else on
  stderr" (`PLAYBOOK.md:389`).
- **Evidence:** live `D:\PP-Instance2`, build `3068ae67`, 2026-09-06, pwsh 7. `6>$null` (the Information
  stream, i.e. `Write-Host`) DID silence it, which is what identifies where it is written.
- **Severity:** low, but it is a real cost for an agent: a bounded poll loop floods the transcript, and
  the documented redirection is the wrong one.

## 2026-09-06 — `6>$null` does NOT silence the per-call banner either (correction to the entry above)

- **Attempted:** the workaround the previous entry recommends — `6>$null` on the `& $ppcli … connect …`
  call, and then on the whole enclosing `& { … } 6>$null` script block.
- **Happened:** the `pipe ppcli-<id> (pid N, build=…, ppcli/1)` banner still reached the transcript on
  every call, in both placements. One bounded poll loop printed it ~250 times.
- **Expected:** either `2>$null` or `6>$null` silences it, per the output contract (`PLAYBOOK.md:389`).
- **Evidence:** live `D:\PP-Instance2`, build `3068ae67`, PID 27480, 2026-09-06, pwsh 7 — the same
  install and build as the entry above. Reproduced on every call of a ~40-call session. Worked around
  by polling every 3 s instead of every 0.7 s, i.e. by making fewer calls.
- **Severity:** low, but the documented workaround is wrong: nothing a caller can redirect suppresses
  it, which suggests the banner is written straight to the console host rather than to a stream.

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

<!-- Append new entries above this line. Keep them evidence-backed. -->
