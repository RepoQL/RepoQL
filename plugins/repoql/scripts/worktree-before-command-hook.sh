#!/bin/bash
# Before a Bash call that may create a git worktree, let rql list the worktrees that exist, so the call's new one can be found after it.
# rql owns the listing and the comparison; this adapter's one test is whether the command mentions `worktree add`.
# It prints nothing and never blocks the call. worktree-before-command-hook.ps1 is the same hook for Windows PowerShell.
set -o pipefail
trap 'exit 0' ERR

command -v rql >/dev/null 2>&1 || exit 0

case "$0" in */*) script_dir=${0%/*} ;; *) script_dir=. ;; esac
script_dir=$(cd "$script_dir" && pwd)
. "$script_dir/json.sh"

input=$(cat)
# Without this test every Bash call would start rql before it runs.
# The x keeps a final newline the substitution would strip.
command=$(printf '%s' "$input" | json_text '^tool_input[.]command$'; printf x)
command=${command%x}
[[ $command == *"worktree add"* ]] || exit 0

session=""
agent=""
while IFS=$'\t' read -r name value; do
    case "$name" in
        session_id) session=$value ;;
        agent_id) agent=$value ;;
    esac
done < <(printf '%s' "$input" | json_leaves '^(session_id|agent_id)$')
reading="${CLAUDE_PROJECT_DIR:-}"
[ -n "$session" ] && [ -d "$reading" ] || exit 0

cd "$reading" || exit 0
args=(worktree check --before-command "$command" --session "$session")
[ -n "$agent" ] && args+=(--agent "$agent")
REPOQL_CWD="$reading" rql "${args[@]}" </dev/null >/dev/null 2>&1 || exit 0
exit 0
