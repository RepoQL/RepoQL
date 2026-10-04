#!/bin/bash
# After a RepoQL tool call, tell the agent when RepoQL answered from a different git worktree than the one it edits.
# rql owns detection, divergence, and notice suppression; this adapter forwards the call's scope and delivered text.
# worktree-check-hook.ps1 is the same hook for Windows PowerShell.
set -o pipefail
trap 'exit 0' ERR

command -v rql >/dev/null 2>&1 || exit 0

case "$0" in */*) script_dir=${0%/*} ;; *) script_dir=. ;; esac
script_dir=$(cd "$script_dir" && pwd)
. "$script_dir/json.sh"

# The text a tool handed back, at most 65536 bytes of it: every UTF-8 byte is at
# most one UTF-16 character, so this stays inside the CLI's 131072 limit. iconv
# drops a character the cut split; without iconv the CLI reads it as one mark.
response_text() {
    local shape
    for shape in "$@"; do
        text=$(printf '%s' "$input" | json_text "$shape" | LC_ALL=C head -c 65536 | { iconv -c -f UTF-8 -t UTF-8 2>/dev/null || cat; })
        [ -z "$text" ] || return 0
    done
}

input=$(cat)
session=""
workspace=""
tool=""
uri_glob=""
uri_glob_snake=""
uri=""
while IFS=$'\t' read -r name value; do
    case "$name" in
        session_id) session=$value ;;
        cwd) workspace=$value ;;
        tool_name) tool=$value ;;
        tool_input.uriGlob) uri_glob=$value ;;
        tool_input.uri_glob) uri_glob_snake=$value ;;
        tool_input.uri) uri=$value ;;
    esac
done < <(printf '%s' "$input" | json_leaves '^(session_id|cwd|tool_name|tool_input[.](uriGlob|uri_glob|uri))$')
reading="${CLAUDE_PROJECT_DIR:-}"
[ -n "$session" ] && [ -d "$reading" ] || exit 0

[[ $tool =~ ^mcp__(.*[Rr][Ee][Pp][Oo][Qq][Ll].*|rql)__ ]] || exit 0

pattern=${uri_glob:-${uri_glob_snake:-$uri}}
# A response is the text itself, or text blocks under content or at the top.
# Blocks that are not text carry no text field.
text=""
response_text '^tool_response$' '^tool_response[.]content$' '^tool_response[.]content[.][0-9]+[.]text$' '^tool_response[.][0-9]+[.]text$'

cd "$reading" || exit 0
args=(worktree check --session "$session")
[ -d "$workspace" ] && args+=(--cwd "$workspace")
[ -n "$pattern" ] && args+=(--pattern "$pattern")
notice=$(printf '%s' "$text" | REPOQL_CWD="$reading" rql "${args[@]}" 2>/dev/null) || exit 0
[ -n "$notice" ] || exit 0
json_context_reply PostToolUse "$notice"
exit 0
