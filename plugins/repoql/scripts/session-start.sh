#!/bin/bash
# RepoQL SessionStart hook — bootstrap the host if needed, then inject
# repository orientation as context.
#
# SessionStart injects only the JSON hookSpecificOutput.additionalContext;
# plain stdout is NOT added to the agent's context, so the orientation is built
# into one string and emitted as that envelope. Always exits 0 so a missing
# rql/jq, a host that is down, or an unindexed repo never blocks the session.
#
# SessionStart hooks complete before MCP servers spawn, so when rql is missing
# the bootstrap below can still make this session's bundled MCP server work.
trap 'exit 0' ERR

# Hooks may run with a minimal PATH; rql installs to ~/.local/bin on
# macOS/Linux and %LOCALAPPDATA%\rql on Windows (hooks run under Git Bash
# there, whose inherited PATH may predate the installer's registry entry).
export PATH="$HOME/.local/bin:$PATH"
case "$(uname -s)" in
    CYGWIN*|MSYS*|MINGW*)
        if [ -n "$LOCALAPPDATA" ] && command -v cygpath >/dev/null 2>&1; then
            export PATH="$(cygpath -u "$LOCALAPPDATA")/rql:$PATH"
        fi
        ;;
esac

script_dir=$(cd "$(dirname "$0")" && pwd)

fresh_install=""
if ! command -v rql >/dev/null 2>&1; then
    if "$script_dir/bootstrap-rql.sh"; then
        fresh_install=1
    fi
fi

command -v jq >/dev/null 2>&1 || exit 0

if ! command -v rql >/dev/null 2>&1; then
    # No host and bootstrap didn't produce one. Say so once, with the fix —
    # otherwise the plugin's tools just silently don't exist.
    [ "${REPOQL_NO_BOOTSTRAP:-0}" = "1" ] && exit 0
    ctx="# RepoQL: host not installed"$'\n'
    ctx+="The repoql plugin is installed but the rql binary is missing and automatic install failed (log: ${CLAUDE_PLUGIN_DATA:-$HOME/.local/state/repoql}/bootstrap.log). Tell the user to install it manually and start a new session:"$'\n'
    ctx+='  macOS/Linux:        curl -fsSL https://downloads.repoql.ai/latest/install-rql.sh | bash'$'\n'
    ctx+='  Windows PowerShell: irm https://downloads.repoql.ai/latest/install-rql.ps1 | iex'$'\n'
    jq -n --arg ctx "$ctx" '{hookSpecificOutput: {hookEventName: "SessionStart", additionalContext: $ctx}}'
    exit 0
fi

ctx="# RepoQL: Repository Orientation"$'\n'

if [ -n "$fresh_install" ]; then
    # Freshly downloaded host: the first index build is still warming up, so
    # skip the imports query and set expectations instead.
    ctx+=$'\n'"rql was just installed (first session with this plugin). The host indexes this repository in the background, so RepoQL tools may need a moment before returning results. If mcp__repoql__* tools are unavailable, tell the user a new Claude Code session started from a fresh terminal (so it picks up the updated PATH) will have them."$'\n'
else
    # Orientation carries only what the agent cannot cheaply pull itself: which repos
    # are mounted. Repository structure and docs are large and re-derivable on demand
    # (read / explore), so they are never dumped here. Readiness is omitted on purpose:
    # a transient "not ready" at startup would wrongly teach the agent that RepoQL is
    # unusable for the whole session, when it just needed a moment to warm up.
    imports=$(rql query "SELECT source_uri FROM Filesystems WHERE source_uri LIKE 'github://%' ORDER BY source_uri" 2>/dev/null | grep '://' || true)
    ctx+=$'\n'"## Imported Repositories"$'\n'
    if [ -n "$imports" ]; then
        ctx+="Use these github:// URIs directly with read / explore / query:"$'\n'"$imports"$'\n'
    else
        ctx+="(none — import one with: rql import github://owner/repo)"$'\n'
    fi
fi

ctx+=$'\n'"## Concepts"$'\n'"Repository invariants, if any, are addressable at concept:// — browse them with read(\"concept:///**\")."$'\n'

jq -n --arg ctx "$ctx" '{hookSpecificOutput: {hookEventName: "SessionStart", additionalContext: $ctx}}'
exit 0
