---
description: Plan for abbreviation-based query expansion with RRF fusion to improve lexical recall
tags: [explore, search, query-expansion, abbreviations, rrf]
audience: { human: 30, agent: 70 }
purpose: { plan: 95, design: 5 }
---

# Plan: Query Expansion

Implements: [Intelligent Context Design](../../designs/future/intelligent-context.md) — Query Expansion section

## Scope

**Covers:**
- `IQueryExpander` interface and `AbbreviationExpander` implementation
- Abbreviation dictionary as embedded JSON resource
- Expansion decision logic (skip conditions)
- Dual search execution (original + expanded)
- RRF fusion of result lists
- Optional expansion annotation in status footer

**Does not cover:**
- Casing variant generation (deferred — abbreviation expansion alone covers the primary gap)
- LLM-based expansion (design considered and rejected)
- Changes to search scoring weights or BM25 parameters
- SimHash dedup, clustering, or allocation changes (Plans: 03, 04, 05)

## Enables

Once Query Expansion exists:
- **"auth" finds "authentication"** — the most common source of missed lexical matches is eliminated
- **Zero-result queries decrease** — abbreviations that match nothing in BM25 get expanded to full terms
- **Downstream phases benefit** — more diverse initial results improve clustering (Plan 04) and give the allocator (Plan 05) a richer candidate pool
- **Expansion is transparent** — agents see which expansions were applied in the footer

## Prerequisites

- `ExploreOrchestrator` calls `ExploreSearchEngine.SearchAsync` with keywords
- `search()` SQL macro accepts keyword string and returns scored results
- Existing RRF pattern: none required — RRF is implemented fresh here

## North Star

An agent's natural vocabulary works. "auth config" finds `AuthenticationService` and `ConfigurationProvider` without the agent needing to know naming conventions. No latency perceptible to the user.

## Done Criteria

### IQueryExpander

- The `IQueryExpander` interface shall define `ExpandedQuery Expand(string keywords)`
- `ExpandedQuery` shall include: `Original` (string), `Expanded` (string), `Expansions` (list of term → variants), `WasExpanded` (bool)

### AbbreviationExpander

- The AbbreviationExpander shall load abbreviations from an embedded JSON resource at construction time
- The dictionary shall contain at minimum these entries: `auth`, `config`, `db`, `repo`, `impl`, `svc`, `ctx`, `req`, `res`, `err`, `msg`, `init`, `param`, `util`, `doc`, `env`, `spec`
- The dictionary shall NOT include ambiguous short words where the abbreviation maps to unrelated domains (e.g., `val` maps to both "value" and "validate" — omit rather than guess)
- The expander shall tokenize keywords by splitting on whitespace
- The expander shall look up each lowercased token in the dictionary
- The expander shall combine original terms and all expansion variants into the `Expanded` string, space-separated, deduplicated

### Skip Conditions

- When keywords start with `"` (quoted), the expander shall return `WasExpanded = false` with `Expanded = Original`
- When keywords match `[A-Z][a-z]+[A-Z]` (CamelCase identifier), skip expansion
- When keywords contain `.` or `/` (qualified name or path), skip expansion
- When keywords length exceeds 60 characters, skip expansion
- When no dictionary entries match any token, return `WasExpanded = false` with `Expanded = Original`

### Dual Search

- The `ExploreSearchEngine` shall gain a private method (e.g., `SearchWithExpansionAsync`) that accepts an `ExpandedQuery` and handles dual search + RRF internally
- The public `SearchAsync` signature shall be unchanged; the orchestrator passes the `ExpandedQuery` via a new parameter or the existing `SearchParameters`
- When `WasExpanded` is true, the engine shall execute two searches: one with `Original`, one with `Expanded`
- When `WasExpanded` is false, the engine shall execute one search with `Original`
- The expanded search result scores shall be multiplied by 0.6 before RRF ranking

### RRF Fusion

- The fusion shall compute `RRF_score(d) = sum(1 / (60 + rank_i(d)))` across both result lists
- Documents appearing in only one list shall receive one RRF term
- The fused list shall be sorted by RRF score descending
- The fused list shall be truncated to the original requested limit

### Footer Annotation

- When `WasExpanded` is true and expanded results contributed > 50% of the top-10 results, the status footer shall include an expansion note
  - Format: `expanded: term→expansion` for each expanded term
- When expansion did not meaningfully contribute, no annotation

### Registration

- `IQueryExpander` shall be registered in DI as a singleton
- `ExploreOrchestrator` shall accept `IQueryExpander?` (nullable) and skip expansion when null

## Constraints

- **No LLM dependency** — expansion is pure dictionary lookup; must work offline
- **No search scoring changes** — existing BM25/semantic/fuzzy weights are unchanged; expansion only affects which terms are searched
- **Embedded resource** — dictionary versioned with code, not stored in database
- **Total expansion terms capped at 20** — prevent excessive search cost from highly-expanded queries

## References

- [Intelligent Context Design](../../designs/future/intelligent-context.md) — Query Expansion section, dictionary entries, skip conditions, RRF formula
- [Query Expansion Flow](../../flows/future/intelligent-context/query-expansion.md) — full stage-by-stage flow
- `src/RepoQL.Explore/ExploreOrchestrator.cs` — orchestrator to modify
- `src/RepoQL.Explore/Search/IExploreSearchEngine.cs` — search engine interface
- `src/RepoQL.Explore/Search/ExploreSearchEngine.cs` — search engine implementation

## Error Policy

Expansion failures must not affect search:
1. If dictionary fails to load, log warning at startup and skip expansion for all queries
2. If expansion produces an empty string, use original query
3. If either search call fails, use whichever succeeded; if both fail, propagate the error
