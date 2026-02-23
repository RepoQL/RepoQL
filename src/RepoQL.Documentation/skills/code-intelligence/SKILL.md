---
description: "Code structure discovery using RepoQL. Answer structural questions without reading files."
tags: ["skill", "code-intelligence", "explore", "read", "discovery", "structure"]
audience: ["LLMs"]
categories: ["Skill[100%]"]
---

# Code Intelligence

RepoQL indexes repositories into a queryable graph. Answer structural questions without reading files.

## The Core Workflow

```
explore → read → query
```

1. **explore** discovers what exists and where it lives
2. **read** fetches specific content with budget control
3. **query** computes aggregations, traverses the graph, or analyzes patterns

Never read blind. The graph knows what exists; you don't.

---

## Capsule: ExploreFirst

**Invariant**
Always use explore before reading. The graph knows what exists; you don't.

**Example**
User asks: "Where is authentication handled?"
Wrong: `read("file:///src/**/*Auth*.cs", 5000)` — wastes tokens on guesses
Right: `explore(intent="Locate", keywords="authentication", tokenBudget=1500)` — finds actual locations
//BOUNDARY: Explore discovers. Read fetches. Never read blind.

**Depth**
- X-ray summaries (headline, summary, structure) are pre-computed on every file
- Semantic search finds conceptually related code, not just name matches
- The graph contains relationships (calls, implements, extends)
- After explore returns URIs, read fetches just those symbols

---

## Capsule: IntentMatching

**Invariant**
Match your explore intent to your knowledge state.

| You know | Use Intent | Why | Budget |
|----------|------------|-----|--------|
| Nothing | Inventory | Survey what exists | 800-2000 |
| Concept | Locate | Find where it lives | 1000-2000 |
| Location | Inspect | Get detailed context | 2000-5000 |
| Target | Explain | LLM synthesis | 1500-3000 |

//BOUNDARY: Wrong intent wastes tokens or misses context.

**Depth**
- **Inventory**: breadth over depth, headlines for every file in scope
- **Locate**: balanced — enough context to decide what to read next
- **Inspect**: depth on specific targets, code snippets with line numbers
- **Explain**: LLM reads wide (50k tokens) and synthesizes (requires OPENROUTER_API_KEY)
- Workflow: Inventory → Locate → Inspect → Explain (accumulate, don't skip)

Full intent reference: `help:///repoql/tools/explore/using-xray.md`

---

## Capsule: FragmentAddressing

**Invariant**
Target precisely what you need. Don't read whole files when a fragment answers the question.

**Example**
```
read("file:///src/Auth.cs#symbol=ValidateToken", 2000)
read("file:///src/Config.cs#line=42,60", 1000)
```
//BOUNDARY: Fragments reduce token cost and increase relevance.

**Depth**
- `#symbol=Name` — read a specific class, method, or function
- `#line=Start,End` — read a line range (1-based, inclusive)
- `#char=Start,End` — read a byte range (0-based, end exclusive)
- Combine with scope: `read("file:///src/Services/**#symbol=*Service", 3000)`

---

## Capsule: XRaySummaries

**Invariant**
Use pre-computed summaries before reading file content. Headlines answer "what is this?", structure answers "what's in this?"

**Example**
```
explore(intent="Inventory", uriGlob="file:///src/Services/**", tokenBudget=1000)
```
Returns headlines and structure for every file — no need to read them.
//BOUNDARY: Don't pay for content when summaries answer the question.

**Depth**
- **headline** — one-line summary (~50-100 chars), what the file is about
- **summary** — brief overview (~200-500 chars), key concepts
- **structure** — detailed TOC with signatures, enough to navigate
- Pre-computed at index time; available in `Files` view and explore results
- Full x-ray reference: `help:///repoql/tools/explore/using-xray.md`

---

## Quick Reference

**Find where something is:**
```
explore(intent="Locate", keywords="caching layer", tokenBudget=1500)
```

**See what's in a directory:**
```
explore(intent="Inventory", uriGlob="file:///src/Services/**", tokenBudget=1000)
```

**Read a specific symbol:**
```
read("file:///src/Auth.cs#symbol=ValidateToken", 2000)
```

**Read with modifiers:**
```
read("file:///src/** => tree: headlines", 3000)
read("file:///src/Foo.cs => history", 2000)
read("file:///src/Foo.cs#line=42 => blame", 1000)
```

**Query for aggregations:**
```sql
query("SELECT lang, COUNT(*) FROM Files GROUP BY lang")
```

---

## When to Use Which Tool

| Need | Tool | Why |
|------|------|-----|
| "What exists? Where is X?" | explore | Broad search with budget allocation |
| "Show me this file/symbol" | read | Fetch known content precisely |
| "How many? Which ones? What pattern?" | query | Computation, aggregation, graph traversal |
| "How has this changed?" | read with `=> history` | Git-aware timeline |
| "Who changed this?" | read with `=> blame` | Line-level attribution |

---

## Cross-References

- **Explore intents and parameters**: `help:///repoql/tools/explore/using-xray.md`
- **Read modifiers (tree, history, blame, etc.)**: `help:///repoql/tools/read/read-command.md`
- **SQL views (Files, Types, Functions, Annotations)**: `help:///repoql/tools/query/views/`
- **SQL functions (search, snippet, etc.)**: `help:///repoql/tools/query/sql-reference.md`
- **Graph schema (5 frozen tables)**: `help:///repoql/tools/query/schema.md`

---

*Explore first. Read what matters. Query for computation.*
