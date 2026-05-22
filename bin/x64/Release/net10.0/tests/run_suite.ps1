# Ra Language - test suite driver.
#
# Runs every .ra under tests/ (except the helpers/ folders and a few
# explicit hangs). For each script:
#   - launches the interpreter with a hard 15 s wall-clock timeout
#   - captures stdout / stderr
#   - parses lines of the form "[<id>] OK" / "[<id>] FAIL" to score the file
#   - records timeout / parse-error / crash separately
#
# Usage from the repository root:
#   pwsh -File tests/run_suite.ps1
#   pwsh -File tests/run_suite.ps1 -Filter operators
#   pwsh -File tests/run_suite.ps1 -IncludeRegressions

[CmdletBinding()]
param(
    [string]$Filter = "",
    [switch]$IncludeRegressions,
    [int]$TimeoutSeconds = 15
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path

# `dotnet build -c Release` lands the executable at bin\Release\net10.0\,
# while `dotnet publish -c Release -r win-x64` (or a previous artifact)
# uses bin\x64\Release\net10.0\. Pick whichever exists.
$exeCandidates = @(
    "bin\Release\net10.0\RaLanguage.exe",
    "bin\x64\Release\net10.0\RaLanguage.exe"
)
$exe = $null
foreach ($cand in $exeCandidates) {
    $abs = Join-Path $repoRoot $cand
    if (Test-Path $abs) { $exe = $abs; break }
}
if (-not $exe) {
    Write-Error "Interpreter not found in any of: $($exeCandidates -join ', '). Run 'dotnet build -c Release' first."
}

$testsRoot = Join-Path $repoRoot "tests"
$files = Get-ChildItem -Path $testsRoot -Filter *.ra -Recurse -File |
    Where-Object {
        $rel = $_.FullName.Substring($testsRoot.Length + 1)
        # Skip helper files imported by other tests.
        if ($rel -match "[\\/]helpers[\\/]") { return $false }
        # Skip the inline probe scratch file.
        if ($_.Name -eq "probe.ra") { return $false }
        # Regressions are opt-in - they are known to fail / hang.
        if (-not $IncludeRegressions -and $rel -like "regressions*") { return $false }
        if ($Filter -ne "" -and $rel -notmatch [regex]::Escape($Filter)) { return $false }
        return $true
    } |
    Sort-Object FullName

$totalOk      = 0
$totalFail    = 0
$totalTimeout = 0
$totalCrash   = 0
$fileResults  = New-Object System.Collections.Generic.List[object]

foreach ($f in $files) {
    $rel = $f.FullName.Substring($repoRoot.Length + 1)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = "`"$($f.FullName)`""
    $psi.WorkingDirectory = $repoRoot
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
        try { $proc.Kill($true) } catch {}
        $proc.WaitForExit()
        $stdout = ""
        $stderr = "[runner] killed after $TimeoutSeconds s"
        $kind = "TIMEOUT"
    } else {
        $stdout = $stdoutTask.Result
        $stderr = $stderrTask.Result
        $kind = if ($proc.ExitCode -eq 0) { "OK" } else { "CRASH" }
    }

    $okMatches   = [regex]::Matches($stdout, '\[[^\]]+\] OK')
    $failMatches = [regex]::Matches($stdout, '\[[^\]]+\] FAIL')
    $okCount   = $okMatches.Count
    $failCount = $failMatches.Count

    $status = ""
    switch ($kind) {
        "OK"      { if ($failCount -gt 0) { $status = "FAIL ($okCount/$($okCount + $failCount))" } else { $status = "PASS ($okCount)" } }
        "CRASH"   { $status = "CRASH (exit $($proc.ExitCode))" }
        "TIMEOUT" { $status = "TIMEOUT (${TimeoutSeconds}s)" }
    }

    Write-Host ("{0,-60} {1}" -f $rel, $status)
    if ($kind -ne "OK" -or $failCount -gt 0) {
        $tail = ($stdout -split "`n" | Select-Object -Last 4) -join " | "
        if ($tail) { Write-Host "   stdout> $tail" }
        if ($stderr) {
            $errTail = ($stderr -split "`n" | Select-Object -First 4) -join " | "
            Write-Host "   stderr> $errTail"
        }
    }

    $totalOk   += $okCount
    $totalFail += $failCount
    if ($kind -eq "TIMEOUT") { $totalTimeout++ }
    if ($kind -eq "CRASH")   { $totalCrash++ }

    $fileResults.Add([pscustomobject]@{
        File = $rel
        Kind = $kind
        OK = $okCount
        FAIL = $failCount
    })
}

Write-Host ""
Write-Host "=== Summary ==="
Write-Host ("files     : {0}" -f $files.Count)
Write-Host ("assertions: {0} OK, {1} FAIL" -f $totalOk, $totalFail)
Write-Host ("files     : {0} timed out, {1} crashed" -f $totalTimeout, $totalCrash)

if ($totalFail -gt 0 -or $totalTimeout -gt 0 -or $totalCrash -gt 0) {
    exit 1
}
exit 0
