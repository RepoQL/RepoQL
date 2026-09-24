#!/bin/bash
# After a RepoQL tool call, tell the agent when RepoQL answered from a different git worktree than the one it edits.
# rql owns detection, divergence, and notice suppression; this adapter forwards the call's scope and delivered text.
set -o pipefail
trap 'exit 0' ERR

command -v jq >/dev/null 2>&1 || exit 0
command -v rql >/dev/null 2>&1 || exit 0

input=$(cat)
session=$(jq -r '.session_id // empty' <<<"$input")
workspace=$(jq -r '.cwd // empty' <<<"$input")
reading="${CLAUDE_PROJECT_DIR:-}"
[ -n "$session" ] && [ -d "$reading" ] || exit 0

if ! jq -e '(.tool_name // "") | test("^mcp__(.*[Rr][Ee][Pp][Oo][Qq][Ll].*|rql)__")' <<<"$input" >/dev/null; then
    exit 0
fi

pattern=$(jq -r '.tool_input.uriGlob // .tool_input.uri_glob // .tool_input.uri // "" | if type == "string" then . else "" end' <<<"$input")
# 65536 Unicode scalars fit within the CLI's 131072 UTF-16 character limit.
content=$(jq -r '.tool_response |
    if type == "string" then .
    elif type == "object" then
        if (.content? | type) == "string" then .content
        elif (.content? | type) == "array" then [.content[] | select(.type == "text") | .text | select(type == "string")] | join("\n")
        else "" end
    elif type == "array" then [.[] | select(type == "object" and .type == "text") | .text | select(type == "string")] | join("\n")
    else "" end | .[0:65536]' <<<"$input")

cd "$reading" || exit 0
args=(worktree check --session "$session")
[ -d "$workspace" ] && args+=(--cwd "$workspace")
[ -n "$pattern" ] && args+=(--pattern "$pattern")
notice=$(printf '%s' "$content" | REPOQL_CWD="$reading" rql "${args[@]}" 2>/dev/null) || exit 0
[ -n "$notice" ] || exit 0
jq -n --arg ctx "$notice" '{hookSpecificOutput: {hookEventName: "PostToolUse", additionalContext: $ctx}}'
exit 0
