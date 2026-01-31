---
description: Vision for RepoQL web UI - what operators and developers should be able to do
tags: [ui, observability, testing, north-star]
audience: { human: 70, agent: 30 }
purpose: { north-star: 100 }
---

# RepoQL Web UI: What Great Looks Like

> See everything RepoQL knows, test everything it does, diagnose everything that breaks.

A developer opens a browser and instantly sees: green, indexing complete, 4,231 files, embeddings ready. They paste a file URI and see exactly what RepoQL extracted—every node, every edge, every annotation. They click an edge and land on the connected file. They type a search query and see not just results, but *why* each result ranked where it did. When something's wrong, they don't dig through logs—the UI surfaces the stuck file, the slow parser, the failed embedding. They run a SQL query and see results in milliseconds. They test how `read` handles a complex URI with fragments and modifiers. They see what external repos are imported and their status. The UI is their window into RepoQL's mind.

---

## Health at a Glance

- An operator should be able to see if RepoQL is healthy with one glance
- An operator should be able to distinguish "idle" from "working" from "broken"
- An operator should be able to see what's wrong without clicking anything
- An operator should be able to see how many files are indexed and how many are pending

---

## Diagnosing Problems

- An operator should be able to see which file is stuck in the queue
- An operator should be able to see which pipeline stage is the bottleneck
- An operator should be able to see per-parser timing to identify slow processors
- An operator should be able to see embedding generation status and latency
- An operator should be able to see errors without digging through logs

```
⚠ Stuck: src/Legacy/HugeFile.cs — Parsing — 47 seconds
  Parser: CSharpProcessor
  Last progress: "Extracting symbols"
```

---

## Testing Queries

- A developer should be able to run arbitrary SQL and see results immediately
- A developer should be able to see errors inline, not in a separate panel
- A developer should be able to discover what macros and views are available
- A developer should be able to see example queries for common operations

---

## Testing Search

- A developer should be able to test the explore tool with all its parameters
- A developer should be able to see why results ranked the way they did
- A developer should be able to see the score breakdown: BM25, fuzzy, semantic
- A developer should be able to see if a file was excluded and why
- A developer should be able to test symbol search separately from document search
- A developer should be able to compare results across different parameter choices

```
Result: src/Auth/TokenValidator.cs
  Score: 0.847
  ├─ BM25: 0.32 (matched "token", "validate")
  ├─ Fuzzy: 0.15
  └─ Semantic: 0.91 (embedding similarity)
  Boosted by: Auth.* pattern
```

---

## Testing Read

- A developer should be able to test the read tool with any URI
- A developer should be able to test fragment syntax: `#line=`, `#symbol=`, `#char=`
- A developer should be able to test modifiers: tree, history, blame, lint
- A developer should be able to see progressive disclosure at different token budgets
- A developer should be able to see what content is returned at each budget level

---

## Inspecting Files

- A developer should be able to see everything RepoQL knows about a file
- A developer should be able to see the X-ray summaries: headline, summary, structure
- A developer should be able to see all nodes extracted from the file
- A developer should be able to see all edges: what it links to, what links to it
- A developer should be able to see all annotations: errors, warnings, lint
- A developer should be able to see if the file has embeddings and when they were generated

```
file:///src/Auth/AuthService.cs
────────────────────────────────
Media: text/plain;kind=code.csharp
Tokens: ~2.1k | Embeddings: ✓

Nodes (12)
├─ cs_class: AuthService [line 15-342]
├─ cs_method: ValidateToken [line 47-89]
└─ ...

Edges (8)
├─ → CALLS: UserRepository.GetById
├─ ← CALLED_BY: LoginController.Authenticate
└─ ...

Annotations (2)
├─ ⚠ CA1822: Consider static [line 112]
└─ ...
```

---

## Exploring Relationships

- A developer should be able to traverse edges interactively
- A developer should be able to ask "what calls this?" and get an answer
- A developer should be able to ask "what does this import?" and get an answer
- A developer should be able to navigate from any node to connected nodes
- A developer should be able to find similar files based on embeddings

---

## Browsing Annotations

- An operator should be able to see all errors and warnings across the repository
- An operator should be able to filter by severity, rule, or file pattern
- An operator should be able to see which files have the most problems
- An operator should be able to see annotation counts by category

---

## Managing Imports

- An operator should be able to see what external repositories are imported
- An operator should be able to see the status of each import
- An operator should be able to add a new import
- An operator should be able to remove an import
- An operator should be able to search across specific imports

---

## Git Integration

- A developer should be able to see blame for any file
- A developer should be able to see which files change most often (hotspots)
- A developer should be able to see recent commits affecting a path
- A developer should be able to find commits related to a semantic query

---

## What Great Looks Like

| Declaration | Why It Matters |
|-------------|----------------|
| An operator should be able to see health with one glance | No digging to know if it's working |
| A developer should be able to see why search ranked results | Debug search without guessing |
| A developer should be able to see everything about a file | Verify parsing is correct |
| A developer should be able to traverse edges interactively | Understand relationships |
| An operator should be able to see what's stuck and why | Diagnose without logs |

---

## Anti-Patterns

| Don't | Declaration Form |
|-------|------------------|
| Show status that requires interpretation | An operator should be able to see health with one glance |
| Show search results without scores | A developer should be able to see why results ranked |
| Show nodes without edges | A developer should be able to explore relationships |
| Require SQL to see file details | A developer should be able to inspect any file directly |
| Hide errors in logs | An operator should be able to see errors in the UI |
| Show "loading" without progress | An operator should be able to see what's happening |

---

*A developer should be able to see what RepoQL sees, test what RepoQL does, and fix what RepoQL breaks—without leaving the browser.*
