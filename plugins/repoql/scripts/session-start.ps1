# RepoQL SessionStart hook for Claude Code under PowerShell - bootstrap the host
# if needed, inject repository orientation, and load .repoql/concepts/readme.md
# when the workspace provides it. session-start.sh is the same hook for bash.
#
# Claude Code does not order this hook against MCP server startup, so the
# bundled server never depends on it: the rql-mcp launcher finds or installs
# rql itself, and the bootstrap here shares that download. Always exits 0 so a
# missing rql, a host that is down, or an unindexed repo never blocks the
# session.
. (Join-Path $PSScriptRoot 'hook-io.ps1')

$dash = [char]0x2014
$ellipsis = [char]0x2026
$context = ''
try {
    $workspace = (Read-HookInput).cwd
    if (-not (Test-Directory $workspace)) { $workspace = (Get-Location).Path }
    Set-Location -LiteralPath $workspace

    $stateDir = $env:PLUGIN_DATA
    if (-not $stateDir) { $stateDir = $env:CLAUDE_PLUGIN_DATA }
    if (-not $stateDir) { $stateDir = Join-Path $HOME '.local\state\repoql' }

    $freshInstall = $false
    if (-not (Get-Command rql)) {
        & (Join-Path $PSScriptRoot 'bootstrap-rql.ps1') | Out-Null
        $freshInstall = $LASTEXITCODE -eq 0
    }

    if (-not (Get-Command rql)) {
        if ($env:REPOQL_NO_BOOTSTRAP -ne '1') {
            $context = "# RepoQL: host not installed`n"
            $context += "The RepoQL plugin is installed but the rql binary is missing and automatic install failed (log: $(Join-Path $stateDir 'bootstrap.log')). Tell the user to install it manually from PowerShell and start a new session:`n"
            $context += "  irm https://downloads.repoql.ai/latest/install-rql.ps1 | iex`n"
        }
    } else {
        $context = "# RepoQL: Repository Orientation`n"
        if ($freshInstall) {
            $context += "`nrql was just installed (first session with this plugin). The host indexes this repository in the background, so RepoQL tools may need a moment before returning results. If the RepoQL MCP tools are unavailable, the download outlasted the server's connection timeout: tell the user to reconnect the repoql server from /mcp, or to start a new session.`n"
        } else {
            # Each workspace repository and import, with the concepts and vocab words it carries. Hosts that
            # predate Filesystems.kind fail the first query and fall back to the GitHub-only listing.
            # --no-launch avoids starting a host just for orientation.
            $listingSql = "WITH repos AS (SELECT kind, source_uri, concat_ws('/', scheme, nullif(authority, ''), nullif(trim(path_prefix, '/'), '')) AS memory FROM Filesystems WHERE kind IN ('workspace', 'import')), memory AS (SELECT uri FROM Files WHERE (uri LIKE 'concept:///%' AND extension = '.md' AND lower(name) <> 'readme.md') OR uri LIKE 'vocabulary:///%'), counts AS (SELECT r.kind, r.source_uri, count(m.uri) FILTER (WHERE starts_with(m.uri, 'concept:///' || r.memory || '/')) AS concepts, count(m.uri) FILTER (WHERE starts_with(m.uri, 'vocabulary:///' || r.memory || '/')) AS words FROM repos r LEFT JOIN memory m ON starts_with(m.uri, 'concept:///' || r.memory || '/') OR starts_with(m.uri, 'vocabulary:///' || r.memory || '/') GROUP BY r.kind, r.source_uri) SELECT kind, source_uri || coalesce(' (' || nullif(concat_ws(', ', CASE WHEN concepts > 0 THEN concepts || ' concept' || CASE WHEN concepts = 1 THEN '' ELSE 's' END END, CASE WHEN words > 0 THEN words || ' vocab word' || CASE WHEN words = 1 THEN '' ELSE 's' END END), '') || ')', '') AS line FROM counts ORDER BY kind, source_uri"
            $legacySql = "SELECT 'import' AS kind, source_uri AS line FROM Filesystems WHERE source_uri LIKE 'github://%' ORDER BY source_uri"
            $listing = $null | rql query $listingSql --timeout-ms 5000 --no-launch 2>$null
            if ($LASTEXITCODE -ne 0) { $listing = $null | rql query $legacySql --timeout-ms 5000 --no-launch 2>$null }
            $queryOk = $LASTEXITCODE -eq 0
            $workspaceRepos = @($listing | Where-Object { $_ -like "workspace`t*://*" } | ForEach-Object { ($_ -split "`t", 2)[1] })
            $imports = @($listing | Where-Object { $_ -like "import`t*://*" } | ForEach-Object { ($_ -split "`t", 2)[1] })
            if ($workspaceRepos.Count -gt 0) {
                $context += "`n## Workspace Repositories`n"
                $context += "This workspace is a directory of repositories. Each answers to its own URI below; file:/// is only the loose files at the top level.`n"
                $context += ($workspaceRepos -join "`n") + "`n"
            }
            $context += "`n## Imported Repositories`n"
            if ($imports.Count -gt 0) {
                $context += "Use these URIs directly with read / explore / query:`n" + ($imports -join "`n") + "`n"
            } elseif ($queryOk) {
                $context += "(none)`n"
            } else {
                $context += "(not checked $dash the RepoQL host was not running)`n"
            }
            $context += "Use the import tool whenever you like to add more.`n"
        }
        $uplinks = $null | rql uplinks 2>$null
        if ($LASTEXITCODE -eq 0) {
            $context += "`n## Accessible Uplinks`n" + (@($uplinks) -join "`n") + "`n"
        } else {
            $context += "`n## Accessible Uplinks`n(not checked $dash run rql uplinks to discover account access)`n"
        }
        $context += "`n## Concepts`nconcept:///** holds the concepts of this repository and its imports.`n"
    }

    $readme = Get-ChildItem -LiteralPath (Join-Path $workspace '.repoql/concepts') -File |
        Where-Object { $_.Name -ieq 'readme.md' } | Select-Object -First 1
    if ($readme) {
        if ($context) { $context += "`n" }
        $context += "## Repository Concepts Index (.repoql/concepts/$($readme.Name))`n`n"
        $context += [IO.File]::ReadAllText($readme.FullName).TrimEnd("`r", "`n") + "`n"
    }
} catch { }

if ($context) { Write-HookContext 'SessionStart' $context }
exit 0
