# ISSUES — PPCLI defects to fix

**If you are the agent working on PPCLI: this is your inbox. Read it at session start.**

Running log of PPCLI defects, gaps and rough edges hit by other agents while doing real work with the
tool. Every entry is written from an actual observed run, never from reading the source. If something
was suspected but not proven, it says so.

Fix an entry, then delete it from this file (the git history keeps it). Leave anything you could not
reproduce in place, with a note on what you tried.

Format per entry: what was attempted → what happened → what was expected → evidence → severity.

---

## 1. No way to capture what is on screen

- **Attempted:** confirm visually that a ContentTool-replaced model renders in game.
- **Happened:** PPCLI has no screenshot/framebuffer verb. The fallback (desktop computer-use tooling)
  does not cover Phoenix Point either — it is a Steam title and is not in the app allowlist, so no
  screenshot channel exists at all.
- **Expected:** some way to prove "this is what the player sees", even a crude one.
- **Workaround used:** enumerate live objects and read material/texture fields back through
  `connect call` reflection. Proves binding, not appearance.
- **Evidence:** verification run 2026-09-01, ContentTool render check.
- **Severity:** medium — blocks any visual acceptance test; everything visual has to be argued
  indirectly.
- **Suggested:** a `screenshot` verb that writes a PNG next to the JSON result would close a whole
  class of verification.

## 2. Deploying to an install whose game is already running silently verifies nothing

- **Attempted:** deploy ContentTool 1.1.2 into the user's Steam install and verify the fix there.
- **Happened:** the DLL and `meta.json` on disk became 1.1.2, but the live process had 1.1.1 loaded
  in memory and cannot hot-swap. The run would have measured the OLD build. Caught only because the
  agent explicitly asked the live process for its version (`ct_version`) instead of trusting the
  deploy.
- **Expected:** `deploy` (or the next `connect`) to say plainly "this install has a running process
  holding an older build; what you measure will be stale".
- **Evidence:** verification run 2026-09-01; work was moved to `D:\PP-Instance2` to get a truthful
  result.
- **Severity:** high — this is exactly the class of mistake the existing build-stamp `stale:true`
  guard exists to prevent, but it did not fire for a mod OTHER than PPBridge itself. The guard
  appears to cover the bridge's own DLL only.
- **Suggested:** extend the stamp check to any mod DLL `deploy` writes, or have `connect state`
  report the on-disk vs in-memory version of every loaded mod.

---

## 3. `connect items` silently returns an empty sweep when pageSize exceeds its cap

- **Attempted:** enumerate loaded assets with `connect items` at `pageSize: 400`.
- **Happened:** returned `code:"args"` and an EMPTY result set. Read at a glance this is
  indistinguishable from "the asset is not loaded" — which sent the investigation down a wrong path.
- **Expected:** either honour the larger page size, or fail loudly enough that the caller cannot read
  the empty list as a negative result. The cap is 200.
- **Evidence:** render-verification run 2026-09-01.
- **Severity:** medium — a silent empty result on a bad argument is a correctness trap for anything
  that sweeps for an asset and concludes "absent".
- **Suggested:** clamp to the cap and say so in the result, or return only the error with no `items`
  key at all, so an empty sweep cannot be mistaken for a finding.

<!-- Append new entries above this line. Keep them evidence-backed. -->
