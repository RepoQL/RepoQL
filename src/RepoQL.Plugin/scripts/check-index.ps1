# RepoQL SessionStart hook - verify index freshness
# Non-blocking: always exits 0, sets environment variables for Claude

param()

# Read stdin JSON if available
$inputJson = $null
if (-not [Console]::IsInputRedirected) {
    # No stdin
} else {
    try {
        $inputJson = [Console]::In.ReadToEnd() | ConvertFrom-Json
    } catch {
        # Ignore parse errors
    }
}

$envFile = if ($inputJson.env_file) { $inputJson.env_file } else { $env:CLAUDE_ENV_FILE }

# Check if repoql is available
$repoqlPath = Get-Command repoql -ErrorAction SilentlyContinue
if (-not $repoqlPath) {
    if ($envFile) {
        Add-Content -Path $envFile -Value "REPOQL_STATUS=not_installed"
    }
    exit 0
}

# Check if index exists
$indexPath = ".repoql/index.db"
if (-not (Test-Path $indexPath)) {
    if ($envFile) {
        Add-Content -Path $envFile -Value "REPOQL_STATUS=no_index"
        Add-Content -Path $envFile -Value "REPOQL_MESSAGE=No index found. Run 'repoql index' to build."
    }
    exit 0
}

# Check index age (in hours)
$indexFile = Get-Item $indexPath
$indexAge = [math]::Floor(((Get-Date) - $indexFile.LastWriteTime).TotalHours)

# Set status based on age
if ($indexAge -gt 24) {
    if ($envFile) {
        Add-Content -Path $envFile -Value "REPOQL_STATUS=stale"
        Add-Content -Path $envFile -Value "REPOQL_MESSAGE=Index is ${indexAge}h old. Consider running 'repoql index --incremental'."
    }
} else {
    if ($envFile) {
        Add-Content -Path $envFile -Value "REPOQL_STATUS=ready"
    }
}

exit 0
