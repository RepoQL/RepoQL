#!/bin/bash
# Ensure the shared rql binary is available before the plugin's MCP server starts.
# REPOQL_NO_BOOTSTRAP=1 disables downloading.

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

if [ -n "$win_posix" ]; then
    command -v powershell.exe >/dev/null 2>&1 || exit 1
else
    command -v curl >/dev/null 2>&1 || exit 1
fi

state_dir="${PLUGIN_DATA:-${CLAUDE_PLUGIN_DATA:-$HOME/.local/state/repoql}}"
mkdir -p "$state_dir" 2>/dev/null || exit 1
log="$state_dir/bootstrap.log"
lock="$state_dir/bootstrap.lock"

if ! mkdir "$lock" 2>/dev/null; then
    find "$lock" -maxdepth 0 -mmin +15 -exec rmdir {} \; 2>/dev/null
    mkdir "$lock" 2>/dev/null || exit 1
fi
trap 'rmdir "$lock" 2>/dev/null' EXIT

echo "[$(date '+%Y-%m-%d %H:%M:%S')] rql missing — installing from downloads.repoql.ai" >>"$log"
if [ -n "$win_posix" ]; then
    powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \
        "irm https://downloads.repoql.ai/latest/install-rql.ps1 | iex" </dev/null >>"$log" 2>&1
else
    curl -fsSL --max-time 30 https://downloads.repoql.ai/latest/install-rql.sh | bash >>"$log" 2>&1
fi

rql_available && exit 0
echo "[$(date '+%Y-%m-%d %H:%M:%S')] bootstrap failed" >>"$log"
exit 1
