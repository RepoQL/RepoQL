#!/bin/bash
# RepoQL SessionStart hook — bootstrap the host if needed, inject repository
# orientation, and load .repoql/concepts/readme.md when CLAUDE.md does not import it.
#
# SessionStart injects only the JSON hookSpecificOutput.additionalContext;
# plain stdout is NOT added to the agent's context, so the orientation is built
# into one string and emitted as that envelope. An install is otherwise silent,
# so its outcome also goes to the user, as the reply's systemMessage. Always exits 0 so a missing
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
bootstrap_status=0
bootstrap_reason=""
if ! command -v rql >/dev/null 2>&1; then
    # A failed bootstrap prints why; exit 2 means the install is still running.
    if bootstrap_reason=$(REPOQL_LAUNCH_PATH="$launch_path" "$script_dir/bootstrap-rql.sh"); then
        fresh_install=1
    else
        bootstrap_status=$?
    fi
    # The launcher sees the new binary within a second, then starts rql and has
    # the client reload its tools. Holding the first prompt briefly lets that
    # finish so the first turn has the tools; nothing here can observe it.
    [ -n "$fresh_install" ] && sleep 3
fi

ctx=""
notice=""
if ! command -v rql >/dev/null 2>&1; then
    # Keep concept-index injection independent of host availability.
    if [ "${REPOQL_NO_BOOTSTRAP:-0}" != "1" ]; then
        if [ "$bootstrap_status" = "2" ]; then
            # Started by this session's MCP launcher, this hook, or another session.
            ctx="# RepoQL: host install in progress"$'\n'
            ctx+="The RepoQL plugin is installed and the rql binary is still downloading in the background, so RepoQL tools are not available yet. They appear in this session when the download finishes if the repoql MCP server is connected; otherwise tell the user to reconnect it from /mcp, or to start a new session in a minute. Nothing needs installing by hand."$'\n'
            notice="RepoQL is still downloading rql (about 180 MB). Its tools appear when the download finishes; if they do not, reconnect repoql from /mcp."
        else
            ctx="# RepoQL: host not installed"$'\n'
            ctx+="The repoql plugin is installed but the rql binary is missing and ${bootstrap_reason:-automatic install failed}. Tell the user to install it manually and start a new session:"$'\n'
            ctx+='  macOS/Linux:        curl -fsSL https://downloads.repoql.ai/latest/install-rql.sh | bash'$'\n'
            ctx+='  Windows PowerShell: irm https://downloads.repoql.ai/latest/install-rql.ps1 | iex'$'\n'
            notice="RepoQL could not install rql: ${bootstrap_reason:-automatic install failed}. Install it manually, then start a new session: curl -fsSL https://downloads.repoql.ai/latest/install-rql.sh | bash"
        fi
    fi
else
    ctx="# RepoQL: Repository Orientation"$'\n'
    if [ -n "$fresh_install" ]; then
        installed_in=$(command -v rql)
        installed_in=${installed_in%/*}
        case "$installed_in" in "$HOME"/*) installed_in="~${installed_in#"$HOME"}" ;; esac
        notice="RepoQL installed rql to $installed_in."
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
        # Every mounted source except the ones named in the WHERE clause, which the agent already knows or did not
        # ask for: the primary file:///, help, memory, and worktrees. A new kind of mount is listed by default, with
        # its kind in brackets. Each line also counts the concepts and vocab words the source carries. Hosts that
        # predate Filesystems.kind fail the first query and fall back to the GitHub-only listing.
        listing_sql="WITH repos AS (SELECT CASE WHEN kind = 'workspace' THEN 'workspace' ELSE 'import' END AS section, CASE WHEN kind NOT IN ('workspace', 'import') THEN kind END AS label, source_uri, concat_ws('/', scheme, nullif(authority, ''), nullif(trim(path_prefix, '/'), '')) AS memory FROM Filesystems WHERE scheme NOT IN ('file', 'help', 'concept', 'vocabulary', 'worktree') AND coalesce(kind, '') NOT IN ('primary', 'worktree')), memory AS (SELECT uri FROM Files WHERE (uri LIKE 'concept:///%' AND extension = '.md' AND lower(name) <> 'readme.md') OR uri LIKE 'vocabulary:///%'), counts AS (SELECT r.section, r.label, r.source_uri, count(m.uri) FILTER (WHERE starts_with(m.uri, 'concept:///' || r.memory || '/')) AS concepts, count(m.uri) FILTER (WHERE starts_with(m.uri, 'vocabulary:///' || r.memory || '/')) AS words FROM repos r LEFT JOIN memory m ON starts_with(m.uri, 'concept:///' || r.memory || '/') OR starts_with(m.uri, 'vocabulary:///' || r.memory || '/') GROUP BY r.section, r.label, r.source_uri) SELECT section AS kind, source_uri || coalesce(' (' || nullif(concat_ws(', ', label, CASE WHEN concepts > 0 THEN concepts || ' concept' || CASE WHEN concepts = 1 THEN '' ELSE 's' END END, CASE WHEN words > 0 THEN words || ' vocab word' || CASE WHEN words = 1 THEN '' ELSE 's' END END), '') || ')', '') AS line FROM counts ORDER BY section, source_uri"
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

# The host adds the line @.repoql/concepts/README.md to CLAUDE.md, and Claude Code
# loads the index through that import; inject the readme only when the import is
# absent. The line counts when it stands alone, as the host's own check reads it.
concepts_readme=""
concepts_relative=""
if ! grep -Eq '^[[:space:]]*@\.repoql/concepts/(README|readme)\.md[[:space:]]*$' "$workspace/CLAUDE.md" 2>/dev/null; then
    for candidate in ".repoql/concepts/readme.md" ".repoql/concepts/README.md"; do
        if [ -f "$workspace/$candidate" ]; then
            concepts_readme=$(cat "$workspace/$candidate" 2>/dev/null)
            concepts_relative="$candidate"
            break
        fi
    done
fi

if [ -n "$concepts_relative" ]; then
    [ -n "$ctx" ] && ctx+=$'\n'
    ctx+="## Repository Concepts Index ($concepts_relative)"$'\n\n'"$concepts_readme"$'\n'
fi

[ -n "$ctx" ] || exit 0
if [ -n "$notice" ]; then
    printf '{"systemMessage":%s,"hookSpecificOutput":{"hookEventName":"SessionStart","additionalContext":%s}}\n' \
        "$(json_string "$notice")" "$(json_string "$ctx")"
else
    json_context_reply SessionStart "$ctx"
fi
exit 0
