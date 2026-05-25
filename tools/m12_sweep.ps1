[CmdletBinding()]
param(
    [int]$TimeoutSeconds = 30
)

# Regression sweep for the full test corpus. Reports failures, crashes, and
# timeouts. Designed for CI / pre-commit sanity checks.

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$exe = (Get-ChildItem -Recurse -Filter "RaLanguage.exe" "$repoRoot\bin" | Where-Object { $_.FullName -notmatch "publish" } | Select-Object -First 1).FullName
if (-not $exe) { Write-Error "RaLanguage.exe not found" }

$testsRoot = (Get-ChildItem -Recurse -Directory -Filter "tests" "$repoRoot\bin" | Where-Object { $_.FullName -notmatch "publish" } | Select-Object -First 1).FullName
$files = Get-ChildItem -Path $testsRoot -Filter *.ra -Recurse -File |
    Where-Object {
        $rel = $_.FullName.Substring($testsRoot.Length + 1)
        if ($rel -match "[\\/]helpers[\\/]") { return $false }
        if ($_.Name -eq "probe.ra") { return $false }
        if ($rel -like "regressions*") { return $false }
        return $true
    } | Sort-Object FullName

$totalOk = 0
$totalFail = 0
$totalTimeout = 0
$totalCrash = 0

foreach ($f in $files) {
    $rel = $f.FullName.Substring($testsRoot.Length + 1)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = "`"$($f.FullName)`""
    $psi.WorkingDirectory = $repoRoot
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    $null = $proc.Start()
    $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
    $stderrTask = $proc.StandardError.ReadToEndAsync()
    $exited = $proc.WaitForExit($TimeoutSeconds * 1000)
    if (-not $exited) {
        try { $proc.Kill($true) } catch {}
        $proc.WaitForExit()
        $kind = "TIMEOUT"
        $stdout = ""
    } else {
        $stdout = $stdoutTask.Result
        $kind = if ($proc.ExitCode -eq 0) { "OK" } else { "CRASH" }
    }
    $okCount = ([regex]::Matches($stdout, '\[[^\]]+\] OK')).Count
    $failCount = ([regex]::Matches($stdout, '\[[^\]]+\] FAIL')).Count
    # Real runtime aborts trip Traceback printing; expected errors caught by
    # `try/catch` log "[X] OK (rejected): error[...]" on a single line. Detect
    # only the abort path by checking for a Traceback that's NOT preceded by
    # an "[X] OK ..." on the same logical line.
    $hasRuntimeError = $stdout -match 'Traceback \(most recent call last\)'
    $totalOk += $okCount
    $totalFail += $failCount
    if ($kind -eq "TIMEOUT") { $totalTimeout++ }
    if ($kind -eq "CRASH") { $totalCrash++ }

    if ($kind -ne "OK" -or $failCount -gt 0 -or $hasRuntimeError) {
        $tag = if ($hasRuntimeError) { "RT-ERR" } else { "FAIL" }
        Write-Host ("$tag : {0,-60} kind={1} ok={2} fail={3}" -f $rel, $kind, $okCount, $failCount)
    }
}

Write-Host ""
Write-Host "=== sweep ==="
Write-Host ("files: {0}" -f $files.Count)
Write-Host ("assertions: {0} OK, {1} FAIL" -f $totalOk, $totalFail)
Write-Host ("processes: {0} timeout, {1} crash" -f $totalTimeout, $totalCrash)
