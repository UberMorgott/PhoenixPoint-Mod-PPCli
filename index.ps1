<#
  The paging half of `ppcli.ps1 index`: every def in the repository, or a refusal. A file of its own
  only so tests\index.tests.ps1 can reach it - ppcli.ps1 takes a Mandatory parameter and cannot be
  dot-sourced (same reason as waits.ps1).

  The refusals here are the whole point of the file. `find` re-enumerates and re-sorts the repository
  on EVERY page, so a def loading between two pages shifts every row after it - some are skipped,
  some come twice, and the catalog that results has holes it can never report. Each of the three
  ways that shows up is refused, and nothing is written.
#>

# Pages `find {all:true}` until it says it is done. Returns @{ defs; pages }, or throws.
# $pageSize 200 is `find`'s own page ceiling and projects to roughly 30 KB - well inside the 64 KB
# reflection cap and the 256 KB frame limit, both of which refuse rather than truncate.
function Get-AllDefs($ep, [int] $pageSize = 200) {
    $defs  = New-Object Collections.Generic.List[object]
    $seen  = New-Object Collections.Generic.HashSet[string]
    $page  = 0
    $total = $null
    while ($true) {
        $reply = Invoke-Verb 'find' ([ordered]@{ all = $true; page = $page; pageSize = $pageSize }) $ep
        if ($reply.status -ne 'done' -or -not $reply.result.ok) {
            throw "find all failed on page ${page}: " + ($reply | ConvertTo-Json -Depth 8 -Compress)
        }
        $r = $reply.result
        # SNAPSHOT INTEGRITY. A moving `total` is the repository shifting under us, and a repeated
        # (name,guid,type) is that having already happened.
        if ($null -eq $total) { $total = $r.total }
        elseif ($r.total -ne $total) {
            throw ("REFUSED: the def repository changed under the index - page $page reports total " +
                   "$($r.total), page 0 reported $total. Nothing was written; re-run on a settled game.")
        }
        foreach ($d in $r.defs) {
            if (-not $seen.Add("$($d.name)`0$($d.guid)`0$($d.type)")) {
                throw ("REFUSED: page $page repeats def '$($d.name)' ($($d.guid)) - the repository moved " +
                       'between pages. Nothing was written; re-run on a settled game.')
            }
            $defs.Add($d)
        }
        Note "page ${page}: $($r.count) of $total"
        if (-not $r.hasMore) { break }
        $page++
        # A server that always says hasMore is a bug, and `while ($true)` would page forever against
        # it. 500 pages is 100000 defs - an order of magnitude past the real repository.
        if ($page -ge 500) { throw "REFUSED: the index passed 500 pages and 'hasMore' is still true. Nothing was written." }
    }
    # The two independent counts must agree. They can disagree while every page above looked fine:
    # a def REMOVED between pages shortens the run without ever repeating a row.
    if ($defs.Count -ne $total) {
        throw "REFUSED: collected $($defs.Count) defs but the repository reports $total. Nothing was written."
    }
    return @{ defs = $defs; pages = $page + 1 }
}
