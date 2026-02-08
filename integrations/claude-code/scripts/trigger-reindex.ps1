# RepoQL PostToolUse hook - trigger incremental reindex after file changes
# Non-blocking: always exits 0, runs reindex in background

param()

# Read tool input from stdin
$inputJson = $null
if ([Console]::IsInputRedirected) {
    try {
        $inputJson = [Console]::In.ReadToEnd() | ConvertFrom-Json
    } catch {
        exit 0
    }
}

if (-not $inputJson) {
    exit 0
}

# Extract file path from tool input
$filePath = $inputJson.tool_input.file_path
if (-not $filePath) {
    exit 0
}

# Check if repoql is available
$repoqlPath = Get-Command repoql -ErrorAction SilentlyContinue
if (-not $repoqlPath) {
    exit 0
}

# Check if we're in a RepoQL-indexed directory
if (-not (Test-Path ".repoql")) {
    exit 0
}

# Trigger incremental reindex in background
# This is fire-and-forget; the index will update asynchronously
Start-Job -ScriptBlock {
    repoql index --incremental --quiet
} | Out-Null

exit 0
