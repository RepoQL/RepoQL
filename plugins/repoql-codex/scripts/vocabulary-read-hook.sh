#!/bin/bash
# Forward delivered text only. The host owns matching, scope, and session suppression.
set -o pipefail
trap 'printf "%s\n" "RepoQL vocabulary hints: hook failed; continuing the read." >&2; exit 0' ERR

command -v jq >/dev/null 2>&1 || exit 0
input=$(cat)
session=$(jq -r '.session_id // empty' <<<"$input")
workspace=$(jq -r '.cwd // empty' <<<"$input")
[ -n "$session" ] && [ -d "$workspace" ] || exit 0

# Do not scan tool arguments, shell commands, images, or failed tool responses.
if ! jq -e '(.tool_name // "") | test("^(Read|read_file|mcp__.*[Rr][Ee][Pp][Oo][Qq][Ll].*__read)$")' <<<"$input" >/dev/null; then
    exit 0
fi
if jq -e '.tool_response | type == "object" and (.isError == true or .is_error == true)' <<<"$input" >/dev/null; then
    exit 0
fi
target=$(jq -r '(.tool_input.file_path // .tool_input.path // .tool_input.uriGlob // .tool_input.uri // "")
    | if type == "string" then split(" =>")[0] | split("#")[0] else "" end' <<<"$input")
[ -n "$target" ] || exit 0
# Native relative paths resolve from the harness cwd; MCP globs are repository-relative.
case $(jq -r '.tool_name' <<<"$input") in
    Read|read_file)
        case "$target" in
            /*|[A-Za-z]:*|*://*) ;;
            *) target="$workspace/$target" ;;
        esac
        ;;
esac
# 65536 Unicode scalars fit within the CLI's 131072 UTF-16 character limit.
content=$(jq -r '.tool_response |
    if type == "string" then .
    elif type == "object" then
        if (.file.content? | type) == "string" then .file.content
        elif (.content? | type) == "string" then .content
        elif (.content? | type) == "array" then [.content[] | select(.type == "text") | .text | select(type == "string")] | join("\n")
        else "" end
    else "" end | .[0:65536]' <<<"$input")
[ -n "$content" ] || exit 0
command -v rql >/dev/null 2>&1 || {
    printf '%s\n' 'RepoQL vocabulary hints: rql is unavailable; continuing the read.' >&2
    exit 0
}
cd "$workspace"
if ! hints=$(printf '%s' "$content" | REPOQL_CWD="$workspace" rql vocabulary hints "$target" --session "$session" --limit 5 --max-chars 2000); then
    printf '%s\n' 'RepoQL vocabulary hints: CLI failed; continuing the read.' >&2
    exit 0
fi
[ -n "$hints" ] || exit 0
jq -n --arg ctx "$hints" '{hookSpecificOutput: {hookEventName: "PostToolUse", additionalContext: $ctx}}'
exit 0
