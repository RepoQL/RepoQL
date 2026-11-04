#!/bin/bash
# After a Bash, Grep or Glob call, tell the agent the RepoQL address of a git worktree the call created or searched.
# rql reads the command and decides everything; this adapter forwards it, writing a Grep or Glob call as the shell search it equals.
# worktree-command-hook.ps1 is the same hook for Windows PowerShell.
set -o pipefail
trap 'exit 0' ERR

command -v rql >/dev/null 2>&1 || exit 0

case "$0" in */*) script_dir=${0%/*} ;; *) script_dir=. ;; esac
script_dir=$(cd "$script_dir" && pwd)
. "$script_dir/json.sh"

# One shell word: bare when it holds only characters no shell treats specially,
# otherwise single-quoted, with each single quote written as '\''. sed does the
# rewrite because bash versions disagree on backslashes in a ${//} replacement.
# The x carried through sed keeps a final newline the substitution would strip.
plain_word='^[A-Za-z0-9_./:=@%+,-]+$'
shell_word() {
    local quoted
    if [[ $1 =~ $plain_word ]]; then
        printf '%s' "$1"
    else
        quoted=$(printf '%sx' "$1" | LC_ALL=C sed "s/'/'\\\\''/g")
        printf "'%s'" "${quoted%x}"
    fi
}

# Set variable $1 to the decoded text of the string at JSON path $2, kept whole:
# tabs, newlines and a final newline survive, which a "path<TAB>value" line
# cannot carry. A command substitution would strip the final newline, so the x
# rides through it and printf -v assigns without a second one.
input=$(cat)
read_text() {
    local text
    text=$(printf '%s' "$input" | json_text "^$2\$"; printf x)
    printf -v "$1" '%s' "${text%x}"
}

session=""
agent=""
tool=""
ignore_case=""
multiline=""
while IFS=$'\t' read -r name value; do
    case "$name" in
        session_id) session=$value ;;
        agent_id) agent=$value ;;
        tool_name) tool=$value ;;
        tool_input.-i) ignore_case=$value ;;
        tool_input.multiline) multiline=$value ;;
    esac
done < <(printf '%s' "$input" | json_leaves '^(session_id|agent_id|tool_name|tool_input[.](-i|multiline))$')
reading="${CLAUDE_PROJECT_DIR:-}"
[ -n "$session" ] && [ -d "$reading" ] || exit 0
read_text workspace cwd

# A Grep call is the ripgrep search it runs, with the fields that decide which
# lines match; output_mode, -n, -A, -B, -C and head_limit change only what is
# printed. A Glob call is a find over whole paths, which rql leaves untranslated.
# Both search their path, or the session's directory without one.
case "$tool" in
    Bash)
        read_text command 'tool_input[.]command' ;;
    Grep|Glob)
        read_text pattern 'tool_input[.]pattern'
        read_text path 'tool_input[.]path'
        path=${path:-$workspace}
        [ -n "$pattern" ] && [ -n "$path" ] || exit 0
        if [ "$tool" = Grep ]; then
            read_text glob 'tool_input[.]glob'
            read_text type 'tool_input[.]type'
            command="rg -n"
            [ "$ignore_case" = true ] && command+=" -i"
            [ -n "$glob" ] && command+=" -g $(shell_word "$glob")"
            [ -n "$type" ] && command+=" -t $(shell_word "$type")"
            [ "$multiline" = true ] && command+=" -U --multiline-dotall"
            command+=" -e $(shell_word "$pattern") -- $(shell_word "$path")"
        else
            case "$pattern" in
                /*) whole=$pattern ;;
                *) case "$path" in */) whole=$path$pattern ;; *) whole=$path/$pattern ;; esac ;;
            esac
            command="find $(shell_word "$path") -path $(shell_word "$whole")"
        fi ;;
    *) exit 0 ;;
esac
[ -n "$command" ] || exit 0

cd "$reading" || exit 0
args=(worktree check --command "$command" --session "$session")
[ -n "$agent" ] && args+=(--agent "$agent")
[ -d "$workspace" ] && args+=(--cwd "$workspace")
notice=$(REPOQL_CWD="$reading" rql "${args[@]}" </dev/null 2>/dev/null) || exit 0
[ -n "$notice" ] || exit 0
json_context_reply PostToolUse "$notice"
exit 0
