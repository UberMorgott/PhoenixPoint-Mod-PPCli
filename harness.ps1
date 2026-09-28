<#
  `ppcli.ps1 test` - the mod test harness. A test case is a PLAN (steps / finally / vars / timeoutMs)
  plus an `expect` block, in a file named *.test.json. The harness runs each case against a live game
  over the pipe and answers ONE compact summary: {ok, passed, failed, ms, cases:[{name, ok, ms, fail?}]}.

  A file of its own so tests\harness.tests.ps1 can drive every function here with a FAKE invoker and
  no game: the transport arrives as a scriptblock `$Invoke(verb, args) -> {status, result}`.

  expect (all optional; an empty expect = "the plan finished ok"):
    ok       true (default) | false  - false: the plan must FAIL; `code` / `step` then narrow how
    results  [{var|output, path?, eq|ne|gt|gte|lt|lte|match|exists|truthy}]  - on a step's saved result
             (`var` = a step's `save` name) or on one of the plan's own `output` fields
    log      {has:[rx], hasNot:[rx], level?}  - Unity log rows written DURING the case
    events   [{target|type, event, match?, min?, max?}]  - harness subscribes before the steps
             [{var, match?, min?, max?}]                 - a subscription a step saved
    traces   [{type, method, sig?, assembly?, force?, keepScene?, match?, min?, max?}] - harness traces
             around the steps; [{var, ...}] = a trace a step started and saved
#>

# The operators `results` understands, in the order a failure message names them.
$script:HarnessOps = @('eq', 'ne', 'gt', 'gte', 'lt', 'lte', 'match', 'exists', 'truthy')

function Get-TestFiles([string] $Path) {
    if (-not (Test-Path $Path)) { throw "no test file or folder at $Path" }
    $item = Get-Item $Path
    if (-not $item.PSIsContainer) { return @($item.FullName) }
    $files = @(Get-ChildItem -Path $Path -Recurse -File -Filter '*.test.json' | Sort-Object FullName | ForEach-Object FullName)
    if ($files.Count -eq 0) { throw "no *.test.json under $Path" }
    $files
}

# A plan reference: "path" or {plan:"path", vars?, save?}. Path is relative to the test file, or
# "@plans/<file>" = PPCLI's own shipped plans (so a mod repo needs no path to PPCLI).
function Resolve-PlanRef($Ref, [string] $BaseDir, [string] $What) {
    $path = $null; $vars = $null; $save = $null
    if ($Ref -is [string]) { $path = $Ref }
    elseif ($Ref -is [psobject] -and $Ref.plan -is [string]) { $path = $Ref.plan; $vars = $Ref.vars; $save = $Ref.save }
    else { throw "$What must be a plan path or {plan:path, vars?, save?}" }
    $full = if ($path.StartsWith('@plans/') -or $path.StartsWith('@plans\')) { Join-Path (Join-Path $PSScriptRoot 'plans') $path.Substring(7) }
            else { [IO.Path]::GetFullPath((Join-Path $BaseDir $path)) }
    if (-not (Test-Path $full)) { throw "$What names a plan that does not exist: $full" }
    $obj = $null
    try { $obj = ConvertFrom-Json (Get-Content -Raw $full) -NoEnumerate -Depth 64 } catch { throw "$What $full is not valid JSON: $($_.Exception.Message)" }
    if (-not $obj.steps) { throw "$What $full has no 'steps' array" }
    [pscustomobject]@{ file = $full; plan = $obj; vars = $vars; save = $save }
}

# One case from one file. Refuses early and locally - a typo in `expect` must not cost a game run.
# The BODY (what `expect` observes) is inline `steps` or `plan` (a reference); `setup` / `teardown`
# are plan references run before the observers attach / after they are read. A setup's `save` makes
# its plan OUTPUT a var "${NAME.field}" for later setups, the body's vars and teardown.
function Read-TestCase([string] $File) {
    $obj = $null
    try { $obj = ConvertFrom-Json (Get-Content -Raw $File) -NoEnumerate -Depth 64 }
    catch { throw "$File is not valid JSON: $($_.Exception.Message)" }
    if ($obj -isnot [psobject]) { throw "$File is not a JSON object" }
    $dir = Split-Path -Parent ([IO.Path]::GetFullPath($File))
    $bodyVars = $null
    if ($obj.PSObject.Properties['plan'] -and $obj.plan) {
        if ($obj.steps) { throw "$File has both 'steps' and 'plan' - the body is one or the other" }
        $ref = Resolve-PlanRef $obj.plan $dir "$File plan"
        $plan = $ref.plan; $bodyVars = $ref.vars
    }
    elseif ($obj.steps) { $plan = $obj }
    else { throw "$File has no 'steps' array and no 'plan' (a test case is a plan plus 'expect')" }
    $setup = @(foreach ($s in @($obj.setup)) { if ($null -ne $s) { Resolve-PlanRef $s $dir "$File setup" } })
    $teardown = @(foreach ($s in @($obj.teardown)) { if ($null -ne $s) { Resolve-PlanRef $s $dir "$File teardown" } })
    $name = if ($obj.PSObject.Properties['name'] -and $obj.name) { [string]$obj.name } else { [IO.Path]::GetFileName($File) -replace '\.test\.json$', '' }
    $expect = if ($obj.PSObject.Properties['expect']) { $obj.expect } else { [pscustomobject]@{} }
    foreach ($p in $expect.PSObject.Properties.Name) {
        if ($p -notin 'ok', 'code', 'step', 'results', 'log', 'events', 'traces') {
            throw "$File expect has unknown key '$p' (known: ok, code, step, results, log, events, traces)"
        }
    }
    foreach ($r in @($expect.results)) {
        if ($null -eq $r) { continue }
        if (-not $r.var -and -not $r.output) { throw "$File expect.results entry needs 'var' (a step's save) or 'output'" }
        $ops = @($r.PSObject.Properties.Name | Where-Object { $_ -in $script:HarnessOps })
        if ($ops.Count -ne 1) { throw "$File expect.results entry for '$($r.var)$($r.output)' needs exactly one of: $($script:HarnessOps -join ', ')" }
    }
    foreach ($e in @($expect.events)) {
        if ($null -eq $e) { continue }
        if (-not $e.var -and -not $e.event) { throw "$File expect.events entry needs 'event' (+ target|type) or 'var'" }
    }
    foreach ($t in @($expect.traces)) {
        if ($null -eq $t) { continue }
        if (-not $t.var -and -not ($t.type -and $t.method)) { throw "$File expect.traces entry needs 'type' + 'method', or 'var'" }
    }
    [pscustomobject]@{ name = $name; file = $File; plan = $plan; vars = $bodyVars; setup = $setup; teardown = $teardown; expect = $expect }
}

# "${NAME.path}" (whole string) -> that value from the saved setup outputs; embedded -> text.
function Expand-CaseVars($Vars, [hashtable] $Ctx) {
    if ($null -eq $Vars) { return $null }
    $out = [ordered]@{}
    foreach ($p in $Vars.PSObject.Properties) {
        $v = $p.Value
        if ($v -is [string]) {
            $m = [regex]::Match($v, '^\$\{([A-Za-z0-9_]+)((?:\.[^}]*)?)\}$')
            if ($m.Success -and $Ctx.ContainsKey($m.Groups[1].Value)) {
                $found = $false
                $v = Get-JsonPath $Ctx[$m.Groups[1].Value] ($m.Groups[2].Value.TrimStart('.')) ([ref]$found)
                if (-not $found) { throw "var $($p.Name) = '$($p.Value)': no such field in the saved setup output" }
            }
            else {
                $v = [regex]::Replace($v, '\$\{([A-Za-z0-9_]+)((?:\.[^}]*)?)\}', {
                    param($mm)
                    $n = $mm.Groups[1].Value
                    if (-not $Ctx.ContainsKey($n)) { return $mm.Value }      # a plan var - the game resolves it
                    $f = $false
                    [string](Get-JsonPath $Ctx[$n] ($mm.Groups[2].Value.TrimStart('.')) ([ref]$f))
                })
            }
        }
        $out[$p.Name] = $v
    }
    [pscustomobject]$out
}

function Merge-Vars($A, $B) {
    if ($null -eq $A) { return $B }
    if ($null -eq $B) { return $A }
    $m = [ordered]@{}
    foreach ($p in $A.PSObject.Properties) { $m[$p.Name] = $p.Value }
    foreach ($p in $B.PSObject.Properties) { $m[$p.Name] = $p.Value }
    [pscustomobject]$m
}
# Every `save` name in a step tree (repeat bodies and finally included).
function Get-SaveNames($steps) {
    $names = New-Object Collections.Generic.List[string]
    foreach ($s in @($steps)) {
        if ($null -eq $s -or $s -isnot [psobject]) { continue }
        if ($s.PSObject.Properties['save'] -and $s.save) { $names.Add([string]$s.save) }
        if ($s.verb -eq 'repeat' -and $s.args -and $s.args.steps) { foreach ($n in (Get-SaveNames $s.args.steps)) { $names.Add($n) } }
    }
    $names
}

# The plan as sent: the case's own plan, its `output` extended with every saved var an assertion
# NAMES (results / events / traces `var`) under `$v:NAME` - asserted HERE on the full step DTO, and
# only those, so a 40-step case does not ship 40 DTOs back. A var no step saves is refused locally.
function New-CasePlan($Case) {
    $plan = $Case.plan | ConvertTo-Json -Depth 64 | ConvertFrom-Json -Depth 64
    foreach ($k in 'name', 'expect', 'setup', 'teardown') { $plan.PSObject.Properties.Remove($k) }
    $out = [ordered]@{}
    if ($plan.PSObject.Properties['output'] -and $plan.output) {
        foreach ($p in $plan.output.PSObject.Properties) { $out[$p.Name] = $p.Value }
    }
    $saves = @(@(Get-SaveNames $plan.steps) + @(Get-SaveNames $plan.finally) | Select-Object -Unique)
    $x = $Case.expect
    $wanted = @(@($x.results) + @($x.events) + @($x.traces) | Where-Object { $_ -and $_.var } | ForEach-Object { [string]$_.var } | Select-Object -Unique)
    foreach ($n in $wanted) {
        if ($saves -notcontains $n) { throw "$($Case.file): expect names var '$n' but no step saves it (saves: $($saves -join ', '))" }
        $out["`$v:$n"] = '${' + $n + '}'
    }
    if ($out.Count -gt 0) { $plan | Add-Member -NotePropertyName output -NotePropertyValue ([pscustomobject]$out) -Force }
    $plan
}

# 'value.items[0].h' off a parsed JSON object. $found says whether the path existed at all.
function Get-JsonPath($Obj, [string] $Path, [ref] $Found) {
    $Found.Value = $true
    if (-not $Path) { return $Obj }
    $cur = $Obj
    foreach ($seg in ($Path -split '\.')) {
        $m = [regex]::Match($seg, '^([^\[]*)((\[\d+\])*)$')
        $name = $m.Groups[1].Value
        if ($name) {
            if ($null -eq $cur -or $cur -isnot [psobject] -or -not $cur.PSObject.Properties[$name]) { $Found.Value = $false; return $null }
            $cur = $cur.$name
        }
        foreach ($ix in [regex]::Matches($m.Groups[2].Value, '\[(\d+)\]')) {
            $i = [int]$ix.Groups[1].Value
            $arr = @($cur)
            if ($null -eq $cur -or $i -ge $arr.Count) { $Found.Value = $false; return $null }
            $cur = $arr[$i]
        }
    }
    $cur
}

function ConvertTo-Canon($v) { if ($null -eq $v) { 'null' } else { ConvertTo-Json $v -Depth 32 -Compress } }

function Test-Truthy($v) {
    if ($null -eq $v) { return $false }
    if ($v -is [bool]) { return $v }
    if ($v -is [string]) { return $v.Length -gt 0 }
    if ($v -is [ValueType]) { return [double]$v -ne 0 }
    $true
}

# One `results` entry against the plan output. Returns $null (passed) or the failure text.
function Test-ResultExpect($Entry, $Output) {
    $key = if ($Entry.var) { "`$v:$($Entry.var)" } else { [string]$Entry.output }
    $label = if ($Entry.var) { "var $($Entry.var)" } else { "output $($Entry.output)" }
    if ($Entry.path) { $label += ".$($Entry.path)" }
    if ($null -eq $Output -or -not $Output.PSObject.Properties[$key]) { return "$label : not in the plan output (was the step skipped, or the save misspelled?)" }
    $found = $false
    $v = Get-JsonPath $Output.$key $Entry.path ([ref]$found)
    $op = @($Entry.PSObject.Properties.Name | Where-Object { $_ -in $script:HarnessOps })[0]
    $want = $Entry.$op
    if ($op -eq 'exists') { return ($found -eq [bool]$want) ? $null : "$label : exists=$found, wanted $want" }
    if (-not $found) { return "$label : path not found in $(ConvertTo-Canon $Output.$key)" }
    $got = ConvertTo-Canon $v
    switch ($op) {
        'eq'     { if ($got -ceq (ConvertTo-Canon $want)) { return $null } }
        'ne'     { if ($got -cne (ConvertTo-Canon $want)) { return $null } }
        'truthy' { if ((Test-Truthy $v) -eq [bool]$want) { return $null } }
        'match'  { if ([regex]::IsMatch(($v -is [string] ? $v : $got), [string]$want)) { return $null } }
        default {
            $n = 0.0
            if (-not [double]::TryParse([string]$v, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$n)) { return "$label : $got is not a number (op $op)" }
            $w = [double]$want
            $ok = switch ($op) { 'gt' { $n -gt $w } 'gte' { $n -ge $w } 'lt' { $n -lt $w } 'lte' { $n -le $w } }
            if ($ok) { return $null }
        }
    }
    "$label : got $got, wanted $op $(ConvertTo-Canon $want)"
}

function Test-CountExpect([string] $Label, [int] $Count, $Entry) {
    $min = if ($null -ne $Entry.min) { [int]$Entry.min } else { 1 }
    if ($Count -lt $min) { return "$Label : $Count hit(s), wanted min $min" }
    if ($null -ne $Entry.max -and $Count -gt [int]$Entry.max) { return "$Label : $Count hit(s), wanted max $($Entry.max)" }
    $null
}

# Every assertion of one case against what the run collected. Pure - the offline test's core.
#   $Run = {plan (the plan verb's result DTO), log (rows[] or $null), logDropped, events: {label->count}, traces: {label->count}, errors[]}
function Test-CaseExpect($Case, $Run) {
    $fail = New-Object Collections.Generic.List[string]
    foreach ($e in @($Run.errors)) { if ($e) { $fail.Add($e) } }
    $x = $Case.expect
    $plan = $Run.plan
    $wantOk = if ($x.PSObject.Properties['ok']) { [bool]$x.ok } else { $true }
    if ($null -eq $plan) { if ($fail.Count -eq 0) { $fail.Add('plan: no result') }; return $fail }
    if ([bool]$plan.ok -ne $wantOk) {
        if ($wantOk) { $fail.Add("plan: failed at step '$($plan.step)' ($($plan.code)): $($plan.error)") }
        else { $fail.Add('plan: finished ok, the case expects it to FAIL') }
    }
    if (-not $wantOk -and -not [bool]$plan.ok) {
        if ($x.code -and $plan.code -ne $x.code) { $fail.Add("plan: failed with code '$($plan.code)', wanted '$($x.code)'") }
        if ($x.step -and $plan.step -ne $x.step) { $fail.Add("plan: failed at step '$($plan.step)', wanted '$($x.step)'") }
    }
    if ([bool]$plan.ok) { foreach ($r in @($x.results)) { if ($r) { $f = Test-ResultExpect $r $plan.output; if ($f) { $fail.Add($f) } } } }
    if ($x.log) {
        $rows = @($Run.log)
        foreach ($rx in @($x.log.has)) {
            if (-not $rx) { continue }
            if (-not ($rows | Where-Object { [regex]::IsMatch([string]$_.m, $rx) })) { $fail.Add("log : no row matched has '$rx' ($($rows.Count) rows scanned)") }
        }
        foreach ($rx in @($x.log.hasNot)) {
            if (-not $rx) { continue }
            # Absence cannot be proven from a ring that overwrote rows it never handed over.
            if ($Run.logDropped) { $fail.Add("log : $($Run.logDropped) row(s) were overwritten before they were read - hasNot '$rx' cannot be proven"); continue }
            $hit = @($rows | Where-Object { [regex]::IsMatch([string]$_.m, $rx) }) | Select-Object -First 1
            if ($hit) { $m = [string]$hit.m; if ($m.Length -gt 200) { $m = $m.Substring(0, 200) + '~' }; $fail.Add("log : hasNot '$rx' matched row $($hit.s): $m") }
        }
    }
    $i = 0
    foreach ($e in @($x.events)) { if (-not $e) { continue }; $label = "event[$i] $(Get-ExpectLabel $e 'event')"; $f = Test-CountExpect $label ([int]$Run.events[$i]) $e; if ($f) { $fail.Add($f) }; $i++ }
    $i = 0
    foreach ($t in @($x.traces)) { if (-not $t) { continue }; $label = "trace[$i] $(Get-ExpectLabel $t 'trace')"; $f = Test-CountExpect $label ([int]$Run.traces[$i]) $t; if ($f) { $fail.Add($f) }; $i++ }
    $fail
}

function Get-ExpectLabel($Entry, [string] $Kind) {
    if ($Entry.var) { return "var $($Entry.var)" }
    if ($Kind -eq 'event') { return "$(if ($Entry.target) { $Entry.target } else { $Entry.type }).$($Entry.event)" }
    "$($Entry.type).$($Entry.method)"
}

# The result DTO of one verb, or a thrown refusal naming it. Transport failure = throw.
function Invoke-HarnessVerb($Invoke, [string] $Verb, $VerbArgs) {
    $reply = & $Invoke $Verb $VerbArgs
    if ($null -eq $reply -or $reply.status -ne 'done') { throw "$Verb did not finish: $(ConvertTo-Canon $reply)" }
    $reply.result
}

# Rows of a cursor read (log / events / trace) after $Since, paged until hasMore is gone.
function Read-HarnessPages($Invoke, [string] $Verb, $BaseArgs, [long] $Since, [int] $MaxPages = 20) {
    $rows = New-Object Collections.Generic.List[object]
    $dropped = 0
    for ($p = 0; $p -lt $MaxPages; $p++) {
        $a = [ordered]@{}
        foreach ($k in $BaseArgs.Keys) { $a[$k] = $BaseArgs[$k] }
        $a.since = $Since; $a.pageSize = 200
        $r = Invoke-HarnessVerb $Invoke $Verb $a
        if (-not $r.ok) { throw "$Verb read refused: $($r.code) $($r.error)" }
        foreach ($row in @($r.rows)) { if ($row) { $rows.Add($row) } }
        if ($r.dropped) { $dropped += [int]$r.dropped }
        $Since = [long]$r.next
        if (-not $r.hasMore) { break }
    }
    [pscustomobject]@{ rows = $rows.ToArray(); dropped = $dropped }
}

# One case end to end. Harness-owned traces/subscriptions are ALWAYS released (finally), whatever
# the plan did. Returns {name, ok, ms, fail?}.
function Invoke-TestCase($Case, $Invoke, $Vars = $null) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $x = $Case.expect
    $run = [pscustomobject]@{ plan = $null; log = @(); logDropped = 0; events = @{}; traces = @{}; errors = (New-Object Collections.Generic.List[string]) }
    $ownTraces = New-Object Collections.Generic.List[int]
    $ownSubs = New-Object Collections.Generic.List[int]
    $traceIds = @{}; $subs = @{}; $ctx = @{}
    try {
        $logStatus = Invoke-HarnessVerb $Invoke 'log' @{ status = $true }
        $logMark = [long]$logStatus.next
        # An unhooked tap has an empty ring: `hasNot` would pass on a log it never saw.
        if ($x.log -and -not $logStatus.hooked) { throw 'the bridge log tap is not hooked - expect.log cannot be checked' }
        # SETUP first: a subscription on @tac needs the mission a setup loads.
        $si = 0
        foreach ($s in @($Case.setup)) {
            $sb = [ordered]@{ plan = $s.plan }
            $sv = Expand-CaseVars $s.vars $ctx
            if ($sv) { $sb.vars = $sv }
            $r = Invoke-HarnessVerb $Invoke 'plan' $sb
            if (-not $r.ok) { throw "setup[$si] $([IO.Path]::GetFileName($s.file)) failed at step '$($r.step)' ($($r.code)): $($r.error)" }
            if ($s.save) { $ctx[[string]$s.save] = $r.output }
            $si++
        }
        $planMs = if ($Case.plan.timeoutMs) { [int]$Case.plan.timeoutMs } else { 60000 }
        $i = 0
        foreach ($t in @($x.traces)) {
            if (-not $t) { continue }
            if (-not $t.var) {
                $start = [ordered]@{ type = $t.type; method = $t.method; maxHits = ($t.maxHits ? [int]$t.maxHits : 1000)
                                     ttlMs = [Math]::Min(900000, $planMs + 60000); keepScene = ($null -eq $t.keepScene ? $true : [bool]$t.keepScene) }
                foreach ($k in 'sig', 'assembly', 'force', 'args') { if ($null -ne $t.$k) { $start[$k] = $t.$k } }
                $r = Invoke-HarnessVerb $Invoke 'trace' @{ start = $start }
                if (-not $r.ok) { throw "trace[$i] $($t.type).$($t.method) refused: $($r.code) $($r.error)" }
                $traceIds[$i] = [int]$r.id
                if (-not $r.existing) { $ownTraces.Add([int]$r.id) }
            }
            $i++
        }
        $i = 0
        foreach ($e in @($x.events)) {
            if (-not $e) { continue }
            if (-not $e.var) {
                $sub = [ordered]@{ event = $e.event }
                if ($e.target) { $sub.target = $e.target } elseif ($e.type) { $sub.type = $e.type }
                $r = Invoke-HarnessVerb $Invoke 'events' @{ subscribe = $sub }
                if (-not $r.ok) { throw "event[$i] $(Get-ExpectLabel $e 'event') refused: $($r.code) $($r.error)" }
                $subs[$i] = @{ sub = [int]$r.sub; since = [long]$r.next }
                if (-not $r.existing) { $ownSubs.Add([int]$r.sub) }
            }
            $i++
        }

        $body = [ordered]@{ plan = (New-CasePlan $Case) }
        $bv = Merge-Vars (Expand-CaseVars $Case.vars $ctx) $Vars      # the caller's vars win
        if ($bv) { $body.vars = $bv }
        $run.plan = Invoke-HarnessVerb $Invoke 'plan' $body

        $i = 0
        foreach ($t in @($x.traces)) {
            if (-not $t) { continue }
            $id = $traceIds[$i]
            if ($t.var) {
                $saved = if ($run.plan.output) { $run.plan.output."`$v:$($t.var)" } else { $null }
                $id = if ($saved -and $saved.id) { [int]$saved.id } else { $null }
                if ($null -eq $id) { $run.errors.Add("trace[$i] var $($t.var): no trace id saved (the step failed or was skipped)"); $run.traces[$i] = 0; $i++; continue }
            }
            if ($t.match) { $run.traces[$i] = (Read-HarnessPages $Invoke 'trace' ([ordered]@{ id = $id; match = $t.match }) 0).rows.Count }
            else { $run.traces[$i] = [int](Invoke-HarnessVerb $Invoke 'trace' @{ id = $id }).hits }
            $i++
        }
        $i = 0
        foreach ($e in @($x.events)) {
            if (-not $e) { continue }
            $s = $subs[$i]
            if ($e.var) {
                $saved = if ($run.plan.output) { $run.plan.output."`$v:$($e.var)" } else { $null }
                $s = if ($saved -and $saved.sub) { @{ sub = [int]$saved.sub; since = [long]($saved.next ?? 0) } } else { $null }
                if (-not $s) { $run.errors.Add("event[$i] var $($e.var): no subscription saved"); $run.events[$i] = 0; $i++; continue }
            }
            $a = [ordered]@{ sub = $s.sub }
            if ($e.match) { $a.match = $e.match }
            $run.events[$i] = (Read-HarnessPages $Invoke 'events' $a $s.since).rows.Count
            $i++
        }
        if ($x.log) {
            $a = [ordered]@{ clip = 1000 }
            if ($x.log.level) { $a.level = $x.log.level }
            $got = Read-HarnessPages $Invoke 'log' $a $logMark
            $run.log = $got.rows; $run.logDropped = $got.dropped
        }
    }
    catch { $run.errors.Add("harness: $($_.Exception.Message)") }
    finally {
        foreach ($id in $ownTraces) { try { Invoke-HarnessVerb $Invoke 'trace' @{ stop = $id } | Out-Null } catch { } }
        foreach ($sid in $ownSubs) { try { Invoke-HarnessVerb $Invoke 'events' @{ unsubscribe = $sid } | Out-Null } catch { } }
        # TEARDOWN always runs; a failing one fails the case (it left the game dirty for the next).
        $ti = 0
        foreach ($s in @($Case.teardown)) {
            try {
                $tb = [ordered]@{ plan = $s.plan }
                $tv = Expand-CaseVars $s.vars $ctx
                if ($tv) { $tb.vars = $tv }
                $r = Invoke-HarnessVerb $Invoke 'plan' $tb
                if (-not $r.ok) { $run.errors.Add("teardown[$ti] $([IO.Path]::GetFileName($s.file)) failed at step '$($r.step)' ($($r.code)): $($r.error)") }
            }
            catch { $run.errors.Add("teardown[$ti]: $($_.Exception.Message)") }
            $ti++
        }
    }
    $fail = @(Test-CaseExpect $Case $run)
    $res = [ordered]@{ name = $Case.name; ok = ($fail.Count -eq 0); ms = [int]$sw.ElapsedMilliseconds }
    if ($fail.Count -gt 0) { $res.fail = $fail }
    [pscustomobject]$res
}

function Invoke-TestSuite([string[]] $Files, $Invoke, $Vars = $null, [string] $Only = '', [scriptblock] $Prepare = $null) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $cases = @(foreach ($f in $Files) { Read-TestCase $f })      # every file parsed BEFORE case 1 runs
    foreach ($c in $cases) { if ($Prepare) { & $Prepare $c }; New-CasePlan $c | Out-Null }   # every var reference checked
    if ($Only) { $cases = @($cases | Where-Object { $_.name -match $Only }) }
    if ($cases.Count -eq 0) { throw "no test case left to run$(if ($Only) { " (-Only '$Only' matched none)" })" }
    $results = @(foreach ($c in $cases) { Invoke-TestCase $c $Invoke $Vars })
    $failed = @($results | Where-Object { -not $_.ok }).Count
    [ordered]@{ ok = ($failed -eq 0); passed = $results.Count - $failed; failed = $failed; ms = [int]$sw.ElapsedMilliseconds; cases = $results }
}

function ConvertTo-JUnitXml($Summary, [string] $SuiteName = 'ppcli') {
    $esc = { param($s) [Security.SecurityElement]::Escape([string]$s) }
    $sb = New-Object Text.StringBuilder
    [void]$sb.AppendLine('<?xml version="1.0" encoding="UTF-8"?>')
    $t = '{0:0.###}' -f ($Summary.ms / 1000.0)
    [void]$sb.AppendLine("<testsuites tests=`"$($Summary.cases.Count)`" failures=`"$($Summary.failed)`" time=`"$t`">")
    [void]$sb.AppendLine("  <testsuite name=`"$(& $esc $SuiteName)`" tests=`"$($Summary.cases.Count)`" failures=`"$($Summary.failed)`" time=`"$t`">")
    foreach ($c in $Summary.cases) {
        $ct = '{0:0.###}' -f ($c.ms / 1000.0)
        if ($c.ok) { [void]$sb.AppendLine("    <testcase name=`"$(& $esc $c.name)`" classname=`"$(& $esc $SuiteName)`" time=`"$ct`"/>"); continue }
        $msg = @($c.fail) -join "`n"
        [void]$sb.AppendLine("    <testcase name=`"$(& $esc $c.name)`" classname=`"$(& $esc $SuiteName)`" time=`"$ct`">")
        [void]$sb.AppendLine("      <failure message=`"$(& $esc (@($c.fail)[0]))`">$(& $esc $msg)</failure>")
        [void]$sb.AppendLine('    </testcase>')
    }
    [void]$sb.AppendLine('  </testsuite>')
    [void]$sb.AppendLine('</testsuites>')
    $sb.ToString()
}
