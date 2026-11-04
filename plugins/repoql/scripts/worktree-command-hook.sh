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
plain_word='^[A-Za-z0-9_./:=@%+,-]+$'
shell_word() {
    if [[ $1 =~ $plain_word ]]; then
        printf '%s' "$1"
    else
        printf "'%s'" "$(printf '%s' "$1" | LC_ALL=C sed "s/'/'\\\\''/g")"
    fi
}

input=$(cat)
session=""
agent=""
workspace=""
tool=""
path=""
ignore_case=""
while IFS=$'\t' read -r name value; do
    case "$name" in
        session_id) session=$value ;;
        agent_id) agent=$value ;;
        cwd) workspace=$value ;;
        tool_name) tool=$value ;;
        tool_input.path) path=$value ;;
        tool_input.-i) ignore_case=$value ;;
    esac
done < <(printf '%s' "$input" | json_leaves '^(session_id|agent_id|cwd|tool_name|tool_input[.](path|-i))$')
reading="${CLAUDE_PROJECT_DIR:-}"
[ -n "$session" ] && [ -d "$reading" ] || exit 0

# Grep and Glob search their path, or the session's directory without one.
case "$tool" in
    Bash)
        command=$(printf '%s' "$input" | json_text '^tool_input[.]command$') ;;
    Grep|Glob)
        pattern=$(printf '%s' "$input" | json_text '^tool_input[.]pattern$')
        path=${path:-$workspace}
        [ -n "$pattern" ] && [ -n "$path" ] || exit 0
        if [ "$tool" = Grep ]; then
            command="grep -rn"
            [ "$ignore_case" = true ] && command+=" -i"
            command+=" -e $(shell_word "$pattern") $(shell_word "$path")"
        else
            command="find $(shell_word "$path") -name $(shell_word "$pattern")"
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
