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
        $sql = "SELECT source_uri FROM Filesystems WHERE source_uri LIKE 'github://%' ORDER BY source_uri"
        $imports = & $rql.Source query $sql --timeout-ms 5000 --no-launch 2>$null | Where-Object { $_ -match "github://" }
        $queryOk = $LASTEXITCODE -eq 0
        $ctx += "`n## Imported Repositories`n"
        if ($imports) {
            $ctx += "Use these github:// URIs directly with read, explore, and query:`n$($imports -join "`n")`n"
        } elseif ($queryOk) {
            $ctx += "(none — import one with: rql import github://owner/repo)`n"
        } else {
            $ctx += "(not checked — the RepoQL host was not running)`n"
        }
    }
    $ctx += "`n## Concepts`nRepository invariants are addressable at concept:// — browse them with read(`"concept:///**`").`n"
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
