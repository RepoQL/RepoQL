#!/bin/bash
# RepoQL SessionStart hook — bootstrap the host if needed, inject repository
# orientation, and load .repoql/concepts/readme.md when the workspace provides it.
#
# SessionStart injects only the JSON hookSpecificOutput.additionalContext;
# plain stdout is NOT added to the agent's context, so the orientation is built
# into one string and emitted as that envelope. Always exits 0 so a missing
# rql/jq, a host that is down, or an unindexed repo never blocks the session.
#
# SessionStart hooks complete before MCP servers spawn, so when rql is missing
# the bootstrap below can still make this session's bundled MCP server work.
trap 'exit 0' ERR

hook_input=$(cat)
workspace="$PWD"
if command -v jq >/dev/null 2>&1; then
    input_cwd=$(jq -r '.cwd // empty' <<<"$hook_input" 2>/dev/null)
    [ -d "$input_cwd" ] && workspace="$input_cwd"
fi

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

ctx=""
if ! command -v rql >/dev/null 2>&1; then
    # Keep concept-index injection independent of host availability.
    if [ "${REPOQL_NO_BOOTSTRAP:-0}" != "1" ]; then
        ctx="# RepoQL: host not installed"$'\n'
        ctx+="The repoql plugin is installed but the rql binary is missing and automatic install failed (log: ${CLAUDE_PLUGIN_DATA:-$HOME/.local/state/repoql}/bootstrap.log). Tell the user to install it manually and start a new session:"$'\n'
        ctx+='  macOS/Linux:        curl -fsSL https://downloads.repoql.ai/latest/install-rql.sh | bash'$'\n'
        ctx+='  Windows PowerShell: irm https://downloads.repoql.ai/latest/install-rql.ps1 | iex'$'\n'
    fi
else
    ctx="# RepoQL: Repository Orientation"$'\n'
    if [ -n "$fresh_install" ]; then
        # Freshly downloaded host: the first index build is still warming up, so
        # skip the imports query and set expectations instead.
        ctx+=$'\n'"rql was just installed (first session with this plugin). The host indexes this repository in the background, so RepoQL tools may need a moment before returning results. If mcp__repoql__* tools are unavailable, tell the user a new Claude Code session started from a fresh terminal (so it picks up the updated PATH) will have them."$'\n'
    else
        # Use a file instead of command substitution so a host inheriting stdout
        # cannot keep the hook open. Also avoid launching a host just for orientation.
        query_out=$(mktemp "${TMPDIR:-/tmp}/repoql-imports.XXXXXX" 2>/dev/null) || query_out="${TMPDIR:-/tmp}/repoql-imports.$$"
        guard=""
        command -v timeout >/dev/null 2>&1 && guard="timeout 30"
        query_ok=""
        # Each workspace repository and import, with its captured memory when it has any. Hosts that
        # predate Filesystems.kind fail the first query and fall back to the GitHub-only listing.
        listing_sql="WITH repos AS (SELECT kind, source_uri, concat_ws('/', scheme, nullif(authority, ''), nullif(trim(path_prefix, '/'), '')) AS memory FROM Filesystems WHERE kind IN ('workspace', 'import')) SELECT r.kind, r.source_uri || coalesce(' (memory: ' || nullif(concat_ws(', ', CASE WHEN EXISTS (SELECT 1 FROM Files f WHERE starts_with(f.uri, 'concept:///' || r.memory || '/')) THEN 'concept:///' || r.memory || '/**' END, CASE WHEN EXISTS (SELECT 1 FROM Files f WHERE starts_with(f.uri, 'vocabulary:///' || r.memory || '/')) THEN 'vocabulary:///' || r.memory || '/**' END), '') || ')', '') AS line FROM repos r ORDER BY r.kind, r.source_uri"
        legacy_sql="SELECT 'import' AS kind, source_uri AS line FROM Filesystems WHERE source_uri LIKE 'github://%' ORDER BY source_uri"
        if $guard rql query "$listing_sql" --timeout-ms 5000 --no-launch </dev/null >"$query_out" 2>/dev/null \
            || $guard rql query "$legacy_sql" --timeout-ms 5000 --no-launch </dev/null >"$query_out" 2>/dev/null; then
            query_ok=1
        fi
        workspace_repos=$(awk -F'\t' '$1 == "workspace" && $2 ~ /:\/\// { print $2 }' "$query_out" 2>/dev/null || true)
        imports=$(awk -F'\t' '$1 == "import" && $2 ~ /:\/\// { print $2 }' "$query_out" 2>/dev/null || true)
        rm -f "$query_out"
        if [ -n "$workspace_repos" ]; then
            ctx+=$'\n'"## Workspace Repositories"$'\n'
            ctx+="This workspace is a directory of repositories. Each answers to its own URI below; file:/// is only the loose files at the top level."$'\n'"$workspace_repos"$'\n'
        fi
        ctx+=$'\n'"## Imported Repositories"$'\n'
        if [ -n "$imports" ]; then
            ctx+="Use these URIs directly with read / explore / query:"$'\n'"$imports"$'\n'
        elif [ -n "$query_ok" ]; then
            ctx+="(none)"$'\n'
        else
            ctx+="(not checked — the RepoQL host was not running)"$'\n'
        fi
        ctx+="Use the import tool whenever you like to add more."$'\n'
    fi
    uplink_context=""
    if uplink_context=$(rql uplinks </dev/null 2>/dev/null); then
        ctx+=$'\n'"## Accessible Uplinks"$'\n'"$uplink_context"$'\n'
    else
        ctx+=$'\n'"## Accessible Uplinks"$'\n'"(not checked — run rql uplinks to discover account access)"$'\n'
    fi
    ctx+=$'\n'"## Concepts"$'\n'"Repository invariants, if any, are addressable at concept:// — browse them with read(\"concept:///**\")."$'\n'
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

[ -n "$ctx" ] || exit 0
jq -n --arg ctx "$ctx" '{hookSpecificOutput: {hookEventName: "SessionStart", additionalContext: $ctx}}'
exit 0
