#!/bin/bash
# RepoQL SessionStart hook — inject repository orientation as context.
#
# SessionStart injects only the JSON hookSpecificOutput.additionalContext;
# plain stdout is NOT added to the agent's context, so the orientation is built
# into one string and emitted as that envelope. Always exits 0 so a missing
# rql/jq, a host that is down, or an unindexed repo never blocks the session.
trap 'exit 0' ERR

# Hooks may run with a minimal PATH; rql installs to ~/.local/bin.
export PATH="$HOME/.local/bin:$PATH"

command -v rql >/dev/null 2>&1 || exit 0
command -v jq  >/dev/null 2>&1 || exit 0

ctx="# RepoQL: Repository Orientation"$'\n'

# Orientation carries only what the agent cannot cheaply pull itself: which repos
# are mounted. Repository structure and docs are large and re-derivable on demand
# (read / explore), so they are never dumped here. Readiness is omitted on purpose:
# a transient "not ready" at startup would wrongly teach the agent that RepoQL is
# unusable for the whole session, when it just needed a moment to warm up.
imports=$(rql query "SELECT source_uri FROM Filesystems WHERE source_uri LIKE 'github://%' ORDER BY source_uri" 2>/dev/null | grep '://' || true)
ctx+=$'\n'"## Imported Repositories"$'\n'
if [ -n "$imports" ]; then
    ctx+="Use these github:// URIs directly with read / explore / query:"$'\n'"$imports"$'\n'
else
    ctx+="(none — import one with: rql import github://owner/repo)"$'\n'
fi

ctx+=$'\n'"## Concepts"$'\n'"Repository invariants, if any, are addressable at concept:// — browse them with read(\"concept:///**\")."$'\n'

jq -n --arg ctx "$ctx" '{hookSpecificOutput: {hookEventName: "SessionStart", additionalContext: $ctx}}'
exit 0
