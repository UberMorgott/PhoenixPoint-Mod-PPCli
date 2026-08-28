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
    # Which per-SteamID profile that install writes. Empty = the single profile directory under
    # ...LocalLow\Snapshot Games Inc\Phoenix Point\Steam\, if there is exactly one.
    [string] $ProfileId = '',
    [int]    $TimeoutSeconds = 300,
    [int]    $InitTimeoutSeconds = 90,
    # Where `index` writes the def catalog and where `plan` resolves names from. A parameter only so
    # the offline tests can point at a fixture; nothing else has a reason to move it.
    [string] $CatalogDir = (Join-Path $PSScriptRoot 'catalog')
)

$ErrorActionPreference = 'Stop'
function Note([string] $m) { [Console]::Error.WriteLine($m) }
. (Join-Path $PSScriptRoot 'names.ps1')
. (Join-Path $PSScriptRoot 'paths.ps1')

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
    if (-not $ProfileId) { $ProfileId = Find-PPProfileId }
    $jopt = Join-Path $env:USERPROFILE "AppData\LocalLow\Snapshot Games Inc\Phoenix Point\Steam\$ProfileId\Options.jopt"
    if (-not (Test-Path $jopt)) { throw "REFUSED: no profile at $jopt - is -ProfileId ($ProfileId) right for $PPRoot?" }
    if ((Get-Content -Raw $jopt) -notlike '*com.morgott.PPBridge*') {
        throw ("REFUSED: 'com.morgott.PPBridge' is not activated in $jopt. " +
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
    # silently disabling every other mod too (headless-harness.md:67-69) - which then looks like the
    # harness breaking for an unrelated reason.
    $joptBefore = [IO.File]::ReadAllBytes($jopt)

    Set-Content -Path $jobsPath -Value $jobsJson -Encoding utf8NoBOM
    $stamp = '(no init line)'; $done = $null; $game = $null
    try {
        # -mods turns PPModLoader on; -logFile keeps this run out of the shared LocalLow log.
        # -PassThru is load-bearing: $game.Id is the only handle Stop-Process may ever use.
        $game = Start-Process -FilePath $exe -ArgumentList '-mods', '-logFile', $logPath -PassThru
        Note "launched PID $($game.Id) (the only process this run may stop)"

        $start = Get-Date; $inited = $false
        while (((Get-Date) - $start).TotalSeconds -lt $TimeoutSeconds) {
            Start-Sleep -Seconds 3
            if ($game.HasExited) { Note 'the game exited before the DONE marker'; break }
            if (-not (Test-Path $logPath)) { continue }
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

# A discovery file outlives a crash, so "the file exists" proves nothing. The PID does: if the
# process is gone the file is swept here, because the next client would otherwise be sent at a pipe
# nobody is listening on and would sit in Connect() until it timed out.
function Get-Endpoint {
    $dir = Join-Path $env:LOCALAPPDATA 'ppcli\endpoints'
    if (-not (Test-Path $dir)) { throw "REFUSED: no endpoints at $dir - no game with PPBridge is running. Use 'run' to cold-launch one." }

    $live = @()
    foreach ($f in Get-ChildItem -Path $dir -Filter '*.json' -ErrorAction SilentlyContinue) {
        $ep = $null
        try { $ep = Get-Content -Raw $f.FullName | ConvertFrom-Json } catch { }
        if ($ep -and $ep.pid -and (Get-Process -Id $ep.pid -ErrorAction SilentlyContinue)) { $live += $ep; continue }
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
    if ($mine.Count -gt 1) { Note "$($mine.Count) endpoints for $PPRoot, using pid $($mine[0].pid)" }
    $mine[0]
}

function Read-Exact([IO.Stream] $s, [int] $count) {
    $buf = New-Object byte[] $count
    $got = 0
    while ($got -lt $count) {
        $n = $s.Read($buf, $got, $count - $got)
        if ($n -le 0) { throw "the pipe closed after $got of $count bytes" }
        $got += $n
    }
    ,$buf
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

        $len = [BitConverter]::ToInt32((Read-Exact $client 4), 0)
        if ($len -le 0 -or $len -gt 262144) { throw "the server announced a $len byte frame" }
        # -NoEnumerate everywhere JSON comes back: PowerShell unrolls a one-element array into a
        # scalar, which silently turns a valid reply into a broken one (it already cost the batch
        # branch a false refusal).
        ConvertFrom-Json ((New-Object Text.UTF8Encoding $false).GetString((Read-Exact $client $len))) -NoEnumerate
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
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 250
            $reply = Invoke-Pipe $ep ([ordered]@{ token = $ep.token; id = 'c1'; verb = 'status'; args = @{ jobId = $jobId } })
            if ($reply.status -ne 'running') { break }
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
    (Invoke-Verb $verb $verbArgs $null) | ConvertTo-Json -Depth 32 -Compress
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
if (-not $PPRoot) { $PPRoot = Find-PPInstall; Note "install: $PPRoot (discovered)" }
$modDir   = Join-Path $PPRoot 'Mods\PPBridge'
$exe      = Join-Path $PPRoot 'PhoenixPointWin64.exe'
$jobsPath = Join-Path $modDir 'ppcli-jobs.json'

switch ($Command) {
    'deploy' {
        & (Join-Path $PSScriptRoot 'deploy.ps1') -PPRoot $PPRoot | ForEach-Object { Note $_ }
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
        $args1 = $null
        # -NoEnumerate: a top-level JSON array of args would otherwise arrive as a scalar.
        if ($Arg2) { $args1 = ConvertFrom-Json $Arg2 -NoEnumerate }   # fails loudly here, not in the game
        Send-Verb $Arg1 $args1
    }
    'plan' {
        if (-not $Arg1) { throw "usage: ppcli.ps1 plan <plan-file.json> ['{""var"":value,...}']" }
        if (-not (Test-Path $Arg1)) { throw "no plan file at $Arg1" }
        # Parsed HERE so a malformed plan is a local error with a line number, not a refusal from
        # inside the game five seconds later.
        $planObj = ConvertFrom-Json (Get-Content -Raw $Arg1) -NoEnumerate -Depth 64
        if (-not $planObj.steps) { throw "$Arg1 has no 'steps' array" }
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

        # 200 rows is `find`'s own page ceiling and projects to roughly 30 KB - well inside the
        # 64 KB reflection cap and the 256 KB frame limit, both of which refuse rather than truncate.
        $pageSize = 200
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
            # SNAPSHOT INTEGRITY. Every page re-enumerates and re-sorts the repository independently,
            # so a def loading (or a scene transition) between two pages silently shifts every row
            # after it - skipping some and duplicating others. A moving `total` is that happening,
            # and a repeated (name,guid,type) is that having already happened. Refuse; a catalog with
            # holes in it refuses real names later and says nothing about why.
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
        }
        if ($defs.Count -ne $total) {
            throw "REFUSED: collected $($defs.Count) defs but the repository reports $total. Nothing was written."
        }

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

        [ordered]@{ ok = $true; rows = $rows.Count; scanned = $defs.Count; pages = ($page + 1)
                    research = $research; catalog = (Join-Path $CatalogDir 'defs.ndjson'); build = $ep.build } |
            ConvertTo-Json -Compress
    }
}
}
catch {
    # THE CONTRACT IS ONE JSON OBJECT, ALWAYS - a refusal included. A bare throw here left stdout
    # completely empty, so `ppcli.ps1 ... | ConvertFrom-Json` gave a caller nothing to read and the
    # reason was only ever on stderr. The exit code still says it failed.
    [ordered]@{ ok = $false; error = $_.Exception.Message } | ConvertTo-Json -Compress
    Note $_.Exception.Message
    exit 1
}
