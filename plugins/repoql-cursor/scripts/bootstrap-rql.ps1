# RepoQL bootstrap for Windows - ensure the rql host binary is installed,
# downloading it on first run. bootstrap-rql.sh does the same on macOS and Linux.
#
# Delegates to the hosted installer so the result is byte-identical to a manual
# install - one canonical binary per machine in %LOCALAPPDATA%\rql (plus the
# user PATH registry entry), lifecycle owned by `rql update`, never a
# plugin-private copy that other harnesses can't see. The installer skips its
# interactive `rql install` step when stdin is not a terminal; the plugin
# already provides the MCP, hook, and skill wiring.
#
# REPOQL_NO_BOOTSTRAP=1 disables downloading entirely.
# Exit 0 = rql available; exit 1 = unavailable (disabled, another session is
# mid-download, or the install failed - see bootstrap.log in the state dir).
$ErrorActionPreference = 'SilentlyContinue'

function Test-Rql {
    if (Get-Command rql) { return $true }
    return [bool]$env:LOCALAPPDATA -and (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'rql\rql.exe'))
}

function Write-Log($message) {
    "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $message" | Out-File -LiteralPath $log -Append -Encoding utf8
}

if (Test-Rql) { exit 0 }
if ($env:REPOQL_NO_BOOTSTRAP -eq '1') { exit 1 }

$stateDir = Join-Path $HOME '.local\state\repoql'
New-Item -ItemType Directory -Force -Path $stateDir | Out-Null
if (-not (Test-Path -LiteralPath $stateDir -PathType Container)) { exit 1 }
$log = Join-Path $stateDir 'bootstrap.log'
$lock = Join-Path $stateDir 'bootstrap.lock'

# One download across concurrent sessions. A stale lock (older than 15 minutes,
# e.g. a hook killed mid-download) is reclaimed; the installer's
# temp-then-rename download means a reclaimed lock never exposes a half-written
# binary.
Get-Item -LiteralPath $lock | Where-Object { $_.LastWriteTime -lt (Get-Date).AddMinutes(-15) } | Remove-Item
if (-not (New-Item -ItemType Directory -Path $lock)) { exit 1 }

try {
    Write-Log 'rql missing - installing from downloads.repoql.ai'
    # A child process keeps the installer's output and exit away from this
    # hook, and the empty stdin keeps it from blocking on a prompt.
    $install = '[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; irm https://downloads.repoql.ai/latest/install-rql.ps1 | iex'
    $null | powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command $install 2>&1 |
        Out-File -LiteralPath $log -Append -Encoding utf8
} finally {
    Remove-Item -LiteralPath $lock
}

if (Test-Rql) { exit 0 }
Write-Log 'bootstrap failed'
exit 1
