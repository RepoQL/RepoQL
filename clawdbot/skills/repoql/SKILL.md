---
name: repoql
description: Core guidance for RepoQL - when to use it vs raw file reads, and the explore workflow.
---

# RepoQL: When and How

RepoQL is a local knowledge graph for repositories. It indexes code structure, enabling exploration without reading every file.

## When to Use RepoQL

**Use RepoQL when:**
- Exploring unfamiliar codebases (Inventory -> Locate -> Inspect)
- Finding where something is implemented (location-oriented questions)
- Understanding code structure without reading every file
- Searching semantically ("how does auth work?")
- Aggregating info (count functions, find patterns)
- Fetching content with token budget awareness (read tool)

**Use regular file reads when:**
- You know exactly what file you need
- Making edits (RepoQL is read-only)
- Working with files RepoQL doesn't index

## The Explore Workflow

Use `keywords` as the vocabulary probe, and add `question` when you have a specific intent. `question` reranks results toward the answer; it does not replace keywords.

**Typical progression:**
1. **Survey** a scope with broad `keywords`
2. **Locate** specific concepts with refined `keywords`
3. **Inspect** relevant files with `repoql_read`
4. **Explain** complex logic with `repoql_explain`

## Tools Summary

| Tool | Use Case |
|------|----------|
| `repoql_explore` | Discovery and understanding (start here) |
| `repoql_keywords` | Reshape rough terms into the repository's real vocabulary |
| `repoql_query` | SQL aggregation and filtering over the graph |
| `repoql_read` | Fetch content with token budget (URIs, fragments, modifiers) |
| `repoql_explain` | Synthesized answers with citations |
| `repoql_execute` | Sandboxed JavaScript over the graph — diagrams, conversions, artifacts |
| `repoql_import` | Add or remove external repositories |
| `repoql_capture_concept` | Write a durable invariant into the repo's concept memory |
| `repoql_command` | Management commands — config, diagnostics, account, host, imports |
| `repoql_watch` | Run a process under the host OTEL collector and query its telemetry |
| `repoql_status` | Check host/socket/plugin health |

The plugin exposes the same surface as the RepoQL MCP server; each tool's
description carries the full MCP guidance. `repoql_explore` and
`repoql_keywords` are the discovery entry points; `repoql_query` and
`repoql_execute` are the power tools; `repoql_command` is the management remote.

## Token Budgets

Budget = tokens you want to spend on the response.

| Task | Typical Budget |
|--------|----------------|
| Inventory | 800-2000 |
| Locate | 1000-2000 |
| Inspect | 2000-5000 |
| Explain | 1000-3000 |

Higher budget = richer detail. Start small, increase if needed.
