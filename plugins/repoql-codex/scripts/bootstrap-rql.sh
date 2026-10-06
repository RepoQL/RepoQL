#!/bin/bash
# Ensure the shared rql binary is available before the plugin's MCP server starts.
# REPOQL_NO_BOOTSTRAP=1 disables downloading.
# REPOQL_BOOTSTRAP_WAIT is how many seconds to wait for another session's
# install before giving up on it (default 60).
# Exit 0 = rql available; exit 1 = unavailable, with the reason on stdout
# (nothing when disabled); exit 2 = another session is still installing.

export PATH="$HOME/.local/bin:$PATH"

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

rql_available && exit 0

[ "${REPOQL_NO_BOOTSTRAP:-0}" = "1" ] && exit 1

# The caller repeats the reason to the user, so it names the actual cause: the
# log does not exist until an install is attempted.
unavailable() {
    echo "automatic install $1"
    exit 1
}

if [ -n "$win_posix" ]; then
    command -v powershell.exe >/dev/null 2>&1 || unavailable "could not run: powershell.exe not found on PATH"
else
    command -v curl >/dev/null 2>&1 || unavailable "could not run: curl not found on PATH"
fi

state_dir="${PLUGIN_DATA:-${CLAUDE_PLUGIN_DATA:-$HOME/.local/state/repoql}}"
mkdir -p "$state_dir" 2>/dev/null || unavailable "could not run: cannot create the state directory $state_dir"
log="$state_dir/bootstrap.log"
lock="$state_dir/bootstrap.lock"

# While another session holds the lock, wait for its install rather than
# report a failure that has not happened.
wait_limit="${REPOQL_BOOTSTRAP_WAIT:-60}"
case "$wait_limit" in ''|*[!0-9]*) wait_limit=60 ;; esac
waited=0
until mkdir "$lock" 2>/dev/null; do
    if [ ! -d "$lock" ]; then
        # Released between the two checks, or the state dir is not writable.
        mkdir "$lock" 2>/dev/null && break
        unavailable "could not run: cannot write to the state directory $state_dir"
    fi
    find "$lock" -maxdepth 0 -mmin +15 -exec rmdir {} \; 2>/dev/null
    [ -d "$lock" ] || continue
    rql_available && exit 0
    [ "$waited" -ge "$wait_limit" ] && exit 2
    sleep 1
    waited=$((waited + 1))
done
trap 'rmdir "$lock" 2>/dev/null' EXIT
# The session that held the lock may have finished the install.
rql_available && exit 0

echo "[$(date '+%Y-%m-%d %H:%M:%S')] rql missing — installing from downloads.repoql.ai" >>"$log"
if [ -n "$win_posix" ]; then
    powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \
        "irm https://downloads.repoql.ai/latest/install-rql.ps1 | iex" </dev/null >>"$log" 2>&1
else
    curl -fsSL --max-time 30 https://downloads.repoql.ai/latest/install-rql.sh | bash >>"$log" 2>&1
fi

rql_available && exit 0
echo "[$(date '+%Y-%m-%d %H:%M:%S')] bootstrap failed" >>"$log"
unavailable "failed (log: $log)"
