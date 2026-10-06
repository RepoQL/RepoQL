#!/bin/bash
# RepoQL bootstrap — ensure the rql host binary is installed, downloading it on
# first run.
#
# Two callers, neither ordered against the other by Claude Code: the MCP
# launcher (rql-mcp), which is what makes the bundled server work, and the
# SessionStart hook, which covers sessions where the server is not spawned.
# Both call this script and wait; the download itself runs once, in a detached
# worker, so a caller that is killed (the MCP connection timeout, the hook
# timeout, a closed session) never aborts it.
#
# Delegates to the hosted installers so the result is byte-identical to a
# manual install — one canonical binary per machine, lifecycle owned by
# `rql update`, never a plugin-private copy that other harnesses can't see:
#   macOS/Linux   install-rql.sh   -> ~/.local/bin (+ shell rc PATH)
#   Windows       install-rql.ps1  -> %LOCALAPPDATA%\rql (+ user PATH registry)
# Both installers skip their interactive `rql install` step when stdin is not
# a TTY; the plugin already provides the MCP, hook, and skill wiring.
#
# REPOQL_NO_BOOTSTRAP=1 disables downloading entirely.
# REPOQL_BOOTSTRAP_WAIT bounds how long this call waits for the worker
# (seconds, default 200 — inside the SessionStart hook's 240s timeout).
# Exit 0 = rql available; exit 1 = unavailable (disabled, or the install
# failed — see bootstrap.log in the state dir); exit 2 = still downloading
# when the wait ran out.

# The installer adds ~/.local/bin to the shell rc only when it is missing from
# PATH, so it must see the PATH this session was launched with — which callers
# that have already put ~/.local/bin first pass in REPOQL_LAUNCH_PATH.
launch_path="${REPOQL_LAUNCH_PATH:-$PATH}"
export REPOQL_LAUNCH_PATH="$launch_path"
export PATH="$HOME/.local/bin:$PATH"

# Hooks are bash even on Windows (Git Bash). The Windows install location is
# %LOCALAPPDATA%\rql, and the hook's PATH was inherited from a process that may
# predate the registry PATH entry — so put the canonical dir on PATH directly.
win_posix=""
win_rql_dir=""
case "$(uname -s)" in
    CYGWIN*|MSYS*|MINGW*)
        win_posix=1
        if [ -n "$LOCALAPPDATA" ] && command -v cygpath >/dev/null 2>&1; then
            win_rql_dir="$(cygpath -u "$LOCALAPPDATA")/rql"
            export PATH="$win_rql_dir:$PATH"
        fi
        ;;
esac

rql_available() {
    command -v rql >/dev/null 2>&1 && return 0
    [ -n "$win_rql_dir" ] && [ -x "$win_rql_dir/rql.exe" ]
}

# One binary per machine, so one lock per machine: every caller, from any
# harness, coordinates here rather than in a per-plugin data dir.
state_dir="$HOME/.local/state/repoql"
log="$state_dir/bootstrap.log"
lock="$state_dir/bootstrap.lock"
owner="$state_dir/bootstrap.pid"

say() { echo "[$(date '+%Y-%m-%d %H:%M:%S')] $*" >>"$log"; }

# A lock is live while its recorded worker runs. A lock with no recorded
# worker belongs to bootstrap-rql.ps1, which records none; trust it until it
# is older than any install is allowed to run.
lock_live() {
    [ -d "$lock" ] || return 1
    local pid
    pid=$(cat "$owner" 2>/dev/null)
    if [ -n "$pid" ]; then
        kill -0 "$pid" 2>/dev/null
    else
        [ -z "$(find "$lock" -maxdepth 0 -mmin +20 2>/dev/null)" ]
    fi
}

# The hosted installer's own downloads are unbounded; a stalled one would hold
# the lock forever, so the whole install gets a deadline.
install_deadline=900

install() {
    if [ -n "$win_posix" ]; then
        # </dev/null keeps stdin redirected so the installer's non-interactive
        # detection holds and nothing can block on a prompt.
        powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \
            "irm https://downloads.repoql.ai/latest/install-rql.ps1 | iex" </dev/null >>"$log" 2>&1
        return
    fi
    local script="$state_dir/install-rql.$$.sh"
    if ! curl -fsSL --max-time 30 https://downloads.repoql.ai/latest/install-rql.sh -o "$script" 2>>"$log"; then
        rm -f "$script"
        return 1
    fi
    PATH="$launch_path" bash "$script" </dev/null >>"$log" 2>&1 &
    local job=$!
    (
        sleep "$install_deadline"
        say "install exceeded ${install_deadline}s — stopping it"
        pkill -TERM -P "$job" 2>/dev/null
        kill -TERM "$job" 2>/dev/null
    ) </dev/null >/dev/null 2>&1 &
    local watchdog=$!
    wait "$job"
    local status=$?
    pkill -P "$watchdog" 2>/dev/null
    kill "$watchdog" 2>/dev/null
    rm -f "$script"
    return $status
}

if [ "$1" = "--worker" ]; then
    # Detached from whoever started it: only an explicit TERM stops it early.
    trap '' HUP INT
    if ! mkdir "$lock" 2>/dev/null; then
        lock_live && exit 0
        rm -f "$owner"
        rmdir "$lock" 2>/dev/null
        mkdir "$lock" 2>/dev/null || exit 0
    fi
    echo $$ >"$owner"
    trap 'rm -f "$owner"; rmdir "$lock" 2>/dev/null' EXIT
    trap 'exit 1' TERM
    rql_available && exit 0
    say "rql missing — installing from downloads.repoql.ai"
    install
    rql_available && exit 0
    say "bootstrap failed"
    exit 1
fi

rql_available && exit 0

[ "${REPOQL_NO_BOOTSTRAP:-0}" = "1" ] && exit 1
if [ -n "$win_posix" ]; then
    command -v powershell.exe >/dev/null 2>&1 || exit 1
else
    command -v curl >/dev/null 2>&1 || exit 1
fi
mkdir -p "$state_dir" 2>/dev/null || exit 1

# The worker inherits nothing of the caller's stdio: a hook's stdout pipe or an
# MCP server's JSON-RPC stream must not be held open or written to by it.
detach=""
command -v setsid >/dev/null 2>&1 && detach="setsid"
nohup $detach bash "$0" --worker </dev/null >/dev/null 2>&1 &
worker=$!

waited=0
budget="${REPOQL_BOOTSTRAP_WAIT:-200}"
while :; do
    rql_available && exit 0
    if ! kill -0 "$worker" 2>/dev/null && ! lock_live; then
        rql_available && exit 0
        exit 1
    fi
    [ "$waited" -ge "$budget" ] && exit 2
    sleep 1
    waited=$((waited + 1))
done
