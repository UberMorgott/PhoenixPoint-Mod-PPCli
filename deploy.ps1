param(
    # The deploy TARGET. Empty = discover it through Steam (paths.ps1). If you keep a separate copy
    # of the game for automation, this is where you name it - the game is single-instance per install
    # and a run that launches into an occupied one dies with an empty log.
    [string] $PPRoot = '',
    # Where the REFERENCE assemblies (ModSDK\, PhoenixPointWin64_Data\Managed\) come from. Only worth
    # setting when the deploy target is a copy that has no ModSDK.
    [string] $RefRoot = '',
    # Deploy somewhere other than the install `ppcli-install.txt` pins. Only means anything on a
    # machine that HAS that file; without one there is nothing to override.
    [switch] $Force,
    # Which file pins this machine's automation install. A parameter only so the offline test can
    # point at a fixture; nothing else passes it.
    [string] $PinFile = ''
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'paths.ps1')

$pinned = Get-PPPinnedInstall $PinFile
if (-not $PPRoot) { $PPRoot = Find-PPInstall; Write-Host "install: $PPRoot ($(Format-InstallOrigin $pinned))" }
# Creating Mods\PPBridge under a path that is not an install is the trap this guard exists for: it
# succeeds, deploys nothing anyone will load, and reads exactly like a working deploy.
if (-not (Test-PPInstall $PPRoot)) {
    throw "No Phoenix Point at $PPRoot (no PhoenixPointWin64.exe there). Pass -PPRoot '<install folder>'."
}

# THE ONLY WRITER. Discovery answers with the install Steam knows about, which on a machine that
# keeps a separate copy for automation is the game its owner actually PLAYS - and a bare `deploy`
# then rewrites a mod inside it without ever saying so. `ppcli-install.txt` names the copy this
# machine automates; a machine that has no such file never reaches this guard.
if ($pinned -and (Get-Item $PPRoot).FullName -ne $pinned -and -not $Force) {
    throw ("REFUSED: this machine automates '$pinned' (named in $($PinFile ? $PinFile : (Join-Path $PSScriptRoot 'ppcli-install.txt'))), " +
           "and this deploy targets '$PPRoot' instead - which is where the game you actually play lives. " +
           "Nothing was built and nothing was written. Deploy there on purpose with: " +
           ".\ppcli.ps1 deploy -PPRoot '$PPRoot' -Force")
}

if (-not $RefRoot) { $RefRoot = (Test-Path (Join-Path $PPRoot 'ModSDK')) ? $PPRoot : (Find-PPInstall) }

# The csproj already builds into a folder named after the assembly, which is exactly the layout
# PPModLoader wants (Mods\<Name>\<Name>.dll + meta.json), so the whole deploy is one copy.
$out  = Join-Path $PSScriptRoot 'bin\Release\PPBridge'
$dest = Join-Path $PPRoot 'Mods\PPBridge'

dotnet build (Join-Path $PSScriptRoot 'PPBridge.csproj') -c Release /p:PPRoot="$RefRoot"
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)." }

New-Item -ItemType Directory -Force -Path $dest | Out-Null
foreach ($file in 'meta.json', 'PPBridge.dll') {
    Copy-Item (Join-Path $out $file) $dest -Force
}
$stamp = (Get-FileHash -Algorithm SHA1 (Join-Path $dest 'PPBridge.dll')).Hash.ToLower().Substring(0, 8)
Write-Host "Deployed PPBridge to $dest (build=$stamp)"
# Deliberately NOT created here: arming the endpoint is the user's decision, not a side effect of
# copying a DLL. See the SECURITY section of README.md.
if (-not (Test-Path (Join-Path $dest 'ppcli-enabled'))) {
    Write-Host "The pipe is OFF until you arm it: New-Item -ItemType File '$(Join-Path $dest 'ppcli-enabled')'"
}
