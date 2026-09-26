<#
coop-lite.ps1 - the opt-in -Lite mode of coop.ps1: every peer in a small tiled window at Very Low
graphics, and the exact restore of whatever -Lite changed. Dot-sourced by coop.ps1; defines functions
only, so a run without -Lite never calls anything here.

WHY the profile and not Unity's command line: Phoenix Point re-applies its OWN video options after boot
(OptionsManager.InitVideoOptions -> Platform.SetGameWindowProperties -> Screen.SetResolution with the
profile's Options_ScreenWidth/Height/Mode, decompiled OptionsManager.cs:494-504, Platform.cs:381-384).
`-screen-width 1280 -screen-height 720 -screen-fullscreen 0` alone still logs "res is 2560x1440" in the
instances' Player.log. So -Lite writes the values into the peer's profile Options.jopt
(LocalLow\Snapshot Games Inc\Phoenix Point\Steam\<steamid>\Options.jopt - one per steam id, so the
owner's profile ...591 is touched only when the Steam install is itself a peer) and puts them back after.

Graphics = the game's own VeryLow preset (OptionsManagerDef.DefinedPresets[0], read live 2026-09-26:
binding "Very Low", detail 0, shadow distance 15, VeryLow textures/particles, Low shaders, every post
effect off, fractures 0) with shadows OFF on top, entered through the override path
(Options_HasGraphicsOverride=true -> InitialPresetSetup reads each field, OptionsManager.cs:345-361),
which also skips the first-run benchmark (PhoenixGame.cs:348).

RESTORE writes back only the keys -Lite changed, from a per-install backup taken the FIRST time -Lite
touches that profile (a second -Lite launch never overwrites it with lite values), and only after that
install's process is gone. Unity's own PlayerPrefs (HKCU\Software\Snapshot Games Inc\Phoenix Point) are
ONE key for every install of the product, written on exit - snapshotted once, restored when no -Lite
peer is left.
#>

$script:LiteDir = Join-Path $PSScriptRoot 'coop-lite'
$script:LiteProfiles = Join-Path $env:USERPROFILE 'AppData\LocalLow\Snapshot Games Inc\Phoenix Point\Steam'
$script:LiteRegKey = 'HKCU:\Software\Snapshot Games Inc\Phoenix Point'
# Unity's screen/quality PlayerPrefs (hashed-name suffix varies by Unity build, so matched by prefix).
$script:LiteRegPattern = '^(Screenmanager (Resolution Width|Resolution Height|Fullscreen mode|Resolution Use Native)|UnityGraphicsQuality|UnitySelectMonitor)_h\d+$'

# Every key -Lite writes, as the raw JSON that replaces the key's BoxedValue.
function LiteValues([int]$w, [int]$h) {
    $veryLow = '{ "EnumName": "VeryLow", "EnumValue": 0 }'
    [ordered]@{
        'Options_UsedPreset'               = '0'
        'Options_HasGraphicsOverride'      = 'true'
        'Option_GPULevel'                  = $veryLow
        'Options_QualityLevelBinding'      = '"Very Low"'
        'Options_GraphicsDetail'           = '0'
        'Options_Shadows'                  = 'false'
        'Options_ShadowsDistance'          = '15'
        'Options_TextureQuality'           = $veryLow
        'Options_ParticleQuality'          = $veryLow
        'Options_ShaderQuality'            = '{ "EnumName": "Low", "EnumValue": 1 }'
        'Options_AmbientOcclusion'         = 'false'
        'Options_Bloom'                    = 'false'
        'Options_DepthOfField'             = 'false'
        'Options_LensDistortion'           = 'false'
        'Options_ChromaticAberration'      = 'false'
        'Options_ScreenSpaceReflection'    = 'false'
        'Options_FractureQuality'          = '0'
        'Options_ScreenWidth'              = "$w"
        'Options_ScreenHeight'             = "$h"
        'Options_ScreenMode'               = '{ "EnumName": "Windowed", "EnumValue": 3 }'
        'Options_VSync'                    = 'false'
    }
}

# The profile an install plays under: Goldberg's force_steamid.txt (the instances), else the Steam
# client's logged-in account (the real install).
function LiteProfileJopt([string]$root) {
    $f = Join-Path $root 'PhoenixPointWin64_Data\Plugins\x86_64\steam_settings\force_steamid.txt'
    if (Test-Path $f) { $id = (Get-Content $f -TotalCount 1).Trim() }
    else {
        $u = (Get-ItemProperty 'HKCU:\Software\Valve\Steam\ActiveProcess' -ErrorAction SilentlyContinue).ActiveUser
        if (-not $u) { throw "REFUSED (-Lite): no force_steamid.txt under $root and no logged-in Steam user - cannot tell which profile's Options.jopt that install reads" }
        $id = [string]([uint64]76561197960265728 + [uint64]$u)
    }
    Join-Path (Join-Path $script:LiteProfiles $id) 'Options.jopt'
}

# Where each key's BoxedValue sits in the file TEXT. Read through the JSON (top-level dictionary ->
# ObjectID -> object), written back by splicing the text, so every other byte stays as the game wrote it.
function JoptSpans([string]$text, [string[]]$keys) {
    $doc = $text | ConvertFrom-Json -Depth 100
    $top = @($doc.Contents.Objects | Where-Object TopLevel)[0]
    $ids = @{}
    foreach ($kv in $top.ObjectValue.CollectionValues) { if ($kv.Key -in $keys) { $ids[$kv.Key] = $kv.Value.ObjectID } }
    $spans = [ordered]@{}
    foreach ($k in $keys) {
        if (-not $ids.Contains($k) -or $null -eq $ids[$k]) { throw "REFUSED (-Lite): Options.jopt has no by-reference '$k' - open the game's Options once on that profile so it writes its video/graphics settings" }
        $rx = '"ObjectID":\s*' + $ids[$k] + ',\s*"TopLevel":\s*false,\s*"ObjectValue":\s*\{\s*"#Type":\s*\d+,\s*"BoxedValue":\s*(?<v>\{[^{}]*\}|"(?:[^"\\]|\\.)*"|[^,\s}]+)'
        $m = [regex]::Match($text, $rx)
        if (-not $m.Success) { throw "REFUSED (-Lite): Options.jopt object $($ids[$k]) ('$k') is not in the shape -Lite knows how to edit" }
        $g = $m.Groups['v']
        $spans[$k] = [pscustomobject]@{ index = $g.Index; length = $g.Length; raw = $g.Value }
    }
    $spans
}
function JoptRead([string]$path, [string[]]$keys) {
    $out = [ordered]@{}
    foreach ($e in (JoptSpans ([IO.File]::ReadAllText($path)) $keys).GetEnumerator()) { $out[$e.Key] = $e.Value.raw }
    $out
}
function JoptWrite([string]$path, $values) {
    $text = [IO.File]::ReadAllText($path)
    $spans = JoptSpans $text @($values.Keys)
    # Last span first, so earlier offsets stay valid.
    foreach ($s in @($spans.GetEnumerator() | Sort-Object { $_.Value.index } -Descending)) {
        $text = $text.Remove($s.Value.index, $s.Value.length).Insert($s.Value.index, [string]$values[$s.Key])
    }
    $null = $text | ConvertFrom-Json -Depth 100   # never write a file the game cannot parse
    $tmp = "$path.coop-lite.tmp"
    [IO.File]::WriteAllText($tmp, $text, [Text.UTF8Encoding]::new($false))
    Move-Item $tmp $path -Force
}

function LiteBackupPath([string]$root) {
    Join-Path $script:LiteDir (($root.TrimEnd('\', '/') -replace '[^A-Za-z0-9]+', '_') + '.json')
}
# Lite values into the install's profile; the originals are saved first, once.
function LiteApply([string]$root, [int]$w, [int]$h) {
    $jopt = LiteProfileJopt $root
    if (-not (Test-Path $jopt)) { throw "REFUSED (-Lite): $jopt missing - launch that install once normally first" }
    $vals = LiteValues $w $h
    $bak = LiteBackupPath $root
    if (-not (Test-Path $bak)) {
        New-Item -ItemType Directory -Force -Path $script:LiteDir | Out-Null
        [pscustomobject]@{ root = $root; jopt = $jopt; at = (Get-Date).ToString('s'); values = (JoptRead $jopt @($vals.Keys)) } |
            ConvertTo-Json -Depth 5 | Set-Content $bak -Encoding utf8NoBOM
    }
    JoptWrite $jopt $vals
    $jopt
}
# Originals back into the CURRENT file (anything else the game saved meanwhile stays). $true = restored.
function LiteRestore([string]$root) {
    $bak = LiteBackupPath $root
    if (-not (Test-Path $bak)) { return $false }
    $b = Get-Content $bak -Raw | ConvertFrom-Json -Depth 5
    $vals = [ordered]@{}
    foreach ($p in $b.values.PSObject.Properties) { $vals[$p.Name] = $p.Value }
    JoptWrite $b.jopt $vals
    Remove-Item $bak
    $true
}
# Free commit ONE -Lite peer needs (coop.ps1's per-peer bar under -Lite unless -MinFreeCommitGB is
# passed). Field run 10 (2026-09-26, three peers in tactical battle) measured 5.33-5.37 GB private bytes
# per lite peer vs ~13.5 GB at full settings; 8 GB keeps headroom -> 24 GB for three, 16 for a pair.
$script:LiteCommitGBPerPeer = 8

# Block until none of $ids is still LISTED by the process enumeration (the view SideProcs has), or $sec
# passes; returns the ids still listed. NOT Wait-Process: it resolves each id via GetProcessById, which
# calls a killed process "not found" as soon as its exit code is set, while a multi-GB game is still
# being torn down and Get-Process still lists it - Wait-Process returned in ms and `stop` skipped every
# restore as "still running" (ISSUES.md 2026-09-26). $listed is injectable for the offline test.
function LiteWaitExit([int[]]$ids, [int]$sec = 60,
                      [scriptblock]$listed = { param($want) @(Get-Process -ErrorAction SilentlyContinue | Where-Object Id -in $want | ForEach-Object Id) }) {
    $dl = (Get-Date).AddSeconds($sec)
    while ($true) {
        $left = @(& $listed $ids)
        if (-not $left.Count -or (Get-Date) -ge $dl) { return , $left }
        Start-Sleep -Milliseconds 250
    }
}

function LiteBackedRoots {
    if (-not (Test-Path $script:LiteDir)) { return @() }
    @(Get-ChildItem $script:LiteDir -Filter '*.json' | Where-Object Name -ne 'registry.json' |
        ForEach-Object { (Get-Content $_.FullName -Raw | ConvertFrom-Json).root })
}

function LiteRegSnapshot {
    $f = Join-Path $script:LiteDir 'registry.json'
    if (Test-Path $f) { return }
    New-Item -ItemType Directory -Force -Path $script:LiteDir | Out-Null
    $vals = [ordered]@{}
    if (Test-Path $script:LiteRegKey) {
        $item = Get-Item $script:LiteRegKey
        foreach ($n in $item.Property) {
            if ($n -match $script:LiteRegPattern) { $vals[$n] = [pscustomobject]@{ kind = [string]$item.GetValueKind($n); value = $item.GetValue($n) } }
        }
    }
    [pscustomobject]@{ at = (Get-Date).ToString('s'); values = $vals } | ConvertTo-Json -Depth 5 | Set-Content $f -Encoding utf8NoBOM
}
# Values that existed go back; screen/quality values that did not exist before -Lite are removed.
function LiteRegRestore {
    $f = Join-Path $script:LiteDir 'registry.json'
    if (-not (Test-Path $f)) { return $false }
    $snap = (Get-Content $f -Raw | ConvertFrom-Json -Depth 5).values
    $had = @($snap.PSObject.Properties.Name)
    if (Test-Path $script:LiteRegKey) {
        foreach ($n in (Get-Item $script:LiteRegKey).Property) {
            if ($n -match $script:LiteRegPattern -and $n -notin $had) { Remove-ItemProperty $script:LiteRegKey -Name $n }
        }
        foreach ($p in $snap.PSObject.Properties) {
            Set-ItemProperty $script:LiteRegKey -Name $p.Name -Value $p.Value.value -Type $p.Value.kind
        }
    }
    Remove-Item $f
    $true
}

# Win32: work area and window moves in PHYSICAL pixels (the game is DPI-aware, pwsh is not - without the
# per-thread awareness switch a 150 % desktop reads 1707x960 instead of 2560x1440).
function LiteWin32 {
    if ('CoopLiteWin' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class CoopLiteWin {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(int a, int b, ref RECT r, int c);
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] static extern bool AdjustWindowRectExForDpi(ref RECT r, uint style, bool menu, uint ex, uint dpi);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    static readonly IntPtr PerMonitorV2 = new IntPtr(-4);
    // {left, top, width, height} of the primary work area (taskbar excluded) and the frame a windowed
    // Unity player adds around its client area (caption + sysmenu + minimize, no resize border).
    public static int[] Layout() {
        IntPtr old = SetThreadDpiAwarenessContext(PerMonitorV2);
        try {
            RECT w = new RECT(); SystemParametersInfo(0x30, 0, ref w, 0);
            RECT f = new RECT();
            AdjustWindowRectExForDpi(ref f, 0x00CA0000u, false, 0, GetDpiForSystem());
            return new[] { w.L, w.T, w.R - w.L, w.B - w.T, f.R - f.L, f.B - f.T };
        } finally { SetThreadDpiAwarenessContext(old); }
    }
    static IntPtr Find(uint pid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid || !IsWindowVisible(h)) return true;
            StringBuilder sb = new StringBuilder(64); GetClassName(h, sb, 64);
            if (sb.ToString() != "UnityWndClass") return true;
            found = h; return false;
        }, IntPtr.Zero);
        return found;
    }
    // Move (never resize - the game owns its client size) the pid's Unity window; returns its new
    // outer rect {x, y, w, h}, or null when the process has no visible Unity window.
    public static int[] Move(int pid, int x, int y) {
        IntPtr old = SetThreadDpiAwarenessContext(PerMonitorV2);
        try {
            IntPtr h = Find((uint)pid);
            if (h == IntPtr.Zero) return null;
            SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, 0x0001u | 0x0004u | 0x0010u);   // NOSIZE|NOZORDER|NOACTIVATE
            RECT r; GetWindowRect(h, out r);
            return new[] { r.L, r.T, r.R - r.L, r.B - r.T };
        } finally { SetThreadDpiAwarenessContext(old); }
    }
}
'@
}
# Tile i of a 2x2 grid on the primary work area, and the client size that fits in it with the frame.
function LiteTile([int]$i) {
    LiteWin32
    $l = [CoopLiteWin]::Layout()
    $tw = [math]::Floor($l[2] / 2); $th = [math]::Floor($l[3] / 2)
    [pscustomobject]@{ x = [int]($l[0] + ($i % 2) * $tw); y = [int]($l[1] + [math]::Floor($i / 2) % 2 * $th)
                       w = [int]($tw - $l[4]); h = [int]($th - $l[5]); workW = $l[2]; workH = $l[3] }
}
function LiteMove([int]$procId, [int]$x, [int]$y) {
    LiteWin32
    $r = [CoopLiteWin]::Move($procId, $x, $y)
    if ($r) { "$($r[0]),$($r[1]) $($r[2])x$($r[3])" } else { $null }
}
