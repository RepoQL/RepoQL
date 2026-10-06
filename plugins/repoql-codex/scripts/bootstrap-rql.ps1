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
# REPOQL_BOOTSTRAP_WAIT is how many seconds to wait for another session's
# install before giving up on it (default 60).
# Exit 0 = rql available; exit 1 = unavailable, with the reason on stdout
# (nothing when disabled); exit 2 = another session is still installing.
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

$stateDir = $env:PLUGIN_DATA
if (-not $stateDir) { $stateDir = $env:CLAUDE_PLUGIN_DATA }
if (-not $stateDir) { $stateDir = Join-Path $HOME '.local\state\repoql' }
New-Item -ItemType Directory -Force -Path $stateDir | Out-Null
# The caller repeats the reason to the user, so it names the actual cause: the
# log does not exist until an install is attempted.
if (-not (Test-Path -LiteralPath $stateDir -PathType Container)) {
    "automatic install could not run: cannot create the state directory $stateDir"
    exit 1
}
$log = Join-Path $stateDir 'bootstrap.log'
$lock = Join-Path $stateDir 'bootstrap.lock'

# One download across concurrent sessions. A stale lock (older than 15 minutes,
# e.g. a hook killed mid-download) is reclaimed; the installer's
# temp-then-rename download means a reclaimed lock never exposes a half-written
# binary.
#
# While another session holds the lock, wait for its install rather than report
# a failure that has not happened.
$waitLimit = 60
if ($env:REPOQL_BOOTSTRAP_WAIT -match '^\d+$') { $waitLimit = [int]$env:REPOQL_BOOTSTRAP_WAIT }
$waited = 0
while (-not (New-Item -ItemType Directory -Path $lock)) {
    if (-not (Test-Path -LiteralPath $lock)) {
        # Released between the two checks, or the state dir is not writable.
        if (New-Item -ItemType Directory -Path $lock) { break }
        "automatic install could not run: cannot write to the state directory $stateDir"
        exit 1
    }
    Get-Item -LiteralPath $lock | Where-Object { $_.LastWriteTime -lt (Get-Date).AddMinutes(-15) } | Remove-Item
    if (-not (Test-Path -LiteralPath $lock)) { continue }
    if (Test-Rql) { exit 0 }
    if ($waited -ge $waitLimit) { exit 2 }
    Start-Sleep -Seconds 1
    $waited++
}

try {
    # The session that held the lock may have finished the install.
    if (Test-Rql) { exit 0 }
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
"automatic install failed (log: $log)"
exit 1
