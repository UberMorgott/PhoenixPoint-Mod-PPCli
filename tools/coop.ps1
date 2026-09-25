#Requires -Version 7
<#
coop.ps1 - drive a TWO-INSTANCE co-op session (Multiplayer mod) from the terminal.

  launch   start host + client instances (no Steam sync), wait until both PPBridge gates answer
  lobby    host opens a session, client joins 127.0.0.1:<port>, client readies
  campaign lobby + host NEW CAMPAIGN through the mod's own intercept, wait both on geoscape
  state    connect state on both
  grep     -Pattern <regex> [-Side host|client|both] [-Since <line>]  (Player.log of each instance)
  stop     kill ONLY the pids this script launched (pid file beside this script, gitignored)

Instances default to D:\PP-Instance2 (host) / D:\PP-Instance3 (client). NEVER point either at the
Steam install you play: launch refuses a path that contains 'steamapps'.

Launch deliberately skips the instance's launch-instance.bat: that bat re-syncs Mods\ FROM the
Steam install, which would overwrite a mod just deployed into the instance with -GameDir.
It replicates the rest: Goldberg must already be active, SteamAppId env, MULTIPLAYER_DIAG=1,
-mods -logFile <inst>\Player.log (previous log rotated to Player-prev.log).
#>
param(
    [Parameter(Position = 0, Mandatory)] [ValidateSet('launch', 'lobby', 'campaign', 'dismiss', 'state', 'grep', 'stop')] [string]$Action,
    [string]$HostRoot = 'D:\PP-Instance2',
    [string]$ClientRoot = 'D:\PP-Instance3',
    [int]$Port = 14242,
    [int]$DifficultyIndex = 1,
    [string]$Pattern = '',
    [ValidateSet('host', 'client', 'both')] [string]$Side = 'both',
    [int]$Since = 0,
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
        foreach ($r in $HostRoot, $ClientRoot) {
            if ($r -match 'steamapps') { throw "REFUSED: $r looks like a Steam library install" }
            $running = Get-Process PhoenixPointWin64 -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -like "$r\*" } catch { $false } }
            if ($running) { throw "REFUSED: $r already running (pid $($running.Id -join ','))" }
            if (-not (Test-Path "$r\Mods\PPBridge\ppcli-enabled")) { throw "REFUSED: $r\Mods\PPBridge\ppcli-enabled missing (arm PPBridge first)" }
        }
        $env:SteamClientLaunch = $null; $env:SteamAppId = '839770'; $env:SteamGameId = '839770'; $env:MULTIPLAYER_DIAG = '1'
        $pids = foreach ($r in $HostRoot, $ClientRoot) {
            if (Test-Path "$r\Player.log") { Move-Item "$r\Player.log" "$r\Player-prev.log" -Force }
            $a = @('-mods', '-logFile', "`"$r\Player.log`"")
            if ($Windowed) { $a += '-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720' }
            (Start-Process -FilePath "$r\PhoenixPointWin64.exe" -WorkingDirectory $r -ArgumentList $a -PassThru).Id
        }
        $pids -join ',' | Set-Content $pidFile
        Note "launched pids $($pids -join ',')"
        $h = WaitGate $HostRoot $TimeoutSeconds; $c = WaitGate $ClientRoot $TimeoutSeconds
        [pscustomobject]@{ ok = $true; pids = $pids; host = $h.phase; client = $c.phase } | ConvertTo-Json -Compress
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
