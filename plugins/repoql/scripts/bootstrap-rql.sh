#!/bin/bash
# RepoQL bootstrap — ensure the rql host binary is installed, downloading it on
# first run.
#
# Runs from the SessionStart hook so `/plugin install` alone yields a working
# system: SessionStart hooks complete before MCP servers spawn, so a download
# here makes the bundled `rql mcp` server usable in the same session (macOS /
# Linux; on Windows the PATH change reaches the next terminal, so the server
# appears from the next session instead).
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
# Exit 0 = rql available; exit 1 = unavailable (disabled, another session is
# mid-download, or the install failed — see bootstrap.log in the state dir).

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

rql_available && exit 0

[ "${REPOQL_NO_BOOTSTRAP:-0}" = "1" ] && exit 1
if [ -n "$win_posix" ]; then
    command -v powershell.exe >/dev/null 2>&1 || exit 1
else
    command -v curl >/dev/null 2>&1 || exit 1
fi

state_dir="${CLAUDE_PLUGIN_DATA:-$HOME/.local/state/repoql}"
mkdir -p "$state_dir" 2>/dev/null || exit 1
log="$state_dir/bootstrap.log"
lock="$state_dir/bootstrap.lock"

# One download across concurrent sessions. A stale lock (>15 min — e.g. a hook
# killed mid-download) is reclaimed; the installers' temp-then-rename download
# means a reclaimed lock never exposes a half-written binary.
if ! mkdir "$lock" 2>/dev/null; then
    find "$lock" -maxdepth 0 -mmin +15 -exec rmdir {} \; 2>/dev/null
    mkdir "$lock" 2>/dev/null || exit 1
fi
trap 'rmdir "$lock" 2>/dev/null' EXIT

echo "[$(date '+%Y-%m-%d %H:%M:%S')] rql missing — installing from downloads.repoql.ai" >>"$log"
if [ -n "$win_posix" ]; then
    # </dev/null keeps stdin redirected so the installer's non-interactive
    # detection holds and nothing can block on a prompt.
    powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \
        "irm https://downloads.repoql.ai/latest/install-rql.ps1 | iex" </dev/null >>"$log" 2>&1
else
    curl -fsSL --max-time 30 https://downloads.repoql.ai/latest/install-rql.sh | bash >>"$log" 2>&1
fi

rql_available && exit 0
echo "[$(date '+%Y-%m-%d %H:%M:%S')] bootstrap failed" >>"$log"
exit 1
