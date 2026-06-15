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

tree=$(rql read "file:///** => tree: folders" --token-budget 6000 2>/dev/null) || tree="(no index — run rql serve)"
ctx+=$'\n'"## Repository Structure"$'\n'"$tree"$'\n'

imports=$(rql query "SELECT source_uri || ' — ' || CAST(file_count AS VARCHAR) || ' files' AS repo FROM Filesystems WHERE source_uri LIKE 'github://%' ORDER BY source_uri" 2>/dev/null | grep '://' || true)
ctx+=$'\n'"## Imported Repositories"$'\n'
if [ -n "$imports" ]; then
    ctx+="Use these github:// URIs directly with read / explore / query:"$'\n'"$imports"$'\n'
else
    ctx+="(none — import one with: rql import github://owner/repo)"$'\n'
fi

docs=$(rql read "help://** => tree: headlines" --token-budget 5000 2>/dev/null) || docs="(no docs indexed)"
ctx+=$'\n'"## Documentation"$'\n'"$docs"

jq -n --arg ctx "$ctx" '{hookSpecificOutput: {hookEventName: "SessionStart", additionalContext: $ctx}}'
exit 0
