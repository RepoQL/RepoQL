# Bootstrap RepoQL if needed, inject orientation, and load the workspace's
# .repoql/concepts/readme.md when present.
# Fail open so an unavailable host never blocks a session.
$ErrorActionPreference = "SilentlyContinue"

function Write-HookContext([string]$Context) {
    @{
        hookSpecificOutput = @{
            hookEventName = "SessionStart"
            additionalContext = $Context
        }
    } | ConvertTo-Json -Compress -Depth 4
}

$hookInput = [Console]::In.ReadToEnd()
$workspace = (Get-Location).Path
try {
    $hookPayload = $hookInput | ConvertFrom-Json
    if ($hookPayload.cwd -and (Test-Path -LiteralPath $hookPayload.cwd -PathType Container)) {
        $workspace = $hookPayload.cwd
    }
} catch {}

$pluginData = if ($env:PLUGIN_DATA) { $env:PLUGIN_DATA } elseif ($env:CLAUDE_PLUGIN_DATA) { $env:CLAUDE_PLUGIN_DATA } else { Join-Path $env:LOCALAPPDATA "RepoQL" }
$env:Path = "$(Join-Path $env:LOCALAPPDATA 'rql');$(Join-Path $env:USERPROFILE '.local\bin');$env:Path"
$rql = Get-Command rql -ErrorAction SilentlyContinue
$freshInstall = $false

if (-not $rql -and $env:REPOQL_NO_BOOTSTRAP -ne "1") {
    New-Item -ItemType Directory -Force -Path $pluginData | Out-Null
    $log = Join-Path $pluginData "bootstrap.log"
    try {
        "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] rql missing — installing from downloads.repoql.ai" | Add-Content $log
        $installer = Invoke-RestMethod -Uri "https://downloads.repoql.ai/latest/install-rql.ps1" -TimeoutSec 30
        Invoke-Expression $installer | Add-Content $log
        $rql = Get-Command rql -ErrorAction SilentlyContinue
        $freshInstall = [bool]$rql
    } catch {
        "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] bootstrap failed: $($_.Exception.Message)" | Add-Content $log
    }
}

$ctx = ""
if (-not $rql) {
    if ($env:REPOQL_NO_BOOTSTRAP -ne "1") {
        $ctx = @"
# RepoQL: host not installed
The RepoQL plugin is installed, but automatic rql installation failed (log: $(Join-Path $pluginData 'bootstrap.log')). Tell the user to run this in PowerShell and start a new Codex task:
  irm https://downloads.repoql.ai/latest/install-rql.ps1 | iex
"@
    }
} else {
    $ctx = "# RepoQL: Repository Orientation`n"
    if ($freshInstall) {
        $ctx += "`nrql was just installed. RepoQL is indexing this repository in the background, so its tools may need a moment before returning results. If the mcp__repoql__* tools are unavailable, start a new Codex task so the MCP server picks up the new PATH.`n"
    } else {
        # Each workspace repository and import, with the concepts and vocab words it carries. Hosts that
        # predate Filesystems.kind fail the first query and fall back to the GitHub-only listing.
        $listingSql = "WITH repos AS (SELECT kind, source_uri, concat_ws('/', scheme, nullif(authority, ''), nullif(trim(path_prefix, '/'), '')) AS memory FROM Filesystems WHERE kind IN ('workspace', 'import')), memory AS (SELECT uri FROM Files WHERE (uri LIKE 'concept:///%' AND extension = '.md' AND lower(name) <> 'readme.md') OR uri LIKE 'vocabulary:///%'), counts AS (SELECT r.kind, r.source_uri, count(m.uri) FILTER (WHERE starts_with(m.uri, 'concept:///' || r.memory || '/')) AS concepts, count(m.uri) FILTER (WHERE starts_with(m.uri, 'vocabulary:///' || r.memory || '/')) AS words FROM repos r LEFT JOIN memory m ON starts_with(m.uri, 'concept:///' || r.memory || '/') OR starts_with(m.uri, 'vocabulary:///' || r.memory || '/') GROUP BY r.kind, r.source_uri) SELECT kind, source_uri || coalesce(' (' || nullif(concat_ws(', ', CASE WHEN concepts > 0 THEN concepts || ' concept' || CASE WHEN concepts = 1 THEN '' ELSE 's' END END, CASE WHEN words > 0 THEN words || ' vocab word' || CASE WHEN words = 1 THEN '' ELSE 's' END END), '') || ')', '') AS line FROM counts ORDER BY kind, source_uri"
        $legacySql = "SELECT 'import' AS kind, source_uri AS line FROM Filesystems WHERE source_uri LIKE 'github://%' ORDER BY source_uri"
        $listing = & $rql.Source query $listingSql --timeout-ms 5000 --no-launch 2>$null
        if ($LASTEXITCODE -ne 0) { $listing = & $rql.Source query $legacySql --timeout-ms 5000 --no-launch 2>$null }
        $queryOk = $LASTEXITCODE -eq 0
        $workspaceRepos = @($listing | Where-Object { $_ -like "workspace`t*://*" } | ForEach-Object { ($_ -split "`t", 2)[1] })
        $imports = @($listing | Where-Object { $_ -like "import`t*://*" } | ForEach-Object { ($_ -split "`t", 2)[1] })
        if ($workspaceRepos.Count -gt 0) {
            $ctx += "`n## Workspace Repositories`nThis workspace is a directory of repositories. Each answers to its own URI below; file:/// is only the loose files at the top level.`n$($workspaceRepos -join "`n")`n"
        }
        $ctx += "`n## Imported Repositories`n"
        if ($imports.Count -gt 0) {
            $ctx += "Use these URIs directly with read, explore, and query:`n$($imports -join "`n")`n"
        } elseif ($queryOk) {
            $ctx += "(none)`n"
        } else {
            $ctx += "(not checked — the RepoQL host was not running)`n"
        }
        $ctx += "Use the import tool whenever you like to add more.`n"
    }
    $uplinkContext = & $rql.Source uplinks 2>$null
    $ctx += "`n## Accessible Uplinks`n"
    if ($LASTEXITCODE -eq 0) {
        $ctx += "$($uplinkContext -join "`n")`n"
    } else {
        $ctx += "(not checked — run rql uplinks to discover account access)`n"
    }
    $ctx += "`n## Concepts`nRepository invariants are addressable at concept://, including the concepts imported sources carry — browse them with read(`"concept:///**`").`n"
}

$conceptsRelative = $null
$conceptsReadme = $null
foreach ($candidate in @(".repoql/concepts/readme.md", ".repoql/concepts/README.md")) {
    $candidatePath = Join-Path $workspace $candidate
    if (Test-Path -LiteralPath $candidatePath -PathType Leaf) {
        $conceptsRelative = $candidate
        $conceptsReadme = Get-Content -LiteralPath $candidatePath -Raw
        break
    }
}

if ($conceptsRelative) {
    if ($ctx) { $ctx += "`n" }
    $ctx += "## Repository Concepts Index ($conceptsRelative)`n`n$conceptsReadme`n"
}

if ($ctx) { Write-HookContext $ctx }
exit 0
