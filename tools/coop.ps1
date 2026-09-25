#Requires -Version 7
<#
coop.ps1 - drive a TWO-INSTANCE co-op session (Multiplayer mod) from the terminal.

  launch    start host + client instances (no Steam sync), wait until both PPBridge gates answer
  lobby     host opens a session, client joins 127.0.0.1:<port>, client readies
  campaign  lobby + host NEW CAMPAIGN through the mod's own intercept, wait both on geoscape
  battle    HOST runs plans\launch-scavenge.json, wait until BOTH sides are in tactical
  kill      -Side host|client   hard-kill ONE side (the crash a reconnect test needs)
  relaunch  -Side host|client   cold launch ONE side again, wait for its PPBridge gate (main menu)
  reconnect -Side client        client JOINs the host again - the mod's own reconnect path
  state     connect state on both
  grep      -Pattern <regex> [-Side host|client|both] [-Since <line>]  (Player.log of each instance)
  stop      kill ONLY the pids this script launched (pid file beside this script, gitignored)

Instances default to D:\PP-Instance2 (host) / D:\PP-Instance3 (client). NEVER point either at the
Steam install you play: launch refuses a path that contains 'steamapps'.

Launch deliberately skips the instance's launch-instance.bat: that bat re-syncs Mods\ FROM the
Steam install, which would overwrite a mod just deployed into the instance with -GameDir.
It replicates the rest: Goldberg must already be active, SteamAppId env, MULTIPLAYER_DIAG=1,
-mods -logFile <inst>\Player.log (previous log rotated to Player-prev.log).

launch/relaunch also gate on free COMMIT (not RAM): each instance commits ~13.5 GB in co-op tactical
and exhausting the commit limit froze both games mid-mission on 2026-09-25. -MinFreeCommitGB 0 skips.
#>
param(
    [Parameter(Position = 0, Mandatory)] [ValidateSet('launch', 'lobby', 'campaign', 'battle', 'kill', 'relaunch', 'reconnect', 'dismiss', 'state', 'grep', 'stop')] [string]$Action,
    [string]$HostRoot = 'D:\PP-Instance2',
    [string]$ClientRoot = 'D:\PP-Instance3',
    [int]$Port = 14242,
    [int]$DifficultyIndex = 1,
    [string]$Pattern = '',
    [ValidateSet('host', 'client', 'both')] [string]$Side = 'both',
    [int]$Since = 0,
    [int]$SiteIndex = 0,
    # Free commit `launch` demands for the PAIR; `relaunch` demands half of it for the one side.
    [double]$MinFreeCommitGB = 30,
    [int]$TimeoutSeconds = 300,
    [switch]$Windowed = $true
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$cli = Join-Path (Split-Path $here -Parent) 'ppcli.ps1'
$pidFile = Join-Path $here 'coop-pids.txt'

function Note($m) { [Console]::Error.WriteLine("[coop] $m") }
function Pp([string]$root, [string[]]$a) {
    $out = & $cli @a -PPRoot $root 2>$null 6>$null
    try { return ($out | Select-Object -Last 1 | ConvertFrom-Json) } catch { return $null }
}
function Call([string]$root, [string]$json) { Pp $root @('connect', 'call', $json) }
function Result($r) { if ($r -and $r.result) { $r.result } else { $r } }
function WaitGate([string]$root, [int]$sec) {
    $dl = (Get-Date).AddSeconds($sec)
    while ((Get-Date) -lt $dl) {
        $r = Pp $root @('connect', 'state')
        if ($r -and $r.result.ok) { return $r.result }
        Start-Sleep 3
    }
    throw "gate timeout: $root"
}
function MpUi([string]$root) {
    $r = Result (Call $root '{"op":"get","type":"Multiplayer.UI.MultiplayerUI","assembly":"Multiplayer","member":"Instance"}')
    if (-not $r.ok -or -not $r.value) { throw "MultiplayerUI.Instance unavailable on $root (mod not active?)" }
    $r.value.h
}
function Invoke-Ui([string]$root, [string]$member, [string]$argsJson = '[]') {
    $h = MpUi $root
    $r = Result (Call $root ('{"op":"invoke","target":"' + $h + '","member":"' + $member + '","args":' + $argsJson + '}'))
    if (-not $r.ok) { throw "$member on ${root}: $($r | ConvertTo-Json -Compress)" }
    $r
}
function Engine([string]$root, [string]$member) {
    $e = Result (Call $root '{"op":"get","type":"Multiplayer.Network.NetworkEngine","assembly":"Multiplayer","member":"Instance"}')
    if (-not $e.value) { return $null }
    if (-not $member) { return $e.value.h }
    (Result (Call $root ('{"op":"get","target":"' + $e.value.h + '","member":"' + $member + '"}'))).value
}
function SideRoot([string]$s) { if ($s -eq 'client') { $ClientRoot } else { $HostRoot } }
function SideProcs([string]$root) {
    @(Get-Process PhoenixPointWin64 -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -like "$root\*" } catch { $false } })
}
# Windows COMMIT charge, not RAM: Win32_OperatingSystem reports the commit limit as
# TotalVirtualMemorySize and (limit - commit charge) as FreeVirtualMemory, both in KB.
function FreeCommit {
    $os = Get-CimInstance Win32_OperatingSystem
    [pscustomobject]@{ freeGB  = [math]::Round($os.FreeVirtualMemory / 1MB, 1)
                       limitGB = [math]::Round($os.TotalVirtualMemorySize / 1MB, 1) }
}
function CommitGate([double]$needGB) {
    if ($needGB -le 0) { return $null }
    $c = FreeCommit
    if ($c.freeGB -lt $needGB) {
        throw ("REFUSED: $($c.freeGB) GB free commit of a $($c.limitGB) GB limit, need $needGB GB " +
               '(each instance commits ~13.5 GB in co-op tactical; exhausting the limit freezes both ' +
               'games at 0% CPU). Close something, or pass -MinFreeCommitGB 0 to override.')
    }
    Note "free commit $($c.freeGB) GB of $($c.limitGB) GB limit (needed $needGB)"
    $c
}
# One instance, exactly the way `launch` starts the pair. Refuses the Steam install, a running game and
# a disarmed PPBridge, rotates Player.log, returns the new pid.
function StartSide([string]$r) {
    if ($r -match 'steamapps') { throw "REFUSED: $r looks like a Steam library install" }
    $running = SideProcs $r
    if ($running) { throw "REFUSED: $r already running (pid $($running.Id -join ','))" }
    if (-not (Test-Path "$r\Mods\PPBridge\ppcli-enabled")) { throw "REFUSED: $r\Mods\PPBridge\ppcli-enabled missing (arm PPBridge first)" }
    $env:SteamClientLaunch = $null; $env:SteamAppId = '839770'; $env:SteamGameId = '839770'; $env:MULTIPLAYER_DIAG = '1'
    if (Test-Path "$r\Player.log") { Move-Item "$r\Player.log" "$r\Player-prev.log" -Force }
    $a = @('-mods', '-logFile', "`"$r\Player.log`"")
    if ($Windowed) { $a += '-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720' }
    (Start-Process -FilePath "$r\PhoenixPointWin64.exe" -WorkingDirectory $r -ArgumentList $a -PassThru).Id
}
# Keep the pid file a superset of what is still ours: dead pids drop, the new one joins, so `stop`
# after a `relaunch` still kills both sides.
function PidsRecord([int[]]$new) {
    $old = if (Test-Path $pidFile) { (Get-Content $pidFile) -split ',' | Where-Object { $_ } | ForEach-Object { [int]$_ } } else { @() }
    $live = @($old | Where-Object { (Get-Process -Id $_ -ErrorAction SilentlyContinue).ProcessName -eq 'PhoenixPointWin64' })
    (@($live + $new) | Select-Object -Unique) -join ',' | Set-Content $pidFile
}
function LogLines([string]$root) { Join-Path $root 'Player.log' }
function WaitLog([string]$root, [string]$rx, [int]$sec) {
    $dl = (Get-Date).AddSeconds($sec)
    while ((Get-Date) -lt $dl) {
        $hit = Select-String -Path (LogLines $root) -Pattern $rx -ErrorAction SilentlyContinue | Select-Object -Last 1
        if ($hit) { return $hit.Line }
        Start-Sleep 2
    }
    throw "log timeout on ${root}: /$rx/"
}
function WaitPhase([string]$root, [string]$phase, [int]$sec) {
    $dl = (Get-Date).AddSeconds($sec)
    while ((Get-Date) -lt $dl) {
        $r = Pp $root @('connect', 'state')
        if ($r -and $r.result.ok -and $r.result.phase -eq $phase -and $r.result.levelState -eq 'Playing') { return $r.result }
        Start-Sleep 3
    }
    throw "phase '$phase' timeout on $root"
}

function Do-Lobby {
    Note "host: CREATE SESSION on $HostRoot"
    Invoke-Ui $HostRoot 'OnGateCreate' | Out-Null
    WaitLog $HostRoot '\[MP\]\[general\] transport initialized' 30 | Out-Null
    Note "client: JOIN 127.0.0.1:$Port from $ClientRoot"
    Invoke-Ui $ClientRoot 'OnGateJoin' ('["127.0.0.1:' + $Port + '"]') | Out-Null
    WaitLog $ClientRoot 'host ACCEPTED the join' 60 | Out-Null
    # Parity: a mismatched JOIN locks READY; host-setting auto-apply usually clears it a beat later
    # ("parity OK - READY unlocked"). A clean first JOIN logs no parity line at all.
    Start-Sleep 3
    $mm = Select-String -Path (LogLines $HostRoot) -Pattern 'parity (mismatch|update|OK)' | Select-Object -Last 1
    if ($mm -and $mm.Line -notmatch 'parity OK') { throw "parity mismatch - READY locked; align mods/versions between instances (details follow this line in host Player.log): $($mm.Line)" }
    Note 'client: READY'
    Invoke-Ui $ClientRoot 'OnLobbyToggleReady' '[true]' | Out-Null
}

switch ($Action) {
    'launch' {
        $commit = CommitGate $MinFreeCommitGB
        # Refuse for BOTH sides before starting either one, so a bad client never leaves a live host.
        foreach ($r in $HostRoot, $ClientRoot) {
            if ($r -match 'steamapps') { throw "REFUSED: $r looks like a Steam library install" }
            $running = SideProcs $r
            if ($running) { throw "REFUSED: $r already running (pid $($running.Id -join ','))" }
            if (-not (Test-Path "$r\Mods\PPBridge\ppcli-enabled")) { throw "REFUSED: $r\Mods\PPBridge\ppcli-enabled missing (arm PPBridge first)" }
        }
        $pids = foreach ($r in $HostRoot, $ClientRoot) { StartSide $r }
        $pids -join ',' | Set-Content $pidFile
        Note "launched pids $($pids -join ',')"
        $h = WaitGate $HostRoot $TimeoutSeconds; $c = WaitGate $ClientRoot $TimeoutSeconds
        [pscustomobject]@{ ok = $true; pids = $pids; host = $h.phase; client = $c.phase
                           freeCommitGB = $commit.freeGB } | ConvertTo-Json -Compress
    }
    'lobby' {
        Do-Lobby
        [pscustomobject]@{ ok = $true; hostClients = (Engine $HostRoot 'IsActiveSession') } | ConvertTo-Json -Compress
    }
    'campaign' {
        Do-Lobby
        Start-Sleep 2
        Note 'host: NEW CAMPAIGN (opens the native settings screen)'
        Invoke-Ui $HostRoot 'OnLobbyNewCampaign' | Out-Null
        Start-Sleep 3
        # The native settings state (top of HomeScreenView._statesStack): confirm it. The mod's prefix
        # HOLDS that confirm and arms its 5 s countdown, whose fire re-issues it
        # (NewCampaignInterceptPatch.CommitNewCampaign) - the same path a human CONFIRM takes.
        # Difficulty = whatever the settings screen shows (profile Options_NewGameDifficultyOption).
        $hsv = Result (Call $HostRoot '{"op":"invoke","type":"UnityEngine.Object","assembly":"UnityEngine.CoreModule","member":"FindObjectOfType","typeArgs":["PhoenixPoint.Home.View.HomeScreenView"],"args":[]}')
        $stk = Result (Call $HostRoot ('{"op":"get","target":"' + $hsv.value.h + '","member":"_statesStack"}'))
        $cur = Result (Call $HostRoot ('{"op":"get","target":"' + $stk.value.h + '","member":"CurrentState"}'))
        if ($cur.value.type -notlike '*UIStateNewGeoscapeGameSettings') { throw "host top state is '$($cur.value.type)', not the new-game settings screen" }
        $conf = Result (Call $HostRoot ('{"op":"invoke","target":"' + $cur.value.h + '","member":"GameSettings_OnConfirm","args":[]}'))
        if (-not $conf.ok) { throw "confirm failed: $($conf | ConvertTo-Json -Compress)" }
        WaitLog $HostRoot 'New-campaign co-op bootstrap ARMED' 30 | Out-Null
        $h = WaitPhase $HostRoot 'geoscape' $TimeoutSeconds
        $c = WaitPhase $ClientRoot 'geoscape' $TimeoutSeconds
        WaitLog $ClientRoot '\[MP\]' 5 | Out-Null
        [pscustomobject]@{ ok = $true; host = $h.scene; client = $c.scene } | ConvertTo-Json -Compress
    }
    'battle' {
        # HOST only: the mod carries the launch to every client (launch-scavenge.json's own "//needs").
        $plan = Join-Path (Split-Path $here -Parent) 'plans\launch-scavenge.json'
        Note "host: plan launch-scavenge.json (siteIndex $SiteIndex)"
        $p = Result (Pp $HostRoot @('plan', $plan, ('{"siteIndex":' + $SiteIndex + '}')))
        if (-not $p -or -not $p.ok) { throw "launch-scavenge failed on host: $($p | ConvertTo-Json -Compress -Depth 6)" }
        $h = WaitPhase $HostRoot 'tactical' $TimeoutSeconds
        $c = WaitPhase $ClientRoot 'tactical' $TimeoutSeconds
        [pscustomobject]@{ ok = $true; site = $p.output.site; mission = $p.output.mission
                           host = $h.scene; client = $c.scene } | ConvertTo-Json -Compress
    }
    'kill' {
        # The CRASH a reconnect test needs: no quit, no save - just the process gone. Matched by install
        # path, so the other instance and the Steam install you play are never touched.
        if ($Side -eq 'both') { throw 'kill needs -Side host|client (use stop to kill both)' }
        $root = SideRoot $Side
        if ($root -match 'steamapps') { throw "REFUSED: $root looks like a Steam library install" }
        $procs = SideProcs $root
        if (-not $procs) { throw "nothing running from $root" }
        foreach ($p in $procs) { Stop-Process -Id $p.Id -Force; Note "killed $($p.Id) ($Side)" }
        [pscustomobject]@{ ok = $true; side = $Side; killed = @($procs.Id) } | ConvertTo-Json -Compress
    }
    'relaunch' {
        # Cold launch ONE side back to the main menu (it must be gone already - StartSide refuses a
        # running instance, so `kill -Side <same>` comes first).
        if ($Side -eq 'both') { throw 'relaunch needs -Side host|client' }
        $root = SideRoot $Side
        $commit = CommitGate ($MinFreeCommitGB / 2)
        $newPid = StartSide $root
        PidsRecord @($newPid)
        Note "relaunched $Side as pid $newPid - waiting for its PPBridge gate"
        $s = WaitGate $root $TimeoutSeconds
        [pscustomobject]@{ ok = $true; side = $Side; pid = $newPid; phase = $s.phase; scene = $s.scene
                           freeCommitGB = $commit.freeGB } | ConvertTo-Json -Compress
    }
    'reconnect' {
        # There is NO separate Reconnect button in the mod: the network-game screen offers CREATE SESSION
        # / JOIN SESSION / JOIN / BACK only (Multiplayer2\src\Lobby\NetworkGatePanel.cs:215-305). A
        # returning peer reconnects by JOINing again - the host sees a persistent playerGUID already bound
        # to a roster entry, prunes the dead connection (SessionLifecycle.StaleRejoinPeers) and resumes the
        # peer (SessionManager.ResumePeer). So this drives the same OnGateJoin the human presses.
        if ($Side -ne 'client') { throw 'reconnect needs -Side client: the returning peer is a client, and a host cannot rejoin itself (relaunch the host and CREATE SESSION again)' }
        Note "client: JOIN 127.0.0.1:$Port again (the mod's reconnect path)"
        Invoke-Ui $ClientRoot 'OnGateJoin' ('["127.0.0.1:' + $Port + '"]') | Out-Null
        $accepted = WaitLog $ClientRoot 'host ACCEPTED the join' 60
        # The host's own resume edge. Best effort: a host that never paused the peer posts no notice.
        $resumed = try { WaitLog $HostRoot 'RESUMED|is back' 30 } catch { $null }
        [pscustomobject]@{ ok = $true; side = 'client'; accepted = $accepted; hostResume = $resumed
                           client = (Pp $ClientRoot @('connect', 'state')).result.phase } | ConvertTo-Json -Compress
    }
    'dismiss' {
        # Click through the geoscape's stacked popups on one side (-Side host|client): a modal gets its
        # OK (UIStateGeoModal.FinishDialog(Confirm)), an event gets FinishEncounter. Stops at the first
        # other screen and reports it - UIStateReplenish is deliberately NOT dismissed.
        $root = if ($Side -eq 'client') { $ClientRoot } else { $HostRoot }
        $seen = @()
        for ($i = 0; $i -lt 12; $i++) {
            $vs = (Pp $root @('connect', 'roots')).result.roots.viewstate.type
            $seen += $vs
            if ($vs -like '*UIStateGeoModal') {
                Call $root '{"op":"invoke","target":"@viewstate","member":"FinishDialog","args":[{"$enum":"Confirm","type":"PhoenixPoint.Common.Utils.ModalResult"}]}' | Out-Null
            } elseif ($vs -like '*UIStateGeoscapeEvent') {
                $m = Result (Call $root '{"op":"get","target":"@viewstate","member":"Module"}')
                Call $root ('{"op":"invoke","target":"' + $m.value.h + '","member":"FinishEncounter","args":[]}') | Out-Null
            } else { break }
            Start-Sleep 2
        }
        [pscustomobject]@{ ok = $true; side = $Side; screens = $seen } | ConvertTo-Json -Compress
    }
    'state' {
        [pscustomobject]@{ host = (Pp $HostRoot @('connect', 'state')).result; client = (Pp $ClientRoot @('connect', 'state')).result } | ConvertTo-Json -Compress -Depth 5
    }
    'grep' {
        $roots = switch ($Side) { 'host' { , $HostRoot } 'client' { , $ClientRoot } default { $HostRoot, $ClientRoot } }
        $rows = foreach ($r in $roots) {
            Select-String -Path (LogLines $r) -Pattern $Pattern | Where-Object LineNumber -gt $Since |
                ForEach-Object { [pscustomobject]@{ side = $(if ($r -eq $HostRoot) { 'host' } else { 'client' }); line = $_.LineNumber; text = $_.Line } }
        }
        [pscustomobject]@{ ok = $true; count = @($rows).Count; rows = @($rows) } | ConvertTo-Json -Compress -Depth 4
    }
    'stop' {
        if (-not (Test-Path $pidFile)) { throw 'no pid file - nothing launched by coop.ps1' }
        $pids = (Get-Content $pidFile) -split ',' | ForEach-Object { [int]$_ }
        foreach ($p in $pids) {
            $proc = Get-Process -Id $p -ErrorAction SilentlyContinue
            if ($proc -and $proc.ProcessName -eq 'PhoenixPointWin64') { Stop-Process -Id $p -Force; Note "stopped $p" }
        }
        Remove-Item $pidFile
        '{"ok":true}'
    }
}
