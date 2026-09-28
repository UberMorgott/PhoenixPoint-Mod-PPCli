<#
coop-util.ps1 - the pure halves of coop.ps1 that decide correctness on their own: action-scoped log
waits and the pid file `stop` trusts. Dot-sourced by coop.ps1 and by tests\coop-util.tests.ps1;
defines functions only.
#>

# ---------------------------------------------------------------- log marks + waits

# Byte offset just past the last COMPLETE line of $f (0 when it does not exist). A trailing partial
# line is NOT counted: the rest of it is written later, and the finished line must still count as new.
function LogEnd([string]$f) {
    if (-not (Test-Path -LiteralPath $f)) { return [long]0 }
    $s = $null
    try { $s = [IO.FileStream]::new($f, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite, Delete') }
    catch { return [long]0 }
    try {
        # Backwards in 64 KiB chunks: logs run to tens of MB and a mark is taken before every action.
        $buf = New-Object byte[] 65536
        $pos = $s.Length
        while ($pos -gt 0) {
            $n = [int][math]::Min(65536, $pos)
            $pos -= $n
            $s.Position = $pos
            $got = 0
            while ($got -lt $n) { $r = $s.Read($buf, $got, $n - $got); if ($r -le 0) { break }; $got += $r }
            $i = [array]::LastIndexOf($buf, [byte]10, $got - 1, $got)
            if ($i -ge 0) { return [long]($pos + $i + 1) }
        }
        [long]0
    } finally { $s.Dispose() }
}

# Complete lines of $f from byte $from on. A file now SHORTER than $from was replaced (rotated or
# truncated), so all of it is new.
function LogLinesSince([string]$f, [long]$from) {
    if (-not (Test-Path -LiteralPath $f)) { return , @() }
    $s = $null
    try { $s = [IO.FileStream]::new($f, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite, Delete') }
    catch { return , @() }
    try {
        if ($from -gt $s.Length) { $from = 0 }
        $s.Position = $from
        $ms = [IO.MemoryStream]::new()
        $s.CopyTo($ms)
        $b = $ms.ToArray()
    } finally { $s.Dispose() }
    $end = [array]::LastIndexOf($b, [byte]10)
    if ($end -lt 0) { return , @() }
    , @([Text.Encoding]::UTF8.GetString($b, 0, $end) -split "`r?`n")
}

# The mark: per file, where its complete lines end NOW, plus the time it was taken ('@at'), which
# decides where a file the mark never saw starts (LateStart).
function LogMarkFiles([string[]]$files) {
    $m = @{ '@at' = (Get-Date).ToUniversalTime() }
    foreach ($f in $files) { if ($f) { $m[$f] = LogEnd $f } }
    $m
}

# A file the wait resolves that the mark did not list (the peer's mod log only became nameable
# mid-wait). Created after the mark: all of it is new. Older: where it ends at first sight - lines
# written between the mark and now are given up rather than risk an old run's line passing.
function LateStart([string]$f, [datetime]$at) {
    $i = Get-Item -LiteralPath $f -ErrorAction SilentlyContinue
    if ($i -and $i.CreationTimeUtc -gt $at) { return [long]0 }
    LogEnd $f
}

# Waits for /$rx/ in a line written after $mark in any file $resolve returns (re-run every poll, so a
# log that appears mid-wait is read). No mark = the whole files. $failRx: a line that means the
# awaited one will never come ends the wait at once - after $graceMs, so the refusal's reason lines
# (written right after it) are complete, and quoted with up to 3 of them.
# $resolve runs in THIS function's scope: its free variables must not share a name with a local here.
function WaitLogCore([scriptblock]$resolve, [string]$rx, [int]$sec, [hashtable]$mark = @{},
                     [string]$failRx = '', [string]$what = 'log', [int]$graceMs = 1000, [int]$pollMs = 2000) {
    $dl = (Get-Date).AddSeconds($sec)
    $lateFrom = @{}
    $files = @()
    while ($true) {
        $files = @(& $resolve | Where-Object { $_ })
        foreach ($f in $files) {
            $from = [long]0
            if ($mark.ContainsKey($f)) { $from = [long]$mark[$f] }
            elseif ($mark.ContainsKey('@at')) {
                if (-not $lateFrom.ContainsKey($f)) { $lateFrom[$f] = LateStart $f $mark['@at'] }
                $from = [long]$lateFrom[$f]
            }
            $lines = LogLinesSince $f $from
            if ($failRx) {
                $idx = -1
                for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match $failRx) { $idx = $i; break } }
                if ($idx -ge 0) {
                    Start-Sleep -Milliseconds $graceMs
                    $lines = LogLinesSince $f $from
                    $last = [math]::Min($lines.Count - 1, $idx + 3)
                    throw "REFUSED on ${what} (waiting for /$rx/): " + ($lines[$idx..$last] -join ' | ')
                }
            }
            $hit = @($lines | Where-Object { $_ -match $rx }) | Select-Object -Last 1
            if ($hit) { return $hit }
        }
        if ((Get-Date) -ge $dl) { break }
        Start-Sleep -Milliseconds $pollMs
    }
    throw "log timeout on ${what}: /$rx/ (looked in: $($files -join ', '))"
}

# ---------------------------------------------------------------- the pid file `stop` trusts

# ---------------------------------------------------------------- join addresses

# One vps-relay.ps1 tunnel entry is up: its ssh still runs AND owns the listening socket on its port (a
# live pid alone is not a tunnel) - the relay's own Test-Alive + Test-Listening.
$script:RelayTunnelAlive = {
    param($e)
    $p = Get-Process -Id $e.pid -ErrorAction SilentlyContinue
    if (-not ($p -and $p.ProcessName -eq 'ssh')) { return $false }
    [bool](Get-NetTCPConnection -LocalAddress 127.0.0.1 -LocalPort $e.port -State Listen -ErrorAction SilentlyContinue |
        Where-Object OwningProcess -EQ $e.pid)
}

# The address each client JOINs, client1 first. Default 127.0.0.1:<port> for every client. -JoinAddress:
# one value = every client, else exactly one per client. -Relay: Multiplayer2\tools\vps-relay.ps1's pid file
# ([{role,pid,port}], role client1..N = its own ssh -L on 127.0.0.1:<port>), and that ssh must be alive.
function JoinAddresses([int]$clients, [string[]]$joinAddress, [bool]$relay, [int]$port, [string]$relayFile,
                       [scriptblock]$alive = $script:RelayTunnelAlive) {
    if ($clients -le 0) { return , @() }
    $given = @($joinAddress | Where-Object { $_ })
    if ($relay -and $given.Count) { throw '-Relay and -JoinAddress are exclusive: the relay names every client address itself' }
    if ($relay) {
        if (-not $relayFile -or -not (Test-Path -LiteralPath $relayFile)) {
            throw "-Relay: no relay pid file at $relayFile - start it first: vps-relay.ps1 -Action start -Clients $clients"
        }
        $entries = @(Get-Content -LiteralPath $relayFile -Raw | ConvertFrom-Json)
        $out = @()
        for ($i = 1; $i -le $clients; $i++) {
            $e = @($entries | Where-Object { $_.role -eq "client$i" })[0]
            if (-not $e) { throw "-Relay: no client$i tunnel in $relayFile (relay started with fewer -Clients than $clients)" }
            if ("127.0.0.1:$($e.port)" -in $out) { throw "-Relay: client$i shares port $($e.port) with another client - a broken pid file" }
            if (-not (& $alive $e)) { throw "-Relay: client$i tunnel (ssh pid $($e.pid), port $($e.port)) is not running - restart the relay" }
            $out += "127.0.0.1:$($e.port)"
        }
        return , $out
    }
    if (-not $given.Count) { return , @(1..$clients | ForEach-Object { "127.0.0.1:$port" }) }
    foreach ($a in $given) { if ($a -notmatch '^\S+:\d{1,5}$') { throw "-JoinAddress '$a' is not host:port" } }
    if ($given.Count -eq 1) { return , @(1..$clients | ForEach-Object { $given[0] }) }
    if ($given.Count -ne $clients) { throw "-JoinAddress has $($given.Count) addresses for $clients clients - pass one (every client) or one per client, client1 first" }
    , $given
}

# ---------------------------------------------------------------- menu readiness (reconnect)

# Why a peer's main menu is NOT ready for a press yet, or $null when it is. $p = { phase, levelState,
# mpUi (MultiplayerUI.Instance present), menuButton (Multiplayer.UI.NativeWidgetFactory.HasMenuButton:
# true/false, $null = this mod build has no such member), top (HomeScreenView state-stack top type) }.
# Pressing RECONNECT 0.2 s after the mod's "UI initialized" joined into a HomeScreen still coming up and
# stuck the client on Loading 0% (ISSUES 2026-09-26). The mod's own auto-rejoin waits for exactly this:
# MultiplayerUI.Instance + HasMenuButton, then a 2 s settle (ReconnectFlow.TickAutoRejoin). A build
# without HasMenuButton falls back to the main-menu state itself (UIStateMainMenu on top).
function MenuNotReady($p) {
    if (-not $p) { return 'no state answer' }
    if ($p.phase -ne 'menu') { return "phase '$($p.phase)', not menu" }
    if ($p.levelState -ne 'Playing') { return "HomeScreen level '$($p.levelState)', not Playing" }
    if (-not $p.mpUi) { return 'MultiplayerUI.Instance not up' }
    if ($p.menuButton -eq $false) { return 'mod menu button not captured yet (NativeWidgetFactory.HasMenuButton false)' }
    if ($null -eq $p.menuButton -and $p.top -notlike '*.UIStateMainMenu') { return "top home state '$($p.top)', not UIStateMainMenu" }
    $null
}

# Polls $probe until MenuNotReady is $null on $streak CONSECUTIVE polls (3 x 1 s = the mod's own 2 s
# settle held), returns that probe; any not-ready poll resets the run; throws with the last reason.
function WaitMenuReadyCore([scriptblock]$probe, [int]$sec, [int]$streak = 3, [int]$pollMs = 1000) {
    $dl = (Get-Date).AddSeconds($sec)
    $run = 0
    while ($true) {
        $p = try { & $probe } catch { $null }
        $why = MenuNotReady $p
        if ($null -eq $why) { $run++; if ($run -ge $streak) { return $p } } else { $run = 0 }
        if ((Get-Date) -ge $dl) {
            throw "menu not ready after ${sec}s: $(if ($why) { $why } else { "ready on $run of $streak consecutive polls" })"
        }
        Start-Sleep -Milliseconds $pollMs
    }
}

# ---------------------------------------------------------------- dismiss

# What `dismiss` does with a geoscape view state: its OK (modal), FinishEncounter (event), the native
# skip OnCancel (cutscene - UIStateGeoCutscene.OnCancel = end callback + FinishQueriedState, exactly
# what Cancel/Submit does in OnInputEvent), or $null = stop there.
function DismissKind([string]$type) {
    if ($type -like '*.UIStateGeoModal') { return 'modal' }
    if ($type -like '*.UIStateGeoscapeEvent') { return 'event' }
    if ($type -like '*.UIStateGeoCutscene') { return 'cutscene' }
    $null
}

# One line per started peer: <pid> TAB <start time, UTC ticks> TAB <exe path>. A pid alone is NOT an
# identity - Windows recycles pids, so a stale number can name any process, the owner's own game
# included. Legacy files (bare pids, comma-joined) parse as legacy entries that nothing will kill.
function PidEntryParse([string[]]$lines) {
    $out = @()
    foreach ($l in $lines) {
        if (-not $l -or -not $l.Trim()) { continue }
        $p = $l -split "`t"
        if ($p.Count -eq 3 -and $p[0] -match '^\d+$' -and $p[1] -match '^\d+$') {
            $out += [pscustomobject]@{ pid = [int]$p[0]; start = [long]$p[1]; path = $p[2]; legacy = $false }
        } else {
            foreach ($t in ($l -split ',')) {
                if ($t.Trim() -match '^\d+$') { $out += [pscustomobject]@{ pid = [int]$t.Trim(); start = 0; path = $null; legacy = $true } }
            }
        }
    }
    $out
}
function PidEntryLine($e) { if ($e.legacy) { "$($e.pid)" } else { "$($e.pid)`t$($e.start)`t$($e.path)" } }

# The entry for a process that is running NOW (captured right after Start-Process), or $null.
function PidEntryOf([int]$id) {
    $p = Get-Process -Id $id -ErrorAction SilentlyContinue
    if (-not $p) { return $null }
    $path = try { $p.Path } catch { $null }
    $start = try { $p.StartTime.ToUniversalTime().Ticks } catch { $null }
    if (-not $path -or -not $start) { return $null }
    [pscustomobject]@{ pid = $id; start = [long]$start; path = [IO.Path]::GetFullPath($path); legacy = $false }
}

# The live process an entry recorded - same pid AND same start time AND same executable - or $null.
# A legacy entry never verifies.
function PidEntryProcess($e, [string]$name = 'PhoenixPointWin64') {
    if ($e.legacy) { return $null }
    $p = Get-Process -Id $e.pid -ErrorAction SilentlyContinue
    if (-not $p -or ($name -and $p.ProcessName -ne $name)) { return $null }
    $now = PidEntryOf $e.pid
    if (-not $now -or $now.start -ne $e.start -or $now.path -ine $e.path) { return $null }
    $p
}
