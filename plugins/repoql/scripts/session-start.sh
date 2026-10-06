#!/bin/bash
# RepoQL SessionStart hook — bootstrap the host if needed, inject repository
# orientation, and load .repoql/concepts/readme.md when the workspace provides it.
#
# SessionStart injects only the JSON hookSpecificOutput.additionalContext;
# plain stdout is NOT added to the agent's context, so the orientation is built
# into one string and emitted as that envelope. Always exits 0 so a missing
# rql, a host that is down, or an unindexed repo never blocks the session.
#
# Claude Code does not order this hook against MCP server startup, and does not
# run it at all in the session where `/plugin install` happens, so the bundled
# server never depends on it: the rql-mcp launcher finds or installs rql itself.
# The bootstrap below shares that launcher's download and is what installs rql
# in a session where the server is not spawned.
trap 'exit 0' ERR

case "$0" in */*) script_dir=${0%/*} ;; *) script_dir=. ;; esac
script_dir=$(cd "$script_dir" && pwd)
. "$script_dir/json.sh"

workspace="$PWD"
input_cwd=$(json_leaves '^cwd$' 2>/dev/null | cut -f2)
[ -d "$input_cwd" ] && workspace="$input_cwd"

# Hooks may run with a minimal PATH; rql installs to ~/.local/bin on
# macOS/Linux and %LOCALAPPDATA%\rql on Windows (hooks run under Git Bash
# there, whose inherited PATH may predate the installer's registry entry).
launch_path="$PATH"
export PATH="$HOME/.local/bin:$PATH"
case "$(uname -s)" in
    CYGWIN*|MSYS*|MINGW*)
        if [ -n "$LOCALAPPDATA" ] && command -v cygpath >/dev/null 2>&1; then
            export PATH="$(cygpath -u "$LOCALAPPDATA")/rql:$PATH"
        fi
        ;;
esac

fresh_install=""
still_installing=""
if ! command -v rql >/dev/null 2>&1; then
    REPOQL_LAUNCH_PATH="$launch_path" "$script_dir/bootstrap-rql.sh" && fresh_install=1 || { [ $? -eq 2 ] && still_installing=1; }
    # The launcher sees the new binary within a second, then starts rql and has
    # the client reload its tools. Holding the first prompt briefly lets that
    # finish so the first turn has the tools; nothing here can observe it.
    [ -n "$fresh_install" ] && sleep 3
fi

ctx=""
if ! command -v rql >/dev/null 2>&1; then
    # Keep concept-index injection independent of host availability.
    if [ -n "$still_installing" ]; then
        ctx="# RepoQL: host still installing"$'\n'
        ctx+="The repoql plugin is downloading the rql binary in the background and it has not finished yet (log: $HOME/.local/state/repoql/bootstrap.log). RepoQL tools are unavailable until it does. If the user asks for them, tell them to reconnect the repoql server from /mcp once the download completes, or to start a new session."$'\n'
    elif [ "${REPOQL_NO_BOOTSTRAP:-0}" != "1" ]; then
        ctx="# RepoQL: host not installed"$'\n'
        ctx+="The repoql plugin is installed but the rql binary is missing and automatic install failed (log: $HOME/.local/state/repoql/bootstrap.log). Tell the user to install it manually and start a new session:"$'\n'
        ctx+='  macOS/Linux:        curl -fsSL https://downloads.repoql.ai/latest/install-rql.sh | bash'$'\n'
        ctx+='  Windows PowerShell: irm https://downloads.repoql.ai/latest/install-rql.ps1 | iex'$'\n'
    fi
else
    ctx="# RepoQL: Repository Orientation"$'\n'
    if [ -n "$fresh_install" ]; then
        # Freshly downloaded host: the first index build is still warming up, so
        # skip the imports query and set expectations instead.
        ctx+=$'\n'"rql was just installed (first session with this plugin). The host indexes this repository in the background, so RepoQL tools may need a moment before returning results. If the RepoQL MCP tools are unavailable, the download outlasted the server's connection timeout: tell the user to reconnect the repoql server from /mcp, or to start a new session."$'\n'
    else
        # Use a file instead of command substitution so a host inheriting stdout
        # cannot keep the hook open. Also avoid launching a host just for orientation.
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
    ctx+=$'\n'"## Concepts"$'\n'"concept:///** holds the concepts of this repository and its imports."$'\n'
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
json_context_reply SessionStart "$ctx"
exit 0
