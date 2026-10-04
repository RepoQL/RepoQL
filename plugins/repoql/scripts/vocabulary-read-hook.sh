#!/bin/bash
# Forward delivered text only. The host owns matching, scope, and session suppression.
# vocabulary-read-hook.ps1 is the same hook for Windows PowerShell.
set -o pipefail
trap 'printf "%s\n" "RepoQL vocabulary hints: hook failed; continuing the read." >&2; exit 0' ERR

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
failed=""
file_path=""
path=""
uri_glob=""
uri=""
while IFS=$'\t' read -r name value; do
    case "$name" in
        session_id) session=$value ;;
        cwd) workspace=$value ;;
        tool_name) tool=$value ;;
        tool_response.isError|tool_response.is_error) [ "$value" != true ] || failed=1 ;;
        tool_input.file_path) file_path=$value ;;
        tool_input.path) path=$value ;;
        tool_input.uriGlob) uri_glob=$value ;;
        tool_input.uri) uri=$value ;;
    esac
done < <(printf '%s' "$input" | json_leaves '^(session_id|cwd|tool_name|tool_response[.](isError|is_error)|tool_input[.](file_path|path|uriGlob|uri))$')
[ -n "$session" ] && [ -d "$workspace" ] || exit 0

# Do not scan tool arguments, shell commands, images, or failed tool responses.
[[ $tool =~ ^(Read|read_file|mcp__.*[Rr][Ee][Pp][Oo][Qq][Ll].*__read)$ ]] || exit 0
[ -z "$failed" ] || exit 0
target=${file_path:-${path:-${uri_glob:-$uri}}}
target=${target%% =>*}
target=${target%%#*}
[ -n "$target" ] || exit 0
# Native relative paths resolve from the harness cwd and are literal files;
# MCP globs are repository-relative and already globs.
case "$tool" in
    Read|read_file)
        case "$target" in
            /*|[A-Za-z]:*|*://*) ;;
            *) target="$workspace/$target" ;;
        esac
        target=$(literal_target "$target")
        ;;
esac
# A response is the text itself, a native read's file body, or MCP text blocks.
# Blocks that are not text carry no text field.
text=""
response_text '^tool_response$' '^tool_response[.]file[.]content$' '^tool_response[.]content$' '^tool_response[.]content[.][0-9]+[.]text$'
[ -n "$text" ] || exit 0
command -v rql >/dev/null 2>&1 || {
    printf '%s\n' 'RepoQL vocabulary hints: rql is unavailable; continuing the read.' >&2
    exit 0
}
cd "$workspace"
if ! hints=$(printf '%s' "$text" | REPOQL_CWD="$workspace" rql vocabulary hints "$target" --session "$session" --limit 5 --max-chars 2000); then
    printf '%s\n' 'RepoQL vocabulary hints: CLI failed; continuing the read.' >&2
    exit 0
fi
[ -n "$hints" ] || exit 0
json_context_reply PostToolUse "$hints"
exit 0
