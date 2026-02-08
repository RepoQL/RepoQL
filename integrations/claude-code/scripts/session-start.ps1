# RepoQL SessionStart hook — inject orientation context.
# Stdout is added to the agent's context. Always exits 0.

$ErrorActionPreference = "SilentlyContinue"

$repoql = Get-Command repoql -ErrorAction SilentlyContinue
if (-not $repoql) {
    Write-Output "RepoQL is not installed. Install: irm https://downloads.repoql.ai/install.ps1 | iex"
    exit 0
}

Write-Output "# RepoQL: Repository Orientation"
Write-Output ""

Write-Output "## Repository Structure"
$repoOutput = & repoql read "file:///** => tree: folders" --budget 3000 2>$null
if ($LASTEXITCODE -eq 0 -and $repoOutput) { Write-Output $repoOutput }
else { Write-Output "(no index — run repoql index)" }
Write-Output ""

Write-Output "## Documentation"
$docsOutput = & repoql read "help://** => tree: headlines" --budget 5000 2>$null
if ($LASTEXITCODE -eq 0 -and $docsOutput) { Write-Output $docsOutput }
else { Write-Output "(no docs indexed)" }

exit 0
