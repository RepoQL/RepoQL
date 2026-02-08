#!/bin/bash
# RepoQL SessionStart hook - verify index freshness
# Non-blocking: always exits 0, sets environment variables for Claude

# Find the env file path from stdin JSON
ENV_FILE=""
if [ -t 0 ]; then
    # No stdin, check environment
    ENV_FILE="${CLAUDE_ENV_FILE:-}"
else
    # Parse from stdin JSON
    ENV_FILE=$(cat | jq -r '.env_file // empty' 2>/dev/null)
fi

# Check if repoql is available
if ! command -v repoql &> /dev/null; then
    if [ -n "$ENV_FILE" ]; then
        echo "REPOQL_STATUS=not_installed" >> "$ENV_FILE"
    fi
    exit 0
fi

# Check if index exists
INDEX_PATH=".repoql/index.db"
if [ ! -f "$INDEX_PATH" ]; then
    if [ -n "$ENV_FILE" ]; then
        echo "REPOQL_STATUS=no_index" >> "$ENV_FILE"
        echo "REPOQL_MESSAGE=No index found. Run 'repoql index' to build." >> "$ENV_FILE"
    fi
    exit 0
fi

# Check index age (in hours)
if [ "$(uname)" = "Darwin" ]; then
    INDEX_AGE=$(( ($(date +%s) - $(stat -f %m "$INDEX_PATH")) / 3600 ))
else
    INDEX_AGE=$(( ($(date +%s) - $(stat -c %Y "$INDEX_PATH")) / 3600 ))
fi

# Set status based on age
if [ "$INDEX_AGE" -gt 24 ]; then
    if [ -n "$ENV_FILE" ]; then
        echo "REPOQL_STATUS=stale" >> "$ENV_FILE"
        echo "REPOQL_MESSAGE=Index is ${INDEX_AGE}h old. Consider running 'repoql index --incremental'." >> "$ENV_FILE"
    fi
else
    if [ -n "$ENV_FILE" ]; then
        echo "REPOQL_STATUS=ready" >> "$ENV_FILE"
    fi
fi

exit 0
