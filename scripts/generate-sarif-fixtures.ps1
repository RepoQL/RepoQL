<#
.SYNOPSIS
    Generates SARIF fixture files from real scanners targeting the RepoQL codebase.

.DESCRIPTION
    Runs available static analysis tools against the RepoQL repo and saves their
    SARIF output for use as test fixtures in RepoQL.Sarif.Tests.

    Tools that aren't installed are skipped with a message. Roslyn is always
    available (requires only dotnet). Other tools need separate installation.

    Generated files go into a 'generated/' subdirectory that is gitignored.
    Curate interesting results into committed fixture files for tests.

.PARAMETER OutputDir
    Directory for generated SARIF files. Defaults to Fixtures/generated/ under
    the SARIF test project.

.EXAMPLE
    ./scripts/generate-sarif-fixtures.ps1
    ./scripts/generate-sarif-fixtures.ps1 -OutputDir ./sarif-output
#>
param(
    [string]$OutputDir
)

$ErrorActionPreference = 'Continue'
$repoRoot = (git -C $PSScriptRoot rev-parse --show-toplevel 2>$null)
if (-not $repoRoot) {
    $repoRoot = Split-Path $PSScriptRoot -Parent
}

if (-not $OutputDir) {
    $OutputDir = Join-Path $repoRoot 'src/tests/RepoQL.Sarif.Tests/Fixtures/generated'
}

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$results = @()

function Write-ToolHeader($name) {
    Write-Host ""
    Write-Host "=== $name ===" -ForegroundColor Cyan
}

function Write-Skip($name, $reason) {
    Write-Host "  SKIP: $reason" -ForegroundColor Yellow
    $script:results += [PSCustomObject]@{ Tool = $name; Status = 'Skipped'; File = ''; Results = 0; Reason = $reason }
}

function Write-Done($name, $file) {
    if (Test-Path $file) {
        $size = (Get-Item $file).Length
        $sizeKB = [math]::Round($size / 1024, 1)
        # Count results in the SARIF
        try {
            $sarif = Get-Content $file -Raw | ConvertFrom-Json
            $count = 0
            foreach ($run in $sarif.runs) {
                if ($run.results) { $count += $run.results.Count }
            }
        } catch {
            $count = '?'
        }
        Write-Host "  OK: $file ($($sizeKB)KB, $count results)" -ForegroundColor Green
        $script:results += [PSCustomObject]@{ Tool = $name; Status = 'OK'; File = $file; Results = $count; Reason = '' }
    } else {
        Write-Host "  FAIL: Expected output not found at $file" -ForegroundColor Red
        $script:results += [PSCustomObject]@{ Tool = $name; Status = 'Failed'; File = $file; Results = 0; Reason = 'Output not found' }
    }
}

function Test-Command($cmd) {
    $null = Get-Command $cmd -ErrorAction SilentlyContinue
    return $?
}

# --- Roslyn ---
Write-ToolHeader 'Roslyn (dotnet build)'

$roslynOutput = Join-Path $OutputDir 'roslyn.sarif'
# ErrorLog per-project doesn't work with solution-level builds.
# Build each project separately? No — build the solution and capture per-project.
# Actually, ErrorLog with a solution build puts SARIF per-project next to the binary.
# Use a different approach: build with BinaryLogger and convert? Too complex.
# Simplest: build the main app project which pulls in most analyzers.
$mainProject = Join-Path $repoRoot 'src/RepoQL.ConsoleApp/RepoQL.ConsoleApp.csproj'
if (Test-Path $mainProject) {
    # ErrorLog path must be absolute for dotnet build
    # The comma between path and version must be escaped — MSBuild treats commas as property separators.
    # Using %2c (URL-encoded comma) or semicolons both work per Roslyn docs.
    $absOutput = [System.IO.Path]::GetFullPath($roslynOutput)
    Write-Host "  Building $mainProject with ErrorLog (SARIF v2.1)..."
    dotnet build $mainProject -c Release "-p:ErrorLog=$absOutput%2cversion=2.1" --no-incremental -v q 2>&1 | Out-Null
    Write-Done 'Roslyn' $roslynOutput
} else {
    Write-Skip 'Roslyn' "Main project not found at $mainProject"
}

# --- Semgrep ---
Write-ToolHeader 'Semgrep'

if (Test-Command 'semgrep') {
    $semgrepOutput = Join-Path $OutputDir 'semgrep.sarif'
    Write-Host "  Running semgrep with auto config..."
    Push-Location $repoRoot
    semgrep --sarif --output $semgrepOutput --config auto --quiet --no-git-ignore --max-target-bytes 500000 . 2>&1 | Out-Null
    Pop-Location
    Write-Done 'Semgrep' $semgrepOutput
} else {
    Write-Skip 'Semgrep' 'Not installed (pip install semgrep)'
}

# --- Trivy ---
Write-ToolHeader 'Trivy'

if (Test-Command 'trivy') {
    $trivyOutput = Join-Path $OutputDir 'trivy.sarif'
    Write-Host "  Running trivy filesystem scan..."
    Push-Location $repoRoot
    trivy fs --format sarif --output $trivyOutput --scanners vuln . 2>&1 | Out-Null
    Pop-Location
    Write-Done 'Trivy' $trivyOutput
} else {
    Write-Skip 'Trivy' 'Not installed (scoop install trivy / brew install trivy)'
}

# --- Snyk Code (SAST) ---
Write-ToolHeader 'Snyk Code'

if (Test-Command 'snyk') {
    # Check auth
    $authCheck = snyk auth check 2>&1
    if ($LASTEXITCODE -eq 0 -or ($authCheck -match 'authenticated')) {
        $snykCodeOutput = Join-Path $OutputDir 'snyk-code.sarif'
        Write-Host "  Running snyk code test..."
        Push-Location $repoRoot
        snyk code test --sarif-file-output=$snykCodeOutput 2>&1 | Out-Null
        Pop-Location
        Write-Done 'Snyk Code' $snykCodeOutput
    } else {
        Write-Skip 'Snyk Code' 'Not authenticated (run: snyk auth)'
    }
} else {
    Write-Skip 'Snyk Code' 'Not installed (npm i -g snyk)'
}

# --- Snyk OSS (SCA) ---
Write-ToolHeader 'Snyk OSS'

if (Test-Command 'snyk') {
    $authCheck = snyk auth check 2>&1
    if ($LASTEXITCODE -eq 0 -or ($authCheck -match 'authenticated')) {
        $snykOssOutput = Join-Path $OutputDir 'snyk-oss.sarif'
        Write-Host "  Running snyk test (OSS)..."
        Push-Location $repoRoot
        snyk test --sarif-file-output=$snykOssOutput --all-projects 2>&1 | Out-Null
        Pop-Location
        Write-Done 'Snyk OSS' $snykOssOutput
    } else {
        Write-Skip 'Snyk OSS' 'Not authenticated (run: snyk auth)'
    }
} else {
    Write-Skip 'Snyk OSS' 'Not installed (npm i -g snyk)'
}

# --- Summary ---
Write-Host ""
Write-Host "=== Summary ===" -ForegroundColor Cyan
Write-Host ""

$ok = ($results | Where-Object Status -eq 'OK').Count
$skipped = ($results | Where-Object Status -eq 'Skipped').Count
$failed = ($results | Where-Object Status -eq 'Failed').Count

foreach ($r in $results) {
    $color = switch ($r.Status) { 'OK' { 'Green' } 'Skipped' { 'Yellow' } default { 'Red' } }
    $detail = if ($r.Status -eq 'OK') { "$($r.Results) results" } else { $r.Reason }
    Write-Host "  $($r.Tool): $($r.Status) — $detail" -ForegroundColor $color
}

Write-Host ""
Write-Host "Generated: $ok  Skipped: $skipped  Failed: $failed" -ForegroundColor White
Write-Host "Output directory: $OutputDir" -ForegroundColor White

if ($ok -gt 0) {
    Write-Host ""
    Write-Host "Next steps:" -ForegroundColor White
    Write-Host "  1. Inspect generated SARIF files for interesting producer quirks" -ForegroundColor Gray
    Write-Host "  2. Curate small excerpts (5-20 results) into committed fixture files" -ForegroundColor Gray
    Write-Host "  3. Place curated files in src/tests/RepoQL.Sarif.Tests/Fixtures/" -ForegroundColor Gray
}
