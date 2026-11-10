# RepoQL SessionStart hook for Claude Code under PowerShell - bootstrap the host
# if needed, inject repository orientation, and load .repoql/concepts/readme.md
# when CLAUDE.md does not import it. session-start.sh is the same hook for bash.
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
# An install is otherwise silent, so its outcome also goes to the user, as the
# reply's systemMessage.
$notice = ''
try {
    $workspace = (Read-HookInput).cwd
    if (-not (Test-Directory $workspace)) { $workspace = (Get-Location).Path }
    Set-Location -LiteralPath $workspace

    $freshInstall = $false
    $bootstrapStatus = 0
    $bootstrapReason = ''
    if (-not (Get-Command rql)) {
        # A failed bootstrap prints why; exit 2 means an install is still running.
        $bootstrapReason = (@(& (Join-Path $PSScriptRoot 'bootstrap-rql.ps1')) -join ' ').Trim()
        $bootstrapStatus = $LASTEXITCODE
        $freshInstall = $bootstrapStatus -eq 0
    }

    if (-not (Get-Command rql)) {
        if ($env:REPOQL_NO_BOOTSTRAP -ne '1') {
            if ($bootstrapStatus -eq 2) {
                $context = "# RepoQL: host install in progress`n"
                $context += "The RepoQL plugin is installed and the rql binary is still downloading in the background, so RepoQL tools are not available yet. They appear in this session when the download finishes if the repoql MCP server is connected; otherwise tell the user to reconnect it from /mcp, or to start a new session in a minute. Nothing needs installing by hand.`n"
                $notice = 'RepoQL is still downloading rql (about 180 MB). Its tools appear when the download finishes; if they do not, reconnect repoql from /mcp.'
            } else {
                if (-not $bootstrapReason) { $bootstrapReason = 'automatic install failed' }
                $context = "# RepoQL: host not installed`n"
                $context += "The RepoQL plugin is installed but the rql binary is missing and $bootstrapReason. Tell the user to install it manually from PowerShell and start a new session:`n"
                $context += "  irm https://downloads.repoql.ai/latest/install-rql.ps1 | iex`n"
                $notice = "RepoQL could not install rql: $bootstrapReason. Install it manually from PowerShell, then start a new session: irm https://downloads.repoql.ai/latest/install-rql.ps1 | iex"
            }
        }
    } else {
        $context = "# RepoQL: Repository Orientation`n"
        if ($freshInstall) {
            $notice = "RepoQL installed rql to $(Split-Path -Parent (Get-Command rql).Source)."
            $context += "`nrql was just installed (first session with this plugin). The host indexes this repository in the background, so RepoQL tools may need a moment before returning results. If the RepoQL MCP tools are unavailable, the download outlasted the server's connection timeout: tell the user to reconnect the repoql server from /mcp, or to start a new session.`n"
        } else {
            # Every mounted source except the ones named in the WHERE clause, which the agent already knows or did not
            # ask for: the primary file:///, help, memory, and worktrees. A new kind of mount is listed by default, with
            # its kind in brackets. Each line also counts the concepts and vocab words the source carries. Hosts that
            # predate Filesystems.kind fail the first query and fall back to the GitHub-only listing. The address column is
            # uri from rql 1.7.20 and source_uri before it; COLUMNS picks whichever the host has, so each query serves both.
            # --no-launch avoids starting a host just for orientation.
            $listingSql = "WITH repos AS (SELECT CASE WHEN kind = 'workspace' THEN 'workspace' ELSE 'import' END AS section, CASE WHEN kind NOT IN ('workspace', 'import') THEN kind END AS label, COLUMNS(c -> c IN ('uri', 'source_uri')) AS uri, concat_ws('/', scheme, nullif(authority, ''), nullif(trim(path_prefix, '/'), '')) AS memory FROM Filesystems WHERE scheme NOT IN ('file', 'help', 'concept', 'vocabulary', 'worktree') AND coalesce(kind, '') NOT IN ('primary', 'worktree')), memory AS (SELECT uri FROM Files WHERE (uri LIKE 'concept:///%' AND extension = '.md' AND lower(name) <> 'readme.md') OR uri LIKE 'vocabulary:///%'), counts AS (SELECT r.section, r.label, r.uri, count(m.uri) FILTER (WHERE starts_with(m.uri, 'concept:///' || r.memory || '/')) AS concepts, count(m.uri) FILTER (WHERE starts_with(m.uri, 'vocabulary:///' || r.memory || '/')) AS words FROM repos r LEFT JOIN memory m ON starts_with(m.uri, 'concept:///' || r.memory || '/') OR starts_with(m.uri, 'vocabulary:///' || r.memory || '/') GROUP BY r.section, r.label, r.uri) SELECT section AS kind, uri || coalesce(' (' || nullif(concat_ws(', ', label, CASE WHEN concepts > 0 THEN concepts || ' concept' || CASE WHEN concepts = 1 THEN '' ELSE 's' END END, CASE WHEN words > 0 THEN words || ' vocab word' || CASE WHEN words = 1 THEN '' ELSE 's' END END), '') || ')', '') AS line FROM counts ORDER BY section, uri"
            $legacySql = "SELECT 'import' AS kind, uri AS line FROM (SELECT COLUMNS(c -> c IN ('uri', 'source_uri')) AS uri FROM Filesystems) WHERE uri LIKE 'github://%' ORDER BY uri"
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

        # Sign-in: nothing else tells a plugin user that signing in exists, because the plugin installs rql without
        # `rql install` and its browser setup. Say it once per machine, and only when rql reports no credential at
        # all: a session that fails its live check (offline, say) is not a signed-out user. Signing in is the consent
        # boundary for sending code and usage records to RepoQL, so this never signs in; it says what signing in does.
        $stateDir = $env:REPOQL_STATE_DIR
        if (-not $stateDir) { $stateDir = Join-Path $HOME '.local\state\repoql' }
        $signinMarker = Join-Path $stateDir 'signin-notice'
        if (-not (Test-Path -LiteralPath $signinMarker)) {
            $whoamiText = (@($null | rql account whoami --json 2>$null) -join "`n")
            $whoamiStatus = $LASTEXITCODE
            $report = ConvertFrom-HookJson $whoamiText
            $signinState = ''
            if ($report -and $report.authenticated -eq $true) {
                $signinState = 'signed-in'
            } elseif ($report -and $whoamiStatus -eq 1 -and $report.authenticated -eq $false -and $report.material -and
                      $report.material.sessionPresent -eq $false -and -not $report.material.apiKeyMasked -and
                      $report.material.oidcEnvironmentAvailable -ne $true) {
                $signinState = 'signed-out'
            }
            if ($signinState -eq 'signed-out') {
                $signinNotice = 'RepoQL is running signed out: it indexes and searches on this machine, and explain, reranking and cloud embeddings are off. Sign in with `rql account login` to turn them on. Signing in sends the text RepoQL embeds or explains, and a usage record per tool call, to RepoQL (https://repoql.com/docs/your-data/), under the Customer Terms and Software Licence (https://repoql.com/terms/2026-09-11/). This note appears once.'
                if ($notice) { $notice = "$notice $signinNotice" } else { $notice = $signinNotice }
                $context += "`n## Account`nrql is signed out: explain, reranking and cloud embeddings are unavailable, and search uses local embeddings. If the user wants them, they sign in with ``rql account login``, which opens a browser. Signing in sends their code's text and usage records to RepoQL, so offer it and let them decide; never run it unasked.`n"
            }
            if ($signinState) {
                try {
                    New-Item -ItemType Directory -Force -Path $stateDir -ErrorAction Stop | Out-Null
                    New-Item -ItemType File -Force -Path $signinMarker -ErrorAction Stop | Out-Null
                } catch { }
            }
        }
    }

    # The host adds the line @.repoql/concepts/README.md to CLAUDE.md, and Claude Code
    # loads the index through that import; inject the readme only when the import is
    # absent. The line counts when it stands alone, as the host's own check reads it.
    $claudeMd = Join-Path $workspace 'CLAUDE.md'
    $imported = $false
    if (Test-Path -LiteralPath $claudeMd -PathType Leaf) {
        $imported = @([IO.File]::ReadAllLines($claudeMd) |
            Where-Object { $_.Trim() -cmatch '^@\.repoql/concepts/(README|readme)\.md$' }).Count -gt 0
    }
    $readme = Get-ChildItem -LiteralPath (Join-Path $workspace '.repoql/concepts') -File |
        Where-Object { $_.Name -ieq 'readme.md' } | Select-Object -First 1
    if ($readme -and -not $imported) {
        if ($context) { $context += "`n" }
        $context += "## Repository Concepts Index (.repoql/concepts/$($readme.Name))`n`n"
        $context += [IO.File]::ReadAllText($readme.FullName).TrimEnd("`r", "`n") + "`n"
    }
} catch { }

if ($context -and $notice) {
    Write-HookOutput @{ systemMessage = $notice; hookSpecificOutput = @{ hookEventName = 'SessionStart'; additionalContext = $context } }
} elseif ($context) {
    Write-HookContext 'SessionStart' $context
}
exit 0
