---
name: repoql
description: Core guidance for RepoQL - when to use it vs raw file reads, and the explore workflow.
---

# RepoQL: When and How

RepoQL is a local knowledge graph for repositories. It indexes code structure, enabling exploration without reading every file.

## When to Use RepoQL

**Use RepoQL when:**
- Exploring unfamiliar codebases (Inventory -> Locate -> Inspect)
- Finding where something is implemented (Locate intent)
- Understanding code structure without reading every file
- Searching semantically ("how does auth work?")
- Aggregating info (count functions, find patterns)
- Fetching content with token budget awareness (read tool)

**Use regular file reads when:**
- You know exactly what file you need
- Making edits (RepoQL is read-only)
- Working with files RepoQL doesn't index

## The Explore Workflow

Match your intent to your knowledge state:

| Intent | Question | Result |
|--------|----------|--------|
| Inventory | "What exists?" | Broad discovery of files/structure |
| Locate | "Where is X?" | Ranked results with snippets |
| Inspect | "Show me structure" | Detailed view with line numbers |
| Explain | "How does X work?" | LLM-synthesized answer |

**Typical progression:**
1. **Inventory** a scope to understand what's there
2. **Locate** specific concepts with keywords
3. **Inspect** relevant files for structure
4. **Explain** complex logic when needed

## Tools Summary

| Tool | Use Case |
|------|----------|
| `repoql_explore` | Discovery and understanding (start here) |
| `repoql_query` | SQL aggregation and filtering |
| `repoql_read` | Fetch content with token budget |
| `repoql_import` | Add external repositories |

## Token Budgets

Budget = tokens you want to spend on the response.

| Intent | Typical Budget |
|--------|----------------|
| Inventory | 800-2000 |
| Locate | 1000-2000 |
| Inspect | 2000-5000 |
| Explain | 1000-3000 |

Higher budget = richer detail. Start small, increase if needed.
