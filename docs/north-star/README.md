# North Star: What RepoQL Enables

> The interaction modalities that RepoQL should make possible.

This document describes the **broad categories of capability** that RepoQL enables. Not an exhaustive list of queries, but the kinds of things agents should be able to do by composing RepoQL tools with each other and with external tools.

---

## Core Tools

| Tool | Purpose |
|------|---------|
| `xray` | Find and understand content by semantic query |
| `query` | SQL over the knowledge graph - aggregation, traversal, joins |
| `read` | Fetch specific content with budget-aware representation |
| `import` | Bring external data sources into the knowledge graph |

These compose with standard agent tools (Edit, Write, Bash) and external MCP servers.

---

## Interaction Modalities

### 1. Search & Navigate

**Finding things when you don't know where they are.**

- Semantic search across all content types (code, docs, config, schemas)
- Symbol search when you know a name but not its location
- Scoped search within specific areas of the codebase
- Exploration when you don't know what you're looking for yet

*The synergies (PPR, MMR, clustering, focused snippets) make this richer—returning not just matches but related context, organized for understanding.*

### 2. Query & Aggregate

**Counting, listing, and traversing the knowledge graph.**

- Inventory queries: "all endpoints", "all models", "all TODOs"
- Graph traversal: "what depends on X", "what does Y call"
- Aggregation: "files by language", "issues by severity"
- Pattern matching: "all files matching glob", "all symbols matching pattern"

*SQL gives precise control. The graph model (nodes, edges, spans, annotations) makes structural queries possible.*

### 3. Understand & Synthesize

**Getting explanations that combine multiple sources.**

- "How does X work?" → synthesized from code + docs + config
- "Explain this code" → with context from callers, callees, tests
- "What is the architecture?" → derived from structure and relationships

*The xray Understand intent does synthesis. Budget allocation ensures the right mix of sources.*

### 4. Trace & Correlate

**Linking related content across boundaries.**

| Correlation | Example |
|-------------|---------|
| Code ↔ Documentation | Find docs that describe this code, code that implements these docs |
| Code ↔ Tests | Find tests for this code, code tested by these tests |
| Code ↔ Configuration | Find config that affects this code, code that uses this config |
| Changes ↔ Changes | Files that change together (co-change analysis) |
| Errors ↔ Causes | Combine search with git history to find when behavior changed |
| Symbols ↔ Usages | Find symbols only used by tests, deprecated APIs still in use |

*The edge table captures explicit relationships. Git history adds temporal correlation. Combining them answers "why" questions.*

### 5. Analyze & Export

**Data analysis and pipeline composition.**

- Parse data files in the repo (CSV, JSON, YAML, Excel, etc.)
- Join repository content with external data sources
- Chain MCP servers: query external API → join with repo → export results
- Aggregate for reporting: complexity metrics, dependency analysis, coverage gaps

*RepoQL as a data hub—everything queryable via SQL, composable with external sources.*

### 6. History & Evolution

**Understanding how the codebase changed over time.**

- What changed recently? What changed in this area?
- Who changed this and why? (blame + commit messages)
- What files change together? (co-change patterns)
- Where is the churn? (hotspots that might need attention)
- What's related to this change? (files often modified together)

*Git functions surface history. Joining with current state answers "why is it like this?"*

### 7. External Sources

**Bringing outside data into the knowledge graph.**

- Import external repositories (GitHub, etc.) for cross-repo analysis
- Compare patterns across codebases: "how does project A handle auth vs project B?"
- Unified search across a microservices ecosystem or monorepo fragments
- Future: monitoring data, SARIF reports, external datasets, and more

*The `import` tool brings external sources into the graph. Once imported, all other modalities work seamlessly across them.*

### 8. Modify & Verify

**Making changes with confidence.**

- Understand before changing (search → read → understand)
- Find all affected locations (graph traversal + search)
- Find patterns to follow (similar code as templates)
- Verify changes work (build, test, lint via Bash)

*RepoQL informs the change. Standard tools execute it.*

---

## Composition Patterns

### The Funnel: Broad → Narrow → Deep

```
xray (explore)  →  query (filter)  →  read (details)
"what's here?"     "which ones?"       "show me"
```

### The Expand: Specific → Related → Context

```
read (known)  →  query (edges)  →  xray (understand)
"this file"       "what's connected?"   "how does it all work?"
```

### The Correlate: Multiple Sources → Join → Insight

```
search (concept)  +  git_history (time)  →  "when did this break?"
query (symbols)   +  query (edges)       →  "what's unused?"
mcp (external)    +  query (repo)        →  "how does our code use this API?"
```

### The Pipeline: Query → Transform → Export

```
query (data)  →  aggregate  →  export/visualize
"all endpoints"   "by module"   "for documentation"
```

---

## Example Modality Combinations

| Goal | Modalities Combined |
|------|---------------------|
| Find cause of regression | Search (concept) + History (when changed) + Understand (why) |
| Assess technical debt | Aggregate (issues) + History (hotspots) + Correlate (overlap) |
| Prepare for refactor | Query (dependencies) + Trace (usages) + Understand (patterns) |
| Onboard to codebase | Navigate (structure) + Understand (architecture) + Explore (key areas) |
| Add new feature | Search (similar) + Understand (patterns) + Modify (implement) |
| Review PR | History (changes) + Correlate (related files) + Understand (impact) |
| Document feature | Trace (code↔docs) + Understand (behavior) + Analyze (coverage) |
| Debug issue | Search (error) + Trace (call chain) + History (recent changes) |

---

## What "Great" Looks Like

For each modality, "great" means:

| Modality | Great Result |
|----------|--------------|
| **Search** | Finds relevant content even with imprecise queries; shows related context; no redundancy |
| **Query** | Returns precise answers; traverses graph accurately; aggregates correctly |
| **Understand** | Synthesizes coherent explanation from multiple sources; cites evidence |
| **Trace** | Finds connections across content types; surfaces non-obvious relationships |
| **Analyze** | Handles various data formats; joins sources meaningfully; exports usefully |
| **History** | Surfaces relevant changes; identifies patterns; correlates with current state |
| **External** | Seamlessly queries across imported sources; finds patterns and differences |
| **Modify** | Finds all affected locations; provides patterns to follow; verifies correctness |

---

## The Vision

An agent using RepoQL should be able to:

1. **Orient quickly** in an unfamiliar codebase
2. **Find anything** regardless of where it lives or what it's called
3. **Understand deeply** by seeing code, docs, config, and history together
4. **Trace connections** that cross file and content-type boundaries
5. **Answer aggregate questions** about the codebase as a whole
6. **Combine sources** from the repo, git history, and external tools
7. **Query across sources** - repos, monitoring, analysis reports - as one
8. **Make informed changes** with confidence about impact

RepoQL provides the building blocks. Agents compose them into workflows. The synergies make each building block more powerful—better search results, smarter context selection, efficient token usage.

---

*Maximum insight, minimum tokens—across code, docs, data, history, and beyond.*
