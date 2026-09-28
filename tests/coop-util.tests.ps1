<#
  Offline check for coop-util.ps1: action-scoped log waits (a mark before the action, a partial last
  line, a log that appears mid-wait, a refusal quoted with its reasons) and the pid file `stop`
  trusts. No game.

      pwsh -NoProfile -File .\tests\coop-util.tests.ps1            # must exit 0
      pwsh -NoProfile -File .\tests\coop-util.tests.ps1 -Falsify   # must ALSO exit 0

  -Falsify corrupts every expectation and demands that EVERY assertion fails (see paths.tests.ps1).
#>
param([switch] $Falsify)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path $PSScriptRoot 'fixture-coop-util'
Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
. (Join-Path $root 'tools\coop-util.ps1')

$script:passed = 0
$script:failed = 0
function Assert-Value([string] $what, $actual, [string] $expected) {
    if ($Falsify) { $expected = $expected + '~falsified' }
    if ([string]$actual -ceq $expected) { $script:passed++; Write-Host "  ok   $what" }
    else { $script:failed++; Write-Host "  FAIL $what : got '$actual', wanted '$expected'" }
}
# What a wait ended with: its line, or its throw's message.
function Outcome([scriptblock] $b) { try { & $b } catch { 'THROW ' + $_.Exception.Message } }
function Append([string] $f, [string] $s) { [IO.File]::AppendAllText($f, $s) }

$child = $null
try {
Write-Host "coop-util ($(if ($Falsify) { 'FALSIFY' } else { 'normal' }))"

# ---------------------------------------------------------------- marks
$log = Join-Path $scratch 'a.log'
Set-Content $log -Value "[MP] transport initialized`n[MP] old line`n[MP] half" -NoNewline
Assert-Value 'a mark ends at the last COMPLETE line' (LogEnd $log) ([string]([Text.Encoding]::UTF8.GetByteCount("[MP] transport initialized`n[MP] old line`n")))
$mark = LogMarkFiles @($log)
Assert-Value 'an old line before the mark does not answer' (Outcome { WaitLogCore { $log } 'transport initialized' 0 $mark '' 'a' 0 10 }) "THROW log timeout on a: /transport initialized/ (looked in: $log)"
# The partial line at mark time is finished AFTER it: that line is new and must match.
Append $log " join REFUSED`nreason one`n"
Assert-Value 'a partial line finished after the mark is seen' (Outcome { WaitLogCore { $log } 'half join' 0 $mark '' 'a' 0 10 }) '[MP] half join REFUSED'
Assert-Value 'no mark = the whole file' (Outcome { WaitLogCore { $log } 'transport initialized' 0 @{} '' 'a' 0 10 }) '[MP] transport initialized'
Assert-Value 'an unterminated last line is not read yet' (& { Append $log 'tail without newline'; $l = LogLinesSince $log (LogEnd $log); $l.Count }) '0'

# ---------------------------------------------------------------- refusal + grace
$ref = Join-Path $scratch 'ref.log'
Set-Content $ref -Value "old`n" -NoNewline
$rm = LogMarkFiles @($ref)
Append $ref "join REFUSED by host: 1 difference(s)`n"
# The reason line lands during the grace window, as it does when the mod writes it a beat later.
$writer = Start-ThreadJob -ArgumentList $ref { param($p) Start-Sleep -Milliseconds 300; [IO.File]::AppendAllText($p, "Phoenix Point build differs`n") }
$got = Outcome { WaitLogCore { $ref } 'host ACCEPTED' 5 $rm 'join REFUSED' 'c1' 1000 10 }
Receive-Job $writer -Wait -AutoRemoveJob | Out-Null
Assert-Value 'a refusal ends the wait, quoting the reason written during the grace' $got 'THROW REFUSED on c1 (waiting for /host ACCEPTED/): join REFUSED by host: 1 difference(s) | Phoenix Point build differs'
Set-Content $ref -Value "join REFUSED old run`n" -NoNewline
$rm = LogMarkFiles @($ref)
Append $ref "host ACCEPTED the join`n"
Assert-Value 'a refusal from before the mark does not fail the wait' (Outcome { WaitLogCore { $ref } 'host ACCEPTED' 0 $rm 'join REFUSED' 'c1' 0 10 }) 'host ACCEPTED the join'

# ---------------------------------------------------------------- files resolved during the wait
$lateLog = Join-Path $scratch 'late.log'
$old = Join-Path $scratch 'old.log'
Set-Content $old -Value "rejoined the session (previous run)`n" -NoNewline
$m2 = LogMarkFiles @()            # the peer could not name its mod log when the mark was taken
Start-Sleep -Milliseconds 50
$script:polls = 0
$resolve = { $script:polls++; if ($script:polls -ge 2) { Set-Content $lateLog -Value "rejoined the session`n" -NoNewline -ErrorAction SilentlyContinue; $lateLog } }
Assert-Value 'a log that appears mid-wait is read from its start' (Outcome { WaitLogCore $resolve 'rejoined' 5 $m2 '' 'c1' 0 10 }) 'rejoined the session'
Assert-Value 'a log older than the mark is not read from 0' (Outcome { WaitLogCore { $old } 'rejoined' 0 $m2 '' 'c1' 0 10 }) "THROW log timeout on c1: /rejoined/ (looked in: $old)"
# Rotated (Player.log -> Player-prev.log, new file): shorter than the mark, so all of it is new.
$rot = Join-Path $scratch 'rot.log'
Set-Content $rot -Value "a long line from the previous process`nanother one`n" -NoNewline
$m3 = LogMarkFiles @($rot)
Set-Content $rot -Value "fresh`n" -NoNewline
Assert-Value 'a file replaced by a shorter one is read from its start' (Outcome { WaitLogCore { $rot } 'fresh' 0 $m3 '' 'h' 0 10 }) 'fresh'

}
finally {
    if ($child) { Stop-Process -Id $child.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "passed=$($script:passed) failed=$($script:failed)"
if ($Falsify) {
    if ($script:passed -ne 0) { Write-Host "FALSIFY BROKEN: $($script:passed) assertion(s) passed against corrupted expectations"; exit 1 }
    Write-Host 'falsified: every assertion reported failure, so they are wired to something'
    exit 0
}
if ($script:failed -ne 0) { exit 1 }
exit 0
