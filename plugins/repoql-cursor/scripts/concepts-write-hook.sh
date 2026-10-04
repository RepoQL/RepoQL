#!/bin/bash
# Surface the concepts relevant to a file before Cursor's agent writes it.
# The host owns relevance matching, ranking, and once-per-session suppression.
# concepts-write-hook.ps1 is the same hook for Windows; run-hook.cmd picks between them.
#
# preToolUse is a permission hook: Cursor blocks the write on invalid JSON, so
# every path prints exactly one JSON object and never a permission decision —
# {} lets the write through untouched. Stderr counts as failure, so it is closed.
set -o pipefail
trap 'printf "{}\n"; exit 0' ERR
exec 2>/dev/null

pass() { printf '{}\n'; exit 0; }

export PATH="$HOME/.local/bin:$PATH"
command -v rql >/dev/null 2>&1 || pass

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

# Cursor does not document the Write tool's input; accept every path field its
# payloads and the Claude-compatible shape are known to use.
path_fields='(file_path|path|target_file|filePath|edits[.][0-9]+[.](file_path|path))'

input=$(cat)
conversation=""
session=""
cwd=""
root=""
files=""
while IFS=$'\t' read -r name value; do
    case "$name" in
        conversation_id) conversation=$value ;;
        session_id) session=$value ;;
        cwd) cwd=$value ;;
        workspace_roots.0) root=$value ;;
        tool_input.*) files+="$value"$'\n' ;;
    esac
done < <(printf '%s' "$input" | json_leaves "^(conversation_id|session_id|cwd|workspace_roots[.]0|tool_input[.]$path_fields)\$")

session=${conversation:-$session}
[ -n "$session" ] || pass
workspace=${cwd:-$root}
[ -d "$workspace" ] || workspace="${CURSOR_PROJECT_DIR:-$PWD}"
cd "$workspace" || pass

# tool_input may arrive as a JSON string instead of an object.
[ -n "$files" ] || files=$(printf '%s' "$input" | json_string_at tool_input | json_leaves "^$path_fields\$" | cut -f2)
files=$(printf '%s\n' "$files" | sort -u)
[ -n "$files" ] || pass

context=""
remaining=5
file_count=0
while IFS= read -r file; do
    [ -n "$file" ] || continue
    [ "$file_count" -lt 8 ] || break
    file_count=$((file_count + 1))
    # Query paths separately so newly created files need not exist in the index.
    # The rendered view is one "uri<TAB>invariant" line per concept, then its why.
    hints=$(rql concept hints "$(literal_target "$file")" --session "$session" --limit "$remaining" </dev/null) || continue
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

[ -n "$context" ] || pass
encoded=$(json_string "$context") || pass
printf '{"additional_context":%s}\n' "$encoded"
exit 0
