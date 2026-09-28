<#
coop-util.ps1 - the pure halves of coop.ps1 that decide correctness on their own: action-scoped log
waits and the pid file `stop` trusts. Dot-sourced by coop.ps1 and by tests\coop-util.tests.ps1;
defines functions only.
#>

# ---------------------------------------------------------------- log marks + waits

# Byte offset just past the last COMPLETE line of $f (0 when it does not exist). A trailing partial
# line is NOT counted: the rest of it is written later, and the finished line must still count as new.
function LogEnd([string]$f) {
    if (-not (Test-Path -LiteralPath $f)) { return [long]0 }
    $s = $null
    try { $s = [IO.FileStream]::new($f, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite, Delete') }
    catch { return [long]0 }
    try {
        # Backwards in 64 KiB chunks: logs run to tens of MB and a mark is taken before every action.
        $buf = New-Object byte[] 65536
        $pos = $s.Length
        while ($pos -gt 0) {
            $n = [int][math]::Min(65536, $pos)
            $pos -= $n
            $s.Position = $pos
            $got = 0
            while ($got -lt $n) { $r = $s.Read($buf, $got, $n - $got); if ($r -le 0) { break }; $got += $r }
            $i = [array]::LastIndexOf($buf, [byte]10, $got - 1, $got)
            if ($i -ge 0) { return [long]($pos + $i + 1) }
        }
        [long]0
    } finally { $s.Dispose() }
}

# Complete lines of $f from byte $from on. A file now SHORTER than $from was replaced (rotated or
# truncated), so all of it is new.
function LogLinesSince([string]$f, [long]$from) {
    if (-not (Test-Path -LiteralPath $f)) { return , @() }
    $s = $null
    try { $s = [IO.FileStream]::new($f, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite, Delete') }
    catch { return , @() }
    try {
        if ($from -gt $s.Length) { $from = 0 }
        $s.Position = $from
        $ms = [IO.MemoryStream]::new()
        $s.CopyTo($ms)
        $b = $ms.ToArray()
    } finally { $s.Dispose() }
    $end = [array]::LastIndexOf($b, [byte]10)
    if ($end -lt 0) { return , @() }
    , @([Text.Encoding]::UTF8.GetString($b, 0, $end) -split "`r?`n")
}

# The mark: per file, where its complete lines end NOW, plus the time it was taken ('@at'), which
# decides where a file the mark never saw starts (LateStart).
function LogMarkFiles([string[]]$files) {
    $m = @{ '@at' = (Get-Date).ToUniversalTime() }
    foreach ($f in $files) { if ($f) { $m[$f] = LogEnd $f } }
    $m
}

# A file the wait resolves that the mark did not list (the peer's mod log only became nameable
# mid-wait). Created after the mark: all of it is new. Older: where it ends at first sight - lines
# written between the mark and now are given up rather than risk an old run's line passing.
function LateStart([string]$f, [datetime]$at) {
    $i = Get-Item -LiteralPath $f -ErrorAction SilentlyContinue
    if ($i -and $i.CreationTimeUtc -gt $at) { return [long]0 }
    LogEnd $f
}

# Waits for /$rx/ in a line written after $mark in any file $resolve returns (re-run every poll, so a
# log that appears mid-wait is read). No mark = the whole files. $failRx: a line that means the
# awaited one will never come ends the wait at once - after $graceMs, so the refusal's reason lines
# (written right after it) are complete, and quoted with up to 3 of them.
# $resolve runs in THIS function's scope: its free variables must not share a name with a local here.
function WaitLogCore([scriptblock]$resolve, [string]$rx, [int]$sec, [hashtable]$mark = @{},
                     [string]$failRx = '', [string]$what = 'log', [int]$graceMs = 1000, [int]$pollMs = 2000) {
    $dl = (Get-Date).AddSeconds($sec)
    $lateFrom = @{}
    $files = @()
    while ($true) {
        $files = @(& $resolve | Where-Object { $_ })
        foreach ($f in $files) {
            $from = [long]0
            if ($mark.ContainsKey($f)) { $from = [long]$mark[$f] }
            elseif ($mark.ContainsKey('@at')) {
                if (-not $lateFrom.ContainsKey($f)) { $lateFrom[$f] = LateStart $f $mark['@at'] }
                $from = [long]$lateFrom[$f]
            }
            $lines = LogLinesSince $f $from
            if ($failRx) {
                $idx = -1
                for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match $failRx) { $idx = $i; break } }
                if ($idx -ge 0) {
                    Start-Sleep -Milliseconds $graceMs
                    $lines = LogLinesSince $f $from
                    $last = [math]::Min($lines.Count - 1, $idx + 3)
                    throw "REFUSED on ${what} (waiting for /$rx/): " + ($lines[$idx..$last] -join ' | ')
                }
            }
            $hit = @($lines | Where-Object { $_ -match $rx }) | Select-Object -Last 1
            if ($hit) { return $hit }
        }
        if ((Get-Date) -ge $dl) { break }
        Start-Sleep -Milliseconds $pollMs
    }
    throw "log timeout on ${what}: /$rx/ (looked in: $($files -join ', '))"
}
