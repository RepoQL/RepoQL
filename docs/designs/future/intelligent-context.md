---
description: Design for transforming explore from flat ranked results to structured, deduplicated, budget-optimal context selection
tags: [explore, search, dedup, clustering, budget, snippets]
audience: { human: 40, agent: 60 }
purpose: { design: 85, flow: 15 }
---

# Intelligent Context Selection — Design

## North Star

Given a query and a token budget, return the most valuable set of results organized for understanding. Not a flat list of matches — structured context where every token earns its place, duplicates are noted not repeated, and the agent sees architecture not just files.

## Context

Explore today returns a flat ranked list. Search finds matches, allocation picks representation levels, output renders them in score order. This works but misses three opportunities:

1. **Chunk scores are discarded** — semantic search identifies which region of a file matched, then throws that information away before rendering. When a file is too large for Rich, it falls to Standard (signatures), losing the actual relevant code.

2. **No vocabulary bridging for lexical search** — "auth" misses "authentication" in BM25. Semantic embeddings partially bridge this, but lexical matches on exact identifiers are lost.

3. **No duplicate awareness** — copy-pasted files, vendored code, and backup copies each consume budget independently. Five copies of the same file spend five times the tokens for one unit of information.

4. **No structural grouping** — results from `src/Auth/` appear interleaved with results from `docs/` and `src/Config/`. The agent reconstructs structure from paths. The tool should present it.

5. **Flat allocation** — one high-scoring file can consume 40% of the budget. No mechanism protects breadth across distinct areas of the codebase.

**Enables flows:** All 5 flows documented in `docs/flows/future/intelligent-context/`

**Informed by:** Ideas `docs/ideas/` (001-MMR, 003-query-expansion, 005-simhash), synergies README

## Constraints

| Constraint | Source | Impact |
|------------|--------|--------|
| Schema frozen — 5 tables, extend via views/macros/UDFs | CLAUDE.md | SimHash is a computed property of artifact content — column addition, not schema drift |
| Single writer through DuckDbDataStore | CLAUDE.md | SimHash computed in pipeline, persisted through existing writer |
| Budget is a contract | Output style | Every phase must track and respect token budget precisely |
| Errors never cascade | CLAUDE.md | Failure in any phase falls through to the next, never blocks results |
| Each phase independently valuable | Flow README | Design must not create hard dependencies between phases |
| Existing allocation is value-based (utility model) | ValueBasedDecisionEngine | New allocation must extend, not replace, the utility model |
| Interactive latency | Existing | Total pipeline < 200ms added overhead |

---

## Components

```
┌──────────────────────────────────────────────────────────────────────────┐
│                        ExploreOrchestrator (modified)                     │
│  Entry point. Coordinates the pipeline. Each stage is a call that can    │
│  be skipped if the component is unavailable.                             │
└──────────────────────────────────────────────────────────────────────────┘
         │                                                          │
    Query│arrives                                              Rendered│output
         │                                                          │
         ▼                                                          │
┌─────────────────┐     ┌─────────────────┐     ┌─────────────────┐ │
│  QueryExpander   │────▶│ExploreSearch    │────▶│ DuplicateDetect │ │
│                  │     │Engine (modified) │     │                 │ │
│ Abbreviation     │     │ Propagates chunk │     │ Pairwise        │ │
│ lookup + casing  │     │ locations to     │     │ Hamming check   │ │
│ variants. RRF    │     │ results. Both    │     │ on simhash      │ │
│ fusion.          │     │ standard + JIT   │     │                 │ │
│                  │     │ paths propagate. │     │                 │ │
└─────────────────┘     └─────────────────┘     └────────┬────────┘ │
                                                          │          │
                                                          ▼          │
                                                ┌─────────────────┐  │
                                                │ ResultClusterer  │  │
                                                │                 │  │
                                                │ Group by path,  │  │
                                                │ type, duplicate │  │
                                                │ relationship    │  │
                                                └────────┬────────┘  │
                                                         │           │
                                                         ▼           │
                                                ┌─────────────────┐  │
                                                │ ValueBased      │  │
                                                │ DecisionEngine  │  │
                                                │ (extended)      │  │
                                                │                 │  │
                                                │ Three-level:    │  │
                                                │ cluster→file→   │  │
                                                │ object. Focused │  │
                                                │ representation. │  │
                                                └────────┬────────┘  │
                                                         │           │
                                                         ▼           │
                                                ┌─────────────────┐  │
                                                │ Snippet PreFetch │──┘
                                                │                 │
                                                │ Fetch snippets  │
                                                │ for Focused     │
                                                │ decisions only  │
                                                │ (async, before  │
                                                │ sync rendering) │
                                                └─────────────────┘


Index-time (separate flow):
┌──────────────────────────────────────────────────────────────────────────┐
│                   SingleFileAnalysisPipeline                             │
│  SimHashAnalyzer runs after parsing. Computes 64-bit fingerprint.       │
│  Stored as artifact.simhash column (UBIGINT).                            │
└──────────────────────────────────────────────────────────────────────────┘
```

**Key principle:** Every new component has a pass-through mode. If QueryExpander has no dictionary, it returns the original query. If DuplicateDetector has no simhash data, it returns results unchanged. If ResultClusterer finds no groups, it wraps each result in a size-1 cluster. The pipeline never blocks on a missing phase.

---

## Contracts

### Existing (reuse, no changes)

| Contract | Purpose |
|----------|---------|
| `IExploreSearchEngine` | Search execution |
| `IAsyncPipeline<IParsedArtifact, Annotation[]>` | SingleFileAnalysis hook |
| `UtilityCalculator` | Utility formula for allocation |
| `NoveltyTracker` | Diminishing returns per-type and per-file |
| `RepresentationFormatter` | Format at each level (extended, not replaced) |

### Modified

#### ExploreResult (extended)

```csharp
public record ExploreResult(
    string Uri,
    int Confidence,
    string? Kind,
    string? Headline,
    string? Structure,
    string? Snippet,
    string? Lang,
    string? SemanticType = null,
    IReadOnlyList<ExploreResult>? ChildObjects = null,
    // --- New fields ---
    int? BestChunkStartLine = null,     // Semantic chunk location
    int? BestChunkEndLine = null,
    string? DuplicateOf = null,         // URI of canonical (null if canonical)
    int? HammingDistance = null          // Distance from canonical
);
```

**Why extend the record**: These fields are nullable and default to null. Existing code that constructs ExploreResult without the new fields continues to compile. No breaking change.

#### SearchResult (extended)

```csharp
public record SearchResult(
    /* ...existing fields... */
    // --- New fields ---
    int? BestChunkStartLine = null,
    int? BestChunkEndLine = null,
    ulong? SimHash = null               // Carried for dedup check
);
```

#### Representation and RepresentationLevel (both extended)

The codebase has two representation enums with identical level names but different roles:

- **`Representation`** (`Representation.cs`) — used by `ExploreTokenEstimator` for rendering cost estimation
- **`RepresentationLevel`** (`OptionValue.cs`) — used by `ValueBasedDecisionEngine` for allocation planning and `OptionValue.GetValue()` for the utility value matrix

Both must gain a `Focused` value at the same position:

```csharp
// Representation.cs — rendering costs
public enum Representation
{
    Minimal, Compact, Standard, Focused, Rich
}

// OptionValue.cs — allocation planning
public enum RepresentationLevel
{
    Minimal, Compact, Standard, Focused, Rich
}
```

**Insertion order matters**: Focused sits between Standard and Rich in the progression. `OptionValue.GetNextLevel()`, `GetLevelProgression()`, and `PickBestFit` must all reflect this ordering in both enums. The `ValueBasedDecisionEngine` uses `RepresentationLevel`; the `RepresentationFormatter` and `ExploreTokenEstimator` use `Representation`. Both must stay in sync.

### New Contracts

#### IQueryExpander

```csharp
public interface IQueryExpander
{
    ExpandedQuery Expand(string keywords);
}

public record ExpandedQuery(
    string Original,
    string Expanded,                     // All terms space-separated
    IReadOnlyList<Expansion> Expansions, // For annotation
    bool WasExpanded                     // False if skipped
);

public record Expansion(
    string Term,
    IReadOnlyList<string> AddedVariants
);
```

**Implementation**: `AbbreviationExpander` — dictionary lookup + casing variants. Dictionary is an embedded resource (not a SQL table) to avoid schema changes and startup dependency on the database.

#### IDuplicateDetector

```csharp
public interface IDuplicateDetector
{
    DuplicateResult Detect(IReadOnlyList<SearchResult> results);
}

public record DuplicateResult(
    IReadOnlyList<AnnotatedResult> Results
);

public record AnnotatedResult(
    SearchResult Result,
    string? DuplicateOf,       // null = canonical
    int? HammingDistance
);
```

**Implementation**: `SimHashDuplicateDetector` — greedy scan in rank order, XOR + popcount, threshold = 3.

#### IResultClusterer

```csharp
public interface IResultClusterer
{
    IReadOnlyList<ResultCluster> Cluster(IReadOnlyList<ExploreResult> results);
}

public record ResultCluster(
    string Label,                        // "src/Auth/ (4 files)"
    ClusterType Type,                    // Directory, Duplicate, ContentType, Ungrouped
    double AggregateScore,               // Max member score
    IReadOnlyList<ExploreResult> Members
);

public enum ClusterType
{
    Directory,
    Duplicate,
    ContentType,
    Ungrouped
}
```

**Implementation**: `PathBasedClusterer` — priority strategy: duplicate groups first, then shared directory prefix, then content type, then ungrouped.

#### SimHashCalculator (indexing)

```csharp
public class SimHashCalculator
{
    public ulong Compute(string content);
    public ulong Compute(IEnumerable<string> tokens);
}
```

Computes 64-bit SimHash from file content tokens. Called during artifact construction (alongside headline, structure, token count). Result stored directly in `artifact.simhash` as native UBIGINT — no parsing, no joins, direct XOR + popcount at query time.

#### ClusterDecision (allocation output)

```csharp
public record ClusterDecision(
    ResultCluster Cluster,
    int AllocatedBudget,
    IReadOnlyList<RenderingDecision> FileDecisions,
    int OmittedFileCount
);
```

---

## Data Flow

### Query-Time Pipeline

```
ExploreQuery { Keywords: "auth config", TokenBudget: 3000, Intent: Locate }
    │
    ▼
QueryExpander.Expand("auth config")
    │  Lookup: auth → [authentication, authorization, oauth]
    │  Lookup: config → [configuration, settings]
    │  Decision: expand (not quoted, not CamelCase)
    │
    → ExpandedQuery { Original: "auth config",
    │                  Expanded: "auth authentication authorization oauth
    │                            config configuration settings" }
    │
    ▼
ExploreSearchEngine.SearchAsync(original) ──────────────────┐
ExploreSearchEngine.SearchAsync(expanded, weight: 0.6) ─────┤
    │                                                         │
    │  RRF fusion: 1/(60+rank_orig) + 1/(60+rank_exp)        │
    │  Attach BestChunkStartLine/EndLine from chunk scores    │
    │    Standard path: from ChunkProximityBooster scores     │
    │    JIT path: from DocumentExpansionCandidate.            │
    │              HighScoringChunks (currently discarded      │
    │              in ConvertJitResults — must be propagated)  │
    │  Attach SimHash from artifact column                    │
    │                                                         │
    → SearchEngineResult { Results: [...with chunk + simhash] }
    │
    ▼
DuplicateDetector.Detect(results)
    │  For each result in score order:
    │    XOR simhash with each canonical, popcount ≤ 3 → duplicate
    │  Result: annotated list with DuplicateOf, HammingDistance
    │
    → AnnotatedResult[] → convert to ExploreResult[]
    │
    ▼
ResultClusterer.Cluster(results)
    │  Strategy A: group duplicates with canonical
    │  Strategy B: group by shared directory prefix (≥ 2 members)
    │  Strategy C: group by content type (docs, config)
    │  Strategy D: ungrouped remainder
    │  Order clusters by max score
    │
    → ResultCluster[] { "src/Auth/ (3 files)", "Documentation", ... }
    │
    ▼
ValueBasedDecisionEngine.AllocateWithClusters(clusters, intent, budget)
    │
    │  Level 1: cluster budgets (proportional to cluster EV)
    │  Level 2: file budgets within cluster (existing utility model)
    │  Level 3: object budgets within file (existing, + Focused level)
    │
    │  Focused selection:
    │    if BestChunkStartLine != null AND EstimateFocused() ≤ budget:
    │        use Focused (chunk in code fence + headline)
    │
    │  Duplicate demotion:
    │    if DuplicateOf != null: EV *= 0.5
    │
    → ClusterDecision[]
    │
    ▼
SnippetPreFetch(clusterDecisions)
    │  Scan decisions for Focused allocations
    │  Batch-fetch all snippets via snippet() macro
    │  Attach snippet content to ExploreResult before rendering
    │
    → ClusterDecision[] (with snippets populated)
    │
    ▼
OutputComposer.ComposeWithClusters(clusterDecisions)
    │  Render cluster headers: ── src/Auth/ (3 files) ──
    │  Render files at allocated level
    │  Render duplicates with annotation
    │  Footer with token count + optional expansion note
    │
    → Final markdown string
```

### Index-Time Pipeline

```
File parsed (Records available)
    │
    ▼
SimHashCalculator.Compute(content)
    │  Extract tokens from file content
    │  Weight: identifiers 1.0, keywords 0.5, structure 0.3
    │  Compute 64-bit SimHash via weighted voting
    │
    → ulong fingerprint (set on Artifact record)
    │
    ▼
IndexingCommitter.CommitAsync()
    │  Artifact written with simhash column
    │
    → Available for query-time via: SELECT simhash FROM artifact
```

### Search SQL Enhancement

SimHash and chunk location must be available to the search engine. The artifact column makes this a trivial projection — no joins needed:

```sql
-- Enhanced search result projection
SELECT
    s.uri,
    s.score,
    -- Chunk location from semantic search
    s.best_chunk_start_line,
    s.best_chunk_end_line,
    -- SimHash from artifact (direct column, no join)
    a.simhash
FROM search_results s
JOIN artifact a ON s.uri = a.uri
```

**Performance**: The artifact table is already joined in the search path. Adding `simhash` to the projection is zero additional cost.

---

## Focused Representation

The critical rendering addition. When a file is too expensive for Rich but we know which region matched, show that region instead of falling back to signatures.

### Token Estimation

```csharp
public static int EstimateFocused(ExploreResult result)
{
    if (result.BestChunkStartLine is null || result.BestChunkEndLine is null)
        return int.MaxValue;  // Cannot use Focused without chunk location

    var headlineTokens = EstimateHeadline(result);
    var uriTokens = EstimateUri(result);
    var lineIndicator = 5;  // "lines 42-68:"
    var chunkLines = result.BestChunkEndLine.Value - result.BestChunkStartLine.Value + 7; // +6 context, +1 inclusive
    var chunkTokens = chunkLines * 10;  // ~10 tokens per code line
    var fenceOverhead = 4;

    return headlineTokens + uriTokens + lineIndicator + chunkTokens + fenceOverhead;
}
```

### Value Matrix Extension

| Intent | Minimal | Compact | Standard | Focused | Rich |
|--------|---------|---------|----------|---------|------|
| Inventory | 0.8 | 0.4 | 0.2 | 0.15 | 0.1 |
| Locate | 0.6 | 0.7 | 0.5 | 0.6 | 0.3 |
| Inspect | 0.4 | 0.6 | 0.8 | 0.85 | 0.7 |
| Explain | 0.5 | 0.6 | 0.6 | 0.7 | 0.5 |

**Focused vs Standard**: For Locate and Inspect, Focused is more valuable than Standard because it contains the actual code that matched, not just signatures. For Inventory, neither is valuable — breadth matters.

**Focused vs Rich**: Focused is more valuable per-token for Inspect because it concentrates on the relevant region. Rich includes the whole file, which may be mostly irrelevant.

### Rendering

```csharp
public static string FormatFocused(ExploreResult result, bool showConfidence, string? parentUri)
{
    // URI + headline line (same as Compact)
    // "lines {start}-{end}:" indicator
    // Code fence with snippet from chunk region
    //
    // Snippet extraction: call snippet() macro with
    //   file:///path#line={BestChunkStartLine},{BestChunkEndLine}
    //   context: 3 lines
}
```

**Snippet population**: Focused snippets are populated lazily — only for results the allocation engine selects for Focused. However, `RepresentationFormatter` is synchronous and `snippet()` requires a DB call. A **pre-fetch step** between allocation and rendering resolves this: after `AllocateWithClusters` returns `ClusterDecision[]`, the orchestrator scans decisions for Focused allocations and fetches all snippets in a single async batch before passing to the synchronous `OutputComposer`.

---

## SimHash Storage Decision

**Decision**: Add `simhash UBIGINT` column to the artifact table.

```sql
ALTER TABLE artifact ADD COLUMN simhash UBIGINT;
```

SimHash is a computed property of the artifact's content — the same category as `token_count`, `headline`, and `structure`, all of which are already artifact columns. The frozen schema constraint prevents *new tables* and *architectural drift*. Adding a derived content property to the content table is neither.

The alternative — storing in the annotation table with hex string encoding, joined through node — would cost ~2ms per query for a JOIN that exists solely to work around a constraint that doesn't apply here. SimHash needs native UBIGINT for XOR + popcount. Encoding it as a hex string in an annotation message field and parsing it back is complexity without purpose.

**Migration**: Single `ALTER TABLE ADD COLUMN`. Existing rows get NULL. SimHash computed on next reindex or file change. NULL simhash means "not yet computed" — DuplicateDetector treats NULL as canonical (never matches as duplicate).

---

## Cluster-Aware Allocation

The existing `ValueBasedDecisionEngine` uses a utility model:

```
U(item, option) = P_relevance × V(option, intent) × evidenceQuality × novelty
```

This model extends cleanly to three levels by running it within each cluster's budget.

### Extension Strategy

Add a new public method alongside the existing `Allocate`:

```csharp
public class ValueBasedDecisionEngine
{
    // Existing — unchanged
    public AllocationResult Allocate(
        IReadOnlyList<SearchResult> candidates,
        Intent intent,
        int tokenBudget);

    // New — cluster-aware
    public IReadOnlyList<ClusterDecision> AllocateWithClusters(
        IReadOnlyList<ResultCluster> clusters,
        Intent intent,
        int tokenBudget);
}
```

**`AllocateWithClusters` carries its own per-cluster allocation** using the same utility model. It does not delegate to the existing `Allocate(IReadOnlyList<SearchResult>)` because cluster members are `ExploreResult`, not `SearchResult`. The utility inputs (Confidence, Kind, SemanticType, child objects) are all available on `ExploreResult`. Level 1 distributes budget across clusters. Level 2+3 run the existing utility formula per cluster, operating on `ExploreResult` directly.

**Novelty tracking is per-cluster by design.** The existing `NoveltyTracker` provides diminishing returns for same-type results (the 5th `csharp.method` is less novel than the 1st). Running it per-cluster means each cluster gets fresh novelty tracking. This is intentional — cross-cluster novelty created the problem we're solving (one dominant type starving other areas). Per-cluster novelty lets each cluster's content compete fairly within its own budget.

**Cluster EV**: `max(member.Confidence) × intentModifier × (1 + 0.1 × log2(size))`. The size factor gives a slight bonus to larger clusters (they cover more ground) without dominating.

**When clustering is unavailable**: `AllocateWithClusters` receives single-member clusters (one per result) and degrades to the existing flat allocation behavior. No conditional logic needed.

---

## Query Expansion

### Dictionary Design

Embedded resource, not a database table. Reasons:
- Available immediately at startup (no DB dependency)
- Small (< 1KB for ~30 entries)
- Versioned with code
- No migration needed

```json
{
  "auth": ["authentication", "authorization", "authenticate", "oauth"],
  "config": ["configuration", "configure", "settings"],
  "db": ["database"],
  "repo": ["repository"],
  "impl": ["implementation"],
  "svc": ["service"],
  "ctx": ["context"],
  "req": ["request"],
  "res": ["response", "result"],
  "err": ["error", "exception"],
  "msg": ["message"],
  "init": ["initialize", "initialization"],
  "val": ["validate", "validation", "validator"],
  "param": ["parameter"],
  "util": ["utility", "utilities"],
  "doc": ["document", "documentation"],
  "env": ["environment"],
  "spec": ["specification"]
}
```

Start small. Expand based on zero-result query analysis.

### RRF Fusion

Two searches: original (full weight) and expanded (0.6× weight). Fused via RRF with k=60:

```
RRF_score(d) = 1/(60 + rank_original(d)) + 1/(60 + rank_expanded(d))
```

If a document appears in only one list, it gets one term. Documents in both lists get naturally boosted. The 0.6× weight on the expanded search reduces its influence — the original query should dominate when it finds results.

### Skip Conditions

```csharp
bool ShouldExpand(string keywords) =>
    !keywords.StartsWith('"') &&           // Not quoted
    !Regex.IsMatch(keywords, @"[A-Z][a-z]+[A-Z]") &&  // Not CamelCase
    !keywords.Contains('.') &&             // Not qualified name
    !keywords.Contains('/') &&             // Not path
    keywords.Length <= 60;                  // Not too long
```

---

## Cross-Cutting Concerns

### Graceful Degradation

Every component returns passthrough output when it cannot contribute:

| Component | Missing condition | Passthrough behavior |
|-----------|-------------------|---------------------|
| QueryExpander | No dictionary loaded | Returns original query unchanged |
| Search (chunk propagation) | No semantic search / no embeddings | BestChunkStartLine = null |
| DuplicateDetector | No simhash values (all NULL) | Returns all results as canonical |
| ResultClusterer | 0 or 1 results | Returns single-member clusters |
| Focused representation | No chunk location on result | Skipped in PickBestFit progression |
| Cluster allocation | No clusters formed | Degrades to existing flat allocation |

**Test**: The entire pipeline with all new components returning passthrough must produce output identical to today's pipeline.

### Budget Accounting

Each phase has a token cost. The budget flows as:

```
Total budget (user-specified)
  - cluster header overhead (~10 tok × cluster count)
  - status footer (~30 tok)
  - expansion annotation (~15 tok, if shown)
  = distributable budget
    → cluster allocation
      → file allocation
        → object allocation
          → representation selection (including Focused)
```

**Invariant**: `sum(rendered tokens) ≤ total budget`. The allocation engine must track the overhead costs and subtract them before distributing.

### Error Isolation

Failures in new components must not affect the existing pipeline:

```csharp
// In ExploreOrchestrator — pattern for each new stage
ExploreResult[] results;
try
{
    var expanded = _queryExpander?.Expand(query.Keywords) ?? new ExpandedQuery(query.Keywords, query.Keywords, [], false);
    // ... use expanded in search
}
catch (Exception ex)
{
    _logger.LogWarning(ex, "Query expansion failed, using original query");
    // Fall through with original query
}
```

Each stage is wrapped in try-catch with logging. The orchestrator continues with whatever data it has.

### Performance Budget

| Component | Expected latency | Technique |
|-----------|-----------------|-----------|
| Query expansion | < 1ms | Dictionary lookup, string ops |
| Dual search (expansion) | ~15ms additional | Second search call |
| SimHash fetch (artifact column) | ~0ms | Already in search projection |
| Duplicate detection | < 0.1ms | XOR + popcount on ~50 results |
| Clustering | < 1ms | String prefix grouping |
| Three-level allocation | ~5ms additional | One allocation call per cluster |
| Focused snippet pre-fetch | ~5ms per snippet, batched | snippet() macro, async before rendering |
| **Total additional** | **~28ms** | Well within interactive budget |

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| Artifact column for SimHash | Annotation table | Native UBIGINT; zero-cost in search projection; same category as token_count |
| Embedded JSON dictionary | SQL table | No DB dependency; versioned with code; tiny |
| RRF fusion | Weighted score sum | Score-agnostic; no calibration between original and expanded |
| Path-based clustering | Spectral clustering | Simple, predictable, covers 80% of cases; spectral deferred to Phase 8 |
| Extend ValueBasedDecisionEngine | Replace allocation | Existing utility model is sound; clusters are a layer above it |
| Focused as new enum value | Parameterized Rich | Clean progression; distinct estimation and rendering logic |
| Lazy snippet population | Eager for all results | Only Focused results need snippets; avoids unnecessary DB calls |
| Duplicate demotion (0.5× EV) | Duplicate removal | Agent needs awareness of copies; removal hides information |

## Alternatives Considered

**SimHash as annotation**: Would avoid modifying the artifact table. Rejected because it requires JOIN through node, hex string encoding/parsing, and adds ~2ms per query for no architectural benefit. SimHash is a computed property of content, same as headline and token_count — it belongs on artifact.

**LLM-based query expansion**: More accurate than dictionary lookup. Rejected because it adds latency (~500ms) and LLM dependency for a feature that should work offline. Dictionary covers the common abbreviations; semantic search covers the rest.

**MMR for diversity selection**: Would complement duplicate detection with embedding-based diversity. Deferred — SimHash dedup handles the most common case (identical/near-identical files) at near-zero cost. MMR adds O(k²) embedding comparisons. Worth adding later when dedup proves the value of diversity.

**Spectral clustering for module detection**: Would provide semantically meaningful clusters from the code graph. Deferred — requires eigensolver UDF, expensive at index time, and the "how many clusters" problem is unsolved. Path-based clustering captures most structure at zero cost.

**Entropy validation**: Would provide information-theoretic measure of result set quality. Deferred — requires topic modeling infrastructure. MMR + dedup cover practical diversity needs. Add entropy when we have benchmarks to measure its impact.

## Risks

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| Query expansion adds noise (irrelevant results) | Medium | Low | RRF naturally ranks noise low; skip conditions prevent expansion of specific queries; small dictionary limits blast radius |
| SimHash false positives (unrelated files flagged as duplicates) | Low | Medium | Conservative threshold (3 bits); demote, don't hide; agent can still read() any file |
| Clustering produces unhelpful groups | Medium | Low | Falls through to ungrouped; cluster headers are cheap (~10 tokens); worst case is a few wasted tokens |
| Focused snippet shows wrong region | Low | Medium | Chunk scores come from semantic search which is already trusted; 3-line context padding provides surrounding code; fallback to Standard if snippet fetch fails |
| Allocation complexity increases maintenance cost | Medium | Medium | `AllocateWithClusters` delegates to existing `Allocate` per cluster; utility model unchanged; cluster level is thin layer |
| Performance regression from dual search | Low | Low | Second search adds ~15ms; measured and bounded; skip expansion when not needed |

## Extension Points

- **IQueryExpander** — swap dictionary for LLM-based expansion without pipeline changes
- **IDuplicateDetector** — swap SimHash for MinHash, TLSH, or embedding-based similarity
- **IResultClusterer** — swap path-based for spectral clustering when eigensolver is available
- **Representation enum** — additional levels can be inserted (e.g., between Compact and Standard for "headline + top-1 method")
- **Fingerprint column** — additional fingerprint columns (e.g., `tlsh`) follow the same pattern as simhash
- **Abbreviation dictionary** — load from user-configurable file to support domain-specific terms

---

## Implementation Order

Phases are independently deployable. Each delivers value without the others.

```
Phase 1: Focused Snippets
  Modify: ExploreResult, SearchResult, Representation enum,
          ExploreTokenEstimator, OptionValue, RepresentationFormatter,
          ValueBasedDecisionEngine (PickBestFit), ExploreSearchEngine (chunk propagation)
  Test: Focused renders correctly; falls back to Standard without chunks;
        budget estimation accurate within 10%

Phase 2: Query Expansion
  Create: IQueryExpander, AbbreviationExpander, abbreviations.json
  Modify: ExploreOrchestrator (call expander), ExploreSearchEngine (dual search + RRF)
  Test: "auth" finds "authentication"; quoted queries skip expansion;
        RRF fusion produces stable ranking; zero-result rate decreases

Phase 3: SimHash Dedup
  Create: SimHashCalculator, IDuplicateDetector, SimHashDuplicateDetector
  Modify: Artifact schema (add simhash column), artifact construction (compute during indexing),
          ExploreSearchEngine (project simhash), ExploreResult (DuplicateOf fields),
          OutputComposer (duplicate annotation rendering)
  Test: Near-identical files detected (hamming ≤ 3); false positive rate < 5%;
        duplicates render with canonical reference; NULL simhash degrades gracefully

Phase 4: Clustered Output
  Create: IResultClusterer, PathBasedClusterer, ResultCluster, ClusterType
  Modify: ExploreOrchestrator (insert clustering step), OutputComposer (cluster headers)
  Test: Results from shared directories group; cluster labels are factual;
        single results don't cluster; output structure matches flow spec

Phase 5: Three-Level Allocation
  Create: ClusterDecision
  Modify: ValueBasedDecisionEngine (AllocateWithClusters), ExploreOrchestrator (pass clusters)
  Test: Budget distributed across clusters; single-cluster degrades to flat;
        duplicates demoted; budget utilization 95-100%
```

## Dependencies

### Existing infrastructure (no changes)

| Component | Used by |
|-----------|---------|
| Artifact construction (Materialize) | SimHash computed alongside headline, structure |
| `UtilityCalculator` | Allocation utility model |
| `NoveltyTracker` | Per-type/per-file diminishing returns |
| `snippet()` SQL macro | Focused snippet extraction |
| `artifact` table | SimHash storage (new column) |
| `search()` SQL macro | Core search (called twice for expansion) |

### New dependencies

None. All new components use existing infrastructure. No new NuGet packages, no external services, no new tables.
