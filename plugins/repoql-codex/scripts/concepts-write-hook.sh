#!/bin/bash
# The host owns relevance matching, ranking, and once-per-session suppression.
# Hook failures report to stderr but must never block an edit.
set -o pipefail
trap 'printf "%s\n" "RepoQL concept hints: hook failed; continuing the edit." >&2; exit 0' ERR

command -v jq >/dev/null 2>&1 || exit 0
command -v rql >/dev/null 2>&1 || {
    printf '%s\n' 'RepoQL concept hints: rql is unavailable; continuing the edit.' >&2
    exit 0
}

input=$(cat)
session=$(jq -r '.session_id // empty' <<<"$input")
workspace=$(jq -r '.cwd // empty' <<<"$input")
[ -d "$workspace" ] || workspace="$PWD"
cd "$workspace"
[ -n "$session" ] || exit 0

# Include both sides of a move, as well as added, updated, and deleted files.
patch=$(jq -r '.tool_input.command // .tool_input.patch // empty' <<<"$input")
files=$(printf '%s\n' "$patch" \
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
    if ! hints=$(rql concept hints "$file" --session "$session" --limit "$remaining" --json); then
        printf '%s\n' 'RepoQL concept hints: CLI failed; continuing the edit.' >&2
        continue
    fi
    if ! count=$(jq -er '.concepts | if type == "array" then length else error("expected concepts array") end' <<<"$hints"); then
        printf '%s\n' 'RepoQL concept hints: invalid CLI response; continuing the edit.' >&2
        continue
    fi
    [ "$count" -gt 0 ] || continue
    entry=$(jq -r '.concepts[] | .uri + "\t" + .invariant +
        (if (.why // "") != "" then "\n  why: " + .why else "" end)' <<<"$hints")
    [ -z "$context" ] || context+=$'\n\n'
    context+="$entry"
    remaining=$((remaining - count))
    [ "$remaining" -gt 0 ] || break
done <<<"$files"

[ -n "$context" ] || exit 0
jq -n --arg ctx "$context" '{
  hookSpecificOutput: {
    hookEventName: "PreToolUse",
    additionalContext: $ctx
  }
}'
exit 0
