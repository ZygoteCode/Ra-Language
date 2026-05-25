[CmdletBinding()]
param(
    [int]$TimeoutSeconds = 30
)

# Regression sweep for the "other_tests" corpus. Same structure as
# tools/m12_sweep.ps1 but rooted at bin/.../other_tests so the suite the
# user added separately to bin/x64/... gets exercised too.

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$exe = (Get-ChildItem -Recurse -Filter "RaLanguage.exe" "$repoRoot\bin" | Where-Object { $_.FullName -notmatch "publish" } | Select-Object -First 1).FullName
if (-not $exe) { Write-Error "RaLanguage.exe not found" }

$testsRoot = (Get-ChildItem -Recurse -Directory -Filter "other_tests" "$repoRoot\bin" | Where-Object { $_.FullName -notmatch "publish" } | Select-Object -First 1).FullName
if (-not $testsRoot) { Write-Error "other_tests directory not found" }

$files = Get-ChildItem -Path $testsRoot -Filter *.ra -Recurse -File |
    Where-Object {
        $rel = $_.FullName.Substring($testsRoot.Length + 1)
        if ($rel -match "[\\/]helpers[\\/]") { return $false }
        if ($rel -match "[\\/]imports_helpers[\\/]") { return $false }
        if ($rel -match "[\\/]tests_namespaces_helpers[\\/]") { return $false }
        if ($rel -match "[\\/]std[\\/]") { return $false }
        if ($_.Name -eq "probe.ra") { return $false }
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
    # Run from the test's own directory so relative imports / file ops resolve.
    $psi.WorkingDirectory = $f.DirectoryName
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
        $stderr = ""
    } else {
        $stdout = $stdoutTask.Result
        $stderr = $stderrTask.Result
        $kind = if ($proc.ExitCode -eq 0) { "OK" } else { "CRASH" }
    }
    $okCount = ([regex]::Matches($stdout, '\[[^\]]+\] OK')).Count
    $failCount = ([regex]::Matches($stdout, '\[[^\]]+\] FAIL')).Count
    $hasRuntimeError = $stdout -match 'Traceback \(most recent call last\)'
    $totalOk += $okCount
    $totalFail += $failCount
    if ($kind -eq "TIMEOUT") { $totalTimeout++ }
    if ($kind -eq "CRASH") { $totalCrash++ }

    if ($kind -ne "OK" -or $failCount -gt 0 -or $hasRuntimeError) {
        $tag = if ($hasRuntimeError) { "RT-ERR" } elseif ($kind -eq "OK") { "WARN" } else { "FAIL" }
        Write-Host ("$tag : {0,-60} kind={1} ok={2} fail={3}" -f $rel, $kind, $okCount, $failCount)
        if ($stderr -and $stderr.Length -gt 0) {
            $firstLine = ($stderr -split "`n")[0]
            Write-Host ("        stderr: $firstLine")
        }
        if ($stdout -and $stdout.Length -gt 0) {
            $errLine = ($stdout -split "`n" | Where-Object { $_ -match 'error\[|Traceback|FAIL' } | Select-Object -First 1)
            if ($errLine) { Write-Host ("        stdout: $($errLine.Trim())") }
        }
    }
}

Write-Host ""
Write-Host "=== other_tests sweep ==="
Write-Host ("files: {0}" -f $files.Count)
Write-Host ("assertions: {0} OK, {1} FAIL" -f $totalOk, $totalFail)
Write-Host ("processes: {0} timeout, {1} crash" -f $totalTimeout, $totalCrash)
