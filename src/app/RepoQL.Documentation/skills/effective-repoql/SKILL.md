---
description: "Effective use of RepoQL — the workflow, the techniques, and the wild magic. Use when you want to get more out of RepoQL or need to understand what's possible."
tags: [skill, effective-repoql, workflow, techniques]
audience: [LLMs]
categories: ["Skill[100%]"]
---

# Effective RepoQL

RepoQL gives you extra senses. You can feel the shape of a thousand files without opening one. You can see relationships that grep will never find. You can hear relevance ranked by meaning, not literal text. And you can reach precisely — a single method body, a line range, a glob across every file in the codebase.

The index is wild magic — composable, responsive to intent, and forgiving. A bad query costs 1500 tokens. A good one saves 50k. Your instincts are probably right. Try them.

---

## The Workflow: Orient → Discover → Shape → Target

### Orient

See the landscape before committing tokens to read anything.

```
read("file:///src/** => tree: headlines", 5000)
```

This gives you the directory structure with one-line summaries — the map. It also surfaces documentation, READMEs, and design docs that keyword-based search misses.

### Discover

Explore to learn the vocabulary — the real class names, patterns, and terms-of-art.

```
explore(keywords="authentication middleware", tokenBudget=1500)
```

Your first explore teaches you what to ask for next. Without it, you're guessing names and grepping blind.

### Shape

Read at the cheapest level that answers your question. Structure before content.

```
read("file:///src/Auth/**/*.cs => structure", 3000)
```

Structure shows every method signature without bodies. You see the shape of an entire subsystem for the cost of reading one file.

### Target

Read specific code with precision addressing.

```
read("file:///src/Auth/TokenService.cs#symbol=ValidateToken", 2000)
```

Symbol fragments, line ranges, multi-URI reads — pay only for the slice you need.

---

## Technique Index

### Finding Things

| Technique | When | Example |
|-----------|------|---------|
| Broad explore | Don't know where things are | `explore(keywords="cache invalidation", tokenBudget=1500)` |
| Scoped explore | Know the area, not the specifics | `explore(uriGlob="file:///src/data/**", keywords="refresh plan", tokenBudget=2000)` |
| Tree headlines | Need the map of a directory | `read("file:///src/pipeline/** => tree: headlines", 3000)` |
| Semantic find | Search within known files | `read("file:///src/Auth/** => find: token refresh", 2000)` |
| Grep within scope | Need literal text matches | `read("file:///src/** => grep: ConnectionString", 2000)` |
| Regex within scope | Need pattern matches | `read("file:///src/**/*.cs => regex: class\\s+\\w+Handler", 2000)` |
| Symbol glob | Find implementations of a pattern | `read("file:///src/**/*.cs#symbol=*Service.Execute* => structure", 3000)` |
| Similar | Find related code, tests, or docs | `read("file:///src/tests/** => similar: file:///src/Auth.cs", 2000)` |

**Deeper:** `help:///tools/explore.md`, `help:///tools/read.md`, `help:///tools/uri-patterns.md`

### Understanding Things

| Technique | When | Example |
|-----------|------|---------|
| Structure view | See the API shape | `read("file:///src/Auth/**/*.cs => structure", 3000)` |
| Symbol read | Read one method body | `read("file:///path.cs#symbol=ClassName.MethodName", 2000)` |
| Multi-symbol read | Read several methods at once | `read("file:///a.cs#symbol=Foo;file:///b.cs#symbol=Bar", 3000)` |
| Question modifier | Ask code a question | `read("file:///src/Auth/** => question: how does token validation work?", 3000)` |
| Scoped explain | Synthesized understanding | `explain(question="How does X work?", uriGlob="file:///src/area/**", tokenBudget=5000)` |
| Blame | Who wrote this and when | `read("file:///path.cs#symbol=Method => blame", 1500)` |
| History | How did this area evolve | `read("file:///src/Auth/** => history: token refresh", 2000)` |

**Deeper:** `help:///tools/read.md` (modifiers section)

### Computing Over the Graph

| Technique | When | Example |
|-----------|------|---------|
| View queries | Standard aggregations | `SELECT lang, COUNT(*), SUM(lines) FROM Files GROUP BY lang` |
| Search function | Semantic + lexical search | `SELECT uri, score FROM search('auth', k := 10)` |
| Symbol search | Find by symbol name | `SELECT symbol, uri FROM search_symbol('ValidateToken')` |
| Edge traversal | What calls/depends on what | `SELECT * FROM edge WHERE source_node_id = '...' AND type = 'CALLS'` |
| Git functions | History and change analysis | `SELECT * FROM git_hotspots(since := '3 months')` |
| Parse function | Inline CSV/JSON as tables | `SELECT * FROM parse(read_text('file:///data.csv'))` |
| MCP functions | Call external tools from SQL | `SELECT * FROM mcp_tools()` for discovery |
| Snippet | Code preview with context | `SELECT * FROM snippet('file:///path#line=42', 3)` |

**Deeper:** `help:///schema/core.md`, `help:///schema/views/`, `help:///schema/functions/`, `help:///patterns/`

### Advanced Patterns

| Pattern | What it does | Where to learn |
|---------|-------------|----------------|
| Aggregation | GROUP BY, HAVING, window functions | `help:///patterns/aggregation.md` |
| Graph traversal | Recursive CTEs over edge table | `help:///patterns/graph-traversal.md` |
| Data reshaping | PIVOT, UNPIVOT, list comprehensions | `help:///patterns/data-reshaping.md` |
| Text analysis | Regex extraction, tokenization | `help:///patterns/text-analysis.md` |
| Temporal joins | Git history + current state | `help:///patterns/temporal-joins.md` |
| Data analysis | Statistical functions, distributions | `help:///patterns/data-analysis.md` |
| Window functions | Running totals, rankings, moving averages | `help:///patterns/window-functions.md` |

---

## Creative Composition

The power is in combinations that no single feature provides:

- **Explore → structure → symbol reads:** Discover the landscape, see the shapes, then read only the bodies that matter
- **Search + LATERAL snippet:** Find by concept, preview the matching code inline
- **Tree + similar:** See a directory, then find code similar to specific files in it
- **Query + parse:** Join repository data with inline CSV/JSON for ad-hoc lookups
- **Glob symbols across files:** `file:///src/**/*.cs#symbol=*Handler.CanHandle => structure` — every implementation of a pattern, signatures only
- **Multi-URI reads:** `file:///a.cs#symbol=Foo;file:///b.cs#symbol=Bar;file:///c.cs#symbol=Baz` — three methods, three files, one call
- **Execute for pipelines:** query → transform in JS → render diagram → write file

A bad composition costs 1500 tokens. A good one achieves things you didn't know were possible.

---

## Representation Levels

Choose the cheapest that answers your question:

| Level | Cost | Use when |
|-------|------|----------|
| headline | ~2 tok/file | Scanning — "is this relevant?" |
| structure | ~50 tok/file | Understanding — "what does this expose?" |
| content | ~200+ tok/file | Implementation — "how does this work?" |

Most discovery questions are answered by structure, not source code.

---

## What Exists in help://

The full documentation is queryable:

```
explore(uriGlob="help://**", keywords="your topic", tokenBudget=1500)
```

| Area | What's there |
|------|-------------|
| `help:///tools/` | Tool-specific guides (explore, read, query, execute, import) |
| `help:///schema/` | Views, scalar functions, table functions |
| `help:///patterns/` | SQL patterns (aggregation, graph traversal, data reshaping, etc.) |
| `help:///formats/` | Per-language documentation (C#, Python, Go, Ruby, Rust, etc.) |
| `help:///commands/` | Per-command documentation |
| `help:///operations/` | Operational guides (cloud auth, embedding cache, timeouts) |

---

- [ ] Explore to understand what exists and find things
- [ ] Read with structure, symbols, and scoped modifiers — not whole files
- [ ] Query when you need to count, list, or traverse relationships
- [ ] Explain scoped to specific directories for synthesized understanding
- [ ] Execute to compose capabilities that don't exist as standalone tools
