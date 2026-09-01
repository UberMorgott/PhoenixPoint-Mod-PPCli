<#
  The client half's offline gate. Drives the REAL ppcli.ps1 'connect' against a stand-in server that
  speaks the same frame format as src\Wire.cs, so endpoint discovery, the stale sweep, framing and
  the accepted -> status poll are all exercised with no game running.

      pwsh -NoProfile -File .\selfcheck\client-pipetest.ps1            # must exit 0
      pwsh -NoProfile -File .\selfcheck\client-pipetest.ps1 -Falsify   # must ALSO exit 0

  -Falsify corrupts every expectation on purpose and demands that EVERY assertion fails - a check
  that passes on nothing is the failure mode this repo has already paid for once.

  The C# half is covered by SelfCheck.exe next door; this covers the PowerShell half.
#>
param([switch] $Falsify)

$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:LOCALAPPDATA 'ppcli\endpoints'
New-Item -ItemType Directory -Force -Path $dir | Out-Null

$pipe  = "ppcli-test-$PID"
$fake  = 'C:\FakeInstall'
$ep    = Join-Path $dir "$PID.json"
$stale = Join-Path $dir '999999.json'
Set-Content -Path $stale -Value '{"pipe":"dead","pid":999999,"install":"C:\\Gone","token":"t"}' -Encoding utf8NoBOM
Set-Content -Path $ep -Value (@{ pipe = $pipe; pid = $PID; install = $fake; protocol = 'ppcli/1'; build = 'testbuild'; token = 'cafebabecafebabe' } | ConvertTo-Json -Compress) -Encoding utf8NoBOM

$script:passed = 0
$script:failed = 0

# Same shape as tests\*.tests.ps1: -Falsify corrupts the EXPECTATION, never the code under test.
function Assert-Value([string] $what, $actual, [string] $expected) {
    if ($Falsify) { $expected = $expected + '~falsified' }
    if ([string]$actual -ceq $expected) { $script:passed++; Write-Host "  ok   $what" }
    else { $script:failed++; Write-Host "  FAIL $what : got '$actual', wanted '$expected'" }
}

# The PATTERN is what gets corrupted here, so -Falsify probes the assertion itself and not a label
# next to it. An empty or missing frame fails outright rather than sliding past a regex on $null -
# the vacuous pass this file used to be capable of.
function Assert-Match([string] $what, $actual, [string] $pattern) {
    if ($Falsify) { $pattern = $pattern + '~falsified' }
    $text = [string]$actual
    if ([string]::IsNullOrWhiteSpace($text)) { $script:failed++; Write-Host "  FAIL $what : nothing was sent or received"; return }
    if ($text -match $pattern) { $script:passed++; Write-Host "  ok   $what" }
    else { $script:failed++; Write-Host "  FAIL $what : '$text' does not match /$pattern/" }
}

$server = Start-Job -ArgumentList $pipe -ScriptBlock {
    param($pipe)
    function ReadExact($s, $n) { $b = New-Object byte[] $n; $g = 0; while ($g -lt $n) { $r = $s.Read($b, $g, $n - $g); if ($r -le 0) { return $null }; $g += $r }; ,$b }
    $enc = New-Object Text.UTF8Encoding $false
    $replies = @('{"status":"done","id":"c1","jobId":"j1","result":{"ok":true,"phase":"menu"}}',
                 '{"status":"accepted","id":"c1","jobId":"j2"}',
                 '{"status":"running","jobId":"j2","id":"c1"}',
                 '{"status":"done","id":"c1","jobId":"j2","result":{"ok":true,"slow":true}}',
                 '{"status":"done","id":"c1","jobId":"j3","result":{"ok":true,"echo":"one"}}',
                 '{"status":"done","id":"c1","jobId":"j4","result":{"ok":true,"row":1}}',
                 '{"status":"done","id":"c1","jobId":"j5","result":{"ok":false,"error":"row 2 refused"}}',
                 '{"status":"done","id":"c1","jobId":"j6","result":{"ok":false,"code":"args","error":"pageSize must be 1..200"}}')
    $seen = @()
    foreach ($r in $replies) {
        $s = New-Object IO.Pipes.NamedPipeServerStream $pipe, ([IO.Pipes.PipeDirection]::InOut)
        $s.WaitForConnection()
        $len = [BitConverter]::ToInt32((ReadExact $s 4), 0)
        $seen += $enc.GetString((ReadExact $s $len))
        $body = $enc.GetBytes($r)
        $s.Write([BitConverter]::GetBytes([int]$body.Length), 0, 4); $s.Write($body, 0, $body.Length); $s.Flush(); $s.WaitForPipeDrain()
        $s.Dispose()
    }
    $seen
}
Start-Sleep -Milliseconds 700

try {
    $cli = Join-Path (Split-Path -Parent $PSScriptRoot) 'ppcli.ps1'
    $out1 = (& $cli connect state -PPRoot $fake 2>$null) -join "`n"
    $out2 = (& $cli connect console '{"command":"x","args":["y"]}' -PPRoot $fake -TimeoutSeconds 20 2>$null) -join "`n"
    $out3 = (& $cli connect ping '["one"]' -PPRoot $fake 2>$null) -join "`n"
    $out4 = (& $cli connect multi '[{"id":"a","verb":"state"},{"id":"b","verb":"call","args":{"op":"get"}}]' -PPRoot $fake 2>$null) -join "`n"
    $out5  = (& $cli connect items '{"h":"h1","pageSize":400}' -PPRoot $fake 2>$null) -join "`n"
    $code5 = $LASTEXITCODE
    $sent  = @(Receive-Job -Job $server -Wait)
}
finally {
    Remove-Job $server -Force -ErrorAction SilentlyContinue
    Remove-Item $ep -Force -ErrorAction SilentlyContinue
}

Write-Host "client pipetest ($(if ($Falsify) { 'FALSIFY' } else { 'normal' }))"

# ---------------------------------------------------------------- what came BACK
# A reply delivered inline, and one that only arrives after the accepted -> status poll. Both are
# read off the parsed object, so a client that printed the raw frame would fail here.
Assert-Match 'an inline done is unwrapped to its result' $out1 '"phase"\s*:\s*"menu"'
Assert-Value 'the inline result parses as an object'  (& { if ($out1) { (ConvertFrom-Json $out1).result.phase } }) 'menu'
Assert-Value 'accepted is polled through to done'     (& { if ($out2) { [string](ConvertFrom-Json $out2).result.slow } }) 'True'
Assert-Value 'the ping reply is returned too'         (& { if ($out3) { [string](ConvertFrom-Json $out3).result.echo } }) 'one'

# ---------------------------------------------------------------- what went OUT on the wire
Assert-Match 'the endpoint token is sent'    $sent[0] '"token"\s*:\s*"cafebabecafebabe"'
Assert-Match 'the verb is sent'              $sent[0] '"verb"\s*:\s*"state"'
Assert-Match 'the command name survives'     $sent[1] '"command"\s*:\s*"x"'
Assert-Match 'the args array survives'       $sent[1] '"args"\s*:\s*\["y"\]'
# PowerShell unrolls a one-element array into a scalar; without -NoEnumerate this arrives as "one".
Assert-Match 'a single-element array stays an array' $sent[4] '"args"\s*:\s*\["one"\]'
Assert-Match 'the poll asks for status'      $sent[2] '"verb"\s*:\s*"status"'
Assert-Match 'the poll names the job it is waiting on' $sent[2] '"jobId"\s*:\s*"j2"'

# ---------------------------------------------------------------- connect multi: N verbs, ONE process
# One endpoint discovery, one token, two requests, and STILL exactly one JSON object on stdout.
Assert-Value 'multi answers for every request'   (& { if ($out4) { [string](ConvertFrom-Json $out4).count } }) '2'
Assert-Value 'multi keeps the caller ids'        (& { if ($out4) { ((ConvertFrom-Json $out4).results.id) -join ',' }}) 'a,b'
Assert-Value 'multi unwraps each reply'          (& { if ($out4) { [string](ConvertFrom-Json $out4).results[0].reply.result.row } }) '1'
# A refused row does NOT abort the run - an enumeration whose row 40 fails still wants rows 41-188.
Assert-Value 'a refused request is a result, not an abort' (& { if ($out4) { [string](ConvertFrom-Json $out4).failed } }) '1'
Assert-Value 'multi is not ok when a row refused' (& { if ($out4) { [string](ConvertFrom-Json $out4).ok } }) 'False'
Assert-Match 'multi sends the same token on every frame' $sent[6] '"token"\s*:\s*"cafebabecafebabe"'
Assert-Match 'multi sends the second verb'       $sent[6] '"verb"\s*:\s*"call"'

# Exactly seven frames: a client that re-polled a job already reported done, or skipped the poll and
# printed 'accepted', both change this count - as would a multi that re-discovered the endpoint.
Assert-Value 'the client sent one frame per exchange' $sent.Count '8'

# ---------------------------------------------------------------- a refusal is not an empty page
# `items` refuses an out-of-range pageSize with ok:false and NO `items` key. Read off a process that
# exited 0, that is indistinguishable from a sweep that found nothing - which is how a bad argument
# once got read as "the asset is not loaded".
Assert-Value 'a refused verb carries no items key'  (& { if ($out5) { if ((ConvertFrom-Json $out5).result.PSObject.Properties['items']) { 'present' } else { 'absent' } } }) 'absent'
Assert-Value 'a refused verb keeps its code'        (& { if ($out5) { [string](ConvertFrom-Json $out5).result.code } }) 'args'
Assert-Value 'the refusal names the cap'            (& { if ($out5) { [string](ConvertFrom-Json $out5).result.error } }) 'pageSize must be 1..200'
Assert-Value 'a refused verb exits non-zero'        $code5 '1'

# PREVALIDATION, and the server is already gone - which is the point. The whole array is checked
# BEFORE row 1 is sent, so a bad row 2 must be refused by name even when there is no endpoint at all
# to send row 1 to. If the check ever slides back into the execution loop this reads
# 'no live PPBridge endpoint' instead, and row 1 will have been sent (and executed) first.
$bad = (& $cli connect multi '[{"id":"a","verb":"state"},{"id":"b"}]' -PPRoot $fake 2>$null) -join "`n"
Assert-Match 'a malformed row is caught before anything is sent' $bad "request 2 .*'verb'"

# ---------------------------------------------------------------- the endpoint directory
Assert-Value 'a dead endpoint file is swept' ($(if (Test-Path $stale) { 'survived' } else { 'swept' })) 'swept'

Write-Host "passed=$($script:passed) failed=$($script:failed)"
if ($Falsify) {
    if ($script:passed -ne 0) { Write-Host "FALSIFY BROKEN: $($script:passed) assertion(s) passed against corrupted expectations"; exit 1 }
    Write-Host 'falsified: every assertion reported failure, so they are wired to something'
    exit 0
}
if ($script:failed -ne 0) { exit 1 }
exit 0
