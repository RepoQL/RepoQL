# RepoQL sessionStart hook for Cursor on Windows - bootstrap the host if needed
# and inject repository orientation. session-start.sh is the same hook for
# macOS and Linux; run-hook.cmd picks between them.
#
# additional_context joins the conversation's initial context. Delivery races
# the first message (a known Cursor bug); the static rule in rules/ carries what
# must always arrive, so this context is a bonus. Unlike the shell hook this one
# exports no PATH: Windows names the variable Path, and a second spelling in a
# child's environment is undefined. Every hook here adds the rql directory itself.
. (Join-Path $PSScriptRoot 'hook-io.ps1')

$dash = [char]0x2014
$ellipsis = [char]0x2026
$context = ''
try {
    $workspace = $env:CURSOR_PROJECT_DIR
    if (-not (Test-Directory $workspace)) { $workspace = (Get-Location).Path }
    Set-Location -LiteralPath $workspace

    $freshInstall = $false
    $bootstrapStatus = 0
    $bootstrapReason = ''
    if (-not (Get-Command rql)) {
        # A failed bootstrap prints why; exit 2 means another session is installing.
        $bootstrapReason = (@(& (Join-Path $PSScriptRoot 'bootstrap-rql.ps1')) -join ' ').Trim()
        $bootstrapStatus = $LASTEXITCODE
        $freshInstall = $bootstrapStatus -eq 0
    }

    if (-not (Get-Command rql)) {
        if ($env:REPOQL_NO_BOOTSTRAP -ne '1') {
            if ($bootstrapStatus -eq 2) {
                $context = "# RepoQL: host install in progress`n"
                $context += "The RepoQL plugin is installed and another session is installing the rql binary right now, so RepoQL tools are not available in this session yet. Tell the user to reload the Cursor window in a minute; nothing needs installing by hand.`n"
            } else {
                if (-not $bootstrapReason) { $bootstrapReason = 'automatic install failed' }
                $context = "# RepoQL: host not installed`n"
                $context += "The RepoQL plugin is installed but the rql binary is missing and $bootstrapReason. Tell the user to install it manually from PowerShell, then reload the Cursor window:`n"
                $context += "  irm https://downloads.repoql.ai/latest/install-rql.ps1 | iex`n"
            }
        }
    } else {
        $context = "# RepoQL: Repository Orientation`n"
        if ($freshInstall) {
            # Cursor starts MCP servers with the editor, before this hook installed rql.
            $context += "`nrql was just installed (first session with this plugin). Cursor started its MCP servers before the install, so RepoQL's tools (explore, read, query, $ellipsis) appear after the user runs `"Developer: Reload Window`". The host indexes this repository in the background, so the tools may need a moment before returning results.`n"
        } else {
            # Every mounted source except the ones named in the WHERE clause, which the agent already knows or did not
            # ask for: the primary file:///, help, memory, and worktrees. A new kind of mount is listed by default, with
            # its kind in brackets. Each line also counts the concepts and vocab words the source carries. Hosts that
            # predate Filesystems.kind fail the first query and fall back to the GitHub-only listing.
            # --no-launch avoids starting a host just for orientation.
            $listingSql = "WITH repos AS (SELECT CASE WHEN kind = 'workspace' THEN 'workspace' ELSE 'import' END AS section, CASE WHEN kind NOT IN ('workspace', 'import') THEN kind END AS label, source_uri, concat_ws('/', scheme, nullif(authority, ''), nullif(trim(path_prefix, '/'), '')) AS memory FROM Filesystems WHERE scheme NOT IN ('file', 'help', 'concept', 'vocabulary', 'worktree') AND coalesce(kind, '') NOT IN ('primary', 'worktree')), memory AS (SELECT uri FROM Files WHERE (uri LIKE 'concept:///%' AND extension = '.md' AND lower(name) <> 'readme.md') OR uri LIKE 'vocabulary:///%'), counts AS (SELECT r.section, r.label, r.source_uri, count(m.uri) FILTER (WHERE starts_with(m.uri, 'concept:///' || r.memory || '/')) AS concepts, count(m.uri) FILTER (WHERE starts_with(m.uri, 'vocabulary:///' || r.memory || '/')) AS words FROM repos r LEFT JOIN memory m ON starts_with(m.uri, 'concept:///' || r.memory || '/') OR starts_with(m.uri, 'vocabulary:///' || r.memory || '/') GROUP BY r.section, r.label, r.source_uri) SELECT section AS kind, source_uri || coalesce(' (' || nullif(concat_ws(', ', label, CASE WHEN concepts > 0 THEN concepts || ' concept' || CASE WHEN concepts = 1 THEN '' ELSE 's' END END, CASE WHEN words > 0 THEN words || ' vocab word' || CASE WHEN words = 1 THEN '' ELSE 's' END END), '') || ')', '') AS line FROM counts ORDER BY section, source_uri"
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

    # The host maintains .cursor/rules/repoql-concepts.g.mdc, an always-applied rule
    # carrying the same index; inject the readme only when that rule is absent.
    if (-not (Test-Path -LiteralPath (Join-Path $workspace '.cursor/rules/repoql-concepts.g.mdc'))) {
        $readme = Get-ChildItem -LiteralPath (Join-Path $workspace '.repoql/concepts') -File |
            Where-Object { $_.Name -ieq 'readme.md' } | Select-Object -First 1
        if ($readme) {
            if ($context) { $context += "`n" }
            $context += "## Repository Concepts Index (.repoql/concepts/$($readme.Name))`n`n"
            $context += [IO.File]::ReadAllText($readme.FullName).TrimEnd("`r", "`n") + "`n"
        }
    }
} catch { }

if ($context) { Write-HookOutput @{ additional_context = $context } } else { Write-HookOutput @{} }
exit 0
