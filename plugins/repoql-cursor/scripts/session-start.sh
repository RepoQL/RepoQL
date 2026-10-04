#!/bin/bash
# RepoQL sessionStart hook for Cursor — bootstrap the host if needed, export a
# PATH that finds rql to later hooks, and inject repository orientation.
# session-start.ps1 is the same hook for Windows; run-hook.cmd picks between them.
#
# Cursor reads snake_case JSON from stdout: additional_context joins the
# conversation's initial context and env reaches every later hook in the
# session. Cursor treats empty stdout as an invalid response and stderr as a
# failure, so every path prints one JSON object and nothing else. Delivery of
# additional_context races the first message (a known Cursor bug); the static
# rule in rules/ carries what must always arrive, so this context is a bonus.
trap 'printf "{}\n"; exit 0' ERR
exec 2>/dev/null

# Hooks may run with a minimal PATH; rql installs to ~/.local/bin.
export PATH="$HOME/.local/bin:$PATH"

case "$0" in */*) script_dir=${0%/*} ;; *) script_dir=. ;; esac
script_dir=$(cd "$script_dir" && pwd)
. "$script_dir/json.sh"

workspace="${CURSOR_PROJECT_DIR:-}"
[ -d "$workspace" ] || workspace="$PWD"
cd "$workspace" || { printf '{}\n'; exit 0; }

fresh_install=""
if ! command -v rql >/dev/null 2>&1; then
    if "$script_dir/bootstrap-rql.sh" >/dev/null; then
        fresh_install=1
    fi
fi

ctx=""
if ! command -v rql >/dev/null 2>&1; then
    if [ "${REPOQL_NO_BOOTSTRAP:-0}" != "1" ]; then
        ctx="# RepoQL: host not installed"$'\n'
        ctx+="The RepoQL plugin is installed but the rql binary is missing and automatic install failed (log: $HOME/.local/state/repoql/bootstrap.log). Tell the user to install it manually, then reload the Cursor window:"$'\n'
        ctx+='  curl -fsSL https://downloads.repoql.ai/latest/install-rql.sh | bash'$'\n'
    fi
else
    ctx="# RepoQL: Repository Orientation"$'\n'
    if [ -n "$fresh_install" ]; then
        # Cursor starts MCP servers with the editor, before this hook installed rql.
        ctx+=$'\n'"rql was just installed (first session with this plugin). Cursor started its MCP servers before the install, so RepoQL's tools (explore, read, query, …) appear after the user runs \"Developer: Reload Window\". The host indexes this repository in the background, so the tools may need a moment before returning results."$'\n'
    else
        # Use a file instead of command substitution so a host inheriting stdout
        # cannot keep the hook open. Also avoid launching a host just for orientation.
        query_out=$(mktemp "${TMPDIR:-/tmp}/repoql-imports.XXXXXX") || query_out="${TMPDIR:-/tmp}/repoql-imports.$$"
        guard=""
        command -v timeout >/dev/null 2>&1 && guard="timeout 30"
        query_ok=""
        # Each workspace repository and import, with the concepts and vocab words it carries. Hosts that
        # predate Filesystems.kind fail the first query and fall back to the GitHub-only listing.
        listing_sql="WITH repos AS (SELECT kind, source_uri, concat_ws('/', scheme, nullif(authority, ''), nullif(trim(path_prefix, '/'), '')) AS memory FROM Filesystems WHERE kind IN ('workspace', 'import')), memory AS (SELECT uri FROM Files WHERE (uri LIKE 'concept:///%' AND extension = '.md' AND lower(name) <> 'readme.md') OR uri LIKE 'vocabulary:///%'), counts AS (SELECT r.kind, r.source_uri, count(m.uri) FILTER (WHERE starts_with(m.uri, 'concept:///' || r.memory || '/')) AS concepts, count(m.uri) FILTER (WHERE starts_with(m.uri, 'vocabulary:///' || r.memory || '/')) AS words FROM repos r LEFT JOIN memory m ON starts_with(m.uri, 'concept:///' || r.memory || '/') OR starts_with(m.uri, 'vocabulary:///' || r.memory || '/') GROUP BY r.kind, r.source_uri) SELECT kind, source_uri || coalesce(' (' || nullif(concat_ws(', ', CASE WHEN concepts > 0 THEN concepts || ' concept' || CASE WHEN concepts = 1 THEN '' ELSE 's' END END, CASE WHEN words > 0 THEN words || ' vocab word' || CASE WHEN words = 1 THEN '' ELSE 's' END END), '') || ')', '') AS line FROM counts ORDER BY kind, source_uri"
        legacy_sql="SELECT 'import' AS kind, source_uri AS line FROM Filesystems WHERE source_uri LIKE 'github://%' ORDER BY source_uri"
        if $guard rql query "$listing_sql" --timeout-ms 5000 --no-launch </dev/null >"$query_out" \
            || $guard rql query "$legacy_sql" --timeout-ms 5000 --no-launch </dev/null >"$query_out"; then
            query_ok=1
        fi
        workspace_repos=$(awk -F'\t' '$1 == "workspace" && $2 ~ /:\/\// { print $2 }' "$query_out" || true)
        imports=$(awk -F'\t' '$1 == "import" && $2 ~ /:\/\// { print $2 }' "$query_out" || true)
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
    if uplink_context=$(rql uplinks </dev/null); then
        ctx+=$'\n'"## Accessible Uplinks"$'\n'"$uplink_context"$'\n'
    else
        ctx+=$'\n'"## Accessible Uplinks"$'\n'"(not checked — run rql uplinks to discover account access)"$'\n'
    fi
    ctx+=$'\n'"## Concepts"$'\n'"concept:///** holds the concepts of this repository and its imports."$'\n'
fi

# The host maintains .cursor/rules/repoql-concepts.g.mdc, an always-applied rule
# carrying the same index; inject the readme only when that rule is absent.
if [ ! -f "$workspace/.cursor/rules/repoql-concepts.g.mdc" ]; then
    for candidate in ".repoql/concepts/readme.md" ".repoql/concepts/README.md"; do
        if [ -f "$workspace/$candidate" ]; then
            [ -n "$ctx" ] && ctx+=$'\n'
            ctx+="## Repository Concepts Index ($candidate)"$'\n\n'"$(cat "$workspace/$candidate")"$'\n'
            break
        fi
    done
fi

output="{\"env\":{\"PATH\":$(json_string "$PATH")}" || { printf '{}\n'; exit 0; }
if [ -n "$ctx" ]; then
    output+=",\"additional_context\":$(json_string "$ctx")" || { printf '{}\n'; exit 0; }
fi
printf '%s}\n' "$output"
exit 0
