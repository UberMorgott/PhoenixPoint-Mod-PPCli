<#
  Offline check for the bounded-wait half of the client: a pipe read that must give up, and the
  dead-run signal that must fire on a mod exception and must NOT fire on the exceptions a healthy
  game writes every session. No game, no framework.

      pwsh -NoProfile -File .\tests\waits.tests.ps1            # must exit 0
      pwsh -NoProfile -File .\tests\waits.tests.ps1 -Falsify   # must ALSO exit 0

  -Falsify corrupts every expectation on purpose and demands that EVERY assertion fails - a check
  that passes on nothing is the failure mode this repo has already paid for once.
#>
param([switch] $Falsify)

$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path $PSScriptRoot 'fixture-waits'
function Remove-Scratch { Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue }
Remove-Scratch
New-Item -ItemType Directory -Force -Path $scratch | Out-Null

. (Join-Path $root 'waits.ps1')

try {

$script:passed = 0
$script:failed = 0

function Assert-Value([string] $what, $actual, [string] $expected) {
    if ($Falsify) { $expected = $expected + '~falsified' }
    if ([string]$actual -ceq $expected) { $script:passed++; Write-Host "  ok   $what" }
    else { $script:failed++; Write-Host "  FAIL $what : got '$actual', wanted '$expected'" }
}

# The refusal TEXT is asserted, not just "it threw": a timeout that does not say what it waited for
# and what to do about it is the bug being guarded against, not the fix.
function Assert-Refusal([string] $what, [string] $mustSay, [scriptblock] $body) {
    if ($Falsify) { $mustSay = $mustSay + '~falsified' }
    try { & $body | Out-Null }
    catch {
        $msg = $_.Exception.Message
        if ($msg -like "*$mustSay*") { $script:passed++; Write-Host "  ok   $what"; return }
        $script:failed++; Write-Host "  FAIL $what : refused, but never said '$mustSay' - $msg"; return
    }
    $script:failed++
    Write-Host "  FAIL $what : nothing was thrown"
}

Write-Host "waits ($(if ($Falsify) { 'FALSIFY' } else { 'normal' }))"

# ---------------------------------------------------------------- the bounded read
# A real named pipe with a server that ACCEPTS and then says nothing - which is exactly what a game
# whose main thread is wedged looks like from out here, and what used to block this client forever.
$pipeName = 'ppcli-test-' + [Guid]::NewGuid().ToString('N')
$server = [PowerShell]::Create()
$server.AddScript({
    param($name)
    $s = New-Object IO.Pipes.NamedPipeServerStream $name, ([IO.Pipes.PipeDirection]::InOut)
    $s.WaitForConnection()
    Start-Sleep -Seconds 10          # accept, then never answer
    $s.Dispose()
}).AddArgument($pipeName) | Out-Null
$handle = $server.BeginInvoke()

$client = New-Object IO.Pipes.NamedPipeClientStream '.', $pipeName, ([IO.Pipes.PipeDirection]::InOut)
$client.Connect(5000)
$elapsed = Measure-Command {
    try { Read-Exact $client 4 700 'the fake game' | Out-Null } catch { $script:readErr = $_.Exception.Message }
}
Assert-Value 'a silent server ends the read instead of blocking' `
    ($(if ($script:readErr -like '*TIMEOUT*') { 'timed out' } else { "wrong:$($script:readErr)" })) 'timed out'
Assert-Value 'the timeout says how many bytes arrived' `
    ($(if ($script:readErr -like '*0 of 4 bytes*') { 'named' } else { "wrong:$($script:readErr)" })) 'named'
# The ceiling has to be the ceiling. A "bounded" read that takes ten seconds to honour a 700 ms
# budget is the same hang wearing a timeout.
Assert-Value 'it gives up at its ceiling, not at the server''s' `
    ($(if ($elapsed.TotalSeconds -lt 5) { 'prompt' } else { "slow:$([int]$elapsed.TotalSeconds)s" })) 'prompt'
# A human reading it must know what to DO, the same standard as the ppcli-enabled refusal.
Assert-Value 'the timeout tells a human what to do' `
    ($(if ($script:readErr -like '*kill that process*') { 'actionable' } else { "wrong:$($script:readErr)" })) 'actionable'
$client.Dispose()
$server.Stop(); $server.Dispose()

# A pipe that CLOSES is a different answer from a pipe that goes quiet, and must stay so.
$closedName = 'ppcli-test-' + [Guid]::NewGuid().ToString('N')
$closer = [PowerShell]::Create()
$closer.AddScript({
    param($name)
    $s = New-Object IO.Pipes.NamedPipeServerStream $name, ([IO.Pipes.PipeDirection]::InOut)
    $s.WaitForConnection()
    $s.Dispose()
}).AddArgument($closedName) | Out-Null
$closer.BeginInvoke() | Out-Null
$c2 = New-Object IO.Pipes.NamedPipeClientStream '.', $closedName, ([IO.Pipes.PipeDirection]::InOut)
$c2.Connect(5000)
Assert-Refusal 'a closed pipe still reports the close, not a timeout' 'the pipe closed after 0 of 4 bytes' `
    { Read-Exact $c2 4 5000 'the fake game' }
$c2.Dispose(); $closer.Stop(); $closer.Dispose()

# ---------------------------------------------------------------- the dead-run signal
$log = Join-Path $scratch 'Player.log'

# THE FALSE POSITIVE THAT MATTERS. Both of these are written by a healthy session - this repo's own
# logs carry ten bare NREs and the mesh warning in runs that finished perfectly. Failing a run on
# either would be worse than the hang the fast-fail exists to cut short.
Set-Content -Path $log -Encoding utf8NoBOM -Value @(
    'ArgumentException: Mesh can not have more than 65000 vertices',
    'UnityEngine.DebugLogHandler:Internal_LogException(Exception, Object)',
    'NullReferenceException',
    '  at PhoenixPoint.Tactical.Entities.TacticalActorBase.OnHealthChange (Base.Entities.Statuses.BaseStat stat)',
    '  at Base.Core.TimingScheduler.CallUpdateable ()')
$mark = New-LogMark $log
Assert-Value 'a healthy log with vanilla exceptions is not a fault' `
    ([string](Get-LogFault @{ path = $log; lines = 0 } 'TFTV')) ''

# ...and the signal itself: an exception whose STACK names the mod.
Add-Content -Path $log -Encoding utf8NoBOM -Value @(
    'NullReferenceException',
    '  at TFTV.TFTVCommonMethods.OnLevelStart (PhoenixPoint.Tactical.Levels.TacticalLevelController controller)',
    '  at Base.Core.TimingScheduler.CallUpdateable ()')
$fault = Get-LogFault $mark 'TFTV'
Assert-Value 'a mod exception after the mark is a fault' `
    ($(if ($fault) { 'caught' } else { 'missed' })) 'caught'
Assert-Value 'the fault carries the frame that named the mod' `
    ($(if ($fault -like '*TFTVCommonMethods.OnLevelStart*') { 'quoted' } else { "wrong:$fault" })) 'quoted'

# THE MARK IS THE WHOLE POINT. The same fault, already in the log before the wait began, must not
# fail a fresh run - otherwise one bad load poisons every call for the rest of the session.
Assert-Value 'a fault older than the mark is ignored' `
    ([string](Get-LogFault (New-LogMark $log) 'TFTV')) ''

# No log at all is not an error: a hand-launched game may not have one, and the watch is then off.
Assert-Value 'a missing log is silence, not a failure' `
    ([string](Get-LogFault (New-LogMark (Join-Path $scratch 'nope.log')) 'TFTV')) ''

# ---------------------------------------------------------------- the DEFAULT, which names no mod
# The default used to be the literal 'TFTV' - one machine's own mod list shipped as everyone's
# default, so a stranger running any other mod set got no fast-fail at all. The signal is a MOD
# FRAME, and that is decidable without knowing which mod: a stack frame whose root namespace is not
# the game, the engine or the runtime.
$other = Join-Path $scratch 'other-mod.log'
Set-Content -Path $other -Encoding utf8NoBOM -Value @(
    'NullReferenceException',
    '  at SomeStrangersMod.Patches.OnLevelStart (PhoenixPoint.Tactical.Levels.TacticalLevelController controller)',
    '  at Base.Core.TimingScheduler.CallUpdateable ()')
Assert-Value 'the default catches a mod it was never told about' `
    ($(if (Get-LogFault @{ path = $other; lines = 0 }) { 'caught' } else { 'missed' })) 'caught'
# ...and it must still not fire on the exceptions a healthy session writes, which is the whole reason
# the old default was narrow in the first place.
Set-Content -Path $other -Encoding utf8NoBOM -Value @(
    'ArgumentException: Mesh can not have more than 65000 vertices',
    '  at UnityEngine.Mesh.SetVertices (System.Collections.Generic.List`1[T] inVertices)',
    'NullReferenceException',
    '  at PhoenixPoint.Tactical.Entities.TacticalActorBase.OnHealthChange (Base.Entities.Statuses.BaseStat stat)',
    '  at Base.Core.TimingScheduler.CallUpdateable ()')
Assert-Value 'the default does not fire on game and engine frames' `
    ([string](Get-LogFault @{ path = $other; lines = 0 })) ''

Assert-Value 'a game frame is not a mod frame' `
    ([bool](Test-ModFrame '  at PhoenixPoint.Tactical.Levels.TacticalLevelController.Update ()')) 'False'
Assert-Value 'a mod frame is a mod frame' `
    (Test-ModFrame '  at TFTV.TFTVCommonMethods.OnLevelStart (X y)') 'True'
Assert-Value 'the exception header itself is not a frame' `
    ([bool](Test-ModFrame 'NullReferenceException')) 'False'

}
finally { Remove-Scratch }

Write-Host "passed=$($script:passed) failed=$($script:failed)"
if ($Falsify) {
    if ($script:passed -ne 0) { Write-Host "FALSIFY BROKEN: $($script:passed) assertion(s) passed against corrupted expectations"; exit 1 }
    Write-Host 'falsified: every assertion reported failure, so they are wired to something'
    exit 0
}
if ($script:failed -ne 0) { exit 1 }
exit 0
