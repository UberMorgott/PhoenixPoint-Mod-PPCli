<#
  Offline check for `ppcli.ps1 test` (harness.ps1): case parsing and its early refusals, the plan the
  harness actually sends, every `expect` operator, and a whole suite driven through a FAKE invoker
  (no game) - including that harness-owned traces/subscriptions are released and teardown runs even
  when the body fails.

      pwsh -NoProfile -File .\tests\harness.tests.ps1            # must exit 0
      pwsh -NoProfile -File .\tests\harness.tests.ps1 -Falsify   # must ALSO exit 0

  -Falsify corrupts every expectation and demands that EVERY assertion fails.
#>
param([switch] $Falsify)

$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path $PSScriptRoot 'fixture-harness'
Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
. (Join-Path $root 'harness.ps1')

$script:passed = 0
$script:failed = 0
function Assert-Value([string] $what, $actual, [string] $expected) {
    if ($Falsify) { $expected = $expected + '~falsified' }
    if ([string]$actual -ceq $expected) { $script:passed++; Write-Host "  ok   $what" }
    else { $script:failed++; Write-Host "  FAIL $what : got '$actual', wanted '$expected'" }
}
function Assert-Refusal([string] $what, [string] $mustSay, [scriptblock] $body) {
    if ($Falsify) { $mustSay = $mustSay + '~falsified' }
    try { & $body | Out-Null }
    catch {
        if ($_.Exception.Message -like "*$mustSay*") { $script:passed++; Write-Host "  ok   $what"; return }
        $script:failed++; Write-Host "  FAIL $what : refused without '$mustSay' - $($_.Exception.Message)"; return
    }
    $script:failed++; Write-Host "  FAIL $what : nothing was thrown"
}
function New-Case([string] $name, [string] $json) {
    $p = Join-Path $scratch "$name.test.json"
    Set-Content -Path $p -Value $json -Encoding utf8NoBOM
    $p
}

Write-Host "harness ($(if ($Falsify) { 'FALSIFY' } else { 'normal' }))"
try {

# ---------------------------------------------------------------- parsing + refusals
Assert-Refusal 'no steps, no plan' "no 'steps' array" { Read-TestCase (New-Case 'bad1' '{"expect":{}}') }
Assert-Refusal 'unknown expect key' "unknown key 'result'" { Read-TestCase (New-Case 'bad2' '{"steps":[{"verb":"state"}],"expect":{"result":[]}}') }
Assert-Refusal 'results needs one op' 'exactly one of' { Read-TestCase (New-Case 'bad3' '{"steps":[{"verb":"state"}],"expect":{"results":[{"var":"A","eq":1,"gt":0}]}}') }
Assert-Refusal 'trace entry needs type+method' "needs 'type' + 'method'" { Read-TestCase (New-Case 'bad4' '{"steps":[{"verb":"state"}],"expect":{"traces":[{"min":1}]}}') }
Assert-Refusal 'missing setup plan' 'does not exist' { Read-TestCase (New-Case 'bad5' '{"steps":[{"verb":"state"}],"setup":["nope.json"]}') }
Assert-Refusal 'steps and plan both' "both 'steps' and 'plan'" { Read-TestCase (New-Case 'bad6' '{"steps":[{"verb":"state"}],"plan":"@plans/kill-actor.json"}') }
$c = Read-TestCase (New-Case 'bad7' '{"steps":[{"id":"a","verb":"state","save":"A"}],"expect":{"results":[{"var":"B","exists":true}]}}')
Assert-Refusal 'expect names an unsaved var' "no step saves it" { New-CasePlan $c }

$ref = Read-TestCase (New-Case 'ref' '{"plan":{"plan":"@plans/kill-actor.json","vars":{"actorName":"X"}},"setup":[{"plan":"@plans/spawn-squad.json","save":"S"}]}')
Assert-Value '@plans/ resolves to the shipped plan' ([IO.Path]::GetFileName($ref.setup[0].file)) 'spawn-squad.json'
Assert-Value 'plan ref body carries its vars' $ref.vars.actorName 'X'
Assert-Value 'name defaults to the file name' $ref.name 'ref'

# ---------------------------------------------------------------- the plan the harness sends
$c = Read-TestCase (New-Case 'sent' '{"name":"n","steps":[{"id":"a","verb":"state","save":"A"},{"id":"b","verb":"state","save":"B"},{"verb":"repeat","args":{"times":2,"steps":[{"verb":"state","save":"C"}]}}],"output":{"mine":"${A.phase}"},"expect":{"results":[{"var":"C","exists":true}]}}')
$sent = New-CasePlan $c
Assert-Value 'output keeps the plan''s own fields' $sent.output.mine '${A.phase}'
Assert-Value 'output adds only the vars expect names (repeat body seen)' (($sent.output.PSObject.Properties.Name) -join ',') 'mine,$v:C'
Assert-Value 'name/expect are not sent' ([bool]($sent.PSObject.Properties['expect'] -or $sent.PSObject.Properties['name'])) 'False'

# ---------------------------------------------------------------- expect operators
$out = [pscustomobject]@{ '$v:R' = [pscustomobject]@{ ok = $true; value = [pscustomobject]@{ n = 5; s = 'Crab_1'; list = @(1, 2) } }; own = 'x' }
function Op($json) { Test-ResultExpect (ConvertFrom-Json $json) $out }
Assert-Value 'eq passes'            (Op '{"var":"R","path":"value.n","eq":5}') ''
Assert-Value 'eq fails with value'  ((Op '{"var":"R","path":"value.n","eq":6}') -like '*got 5, wanted eq 6*') 'True'
Assert-Value 'gt/lte numbers'       ("$(Op '{"var":"R","path":"value.n","gt":4}')$(Op '{"var":"R","path":"value.n","lte":5}')") ''
Assert-Value 'lt fails'             ([bool](Op '{"var":"R","path":"value.n","lt":5}')) 'True'
Assert-Value 'match on string'      (Op '{"var":"R","path":"value.s","match":"^Crab_\\d$"}') ''
Assert-Value 'index path'           (Op '{"var":"R","path":"value.list[1]","eq":2}') ''
Assert-Value 'exists false passes'  (Op '{"var":"R","path":"value.zz","exists":false}') ''
Assert-Value 'missing path fails'   ((Op '{"var":"R","path":"value.zz","eq":1}') -like '*path not found*') 'True'
Assert-Value 'truthy'               (Op '{"var":"R","path":"ok","truthy":true}') ''
Assert-Value 'output field'         (Op '{"output":"own","eq":"x"}') ''
Assert-Value 'ne'                   (Op '{"output":"own","ne":"y"}') ''

$case = [pscustomobject]@{ expect = (ConvertFrom-Json '{"ok":false,"code":"step","step":"k","log":{"has":["ready"],"hasNot":["Exception"]},"events":[{"event":"E","min":2}],"traces":[{"type":"T","method":"M","max":3}]}') }
$good = [pscustomobject]@{ plan = [pscustomobject]@{ ok = $false; code = 'step'; step = 'k' }; log = @([pscustomobject]@{ s = 1; m = 'mission ready' }); logDropped = 0; events = @{ 0 = 2 }; traces = @{ 0 = 3 }; errors = @() }
Assert-Value 'expected failure + log + counts all pass' (@(Test-CaseExpect $case $good).Count) '0'
$bad = [pscustomobject]@{ plan = [pscustomobject]@{ ok = $false; code = 'timeout'; step = 'j' }; log = @([pscustomobject]@{ s = 9; m = 'NullReferenceException: x' }); logDropped = 0; events = @{ 0 = 1 }; traces = @{ 0 = 4 }; errors = @() }
Assert-Value 'wrong code, wrong step, missing has, hit hasNot, low min, high max' (@(Test-CaseExpect $case $bad).Count) '6'
$drop = [pscustomobject]@{ plan = $good.plan; log = @(); logDropped = 5; events = @{ 0 = 2 }; traces = @{ 0 = 0 }; errors = @() }
Assert-Value 'dropped log rows cannot prove hasNot' ((@(Test-CaseExpect $case $drop) | Where-Object { $_ -like '*cannot be proven*' }).Count) '1'

# ---------------------------------------------------------------- a suite through a fake game
$script:calls = New-Object Collections.Generic.List[string]
$script:planOk = $true
$fake = {
    param($verb, $a)
    $j = ConvertTo-Json $a -Depth 32 -Compress
    $script:calls.Add("$verb $j")
    $r = switch ($verb) {
        'log' {
            if ($a.status) { [pscustomobject]@{ ok = $true; hooked = $true; next = 10 } }
            else { [pscustomobject]@{ ok = $true; rows = @([pscustomobject]@{ s = 11; l = 'L'; m = 'hello world' }); next = 11 } }
        }
        'trace' {
            if ($a.start) { [pscustomobject]@{ ok = $true; id = 7; next = 0 } }
            elseif ($a.stop) { [pscustomobject]@{ ok = $true; stopped = 1; hits = 3 } }
            else { [pscustomobject]@{ ok = $true; next = 3; hits = 3 } }
        }
        'events' {
            if ($a.subscribe) { [pscustomobject]@{ ok = $true; sub = 4; next = 20 } }
            elseif ($a.unsubscribe) { [pscustomobject]@{ ok = $true; removed = 1 } }
            else { [pscustomobject]@{ ok = $true; rows = @([pscustomobject]@{ s = 21; sub = 4 }); next = 21 } }
        }
        'plan' {
            $p = $a.plan
            if ($p.steps[0].id -eq 'setup') { [pscustomobject]@{ ok = $true; output = [pscustomobject]@{ actor = 'Crab_9' } } }
            elseif ($p.steps[0].id -eq 'down') { [pscustomobject]@{ ok = $true } }
            elseif ($script:planOk) { [pscustomobject]@{ ok = $true; output = [pscustomobject]@{ '$v:A' = [pscustomobject]@{ ok = $true; value = $a.vars.who } } } }
            else { [pscustomobject]@{ ok = $false; code = 'step'; step = 'a'; error = 'boom' } }
        }
    }
    [pscustomobject]@{ status = 'done'; result = $r }
}
New-Item -ItemType Directory -Force (Join-Path $scratch 'suite') | Out-Null
Set-Content (Join-Path $scratch 'suite\setup.json') '{"steps":[{"id":"setup","verb":"state"}],"finally":[]}' -Encoding utf8NoBOM
Set-Content (Join-Path $scratch 'suite\down.json') '{"steps":[{"id":"down","verb":"state"}],"finally":[]}' -Encoding utf8NoBOM
Set-Content (Join-Path $scratch 'suite\a.test.json') -Encoding utf8NoBOM -Value @'
{ "name": "full", "setup": [ { "plan": "setup.json", "save": "S" } ], "teardown": [ "down.json" ],
  "plan": { "plan": "body.json", "vars": { "who": "${S.actor}" } },
  "expect": { "results": [ { "var": "A", "path": "value", "eq": "Crab_9" } ], "log": { "has": ["hello"] },
              "events": [ { "target": "@tac", "event": "ActorDeathEvent", "min": 1 } ],
              "traces": [ { "type": "T", "method": "M", "min": 3 } ] } }
'@
Set-Content (Join-Path $scratch 'suite\body.json') '{"steps":[{"id":"a","verb":"state","save":"A"}],"finally":[]}' -Encoding utf8NoBOM
$files = @(Get-TestFiles (Join-Path $scratch 'suite'))
Assert-Value 'only *.test.json are cases' $files.Count '1'
$sum = Invoke-TestSuite $files $fake
Assert-Value 'suite passes: setup output feeds body var, results/log/events/traces' "$($sum.ok) $($sum.passed) $($sum.failed) $(@($sum.cases[0].fail) -join '|')" 'True 1 0 '
$order = @($script:calls | ForEach-Object { ($_ -split ' ')[0] + $(if ($_ -match '"(start|stop|subscribe|unsubscribe|status)"') { ':' + $Matches[1] } else { '' }) }) -join ','
Assert-Value 'order: log mark, setup, observers, body, reads, release, teardown' $order 'log:status,plan,trace:start,events:subscribe,plan,trace,events,log,trace:stop,events:unsubscribe,plan'

$script:calls.Clear(); $script:planOk = $false
$sum = Invoke-TestSuite $files $fake
Assert-Value 'a failing body fails the case with the step named' (($sum.cases[0].fail | Select-Object -First 1) -like "*failed at step 'a'*") 'True'
Assert-Value 'observers released + teardown run on failure' ((@($script:calls | Where-Object { $_ -match '"stop"|"unsubscribe"|"down"' })).Count) '3'
Assert-Refusal '-Only matching nothing refuses' 'matched none' { Invoke-TestSuite $files $fake $null 'zzz' }

$xml = ConvertTo-JUnitXml $sum 'mods'
Assert-Value 'junit counts + escaped failure' ([bool]($xml -match 'tests="1" failures="1"' -and $xml -match '<failure message="plan: failed at step &apos;a&apos;')) 'True'
[xml]$parsed = $xml
Assert-Value 'junit is well-formed' $parsed.testsuites.testsuite.name 'mods'
$prevCulture = [Threading.Thread]::CurrentThread.CurrentCulture
[Threading.Thread]::CurrentThread.CurrentCulture = 'ru-RU'
try { $ruXml = ConvertTo-JUnitXml ([ordered]@{ ms = 1500; failed = 0; cases = @([pscustomobject]@{ name = 'x'; ok = $true; ms = 1500 }) }) 's' }
finally { [Threading.Thread]::CurrentThread.CurrentCulture = $prevCulture }
Assert-Value 'junit time is invariant (1.5 under ru-RU)' ([bool]($ruXml -match 'time="1\.5"')) 'True'

}
finally { Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue }

if ($Falsify) {
    if ($script:passed -gt 0) { Write-Host "harness.tests -Falsify: $script:passed assertion(s) passed on a corrupted expectation"; exit 1 }
    Write-Host "harness.tests -Falsify: PASS (all $script:failed assertions failed as they must)"; exit 0
}
if ($script:failed -gt 0) { Write-Host "harness.tests: $script:failed FAILURE(S)"; exit 1 }
Write-Host "harness.tests: PASS ($script:passed)"
exit 0
