# The client half's offline gate. Drives the REAL ppcli.ps1 'connect' against a stand-in server that
# speaks the same frame format as src\Wire.cs, so endpoint discovery, the stale sweep, framing and
# the accepted -> status poll are all exercised with no game running.
#
#   pwsh -NoProfile -File PPCLI\selfcheck\client-pipetest.ps1
#
# The C# half is covered by SelfCheck.exe next door; this covers the PowerShell half.
$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:LOCALAPPDATA 'ppcli\endpoints'
New-Item -ItemType Directory -Force -Path $dir | Out-Null

$pipe = "ppcli-test-$PID"
$fake = 'C:\FakeInstall'
$ep   = Join-Path $dir "$PID.json"
$stale = Join-Path $dir '999999.json'
Set-Content -Path $stale -Value '{"pipe":"dead","pid":999999,"install":"C:\\Gone","token":"t"}' -Encoding utf8NoBOM
Set-Content -Path $ep -Value (@{ pipe = $pipe; pid = $PID; install = $fake; protocol = 'ppcli/1'; build = 'testbuild'; token = 'cafebabecafebabe' } | ConvertTo-Json -Compress) -Encoding utf8NoBOM

$server = Start-Job -ArgumentList $pipe -ScriptBlock {
    param($pipe)
    function ReadExact($s, $n) { $b = New-Object byte[] $n; $g = 0; while ($g -lt $n) { $r = $s.Read($b, $g, $n - $g); if ($r -le 0) { return $null }; $g += $r }; ,$b }
    $enc = New-Object Text.UTF8Encoding $false
    $replies = @('{"status":"done","id":"c1","jobId":"j1","result":{"ok":true,"phase":"menu"}}',
                 '{"status":"accepted","id":"c1","jobId":"j2"}',
                 '{"status":"running","jobId":"j2","id":"c1"}',
                 '{"status":"done","id":"c1","jobId":"j2","result":{"ok":true,"slow":true}}',
                 '{"status":"done","id":"c1","jobId":"j3","result":{"ok":true}}')
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

$cli = Join-Path (Split-Path -Parent $PSScriptRoot) 'ppcli.ps1'
$out1 = & $cli connect state -PPRoot $fake 2>$null
$out2 = & $cli connect console '{"command":"x","args":["y"]}' -PPRoot $fake -TimeoutSeconds 20 2>$null
$out3 = & $cli connect ping '["one"]' -PPRoot $fake 2>$null
$sent = Receive-Job -Job $server -Wait
Remove-Job $server -Force
Remove-Item $ep -Force -ErrorAction SilentlyContinue

$fail = 0
function T($n, $ok, $d) { if (-not $ok) { $script:fail++; "FAIL $n : $d" } }
T 'inline-done'    ((ConvertFrom-Json $out1).result.phase -eq 'menu') $out1
T 'poll-to-done'   ((ConvertFrom-Json $out2).result.slow -eq $true) $out2
T 'token-sent'     ($sent[0] -match '"token":"cafebabecafebabe"') $sent[0]
T 'verb-sent'      ($sent[0] -match '"verb":"state"') $sent[0]
T 'args-sent'      ($sent[1] -match '"command":"x"' -and $sent[1] -match '"args":\["y"\]') $sent[1]
# PowerShell unrolls a one-element array into a scalar; without -NoEnumerate this arrives as "one".
T 'single-elem-array-survives' ($sent[4] -match '"args":\["one"\]') $sent[4]
T 'status-polls-jobid' ($sent[2] -match '"verb":"status"' -and $sent[2] -match '"jobId":"j2"') $sent[2]
T 'stale-swept'    (-not (Test-Path $stale)) 'the dead endpoint file survived'
if ($fail -eq 0) { 'ppcli client pipetest: PASS' } else { "ppcli client pipetest: $fail FAILURE(S)" }
