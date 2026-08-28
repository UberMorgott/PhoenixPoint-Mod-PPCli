<#
  Where Phoenix Point and its profile actually are on THIS machine.

  Dot-sourced by ppcli.ps1 and deploy.ps1 - it defines functions and runs nothing.
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
<#
  The install THIS MACHINE automates, remembered once instead of rediscovered.

  Steam discovery is right for a stranger with exactly one install and wrong for anyone who keeps a
  separate copy for automation: it finds the REAL Steam install - the game the user actually plays -
  and a bare `deploy` then writes into it. One optional line in `ppcli-install.txt` beside this
  script settles it, and a machine without that file behaves exactly as before.

  -PinFile exists so the offline test can point at a fixture; nothing else passes it.
  ponytail: one path, one line. A machine that needs two automation installs names them with -PPRoot.
#>
function Get-PPPinnedInstall([string] $PinFile) {
    $pin = $PinFile ? $PinFile : (Join-Path $PSScriptRoot 'ppcli-install.txt')
    if (-not (Test-Path $pin)) { return $null }
    $path = (Get-Content -Raw $pin).Trim()
    # A stale pin must not silently fall back to discovery - that is the incident this file prevents.
    if (-not (Test-PPInstall $path)) {
        throw ("REFUSED: $pin pins '$path', which has no PhoenixPointWin64.exe in it. Point it at the " +
               "install you automate, or delete it to go back to Steam discovery: Remove-Item '$pin'.")
    }
    return (Get-Item $path).FullName
}

<#
  How the install about to be written to was CHOSEN, said out loud.

  The refusal the docs used to promise - "discovery could not decide, name one" - only ever fires on
  a machine with two installs that Steam BOTH knows about. An automation copy outside a Steam library
  is invisible to discovery, so it sees exactly one install and proceeds, and that one is the game its
  owner plays. No prompt: a stranger with one install is right, and a confirmation there would be
  ceremony on the documented first run. Naming the target loudly is the honest half.
#>
function Format-InstallOrigin([string] $Pinned) {
    if ($Pinned) { return 'pinned in ppcli-install.txt' }
    return ('discovered through Steam - this is the install you PLAY. Keep a separate copy for ' +
            'automation? Put its path in ppcli-install.txt beside ppcli.ps1 and it becomes the default')
}

function Find-PPInstall([string[]] $Roots) {
    if (-not $Roots) {
        $pinned = Get-PPPinnedInstall
        if ($pinned) { return $pinned }
    }
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
<#
  Is a mod id actually in the profile's MOD_ACTIVATED array?

  The preflight used to ask whether the id appeared ANYWHERE in Options.jopt, which a deactivated mod
  satisfies just as well as an activated one: the id is still written into the file (the mod list, a
  leftover key, another array), so the check passed on installs where the mod was switched OFF and the
  run then died with the mod loaded and silent - the exact failure the preflight exists to prevent.

  The file's shape: Contents.Objects is a flat pool of {ObjectID, ObjectValue} records. The options
  DICTIONARY holds {Key,Value:{ObjectID}} pairs, and MOD_ACTIVATED's points at the record whose
  CollectionValues IS the activated list. Read-only, always - re-serialising this file once shrank it
  32991 -> 18996 bytes.
#>
function Test-ModActivated([string] $JoptPath, [string] $ModId) {
    if (-not (Test-Path $JoptPath)) { return $false }
    $objects = @((Get-Content -Raw $JoptPath | ConvertFrom-Json -Depth 64).Contents.Objects)
    # Member enumeration over the pool: records with no CollectionValues, and the string arrays that
    # ARE CollectionValues, both simply have no .Key and drop out.
    $ref = $objects.ObjectValue.CollectionValues | Where-Object { $_.Key -eq 'MOD_ACTIVATED' } | Select-Object -First 1
    if (-not $ref -or -not $ref.Value) { return $false }
    $arr = $objects | Where-Object { $_.ObjectID -eq $ref.Value.ObjectID } | Select-Object -First 1
    return (@($arr.ObjectValue.CollectionValues) -contains $ModId)
}

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
