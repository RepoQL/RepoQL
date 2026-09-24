#!/bin/bash
# Record which git worktree this session edits, so worktree-check-hook.sh can say when RepoQL reads another tree.
# rql owns tree resolution and state. Tracking runs detached: it must never slow or block an edit.
set -o pipefail
trap 'exit 0' ERR

command -v jq >/dev/null 2>&1 || exit 0
command -v rql >/dev/null 2>&1 || exit 0

input=$(cat)
session=$(jq -r '.session_id // empty' <<<"$input")
workspace=$(jq -r '.cwd // empty' <<<"$input")
# The MCP server, and therefore RepoQL's reading tree, starts in the project directory and stays there.
reading="${CLAUDE_PROJECT_DIR:-}"
[ -n "$session" ] && [ -d "$reading" ] || exit 0

files=$(jq -r '
  [.tool_input.file_path?, .tool_input.notebook_path?, .tool_input.path?,
   (.tool_input.edits[]?.file_path?), (.tool_input.edits[]?.path?)]
  | map(select(type == "string" and length > 0)) | unique[]
' <<<"$input")
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
