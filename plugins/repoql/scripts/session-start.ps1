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

# Each workspace repository and import, with the concepts and vocab words it carries. Hosts that
# predate Filesystems.kind fail the first query and fall back to the GitHub-only listing.
$listingSql = "WITH repos AS (SELECT kind, source_uri, concat_ws('/', scheme, nullif(authority, ''), nullif(trim(path_prefix, '/'), '')) AS memory FROM Filesystems WHERE kind IN ('workspace', 'import')), memory AS (SELECT uri FROM Files WHERE (uri LIKE 'concept:///%' AND extension = '.md' AND lower(name) <> 'readme.md') OR uri LIKE 'vocabulary:///%'), counts AS (SELECT r.kind, r.source_uri, count(m.uri) FILTER (WHERE starts_with(m.uri, 'concept:///' || r.memory || '/')) AS concepts, count(m.uri) FILTER (WHERE starts_with(m.uri, 'vocabulary:///' || r.memory || '/')) AS words FROM repos r LEFT JOIN memory m ON starts_with(m.uri, 'concept:///' || r.memory || '/') OR starts_with(m.uri, 'vocabulary:///' || r.memory || '/') GROUP BY r.kind, r.source_uri) SELECT kind, source_uri || coalesce(' (' || nullif(concat_ws(', ', CASE WHEN concepts > 0 THEN concepts || ' concept' || CASE WHEN concepts = 1 THEN '' ELSE 's' END END, CASE WHEN words > 0 THEN words || ' vocab word' || CASE WHEN words = 1 THEN '' ELSE 's' END END), '') || ')', '') AS line FROM counts ORDER BY kind, source_uri"
$legacySql = "SELECT 'import' AS kind, source_uri AS line FROM Filesystems WHERE source_uri LIKE 'github://%' ORDER BY source_uri"
$listing = & rql query $listingSql --timeout-ms 5000 --no-launch 2>$null
if ($LASTEXITCODE -ne 0) { $listing = & rql query $legacySql --timeout-ms 5000 --no-launch 2>$null }
$queryOk = $LASTEXITCODE -eq 0
$workspaceRepos = @($listing | Where-Object { $_ -like "workspace`t*://*" } | ForEach-Object { ($_ -split "`t", 2)[1] })
$imports = @($listing | Where-Object { $_ -like "import`t*://*" } | ForEach-Object { ($_ -split "`t", 2)[1] })
if ($workspaceRepos.Count -gt 0) {
    Write-Output "## Workspace Repositories"
    Write-Output "This workspace is a directory of repositories. Each answers to its own URI below; file:/// is only the loose files at the top level."
    Write-Output $workspaceRepos
    Write-Output ""
}
Write-Output "## Imported Repositories"
if ($imports.Count -gt 0) {
    Write-Output "Use these URIs directly with read / explore / query:"
    Write-Output $imports
}
elseif ($queryOk) { Write-Output "(none)" }
else { Write-Output "(not checked — the RepoQL host was not running)" }
Write-Output "Use the import tool whenever you like to add more."
Write-Output ""

Write-Output "## Accessible Uplinks"
$uplinkContext = & rql uplinks 2>$null
if ($LASTEXITCODE -eq 0) { Write-Output $uplinkContext }
else { Write-Output "(not checked — run rql uplinks to discover account access)" }
Write-Output ""

Write-Output "## Concepts"
Write-Output "concept:///** holds the concepts of this repository and its imports."
Write-Output ""

Write-Output "## Documentation"
$docsOutput = & rql read "help://** => tree: headlines" --token-budget 5000 2>$null
if ($LASTEXITCODE -eq 0 -and $docsOutput) { Write-Output $docsOutput }
else { Write-Output "(no docs indexed)" }

exit 0
