<#
  PPCLI - drive Phoenix Point from a terminal through the PPBridge mod.

    .\ppcli.ps1 deploy
    .\ppcli.ps1 run ping
    .\ppcli.ps1 run state
    .\ppcli.ps1 run console '{"command":"ct_version","args":[]}'
    .\ppcli.ps1 batch jobs.json          # a bare JSON array of {id,verb,args}
    .\ppcli.ps1 connect state            # milliseconds, against the game ALREADY running
    .\ppcli.ps1 connect wait '{"ready":true,"timeoutMs":120000}'
    .\ppcli.ps1 connect var '{"name":"ai_enabled","value":"false"}'   # console VARIABLES, not commands
    .\ppcli.ps1 plan .\plans\spawn-at-coordinate.json '{"x":11.5,"z":-4.5,"faction":"alien"}'
    .\ppcli.ps1 plan .\plans\aim-and-run.json '{"x":-0.5,"y":0,"z":14.5,"command":"info","cmdArgs":[]}'

  Exactly ONE compact JSON object goes to stdout; everything else goes to stderr, so a caller can
  pipe stdout straight into ConvertFrom-Json.

  `connect` talks to a running game over its named pipe and is what you want in a loop. `run`/`batch`
  cold-launch the game instead - measured ~17s to an answer for menu-only jobs, minutes if a job
  loads a mission - and remain the fallback for when nothing is running yet.

  The install is discovered through Steam when -PPRoot is not given; the pipe itself is opt-in and
  needs a marker file `Mods\PPBridge\ppcli-enabled` beside the DLL.
#>
param(
    [Parameter(Position = 0, Mandatory = $true)]
    [ValidateSet('deploy', 'run', 'batch', 'connect', 'plan', 'index')]
    [string] $Command,

    [Parameter(Position = 1)] [string] $Arg1,
    [Parameter(Position = 2)] [string] $Arg2,

    # Which install to drive. Empty = discover it through Steam (paths.ps1); pass it explicitly when
    # you keep more than one install, which is the normal setup for automation.
    [string] $PPRoot = '',
    # Which per-SteamID profile that install writes. Empty = line 2 of ppcli-install.txt, else the
    # single profile directory under ...LocalLow\Snapshot Games Inc\Phoenix Point\Steam\.
    [string] $ProfileId = '',
    [int]    $TimeoutSeconds = 300,
    [int]    $InitTimeoutSeconds = 90,
    # How long ONE pipe frame may take to arrive. A verb answers in 17-60 ms and a cross-frame verb
    # answers `accepted` immediately, so 30 s is already enormous; it is a ceiling on a wedged game,
    # not a budget for a slow one.
    [int]    $PipeTimeoutSeconds = 30,
    # EMPTY = the default: any exception whose stack names a MOD frame while the client is waiting
    # means the run is already dead. Pass a regex to narrow it to one mod. See Get-LogFault in
    # waits.ps1 for why the signal is a MOD frame and not the word "Exception".
    [string] $FaultPattern = '',
    # Wait out the full budget anyway. For measuring a fault the fast-fail would otherwise cut short.
    [switch] $IgnoreLogFaults,
    # `deploy` only: write into an install other than the one `ppcli-install.txt` pins.
    [switch] $Force,
    # `deploy` only: stage the files even though that install has the game running.
    [switch] $AllowRunning,
    # Where `index` writes the def catalog and where `plan` resolves names from. A parameter only so
    # the offline tests can point at a fixture; nothing else has a reason to move it.
    [string] $CatalogDir = (Join-Path $PSScriptRoot 'catalog')
)

$ErrorActionPreference = 'Stop'
function Note([string] $m) { [Console]::Error.WriteLine($m) }
. (Join-Path $PSScriptRoot 'names.ps1')
. (Join-Path $PSScriptRoot 'paths.ps1')
. (Join-Path $PSScriptRoot 'waits.ps1')
. (Join-Path $PSScriptRoot 'index.ps1')

# One place decides whether a wait has already lost. Returns nothing, or throws naming the fault.
# Throttled to once every 2 s: a poll loop runs four times a second and the log it re-reads is tens
# of thousands of lines, so an unthrottled check would cost more than the wait it is shortening.
function Assert-NoLogFault($mark) {
    if ($IgnoreLogFaults) { return }
    if ($mark.next -and (Get-Date) -lt $mark.next) { return }
    $mark.next = (Get-Date).AddSeconds(2)
    $fault = Get-LogFault $mark $FaultPattern
    if (-not $fault) { return }
    throw ("DEAD RUN: the game logged an exception whose stack names " +
           ($FaultPattern ? "'$FaultPattern'" : 'a mod') + " while this client was " +
           "waiting, so the wait was abandoned instead of run to its full budget. Kill that game " +
           "process and relaunch it; pass -IgnoreLogFaults to wait anyway, or -FaultPattern to " +
           "change what counts. Log: $($mark.path)`n" + $fault)
}

function Invoke-Jobs([string] $jobsJson) {
    if (-not (Test-Path $exe))    { throw "No game executable at $exe" }
    if (-not (Test-Path $modDir)) { throw "PPBridge is not deployed: $modDir missing. Run '.\ppcli.ps1 deploy'." }
    $dll = Join-Path $modDir 'PPBridge.dll'
    if (-not (Test-Path $dll))    { throw "No PPBridge.dll in $modDir. Run '.\ppcli.ps1 deploy'." }

    # PREFLIGHT. The pipe is OPT-IN (PPBridgeMain.ArmFile): with no marker beside the DLL the mod
    # loads and does nothing, which from out here is indistinguishable from a crashed mod.
    $arm = Join-Path $modDir 'ppcli-enabled'
    if (-not (Test-Path $arm)) {
        throw ("REFUSED: PPBridge is not armed. Create the marker file and re-run: " +
               "New-Item -ItemType File '$arm'. Delete it again when you are done.")
    }

    # PREFLIGHT. The likeliest first-run failure by far is a deployed mod that was never switched on
    # in this install's profile: the game then launches perfectly, prints nothing, and the run reads
    # exactly like a crashed mod. Read-only - this file is never rewritten by hand, because
    # re-serialising it once shrank it 32991 -> 18996 B.
    # -Install: the pinned profile is line 1's profile, so it applies only when THIS install is line 1.
    if (-not $ProfileId) { $ProfileId = Find-PPProfileId -Install $PPRoot }
    $jopt = Join-Path $env:USERPROFILE "AppData\LocalLow\Snapshot Games Inc\Phoenix Point\Steam\$ProfileId\Options.jopt"
    if (-not (Test-Path $jopt)) { throw "REFUSED: no profile at $jopt - is -ProfileId ($ProfileId) right for ${PPRoot}?" }
    if (-not (Test-ModActivated $jopt 'com.morgott.PPBridge')) {
        throw ("REFUSED: 'com.morgott.PPBridge' is not in MOD_ACTIVATED in $jopt. " +
               "Launch $PPRoot once, enable PPBridge in the mod manager, quit, then re-run. " +
               'Nothing was launched and no file was written.')
    }

    # A Phoenix Point this script did not start is SOMEONE ELSE'S. Compare by executable PATH, not by
    # process name - the two installs exist precisely so they can run side by side - and never kill by
    # name: the only PID this run may stop is the one Start-Process hands back.
    $mine = (Get-Item $exe).FullName
    $busy = @(Get-CimInstance Win32_Process -Filter "Name='PhoenixPointWin64.exe'" |
              Where-Object { $_.ExecutablePath -and (Get-Item $_.ExecutablePath).FullName -eq $mine })
    if ($busy.Count -gt 0) {
        throw ("REFUSED: Phoenix Point is already running from $PPRoot (PID " + ($busy.ProcessId -join ', ') +
               '), started by someone other than this script. It is NOT killed.')
    }

    # Which build the session RAN is not which build is on disk. The mod stamps the SHA-1 of the DLL
    # it loaded; this is the same hash, computed here.
    $expected = (Get-FileHash -Algorithm SHA1 $dll).Hash.ToLower().Substring(0, 8)
    Note "expecting build=$expected"

    # The log name carries the install and this script's PID: a shared name lets a parallel run
    # truncate this one's evidence, and an empty log reads exactly like "the mod printed nothing".
    $logPath = Join-Path $env:TEMP ("ppcli-" + ($PPRoot -replace '[^A-Za-z0-9]', '') + "-$PID.log")
    if (Test-Path $logPath) { Remove-Item $logPath -Force }

    # Byte-exact restore. A mod that fails to load makes the game rewrite MOD_ACTIVATED EMPTY,
    # silently disabling every other mod too - which then looks like the harness breaking for an
    # unrelated reason.
    $joptBefore = [IO.File]::ReadAllBytes($jopt)

    Set-Content -Path $jobsPath -Value $jobsJson -Encoding utf8NoBOM
    $stamp = '(no init line)'; $done = $null; $game = $null
    try {
        # -mods turns PPModLoader on; -logFile keeps this run out of the shared LocalLow log.
        # -PassThru is load-bearing: $game.Id is the only handle Stop-Process may ever use.
        $game = Start-Process -FilePath $exe -ArgumentList '-mods', '-logFile', $logPath -PassThru
        Note "launched PID $($game.Id) (the only process this run may stop)"

        $start = Get-Date; $inited = $false
        # This log did not exist a moment ago, so the mark is 0 and every line in it belongs to
        # this run. A load that dies on a mod exception is caught here instead of at $TimeoutSeconds.
        $mark = New-LogMark $logPath
        while (((Get-Date) - $start).TotalSeconds -lt $TimeoutSeconds) {
            Start-Sleep -Seconds 3
            if ($game.HasExited) { Note 'the game exited before the DONE marker'; break }
            if (-not (Test-Path $logPath)) { continue }
            $mark.path = $logPath
            Assert-NoLogFault $mark
            if (-not $inited) {
                $line = Select-String -Path $logPath -Pattern 'PPBridge \d.*build=([0-9a-f]{8})' | Select-Object -First 1
                if ($line) { $inited = $true; $stamp = $line.Matches[0].Groups[1].Value }
                elseif (((Get-Date) - $start).TotalSeconds -gt $InitTimeoutSeconds) {
                    throw "PPBridge never initialised within ${InitTimeoutSeconds}s - is the game launching with -mods? (log: $logPath)"
                }
            }
            $d = Select-String -Path $logPath -Pattern 'PPCLI\|DONE\|(\d+)' | Select-Object -First 1
            if ($d) { $done = [int]$d.Matches[0].Groups[1].Value; break }
        }
    }
    finally {
        if ($game -and -not $game.HasExited) { Stop-Process -Id $game.Id -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2 }
        Remove-Item $jobsPath -Force -ErrorAction SilentlyContinue   # never fire on a later manual launch
        if (Test-Path $jopt) {
            $after = [IO.File]::ReadAllBytes($jopt)
            if (-not [Linq.Enumerable]::SequenceEqual($joptBefore, $after)) {
                [IO.File]::WriteAllBytes($jopt, $joptBefore)
                Note "restored $jopt byte-exact (the run changed it)"
            }
        }
    }

    if (-not (Test-Path $logPath)) { throw "no log was written to $logPath" }
    $stale = $stamp -ne $expected
    if ($stale) { Note "THE SESSION RAN build=$stamp, NOT the deployed $expected - every result below is a ghost" }
    else { Note "confirmed build=$stamp" }

    # Only PPCLI markers, never the log's own noise. Everything from 'PPCLI|' onward, so whatever
    # prefix Unity or the mod logger puts in front cannot shift the fields.
    $results = @()
    foreach ($hit in Select-String -Path $logPath -Pattern 'PPCLI\|' -Encoding utf8) {
        $text = $hit.Line.Substring($hit.Line.IndexOf('PPCLI|'))
        $parts = $text.Split('|', 3)
        if ($parts.Count -lt 3 -or $parts[1] -eq 'DONE') { continue }
        $payload = $null
        try { $payload = $parts[2] | ConvertFrom-Json } catch { $payload = @{ ok = $false; error = 'unparseable payload'; raw = $parts[2] } }
        $results += [ordered]@{ id = $parts[1]; result = $payload }
    }

    return [ordered]@{
        ok      = (-not $stale) -and ($null -ne $done)
        build   = $stamp
        stale   = $stale
        done    = $done
        log     = $logPath
        results = $results
    }
}

# ---------------------------------------------------------------- pipe transport (the fast path)

# A discovery file outlives a crash, so "the file exists" proves nothing. Neither does a live PID:
# Windows RECYCLES pids, so a dead game's number comes back as some unrelated process and the file
# survives the sweep, wins the pick and every verb then hangs in Connect() until it times out.
# The endpoint is real only if its pid is still the GAME of that install - matched by executable
# path, the way a running install is matched before a deploy.
function Test-EndpointAlive($ep) {
    $p = Get-Process -Id $ep.pid -ErrorAction SilentlyContinue
    if (-not $p -or $p.Name -ne 'PhoenixPointWin64') { return $null }
    if ($ep.install) {
        $exe = $null
        try { $exe = $p.Path } catch { }
        # No readable path (rare, another user's process) is not proof of a stale file: the name
        # already matched, so keep it rather than sweep a working endpoint.
        if ($exe -and ([IO.Path]::GetFullPath($exe) -ine
                       [IO.Path]::GetFullPath((Join-Path $ep.install.TrimEnd('\', '/') 'PhoenixPointWin64.exe')))) { return $null }
    }
    $p
}

function Get-Endpoint {
    $dir = Join-Path $env:LOCALAPPDATA 'ppcli\endpoints'
    if (-not (Test-Path $dir)) { throw "REFUSED: no endpoints at $dir - no game with PPBridge is running. Use 'run' to cold-launch one." }

    $live = @()
    foreach ($f in Get-ChildItem -Path $dir -Filter '*.json' -ErrorAction SilentlyContinue) {
        $ep = $null
        try { $ep = Get-Content -Raw $f.FullName | ConvertFrom-Json } catch { }
        $proc = $null
        if ($ep -and $ep.pid) { $proc = Test-EndpointAlive $ep }
        if ($proc) {
            # Newest game wins the tie below; two endpoints for one install means an older one leaked.
            $ep | Add-Member -NotePropertyName started -NotePropertyValue $proc.StartTime -Force
            $live += $ep; continue
        }
        Remove-Item $f.FullName -Force -ErrorAction SilentlyContinue
        Note "swept stale endpoint $($f.Name)"
    }
    if ($live.Count -eq 0) {
        throw ("REFUSED: no live PPBridge endpoint. Launch $PPRoot with -mods (or use 'run'), and check " +
               "that '$(Join-Path $PPRoot 'Mods\PPBridge\ppcli-enabled')' exists - the pipe is opt-in.")
    }

    # Installs can run side by side; -PPRoot decides which one this call means.
    $want = $PPRoot.TrimEnd('\', '/')
    $mine = @($live | Where-Object { $_.install -and $_.install.TrimEnd('\', '/') -ieq $want })
    if ($mine.Count -eq 0) {
        throw ("REFUSED: no endpoint for $PPRoot. Live: " + (($live | ForEach-Object { "$($_.install) (pid $($_.pid))" }) -join ', '))
    }
    if ($mine.Count -gt 1) {
        $mine = @($mine | Sort-Object started -Descending)
        Note "$($mine.Count) endpoints for $PPRoot, using the newest game (pid $($mine[0].pid))"
    }
    $mine[0]
}

# One request per connection: connect, one length-prefixed UTF-8 frame out, one back, close.
function Invoke-Pipe($ep, $body) {
    # Depth 32, not 12: a plan is a step list whose steps carry argument envelopes, and at depth 12
    # ConvertTo-Json silently stringifies the tail of it instead of failing.
    $json  = $body | ConvertTo-Json -Depth 32 -Compress
    $bytes = (New-Object Text.UTF8Encoding $false).GetBytes($json)
    if ($bytes.Length -gt 262144) { throw "request of $($bytes.Length) bytes exceeds the 262144 byte frame limit" }

    $client = New-Object IO.Pipes.NamedPipeClientStream '.', $ep.pipe, ([IO.Pipes.PipeDirection]::InOut)
    try {
        $client.Connect(5000)
        $client.Write([BitConverter]::GetBytes([int]$bytes.Length), 0, 4)
        $client.Write($bytes, 0, $bytes.Length)
        $client.Flush()

        # BOUNDED, both reads. A wedged game keeps this connection open and answers nothing.
        $ms = $PipeTimeoutSeconds * 1000
        $len = [BitConverter]::ToInt32((Read-Exact $client 4 $ms "pid $($ep.pid)"), 0)
        if ($len -le 0 -or $len -gt 262144) { throw "the server announced a $len byte frame" }
        # -NoEnumerate everywhere JSON comes back: PowerShell unrolls a one-element array into a
        # scalar, which silently turns a valid reply into a broken one (it already cost the batch
        # branch a false refusal).
        ConvertFrom-Json ((New-Object Text.UTF8Encoding $false).GetString((Read-Exact $client $len $ms "pid $($ep.pid)"))) -NoEnumerate
    }
    finally { $client.Dispose() }
}

# One verb over the pipe, including the poll a cross-frame verb (wait / snapshot / plan) needs.
# Keeping the poll here is what keeps the contract of exactly ONE JSON object on stdout instead of
# pushing the job protocol onto every caller.
function Invoke-Verb([string] $verb, $verbArgs, $ep) {
    if (-not $ep) {
        $ep = Get-Endpoint
        Note "pipe $($ep.pipe) (pid $($ep.pid), build=$($ep.build), $($ep.protocol))"
    }

    $req = [ordered]@{ token = $ep.token; id = 'c1'; verb = $verb }
    if ($null -ne $verbArgs) { $req.args = $verbArgs }
    $reply = Invoke-Pipe $ep $req

    if ($reply.status -eq 'accepted') {
        $jobId = $reply.jobId
        Note "job $jobId accepted, polling"
        $started  = Get-Date
        $deadline = $started.AddSeconds($TimeoutSeconds)
        # A job that is dead is not slow. The mark is taken BEFORE the first poll, so only what the
        # game logs while this client is actually waiting can end the wait.
        $mark = New-LogMark (Get-GameLogPath $ep.pid)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 250
            $reply = Invoke-Pipe $ep ([ordered]@{ token = $ep.token; id = 'c1'; verb = 'status'; args = @{ jobId = $jobId } })
            if ($reply.status -ne 'running') { break }
            Assert-NoLogFault $mark
        }
        # The client's own ceiling, and it must not answer with the last poll: "running" as a FINAL
        # answer reads like a result and is not one, and the job would still be holding whatever it
        # changed. Cancel it - which for a plan means its `finally` block runs - and say so.
        if ($reply.status -eq 'running') {
            $waited = [int]((Get-Date) - $started).TotalSeconds
            Note "job $jobId still running after ${waited}s - cancelling"
            $cancel = $null
            try { $cancel = Invoke-Pipe $ep ([ordered]@{ token = $ep.token; id = 'c1'; verb = 'cancel'; args = @{ jobId = $jobId } }) } catch { }
            $reply = [ordered]@{
                status    = 'timeout'
                jobId     = $jobId
                waitedSec = $waited
                cancelled = ($null -ne $cancel -and $cancel.status -in 'cancelling', 'done')
                error     = "the client gave up after ${waited}s (-TimeoutSeconds $TimeoutSeconds); the job was asked to cancel. " +
                            "A plan's own timeoutMs may be longer than this - poll 'connect status' with the jobId, or raise -TimeoutSeconds."
                last      = $cancel
            }
        }
    }
    $reply
}

# The one JSON object on stdout. Every caller that wants the answer instead of the printout uses
# Invoke-Verb directly, so the contract lives in exactly one place.
function Send-Verb([string] $verb, $verbArgs) {
    $reply = Invoke-Verb $verb $verbArgs $null
    $reply | ConvertTo-Json -Depth 32 -Compress
    # A REFUSAL MUST NOT EXIT 0. `items` with an out-of-range pageSize answers ok:false with no
    # `items` key at all - and `$r.result.items` off that is $null, which reads exactly like a page
    # that swept and found nothing. The exit code is what tells a bad argument from a real absence.
    if (($reply.status -ne 'done') -or
        ($null -ne $reply.result -and $null -ne $reply.result.PSObject.Properties['ok'] -and -not $reply.result.ok)) {
        $script:AnyRefusal = $true
    }
}

# Caller vars that name a def are normalised locally BEFORE the plan is sent; the plan JSON itself
# goes over the wire unchanged.
function Resolve-PlanVars($vars, [string[]] $Skip = @()) {
    if ($null -eq $vars) { return $vars }
    # Driven by names.ps1's own table, not by a second literal list: a var added there and forgotten
    # here would be sent to the game unresolved and would look like a bad def name.
    foreach ($name in $script:PPCLI_VarFamily.Keys) {
        if ($Skip -contains $name) { continue }
        $prop = $vars.PSObject.Properties[$name]
        if (-not $prop) { continue }
        $prop.Value = Resolve-DefValue -VarName $name -Value ([string]$prop.Value) -CatalogDir $CatalogDir
    }
    $vars
}

try {
# Inside the try so a discovery refusal still leaves exactly one JSON object on stdout.
if (-not $PPRoot) { $PPRoot = Find-PPInstall; Note "install: $PPRoot ($(Format-InstallOrigin (Get-PPPinnedInstall)))" }
$modDir   = Join-Path $PPRoot 'Mods\PPBridge'
$exe      = Join-Path $PPRoot 'PhoenixPointWin64.exe'
$jobsPath = Join-Path $modDir 'ppcli-jobs.json'

switch ($Command) {
    'deploy' {
        & (Join-Path $PSScriptRoot 'deploy.ps1') -PPRoot $PPRoot -Force:$Force -AllowRunning:$AllowRunning | ForEach-Object { Note $_ }
        [ordered]@{ ok = $true; deployed = (Join-Path $modDir 'PPBridge.dll') } | ConvertTo-Json -Compress
    }
    'run' {
        if (-not $Arg1) { throw "usage: ppcli.ps1 run <verb> [json-args]" }
        $args1 = $null
        # -NoEnumerate: a top-level JSON array of args would otherwise arrive as a scalar.
        if ($Arg2) { $args1 = ConvertFrom-Json $Arg2 -NoEnumerate }   # fails loudly here, not in the game
        $job = [ordered]@{ id = 'r1'; verb = $Arg1 }
        if ($null -ne $args1) { $job.args = $args1 }
        Invoke-Jobs (ConvertTo-Json @($job) -Depth 12 -Compress) | ConvertTo-Json -Depth 12 -Compress
    }
    'batch' {
        if (-not $Arg1) { throw "usage: ppcli.ps1 batch <file>" }
        if (-not (Test-Path $Arg1)) { throw "no batch file at $Arg1" }
        $text = Get-Content -Raw $Arg1
        # -NoEnumerate: without it a one-job array arrives as a scalar and a valid batch is refused.
        $parsed = ConvertFrom-Json $text -NoEnumerate       # refuse a malformed batch before launching
        if ($parsed -isnot [array]) { throw "$Arg1 must be a JSON ARRAY of {id,verb,args} objects" }
        Invoke-Jobs $text | ConvertTo-Json -Depth 12 -Compress
    }
    'connect' {
        if (-not $Arg1) { throw "usage: ppcli.ps1 connect <verb> [json-args]" }
        # N VERBS, ONE PROCESS. Not a game-side verb: the pipe is one request per connection either
        # way, and what cost four minutes for a 188-call enumeration was PowerShell start-up, not the
        # game (which answers in 17-60 ms). The endpoint is discovered ONCE and every request rides
        # the same token; `batch` is untouched because it cold-launches by design.
        if ($Arg1 -eq 'multi') {
            if (-not $Arg2) { throw "usage: ppcli.ps1 connect multi '<json array of {id,verb,args}>' | @requests.json | -" }
            $text = if ($Arg2 -eq '-') { [Console]::In.ReadToEnd() }
                    elseif ($Arg2.StartsWith('@')) {
                        $p = $Arg2.Substring(1)
                        if (-not (Test-Path $p)) { throw "no request file at $p" }
                        Get-Content -Raw $p
                    }
                    else { $Arg2 }
            # -NoEnumerate: a one-request array would otherwise arrive as a scalar and be refused.
            $reqs = ConvertFrom-Json $text -NoEnumerate -Depth 64
            if ($reqs -isnot [array]) { throw "connect multi takes a JSON ARRAY of {id, verb, args} objects" }
            # PREVALIDATE THE WHOLE ARRAY BEFORE ROW 1 IS SENT. The check used to live inside the
            # execution loop, so a typo in row 2 was discovered only AFTER row 1 had already changed
            # the game - and the thrown error then took row 1's result with it. This is sequential,
            # never transactional: once sending starts, a transport failure still leaves every
            # earlier row's side effects committed.
            $n = 0
            foreach ($r in $reqs) {
                $n++
                if ($r -isnot [psobject] -or $null -eq $r.PSObject.Properties['verb']) { throw "request $n is not an object with a 'verb'" }
                if (-not $r.verb) { throw "request $n has no 'verb'" }
            }

            $ep = Get-Endpoint
            Note "pipe $($ep.pipe) (pid $($ep.pid), build=$($ep.build), $($ep.protocol)) - $($reqs.Count) requests"

            $out = New-Object Collections.Generic.List[object]
            $failed = 0
            $n = 0
            foreach ($r in $reqs) {
                $n++
                $reply = Invoke-Verb $r.verb $r.args $ep
                # A refusal is a RESULT here, never an abort: an enumeration whose row 40 fails still
                # wants rows 41-188, and the per-row `ok` says which ones to distrust.
                $bad = ($reply.status -ne 'done') -or
                       ($null -ne $reply.result -and $null -ne $reply.result.PSObject.Properties['ok'] -and -not $reply.result.ok)
                if ($bad) { $failed++; $script:AnyRefusal = $true }
                $out.Add([ordered]@{ id = ($r.id ? $r.id : "m$n"); ok = (-not $bad); reply = $reply })
            }
            [ordered]@{ ok = ($failed -eq 0); count = $out.Count; failed = $failed; results = $out } |
                ConvertTo-Json -Depth 32 -Compress
        }
        else {
            $args1 = $null
            # -NoEnumerate: a top-level JSON array of args would otherwise arrive as a scalar.
            if ($Arg2) { $args1 = ConvertFrom-Json $Arg2 -NoEnumerate }   # fails loudly here, not in the game
            Send-Verb $Arg1 $args1
        }
    }
    'plan' {
        if (-not $Arg1) { throw "usage: ppcli.ps1 plan <plan-file.json> ['{""var"":value,...}']" }
        if (-not (Test-Path $Arg1)) { throw "no plan file at $Arg1" }
        # Parsed HERE so a malformed plan is a local error with a line number, not a refusal from
        # inside the game five seconds later.
        $planObj = ConvertFrom-Json (Get-Content -Raw $Arg1) -NoEnumerate -Depth 64
        if (-not $planObj.steps) { throw "$Arg1 has no 'steps' array" }
        # THE CLIENT MUST NOT BE THE SHORTER CLOCK. -TimeoutSeconds defaults to 300, and six shipped
        # plans declare a longer timeoutMs than that (start-campaign 900 s, build-mission and
        # start-mission 600, load-mission/situation/weapon-test 540) - so the very first thing a
        # newcomer does, run a shipped plan, used to be cancelled mid-run and reported as a timeout on
        # a perfectly healthy game. Derived rather than raised to a bigger constant, because a constant
        # rots the moment a plan changes. An EXPLICIT -TimeoutSeconds still wins: that is the caller
        # deliberately being the shorter clock.
        if (-not $PSBoundParameters.ContainsKey('TimeoutSeconds') -and $planObj.timeoutMs) {
            # +60 s so the plan's own deadline fires first and its `finally` runs - a client cancel
            # gets the same cleanup, but the plan's own timeout says which step it died on.
            $want = [int]($planObj.timeoutMs / 1000) + 60
            if ($want -gt $TimeoutSeconds) { $TimeoutSeconds = $want; Note "client ceiling raised to ${TimeoutSeconds}s for this plan's own $($planObj.timeoutMs) ms deadline" }
        }
        $body = [ordered]@{ plan = $planObj }
        # Caller vars override the plan file's own defaults - that is what parameterises a stored
        # plan without editing it.
        $callerVars = $null
        if ($Arg2) { $callerVars = ConvertFrom-Json $Arg2 -NoEnumerate }
        # A plan file's OWN defaults name defs too (unlock-research.json defaults researchId to a def
        # NAME, which GetResearchById can never match), so they go through the same resolution. Only
        # the ones the caller did not override: a default the caller is replacing must not refuse.
        if ($planObj.vars) {
            $overridden = @(if ($callerVars) { $callerVars.PSObject.Properties.Name } else { @() })
            Resolve-PlanVars $planObj.vars $overridden | Out-Null
        }
        if ($callerVars) { $body.vars = Resolve-PlanVars $callerVars }
        Send-Verb 'plan' $body
    }
    'index' {
        # Pages `find {all:true}` against an ALREADY-RUNNING game and writes the two catalog files.
        # One-time (per game build); everything after it is offline.
        $ep = Get-Endpoint
        Note "pipe $($ep.pipe) (pid $($ep.pid), build=$($ep.build), $($ep.protocol))"

        # Every def, or a refusal. The paging and its three snapshot-integrity refusals live in
        # index.ps1 so tests\index.tests.ps1 can drive them with no game.
        $paged = Get-AllDefs $ep
        $defs  = $paged.defs
        $pages = $paged.pages

        # ResearchDef.Id (ResearchDef.cs:33) is what GetResearchById matches (Research.cs:763-765,
        # ResearchElement.cs:221) and it is NOT the def name, so research rows carry it. The guid is
        # read here and thrown away: DefRepository.GetDef(guid) (DefRepository.cs:70) is the only way
        # to a handle, and a plan gets its own guid from a live `find` anyway.
        $rows = New-Object Collections.Generic.List[string]
        $research = 0
        $researchRows = 0
        foreach ($d in $defs) {
            $row = [ordered]@{ f = (Get-DefFamily $d.type); n = $d.name; t = $d.type }
            # 85% of the repository is family 'other' and NO plan var can ever resolve it - it is 1.6 MB
            # of committed rows that only slow the scan down. The named families all feed something:
            # actor/item plans, research -> unlock-research, status -> apply_status,
            # mission-type -> create_mission, map-plot -> mission loading.
            if ($row.f -eq 'other') { continue }
            if ($row.f -eq 'research') { $researchRows++ }
            if ($row.f -eq 'research' -and $d.guid) {
                $got = Invoke-Verb 'call' ([ordered]@{ op = 'invoke'; target = '@defs'; member = 'GetDef'; args = @($d.guid) }) $ep
                if ($got.status -eq 'done' -and $got.result.ok -and $got.result.value.h) {
                    $id = Invoke-Verb 'call' ([ordered]@{ op = 'get'; target = $got.result.value.h; member = 'Id' }) $ep
                    if ($id.status -eq 'done' -and $id.result.ok -and $id.result.value) {
                        $row.id = [string]$id.result.value
                        $research++
                    }
                }
                if (-not $row.Contains('id')) { Note "no ResearchDef.Id for $($d.name) - the row ships without one" }
            }
            $rows.Add(($row | ConvertTo-Json -Compress))
        }

        $gameVersion = $null
        $ver = Invoke-Verb 'call' ([ordered]@{ op = 'get'; type = 'UnityEngine.Application'; member = 'version' }) $ep
        if ($ver.status -eq 'done' -and $ver.result.ok) { $gameVersion = [string]$ver.result.value }

        # A research row with no Id is a row unlock-research can never use: names.ps1 would hand the
        # plan a def NAME and GetResearchById would never match it. One failed GetDef mid-run used to
        # drop ids silently, so the coverage is asserted here and recorded in meta.json.
        if ($research -ne $researchRows) {
            throw ("REFUSED: only $research of $researchRows research defs yielded a ResearchDef.Id " +
                   '(see the per-def notes above). Nothing was written; re-run on a settled game.')
        }

        New-Item -ItemType Directory -Force -Path $CatalogDir | Out-Null
        $meta = [ordered]@{
            gameVersion  = $gameVersion
            build        = $ep.build
            generated    = (Get-Date).ToUniversalTime().ToString('o')
            rows         = $rows.Count
            researchRows = $researchRows
            researchIds  = $research
            defsScanned  = $defs.Count
        }
        # ATOMIC, and in this ORDER. [IO.File]::Move(overwrite) is MoveFileEx(REPLACE_EXISTING) - one
        # operation - where Move-Item -Force deletes the target first and leaves NO catalog at all if
        # the process dies inside that window. meta.json goes LAST because it is the file that
        # describes the other one; a death between the two leaves meta describing the OLD row count,
        # which Assert-CatalogIntact (names.ps1) then reports as a stale catalog instead of trusting it.
        foreach ($pair in @(
            @{ path = (Join-Path $CatalogDir 'defs.ndjson'); text = ($rows -join "`n") + "`n" },
            @{ path = (Join-Path $CatalogDir 'meta.json');   text = ($meta | ConvertTo-Json) })) {
            $tmp = $pair.path + '.tmp'
            Set-Content -Path $tmp -Value $pair.text -Encoding utf8NoBOM -NoNewline
            [IO.File]::Move($tmp, $pair.path, $true)
        }

        [ordered]@{ ok = $true; rows = $rows.Count; scanned = $defs.Count; pages = $pages
                    research = $research; catalog = (Join-Path $CatalogDir 'defs.ndjson'); build = $ep.build } |
            ConvertTo-Json -Compress
    }
}
# EVERY verb ends with an exit code, not just the ones that happen to run a native command. `index`
# used to leave $LASTEXITCODE at whatever the caller's previous command set, so a wrapper's
# `if ($LASTEXITCODE -ne 0)` read a stale value and called a good index a failure - or worse.
# A verb that PRINTED a refusal exits non-zero too: the JSON says ok:false, and a caller that reads
# one field off it would otherwise never learn the answer was a refusal and not a finding.
exit ($script:AnyRefusal ? 1 : 0)
}
catch {
    # THE CONTRACT IS ONE JSON OBJECT, ALWAYS - a refusal included. A bare throw here left stdout
    # completely empty, so `ppcli.ps1 ... | ConvertFrom-Json` gave a caller nothing to read and the
    # reason was only ever on stderr. The exit code still says it failed.
    [ordered]@{ ok = $false; error = $_.Exception.Message } | ConvertTo-Json -Compress
    Note $_.Exception.Message
    exit 1
}
