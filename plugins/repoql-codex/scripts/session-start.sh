#!/bin/bash
# Bootstrap RepoQL if needed, inject compact repository orientation, and load
# .repoql/concepts/readme.md when the workspace provides one.
# Fail open so an unavailable host never blocks a Codex session.
trap 'exit 0' ERR

hook_input=$(cat)
workspace="$PWD"
if command -v jq >/dev/null 2>&1; then
    input_cwd=$(jq -r '.cwd // empty' <<<"$hook_input" 2>/dev/null)
    [ -d "$input_cwd" ] && workspace="$input_cwd"
fi

emit_context() {
    [ -n "$1" ] || exit 0
    if command -v jq >/dev/null 2>&1; then
        jq -n --arg ctx "$1" '{hookSpecificOutput: {hookEventName: "SessionStart", additionalContext: $ctx}}'
    else
        printf '%s\n' "$1"
    fi
}

export PATH="$HOME/.local/bin:$PATH"
case "$(uname -s)" in
    CYGWIN*|MSYS*|MINGW*)
        if [ -n "$LOCALAPPDATA" ] && command -v cygpath >/dev/null 2>&1; then
            export PATH="$(cygpath -u "$LOCALAPPDATA")/rql:$PATH"
        fi
        ;;
esac

script_dir=$(cd "$(dirname "$0")" && pwd)
state_dir="${PLUGIN_DATA:-${CLAUDE_PLUGIN_DATA:-$HOME/.local/state/repoql}}"

fresh_install=""
if ! command -v rql >/dev/null 2>&1; then
    if "$script_dir/bootstrap-rql.sh"; then
        fresh_install=1
    fi
fi

ctx=""
if ! command -v rql >/dev/null 2>&1; then
    if [ "${REPOQL_NO_BOOTSTRAP:-0}" != "1" ]; then
        ctx="# RepoQL: host not installed"$'\n'
        ctx+="The RepoQL plugin is installed, but automatic rql installation failed (log: $state_dir/bootstrap.log). Tell the user to install it manually and start a new Codex task:"$'\n'
        ctx+='  macOS/Linux:        curl -fsSL https://downloads.repoql.ai/latest/install-rql.sh | bash'$'\n'
        ctx+='  Windows PowerShell: irm https://downloads.repoql.ai/latest/install-rql.ps1 | iex'$'\n'
    fi
else
    ctx="# RepoQL: Repository Orientation"$'\n'
    if [ -n "$fresh_install" ]; then
        ctx+=$'\n'"rql was just installed. RepoQL is indexing this repository in the background, so its tools may need a moment before returning results. If the mcp__repoql__* tools are unavailable, start a new Codex task so the MCP server picks up the new PATH."$'\n'
    else
        query_out=$(mktemp "${TMPDIR:-/tmp}/repoql-imports.XXXXXX" 2>/dev/null) || query_out="${TMPDIR:-/tmp}/repoql-imports.$$"
        guard=""
        command -v timeout >/dev/null 2>&1 && guard="timeout 30"
        query_ok=""
        if $guard rql query "SELECT source_uri FROM Filesystems WHERE source_uri LIKE 'github://%' ORDER BY source_uri" \
            --timeout-ms 5000 --no-launch </dev/null >"$query_out" 2>/dev/null; then
            query_ok=1
        fi
        imports=$(grep '://' "$query_out" 2>/dev/null || true)
        rm -f "$query_out"
        ctx+=$'\n'"## Imported Repositories"$'\n'
        if [ -n "$imports" ]; then
            ctx+="Use these github:// URIs directly with read, explore, and query:"$'\n'"$imports"$'\n'
        elif [ -n "$query_ok" ]; then
            ctx+="(none — import one with: rql import github://owner/repo)"$'\n'
        else
            ctx+="(not checked — the RepoQL host was not running)"$'\n'
        fi
    fi
    ctx+=$'\n'"## Concepts"$'\n'"Repository invariants are addressable at concept:// — browse them with read(\"concept:///**\")."$'\n'
fi

concepts_readme=""
concepts_relative=""
for candidate in ".repoql/concepts/readme.md" ".repoql/concepts/README.md"; do
    if [ -f "$workspace/$candidate" ]; then
        concepts_readme=$(cat "$workspace/$candidate" 2>/dev/null)
        concepts_relative="$candidate"
        break
    fi
done

if [ -n "$concepts_relative" ]; then
    [ -n "$ctx" ] && ctx+=$'\n'
    ctx+="## Repository Concepts Index ($concepts_relative)"$'\n\n'"$concepts_readme"$'\n'
fi

emit_context "$ctx"
exit 0
