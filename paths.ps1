<#
  Where Phoenix Point and its profile actually are on THIS machine.

  Dot-sourced by ppcli.ps1, deploy.ps1 and spider-demo.ps1 - it defines functions and runs nothing.
  Nothing here is ever a default baked into a parameter: an install path or a Steam id that is right
  on one machine is a first-run failure on every other one.
#>

function Test-PPInstall([string] $Path) {
    return $Path -and (Test-Path (Join-Path $Path 'PhoenixPointWin64.exe'))
}

<#
  The Steam client's own record of where its libraries are, never a guessed list of drive letters:
  HKCU\Software\Valve\Steam\SteamPath is the client, and steamapps\libraryfolders.vdf lists every
  other library it manages. Exactly one hit is an answer; zero or several is a question only the
  caller can settle, so both refuse by name instead of picking.

  -Roots exists so the offline test can hand this function a library list; nothing else passes it.
#>
function Find-PPInstall([string[]] $Roots) {
    # NOT named $roots: PowerShell variable names are case-insensitive, so a local $roots IS the
    # $Roots parameter and clearing it silently threw the caller's list away.
    $libraries = @()
    $steam = $null
    if ($Roots) { $libraries = @($Roots); $steam = '(supplied)' }
    else {
        $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -Name SteamPath -ErrorAction Ignore).SteamPath
        if ($steam) {
            $libraries += $steam
            $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
            # The VDF is read with one regex, not parsed: "path" is the only key here that matters
            # and its value is a JSON-style escaped Windows path.
            if (Test-Path $vdf) {
                foreach ($m in [regex]::Matches((Get-Content -Raw $vdf), '"path"\s*"([^"]+)"')) {
                    $libraries += ($m.Groups[1].Value -replace '\\\\', '\')
                }
            }
        }
    }

    $hits = New-Object Collections.Generic.List[string]
    foreach ($r in $libraries) {
        $p = Join-Path $r 'steamapps\common\Phoenix Point'
        if (-not (Test-PPInstall $p)) { continue }
        $full = (Get-Item $p).FullName
        if (-not $hits.Contains($full)) { $hits.Add($full) }
    }

    if ($hits.Count -eq 1) { return $hits[0] }
    if ($hits.Count -eq 0) {
        throw ("REFUSED: no Phoenix Point install found through Steam" +
               ($steam ? " (library list: $steam\steamapps\libraryfolders.vdf)" : ' (Steam is not registered under HKCU\Software\Valve\Steam)') +
               ". Pass -PPRoot '<folder containing PhoenixPointWin64.exe>'.")
    }
    throw ("REFUSED: $($hits.Count) Phoenix Point installs found (" + ($hits -join ', ') +
           "). Pass -PPRoot '<the one you mean>'.")
}

<#
  Which per-SteamID profile an install writes. One directory under the profile root is the answer;
  anything else is a question, because a machine with two Steam accounts has two of them and picking
  the wrong one makes the mod-activation preflight refuse a perfectly good install.

  -Dir exists so the offline test can point at a fixture; nothing else passes it.
#>
function Find-PPProfileId([string] $Dir) {
    # $dir would BE $Dir - see the note in Find-PPInstall.
    $profileRoot = $Dir ? $Dir : (Join-Path $env:USERPROFILE 'AppData\LocalLow\Snapshot Games Inc\Phoenix Point\Steam')
    $ids = @()
    if (Test-Path $profileRoot) { $ids = @(Get-ChildItem -Directory $profileRoot -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name) }
    if ($ids.Count -eq 1) { return $ids[0] }
    if ($ids.Count -eq 0) {
        throw ("REFUSED: no Steam profile under $profileRoot - launch Phoenix Point once so it writes one, " +
               'or pass -ProfileId <SteamID64>.')
    }
    throw ("REFUSED: $($ids.Count) Steam profiles under $profileRoot (" + ($ids -join ', ') +
           ') - pass -ProfileId <SteamID64> to say which one this install writes.')
}
