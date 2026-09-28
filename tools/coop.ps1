#Requires -Version 7
<#
coop.ps1 - drive an N-PEER co-op session (Multiplayer mod) from the terminal.

  launch    start the peers (no Steam sync), wait until every PPBridge gate answers
  lobby     host opens a session, every client joins 127.0.0.1:<port> and readies
  campaign  lobby + host picks NEW GAME, every peer READYs (host too, which is what opens the native
            new-game settings), confirm it through the mod's own intercept, wait all on geoscape
  battle    HOST runs plans\launch-scavenge.json, wait until EVERY peer is in tactical
  kill      -Side host|client1|client2   hard-kill ONE peer (the crash a reconnect test needs)
  relaunch  -Side host|client1|client2   cold launch ONE peer again, wait for its gate (main menu)
  reconnect -Side client1|client2        press the mod's RECONNECT (ReconnectFlow.Start); older: re-JOIN
  state     connect state on every peer
  grep      -Pattern <regex> [-Side host|client1|client2|all] [-Log mp|player|both] [-Since <line>]
            per peer: the mod log the peer names itself (default; an offline peer's file is derived
            from its install path instead), its Player.log, or both. `sources` says which, per peer
  stop      kill ONLY the pids this script launched (pid file beside this script, gitignored)

-Side takes host|client1|client2|...|all; 'client' stays the 2-peer spelling of client1 and 'both'
of 'all', so the commands already in PLAYBOOK.md keep working unchanged.

Peers are DATA: two by default (D:\PP-Instance2 host, D:\PP-Instance3 client1). -Peers 3 takes the
next root from -Client2Root, or pass the whole ordered list (host first) as -PeerRoots. Relaying
through the host (client -> host -> other client) behaves unlike a 2-peer pair, so hard network
checks want three.

NEVER point a peer at the Steam install you play: every lifecycle verb (launch/relaunch/kill)
refuses a path containing 'steamapps' unless -AllowSteamInstall is passed, and even then refuses
while a PP process from that path runs that coop.ps1 did not start.

Launch deliberately skips the instance's launch-instance.bat: that bat re-syncs Mods\ FROM the
Steam install, which would overwrite a mod just deployed into the instance with -GameDir.
It replicates the rest: Goldberg must already be active, SteamAppId env, MULTIPLAYER_DIAG=1,
-mods -logFile <inst>\Player.log (previous log rotated to Player-prev.log).

launch/relaunch also gate on free COMMIT (not RAM): each instance commits ~13.5 GB in co-op tactical
and exhausting the commit limit froze both games mid-mission on 2026-09-25. The bar is per peer -
half of -MinFreeCommitGB, times the peers actually being started. -MinFreeCommitGB 0 skips. Under
-Lite the default bar is 8 GB per peer (measured ~5.35 GB in battle); an explicit -MinFreeCommitGB wins.

-Lite (opt-in, launch/relaunch): each peer windowed in its own quarter of the primary work area (2x2,
host top-left, client1 top-right, client2 bottom-left) at the game's Very Low preset, shadows off.
Written into each peer's profile Options.jopt (the game re-applies those over Unity's -screen-* args),
originals backed up once and put back by `stop` (or `restore`) after the process is gone; Unity's
shared screen PlayerPrefs likewise. -WindowW/-WindowH override the client size. Details: coop-lite.ps1.
#>
param(
    [Parameter(Position = 0, Mandatory)] [ValidateSet('launch', 'lobby', 'campaign', 'battle', 'kill', 'relaunch', 'reconnect', 'dismiss', 'state', 'grep', 'stop', 'restore')] [string]$Action,
    [string]$HostRoot = 'D:\PP-Instance2',
    [string]$ClientRoot = 'D:\PP-Instance3',
    # Third peer: the owner's own Steam game is the intended install, hence -AllowSteamInstall.
    [string]$Client2Root = 'D:\Steam\steamapps\common\Phoenix Point',
    # Whole ordered peer table (host first) when the defaults above are not the installs wanted.
    [string[]]$PeerRoots = @(),
    [ValidateRange(1, 4)] [int]$Peers = 2,
    [switch]$AllowSteamInstall,
    [int]$Port = 14242,
    [int]$DifficultyIndex = 1,
    [string]$Pattern = '',
    # Which log `grep` reads per peer: the mod's own (default), the instance's Player.log, or both.
    [ValidateSet('mp', 'player', 'both')] [string]$Log = 'mp',
    [ValidateSet('host', 'client', 'client1', 'client2', 'client3', 'both', 'all')] [string]$Side = 'all',
    [int]$Since = 0,
    [int]$SiteIndex = 0,
    # Free commit `launch` demands for a PAIR; the real bar is half of it per peer started.
    [double]$MinFreeCommitGB = 30,
    [int]$TimeoutSeconds = 300,
    [switch]$Windowed = $true,
    # Opt-in light peers: quarter-screen tiled windows + Very Low graphics, restored by stop/restore.
    [switch]$Lite,
    # Client size under -Lite; 0 = whatever fits the peer's quarter tile of the primary work area.
    [int]$WindowW = 0,
    [int]$WindowH = 0
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
. (Join-Path $here 'coop-lite.ps1')
. (Join-Path $here 'coop-util.ps1')
$cli = Join-Path (Split-Path $here -Parent) 'ppcli.ps1'
$pidFile = Join-Path $here 'coop-pids.txt'
# The peer table: index 0 is the host, the rest are client1..clientN. Every verb reads it, so a third
# install is one more entry here (-Peers 3 / -PeerRoots), never a new branch in a verb.
$peerRoots = if ($PeerRoots.Count) { @($PeerRoots) } else { @(@($HostRoot, $ClientRoot, $Client2Root) | Select-Object -First $Peers) }
if ($peerRoots.Count -lt $Peers) { throw "-Peers $Peers needs $Peers roots, only $($peerRoots.Count) known - pass -PeerRoots <ordered list, host first>" }
$peerNames = @(0..($peerRoots.Count - 1) | ForEach-Object { if ($_ -eq 0) { 'host' } else { "client$_" } })
$HostRoot = $peerRoots[0]
$clientRoots = @($peerRoots | Select-Object -Skip 1)
$ClientRoot = if ($clientRoots.Count) { $clientRoots[0] } else { $null }
# Free commit one peer needs; `launch` multiplies it by the peers it is actually starting. -Lite peers
# commit far less (coop-lite.ps1 $LiteCommitGBPerPeer) - unless -MinFreeCommitGB is passed explicitly.
$perPeerCommitGB = if ($Lite -and -not $PSBoundParameters.ContainsKey('MinFreeCommitGB')) { $script:LiteCommitGBPerPeer } else { $MinFreeCommitGB / 2 }

function Note($m) { [Console]::Error.WriteLine("[coop] $m") }
function Pp([string]$root, [string[]]$a) {
    $out = & $cli @a -PPRoot $root -Quiet 2>$null 6>$null
    # A refused verb exits 1 (e.g. no live endpoint on a peer that is not running). That is a RESULT
    # here, not this script's exit code: probes like MpLogPath ask peers that may be down.
    $global:LASTEXITCODE = 0
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
function SideName([string]$root) { $peerNames[[array]::IndexOf($peerRoots, $root)] }
# 'client' = the old 2-peer spelling of client1, 'both' = the old spelling of 'all'.
function SideRoots([string]$s) {
    if ($s -in 'all', 'both') { return $peerRoots }
    $i = if ($s -eq 'host') { 0 } elseif ($s -eq 'client') { 1 } else { [int]($s -replace '\D') }
    if ($i -ge $peerRoots.Count) { throw "no peer '$s': $($peerRoots.Count) peers ($($peerNames -join ', ')) - raise -Peers or pass -PeerRoots" }
    , $peerRoots[$i]
}
function OneRoot([string]$s, [string]$verb) {
    if ($s -in 'all', 'both') { throw "$verb needs one peer: -Side $($peerNames -join '|')" }
    @(SideRoots $s)[0]
}
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
               "(each instance commits ~$(if ($Lite) { '5.4 GB under -Lite' } else { '13.5 GB' }) in co-op tactical; exhausting the limit freezes both " +
               'games at 0% CPU). Close something, or pass -MinFreeCommitGB 0 to override.')
    }
    Note "free commit $($c.freeGB) GB of $($c.limitGB) GB limit (needed $needGB)"
    $c
}
function CoopPids {
    if (-not (Test-Path $pidFile)) { return @() }
    @((Get-Content $pidFile) -split ',' | Where-Object { $_ } | ForEach-Object { [int]$_ })
}
# Lifecycle gate (launch/relaunch/kill). The Steam install is the game the OWNER plays: refused by
# default, and even with -AllowSteamInstall refused while a PP process from it runs that coop.ps1 did
# not start - killing or cold-launching over a live session of his is not recoverable. Reads (state,
# grep, connect) never come through here, so a third peer stays observable either way.
function GuardLifecycle([string]$r) {
    if ($r -notmatch 'steamapps') { return }
    if (-not $AllowSteamInstall) {
        throw ("REFUSED: $r looks like a Steam library install - the owner's own game. Pass " +
               '-AllowSteamInstall only when he has said that install may be launched and killed.')
    }
    $foreign = @(SideProcs $r | Where-Object { $_.Id -notin (CoopPids) })
    if ($foreign) { throw "REFUSED: $r is already running (pid $($foreign.Id -join ',')) and coop.ps1 did not start it - never touch the process the owner is playing" }
}
# One instance, exactly the way `launch` starts the peers. Refuses the Steam install, a running game and
# a disarmed PPBridge, rotates Player.log, returns the new pid.
function StartSide([string]$r) {
    GuardLifecycle $r
    $running = SideProcs $r
    if ($running) { throw "REFUSED: $r already running (pid $($running.Id -join ','))" }
    if (-not (Test-Path "$r\Mods\PPBridge\ppcli-enabled")) { throw "REFUSED: $r\Mods\PPBridge\ppcli-enabled missing (arm PPBridge first)" }
    $env:SteamClientLaunch = $null; $env:SteamAppId = '839770'; $env:SteamGameId = '839770'; $env:MULTIPLAYER_DIAG = '1'
    if (Test-Path "$r\Player.log") { Move-Item "$r\Player.log" "$r\Player-prev.log" -Force }
    $a = @('-mods', '-logFile', "`"$r\Player.log`"")
    if ($Lite) {
        # The profile is what the game applies (coop-lite.ps1 header); the Unity args only size the
        # first frames before it does. Registry snapshot first: it is shared by every install.
        $t = LiteTile ([array]::IndexOf($peerRoots, $r))
        $w = if ($WindowW -gt 0) { $WindowW } else { $t.w }
        $h = if ($WindowH -gt 0) { $WindowH } else { $t.h }
        LiteRegSnapshot
        $jopt = LiteApply $r $w $h
        Note "lite: $(SideName $r) ${w}x$h Very Low -> $jopt (originals in $(LiteBackupPath $r))"
        $a += '-screen-fullscreen', '0', '-screen-width', "$w", '-screen-height', "$h"
    } else {
        # Only ever true after a -Lite run that was not stopped/restored: a plain start plays the
        # owner's own settings again, not the leftover lite ones.
        if (LiteRestore $r) { Note "lite: restored $(SideName $r)'s own video/graphics options before a normal start" }
        if ($Windowed) { $a += '-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720' }
    }
    (Start-Process -FilePath "$r\PhoenixPointWin64.exe" -WorkingDirectory $r -ArgumentList $a -PassThru).Id
}
# -Lite: move the peer's window into its tile. After the gate, because the game re-applies its own
# resolution (InitVideoOptions) during boot, long before PPBridge answers.
function LitePlace([string]$r, [int]$procId) {
    $t = LiteTile ([array]::IndexOf($peerRoots, $r))
    $got = LiteMove $procId $t.x $t.y
    if (-not $got) { Note "lite: $(SideName $r) pid $procId has no visible Unity window - not tiled" }
    $got
}
# -Lite undo for every install in $roots whose game is gone; the shared registry snapshot only once no
# install is still backed up and no coop-started game runs. Returns what was restored.
function LiteRestoreIdle([string[]]$roots) {
    $done = @()
    foreach ($r in $roots) {
        if (SideProcs $r) { Note "lite: $r still running - its options stay lite until it exits (then: restore)"; continue }
        if (LiteRestore $r) { $done += $(if ($r -in $peerRoots) { SideName $r } else { $r }) }
    }
    $alive = @((CoopPids) | Where-Object { (Get-Process -Id $_ -ErrorAction SilentlyContinue).ProcessName -eq 'PhoenixPointWin64' })
    if (-not (LiteBackedRoots) -and -not $alive -and (LiteRegRestore)) { $done += 'registry' }
    $done
}
# Keep the pid file a superset of what is still ours: dead pids drop, the new one joins, so `stop`
# after a `relaunch` still kills both sides.
function PidsRecord([int[]]$new) {
    $live = @((CoopPids) | Where-Object { (Get-Process -Id $_ -ErrorAction SilentlyContinue).ProcessName -eq 'PhoenixPointWin64' })
    (@($live + $new) | Select-Object -Unique) -join ',' | Set-Content $pidFile
}
# The instance's own -logFile (engine + duplicated [MP] lines). Unity's default LocalLow Player.log is
# NOT a fallback for it: that one belongs to whichever install ran last without -logFile.
function LogLines([string]$root) { Join-Path $root 'Player.log' }
# The mod's OWN log, asked of the running peer itself: Multiplayer.Util.MultiplayerLog.LogPath
# (Multiplayer2\src\Bootstrap\MultiplayerLog.cs:36,61-93). It does NOT sit beside the DLL - it is
# <Application.persistentDataPath>\Multiplayer\multiplayer.log, a dir SHARED by every install, and a
# second same-machine instance falls back to multiplayer-2.log, -3.log … by LOCK ORDER at launch, not
# by install folder (MultiplayerLog.cs:79-83). So the file cannot be derived from the peer's root:
# only the peer can say which one is its own, and that is the only log surface a peer coop did not
# cold-launch (the owner's own Steam game, started without -logFile) has at all.
function MpLogPath([string]$root) {
    $r = Result (Call $root '{"op":"get","type":"Multiplayer.Util.MultiplayerLog","assembly":"Multiplayer","member":"LogPath"}')
    $v = if ($r -and $r.ok) { $r.value } else { $null }
    if ($v -is [pscustomobject]) { $v = $v.Value }
    if ($v -and (Test-Path $v)) { $v } else { $null }
}
# OFFLINE mapping for a peer that cannot be probed (killed, crashed, not launched yet). Needs the mod
# log NAMED BY INSTALL: Multiplayer main 4ae4431+, multiplayer-<tag>.log where tag is the install
# folder's name (sanitized, 24 chars) + FNV-1a 32 of the full path, lower-cased and backslashed, "x8".
# EXACT reproduction of MultiplayerLog.InstallTag (Multiplayer2\src\Bootstrap\MultiplayerLog.cs:147-161),
# checked against a C# copy of it. Older builds share ONE multiplayer.log, so nothing is derivable there
# and this returns nothing rather than guessing another install's file.
function MpInstallTag([string]$installDir) {
    $p = $installDir.TrimEnd('\', '/')
    if (-not $p) { return 'unknown' }
    $sb = ''
    foreach ($ch in ([string](($p -split '[\\/]')[-1])).ToCharArray()) {
        if ($sb.Length -ge 24) { break }
        $sb += if ([char]::IsLetterOrDigit($ch) -or $ch -eq '-' -or $ch -eq '_') { $ch } else { '_' }
    }
    $h = [uint32]2166136261
    foreach ($ch in $p.Replace('/', '\').ToLowerInvariant().ToCharArray()) {
        $h = [uint32]($h -bxor [uint32][int]$ch)
        $h = [uint32](([uint64]$h * [uint64]16777619) % 4294967296)
    }
    $lead = if ($sb.Length -eq 0) { 'game' } else { $sb }
    "$lead-" + $h.ToString('x8')
}
function MpLogOffline([string]$root) {
    $dir = Join-Path $env:USERPROFILE 'AppData\LocalLow\Snapshot Games Inc\Phoenix Point\Multiplayer'
    if (-not (Test-Path $dir)) { return $null }
    $tag = MpInstallTag $root
    $exact = Join-Path $dir "multiplayer-$tag.log"
    if (Test-Path $exact) { return $exact }
    # Same install launched twice writes multiplayer-<tag>-2.log …; -prev* is history, not a live run.
    @(Get-ChildItem $dir -Filter "multiplayer-$tag-*.log" -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notmatch '-prev\d*\.log$' } | Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName)[0]
}
# The peer's own log, and which route named it: the live probe first, the install mapping second.
function MpLogFor([string]$root) {
    $p = MpLogPath $root
    if ($p) { return [pscustomobject]@{ path = $p; via = 'live probe' } }
    $p = MpLogOffline $root
    if ($p) { return [pscustomobject]@{ path = $p; via = 'offline install mapping' } }
    $null
}
# Both surfaces of that one peer: its Player.log and the mod log the peer names itself.
function LogFiles([string]$root) { @((LogLines $root), (MpLogFor $root).path) | Where-Object { $_ } }
# Where each log file's complete lines end NOW (coop-util.ps1 LogMarkFiles). Taken BEFORE an action,
# it makes WaitLog believe only lines written after it: the mod log is shared across runs and a
# long-lived host log otherwise answers with the same line from an earlier press or an earlier run.
# EVERY wait for the effect of an action takes one first.
function LogMark([string]$root) { LogMarkFiles @(LogFiles $root) }
# The files are re-resolved every poll (a mod log only nameable once the peer answers still counts).
# $failRx: a line that means the awaited one will never come (a refused join) ends the wait at once,
# quoted with up to 3 lines after it (the refusal's reasons, given ~1 s to land).
function WaitLog([string]$root, [string]$rx, [int]$sec, [hashtable]$mark = @{}, [string]$failRx = '') {
    WaitLogCore { LogFiles $root } $rx $sec $mark $failRx $root
}
# The mod's refusal of a join, e.g. a game-build parity mismatch:
# "[MP][parity] join REFUSED by host: 1 difference(s)" + "Phoenix Point build differs: host … != client …".
$joinRefusedRx = 'join REFUSED'
function WaitPhase([string]$root, [string]$phase, [int]$sec) {
    $dl = (Get-Date).AddSeconds($sec)
    while ((Get-Date) -lt $dl) {
        $r = Pp $root @('connect', 'state')
        if ($r -and $r.result.ok -and $r.result.phase -eq $phase -and $r.result.levelState -eq 'Playing') { return $r.result }
        Start-Sleep 3
    }
    throw "phase '$phase' timeout on $root"
}

# $chooseNewGame: the lobby's campaign choice (menu redesign, Multiplayer2 main f2e945a). The host's
# own READY is a term of the start gate now and it REFUSES with nothing chosen
# (MultiplayerUI.OnLobbyToggleReady, MultiplayerUI.cs:643-660), so NEW GAME
# (MultiplayerUI.OnLobbyChooseNewGame, MultiplayerUI.cs:1410) has to be pressed BEFORE any READY.
function Do-Lobby([bool]$chooseNewGame = $false) {
    Note "host: CREATE SESSION on $HostRoot"
    $hm = LogMark $HostRoot
    Invoke-Ui $HostRoot 'OnGateCreate' | Out-Null
    WaitLog $HostRoot '\[MP\]\[general\] transport initialized' 30 $hm | Out-Null
    # Clients join one after another: with 3+ peers the host has to relay an already-joined client's
    # state to the next one, which is the whole point of a third peer.
    foreach ($c in $clientRoots) {
        Note "$(SideName $c): JOIN 127.0.0.1:$Port from $c"
        $mark = LogMark $c
        Invoke-Ui $c 'OnGateJoin' ('["127.0.0.1:' + $Port + '"]') | Out-Null
        WaitLog $c 'host ACCEPTED the join' 60 $mark $joinRefusedRx | Out-Null
    }
    # Parity: a mismatched JOIN locks READY; host-setting auto-apply usually clears it a beat later
    # ("parity OK - READY unlocked"). A clean first JOIN logs no parity line at all.
    Start-Sleep 3
    $mm = Select-String -Path (LogLines $HostRoot) -Pattern 'parity (mismatch|update|OK)' | Select-Object -Last 1
    if ($mm -and $mm.Line -notmatch 'parity OK') { throw "parity mismatch - READY locked; align mods/versions between instances (details follow this line in host Player.log): $($mm.Line)" }
    if ($chooseNewGame) {
        # Before the readies: the choice RESETS every ready when it changes (MultiplayerUI.cs:1420-1426).
        Note 'host: NEW GAME (the lobby campaign choice)'
        $hm = LogMark $HostRoot
        Invoke-Ui $HostRoot 'OnLobbyChooseNewGame' | Out-Null
        WaitLog $HostRoot '\[MP\]\[lobby\] campaign choice: NEW GAME' 30 $hm | Out-Null
    }
    # Before the first READY: `campaign` waits for "everyone is READY" written after THIS mark.
    $script:readyMark = LogMark $HostRoot
    foreach ($c in $clientRoots) {
        Note "$(SideName $c): READY"
        Invoke-Ui $c 'OnLobbyToggleReady' '[true]' | Out-Null
    }
    if ($chooseNewGame) {
        # The host READYs like everyone else; the gate counts it (MultiplayerUI.cs:631-634).
        Note 'host: READY'
        Invoke-Ui $HostRoot 'OnLobbyToggleReady' '[true]' | Out-Null
    }
}

switch ($Action) {
    'launch' {
        # -Side narrows what is started; the commit bar is per peer times the peers being started.
        $roots = @(SideRoots $Side)
        $commit = CommitGate ($perPeerCommitGB * $roots.Count)
        # Refuse for EVERY peer before starting any one, so a bad client never leaves a live host.
        foreach ($r in $roots) {
            GuardLifecycle $r
            $running = SideProcs $r
            if ($running) { throw "REFUSED: $r already running (pid $($running.Id -join ','))" }
            if (-not (Test-Path "$r\Mods\PPBridge\ppcli-enabled")) { throw "REFUSED: $r\Mods\PPBridge\ppcli-enabled missing (arm PPBridge first)" }
        }
        $pids = @(foreach ($r in $roots) { StartSide $r })
        PidsRecord $pids
        Note "launched pids $($pids -join ',')"
        $phases = [ordered]@{}
        foreach ($r in $roots) { $phases[(SideName $r)] = (WaitGate $r $TimeoutSeconds).phase }
        $out = [ordered]@{ ok = $true; pids = $pids; peers = $phases; freeCommitGB = $commit.freeGB }
        if ($Lite) {
            $win = [ordered]@{}
            for ($i = 0; $i -lt $roots.Count; $i++) { $win[(SideName $roots[$i])] = LitePlace $roots[$i] $pids[$i] }
            $out.windows = $win
        }
        [pscustomobject]$out | ConvertTo-Json -Compress
    }
    'lobby' {
        Do-Lobby
        [pscustomobject]@{ ok = $true; hostClients = (Engine $HostRoot 'IsActiveSession') } | ConvertTo-Json -Compress
    }
    'campaign' {
        # NEW GAME is chosen inside Do-Lobby, then every peer READYs (host included) and the MOD opens
        # the native settings screen by itself - MultiplayerUI.TickNewGameRoute -> OnLobbyNewCampaign
        # (MultiplayerUI.cs:1457-1467). Pressing NEW CAMPAIGN from here would race that auto-open.
        Do-Lobby $true
        Note 'host: waiting for the mod to open the native new-game settings (everyone READY)'
        WaitLog $HostRoot '\[MP\]\[lobby\] NEW GAME: everyone is READY' 60 $script:readyMark | Out-Null
        Start-Sleep 3
        # The native settings state (top of HomeScreenView._statesStack): confirm it. The mod's prefix
        # HOLDS that confirm and arms its 5 s countdown, whose fire re-issues it
        # (NewCampaignInterceptPatch.CommitNewCampaign) - the same path a human CONFIRM takes.
        # Difficulty = whatever the settings screen shows (profile Options_NewGameDifficultyOption).
        $hsv = Result (Call $HostRoot '{"op":"invoke","type":"UnityEngine.Object","assembly":"UnityEngine.CoreModule","member":"FindObjectOfType","typeArgs":["PhoenixPoint.Home.View.HomeScreenView"],"args":[]}')
        $stk = Result (Call $HostRoot ('{"op":"get","target":"' + $hsv.value.h + '","member":"_statesStack"}'))
        $cur = Result (Call $HostRoot ('{"op":"get","target":"' + $stk.value.h + '","member":"CurrentState"}'))
        if ($cur.value.type -notlike '*UIStateNewGeoscapeGameSettings') { throw "host top state is '$($cur.value.type)', not the new-game settings screen" }
        # Marks before the confirm: ARMED and each client's [MP] traffic must come from THIS campaign.
        $armMark = LogMark $HostRoot
        $clientMarks = @{}
        foreach ($c in $clientRoots) { $clientMarks[$c] = LogMark $c }
        $conf = Result (Call $HostRoot ('{"op":"invoke","target":"' + $cur.value.h + '","member":"GameSettings_OnConfirm","args":[]}'))
        if (-not $conf.ok) { throw "confirm failed: $($conf | ConvertTo-Json -Compress)" }
        WaitLog $HostRoot 'New-campaign co-op bootstrap ARMED' 30 $armMark | Out-Null
        $scenes = [ordered]@{ host = (WaitPhase $HostRoot 'geoscape' $TimeoutSeconds).scene }
        foreach ($c in $clientRoots) {
            $scenes[(SideName $c)] = (WaitPhase $c 'geoscape' $TimeoutSeconds).scene
            WaitLog $c '\[MP\]' 5 $clientMarks[$c] | Out-Null
        }
        [pscustomobject]@{ ok = $true; peers = $scenes } | ConvertTo-Json -Compress
    }
    'battle' {
        # HOST only: the mod carries the launch to every client (launch-scavenge.json's own "//needs").
        $plan = Join-Path (Split-Path $here -Parent) 'plans\launch-scavenge.json'
        Note "host: plan launch-scavenge.json (siteIndex $SiteIndex)"
        $p = Result (Pp $HostRoot @('plan', $plan, ('{"siteIndex":' + $SiteIndex + '}')))
        if (-not $p -or -not $p.ok) { throw "launch-scavenge failed on host: $($p | ConvertTo-Json -Compress -Depth 6)" }
        $scenes = [ordered]@{ host = (WaitPhase $HostRoot 'tactical' $TimeoutSeconds).scene }
        foreach ($c in $clientRoots) { $scenes[(SideName $c)] = (WaitPhase $c 'tactical' $TimeoutSeconds).scene }
        [pscustomobject]@{ ok = $true; site = $p.output.site; mission = $p.output.mission
                           peers = $scenes } | ConvertTo-Json -Compress
    }
    'kill' {
        # The CRASH a reconnect test needs: no quit, no save - just the process gone. Matched by install
        # path, so the other peers and the Steam install you play are never touched.
        $root = OneRoot $Side 'kill (use stop to kill every peer coop started)'
        GuardLifecycle $root
        $procs = SideProcs $root
        if (-not $procs) { throw "nothing running from $root" }
        foreach ($p in $procs) { Stop-Process -Id $p.Id -Force; Note "killed $($p.Id) ($Side)" }
        [pscustomobject]@{ ok = $true; side = $Side; killed = @($procs.Id) } | ConvertTo-Json -Compress
    }
    'relaunch' {
        # Cold launch ONE side back to the main menu (it must be gone already - StartSide refuses a
        # running instance, so `kill -Side <same>` comes first).
        $root = OneRoot $Side 'relaunch'
        $commit = CommitGate $perPeerCommitGB
        $newPid = StartSide $root
        PidsRecord @($newPid)
        Note "relaunched $Side as pid $newPid - waiting for its PPBridge gate"
        $s = WaitGate $root $TimeoutSeconds
        $out = [ordered]@{ ok = $true; side = $Side; pid = $newPid; phase = $s.phase; scene = $s.scene
                           freeCommitGB = $commit.freeGB }
        if ($Lite) { $out.window = LitePlace $root $newPid }
        [pscustomobject]$out | ConvertTo-Json -Compress
    }
    'reconnect' {
        # Presses the mod's own RECONNECT, the first button on the network-game screen: static
        # Multiplayer.UI.ReconnectFlow.Start() (Multiplayer2\src\Lobby\ReconnectFlow.cs:40), which replays
        # the last-session record (LastSession.Current) through MultiplayerUI.BeginReconnect.
        # PROBED, not assumed: a build without ReconnectFlow falls back to the older path - a second
        # OnGateJoin at the host address, which the host still treats as a reconnect (it matches the
        # persistent playerGUID: SessionLifecycle.StaleRejoinPeers -> SessionManager.ResumePeer).
        if ($Side -notlike 'client*') { throw 'reconnect needs -Side client|client1|client2: the returning peer is a client, and a host cannot rejoin itself (relaunch the host and CREATE SESSION again)' }
        $ClientRoot = OneRoot $Side 'reconnect'
        $probe = Result (Call $ClientRoot '{"op":"get","type":"Multiplayer.UI.ReconnectFlow","assembly":"Multiplayer","member":"CanReconnect"}')
        $can = if ($probe -and $probe.ok) { $probe.value } else { $null }
        if ($can -is [pscustomobject]) { $can = $can.Value }
        if ($can -is [string]) { $can = $can -eq 'True' }
        $via = if ($null -eq $can) { 'OnGateJoin (no ReconnectFlow on this build)' }
               elseif (-not $can) { 'OnGateJoin (ReconnectFlow present but CanReconnect false - no last-session record)' }
               else { 'ReconnectFlow.Start' }
        Note "client: $via"
        # Before the press: hostResume must quote THIS rejoin, not an older resume line in the host log.
        $hostMark = LogMark $HostRoot
        # Same for the client's own lines: a previous reconnect's "rejoined" must not pass this one.
        $mark = LogMark $ClientRoot
        if ($via -eq 'ReconnectFlow.Start') {
            $r = Result (Call $ClientRoot '{"op":"invoke","type":"Multiplayer.UI.ReconnectFlow","assembly":"Multiplayer","member":"Start","args":[]}')
            if (-not $r.ok) { throw "ReconnectFlow.Start on ${ClientRoot}: $($r | ConvertTo-Json -Compress)" }
            $pressed = WaitLog $ClientRoot '\[MP\]\[reconnect\] RECONNECT pressed' 30 $mark
            $joined = WaitLog $ClientRoot '\[MP\]\[reconnect\] rejoined the session' 120 $mark
        } else {
            Invoke-Ui $ClientRoot 'OnGateJoin' ('["127.0.0.1:' + $Port + '"]') | Out-Null
            $pressed = $null
            $joined = WaitLog $ClientRoot 'host ACCEPTED the join' 60 $mark $joinRefusedRx
        }
        # The host's own resume edge. Best effort: a host that never paused the peer posts no notice.
        $resumed = try { WaitLog $HostRoot 'RESUMED|is back' 30 $hostMark } catch { $null }
        [pscustomobject]@{ ok = $true; side = (SideName $ClientRoot); via = $via; pressed = $pressed; joined = $joined
                           hostResume = $resumed
                           client = (Pp $ClientRoot @('connect', 'state')).result.phase } | ConvertTo-Json -Compress
    }
    'dismiss' {
        # Click through the geoscape's stacked popups on one side (-Side host|client): a modal gets its
        # OK (UIStateGeoModal.FinishDialog(Confirm)), an event gets FinishEncounter. Stops at the first
        # other screen and reports it - UIStateReplenish is deliberately NOT dismissed.
        $root = if ($Side -in 'all', 'both') { $HostRoot } else { @(SideRoots $Side)[0] }
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
        [pscustomobject]@{ ok = $true; side = (SideName $root); screens = $seen } | ConvertTo-Json -Compress
    }
    'state' {
        $st = [ordered]@{}
        foreach ($r in @(SideRoots $Side)) { $st[(SideName $r)] = (Pp $r @('connect', 'state')).result }
        [pscustomobject]$st | ConvertTo-Json -Compress -Depth 5
    }
    'grep' {
        # PER PEER: -Log mp (default) reads the mod log that peer names, player reads its -logFile,
        # both reads each. `sources` always says which file answered for which peer - a peer with no
        # readable log is reported by name instead of contributing silence.
        $rows = @(); $sources = [ordered]@{}
        foreach ($r in @(SideRoots $Side)) {
            $name = SideName $r
            $files = @(); $how = @()
            if ($Log -in 'mp', 'both') {
                $mp = MpLogFor $r      # $null when neither route names a file
                if ($mp) { $files += $mp.path; $how += "$($mp.path) [$($mp.via)]" }
            }
            if (($Log -in 'player', 'both') -and (Test-Path (LogLines $r))) {
                $files += LogLines $r; $how += "$(LogLines $r) [install -logFile]"
            }
            $sources[$name] = if ($how) { $how } else { "NO LOG: no live probe, no install-named mod log (pre-4ae4431 builds share one multiplayer.log, which is not mappable to an install) and $(LogLines $r) does not exist" }
            foreach ($f in $files) {
                $rows += Select-String -Path $f -Pattern $Pattern | Where-Object LineNumber -gt $Since |
                    ForEach-Object { [pscustomobject]@{ side = $name; log = (Split-Path $f -Leaf); line = $_.LineNumber; text = $_.Line } }
            }
        }
        [pscustomobject]@{ ok = $true; count = @($rows).Count; sources = $sources; rows = @($rows) } | ConvertTo-Json -Compress -Depth 4
    }
    'stop' {
        # Every coop-started peer by default; -Side <peer> stops just that one and keeps the pid file
        # for the rest. Only ever pids this script recorded, whatever -Side says.
        if (-not (Test-Path $pidFile)) { throw 'no pid file - nothing launched by coop.ps1' }
        $roots = @(SideRoots $Side)
        $everyPeer = $Side -in 'all', 'both'
        $stopped = @(); $kept = @()
        foreach ($p in (CoopPids)) {
            $proc = Get-Process -Id $p -ErrorAction SilentlyContinue
            if (-not $proc -or $proc.ProcessName -ne 'PhoenixPointWin64') { continue }
            $path = try { $proc.Path } catch { $null }
            if ($everyPeer -or @($roots | Where-Object { $path -like "$_\*" })) {
                Stop-Process -Id $p -Force; Note "stopped $p"; $stopped += $p
            } else { $kept += $p }
        }
        if ($kept) { $kept -join ',' | Set-Content $pidFile } else { Remove-Item $pidFile }
        $out = [ordered]@{ ok = $true; stopped = $stopped; kept = $kept }
        # -Lite undo, only when a -Lite run left backups: the processes must be GONE first, since Unity
        # writes its PlayerPrefs on exit and the game may save its options.
        $backed = @(LiteBackedRoots | Where-Object { $everyPeer -or $_ -in $roots })
        if ($backed -or ($everyPeer -and (Test-Path (Join-Path $script:LiteDir 'registry.json')))) {
            if ($stopped) {
                $left = @(LiteWaitExit $stopped 90)
                if ($left.Count) { Note "lite: pid $($left -join ',') still listed 90 s after the kill" }
            }
            $out.liteRestored = @(LiteRestoreIdle $backed)
        }
        [pscustomobject]$out | ConvertTo-Json -Compress
    }
    'restore' {
        # Put back what -Lite changed, for every backed-up install that is not running (after a kill,
        # a crash, or a stop that ran while a game still exited). Never touches a running game's profile.
        $backed = @(LiteBackedRoots)
        $restored = @(LiteRestoreIdle $backed)
        [pscustomobject]@{ ok = $true; restored = $restored; pending = @(LiteBackedRoots)
                           registryPending = (Test-Path (Join-Path $script:LiteDir 'registry.json')) } | ConvertTo-Json -Compress
    }
}
