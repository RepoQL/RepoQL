# Tool Selection Guide

> Choosing the right tool for the job.

## Decision Tree

```
What do you need?
│
├─ Find something (don't know where)
│  └─ explore
│
├─ Read something (know the URI)
│  └─ read
│
├─ Count, list, or traverse
│  └─ query
│
├─ Understand how something works
│  └─ explore (Explain) or read + query combination
│
├─ Find relationships (what calls X, what depends on Y)
│  └─ query (edge table)
│
├─ Analyze data files (CSV, JSON, Excel)
│  └─ query with parse() / xlsx()
│
├─ Check git history
│  └─ query with git_*() functions
│
├─ Use external MCP tools
│  └─ query with mcp__*() functions
│
├─ Bring in external sources (repos, data, reports)
│  └─ import (to add sources) + explore/query/read (to query)
│
└─ Make changes
   └─ Edit / Write / Bash
```

## Quick Reference

| Need | Tool | Example |
|------|------|---------|
| Semantic search | `explore` | `explore(intent="Locate", keywords="authentication")` |
| Specific file | `read` | `read("file:///src/Auth.cs", 2000)` |
| List all X | `query` | `SELECT * FROM Functions WHERE kind = 'endpoint'` |
| What depends on X | `query` | `SELECT source_uri FROM edge WHERE target_uri = '...'` |
| How does X work | `explore` | `explore(intent="Explain", keywords="How does auth work?")` |
| Git history | `query` | `SELECT * FROM git_hotspots(since := '3 months')` |
| Parse data | `query` | `SELECT * FROM parse(read_text('file:///data.csv'))` |
| External docs | `query` | `SELECT * FROM mcp__context7__query_docs(...)` |
| Import source | `import` | `import("github://owner/repo@main")` |
| Query imports | `explore` | After import, same tools work across all sources |

## Anti-Patterns

| Don't | Do Instead |
|-------|------------|
| `explore` for "list all X" | `query` with aggregation |
| `query` for "how does X work" | `explore` with Explain |
| `read` entire large file | `read` with symbol or line fragment |
| `Bash("grep ...")` | `query` with search() or Grep tool |
| Multiple `explore` for same area | One `explore` with appropriate budget |

## Composition

Tools are meant to be composed:

```
explore → find the area
query → get specific details
read → examine closely
Edit → make changes
Bash → verify
```

The output of one tool informs the input to the next.
