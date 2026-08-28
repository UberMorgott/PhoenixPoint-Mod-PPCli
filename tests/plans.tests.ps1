<#
  Offline check that every plan in plans\ is a plan the client will accept: valid JSON, a non-empty
  `steps` array in which every step names a verb, and a `finally` block. `ppcli.ps1 plan` parses the
  file locally before sending it, so a broken plan fails at the caller with no game involved - but
  only once someone runs it, which for a rarely-used plan can be months later. No game, no pipe, no
  framework.

      pwsh -NoProfile -File .\tests\plans.tests.ps1            # must exit 0
      pwsh -NoProfile -File .\tests\plans.tests.ps1 -Falsify   # must ALSO exit 0

  -Falsify runs the SAME rules against deliberately broken plans and demands that each rule catches
  its own breakage. It is not a mode switch on the assertions: a rule that quietly passes on junk
  has already cost this repo a day (see paths.tests.ps1), and a rule that is skipped rather than run
  is the same bug wearing a hat.
#>
param([switch] $Falsify)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# The rules, as one function over raw plan TEXT, so the same code judges a shipped file and a
# broken fixture. Returns the list of rule names that FAILED.
function Test-PlanText([string] $text) {
    $bad = New-Object Collections.Generic.List[string]
    $obj = $null
    # -Depth 64 is what ppcli.ps1 itself uses; a plan nesting deeper is silently stringified at the
    # default depth, which is one of the failures this exists to catch.
    try { $obj = ConvertFrom-Json $text -NoEnumerate -Depth 64 } catch { $obj = $null }
    if ($null -eq $obj) { $bad.Add('parses'); return $bad }
    if (-not ($obj.steps -is [array]) -or $obj.steps.Count -eq 0) { $bad.Add('has-steps') }
    # Every plan here is required to clean up on success, failure, timeout AND cancellation;
    # `finally` is the only block that runs on all four.
    if ($null -eq $obj.finally) { $bad.Add('has-finally') }
    if ($obj.steps -is [array]) {
        foreach ($s in $obj.steps) { if ([string]::IsNullOrEmpty($s.verb)) { $bad.Add('step-has-verb'); break } }
    }
    return $bad
}

$fails = 0
function Fail([string] $m) { $script:fails++; Write-Host "FAIL $m" }

if ($Falsify) {
    # One broken fixture per rule, and each must be caught by THAT rule.
    $broken = @{
        'parses'        = '{ "steps": [ }'
        'has-steps'     = '{ "steps": [], "finally": [] }'
        'has-finally'   = '{ "steps": [ { "id": "a", "verb": "state" } ] }'
        'step-has-verb' = '{ "steps": [ { "id": "a" } ], "finally": [] }'
    }
    foreach ($rule in $broken.Keys) {
        $caught = Test-PlanText $broken[$rule]
        if ($caught -notcontains $rule) { Fail "rule '$rule' did not catch its own broken plan (caught: $($caught -join ', '))" }
    }
    # And a plan that is fine must be reported fine, or every rule above is meaningless.
    $ok = Test-PlanText '{ "steps": [ { "id": "a", "verb": "state" } ], "finally": [] }'
    if ($ok.Count -ne 0) { Fail "a valid plan was rejected by: $($ok -join ', ')" }
    if ($fails -gt 0) { Write-Host "plans.tests -Falsify: $fails FAILURE(S)"; exit 1 }
    Write-Host "plans.tests -Falsify: PASS (every rule caught its own breakage)"
    exit 0
}

$plans = Get-ChildItem -Path (Join-Path $root 'plans') -Filter '*.json' -File
if ($plans.Count -eq 0) { Write-Host 'FAIL no plans found at all'; exit 1 }
foreach ($p in $plans) {
    $bad = Test-PlanText (Get-Content -Raw $p.FullName)
    if ($bad.Count -gt 0) { Fail "$($p.Name): $($bad -join ', ')" }
}

if ($fails -gt 0) { Write-Host "plans.tests: $fails FAILURE(S)"; exit 1 }
Write-Host "plans.tests: PASS ($($plans.Count) plans)"
exit 0
