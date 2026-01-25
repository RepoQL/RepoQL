# Read Tool Flows

Flow documentation for the read tool's modifiers.

## Overview

The read tool uses a unified syntax: `read("<pattern> => <modifier>: <parameter>", tokenBudget)`

- **Pattern**: URI or glob selecting files/symbols
- **Modifier**: How to transform or query the selected content
- **Token budget**: Controls output size and detail level

## Modifiers by Category

### Representation

Control how selected content is displayed.

| Modifier | Flow | Purpose |
|----------|------|---------|
| *(default)* | [default.md](default.md) | Auto-select representation based on budget |
| `=> headline` | [headline.md](headline.md) | Single-line summary per file |
| `=> structure` | [structure.md](structure.md) | Hierarchical outline with signatures |
| `=> content` | [content.md](content.md) | Full source code |
| `=> tree` | [tree.md](tree.md) | Directory tree with progressive verbosity |

### Search

Find content within selected files.

| Modifier | Flow | Purpose |
|----------|------|---------|
| `=> question: <q>` | [question.md](question.md) | LLM-synthesized answer with citations |
| `=> find: <keywords>` | [find.md](find.md) | Semantic search for concepts |
| `=> grep: <string>` | [grep.md](grep.md) | Literal string search |
| `=> regex: <pattern>` | [regex.md](regex.md) | Regular expression search |
| `=> astgrep: <pattern>` | [astgrep.md](astgrep.md) | Syntax-aware structural search |

### Graph

Traverse relationships from selected content.

| Modifier | Flow | Purpose |
|----------|------|---------|
| `=> <edge_type>` | [edges.md](edges.md) | Follow edges (IMPORTS, USES_SYMBOL, etc.) |
| `=> roots` | [roots.md](roots.md) | Walk up to entry points |
| `=> leaves` | [leaves.md](leaves.md) | Walk down to terminals |
| `=> tests` | [tests.md](tests.md) | Find covering tests |
| `=> similar` | [similar.md](similar.md) | Find semantically similar code |
| `=> docs` | [docs.md](docs.md) | Find related documentation |

### Diagnostics

Surface problems in selected content.

| Modifier | Flow | Purpose |
|----------|------|---------|
| `=> lint` | [lint.md](lint.md) | Show all diagnostics |
| `=> lint: errors` | [lint.md](lint.md) | Show errors only |
| `=> lint: warnings` | [lint.md](lint.md) | Show warnings only |

### History

Understand how content evolved.

| Modifier | Flow | Purpose |
|----------|------|---------|
| `=> history` | [history.md](history.md) | Git history for files |
| `=> history: <keywords>` | [history.md](history.md) | History filtered by relevance |
| `=> changes` | [changes.md](changes.md) | Working copy changes by changelist |
| `=> blame` | [blame.md](blame.md) | Per-line attribution |

## Related

- [North star: read-tool.md](../../../north-star/read-tool.md) — outcomes and "what great looks like"
- [North star: xray-elements.md](../../../north-star/xray-elements.md) — headline, structure, snippet format specs
