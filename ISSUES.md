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

<!-- Append new entries above this line. Keep them evidence-backed. -->
