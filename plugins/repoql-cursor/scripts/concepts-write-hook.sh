#!/bin/bash
# Surface the concepts relevant to a file before Cursor's agent writes it.
# The host owns relevance matching, ranking, and once-per-session suppression.
#
# preToolUse is a permission hook: Cursor blocks the write on invalid JSON, so
# every path prints exactly one JSON object and never a permission decision —
# {} lets the write through untouched. Stderr counts as failure, so it is closed.
set -o pipefail
trap 'printf "{}\n"; exit 0' ERR
exec 2>/dev/null

pass() { printf '{}\n'; exit 0; }

export PATH="$HOME/.local/bin:$PATH"
command -v jq >/dev/null 2>&1 || pass
command -v rql >/dev/null 2>&1 || pass

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
session=$(jq -r '.conversation_id // .session_id // empty' <<<"$input")
[ -n "$session" ] || pass
workspace=$(jq -r '.cwd // .workspace_roots[0] // empty' <<<"$input")
[ -d "$workspace" ] || workspace="${CURSOR_PROJECT_DIR:-$PWD}"
cd "$workspace" || pass

# Cursor does not document the Write tool's input; accept every path field its
# payloads and the Claude-compatible shape are known to use. tool_input may
# arrive as a JSON string.
files=$(jq -r '
  (.tool_input | if type == "string" then (try fromjson catch {}) else . end) as $in
  | [$in.file_path?, $in.path?, $in.target_file?, $in.filePath?,
     ($in.edits[]?.file_path?), ($in.edits[]?.path?)]
  | map(select(type == "string" and length > 0)) | unique[]
' <<<"$input")
[ -n "$files" ] || pass

context=""
remaining=5
file_count=0
while IFS= read -r file; do
    [ -n "$file" ] || continue
    [ "$file_count" -lt 8 ] || break
    file_count=$((file_count + 1))
    # Query paths separately so newly created files need not exist in the index.
    hints=$(rql concept hints "$(literal_target "$file")" --session "$session" --limit "$remaining" --json) || continue
    count=$(jq -er '.concepts | if type == "array" then length else error("expected concepts array") end' <<<"$hints") || continue
    [ "$count" -gt 0 ] || continue
    entry=$(jq -r '.concepts[] | .uri + "\t" + .invariant +
        (if (.why // "") != "" then "\n  why: " + .why else "" end)' <<<"$hints")
    [ -z "$context" ] || context+=$'\n\n'
    context+="$entry"
    remaining=$((remaining - count))
    [ "$remaining" -gt 0 ] || break
done <<<"$files"

[ -n "$context" ] || pass
jq -cn --arg ctx "$context" '{additional_context: $ctx}'
exit 0
