# Phase 2: Query Expansion

Expand queries with abbreviation variants and casing transformations before search. Fuse original and expanded results via Reciprocal Rank Fusion.

## Why This Matters

| Without expansion | With expansion |
|-------------------|----------------|
| "auth" misses "authentication" | "auth" → finds "authentication", "authorization", "OAuth" |
| "config" misses "IConfigurationProvider" | "config" → finds "configuration", "configure", "settings" |
| "db conn" returns nothing | "db conn" → finds "database connection", "SqlConnection" |
| Agent must guess the right vocabulary | Agent's natural language works |

**The desire path**: Agents think in concepts ("auth"), code uses identifiers ("AuthenticationService"). Expansion bridges that gap without requiring the agent to know naming conventions.

## Current State

Today's search pipeline has three scoring components:
- **Lexical** (`_search_lexical`): Exact/contains matching on symbol names, paths, body
- **Fuzzy** (`match_score` UDF): Subsequence matching with gap penalties
- **Semantic** (`_search_semantic`): Embedding cosine similarity

Semantic search partially bridges the vocabulary gap (embeddings for "auth" and "authentication" are similar), but lexical search — which catches exact identifier matches — misses entirely. Fuzzy helps with subsequences ("auth" in "AuthService") but not with synonyms ("config" vs "settings").

## Trigger

Any explore or search call with keywords.

## Stages

### 1. Query Reception

**Actor**: ExploreOrchestrator
**Action**: Receives query with keywords
**Output**: Raw keyword string
**Failure**: N/A

### 2. Expansion Decision

**Actor**: QueryExpander (new component)
**Action**: Determine whether to expand this query
**Output**: Boolean: expand or pass through

**Skip expansion when**:

| Condition | Detection | Rationale |
|-----------|-----------|-----------|
| Quoted exact match | Keywords wrapped in `"..."` | User wants literal |
| Already CamelCase | Matches `[A-Z][a-z]+[A-Z]` | Already a specific identifier |
| Already qualified | Contains `.` or `::` or `/` | Already a path or qualified name |
| Very long query | > 60 characters | Risk of drift |

**Default**: Expand. These skip conditions are safety rails, not the common path.

### 3. Term Tokenization

**Actor**: QueryExpander
**Action**: Split keywords into individual terms
**Output**: List of normalized terms

```
"auth config" → ["auth", "config"]
"get user by id" → ["get", "user", "by", "id"]
```

**Normalization**: Lowercase. Split on whitespace. Preserve quoted phrases as single terms.

### 4. Abbreviation Lookup

**Actor**: QueryExpander, using abbreviation dictionary
**Action**: For each term, look up known expansions
**Output**: Map of term → expansion variants

**Dictionary** (stored as SQL table or embedded resource):

```
auth     → [authentication, authorization, authenticate, oauth]
config   → [configuration, configure, settings, options]
db       → [database]
repo     → [repository]
impl     → [implementation, implement]
svc      → [service]
ctx      → [context]
req      → [request]
res      → [response, result]
err      → [error, exception]
msg      → [message]
init     → [initialize, initialization]
val      → [validate, validation, validator]
param    → [parameter]
util     → [utility, utilities]
mgr      → [manager]
proc     → [process, processor]
info     → [information]
doc      → [document, documentation]
spec     → [specification]
env      → [environment]
deps     → [dependencies, dependency]
```

**Design choice**: Start with a small, high-confidence dictionary (~30 entries). Expand based on zero-result query analysis. Do not attempt to be comprehensive — the cost of a bad expansion (noise) exceeds the cost of a missing expansion (one more query).

### 5. Casing Variant Generation

**Actor**: QueryExpander
**Action**: For each expanded term, generate casing variants that match common code conventions
**Output**: Additional variants per term

```
"authentication" → [
    "authentication",       // lowercase
    "Authentication",       // PascalCase
    "AUTHENTICATION",       // UPPER (constants)
    "IAuthentication",      // C# interface prefix
]
```

**Scope**: Only generate variants for terms that will hit the lexical scorer. Semantic search is case-insensitive via embeddings, so casing variants only help BM25/contains matching.

**Limit**: Max 4 casing variants per term. More adds cost without meaningful recall improvement.

### 6. Expanded Query Construction

**Actor**: QueryExpander
**Action**: Combine original terms + expansions into expanded query
**Output**: `ExpandedQuery { Original, Expanded, Expansions[] }`

```
Input: "auth config"

Output:
  Original: "auth config"
  Expanded: "auth authentication authorization oauth config configuration settings"
  Expansions: [
    { Term: "auth", AddedVariants: ["authentication", "authorization", "oauth"] },
    { Term: "config", AddedVariants: ["configuration", "settings"] }
  ]
```

### 7. Dual Search Execution

**Actor**: ExploreSearchEngine
**Action**: Run search twice — once with original query, once with expanded query
**Output**: Two ranked result lists

```
Original results:  search("auth config", k=50)  → R_orig
Expanded results:  search("auth authentication authorization ...", k=50)  → R_exp
```

**Weight differentiation**: Original query gets full weight. Expanded query gets reduced weight (0.6×) to prevent expansion drift from dominating.

**Performance**: Two search calls instead of one. At ~15ms per search, this adds ~15ms. Acceptable for the recall improvement.

### 8. Reciprocal Rank Fusion

**Actor**: ExploreSearchEngine
**Action**: Combine the two result lists using RRF
**Output**: Single fused ranked list

```
RRF_score(d) = Σ  1 / (k + rank_i(d))
               i∈{orig, expanded}

Where k = 60 (standard RRF constant)
```

**Why RRF over weighted sum**: RRF is score-agnostic — it works on ranks, not raw scores. This avoids calibration issues between the original and expanded searches (which may have different score distributions).

**Tiebreaking**: If a document appears in only one list, it still gets a score (just from one term). Documents appearing in both lists get naturally boosted.

### 9. Expansion Annotation (Optional)

**Actor**: OutputComposer
**Action**: If results came primarily from expanded terms, annotate the output
**Output**: Subtle note in status footer

```
[2.1k tok | 23 ms | index: ready | semantic: ready | expanded: auth→authentication]
```

**Purpose**: Transparency. If the agent sees unexpected results, the annotation explains why. Only show if expanded results dominate (>50% of top-10 came from expansion).

## Flow Diagram

```mermaid
flowchart TD
    Query([Keywords: "auth config"]) --> Decision{Should expand?}

    Decision -->|Quoted/CamelCase/Long| PassThrough[Use original only]
    Decision -->|Yes| Tokenize[Split into terms]

    Tokenize --> Lookup[Abbreviation dictionary lookup]
    Lookup --> Casing[Generate casing variants]
    Casing --> Build[Build expanded query string]

    Build --> DualSearch[Run search twice]
    PassThrough --> SingleSearch[Run search once]

    DualSearch --> Original[search: original query]
    DualSearch --> Expanded[search: expanded query × 0.6 weight]

    Original --> RRF[Reciprocal Rank Fusion]
    Expanded --> RRF
    SingleSearch --> RRF

    RRF --> Results([Fused ranked results])
```

## Data Shapes

**Input**:
```
ExploreQuery {
    Keywords: "auth config"
    ...
}
```

**After expansion**:
```
ExpandedQuery {
    Original: "auth config"
    Expanded: "auth authentication authorization oauth config configuration settings"
    Expansions: [
        { Term: "auth", Variants: ["authentication", "authorization", "oauth"] },
        { Term: "config", Variants: ["configuration", "settings"] }
    ]
    ShouldExpand: true
}
```

**After dual search + RRF**:
```
FusedResult[] {
    { Uri: "file:///src/Auth/AuthService.cs", RrfScore: 0.032, Sources: [orig:1, exp:1] },
    { Uri: "file:///src/Auth/AuthConfig.cs", RrfScore: 0.028, Sources: [orig:3, exp:2] },
    { Uri: "file:///src/Config/ConfigurationProvider.cs", RrfScore: 0.025, Sources: [exp:3] },
    ...
}
```

Note the third result — found only by the expanded query. Without expansion, it wouldn't appear.

## Edge Cases

| Case | Behaviour |
|------|-----------|
| No expansions found for any term | Expanded query = original → single search, no cost |
| All terms expand to many variants | Cap total expanded terms at 20 to bound search cost |
| Expansion introduces noise | RRF naturally ranks noise low (appears in expanded list only, at low rank) |
| Keywords are a full sentence (Explain intent) | Still expand individual words; the semantic scorer handles phrase meaning |
| Boost/penalize patterns set | Apply after RRF fusion, same as today |

## Failure Modes

| Failure | Detection | Recovery |
|---------|-----------|----------|
| Dictionary table missing | SQL error on lookup | Skip expansion, use original only |
| Expansion produces zero results | Expanded search returns empty | Fall back to original-only results |
| Expansion too broad (many low-quality results) | Top-10 RRF scores all very low | Log warning; consider tightening dictionary |

## Key Files to Create/Modify

| File | Change |
|------|--------|
| `QueryExpander.cs` (new) | Tokenization, dictionary lookup, casing variants, expansion decision |
| `ExploreOrchestrator.cs` | Call QueryExpander before search |
| `ExploreSearchEngine.cs` | Accept expanded query, run dual search, RRF fusion |
| `abbreviations.sql` or embedded resource | Dictionary data |
| `OutputComposer.cs` | Optional expansion annotation in footer |

## Interaction with Other Phases

- **Phase 1 (Focused Snippets)**: Expansion finds more files → more opportunities for focused snippets on relevant chunks.
- **Phase 3 (SimHash Dedup)**: Expansion may surface more clones (different paths to same file) → dedup becomes more important.
- **Phase 4 (Clustered Output)**: Expanded results span more directories → clustering adds more value.

## Metrics

| Metric | How to Measure | Target |
|--------|----------------|--------|
| Zero-result rate | % of keyword queries with 0 results | -50% vs current |
| Expansion hit rate | % of queries where expansion found additional results | > 40% |
| Recall@20 | Relevant documents in top 20 (needs ground truth) | +20% vs current |
| Latency overhead | Additional ms from dual search | < 20ms |
