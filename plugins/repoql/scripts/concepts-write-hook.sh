#!/bin/bash
# RepoQL PreToolUse hook — surface the concepts relevant to the file being edited.
#
# Claude Code pipes the tool call as JSON on stdin. This maps it to
#   rql concepts <file> --session <id>
# which asks the running host for the capsules whose `relevance` glob matches the
# file and prints their invariants; they are injected as additionalContext so the
# agent sees them just before the write. The host dedupes per session, so each
# invariant surfaces once — not on every edit. Always exits 0: a missing rql/jq, a
# host that is down, or an unindexed repo must never block an edit.
trap 'exit 0' ERR

command -v rql >/dev/null 2>&1 || exit 0
command -v jq  >/dev/null 2>&1 || exit 0

input=$(cat)
file=$(jq -r '.tool_input.file_path // empty' <<<"$input")
session=$(jq -r '.session_id // empty' <<<"$input")
[ -n "$file" ] || exit 0

hints=$(rql concepts "$file" --session "$session" --limit 5 2>/dev/null)
[ -n "$hints" ] || exit 0

jq -n --arg ctx "$hints" '{
  hookSpecificOutput: {
    hookEventName: "PreToolUse",
    additionalContext: $ctx
  }
}'
exit 0
