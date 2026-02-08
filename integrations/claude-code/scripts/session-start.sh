#!/bin/bash
# RepoQL SessionStart hook — inject orientation context.
# Stdout is added to the agent's context. Always exits 0.

trap 'exit 0' ERR

if ! command -v repoql &> /dev/null; then
    echo "RepoQL is not installed. Install: curl -fsSL https://downloads.repoql.ai/install.sh | bash"
    exit 0
fi

echo "# RepoQL: Repository Orientation"
echo ""

echo "## Repository Structure"
repoql read "file:///** => tree: folders" --budget 3000 2>/dev/null || echo "(no index — run repoql index)"
echo ""

echo "## Documentation"
repoql read "help://** => tree: headlines" --budget 5000 2>/dev/null || echo "(no docs indexed)"

exit 0
