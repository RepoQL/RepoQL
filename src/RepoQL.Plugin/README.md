# RepoQL Plugin for Claude Code

Queryable code intelligence - explore codebase structure without reading files.

RepoQL indexes your repository into a graph database so Claude can query structure without reading every file. This saves tokens, finds things faster, and provides semantic search across your codebase.

## Prerequisites

1. **RepoQL installed** - The `repoql` command must be available in PATH
2. **Repository indexed** - Run `repoql index` in your project root
3. **Claude Code** - Version 2.0+ with plugin support
4. **Bash** - Required for hooks (Git Bash on Windows)

## Installation

### From Marketplace (Recommended)

```
/plugin marketplace add anthropics/repoql-plugin
/plugin install repoql
```

### From GitHub

```
/plugin install anthropics/repoql-plugin
```

### Local Development

```bash
claude --plugin-dir ./src/RepoQL.Plugin
```

## Quick Start

After installation, RepoQL provides:

### Slash Commands

| Command | Purpose |
|---------|---------|
| `/repoql:explore` | Full discovery workflow |
| `/repoql:find` | Quick semantic search |
| `/repoql:structure` | Directory overview |
| `/repoql:query` | SQL interface |
| `/repoql:imports` | External repo management |
| `/repoql:diagnose` | Troubleshooting |
| `/repoql:help` | Query embedded docs |

### Auto-Triggered Skills

The plugin includes skills that auto-activate:

- **code-intelligence** - Triggers on "where is", "what files", "show me the structure"
- **sql-expert** - Triggers on "how many", "count the", "what calls"

### Research Agent

For thorough investigation, the deep-dive agent runs in isolated context:

```
Use the deep-dive agent to investigate how authentication is implemented
```

## Key Concepts

### Explore Before Read

```
# Wrong: guess and read
read("file:///src/**/*Auth*.cs", 5000)

# Right: explore finds, read fetches
explore(intent=Locate, keywords="authentication", tokenBudget=1500)
read("file:///src/Auth.cs#symbol=ValidateToken", 2000)
```

### Intent-Based Exploration

| Intent | Use When |
|--------|----------|
| Inventory | "What's here?" - Survey a directory |
| Locate | Know concept, not location |
| Inspect | Know target, need depth |
| Explain | Need synthesized understanding |

### SQL for Computation

```sql
-- Language distribution
SELECT lang, COUNT(*) FROM Files GROUP BY lang;

-- Find error-prone files
SELECT uri, error_count FROM Files WHERE error_count > 0;

-- Semantic search
SELECT uri, score FROM search('authentication', k := 10);
```

## Hooks

The plugin includes automatic hooks:

- **SessionStart** - Checks index freshness and sets `REPOQL_STATUS` environment variable
- **PostToolUse (Write|Edit)** - Triggers incremental reindex when files change

## Troubleshooting

### Index Not Found

```bash
# Build initial index
repoql index
```

### Stale Index

```bash
# Incremental update
repoql index --incremental
```

### Connection Issues

```
/repoql:diagnose connection refused
```

### Check Status

```sql
query(":diagnostics:")
```

## Documentation

All RepoQL documentation is embedded and queryable:

```
explore(intent=Locate, uriGlob="help://**", keywords="your topic", tokenBudget=1500)
```

Or browse:

```sql
SELECT uri, headline FROM Files WHERE uri LIKE 'help://%';
```

## License

MIT
