#!/bin/bash
# Record which git worktree this session edits, so worktree-check-hook.sh can say when RepoQL reads another tree.
# rql owns tree resolution and state. Tracking runs detached: it must never slow or block an edit.
# worktree-track-hook.ps1 is the same hook for Windows PowerShell.
set -o pipefail
trap 'exit 0' ERR

command -v rql >/dev/null 2>&1 || exit 0

case "$0" in */*) script_dir=${0%/*} ;; *) script_dir=. ;; esac
script_dir=$(cd "$script_dir" && pwd)
. "$script_dir/json.sh"

session=""
workspace=""
files=""
while IFS=$'\t' read -r name value; do
    case "$name" in
        session_id) session=$value ;;
        cwd) workspace=$value ;;
        tool_input.*) files+="$value"$'\n' ;;
    esac
done < <(json_leaves '^(session_id|cwd|tool_input[.](file_path|notebook_path|path|edits[.][0-9]+[.](file_path|path)))$')
# The MCP server, and therefore RepoQL's reading tree, starts in the project directory and stays there.
reading="${CLAUDE_PROJECT_DIR:-}"
[ -n "$session" ] && [ -d "$reading" ] || exit 0
files=$(printf '%s' "$files" | sort -u)
[ -n "$files" ] || exit 0

(
    cd "$reading" || exit 0
    count=0
    while IFS= read -r file; do
        [ -n "$file" ] || continue
        [ "$count" -lt 8 ] || break
        count=$((count + 1))
        case "$file" in
            /*|[A-Za-z]:*) ;;
            *) [ -d "$workspace" ] && file="$workspace/$file" || continue ;;
        esac
        REPOQL_CWD="$reading" rql worktree track "$file" --session "$session"
    done <<<"$files"
) </dev/null >/dev/null 2>&1 &
exit 0
