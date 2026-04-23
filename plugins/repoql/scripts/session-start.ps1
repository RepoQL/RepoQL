# RepoQL SessionStart hook — inject orientation context.
# Stdout is added to the agent's context. Always exits 0.

$ErrorActionPreference = "SilentlyContinue"

$rql = Get-Command rql -ErrorAction SilentlyContinue
if (-not $rql) {
    Write-Output "RepoQL is not installed. Install: irm https://downloads.repoql.ai/install.ps1 | iex"
    exit 0
}

Write-Output "# RepoQL: Repository Orientation"
Write-Output ""

Write-Output "## Repository Structure"
$repoOutput = & rql read "file:///** => tree: folders" --token-budget 3000 2>$null
if ($LASTEXITCODE -eq 0 -and $repoOutput) { Write-Output $repoOutput }
else { Write-Output "(no index — run rql serve)" }
Write-Output ""

Write-Output "## Documentation"
$docsOutput = & rql read "help://** => tree: headlines" --token-budget 5000 2>$null
if ($LASTEXITCODE -eq 0 -and $docsOutput) { Write-Output $docsOutput }
else { Write-Output "(no docs indexed)" }

exit 0
