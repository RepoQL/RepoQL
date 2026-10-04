#!/bin/bash
# The host owns relevance matching, ranking, and once-per-session suppression.
# Hook failures report to stderr but must never block an edit.
# concepts-write-hook.ps1 is the same hook for Windows PowerShell.
set -o pipefail
trap 'printf "%s\n" "RepoQL concept hints: hook failed; continuing the edit." >&2; exit 0' ERR

command -v rql >/dev/null 2>&1 || {
    printf '%s\n' 'RepoQL concept hints: rql is unavailable; continuing the edit.' >&2
    exit 0
}

case "$0" in */*) script_dir=${0%/*} ;; *) script_dir=. ;; esac
script_dir=$(cd "$script_dir" && pwd)
. "$script_dir/json.sh"

# The host reads targets as URI globs: escape the metacharacters a real path can
# hold, so app/[slug]/page.tsx names that file instead of a character class.
literal_target() {
    local path=$1
    path=${path//\*/%2A}
    path=${path//\?/%3F}
    path=${path//\[/%5B}
    path=${path//\{/%7B}
    path=${path//;/%3B}
    printf '%s' "$path"
}

input=$(cat)
session=""
workspace=""
while IFS=$'\t' read -r name value; do
    case "$name" in
        session_id) session=$value ;;
        cwd) workspace=$value ;;
    esac
done < <(printf '%s' "$input" | json_leaves '^(session_id|cwd)$')
[ -d "$workspace" ] || workspace="$PWD"
cd "$workspace"
[ -n "$session" ] || exit 0

# Include both sides of a move, as well as added, updated, and deleted files.
files=$(printf '%s' "$input" | json_text '^tool_input[.](command|patch)$' \
    | sed -nE 's/^\*\*\* (Update File|Add File|Delete File|Move to): //p' \
    | awk '!seen[$0]++')
[ -n "$files" ] || exit 0

context=""
remaining=5
file_count=0
while IFS= read -r file; do
    [ -n "$file" ] || continue
    [ "$file_count" -lt 8 ] || break
    file_count=$((file_count + 1))
    # Query paths separately so newly created files need not exist in the index.
    # The rendered view is one "uri<TAB>invariant" line per concept, then its why.
    if ! hints=$(rql concept hints "$(literal_target "$file")" --session "$session" --limit "$remaining" </dev/null); then
        printf '%s\n' 'RepoQL concept hints: CLI failed; continuing the edit.' >&2
        continue
    fi
    count=0
    while IFS= read -r line; do
        case "$line" in [a-z]*://*$'\t'*) count=$((count + 1)) ;; esac
    done <<<"$hints"
    [ "$count" -gt 0 ] || continue
    [ -z "$context" ] || context+=$'\n\n'
    context+="$hints"
    remaining=$((remaining - count))
    [ "$remaining" -gt 0 ] || break
done <<<"$files"

[ -n "$context" ] || exit 0
json_context_reply PreToolUse "$context"
exit 0
