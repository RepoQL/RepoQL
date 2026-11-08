#!/bin/bash
# After a Bash call that may have created a git worktree, tell the agent the RepoQL address rql mounted it under.
# rql finds the new worktree and decides the notice; this adapter's one test is whether the command mentions `worktree add`.
# worktree-command-hook.ps1 is the same hook for Windows PowerShell.
set -o pipefail
trap 'exit 0' ERR

command -v rql >/dev/null 2>&1 || exit 0

case "$0" in */*) script_dir=${0%/*} ;; *) script_dir=. ;; esac
script_dir=$(cd "$script_dir" && pwd)
. "$script_dir/json.sh"

input=$(cat)
session=""
agent=""
command=""
have_command=""
while IFS=$'\t' read -r name value; do
    case "$name" in
        session_id) session=$value ;;
        agent_id) agent=$value ;;
        tool_input.command) command=$value; have_command=1 ;;
    esac
done < <(printf '%s' "$input" | json_leaves '^(session_id|agent_id|tool_input[.]command)$')
# json_leaves leaves out a value holding a tab or a newline; only then is the
# payload scanned again for the whole text. The x keeps a final newline the
# substitution would strip.
if [ -z "$have_command" ]; then
    command=$(printf '%s' "$input" | json_text '^tool_input[.]command$'; printf x)
    command=${command%x}
fi
# Without this test every Bash call would start rql after it runs.
[[ $command == *"worktree add"* ]] || exit 0
reading="${CLAUDE_PROJECT_DIR:-}"
[ -n "$session" ] && [ -d "$reading" ] || exit 0

cd "$reading" || exit 0
args=(worktree check --command "$command" --session "$session")
[ -n "$agent" ] && args+=(--agent "$agent")
notice=$(REPOQL_CWD="$reading" rql "${args[@]}" </dev/null 2>/dev/null) || exit 0
[ -n "$notice" ] || exit 0
json_context_reply PostToolUse "$notice"
exit 0
