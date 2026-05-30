# Ra Language - test suite driver.
#
# Runs every .ra test under tests/ against the interpreter and scores it.
#
# Scoring (authoritative):
#   PASS  <=>  process exit code == 0  AND  no "[id] FAIL" / "FAIL ..." line
#   FAIL  <=>  any FAIL marker, OR a non-zero exit (uncaught runtime error,
#              parse/lex abort, or file-read failure -- the interpreter now
#              reports all of these via Environment.ExitCode).
#
# The exit-code signal is reliable as of the Program.cs hardening: an
# uncaught error or compile abort yields exit 1, a caught error / clean run
# yields 0. The FAIL-marker scan is a second, independent check that also
# catches "soft-assert" files that print "[id] FAIL" but keep running.
#
# Two reporting conventions are recognised, so both the structured suite and
# the older hard-asserting files score correctly without rewrites:
#   * soft-assert:  one "[<id>] OK" / "[<id>] FAIL: ..." line per case
#   * hard-assert:  "OK  <label>" lines + a `throw` on failure (=> exit 1)
# Markers are anchored to column 0 so error source-echoes (rendered as
# "  394 | ... [id] FAIL ...") never count as real markers.
#
# Files are skipped when they live under a helpers/ or std/ folder, are a
# known multi-file fixture, or carry a "# runner: skip" directive on an early
# line (used for sub-entry modules that other tests import / drive).
#
# Usage (Windows PowerShell 5.1 or PowerShell 7) from anywhere:
#   powershell -ExecutionPolicy Bypass -File tests\run_suite.ps1
#   powershell -File tests\run_suite.ps1 -Filter operators
#   powershell -File tests\run_suite.ps1 -TimeoutSeconds 45

[CmdletBinding()]
param(
    [string]$Filter = "",
    [int]$TimeoutSeconds = 30,
    [switch]$Quiet,
    [switch]$NoArchive
)

$ErrorActionPreference = "Stop"
$testsRoot = $PSScriptRoot

# The interpreter lives one directory up from tests/ (tests/ is shipped
# inside the build output, next to RaLanguage.exe). Fall back to the two
# historical build locations for anyone running from a source tree.
$exeCandidates = @(
    (Join-Path $testsRoot "..\RaLanguage.exe"),
    (Join-Path $testsRoot "..\bin\x64\Release\net10.0\RaLanguage.exe"),
    (Join-Path $testsRoot "..\bin\Release\net10.0\RaLanguage.exe")
)
$exe = $null
foreach ($cand in $exeCandidates) {
    if (Test-Path $cand) { $exe = (Resolve-Path $cand).Path; break }
}
if (-not $exe) {
    Write-Error "RaLanguage.exe not found next to tests/ (looked at: $($exeCandidates -join ', ')). Build with 'dotnet build -c Release -p:Platform=x64'."
}

# Imports resolve relative to the importing file first, then to the project
# root. Running with the build dir as the working directory makes std.* and
# any project-root-relative fallbacks resolve.
$workingDir = (Resolve-Path (Join-Path $testsRoot "..")).Path

function Test-Skipped([System.IO.FileInfo]$file) {
    $rel = $file.FullName.Substring($testsRoot.Length).TrimStart('\','/')
    # Skip any folder used to host imported modules / fixtures rather than
    # runnable tests: a segment containing "helpers", or named std/fixtures.
    if ($rel -match "[\\/][^\\/]*helpers[^\\/]*[\\/]") { return $true }
    if ($rel -match "(^|[\\/])std[\\/]")               { return $true }
    if ($rel -match "(^|[\\/])fixtures[\\/]")          { return $true }
    # regressions/ is the parking lot for known-broken probes (documented
    # bugs awaiting a fix). They are skipped so the suite stays green; each
    # file's header explains the bug and links the tracking task.
    if ($rel -match "(^|[\\/])regressions[\\/]")        { return $true }
    if ($file.Name -eq "probe.ra")                     { return $true }
    # Opt-out directive on any of the first 3 lines.
    $head = Get-Content -LiteralPath $file.FullName -TotalCount 3 -ErrorAction SilentlyContinue
    foreach ($line in $head) { if ($line -match "#\s*runner:\s*skip") { return $true } }
    return $false
}

$files = Get-ChildItem -Path $testsRoot -Filter *.ra -Recurse -File |
    Where-Object {
        if (Test-Skipped $_) { return $false }
        if ($Filter -ne "") {
            $rel = $_.FullName.Substring($testsRoot.Length)
            if ($rel -notmatch [regex]::Escape($Filter)) { return $false }
        }
        return $true
    } | Sort-Object FullName

$okMarker   = '^\[[^\]]+\]\s+OK\b|^OK\s'
$failMarker = '^\[[^\]]+\]\s+FAIL\b|^FAIL\s'
$errSig     = '^error\[|^Traceback \(most recent|Compilation aborted'

$totalOk = 0; $totalFail = 0
$filesPass = 0; $filesFail = 0; $filesTimeout = 0
$failures = New-Object System.Collections.Generic.List[string]

foreach ($f in $files) {
    $rel = $f.FullName.Substring($testsRoot.Length).TrimStart('\','/')

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = '"' + $f.FullName + '"'
    $psi.WorkingDirectory = $workingDir
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow  = $true

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    $null = $proc.Start()
    $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
    $stderrTask = $proc.StandardError.ReadToEndAsync()
    $exited = $proc.WaitForExit($TimeoutSeconds * 1000)

    if (-not $exited) {
        try { $proc.Kill() } catch {}
        $proc.WaitForExit()
        Write-Host ("{0,-62} {1}" -f $rel, "TIMEOUT (${TimeoutSeconds}s)")
        $filesTimeout++
        $failures.Add($rel + "  (timeout)")
        continue
    }

    $stdout = $stdoutTask.Result
    $stderr = $stderrTask.Result
    $code   = $proc.ExitCode

    $okCount   = ([regex]::Matches($stdout, $okMarker,   'Multiline')).Count
    $failCount = ([regex]::Matches($stdout, $failMarker, 'Multiline')).Count
    $hasErrSig = [regex]::IsMatch($stdout, $errSig, 'Multiline') -or [regex]::IsMatch($stderr, $errSig, 'Multiline')

    $totalOk   += $okCount
    $totalFail += $failCount

    $pass = ($code -eq 0) -and ($failCount -eq 0)
    if ($pass) {
        $filesPass++
        if (-not $Quiet) { Write-Host ("{0,-62} {1}" -f $rel, "PASS ($okCount)") }
    } else {
        $filesFail++
        $why = @()
        if ($code -ne 0)      { $why += "exit $code" }
        if ($failCount -gt 0) { $why += "$failCount FAIL" }
        if ($hasErrSig)       { $why += "uncaught error" }
        $status = "FAIL [" + ($why -join ", ") + "]"
        Write-Host ("{0,-62} {1}" -f $rel, $status)
        $tail = (($stdout -split "`n") | Where-Object { $_ -ne "" } | Select-Object -Last 4) -join " | "
        if ($tail)   { Write-Host "   out> $tail" }
        if ($stderr) { Write-Host "   err> $((($stderr -split "`n") | Select-Object -First 2) -join ' | ')" }
        $failures.Add($rel + "  ($status)")
    }
}

Write-Host ""
Write-Host "=== Summary ==="
Write-Host ("files      : {0} pass, {1} fail, {2} timeout (of {3})" -f $filesPass, $filesFail, $filesTimeout, $files.Count)
Write-Host ("assertions : {0} OK, {1} FAIL" -f $totalOk, $totalFail)
$filePhaseFailed = ($failures.Count -gt 0)
if ($filePhaseFailed) {
    Write-Host ""
    Write-Host "Failing files:"
    foreach ($x in $failures) { Write-Host "  - $x" }
}

# .rac archive pipeline phase. The .ra runner above can't exercise the
# compile -> archive -> run path, so delegate to the dedicated driver. Skipped
# under -NoArchive or when a -Filter narrows the run to a category.
$archivePhaseFailed = $false
$archiveDriver = Join-Path $testsRoot "archive\run_archive_tests.ps1"
if (-not $NoArchive -and $Filter -eq "" -and (Test-Path $archiveDriver)) {
    Write-Host ""
    Write-Host "=== Archive (.rac) pipeline ==="
    & $archiveDriver -TimeoutSeconds $TimeoutSeconds -Quiet:$Quiet
    if ($LASTEXITCODE -ne 0) { $archivePhaseFailed = $true }
}

if ($filePhaseFailed -or $archivePhaseFailed) { exit 1 }
exit 0
