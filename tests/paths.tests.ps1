<#
  Offline check for the first-run path: install discovery, profile discovery, and the two refusals a
  stranger hits before anything else. No game, no pipe, no framework.

      pwsh -NoProfile -File .\tests\paths.tests.ps1            # must exit 0
      pwsh -NoProfile -File .\tests\paths.tests.ps1 -Falsify   # must ALSO exit 0

  -Falsify corrupts every expectation on purpose and demands that EVERY assertion fails, for the
  same reason as resolve-names.tests.ps1: a helper that quietly passes on empty input has already
  cost this repo a day.
#>
param([switch] $Falsify)

$ErrorActionPreference = 'Stop'
$root  = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path $PSScriptRoot 'fixture-paths'
function Remove-Scratch { Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue }
Remove-Scratch

# Two fake Steam libraries and one that holds nothing, plus a fake install complete enough for the
# client's preflight to get as far as the arm marker (an exe, a deployed DLL, no marker).
$libA    = Join-Path $scratch 'libA'
$libB    = Join-Path $scratch 'libB'
$libNone = Join-Path $scratch 'libNone'
foreach ($lib in $libA, $libB) {
    $game = Join-Path $lib 'steamapps\common\Phoenix Point'
    New-Item -ItemType Directory -Force -Path (Join-Path $game 'Mods\PPBridge') | Out-Null
    Set-Content -Path (Join-Path $game 'PhoenixPointWin64.exe') -Value 'not really an exe' -Encoding utf8NoBOM
    Set-Content -Path (Join-Path $game 'Mods\PPBridge\PPBridge.dll') -Value 'not really a dll' -Encoding utf8NoBOM
}
New-Item -ItemType Directory -Force -Path $libNone | Out-Null
$installA = Join-Path $libA 'steamapps\common\Phoenix Point'

$profilesOne  = Join-Path $scratch 'profiles-one'
$profilesTwo  = Join-Path $scratch 'profiles-two'
$profilesNone = Join-Path $scratch 'profiles-none'
New-Item -ItemType Directory -Force -Path (Join-Path $profilesOne '11111111111111111') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $profilesTwo '11111111111111111') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $profilesTwo '22222222222222222') | Out-Null
New-Item -ItemType Directory -Force -Path $profilesNone | Out-Null

. (Join-Path $root 'paths.ps1')

try {

$script:passed = 0
$script:failed = 0

function Assert-Value([string] $what, $actual, [string] $expected) {
    if ($Falsify) { $expected = $expected + '~falsified' }
    if ([string]$actual -ceq $expected) { $script:passed++; Write-Host "  ok   $what" }
    else { $script:failed++; Write-Host "  FAIL $what : got '$actual', wanted '$expected'" }
}

# The refusal TEXT is asserted, not just "it threw": every one of these paths can throw for an
# unrelated reason, and a refusal that does not name the fix is the bug being guarded against.
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

Write-Host "paths ($(if ($Falsify) { 'FALSIFY' } else { 'normal' }))"

Assert-Value 'an install is a folder with PhoenixPointWin64.exe in it' `
    (Test-PPInstall $installA) 'True'
Assert-Value 'a folder without the exe is not an install' `
    ([bool](Test-PPInstall $libNone)) 'False'

Assert-Value 'exactly one library with a game is the answer' `
    (Find-PPInstall @($libNone, $libA)) ((Get-Item $installA).FullName)
Assert-Refusal 'no install anywhere refuses and names -PPRoot' '-PPRoot' `
    { Find-PPInstall @($libNone) }
Assert-Refusal 'two installs refuse rather than pick one' '2 Phoenix Point installs found' `
    { Find-PPInstall @($libA, $libB) }

# The pin is what stops a bare `deploy` writing into the install Steam happens to know about, which
# on a machine with a separate automation copy is the game its owner plays.
$pinGood = Join-Path $scratch 'pin-good.txt'
$pinBad  = Join-Path $scratch 'pin-bad.txt'
Set-Content -Path $pinGood -Value $installA -Encoding utf8NoBOM
Set-Content -Path $pinBad  -Value $libNone  -Encoding utf8NoBOM
Assert-Value 'no pin file at all leaves discovery exactly as it was' `
    ([string](Get-PPPinnedInstall (Join-Path $scratch 'pin-absent.txt'))) ''
Assert-Value 'a pin file names the install to automate' `
    (Get-PPPinnedInstall $pinGood) ((Get-Item $installA).FullName)
Assert-Refusal 'a pin pointing at something that is not an install refuses, and names how to undo it' 'Remove-Item' `
    { Get-PPPinnedInstall $pinBad }

# The incident this guards: a deploy with no -PPRoot went to the play install because discovery found it.
$installB = Join-Path $libB 'steamapps\common\Phoenix Point'
$gateOut = & pwsh -NoProfile -File (Join-Path $root 'deploy.ps1') -PPRoot $installB -PinFile $pinGood 2>&1
Assert-Value 'deploy refuses an install other than the pinned one, and names -Force' `
    ($(if ("$gateOut" -like '*-Force*') { 'refused' } else { "wrong:$gateOut" })) 'refused'
Assert-Value 'the refused deploy wrote nothing into it' `
    ([bool](Test-Path (Join-Path $installB 'Mods\PPBridge\meta.json'))) 'False'

# THE FALSE POSITIVE THIS REPLACED: the preflight used to ask whether 'com.morgott.PPBridge' appeared
# ANYWHERE in Options.jopt. A mod that is present but switched OFF still leaves its id in the file, so
# the check passed and the run went on to launch a game where the mod loads and says nothing - the one
# failure the preflight exists to catch. $joptOff is exactly that file: the id is in it, and it is not
# in MOD_ACTIVATED.
$joptShape = @'
{"Version":1,"Contents":{"Objects":[
 {"ObjectID":1,"TopLevel":true,"ObjectValue":{"CollectionValues":[
   {"Key":"IsModsOpenedFirstTime","Value":{"ObjectID":9}},
   {"Key":"MOD_ACTIVATED","Value":{"ObjectID":17}}]}},
 {"ObjectID":9,"TopLevel":false,"ObjectValue":{"BoxedValue":true}},
 {"ObjectID":17,"TopLevel":false,"ObjectValue":{
   "ArrayDimensions":{"CollectionValues":[__N__]},"CollectionValues":[__LIST__]}},
 {"ObjectID":18,"TopLevel":false,"ObjectValue":{"BoxedValue":"last mod seen: com.morgott.PPBridge"}}]}}
'@
$joptOn  = Join-Path $scratch 'Options-on.jopt'
$joptOff = Join-Path $scratch 'Options-off.jopt'
Set-Content -Path $joptOn -Encoding utf8NoBOM -Value ($joptShape -replace '__N__', '2' -replace '__LIST__', '"phoenixrising.tftv","com.morgott.PPBridge"')
Set-Content -Path $joptOff -Encoding utf8NoBOM -Value ($joptShape -replace '__N__', '1' -replace '__LIST__', '"phoenixrising.tftv"')

Assert-Value 'an id inside MOD_ACTIVATED reads as activated' `
    (Test-ModActivated $joptOn 'com.morgott.PPBridge') 'True'
Assert-Value 'an id that is only MENTIONED in the file is not activated' `
    ([bool](Test-ModActivated $joptOff 'com.morgott.PPBridge')) 'False'
Assert-Value 'another mod in the array is not this one' `
    ([bool](Test-ModActivated $joptOn 'com.morgott.NotInstalled')) 'False'
Assert-Value 'no profile file at all is not activated' `
    ([bool](Test-ModActivated (Join-Path $scratch 'no-such.jopt') 'com.morgott.PPBridge')) 'False'

Assert-Value 'exactly one profile directory is the answer' `
    (Find-PPProfileId $profilesOne) '11111111111111111'
Assert-Refusal 'no profile refuses and names -ProfileId' '-ProfileId' `
    { Find-PPProfileId $profilesNone }
Assert-Refusal 'two profiles refuse rather than pick one' '2 Steam profiles' `
    { Find-PPProfileId $profilesTwo }

# deploy into a path that is not an install used to SUCCEED, creating Mods\PPBridge under it and
# reading exactly like a working deploy.
$deployOut = & pwsh -NoProfile -File (Join-Path $root 'deploy.ps1') -PPRoot $libNone 2>&1
Assert-Value 'deploy refuses a target that is not an install' `
    ($(if ("$deployOut" -like '*No Phoenix Point at*') { 'refused' } else { "wrong:$deployOut" })) 'refused'
Assert-Value 'the refused deploy created no Mods folder' `
    ([bool](Test-Path (Join-Path $libNone 'Mods'))) 'False'

# The endpoint is opt-in: deployed and activated is not armed.
$armOut = & pwsh -NoProfile -File (Join-Path $root 'ppcli.ps1') run ping -PPRoot $installA 2>$null
$armErr = $null
try { $armErr = [string]::Join('', @($armOut)) | ConvertFrom-Json } catch { }
Assert-Value 'an unarmed install is refused by name, before anything is launched' `
    ($(if ($armErr.error -like '*ppcli-enabled*') { 'named' } else { "wrong:$armOut" })) 'named'

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
