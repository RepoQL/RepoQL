#!/bin/bash
# RepoQL PostToolUse hook - trigger incremental reindex after file changes
# Non-blocking: always exits 0, runs reindex in background

# Read tool input from stdin
INPUT=$(cat)

# Extract file path from tool input
FILE_PATH=$(echo "$INPUT" | jq -r '.tool_input.file_path // empty' 2>/dev/null)

if [ -z "$FILE_PATH" ]; then
    # No file path in input, skip
    exit 0
fi

# Check if repoql is available
if ! command -v repoql &> /dev/null; then
    exit 0
fi

# Check if we're in a RepoQL-indexed directory
if [ ! -d ".repoql" ]; then
    exit 0
fi

# Trigger incremental reindex in background
# This is fire-and-forget; the index will update asynchronously
(repoql index --incremental --quiet &) 2>/dev/null

exit 0
