#!/bin/bash
# When a subagent starts, tell it the RepoQL address of each git worktree its session works in.
# rql owns the session's worktrees, the mounts, and which addresses it names; this adapter forwards the ids.
# worktree-subagent-hook.ps1 is the same hook for Windows PowerShell.
set -o pipefail
trap 'exit 0' ERR

command -v rql >/dev/null 2>&1 || exit 0

case "$0" in */*) script_dir=${0%/*} ;; *) script_dir=. ;; esac
script_dir=$(cd "$script_dir" && pwd)
. "$script_dir/json.sh"

session=""
agent=""
while IFS=$'\t' read -r name value; do
    case "$name" in
        session_id) session=$value ;;
        agent_id) agent=$value ;;
    esac
done < <(json_leaves '^(session_id|agent_id)$')
reading="${CLAUDE_PROJECT_DIR:-}"
[ -n "$session" ] && [ -d "$reading" ] || exit 0

cd "$reading" || exit 0
args=(worktree check --subagent --session "$session")
[ -n "$agent" ] && args+=(--agent "$agent")
notice=$(REPOQL_CWD="$reading" rql "${args[@]}" </dev/null 2>/dev/null) || exit 0
[ -n "$notice" ] || exit 0
json_context_reply SubagentStart "$notice"
exit 0
