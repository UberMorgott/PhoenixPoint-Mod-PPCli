<#
  spider-demo.ps1 - the ContentTool spider scenario, end to end, as ONE replayable run.

  NOT a PPCLI feature - it is the longest WORKED EXAMPLE of driving the client, kept here because
  it exercises nearly the whole verb surface (deploy, state, roots, find, items, var, console,
  wait, call, snapshot, plan) in one honest sequence. Running it needs two mods PPCLI does not
  ship: the ContentTool engine mod (a sibling repo, whose deploy.ps1 this calls) and the
  `morgott.demo.customcreature` demo content mod. Read it for the idioms; it will not run without
  those two.

  Everything in here is a step that WAS OBSERVED TO WORK on 2026-08-26 against D:\PP-Instance2,
  save `4` (SCV_PLT_Ambush_56x56_A). No exploratory calls survive; every coordinate is derived
  from the live map (@selected -> spawn -> ring probe -> SnapXYZ/CanStandAt inside the shipped
  spawn plan), never typed in.

    .\spider-demo.ps1                 # deploy, activate, launch if needed, run the scenario
    .\spider-demo.ps1 -NoDeploy       # a game is already up and current - just run the scenario

  A plan file cannot be the artifact here: the plan engine has no way to build, to edit the
  profile, or to start the process. The two things it IS good at - the 21-step spawn and the
  savegame load - are called as the shipped plans they already are.
#>
param(
    [string] $PPRoot    = 'D:\PP-Instance2',
    # Empty = resolved the way ppcli.ps1 resolves it: line 2 of ppcli-install.txt, else the single
    # profile on the machine, else a refusal naming the candidates. Never a hard-coded SteamID64.
    [string] $ProfileId = '',
    [string] $SaveName  = '4',
    [switch] $NoDeploy,
    [switch] $NoLaunch,
    # Force the savegame load even when a tactical level is already up - a second run on a level
    # this script already played leaves its corpses and its victim standing about.
    [switch] $Reload,

    # EVERY wait in this file and in the plans it calls is bounded by one of these, sized to what
    # the operations actually take (launch ~30s, save load ~20s, one action ~2s). A run that blows
    # a budget fails LOUD and exits non-zero - it never sits on the main menu waiting for someone
    # to notice.
    [int] $LaunchTimeoutSec = 60,
    [int] $LoadTimeoutSec   = 90,
    [int] $ActionTimeoutSec = 15,
    # The ppcli client's own poll ceiling per job; it cancels the job (running its finally) and
    # answers, rather than polling forever. Must exceed the longest single job, which is the load.
    [int] $ClientTimeoutSec = 120
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'paths.ps1')
if (-not $ProfileId) { $ProfileId = Find-PPProfileId -Install $PPRoot }
$ppcli = Join-Path $PSScriptRoot 'ppcli.ps1'
$plans = Join-Path $PSScriptRoot 'plans'
function Say([string] $m) { Write-Host $m }

# The outermost net. Anything that throws - a blown timeout, a failed assertion, a refused call -
# lands here, says so in one line WITH the last state the game reported, puts ai_enabled back and
# exits non-zero. `trap` rather than a wrapping try/catch so the body needs no re-indenting.
function Fail($e) {
    $st = 'unavailable'
    try { $s = PP 'state' $null; $st = "phase=$($s.phase) scene=$($s.scene) levelState=$($s.levelState)" } catch { }
    if ($script:aiWas) { try { PP 'var' @{ name = 'ai_enabled'; value = ($script:aiWas -eq 'True') } | Out-Null } catch { } }
    [Console]::Error.WriteLine("SPIDER-DEMO FAILED: $e | last state: $st")
    exit 1
}
trap { Fail $_ }

# ---------------------------------------------------------------- transport

function PP([string] $verb, $argObj) {
    $json = if ($null -ne $argObj) { $argObj | ConvertTo-Json -Depth 16 -Compress } else { $null }
    $out  = if ($json) { & $ppcli connect $verb $json -PPRoot $PPRoot -ProfileId $ProfileId -TimeoutSeconds $ClientTimeoutSec 2>$null }
            else       { & $ppcli connect $verb      -PPRoot $PPRoot -ProfileId $ProfileId -TimeoutSeconds $ClientTimeoutSec 2>$null }
    $r = ($out | ConvertFrom-Json)
    if ($r.status -ne 'done') { throw "verb '$verb' -> $($r | ConvertTo-Json -Depth 8 -Compress)" }
    $r.result
}
function Call($op) { $r = PP 'call' $op; if (-not $r.ok) { throw "call failed: $($r.error)" }; $r.value }
function Get1($target, $member) { Call @{ op = 'get'; target = $target; member = $member } }
# Ability.Activate is declared three times with the identical signature (Ability / TacticalAbility /
# MoveAbility); without this exact `type` filter the binder refuses it as ambiguous.
function Activate($ability, $target) {
    Call @{ op = 'invoke'; target = $ability; type = 'Base.Entities.Abilities.Ability';
            member = 'Activate'; args = @(@{ '$h' = $target }) } | Out-Null
}
function Plan([string] $file, $vars) {
    $out = & $ppcli plan (Join-Path $plans $file) ($vars | ConvertTo-Json -Depth 8 -Compress) `
                   -PPRoot $PPRoot -ProfileId $ProfileId -TimeoutSeconds $ClientTimeoutSec 2>$null
    $r = ($out | ConvertFrom-Json).result
    if (-not $r.ok) { throw "plan $file failed at '$($r.step)': $($r.error)" }
    $r.output
}
function Items($h, $n = 20) { (PP 'items' @{ h = $h; pageSize = $n }).items }
function V3($p) { @{ '$v3' = @($p.x, $p.y, $p.z) } }
function Dist($a, $b) { [Math]::Sqrt([Math]::Pow($a.x - $b.x, 2) + [Math]::Pow($a.z - $b.z, 2)) }
function Assert([bool] $ok, [string] $what) { if (-not $ok) { throw "FAILED: $what" }; Say "  PASS $what" }
# Every stat here is a Base.Entities.Statuses.StatusStat; .Value projects as a ModifiableValue.
function Hp($actor) { (Get1 (Get1 $actor 'Health').h 'Value').BaseValue }

# ---------------------------------------------------------------- 1. deploy + activate + launch

if (-not $NoDeploy) {
    & (Join-Path (Split-Path $PSScriptRoot -Parent) 'ContentTool\deploy.ps1') -PPRoot $PPRoot |
        Where-Object { $_ -like 'Deployed*' } | ForEach-Object { Say $_ }
    & $ppcli deploy -PPRoot $PPRoot -ProfileId $ProfileId | Out-Null
}

# The client REFUSES to launch when a mod is missing from MOD_ACTIVATED, and PP itself rewrites the
# array EMPTY when a mod fails to load - so this is asserted every run, not assumed. Raw-text
# surgery: re-serialising this file shrinks it (ppcli.ps1's own read-only note says the same).
$jopt = Join-Path $env:USERPROFILE "AppData\LocalLow\Snapshot Games Inc\Phoenix Point\Steam\$ProfileId\Options.jopt"
$want = 'com.morgott.ContentTool', 'com.morgott.PPBridge', 'morgott.demo.customcreature'
$raw  = [IO.File]::ReadAllText($jopt)
$add  = @($want | Where-Object { $raw -notlike "*`"$_`"*" })
if ($add.Count) {
    Copy-Item $jopt "$jopt.spiderdemo-bak" -Force
    $j     = $raw | ConvertFrom-Json
    $key   = ($j.Contents.Objects | Where-Object TopLevel).ObjectValue.CollectionValues |
             Where-Object Key -eq 'MOD_ACTIVATED'
    $arr   = ($j.Contents.Objects | Where-Object ObjectID -eq $key.Value.ObjectID).ObjectValue
    $first = $arr.CollectionValues[0]
    $count = $arr.CollectionValues.Count
    $lines = ($add | ForEach-Object { "            `"$_`"," }) -join "`n"
    $old   = "              $count`n            ]`n          },`n          `"CollectionValues`": [`n            `"$first`","
    $new   = "              $($count + $add.Count)`n            ]`n          },`n          `"CollectionValues`": [`n$lines`n            `"$first`","
    if (-not $raw.Contains($old)) { throw "MOD_ACTIVATED anchor not found in $jopt" }
    [IO.File]::WriteAllText($jopt, $raw.Replace($old, $new))
    Say "activated: $($add -join ', ')"
}

$exe  = Join-Path $PPRoot 'PhoenixPointWin64.exe'
$mine = @(Get-CimInstance Win32_Process -Filter "Name='PhoenixPointWin64.exe'" |
          Where-Object { $_.ExecutablePath -and (Get-Item $_.ExecutablePath).FullName -eq (Get-Item $exe).FullName })
if (-not $mine.Count -and -not $NoLaunch) {
    $log = Join-Path $env:TEMP 'spider-demo.log'
    if (Test-Path $log) { Remove-Item $log -Force }
    $g = Start-Process -FilePath $exe -ArgumentList '-mods', '-logFile', $log -PassThru
    Say "launched PID $($g.Id), log $log"
    # Gate discipline, and `state` ANSWERING is not the gate. PPBridge replies as soon as the mod
    # inits, minutes before the menu exists; a load_game issued into that window is accepted, logs
    # nothing and never happens - which reads exactly like a hung plan. The gate is the main menu
    # actually standing: HomeScreen, Playing. Measured: from that state load_game 4 reaches
    # tactical in 15s, every time.
    $t0 = Get-Date
    do {
        Start-Sleep -Seconds 3
        if ($g.HasExited) { throw 'the game exited before the pipe came up' }
        $up = $false
        try { $s = PP 'state' $null; $up = $s.ok -and $s.scene -eq 'HomeScreen' -and $s.levelState -eq 'Playing' } catch { }
    } until ($up -or ((Get-Date) - $t0).TotalSeconds -gt $LaunchTimeoutSec)
    if (-not $up) { throw "the main menu never came up within ${LaunchTimeoutSec}s" }
}

# ---------------------------------------------------------------- 2. the mission

$st = PP 'state' $null
if ($st.phase -ne 'tactical' -or $Reload) {
    Say "loading save '$SaveName'"
    $tacWas = (PP 'roots' $null).roots.tac.instanceId
    # The plan file's own 420s waits are overridden: a load that was swallowed must fail with a
    # named error inside the client's poll ceiling instead of looking like a hang. Measured load
    # time from a settled menu: ~15-20s.
    $m = Plan 'load-mission.json' @{ name = $SaveName
                                     phaseTimeoutMs = $LoadTimeoutSec * 1000
                                     readyTimeoutMs = $LoadTimeoutSec * 1000 }
    Say "  $($m.scene) $($m.levelState) turn $($m.turn)"
    # `restore` is ISSUE-ONLY, and a tactical level that is already up satisfies both the plan's
    # phase wait AND HasAnyTurnStarted before the new load has begun tearing it down - so the plan
    # can report ready about the OLD level and @tac then goes null under you (measured twice).
    # The controller's own instanceId is the identity that cannot lie.
    $t0 = Get-Date
    do {
        Start-Sleep -Milliseconds 500
        $tacNow = (PP 'roots' $null).roots.tac
    } until (($tacNow -and $tacNow.instanceId -ne $tacWas) -or ((Get-Date) - $t0).TotalSeconds -gt $LoadTimeoutSec)
    if (-not $tacNow -or $tacNow.instanceId -eq $tacWas) { throw "the reloaded tactical level never came up within ${LoadTimeoutSec}s" }
}
PP 'snapshot' @{ name = 'spider_demo_before'; timeoutMs = ($LoadTimeoutSec * 1000) } | Out-Null
$aiWas = (PP 'var' @{ name = 'ai_enabled' }).value
PP 'var' @{ name = 'ai_enabled'; value = $false } | Out-Null
# Marks every actor located; without it a freshly spawned hostile is not knowledge the shooting
# faction has, and GetTargets offers nothing.
PP 'console' @{ command = 'locate_all'; args = @() } | Out-Null
# Gate on the root alias itself, not only on the phase: a `wait` predicate that ERRORS counts as
# "not true yet", which is exactly the shape of a level still settling.
PP 'wait' @{ ready = $true; timeoutMs = ($LoadTimeoutSec * 1000) } | Out-Null
PP 'wait' @{ timeoutMs = ($ActionTimeoutSec * 1000); everyFrames = 10
             call = @{ op = 'get'; target = '@tac'; member = 'CurrentFaction' } } | Out-Null

try {

# ---------------------------------------------------------------- 3. the spider

# The def by name out of the live repository - never a typed guid.
$spiderDef = (PP 'find' @{ query = 'customcreature_CharacterTemplateDef'
                           type  = 'PhoenixPoint.Tactical.Entities.TacActorDef' }).defs[0]
$victimDef = (PP 'find' @{ query = 'Swarmer_TacCharacterDef'
                           type  = 'PhoenixPoint.Tactical.Entities.TacActorDef' }).defs |
             Where-Object name -eq 'Swarmer_TacCharacterDef'
Say "spider $($spiderDef.name) $($spiderDef.guid)"

# A live actor of the faction on turn is a guaranteed-valid map coordinate to derive from.
# @selected is cheaper but reads null once a selection has been reset or the selected actor died.
$anchor = Get1 (Items (Get1 (Get1 '@tac' 'CurrentFaction').h 'TacticalActors').h 40 |
                Where-Object type -like '*.TacticalActor' | Select-Object -First 1).h 'Pos'
$spider = $null
foreach ($a in 0..7) {
    $r = [Math]::PI / 4 * $a
    $x = [Math]::Floor($anchor.x + 3 * [Math]::Cos($r)) + 0.5
    $z = [Math]::Floor($anchor.z + 3 * [Math]::Sin($r)) + 0.5
    # faction "" = the faction currently on turn, so StartTurn below is the right housekeeping.
    try { $spider = Plan 'spawn-at-coordinate.json' @{ defName = $spiderDef.guid; faction = ''; x = $x; z = $z
                                                       readyTimeoutMs = $ActionTimeoutSec * 1000
                                                       preloadTimeoutMs = $LoadTimeoutSec * 1000 }; break }
    catch { }
}
Assert ($null -ne $spider) "spider spawned at ($($spider.achieved.x), $($spider.achieved.z))"
$S = $spider.actor
$start = Get1 $S 'Pos'
Assert ((Get1 $S 'InPlay') -eq $true) "InPlay"
Say "  $(Get1 $S 'DisplayName') / $($spider.actorName), HP $(Hp $S)"

# TacticalDeployZone.cs:419 does this when the new actor's faction is the level's CurrentFaction;
# the shipped plan deliberately skips it. Without it the actor has neither ability traits nor AP
# (TacticalActor.RestartAbilities:1239-1250) and every later reading measures a half-started turn.
Call @{ op = 'invoke'; target = $S; member = 'StartTurn'; args = @() } | Out-Null
# Nobody is pointing a camera at it, and an unwatched Animator fires no animation events - which is
# a 10s stall per blocking event (AnimEventReceiver.cs:100,126).
Call @{ op = 'set'; target = (Get1 $S 'Animator').h; member = 'cullingMode'
        value = @{ '$enum' = 'AlwaysAnimate'; type = 'UnityEngine.AnimatorCullingMode' } } | Out-Null
Call @{ op = 'set'; target = '@view'; member = 'SelectedActor'; value = @{ '$h' = $S } } | Out-Null

$eq   = (Get1 $S 'Equipments').h
$move = $null; $bash = $null; $spit = $null
foreach ($ab in Items (Get1 $S 'Abilities').h 40) {
    switch -Wildcard ($ab.type) {
        '*.MoveAbility' { $move = $ab.h }
        '*.BashAbility' { $bash = $ab.h }
        '*.ShootAbility' {
            $w = Get1 $ab.h 'Source'
            if ((Get1 $w.h 'WeaponDef').name -like '*_RangedWeaponDef') { $spit = $ab.h; $spitWeapon = $w.h }
        }
    }
}
$meleeWeapon = (Get1 $bash 'Source').h
Assert ($move -and $bash -and $spit) "move / bash / spit abilities resolved"
Say "  spit = $((Get1 $spit 'ShootAbilityDef').name), range $((Get1 (Get1 (Get1 $spitWeapon 'WeaponDef').h 'DamagePayload').h 'Range')) tiles"

# ---------------------------------------------------------------- 4. the victim, 8 tiles out

# 8 tiles is deliberately outside the spitter's 5-tile payload range: the spider has to close.
# Every candidate below is judged by an engine answer, never by geometry: GetTargetDataFor is null
# for anything unreachable inside MaxMoveRange, and GetTargetsAt asks the ability itself what it
# could hit FROM a position - range and line of sight together - before a step is taken.
function Ring($centre, [double] $r) {
    0..7 | ForEach-Object {
        $a = [Math]::PI / 4 * $_
        # Floor, never Round: tile centres sit on .5, and Round(7.5) is 8 under banker's rounding,
        # which quietly pushes every ring one tile out (measured as a 2.00 "melee" distance).
        @{ x = [Math]::Floor($centre.x + $r * [Math]::Cos($a)) + 0.5; y = 0.0
           z = [Math]::Floor($centre.z + $r * [Math]::Sin($a)) + 0.5 }
    }
}
function Reachable($to) { Call @{ op = 'invoke'; target = $move; member = 'GetTargetDataFor'; args = @((V3 $to), $false, $null) } }
function Sees($from) { @(Items (Call @{ op = 'invoke'; target = $spit; member = 'GetTargetsAt'; args = @((V3 $from)) }).h).Count -gt 0 }

# A spot is only good if, once the victim stands there, some tile inside spit range can be reached
# AND sees it. That cannot be known before the spawn, so a rejected spot's victim is killed off
# rather than left standing about. The firing point is picked around the VICTIM, not along the
# outbound ray: rounding a ray to whole tiles can land the melee square on the victim itself.
$victim = $null; $approach = $null
foreach ($p in Ring $start 8) {
    if (-not (Reachable $p)) { continue }          # unwalkable ground - skip it BEFORE spawning into it
    try { $v = Plan 'spawn-at-coordinate.json' @{ defName = $victimDef.guid; faction = 'alien'; x = $p.x; z = $p.z
                                                  readyTimeoutMs = $ActionTimeoutSec * 1000
                                                  preloadTimeoutMs = $LoadTimeoutSec * 1000 } }
    catch { continue }
    $vp = Get1 $v.actor 'Pos'
    # Nearest ring first: GetTargetsAt proves range and perception, but NOT that the projectile
    # line is clear - a spit fired over 4 tiles was measured hitting a DestructableDamageReceiver
    # instead of the target. A short line has less in it, and the shortlist is retried in order.
    $approach = @(3, 4 | ForEach-Object { Ring $vp $_ } | Where-Object { (Reachable $_) -and (Sees $_) })
    if ($approach.Count) { $victim = $v; $vpos = $vp; break }
    Say "  (($($vp.x), $($vp.z)): no firing point reaches it - discarding $($v.actorName))"
    $vh = (Get1 $v.actor 'Health').h
    Call @{ op = 'invoke'; target = $vh; member = 'Subtract'; args = @([double](Get1 $vh 'Value').BaseValue) } | Out-Null
}
Assert ($null -ne $victim) "victim $($victimDef.name) spawned at ($($victim.achieved.x), $($victim.achieved.z))"
$V = $victim.actor
Say "  start distance $('{0:F2}' -f (Dist $start $vpos)) tiles, $($approach.Count) firing point(s) shortlisted"

# ---------------------------------------------------------------- 5. the scenario

$ap = (Get1 (Get1 $S 'CharacterStats').h 'ActionPoints').h
# A scripted run spends a whole turn's AP inside one turn, using the game's own setter rather than
# writing the stat: SetToMax is what the engine itself calls at turn start.
function Refill { Call @{ op = 'invoke'; target = $ap; member = 'SetToMax'; args = @() } | Out-Null }
function Walk($to, [string] $what) {
    Refill
    $td = Call @{ op = 'invoke'; target = $move; member = 'GetTargetDataFor'; args = @((V3 $to), $false, $null) }
    if (-not $td) { throw "FAILED: $what - no path" }
    $len = Get1 $td.h 'PathLength'
    Activate $move (Call @{ op = 'invoke'; target = $td.h; member = 'ToTarget'; args = @() }).h
    # Arrival is NOT completion: the actor reaches the tile while MoveAbility is still executing and
    # the AP charge lands after it ends (MoveAbility.OnPlayingActionEnd:83-90). Refilling in that
    # window gets overwritten - measured as 16 -> 4.51 AP and a NotEnoughActionPoints spit.
    $t0 = Get-Date
    do {
        Start-Sleep -Milliseconds 300
        $p = Get1 $S 'Pos'
        $done = (Dist $p $to) -lt 0.3 -and -not (Get1 $move 'IsExecuting')
    } until ($done -or ((Get-Date) - $t0).TotalSeconds -gt $ActionTimeoutSec)
    Assert $done ("$what - path $('{0:F2}' -f $len) tiles in $('{0:F2}' -f ((Get-Date)-$t0).TotalSeconds)s")
    $p
}
function OfferFor($ability, [string] $actorName) {
    foreach ($o in Items (Call @{ op = 'invoke'; target = $ability; member = 'GetTargets'; args = @() }).h) {
        if ((Get1 $o.h 'Actor').name -eq $actorName) { return $o.h }
    }
    $null
}
function AwaitHp($actor, $was, [int] $secs = $ActionTimeoutSec) {
    $t0 = Get-Date
    do { Start-Sleep -Milliseconds 300; $now = Hp $actor } while ($now -eq $was -and ((Get-Date) - $t0).TotalSeconds -lt $secs)
    $now
}
function DisabledKey($ability) {
    (Get1 (Call @{ op = 'invoke'; target = $ability; member = 'GetDisabledState'; args = @($null) }).h 'Key')
}

Say 'a) close to spit range, then spit'
$before = Hp $V; $after = $before
foreach ($fp in $approach) {
    $p = Walk $fp 'moved into spit range'
    Assert ((Dist $p $vpos) -le 5.0) ("distance $('{0:F2}' -f (Dist $p $vpos)) <= the 5-tile payload range")
    Refill
    # Both attacks read the SELECTED equipment; without this the button is EquipmentNotSelected.
    Call @{ op = 'invoke'; target = $eq; member = 'SetSelectedEquipment'; args = @(@{ '$h' = $spitWeapon }) } | Out-Null
    Assert ((DisabledKey $spit) -eq 'NotDisabled') 'the spit is OFFERED, not just activatable'
    $offer = OfferFor $spit $victim.actorName
    Assert ($null -ne $offer) "the victim is among the spit's own GetTargets offers"
    $before = Hp $V
    Activate $spit $offer
    $after = AwaitHp $V $before
    if ($after -lt $before) { break }
    Say '  (the spit landed on cover, not on the target - shifting to the next firing point)'
}
Assert ($after -lt $before) "poison spit landed: HP $before -> $after"
$dot = @(Items (Get1 (Get1 $V 'Status').h 'Statuses').h | Where-Object type -like '*DamageOverTimeStatus')
Assert ($dot.Count -gt 0) "poison applied: $((Get1 $dot[0].h 'Def').name)"

Say 'b/c) close to melee, then bash'
$meleePos = Ring $vpos 1 | Where-Object { Reachable $_ } | Sort-Object { Dist $_ $p } | Select-Object -First 1
Assert ($null -ne $meleePos) 'a reachable tile next to the victim'
$p = Walk $meleePos 'moved into melee range'
Assert ((Dist $p $vpos) -le 1.5) ("melee distance $('{0:F2}' -f (Dist $p $vpos))")
Refill
Call @{ op = 'invoke'; target = $eq; member = 'SetSelectedEquipment'; args = @(@{ '$h' = $meleeWeapon }) } | Out-Null
Assert ((DisabledKey $bash) -eq 'NotDisabled') 'the melee button is LIT'
$offer = OfferFor $bash $victim.actorName
Assert ($null -ne $offer) 'the victim is among the bash offers'
$before = Hp $V
$t0 = Get-Date
Activate $bash $offer
$after = AwaitHp $V $before
Assert ($after -lt $before) "bash landed: HP $before -> $after in $('{0:F2}' -f ((Get-Date)-$t0).TotalSeconds)s"

Say 'd) retreat'
$p = Walk $start 'retreated to the spawn point'
Say "  distance now $('{0:F2}' -f (Dist $p $vpos)) tiles"

Say 'e) death'
# Health.Subtract is the very call TacticalActorBase.ApplyDamageInternal:874 makes, and
# OnHealthChange:616-622 is the ONLY route to Die().
$hs = (Get1 $S 'Health').h
$was = Hp $S
Call @{ op = 'invoke'; target = $hs; member = 'Subtract'; args = @(1.0) } | Out-Null
Assert ((Hp $S) -eq ($was - 1)) "health is writable: $was -> $(Hp $S)"
Call @{ op = 'invoke'; target = $hs; member = 'Subtract'; args = @([double]$was) } | Out-Null
$t0 = Get-Date
do { Start-Sleep -Milliseconds 300; $dead = Get1 $S 'IsDead' } while (-not $dead -and ((Get-Date) - $t0).TotalSeconds -lt $ActionTimeoutSec)
Assert ($dead -eq $true) "IsDead after $('{0:F2}' -f ((Get-Date)-$t0).TotalSeconds)s, HP $(Hp $S)"
$die = Call @{ op = 'invoke'; target = $S; type = 'PhoenixPoint.Tactical.Entities.TacticalActorBase'
               member = 'GetPreferredDieAbility'; args = @() }
Assert ($die.type -like '*RagdollDieAbility') "die ability $($die.type)"
$t0 = Get-Date
do { Start-Sleep -Milliseconds 300; $n = (Get1 $S 'ExecutingAbilities').count } while ($n -and ((Get-Date) - $t0).TotalSeconds -lt $ActionTimeoutSec)
Assert (-not $n) 'no ability left executing'
# InPlay stays TRUE on a corpse - the game leaves the body on the map. Read, not asserted away.
Say "  InPlay after death: $(Get1 $S 'InPlay') (the corpse stays on the map - this is the game's own behaviour)"

Say 'SCENARIO PASS'

}
finally {
    # One exit for every door: success, a failed assertion, Ctrl+C.
    try { PP 'var' @{ name = 'ai_enabled'; value = ($aiWas -eq 'True') } | Out-Null; Say "restored ai_enabled=$aiWas" }
    catch { Write-Warning "could NOT restore ai_enabled (was $aiWas): $_" }
}
