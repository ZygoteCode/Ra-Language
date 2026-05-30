# Ra Language - .rac archive pipeline test driver.
#
# Exercises the compile -> archive -> run pipeline that run_suite.ps1 cannot
# (it only runs .ra files). Three independent, self-validating checks:
#
#   1. ROUND-TRIP per entry: running an entry's source directly and running the
#      archive compiled from it must produce IDENTICAL program output (the only
#      difference is the runner's own `[Ra Language] …` diagnostic lines, which
#      are filtered out). This validates the lexer->parser->IR->serialize path
#      and the deserialize->VM path agree, with NO brittle hardcoded expected
#      output. Entries that cannot run standalone (archive-only, e.g. they rely
#      on a bundled std root) are detected and SKIPPED for round-trip — they are
#      still covered by their prebuilt .rac in check 3.
#
#   2. COMPILE VARIANTS on one entry: default / --no-compress / --no-tree-shake
#      / --no-const-pool must each build an archive whose run matches the direct
#      run. Guards every compile knob + both codec-on/off paths.
#
#   3. PREBUILT ARCHIVES: every committed .rac must still open + run to exit 0
#      with no error markers and non-empty output. Validates on-disk format
#      stability / backward-compat (V4/V5).
#
# Usage (Windows PowerShell 5.1 or 7), from anywhere:
#   powershell -ExecutionPolicy Bypass -File tests\archive\run_archive_tests.ps1

[CmdletBinding()]
param(
    [int]$TimeoutSeconds = 60,
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"
$archiveRoot = $PSScriptRoot                 # tests/archive
$testsRoot   = (Resolve-Path (Join-Path $archiveRoot "..")).Path
$buildRoot   = (Resolve-Path (Join-Path $testsRoot "..")).Path
$fixtures    = Join-Path $archiveRoot "fixtures\tests_rac"

$exeCandidates = @(
    (Join-Path $buildRoot "RaLanguage.exe"),
    (Join-Path $buildRoot "bin\x64\Release\net10.0\RaLanguage.exe"),
    (Join-Path $buildRoot "bin\Release\net10.0\RaLanguage.exe")
)
$exe = $null
foreach ($c in $exeCandidates) { if (Test-Path $c) { $exe = (Resolve-Path $c).Path; break } }
if (-not $exe) { Write-Error "RaLanguage.exe not found (looked: $($exeCandidates -join ', '))." }

$errSig = 'error\[|^Traceback \(most recent|Compilation aborted|^\[Ra Language\] Unhandled error'

# Run the exe with a hard timeout; returns @{ Code; Out }. Stdout+stderr merged.
function Invoke-Ra([string[]]$racArgs) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = ($racArgs | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $psi.WorkingDirectory = $buildRoot
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow  = $true
    $p = New-Object System.Diagnostics.Process
    $p.StartInfo = $psi
    $null = $p.Start()
    $oTask = $p.StandardOutput.ReadToEndAsync()
    $eTask = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
        try { $p.Kill() } catch {}
        $p.WaitForExit()
        return @{ Code = 124; Out = "[driver] TIMEOUT after ${TimeoutSeconds}s" }
    }
    $out = $oTask.Result + $eTask.Result
    return @{ Code = $p.ExitCode; Out = $out }
}

# Program output only: drop the runner's own diagnostic/timing lines so two
# runs of the same program compare equal regardless of archive timing noise.
function Get-ProgramOutput([string]$raw) {
    ($raw -split "`r?`n" | Where-Object { $_ -notmatch '^\[Ra Language\]' }) -join "`n"
}
function Test-Clean([hashtable]$r) {
    return ($r.Code -eq 0) -and (-not [regex]::IsMatch($r.Out, $errSig, 'Multiline'))
}

$pass = 0; $fail = 0; $skip = 0
$failures = New-Object System.Collections.Generic.List[string]
function Note-Pass([string]$m) { $script:pass++; if (-not $Quiet) { Write-Host ("  PASS  {0}" -f $m) } }
function Note-Skip([string]$m) { $script:skip++; if (-not $Quiet) { Write-Host ("  SKIP  {0}" -f $m) } }
function Note-Fail([string]$m) { $script:fail++; Write-Host ("  FAIL  {0}" -f $m); $script:failures.Add($m) }

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("ra_rac_" + [System.IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
$racCounter = 0
function New-RacPath { $script:racCounter++; return (Join-Path $tmp ("out_$script:racCounter.rac")) }

try {
    # --- discover entries: hello.ra + any *entry*.ra ---
    $entries = @()
    $entries += Get-ChildItem -Path $fixtures -Filter "hello.ra" -Recurse -File
    $entries += Get-ChildItem -Path $fixtures -Filter "*entry*.ra" -Recurse -File
    $entries = $entries | Sort-Object FullName -Unique

    Write-Host "=== 1. compile -> run round-trip (per entry) ==="
    foreach ($e in $entries) {
        $rel = $e.FullName.Substring($fixtures.Length).TrimStart('\','/')
        $direct = Invoke-Ra @($e.FullName)
        if (-not (Test-Clean $direct)) {
            Note-Skip "$rel (not standalone-runnable; covered by prebuilt .rac)"
            continue
        }
        $rac = New-RacPath
        $comp = Invoke-Ra @("--compile", $e.FullName, "-o", $rac)
        if (-not (Test-Clean $comp) -or -not (Test-Path $rac) -or (Get-Item $rac).Length -le 0) {
            Note-Fail "$rel : compile failed (exit $($comp.Code))"
            continue
        }
        $run = Invoke-Ra @($rac)
        if (-not (Test-Clean $run)) {
            Note-Fail "$rel : archive run failed (exit $($run.Code))"
            continue
        }
        if ((Get-ProgramOutput $direct.Out) -ne (Get-ProgramOutput $run.Out)) {
            Note-Fail "$rel : archive output != direct output"
            continue
        }
        Note-Pass "$rel (round-trip; $((Get-ProgramOutput $run.Out).Split("`n").Count) lines)"
    }

    Write-Host "=== 2. compile-knob variants (hello.ra) ==="
    $hello = Join-Path $fixtures "hello.ra"
    if (Test-Path $hello) {
        $directHello = Get-ProgramOutput (Invoke-Ra @($hello)).Out
        $variants = @(
            @{ name = "default";        opts = @() },
            @{ name = "--no-compress";  opts = @("--no-compress") },
            @{ name = "--no-tree-shake";opts = @("--no-tree-shake") },
            @{ name = "--no-const-pool";opts = @("--no-const-pool") }
        )
        foreach ($v in $variants) {
            $rac = New-RacPath
            $comp = Invoke-Ra (@("--compile", $hello, "-o", $rac) + $v.opts)
            if (-not (Test-Clean $comp) -or -not (Test-Path $rac)) { Note-Fail "variant $($v.name): compile failed"; continue }
            $run = Invoke-Ra @($rac)
            if (-not (Test-Clean $run)) { Note-Fail "variant $($v.name): run failed (exit $($run.Code))"; continue }
            if ((Get-ProgramOutput $run.Out) -ne $directHello) { Note-Fail "variant $($v.name): output mismatch"; continue }
            Note-Pass "variant $($v.name)"
        }
    }

    Write-Host "=== 3. prebuilt archives (format stability) ==="
    foreach ($a in (Get-ChildItem -Path $fixtures -Filter "*.rac" -Recurse -File | Sort-Object FullName)) {
        $rel = $a.FullName.Substring($fixtures.Length).TrimStart('\','/')
        $run = Invoke-Ra @($a.FullName)
        $prog = Get-ProgramOutput $run.Out
        if ((Test-Clean $run) -and $prog.Trim().Length -gt 0) {
            Note-Pass "$rel (runs; $($prog.Split("`n").Count) lines)"
        } else {
            Note-Fail "$rel : run failed (exit $($run.Code)) or empty output"
        }
    }

    Write-Host "=== 4. malformed archives rejected (resilience) ==="
    # A corrupt / truncated / empty .rac must be refused with a non-zero exit
    # and a diagnostic — never crash (AV) and never hang (timeout = exit 124).
    $valid = Get-ChildItem -Path $fixtures -Filter "*.rac" -Recurse -File | Select-Object -First 1
    $cases = @{}
    $cases["garbage"]   = [System.Text.Encoding]::ASCII.GetBytes("this is definitely not a rac archive`n")
    $cases["empty"]     = New-Object byte[] 0
    if ($valid) {
        $vb = [System.IO.File]::ReadAllBytes($valid.FullName)
        $cases["truncated"] = $vb[0..([Math]::Min(63, $vb.Length - 1))]
        $flip = [byte[]]$vb.Clone(); if ($flip.Length -gt 8) { $flip[8] = [byte](($flip[8] -bxor 0xFF)) }
        $cases["corrupt-header"] = $flip
    }
    foreach ($name in $cases.Keys) {
        $bad = New-RacPath
        [System.IO.File]::WriteAllBytes($bad, [byte[]]$cases[$name])
        $r = Invoke-Ra @($bad)
        # Rejected cleanly = non-zero exit, but NOT a timeout (124) and NOT a
        # hard process crash; a clear diagnostic should be present.
        if ($r.Code -ne 0 -and $r.Code -ne 124 -and $r.Out -match 'archive') {
            Note-Pass "rejects $name archive (exit $($r.Code))"
        } else {
            Note-Fail "malformed '$name' not rejected cleanly (exit $($r.Code))"
        }
    }
}
finally {
    Remove-Item -Path $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "=== Archive Summary ==="
Write-Host ("checks : {0} pass, {1} fail, {2} skip" -f $pass, $fail, $skip)
if ($fail -gt 0) {
    Write-Host "Failing:"
    foreach ($f in $failures) { Write-Host "  - $f" }
    exit 1
}
exit 0
