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

## 2026-09-02 — handles are evicted under pressure and a large `connect multi` silently loses rows

- **Attempted:** a rig census on `D:\PP-Instance2` (build `8939f00f`, phase `menu`). 37 array handles were
  obtained with `call {"op":"invoke","member":"GetComponentsInChildren","typeArgs":["UnityEngine.Transform"],"args":[true]}`,
  reporting `count` 5..216 each, **2551 transforms in total**. Then ONE
  `.\ppcli.ps1 connect multi '@req-pages.json'` with 38 `items {"h":"<ARRAY>","page":P,"pageSize":200}` rows.
- **Happened:** only **528** of the 2551 rows came back. The run reported `ok` per request as it went, and a
  repeat of the same file answered `{"ok":false,"error":"handle 'h:17:1811' expired or was released"}` on
  request `i0` onwards, ~3 minutes after those handles were minted — far inside the documented 900 s TTL, and
  nothing was released explicitly. Minting ~200 fresh `items` handles per request evidently evicted the array
  handles the later requests still needed.
- **Expected:** either every row returns its page, or the rows that cannot be served fail loudly with a
  distinguishable reason. Two problems: (1) exhaustion is reported as `expired or was released`, which reads
  like a TTL/user-release problem and sends you looking in the wrong place; (2) a `multi` that loses most of
  its payload still looks like a successful sweep unless the caller counts rows itself.
- **Evidence:** `STEP2 sum transforms: 2551` vs `STEP3 total transforms collected: 528` in the same script run;
  the re-run's per-request errors quoted above. The identical `items` call issued on its own returned
  `returned=124 hasMore=False count=124` for `CHR_Human_Rig_Ready`, so paging itself is fine.
- **Workaround used:** process one rig at a time (get array → page it → read `childCount` per transform → next
  rig), keeping peak live handles in the low hundreds. 37 rigs / 2551 transforms then completed with zero loss.
- **Severity:** medium — silent data loss in a batched read. A cap or eviction is reasonable; reporting it as
  `expired` and letting the batch report success is not. A distinct `code`/message for "handle table full,
  evicted" (and ideally the cap in the reply) would have made this five minutes instead of an hour.
- **Status 2026-09-02:** eviction is now named as such — `Reflect` counts evictions and the highest
  evicted id, so a missing handle at or below that id refuses with "was EVICTED under pressure, not
  expired", naming the 512-lease cap and how many leases have been dropped this session; a handle
  that really did age out still says "expired or was released". Builds clean, deployed to
  `D:\PP-Instance2` as build `533d3b1e`, NOT verified live yet (Instance2 was taken again before the
  pressure run). Delete this entry once a run mints >512 handles and an older handle refuses with the
  EVICTED wording. Note `connect multi` already reports `failed` and exits 1 on any `ok:false` row —
  a batch that loses rows is loud IF the caller reads `failed`/the exit code instead of summing its
  own rows.

<!-- Append new entries above this line. Keep them evidence-backed. -->
