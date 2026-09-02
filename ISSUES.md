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
`D:\PP-Instance2` the same day._

## 2026-09-02 — `call` `op:"new"` refuses a struct (no explicit constructor) with "has no accessible constructor"

- **Attempted:** `.\ppcli.ps1 connect call '{"op":"new","type":"Base.UI.MessageBox.MessageBoxCallbackResult","args":[]}'`
  to build the argument for `MessageBox.OnPromptResult` (answering a prompt from the terminal).
- **Happened:** `{"ok":false,"code":"member","error":"Base.UI.MessageBox.MessageBoxCallbackResult has no accessible constructor"}`.
  `MessageBoxCallbackResult` is a `public struct` with only the implicit parameterless constructor, which
  reflection does not list as a `ConstructorInfo`.
- **Expected:** `new` with no args on a value type returns the default instance (boxed handle) — the same thing
  `Activator.CreateInstance(Type)` does.
- **Evidence:** the refusal above; the workaround
  `{"op":"invoke","type":"System.Activator","assembly":"mscorlib","member":"CreateInstance","args":[{"$type":"Base.UI.MessageBox.MessageBoxCallbackResult"}]}`
  returned `h:1:24`, and a subsequent `set` of `DialogResult` + `get` read back `{"$enum":"No"}`.
- **Severity:** low — one-line workaround, but the message points at a missing constructor instead of at
  the value-type case.
- **Status 2026-09-02:** fixed in `src\Reflect.cs` `New()` (a value type called with no args now
  returns `Activator.CreateInstance(type)`), builds clean, but NOT verified in-game yet —
  `D:\PP-Instance2` was busy with another agent's batch, so the new DLL could not be deployed.
  Delete this entry once one live `call op:"new"` on a struct answers `ok:true`.

<!-- Append new entries above this line. Keep them evidence-backed. -->
