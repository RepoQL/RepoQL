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
        # Each workspace repository and import, with the concepts and vocab words it carries. Hosts that
        # predate Filesystems.kind fail the first query and fall back to the GitHub-only listing.
        listing_sql="WITH repos AS (SELECT kind, source_uri, concat_ws('/', scheme, nullif(authority, ''), nullif(trim(path_prefix, '/'), '')) AS memory FROM Filesystems WHERE kind IN ('workspace', 'import')), memory AS (SELECT uri FROM Files WHERE (uri LIKE 'concept:///%' AND extension = '.md' AND lower(name) <> 'readme.md') OR uri LIKE 'vocabulary:///%'), counts AS (SELECT r.kind, r.source_uri, count(m.uri) FILTER (WHERE starts_with(m.uri, 'concept:///' || r.memory || '/')) AS concepts, count(m.uri) FILTER (WHERE starts_with(m.uri, 'vocabulary:///' || r.memory || '/')) AS words FROM repos r LEFT JOIN memory m ON starts_with(m.uri, 'concept:///' || r.memory || '/') OR starts_with(m.uri, 'vocabulary:///' || r.memory || '/') GROUP BY r.kind, r.source_uri) SELECT kind, source_uri || coalesce(' (' || nullif(concat_ws(', ', CASE WHEN concepts > 0 THEN concepts || ' concept' || CASE WHEN concepts = 1 THEN '' ELSE 's' END END, CASE WHEN words > 0 THEN words || ' vocab word' || CASE WHEN words = 1 THEN '' ELSE 's' END END), '') || ')', '') AS line FROM counts ORDER BY kind, source_uri"
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
            ctx+="Use these URIs directly with read, explore, and query:"$'\n'"$imports"$'\n'
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
    ctx+=$'\n'"## Concepts"$'\n'"Repository invariants are addressable at concept://, including the concepts imported sources carry — browse them with read(\"concept:///**\")."$'\n'
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
