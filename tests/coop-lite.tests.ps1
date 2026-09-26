<#
  Offline check for coop.ps1 -Lite's profile + registry surgery: the lite values land, the originals are
  backed up ONCE, restore puts back exactly those keys (and leaves anything the game saved meanwhile),
  and a profile -Lite cannot edit is refused by name. No game, no real profile, no real PlayerPrefs.

      pwsh -NoProfile -File .\tests\coop-lite.tests.ps1            # must exit 0
      pwsh -NoProfile -File .\tests\coop-lite.tests.ps1 -Falsify   # must ALSO exit 0

  -Falsify corrupts every expectation and demands that EVERY assertion fails (see paths.tests.ps1).
#>
param([switch] $Falsify)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path $PSScriptRoot 'fixture-coop-lite'
$regKey = 'HKCU:\Software\PPCLI-coop-lite-test'
function Remove-Scratch {
    Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $regKey -Recurse -Force -ErrorAction SilentlyContinue
}
Remove-Scratch

. (Join-Path $root 'tools\coop-lite.ps1')
$script:LiteDir = Join-Path $scratch 'backup'
$script:LiteProfiles = Join-Path $scratch 'profiles'
$script:LiteRegKey = $regKey

# A fake install whose Goldberg id points at a fake profile shaped like the game's Options.jopt:
# a top-level dictionary whose values are references to boxed objects.
$install = Join-Path $scratch 'PP-Instance9'
$sid = Join-Path $install 'PhoenixPointWin64_Data\Plugins\x86_64\steam_settings'
New-Item -ItemType Directory -Force -Path $sid | Out-Null
Set-Content (Join-Path $sid 'force_steamid.txt') '76561190000000009' -Encoding utf8NoBOM
$profileDir = Join-Path $script:LiteProfiles '76561190000000009'
New-Item -ItemType Directory -Force -Path $profileDir | Out-Null
$jopt = Join-Path $profileDir 'Options.jopt'

$orig = [ordered]@{}
foreach ($k in (LiteValues 1 1).Keys) { $orig[$k] = $true }
$orig['Options_ScreenWidth'] = 2560; $orig['Options_ScreenHeight'] = 1440; $orig['Options_UsedPreset'] = 5
$orig['Options_QualityLevelBinding'] = 'Ultra'; $orig['Options_ShadowsDistance'] = 150
$orig['Options_ScreenMode'] = [ordered]@{ EnumName = 'FullScreenWindow'; EnumValue = 1 }
$orig['Options_TextureQuality'] = [ordered]@{ EnumName = 'Ultra'; EnumValue = 5 }
$orig['Options_NewGameDifficultyOption'] = 1
$cv = @(); $objs = @(); $id = 2
foreach ($k in $orig.Keys) {
    $cv += [ordered]@{ '#Type' = 3; Key = $k; Value = [ordered]@{ ObjectID = $id } }
    $objs += [ordered]@{ ObjectID = $id; TopLevel = $false; ObjectValue = [ordered]@{ '#Type' = 4; BoxedValue = $orig[$k] } }
    $id++
}
$doc = [ordered]@{ Version = 1; Contents = [ordered]@{
        Objects = @([ordered]@{ ObjectID = 1; TopLevel = $true; ObjectValue = [ordered]@{ '#Type' = 2; CollectionValues = $cv } }) + $objs
        Types   = @([ordered]@{ '#Type' = 1; TypeName = 'Base.Serialization.General.TypeData'; Id = 1; Version = 1 }) } }
[IO.File]::WriteAllText($jopt, ($doc | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
$pristine = [IO.File]::ReadAllText($jopt)

try {

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
        $msg = $_.Exception.Message
        if ($msg -like "*$mustSay*") { $script:passed++; Write-Host "  ok   $what"; return }
        $script:failed++; Write-Host "  FAIL $what : refused, but never said '$mustSay' - $msg"; return
    }
    $script:failed++
    Write-Host "  FAIL $what : nothing was thrown"
}
function Boxed([string] $key) {
    $d = Get-Content $jopt -Raw | ConvertFrom-Json -Depth 20
    $ref = ($d.Contents.Objects[0].ObjectValue.CollectionValues | Where-Object Key -eq $key).Value.ObjectID
    ($d.Contents.Objects | Where-Object ObjectID -eq $ref).ObjectValue.BoxedValue | ConvertTo-Json -Compress
}

Write-Host "coop-lite ($(if ($Falsify) { 'FALSIFY' } else { 'normal' }))"

Assert-Value 'the install maps to the profile its force_steamid names' (LiteProfileJopt $install) $jopt

LiteApply $install 1258 628 | Out-Null
Assert-Value 'lite width lands in the profile' (Boxed 'Options_ScreenWidth') '1258'
Assert-Value 'lite height lands in the profile' (Boxed 'Options_ScreenHeight') '628'
Assert-Value 'lite window mode is Windowed (3)' (Boxed 'Options_ScreenMode') '{"EnumName":"Windowed","EnumValue":3}'
Assert-Value 'lite preset is the VeryLow one (index 0)' (Boxed 'Options_UsedPreset') '0'
Assert-Value 'lite takes the override path (per-field values, no benchmark)' (Boxed 'Options_HasGraphicsOverride') 'true'
Assert-Value 'lite textures are VeryLow' (Boxed 'Options_TextureQuality') '{"EnumName":"VeryLow","EnumValue":0}'
Assert-Value 'lite shadows are off' (Boxed 'Options_Shadows') 'false'
Assert-Value 'a key -Lite does not own is untouched' (Boxed 'Options_NewGameDifficultyOption') '1'

# A second -Lite launch (relaunch after kill) must not replace the originals with lite values.
LiteApply $install 800 450 | Out-Null
$bak = Get-Content (LiteBackupPath $install) -Raw | ConvertFrom-Json
Assert-Value 'the backup still holds the ORIGINAL width after a second apply' $bak.values.Options_ScreenWidth '2560'
Assert-Value 'the backed-up install is listed for stop/restore' ((LiteBackedRoots) -join ',') $install

# Restore after the game saved something of its own meanwhile: only -Lite's keys go back.
JoptWrite $jopt ([ordered]@{ 'Options_NewGameDifficultyOption' = '3' })
Assert-Value 'restore reports it restored' (LiteRestore $install) 'True'
Assert-Value 'restore puts the original width back' (Boxed 'Options_ScreenWidth') '2560'
Assert-Value 'restore puts the original mode back' (Boxed 'Options_ScreenMode') '{"EnumName":"FullScreenWindow","EnumValue":1}'
Assert-Value 'restore keeps what the game saved meanwhile' (Boxed 'Options_NewGameDifficultyOption') '3'
Assert-Value 'the backup is consumed' ([bool](Test-Path (LiteBackupPath $install))) 'False'
Assert-Value 'a second restore is a no-op' (LiteRestore $install) 'False'
JoptWrite $jopt ([ordered]@{ 'Options_NewGameDifficultyOption' = '1' })
Assert-Value 'apply + restore round-trips the file byte for byte' ([IO.File]::ReadAllText($jopt) -ceq $pristine) 'True'

# Unity's shared PlayerPrefs: existing values come back, ones -Lite's run created are removed.
New-Item $regKey -Force | Out-Null
New-ItemProperty $regKey -Name 'Screenmanager Resolution Width_h182942802' -Value 2560 -PropertyType DWord | Out-Null
New-ItemProperty $regKey -Name 'I2 Language_h3293684300' -Value 7 -PropertyType DWord | Out-Null
LiteRegSnapshot
Set-ItemProperty $regKey -Name 'Screenmanager Resolution Width_h182942802' -Value 1258 -Type DWord
New-ItemProperty $regKey -Name 'UnityGraphicsQuality_h1669003810' -Value 0 -PropertyType DWord | Out-Null
Set-ItemProperty $regKey -Name 'I2 Language_h3293684300' -Value 9 -Type DWord
LiteRegSnapshot   # a second snapshot must keep the first one
Assert-Value 'registry restore reports it restored' (LiteRegRestore) 'True'
Assert-Value 'registry width is back' ((Get-ItemProperty $regKey).'Screenmanager Resolution Width_h182942802') '2560'
Assert-Value 'a screen value that did not exist before is removed' ([bool]((Get-Item $regKey).Property -contains 'UnityGraphicsQuality_h1669003810')) 'False'
Assert-Value 'a value outside the screen/quality set is left alone' ((Get-ItemProperty $regKey).'I2 Language_h3293684300') '9'

# `stop` must wait until a killed game is gone from the process LIST (what the restore checks), not
# until Wait-Process calls it exited - field run 10 restored nothing because of that gap.
$script:calls = 0
$left = LiteWaitExit @(4242) 10 { param($want) $script:calls++; if ($script:calls -le 2) { $want } else { @() } }
Assert-Value 'stop waits while a killed game is still listed, then reports none left' "$($left.Count) after $($script:calls) polls" '0 after 3 polls'
$left = LiteWaitExit @(4242, 4343) 1 { param($want) $want }
Assert-Value 'a game still listed at the deadline is reported, not waited on forever' ($left -join ',') '4242,4343'
$child = Start-Process pwsh -ArgumentList '-NoProfile', '-Command', 'Start-Sleep 120' -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 500
Stop-Process -Id $child.Id -Force
$left = LiteWaitExit @($child.Id) 30
Assert-Value 'a real killed process: nothing reported left' $left.Count '0'
Assert-Value 'a real killed process: Get-Process no longer lists it when the wait returns' @(Get-Process -ErrorAction SilentlyContinue | Where-Object Id -eq $child.Id).Count '0'

# The -Lite commit bar: three lite peers need what field run 10 had to pass by hand (-MinFreeCommitGB 24).
Assert-Value 'the lite commit bar for three peers' (3 * $script:LiteCommitGBPerPeer) '24'

# A profile that never saved its video options cannot be edited in place: refused by name.
[IO.File]::WriteAllText($jopt, '{"Version":1,"Contents":{"Objects":[{"ObjectID":1,"TopLevel":true,"ObjectValue":{"#Type":2,"CollectionValues":[]}}]}}')
Assert-Refusal 'a profile without the keys is refused, naming the fix' 'open the game''s Options once' { LiteApply $install 1 1 }
Assert-Value 'the refused apply wrote no backup' ([bool](Test-Path (LiteBackupPath $install))) 'False'

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
