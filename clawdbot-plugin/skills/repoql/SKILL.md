---
name: repoql
description: Query and understand code repositories via RepoQL's knowledge graph. Use for code exploration, search, and structure understanding instead of raw file reads.
---

# RepoQL

RepoQL is a local knowledge graph for repositories. Use it via mcporter when working with code.

## When to Use

**Use RepoQL instead of raw file reads when:**
- Exploring unfamiliar codebases
- Finding where something is implemented
- Understanding code structure without reading every file
- Searching semantically ("how does auth work?")
- Aggregating info (count functions, find patterns, list types)

**Use regular file reads when:**
- You know exactly what file you need
- Making edits (RepoQL is read-only)
- Working with files RepoQL doesn't index

## Core Tools

### explore - X-ray vision (start here)

Intent-based exploration. Match intent to your knowledge state:

| Intent | When | Example |
|--------|------|---------|
| Inventory | Don't know what exists | `intent=Inventory scope="file:///src/**"` |
| Locate | Know concept, need location | `intent=Locate keywords="authentication flow"` |
| Inspect | Know location, need details | `intent=Inspect scope="file:///src/Auth/**"` |
| Explain | Want synthesized answer | `intent=Explain keywords="How does JWT refresh work?"` |

```bash
mcporter call repoql.explore intent=Inventory tokenBudget=2000 scope="file:///src/**"
mcporter call repoql.explore intent=Locate tokenBudget=1500 keywords="payment processing"
mcporter call repoql.explore intent=Explain tokenBudget=2000 keywords="What is the auth flow?"
```

### query - SQL on the codebase

DuckDB SQL with pre-built views:

```bash
# Core views: Files, Functions, Types, Annotations
mcporter call repoql.query sql="SELECT name, signature FROM Functions WHERE declaring_type = 'AuthService'"
mcporter call repoql.query sql="SELECT uri, error_count FROM Files WHERE error_count > 0"
mcporter call repoql.query sql="SELECT * FROM search('config validation', k := 10)"
```

### read - Fetch known content

Token-budget-aware file reading:

```bash
mcporter call repoql.read uri="file:///src/Auth.cs" tokenBudget=3000
mcporter call repoql.read uri="file:///src/Auth.cs#symbol=ValidateToken" tokenBudget=1500
mcporter call repoql.read uri="file:///docs/API.md // What auth methods exist?" tokenBudget=2000
```

### import - Add external repos

```bash
mcporter call repoql.import uri="github://owner/repo@main"
mcporter call repoql.query sql="SELECT * FROM Filesystems"  # List imports
```

## Token Budgets

- **Inventory:** 800-2000 (broad overview)
- **Locate:** 1000-2000 (ranked results with snippets)
- **Inspect:** 2000-5000 (detailed structure)
- **Explain:** 1000-3000 (prose synthesis)

Budget = how many tokens you want to spend seeing the answer. Higher = richer detail.

## Patterns

**Boost/Penalize results:**
```bash
mcporter call repoql.explore intent=Locate keywords="error handling" boost="(?i)exception|error" penalize="(?i)test|mock"
```

**Scope to paths:**
```bash
mcporter call repoql.explore intent=Inventory scope="file:///src/services/**/*.cs"
```

## Notes

- RepoQL indexes the current working directory on startup
- Docs are queryable at `repoql-docs:///`
- Results auto-summarize if they exceed token budget
