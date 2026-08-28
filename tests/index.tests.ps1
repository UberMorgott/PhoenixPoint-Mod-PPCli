<#
  Offline check for the snapshot-integrity refusals in `ppcli.ps1 index`. They exist because the def
  repository can move between pages, which no live run has ever been able to reproduce on purpose -
  so every one of them shipped never having fired once. Here `Invoke-Verb` is replaced by a function
  that hands back scripted pages, and the REAL Get-AllDefs (index.ps1) is asked to refuse them.

      pwsh -NoProfile -File .\tests\index.tests.ps1            # must exit 0
      pwsh -NoProfile -File .\tests\index.tests.ps1 -Falsify   # must ALSO exit 0

  -Falsify corrupts every expectation and demands that EVERY assertion fails. A refusal test that
  passes because nothing threw is the same bug as no test at all.
#>
param([switch] $Falsify)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Note([string] $m) { }          # index.ps1 reports progress on stderr; not under test
. (Join-Path $root 'index.ps1')

$script:passed = 0
$script:failed = 0

# The scripted pages the fake `find` will hand out, one per call.
$script:pages = @()
$script:at = 0
function Invoke-Verb([string] $verb, $verbArgs, $ep) {
    if ($verb -ne 'find') { throw "the fake endpoint only serves 'find', not '$verb'" }
    $p = $script:pages[$script:at]
    $script:at++
    return $p
}

# A page as `find` shapes it. `d` is a list of def names; guid and type are derived so a repeat of a
# name is a repeat of the whole (name,guid,type) key.
function Page([int] $total, [switch] $hasMore, [string[]] $d) {
    $defs = @($d | ForEach-Object { [pscustomobject]@{ name = $_; guid = "g-$_"; type = 't' } })
    [pscustomobject]@{ status = 'done'; result = [pscustomobject]@{
        ok = $true; total = $total; count = $defs.Count; hasMore = [bool]$hasMore; defs = $defs } }
}

# The refusal TEXT is asserted, not just "it threw": these all say what to do next (re-run on a
# settled game) and a refusal that only says "REFUSED" sends the reader into the source.
function Assert-Refusal([string] $what, [string] $mustSay, [object[]] $scripted) {
    if ($Falsify) { $mustSay = $mustSay + '~falsified' }
    $script:pages = $scripted
    $script:at = 0
    try {
        Get-AllDefs $null 2 | Out-Null
        $script:failed++; Write-Host "  FAIL $what : nothing was refused"
    } catch {
        if ($_.Exception.Message -like "*$mustSay*") { $script:passed++; Write-Host "  ok   $what" }
        else { $script:failed++; Write-Host "  FAIL $what : refused with '$($_.Exception.Message)'" }
    }
}

Assert-Refusal 'a moving total is refused' 'page 1 reports total 41, page 0 reported 42' @(
    (Page 42 -hasMore -d 'a', 'b'), (Page 41 -d 'c'))

Assert-Refusal 'a repeated def is refused' "page 1 repeats def 'b'" @(
    (Page 4 -hasMore -d 'a', 'b'), (Page 4 -d 'b', 'c'))

Assert-Refusal 'a short collection is refused' 'collected 3 defs but the repository reports 4' @(
    (Page 4 -hasMore -d 'a', 'b'), (Page 4 -d 'c'))

Assert-Refusal 'a page that failed is refused' 'find all failed on page 1' @(
    (Page 4 -hasMore -d 'a', 'b'),
    ([pscustomobject]@{ status = 'done'; result = [pscustomobject]@{ ok = $false; error = 'nope' } }))

# An endless `hasMore` must stop at 500 pages rather than page forever. 501 identical-total pages,
# each with one NEW def, so nothing else can refuse first.
$endless = @(0..500 | ForEach-Object { Page 100000 -hasMore -d "d$_" })
Assert-Refusal 'an endless hasMore is refused' 'passed 500 pages' $endless

# And the honest run must be ACCEPTED, or every refusal above is just a function that always throws.
$script:pages = @((Page 3 -hasMore -d 'a', 'b'), (Page 3 -d 'c'))
$script:at = 0
$got = Get-AllDefs $null 2
$want = if ($Falsify) { '9/9' } else { '3/2' }
if ("$($got.defs.Count)/$($got.pages)" -eq $want) { $script:passed++; Write-Host '  ok   a settled repository is accepted' }
else { $script:failed++; Write-Host "  FAIL a settled repository is accepted : got $($got.defs.Count) defs over $($got.pages) pages" }

if ($script:failed -gt 0 -and -not $Falsify) { Write-Host "index.tests: $script:failed FAILURE(S)"; exit 1 }
if ($Falsify) {
    if ($script:passed -gt 0) { Write-Host "index.tests -Falsify: $script:passed assertion(s) still PASSED on corrupted expectations"; exit 1 }
    Write-Host "index.tests -Falsify: PASS (all $script:failed assertions failed as they must)"
    exit 0
}
Write-Host "index.tests: PASS ($script:passed assertions)"
exit 0
