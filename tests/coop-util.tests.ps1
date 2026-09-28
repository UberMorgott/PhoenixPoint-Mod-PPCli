<#
  Offline check for coop-util.ps1: action-scoped log waits (a mark before the action, a partial last
  line, a log that appears mid-wait, a refusal quoted with its reasons) and the pid file `stop`
  trusts (pid + start time + exe path, legacy pid-only entries never verify). No game.

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

# ---------------------------------------------------------------- the pid file
$parsed = PidEntryParse @('4242,4343', "77`t638000000000000000`tD:\PP-Instance2\PhoenixPointWin64.exe", '')
Assert-Value 'a legacy comma line parses as legacy entries' (($parsed | Where-Object legacy | ForEach-Object pid) -join ',') '4242,4343'
Assert-Value 'a full entry keeps its path' ($parsed | Where-Object { -not $_.legacy }).path 'D:\PP-Instance2\PhoenixPointWin64.exe'
Assert-Value 'a legacy entry round-trips as a bare pid' (PidEntryLine $parsed[0]) '4242'
$child = Start-Process pwsh -ArgumentList '-NoProfile', '-Command', 'Start-Sleep 60' -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 300
$e = PidEntryOf $child.Id
$back = (PidEntryParse @(PidEntryLine $e))[0]
Assert-Value 'the recorded entry verifies against its own process' ([bool](PidEntryProcess $back 'pwsh')) 'True'
Assert-Value 'the same pid with another start time is NOT ours' ([bool](PidEntryProcess ([pscustomobject]@{ pid = $e.pid; start = $e.start - 1; path = $e.path; legacy = $false }) 'pwsh')) 'False'
Assert-Value 'the same pid with another exe is NOT ours' ([bool](PidEntryProcess ([pscustomobject]@{ pid = $e.pid; start = $e.start; path = 'D:\Steam\steamapps\common\Phoenix Point\PhoenixPointWin64.exe'; legacy = $false }) 'pwsh')) 'False'
Assert-Value 'a legacy pid-only entry never verifies, even for a live pid' ([bool](PidEntryProcess ([pscustomobject]@{ pid = $e.pid; start = 0; path = $null; legacy = $true }) '')) 'False'
Assert-Value 'the game-name filter rejects a pwsh' ([bool](PidEntryProcess $back)) 'False'

# ---------------------------------------------------------------- join addresses (ISSUES 2026-09-26 relay)
Assert-Value 'no address given = loopback at -Port for every client' ((JoinAddresses 2 @() $false 14242 '') -join ',') '127.0.0.1:14242,127.0.0.1:14242'
Assert-Value 'one -JoinAddress = every client' ((JoinAddresses 2 @('10.0.0.5:34242') $false 14242 '') -join ',') '10.0.0.5:34242,10.0.0.5:34242'
Assert-Value 'one -JoinAddress per client, in order' ((JoinAddresses 2 @('127.0.0.1:34242', '127.0.0.1:34243') $false 14242 '') -join ',') '127.0.0.1:34242,127.0.0.1:34243'
Assert-Value 'a count that fits neither form is refused' (Outcome { JoinAddresses 3 @('a:1', 'b:2') $false 14242 '' }) 'THROW -JoinAddress has 2 addresses for 3 clients - pass one (every client) or one per client, client1 first'
Assert-Value 'a non host:port address is refused' (Outcome { JoinAddresses 1 @('34242') $false 14242 '' }) "THROW -JoinAddress '34242' is not host:port"
Assert-Value 'zero clients = no addresses' ((JoinAddresses 0 @() $false 14242 '').Count) '0'
$relayFile = Join-Path $scratch 'relay.pids'
@([pscustomobject]@{ role = 'host'; pid = 1; port = 24242 }, [pscustomobject]@{ role = 'client1'; pid = 11; port = 34242 },
  [pscustomobject]@{ role = 'client2'; pid = 12; port = 34243 }) | ConvertTo-Json -AsArray | Set-Content $relayFile
$up = { param($e) $true }
Assert-Value '-Relay maps clientN to its own tunnel port' ((JoinAddresses 2 @() $true 14242 $relayFile $up) -join ',') '127.0.0.1:34242,127.0.0.1:34243'
Assert-Value '-Relay with -JoinAddress is refused' (Outcome { JoinAddresses 2 @('a:1') $true 14242 $relayFile $up }) 'THROW -Relay and -JoinAddress are exclusive: the relay names every client address itself'
Assert-Value '-Relay short of a client tunnel is refused' (Outcome { JoinAddresses 3 @() $true 14242 $relayFile $up }) "THROW -Relay: no client3 tunnel in $relayFile (relay started with fewer -Clients than 3)"
Assert-Value '-Relay with a dead tunnel is refused' (Outcome { JoinAddresses 2 @() $true 14242 $relayFile { param($e) $e.role -ne 'client2' } }) 'THROW -Relay: client2 tunnel (ssh pid 12, port 34243) is not running - restart the relay'
Assert-Value '-Relay without a relay is refused' (Outcome { JoinAddresses 1 @() $true 14242 (Join-Path $scratch 'none.pids') $up }) "THROW -Relay: no relay pid file at $(Join-Path $scratch 'none.pids') - start it first: vps-relay.ps1 -Action start -Clients 1"
@([pscustomobject]@{ role = 'client1'; pid = 11; port = 34242 }, [pscustomobject]@{ role = 'client2'; pid = 12; port = 34242 }) | ConvertTo-Json -AsArray | Set-Content $relayFile
Assert-Value '-Relay refuses two clients on one port' (Outcome { JoinAddresses 2 @() $true 14242 $relayFile $up }) 'THROW -Relay: client2 shares port 34242 with another client - a broken pid file'
Assert-Value 'the default tunnel check rejects a pid that is not ssh' ([bool](& $script:RelayTunnelAlive ([pscustomobject]@{ pid = $PID; port = 1 }))) 'False'

# ---------------------------------------------------------------- menu readiness (ISSUES 2026-09-26 reconnect race)
$menu = [pscustomobject]@{ phase = 'menu'; levelState = 'Playing'; mpUi = $true; menuButton = $true; top = 'PhoenixPoint.Home.View.ViewStates.UIStateHomeScreenCutscene' }
Assert-Value 'a settled menu with the mod button is ready (top state not required)' ([string](MenuNotReady $menu)) ''
Assert-Value 'a still-loading HomeScreen is not ready' (MenuNotReady ([pscustomobject]@{ phase = 'menu'; levelState = 'Loading'; mpUi = $true; menuButton = $true })) "HomeScreen level 'Loading', not Playing"
Assert-Value 'no captured menu button is not ready' (MenuNotReady ([pscustomobject]@{ phase = 'menu'; levelState = 'Playing'; mpUi = $true; menuButton = $false })) 'mod menu button not captured yet (NativeWidgetFactory.HasMenuButton false)'
Assert-Value 'no MultiplayerUI is not ready' (MenuNotReady ([pscustomobject]@{ phase = 'menu'; levelState = 'Playing'; mpUi = $false; menuButton = $true })) 'MultiplayerUI.Instance not up'
Assert-Value 'an older build falls back to UIStateMainMenu' (MenuNotReady ([pscustomobject]@{ phase = 'menu'; levelState = 'Playing'; mpUi = $true; menuButton = $null; top = 'X.UIStateHomeScreenCutscene' })) "top home state 'X.UIStateHomeScreenCutscene', not UIStateMainMenu"
Assert-Value 'an older build on the main menu is ready' ([string](MenuNotReady ([pscustomobject]@{ phase = 'menu'; levelState = 'Playing'; mpUi = $true; menuButton = $null; top = 'PhoenixPoint.Home.View.ViewStates.UIStateMainMenu' }))) ''
Assert-Value 'the geoscape is not the menu' (MenuNotReady ([pscustomobject]@{ phase = 'geoscape'; levelState = 'Playing'; mpUi = $true; menuButton = $true })) "phase 'geoscape', not menu"
# Sequence probe: not ready, ready, not ready (flicker), ready x3 -> returns on the 6th poll.
$script:seq = @($false, $true, $false, $true, $true, $true, $true); $script:polls = 0
$probe = { $ok = $script:seq[$script:polls]; $script:polls++; if ($ok) { $menu } else { [pscustomobject]@{ phase = 'loading' } } }
$null = WaitMenuReadyCore $probe 10 3 1
Assert-Value 'readiness needs 3 CONSECUTIVE ready polls (a flicker resets)' $script:polls '6'
$script:polls = 0
Assert-Value 'never settling throws the last reason' (Outcome { WaitMenuReadyCore { [pscustomobject]@{ phase = 'loading' } } 0 3 1 }) "THROW menu not ready after 0s: phase 'loading', not menu"
Assert-Value 'a throwing probe counts as not ready' (Outcome { WaitMenuReadyCore { throw 'pipe down' } 0 3 1 }) 'THROW menu not ready after 0s: no state answer'

# ---------------------------------------------------------------- dismiss (ISSUES 2026-09-26 cutscene)
Assert-Value 'a geoscape cutscene is skipped' (DismissKind 'PhoenixPoint.Geoscape.View.ViewStates.UIStateGeoCutscene') 'cutscene'
Assert-Value 'a modal gets its OK' (DismissKind 'PhoenixPoint.Geoscape.View.ViewStates.UIStateGeoModal') 'modal'
Assert-Value 'an event gets FinishEncounter' (DismissKind 'PhoenixPoint.Geoscape.View.ViewStates.UIStateGeoscapeEvent') 'event'
Assert-Value 'Replenish is left alone' ([string](DismissKind 'PhoenixPoint.Geoscape.View.ViewStates.UIStateReplenish')) ''
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
