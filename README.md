# PPCLI

Phoenix Point normally tells you what is happening only through the game itself. PPCLI is a terminal control channel into a running copy of the game: a small developer-only mod called `PPBridge` plus a PowerShell 7 client, `ppcli.ps1`. It exists so a developer or an AI agent can ask the live game what is actually true, and make it do things, without clicking through the UI. Decompiled source can show intent; PPCLI shows what the process really did.

## What it can do

PPCLI can:

- read the current scene, phase, level, objects, UI roots, and other live game state;
- run any of the game's roughly 344 registered native console commands and capture their output;
- read and write the game's separate console-variable surface;
- construct objects, read or write fields and properties, and invoke public or private methods by reflection;
- search or page the loaded definition repository, inspect types and members, and enumerate collections of live objects and assets;
- capture the rendered framebuffer as a PNG;
- execute declarative multi-step plans with waits, assertions, variables, repetition, output projection, and cleanup—for example starting a mission or campaign, spawning units, or firing a geoscape event; and
- build and deploy `PPBridge` into a Phoenix Point installation.

Every client invocation writes exactly one compact JSON object to stdout and sends diagnostics to stderr. This is deliberate: a PowerShell caller can always use `| ConvertFrom-Json` without first stripping banners or progress text.

## What it is not

PPCLI is not a player mod and is not distributed through the Steam Workshop. `PPBridge` is a development tool, has never been published as a Workshop mod, and exposes reflection-equivalent control inside the game process. Install it only in a development setup you control.

The endpoint is opt-in. The mod stays inert unless a file named `ppcli-enabled` sits beside `PPBridge.dll`; deployment never creates that file for you. Delete the marker when you are finished. A separate Phoenix Point copy is strongly recommended for automation.

## Getting started

You need Windows, PowerShell 7, a .NET SDK, and a Phoenix Point installation containing the game's `ModSDK` and managed assemblies. From the repository root, let the client find the Steam installation and deploy the bridge:

```powershell
. .\paths.ps1
$PPRoot = Find-PPInstall
.\ppcli.ps1 deploy -PPRoot $PPRoot
```

`deploy` performs the Release build and copies `PPBridge.dll` and `meta.json` to `$PPRoot\Mods\PPBridge`. It refuses to deploy while that exact installation is running, because a live process cannot replace the DLL it already loaded.

Arm the endpoint explicitly:

```powershell
New-Item -ItemType File -Force (Join-Path $PPRoot 'Mods\PPBridge\ppcli-enabled')
```

On the first launch, start Phoenix Point with mod loading enabled, select **PPBridge** (`com.morgott.PPBridge`) in the in-game mod manager, then quit:

```powershell
Start-Process (Join-Path $PPRoot 'PhoenixPointWin64.exe') -ArgumentList '-mods'
```

Launch it again with `-mods`, leave it running, and make `state` the first request. Do not send other runtime requests until this gate answers:

```powershell
Start-Process (Join-Path $PPRoot 'PhoenixPointWin64.exe') -ArgumentList '-mods'
$reply = .\ppcli.ps1 connect state -PPRoot $PPRoot | ConvertFrom-Json
$reply.result
```

Once the gate is healthy, these are representative requests:

```powershell
.\ppcli.ps1 connect console '{"command":"info","args":[]}'
.\ppcli.ps1 connect var '{"name":"ai_enabled","value":"false"}'
.\ppcli.ps1 connect call '{"op":"get","target":"@selected","member":"Pos"}'
.\ppcli.ps1 connect screenshot
.\ppcli.ps1 plan .\plans\start-mission.json '{"scene":"ALN_PLT_Nest_48x48_A","seed":12345}'
```

If you keep an automation copy outside Steam, put its absolute path in `ppcli-install.txt` beside `ppcli.ps1`; the file is gitignored. Otherwise pass `-PPRoot` explicitly and read the selected-install diagnostic before deploying or changing live state.

For agent operating rules and the exact verb shapes, read [`AGENTS.md`](AGENTS.md). For intent-to-command recipes, use [`PLAYBOOK.md`](PLAYBOOK.md). The complete protocol, reflection, plan, security, and failure reference is in [`docs/REFERENCE.md`](docs/REFERENCE.md).
