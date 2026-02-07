---
name: code-intelligence
description: Code structure discovery using RepoQL. Use when answering questions about where code is, what exists, how things are organized, or finding implementations. Triggers on "where is", "what files", "show me the structure", "find the implementation".
---

# Code Intelligence

RepoQL indexes repositories into a queryable graph. You can answer structural questions without reading files.

## The Core Insight

Traditional approach: grep/find files, read them, synthesize. Token-expensive, often incomplete.

RepoQL approach: query pre-indexed structure, get precise URIs, read only what matters.

## Capsule: ExploreFirst

**Invariant**
Always use explore before reading. The graph knows what exists; you don't.

**Example**
User asks: "Where is authentication handled?"
Wrong: `read("file:///src/**/*Auth*.cs", 5000)` - wastes tokens on guesses
Right: `explore(intent=Locate, keywords="authentication", tokenBudget=1500)` - finds actual locations
//BOUNDARY: Explore discovers. Read fetches. Never read blind.

**Depth**
- X-ray summaries (headline, summary, structure) are pre-computed on every file
- Semantic search finds conceptually related code, not just name matches
- The graph contains relationships (calls, implements, extends)
- After explore returns URIs, read fetches just those symbols

## Capsule: IntentMatching

**Invariant**
Match your explore intent to your knowledge state.

**Example**
| You know | Use Intent | Why |
|----------|------------|-----|
| Nothing | Inventory | Survey what exists |
| Concept | Locate | Find where it lives |
| Location | Inspect | Get detailed context |
| Target | Explain | LLM synthesis |
//BOUNDARY: Wrong intent wastes tokens or misses context.

**Depth**
- Inventory: breadth over depth, see into files through headlines
- Locate: balanced, enough context to decide what to read next
- Inspect: depth on specific targets, code snippets with line numbers
- Explain: massive compression, LLM reads and synthesizes (50k -> 1k)
- Workflow: Inventory -> Locate -> Inspect -> Explain (accumulate, don't skip)

## Quick Reference

**Find where something is:**
```
explore(intent=Locate, keywords="caching layer", tokenBudget=1500)
```

**See what's in a directory:**
```
explore(intent=Inventory, uriGlob="file:///src/Services/**", tokenBudget=1000)
```

**Read a specific symbol:**
```
read("file:///src/Auth.cs#symbol=ValidateToken", 2000)
```

**Query for aggregations:**
```sql
query("SELECT lang, COUNT(*) FROM Files GROUP BY lang")
```

## When to Use Query Instead

| Need | Tool |
|------|------|
| Discovery | explore |
| Content | read |
| Aggregation/computation | query |

Use query for: counting, grouping, pattern extraction, graph traversal, cross-file analysis.

## References

See `references/explore-patterns.md` for detailed intent examples.
See `references/query-patterns.md` for SQL patterns.
