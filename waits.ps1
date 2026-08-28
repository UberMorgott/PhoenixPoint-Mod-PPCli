<#
  Bounded waiting. Every function here answers one question: how long may this block, and what does
  it say when it gives up. A file of its own only so tests\waits.tests.ps1 can reach it - ppcli.ps1
  takes a Mandatory parameter and cannot be dot-sourced.

  Why it exists: the game can wedge. A mod throwing during a load leaves the main thread stuck, the
  pipe server still accepts the connection, and the verb it enqueued never runs. Everything the
  client does after that used to wait for an answer that was never coming.
#>

# Every pipe read is BOUNDED. PipeStream has no usable ReadTimeout, so the ceiling is enforced
# around ReadAsync instead: an unbounded Read() against a wedged game blocks for as long as the
# game lives, which is indistinguishable from a slow load and is the hang this file exists to stop.
function Read-Exact([IO.Stream] $s, [int] $count, [int] $timeoutMs = 30000, [string] $what = 'the game') {
    $buf = New-Object byte[] $count
    $got = 0
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ($got -lt $count) {
        $left = [int]($deadline - (Get-Date)).TotalMilliseconds
        $task = $null
        if ($left -gt 0) { $task = $s.ReadAsync($buf, $got, $count - $got) }
        if ($left -le 0 -or -not $task.Wait($left)) {
            throw ("TIMEOUT: $what accepted the connection but sent only $got of $count bytes in " +
                   "${timeoutMs}ms. That is a wedged game, not a slow one - the pipe thread is alive " +
                   'and the main thread is not. Look at the game window, then kill that process and ' +
                   'relaunch it; raise -PipeTimeoutSeconds only if you know the call is genuinely long.')
        }
        $n = $task.Result
        if ($n -le 0) { throw "the pipe closed after $got of $count bytes" }
        $got += $n
    }
    ,$buf
}

# The game's own log for the process being driven: `run` passes -logFile, a hand-launched game
# writes Unity's default. Null when neither is there, and the watch is then simply off - a missing
# log must never be an error in its own right.
function Get-GameLogPath([int] $ProcessId = 0) {
    if ($ProcessId) {
        $cmd = (Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId" -ErrorAction SilentlyContinue).CommandLine
        if ($cmd -match '-logFile\s+"?([^"]+?)"?(\s|$)') { return $Matches[1] }
    }
    $unity = Join-Path $env:USERPROFILE 'AppData\LocalLow\Snapshot Games Inc\Phoenix Point\Player.log'
    if (Test-Path $unity) { return $unity }
    $null
}

# Where the log ends RIGHT NOW. Everything Get-LogFault reports is written after this point, so a
# log full of a previous session's noise can never fail a fresh run.
function New-LogMark([string] $Path) {
    $n = 0
    if ($Path -and (Test-Path $Path)) { $n = @(Get-Content -Path $Path -ErrorAction SilentlyContinue).Count }
    # A hashtable, not a PSCustomObject: the caller stamps `next` into it to throttle its own polling.
    @{ path = $Path; lines = $n; next = $null }
}

# Root namespaces that belong to the GAME, its engine or its runtime. Everything else in a stack
# frame is somebody's mod, which is the whole signal - so this list, not a mod name, is what the
# default fault detection is built on. A name here that should not be silences a real fault; a name
# missing from it costs a false fast-fail that -IgnoreLogFaults reopens, so it errs long.
# ponytail: a flat allowlist, matched on the FIRST identifier of the frame. It cannot tell a mod that
# declares its types in `PhoenixPoint.*` from the game; -FaultPattern is the answer if that ever bites.
$script:PPCLI_EngineRoots = @(
    'PhoenixPoint', 'Base', 'Unity', 'UnityEngine', 'UnityEditor', 'System', 'Mono', 'Microsoft',
    'Newtonsoft', 'TMPro', 'I2', 'Rewired', 'Steamworks', 'Cinemachine', 'DG', 'UniLinq', 'Doozy',
    'AK', 'Photon', 'ExitGames', 'SG', 'HarmonyLib', 'Harmony', 'PhoenixPointModLoader', 'PPModLoader',
    'Assets', 'Epic', 'EOS', 'Sirenix', 'Spine', 'JetBrains'
)

# Does this stack frame name a MOD? A frame reads `  at <Root>.<Type>.<Method> (...)`; anything whose
# root identifier is not engine or game code was loaded by the mod loader.
function Test-ModFrame([string] $Line) {
    if ($Line -notmatch '^\s*at\s+([A-Za-z_][A-Za-z0-9_]*)\s*[.<]') { return $false }
    return -not ($script:PPCLI_EngineRoots -contains $Matches[1])
}

# THE DEAD-RUN SIGNAL, and it is deliberately narrow. A bare `NullReferenceException` is written by
# a perfectly healthy session - this repo's own logs carry ten of them in one run - and so is
# "ArgumentException: Mesh can not have more than 65000 vertices". Failing a run on either would be
# worse than the hang it is meant to catch. What is NOT normal is an exception whose stack names a
# MOD: that is the "a mod throws and the load never finishes" case, and it means the run is already
# dead, so waiting out the rest of the budget buys nothing.
#
# -Pattern EMPTY (the default) means "any mod frame at all", decided by Test-ModFrame. It used to
# default to the literal 'TFTV' - one session's own mod list shipped as everyone's default, so a
# stranger with a different mod set got no fast-fail whatsoever. A non-empty -Pattern narrows to that
# regex instead, which is what you want when you already know which mod you are chasing.
#
# Returns the exception header plus the frames that named the mod, or $null.
function Get-LogFault($Mark, [string] $Pattern = '', [int] $Frames = 8) {
    if (-not $Mark -or -not $Mark.path -or -not (Test-Path $Mark.path)) { return $null }
    $lines = @(Get-Content -Path $Mark.path -ErrorAction SilentlyContinue)
    for ($i = $Mark.lines; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -notmatch '^\s*[\w\.]*Exception(:|\s*$)') { continue }
        $last = [Math]::Min($i + $Frames, $lines.Count - 1)
        $block = $lines[$i..$last]
        if ($Pattern) {
            if (($block -join "`n") -match $Pattern) { return ($block -join "`n") }
            continue
        }
        foreach ($line in $block) { if (Test-ModFrame $line) { return ($block -join "`n") } }
    }
    $null
}
