<#
  Offline check for local def-name resolution. No game, no pipe, no framework.

      pwsh -NoProfile -File .\tests\resolve-names.tests.ps1            # must exit 0
      pwsh -NoProfile -File .\tests\resolve-names.tests.ps1 -Falsify   # must ALSO exit 0

  -Falsify corrupts every expectation on purpose and then demands that EVERY assertion fails. That
  is the only thing that proves the assertions are wired to anything: a helper that quietly passes
  on empty input has already cost this repo a day (see memory
  `powershell-short-name-alias-vacuous-pass`). Helper names are spelled out in full for the same
  reason - a one-letter helper resolves to a builtin alias.
#>
param([switch] $Falsify)

$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $PSScriptRoot 'fixture'
$empty   = Join-Path $PSScriptRoot 'fixture-empty'
$stale   = Join-Path $PSScriptRoot 'fixture-stale'
# Cleaned up in the `finally` below, and again HERE: a mid-test throw used to leave fixture-empty
# behind, and the next run then passed because the leftover happened to still be empty.
function Remove-Scratch { foreach ($d in $empty, $stale) { Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue } }
Remove-Scratch
New-Item -ItemType Directory -Force -Path $empty | Out-Null
# A catalog whose meta.json disagrees with defs.ndjson is the shape a run that died between the two
# moves leaves behind. It must be reported stale, not trusted.
New-Item -ItemType Directory -Force -Path $stale | Out-Null
Copy-Item (Join-Path $fixture 'defs.ndjson') $stale
Set-Content -Path (Join-Path $stale 'meta.json') -Value '{"rows":99}' -Encoding utf8NoBOM

. (Join-Path $root 'names.ps1')

try {

$script:passed = 0
$script:failed = 0

function Assert-Value([string] $what, $actual, [string] $expected) {
    if ($Falsify) { $expected = $expected + '~falsified' }
    if ([string]$actual -ceq $expected) { $script:passed++; Write-Host "  ok   $what" }
    else { $script:failed++; Write-Host "  FAIL $what : got '$actual', wanted '$expected'" }
}

function Assert-Throws([string] $what, [scriptblock] $body) {
    if ($Falsify) { $body = { 'this block cannot throw' } }
    try { & $body | Out-Null }
    catch { $script:passed++; Write-Host "  ok   $what (refused: $($_.Exception.Message.Split([char]10)[0]))"; return }
    $script:failed++
    Write-Host "  FAIL $what : nothing was thrown"
}

function Resolve-Fixture([string] $var, [string] $value, [string] $dir = $fixture) {
    Resolve-DefValue -VarName $var -Value $value -CatalogDir $dir
}

Write-Host "resolve-names ($(if ($Falsify) { 'FALSIFY' } else { 'normal' }))"

Assert-Value 'exact def name is returned unchanged' `
    (Resolve-Fixture 'defName' 'Swarmer_TacCharacterDef') 'Swarmer_TacCharacterDef'

Assert-Value 'alias beats an ambiguous substring' `
    (Resolve-Fixture 'defName' 'crabman') 'Crabman_Gunner_TacCharacterDef'

Assert-Value 'unique substring resolves' `
    (Resolve-Fixture 'defName' 'Swarmer') 'Swarmer_TacCharacterDef'

Assert-Value 'research resolves to ResearchDef.Id, not the def name' `
    (Resolve-Fixture 'researchId' 'autopsy') 'PX_Alien_Autopsy'

Assert-Value 'an item alias in the plan-var itemName resolves' `
    (Resolve-Fixture 'itemName' 'PX_AssaultRifle_WeaponDef') 'PX_AssaultRifle_WeaponDef'

# weapon-test.json names two defs at once, so it has two vars of its own. The rifle case is the
# whole reason resolution is worth having here: "PX_AssaultRifle" matches the weapon AND its ammo
# clip, and a plan that took the first hit would measure the clip.
Assert-Value 'weaponDef resolves in the item family' `
    (Resolve-Fixture 'weaponDef' 'PX_AssaultRifle_WeaponDef') 'PX_AssaultRifle_WeaponDef'

Assert-Value 'enemyDef resolves in the actor family' `
    (Resolve-Fixture 'enemyDef' 'Swarmer') 'Swarmer_TacCharacterDef'

Assert-Throws 'an ambiguous weaponDef REFUSES (the rifle and its ammo clip)' `
    { Resolve-Fixture 'weaponDef' 'PX_AssaultRifle' }

Assert-Value 'an empty value is passed through, not refused' `
    (Resolve-Fixture 'defName' '') ''

Assert-Value 'a var that names no def is passed through' `
    (Resolve-Fixture 'faction' 'crabman') 'crabman'

Assert-Value 'no catalog: the value goes as given (warning only)' `
    (Resolve-Fixture 'defName' 'crabman' $empty) 'crabman'

Assert-Value 'a curated alias the catalog does not know still goes as given' `
    (Resolve-Fixture 'defName' 'ghost') 'Not_In_The_Catalog_TacCharacterDef'

Assert-Throws 'an ambiguous substring REFUSES' { Resolve-Fixture 'defName' 'Crabman_' }
Assert-Throws 'an unknown name REFUSES'        { Resolve-Fixture 'defName' 'Zoanthrope' }

# aliases.ndjson is hand-edited and unvalidated: a casual name that happens to equal a real def name
# must NOT steer a correct exact input somewhere else. The fixture aliases Swarmer_TacCharacterDef
# (a real def) to the Berserker on purpose.
Assert-Value 'an exact def name BEATS a colliding alias' `
    (Resolve-Fixture 'defName' 'Swarmer_TacCharacterDef') 'Swarmer_TacCharacterDef'

# Real data: CopyOfPX_PandoraKey_ResearchDef carries id PX_PandoraKey_ResearchDef, so that input
# matches one row by NAME and another by ID. The name is the answer.
Assert-Value 'an exact def NAME beats an exact research id' `
    (Resolve-Fixture 'researchId' 'PX_PandoraKey_ResearchDef') 'PX_PandoraKey'

Assert-Throws 'two exact hits with DISTINCT values REFUSE' { Resolve-Fixture 'researchId' 'Twin_ResearchDef' }

Assert-Throws 'a catalog whose meta.json disagrees is reported STALE' `
    { Resolve-Fixture 'defName' 'Swarmer_TacCharacterDef' $stale }

# The stdout contract: resolution must yield exactly ONE value and print nothing else. A stray
# Write-Output anywhere in names.ps1 would show up here as a second pipeline element and would
# corrupt `ppcli.ps1 ... | ConvertFrom-Json` in exactly the way that is hard to notice.
Assert-Value 'resolution emits exactly one object on stdout' `
    (@(Resolve-Fixture 'defName' 'crabman').Count) '1'

# End to end: the contract is ONE JSON object on stdout even for a refusal, and a non-zero exit.
# The message is asserted, not just "it failed": -PPRoot Z:\no-such-install makes Get-Endpoint fail
# too, so a bare "did it exit non-zero" stayed green even when resolution stopped refusing at all.
$out = & pwsh -NoProfile -File (Join-Path $root 'ppcli.ps1') plan (Join-Path $root 'plans\spawn-squad.json') `
        '{"defName":"Crabman_"}' -CatalogDir $fixture -PPRoot 'Z:\no-such-install' 2>$null
$code = $LASTEXITCODE
$err  = $null
try { $err = [string]::Join('', @($out)) | ConvertFrom-Json } catch { }
Assert-Value 'a refused plan still writes ONE json object on stdout' `
    ($(if ($err -and $err.PSObject.Properties['ok'] -and -not $err.ok) { 'json-error' } else { "bad:$out" })) 'json-error'
Assert-Value 'the refusal names the AMBIGUITY, not the missing install' `
    ($(if ($err.error -like "*matches 2 actor defs*") { 'ambiguous' } else { "wrong:$($err.error)" })) 'ambiguous'
Assert-Value 'a refused plan exits non-zero' ($(if ($code -ne 0) { 'nonzero' } else { 'zero' })) 'nonzero'

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
